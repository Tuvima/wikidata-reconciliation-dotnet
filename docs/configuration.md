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
    MaxConcurrency = 5,          // max concurrent outbound HTTP requests across all services
    MaxRetries = 3,              // retry attempts for transient 408/429/5xx failures

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

`MaxConcurrency` is enforced in the shared request sender, so it limits real outbound traffic from reconciliation, Wikipedia, Stage 2, and ASP.NET batch paths alike. `MaxRetries` applies to transient throttling/server failures, and `Retry-After` is honored when Wikimedia sends it.

## Caching

The library deliberately does not include a built-in cache to avoid stale data issues (a [known problem](https://github.com/wetneb/openrefine-wikibase/issues/146) in the upstream Python implementation). Use .NET's standard `HttpClient` middleware pattern:

```csharp
public class CachingHandler : DelegatingHandler
{
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _ttl;

    public CachingHandler(IMemoryCache cache, TimeSpan ttl)
    {
        _cache = cache;
        _ttl = ttl;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var key = request.RequestUri?.ToString() ?? "";
        if (_cache.TryGetValue(key, out HttpResponseMessage? cached))
            return cached!;

        var response = await base.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
            _cache.Set(key, response, _ttl);
        return response;
    }
}

var cache = new MemoryCache(new MemoryCacheOptions());
var handler = new CachingHandler(cache, TimeSpan.FromMinutes(30))
{
    InnerHandler = new HttpClientHandler()
};
var httpClient = new HttpClient(handler);
using var reconciler = new WikidataReconciler(httpClient, options);
```

## Custom Wikibase Instances

The library works with any Wikibase instance, not just Wikidata:

```csharp
var reconciler = new WikidataReconciler(new WikidataReconcilerOptions
{
    ApiEndpoint = "https://my-wikibase.example.com/w/api.php",
    TypePropertyId = "P1",  // your instance's "instance of" property
});
```
