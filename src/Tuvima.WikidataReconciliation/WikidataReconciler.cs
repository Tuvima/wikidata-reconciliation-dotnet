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
        _searchClient = new WikidataSearchClient(httpClient, options);
        _entityFetcher = new WikidataEntityFetcher(httpClient, options);
        _scorer = new ReconciliationScorer(options);
        _typeChecker = new TypeChecker(options.TypePropertyId);
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
        var scored = new List<(string Id, WikidataEntity Entity, double Score, List<string> Types)>();

        foreach (var id in candidateIds)
        {
            if (!entities.TryGetValue(id, out var entity))
                continue;

            // Type checking
            var typeResult = _typeChecker.Check(entity, request.Type, request.ExcludeTypes);
            if (typeResult == TypeMatchResult.Excluded || typeResult == TypeMatchResult.NotMatched)
                continue;

            // Score the candidate
            var score = _scorer.Score(request.Query, entity, language, request.Properties);

            // Halve score for entities with no type when a type was requested
            if (typeResult == TypeMatchResult.NoType && !string.IsNullOrEmpty(request.Type))
                score /= 2.0;

            var types = WikidataEntityFetcher.GetTypeIds(entity, _options.TypePropertyId);
            scored.Add((id, entity, score, types));
        }

        // Step 4: Sort by score descending, then by QID number ascending (tiebreaker)
        scored.Sort((a, b) =>
        {
            var cmp = b.Score.CompareTo(a.Score);
            if (cmp != 0) return cmp;
            return CompareQids(a.Id, b.Id);
        });

        // Step 5: Determine auto-match and build results
        var numProperties = request.Properties?.Count ?? 0;
        var results = new List<ReconciliationResult>();

        for (var i = 0; i < Math.Min(scored.Count, limit); i++)
        {
            var (id, entity, score, types) = scored[i];
            double? secondBest = i == 0 && scored.Count > 1 ? scored[1].Score : null;

            var label = entity.Labels?.TryGetValue(language, out var lv) == true ? lv.Value : id;
            var description = entity.Descriptions?.TryGetValue(language, out var dv) == true ? dv.Value : null;

            results.Add(new ReconciliationResult
            {
                Id = id,
                Name = label,
                Description = description,
                Score = Math.Round(score, 2),
                Match = i == 0 && _scorer.IsAutoMatch(score, secondBest, numProperties),
                Types = types.Count > 0 ? types : null
            });
        }

        return results;
    }

    /// <summary>
    /// Reconciles multiple queries in parallel.
    /// </summary>
    public async Task<IReadOnlyList<IReadOnlyList<ReconciliationResult>>> ReconcileBatchAsync(
        IReadOnlyList<ReconciliationRequest> requests, CancellationToken cancellationToken = default)
    {
        var tasks = requests.Select(r => ReconcileAsync(r, cancellationToken)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    private static int CompareQids(string a, string b)
    {
        // Compare QIDs numerically (Q42 < Q100)
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
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
