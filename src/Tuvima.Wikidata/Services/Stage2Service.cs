using Tuvima.Wikidata.Internal;

namespace Tuvima.Wikidata.Services;

/// <summary>
/// Unified Stage 2 resolver. Accepts <see cref="BridgeStage2Request"/>, <see cref="MusicStage2Request"/>,
/// or <see cref="TextStage2Request"/> via the marker interface <see cref="IStage2Request"/>, picks the
/// right strategy based on the concrete request type (no auto-detect heuristics), and returns a
/// <see cref="Stage2Result"/>. Batch operations group identical requests by natural key so that N
/// callers submitting the same bridge / album / text query share a single API round-trip.
/// Obtained via <see cref="WikidataReconciler.Stage2"/>.
/// </summary>
public sealed class Stage2Service
{
    private readonly ReconcilerContext _ctx;
    private readonly ReconciliationService _reconcile;
    private readonly EditionService _editions;
    private readonly AuthorsService _authors;
    private readonly PersonsService _persons;

    internal Stage2Service(
        ReconcilerContext ctx,
        ReconciliationService reconcile,
        EditionService editions,
        AuthorsService authors,
        PersonsService persons)
    {
        _ctx = ctx;
        _reconcile = reconcile;
        _editions = editions;
        _authors = authors;
        _persons = persons;
    }

    /// <summary>
    /// Resolves a single Stage 2 request. Equivalent to passing a one-element list to
    /// <see cref="ResolveBatchAsync"/> and returning that one result.
    /// </summary>
    public async Task<Stage2Result> ResolveAsync(
        IStage2Request request,
        CancellationToken cancellationToken = default)
    {
        var batch = await ResolveBatchAsync([request], cancellationToken).ConfigureAwait(false);
        return batch.TryGetValue(request.CorrelationKey, out var result) ? result : Stage2Result.NotFound;
    }

    /// <summary>
    /// Resolves a batch of Stage 2 requests. Groups requests by natural key so identical
    /// queries share a single round-trip, then fans out results by correlation key.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, Stage2Result>> ResolveBatchAsync(
        IReadOnlyList<IStage2Request> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
            return new Dictionary<string, Stage2Result>();

        var results = new Dictionary<string, Stage2Result>(StringComparer.Ordinal);

        // Group requests by concrete type for independent resolution phases.
        var bridgeRequests = requests.OfType<BridgeStage2Request>().ToList();
        var musicRequests = requests.OfType<MusicStage2Request>().ToList();
        var textRequests = requests.OfType<TextStage2Request>().ToList();

        // Validate that every request is one of the known types (the interface is sealed by convention).
        foreach (var req in requests)
        {
            if (req is not (BridgeStage2Request or MusicStage2Request or TextStage2Request))
            {
                throw new NotSupportedException(
                    $"IStage2Request implementation {req.GetType().FullName} is not supported. " +
                    "Use BridgeStage2Request, MusicStage2Request, or TextStage2Request.");
            }
        }

        var bridgeGroups = bridgeRequests
            .GroupBy(r => BridgeGroupKey(r), StringComparer.OrdinalIgnoreCase)
            .ToList();
        var musicGroups = musicRequests
            .GroupBy(r => MusicGroupKey(r), StringComparer.OrdinalIgnoreCase)
            .ToList();
        var textGroups = textRequests
            .GroupBy(r => TextGroupKey(r), StringComparer.OrdinalIgnoreCase)
            .ToList();
        var resolutionTasks = new List<Task<IReadOnlyList<(string CorrelationKey, Stage2Result Result)>>>(
            bridgeGroups.Count() + musicGroups.Count() + textGroups.Count());

        resolutionTasks.AddRange(bridgeGroups.Select(group => ResolveGroupAsync(
            group.Select(req => req.CorrelationKey),
            () => ResolveBridgeAsync(group.First(), cancellationToken))));

        resolutionTasks.AddRange(musicGroups.Select(group => ResolveGroupAsync(
            group.Select(req => req.CorrelationKey),
            () => ResolveMusicAsync(group.First(), cancellationToken))));

