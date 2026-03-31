using System.Text.Json;
using System.Text.RegularExpressions;
using Tuvima.WikidataReconciliation.Internal.Json;

namespace Tuvima.WikidataReconciliation.Internal;

/// <summary>
/// Searches Wikidata using both wbsearchentities (label/alias autocomplete) and
/// action=query&amp;list=search (full-text search), then merges results.
/// </summary>
internal sealed class WikidataSearchClient
{
    private static readonly Regex QidPattern = new(@"^Q\d+$", RegexOptions.Compiled);

    private readonly ResilientHttpClient _httpClient;
    private readonly WikidataReconcilerOptions _options;

    public WikidataSearchClient(ResilientHttpClient httpClient, WikidataReconcilerOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    /// <summary>
    /// Searches for candidate entities matching the query.
    /// Returns deduplicated entity IDs (full-text results first, then autocomplete results).
    /// </summary>
    public async Task<List<string>> SearchAsync(string query, string language, int limit,
        bool diacriticInsensitive = false, CancellationToken cancellationToken = default)
    {
        // Direct QID lookup bypass
        if (QidPattern.IsMatch(query.Trim()))
            return [query.Trim().ToUpperInvariant()];

        // Truncate long queries to avoid silent empty results (upstream issue #116)
        var searchQuery = query.Length > 250 ? query[..250] : query;
        var fetchLimit = Math.Min(2 * limit, 50);

        // Run both searches concurrently
        var tasks = new List<Task<List<string>>>
        {
            SearchEntitiesAsync(searchQuery, language, fetchLimit, cancellationToken),
            FullTextSearchAsync(searchQuery, language, fetchLimit, cancellationToken)
        };

        // If diacritic-insensitive and the stripped query differs, run additional searches
        if (diacriticInsensitive)
        {
            var stripped = FuzzyMatcher.RemoveDiacritics(searchQuery);
            if (!string.Equals(stripped, searchQuery, StringComparison.Ordinal))
            {
                tasks.Add(SearchEntitiesAsync(stripped, language, fetchLimit, cancellationToken));
                tasks.Add(FullTextSearchAsync(stripped, language, fetchLimit, cancellationToken));
            }
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        // Merge: full-text results first (index 1, then 3 if present), then autocomplete (0, then 2)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>();

        // Full-text results first
        AddUnique(await tasks[1].ConfigureAwait(false), seen, merged);
        if (tasks.Count > 2)
            AddUnique(await tasks[3].ConfigureAwait(false), seen, merged);

        // Then autocomplete results
        AddUnique(await tasks[0].ConfigureAwait(false), seen, merged);
        if (tasks.Count > 2)
            AddUnique(await tasks[2].ConfigureAwait(false), seen, merged);

        return merged;
    }

    private static void AddUnique(List<string> ids, HashSet<string> seen, List<string> merged)
    {
        foreach (var id in ids)
        {
            if (seen.Add(id))
                merged.Add(id);
        }
    }

    /// <summary>
    /// Searches in multiple languages concurrently, merging and deduplicating results by QID.
    /// </summary>
    public async Task<List<string>> SearchMultiLanguageAsync(
        string query, IReadOnlyList<string> languages, int limit,
        bool diacriticInsensitive = false, CancellationToken cancellationToken = default)
    {
        var tasks = languages.Select(lang => SearchAsync(query, lang, limit, diacriticInsensitive, cancellationToken));
        var allResults = await Task.WhenAll(tasks).ConfigureAwait(false);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>();

        foreach (var results in allResults)
        {
            foreach (var id in results)
            {
                if (seen.Add(id))
                    merged.Add(id);
            }
        }

        return merged;
    }

    /// <summary>
    /// Lightweight autocomplete search for type-ahead scenarios.
    /// Returns label, description, and ID for each match.
    /// </summary>
    public async Task<List<WbSearchResult>> SuggestAsync(string prefix, string language, int limit, CancellationToken cancellationToken = default)
    {
        var url = $"{_options.ApiEndpoint}?action=wbsearchentities&search={Uri.EscapeDataString(prefix)}" +
                  $"&language={Uri.EscapeDataString(language)}&limit={limit}&format=json";

        var json = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize(json, WikidataJsonContext.Default.WbSearchEntitiesResponse);

        return response?.Search ?? [];
    }

    /// <summary>
    /// Lightweight autocomplete search for Wikidata properties.
    /// Uses wbsearchentities with type=property.
    /// </summary>
    public async Task<List<WbSearchResult>> SuggestPropertiesAsync(string prefix, string language, int limit, CancellationToken cancellationToken = default)
    {
        var url = $"{_options.ApiEndpoint}?action=wbsearchentities&search={Uri.EscapeDataString(prefix)}" +
                  $"&language={Uri.EscapeDataString(language)}&type=property&limit={limit}&format=json";

        var json = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize(json, WikidataJsonContext.Default.WbSearchEntitiesResponse);

        return response?.Search ?? [];
    }

    /// <summary>
    /// Searches for entities with CirrusSearch type filtering (haswbstatement:P31=QID).
    /// Returns entities whose P31 matches any of the provided types.
    /// </summary>
    public async Task<List<string>> SearchWithTypeFilterAsync(
        string query, string language, IReadOnlyList<string> typeQids, int limit,
        CancellationToken cancellationToken = default)
    {
        // Build CirrusSearch query with haswbstatement for each type (OR logic)
        var typeFilters = string.Join(" OR ", typeQids.Select(t => $"haswbstatement:P31={t}"));
        var searchQuery = $"{query} ({typeFilters})";

        var url = $"{_options.ApiEndpoint}?action=query&list=search&srnamespace=0" +
                  $"&srlimit={limit}&srsearch={Uri.EscapeDataString(searchQuery)}&format=json";

        var json = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize(json, WikidataJsonContext.Default.QuerySearchResponse);

        return response?.Query?.Search?.Select(r => r.Title).ToList() ?? [];
    }

    /// <summary>
    /// Searches for entities by an external ID property value using haswbstatement.
    /// </summary>
    public async Task<List<string>> SearchByExternalIdAsync(
        string propertyId, string value, int limit, CancellationToken cancellationToken = default)
    {
        // Use CirrusSearch haswbstatement filter for exact property-value lookup
        var query = $"haswbstatement:{propertyId}={value}";
        var url = $"{_options.ApiEndpoint}?action=query&list=search&srnamespace=0" +
                  $"&srlimit={limit}&srsearch={Uri.EscapeDataString(query)}&format=json";

        var json = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize(json, WikidataJsonContext.Default.QuerySearchResponse);

        return response?.Query?.Search?.Select(r => r.Title).ToList() ?? [];
    }

    private async Task<List<string>> SearchEntitiesAsync(string query, string language, int limit, CancellationToken cancellationToken)
    {
        var url = $"{_options.ApiEndpoint}?action=wbsearchentities&search={Uri.EscapeDataString(query)}" +
                  $"&language={Uri.EscapeDataString(language)}&limit={limit}&format=json";

        var json = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize(json, WikidataJsonContext.Default.WbSearchEntitiesResponse);

        return response?.Search?.Select(r => r.Id).ToList() ?? [];
    }

    private async Task<List<string>> FullTextSearchAsync(string query, string language, int limit, CancellationToken cancellationToken)
    {
        var url = $"{_options.ApiEndpoint}?action=query&list=search&srnamespace=0" +
                  $"&srlimit={limit}&srsearch={Uri.EscapeDataString(query)}&srwhat=text&format=json";

        var json = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize(json, WikidataJsonContext.Default.QuerySearchResponse);

        return response?.Query?.Search?.Select(r => r.Title).ToList() ?? [];
    }
}
