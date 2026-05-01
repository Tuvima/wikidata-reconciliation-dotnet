namespace Tuvima.Wikidata.Tests;

public class BridgeResolutionServiceTests
{
    [Fact]
    public async Task ResolveBatchAsync_DeduplicatesDuplicateBridgeLookups()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());

            if (uri.Contains("haswbstatement:P345=tt0903747", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.QueryResponse("Q1")));

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase) &&
                uri.Contains("ids=Q1", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(
                    TestPayloads.Entity("Q1", "Breaking Bad", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q5398426"), "normal"),
                        ("P345", "external-id", TestPayloads.StringDataValue("tt0903747"), "normal"))))));
            }

            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        using var reconciler = TestPayloads.CreateReconciler(handler);

        var results = await reconciler.Bridge.ResolveBatchAsync([
            new BridgeResolutionRequest
            {
                CorrelationKey = "a",
                MediaKind = BridgeMediaKind.TvSeries,
                BridgeIds = new Dictionary<string, string> { ["imdb_id"] = "tt0903747" }
            },
            new BridgeResolutionRequest
            {
                CorrelationKey = "b",
                MediaKind = BridgeMediaKind.TvSeries,
                BridgeIds = new Dictionary<string, string> { ["imdb_id"] = "tt0903747" }
            }
        ]);

        Assert.Equal("Q1", results["a"].SelectedCandidate?.Qid);
        Assert.Equal("Q1", results["b"].SelectedCandidate?.Qid);
        Assert.Equal(1, handler.RequestedUris.Count(uri =>
            Uri.UnescapeDataString(uri).Contains("haswbstatement:P345=tt0903747", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task ResolveAsync_RanksByMediaTypeAndTitle()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());

            if (uri.Contains("haswbstatement:P345=tt0903747", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.QueryResponse("Q2", "Q1")));

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase) &&
                (uri.Contains("ids=Q1|Q2", StringComparison.OrdinalIgnoreCase) ||
                 uri.Contains("ids=Q2|Q1", StringComparison.OrdinalIgnoreCase)))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(
                    TestPayloads.Entity("Q1", "Breaking Bad", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q5398426"), "normal"),
                        ("P345", "external-id", TestPayloads.StringDataValue("tt0903747"), "normal"))),
                    TestPayloads.Entity("Q2", "Breaking Bad episode", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q21191270"), "normal"),
                        ("P345", "external-id", TestPayloads.StringDataValue("tt0903747"), "normal"))))));
            }

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase) &&
                (uri.Contains("ids=Q1", StringComparison.OrdinalIgnoreCase) ||
                 uri.Contains("ids=Q2", StringComparison.OrdinalIgnoreCase)))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(
                    TestPayloads.Entity("Q1", "Breaking Bad", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q5398426"), "normal"),
                        ("P345", "external-id", TestPayloads.StringDataValue("tt0903747"), "normal"))),
                    TestPayloads.Entity("Q2", "Breaking Bad episode", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q21191270"), "normal"),
                        ("P345", "external-id", TestPayloads.StringDataValue("tt0903747"), "normal"))))));
            }

            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        using var reconciler = TestPayloads.CreateReconciler(handler);

        var result = await reconciler.Bridge.ResolveAsync(new BridgeResolutionRequest
        {
            CorrelationKey = "show",
            MediaKind = BridgeMediaKind.TvSeries,
            Title = "Breaking Bad",
            BridgeIds = new Dictionary<string, string> { ["imdb_id"] = "tt0903747" }
        });

        Assert.True(result.Found);
        Assert.Equal(BridgeResolutionStrategy.BridgeId, result.MatchedBy);
        Assert.Equal("Q1", result.SelectedCandidate?.Qid);
        Assert.Equal("P345", result.SelectedCandidate?.MatchedPropertyId);
        Assert.Contains("type.match", result.SelectedCandidate?.ReasonCodes ?? []);
    }

    [Fact]
    public async Task ResolveAsync_RollsEditionToCanonicalWork()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());

            if (uri.Contains("haswbstatement:P212=9780441172719", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.QueryResponse("QEdition")));

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase) &&
                uri.Contains("ids=QEdition", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(
                    TestPayloads.Entity("QEdition", "Dune paperback", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q3331189"), "normal"),
                        ("P212", "external-id", TestPayloads.StringDataValue("9780441172719"), "normal"),
                        ("P629", "wikibase-item", TestPayloads.ItemDataValue("QWork"), "normal"))))));
            }

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase) &&
                uri.Contains("ids=QWork", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(
                    TestPayloads.Entity("QWork", "Dune", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q7725634"), "normal"))))));
            }

            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        using var reconciler = TestPayloads.CreateReconciler(handler);

        var result = await reconciler.Bridge.ResolveAsync(new BridgeResolutionRequest
        {
            CorrelationKey = "book",
            MediaKind = BridgeMediaKind.Book,
            BridgeIds = new Dictionary<string, string> { ["isbn13"] = "978-0-441-17271-9" }
        });

        Assert.True(result.Found);
        Assert.Equal("QEdition", result.SelectedCandidate?.Qid);
        Assert.Equal("QWork", result.CanonicalWorkQid);
        Assert.Equal("P629", result.Rollup?.RelationshipPath.Single().PropertyId);
    }

    [Fact]
    public async Task WikipediaSummaryResults_ReturnsCleanNoSitelinkResult()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(
                    TestPayloads.Entity("Q1", "No Page"))));
            }

            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        using var reconciler = TestPayloads.CreateReconciler(handler);

        var results = await reconciler.Wikipedia.GetWikipediaSummaryResultsAsync(["Q1"]);

        Assert.False(results["Q1"].Found);
        Assert.Equal(WikidataFailureKind.NoSitelink, results["Q1"].FailureKind);
    }
}
