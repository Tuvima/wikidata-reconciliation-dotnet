using Tuvima.Wikidata.Internal;

namespace Tuvima.Wikidata.Services;

/// <summary>
/// Child-entity traversal and manifest-building operations.
/// Obtained via <see cref="WikidataReconciler.Children"/>.
/// </summary>
public sealed class ChildrenService
{
    private readonly ReconcilerContext _ctx;

    internal ChildrenService(ReconcilerContext ctx) => _ctx = ctx;

    /// <summary>
    /// Generic child-entity traversal. Discovers children of a parent via a relationship property,
    /// optionally filtered by P31 type classes. This is the lower-level primitive used by the
    /// preset-based <see cref="GetChildEntitiesAsync"/> (v2 rename of the v1 method by the same name,
    /// which returned the same shape but now takes a <see cref="ChildEntityRequest"/>).
    /// </summary>
    /// <param name="parentQid">The parent entity's QID.</param>
    /// <param name="relationshipProperty">The Wikidata property to traverse (e.g., "P527").</param>
    /// <param name="direction">
    /// <see cref="Direction.Outgoing"/> follows the property forward from the parent;
    /// <see cref="Direction.Incoming"/> finds entities whose property points to the parent.
    /// </param>
    /// <param name="childTypeFilter">Optional P31 class QIDs to filter children by.</param>
    /// <param name="childProperties">Property codes to fetch for each child entity.</param>
    /// <param name="language">Language for labels. Defaults to configured language.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Ordered child entities (by P1545 ordinal → P577 date → label).</returns>
    public async Task<IReadOnlyList<ChildEntityInfo>> TraverseChildrenAsync(
        string parentQid,
        string relationshipProperty,
        Direction direction = Direction.Outgoing,
        IReadOnlyList<string>? childTypeFilter = null,
        IReadOnlyList<string>? childProperties = null,
        string? language = null,
        CancellationToken cancellationToken = default)
        => await TraverseChildrenInternalAsync(
            parentQid,
            relationshipProperty,
            direction,
            childTypeFilter,
            childProperties,
            ordinalProperty: "P1545",
            language,
            cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<ChildEntityInfo>> TraverseChildrenInternalAsync(
        string parentQid,
        string relationshipProperty,
        Direction direction,
        IReadOnlyList<string>? childTypeFilter,
        IReadOnlyList<string>? childProperties,
        string ordinalProperty,
        string? language,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentQid);
        ArgumentException.ThrowIfNullOrWhiteSpace(relationshipProperty);

        var lang = language ?? _ctx.Options.Language;
        var isReverse = direction == Direction.Incoming;

        List<string> childIds;

        if (isReverse)
        {
            var query = $"haswbstatement:{relationshipProperty}={parentQid}";
            childIds = await _ctx.SearchClient.SearchAllByStatementAsync(query, childTypeFilter, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            var parentEntities = await _ctx.EntityFetcher.FetchEntitiesAsync([parentQid], lang, cancellationToken)
                .ConfigureAwait(false);

            if (!parentEntities.TryGetValue(parentQid, out var parentEntity))
                return [];

            childIds = WikidataEntityFetcher.GetClaimValues(parentEntity, relationshipProperty)
                .Select(dv => EntityMapper.MapDataValue(dv, "wikibase-item"))
                .Where(v => v.Kind == WikidataValueKind.EntityId && !string.IsNullOrEmpty(v.EntityId))
                .Select(v => v.EntityId!)
                .ToList();
        }

        if (childIds.Count == 0)
            return [];

        var childEntities = await _ctx.EntityFetcher.FetchEntitiesAsync(childIds, lang, cancellationToken)
            .ConfigureAwait(false);

        var filterSet = childTypeFilter is { Count: > 0 }
            ? new HashSet<string>(childTypeFilter, StringComparer.OrdinalIgnoreCase)
            : null;

        var requestedProps = childProperties is { Count: > 0 }
            ? new HashSet<string>(childProperties, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var sortableResults = new List<(ChildEntityInfo Info, string? DateSort)>();
        foreach (var (id, entity) in childEntities)
        {
            if (!isReverse && filterSet is not null)
            {
                var types = WikidataEntityFetcher.GetTypeIds(entity, _ctx.Options.TypePropertyId);
                if (!types.Any(t => filterSet.Contains(t)))
                    continue;
            }

            LanguageFallback.TryGetValue(entity.Labels, lang, out var label);
            LanguageFallback.TryGetValue(entity.Descriptions, lang, out var description);

            int? ordinal = null;
            var ordinalValues = WikidataEntityFetcher.GetClaimValues(entity, ordinalProperty);
            if (ordinalValues.Count > 0)
            {
                var ordinalValue = EntityMapper.MapDataValue(ordinalValues[0], "string");
                if (int.TryParse(ordinalValue.RawValue, out var parsed))
                    ordinal = parsed;
            }

            string? dateSort = null;
            var dateValues = WikidataEntityFetcher.GetClaimValues(entity, "P577");
            if (dateValues.Count > 0)
            {
                var dateValue = EntityMapper.MapDataValue(dateValues[0], "time");
                if (dateValue.Kind == WikidataValueKind.Time)
                    dateSort = dateValue.RawValue;
            }

            var props = EntityMapper.MapClaims(entity.Claims);
            if (requestedProps.Count > 0)
            {
                props = new Dictionary<string, IReadOnlyList<WikidataClaim>>(
                    props.Where(kvp => requestedProps.Contains(kvp.Key)));
            }

            sortableResults.Add((new ChildEntityInfo
            {
                EntityId = id,
                Label = string.IsNullOrEmpty(label) ? null : label,
                Description = string.IsNullOrEmpty(description) ? null : description,
                Ordinal = ordinal,
                Properties = props
            }, dateSort));
        }

        sortableResults.Sort((a, b) =>
        {
            if (a.Info.Ordinal.HasValue && b.Info.Ordinal.HasValue)
                return a.Info.Ordinal.Value.CompareTo(b.Info.Ordinal.Value);

            if (a.Info.Ordinal.HasValue != b.Info.Ordinal.HasValue)
                return a.Info.Ordinal.HasValue ? -1 : 1;

            if (a.DateSort is not null && b.DateSort is not null)
                return string.Compare(a.DateSort, b.DateSort, StringComparison.Ordinal);
            if (a.DateSort is not null || b.DateSort is not null)
                return a.DateSort is not null ? -1 : 1;

            return string.Compare(a.Info.Label, b.Info.Label, StringComparison.OrdinalIgnoreCase);
        });

        return sortableResults.Select(r => r.Info).ToList();
    }

    /// <summary>
    /// Builds a structured <see cref="ChildEntityManifest"/> for the parent entity using the
    /// preset specified in <paramref name="request"/>. For multi-level presets like
    /// <see cref="ChildEntityKind.TvSeasonsAndEpisodes"/>, the manifest contains every level.
    /// </summary>
    public async Task<ChildEntityManifest> GetChildEntitiesAsync(
        ChildEntityRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ParentQid);

        return request.Kind switch
        {
            ChildEntityKind.TvSeasonsAndEpisodes => await BuildTvManifestAsync(request, cancellationToken).ConfigureAwait(false),
            ChildEntityKind.MusicTracks => await BuildMusicManifestAsync(request, cancellationToken).ConfigureAwait(false),
            ChildEntityKind.ComicIssues => await BuildComicIssuesManifestAsync(request, cancellationToken).ConfigureAwait(false),
            ChildEntityKind.BookSequels => await BuildBookSequelsManifestAsync(request, cancellationToken).ConfigureAwait(false),
            ChildEntityKind.Custom => await BuildCustomManifestAsync(request, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException($"Unsupported ChildEntityKind: {request.Kind}", nameof(request))
        };
    }

    private async Task<ChildEntityManifest> BuildTvManifestAsync(ChildEntityRequest request, CancellationToken ct)
    {
        // Seasons via P527 filtered to Q3464665 (TV season)
        var seasons = await TraverseChildrenAsync(
            request.ParentQid, "P527", Direction.Outgoing,
            childTypeFilter: ["Q3464665"],
            childProperties: null,
            language: request.Language,
            cancellationToken: ct).ConfigureAwait(false);

        var cappedSeasons = seasons.Take(request.MaxPrimary).ToList();
        var children = new List<ChildEntityRef>();

        // Add season entries
        foreach (var season in cappedSeasons)
        {
            children.Add(MapToChildRef(season, parent: null));
            if (children.Count >= request.MaxTotal) break;
        }

        // Episodes per season
        foreach (var season in cappedSeasons)
        {
            if (children.Count >= request.MaxTotal) break;

            var episodes = await TraverseChildrenAsync(
                season.EntityId, "P527", Direction.Outgoing,
                childTypeFilter: ["Q21191270"],
                childProperties: request.IncludeCreatorProperties ? ["P57", "P58", "P577", "P2047"] : ["P577", "P2047"],
                language: request.Language,
                cancellationToken: ct).ConfigureAwait(false);

            foreach (var episode in episodes)
            {
                if (children.Count >= request.MaxTotal) break;
                children.Add(MapToChildRef(episode, parent: season.Ordinal));
            }
        }

        return new ChildEntityManifest
        {
            ParentQid = request.ParentQid,
            PrimaryCount = seasons.Count,
            TotalCount = children.Count,
            Children = children
        };
    }

    private async Task<ChildEntityManifest> BuildMusicManifestAsync(ChildEntityRequest request, CancellationToken ct)
    {
        // Try P658 (tracklist) first, fall back to P527 (has parts)
        var tracks = await TraverseChildrenAsync(
            request.ParentQid, "P658", Direction.Outgoing,
            childProperties: request.IncludeCreatorProperties ? ["P175", "P577", "P2047"] : ["P577", "P2047"],
            language: request.Language,
            cancellationToken: ct).ConfigureAwait(false);

        if (tracks.Count == 0)
        {
            tracks = await TraverseChildrenAsync(
                request.ParentQid, "P527", Direction.Outgoing,
                childProperties: request.IncludeCreatorProperties ? ["P175", "P577", "P2047"] : ["P577", "P2047"],
                language: request.Language,
                cancellationToken: ct).ConfigureAwait(false);
        }

        var capped = tracks.Take(Math.Min(request.MaxPrimary, request.MaxTotal)).ToList();

        return new ChildEntityManifest
        {
            ParentQid = request.ParentQid,
            PrimaryCount = tracks.Count,
            TotalCount = capped.Count,
            Children = capped.Select(t => MapToChildRef(t, parent: null)).ToList()
        };
    }

    private async Task<ChildEntityManifest> BuildComicIssuesManifestAsync(ChildEntityRequest request, CancellationToken ct)
    {
        // Reverse P179 (part of series) filtered to Q14406742 (comic issue)
        var issues = await TraverseChildrenAsync(
            request.ParentQid, "P179", Direction.Incoming,
            childTypeFilter: ["Q14406742"],
            childProperties: request.IncludeCreatorProperties ? ["P50", "P577"] : ["P577"],
            language: request.Language,
            cancellationToken: ct).ConfigureAwait(false);

        var capped = issues.Take(Math.Min(request.MaxPrimary, request.MaxTotal)).ToList();

        return new ChildEntityManifest
        {
            ParentQid = request.ParentQid,
            PrimaryCount = issues.Count,
            TotalCount = capped.Count,
            Children = capped.Select(i => MapToChildRef(i, parent: null)).ToList()
        };
    }

    private async Task<ChildEntityManifest> BuildBookSequelsManifestAsync(ChildEntityRequest request, CancellationToken ct)
    {
        // Collect P156 (followed by) and P155 (follows) into a combined list.
        var follows = await TraverseChildrenAsync(
            request.ParentQid, "P155", Direction.Outgoing,
            childProperties: request.IncludeCreatorProperties ? ["P50", "P577"] : ["P577"],
            language: request.Language,
            cancellationToken: ct).ConfigureAwait(false);

        var followedBy = await TraverseChildrenAsync(
            request.ParentQid, "P156", Direction.Outgoing,
            childProperties: request.IncludeCreatorProperties ? ["P50", "P577"] : ["P577"],
            language: request.Language,
            cancellationToken: ct).ConfigureAwait(false);

        var combined = follows.Concat(followedBy).ToList();
        var capped = combined.Take(Math.Min(request.MaxPrimary, request.MaxTotal)).ToList();

        return new ChildEntityManifest
        {
            ParentQid = request.ParentQid,
            PrimaryCount = combined.Count,
            TotalCount = capped.Count,
            Children = capped.Select(b => MapToChildRef(b, parent: null)).ToList()
        };
    }

    private async Task<ChildEntityManifest> BuildCustomManifestAsync(ChildEntityRequest request, CancellationToken ct)
    {
        var traversal = request.CustomTraversal
            ?? throw new ArgumentException("CustomTraversal is required when Kind is Custom.", nameof(request));

        var properties = new List<string>();
        if (traversal.CreatorRoles is { Count: > 0 })
        {
            properties.AddRange(traversal.CreatorRoles.Values);
        }
        properties.Add("P577");
        properties.Add("P2047");

        var children = await TraverseChildrenInternalAsync(
            request.ParentQid, traversal.RelationshipProperty, traversal.Direction,
            traversal.ChildTypeFilter,
            properties,
            traversal.OrdinalProperty,
            request.Language,
            ct).ConfigureAwait(false);

        var capped = children.Take(Math.Min(request.MaxPrimary, request.MaxTotal)).ToList();

        return new ChildEntityManifest
        {
            ParentQid = request.ParentQid,
            PrimaryCount = children.Count,
            TotalCount = capped.Count,
            Children = capped.Select(c => MapCustomChildRef(c, traversal)).ToList()
        };
    }

    private static ChildEntityRef MapToChildRef(ChildEntityInfo info, int? parent)
    {
        return new ChildEntityRef
        {
            Qid = info.EntityId,
            Title = info.Label,
            Ordinal = info.Ordinal,
            Parent = parent,
            ReleaseDate = ExtractReleaseDate(info),
            Duration = ExtractDuration(info),
            Creators = ExtractCreators(info)
        };
    }

    private static ChildEntityRef MapCustomChildRef(ChildEntityInfo info, CustomChildTraversal traversal)
    {
        IReadOnlyDictionary<string, string>? creators = null;
        if (traversal.CreatorRoles is { Count: > 0 })
        {
            var creatorMap = new Dictionary<string, string>();
            foreach (var (role, propId) in traversal.CreatorRoles)
            {
                if (info.Properties.TryGetValue(propId, out var claims) && claims.Count > 0)
                {
                    var firstLabel = claims[0].Value?.EntityLabel ?? claims[0].Value?.RawValue;
                    if (!string.IsNullOrEmpty(firstLabel))
                        creatorMap[role] = firstLabel;
                }
            }
            if (creatorMap.Count > 0)
                creators = creatorMap;
        }

        return new ChildEntityRef
        {
            Qid = info.EntityId,
            Title = info.Label,
            Ordinal = info.Ordinal,
            ReleaseDate = ExtractReleaseDate(info),
            Duration = ExtractDuration(info),
            Creators = creators
        };
    }

    private static DateOnly? ExtractReleaseDate(ChildEntityInfo info)
    {
        if (!info.Properties.TryGetValue("P577", out var claims) || claims.Count == 0)
            return null;

        var value = claims[0].Value;
        if (value?.Kind != WikidataValueKind.Time || string.IsNullOrEmpty(value.RawValue))
            return null;

        // Wikidata time format: "+YYYY-MM-DDTHH:MM:SSZ" (positive) or "-YYYY-..." (BCE)
        var raw = value.RawValue;
        var trimmed = raw.StartsWith('+') ? raw[1..] : raw;
        if (DateTimeOffset.TryParse(trimmed, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return DateOnly.FromDateTime(parsed.UtcDateTime);
        }
        return null;
    }

    private static TimeSpan? ExtractDuration(ChildEntityInfo info)
    {
        if (!info.Properties.TryGetValue("P2047", out var claims) || claims.Count == 0)
            return null;

        var raw = claims[0].Value?.RawValue;
        if (string.IsNullOrEmpty(raw) || !double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var minutes))
            return null;

        return TimeSpan.FromMinutes(minutes);
    }

    private static IReadOnlyDictionary<string, string>? ExtractCreators(ChildEntityInfo info)
    {
        // Default creator roles based on common properties.
        var roleMap = new Dictionary<string, string>
        {
            { "P57", "Director" },
            { "P58", "Writer" },
            { "P50", "Author" },
            { "P175", "Performer" },
            { "P162", "Producer" }
        };

        var result = new Dictionary<string, string>();
        foreach (var (propId, role) in roleMap)
        {
            if (info.Properties.TryGetValue(propId, out var claims) && claims.Count > 0)
            {
                var label = claims[0].Value?.EntityLabel ?? claims[0].Value?.RawValue;
                if (!string.IsNullOrEmpty(label))
                    result[role] = label;
            }
        }

        return result.Count > 0 ? result : null;
    }
}
