using System.Diagnostics;
using Tuvima.Wikidata.Internal;
using Tuvima.Wikidata.Internal.Json;

namespace Tuvima.Wikidata.Services;

/// <summary>
/// High-level identity resolver for provider bridge IDs, canonical work rollups,
/// relationship extraction, and explainable Wikidata candidates.
/// Obtained via <see cref="WikidataReconciler.Bridge"/>.
/// </summary>
public sealed class BridgeResolutionService
{
    private static readonly IReadOnlyDictionary<BridgeMediaKind, IReadOnlyList<string>> MediaTypeHints =
        new Dictionary<BridgeMediaKind, IReadOnlyList<string>>
        {
            [BridgeMediaKind.Book] = ["Q571", "Q7725634", "Q47461344", "Q3331189"],
            [BridgeMediaKind.Audiobook] = ["Q742421", "Q3331189", "Q571"],
            [BridgeMediaKind.ComicSeries] = ["Q1004", "Q14406742"],
            [BridgeMediaKind.ComicIssue] = ["Q1114461", "Q1004"],
            [BridgeMediaKind.Movie] = ["Q11424"],
            [BridgeMediaKind.TvSeries] = ["Q5398426"],
            [BridgeMediaKind.TvSeason] = ["Q3464665"],
            [BridgeMediaKind.TvEpisode] = ["Q21191270"],
            [BridgeMediaKind.MusicAlbum] = ["Q482994"],
            [BridgeMediaKind.MusicRelease] = ["Q2031291", "Q482994"],
            [BridgeMediaKind.MusicRecording] = ["Q2188189"],
            [BridgeMediaKind.MusicWork] = ["Q2188189", "Q7366"],
            [BridgeMediaKind.MusicTrack] = ["Q7302866", "Q2188189", "Q7366"],
            [BridgeMediaKind.Game] = ["Q7889"],
            [BridgeMediaKind.App] = ["Q7397"]
        };

    private static readonly string[] CreatorPropertyIds = ["P50", "P57", "P58", "P86", "P162", "P170", "P175", "P676"];
    private static readonly string[] SeriesPropertyIds = ["P179", "P361"];

    private readonly ReconcilerContext _ctx;
    private readonly ReconciliationService _reconcile;

    internal BridgeResolutionService(ReconcilerContext ctx, ReconciliationService reconcile)
    {
        _ctx = ctx;
        _reconcile = reconcile;
    }

    /// <summary>
    /// Resolves a single bridge request.
    /// </summary>
    public async Task<BridgeResolutionResult> ResolveAsync(
        BridgeResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var results = await ResolveBatchAsync([request], cancellationToken).ConfigureAwait(false);
        return results.TryGetValue(request.CorrelationKey, out var result)
            ? result
            : BuildFailure(
                request.CorrelationKey,
                BridgeResolutionStatus.NotFound,
                WikidataFailureKind.NotFound,
                "No bridge resolution result was produced.",
                new DiagnosticsBuilder(),
                TimeSpan.Zero);
    }

    /// <summary>
    /// Resolves many bridge requests. External ID lookups are grouped by Wikidata property
    /// and normalized value so duplicate bridge IDs share one provider call.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, BridgeResolutionResult>> ResolveBatchAsync(
        IReadOnlyList<BridgeResolutionRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
            return new Dictionary<string, BridgeResolutionResult>(StringComparer.Ordinal);

        var stopwatch = Stopwatch.StartNew();
        var before = _ctx.Diagnostics.GetSnapshot();
        var normalizedByKey = new Dictionary<string, IReadOnlyList<ResolvedBridgeIdentifier>>(StringComparer.Ordinal);
        var diagnosticsByKey = new Dictionary<string, DiagnosticsBuilder>(StringComparer.Ordinal);

        foreach (var request in requests)
        {
            var diagnostics = new DiagnosticsBuilder();
            diagnosticsByKey[request.CorrelationKey] = diagnostics;

            var identifiers = BridgeIdCatalog.Normalize(request);
            normalizedByKey[request.CorrelationKey] = identifiers;

            foreach (var identifier in identifiers)
                diagnostics.AttemptedStrategies.Add($"bridge:{identifier.NormalizedKey}:{identifier.PropertyId}");

            if (!string.IsNullOrWhiteSpace(request.Title))
                diagnostics.AttemptedStrategies.Add("text:fallback");
        }

