using System.Diagnostics.CodeAnalysis;

namespace Tuvima.WikidataReconciliation;

/// <summary>
/// A property-value pair used to constrain reconciliation scoring.
/// </summary>
public sealed class PropertyConstraint
{
    /// <summary>
    /// The Wikidata property ID (e.g., "P569" for date of birth, "P27" for country of citizenship).
    /// </summary>
    public required string PropertyId { get; init; }

    /// <summary>
    /// The expected value. Can be a QID (e.g., "Q145"), a string, a date ("1952-03-11"),
    /// a number, coordinates ("51.5,-0.1"), or a URL.
    /// </summary>
    public required string Value { get; init; }

    public PropertyConstraint() { }

    [SetsRequiredMembers]
    public PropertyConstraint(string propertyId, string value)
    {
        PropertyId = propertyId;
        Value = value;
    }
}
