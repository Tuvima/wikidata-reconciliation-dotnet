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
    /// When <see cref="Types"/> is also set, <see cref="Types"/> takes precedence.
    /// </summary>
    public string? Type { get; init; }

    /// <summary>
    /// Optional list of acceptable types (OR logic). Candidates whose P31 matches
    /// ANY of these types pass the filter. Also enables CirrusSearch type filtering
    /// at query time for better recall. Takes precedence over <see cref="Type"/>.
    /// </summary>
    public IReadOnlyList<string>? Types { get; init; }

    /// <summary>
    /// Optional types to exclude from results. Candidates whose P31 includes
    /// any of these types will be removed.
    /// </summary>
    public IReadOnlyList<string>? ExcludeTypes { get; init; }

    /// <summary>
    /// Per-request override for P279 subclass hierarchy walk depth.
    /// If null, uses the global <see cref="WikidataReconcilerOptions.TypeHierarchyDepth"/>.
    /// Set to 0 for direct P31 match only, or a positive value (e.g., 3) for subclass walking.
    /// </summary>
    public int? TypeHierarchyDepth { get; init; }

    /// <summary>
    /// Maximum number of results to return. Default is 5.
    /// </summary>
    public int Limit { get; init; } = 5;

    /// <summary>
    /// Language code for search and label matching (e.g., "en", "de", "fr").
    /// If null, uses the language from <see cref="WikidataReconcilerOptions"/>.
    /// For multi-language search, use <see cref="Languages"/> instead.
    /// </summary>
    public string? Language { get; init; }

    /// <summary>
    /// When set, searches run concurrently in each language and results are merged by QID.
    /// The first language in the list is used for label/description display.
    /// Takes precedence over <see cref="Language"/> for search.
    /// </summary>
    public IReadOnlyList<string>? Languages { get; init; }

    /// <summary>
    /// Optional property constraints to improve matching accuracy.
    /// </summary>
    public IReadOnlyList<PropertyConstraint>? Properties { get; init; }

    /// <summary>
    /// When true, search and scoring normalize diacritics so that "Shōgun" matches "Shogun".
    /// An additional search with the ASCII-normalized query runs to improve recall.
    /// Default is false.
    /// </summary>
    public bool DiacriticInsensitive { get; init; }

    /// <summary>
    /// Optional pipeline of text transformations applied to the query before search.
    /// Cleaners run sequentially; the cleaned text is used for search while the original
    /// is preserved for scoring. Use <see cref="QueryCleaners"/> presets or supply custom functions.
    /// </summary>
    public IReadOnlyList<Func<string, string>>? Cleaners { get; init; }
}
