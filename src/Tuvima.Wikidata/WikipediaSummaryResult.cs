namespace Tuvima.Wikidata;

/// <summary>
/// QID-based Wikipedia summary lookup result with typed not-found/no-sitelink outcomes.
/// </summary>
public sealed class WikipediaSummaryResult
{
    public required string EntityId { get; init; }

    public bool Found { get; init; }

    public string? Summary { get; init; }

    public string? PageTitle { get; init; }

    public string? PageUrl { get; init; }

    public string? Language { get; init; }

    public string SourceProvider { get; init; } = "wikipedia";

    public DateTimeOffset? LastModified { get; init; }

    public WikidataFailureKind? FailureKind { get; init; }

    public string? FailureMessage { get; init; }
}
