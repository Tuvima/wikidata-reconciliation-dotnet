using Tuvima.Wikidata.Internal;
using Tuvima.Wikidata.Internal.Json;

namespace Tuvima.Wikidata.Services;

/// <summary>
/// Wikipedia article URL resolution, summaries, and section content extraction.
/// Obtained via <see cref="WikidataReconciler.Wikipedia"/>.
/// </summary>
public sealed class WikipediaService
{
    private readonly ReconcilerContext _ctx;

    internal WikipediaService(ReconcilerContext ctx) => _ctx = ctx;

    /// <summary>
    /// Resolves Wikipedia article URLs for the given QIDs.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> GetWikipediaUrlsAsync(
        IReadOnlyList<string> qids, string language = "en", CancellationToken cancellationToken = default)
    {
        var entities = await _ctx.EntityFetcher.FetchEntitiesWithSitelinksAsync(qids, language, cancellationToken)
            .ConfigureAwait(false);

        var siteKey = $"{language}wiki";
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, entity) in entities)
        {
            if (entity.Sitelinks?.TryGetValue(siteKey, out var sitelink) == true &&
                !string.IsNullOrEmpty(sitelink.Title))
            {
                result[id] = $"https://{language}.wikipedia.org/wiki/{Uri.EscapeDataString(sitelink.Title)}";
            }
        }

        return result;
    }

    /// <summary>
    /// Fetches Wikipedia article summaries for the given QIDs.
    /// </summary>
    public async Task<IReadOnlyList<WikipediaSummary>> GetWikipediaSummariesAsync(
        IReadOnlyList<string> qids, string language = "en", CancellationToken cancellationToken = default)
    {
        var entities = await _ctx.EntityFetcher.FetchEntitiesWithSitelinksAsync(qids, language, cancellationToken)
            .ConfigureAwait(false);

        var siteKey = $"{language}wiki";
        var titleToQid = new Dictionary<string, string>();

        foreach (var (id, entity) in entities)
        {
            if (entity.Sitelinks?.TryGetValue(siteKey, out var sitelink) == true &&
                !string.IsNullOrEmpty(sitelink.Title))
            {
                titleToQid[sitelink.Title] = id;
            }
        }

        if (titleToQid.Count == 0)
            return [];

        var tasks = titleToQid.Select(async kvp =>
        {
            try
            {
                var url = $"https://{language}.wikipedia.org/api/rest_v1/page/summary/{Uri.EscapeDataString(kvp.Key)}";
                var json = await _ctx.ResilientClient.GetStringAsync(
                    url, cancellationToken, applyMaxLag: false).ConfigureAwait(false);
                var response = System.Text.Json.JsonSerializer.Deserialize(json,
                    WikidataJsonContext.Default.WikipediaSummaryResponse);

                if (response is not null && !string.IsNullOrEmpty(response.Extract))
                {
                    return new WikipediaSummary
                    {
                        EntityId = kvp.Value,
                        Title = response.Title,
                        Extract = response.Extract,
                        Description = response.Description,
                        ThumbnailUrl = response.Thumbnail?.Source,
                        ArticleUrl = response.ContentUrls?.Desktop?.Page
                            ?? $"https://{language}.wikipedia.org/wiki/{Uri.EscapeDataString(kvp.Key)}"
                    };
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Skip on failure
            }
            return null;
        });

        var fetched = await Task.WhenAll(tasks).ConfigureAwait(false);
        return fetched.Where(s => s is not null).ToList()!;
    }

    /// <summary>
    /// Fetches Wikipedia summaries with language fallback.
    /// </summary>
    public async Task<IReadOnlyList<WikipediaSummary>> GetWikipediaSummariesAsync(
        IReadOnlyList<string> qids, string language,
        IReadOnlyList<string>? fallbackLanguages, CancellationToken cancellationToken = default)
    {
        var langChain = fallbackLanguages is { Count: > 0 }
            ? new List<string> { language }.Concat(fallbackLanguages.Where(l => !string.Equals(l, language, StringComparison.OrdinalIgnoreCase))).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : LanguageFallback.GetFallbackChain(language);

        var entities = await _ctx.EntityFetcher.FetchEntitiesWithSitelinksAsync(qids, language, cancellationToken)
            .ConfigureAwait(false);

        var qidToLangTitle = new Dictionary<string, (string Language, string Title)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, entity) in entities)
        {
            if (entity.Sitelinks is null) continue;

            foreach (var lang in langChain)
            {
                var siteKey = $"{lang}wiki";
                if (entity.Sitelinks.TryGetValue(siteKey, out var sitelink) && !string.IsNullOrEmpty(sitelink.Title))
                {
                    qidToLangTitle[id] = (lang, sitelink.Title);
                    break;
                }
            }
        }

        if (qidToLangTitle.Count == 0)
            return [];

        var tasks = qidToLangTitle.Select(async kvp =>
        {
            try
            {
                var lang = kvp.Value.Language;
                var title = kvp.Value.Title;
                var url = $"https://{lang}.wikipedia.org/api/rest_v1/page/summary/{Uri.EscapeDataString(title)}";
                var json = await _ctx.ResilientClient.GetStringAsync(
                    url, cancellationToken, applyMaxLag: false).ConfigureAwait(false);
                var response = System.Text.Json.JsonSerializer.Deserialize(json,
                    WikidataJsonContext.Default.WikipediaSummaryResponse);

                if (response is not null && !string.IsNullOrEmpty(response.Extract))
                {
                    return new WikipediaSummary
                    {
                        EntityId = kvp.Key,
                        Title = response.Title,
                        Extract = response.Extract,
                        Description = response.Description,
                        ThumbnailUrl = response.Thumbnail?.Source,
                        ArticleUrl = response.ContentUrls?.Desktop?.Page
                            ?? $"https://{lang}.wikipedia.org/wiki/{Uri.EscapeDataString(title)}",
                        Language = lang
                    };
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Skip on failure
            }
            return null;
        });

        var fetched = await Task.WhenAll(tasks).ConfigureAwait(false);
        return fetched.Where(s => s is not null).ToList()!;
    }

    /// <summary>
    /// Gets the table of contents for Wikipedia articles of the specified entities.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<WikipediaSection>>> GetWikipediaSectionsAsync(
        IReadOnlyList<string> qids, string language = "en",
        CancellationToken cancellationToken = default)
    {
        var entities = await _ctx.EntityFetcher.FetchEntitiesWithSitelinksAsync(qids, language, cancellationToken)
            .ConfigureAwait(false);

        var siteKey = $"{language}wiki";
        var titleToQid = new Dictionary<string, string>();

        foreach (var (id, entity) in entities)
        {
            if (entity.Sitelinks?.TryGetValue(siteKey, out var sitelink) == true &&
                !string.IsNullOrEmpty(sitelink.Title))
            {
                titleToQid[sitelink.Title] = id;
            }
        }

        if (titleToQid.Count == 0)
            return new Dictionary<string, IReadOnlyList<WikipediaSection>>();

        var result = new Dictionary<string, IReadOnlyList<WikipediaSection>>(StringComparer.OrdinalIgnoreCase);

        var tasks = titleToQid.Select(async kvp =>
        {
            try
            {
                var url = $"https://{language}.wikipedia.org/w/api.php?action=parse" +
                          $"&page={Uri.EscapeDataString(kvp.Key)}&prop=tocdata&format=json";
                var json = await _ctx.ResilientClient.GetStringAsync(url, cancellationToken)
                    .ConfigureAwait(false);
                var response = System.Text.Json.JsonSerializer.Deserialize(json,
                    WikidataJsonContext.Default.ParseResponse);

                if (response?.Parse?.TocData?.Sections is { Count: > 0 } sections)
                {
                    return (Qid: kvp.Value, Sections: (IReadOnlyList<WikipediaSection>)sections
                        .Select(s => new WikipediaSection
                        {
                            Title = HtmlTextExtractor.StripInlineHtml(s.Line),
                            Index = int.TryParse(s.Index, out var idx) ? idx : 0,
                            Level = s.HLevel,
                            Number = s.Number,
                            Anchor = s.Anchor
                        })
                        .ToList());
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Skip on failure
            }
            return (Qid: kvp.Value, Sections: (IReadOnlyList<WikipediaSection>?)null);
        });

        var fetched = await Task.WhenAll(tasks).ConfigureAwait(false);
        foreach (var item in fetched)
        {
            if (item.Sections is not null)
                result[item.Qid] = item.Sections;
        }

        return result;
    }

    /// <summary>
    /// Fetches the content of a specific Wikipedia article section as plain text.
    /// </summary>
    public async Task<string?> GetWikipediaSectionContentAsync(
        string qid, int sectionIndex, string language = "en",
        CancellationToken cancellationToken = default)
    {
        var pageTitle = await ResolveWikipediaTitle(qid, language, cancellationToken).ConfigureAwait(false);
        if (pageTitle is null)
            return null;

        var text = await FetchSectionText(pageTitle, sectionIndex, language, cancellationToken)
            .ConfigureAwait(false);
        if (text is null)
            return null;

        text = HtmlTextExtractor.StripLeadingHeading(text);
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>
    /// Fetches a Wikipedia section and all its subsections as a structured list.
    /// </summary>
    public async Task<IReadOnlyList<SectionContent>?> GetWikipediaSectionWithSubsectionsAsync(
        string qid, int sectionIndex, string language = "en",
        CancellationToken cancellationToken = default)
    {
        var toc = await GetWikipediaSectionsAsync([qid], language, cancellationToken).ConfigureAwait(false);
        if (!toc.TryGetValue(qid, out var sections))
            return null;

        var targetSection = sections.FirstOrDefault(s => s.Index == sectionIndex);
        if (targetSection is null)
            return null;

        var sectionIndices = new List<(int Index, string Title)> { (targetSection.Index, targetSection.Title) };

        var foundTarget = false;
        foreach (var s in sections)
        {
            if (s.Index == sectionIndex)
            {
                foundTarget = true;
                continue;
            }
            if (!foundTarget)
                continue;
            if (s.Level <= targetSection.Level)
                break;
            sectionIndices.Add((s.Index, s.Title));
        }

        var pageTitle = await ResolveWikipediaTitle(qid, language, cancellationToken).ConfigureAwait(false);
        if (pageTitle is null)
            return null;

        var fetchTasks = sectionIndices.Select(async si =>
        {
            var text = await FetchSectionText(pageTitle, si.Index, language, cancellationToken)
                .ConfigureAwait(false);
            if (text is not null)
                text = HtmlTextExtractor.StripLeadingHeading(text);
            return (si.Title, Content: string.IsNullOrWhiteSpace(text) ? null : text!.Trim());
        });

        var results = await Task.WhenAll(fetchTasks).ConfigureAwait(false);

        var content = results
            .Where(r => r.Content is not null)
            .Select(r => new SectionContent { Title = r.Title, Content = r.Content! })
            .ToList();

        return content.Count > 0 ? content : null;
    }

    private async Task<string?> ResolveWikipediaTitle(string qid, string language, CancellationToken cancellationToken)
    {
        var entities = await _ctx.EntityFetcher.FetchEntitiesWithSitelinksAsync([qid], language, cancellationToken)
            .ConfigureAwait(false);

        var siteKey = $"{language}wiki";
        if (!entities.TryGetValue(qid, out var entity) ||
            entity.Sitelinks?.TryGetValue(siteKey, out var sitelink) != true ||
            string.IsNullOrEmpty(sitelink!.Title))
        {
            return null;
        }

        return sitelink.Title;
    }

    private async Task<string?> FetchSectionText(string pageTitle, int sectionIndex, string language, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://{language}.wikipedia.org/w/api.php?action=parse" +
                      $"&page={Uri.EscapeDataString(pageTitle)}&section={sectionIndex}" +
                      "&prop=text&format=json";
            var json = await _ctx.ResilientClient.GetStringAsync(url, cancellationToken)
                .ConfigureAwait(false);
            var response = System.Text.Json.JsonSerializer.Deserialize(json,
                WikidataJsonContext.Default.ParseResponse);

            if (response?.Error is not null || response?.Parse?.Text?.Html is null)
                return null;

            var text = HtmlTextExtractor.ExtractText(response.Parse.Text.Html);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
