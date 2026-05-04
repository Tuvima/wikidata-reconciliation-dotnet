namespace Tuvima.Wikidata;

/// <summary>
/// Ordered, provenance-rich manifest for works linked to a Wikidata series entity.
/// </summary>
public sealed class SeriesManifestResult
{
    public required string SeriesQid { get; init; }
    public string? SeriesLabel { get; init; }
    public IReadOnlyList<SeriesManifestItem> Items { get; init; } = [];
    public IReadOnlyList<SeriesManifestWarning> Warnings { get; init; } = [];
    public SeriesManifestCompleteness Completeness { get; init; }
}
