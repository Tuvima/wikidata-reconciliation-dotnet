namespace Tuvima.Wikidata;

/// <summary>
/// The outcome of a <see cref="PersonSearchRequest"/>.
/// </summary>
public sealed class PersonSearchResult
{
    /// <summary>
    /// True when the top candidate's normalized score met <see cref="PersonSearchRequest.AcceptThreshold"/>.
    /// When false, the other fields may still be populated with the best (but rejected) candidate.
    /// </summary>
    public bool Found { get; init; }

    /// <summary>The resolved Wikidata QID, or null if no candidate was returned.</summary>
    public string? Qid { get; init; }

    /// <summary>The canonical display label of the resolved entity, or null if unresolved.</summary>
    public string? CanonicalName { get; init; }

    /// <summary>
    /// True when the resolved entity is a musical group or ensemble (Q215380 / Q5741069)
    /// rather than a human (Q5).
    /// </summary>
    public bool IsGroup { get; init; }

    /// <summary>The normalized confidence score (0.0–1.0).</summary>
    public double Score { get; init; }

    /// <summary>
    /// P106 (occupation) QIDs on the resolved entity. Useful for downstream consumers who
    /// want to verify the role match.
    /// </summary>
    public IReadOnlyList<string> Occupations { get; init; } = [];

    /// <summary>
    /// P800 (notable work) QIDs on the resolved entity.
    /// </summary>
    public IReadOnlyList<string> NotableWorks { get; init; } = [];

    /// <summary>
    /// When <see cref="IsGroup"/> is true and <see cref="PersonSearchRequest.ExpandGroupMembers"/>
    /// was set, contains the QIDs of the group's members (P527). Null otherwise.
    /// </summary>
    public IReadOnlyList<string>? GroupMembers { get; init; }
}
