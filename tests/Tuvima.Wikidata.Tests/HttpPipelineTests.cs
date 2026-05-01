using System.Net;
using System.Text.Json;
using Tuvima.Wikidata.Internal;

namespace Tuvima.Wikidata.Tests;

public class HttpPipelineTests
{
    [Fact]
    public async Task GetStringAsync_RespectsRetryAfter()
    {
        var calls = 0;
        var handler = new TestHttpMessageHandler((_, _) =>
        {
            calls++;
            if (calls == 1)
            {
                var response = TestHttpMessageHandler.Json("{}", HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(4));
                return Task.FromResult(response);
            }

            return Task.FromResult(TestHttpMessageHandler.Json("{\"ok\":true}"));
        });

        var delays = new List<TimeSpan>();
        using var client = TestPayloads.CreateHttpClient(handler);
        using var pipeline = CreatePipeline(
            client,
            delayAsync: (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var body = await pipeline.GetStringAsync(WikidataUrl("Q1"), CancellationToken.None);

        Assert.Equal("{\"ok\":true}", body);
        Assert.Equal(2, calls);
        Assert.Equal(TimeSpan.FromSeconds(4), delays.Single());
    }

    [Fact]
    public async Task GetStringAsync_UsesExponentialBackoffWhenRetryAfterIsAbsent()
    {
        var calls = 0;
        var handler = new TestHttpMessageHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(calls <= 2
                ? TestHttpMessageHandler.Json("{}", HttpStatusCode.ServiceUnavailable)
                : TestHttpMessageHandler.Json("{\"ok\":true}"));
        });

        var delays = new List<TimeSpan>();
        using var client = TestPayloads.CreateHttpClient(handler);
        using var pipeline = CreatePipeline(
            client,
            maxRetries: 2,
            delayAsync: (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        await pipeline.GetStringAsync(WikidataUrl("Q1"), CancellationToken.None);

        Assert.Equal(3, calls);
        Assert.Equal([TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200)], delays);
    }

    [Fact]
    public async Task GetStringAsync_CoalescesConcurrentIdenticalRequests()
    {
        var calls = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new TestHttpMessageHandler(async (_, _) =>
        {
            Interlocked.Increment(ref calls);
            await release.Task.ConfigureAwait(false);
            return TestHttpMessageHandler.Json("{\"ok\":true}");
        });

        using var client = TestPayloads.CreateHttpClient(handler);
        var diagnostics = new WikidataDiagnostics();
        using var pipeline = CreatePipeline(client, diagnostics: diagnostics);

        var first = pipeline.GetStringAsync(WikidataUrl("Q1"), CancellationToken.None);
        SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 1, TimeSpan.FromSeconds(1));
        var second = pipeline.GetStringAsync(WikidataUrl("Q1"), CancellationToken.None);

        release.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(["{\"ok\":true}", "{\"ok\":true}"], results);
        Assert.Equal(1, calls);
        Assert.True(diagnostics.GetSnapshot().CoalescedRequests >= 1);
    }

