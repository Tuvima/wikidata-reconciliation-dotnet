namespace Tuvima.Wikidata;

/// <summary>
/// A typed provider/data failure captured for diagnostics without forcing exception parsing.
/// </summary>
public sealed record WikidataFailure(
    WikidataFailureKind Kind,
    string? EntityId,
    string? Endpoint,
    string Message);
