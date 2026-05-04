using Tuvima.Wikidata.Internal;
using Tuvima.Wikidata.Services;

namespace Tuvima.Wikidata;

/// <summary>
/// Facade for the Tuvima.Wikidata library. Exposes focused sub-services:
/// <see cref="Reconcile"/>, <see cref="Entities"/>, <see cref="Wikipedia"/>,
/// <see cref="Editions"/>, <see cref="Children"/>, <see cref="Authors"/>, <see cref="Labels"/>, <see cref="Series"/>, <see cref="Bridge"/>.
/// <para>
/// Top-level methods on this class remain as thin delegates to their owning sub-service
/// for source-compat with v1 call sites; new code should prefer calling the sub-services directly.
/// </para>
/// </summary>
public sealed class WikidataReconciler : IDisposable
{
    private readonly ReconcilerContext _context;
    private readonly bool _ownsHttpClient;

    /// <summary>Reconciliation and suggest (autocomplete) operations.</summary>
    public ReconciliationService Reconcile { get; }

    /// <summary>Entity and property data fetching, external-ID lookup, staleness and change monitoring.</summary>
    public EntityService Entities { get; }

    /// <summary>Wikipedia URLs, summaries, and section content extraction.</summary>
    public WikipediaService Wikipedia { get; }

    /// <summary>Work-to-edition pivoting (P747 / P629).</summary>
    public EditionService Editions { get; }

    /// <summary>Child-entity traversal and manifest building.</summary>
    public ChildrenService Children { get; }

    /// <summary>Author string parsing, multi-author resolution, pen-name detection.</summary>
    public AuthorsService Authors { get; }

    /// <summary>Single-entity and batch label lookup with language fallback.</summary>
    public LabelsService Labels { get; }

    /// <summary>Role-aware person search (humans + musical groups) with occupation filtering and year/work hints.</summary>
    public PersonsService Persons { get; }

    /// <summary>Generic Wikidata series manifest retrieval and ordering.</summary>
    public SeriesManifestService Series { get; }

    /// <summary>High-level bridge and identity resolution.</summary>
    public BridgeResolutionService Bridge { get; }

    /// <summary>Shared HTTP/cache/throttle telemetry for all sub-services owned by this reconciler.</summary>
    public WikidataDiagnostics Diagnostics => _context.Diagnostics;

    public WikidataReconciler()
        : this(new WikidataReconcilerOptions()) { }

    public WikidataReconciler(WikidataReconcilerOptions options)
        : this(CreateHttpClient(options), options, ownsHttpClient: true) { }

    public WikidataReconciler(HttpClient httpClient)
        : this(httpClient, new WikidataReconcilerOptions(), ownsHttpClient: false) { }

    public WikidataReconciler(HttpClient httpClient, WikidataReconcilerOptions options)
        : this(httpClient, options, ownsHttpClient: false) { }

    private WikidataReconciler(HttpClient httpClient, WikidataReconcilerOptions options, bool ownsHttpClient)
    {
        _context = new ReconcilerContext(httpClient, options);
        _ownsHttpClient = ownsHttpClient;

        Reconcile = new ReconciliationService(_context);
        Entities = new EntityService(_context);
        Wikipedia = new WikipediaService(_context);
        Editions = new EditionService(_context);
        Children = new ChildrenService(_context);
        Authors = new AuthorsService(_context, Reconcile);
        Labels = new LabelsService(_context);
        Persons = new PersonsService(_context, Reconcile, Labels);
        Series = new SeriesManifestService(_context);
        Bridge = new BridgeResolutionService(_context, Reconcile);
    }

    // ─── Back-compat delegates (v1 API surface) ─────────────────────
    // These keep existing v1 call sites compiling unchanged. New code should
    // prefer the sub-service properties above.

    public Task<IReadOnlyList<ReconciliationResult>> ReconcileAsync(
        string query, CancellationToken cancellationToken = default)
        => Reconcile.ReconcileAsync(query, cancellationToken);

    public Task<IReadOnlyList<ReconciliationResult>> ReconcileAsync(
        string query, string type, CancellationToken cancellationToken = default)
        => Reconcile.ReconcileAsync(query, type, cancellationToken);

    public Task<IReadOnlyList<ReconciliationResult>> ReconcileAsync(
        ReconciliationRequest request, CancellationToken cancellationToken = default)
        => Reconcile.ReconcileAsync(request, cancellationToken);

    public Task<IReadOnlyList<IReadOnlyList<ReconciliationResult>>> ReconcileBatchAsync(
        IReadOnlyList<ReconciliationRequest> requests, CancellationToken cancellationToken = default)
        => Reconcile.ReconcileBatchAsync(requests, cancellationToken);

    public IAsyncEnumerable<(int Index, IReadOnlyList<ReconciliationResult> Results)> ReconcileBatchStreamAsync(
        IReadOnlyList<ReconciliationRequest> requests, CancellationToken cancellationToken = default)
        => Reconcile.ReconcileBatchStreamAsync(requests, cancellationToken);

    public Task<IReadOnlyList<SuggestResult>> SuggestAsync(
        string prefix, int limit = 7, string? language = null, CancellationToken cancellationToken = default)
        => Reconcile.SuggestAsync(prefix, limit, language, cancellationToken);

    public Task<IReadOnlyList<SuggestResult>> SuggestPropertiesAsync(
        string prefix, int limit = 7, string? language = null, CancellationToken cancellationToken = default)
        => Reconcile.SuggestPropertiesAsync(prefix, limit, language, cancellationToken);

