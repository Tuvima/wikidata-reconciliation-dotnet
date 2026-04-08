namespace Tuvima.Wikidata;

/// <summary>
/// A single author extracted from a multi-author string and resolved against Wikidata.
/// </summary>
public sealed class ResolvedAuthor
{
    /// <summary>
    /// The name as it appeared in the input, after splitting and trimming.
    /// </summary>
    public required string OriginalName { get; init; }

    /// <summary>
    /// The resolved Wikidata QID, or null if reconciliation failed.
    /// </summary>
    public string? Qid { get; init; }

    /// <summary>
    /// The canonical display label for the resolved entity, or null if unresolved.
    /// </summary>
    public string? CanonicalName { get; init; }

    /// <summary>
    /// When the resolved author is a known pseudonym (P742 is set on the owning real author,
    /// or the resolved entity is the pseudonym literal), this is the QID of the real author.
    /// Null when the resolved entity is not a pseudonym or when pseudonym detection is disabled.
    /// </summary>
    public string? RealNameQid { get; init; }

    /// <summary>
    /// The resolved entity's own P742 (pseudonym) claims as raw strings, when the author
    /// uses one or more pen names. Populated by <see cref="Services.AuthorsService.ResolveAsync"/>
    /// when <see cref="AuthorResolutionRequest.DetectPseudonyms"/> is true. Null otherwise.
    /// </summary>
    /// <remarks>
    /// Wikidata typically models pseudonyms as P742 string values on the real author's entity
    /// rather than as separate entities. If Stephen King (Q39829) has P742 = "Richard Bachman",
    /// looking up either "Stephen King" or "Richard Bachman" will resolve to Q39829 and populate
    /// this list with "Richard Bachman".
    /// </remarks>
    public IReadOnlyList<string>? Pseudonyms { get; init; }

    /// <summary>
    /// The reconciliation score (0.0–100.0) that led to this match. Zero when unresolved.
    /// </summary>
    public double Confidence { get; init; }
}
