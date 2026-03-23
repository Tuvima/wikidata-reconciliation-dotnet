using System.Text.Json;
using Tuvima.WikidataReconciliation.Internal.Json;

namespace Tuvima.WikidataReconciliation.Internal;

/// <summary>
/// Resolves chained property paths like "P131/P17" (administrative territory / country).
/// For a path like "P131/P17", it first gets the P131 values (which are entity QIDs),
/// then fetches those entities and gets their P17 values.
/// </summary>
internal sealed class PropertyPath
{
    private readonly string[] _segments;

    public PropertyPath(string path)
    {
        _segments = path.Split('/');
    }

    /// <summary>
    /// The first property in the chain. Used for direct claim lookup on the candidate entity.
    /// </summary>
    public string RootProperty => _segments[0];

    /// <summary>
    /// Whether this is a chained path (more than one segment).
    /// </summary>
    public bool IsChained => _segments.Length > 1;

    /// <summary>
    /// The remaining segments after the root (e.g., for "P131/P17", returns ["P17"]).
    /// </summary>
    public ReadOnlySpan<string> TailSegments => _segments.AsSpan(1);

    /// <summary>
    /// Resolves claim values for a property path on an entity.
    /// For simple paths, returns values directly. For chained paths,
    /// resolves intermediate entity references using the fetcher.
    /// </summary>
    public async Task<List<DataValue>> ResolveAsync(
        WikidataEntity entity,
        WikidataEntityFetcher fetcher,
        string language,
        CancellationToken cancellationToken)
    {
        var values = WikidataEntityFetcher.GetClaimValues(entity, _segments[0]);

        if (!IsChained || values.Count == 0)
            return values;

        // For chained paths, resolve intermediate entities
        // Extract QIDs from the intermediate values
        var intermediateIds = new List<string>();
        foreach (var val in values)
        {
            if (val.Value is JsonElement element && element.TryGetProperty("id", out var idProp))
            {
                var id = idProp.GetString();
                if (!string.IsNullOrEmpty(id))
                    intermediateIds.Add(id);
            }
        }

        if (intermediateIds.Count == 0)
            return [];

        // Fetch intermediate entities
        var intermediateEntities = await fetcher.FetchEntitiesAsync(intermediateIds, language, cancellationToken)
            .ConfigureAwait(false);

        // Walk remaining segments
        var currentEntities = intermediateEntities.Values.ToList();
        for (var i = 1; i < _segments.Length; i++)
        {
            var segment = _segments[i];
            var result = new List<DataValue>();

            foreach (var ent in currentEntities)
            {
                result.AddRange(WikidataEntityFetcher.GetClaimValues(ent, segment));
            }

            if (i < _segments.Length - 1 && result.Count > 0)
            {
                // More segments to resolve — extract QIDs and fetch
                var nextIds = new List<string>();
                foreach (var val in result)
                {
                    if (val.Value is JsonElement el && el.TryGetProperty("id", out var idp))
                    {
                        var id = idp.GetString();
                        if (!string.IsNullOrEmpty(id))
                            nextIds.Add(id);
                    }
                }

                if (nextIds.Count == 0)
                    return [];

                var nextEntities = await fetcher.FetchEntitiesAsync(nextIds, language, cancellationToken)
                    .ConfigureAwait(false);
                currentEntities = nextEntities.Values.ToList();
            }
            else
            {
                return result;
            }
        }

        return [];
    }
}
