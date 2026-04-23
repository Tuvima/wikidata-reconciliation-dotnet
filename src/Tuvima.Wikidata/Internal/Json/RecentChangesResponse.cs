using System.Text.Json.Serialization;

namespace Tuvima.Wikidata.Internal.Json;

internal sealed class RecentChangesResponse
{
    [JsonPropertyName("query")]
    public RcQuery? Query { get; set; }

    [JsonPropertyName("continue")]
    public RcContinue? Continue { get; set; }
}

internal sealed class RcQuery
{
    [JsonPropertyName("recentchanges")]
    public List<RcEntry>? RecentChanges { get; set; }
}

internal sealed class RcContinue
{
    [JsonPropertyName("rccontinue")]
    public string? RcContinueToken { get; set; }
}

internal sealed class RcEntry
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("revid")]
    public long RevId { get; set; }

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = "";

    [JsonPropertyName("user")]
    public string? User { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }
}
