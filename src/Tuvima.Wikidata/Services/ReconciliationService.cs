using System.Runtime.CompilerServices;
using Tuvima.Wikidata.Internal;

namespace Tuvima.Wikidata.Services;

/// <summary>
/// Reconciliation and suggest (autocomplete) operations.
/// Obtained via <see cref="WikidataReconciler.Reconcile"/>.
/// </summary>
public sealed class ReconciliationService
{
    private readonly ReconcilerContext _ctx;

    internal ReconciliationService(ReconcilerContext ctx) => _ctx = ctx;

    /// <summary>
    /// Reconciles a text query against Wikidata.
    /// </summary>
    public Task<IReadOnlyList<ReconciliationResult>> ReconcileAsync(
        string query, CancellationToken cancellationToken = default)
        => ReconcileAsync(new ReconciliationRequest { Query = query }, cancellationToken);

    /// <summary>
    /// Reconciles a text query with a type constraint.
    /// </summary>
    public Task<IReadOnlyList<ReconciliationResult>> ReconcileAsync(
        string query, string type, CancellationToken cancellationToken = default)
        => ReconcileAsync(new ReconciliationRequest { Query = query, Types = [type] }, cancellationToken);

    /// <summary>
    /// Reconciles a query with full options (types, properties, language, etc.).
    /// </summary>
    public async Task<IReadOnlyList<ReconciliationResult>> ReconcileAsync(
        ReconciliationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);

        var options = _ctx.Options;
        var language = request.Language ?? options.Language;
        var limit = request.Limit > 0 ? request.Limit : 5;

        // Apply query cleaners (cleaned text for search, original for scoring)
        var searchQuery = request.Query;
        if (request.Cleaners is { Count: > 0 })
        {
            foreach (var cleaner in request.Cleaners)
                searchQuery = cleaner(searchQuery);

            if (string.IsNullOrWhiteSpace(searchQuery))
                searchQuery = request.Query;
        }

        var effectiveTypes = request.Types is { Count: > 0 } ? request.Types : null;
        var hasTypeConstraint = effectiveTypes is { Count: > 0 };

        // Resolve per-request subclass resolver
        var subclassResolver = _ctx.SubclassResolver;
        if (request.TypeHierarchyDepth.HasValue && request.TypeHierarchyDepth.Value > 0 && subclassResolver == null)
        {
            subclassResolver = new SubclassResolver(_ctx.EntityFetcher, request.TypeHierarchyDepth.Value);
        }

        // Step 1: Search for candidate entity IDs
        var useMultiLanguage = request.Languages is { Count: > 1 };
        var searchTasks = new List<Task<List<string>>>();

        if (useMultiLanguage)
        {
            searchTasks.Add(_ctx.SearchClient.SearchMultiLanguageAsync(searchQuery, request.Languages!,
                limit, request.DiacriticInsensitive, cancellationToken));
        }
        else
        {
            searchTasks.Add(_ctx.SearchClient.SearchAsync(searchQuery, language, limit,
                request.DiacriticInsensitive, cancellationToken));
        }

        if (hasTypeConstraint)
        {
            searchTasks.Add(_ctx.SearchClient.SearchWithTypeFilterAsync(
                searchQuery, language, effectiveTypes!, limit, cancellationToken));
        }

        await Task.WhenAll(searchTasks).ConfigureAwait(false);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidateIds = new List<string>();

        if (hasTypeConstraint)
        {
            foreach (var id in await searchTasks[^1].ConfigureAwait(false))
            {
                if (seen.Add(id))
                    candidateIds.Add(id);
            }
        }

        foreach (var id in await searchTasks[0].ConfigureAwait(false))
        {
            if (seen.Add(id))
                candidateIds.Add(id);
        }

        if (candidateIds.Count == 0)
            return [];

        // Step 2: Fetch entity data
        var entities = await _ctx.EntityFetcher.FetchEntitiesAllLanguagesAsync(
            candidateIds, options.IncludeSitelinkLabels, cancellationToken).ConfigureAwait(false);

        // Step 3: Score and filter candidates
        var scored = new List<(string Id, Internal.Json.WikidataEntity Entity, ScoringResult Scoring, List<string> Types, TypeMatchResult TypeResult)>();

        foreach (var id in candidateIds)
        {
            if (!entities.TryGetValue(id, out var entity))
                continue;

            var typeResult = await _ctx.TypeChecker.CheckAsync(
                entity, effectiveTypes, request.ExcludeTypes,
                subclassResolver, language, cancellationToken).ConfigureAwait(false);

            if (typeResult == TypeMatchResult.Excluded || typeResult == TypeMatchResult.NotMatched)
                continue;

            var scoring = await _ctx.Scorer.ScoreAsync(
                request.Query,
                entity,
                language,
                request.Properties,
                _ctx.EntityFetcher,
                cancellationToken,
                request.DiacriticInsensitive).ConfigureAwait(false);

            var finalScore = scoring.Score;
            if (typeResult == TypeMatchResult.NoType && hasTypeConstraint)
                finalScore /= 2.0;

            var types = WikidataEntityFetcher.GetTypeIds(entity, options.TypePropertyId);
            scored.Add((id, entity, scoring with { Score = finalScore }, types, typeResult));
        }

