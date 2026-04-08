namespace Tuvima.Wikidata;

/// <summary>
/// A role-aware person search request. Used by <see cref="Services.PersonsService.SearchAsync"/>
/// to reconcile a raw name against Wikidata, weighted by role-appropriate occupations and
/// (optionally) context hints like an associated work, birth/death year, or known collaborators.
/// </summary>
public sealed class PersonSearchRequest
{
    /// <summary>
    /// The raw person name as it appears in source data (e.g. "Neil Gaiman", "Daft Punk").
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The role the person plays relative to the work in context. Drives the occupation
    /// filter and the default value of <see cref="IncludeMusicalGroups"/>.
    /// </summary>
    public PersonRole Role { get; init; } = PersonRole.Unknown;

    /// <summary>
    /// Optional title of the associated work (e.g. "American Gods"). Used as a soft
    /// context signal during scoring — candidates whose labels/aliases match this title
    /// in their notable-works (P800) get a small score boost.
    /// </summary>
    public string? TitleHint { get; init; }

    /// <summary>
    /// Optional QID of the associated work (stronger context than <see cref="TitleHint"/>).
    /// Candidates whose P800 (notable work) or claim graph references this work score higher.
    /// </summary>
    public string? WorkQid { get; init; }

    /// <summary>
    /// When true, the search type filter includes Q215380 (musical group) and Q5741069 (musical ensemble)
    /// in addition to Q5 (human). When false, only Q5 is used.
    /// When null (default), the value is derived from <see cref="Role"/>:
    /// <see cref="PersonRole.Performer"/> and <see cref="PersonRole.Artist"/> default to true;
    /// all other roles default to false.
    /// </summary>
    public bool? IncludeMusicalGroups { get; init; }

    /// <summary>
    /// Optional birth year hint (P569). Candidates whose birth year is within ±2 years of this value
    /// receive a small score boost; candidates more than 10 years off are penalised.
    /// </summary>
    public int? BirthYearHint { get; init; }

    /// <summary>
    /// Optional death year hint (P570). Same scoring behaviour as <see cref="BirthYearHint"/>.
    /// </summary>
    public int? DeathYearHint { get; init; }

    /// <summary>
    /// Names of known collaborators, co-authors, or companions. Candidates whose notable-works
    /// graph includes any of these names (fuzzy label match) score higher.
    /// </summary>
    public IReadOnlyList<string>? CompanionNameHints { get; init; }

    /// <summary>
    /// When true and the resolved entity is a musical group (<see cref="PersonSearchResult.IsGroup"/>),
    /// the service also fetches its members (P527) and populates
    /// <see cref="PersonSearchResult.GroupMembers"/>. Default false.
    /// </summary>
    public bool ExpandGroupMembers { get; init; }

    /// <summary>
    /// Language for labels and search. Defaults to the reconciler's configured language.
    /// </summary>
    public string? Language { get; init; }

    /// <summary>
    /// Minimum normalized score (0.0–1.0) for the result to be considered a match.
    /// Scores below this threshold still populate the result but with <see cref="PersonSearchResult.Found"/> = false.
    /// Default 0.80.
    /// </summary>
    public double AcceptThreshold { get; init; } = 0.80;
}
