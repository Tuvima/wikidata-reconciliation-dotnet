namespace Tuvima.WikidataReconciliation;

/// <summary>
/// A scored Wikidata entity match returned from reconciliation.
/// </summary>
public sealed class ReconciliationResult
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

    /// <summary>
    /// Reconciliation score from 0 to 100.
    /// </summary>
    public double Score { get; init; }

    /// <summary>
    /// Whether this result is a confident automatic match.
    /// True when the score exceeds the threshold and has sufficient gap over the next candidate.
    /// </summary>
    public bool Match { get; init; }

    /// <summary>
    /// The P31 (instance of) type IDs for this entity, if available.
    /// </summary>
    public IReadOnlyList<string>? Types { get; init; }

    /// <summary>
    /// The label or alias text that produced the best fuzzy match against the query.
    /// May differ from <see cref="Name"/> when the query matches a foreign-language label or alias.
    /// For example, searching "Die Verwandlung" returns Name="The Metamorphosis" with MatchedLabel="Die Verwandlung".
    /// </summary>
    public string? MatchedLabel { get; init; }

    /// <summary>
    /// Detailed breakdown of how the score was computed.
    /// Contains individual label, property, and type match scores.
    /// </summary>
    public ScoreBreakdown? Breakdown { get; init; }
}
