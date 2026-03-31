namespace Tuvima.WikidataReconciliation;

/// <summary>
/// Configuration options for <see cref="WikidataReconciler"/>.
/// </summary>
public sealed class WikidataReconcilerOptions
{
    /// <summary>
    /// The MediaWiki API endpoint. Default is the Wikidata API.
    /// </summary>
    public string ApiEndpoint { get; init; } = "https://www.wikidata.org/w/api.php";

    /// <summary>
    /// Default language for search and label matching. Default is "en".
    /// Can be overridden per-request via <see cref="ReconciliationRequest.Language"/>.
    /// </summary>
    public string Language { get; init; } = "en";

    /// <summary>
    /// User-Agent header sent with API requests. Should identify your application
    /// per Wikimedia policy.
    /// </summary>
    public string UserAgent { get; init; } = "Tuvima.WikidataReconciliation/0.1 (https://github.com/Tuvima/wikidata-reconciliation-dotnet)";

    /// <summary>
    /// HTTP request timeout. Default is 30 seconds.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The property ID used for type classification (instance of).
    /// Default is "P31" for Wikidata. Custom Wikibase instances may use a different property.
    /// </summary>
    public string TypePropertyId { get; init; } = "P31";

    /// <summary>
    /// The property weight used for scoring property matches. Default is 0.4.
    /// The label match always has weight 1.0.
    /// </summary>
    public double PropertyWeight { get; init; } = 0.4;

    /// <summary>
    /// The validation threshold for auto-matching. Default is 95.
    /// A candidate must score above (threshold - 5 * numProperties) to be an auto-match.
    /// </summary>
    public double AutoMatchThreshold { get; init; } = 95;

    /// <summary>
    /// The minimum score gap between the top candidate and the second candidate
    /// for auto-matching. Default is 10.
    /// </summary>
    public double AutoMatchScoreGap { get; init; } = 10;

    /// <summary>
    /// Maximum number of concurrent API requests during batch reconciliation.
    /// Limits parallelism to avoid hitting Wikimedia rate limits.
    /// Default is 5. Set to 1 for fully sequential processing.
    /// </summary>
    public int MaxConcurrency { get; init; } = 5;

    /// <summary>
    /// Number of retry attempts when the API returns HTTP 429 (Too Many Requests).
    /// Uses exponential backoff (1s, 2s, 4s, ...). Default is 3.
    /// </summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// The maxlag parameter sent with every API request (Wikimedia bot etiquette).
    /// If the server is lagging more than this many seconds, it returns a 429 response
    /// instead of processing the request. Default is 5 seconds.
    /// Set to 0 to disable. See https://www.mediawiki.org/wiki/Manual:Maxlag_parameter
    /// </summary>
    public int MaxLag { get; init; } = 5;

    /// <summary>
    /// Maximum depth for P279 (subclass of) hierarchy walking during type checking.
    /// Default is 0 (direct P31 match only — fast, no extra API calls).
    /// Set to a positive value (e.g., 5) to walk superclasses and improve type recall.
    /// For example, with depth 3, a "novel" (Q8261) entity would match a query
    /// for "literary work" (Q7725634) because novel → literary work via P279.
    /// </summary>
    public int TypeHierarchyDepth { get; init; } = 0;

    /// <summary>
    /// When true, Wikipedia sitelink titles are included in the label pool for scoring.
    /// Sitelink titles are often more human-friendly than formal Wikidata labels
    /// (e.g., "Frankenstein" instead of "Frankenstein; or, The Modern Prometheus").
    /// Default is false (opt-in to avoid increased API response size).
    /// </summary>
    public bool IncludeSitelinkLabels { get; init; } = false;

    /// <summary>
    /// Property IDs considered unique identifiers. When a property constraint matches
    /// one of these with score 100, the overall reconciliation score is set to 100.
    /// Default includes common authority control IDs.
    /// Set to an empty set to disable the shortcut.
    /// </summary>
    public IReadOnlySet<string> UniqueIdProperties { get; init; } = new HashSet<string>
    {
        "P213",  // ISNI
        "P214",  // VIAF ID
        "P227",  // GND ID
        "P244",  // Library of Congress authority ID
        "P268",  // BnF ID
        "P269",  // IdRef ID
        "P349",  // National Diet Library ID
        "P496",  // ORCID iD
        "P906",  // SELIBR ID
        "P1006", // NTA ID (Netherlands)
        "P1015", // NORAF ID
        "P1566", // GeoNames ID
        "P2427", // GRID ID
    };
}