        var distinctLookups = normalizedByKey.Values
            .SelectMany(x => x)
            .GroupBy(x => x.LookupKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var lookupResults = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var lookupFailures = new Dictionary<string, WikidataProviderException>(StringComparer.OrdinalIgnoreCase);

        foreach (var lookup in distinctLookups)
        {
            try
            {
                var qids = await _ctx.SearchClient
                    .SearchByExternalIdAsync(lookup.PropertyId, lookup.NormalizedValue, 20, cancellationToken)
                    .ConfigureAwait(false);
                lookupResults[lookup.LookupKey] = qids;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (WikidataProviderException ex)
            {
                lookupFailures[lookup.LookupKey] = ex;
            }
        }

        var allQids = lookupResults.Values
            .SelectMany(qids => qids)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new Dictionary<string, BridgeResolutionResult>(StringComparer.Ordinal);
        var candidateEntitiesByLanguage = await FetchEntitiesByRequestLanguageAsync(
            allQids,
            requests,
            cancellationToken).ConfigureAwait(false);

        foreach (var request in requests)
        {
            var diagnostics = diagnosticsByKey[request.CorrelationKey];
            var identifiers = normalizedByKey[request.CorrelationKey];
            var language = request.Language ?? _ctx.Options.Language;
            var entities = candidateEntitiesByLanguage.TryGetValue(language, out var byQid)
                ? byQid
                : new Dictionary<string, WikidataEntityInfo>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var resolved = await ResolveOneAsync(
                    request,
                    identifiers,
                    lookupResults,
                    lookupFailures,
                    entities,
                    diagnostics,
                    stopwatch,
                    before,
                    cancellationToken).ConfigureAwait(false);
                result[request.CorrelationKey] = resolved;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (WikidataProviderException ex)
            {
                result[request.CorrelationKey] = BuildFailure(
                    request.CorrelationKey,
                    BridgeResolutionStatus.Failed,
                    ex.Kind,
                    ex.Message,
                    diagnostics,
                    stopwatch.Elapsed,
                    before);
            }
        }

        return result;
    }

    private async Task<BridgeResolutionResult> ResolveOneAsync(
        BridgeResolutionRequest request,
        IReadOnlyList<ResolvedBridgeIdentifier> identifiers,
        IReadOnlyDictionary<string, IReadOnlyList<string>> lookupResults,
        IReadOnlyDictionary<string, WikidataProviderException> lookupFailures,
        IReadOnlyDictionary<string, WikidataEntityInfo> prefetchedEntities,
        DiagnosticsBuilder diagnostics,
        Stopwatch stopwatch,
        WikidataDiagnosticsSnapshot before,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CorrelationKey))
        {
            return BuildFailure(
                request.CorrelationKey ?? "",
                BridgeResolutionStatus.InvalidRequest,
                WikidataFailureKind.MalformedResponse,
                "BridgeResolutionRequest.CorrelationKey is required.",
                diagnostics,
                stopwatch.Elapsed,
                before);
        }

        if (identifiers.Count == 0 && string.IsNullOrWhiteSpace(request.Title))
        {
            return BuildFailure(
                request.CorrelationKey,
                BridgeResolutionStatus.InvalidRequest,
                WikidataFailureKind.NotFound,
                "No recognized bridge IDs or title hint were supplied.",
                diagnostics,
                stopwatch.Elapsed,
                before);
        }

        var language = request.Language ?? _ctx.Options.Language;
        var hintLabels = await FetchHintLabelsAsync(
            request,
            GetCandidateEntities(identifiers, lookupResults, prefetchedEntities),
            language,
            cancellationToken).ConfigureAwait(false);

        var bridgeCandidates = BuildBridgeCandidates(
            request,
            identifiers,
            lookupResults,
            prefetchedEntities,
            diagnostics,
            hintLabels);

        if (bridgeCandidates.Count > 0)
        {
            return await BuildResolvedResultAsync(
                request,
                bridgeCandidates,
                BridgeResolutionStrategy.BridgeId,
                diagnostics,
                stopwatch.Elapsed,
                before,
                cancellationToken).ConfigureAwait(false);
        }

        var failedLookup = identifiers
            .Select(i => i.LookupKey)
            .Where(lookupFailures.ContainsKey)
            .Select(key => lookupFailures[key])
            .FirstOrDefault();

        if (failedLookup is not null && string.IsNullOrWhiteSpace(request.Title))
        {
            return BuildFailure(
                request.CorrelationKey,
                BridgeResolutionStatus.Failed,
                failedLookup.Kind,
                failedLookup.Message,
                diagnostics,
                stopwatch.Elapsed,
                before);
        }

