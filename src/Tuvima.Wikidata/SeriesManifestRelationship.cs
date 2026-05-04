namespace Tuvima.Wikidata;

/// <summary>
/// Relationship evidence that explains why a series manifest item was included.
/// </summary>
public sealed class SeriesManifestRelationship
{
    public required string PropertyId { get; init; }
    public required string TargetQid { get; init; }
    public string? TargetLabel { get; init; }
    public string Direction { get; init; } = "Outgoing";
}
