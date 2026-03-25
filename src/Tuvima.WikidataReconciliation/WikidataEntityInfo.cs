namespace Tuvima.WikidataReconciliation;

/// <summary>
/// Full entity data retrieved from Wikidata, including labels, descriptions, and claims with qualifiers.
/// </summary>
public sealed class WikidataEntityInfo
{
    /// <summary>
    /// The entity ID (e.g., "Q42").
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The entity's label in the requested language (with fallback). Null if no label exists.
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// The entity's description in the requested language (with fallback). Null if no description exists.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Aliases in the requested language. Empty if none.
    /// </summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>
    /// All claims grouped by property ID.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<WikidataClaim>> Claims { get; init; }
        = new Dictionary<string, IReadOnlyList<WikidataClaim>>();

    /// <summary>
    /// The last revision ID for this entity. Useful for staleness detection —
    /// compare with <see cref="WikidataReconciler.GetRevisionIdsAsync"/> to check if data has changed.
    /// </summary>
    public long LastRevisionId { get; init; }

    /// <summary>
    /// When this entity was last modified on Wikidata. Null if the API did not return this field.
    /// </summary>
    public DateTimeOffset? Modified { get; init; }
}
