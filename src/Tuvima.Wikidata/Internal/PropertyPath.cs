using System.Text.Json;
using Tuvima.Wikidata.Internal.Json;

namespace Tuvima.Wikidata.Internal;

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
    public async Task<List<ResolvedPropertyValue>> ResolveAsync(
        WikidataEntity entity,
        WikidataEntityFetcher fetcher,
        string language,
        CancellationToken cancellationToken)
    {
        var values = GetPropertyValues(entity, _segments[0]);

        if (!IsChained || values.Count == 0)
            return values;

        // For chained paths, resolve intermediate entities
        // Extract QIDs from the intermediate values
        var intermediateIds = ExtractEntityIds(values);

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
            var result = new List<ResolvedPropertyValue>();

            foreach (var ent in currentEntities)
            {
                result.AddRange(GetPropertyValues(ent, segment));
            }

            if (i < _segments.Length - 1 && result.Count > 0)
            {
                // More segments to resolve — extract QIDs and fetch
                var nextIds = ExtractEntityIds(result);

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

    private static List<ResolvedPropertyValue> GetPropertyValues(WikidataEntity entity, string propertyId)
    {
        if (entity.Claims?.TryGetValue(propertyId, out var claims) != true || claims!.Count == 0)
            return [];

        var validClaims = claims
            .Where(c => c.Rank != "deprecated" && c.MainSnak?.SnakType == "value" && c.MainSnak.DataValue != null)
            .ToList();

        var preferred = validClaims.Where(c => c.Rank == "preferred").ToList();
        var source = preferred.Count > 0 ? preferred : validClaims.Where(c => c.Rank == "normal").ToList();

        return source
            .Select(c => new ResolvedPropertyValue(c.MainSnak!.DataValue!, c.MainSnak.DataType))
            .ToList();
    }

    private static List<string> ExtractEntityIds(IEnumerable<ResolvedPropertyValue> values)
    {
        var ids = new List<string>();
        foreach (var value in values)
        {
            if (value.DataValue.Value is JsonElement element && element.TryGetProperty("id", out var idProp))
            {
                var id = idProp.GetString();
                if (!string.IsNullOrEmpty(id))
                    ids.Add(id);
            }
        }

        return ids;
    }
}

internal readonly record struct ResolvedPropertyValue(DataValue DataValue, string? DataType);
