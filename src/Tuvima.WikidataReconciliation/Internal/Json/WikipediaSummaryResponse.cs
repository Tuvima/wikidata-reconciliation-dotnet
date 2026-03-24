using System.Text.Json.Serialization;

namespace Tuvima.WikidataReconciliation.Internal.Json;

internal sealed class WikipediaSummaryResponse
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("extract")]
    public string? Extract { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("thumbnail")]
    public WikipediaThumbnail? Thumbnail { get; set; }

    [JsonPropertyName("content_urls")]
    public WikipediaContentUrls? ContentUrls { get; set; }
}

internal sealed class WikipediaThumbnail
{
    [JsonPropertyName("source")]
    public string? Source { get; set; }
}

internal sealed class WikipediaContentUrls
{
    [JsonPropertyName("desktop")]
    public WikipediaDesktopUrl? Desktop { get; set; }
}

internal sealed class WikipediaDesktopUrl
{
    [JsonPropertyName("page")]
    public string? Page { get; set; }
}