    public Task<IReadOnlyList<SuggestResult>> SuggestTypesAsync(
        string prefix, int limit = 7, string? language = null, CancellationToken cancellationToken = default)
        => Reconcile.SuggestTypesAsync(prefix, limit, language, cancellationToken);

    public Task<IReadOnlyDictionary<string, WikidataEntityInfo>> GetEntitiesAsync(
        IReadOnlyList<string> qids, string? language = null, CancellationToken cancellationToken = default)
        => Entities.GetEntitiesAsync(qids, language, cancellationToken);

    public Task<IReadOnlyDictionary<string, WikidataEntityInfo>> GetEntitiesAsync(
        IReadOnlyList<string> qids, bool resolveEntityLabels, string? language = null, CancellationToken cancellationToken = default)
        => Entities.GetEntitiesAsync(qids, resolveEntityLabels, language, cancellationToken);

    public Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<WikidataClaim>>>> GetPropertiesAsync(
        IReadOnlyList<string> qids, IReadOnlyList<string> propertyIds, string? language = null, CancellationToken cancellationToken = default)
        => Entities.GetPropertiesAsync(qids, propertyIds, language, cancellationToken);

    public Task<IReadOnlyList<WikidataEntityInfo>> LookupByExternalIdAsync(
        string propertyId, string value, string? language = null, CancellationToken cancellationToken = default)
        => Entities.LookupByExternalIdAsync(propertyId, value, language, cancellationToken);

    public Task<IReadOnlyDictionary<string, string>> GetPropertyLabelsAsync(
        IReadOnlyList<string> propertyIds, string? language = null, CancellationToken cancellationToken = default)
        => Entities.GetPropertyLabelsAsync(propertyIds, language, cancellationToken);

    public Task<IReadOnlyDictionary<string, string>> GetImageUrlsAsync(
        IReadOnlyList<string> qids, string? language = null, CancellationToken cancellationToken = default)
        => Entities.GetImageUrlsAsync(qids, language, cancellationToken);

    public Task<IReadOnlyDictionary<string, EntityRevision>> GetRevisionIdsAsync(
        IReadOnlyList<string> qids, CancellationToken cancellationToken = default)
        => Entities.GetRevisionIdsAsync(qids, cancellationToken);

    public Task<IReadOnlyList<EntityChange>> GetRecentChangesAsync(
        IReadOnlyList<string> qids, DateTimeOffset? since = null, CancellationToken cancellationToken = default)
        => Entities.GetRecentChangesAsync(qids, since, cancellationToken);

    public Task<IReadOnlyDictionary<string, string>> GetWikipediaUrlsAsync(
        IReadOnlyList<string> qids, string language = "en", CancellationToken cancellationToken = default)
        => Wikipedia.GetWikipediaUrlsAsync(qids, language, cancellationToken);

    public Task<IReadOnlyList<WikipediaSummary>> GetWikipediaSummariesAsync(
        IReadOnlyList<string> qids, string language = "en", CancellationToken cancellationToken = default)
        => Wikipedia.GetWikipediaSummariesAsync(qids, language, cancellationToken);

    public Task<IReadOnlyList<WikipediaSummary>> GetWikipediaSummariesAsync(
        IReadOnlyList<string> qids, string language, IReadOnlyList<string>? fallbackLanguages, CancellationToken cancellationToken = default)
        => Wikipedia.GetWikipediaSummariesAsync(qids, language, fallbackLanguages, cancellationToken);

    public Task<IReadOnlyDictionary<string, IReadOnlyList<WikipediaSection>>> GetWikipediaSectionsAsync(
        IReadOnlyList<string> qids, string language = "en", CancellationToken cancellationToken = default)
        => Wikipedia.GetWikipediaSectionsAsync(qids, language, cancellationToken);

    public Task<string?> GetWikipediaSectionContentAsync(
        string qid, int sectionIndex, string language = "en", CancellationToken cancellationToken = default)
        => Wikipedia.GetWikipediaSectionContentAsync(qid, sectionIndex, language, cancellationToken);

    public Task<IReadOnlyList<SectionContent>?> GetWikipediaSectionWithSubsectionsAsync(
        string qid, int sectionIndex, string language = "en", CancellationToken cancellationToken = default)
        => Wikipedia.GetWikipediaSectionWithSubsectionsAsync(qid, sectionIndex, language, cancellationToken);

    public Task<IReadOnlyList<EditionInfo>> GetEditionsAsync(
        string workQid, IReadOnlyList<string>? filterTypes = null, string? language = null, CancellationToken cancellationToken = default)
        => Editions.GetEditionsAsync(workQid, filterTypes, language, cancellationToken);

    public Task<WikidataEntityInfo?> GetWorkForEditionAsync(
        string editionQid, string? language = null, CancellationToken cancellationToken = default)
        => Editions.GetWorkForEditionAsync(editionQid, language, cancellationToken);

    // ─── Disposal ──────────────────────────────────────────────────

    private static HttpClient CreateHttpClient(WikidataReconcilerOptions options)
    {
        var client = new HttpClient { Timeout = options.Timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        return client;
    }

    public void Dispose()
    {
        _context.ResilientClient.Dispose();
        _context.ConcurrencyLimiter.Dispose();
        if (_ownsHttpClient)
            _context.HttpClient.Dispose();
    }
}
