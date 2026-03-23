namespace Tuvima.WikidataReconciliation;

/// <summary>
/// A request to reconcile a text query against Wikidata entities.
/// </summary>
public sealed class ReconciliationRequest
{
    /// <summary>
    /// The text query to reconcile (e.g., "Douglas Adams").
    /// </summary>
    public required string Query { get; init; }

    /// <summary>
    /// Optional Wikidata type to filter by (e.g., "Q5" for humans).
    /// Candidates whose P31 (instance of) does not match will be removed.
    /// </summary>
    public string? Type { get; init; }

    /// <summary>
    /// Optional types to exclude from results. Candidates whose P31 includes
    /// any of these types will be removed.
    /// </summary>
    public IReadOnlyList<string>? ExcludeTypes { get; init; }

    /// <summary>
    /// Maximum number of results to return. Default is 5.
    /// </summary>
    public int Limit { get; init; } = 5;

    /// <summary>
    /// Language code for search and label matching (e.g., "en", "de", "fr").
    /// If null, uses the language from <see cref="WikidataReconcilerOptions"/>.
    /// </summary>
    public string? Language { get; init; }

    /// <summary>
    /// Optional property constraints to improve matching accuracy.
    /// </summary>
    public IReadOnlyList<PropertyConstraint>? Properties { get; init; }
}
