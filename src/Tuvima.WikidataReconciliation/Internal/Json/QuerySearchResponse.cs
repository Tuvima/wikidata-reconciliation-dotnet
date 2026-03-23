using System.Text.Json.Serialization;

namespace Tuvima.WikidataReconciliation.Internal.Json;

internal sealed class QuerySearchResponse
{
    [JsonPropertyName("query")]
    public QuerySearchQuery? Query { get; set; }
}

internal sealed class QuerySearchQuery
{
    [JsonPropertyName("search")]
    public List<QuerySearchResult>? Search { get; set; }
}

internal sealed class QuerySearchResult
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";
}
