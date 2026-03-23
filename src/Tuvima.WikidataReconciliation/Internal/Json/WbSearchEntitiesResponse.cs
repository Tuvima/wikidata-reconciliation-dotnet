using System.Text.Json.Serialization;

namespace Tuvima.WikidataReconciliation.Internal.Json;

internal sealed class WbSearchEntitiesResponse
{
    [JsonPropertyName("search")]
    public List<WbSearchResult>? Search { get; set; }

    [JsonPropertyName("success")]
    public int Success { get; set; }
}

internal sealed class WbSearchResult
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("aliases")]
    public List<string>? Aliases { get; set; }
}
