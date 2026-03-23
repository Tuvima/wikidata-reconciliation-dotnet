using System.Runtime.CompilerServices;
using Tuvima.WikidataReconciliation.Internal;
using Tuvima.WikidataReconciliation.Internal.Json;

namespace Tuvima.WikidataReconciliation;

/// <summary>
/// Reconciles text queries against Wikidata entities using dual-search,
/// fuzzy matching, type filtering, and property-based scoring.
/// </summary>
public sealed class WikidataReconciler : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly WikidataReconcilerOptions _options;
    private readonly WikidataSearchClient _searchClient;
    private readonly WikidataEntityFetcher _entityFetcher;
    private readonly ReconciliationScorer _scorer;
    private readonly TypeChecker _typeChecker;
    private readonly SemaphoreSlim _concurrencyLimiter;

    public WikidataReconciler()
        : this(new WikidataReconcilerOptions())
    {
    }

    public WikidataReconciler(WikidataReconcilerOptions options)
        : this(CreateHttpClient(options), options, ownsHttpClient: true)
    {
    }

    public WikidataReconciler(HttpClient httpClient)
        : this(httpClient, new WikidataReconcilerOptions(), ownsHttpClient: false)
    {
    }

    public WikidataReconciler(HttpClient httpClient, WikidataReconcilerOptions options)
        : this(httpClient, options, ownsHttpClient: false)
    {
    }

    private WikidataReconciler(HttpClient httpClient, WikidataReconcilerOptions options, bool ownsHttpClient)
    {
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        _options = options;

        var resilientClient = new ResilientHttpClient(httpClient, options.MaxRetries);
        _searchClient = new WikidataSearchClient(resilientClient, options);
        _entityFetcher = new WikidataEntityFetcher(resilientClient, options);
        _scorer = new ReconciliationScorer(options);
        _typeChecker = new TypeChecker(options.TypePropertyId);
        _concurrencyLimiter = new SemaphoreSlim(Math.Max(1, options.MaxConcurrency));
    }

    /// <summary>
    /// Reconciles a text query against Wikidata.
    /// </summary>
    public Task<IReadOnlyList<ReconciliationResult>> ReconcileAsync(
        string query, CancellationToken cancellationToken = default)
    {
        return ReconcileAsync(new ReconciliationRequest { Query = query }, cancellationToken);
    }

    /// <summary>
    /// Reconciles a text query with a type constraint.
    /// </summary>
    public Task<IReadOnlyList<ReconciliationResult>> ReconcileAsync(
        string query, string type, CancellationToken cancellationToken = default)
    {
        return ReconcileAsync(new ReconciliationRequest { Query = query, Type = type }, cancellationToken);
    }

    /// <summary>
    /// Reconciles a query with full options (type, properties, language, etc.).
    /// </summary>
    public async Task<IReadOnlyList<ReconciliationResult>> ReconcileAsync(
        ReconciliationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);

        var language = request.Language ?? _options.Language;
        var limit = request.Limit > 0 ? request.Limit : 5;

        // Step 1: Search for candidate entity IDs
        var candidateIds = await _searchClient.SearchAsync(request.Query, language, limit, cancellationToken)
            .ConfigureAwait(false);

        if (candidateIds.Count == 0)
            return [];

        // Step 2: Fetch entity data
        var entities = await _entityFetcher.FetchEntitiesAsync(candidateIds, language, cancellationToken)
            .ConfigureAwait(false);

        // Step 3: Score and filter candidates
        var scored = new List<(string Id, WikidataEntity Entity, ScoringResult Scoring, List<string> Types, TypeMatchResult TypeResult)>();

        foreach (var id in candidateIds)
        {
            if (!entities.TryGetValue(id, out var entity))
                continue;

            // Type checking
            var typeResult = _typeChecker.Check(entity, request.Type, request.ExcludeTypes);
            if (typeResult == TypeMatchResult.Excluded || typeResult == TypeMatchResult.NotMatched)
                continue;

            // Score the candidate
            var scoring = _scorer.Score(request.Query, entity, language, request.Properties);

            // Halve score for entities with no type when a type was requested
            var finalScore = scoring.Score;
            if (typeResult == TypeMatchResult.NoType && !string.IsNullOrEmpty(request.Type))
                finalScore /= 2.0;

            var types = WikidataEntityFetcher.GetTypeIds(entity, _options.TypePropertyId);
            scored.Add((id, entity, scoring with { Score = finalScore }, types, typeResult));
        }

        // Step 4: Sort by score descending, then by QID number ascending (tiebreaker)
        scored.Sort((a, b) =>
        {
            var cmp = b.Scoring.Score.CompareTo(a.Scoring.Score);
            if (cmp != 0) return cmp;
            return CompareQids(a.Id, b.Id);
        });

        // Step 5: Determine auto-match and build results
        var numProperties = request.Properties?.Count ?? 0;
        var results = new List<ReconciliationResult>();

        for (var i = 0; i < Math.Min(scored.Count, limit); i++)
        {
            var (id, entity, scoring, types, typeResult) = scored[i];
            double? secondBest = i == 0 && scored.Count > 1 ? scored[1].Scoring.Score : null;

            var label = entity.Labels?.TryGetValue(language, out var lv) == true ? lv.Value : id;
            var description = entity.Descriptions?.TryGetValue(language, out var dv) == true ? dv.Value : null;

            var typePenaltyApplied = typeResult == TypeMatchResult.NoType && !string.IsNullOrEmpty(request.Type);

            results.Add(new ReconciliationResult
            {
                Id = id,
                Name = label,
                Description = description,
                Score = Math.Round(scoring.Score, 2),
                Match = i == 0 && _scorer.IsAutoMatch(scoring.Score, secondBest, numProperties),
                Types = types.Count > 0 ? types : null,
                Breakdown = new ScoreBreakdown
                {
                    LabelScore = scoring.LabelScore,
                    PropertyScores = scoring.PropertyScores,
                    TypeMatched = string.IsNullOrEmpty(request.Type) ? null : typeResult == TypeMatchResult.Matched,
                    WeightedScore = Math.Round(scoring.WeightedScore, 2),
                    TypePenaltyApplied = typePenaltyApplied
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
        var results = new IReadOnlyList<ReconciliationResult>[requests.Count];

        var tasks = requests.Select((request, index) => ThrottledReconcileAsync(request, index, results, cancellationToken));
        await Task.WhenAll(tasks).ConfigureAwait(false);

        return results;
    }

    /// <summary>
    /// Reconciles multiple queries as a streaming async enumerable.
    /// Results are yielded as they complete, enabling progress reporting and reduced memory pressure.
    /// Each item is a tuple of the request index and its results.
    /// </summary>
    public async IAsyncEnumerable<(int Index, IReadOnlyList<ReconciliationResult> Results)> ReconcileBatchStreamAsync(
        IReadOnlyList<ReconciliationRequest> requests, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Create all tasks upfront
        var tasks = new Task<(int Index, IReadOnlyList<ReconciliationResult> Results)>[requests.Count];

        for (var i = 0; i < requests.Count; i++)
        {
            var index = i;
            var request = requests[i];
            tasks[i] = ThrottledReconcileWithIndexAsync(request, index, cancellationToken);
        }

        // Yield results as they complete
        var remaining = new HashSet<Task<(int Index, IReadOnlyList<ReconciliationResult> Results)>>(tasks);

        while (remaining.Count > 0)
        {
            var completed = await Task.WhenAny(remaining).ConfigureAwait(false);
            remaining.Remove(completed);
            yield return await completed.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Suggests Wikidata entities matching a text prefix. Useful for autocomplete/type-ahead UIs.
    /// Wraps the wbsearchentities API for lightweight, fast lookups.
    /// </summary>
    public async Task<IReadOnlyList<SuggestResult>> SuggestAsync(
        string prefix, int limit = 7, string? language = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var lang = language ?? _options.Language;
        var searchResults = await _searchClient.SuggestAsync(prefix, lang, limit, cancellationToken)
            .ConfigureAwait(false);

        return searchResults.Select(r => new SuggestResult
        {
            Id = r.Id,
            Name = r.Label ?? r.Id,
            Description = r.Description
        }).ToList();
    }

    private async Task ThrottledReconcileAsync(
        ReconciliationRequest request, int index,
        IReadOnlyList<ReconciliationResult>[] results,
        CancellationToken cancellationToken)
    {
        await _concurrencyLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            results[index] = await ReconcileAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _concurrencyLimiter.Release();
        }
    }

    private async Task<(int Index, IReadOnlyList<ReconciliationResult> Results)> ThrottledReconcileWithIndexAsync(
        ReconciliationRequest request, int index, CancellationToken cancellationToken)
    {
        await _concurrencyLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = await ReconcileAsync(request, cancellationToken).ConfigureAwait(false);
            return (index, results);
        }
        finally
        {
            _concurrencyLimiter.Release();
        }
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

    private static HttpClient CreateHttpClient(WikidataReconcilerOptions options)
    {
        var client = new HttpClient { Timeout = options.Timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        return client;
    }

    public void Dispose()
    {
        _concurrencyLimiter.Dispose();
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
