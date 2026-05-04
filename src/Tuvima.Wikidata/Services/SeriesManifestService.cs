using System.Globalization;
using Tuvima.Wikidata.Internal;

namespace Tuvima.Wikidata.Services;

/// <summary>
/// Generic Wikidata series manifest retrieval and ordering.
/// Obtained via <see cref="WikidataReconciler.Series"/>.
/// </summary>
public sealed class SeriesManifestService
{
    private const string PartOfSeries = "P179";
    private const string PartOf = "P361";
    private const string HasPart = "P527";
    private const string SeriesOrdinal = "P1545";
    private const string Follows = "P155";
    private const string FollowedBy = "P156";
    private const string PublicationDate = "P577";

    private static readonly string[] RelationshipProperties = [PartOfSeries, PartOf, Follows, FollowedBy, HasPart];

    private readonly ReconcilerContext _ctx;

    internal SeriesManifestService(ReconcilerContext ctx) => _ctx = ctx;

    /// <summary>
    /// Builds a series manifest using default request options.
    /// </summary>
    public Task<SeriesManifestResult> GetManifestAsync(
        string seriesQid,
        CancellationToken cancellationToken = default)
        => GetManifestAsync(new SeriesManifestRequest { SeriesQid = seriesQid }, cancellationToken);

    /// <summary>
    /// Builds a series manifest from Wikidata relationship patterns such as P179, P361, and P527.
    /// </summary>
    public async Task<SeriesManifestResult> GetManifestAsync(
        SeriesManifestRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SeriesQid);

        var language = string.IsNullOrWhiteSpace(request.Language)
            ? _ctx.Options.Language
            : request.Language;
        var maxDepth = Math.Max(1, request.MaxDepth);
        var maxItems = Math.Max(1, request.MaxItems);
        var warnings = new WarningCollector();

        var fetchedRoot = await FetchPublicEntitiesAsync([request.SeriesQid], language, cancellationToken)
            .ConfigureAwait(false);
        fetchedRoot.TryGetValue(request.SeriesQid, out var seriesEntity);

        var discovered = new Dictionary<string, DiscoveredItem>(StringComparer.OrdinalIgnoreCase);
        var pendingFetch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var p179Task = _ctx.SearchClient.SearchAllByStatementAsync(
            $"haswbstatement:{PartOfSeries}={request.SeriesQid}",
            typeFilter: null,
            cancellationToken);
        var p361Task = _ctx.SearchClient.SearchAllByStatementAsync(
            $"haswbstatement:{PartOf}={request.SeriesQid}",
            typeFilter: null,
            cancellationToken);

        await Task.WhenAll(p179Task, p361Task).ConfigureAwait(false);

        foreach (var qid in await p179Task.ConfigureAwait(false))
            AddDiscovery(discovered, pendingFetch, qid, PartOfSeries, request.SeriesQid, null, null, null, warnings);

        foreach (var qid in await p361Task.ConfigureAwait(false))
            AddDiscovery(discovered, pendingFetch, qid, PartOf, request.SeriesQid, null, null, null, warnings);

        if (seriesEntity is not null)
        {
            foreach (var claim in GetClaims(seriesEntity, HasPart))
            {
                var childQid = claim.Value?.EntityId;
                if (string.IsNullOrWhiteSpace(childQid))
                    continue;

                AddDiscovery(
                    discovered,
                    pendingFetch,
                    childQid,
                    HasPart,
                    request.SeriesQid,
                    parentCollectionQid: null,
                    parentCollectionLabel: null,
                    ordinalFromParent: GetFirstQualifierRaw(claim, SeriesOrdinal),
                    warnings);
            }
        }

        var entities = await FetchPublicEntitiesAsync(pendingFetch.ToList(), language, cancellationToken)
            .ConfigureAwait(false);
        if (seriesEntity is not null)
            entities[request.SeriesQid] = seriesEntity;

        if (request.ExpandCollections)
        {
            await ExpandCollectionsAsync(
                request,
                language,
                maxDepth,
                discovered,
                entities,
                warnings,
                cancellationToken).ConfigureAwait(false);
        }

        var visible = discovered.Values
            .Where(item => request.IncludeCollections || !IsCollection(item, entities))
            .ToList();

