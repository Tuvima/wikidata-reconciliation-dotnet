using System.Text.Json;
using Tuvima.WikidataReconciliation.Internal.Json;

namespace Tuvima.WikidataReconciliation.Internal;

/// <summary>
/// Fetches entity data (labels, descriptions, claims) from the Wikidata API using wbgetentities.
/// </summary>
internal sealed class WikidataEntityFetcher
{
    private const int MaxIdsPerRequest = 50;

    private readonly HttpClient _httpClient;
    private readonly WikidataReconcilerOptions _options;

    public WikidataEntityFetcher(HttpClient httpClient, WikidataReconcilerOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    /// <summary>
    /// Fetches entities by their IDs. Automatically batches requests (max 50 per API call).
    /// </summary>
    public async Task<Dictionary<string, WikidataEntity>> FetchEntitiesAsync(
        IReadOnlyList<string> ids, string language, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, WikidataEntity>(StringComparer.OrdinalIgnoreCase);

        // Batch into groups of 50
        for (var i = 0; i < ids.Count; i += MaxIdsPerRequest)
        {
            var batch = ids.Skip(i).Take(MaxIdsPerRequest).ToList();
            var batchResult = await FetchBatchAsync(batch, language, cancellationToken).ConfigureAwait(false);

            foreach (var kvp in batchResult)
                result[kvp.Key] = kvp.Value;
        }

        return result;
    }

    private async Task<Dictionary<string, WikidataEntity>> FetchBatchAsync(
        List<string> ids, string language, CancellationToken cancellationToken)
    {
        var idsParam = string.Join('|', ids);
        var url = $"{_options.ApiEndpoint}?action=wbgetentities&ids={Uri.EscapeDataString(idsParam)}" +
                  $"&languages={Uri.EscapeDataString(language)}&format=json" +
                  "&props=labels|descriptions|aliases|claims";

        var json = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize(json, WikidataJsonContext.Default.WbGetEntitiesResponse);

        return response?.Entities ?? new Dictionary<string, WikidataEntity>();
    }

    /// <summary>
    /// Extracts all labels and aliases for a given entity in the requested language.
    /// </summary>
    public static List<string> GetAllLabels(WikidataEntity entity, string language)
    {
        var labels = new List<string>();

        if (entity.Labels?.TryGetValue(language, out var label) == true && !string.IsNullOrEmpty(label.Value))
            labels.Add(label.Value);

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
    /// Extracts claim values for a property, respecting Wikidata rank hierarchy.
    /// Returns values from preferred-rank claims if any exist, otherwise normal-rank.
    /// Deprecated-rank claims are excluded.
    /// </summary>
    public static List<DataValue> GetClaimValues(WikidataEntity entity, string propertyId)
    {
        if (entity.Claims?.TryGetValue(propertyId, out var claims) != true || claims!.Count == 0)
            return [];

        // Filter out deprecated and novalue/somevalue snaks
        var validClaims = claims
            .Where(c => c.Rank != "deprecated" && c.MainSnak?.SnakType == "value" && c.MainSnak.DataValue != null)
            .ToList();

        // Prefer preferred rank if any exist
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
