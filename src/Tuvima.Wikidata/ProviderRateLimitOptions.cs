namespace Tuvima.Wikidata;

/// <summary>
/// Provider-safe throttling and batching settings for one Wikimedia host family.
/// </summary>
public sealed record ProviderRateLimitOptions
{
    /// <summary>
    /// Maximum number of in-flight HTTP requests allowed for the host.
    /// </summary>
    public int MaxConcurrentRequests { get; init; } = 1;

    /// <summary>
    /// Maximum request start rate for the host. Set to 0 to disable request pacing.
    /// </summary>
    public double RequestsPerSecond { get; init; } = 1;

    /// <summary>
    /// Maximum number of QIDs or titles to place in one provider API request.
    /// Values above Wikimedia's public API limit are capped internally.
    /// </summary>
    public int MaxBatchSize { get; init; } = 50;

    /// <summary>
    /// A test-friendly policy with concurrency allowed and no request pacing.
    /// </summary>
    public static ProviderRateLimitOptions Unthrottled => new()
    {
        MaxConcurrentRequests = 1024,
        RequestsPerSecond = 0,
        MaxBatchSize = 50
    };
}
