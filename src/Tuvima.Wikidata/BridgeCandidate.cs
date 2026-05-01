namespace Tuvima.Wikidata;

/// <summary>
/// Ranked Wikidata candidate returned by bridge resolution.
/// </summary>
public sealed class BridgeCandidate
{
    public required string Qid { get; init; }

    public string? Label { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<string> EntityTypes { get; init; } = [];

    public string? MatchedBridgeIdType { get; init; }

    public string? MatchedPropertyId { get; init; }

    public string? MatchedBridgeValue { get; init; }

    public double Confidence { get; init; }

    public IReadOnlyList<string> ReasonCodes { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public IReadOnlyDictionary<string, string> CollectedBridgeIds { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
