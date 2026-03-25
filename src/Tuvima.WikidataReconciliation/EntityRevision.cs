namespace Tuvima.WikidataReconciliation;

/// <summary>
/// Lightweight revision metadata for an entity, used for staleness detection without fetching full entity data.
/// </summary>
public sealed class EntityRevision
{
    /// <summary>
    /// The entity ID (e.g., "Q42").
    /// </summary>
    public required string EntityId { get; init; }

    /// <summary>
    /// The current revision ID on Wikidata. Compare with <see cref="WikidataEntityInfo.LastRevisionId"/>
    /// to determine if locally cached data is stale.
    /// </summary>
    public long RevisionId { get; init; }

    /// <summary>
    /// When this revision was created. Null if the API did not return this field.
    /// </summary>
    public DateTimeOffset? Timestamp { get; init; }
}