    [Fact]
    public async Task GetStringAsync_UsesResponseCacheForCacheableRequests()
    {
        var calls = 0;
        var handler = new TestHttpMessageHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(TestHttpMessageHandler.Json("{\"entities\":{},\"success\":1}"));
        });

        using var client = TestPayloads.CreateHttpClient(handler);
        var diagnostics = new WikidataDiagnostics();
        using var pipeline = CreatePipeline(
            client,
            diagnostics: diagnostics,
            enableCache: true);

        await pipeline.GetStringAsync(WikidataUrl("Q1"), CancellationToken.None);
        await pipeline.GetStringAsync(WikidataUrl("Q1"), CancellationToken.None);

        var snapshot = diagnostics.GetSnapshot();
        Assert.Equal(1, calls);
        Assert.Equal(1, snapshot.CacheMisses);
        Assert.Equal(1, snapshot.CacheHits);
    }

    [Fact]
    public async Task EntityFetcher_SplitsOversizedBatchesUsingConfiguredLimit()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var ids = GetQueryValue(request.RequestUri!, "ids").Split('|', StringSplitOptions.RemoveEmptyEntries);
            var entities = ids.Select(id => TestPayloads.Entity(id, id)).ToArray();
            return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(entities)));
        });

        using var client = TestPayloads.CreateHttpClient(handler);
        var options = new WikidataReconcilerOptions
        {
            UserAgent = "Tuvima.Wikidata.Tests/2.6 (https://github.com/Tuvima/wikidata)",
            EnableResponseCaching = false,
            RetryBaseDelay = TimeSpan.FromMilliseconds(100),
            MaxRetryDelay = TimeSpan.FromSeconds(1),
            RetryJitterRatio = 0,
            WikidataRateLimit = ProviderRateLimitOptions.Unthrottled with { MaxBatchSize = 2 },
            WikipediaRateLimit = ProviderRateLimitOptions.Unthrottled,
            CommonsRateLimit = ProviderRateLimitOptions.Unthrottled,
            DefaultRateLimit = ProviderRateLimitOptions.Unthrottled
        };
        using var reconciler = new WikidataReconciler(client, options);

        var result = await reconciler.Entities.GetEntitiesAsync(["Q5", "Q4", "Q3", "Q2", "Q1"]);

        Assert.Equal(5, result.Count);
        var requests = handler.RequestedUris
            .Where(uri => uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Equal(3, requests.Length);
        Assert.Contains(requests, uri => Uri.UnescapeDataString(uri).Contains("ids=Q1|Q2", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(requests, uri => Uri.UnescapeDataString(uri).Contains("ids=Q3|Q4", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(requests, uri => Uri.UnescapeDataString(uri).Contains("ids=Q5", StringComparison.OrdinalIgnoreCase));

        var batchMetrics = reconciler.Diagnostics.GetSnapshot().BatchMetricsByEndpoint["wbgetentities"];
        Assert.Equal(3, batchMetrics.BatchCount);
        Assert.Equal(2, batchMetrics.MaxBatchSize);
    }

    [Fact]
    public async Task WikipediaSummaries_AreFetchedInBatchesAndMappedByQid()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());
            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(
                    TestPayloads.Entity("Q1", "One", sitelinks: TestPayloads.Sitelinks(("enwiki", "Article One"))),
                    TestPayloads.Entity("Q2", "Two", sitelinks: TestPayloads.Sitelinks(("enwiki", "Article Two"))),
                    TestPayloads.Entity("Q3", "Three", sitelinks: TestPayloads.Sitelinks(("enwiki", "Article Three"))))));
            }

            if (uri.Contains("action=query", StringComparison.OrdinalIgnoreCase) &&
                uri.Contains("prop=extracts|pageimages|info|description", StringComparison.OrdinalIgnoreCase))
            {
                var titles = GetQueryValue(request.RequestUri!, "titles").Split('|', StringSplitOptions.RemoveEmptyEntries);
                return Task.FromResult(TestHttpMessageHandler.Json(SummaryBatchResponse(titles)));
            }

            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        using var client = TestPayloads.CreateHttpClient(handler);
        using var reconciler = new WikidataReconciler(client, new WikidataReconcilerOptions
        {
            UserAgent = "Tuvima.Wikidata.Tests/2.6 (https://github.com/Tuvima/wikidata)",
            EnableResponseCaching = false,
            WikidataRateLimit = ProviderRateLimitOptions.Unthrottled,
            WikipediaRateLimit = ProviderRateLimitOptions.Unthrottled with { MaxBatchSize = 2 },
            CommonsRateLimit = ProviderRateLimitOptions.Unthrottled,
            DefaultRateLimit = ProviderRateLimitOptions.Unthrottled
        });

        var summaries = await reconciler.Wikipedia.GetWikipediaSummariesAsync(["Q1", "Q2", "Q3"]);

        Assert.Equal(["Q1", "Q3", "Q2"], summaries.Select(summary => summary.EntityId).ToArray());
        Assert.All(summaries, summary => Assert.StartsWith("Summary for", summary.Extract, StringComparison.Ordinal));
        Assert.Equal(2, handler.RequestedUris.Count(uri =>
            Uri.UnescapeDataString(uri).Contains("prop=extracts|pageimages|info|description", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task GetStringAsync_ThrowsTypedNotFoundFailure()
    {
        var handler = new TestHttpMessageHandler((_, _) =>
            Task.FromResult(TestHttpMessageHandler.Json("{}", HttpStatusCode.NotFound)));

        using var client = TestPayloads.CreateHttpClient(handler);
        using var pipeline = CreatePipeline(client, maxRetries: 0);

        var ex = await Assert.ThrowsAsync<WikidataProviderException>(() =>
            pipeline.GetStringAsync(WikidataUrl("Q1"), CancellationToken.None));

        Assert.Equal(WikidataFailureKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task GetStringAsync_ThrowsTypedRateLimitedFailureWhenRetriesAreExhausted()
    {
        var handler = new TestHttpMessageHandler((_, _) =>
            Task.FromResult(TestHttpMessageHandler.Json("{}", HttpStatusCode.TooManyRequests)));

        var delays = new List<TimeSpan>();
        using var client = TestPayloads.CreateHttpClient(handler);
        using var pipeline = CreatePipeline(
            client,
            maxRetries: 1,
            delayAsync: (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var ex = await Assert.ThrowsAsync<WikidataProviderException>(() =>
            pipeline.GetStringAsync(WikidataUrl("Q1"), CancellationToken.None));

        Assert.Equal(WikidataFailureKind.RateLimited, ex.Kind);
        Assert.Single(delays);
    }

    [Fact]
    public async Task PublicApi_ThrowsTypedMalformedResponseFailure()
    {
        var handler = new TestHttpMessageHandler((_, _) =>
            Task.FromResult(TestHttpMessageHandler.Json("{not json")));

        using var reconciler = TestPayloads.CreateReconciler(handler);

        var ex = await Assert.ThrowsAsync<WikidataProviderException>(() =>
            reconciler.Entities.GetEntitiesAsync(["Q1"]));

        Assert.Equal(WikidataFailureKind.MalformedResponse, ex.Kind);
    }

    [Fact]
    public async Task GetStringAsync_CancellationIsNotRetriedOrWrapped()
    {
        using var cts = new CancellationTokenSource();
        var handler = new TestHttpMessageHandler((_, token) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(token);
        });

        var diagnostics = new WikidataDiagnostics();
        using var client = TestPayloads.CreateHttpClient(handler);
        using var pipeline = CreatePipeline(client, diagnostics: diagnostics);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pipeline.GetStringAsync(WikidataUrl("Q1"), cts.Token));

        Assert.True(diagnostics.GetSnapshot().FailuresByKind.ContainsKey(nameof(WikidataFailureKind.Cancelled)));
    }

    private static ResilientHttpClient CreatePipeline(
        HttpClient client,
        int maxRetries = 3,
        WikidataDiagnostics? diagnostics = null,
        bool enableCache = false,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        return new ResilientHttpClient(
            client,
            TestOptions(maxRetries, enableCache),
            diagnostics ?? new WikidataDiagnostics(),
            delayAsync);
    }

    private static WikidataReconcilerOptions TestOptions(int maxRetries = 3, bool enableCache = false)
        => new()
        {
            UserAgent = "Tuvima.Wikidata.Tests/2.6 (https://github.com/Tuvima/wikidata)",
            MaxRetries = maxRetries,
            RetryBaseDelay = TimeSpan.FromMilliseconds(100),
            MaxRetryDelay = TimeSpan.FromSeconds(1),
            RetryJitterRatio = 0,
            EnableResponseCaching = enableCache,
            ResponseCache = enableCache ? new InMemoryWikidataResponseCache() : null,
            WikidataRateLimit = ProviderRateLimitOptions.Unthrottled,
            WikipediaRateLimit = ProviderRateLimitOptions.Unthrottled,
            CommonsRateLimit = ProviderRateLimitOptions.Unthrottled,
            DefaultRateLimit = ProviderRateLimitOptions.Unthrottled
        };

    private static string WikidataUrl(string qid)
        => $"https://www.wikidata.org/w/api.php?action=wbgetentities&ids={qid}&format=json";

    private static string GetQueryValue(Uri uri, string name)
    {
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in query)
        {
            var separator = pair.IndexOf('=');
            var key = separator >= 0 ? pair[..separator] : pair;
            if (!string.Equals(Uri.UnescapeDataString(key), name, StringComparison.OrdinalIgnoreCase))
                continue;

            var value = separator >= 0 ? pair[(separator + 1)..] : "";
            return Uri.UnescapeDataString(value);
        }

        return "";
    }

    private static string SummaryBatchResponse(IEnumerable<string> titles)
    {
        return JsonSerializer.Serialize(new
        {
            query = new
            {
                pages = titles.Select(title => new
                {
                    title,
                    extract = $"Summary for {title}",
                    description = $"Description for {title}",
                    fullurl = $"https://en.wikipedia.org/wiki/{Uri.EscapeDataString(title)}"
                }).ToList()
            }
        });
    }
}
