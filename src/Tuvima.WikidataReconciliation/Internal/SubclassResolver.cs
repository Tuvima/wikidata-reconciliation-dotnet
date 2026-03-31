using System.Collections.Concurrent;
using System.Text.Json;
using Tuvima.WikidataReconciliation.Internal.Json;

namespace Tuvima.WikidataReconciliation.Internal;

/// <summary>
/// Resolves P279 (subclass of) hierarchies to determine if an entity's type
/// is a subclass of a target type. Uses BFS with in-memory caching.
/// </summary>
internal sealed class SubclassResolver
{
    private readonly WikidataEntityFetcher _fetcher;
    private readonly int _maxDepth;

    // Cache: type QID → set of all known superclass QIDs (including itself)
    private readonly ConcurrentDictionary<string, HashSet<string>> _superclassCache = new(StringComparer.OrdinalIgnoreCase);

    public SubclassResolver(WikidataEntityFetcher fetcher, int maxDepth)
    {
        _fetcher = fetcher;
        _maxDepth = maxDepth;
    }

    /// <summary>
    /// Returns true if any of the entity's type QIDs is equal to or a subclass of the target type.
    /// Walks P279 up to maxDepth levels (or overrideDepth if specified).
    /// </summary>
    public async Task<bool> IsSubclassOfAsync(
        IReadOnlyList<string> entityTypeQids,
        string targetTypeQid,
        string language,
        CancellationToken cancellationToken,
        int? overrideDepth = null)
    {
        // Quick check: direct match (no API calls needed)
        foreach (var typeQid in entityTypeQids)
        {
            if (string.Equals(typeQid, targetTypeQid, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Check cached superclasses first
        foreach (var typeQid in entityTypeQids)
        {
            if (_superclassCache.TryGetValue(typeQid, out var cached) && cached.Contains(targetTypeQid))
                return true;
        }

        // BFS: walk P279 hierarchy for each entity type
        var depth = overrideDepth ?? _maxDepth;
        foreach (var typeQid in entityTypeQids)
        {
            if (await WalkSuperclassesAsync(typeQid, targetTypeQid, language, depth, cancellationToken).ConfigureAwait(false))
                return true;
        }

        return false;
    }

    private async Task<bool> WalkSuperclassesAsync(
        string startQid, string targetQid, string language, int maxDepth, CancellationToken cancellationToken)
    {
        // Check cache
        if (_superclassCache.TryGetValue(startQid, out var cached))
            return cached.Contains(targetQid);

        var superclasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { startQid };
        var frontier = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { startQid };

        for (var depth = 0; depth < maxDepth && frontier.Count > 0; depth++)
        {
            // Fetch all frontier entities in one batch
            var entities = await _fetcher.FetchEntitiesAsync(frontier.ToList(), language, cancellationToken)
                .ConfigureAwait(false);

            var nextFrontier = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (qid, entity) in entities)
            {
                // Extract P279 (subclass of) values
                var parentTypes = GetP279Values(entity);
                foreach (var parent in parentTypes)
                {
                    if (superclasses.Add(parent))
                        nextFrontier.Add(parent);
                }
            }

            frontier = nextFrontier;

            // Early exit if we found the target
            if (superclasses.Contains(targetQid))
            {
                _superclassCache[startQid] = superclasses;
                return true;
            }
        }

        // Cache the result even if not found (avoid re-fetching)
        _superclassCache[startQid] = superclasses;
        return false;
    }

    private static List<string> GetP279Values(WikidataEntity entity)
    {
        if (entity.Claims?.TryGetValue("P279", out var claims) != true)
            return [];

        var values = new List<string>();
        foreach (var claim in claims!)
        {
            if (claim.MainSnak?.SnakType == "value" && claim.MainSnak.DataValue?.Value is JsonElement element)
            {
                if (element.TryGetProperty("id", out var idProp))
                {
                    var id = idProp.GetString();
                    if (!string.IsNullOrEmpty(id))
                        values.Add(id);
                }
            }
        }

        return values;
    }
}
