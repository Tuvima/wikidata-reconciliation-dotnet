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

        // Apply query cleaners (cleaned text for search, original for scoring)
        var searchQuery = request.Query;
        if (request.Cleaners is { Count: > 0 })
        {
            foreach (var cleaner in request.Cleaners)
                searchQuery = cleaner(searchQuery);

            if (string.IsNullOrWhiteSpace(searchQuery))
                searchQuery = request.Query; // Fall back to original if cleaners emptied it
        }

        // Resolve effective types: Types takes precedence over Type
        var effectiveTypes = request.Types is { Count: > 0 }
            ? request.Types
            : (!string.IsNullOrEmpty(request.Type) ? new[] { request.Type } : null);
        var hasTypeConstraint = effectiveTypes is { Count: > 0 };

        // Resolve per-request subclass resolver
        var subclassResolver = _subclassResolver;
        if (request.TypeHierarchyDepth.HasValue && request.TypeHierarchyDepth.Value > 0 && _subclassResolver == null)
        {
            // Create a temporary resolver for this request
            subclassResolver = new SubclassResolver(_entityFetcher, request.TypeHierarchyDepth.Value);
        }
        var subclassDepth = request.TypeHierarchyDepth ?? _options.TypeHierarchyDepth;

        // Step 1: Search for candidate entity IDs
        var useMultiLanguage = request.Languages is { Count: > 1 };
        var searchTasks = new List<Task<List<string>>>();

        // Primary search (single or multi-language)
        if (useMultiLanguage)
        {
            searchTasks.Add(_searchClient.SearchMultiLanguageAsync(searchQuery, request.Languages!,
                limit, request.DiacriticInsensitive, cancellationToken));
        }
        else
        {
            searchTasks.Add(_searchClient.SearchAsync(searchQuery, language, limit,
                request.DiacriticInsensitive, cancellationToken));
        }

        // Type-filtered CirrusSearch for better recall when types are specified
        if (hasTypeConstraint)
        {
            searchTasks.Add(_searchClient.SearchWithTypeFilterAsync(
                searchQuery, language, effectiveTypes!, limit, cancellationToken));
        }

        await Task.WhenAll(searchTasks).ConfigureAwait(false);

        // Merge: type-filtered results first (if present), then primary search, deduplicated
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidateIds = new List<string>();

        if (hasTypeConstraint)
        {
            // Type-filtered results first for better type recall
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

        // Step 2: Fetch entity data (all languages for cross-language label scoring)
        var entities = await _entityFetcher.FetchEntitiesAllLanguagesAsync(
            candidateIds, _options.IncludeSitelinkLabels, cancellationToken).ConfigureAwait(false);

        // Step 3: Score and filter candidates
        var scored = new List<(string Id, WikidataEntity Entity, ScoringResult Scoring, List<string> Types, TypeMatchResult TypeResult)>();

        foreach (var id in candidateIds)
        {
            if (!entities.TryGetValue(id, out var entity))
                continue;

            // Type checking (async for P279 support, multi-type OR logic)
            var typeResult = await _typeChecker.CheckAsync(
                entity, effectiveTypes, request.ExcludeTypes,
                subclassResolver, language, cancellationToken).ConfigureAwait(false);

            if (typeResult == TypeMatchResult.Excluded || typeResult == TypeMatchResult.NotMatched)
                continue;

            // Score the candidate
            var scoring = _scorer.Score(request.Query, entity, language, request.Properties, request.DiacriticInsensitive);

            // Halve score for entities with no type when a type was requested
            var finalScore = scoring.Score;
            if (typeResult == TypeMatchResult.NoType && hasTypeConstraint)
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

            var typePenaltyApplied = typeResult == TypeMatchResult.NoType && hasTypeConstraint;

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
                    TypeMatched = !hasTypeConstraint ? null : typeResult == TypeMatchResult.Matched,
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
    public Task<IReadOnlyDictionary<string, WikidataEntityInfo>> GetEntitiesAsync(
        IReadOnlyList<string> qids, string? language = null, CancellationToken cancellationToken = default)
    {
        return GetEntitiesAsync(qids, resolveEntityLabels: false, language, cancellationToken);
    }

    /// <summary>
    /// Fetches full entity data for the given QIDs. When <paramref name="resolveEntityLabels"/> is true,
    /// entity-valued claims (e.g., P50 author → Q42) will have their <see cref="WikidataValue.EntityLabel"/>
    /// populated with the referenced entity's label in the requested language.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, WikidataEntityInfo>> GetEntitiesAsync(
        IReadOnlyList<string> qids, bool resolveEntityLabels,
        string? language = null, CancellationToken cancellationToken = default)
    {
        var lang = language ?? _options.Language;
        var entities = await _entityFetcher.FetchEntitiesAsync(qids, lang, cancellationToken)
            .ConfigureAwait(false);

        var result = new Dictionary<string, WikidataEntityInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, entity) in entities)
        {
            result[id] = EntityMapper.MapEntity(entity, lang);
        }

        if (resolveEntityLabels)
            await ResolveEntityLabelsAsync(result, lang, cancellationToken).ConfigureAwait(false);

        return result;
    }

    private async Task ResolveEntityLabelsAsync(
        Dictionary<string, WikidataEntityInfo> entities, string language, CancellationToken cancellationToken)
    {
        // Collect all unique entity IDs referenced in claims and qualifiers
        var referencedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entityInfo in entities.Values)
        {
            foreach (var claims in entityInfo.Claims.Values)
            {
                foreach (var claim in claims)
                {
                    if (claim.Value?.Kind == WikidataValueKind.EntityId && !string.IsNullOrEmpty(claim.Value.EntityId))
                        referencedIds.Add(claim.Value.EntityId);

                    foreach (var qualValues in claim.Qualifiers.Values)
                    {
                        foreach (var qv in qualValues)
                        {
                            if (qv.Kind == WikidataValueKind.EntityId && !string.IsNullOrEmpty(qv.EntityId))
                                referencedIds.Add(qv.EntityId);
                        }
                    }
                }
            }
        }

        // Remove IDs we already have (they're in the result set)
        foreach (var id in entities.Keys)
            referencedIds.Remove(id);

        if (referencedIds.Count == 0 && entities.Count == 0)
            return;

        // Batch-fetch labels for all referenced entities
        var labelLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // First, use labels from entities we already have
        foreach (var (id, info) in entities)
        {
            if (!string.IsNullOrEmpty(info.Label))
                labelLookup[id] = info.Label;
        }

        // Then fetch labels for remaining referenced IDs
        if (referencedIds.Count > 0)
        {
            var labelEntities = await _entityFetcher.FetchLabelsOnlyAsync(
                referencedIds.ToList(), language, cancellationToken).ConfigureAwait(false);

            foreach (var (id, entity) in labelEntities)
            {
                if (LanguageFallback.TryGetValue(entity.Labels, language, out var label))
                    labelLookup[id] = label;
            }
        }

        // Walk all claims and set EntityLabel
        foreach (var entityInfo in entities.Values)
        {
            foreach (var claims in entityInfo.Claims.Values)
            {
                foreach (var claim in claims)
                {
                    if (claim.Value?.Kind == WikidataValueKind.EntityId &&
                        !string.IsNullOrEmpty(claim.Value.EntityId) &&
                        labelLookup.TryGetValue(claim.Value.EntityId, out var label))
                    {
                        claim.Value.EntityLabel = label;
                    }

                    foreach (var qualValues in claim.Qualifiers.Values)
                    {
                        foreach (var qv in qualValues)
                        {
                            if (qv.Kind == WikidataValueKind.EntityId &&
                                !string.IsNullOrEmpty(qv.EntityId) &&
                                labelLookup.TryGetValue(qv.EntityId, out var qLabel))
                            {
                                qv.EntityLabel = qLabel;
                            }
                        }
                    }
                }
            }
        }
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

    /// <summary>
    /// Fetches Wikipedia article summaries with language fallback.
    /// For each entity, tries the requested language first, then each fallback language in order.
    /// The <see cref="WikipediaSummary.Language"/> property indicates which edition was used.
    /// </summary>
    /// <param name="qids">Entity IDs to fetch summaries for.</param>
    /// <param name="language">Preferred Wikipedia language edition.</param>
    /// <param name="fallbackLanguages">Additional languages to try if the preferred language has no article.
    /// If null, uses the default fallback chain (subtag parent → "en").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyList<WikipediaSummary>> GetWikipediaSummariesAsync(
        IReadOnlyList<string> qids, string language,
        IReadOnlyList<string>? fallbackLanguages, CancellationToken cancellationToken = default)
    {
        // Build language chain: requested → fallback languages (or default chain)
        var langChain = fallbackLanguages is { Count: > 0 }
            ? new List<string> { language }.Concat(fallbackLanguages.Where(l => !string.Equals(l, language, StringComparison.OrdinalIgnoreCase))).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : LanguageFallback.GetFallbackChain(language);

        // Fetch sitelinks without language filter to get all available sitelinks
        var entities = await _entityFetcher.FetchEntitiesWithSitelinksAsync(qids, language, cancellationToken)
            .ConfigureAwait(false);

        // For each QID, find the first language with a sitelink
        var qidToLangTitle = new Dictionary<string, (string Language, string Title)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, entity) in entities)
        {
            if (entity.Sitelinks is null) continue;

            foreach (var lang in langChain)
            {
                var siteKey = $"{lang}wiki";
                if (entity.Sitelinks.TryGetValue(siteKey, out var sitelink) && !string.IsNullOrEmpty(sitelink.Title))
                {
                    qidToLangTitle[id] = (lang, sitelink.Title);
                    break;
                }
            }
        }

        if (qidToLangTitle.Count == 0)
            return [];

        var tasks = qidToLangTitle.Select(async kvp =>
        {
            await _concurrencyLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var lang = kvp.Value.Language;
                var title = kvp.Value.Title;
                var url = $"https://{lang}.wikipedia.org/api/rest_v1/page/summary/{Uri.EscapeDataString(title)}";
                var json = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
                var response = System.Text.Json.JsonSerializer.Deserialize(json,
                    Internal.Json.WikidataJsonContext.Default.WikipediaSummaryResponse);

                if (response is not null && !string.IsNullOrEmpty(response.Extract))
                {
                    return new WikipediaSummary
                    {
                        EntityId = kvp.Key,
                        Title = response.Title,
                        Extract = response.Extract,
                        Description = response.Description,
                        ThumbnailUrl = response.Thumbnail?.Source,
                        ArticleUrl = response.ContentUrls?.Desktop?.Page
                            ?? $"https://{lang}.wikipedia.org/wiki/{Uri.EscapeDataString(title)}",
                        Language = lang
                    };
                }
            }
            catch
            {
                // Skip on failure
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

    // ─── Wikipedia Section Content ──────────────────────────────────

    /// <summary>
    /// Gets the table of contents (section headings) for the Wikipedia articles associated with the specified entities.
    /// Returns sections with titles, indices, and heading levels. Use <see cref="GetWikipediaSectionContentAsync"/>
    /// with a section's <see cref="WikipediaSection.Index"/> to fetch its content.
    /// </summary>
    /// <param name="qids">Entity IDs to get sections for.</param>
    /// <param name="language">Wikipedia language edition. Defaults to "en".</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A dictionary mapping entity IDs to their Wikipedia article sections. Entities without a Wikipedia article are omitted.</returns>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<WikipediaSection>>> GetWikipediaSectionsAsync(
        IReadOnlyList<string> qids, string language = "en",
        CancellationToken cancellationToken = default)
    {
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
            return new Dictionary<string, IReadOnlyList<WikipediaSection>>();

        var result = new Dictionary<string, IReadOnlyList<WikipediaSection>>(StringComparer.OrdinalIgnoreCase);

        var tasks = titleToQid.Select(async kvp =>
        {
            await _concurrencyLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var url = $"https://{language}.wikipedia.org/w/api.php?action=parse" +
                          $"&page={Uri.EscapeDataString(kvp.Key)}&prop=tocdata&format=json";
                var json = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
                var response = System.Text.Json.JsonSerializer.Deserialize(json,
                    Internal.Json.WikidataJsonContext.Default.ParseResponse);

                if (response?.Parse?.TocData?.Sections is { Count: > 0 } sections)
                {
                    return (Qid: kvp.Value, Sections: (IReadOnlyList<WikipediaSection>)sections
                        .Select(s => new WikipediaSection
                        {
                            Title = Internal.HtmlTextExtractor.StripInlineHtml(s.Line),
                            Index = int.TryParse(s.Index, out var idx) ? idx : 0,
                            Level = s.HLevel,
                            Number = s.Number,
                            Anchor = s.Anchor
                        })
                        .ToList());
                }
            }
            catch
            {
                // Gracefully skip on failure
            }
            finally
            {
                _concurrencyLimiter.Release();
            }
            return (Qid: kvp.Value, Sections: (IReadOnlyList<WikipediaSection>?)null);
        });

        var fetched = await Task.WhenAll(tasks).ConfigureAwait(false);
        foreach (var item in fetched)
        {
            if (item.Sections is not null)
                result[item.Qid] = item.Sections;
        }

        return result;
    }

    /// <summary>
    /// Fetches the content of a specific Wikipedia article section as plain text.
    /// Use <see cref="GetWikipediaSectionsAsync"/> first to discover available sections and their indices.
    /// </summary>
    /// <param name="qid">The entity ID (e.g., "Q42").</param>
    /// <param name="sectionIndex">The section index from <see cref="WikipediaSection.Index"/>.</param>
    /// <param name="language">Wikipedia language edition. Defaults to "en".</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The section content as plain text, or null if the entity has no Wikipedia article or the section doesn't exist.</returns>
    public async Task<string?> GetWikipediaSectionContentAsync(
        string qid, int sectionIndex, string language = "en",
        CancellationToken cancellationToken = default)
    {
        var entities = await _entityFetcher.FetchEntitiesWithSitelinksAsync([qid], language, cancellationToken)
            .ConfigureAwait(false);

        var siteKey = $"{language}wiki";
        if (!entities.TryGetValue(qid, out var entity) ||
            entity.Sitelinks?.TryGetValue(siteKey, out var sitelink) != true ||
            string.IsNullOrEmpty(sitelink!.Title))
        {
            return null;
        }

        try
        {
            var url = $"https://{language}.wikipedia.org/w/api.php?action=parse" +
                      $"&page={Uri.EscapeDataString(sitelink.Title)}&section={sectionIndex}" +
                      "&prop=text&format=json";
            var json = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            var response = System.Text.Json.JsonSerializer.Deserialize(json,
                Internal.Json.WikidataJsonContext.Default.ParseResponse);

            if (response?.Error is not null || response?.Parse?.Text?.Html is null)
                return null;

            var text = Internal.HtmlTextExtractor.ExtractText(response.Parse.Text.Html);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
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

    // ─── Staleness Detection ────────────────────────────────────────

    /// <summary>
    /// Gets the current revision IDs for the specified entities. This is an ultra-lightweight API call
    /// that returns only revision IDs and timestamps — no labels, descriptions, or claims.
    /// Compare with <see cref="WikidataEntityInfo.LastRevisionId"/> to detect stale cached data.
    /// </summary>
    /// <param name="qids">Entity IDs to check (e.g., ["Q42", "Q5"]).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A dictionary mapping entity IDs to their current revision metadata.</returns>
    public async Task<IReadOnlyDictionary<string, EntityRevision>> GetRevisionIdsAsync(
        IReadOnlyList<string> qids, CancellationToken cancellationToken = default)
    {
        if (qids.Count == 0)
            return new Dictionary<string, EntityRevision>();

        var result = new Dictionary<string, EntityRevision>(StringComparer.OrdinalIgnoreCase);

        // Batch in groups of 50 (same limit as wbgetentities)
        for (var i = 0; i < qids.Count; i += 50)
        {
            var batch = qids.Skip(i).Take(50).ToList();
            var titles = string.Join('|', batch);

            var url = $"{_options.ApiEndpoint}?action=query&prop=revisions" +
                      $"&titles={Uri.EscapeDataString(titles)}&rvprop=ids|timestamp&format=json";

            var resilientClient = new ResilientHttpClient(_httpClient, _options.MaxRetries, _options.MaxLag);
            var json = await resilientClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            var response = System.Text.Json.JsonSerializer.Deserialize(json,
                WikidataJsonContext.Default.RevisionQueryResponse);

            if (response?.Query?.Pages is null)
                continue;

            foreach (var page in response.Query.Pages.Values)
            {
                if (page.Revisions is not { Count: > 0 })
                    continue;

                var rev = page.Revisions[0];
                DateTimeOffset? timestamp = null;
                if (!string.IsNullOrEmpty(rev.Timestamp) && DateTimeOffset.TryParse(rev.Timestamp, out var ts))
                    timestamp = ts;

                result[page.Title] = new EntityRevision
                {
                    EntityId = page.Title,
                    RevisionId = rev.RevId,
                    Timestamp = timestamp
                };
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

    // ─── Work-to-Edition Pivoting ──────────────────────────────────

    /// <summary>
    /// Fetches editions and translations (P747) of a work entity.
    /// Optionally filters by P31 type (e.g., only audiobook editions).
    /// </summary>
    /// <param name="workQid">The QID of the work entity (e.g., "Q190192" for The Hitchhiker's Guide to the Galaxy).</param>
    /// <param name="filterTypes">Optional P31 type QIDs to filter editions. Only editions matching any of these types are returned.</param>
    /// <param name="language">Language for labels/descriptions. Defaults to configured language.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyList<EditionInfo>> GetEditionsAsync(
        string workQid, IReadOnlyList<string>? filterTypes = null,
        string? language = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workQid);

        var lang = language ?? _options.Language;

        // Fetch the work entity to get P747 (has edition or translation)
        var workEntities = await _entityFetcher.FetchEntitiesAsync([workQid], lang, cancellationToken)
            .ConfigureAwait(false);

        if (!workEntities.TryGetValue(workQid, out var workEntity))
            return [];

        var editionIds = WikidataEntityFetcher.GetClaimValues(workEntity, "P747")
            .Select(dv => EntityMapper.MapDataValue(dv, "wikibase-item"))
            .Where(v => v.Kind == WikidataValueKind.EntityId && !string.IsNullOrEmpty(v.EntityId))
            .Select(v => v.EntityId!)
            .ToList();

        if (editionIds.Count == 0)
            return [];

        // Batch-fetch all editions
        var editionEntities = await _entityFetcher.FetchEntitiesAsync(editionIds, lang, cancellationToken)
            .ConfigureAwait(false);

        var filterSet = filterTypes is { Count: > 0 }
            ? new HashSet<string>(filterTypes, StringComparer.OrdinalIgnoreCase)
            : null;

        var results = new List<EditionInfo>();
        foreach (var (id, entity) in editionEntities)
        {
            var types = WikidataEntityFetcher.GetTypeIds(entity, _options.TypePropertyId);

            // Apply type filter if specified
            if (filterSet is not null && !types.Any(t => filterSet.Contains(t)))
                continue;

            LanguageFallback.TryGetValue(entity.Labels, lang, out var label);
            LanguageFallback.TryGetValue(entity.Descriptions, lang, out var description);

            results.Add(new EditionInfo
            {
                EntityId = id,
                Label = string.IsNullOrEmpty(label) ? null : label,
                Description = string.IsNullOrEmpty(description) ? null : description,
                Types = types,
                Claims = EntityMapper.MapClaims(entity.Claims)
            });
        }

        return results;
    }

    /// <summary>
    /// Given an edition QID, finds the parent work via P629 (edition or translation of).
    /// Returns the work entity info, or null if the entity has no P629 claim.
    /// </summary>
    public async Task<WikidataEntityInfo?> GetWorkForEditionAsync(
        string editionQid, string? language = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(editionQid);

        var lang = language ?? _options.Language;
        var editionEntities = await _entityFetcher.FetchEntitiesAsync([editionQid], lang, cancellationToken)
            .ConfigureAwait(false);

        if (!editionEntities.TryGetValue(editionQid, out var editionEntity))
            return null;

        var workIds = WikidataEntityFetcher.GetClaimValues(editionEntity, "P629")
            .Select(dv => EntityMapper.MapDataValue(dv, "wikibase-item"))
            .Where(v => v.Kind == WikidataValueKind.EntityId && !string.IsNullOrEmpty(v.EntityId))
            .Select(v => v.EntityId!)
            .ToList();

        if (workIds.Count == 0)
            return null;

        var workEntities = await _entityFetcher.FetchEntitiesAsync([workIds[0]], lang, cancellationToken)
            .ConfigureAwait(false);

        return workEntities.TryGetValue(workIds[0], out var workEntity)
            ? EntityMapper.MapEntity(workEntity, lang)
            : null;
    }

    // ─── Pen Name / Pseudonym Detection ─────────────────────────────

    /// <summary>
    /// Detects pseudonyms (P742) for authors associated with an entity.
    /// If the entity itself is an author (has P742), returns its pseudonyms directly.
    /// If the entity has P50 (author) claims, fetches those authors and returns their pseudonyms.
    /// </summary>
    public async Task<IReadOnlyList<PseudonymInfo>> GetAuthorPseudonymsAsync(
        string entityQid, string? language = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityQid);

        var lang = language ?? _options.Language;
        var entities = await _entityFetcher.FetchEntitiesAsync([entityQid], lang, cancellationToken)
            .ConfigureAwait(false);

        if (!entities.TryGetValue(entityQid, out var entity))
            return [];

        // Check if entity itself has P742 (pseudonym) — it's an author
        var directPseudonyms = GetPseudonymsFromEntity(entity);
        if (directPseudonyms.Count > 0)
        {
            LanguageFallback.TryGetValue(entity.Labels, lang, out var label);
            return [new PseudonymInfo
            {
                AuthorEntityId = entityQid,
                AuthorLabel = string.IsNullOrEmpty(label) ? null : label,
                Pseudonyms = directPseudonyms
            }];
        }

        // Otherwise, check P50 (author) claims
        var authorIds = WikidataEntityFetcher.GetClaimValues(entity, "P50")
            .Select(dv => EntityMapper.MapDataValue(dv, "wikibase-item"))
            .Where(v => v.Kind == WikidataValueKind.EntityId && !string.IsNullOrEmpty(v.EntityId))
            .Select(v => v.EntityId!)
            .ToList();

        if (authorIds.Count == 0)
            return [];

        var authorEntities = await _entityFetcher.FetchEntitiesAsync(authorIds, lang, cancellationToken)
            .ConfigureAwait(false);

        var results = new List<PseudonymInfo>();
        foreach (var (id, authorEntity) in authorEntities)
        {
            var pseudonyms = GetPseudonymsFromEntity(authorEntity);
            if (pseudonyms.Count == 0)
                continue;

            LanguageFallback.TryGetValue(authorEntity.Labels, lang, out var authorLabel);
            results.Add(new PseudonymInfo
            {
                AuthorEntityId = id,
                AuthorLabel = string.IsNullOrEmpty(authorLabel) ? null : authorLabel,
                Pseudonyms = pseudonyms
            });
        }

        return results;
    }

    private static List<string> GetPseudonymsFromEntity(Internal.Json.WikidataEntity entity)
    {
        return WikidataEntityFetcher.GetClaimValues(entity, "P742")
            .Select(dv => EntityMapper.MapDataValue(dv, "string"))
            .Where(v => !string.IsNullOrEmpty(v.RawValue))
            .Select(v => v.RawValue)
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
