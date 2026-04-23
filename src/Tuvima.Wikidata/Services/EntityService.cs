using Tuvima.Wikidata.Internal;
using Tuvima.Wikidata.Internal.Json;

namespace Tuvima.Wikidata.Services;

/// <summary>
/// Entity and property data fetching (wbgetentities), external ID lookup, staleness detection,
/// and change monitoring. Obtained via <see cref="WikidataReconciler.Entities"/>.
/// </summary>
public sealed class EntityService
{
    private readonly ReconcilerContext _ctx;

    internal EntityService(ReconcilerContext ctx) => _ctx = ctx;

    /// <summary>
    /// Fetches full entity data for the given QIDs.
    /// </summary>
    public Task<IReadOnlyDictionary<string, WikidataEntityInfo>> GetEntitiesAsync(
        IReadOnlyList<string> qids, string? language = null, CancellationToken cancellationToken = default)
        => GetEntitiesAsync(qids, resolveEntityLabels: false, language, cancellationToken);

    /// <summary>
    /// Fetches full entity data. When <paramref name="resolveEntityLabels"/> is true,
    /// entity-valued claims have their <see cref="WikidataValue.EntityLabel"/> populated.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, WikidataEntityInfo>> GetEntitiesAsync(
        IReadOnlyList<string> qids, bool resolveEntityLabels,
        string? language = null, CancellationToken cancellationToken = default)
    {
        var lang = language ?? _ctx.Options.Language;
        var entities = await _ctx.EntityFetcher.FetchEntitiesAsync(qids, lang, cancellationToken)
            .ConfigureAwait(false);

        var result = new Dictionary<string, WikidataEntityInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, entity) in entities)
        {
            result[id] = EntityMapper.MapEntity(entity, lang);
        }

        if (resolveEntityLabels)
            await ResolveEntityLabelsAsync(result, lang, cancellationToken).ConfigureAwait(false);

        return result;
    }

    private async Task ResolveEntityLabelsAsync(
        Dictionary<string, WikidataEntityInfo> entities, string language, CancellationToken cancellationToken)
    {
        var referencedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entityInfo in entities.Values)
        {
            foreach (var claims in entityInfo.Claims.Values)
            {
                foreach (var claim in claims)
                {
                    if (claim.Value?.Kind == WikidataValueKind.EntityId && !string.IsNullOrEmpty(claim.Value.EntityId))
                        referencedIds.Add(claim.Value.EntityId);

                    foreach (var qualValues in claim.Qualifiers.Values)
                    {
                        foreach (var qv in qualValues)
                        {
                            if (qv.Kind == WikidataValueKind.EntityId && !string.IsNullOrEmpty(qv.EntityId))
                                referencedIds.Add(qv.EntityId);
                        }
                    }
                }
            }
        }

        foreach (var id in entities.Keys)
            referencedIds.Remove(id);

        if (referencedIds.Count == 0 && entities.Count == 0)
            return;

        var labelLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, info) in entities)
        {
            if (!string.IsNullOrEmpty(info.Label))
                labelLookup[id] = info.Label;
        }

        if (referencedIds.Count > 0)
        {
            var labelEntities = await _ctx.EntityFetcher.FetchLabelsOnlyAsync(
                referencedIds.ToList(), language, cancellationToken).ConfigureAwait(false);

            foreach (var (id, entity) in labelEntities)
            {
                if (LanguageFallback.TryGetValue(entity.Labels, language, out var label))
                    labelLookup[id] = label;
            }
        }

        foreach (var entityInfo in entities.Values)
        {
            foreach (var claims in entityInfo.Claims.Values)
            {
                foreach (var claim in claims)
                {
                    if (claim.Value?.Kind == WikidataValueKind.EntityId &&
                        !string.IsNullOrEmpty(claim.Value.EntityId) &&
                        labelLookup.TryGetValue(claim.Value.EntityId, out var label))
                    {
                        claim.Value.EntityLabel = label;
                    }

                    foreach (var qualValues in claim.Qualifiers.Values)
                    {
                        foreach (var qv in qualValues)
                        {
                            if (qv.Kind == WikidataValueKind.EntityId &&
                                !string.IsNullOrEmpty(qv.EntityId) &&
                                labelLookup.TryGetValue(qv.EntityId, out var qLabel))
                            {
                                qv.EntityLabel = qLabel;
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Fetches specific properties for the given QIDs.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<WikidataClaim>>>> GetPropertiesAsync(
        IReadOnlyList<string> qids, IReadOnlyList<string> propertyIds,
        string? language = null, CancellationToken cancellationToken = default)
    {
        var lang = language ?? _ctx.Options.Language;
        var entities = await _ctx.EntityFetcher.FetchEntitiesAsync(qids, lang, cancellationToken)
            .ConfigureAwait(false);

        var propertySet = new HashSet<string>(propertyIds, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<WikidataClaim>>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, entity) in entities)
        {
            var allClaims = EntityMapper.MapClaims(entity.Claims);
            var filtered = new Dictionary<string, IReadOnlyList<WikidataClaim>>();

            foreach (var (propId, claims) in allClaims)
            {
                if (propertySet.Contains(propId))
                    filtered[propId] = claims;
            }

            result[id] = filtered;
        }

        await ResolveClaimsEntityLabelsAsync(result, lang, cancellationToken).ConfigureAwait(false);

        return result;
    }

    private async Task ResolveClaimsEntityLabelsAsync(
        Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<WikidataClaim>>> propertyResult,
        string language, CancellationToken cancellationToken)
    {
        var referencedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var propDict in propertyResult.Values)
        {
            foreach (var claims in propDict.Values)
            {
                foreach (var claim in claims)
                {
                    if (claim.Value?.Kind == WikidataValueKind.EntityId && !string.IsNullOrEmpty(claim.Value.EntityId))
                        referencedIds.Add(claim.Value.EntityId);

                    foreach (var qualValues in claim.Qualifiers.Values)
                    {
                        foreach (var qv in qualValues)
                        {
                            if (qv.Kind == WikidataValueKind.EntityId && !string.IsNullOrEmpty(qv.EntityId))
                                referencedIds.Add(qv.EntityId);
                        }
                    }
                }
            }
        }

        if (referencedIds.Count == 0)
            return;

        var labelLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var labelEntities = await _ctx.EntityFetcher.FetchLabelsOnlyAsync(
            referencedIds.ToList(), language, cancellationToken).ConfigureAwait(false);

        foreach (var (id, entity) in labelEntities)
        {
            if (LanguageFallback.TryGetValue(entity.Labels, language, out var label))
                labelLookup[id] = label;
        }

        foreach (var propDict in propertyResult.Values)
        {
            foreach (var claims in propDict.Values)
            {
                foreach (var claim in claims)
                {
                    if (claim.Value?.Kind == WikidataValueKind.EntityId &&
                        !string.IsNullOrEmpty(claim.Value.EntityId) &&
                        labelLookup.TryGetValue(claim.Value.EntityId, out var claimLabel))
                    {
                        claim.Value.EntityLabel = claimLabel;
                    }

                    foreach (var qualValues in claim.Qualifiers.Values)
                    {
                        foreach (var qv in qualValues)
                        {
                            if (qv.Kind == WikidataValueKind.EntityId &&
                                !string.IsNullOrEmpty(qv.EntityId) &&
                                labelLookup.TryGetValue(qv.EntityId, out var qLabel))
                            {
                                qv.EntityLabel = qLabel;
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Finds Wikidata entities by an external identifier value.
    /// </summary>
    public async Task<IReadOnlyList<WikidataEntityInfo>> LookupByExternalIdAsync(
        string propertyId, string value, string? language = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var lang = language ?? _ctx.Options.Language;
        var ids = await _ctx.SearchClient.SearchByExternalIdAsync(propertyId, value, 10, cancellationToken)
            .ConfigureAwait(false);

        if (ids.Count == 0)
            return [];

        var entities = await _ctx.EntityFetcher.FetchEntitiesAsync(ids, lang, cancellationToken)
            .ConfigureAwait(false);

        return ids
            .Where(id => entities.ContainsKey(id))
            .Select(id => EntityMapper.MapEntity(entities[id], lang))
            .ToList();
    }

    /// <summary>
    /// Resolves human-readable labels for Wikidata property IDs (e.g., "P569" → "date of birth").
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> GetPropertyLabelsAsync(
        IReadOnlyList<string> propertyIds, string? language = null, CancellationToken cancellationToken = default)
    {
        var lang = language ?? _ctx.Options.Language;

        var entities = await _ctx.EntityFetcher.FetchEntitiesAsync(propertyIds, lang, cancellationToken)
            .ConfigureAwait(false);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, entity) in entities)
        {
            if (LanguageFallback.TryGetValue(entity.Labels, lang, out var label))
                result[id] = label;
        }

        return result;
    }

    /// <summary>
    /// Fetches Wikimedia Commons image URLs for entities with a P18 (image) claim.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> GetImageUrlsAsync(
        IReadOnlyList<string> qids, string? language = null, CancellationToken cancellationToken = default)
    {
        var lang = language ?? _ctx.Options.Language;
        var entities = await _ctx.EntityFetcher.FetchEntitiesAsync(qids, lang, cancellationToken)
            .ConfigureAwait(false);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, entity) in entities)
        {
            var imageValues = WikidataEntityFetcher.GetClaimValues(entity, "P18");
            if (imageValues.Count > 0 && imageValues[0].Value is System.Text.Json.JsonElement element &&
                element.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var filename = element.GetString();
                if (!string.IsNullOrEmpty(filename))
                    result[id] = $"https://commons.wikimedia.org/wiki/Special:FilePath/{Uri.EscapeDataString(filename)}";
            }
        }

        return result;
    }

    /// <summary>
    /// Lightweight revision ID + timestamp lookup for staleness detection.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, EntityRevision>> GetRevisionIdsAsync(
        IReadOnlyList<string> qids, CancellationToken cancellationToken = default)
    {
        if (qids.Count == 0)
            return new Dictionary<string, EntityRevision>();

        var result = new Dictionary<string, EntityRevision>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < qids.Count; i += 50)
        {
            var batch = qids.Skip(i).Take(50).ToList();
            var titles = string.Join('|', batch);

            var url = $"{_ctx.Options.ApiEndpoint}?action=query&prop=revisions" +
                      $"&titles={Uri.EscapeDataString(titles)}&rvprop=ids|timestamp&format=json";

            var json = await _ctx.ResilientClient.GetStringAsync(url, cancellationToken)
                .ConfigureAwait(false);
            var response = System.Text.Json.JsonSerializer.Deserialize(json,
                WikidataJsonContext.Default.RevisionQueryResponse);

            if (response?.Query?.Pages is null)
                continue;

            foreach (var page in response.Query.Pages.Values)
            {
                if (page.Revisions is not { Count: > 0 })
                    continue;

                var rev = page.Revisions[0];
                DateTimeOffset? timestamp = null;
                if (!string.IsNullOrEmpty(rev.Timestamp) && DateTimeOffset.TryParse(rev.Timestamp, out var ts))
                    timestamp = ts;

                result[page.Title] = new EntityRevision
                {
                    EntityId = page.Title,
                    RevisionId = rev.RevId,
                    Timestamp = timestamp
                };
            }
        }

        return result;
    }

    /// <summary>
    /// Checks for recent changes to specific Wikidata entities.
    /// </summary>
    public async Task<IReadOnlyList<EntityChange>> GetRecentChangesAsync(
        IReadOnlyList<string> qids, DateTimeOffset? since = null,
        CancellationToken cancellationToken = default)
    {
        if (qids.Count == 0)
            return [];

        var sinceTime = since ?? DateTimeOffset.UtcNow.AddHours(-24);
        var titles = string.Join('|', qids);
        var rcStart = sinceTime.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");

        var qidSet = new HashSet<string>(qids, StringComparer.OrdinalIgnoreCase);
        var changes = new List<EntityChange>();
        string? rcContinue = null;

        do
        {
            var url = $"{_ctx.Options.ApiEndpoint}?action=query&list=recentchanges" +
                      $"&rctitle={Uri.EscapeDataString(titles)}" +
                      $"&rcstart={rcStart}&rcdir=newer&rclimit=500" +
                      "&rcprop=title|timestamp|user|comment|ids&rctype=edit|new&format=json";

            if (!string.IsNullOrEmpty(rcContinue))
                url += $"&rccontinue={Uri.EscapeDataString(rcContinue)}";

            var json = await _ctx.ResilientClient.GetStringAsync(url, cancellationToken)
                .ConfigureAwait(false);
            var response = System.Text.Json.JsonSerializer.Deserialize(json,
                WikidataJsonContext.Default.RecentChangesResponse);

            if (response?.Query?.RecentChanges is { Count: > 0 } recentChanges)
            {
                changes.AddRange(recentChanges
                    .Where(rc => qidSet.Contains(rc.Title))
                    .Select(rc => new EntityChange
                    {
                        EntityId = rc.Title,
                        ChangeType = rc.Type,
                        Timestamp = DateTimeOffset.TryParse(rc.Timestamp, out var ts) ? ts : DateTimeOffset.MinValue,
                        User = rc.User,
                        Comment = rc.Comment,
                        RevisionId = rc.RevId
                    }));
            }

            rcContinue = response?.Continue?.RcContinueToken;
        }
        while (!string.IsNullOrEmpty(rcContinue));

        return changes
            .OrderByDescending(c => c.Timestamp)
            .ToList();
    }
}
