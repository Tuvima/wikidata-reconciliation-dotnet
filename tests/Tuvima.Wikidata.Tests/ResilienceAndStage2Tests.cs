namespace Tuvima.Wikidata.Tests;

public class ResilienceTests
{
    [Fact]
    public async Task AuthorsService_CancellationDuringReverseLookup_IsNotSwallowed()
    {
        using var cts = new CancellationTokenSource();
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());

            if (uri.Contains("action=wbsearchentities", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.SearchResponse()));

            if (uri.Contains("action=query&list=search", StringComparison.OrdinalIgnoreCase) &&
                !uri.Contains("haswbstatement:P742", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.QueryResponse()));
            }

            if (uri.Contains("haswbstatement:P742", StringComparison.OrdinalIgnoreCase))
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            }

            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        using var reconciler = TestPayloads.CreateReconciler(handler);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            reconciler.Authors.ResolveAsync(new AuthorResolutionRequest
            {
                RawAuthorString = "Pen Name"
            }, cts.Token));
    }

    [Fact]
    public async Task WikipediaService_CancellationDuringSummaryFetch_IsNotSwallowed()
    {
        using var cts = new CancellationTokenSource();
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(
                    TestPayloads.Entity(
                        "Q1",
                        "Article Entity",
                        sitelinks: TestPayloads.Sitelinks(("enwiki", "Article"))))));
            }

            if (uri.Contains("action=query", StringComparison.OrdinalIgnoreCase) &&
                uri.Contains("prop=extracts|pageimages|info|description", StringComparison.OrdinalIgnoreCase) &&
                uri.Contains("titles=Article", StringComparison.OrdinalIgnoreCase))
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            }

            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        using var reconciler = TestPayloads.CreateReconciler(handler);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            reconciler.Wikipedia.GetWikipediaSummariesAsync(["Q1"], cancellationToken: cts.Token));
    }

    [Fact]
    public async Task EntityService_GetRecentChangesAsync_FollowsContinuation()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());

            if (uri.Contains("list=recentchanges", StringComparison.OrdinalIgnoreCase) &&
                !uri.Contains("rccontinue=", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.RecentChangesResponse(
                    new[]
                    {
                        new
                        {
                            type = "edit",
                            title = "Q42",
                            revid = 1L,
                            timestamp = "2026-04-22T00:00:00Z",
                            user = "alice",
                            comment = "first"
                        }
                    },
                    continueToken: "20260422000100|1")));
            }

            if (uri.Contains("list=recentchanges", StringComparison.OrdinalIgnoreCase) &&
                uri.Contains("rccontinue=", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.RecentChangesResponse(
                    new[]
                    {
                        new
                        {
                            type = "edit",
                            title = "Q42",
                            revid = 2L,
                            timestamp = "2026-04-22T01:00:00Z",
                            user = "bob",
                            comment = "second"
                        }
                    })));
            }

            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        using var reconciler = TestPayloads.CreateReconciler(handler);

        var changes = await reconciler.Entities.GetRecentChangesAsync(
            ["Q42"],
            since: new DateTimeOffset(2026, 4, 22, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(2, changes.Count);
        Assert.Equal([2L, 1L], changes.Select(c => c.RevisionId).ToArray());

        var requests = handler.RequestedUris.ToArray();
        Assert.Equal(2, requests.Count(uri => uri.Contains("list=recentchanges", StringComparison.OrdinalIgnoreCase)));
    }

}
