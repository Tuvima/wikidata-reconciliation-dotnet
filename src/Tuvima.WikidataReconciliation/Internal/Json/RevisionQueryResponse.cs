using System.Text.Json.Serialization;

namespace Tuvima.WikidataReconciliation.Internal.Json;

internal sealed class RevisionQueryResponse
{
    [JsonPropertyName("query")]
    public RevisionQuery? Query { get; set; }
}

internal sealed class RevisionQuery
{
    [JsonPropertyName("pages")]
    public Dictionary<string, RevisionPage>? Pages { get; set; }
}

internal sealed class RevisionPage
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("revisions")]
    public List<RevisionEntry>? Revisions { get; set; }
}

internal sealed class RevisionEntry
{
    [JsonPropertyName("revid")]
    public long RevId { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }
}
