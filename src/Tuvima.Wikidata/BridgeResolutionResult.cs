namespace Tuvima.Wikidata;

/// <summary>
/// Result for one <see cref="BridgeResolutionRequest"/>.
/// </summary>
public sealed class BridgeResolutionResult
{
    public required string CorrelationKey { get; init; }

    public BridgeResolutionStatus Status { get; init; }

    public WikidataFailureKind? FailureKind { get; init; }

    public string? FailureMessage { get; init; }

    public BridgeResolutionStrategy MatchedBy { get; init; } = BridgeResolutionStrategy.NotResolved;

    public BridgeCandidate? SelectedCandidate { get; init; }

    public IReadOnlyList<BridgeCandidate> Candidates { get; init; } = [];

    public CanonicalRollup? Rollup { get; init; }

    public IReadOnlyList<BridgeSeriesInfo> Series { get; init; } = [];

    public IReadOnlyList<BridgeRelationshipEdge> Relationships { get; init; } = [];

    public BridgeResolutionDiagnostics Diagnostics { get; init; } = new();

    public bool Found => SelectedCandidate is not null && Status == BridgeResolutionStatus.Resolved;

    public string? ResolvedEntityQid => Rollup?.ResolvedEntityQid ?? SelectedCandidate?.Qid;

    public string? CanonicalWorkQid => Rollup?.CanonicalWorkQid;
}
