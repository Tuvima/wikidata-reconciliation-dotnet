using System.Collections.Frozen;
using Tuvima.Wikidata.Internal;

namespace Tuvima.Wikidata.Services;

/// <summary>
/// Role-aware person search. Reconciles a raw name against Q5 (human) — plus optionally
/// Q215380 (musical group) and Q5741069 (musical ensemble) — with scoring weighted by
/// role-appropriate occupations, notable-work context, and year/companion hints.
/// Obtained via <see cref="WikidataReconciler.Persons"/>.
/// </summary>
public sealed class PersonsService
{
    private const string HumanType = "Q5";
    private const string MusicalGroupType = "Q215380";
    private const string MusicalEnsembleType = "Q5741069";

    // Maps each role to the P106 (occupation) QIDs that best match it.
    // When Role == Unknown, no occupation filter is applied.
    private static readonly FrozenDictionary<PersonRole, string[]> RoleOccupations =
        new Dictionary<PersonRole, string[]>
        {
            [PersonRole.Author] = ["Q36180", "Q4853732", "Q49757"],              // writer, author, poet
            [PersonRole.Narrator] = ["Q1622272", "Q10800557"],                     // voice actor, audiobook narrator
            [PersonRole.Director] = ["Q2526255", "Q2722764", "Q3282637"],          // film director, TV director, film producer
            [PersonRole.Actor] = ["Q33999", "Q10800557", "Q2405480"],              // actor, voice actor, voice actor
            [PersonRole.VoiceActor] = ["Q1622272", "Q10800557"],                   // voice actor, audiobook narrator
            [PersonRole.Composer] = ["Q36834", "Q486748", "Q639669"],              // composer, film composer, musician
            [PersonRole.Performer] = ["Q488205", "Q639669", "Q177220", "Q33999"],  // performing artist, musician, singer, actor
            [PersonRole.Artist] = ["Q483501", "Q639669", "Q1028181"],              // artist, musician, painter
            [PersonRole.Screenwriter] = ["Q28389", "Q36180"]                       // screenwriter, writer
        }.ToFrozenDictionary();

    // Roles whose default IncludeMusicalGroups is true.
    private static readonly FrozenSet<PersonRole> MusicalGroupDefaultRoles =
        new HashSet<PersonRole> { PersonRole.Performer, PersonRole.Artist }.ToFrozenSet();

    private readonly ReconcilerContext _ctx;
    private readonly ReconciliationService _reconcile;
    private readonly LabelsService _labels;

    internal PersonsService(ReconcilerContext ctx, ReconciliationService reconcile, LabelsService labels)
    {
        _ctx = ctx;
        _reconcile = reconcile;
        _labels = labels;
    }

    /// <summary>
    /// Searches for a person (or musical group) matching the supplied name and role.
    /// </summary>
    public async Task<PersonSearchResult> SearchAsync(
        PersonSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);

        var language = request.Language ?? _ctx.Options.Language;
        var includeGroups = request.IncludeMusicalGroups
            ?? MusicalGroupDefaultRoles.Contains(request.Role);

        // Build the types filter for reconciliation.
        var types = new List<string> { HumanType };
        if (includeGroups)
        {
            types.Add(MusicalGroupType);
            types.Add(MusicalEnsembleType);
        }

        // Build property constraints from role, year, and companion hints.
        var constraints = BuildConstraints(request);

        var reconcileRequest = new ReconciliationRequest
        {
            Query = request.Name,
            Types = types,
            Properties = constraints.Count > 0 ? constraints : null,
            Language = language,
            Limit = 5
        };

        var matches = await _reconcile.ReconcileAsync(reconcileRequest, cancellationToken)
            .ConfigureAwait(false);

        if (matches.Count == 0)
        {
            return new PersonSearchResult { Found = false, Score = 0.0 };
        }

        var best = matches[0];
        var normalizedScore = best.Score / 100.0;

        // Fetch the resolved entity's claims to populate occupations, notable works, and (optionally) group membership.
        var entities = await _ctx.EntityFetcher.FetchEntitiesAsync([best.Id], language, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<string> occupations = [];
        IReadOnlyList<string> notableWorks = [];
        var isGroup = false;
        IReadOnlyList<string>? groupMembers = null;

        if (entities.TryGetValue(best.Id, out var entity))
        {
            occupations = WikidataEntityFetcher.GetClaimValues(entity, "P106")
                .Select(dv => EntityMapper.MapDataValue(dv, "wikibase-item"))
                .Where(v => v.Kind == WikidataValueKind.EntityId && !string.IsNullOrEmpty(v.EntityId))
                .Select(v => v.EntityId!)
                .ToList();

            notableWorks = WikidataEntityFetcher.GetClaimValues(entity, "P800")
                .Select(dv => EntityMapper.MapDataValue(dv, "wikibase-item"))
                .Where(v => v.Kind == WikidataValueKind.EntityId && !string.IsNullOrEmpty(v.EntityId))
                .Select(v => v.EntityId!)
                .ToList();

            var entityTypes = WikidataEntityFetcher.GetTypeIds(entity, _ctx.Options.TypePropertyId);
            isGroup = entityTypes.Any(t =>
                string.Equals(t, MusicalGroupType, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t, MusicalEnsembleType, StringComparison.OrdinalIgnoreCase));

            if (isGroup && request.ExpandGroupMembers)
            {
                groupMembers = WikidataEntityFetcher.GetClaimValues(entity, "P527")
                    .Select(dv => EntityMapper.MapDataValue(dv, "wikibase-item"))
                    .Where(v => v.Kind == WikidataValueKind.EntityId && !string.IsNullOrEmpty(v.EntityId))
                    .Select(v => v.EntityId!)
                    .ToList();
            }
        }

        return new PersonSearchResult
        {
            Found = normalizedScore >= request.AcceptThreshold,
            Qid = best.Id,
            CanonicalName = best.Name,
            IsGroup = isGroup,
            Score = normalizedScore,
            Occupations = occupations,
            NotableWorks = notableWorks,
            GroupMembers = groupMembers
        };
    }

    private static List<PropertyConstraint> BuildConstraints(PersonSearchRequest request)
    {
        var constraints = new List<PropertyConstraint>();

        // P106 (occupation) constraint based on role.
        if (request.Role != PersonRole.Unknown &&
            RoleOccupations.TryGetValue(request.Role, out var occupations))
        {
            constraints.Add(new PropertyConstraint("P106", occupations));
        }

        // P800 (notable work) context via WorkQid.
        if (!string.IsNullOrEmpty(request.WorkQid))
        {
            constraints.Add(new PropertyConstraint("P800", request.WorkQid));
        }

        // P569 (date of birth) year hint.
        if (request.BirthYearHint.HasValue)
        {
            constraints.Add(new PropertyConstraint("P569", $"{request.BirthYearHint.Value:D4}-01-01"));
        }

        // P570 (date of death) year hint.
        if (request.DeathYearHint.HasValue)
        {
            constraints.Add(new PropertyConstraint("P570", $"{request.DeathYearHint.Value:D4}-01-01"));
        }

        // Companion names — unlike the other hints, we can't directly express "a P800 that references
        // an entity labelled X" as a PropertyConstraint. Companion hints currently feed the title hint
        // via the reconciler's label scoring pool when available. A stronger integration is a v2.3 enhancement.

        return constraints;
    }
}
