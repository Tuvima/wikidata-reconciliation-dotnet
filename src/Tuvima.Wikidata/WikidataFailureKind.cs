namespace Tuvima.Wikidata;

/// <summary>
/// Typed provider failure categories reported by the HTTP pipeline and diagnostics.
/// </summary>
public enum WikidataFailureKind
{
    NotFound,
    NoSitelink,
    RateLimited,
    TransientNetworkFailure,
    MalformedResponse,
    Cancelled
}
