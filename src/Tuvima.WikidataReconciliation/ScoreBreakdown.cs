namespace Tuvima.WikidataReconciliation;

/// <summary>
/// Detailed breakdown of how a reconciliation score was computed.
/// Enables users to build custom trust rules (e.g., "only auto-match if date of birth is 100").
/// </summary>
public sealed class ScoreBreakdown
{
    /// <summary>
    /// The label/alias match score (0-100). Best fuzzy match across all labels and aliases in all languages.
    /// </summary>
    public double LabelScore { get; init; }

    /// <summary>
    /// The label or alias text that produced the best fuzzy match.
    /// Null when no labels or aliases exist for the entity.
    /// </summary>
    public string? MatchedLabel { get; init; }

    /// <summary>
    /// Individual property match scores, keyed by property ID.
    /// Each score is 0-100 representing how well the query value matched the entity's claim.
    /// </summary>
    public IReadOnlyDictionary<string, double> PropertyScores { get; init; } = new Dictionary<string, double>();

    /// <summary>
    /// Whether the entity matched the requested type constraint.
    /// Null if no type constraint was specified.
    /// </summary>
    public bool? TypeMatched { get; init; }

    /// <summary>
    /// The weighted formula result before any type penalty.
    /// Equal to (LabelScore * 1.0 + sum(PropertyScore * weight)) / totalWeight.
    /// </summary>
    public double WeightedScore { get; init; }

    /// <summary>
    /// Whether a type penalty was applied (score halved because entity has no type claims).
    /// </summary>
    public bool TypePenaltyApplied { get; init; }

    /// <summary>
    /// Whether the score was set to 100 due to a unique identifier match
    /// (e.g., VIAF, ISNI, ORCID exact match). See <see cref="WikidataReconcilerOptions.UniqueIdProperties"/>.
    /// </summary>
    public bool UniqueIdMatch { get; init; }
}
