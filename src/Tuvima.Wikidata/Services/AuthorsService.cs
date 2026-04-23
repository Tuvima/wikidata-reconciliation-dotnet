using System.Collections.Frozen;
using Tuvima.Wikidata.Internal;

namespace Tuvima.Wikidata.Services;

/// <summary>
/// Multi-author string parsing + pen-name aware author resolution.
/// Obtained via <see cref="WikidataReconciler.Authors"/>.
/// </summary>
public sealed class AuthorsService
{
    // Wikidata P31 classes that identify an entity as a pseudonym rather than a real person.
    // When a resolved entity has any of these in its P31 claims, the service treats it as a
    // collective pseudonym and walks P50 (author) / P170 (creator) to find the real authors.
    private static readonly FrozenSet<string> PseudonymClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Q16017119",  // collective pseudonym
        "Q4647632",   // pen name (hereditary pseudonym / shared identity)
        "Q108946349", // pseudonym used by multiple persons
        "Q2500638"    // nom de plume
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // Properties that link a collective pseudonym entity to its real authors, tried in order.
    // P527 (has part) is how Wikidata actually models collective pseudonyms like James S.A. Corey
    // (Q6142591) — the pseudonym entity "has parts" equal to its constituent real people.
    // P50 (author) and P170 (creator) are defensive fallbacks for cases where Wikidata uses
    // a different idiom. Post-filtering by Q5 (human) P31 on the discovered entities guards
    // against P527 noise from non-collective-pseudonym contexts slipping through.
    private static readonly string[] RealAuthorProperties = ["P527", "P50", "P170"];

    // Minimum reconciliation score for a resolved author to skip the reverse P742 fallback path.
    // Above this threshold we trust the direct reconcile hit; below it we probe the reverse
    // P742 lookup to see whether the input string was a pen name for a different entity.
    private const double DirectMatchConfidence = 80.0;

    // Minimum reconciliation score for a candidate to be considered resolved at all.
    // Below this threshold the name goes to UnresolvedNames.
    private const double MinAcceptConfidence = 50.0;

    private readonly ReconcilerContext _ctx;
    private readonly ReconciliationService _reconcile;

    internal AuthorsService(ReconcilerContext ctx, ReconciliationService reconcile)
    {
        _ctx = ctx;
        _reconcile = reconcile;
    }

    /// <summary>
    /// Resolves a raw author string into typed author matches.
    /// Handles multi-author splitting ("and", "&amp;", ";", ",", "with", CJK comma),
    /// "Last, First" detection, "et al." markers, and three distinct pseudonym patterns
    /// when <see cref="AuthorResolutionRequest.DetectPseudonyms"/> is true:
    /// <list type="bullet">
    /// <item><b>Pattern 1 — solo pen name:</b> "Richard Bachman" reverse-maps to Stephen King via <c>haswbstatement:P742</c>. Populates <see cref="ResolvedAuthor.RealNameQid"/>.</item>
    /// <item><b>Pattern 2 — pen name listed on real author:</b> "Stephen King" resolves directly and exposes his pen names via <see cref="ResolvedAuthor.Pseudonyms"/>.</item>
    /// <item><b>Pattern 3 — collective pseudonym:</b> "James S.A. Corey" resolves to the collective pseudonym entity and expands to its real authors via <see cref="ResolvedAuthor.RealAuthors"/>.</item>
    /// </list>
    /// </summary>
    public async Task<AuthorResolutionResult> ResolveAsync(
        AuthorResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RawAuthorString);

        var lang = request.Language ?? _ctx.Options.Language;
        var (names, unresolved) = SplitAuthors(request.RawAuthorString);

        if (names.Count == 0)
        {
            return new AuthorResolutionResult
            {
                Authors = [],
                UnresolvedNames = unresolved
            };
        }

        // Resolve names in parallel. The shared request sender enforces the configured
        // MaxConcurrency cap on outbound HTTP, while Task.WhenAll preserves input order
        // in the result array so Authors still reflects the raw-string order.
        var resolveTasks = new Task<ResolvedAuthor>[names.Count];
        for (var i = 0; i < names.Count; i++)
        {
            resolveTasks[i] = ResolveSingleNameAsync(
                names[i], request.WorkQidHint, request.DetectPseudonyms, lang, cancellationToken);
        }

        var resolved = await Task.WhenAll(resolveTasks).ConfigureAwait(false);

        var additionalUnresolved = new List<string>(unresolved);
        foreach (var r in resolved)
        {
            if (r.Qid is null)
                additionalUnresolved.Add(r.OriginalName);
        }