        if (visible.Count == 0)
        {
            warnings.Add("NoChildrenFound", $"No child works were found for series {request.SeriesQid}.", request.SeriesQid);
        }

        HydrateEvidence(visible, entities, request.SeriesQid, request.IncludePublicationDate);
        AddOrderingWarnings(visible, warnings);

        var sorted = SortAndAssignOrderSources(visible, warnings);
        if (sorted.Count > maxItems)
        {
            warnings.Add("MaxItemsReached", $"Series manifest was truncated to MaxItems={maxItems}.", request.SeriesQid);
            sorted = sorted.Take(maxItems).ToList();
        }

        var relationshipTargets = sorted
            .SelectMany(item => item.Relationships.Select(r => r.TargetQid))
            .Concat(sorted.Select(item => item.ParentCollectionQid).Where(qid => !string.IsNullOrWhiteSpace(qid)).Select(qid => qid!))
            .Append(request.SeriesQid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(qid => !entities.ContainsKey(qid) && !string.Equals(qid, request.SeriesQid, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var labels = relationshipTargets.Count == 0
            ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            : await FetchLabelsAsync(relationshipTargets, language, cancellationToken).ConfigureAwait(false);

        var manifestItems = sorted.Select(item => ToPublicItem(item, entities, labels, request.IncludeDescriptions)).ToList();

        return new SeriesManifestResult
        {
            SeriesQid = request.SeriesQid,
            SeriesLabel = seriesEntity?.Label,
            Items = manifestItems,
            Warnings = warnings.ToList(),
            Completeness = DetermineCompleteness(manifestItems, warnings)
        };
    }

    private async Task ExpandCollectionsAsync(
        SeriesManifestRequest request,
        string language,
        int maxDepth,
        Dictionary<string, DiscoveredItem> discovered,
        Dictionary<string, WikidataEntityInfo> entities,
        WarningCollector warnings,
        CancellationToken cancellationToken)
    {
        var depth = 1;
        var frontier = discovered.Keys.ToList();

        while (frontier.Count > 0)
        {
            var missing = frontier.Where(qid => !entities.ContainsKey(qid)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (missing.Count > 0)
            {
                var fetched = await FetchPublicEntitiesAsync(missing, language, cancellationToken).ConfigureAwait(false);
                foreach (var (qid, entity) in fetched)
                    entities[qid] = entity;
            }

            var next = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var parentQid in frontier)
            {
                if (!entities.TryGetValue(parentQid, out var parent) || !HasEntityClaim(parent, HasPart))
                    continue;

                if (depth >= maxDepth)
                {
                    warnings.Add("MaxDepthReached", $"Collection expansion reached MaxDepth={maxDepth}.", parentQid);
                    continue;
                }

                foreach (var claim in GetClaims(parent, HasPart))
                {
                    var childQid = claim.Value?.EntityId;
                    if (string.IsNullOrWhiteSpace(childQid))
                        continue;

                    AddDiscovery(
                        discovered,
                        next,
                        childQid,
                        HasPart,
                        parentQid,
                        parentCollectionQid: parentQid,
                        parentCollectionLabel: parent.Label,
                        ordinalFromParent: GetFirstQualifierRaw(claim, SeriesOrdinal),
                        warnings);
                }
            }

            frontier = next.ToList();
            depth++;
        }

        var remainingMissing = discovered.Keys.Where(qid => !entities.ContainsKey(qid)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (remainingMissing.Count > 0)
        {
            var fetched = await FetchPublicEntitiesAsync(remainingMissing, language, cancellationToken).ConfigureAwait(false);
            foreach (var (qid, entity) in fetched)
                entities[qid] = entity;
        }
    }

    private async Task<Dictionary<string, WikidataEntityInfo>> FetchPublicEntitiesAsync(
        IReadOnlyList<string> qids,
        string language,
        CancellationToken cancellationToken)
    {
        if (qids.Count == 0)
            return new Dictionary<string, WikidataEntityInfo>(StringComparer.OrdinalIgnoreCase);

        var fetched = await _ctx.EntityFetcher.FetchEntitiesAsync(qids, language, cancellationToken).ConfigureAwait(false);
        return fetched.ToDictionary(
            kvp => kvp.Key,
            kvp => EntityMapper.MapEntity(kvp.Value, language),
            StringComparer.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyDictionary<string, string?>> FetchLabelsAsync(
        IReadOnlyList<string> qids,
        string language,
        CancellationToken cancellationToken)
    {
        var fetched = await _ctx.EntityFetcher.FetchLabelsOnlyAsync(qids, language, cancellationToken).ConfigureAwait(false);
        return fetched.ToDictionary(
            kvp => kvp.Key,
            kvp =>
            {
                LanguageFallback.TryGetValue(kvp.Value.Labels, language, out var label);
                return string.IsNullOrWhiteSpace(label) ? null : label;
            },
            StringComparer.OrdinalIgnoreCase);
    }

    private static void AddDiscovery(
        Dictionary<string, DiscoveredItem> discovered,
        HashSet<string> pendingFetch,
        string qid,
        string sourceProperty,
        string sourceTargetQid,
        string? parentCollectionQid,
        string? parentCollectionLabel,
        string? ordinalFromParent,
        WarningCollector warnings)
    {
        if (!discovered.TryGetValue(qid, out var item))
        {
            item = new DiscoveredItem(qid);
            discovered[qid] = item;
            pendingFetch.Add(qid);
        }
        else
        {
            warnings.Add("DuplicateItem", $"Item {qid} was discovered through multiple paths.", qid);
        }

        item.SourceProperties.Add(sourceProperty);
        item.DiscoveryTargets.Add((sourceProperty, sourceTargetQid));
        if (!string.IsNullOrWhiteSpace(parentCollectionQid))
        {
            item.IsExpandedFromCollection = true;
            item.ParentCollectionQid ??= parentCollectionQid;
            item.ParentCollectionLabel ??= parentCollectionLabel;
        }

        if (!string.IsNullOrWhiteSpace(ordinalFromParent))
            item.OrdinalCandidates.Add(ordinalFromParent);
    }

    private static void HydrateEvidence(
        IReadOnlyList<DiscoveredItem> items,
        IReadOnlyDictionary<string, WikidataEntityInfo> entities,
        string seriesQid,
        bool includePublicationDate)
    {
        var itemQids = items.Select(item => item.Qid).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            if (!entities.TryGetValue(item.Qid, out var entity))
                continue;

            item.Label = entity.Label;
            item.Description = entity.Description;
            item.IsCollection = IsCollection(item, entities);
            item.PublicationDate = includePublicationDate ? ExtractDateOnly(entity, PublicationDate) : null;
            item.PreviousQid = GetFirstEntityId(entity, Follows);
            item.NextQid = GetFirstEntityId(entity, FollowedBy);

            foreach (var (propertyId, targetQid) in item.DiscoveryTargets)
            {
                var ordinal = GetOrdinalForTarget(entity, propertyId, targetQid);
                if (!string.IsNullOrWhiteSpace(ordinal))
                    item.OrdinalCandidates.Add(ordinal);
            }

            foreach (var propertyId in RelationshipProperties)
            {
                foreach (var targetQid in GetEntityIds(entity, propertyId))
                {
                    if (propertyId is PartOfSeries or PartOf or Follows or FollowedBy)
                    {
                        item.Relationships.Add(new RelationshipEvidence(propertyId, targetQid, "Outgoing"));
                    }
                }
            }

            foreach (var targetQid in item.DiscoveryTargets.Where(t => t.PropertyId == HasPart).Select(t => t.TargetQid))
                item.Relationships.Add(new RelationshipEvidence(HasPart, targetQid, "Incoming"));

            item.Relationships = item.Relationships
                .DistinctBy(r => $"{r.PropertyId}|{r.TargetQid}|{r.Direction}", StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (item.PreviousQid is not null && !itemQids.Contains(item.PreviousQid))
                item.HasBrokenPreviousNext = true;
            if (item.NextQid is not null && !itemQids.Contains(item.NextQid))
                item.HasBrokenPreviousNext = true;
        }

        foreach (var item in items)
        {
            var distinctOrdinals = item.OrdinalCandidates
                .Where(o => !string.IsNullOrWhiteSpace(o))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            item.RawSeriesOrdinal = distinctOrdinals.FirstOrDefault();
            item.HasConflictingOrdinals = distinctOrdinals.Count > 1;
            item.ParsedSeriesOrdinal = TryParseOrdinal(item.RawSeriesOrdinal);

            if (item.Relationships.Count == 0 && !string.IsNullOrWhiteSpace(seriesQid))
                item.Relationships.Add(new RelationshipEvidence(PartOfSeries, seriesQid, "Outgoing"));
        }
    }

    private static void AddOrderingWarnings(IReadOnlyList<DiscoveredItem> items, WarningCollector warnings)
    {
        if (items.Count == 0)
            return;

        if (items.Any(item => string.IsNullOrWhiteSpace(item.RawSeriesOrdinal)))
            warnings.Add("MissingOrdinals", "Some series manifest items do not have a P1545 ordinal.");

        foreach (var item in items.Where(item => item.HasConflictingOrdinals))
            warnings.Add("ConflictingOrdinals", $"Item {item.Qid} has conflicting P1545 ordinal values.", item.Qid);

        foreach (var item in items.Where(item => item.HasBrokenPreviousNext))
            warnings.Add("BrokenPreviousNextChain", $"Item {item.Qid} has a P155/P156 link outside the manifest candidate set.", item.Qid);
    }

    private static List<DiscoveredItem> SortAndAssignOrderSources(
        IReadOnlyList<DiscoveredItem> items,
        WarningCollector warnings)
    {
        var chainIndexes = BuildChainIndexes(items, warnings);
        var hasAnyOrdinal = items.Any(item => !string.IsNullOrWhiteSpace(item.RawSeriesOrdinal));
        var hasAnyChain = chainIndexes.Count > 0;

        foreach (var item in items)
        {
            var fallbackSource = chainIndexes.ContainsKey(item.Qid)
                ? SeriesManifestOrderSource.PreviousNextChain
                : item.PublicationDate.HasValue
                    ? SeriesManifestOrderSource.PublicationDate
                    : !string.IsNullOrWhiteSpace(item.Label)
                        ? SeriesManifestOrderSource.LabelFallback
                        : SeriesManifestOrderSource.Unknown;

            item.OrderSource = !string.IsNullOrWhiteSpace(item.RawSeriesOrdinal)
                ? SeriesManifestOrderSource.SeriesOrdinal
                : hasAnyOrdinal && fallbackSource != SeriesManifestOrderSource.Unknown
                    ? SeriesManifestOrderSource.Mixed
                    : fallbackSource;
        }

        if (items.Count > 1 && items.All(item => item.OrderSource is SeriesManifestOrderSource.LabelFallback or SeriesManifestOrderSource.Unknown))
            warnings.Add("LabelFallbackOnly", "Series manifest items were sorted only by fallback label.");

        if (hasAnyOrdinal && hasAnyChain)
        {
            foreach (var item in items.Where(i => i.ParsedSeriesOrdinal.HasValue && chainIndexes.ContainsKey(i.Qid)))
            {
                var ordinalRank = items
                    .Where(i => i.ParsedSeriesOrdinal.HasValue)
                    .OrderBy(i => i.ParsedSeriesOrdinal!.Value)
                    .Select((i, index) => (i.Qid, Index: index))
                    .FirstOrDefault(x => string.Equals(x.Qid, item.Qid, StringComparison.OrdinalIgnoreCase)).Index;

                if (ordinalRank != chainIndexes[item.Qid])
                    warnings.Add("PreviousNextConflictsWithOrdinal", $"Item {item.Qid} has P155/P156 ordering that conflicts with P1545.", item.Qid);
            }
        }

        return items
            .OrderBy(item => string.IsNullOrWhiteSpace(item.RawSeriesOrdinal) ? 1 : 0)
            .ThenBy(item => item.ParsedSeriesOrdinal ?? decimal.MaxValue)
            .ThenBy(item => item.ParsedSeriesOrdinal.HasValue ? null : item.RawSeriesOrdinal, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => chainIndexes.TryGetValue(item.Qid, out var index) ? index : int.MaxValue)
            .ThenBy(item => item.PublicationDate ?? DateOnly.MaxValue)
            .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => QidNumber(item.Qid))
            .ToList();
    }

    private static Dictionary<string, int> BuildChainIndexes(
        IReadOnlyList<DiscoveredItem> items,
        WarningCollector warnings)
    {
        var byQid = items.ToDictionary(item => item.Qid, StringComparer.OrdinalIgnoreCase);
        var nextByPrevious = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var previousTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item.NextQid) && byQid.ContainsKey(item.NextQid))
            {
                if (nextByPrevious.ContainsKey(item.Qid))
                    warnings.Add("BrokenPreviousNextChain", $"Item {item.Qid} has multiple next links in the manifest.", item.Qid);
                else
                    nextByPrevious[item.Qid] = item.NextQid!;
                previousTargets.Add(item.NextQid!);
            }

            if (!string.IsNullOrWhiteSpace(item.PreviousQid) && byQid.ContainsKey(item.PreviousQid))
            {
                if (nextByPrevious.TryGetValue(item.PreviousQid!, out var existing) &&
                    !string.Equals(existing, item.Qid, StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add("BrokenPreviousNextChain", $"Previous/next chain has conflicting links near {item.Qid}.", item.Qid);
                }
                else
                {
                    nextByPrevious[item.PreviousQid!] = item.Qid;
                }
                previousTargets.Add(item.Qid);
            }
        }

        if (nextByPrevious.Count == 0)
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var starts = items
            .Select(item => item.Qid)
            .Where(qid => nextByPrevious.ContainsKey(qid) && !previousTargets.Contains(qid))
            .OrderBy(qid => QidNumber(qid))
            .ToList();

        if (starts.Count == 0)
            starts = [nextByPrevious.Keys.OrderBy(QidNumber).First()];

        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var start in starts)
        {
            var current = start;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cycle = false;
            while (byQid.ContainsKey(current))
            {
                if (!seen.Add(current))
                {
                    cycle = true;
                    break;
                }

                result.TryAdd(current, index++);
                if (!nextByPrevious.TryGetValue(current, out var next))
                    break;
                current = next;
            }

            if (cycle)
                warnings.Add("BrokenPreviousNextChain", $"Previous/next chain contains a cycle near {current}.", current);
        }

        return result;
    }

    private static SeriesManifestItem ToPublicItem(
        DiscoveredItem item,
        IReadOnlyDictionary<string, WikidataEntityInfo> entities,
        IReadOnlyDictionary<string, string?> labels,
        bool includeDescription)
    {
        string? LabelFor(string qid)
        {
            if (entities.TryGetValue(qid, out var entity) && !string.IsNullOrWhiteSpace(entity.Label))
                return entity.Label;
            return labels.TryGetValue(qid, out var label) ? label : null;
        }

        return new SeriesManifestItem
        {
            Qid = item.Qid,
            Label = item.Label,
            Description = includeDescription ? item.Description : null,
            RawSeriesOrdinal = item.RawSeriesOrdinal,
            ParsedSeriesOrdinal = item.ParsedSeriesOrdinal,
            PublicationDate = item.PublicationDate,
            PreviousQid = item.PreviousQid,
            NextQid = item.NextQid,
            ParentCollectionQid = item.ParentCollectionQid,
            ParentCollectionLabel = item.ParentCollectionLabel ?? (item.ParentCollectionQid is null ? null : LabelFor(item.ParentCollectionQid)),
            IsCollection = item.IsCollection,
            IsExpandedFromCollection = item.IsExpandedFromCollection,
            SourceProperties = item.SourceProperties.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
            OrderSource = item.OrderSource,
            Relationships = item.Relationships
                .Select(r => new SeriesManifestRelationship
                {
                    PropertyId = r.PropertyId,
                    TargetQid = r.TargetQid,
                    TargetLabel = LabelFor(r.TargetQid),
                    Direction = r.Direction
                })
                .ToList()
        };
    }

    private static SeriesManifestCompleteness DetermineCompleteness(
        IReadOnlyList<SeriesManifestItem> items,
        WarningCollector warnings)
    {
        if (items.Count == 0)
            return SeriesManifestCompleteness.Empty;

        if (warnings.Contains("MaxItemsReached"))
            return SeriesManifestCompleteness.Truncated;

        return warnings.Count == 0
            ? SeriesManifestCompleteness.Complete
            : SeriesManifestCompleteness.Partial;
    }

    private static bool IsCollection(DiscoveredItem item, IReadOnlyDictionary<string, WikidataEntityInfo> entities)
        => entities.TryGetValue(item.Qid, out var entity) && HasEntityClaim(entity, HasPart);

    private static bool HasEntityClaim(WikidataEntityInfo entity, string propertyId)
        => GetEntityIds(entity, propertyId).Count > 0;

    private static IReadOnlyList<WikidataClaim> GetClaims(WikidataEntityInfo entity, string propertyId)
        => entity.Claims.TryGetValue(propertyId, out var claims)
            ? claims.Where(claim => !string.Equals(claim.Rank, "deprecated", StringComparison.OrdinalIgnoreCase)).ToList()
            : [];

    private static List<string> GetEntityIds(WikidataEntityInfo entity, string propertyId)
        => GetClaims(entity, propertyId)
            .Select(claim => claim.Value?.EntityId)
            .Where(qid => !string.IsNullOrWhiteSpace(qid))
            .Select(qid => qid!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string? GetFirstEntityId(WikidataEntityInfo entity, string propertyId)
        => GetEntityIds(entity, propertyId).FirstOrDefault();

    private static string? GetOrdinalForTarget(WikidataEntityInfo entity, string propertyId, string targetQid)
    {
        foreach (var claim in GetClaims(entity, propertyId))
        {
            if (string.Equals(claim.Value?.EntityId, targetQid, StringComparison.OrdinalIgnoreCase))
                return GetFirstQualifierRaw(claim, SeriesOrdinal);
        }

        return null;
    }

    private static string? GetFirstQualifierRaw(WikidataClaim claim, string propertyId)
        => claim.Qualifiers.TryGetValue(propertyId, out var values)
            ? values.Select(v => v.RawValue).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
            : null;

    private static decimal? TryParseOrdinal(string? raw)
        => decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static DateOnly? ExtractDateOnly(WikidataEntityInfo entity, string propertyId)
    {
        var raw = GetClaims(entity, propertyId).Select(claim => claim.Value?.RawValue).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var trimmed = raw.StartsWith('+') ? raw[1..] : raw;
        return DateTimeOffset.TryParse(
            trimmed,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? DateOnly.FromDateTime(parsed.UtcDateTime)
            : null;
    }

    private static long QidNumber(string qid)
        => qid.Length > 1 && long.TryParse(qid.AsSpan(1), out var n) ? n : long.MaxValue;

    private sealed class DiscoveredItem
    {
        public DiscoveredItem(string qid) => Qid = qid;

        public string Qid { get; }
        public string? Label { get; set; }
        public string? Description { get; set; }
        public string? RawSeriesOrdinal { get; set; }
        public decimal? ParsedSeriesOrdinal { get; set; }
        public DateOnly? PublicationDate { get; set; }
        public string? PreviousQid { get; set; }
        public string? NextQid { get; set; }
        public string? ParentCollectionQid { get; set; }
        public string? ParentCollectionLabel { get; set; }
        public bool IsCollection { get; set; }
        public bool IsExpandedFromCollection { get; set; }
        public bool HasConflictingOrdinals { get; set; }
        public bool HasBrokenPreviousNext { get; set; }
        public SeriesManifestOrderSource OrderSource { get; set; } = SeriesManifestOrderSource.Unknown;
        public HashSet<string> SourceProperties { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string PropertyId, string TargetQid)> DiscoveryTargets { get; } = [];
        public List<string> OrdinalCandidates { get; } = [];
        public List<RelationshipEvidence> Relationships { get; set; } = [];
    }

    private sealed record RelationshipEvidence(string PropertyId, string TargetQid, string Direction);

    private sealed class WarningCollector
    {
        private readonly List<SeriesManifestWarning> _warnings = [];
        private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);

        public int Count => _warnings.Count;

        public void Add(string code, string message, string? qid = null)
        {
            var key = $"{code}|{qid}|{message}";
            if (!_keys.Add(key))
                return;

            _warnings.Add(new SeriesManifestWarning
            {
                Code = code,
                Message = message,
                Qid = qid
            });
        }

        public bool Contains(string code)
            => _warnings.Any(w => string.Equals(w.Code, code, StringComparison.OrdinalIgnoreCase));

        public IReadOnlyList<SeriesManifestWarning> ToList() => _warnings.ToList();
    }
}
