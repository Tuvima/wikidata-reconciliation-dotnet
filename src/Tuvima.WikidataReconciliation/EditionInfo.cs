namespace Tuvima.WikidataReconciliation;

/// <summary>
/// Information about an edition or translation of a work entity.
/// Retrieved via P747 (has edition or translation) from a work item,
/// or via P629 (edition or translation of) from an edition item.
/// </summary>
public sealed class EditionInfo
{
    /// <summary>
    /// The Wikidata entity ID of the edition (e.g., "Q15228").
    /// </summary>
    public required string EntityId { get; init; }

    /// <summary>
    /// The label of the edition entity in the requested language.
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// The description of the edition entity.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// P31 (instance of) type QIDs for this edition (e.g., Q3331189 for "edition", Q122731938 for "audiobook edition").
    /// </summary>
    public IReadOnlyList<string> Types { get; init; } = [];

    /// <summary>
    /// All claims on the edition entity, for accessing ISBNs, formats, publishers, etc.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<WikidataClaim>> Claims { get; init; }
        = new Dictionary<string, IReadOnlyList<WikidataClaim>>();
}
