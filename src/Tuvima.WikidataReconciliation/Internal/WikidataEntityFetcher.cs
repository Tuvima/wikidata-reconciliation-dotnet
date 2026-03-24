using System.Text.Json;
using Tuvima.WikidataReconciliation.Internal.Json;

namespace Tuvima.WikidataReconciliation.Internal;

/// <summary>
/// Fetches entity data (labels, descriptions, claims) from the Wikidata API using wbgetentities.
/// </summary>
internal sealed class WikidataEntityFetcher
{
    private const int MaxIdsPerRequest = 50;

    private readonly ResilientHttpClient _httpClient;
    private readonly WikidataReconcilerOptions _options;

    public WikidataEntityFetcher(ResilientHttpClient httpClient, WikidataReconcilerOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    /// <summary>
    /// Fetches entities by their IDs. Automatically batches requests (max 50 per API call).
    /// Includes labels, descriptions, aliases, and claims with language fallback.
    /// </summary>
    public async Task<Dictionary<string, WikidataEntity>> FetchEntitiesAsync(
        IReadOnlyList<string> ids, string language, CancellationToken cancellationToken = default)
    {
        return await FetchInBatchesAsync(ids, language, "labels|descriptions|aliases|claims", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches entities with labels and aliases in ALL languages (no language filter).
    /// Used by the reconciliation pipeline so cross-language label matching works.
    /// </summary>
    public async Task<Dictionary<string, WikidataEntity>> FetchEntitiesAllLanguagesAsync(
        IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        return await FetchInBatchesAsync(ids, null, "labels|descriptions|aliases|claims", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches entities with sitelinks only (lightweight, for Wikipedia URL resolution).
    /// </summary>
    public async Task<Dictionary<string, WikidataEntity>> FetchEntitiesWithSitelinksAsync(
        IReadOnlyList<string> ids, string language, CancellationToken cancellationToken = default)
    {
        return await FetchInBatchesAsync(ids, language, "sitelinks", cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<Dictionary<string, WikidataEntity>> FetchInBatchesAsync(
        IReadOnlyList<string> ids, string? language, string props, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, WikidataEntity>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < ids.Count; i += MaxIdsPerRequest)
        {
            var batch = ids.Skip(i).Take(MaxIdsPerRequest).ToList();
            var batchResult = await FetchBatchAsync(batch, language, props, cancellationToken).ConfigureAwait(false);

            foreach (var kvp in batchResult)
                result[kvp.Key] = kvp.Value;
        }

        return result;
    }

    private async Task<Dictionary<string, WikidataEntity>> FetchBatchAsync(
        List<string> ids, string? language, string props, CancellationToken cancellationToken)
    {
        var idsParam = string.Join('|', ids);
        var url = $"{_options.ApiEndpoint}?action=wbgetentities&ids={Uri.EscapeDataString(idsParam)}" +
                  $"&format=json&props={Uri.EscapeDataString(props)}";

        if (language is not null)
        {
            var languageParam = LanguageFallback.BuildLanguageParam(language);
            url += $"&languages={Uri.EscapeDataString(languageParam)}";
        }

        var json = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize(json, WikidataJsonContext.Default.WbGetEntitiesResponse);

        return response?.Entities ?? new Dictionary<string, WikidataEntity>();
    }

    /// <summary>
    /// Extracts all labels and aliases for a given entity in the requested language.
    /// Uses language fallback chain for labels.
    /// </summary>
    public static List<string> GetAllLabels(WikidataEntity entity, string language)
    {
        var labels = new List<string>();

        // Primary label with fallback
        if (LanguageFallback.TryGetValue(entity.Labels, language, out var label))
            labels.Add(label);

        // Aliases in requested language (no fallback — aliases are language-specific)
        if (entity.Aliases?.TryGetValue(language, out var aliases) == true)
        {
            foreach (var alias in aliases)
            {
                if (!string.IsNullOrEmpty(alias.Value))
                    labels.Add(alias.Value);
            }
        }

        return labels;
    }

    /// <summary>
    /// Extracts all labels and aliases across ALL languages for cross-language scoring.
    /// Returns deduplicated label strings from every available language.
    /// </summary>
    public static List<string> GetAllLabelsAllLanguages(WikidataEntity entity)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var labels = new List<string>();

        // All labels across every language
        if (entity.Labels is not null)
        {
            foreach (var lv in entity.Labels.Values)
            {
                if (!string.IsNullOrEmpty(lv.Value) && seen.Add(lv.Value))
                    labels.Add(lv.Value);
            }
        }

        // All aliases across every language
        if (entity.Aliases is not null)
        {
            foreach (var aliasList in entity.Aliases.Values)
            {
                foreach (var alias in aliasList)
                {
                    if (!string.IsNullOrEmpty(alias.Value) && seen.Add(alias.Value))
                        labels.Add(alias.Value);
                }
            }
        }

        return labels;
    }

    /// <summary>
    /// Extracts claim values for a property, respecting Wikidata rank hierarchy.
    /// </summary>
    public static List<DataValue> GetClaimValues(WikidataEntity entity, string propertyId)
    {
        if (entity.Claims?.TryGetValue(propertyId, out var claims) != true || claims!.Count == 0)
            return [];

        var validClaims = claims
            .Where(c => c.Rank != "deprecated" && c.MainSnak?.SnakType == "value" && c.MainSnak.DataValue != null)
            .ToList();

        var preferred = validClaims.Where(c => c.Rank == "preferred").ToList();
        var source = preferred.Count > 0 ? preferred : validClaims.Where(c => c.Rank == "normal").ToList();

        return source.Select(c => c.MainSnak!.DataValue!).ToList();
    }

    /// <summary>
    /// Extracts P31 (instance of) type QIDs for an entity.
    /// </summary>
    public static List<string> GetTypeIds(WikidataEntity entity, string typePropertyId)
    {
        if (entity.Claims?.TryGetValue(typePropertyId, out var claims) != true)
            return [];

        var types = new List<string>();
        foreach (var claim in claims!)
        {
            if (claim.MainSnak?.SnakType == "value" && claim.MainSnak.DataValue?.Value is JsonElement element)
            {
                if (element.TryGetProperty("id", out var idProp))
                    types.Add(idProp.GetString() ?? "");
            }
        }

        return types.Where(t => !string.IsNullOrEmpty(t)).ToList();
    }
}
