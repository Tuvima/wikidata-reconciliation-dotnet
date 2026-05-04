namespace Tuvima.Wikidata;

/// <summary>
/// A work or collection row in a Wikidata series manifest.
/// </summary>
public sealed class SeriesManifestItem
{
    public required string Qid { get; init; }
    public string? Label { get; init; }
    public string? Description { get; init; }
    public string? RawSeriesOrdinal { get; init; }
    public decimal? ParsedSeriesOrdinal { get; init; }
    public DateOnly? PublicationDate { get; init; }
    public string? PreviousQid { get; init; }
    public string? NextQid { get; init; }
    public string? ParentCollectionQid { get; init; }
    public string? ParentCollectionLabel { get; init; }
    public bool IsCollection { get; init; }
    public bool IsExpandedFromCollection { get; init; }
    public IReadOnlyList<string> SourceProperties { get; init; } = [];
    public SeriesManifestOrderSource OrderSource { get; init; }
    public IReadOnlyList<SeriesManifestRelationship> Relationships { get; init; } = [];
}
