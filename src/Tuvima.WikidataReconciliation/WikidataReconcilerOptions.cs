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
}
