namespace Tuvima.Wikidata;

/// <summary>
/// Description fields kept separate by source so consumers can store and merge them explicitly.
/// </summary>
public sealed class WikidataDescriptionResult
{
    public required string EntityId { get; init; }

    public bool Found { get; init; }

    public string? WikipediaDescription { get; init; }

    public string? WikidataDescription { get; init; }

    public string? ShortDescription { get; init; }

    public IReadOnlyList<string> Aliases { get; init; } = [];

    public string? Language { get; init; }

    public WikidataFailureKind? FailureKind { get; init; }

    public string? FailureMessage { get; init; }
}
