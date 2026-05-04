namespace Tuvima.Wikidata;

/// <summary>
/// Request for building a factual Wikidata series manifest from a parent series QID.
/// </summary>
public sealed class SeriesManifestRequest
{
    public required string SeriesQid { get; init; }
    public string Language { get; init; } = "en";
    public bool IncludeCollections { get; init; } = true;
    public bool ExpandCollections { get; init; } = true;
    public bool IncludePublicationDate { get; init; } = true;
    public bool IncludeDescriptions { get; init; } = false;
    public int MaxDepth { get; init; } = 2;
    public int MaxItems { get; init; } = 500;
}
