using Tuvima.Wikidata.Internal;

namespace Tuvima.Wikidata.Services;

/// <summary>
/// Multi-author string parsing + pen-name aware author resolution.
/// Obtained via <see cref="WikidataReconciler.Authors"/>.
/// </summary>
public sealed class AuthorsService
{
    private readonly ReconcilerContext _ctx;
    private readonly ReconciliationService _reconcile;

    internal AuthorsService(ReconcilerContext ctx, ReconciliationService reconcile)
    {
        _ctx = ctx;
        _reconcile = reconcile;
    }

    /// <summary>
    /// Resolves a raw author string into typed author matches.
    /// Handles multi-author splitting, "Last, First" detection, "et al." markers,
    /// and (optionally) pen-name resolution via P742.
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

        // Reconcile each name against Q5 (human) with the work hint as context.
        var resolvedList = new List<ResolvedAuthor>(names.Count);
        var additionalUnresolved = new List<string>(unresolved);

        foreach (var name in names)
        {
            var reconcileRequest = new ReconciliationRequest
            {
                Query = name,
                Types = ["Q5"],
                Language = lang,
                Limit = 3
            };

            var matches = await _reconcile.ReconcileAsync(reconcileRequest, cancellationToken)
                .ConfigureAwait(false);

            var best = matches.Count > 0 ? matches[0] : null;

            if (best is null || best.Score < 50.0)
            {
                resolvedList.Add(new ResolvedAuthor
                {
                    OriginalName = name,
                    Confidence = best?.Score ?? 0.0
                });
                additionalUnresolved.Add(name);
                continue;
            }

            IReadOnlyList<string>? pseudonyms = null;
            if (request.DetectPseudonyms)
            {
                pseudonyms = await GetPseudonymsAsync(best.Id, lang, cancellationToken)
                    .ConfigureAwait(false);
            }

            resolvedList.Add(new ResolvedAuthor
            {
                OriginalName = name,
                Qid = best.Id,
                CanonicalName = best.Name,
                RealNameQid = null, // reserved for future reverse lookup; see remarks on ResolvedAuthor.Pseudonyms
                Pseudonyms = pseudonyms,
                Confidence = best.Score
            });
        }

        return new AuthorResolutionResult
        {
            Authors = resolvedList,
            UnresolvedNames = additionalUnresolved
        };
    }

    /// <summary>
    /// Reads P742 (pseudonym) claims from the resolved author and returns the raw string values.
    /// Wikidata models pseudonyms as string claims on the owning real author rather than as
    /// separate pseudonym entities, so looking up either "Stephen King" or "Richard Bachman"
    /// typically resolves to the same QID (Q39829) — which has P742 = "Richard Bachman".
    /// Returns null when the entity cannot be fetched or has no P742 claims.
    /// </summary>
    private async Task<IReadOnlyList<string>?> GetPseudonymsAsync(string qid, string language, CancellationToken cancellationToken)
    {
        var entities = await _ctx.EntityFetcher.FetchEntitiesAsync([qid], language, cancellationToken)
            .ConfigureAwait(false);

        if (!entities.TryGetValue(qid, out var entity))
            return null;

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
