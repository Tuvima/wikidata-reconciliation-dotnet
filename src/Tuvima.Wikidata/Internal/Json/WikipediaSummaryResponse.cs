using System.Text.Json.Serialization;

namespace Tuvima.Wikidata.Internal.Json;

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

internal sealed class WikipediaSummaryBatchResponse
{
    [JsonPropertyName("query")]
    public WikipediaSummaryBatchQuery? Query { get; set; }
}

internal sealed class WikipediaSummaryBatchQuery
{
    [JsonPropertyName("pages")]
    public List<WikipediaSummaryBatchPage>? Pages { get; set; }

    [JsonPropertyName("normalized")]
    public List<WikipediaTitleMap>? Normalized { get; set; }

    [JsonPropertyName("redirects")]
    public List<WikipediaTitleMap>? Redirects { get; set; }
}

internal sealed class WikipediaSummaryBatchPage
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("extract")]
    public string? Extract { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("thumbnail")]
    public WikipediaThumbnail? Thumbnail { get; set; }

    [JsonPropertyName("fullurl")]
    public string? FullUrl { get; set; }

    [JsonPropertyName("missing")]
    public bool Missing { get; set; }
}

internal sealed class WikipediaTitleMap
{
    [JsonPropertyName("from")]
    public string From { get; set; } = "";

    [JsonPropertyName("to")]
    public string To { get; set; } = "";
}
