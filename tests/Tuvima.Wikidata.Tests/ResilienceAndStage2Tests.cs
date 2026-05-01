namespace Tuvima.Wikidata.Tests;

public class ResilienceAndStage2Tests
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

    [Fact]
    public async Task Stage2_Music_UsesResolvedArtistQidConstraint()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());

            if (uri.Contains("The Example Band", StringComparison.OrdinalIgnoreCase) &&
                uri.Contains("action=wbsearchentities", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(
                    TestPayloads.SearchResponse(("Q10", "The Example Band"))));
            }

            if (uri.Contains("The Example Band", StringComparison.OrdinalIgnoreCase) &&
                uri.Contains("action=query&list=search", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(
                    TestPayloads.QueryResponse("Q10")));
            }

            if (uri.Contains("Sample Album", StringComparison.OrdinalIgnoreCase) &&
                uri.Contains("action=wbsearchentities", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(
                    TestPayloads.SearchResponse(("Q1", "Sample Album"), ("Q2", "Sample Album"))));
            }

            if (uri.Contains("Sample Album", StringComparison.OrdinalIgnoreCase) &&
                uri.Contains("action=query&list=search", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(
                    TestPayloads.QueryResponse("Q1", "Q2")));
            }

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase) &&
                uri.Contains("ids=Q10", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(
                    TestPayloads.Entity("Q10", "The Example Band", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q215380"), "normal"))))));
            }

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase) &&
                uri.Contains("ids=Q1|Q2", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(
                    TestPayloads.Entity("Q1", "Sample Album", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q482994"), "normal"),
                        ("P175", "wikibase-item", TestPayloads.ItemDataValue("Q99"), "normal"))),
                    TestPayloads.Entity("Q2", "Sample Album", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q482994"), "normal"),
                        ("P175", "wikibase-item", TestPayloads.ItemDataValue("Q10"), "normal"))))));
            }

            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        using var reconciler = TestPayloads.CreateReconciler(handler);

        var result = await reconciler.Stage2.ResolveAsync(Stage2Request.Music(
            correlationKey: "album-1",
            albumTitle: "Sample Album",
            artist: "The Example Band"));

        Assert.True(result.Found);
        Assert.Equal("Q2", result.Qid);
    }

    [Fact]
    public async Task Stage2_Text_UsesResolvedAuthorQidConstraint()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());

            if (uri.Contains("Jane Doe", StringComparison.OrdinalIgnoreCase) &&
                uri.Contains("action=wbsearchentities", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(
                    TestPayloads.SearchResponse(("Q10", "Jane Doe"))));
            }

            if (uri.Contains("Jane Doe", StringComparison.OrdinalIgnoreCase) &&
                uri.Contains("action=query&list=search", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(
                    TestPayloads.QueryResponse("Q10")));
            }

            if (uri.Contains("Novel Title", StringComparison.OrdinalIgnoreCase) &&
                uri.Contains("action=wbsearchentities", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(
                    TestPayloads.SearchResponse(("Q1", "Novel Title"), ("Q2", "Novel Title"))));
            }

            if (uri.Contains("Novel Title", StringComparison.OrdinalIgnoreCase) &&
                uri.Contains("action=query&list=search", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(
                    TestPayloads.QueryResponse("Q1", "Q2")));
            }

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase) &&
                uri.Contains("ids=Q10", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(
                    TestPayloads.Entity("Q10", "Jane Doe", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q5"), "normal"))))));
            }

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase) &&
                uri.Contains("ids=Q1|Q2", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(
                    TestPayloads.Entity("Q1", "Novel Title", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q7725634"), "normal"),
                        ("P50", "wikibase-item", TestPayloads.ItemDataValue("Q99"), "normal"))),
                    TestPayloads.Entity("Q2", "Novel Title", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q7725634"), "normal"),
                        ("P50", "wikibase-item", TestPayloads.ItemDataValue("Q10"), "normal"))))));
            }

            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        using var reconciler = TestPayloads.CreateReconciler(handler);

        var result = await reconciler.Stage2.ResolveAsync(Stage2Request.Text(
            correlationKey: "text-1",
            title: "Novel Title",
            cirrusSearchTypes: ["Q7725634"],
            author: "Jane Doe"));

        Assert.True(result.Found);
        Assert.Equal("Q2", result.Qid);
    }
}