        var fallback = await ResolveByTextFallbackAsync(
            request,
            diagnostics,
            cancellationToken).ConfigureAwait(false);

        if (fallback.Count > 0)
        {
            return await BuildResolvedResultAsync(
                request,
                fallback,
                BridgeResolutionStrategy.TextSearch,
                diagnostics,
                stopwatch.Elapsed,
                before,
                cancellationToken).ConfigureAwait(false);
        }

        if (failedLookup is not null)
        {
            return BuildFailure(
                request.CorrelationKey,
                BridgeResolutionStatus.Failed,
                failedLookup.Kind,
                failedLookup.Message,
                diagnostics,
                stopwatch.Elapsed,
                before);
        }

        return BuildFailure(
            request.CorrelationKey,
            BridgeResolutionStatus.NotFound,
            WikidataFailureKind.NotFound,
            "No Wikidata candidate matched the supplied bridge IDs or title hints.",
            diagnostics,
            stopwatch.Elapsed,
            before);
    }

    private async Task<Dictionary<string, Dictionary<string, WikidataEntityInfo>>> FetchEntitiesByRequestLanguageAsync(
        IReadOnlyList<string> qids,
        IReadOnlyList<BridgeResolutionRequest> requests,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, Dictionary<string, WikidataEntityInfo>>(StringComparer.OrdinalIgnoreCase);
        if (qids.Count == 0)
            return result;

        var languages = requests
            .Select(r => r.Language ?? _ctx.Options.Language)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var language in languages)
        {
            var fetched = await _ctx.EntityFetcher.FetchEntitiesAsync(qids, language, cancellationToken)
                .ConfigureAwait(false);
            result[language] = fetched.ToDictionary(
                kvp => kvp.Key,
                kvp => EntityMapper.MapEntity(kvp.Value, language),
                StringComparer.OrdinalIgnoreCase);
        }

        return result;
    }

    private static IReadOnlyList<WikidataEntityInfo> GetCandidateEntities(
        IReadOnlyList<ResolvedBridgeIdentifier> identifiers,
        IReadOnlyDictionary<string, IReadOnlyList<string>> lookupResults,
        IReadOnlyDictionary<string, WikidataEntityInfo> entities)
    {
        var qids = identifiers
            .Where(identifier => lookupResults.ContainsKey(identifier.LookupKey))
            .SelectMany(identifier => lookupResults[identifier.LookupKey])
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var result = new List<WikidataEntityInfo>();
        foreach (var qid in qids)
        {
            if (entities.TryGetValue(qid, out var entity))
                result.Add(entity);
        }

        return result;
    }

    private async Task<IReadOnlyDictionary<string, string?>> FetchHintLabelsAsync(
        BridgeResolutionRequest request,
        IEnumerable<WikidataEntityInfo> entities,
        string language,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Creator) && string.IsNullOrWhiteSpace(request.SeriesTitle))
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var qids = entities
            .SelectMany(entity => CreatorPropertyIds.Concat(SeriesPropertyIds)
                .SelectMany(propertyId => GetEntityIds(entity, propertyId)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return qids.Count == 0
            ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            : await FetchLabelsAsync(qids, language, cancellationToken).ConfigureAwait(false);
    }

    private List<BridgeCandidate> BuildBridgeCandidates(
        BridgeResolutionRequest request,
        IReadOnlyList<ResolvedBridgeIdentifier> identifiers,
        IReadOnlyDictionary<string, IReadOnlyList<string>> lookupResults,
        IReadOnlyDictionary<string, WikidataEntityInfo> entities,
        DiagnosticsBuilder diagnostics,
        IReadOnlyDictionary<string, string?> hintLabels)
    {
        var qidToMatches = new Dictionary<string, List<ResolvedBridgeIdentifier>>(StringComparer.OrdinalIgnoreCase);

        foreach (var identifier in identifiers)
        {
            if (!lookupResults.TryGetValue(identifier.LookupKey, out var qids) || qids.Count == 0)
                continue;

            diagnostics.MatchedProperties.Add(identifier.PropertyId);

            foreach (var qid in qids)
            {
                if (!qidToMatches.TryGetValue(qid, out var matches))
                {
                    matches = [];
                    qidToMatches[qid] = matches;
                }

                matches.Add(identifier);
            }
        }

        var candidates = new List<BridgeCandidate>();
        foreach (var (qid, matches) in qidToMatches)
        {
            if (!entities.TryGetValue(qid, out var entity))
            {
                diagnostics.RejectedCandidates.Add($"{qid}:entity-not-returned");
                continue;
            }

            var verifiedMatches = matches
                .Where(m => ClaimHasValue(entity, m.PropertyId, m.NormalizedValue))
                .ToList();

            if (verifiedMatches.Count == 0)
            {
                diagnostics.RejectedCandidates.Add($"{qid}:bridge-claim-not-verified");
                continue;
            }

            candidates.Add(BuildCandidate(request, entity, verifiedMatches, diagnostics, hintLabels));
        }

        return SortCandidates(candidates);
    }

    private BridgeCandidate BuildCandidate(
        BridgeResolutionRequest request,
        WikidataEntityInfo entity,
        IReadOnlyList<ResolvedBridgeIdentifier> matches,
        DiagnosticsBuilder diagnostics,
        IReadOnlyDictionary<string, string?> hintLabels)
    {
        var reasonCodes = new List<string> { "bridge.exact" };
        var warnings = new List<string>();
        var entityTypes = GetEntityIds(entity, _ctx.Options.TypePropertyId);
        var typeScore = ScoreMediaType(request.MediaKind, entityTypes, reasonCodes, warnings);
        var titleScore = ScoreTitle(request.Title, entity, reasonCodes, warnings);
        var creatorScore = ScoreLinkedEntityHint(
            request.Creator,
            entity,
            hintLabels,
            CreatorPropertyIds,
            "creator",
            reasonCodes,
            warnings,
            strongScore: 0.05,
            partialScore: 0.03);
        var seriesScore = ScoreLinkedEntityHint(
            request.SeriesTitle,
            entity,
            hintLabels,
            SeriesPropertyIds,
            "series",
            reasonCodes,
            warnings,
            strongScore: 0.04,
            partialScore: 0.02);
        var yearScore = ScoreYear(request.Year, entity, reasonCodes);
        var bridgeScore = Math.Min(0.74 + (matches.Count - 1) * 0.03, 0.82);
        var confidence = Math.Clamp(bridgeScore + typeScore + titleScore + creatorScore + seriesScore + yearScore, 0, 1);
        var firstMatch = matches[0];
        var collected = CollectKnownBridgeIds(entity, request, matches);

        foreach (var warning in warnings)
            diagnostics.Warnings.Add($"{entity.Id}:{warning}");

        return new BridgeCandidate
        {
            Qid = entity.Id,
            Label = entity.Label,
            Description = entity.Description,
            EntityTypes = entityTypes,
            MatchedBridgeIdType = firstMatch.RawKey,
            MatchedPropertyId = firstMatch.PropertyId,
            MatchedBridgeValue = firstMatch.NormalizedValue,
            Confidence = Math.Round(confidence, 4),
            ReasonCodes = reasonCodes,
            Warnings = warnings,
            CollectedBridgeIds = collected
        };
    }

    private async Task<IReadOnlyList<BridgeCandidate>> ResolveByTextFallbackAsync(
        BridgeResolutionRequest request,
        DiagnosticsBuilder diagnostics,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            return [];

        var language = request.Language ?? _ctx.Options.Language;
        var types = GetMediaTypeHints(request.MediaKind);

        try
        {
            var matches = await _reconcile.ReconcileAsync(new ReconciliationRequest
            {
                Query = request.Title,
                Types = types.Count > 0 ? types : null,
                Language = language,
                Limit = 5
            }, cancellationToken).ConfigureAwait(false);

            var qids = matches.Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (qids.Count == 0)
                return [];

            var fetched = await _ctx.EntityFetcher.FetchEntitiesAsync(qids, language, cancellationToken)
                .ConfigureAwait(false);
            var mapped = fetched.ToDictionary(
                kvp => kvp.Key,
                kvp => EntityMapper.MapEntity(kvp.Value, language),
                StringComparer.OrdinalIgnoreCase);
            var hintLabels = await FetchHintLabelsAsync(
                request,
                mapped.Values,
                language,
                cancellationToken).ConfigureAwait(false);

            var candidates = new List<BridgeCandidate>();
            foreach (var match in matches)
            {
                if (!mapped.TryGetValue(match.Id, out var entity))
                    continue;

                var reasonCodes = new List<string> { "text.fallback" };
                var warnings = new List<string>();
                var entityTypes = GetEntityIds(entity, _ctx.Options.TypePropertyId);
                var score = Math.Clamp(match.Score / 100.0, 0, 1);
                score += ScoreMediaType(request.MediaKind, entityTypes, reasonCodes, warnings);
                score += ScoreLinkedEntityHint(
                    request.Creator,
                    entity,
                    hintLabels,
                    CreatorPropertyIds,
                    "creator",
                    reasonCodes,
                    warnings,
                    strongScore: 0.05,
                    partialScore: 0.03);
                score += ScoreLinkedEntityHint(
                    request.SeriesTitle,
                    entity,
                    hintLabels,
                    SeriesPropertyIds,
                    "series",
                    reasonCodes,
                    warnings,
                    strongScore: 0.04,
                    partialScore: 0.02);
                score += ScoreYear(request.Year, entity, reasonCodes);

                candidates.Add(new BridgeCandidate
                {
                    Qid = entity.Id,
                    Label = entity.Label ?? match.Name,
                    Description = entity.Description ?? match.Description,
                    EntityTypes = entityTypes,
                    Confidence = Math.Round(Math.Clamp(score, 0, 1), 4),
                    ReasonCodes = reasonCodes,
                    Warnings = warnings,
                    CollectedBridgeIds = CollectKnownBridgeIds(entity, request, [])
                });
            }

            return SortCandidates(candidates);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WikidataProviderException ex)
        {
            diagnostics.Warnings.Add($"text-fallback-provider-failure:{ex.Kind}");
            return [];
        }
    }

    private async Task<BridgeResolutionResult> BuildResolvedResultAsync(
        BridgeResolutionRequest request,
        IReadOnlyList<BridgeCandidate> candidates,
        BridgeResolutionStrategy strategy,
        DiagnosticsBuilder diagnostics,
        TimeSpan providerLatency,
        WikidataDiagnosticsSnapshot before,
        CancellationToken cancellationToken)
    {
        var selected = candidates[0];
        if (candidates.Count > 1 && Math.Abs(candidates[0].Confidence - candidates[1].Confidence) < 0.02)
            diagnostics.Warnings.Add("candidate.ambiguous");

        var language = request.Language ?? _ctx.Options.Language;
        var entity = await FetchPublicEntityAsync(selected.Qid, language, cancellationToken).ConfigureAwait(false);
        var rollup = entity is null
            ? new CanonicalRollup
            {
                ResolvedEntityQid = selected.Qid,
                CanonicalWorkQid = selected.Qid,
                IsRollup = false
            }
            : BuildRollup(request, entity);

        var relatedQids = entity is null ? [] : GetRelationshipQids(entity);
        var labels = relatedQids.Count == 0
            ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            : await FetchLabelsAsync(relatedQids, language, cancellationToken).ConfigureAwait(false);

        var series = entity is null ? [] : ExtractSeries(entity, labels);
        var relationships = entity is null ? [] : ExtractRelationships(entity, labels);

        return new BridgeResolutionResult
        {
            CorrelationKey = request.CorrelationKey,
            Status = BridgeResolutionStatus.Resolved,
            MatchedBy = strategy,
            SelectedCandidate = selected,
            Candidates = candidates,
            Rollup = rollup,
            Series = series,
            Relationships = relationships,
            Diagnostics = diagnostics.Build(providerLatency, before, _ctx.Diagnostics.GetSnapshot())
        };
    }

    private async Task<WikidataEntityInfo?> FetchPublicEntityAsync(
        string qid,
        string language,
        CancellationToken cancellationToken)
    {
        var fetched = await _ctx.EntityFetcher.FetchEntitiesAsync([qid], language, cancellationToken)
            .ConfigureAwait(false);
        return fetched.TryGetValue(qid, out var entity)
            ? EntityMapper.MapEntity(entity, language)
            : null;
    }

    private CanonicalRollup BuildRollup(BridgeResolutionRequest request, WikidataEntityInfo entity)
    {
        var resolvedEntityQid = entity.Id;
        var canonicalWorkQid = entity.Id;
        var path = new List<BridgeRelationshipPathStep>();

        var parentWorks = GetEntityIds(entity, "P629");
        if (parentWorks.Count > 0 && request.RollupTarget != BridgeRollupTarget.ResolvedEntity)
        {
            canonicalWorkQid = parentWorks[0];
            if (request.RollupTarget == BridgeRollupTarget.PreferCanonicalWork)
                resolvedEntityQid = canonicalWorkQid;

            path.Add(new BridgeRelationshipPathStep
            {
                SubjectQid = entity.Id,
                PropertyId = "P629",
                ObjectQid = canonicalWorkQid,
                Direction = Direction.Outgoing
            });
        }
        else if (request.RollupTarget == BridgeRollupTarget.PreferEdition)
        {
            var editions = GetEntityIds(entity, "P747");
            if (editions.Count > 0)
            {
                canonicalWorkQid = entity.Id;
                resolvedEntityQid = editions.OrderBy(QidNumber).First();
                path.Add(new BridgeRelationshipPathStep
                {
                    SubjectQid = entity.Id,
                    PropertyId = "P747",
                    ObjectQid = resolvedEntityQid,
                    Direction = Direction.Outgoing
                });
            }
        }

        return new CanonicalRollup
        {
            ResolvedEntityQid = resolvedEntityQid,
            CanonicalWorkQid = canonicalWorkQid,
            IsRollup = path.Count > 0,
            RelationshipPath = path
        };
    }

    private static IReadOnlyList<BridgeSeriesInfo> ExtractSeries(
        WikidataEntityInfo entity,
        IReadOnlyDictionary<string, string?> labels)
    {
        var result = new List<BridgeSeriesInfo>();

        if (entity.Claims.TryGetValue("P179", out var seriesClaims))
        {
            foreach (var claim in seriesClaims)
            {
                var seriesQid = claim.Value?.EntityId;
                if (string.IsNullOrWhiteSpace(seriesQid))
                    continue;

                result.Add(new BridgeSeriesInfo
                {
                    SeriesQid = seriesQid,
                    SeriesLabel = labels.TryGetValue(seriesQid, out var label) ? label : null,
                    Position = TryGetQualifierValue(claim, "P1545") ?? GetFirstRawValue(entity, "P1545"),
                    PreviousQid = GetFirstEntityId(entity, "P155"),
                    NextQid = GetFirstEntityId(entity, "P156"),
                    SourcePropertyId = "P179",
                    Confidence = 1.0
                });
            }
        }

        var partOf = GetEntityIds(entity, "P361");
        foreach (var qid in partOf)
        {
            result.Add(new BridgeSeriesInfo
            {
                SeriesQid = qid,
                SeriesLabel = labels.TryGetValue(qid, out var label) ? label : null,
                Position = GetFirstRawValue(entity, "P1545"),
                PreviousQid = GetFirstEntityId(entity, "P155"),
                NextQid = GetFirstEntityId(entity, "P156"),
                SourcePropertyId = "P361",
                Confidence = 0.75
            });
        }

        return result;
    }

    private static IReadOnlyList<BridgeRelationshipEdge> ExtractRelationships(
        WikidataEntityInfo entity,
        IReadOnlyDictionary<string, string?> labels)
    {
        var result = new List<BridgeRelationshipEdge>();
        AddEdges(entity, labels, result, "P179", "series");
        AddEdges(entity, labels, result, "P1080", "universe");
        AddEdges(entity, labels, result, "P361", "parent-work");
        AddEdges(entity, labels, result, "P527", "has-part");
        AddEdges(entity, labels, result, "P155", "previous");
        AddEdges(entity, labels, result, "P156", "next");
        return result;
    }

    private static void AddEdges(
        WikidataEntityInfo entity,
        IReadOnlyDictionary<string, string?> labels,
        List<BridgeRelationshipEdge> result,
        string propertyId,
        string kind)
    {
        foreach (var objectQid in GetEntityIds(entity, propertyId))
        {
            result.Add(new BridgeRelationshipEdge
            {
                SubjectQid = entity.Id,
                PropertyId = propertyId,
                ObjectQid = objectQid,
                ObjectLabel = labels.TryGetValue(objectQid, out var label) ? label : null,
                RelationshipKind = kind,
                Confidence = 1.0
            });
        }
    }

    private async Task<IReadOnlyDictionary<string, string?>> FetchLabelsAsync(
        IReadOnlyList<string> qids,
        string language,
        CancellationToken cancellationToken)
    {
        var fetched = await _ctx.EntityFetcher.FetchLabelsOnlyAsync(qids, language, cancellationToken)
            .ConfigureAwait(false);

        return fetched.ToDictionary(
            kvp => kvp.Key,
            kvp =>
            {
                LanguageFallback.TryGetValue(kvp.Value.Labels, language, out var label);
                return string.IsNullOrWhiteSpace(label) ? null : label;
            },
            StringComparer.OrdinalIgnoreCase);
    }

    private static List<BridgeCandidate> SortCandidates(IEnumerable<BridgeCandidate> candidates)
    {
        return candidates
            .OrderByDescending(c => c.Confidence)
            .ThenBy(c => QidNumber(c.Qid))
            .ToList();
    }

    private static double ScoreMediaType(
        BridgeMediaKind mediaKind,
        IReadOnlyList<string> entityTypes,
        List<string> reasonCodes,
        List<string> warnings)
    {
        var expected = GetMediaTypeHints(mediaKind);
        if (expected.Count == 0)
        {
            reasonCodes.Add("type.unchecked");
            return 0;
        }

        if (entityTypes.Any(t => expected.Contains(t, StringComparer.OrdinalIgnoreCase)))
        {
            reasonCodes.Add("type.match");
            return 0.12;
        }

        if (entityTypes.Count == 0)
        {
            warnings.Add("type.missing");
            return 0;
        }

        warnings.Add("type.mismatch");
        return -0.10;
    }

    private static double ScoreTitle(
        string? title,
        WikidataEntityInfo entity,
        List<string> reasonCodes,
        List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(title))
            return 0;

        var labels = new List<string>();
        if (!string.IsNullOrWhiteSpace(entity.Label))
            labels.Add(entity.Label);
        labels.AddRange(entity.Aliases);

        var best = labels.Count == 0
            ? 0
            : labels.Max(label => FuzzyMatcher.TokenSortRatio(title, label));

        if (best >= 95)
        {
            reasonCodes.Add("title.exact");
            return 0.09;
        }

        if (best >= 85)
        {
            reasonCodes.Add("title.strong");
            return 0.06;
        }

        if (best >= 70)
        {
            reasonCodes.Add("title.partial");
            return 0.03;
        }

        warnings.Add("title.weak");
        return 0;
    }

    private static double ScoreLinkedEntityHint(
        string? hint,
        WikidataEntityInfo entity,
        IReadOnlyDictionary<string, string?> labels,
        IReadOnlyList<string> propertyIds,
        string reasonPrefix,
        List<string> reasonCodes,
        List<string> warnings,
        double strongScore,
        double partialScore)
    {
        if (string.IsNullOrWhiteSpace(hint))
            return 0;

        var candidateLabels = propertyIds
            .SelectMany(propertyId => GetEntityIds(entity, propertyId))
            .Select(qid => labels.TryGetValue(qid, out var label) ? label : null)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Select(label => label!)
            .ToList();

        if (candidateLabels.Count == 0)
            return 0;

        var best = candidateLabels.Max(label => FuzzyMatcher.TokenSortRatio(hint, label));
        if (best >= 90)
        {
            reasonCodes.Add($"{reasonPrefix}.strong");
            return strongScore;
        }

        if (best >= 75)
        {
            reasonCodes.Add($"{reasonPrefix}.partial");
            return partialScore;
        }

        warnings.Add($"{reasonPrefix}.weak");
        return 0;
    }

    private static double ScoreYear(int? year, WikidataEntityInfo entity, List<string> reasonCodes)
    {
        if (year is null)
            return 0;

        foreach (var propertyId in new[] { "P577", "P571", "P580" })
        {
            if (!entity.Claims.TryGetValue(propertyId, out var claims))
                continue;

            foreach (var claim in claims)
            {
                if (TryParseWikidataYear(claim.Value?.RawValue, out var candidateYear) &&
                    candidateYear == year.Value)
                {
                    reasonCodes.Add("year.match");
                    return 0.04;
                }
            }
        }

        return 0;
    }

    private static bool ClaimHasValue(WikidataEntityInfo entity, string propertyId, string normalizedValue)
    {
        if (!entity.Claims.TryGetValue(propertyId, out var claims))
            return false;

        return claims.Any(claim =>
            claim.Value is not null &&
            string.Equals(
                NormalizeClaimValue(propertyId, claim.Value.RawValue),
                normalizedValue,
                StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, string> CollectKnownBridgeIds(
        WikidataEntityInfo entity,
        BridgeResolutionRequest request,
        IReadOnlyList<ResolvedBridgeIdentifier> matches)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var match in matches)
        {
            var value = GetFirstRawValue(entity, match.PropertyId);
            if (!string.IsNullOrWhiteSpace(value))
                result[match.RawKey] = value;
        }

        var customProperties = request.CustomWikidataProperties?.Values ?? [];
        var propertyIds = BridgeIdCatalog.GetKnownPropertyIds(request.MediaKind)
            .Concat(customProperties)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var propertyId in propertyIds)
        {
            if (!entity.Claims.TryGetValue(propertyId, out var claims))
                continue;

            var value = claims.FirstOrDefault(c => c.Value is not null)?.Value?.RawValue;
            if (!string.IsNullOrWhiteSpace(value))
                result.TryAdd(propertyId, value);
        }

        return result;
    }

    private static IReadOnlyList<string> GetRelationshipQids(WikidataEntityInfo entity)
    {
        return new[] { "P179", "P1080", "P361", "P527", "P155", "P156", "P629", "P747" }
            .SelectMany(propertyId => GetEntityIds(entity, propertyId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> GetEntityIds(WikidataEntityInfo entity, string propertyId)
    {
        if (!entity.Claims.TryGetValue(propertyId, out var claims))
            return [];

        return claims
            .Select(c => c.Value?.EntityId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? GetFirstEntityId(WikidataEntityInfo entity, string propertyId)
        => GetEntityIds(entity, propertyId).FirstOrDefault();

    private static string? GetFirstRawValue(WikidataEntityInfo entity, string propertyId)
    {
        return entity.Claims.TryGetValue(propertyId, out var claims)
            ? claims.Select(c => c.Value?.RawValue).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
            : null;
    }

    private static string? TryGetQualifierValue(WikidataClaim claim, string propertyId)
    {
        return claim.Qualifiers.TryGetValue(propertyId, out var values)
            ? values.Select(v => v.RawValue).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
            : null;
    }

    private static string NormalizeClaimValue(string propertyId, string value)
    {
        var trimmed = value.Trim();
        return propertyId switch
        {
            "P212" or "P957" => new string(trimmed.Where(c => char.IsDigit(c) || c is 'X' or 'x').ToArray()).ToUpperInvariant(),
            "P345" => trimmed.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? trimmed.ToLowerInvariant() : $"tt{trimmed.PadLeft(7, '0')}",
            "P4947" or "P4983" or "P4835" or "P7043" or "P6395" or "P2281" or "P2850" or "P10110" or "P9586" or "P9751" or "P9750" or "P6381" or "P6398" or "P3861" => new string(trimmed.Where(char.IsDigit).ToArray()),
            "P435" or "P436" or "P5813" or "P4404" => trimmed.ToLowerInvariant(),
            "P648" => trimmed.ToUpperInvariant(),
            _ => trimmed
        };
    }

    private static bool TryParseWikidataYear(string? raw, out int year)
    {
        year = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var trimmed = raw.TrimStart('+');
        return trimmed.Length >= 4 && int.TryParse(trimmed[..4], out year);
    }

    private static IReadOnlyList<string> GetMediaTypeHints(BridgeMediaKind mediaKind)
    {
        return MediaTypeHints.TryGetValue(mediaKind, out var types) ? types : [];
    }

    private static long QidNumber(string qid)
    {
        return qid.Length > 1 && long.TryParse(qid.AsSpan(1), out var n)
            ? n
            : long.MaxValue;
    }

    private static BridgeResolutionResult BuildFailure(
        string correlationKey,
        BridgeResolutionStatus status,
        WikidataFailureKind failureKind,
        string message,
        DiagnosticsBuilder diagnostics,
        TimeSpan providerLatency,
        WikidataDiagnosticsSnapshot? before = null)
    {
        var after = before ?? new WikidataDiagnosticsSnapshot();
        return new BridgeResolutionResult
        {
            CorrelationKey = correlationKey,
            Status = status,
            FailureKind = failureKind,
            FailureMessage = message,
            Diagnostics = before is null
                ? diagnostics.Build(providerLatency, after, after)
                : diagnostics.Build(providerLatency, before, after)
        };
    }

    private sealed class DiagnosticsBuilder
    {
        public List<string> AttemptedStrategies { get; } = [];
        public List<string> MatchedProperties { get; } = [];
        public List<string> RejectedCandidates { get; } = [];
        public List<string> Warnings { get; } = [];

        public BridgeResolutionDiagnostics Build(
            TimeSpan providerLatency,
            WikidataDiagnosticsSnapshot before,
            WikidataDiagnosticsSnapshot after)
        {
            return new BridgeResolutionDiagnostics
            {
                AttemptedStrategies = AttemptedStrategies.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                MatchedProperties = MatchedProperties.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                RejectedCandidates = RejectedCandidates.ToList(),
                Warnings = Warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                ProviderLatency = providerLatency,
                CacheHits = Math.Max(0, after.CacheHits - before.CacheHits),
                CacheMisses = Math.Max(0, after.CacheMisses - before.CacheMisses),
                RetryCount = Math.Max(0, after.RetryCount - before.RetryCount),
                RateLimitResponses = Math.Max(0, after.RateLimitResponses - before.RateLimitResponses)
            };
        }
    }
}
