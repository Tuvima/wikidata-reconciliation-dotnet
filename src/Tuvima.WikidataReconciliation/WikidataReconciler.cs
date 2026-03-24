using System.Runtime.CompilerServices;
using Tuvima.WikidataReconciliation.Internal;
using Tuvima.WikidataReconciliation.Internal.Json;

namespace Tuvima.WikidataReconciliation;

/// <summary>
/// Reconciles text queries against Wikidata entities using dual-search,
/// fuzzy matching, type filtering, and property-based scoring.
/// Also provides entity/property fetching and Wikipedia URL resolution.
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
    private readonly SubclassResolver? _subclassResolver;
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

        var resilientClient = new ResilientHttpClient(httpClient, options.MaxRetries, options.MaxLag);
        _searchClient = new WikidataSearchClient(resilientClient, options);
        _entityFetcher = new WikidataEntityFetcher(resilientClient, options);
        _scorer = new ReconciliationScorer(options);
        _typeChecker = new TypeChecker(options.TypePropertyId);
        _subclassResolver = options.TypeHierarchyDepth > 0
            ? new SubclassResolver(_entityFetcher, options.TypeHierarchyDepth)
            : null;
        _concurrencyLimiter = new SemaphoreSlim(Math.Max(1, options.MaxConcurrency));
    }

    // ─── Reconciliation ─────────────────────────────────────────────

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

        // Step 2: Fetch entity data (all languages for cross-language label scoring)
        var entities = await _entityFetcher.FetchEntitiesAllLanguagesAsync(candidateIds, cancellationToken)
            .ConfigureAwait(false);

        // Step 3: Score and filter candidates
        var scored = new List<(string Id, WikidataEntity Entity, ScoringResult Scoring, List<string> Types, TypeMatchResult TypeResult)>();

        foreach (var id in candidateIds)
        {
            if (!entities.TryGetValue(id, out var entity))
                continue;

            // Type checking (async for P279 support)
            var typeResult = await _typeChecker.CheckAsync(
                entity, request.Type, request.ExcludeTypes,
                _subclassResolver, language, cancellationToken).ConfigureAwait(false);

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

            LanguageFallback.TryGetValue(entity.Labels, language, out var label);
            LanguageFallback.TryGetValue(entity.Descriptions, language, out var description);

            var typePenaltyApplied = typeResult == TypeMatchResult.NoType && !string.IsNullOrEmpty(request.Type);

            results.Add(new ReconciliationResult
            {
                Id = id,
                Name = string.IsNullOrEmpty(label) ? id : label,
                Description = string.IsNullOrEmpty(description) ? null : description,
                Score = Math.Round(scoring.Score, 2),
                Match = i == 0 && (scoring.UniqueIdMatch || _scorer.IsAutoMatch(scoring.Score, secondBest, numProperties)),
                Types = types.Count > 0 ? types : null,
                MatchedLabel = scoring.MatchedLabel,
                Breakdown = new ScoreBreakdown
                {
                    LabelScore = scoring.LabelScore,
                    MatchedLabel = scoring.MatchedLabel,
                    PropertyScores = scoring.PropertyScores,
                    TypeMatched = string.IsNullOrEmpty(request.Type) ? null : typeResult == TypeMatchResult.Matched,
                    WeightedScore = Math.Round(scoring.WeightedScore, 2),
                    TypePenaltyApplied = typePenaltyApplied,
                    UniqueIdMatch = scoring.UniqueIdMatch
                }
            });
        }

        return results;
    }

    // ─── Batch Reconciliation ───────────────────────────────────────

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
    /// </summary>
    public async IAsyncEnumerable<(int Index, IReadOnlyList<ReconciliationResult> Results)> ReconcileBatchStreamAsync(
        IReadOnlyList<ReconciliationRequest> requests, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var tasks = new Task<(int Index, IReadOnlyList<ReconciliationResult> Results)>[requests.Count];

        for (var i = 0; i < requests.Count; i++)
        {
            var index = i;
            var request = requests[i];
            tasks[i] = ThrottledReconcileWithIndexAsync(request, index, cancellationToken);
        }

        var remaining = new HashSet<Task<(int Index, IReadOnlyList<ReconciliationResult> Results)>>(tasks);

        while (remaining.Count > 0)
        {
            var completed = await Task.WhenAny(remaining).ConfigureAwait(false);
            remaining.Remove(completed);
            yield return await completed.ConfigureAwait(false);
        }
    }

    // ─── Suggest / Autocomplete ─────────────────────────────────────

    /// <summary>
    /// Suggests Wikidata entities matching a text prefix. Useful for autocomplete/type-ahead UIs.
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

    /// <summary>
    /// Suggests Wikidata properties matching a text prefix. Useful for building UIs
    /// where users select properties like "date of birth" or "country of citizenship".
    /// </summary>
    public async Task<IReadOnlyList<SuggestResult>> SuggestPropertiesAsync(
        string prefix, int limit = 7, string? language = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var lang = language ?? _options.Language;
        var searchResults = await _searchClient.SuggestPropertiesAsync(prefix, lang, limit, cancellationToken)
            .ConfigureAwait(false);

        return searchResults.Select(r => new SuggestResult
        {
            Id = r.Id,
            Name = r.Label ?? r.Id,
            Description = r.Description
        }).ToList();
    }

    /// <summary>
    /// Suggests Wikidata types/classes matching a text prefix. Useful for building UIs
    /// where users select entity types like "book", "human", or "city".
    /// Types are regular Wikidata items used as P31 (instance of) values.
    /// </summary>
    public async Task<IReadOnlyList<SuggestResult>> SuggestTypesAsync(
        string prefix, int limit = 7, string? language = null, CancellationToken cancellationToken = default)
    {
        // Types are just regular Wikidata items — reuse entity suggest
        return await SuggestAsync(prefix, limit, language, cancellationToken).ConfigureAwait(false);
    }

    // ─── Entity / Property Fetching (Data Extension) ────────────────

    /// <summary>
    /// Fetches full entity data for the given QIDs, including labels, descriptions, aliases,
    /// and all claims with qualifiers.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, WikidataEntityInfo>> GetEntitiesAsync(
        IReadOnlyList<string> qids, string? language = null, CancellationToken cancellationToken = default)
    {
        var lang = language ?? _options.Language;
        var entities = await _entityFetcher.FetchEntitiesAsync(qids, lang, cancellationToken)
            .ConfigureAwait(false);

        var result = new Dictionary<string, WikidataEntityInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, entity) in entities)
        {
            result[id] = EntityMapper.MapEntity(entity, lang);
        }

        return result;
    }

    /// <summary>
    /// Fetches specific properties for the given QIDs.
    /// Returns only the requested property claims for each entity.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<WikidataClaim>>>> GetPropertiesAsync(
        IReadOnlyList<string> qids, IReadOnlyList<string> propertyIds,
        string? language = null, CancellationToken cancellationToken = default)
    {
        var lang = language ?? _options.Language;
        var entities = await _entityFetcher.FetchEntitiesAsync(qids, lang, cancellationToken)
            .ConfigureAwait(false);

        var propertySet = new HashSet<string>(propertyIds, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<WikidataClaim>>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, entity) in entities)
        {
            var allClaims = EntityMapper.MapClaims(entity.Claims);
            var filtered = new Dictionary<string, IReadOnlyList<WikidataClaim>>();

            foreach (var (propId, claims) in allClaims)
            {
                if (propertySet.Contains(propId))
                    filtered[propId] = claims;
            }

            result[id] = filtered;
        }

        return result;
    }

    // ─── Wikipedia URL Resolution ───────────────────────────────────

    /// <summary>
    /// Resolves Wikipedia article URLs for the given QIDs.
    /// Only returns URLs for entities that actually have a Wikipedia article in the requested language.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> GetWikipediaUrlsAsync(
        IReadOnlyList<string> qids, string language = "en", CancellationToken cancellationToken = default)
    {
        var entities = await _entityFetcher.FetchEntitiesWithSitelinksAsync(qids, language, cancellationToken)
            .ConfigureAwait(false);

        var siteKey = $"{language}wiki";
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, entity) in entities)
        {
            if (entity.Sitelinks?.TryGetValue(siteKey, out var sitelink) == true &&
                !string.IsNullOrEmpty(sitelink.Title))
            {
                result[id] = $"https://{language}.wikipedia.org/wiki/{Uri.EscapeDataString(sitelink.Title)}";
            }
        }

        return result;
    }

    // ─── Wikipedia Summaries ──────────────────────────────────────

    /// <summary>
    /// Fetches Wikipedia article summaries for the given QIDs.
    /// Uses the Wikipedia REST API to extract the first paragraph, description, and thumbnail.
    /// Only returns summaries for entities that have a Wikipedia article in the requested language.
    /// </summary>
    public async Task<IReadOnlyList<WikipediaSummary>> GetWikipediaSummariesAsync(
        IReadOnlyList<string> qids, string language = "en", CancellationToken cancellationToken = default)
    {
        // First resolve QIDs to Wikipedia article titles via sitelinks
        var entities = await _entityFetcher.FetchEntitiesWithSitelinksAsync(qids, language, cancellationToken)
            .ConfigureAwait(false);

        var siteKey = $"{language}wiki";
        var titleToQid = new Dictionary<string, string>();

        foreach (var (id, entity) in entities)
        {
            if (entity.Sitelinks?.TryGetValue(siteKey, out var sitelink) == true &&
                !string.IsNullOrEmpty(sitelink.Title))
            {
                titleToQid[sitelink.Title] = id;
            }
        }

        if (titleToQid.Count == 0)
            return [];

        // Fetch summaries from Wikipedia REST API concurrently (respecting concurrency limit)
        // Note: uses raw HttpClient, not ResilientHttpClient, because Wikipedia REST API
        // is a different service that doesn't support the maxlag parameter.
        var results = new List<WikipediaSummary>();

        var tasks = titleToQid.Select(async kvp =>
        {
            await _concurrencyLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var url = $"https://{language}.wikipedia.org/api/rest_v1/page/summary/{Uri.EscapeDataString(kvp.Key)}";
                var json = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
                var response = System.Text.Json.JsonSerializer.Deserialize(json,
                    Internal.Json.WikidataJsonContext.Default.WikipediaSummaryResponse);

                if (response is not null && !string.IsNullOrEmpty(response.Extract))
                {
                    return new WikipediaSummary
                    {
                        EntityId = kvp.Value,
                        Title = response.Title,
                        Extract = response.Extract,
                        Description = response.Description,
                        ThumbnailUrl = response.Thumbnail?.Source,
                        ArticleUrl = response.ContentUrls?.Desktop?.Page
                            ?? $"https://{language}.wikipedia.org/wiki/{Uri.EscapeDataString(kvp.Key)}"
                    };
                }
            }
            catch
            {
                // Skip entities whose Wikipedia summary fails to fetch
            }
            finally
            {
                _concurrencyLimiter.Release();
            }
            return null;
        });

        var fetched = await Task.WhenAll(tasks).ConfigureAwait(false);
        return fetched.Where(s => s is not null).ToList()!;
    }

    // ─── Reverse Lookup by External ID ────────────────────────────

    /// <summary>
    /// Finds Wikidata entities by an external identifier value.
    /// For example, look up an entity by its ISBN, IMDB ID, ORCID, or any other external ID property.
    /// Uses the CirrusSearch haswbstatement filter for exact property-value matching.
    /// </summary>
    /// <param name="propertyId">The external ID property (e.g., "P213" for ISNI, "P345" for IMDB ID, "P212" for ISBN-13).</param>
    /// <param name="value">The external ID value to look up.</param>
    /// <param name="language">Language for returned labels/descriptions. Defaults to configured language.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matching entities with full data, or empty if no match found.</returns>
    public async Task<IReadOnlyList<WikidataEntityInfo>> LookupByExternalIdAsync(
        string propertyId, string value, string? language = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var lang = language ?? _options.Language;
        var ids = await _searchClient.SearchByExternalIdAsync(propertyId, value, 10, cancellationToken)
            .ConfigureAwait(false);

        if (ids.Count == 0)
            return [];

        var entities = await _entityFetcher.FetchEntitiesAsync(ids, lang, cancellationToken)
            .ConfigureAwait(false);

        return ids
            .Where(id => entities.ContainsKey(id))
            .Select(id => EntityMapper.MapEntity(entities[id], lang))
            .ToList();
    }

    // ─── Property Label Resolution ──────────────────────────────────

    /// <summary>
    /// Resolves human-readable labels for Wikidata property IDs.
    /// For example, "P569" → "date of birth", "P27" → "country of citizenship".
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> GetPropertyLabelsAsync(
        IReadOnlyList<string> propertyIds, string? language = null, CancellationToken cancellationToken = default)
    {
        var lang = language ?? _options.Language;

        // Property entities use the same wbgetentities API as items
        var entities = await _entityFetcher.FetchEntitiesAsync(propertyIds, lang, cancellationToken)
            .ConfigureAwait(false);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, entity) in entities)
        {
            if (LanguageFallback.TryGetValue(entity.Labels, lang, out var label))
                result[id] = label;
        }

        return result;
    }

    // ─── Commons Image URLs ─────────────────────────────────────────

    /// <summary>
    /// Fetches Wikimedia Commons image URLs for entities that have a P18 (image) claim.
    /// Returns a mapping of QID → image URL for entities that have images.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> GetImageUrlsAsync(
        IReadOnlyList<string> qids, string? language = null, CancellationToken cancellationToken = default)
    {
        var lang = language ?? _options.Language;
        var entities = await _entityFetcher.FetchEntitiesAsync(qids, lang, cancellationToken)
            .ConfigureAwait(false);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, entity) in entities)
        {
            var imageValues = WikidataEntityFetcher.GetClaimValues(entity, "P18");
            if (imageValues.Count > 0 && imageValues[0].Value is System.Text.Json.JsonElement element &&
                element.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var filename = element.GetString();
                if (!string.IsNullOrEmpty(filename))
                    result[id] = $"https://commons.wikimedia.org/wiki/Special:FilePath/{Uri.EscapeDataString(filename)}";
            }
        }

        return result;
    }

    // ─── Entity Change Monitoring ─────────────────────────────────

    /// <summary>
    /// Checks for recent changes to specific Wikidata entities.
    /// Useful for cache invalidation — call periodically to detect when watched entities have been modified.
    /// </summary>
    /// <param name="qids">Entity IDs to check for changes.</param>
    /// <param name="since">Only return changes after this timestamp. Default is last 24 hours.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Changes to the specified entities, ordered by timestamp descending.</returns>
    public async Task<IReadOnlyList<EntityChange>> GetRecentChangesAsync(
        IReadOnlyList<string> qids, DateTimeOffset? since = null,
        CancellationToken cancellationToken = default)
    {
        var sinceTime = since ?? DateTimeOffset.UtcNow.AddHours(-24);
        var titles = string.Join('|', qids);
        var rcStart = sinceTime.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");

        var url = $"{_options.ApiEndpoint}?action=query&list=recentchanges" +
                  $"&rctitle={Uri.EscapeDataString(titles)}" +
                  $"&rcstart={rcStart}&rcdir=newer&rclimit=500" +
                  "&rcprop=title|timestamp|user|comment|ids&rctype=edit|new&format=json";

        var resilientClient = new Internal.ResilientHttpClient(_httpClient, _options.MaxRetries, _options.MaxLag);
        var json = await resilientClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        var response = System.Text.Json.JsonSerializer.Deserialize(json,
            Internal.Json.WikidataJsonContext.Default.RecentChangesResponse);

        if (response?.Query?.RecentChanges is null)
            return [];

        var qidSet = new HashSet<string>(qids, StringComparer.OrdinalIgnoreCase);

        return response.Query.RecentChanges
            .Where(rc => qidSet.Contains(rc.Title))
            .Select(rc => new EntityChange
            {
                EntityId = rc.Title,
                ChangeType = rc.Type,
                Timestamp = DateTimeOffset.TryParse(rc.Timestamp, out var ts) ? ts : DateTimeOffset.MinValue,
                User = rc.User,
                Comment = rc.Comment,
                RevisionId = rc.RevId
            })
            .OrderByDescending(c => c.Timestamp)
            .ToList();
    }

    // ─── Private helpers ────────────────────────────────────────────

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
