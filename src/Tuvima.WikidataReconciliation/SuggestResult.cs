namespace Tuvima.WikidataReconciliation;

/// <summary>
/// A suggestion returned from the autocomplete/suggest API.
/// </summary>
public sealed class SuggestResult
{
    /// <summary>
    /// The Wikidata entity ID (e.g., "Q42").
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The entity's label in the requested language.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The entity's description in the requested language.
    /// </summary>
    public string? Description { get; init; }
}