        scored.Sort((a, b) =>
        {
            var cmp = b.Scoring.Score.CompareTo(a.Scoring.Score);
            if (cmp != 0) return cmp;
            return CompareQids(a.Id, b.Id);
        });

        var numProperties = request.Properties?.Count ?? 0;
        var results = new List<ReconciliationResult>();

        for (var i = 0; i < Math.Min(scored.Count, limit); i++)
        {
            var (id, entity, scoring, types, typeResult) = scored[i];
            double? secondBest = i == 0 && scored.Count > 1 ? scored[1].Scoring.Score : null;

            LanguageFallback.TryGetValue(entity.Labels, language, out var label);
            LanguageFallback.TryGetValue(entity.Descriptions, language, out var description);

            var typePenaltyApplied = typeResult == TypeMatchResult.NoType && hasTypeConstraint;

            results.Add(new ReconciliationResult
            {
                Id = id,
                Name = string.IsNullOrEmpty(label) ? id : label,
                Description = string.IsNullOrEmpty(description) ? null : description,
                Score = Math.Round(scoring.Score, 2),
                Match = i == 0 && (scoring.UniqueIdMatch || _ctx.Scorer.IsAutoMatch(scoring.Score, secondBest, numProperties)),
                Types = types.Count > 0 ? types : null,
                MatchedLabel = scoring.MatchedLabel,
                Breakdown = new ScoreBreakdown
                {
                    LabelScore = scoring.LabelScore,
                    MatchedLabel = scoring.MatchedLabel,
                    PropertyScores = scoring.PropertyScores,
                    TypeMatched = !hasTypeConstraint ? null : typeResult == TypeMatchResult.Matched,
                    WeightedScore = Math.Round(scoring.WeightedScore, 2),
                    TypePenaltyApplied = typePenaltyApplied,
                    UniqueIdMatch = scoring.UniqueIdMatch
                }
            });
        }

        return results;
    }

    /// <summary>
    /// Reconciles multiple queries in parallel, respecting the configured concurrency limit.
    /// </summary>
    public async Task<IReadOnlyList<IReadOnlyList<ReconciliationResult>>> ReconcileBatchAsync(
        IReadOnlyList<ReconciliationRequest> requests, CancellationToken cancellationToken = default)
    {
        var tasks = requests.Select(request => ReconcileAsync(request, cancellationToken));
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Reconciles multiple queries as a streaming async enumerable.
    /// </summary>
    public async IAsyncEnumerable<(int Index, IReadOnlyList<ReconciliationResult> Results)> ReconcileBatchStreamAsync(
        IReadOnlyList<ReconciliationRequest> requests, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var tasks = new Task<(int Index, IReadOnlyList<ReconciliationResult> Results)>[requests.Count];

        for (var i = 0; i < requests.Count; i++)
        {
            var index = i;
            var request = requests[i];
            tasks[i] = ReconcileWithIndexAsync(request, index, cancellationToken);
        }

        var remaining = new HashSet<Task<(int Index, IReadOnlyList<ReconciliationResult> Results)>>(tasks);

        while (remaining.Count > 0)
        {
            var completed = await Task.WhenAny(remaining).ConfigureAwait(false);
            remaining.Remove(completed);
            yield return await completed.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Suggests Wikidata entities matching a text prefix.
    /// </summary>
    public async Task<IReadOnlyList<SuggestResult>> SuggestAsync(
        string prefix, int limit = 7, string? language = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var lang = language ?? _ctx.Options.Language;
        var searchResults = await _ctx.SearchClient.SuggestAsync(prefix, lang, limit, cancellationToken)
            .ConfigureAwait(false);

        return searchResults.Select(r => new SuggestResult
        {
            Id = r.Id,
            Name = r.Label ?? r.Id,
            Description = r.Description
        }).ToList();
    }

    /// <summary>
    /// Suggests Wikidata properties matching a text prefix.
    /// </summary>
    public async Task<IReadOnlyList<SuggestResult>> SuggestPropertiesAsync(
        string prefix, int limit = 7, string? language = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var lang = language ?? _ctx.Options.Language;
        var searchResults = await _ctx.SearchClient.SuggestPropertiesAsync(prefix, lang, limit, cancellationToken)
            .ConfigureAwait(false);

        return searchResults.Select(r => new SuggestResult
        {
            Id = r.Id,
            Name = r.Label ?? r.Id,
            Description = r.Description
        }).ToList();
    }

    /// <summary>
    /// Suggests Wikidata types/classes matching a text prefix.
    /// </summary>
    public Task<IReadOnlyList<SuggestResult>> SuggestTypesAsync(
        string prefix, int limit = 7, string? language = null, CancellationToken cancellationToken = default)
        => SuggestAsync(prefix, limit, language, cancellationToken);

    private async Task<(int Index, IReadOnlyList<ReconciliationResult> Results)> ReconcileWithIndexAsync(
        ReconciliationRequest request, int index, CancellationToken cancellationToken)
    {
        var results = await ReconcileAsync(request, cancellationToken).ConfigureAwait(false);
        return (index, results);
    }

    private static int CompareQids(string a, string b)
    {
        if (a.Length > 1 && b.Length > 1 &&
            long.TryParse(a.AsSpan(1), out var numA) &&
            long.TryParse(b.AsSpan(1), out var numB))
        {
            return numA.CompareTo(numB);
        }

        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
