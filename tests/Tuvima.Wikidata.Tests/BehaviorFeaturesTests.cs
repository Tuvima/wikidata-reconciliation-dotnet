using System.Net;
using Tuvima.Wikidata.Internal;

namespace Tuvima.Wikidata.Tests;

public class BehaviorFeaturesTests
{
    [Fact]
    public async Task SearchMultiLanguageAsync_RunsFullTextOnlyOnce()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());

            if (uri.Contains("action=wbsearchentities", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(
                    TestPayloads.SearchResponse(("Q42", "Douglas Adams"))));
            }

            if (uri.Contains("action=query&list=search", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(
                    TestPayloads.QueryResponse("Q42")));
            }

            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        using var client = TestPayloads.CreateHttpClient(handler);
        using var limiter = new SemaphoreSlim(8);
        var resilient = new ResilientHttpClient(
            client,
            maxRetries: 3,
            maxLag: 5,
            concurrencyLimiter: limiter);
        var searchClient = new WikidataSearchClient(resilient, new WikidataReconcilerOptions());

        var results = await searchClient.SearchMultiLanguageAsync(
            "Douglas Adams",
            ["en", "de"],
            limit: 5);

        Assert.Equal(["Q42"], results);

        var requests = handler.RequestedUris.ToArray();
        Assert.Equal(1, requests.Count(uri => uri.Contains("action=query&list=search", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(2, requests.Count(uri => uri.Contains("action=wbsearchentities", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task AuthorsService_WorkQidHint_ReordersCandidates()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());

            if (uri.Contains("action=wbsearchentities", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(
                    TestPayloads.SearchResponse(("Q1", "Jane Doe"), ("Q2", "Jane Doe")),
                    HttpStatusCode.OK));
            }

            if (uri.Contains("action=query&list=search", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(
                    TestPayloads.QueryResponse("Q1", "Q2")));
            }

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(EntityResponse(
                    TestPayloads.Entity("Q1", "Jane Doe", claims: TestPayloads.Claims(
                        ("P800", "wikibase-item", TestPayloads.ItemDataValue("Q901"), "normal"))),
                    TestPayloads.Entity("Q2", "Jane Doe", claims: TestPayloads.Claims(
                        ("P800", "wikibase-item", TestPayloads.ItemDataValue("Q900"), "normal"))))));
            }

            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        using var reconciler = TestPayloads.CreateReconciler(handler);

        var withoutHint = await reconciler.Authors.ResolveAsync(new AuthorResolutionRequest
        {
            RawAuthorString = "Jane Doe",
            DetectPseudonyms = false
        });

        var withHint = await reconciler.Authors.ResolveAsync(new AuthorResolutionRequest
        {
            RawAuthorString = "Jane Doe",
            WorkQidHint = "Q900",
            DetectPseudonyms = false
        });

        Assert.Equal("Q1", withoutHint.Authors[0].Qid);
        Assert.Equal("Q2", withHint.Authors[0].Qid);
    }

    [Fact]
    public async Task PersonsService_TitleHint_ReordersCandidates()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());

            if (uri.Contains("action=wbsearchentities", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(
                    TestPayloads.SearchResponse(("Q1", "Alex Author"), ("Q2", "Alex Author"))));
            }

            if (uri.Contains("action=query&list=search", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(
                    TestPayloads.QueryResponse("Q1", "Q2")));
            }

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase) &&
                uri.Contains("ids=Q1|Q2", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(EntityResponse(
                    TestPayloads.Entity("Q1", "Alex Author", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q5"), "normal"),
                        ("P106", "wikibase-item", TestPayloads.ItemDataValue("Q36180"), "normal"),
                        ("P800", "wikibase-item", TestPayloads.ItemDataValue("Q500"), "normal"))),
                    TestPayloads.Entity("Q2", "Alex Author", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q5"), "normal"),
                        ("P106", "wikibase-item", TestPayloads.ItemDataValue("Q36180"), "normal"),
                        ("P800", "wikibase-item", TestPayloads.ItemDataValue("Q600"), "normal"))))));
            }

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase) &&
                uri.Contains("ids=Q500|Q600", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(EntityResponse(
                    TestPayloads.Entity("Q500", "Book A"),
                    TestPayloads.Entity("Q600", "Target Book"))));
            }

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase) &&
                uri.Contains("ids=Q1", StringComparison.OrdinalIgnoreCase) &&
                !uri.Contains("ids=Q1|Q2", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(EntityResponse(
                    TestPayloads.Entity("Q1", "Alex Author", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q5"), "normal"),
                        ("P106", "wikibase-item", TestPayloads.ItemDataValue("Q36180"), "normal"),
                        ("P800", "wikibase-item", TestPayloads.ItemDataValue("Q500"), "normal"))))));
            }

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase) &&
                uri.Contains("ids=Q2", StringComparison.OrdinalIgnoreCase) &&
                !uri.Contains("ids=Q1|Q2", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(EntityResponse(
                    TestPayloads.Entity("Q2", "Alex Author", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q5"), "normal"),
                        ("P106", "wikibase-item", TestPayloads.ItemDataValue("Q36180"), "normal"),
                        ("P800", "wikibase-item", TestPayloads.ItemDataValue("Q600"), "normal"))))));
            }

            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        using var reconciler = TestPayloads.CreateReconciler(handler);

        var withoutHint = await reconciler.Persons.SearchAsync(new PersonSearchRequest
        {
            Name = "Alex Author",
            Role = PersonRole.Author
        });

        var withHint = await reconciler.Persons.SearchAsync(new PersonSearchRequest
        {
            Name = "Alex Author",
            Role = PersonRole.Author,
            TitleHint = "Target Book"
        });

        Assert.Equal("Q1", withoutHint.Qid);
        Assert.Equal("Q2", withHint.Qid);
    }

    [Fact]
    public async Task ChildrenService_CustomOrdinalProperty_DrivesOrdering()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase) &&
                uri.Contains("ids=QParent", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(EntityResponse(
                    TestPayloads.Entity("QParent", "Parent", claims: TestPayloads.Claims(
                        ("P527", "wikibase-item", TestPayloads.ItemDataValue("Q2"), "normal"),
                        ("P527", "wikibase-item", TestPayloads.ItemDataValue("Q3"), "normal"))))));
            }

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase) &&
                uri.Contains("ids=Q2|Q3", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(EntityResponse(
                    TestPayloads.Entity("Q2", "Second", claims: TestPayloads.Claims(
                        ("P999", "string", TestPayloads.StringDataValue("2"), "normal"))),
                    TestPayloads.Entity("Q3", "First", claims: TestPayloads.Claims(
                        ("P999", "string", TestPayloads.StringDataValue("1"), "normal"))))));
            }

            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        using var reconciler = TestPayloads.CreateReconciler(handler);

        var manifest = await reconciler.Children.GetChildEntitiesAsync(new ChildEntityRequest
        {
            ParentQid = "QParent",
            Kind = ChildEntityKind.Custom,
            CustomTraversal = new CustomChildTraversal
            {
                RelationshipProperty = "P527",
                OrdinalProperty = "P999"
            }
        });

        Assert.Equal(["Q3", "Q2"], manifest.Children.Select(c => c.Qid).ToArray());
        Assert.Equal([1, 2], manifest.Children.Select(c => c.Ordinal).ToArray());
    }

    [Fact]
    public async Task ReconcileAsync_ChainedPropertyPath_ScoresThroughIntermediateEntity()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());

            if (uri.Contains("action=wbsearchentities", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(
                    TestPayloads.SearchResponse(("Q1", "Springfield"), ("Q2", "Springfield"))));
            }

            if (uri.Contains("action=query&list=search", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(
                    TestPayloads.QueryResponse("Q1", "Q2")));
            }

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase) &&
                uri.Contains("ids=Q1|Q2", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(EntityResponse(
                    TestPayloads.Entity("Q1", "Springfield", claims: TestPayloads.Claims(
                        ("P131", "wikibase-item", TestPayloads.ItemDataValue("Q100"), "normal"))),
                    TestPayloads.Entity("Q2", "Springfield", claims: TestPayloads.Claims(
                        ("P131", "wikibase-item", TestPayloads.ItemDataValue("Q200"), "normal"))))));
            }

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase) &&
                uri.Contains("ids=Q100", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(EntityResponse(
                    TestPayloads.Entity("Q100", "Region A", claims: TestPayloads.Claims(
                        ("P17", "wikibase-item", TestPayloads.ItemDataValue("Q30"), "normal"))))));
            }

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase) &&
                uri.Contains("ids=Q200", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(EntityResponse(
                    TestPayloads.Entity("Q200", "Region B", claims: TestPayloads.Claims(
                        ("P17", "wikibase-item", TestPayloads.ItemDataValue("Q145"), "normal"))))));
            }

            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        using var reconciler = TestPayloads.CreateReconciler(handler);

        var results = await reconciler.Reconcile.ReconcileAsync(new ReconciliationRequest
        {
            Query = "Springfield",
            Properties = [new PropertyConstraint("P131/P17", "Q145")]
        });

        Assert.Equal("Q2", results[0].Id);
        Assert.Equal(100.0, results[0].Breakdown!.PropertyScores["P131/P17"]);
    }

    private static string EntityResponse(params Dictionary<string, object?>[] entities)
        => TestPayloads.EntityResponse(entities);
}