        resolutionTasks.AddRange(textGroups.Select(group => ResolveGroupAsync(
            group.Select(req => req.CorrelationKey),
            () => ResolveTextAsync(group.First(), cancellationToken))));

        var resolvedGroups = await Task.WhenAll(resolutionTasks).ConfigureAwait(false);
        foreach (var groupResults in resolvedGroups)
        {
            foreach (var (correlationKey, result) in groupResults)
                results[correlationKey] = result;
        }

        return results;
    }

    // ─── Grouping keys ───────────────────────────────────────────────

    private static string BridgeGroupKey(BridgeStage2Request req)
    {
        // Natural key = first non-empty bridge ID in preferred order.
        var order = req.PreferredOrder ?? req.BridgeIds.Keys.ToList();
        foreach (var key in order)
        {
            if (req.BridgeIds.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
                return $"bridge|{key}|{value}";
        }
        return $"bridge|empty|{req.CorrelationKey}"; // unique fallback so empty requests don't collide
    }

    private static string MusicGroupKey(MusicStage2Request req)
    {
        var title = req.AlbumTitle.Trim();
        var artist = req.Artist?.Trim() ?? "";
        return $"music|{title}|{artist}";
    }

    private static string TextGroupKey(TextStage2Request req)
    {
        var title = req.Title.Trim();
        var author = req.Author?.Trim() ?? "";
        var sortedTypes = req.CirrusSearchTypes.OrderBy(t => t, StringComparer.Ordinal);
        return $"text|{title}|{author}|{string.Join(',', sortedTypes)}";
    }

    // ─── Bridge resolution ───────────────────────────────────────────

    private async Task<Stage2Result> ResolveBridgeAsync(
        BridgeStage2Request req, CancellationToken ct)
    {
        var language = req.Language ?? _ctx.Options.Language;
        var order = req.PreferredOrder ?? req.BridgeIds.Keys.ToList();

        string? resolvedQid = null;
        string? matchedKey = null;

        foreach (var key in order)
        {
            if (!req.BridgeIds.TryGetValue(key, out var value) || string.IsNullOrEmpty(value))
                continue;
            if (!req.WikidataProperties.TryGetValue(key, out var propertyId) || string.IsNullOrEmpty(propertyId))
                continue;

            var hits = await _ctx.SearchClient.SearchByExternalIdAsync(propertyId, value, 10, ct)
                .ConfigureAwait(false);

            if (hits.Count > 0)
            {
                resolvedQid = hits[0];
                matchedKey = key;
                break;
            }
        }

        if (resolvedQid is null)
            return Stage2Result.NotFound;

        // Fetch the resolved entity so we can collect verified bridge IDs and decide on pivoting.
        var entityMap = await _ctx.EntityFetcher.FetchEntitiesAsync([resolvedQid], language, ct)
            .ConfigureAwait(false);
        if (!entityMap.TryGetValue(resolvedQid, out var entity))
            return Stage2Result.NotFound;

        var entityTypes = WikidataEntityFetcher.GetTypeIds(entity, _ctx.Options.TypePropertyId);
        var collected = CollectVerifiedBridgeIds(entity, req);
        LanguageFallback.TryGetValue(entity.Labels, language, out var resolvedLabel);

        // Determine whether this is an edition and whether we should pivot.
        var workQid = resolvedQid;
        string? editionQid = null;
        var isEdition = false;
        var label = string.IsNullOrEmpty(resolvedLabel) ? resolvedQid : resolvedLabel;

        if (req.EditionPivot is { } pivot)
        {
            var editionSet = new HashSet<string>(pivot.EditionClasses, StringComparer.OrdinalIgnoreCase);
            var workSet = new HashSet<string>(pivot.WorkClasses, StringComparer.OrdinalIgnoreCase);

            isEdition = entityTypes.Any(t => editionSet.Contains(t));
            var isWork = entityTypes.Any(t => workSet.Contains(t));

            if (isEdition)
            {
                // Edition → walk P629 to the parent work.
                editionQid = resolvedQid;
                var work = await _editions.GetWorkForEditionAsync(resolvedQid, language, ct).ConfigureAwait(false);
                if (work is not null)
                {
                    workQid = work.Id;
                    label = work.Label ?? label;
                }
            }
            else if (isWork && pivot.PreferEdition)
            {
                // Work → walk P747 to editions, rank by hints.
                var editions = await _editions.GetEditionsAsync(resolvedQid, filterTypes: null, language, ct)
                    .ConfigureAwait(false);

                if (editions.Count > 0)
                {
                    var best = PickBestEdition(editions, pivot.RankingHints);
                    if (best is not null)
                    {
                        editionQid = best.EntityId;
                        isEdition = true;
                        // workQid stays on the original work.
                        label = best.Label ?? label;
                    }
                }
            }
        }

        return new Stage2Result
        {
            Found = true,
            Qid = isEdition ? (editionQid ?? resolvedQid) : workQid,
            WorkQid = workQid,
            EditionQid = editionQid,
            IsEdition = isEdition,
            MatchedBy = Stage2MatchedStrategy.BridgeId,
            PrimaryBridgeIdType = matchedKey,
            CollectedBridgeIds = collected,
            Label = label
        };
    }

    private static IReadOnlyDictionary<string, string> CollectVerifiedBridgeIds(
        Internal.Json.WikidataEntity entity, BridgeStage2Request req)
    {
        var collected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, propertyId) in req.WikidataProperties)
        {
            var claimValues = WikidataEntityFetcher.GetClaimValues(entity, propertyId);
            if (claimValues.Count == 0)
                continue;

            var first = EntityMapper.MapDataValue(claimValues[0], "external-id");
            if (!string.IsNullOrEmpty(first.RawValue))
                collected[key] = first.RawValue;
        }

        return collected;
    }

    private EditionInfo? PickBestEdition(
        IReadOnlyList<EditionInfo> editions,
        IReadOnlyList<RankingHint>? hints)
    {
        if (hints is null || hints.Count == 0)
        {
            // Deterministic: sort by QID number asc.
            return editions.OrderBy(e => QidNumber(e.EntityId)).FirstOrDefault();
        }

        // Epsilon for float-score tiebreaking. Ranking hint scores come from integer fuzzy
        // ratios divided by float weights so exact-equality on two different candidates is
        // possible but not guaranteed — use a tolerance rather than strict equality.
        const double ScoreEpsilon = 1e-9;

        EditionInfo? best = null;
        double bestScore = double.MinValue;

        foreach (var edition in editions)
        {
            double score = 0.0;
            double weightSum = 0.0;

            foreach (var hint in hints)
            {
                if (!edition.Claims.TryGetValue(hint.PropertyId, out var claims) || claims.Count == 0)
                    continue;

                int bestHintScore = 0;
                foreach (var claim in claims)
                {
                    if (claim.Value is null)
                        continue;

                    var candidateText = claim.Value.EntityLabel ?? claim.Value.RawValue ?? "";
                    foreach (var target in hint.Values)
                    {
                        var ratio = FuzzyMatcher.TokenSortRatio(candidateText, target);
                        if (ratio > bestHintScore)
                            bestHintScore = ratio;
                    }
                }

                score += bestHintScore * hint.Weight;
                weightSum += hint.Weight;
            }

            var normalizedScore = weightSum > 0 ? score / weightSum : 0.0;

            if (normalizedScore > bestScore + ScoreEpsilon)
            {
                // Strictly better than the current best.
                bestScore = normalizedScore;
                best = edition;
            }
            else if (best is not null &&
                     Math.Abs(normalizedScore - bestScore) < ScoreEpsilon &&
                     QidNumber(edition.EntityId) < QidNumber(best.EntityId))
            {
                // Tied within epsilon — QID number ascending as deterministic tiebreaker.
                best = edition;
            }
        }

        return best;
    }

    private static long QidNumber(string qid)
    {
        if (qid.Length > 1 && long.TryParse(qid.AsSpan(1), out var n))
            return n;
        return long.MaxValue;
    }

    // ─── Music resolution ────────────────────────────────────────────

    private async Task<Stage2Result> ResolveMusicAsync(MusicStage2Request req, CancellationToken ct)
    {
        var language = req.Language ?? _ctx.Options.Language;

        var constraints = new List<PropertyConstraint>();
        if (!string.IsNullOrWhiteSpace(req.Artist))
        {
            var artistConstraint = await ResolveArtistConstraintAsync(req.Artist, language, ct)
                .ConfigureAwait(false);
            if (artistConstraint is not null)
                constraints.Add(artistConstraint);
        }

        var reconcileRequest = new ReconciliationRequest
        {
            Query = req.AlbumTitle,
            Types = ["Q482994"],  // music album
            Properties = constraints.Count > 0 ? constraints : null,
            Language = language,
            Limit = 3
        };

        var matches = await _reconcile.ReconcileAsync(reconcileRequest, ct).ConfigureAwait(false);
        if (matches.Count == 0)
            return Stage2Result.NotFound;

        var best = matches[0];
        return new Stage2Result
        {
            Found = true,
            Qid = best.Id,
            WorkQid = best.Id,
            MatchedBy = Stage2MatchedStrategy.MusicAlbum,
            Label = best.Name
        };
    }

    // ─── Text resolution ─────────────────────────────────────────────

    private async Task<Stage2Result> ResolveTextAsync(TextStage2Request req, CancellationToken ct)
    {
        if (req.CirrusSearchTypes.Count == 0 && !req.AllowUnfilteredText)
        {
            throw new ArgumentException(
                "TextStage2Request.CirrusSearchTypes is empty. Set AllowUnfilteredText = true " +
                "to explicitly opt into running text reconciliation without a type filter.",
                nameof(req));
        }

        var language = req.Language ?? _ctx.Options.Language;

        var constraints = new List<PropertyConstraint>();
        if (!string.IsNullOrWhiteSpace(req.Author))
        {
            var authorConstraint = await ResolveAuthorConstraintAsync(req.Author, language, ct)
                .ConfigureAwait(false);
            if (authorConstraint is not null)
                constraints.Add(authorConstraint);
        }

        var reconcileRequest = new ReconciliationRequest
        {
            Query = req.Title,
            Types = req.CirrusSearchTypes.Count > 0 ? req.CirrusSearchTypes : null,
            Properties = constraints.Count > 0 ? constraints : null,
            Language = language,
            Limit = 5,
            Cleaners = req.QueryCleaners
        };

        var matches = await _reconcile.ReconcileAsync(reconcileRequest, ct).ConfigureAwait(false);
        if (matches.Count == 0)
            return Stage2Result.NotFound;

        var best = matches[0];
        var normalized = best.Score / 100.0;

        if (normalized < req.AcceptThreshold)
            return Stage2Result.NotFound;

        return new Stage2Result
        {
            Found = true,
            Qid = best.Id,
            WorkQid = best.Id,
            MatchedBy = Stage2MatchedStrategy.TextReconciliation,
            Label = best.Name
        };
    }

    private async Task<PropertyConstraint?> ResolveArtistConstraintAsync(
        string artist,
        string language,
        CancellationToken cancellationToken)
    {
        var result = await _persons.SearchAsync(new PersonSearchRequest
        {
            Name = artist,
            Role = PersonRole.Performer,
            Language = language
        }, cancellationToken).ConfigureAwait(false);

        return result.Found && !string.IsNullOrEmpty(result.Qid)
            ? new PropertyConstraint("P175", result.Qid)
            : null;
    }

    private async Task<PropertyConstraint?> ResolveAuthorConstraintAsync(
        string author,
        string language,
        CancellationToken cancellationToken)
    {
        var result = await _authors.ResolveAsync(new AuthorResolutionRequest
        {
            RawAuthorString = author,
            Language = language
        }, cancellationToken).ConfigureAwait(false);

        var authorQids = result.Authors
            .Where(a => !string.IsNullOrEmpty(a.Qid))
            .Select(a => a.Qid!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return authorQids.Count > 0
            ? new PropertyConstraint("P50", authorQids)
            : null;
    }

    private static async Task<IReadOnlyList<(string CorrelationKey, Stage2Result Result)>> ResolveGroupAsync(
        IEnumerable<string> correlationKeys,
        Func<Task<Stage2Result>> resolveAsync)
    {
        var result = await resolveAsync().ConfigureAwait(false);
        return correlationKeys
            .Select(key => (key, result))
            .ToList();
    }
}
