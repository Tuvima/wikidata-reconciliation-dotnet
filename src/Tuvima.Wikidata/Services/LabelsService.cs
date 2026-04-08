using Tuvima.Wikidata.Internal;

namespace Tuvima.Wikidata.Services;

/// <summary>
/// Single-entity and batch label lookup with language fallback.
/// Obtained via <see cref="WikidataReconciler.Labels"/>.
/// </summary>
public sealed class LabelsService
{
    private readonly ReconcilerContext _ctx;

    internal LabelsService(ReconcilerContext ctx) => _ctx = ctx;

    /// <summary>
    /// Gets the display label for a single Wikidata entity.
    /// </summary>
    /// <param name="qid">The Wikidata QID (e.g., "Q42").</param>
    /// <param name="language">Preferred language. Defaults to the configured language.</param>
    /// <param name="withFallbackLanguage">
    /// When true (default), applies the language fallback chain (e.g., "de-ch" → "de" → "mul" → "en")
    /// if no label exists in the requested language. When false, returns null if the exact language has no label.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The label, or null if the entity does not exist or has no label in the requested language (and fallback didn't resolve one).</returns>
    public async Task<string?> GetAsync(
        string qid,
        string? language = null,
        bool withFallbackLanguage = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qid);

        var lang = language ?? _ctx.Options.Language;
        var result = await GetBatchAsync([qid], lang, withFallbackLanguage, cancellationToken)
            .ConfigureAwait(false);

        return result.TryGetValue(qid, out var label) ? label : null;
    }

    /// <summary>
    /// Gets display labels for multiple Wikidata entities.
    /// </summary>
    /// <param name="qids">The Wikidata QIDs to look up.</param>
    /// <param name="language">Preferred language. Defaults to the configured language.</param>
    /// <param name="withFallbackLanguage">
    /// When true (default), applies the language fallback chain per entity.
    /// When false, returns null for entries that have no label in the exact requested language.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A dictionary containing every input QID. The value is the resolved label, or null if
    /// the entity exists but has no label in the requested language (respecting the fallback flag).
    /// Entities that don't exist at all are absent from the dictionary.
    /// </returns>
    public async Task<IReadOnlyDictionary<string, string?>> GetBatchAsync(
        IReadOnlyList<string> qids,
        string? language = null,
        bool withFallbackLanguage = true,
        CancellationToken cancellationToken = default)
    {
        if (qids.Count == 0)
            return new Dictionary<string, string?>();

        var lang = language ?? _ctx.Options.Language;

        // Pre-filter: Wikidata's wbgetentities API rejects the entire batch when any single
        // title is malformed. Keep only syntactically-valid QIDs so one bad input in a large
        // batch can't drop every label. Invalid QIDs are simply absent from the result —
        // same semantics as entities that do not exist.
        var validQids = qids.Where(IsValidQid).ToList();
        if (validQids.Count == 0)
            return new Dictionary<string, string?>();

        var entities = await _ctx.EntityFetcher.FetchLabelsOnlyAsync(validQids, lang, cancellationToken)
            .ConfigureAwait(false);

        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, entity) in entities)
        {
            string? label = null;

            if (withFallbackLanguage)
            {
                LanguageFallback.TryGetValue(entity.Labels, lang, out label);
            }
            else if (entity.Labels?.TryGetValue(lang, out var langValue) == true)
            {
                label = langValue.Value;
            }

            result[id] = string.IsNullOrEmpty(label) ? null : label;
        }

        return result;
    }

    /// <summary>
    /// Returns true when <paramref name="qid"/> is a syntactically valid Wikidata item ID
    /// (e.g., "Q42"). Used to pre-filter batch input so a single malformed entry does not
    /// cause the wbgetentities API to reject the whole batch.
    /// </summary>
    private static bool IsValidQid(string? qid)
    {
        if (string.IsNullOrEmpty(qid) || qid.Length < 2)
            return false;
        if (qid[0] != 'Q' && qid[0] != 'q')
            return false;
        for (var i = 1; i < qid.Length; i++)
        {
            if (!char.IsDigit(qid[i]))
                return false;
        }
        return true;
    }
}
