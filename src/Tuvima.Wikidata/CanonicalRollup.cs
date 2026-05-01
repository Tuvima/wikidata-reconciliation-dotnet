namespace Tuvima.Wikidata;

/// <summary>
/// Canonical entity relationship selected during bridge resolution.
/// </summary>
public sealed class CanonicalRollup
{
    public required string ResolvedEntityQid { get; init; }

    public required string CanonicalWorkQid { get; init; }

    public bool IsRollup { get; init; }

    public IReadOnlyList<BridgeRelationshipPathStep> RelationshipPath { get; init; } = [];
}
