namespace Tuvima.WikidataReconciliation;

/// <summary>
/// Pseudonym information for an author entity, based on P742 (pseudonym) claims.
/// </summary>
public sealed class PseudonymInfo
{
    /// <summary>
    /// The Wikidata entity ID of the author (e.g., "Q42").
    /// </summary>
    public required string AuthorEntityId { get; init; }

    /// <summary>
    /// The label of the author entity in the requested language.
    /// </summary>
    public string? AuthorLabel { get; init; }

    /// <summary>
    /// List of pseudonyms (P742 values) for this author.
    /// </summary>
    public IReadOnlyList<string> Pseudonyms { get; init; } = [];
}
