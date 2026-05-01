namespace Tuvima.Wikidata;

/// <summary>
/// High-level outcome of a bridge resolution request.
/// </summary>
public enum BridgeResolutionStatus
{
    Resolved = 0,
    NotFound,
    Ambiguous,
    InvalidRequest,
    Failed
}
