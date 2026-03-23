using Tuvima.WikidataReconciliation.Internal.Json;

namespace Tuvima.WikidataReconciliation.Internal;

/// <summary>
/// Checks entity types (P31 instance of) against requested type constraints.
/// Uses direct P31 match only — no SPARQL subclass traversal — to avoid
/// timeout issues with broad types (see upstream issue #131).
/// </summary>
internal sealed class TypeChecker
{
    private readonly string _typePropertyId;

    public TypeChecker(string typePropertyId)
    {
        _typePropertyId = typePropertyId;
    }

    /// <summary>
    /// Determines the type match status for an entity.
    /// </summary>
    public TypeMatchResult Check(WikidataEntity entity, string? requiredType, IReadOnlyList<string>? excludeTypes)
    {
        var entityTypes = WikidataEntityFetcher.GetTypeIds(entity, _typePropertyId);

        // Check exclusions first
        if (excludeTypes is { Count: > 0 })
        {
            foreach (var excludeType in excludeTypes)
            {
                if (entityTypes.Any(t => string.Equals(t, excludeType, StringComparison.OrdinalIgnoreCase)))
                    return TypeMatchResult.Excluded;
            }
        }

        // No type constraint requested — passes
        if (string.IsNullOrEmpty(requiredType))
            return TypeMatchResult.NoConstraint;

        // Entity has no types at all
        if (entityTypes.Count == 0)
            return TypeMatchResult.NoType;

        // Check direct P31 match
        if (entityTypes.Any(t => string.Equals(t, requiredType, StringComparison.OrdinalIgnoreCase)))
            return TypeMatchResult.Matched;

        return TypeMatchResult.NotMatched;
    }
}

internal enum TypeMatchResult
{
    /// <summary>No type constraint was requested.</summary>
    NoConstraint,

    /// <summary>Entity matches the required type.</summary>
    Matched,

    /// <summary>Entity does not match the required type.</summary>
    NotMatched,

    /// <summary>Entity has no type claims at all.</summary>
    NoType,

    /// <summary>Entity matches an excluded type and should be removed.</summary>
    Excluded
}
