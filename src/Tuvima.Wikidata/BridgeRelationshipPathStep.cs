namespace Tuvima.Wikidata;

/// <summary>
/// One audited relationship hop used to explain canonical rollup decisions.
/// </summary>
public sealed class BridgeRelationshipPathStep
{
    public required string SubjectQid { get; init; }

    public required string PropertyId { get; init; }

    public required string ObjectQid { get; init; }

    public Direction Direction { get; init; } = Direction.Outgoing;
}
