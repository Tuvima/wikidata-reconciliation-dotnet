using Tuvima.WikidataReconciliation.Internal.Json;

namespace Tuvima.WikidataReconciliation.Internal;

/// <summary>
/// Checks entity types (P31 instance of) against requested type constraints.
/// Supports optional P279 subclass hierarchy walking via SubclassResolver.
/// </summary>
internal sealed class TypeChecker
{
    private readonly string _typePropertyId;

    public TypeChecker(string typePropertyId)
    {
        _typePropertyId = typePropertyId;
    }

    /// <summary>
    /// Determines the type match status for an entity against a single required type.
    /// </summary>
    public Task<TypeMatchResult> CheckAsync(
        WikidataEntity entity,
        string? requiredType,
        IReadOnlyList<string>? excludeTypes,
        SubclassResolver? subclassResolver,
        string language,
        CancellationToken cancellationToken)
    {
        var requiredTypes = string.IsNullOrEmpty(requiredType) ? null : new[] { requiredType };
        return CheckAsync(entity, requiredTypes, excludeTypes, subclassResolver, language, cancellationToken);
    }

    /// <summary>
    /// Determines the type match status for an entity against multiple required types (OR logic).
    /// When subclassResolver is provided and depth > 0, walks P279 hierarchy.
    /// </summary>
    public async Task<TypeMatchResult> CheckAsync(
        WikidataEntity entity,
        IReadOnlyList<string>? requiredTypes,
        IReadOnlyList<string>? excludeTypes,
        SubclassResolver? subclassResolver,
        string language,
        CancellationToken cancellationToken)
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

            if (subclassResolver != null)
            {
                foreach (var excludeType in excludeTypes)
                {
                    if (await subclassResolver.IsSubclassOfAsync(entityTypes, excludeType, language, cancellationToken)
                            .ConfigureAwait(false))
                        return TypeMatchResult.Excluded;
                }
            }
        }

        // No type constraint requested — passes
        if (requiredTypes is not { Count: > 0 })
            return TypeMatchResult.NoConstraint;

        // Entity has no types at all
        if (entityTypes.Count == 0)
            return TypeMatchResult.NoType;

        // Check direct P31 match against any required type (OR logic)
        foreach (var requiredType in requiredTypes)
        {
            if (entityTypes.Any(t => string.Equals(t, requiredType, StringComparison.OrdinalIgnoreCase)))
                return TypeMatchResult.Matched;
        }

        // Check P279 subclass hierarchy if resolver is available
        if (subclassResolver != null)
        {
            foreach (var requiredType in requiredTypes)
            {
                if (await subclassResolver.IsSubclassOfAsync(entityTypes, requiredType, language, cancellationToken)
                        .ConfigureAwait(false))
                    return TypeMatchResult.Matched;
            }
        }

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
