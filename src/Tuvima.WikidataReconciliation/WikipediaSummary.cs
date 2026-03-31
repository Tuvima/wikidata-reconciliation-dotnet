namespace Tuvima.WikidataReconciliation;

/// <summary>
/// A summary extracted from a Wikipedia article via the REST API.
/// </summary>
public sealed class WikipediaSummary
{
    /// <summary>
    /// The Wikidata entity ID (e.g., "Q42").
    /// </summary>
    public required string EntityId { get; init; }

    /// <summary>
    /// The article title on Wikipedia.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Plain text extract/summary of the article (first paragraph).
    /// </summary>
    public required string Extract { get; init; }

    /// <summary>
    /// Short description of the article (typically one line).
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// URL of the article's thumbnail image, if available.
    /// </summary>
    public string? ThumbnailUrl { get; init; }

    /// <summary>
    /// Full URL to the Wikipedia article.
    /// </summary>
    public required string ArticleUrl { get; init; }

    /// <summary>
    /// The Wikipedia language edition this summary was fetched from (e.g., "en", "de").
    /// Populated when language fallback is used; null when fetched with the original single-language method.
    /// </summary>
    public string? Language { get; init; }
}
