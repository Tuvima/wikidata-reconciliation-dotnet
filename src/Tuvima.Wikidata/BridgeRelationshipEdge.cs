namespace Tuvima.Wikidata;

/// <summary>
/// Auditable relationship edge extracted from Wikidata.
/// </summary>
public sealed class BridgeRelationshipEdge
{
    public required string SubjectQid { get; init; }

    public required string PropertyId { get; init; }

    public required string ObjectQid { get; init; }

    public string? ObjectLabel { get; init; }

    public required string RelationshipKind { get; init; }

    public double Confidence { get; init; } = 1.0;
}
