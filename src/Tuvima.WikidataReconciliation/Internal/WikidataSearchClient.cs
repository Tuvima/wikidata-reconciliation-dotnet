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
    public async Task<List<string>> SearchAsync(string query, string language, int limit, CancellationToken cancellationToken = default)
    {
        // Direct QID lookup bypass
        if (QidPattern.IsMatch(query.Trim()))
            return [query.Trim().ToUpperInvariant()];

        // Truncate long queries to avoid silent empty results (upstream issue #116)
        var searchQuery = query.Length > 250 ? query[..250] : query;
        var fetchLimit = Math.Min(2 * limit, 50);

        // Run both searches concurrently
        var autocompleteTask = SearchEntitiesAsync(searchQuery, language, fetchLimit, cancellationToken);
        var fullTextTask = FullTextSearchAsync(searchQuery, language, fetchLimit, cancellationToken);

        await Task.WhenAll(autocompleteTask, fullTextTask).ConfigureAwait(false);

        var autocompleteIds = await autocompleteTask.ConfigureAwait(false);
        var fullTextIds = await fullTextTask.ConfigureAwait(false);

        // Merge: full-text results first, then autocomplete, deduplicated
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>();

        foreach (var id in fullTextIds.Concat(autocompleteIds))
        {
            if (seen.Add(id))
                merged.Add(id);
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