        return new AuthorResolutionResult
        {
            Authors = resolved,
            UnresolvedNames = additionalUnresolved
        };
    }

    /// <summary>
    /// Resolves a single author name, applying the three pseudonym patterns when requested.
    /// Each name gets its own reconciliation + pseudonym enrichment pipeline so multi-author
    /// inputs can have a mix of direct matches, reverse-looked-up pen names, and collective
    /// pseudonyms all coexisting in one result.
    /// </summary>
    private async Task<ResolvedAuthor> ResolveSingleNameAsync(
        string name,
        string? workQidHint,
        bool detectPseudonyms,
        string language,
        CancellationToken cancellationToken)
    {
        // Initial reconciliation without a type filter. Earlier versions filtered to
        // [Q5] + known pseudonym classes, but Wikidata's ontology is inconsistent across
        // collective pseudonyms — some are Q5, some are Q16017119, some use a class we
        // don't know about. Filtering excluded real matches. We trust the label match here
        // and post-check the resolved entity's P31 for pseudonym classes during enrichment.
        var matches = await _reconcile.ReconcileAsync(new ReconciliationRequest
        {
            Query = name,
            Properties = !string.IsNullOrWhiteSpace(workQidHint)
                ? [new PropertyConstraint("P800", workQidHint)]
                : null,
            Language = language,
            Limit = 3
        }, cancellationToken).ConfigureAwait(false);

        var best = matches.Count > 0 ? matches[0] : null;

        // Below the high-confidence threshold we still attempt Pattern 1 — the input might
        // be a pen name that doesn't reconcile directly as a human but has a P742 reverse hit.
        if (detectPseudonyms && (best is null || best.Score < DirectMatchConfidence))
        {
            var reverseHit = await TryReverseP742LookupAsync(name, language, cancellationToken)
                .ConfigureAwait(false);

            if (reverseHit is not null)
                return reverseHit;
        }

        if (best is null || best.Score < MinAcceptConfidence)
        {
            return new ResolvedAuthor
            {
                OriginalName = name,
                Confidence = best?.Score ?? 0.0
            };
        }

        // Direct hit above the accept threshold. Optionally enrich with pseudonym info.
        if (!detectPseudonyms)
        {
            return new ResolvedAuthor
            {
                OriginalName = name,
                Qid = best.Id,
                CanonicalName = best.Name,
                Confidence = best.Score
            };
        }

        var enrichment = await GetPseudonymEnrichmentAsync(best.Id, language, cancellationToken)
            .ConfigureAwait(false);

        return new ResolvedAuthor
        {
            OriginalName = name,
            Qid = best.Id,
            CanonicalName = best.Name,
            Pseudonyms = enrichment.Pseudonyms,
            RealAuthors = enrichment.RealAuthors,
            Confidence = best.Score
        };
    }

    /// <summary>
    /// Looks up the resolved entity's own claims and returns Pattern 2 (pseudonym strings) +
    /// Pattern 3 (collective pseudonym real-author expansion) data. The caller composes this
    /// enrichment with the base reconcile match info to build a full <see cref="ResolvedAuthor"/>.
    /// <para>
    /// When the entity has already been fetched (e.g., during reverse P742 lookup), pass it
    /// via <paramref name="prefetchedEntity"/> to skip the duplicate <c>wbgetentities</c> call.
    /// </para>
    /// </summary>
    private async Task<(IReadOnlyList<string>? Pseudonyms, IReadOnlyList<RealAuthor>? RealAuthors)>
        GetPseudonymEnrichmentAsync(
            string qid,
            string language,
            CancellationToken cancellationToken,
            Internal.Json.WikidataEntity? prefetchedEntity = null)
    {
        Internal.Json.WikidataEntity? entity = prefetchedEntity;

        if (entity is null)
        {
            var entities = await _ctx.EntityFetcher.FetchEntitiesAsync([qid], language, cancellationToken)
                .ConfigureAwait(false);

            if (!entities.TryGetValue(qid, out entity))
                return (null, null);
        }

        // Pattern 2: P742 strings on the resolved entity are the pen names it uses.
        var pseudonyms = ReadP742Strings(entity);

        // Pattern 3: if the resolved entity is a collective pseudonym, walk P527 / P50 / P170
        // to find the real authors and resolve their display labels in one batch.
        IReadOnlyList<RealAuthor>? realAuthors = null;
        var entityTypes = WikidataEntityFetcher.GetTypeIds(entity, _ctx.Options.TypePropertyId);

        if (entityTypes.Any(t => PseudonymClasses.Contains(t)))
        {
            realAuthors = await ExpandCollectivePseudonymAsync(entity, language, cancellationToken)
                .ConfigureAwait(false);
        }

        return (pseudonyms, realAuthors);
    }

    /// <summary>
    /// Pattern 3 implementation: walks the known "real author" properties on a collective
    /// pseudonym entity (P50 author, P170 creator) and batch-resolves the referenced entities'
    /// display labels. Returns null if no real authors could be discovered.
    /// </summary>
    private async Task<IReadOnlyList<RealAuthor>?> ExpandCollectivePseudonymAsync(
        Internal.Json.WikidataEntity pseudonymEntity,
        string language,
        CancellationToken cancellationToken)
    {
        var realAuthorQids = new List<string>();

        foreach (var propertyId in RealAuthorProperties)
        {
            var qids = WikidataEntityFetcher.GetClaimValues(pseudonymEntity, propertyId)
                .Select(dv => EntityMapper.MapDataValue(dv, "wikibase-item"))
                .Where(v => v.Kind == WikidataValueKind.EntityId && !string.IsNullOrEmpty(v.EntityId))
                .Select(v => v.EntityId!);

            foreach (var qid in qids)
            {
                if (!realAuthorQids.Contains(qid, StringComparer.OrdinalIgnoreCase))
                    realAuthorQids.Add(qid);
            }
        }

        if (realAuthorQids.Count == 0)
            return null;

        var realEntities = await _ctx.EntityFetcher.FetchEntitiesAsync(realAuthorQids, language, cancellationToken)
            .ConfigureAwait(false);

        var result = new List<RealAuthor>();
        foreach (var rid in realAuthorQids)
        {
            if (!realEntities.TryGetValue(rid, out var realEntity))
                continue;

            // Guard: only keep parts that are actually humans. P527 is a general-purpose
            // "has part" relationship and a pseudonym entity might also have non-human parts
            // (associated organizations, works, etc.) — we only want the real authors.
            var partTypes = WikidataEntityFetcher.GetTypeIds(realEntity, _ctx.Options.TypePropertyId);
            if (partTypes.Count > 0 && !partTypes.Contains("Q5", StringComparer.OrdinalIgnoreCase))
                continue;

            LanguageFallback.TryGetValue(realEntity.Labels, language, out var realLabel);
            if (string.IsNullOrEmpty(realLabel))
                continue;

            result.Add(new RealAuthor
            {
                Qid = rid,
                CanonicalName = realLabel
            });
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// Pattern 1 implementation: runs <c>haswbstatement:P742=&lt;name&gt;</c> to find a Wikidata
    /// entity whose P742 (pseudonym) claim contains the input string. If any hit comes back,
    /// the first hit is treated as the real author, and a <see cref="ResolvedAuthor"/> is built
    /// from it with <see cref="ResolvedAuthor.RealNameQid"/> pointing back at the same QID
    /// (there is no separate entity for the pen name).
    /// <para>
    /// Returns null when the reverse lookup finds nothing — either because the input isn't a
    /// pen name, or because Wikidata's CirrusSearch doesn't index the relevant P742 value.
    /// Callers then fall back to direct reconciliation.
    /// </para>
    /// </summary>
    private async Task<ResolvedAuthor?> TryReverseP742LookupAsync(
        string name,
        string language,
        CancellationToken cancellationToken)
    {
        var query = $"haswbstatement:P742=\"{name}\"";
        List<string> hits;
        try
        {
            hits = await _ctx.SearchClient.SearchAllByStatementAsync(query, null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // haswbstatement behavior on monolingualtext / string values isn't fully documented;
            // be defensive and treat API hiccups as "reverse lookup unavailable" rather than
            // propagating the exception.
            return null;
        }

        if (hits.Count == 0)
            return null;

        var realAuthorQid = hits[0];
        var realEntities = await _ctx.EntityFetcher.FetchEntitiesAsync([realAuthorQid], language, cancellationToken)
            .ConfigureAwait(false);

        if (!realEntities.TryGetValue(realAuthorQid, out var realEntity))
            return null;

        LanguageFallback.TryGetValue(realEntity.Labels, language, out var realLabel);
        if (string.IsNullOrEmpty(realLabel))
            realLabel = realAuthorQid;

        // Run the same enrichment on the real author so we still populate Pseudonyms /
        // RealAuthors consistently even when the resolution went through the reverse path.
        // Pass the already-fetched entity so we don't pay for a second wbgetentities call.
        var enrichment = await GetPseudonymEnrichmentAsync(
                realAuthorQid, language, cancellationToken, prefetchedEntity: realEntity)
            .ConfigureAwait(false);

        return new ResolvedAuthor
        {
            OriginalName = name,
            Qid = realAuthorQid,
            CanonicalName = realLabel,
            RealNameQid = realAuthorQid,
            Pseudonyms = enrichment.Pseudonyms,
            RealAuthors = enrichment.RealAuthors,
            // Confidence for reverse-resolved pen names is reported as 100 because the P742
            // claim is an authoritative Wikidata assertion, not a fuzzy label match.
            Confidence = 100.0
        };
    }

    private static IReadOnlyList<string>? ReadP742Strings(Internal.Json.WikidataEntity entity)
    {
        var pseudonyms = WikidataEntityFetcher.GetClaimValues(entity, "P742")
            .Select(dv => EntityMapper.MapDataValue(dv, "string"))
            .Where(v => !string.IsNullOrEmpty(v.RawValue))
            .Select(v => v.RawValue)
            .ToList();

        return pseudonyms.Count > 0 ? pseudonyms : null;
    }

    /// <summary>
    /// Splits a raw author string into individual names.
    /// Supported separators: " and ", " &amp; ", "; ", ", ", " with ", "、".
    /// Handles "Last, First" single-name form heuristically.
    /// Extracts trailing "et al." markers into the unresolved list.
    /// </summary>
    internal static (List<string> Names, List<string> Unresolved) SplitAuthors(string raw)
    {
        var unresolved = new List<string>();
        var trimmed = raw.Trim();

        // Extract trailing "et al." / "et al"
        var etAl = new[] { "et al.", "et al", "et. al.", "et. al" };
        foreach (var marker in etAl)
        {
            var idx = trimmed.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && idx >= trimmed.Length - marker.Length - 3)
            {
                unresolved.Add(marker);
                trimmed = trimmed[..idx].TrimEnd(',', ' ');
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(trimmed))
            return (new List<string>(), unresolved);

        // "Last, First" heuristic — a single comma with one space-separated token on each side.
        // e.g. "Tolkien, J. R. R." → single name.
        if (IsLastFirstForm(trimmed))
        {
            return (new List<string> { NormalizeName(trimmed) }, unresolved);
        }

        // Split on separators. Order matters: longer/word-boundary first.
        var separators = new[] { " and ", " & ", " with ", ";", "、" };
        var parts = new List<string> { trimmed };

        foreach (var sep in separators)
        {
            var next = new List<string>();
            foreach (var part in parts)
            {
                next.AddRange(part.Split(sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
            parts = next;
        }

        // Comma splitting is tricky — only split on comma if the comma is NOT part of a "Last, First" form.
        // Heuristic: if any resulting part looks like "Last, First", merge adjacent chunks.
        var commaExpanded = new List<string>();
        foreach (var part in parts)
        {
            // Count commas — if 0 or >= 2, split on commas; if exactly 1 and IsLastFirstForm, keep.
            if (part.Count(c => c == ',') == 0)
            {
                commaExpanded.Add(part);
            }
            else if (part.Count(c => c == ',') == 1 && IsLastFirstForm(part))
            {
                commaExpanded.Add(part);
            }
            else
            {
                commaExpanded.AddRange(part.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }

        var names = commaExpanded
            .Select(NormalizeName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();

        return (names, unresolved);
    }

    private static bool IsLastFirstForm(string text)
    {
        // Exactly one comma, non-empty on each side, and the first side has one token,
        // the second side has 1–4 tokens (first name + optional middle initials).
        var commaIdx = text.IndexOf(',');
        if (commaIdx < 0 || text.IndexOf(',', commaIdx + 1) >= 0)
            return false;

        var left = text[..commaIdx].Trim();
        var right = text[(commaIdx + 1)..].Trim();

        if (left.Length == 0 || right.Length == 0)
            return false;

        var leftTokens = left.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var rightTokens = right.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Left: single surname (possibly hyphenated/accented)
        // Right: 1-4 first/middle name tokens, each reasonably short
        if (leftTokens.Length != 1) return false;
        if (rightTokens.Length is < 1 or > 4) return false;

        // Heuristic: if the right side contains a word longer than 15 chars, it's probably not a first name.
        if (rightTokens.Any(t => t.Length > 15)) return false;

        return true;
    }

    private static string NormalizeName(string name)
    {
        name = name.Trim();

        // Convert "Last, First Middle" to "First Middle Last" for search.
        if (IsLastFirstForm(name))
        {
            var commaIdx = name.IndexOf(',');
            var last = name[..commaIdx].Trim();
            var first = name[(commaIdx + 1)..].Trim();
            return $"{first} {last}";
        }

        return name;
    }
}
