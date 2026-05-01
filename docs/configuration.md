# Configuration

## WikidataReconcilerOptions

```csharp
var reconciler = new WikidataReconciler(new WikidataReconcilerOptions
{
    // API endpoint (default: Wikidata)
    ApiEndpoint = "https://www.wikidata.org/w/api.php",

    // Search language (default: "en", overridable per-request)
    Language = "en",

    // User-Agent header (required by Wikimedia policy)
    UserAgent = "MyApp/1.0 (contact@example.com)",

    // HTTP timeout (default: 30 seconds)
    Timeout = TimeSpan.FromSeconds(30),

    // Type property (default: "P31" — custom Wikibase may use different IDs)
    TypePropertyId = "P31",

    // Scoring tuning
    PropertyWeight = 0.4,        // weight for each property match (label = 1.0)
    AutoMatchThreshold = 95,     // minimum score for auto-match
    AutoMatchScoreGap = 10,      // minimum gap over second-best candidate

    // Resilience
    MaxRetries = 3,                         // retry attempts for transient 408/429/5xx failures
    RetryBaseDelay = TimeSpan.FromSeconds(1),
    MaxRetryDelay = TimeSpan.FromSeconds(30),
    RetryJitterRatio = 0.2,
    MaxLag = 5,                             // sent to Wikidata API requests

    // Provider-safe host limits
    WikidataRateLimit = new ProviderRateLimitOptions
    {
        MaxConcurrentRequests = 1,
        RequestsPerSecond = 1,
        MaxBatchSize = 50
    },
    WikipediaRateLimit = new ProviderRateLimitOptions
    {
        MaxConcurrentRequests = 2,
        RequestsPerSecond = 2,
        MaxBatchSize = 50
    },

    // Shared pipeline features
    EnableRequestCoalescing = true,
    EnableResponseCaching = true,
    ResponseCache = new InMemoryWikidataResponseCache(),
    ResponseCacheTtl = TimeSpan.FromHours(12),

    // Type hierarchy (P279 subclass walking)
    TypeHierarchyDepth = 0,      // 0 = direct P31 match only (fast)
                                  // 5 = walk up to 5 levels of P279

    // Display-friendly labels
    IncludeSitelinkLabels = false,  // include Wikipedia sitelink titles in scoring
});
```

## Bring Your Own HttpClient

For connection pooling, custom handlers, or dependency injection:

```csharp
var httpClient = httpClientFactory.CreateClient("Wikidata");
using var reconciler = new WikidataReconciler(httpClient, options);
```

When you pass your own `HttpClient`, the reconciler will not dispose it. When the reconciler creates its own, it owns and disposes the client.

Every service owned by a `WikidataReconciler` shares one internal HTTP pipeline. The pipeline applies host throttling, retries, `Retry-After`, maxlag, cache lookup/store, request coalescing, logging, and diagnostics consistently across reconciliation, entity fetching, Wikipedia, Stage 2, and ASP.NET batch paths.

`WikidataRateLimit`, `WikipediaRateLimit`, `CommonsRateLimit`, and `DefaultRateLimit` configure independent per-host limiters. Wikidata defaults to a conservative single-flight / low-RPS policy. Each `*.wikipedia.org` language host gets its own limiter using `WikipediaRateLimit`.

`MaxRetries` caps retry attempts. If a provider sends `Retry-After`, that duration is used. Otherwise the pipeline uses exponential backoff from `RetryBaseDelay`, capped by `MaxRetryDelay`, with `RetryJitterRatio` extra jitter.

## Caching

The shared pipeline includes a response-cache abstraction:

```csharp
public sealed class SqliteWikidataResponseCache : IWikidataResponseCache
{
    public ValueTask<string?> GetAsync(
        WikidataResponseCacheKey key,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException(); // load raw JSON by key

    public ValueTask SetAsync(
        WikidataResponseCacheKey key,
        string response,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException(); // store raw JSON
}

using var reconciler = new WikidataReconciler(new WikidataReconcilerOptions
{
    ResponseCache = new SqliteWikidataResponseCache(),
    ResponseCacheTtl = TimeSpan.FromHours(24)
});
```

The default is `InMemoryWikidataResponseCache`. Cache keys are canonicalized so equivalent request shapes coalesce across parameter ordering where safe. The built-in cache policy covers successful entity/property/label/sitelink responses, Wikipedia summary batches, and Commons-capable responses.

Set `EnableResponseCaching = false` to disable cache lookup/store while keeping throttling and retry behavior.

## Diagnostics and Logging

```csharp
using var reconciler = new WikidataReconciler(new WikidataReconcilerOptions
{
    RequestLogger = entry =>
        Console.WriteLine($"{entry.Host} {entry.Endpoint} {entry.StatusCode} {entry.Latency}")
});

// ...run ingestion...

var snapshot = reconciler.Diagnostics.GetSnapshot();
Console.WriteLine($"Wikidata requests: {snapshot.RequestCountByHost["www.wikidata.org"]}");
Console.WriteLine($"Cache hits: {snapshot.CacheHits}, misses: {snapshot.CacheMisses}");
Console.WriteLine($"429s: {snapshot.RateLimitResponses}, retries: {snapshot.RetryCount}");
```

`RecentFailures` and `FailuresByKind` use `WikidataFailureKind` values such as `NoSitelink`, `RateLimited`, and `MalformedResponse`, so consumers do not need to parse exception strings.

## Custom Wikibase Instances

The library works with any Wikibase instance, not just Wikidata:

```csharp
var reconciler = new WikidataReconciler(new WikidataReconcilerOptions
{
    ApiEndpoint = "https://my-wikibase.example.com/w/api.php",
    TypePropertyId = "P1",  // your instance's "instance of" property
});
```
