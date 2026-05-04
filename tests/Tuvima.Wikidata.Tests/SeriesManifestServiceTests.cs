using System.Text.RegularExpressions;

namespace Tuvima.Wikidata.Tests;

public class SeriesManifestServiceTests
{
    [Fact]
    public async Task GetManifestAsync_IncomingP179_OrdersBySeriesOrdinalQualifier()
    {
        using var reconciler = CreateReconciler(
            new()
            {
                ["QSeries"] = Entity("QSeries", "Series"),
                ["Q1"] = Entity("Q1", "Book One", Claims(ItemClaim("P179", "QSeries", "1"))),
                ["Q2"] = Entity("Q2", "Book Two", Claims(ItemClaim("P179", "QSeries", "2")))
            },
            p179: ["Q2", "Q1"]);

        var manifest = await reconciler.Series.GetManifestAsync("QSeries");

        Assert.Equal(["Q1", "Q2"], manifest.Items.Select(i => i.Qid));
        Assert.Equal(1m, manifest.Items[0].ParsedSeriesOrdinal);
        Assert.Equal("1", manifest.Items[0].RawSeriesOrdinal);
        Assert.Equal(SeriesManifestOrderSource.SeriesOrdinal, manifest.Items[0].OrderSource);
        Assert.Contains("P179", manifest.Items[0].SourceProperties);
    }

    [Fact]
    public async Task GetManifestAsync_IncomingP361_PreservesPartOfSource()
    {
        using var reconciler = CreateReconciler(
            new()
            {
                ["QSeries"] = Entity("QSeries", "Series"),
                ["Q1"] = Entity("Q1", "Part", Claims(ItemClaim("P361", "QSeries", "1")))
            },
            p361: ["Q1"]);

        var manifest = await reconciler.Series.GetManifestAsync("QSeries");

        var item = Assert.Single(manifest.Items);
        Assert.Equal("Q1", item.Qid);
        Assert.Equal(["P361"], item.SourceProperties);
        Assert.Contains(item.Relationships, r => r.PropertyId == "P361" && r.TargetQid == "QSeries" && r.Direction == "Outgoing");
    }

    [Fact]
    public async Task GetManifestAsync_OutgoingP527_UsesParentStatementOrdinal()
    {
        using var reconciler = CreateReconciler(
            new()
            {
                ["QSeries"] = Entity("QSeries", "Series", Claims(
                    ItemClaim("P527", "Q2", "2"),
                    ItemClaim("P527", "Q1", "1"))),
                ["Q1"] = Entity("Q1", "Book One"),
                ["Q2"] = Entity("Q2", "Book Two")
            });

        var manifest = await reconciler.Series.GetManifestAsync("QSeries");

        Assert.Equal(["Q1", "Q2"], manifest.Items.Select(i => i.Qid));
        Assert.Equal(["P527"], manifest.Items[0].SourceProperties);
        Assert.Contains(manifest.Items[0].Relationships, r => r.PropertyId == "P527" && r.TargetQid == "QSeries" && r.Direction == "Incoming");
    }

    [Fact]
    public async Task GetManifestAsync_ExpandsCollectionChildren()
    {
        using var reconciler = CreateReconciler(
            new()
            {
                ["QSeries"] = Entity("QSeries", "Series", Claims(ItemClaim("P527", "QCollection", "1"))),
                ["QCollection"] = Entity("QCollection", "Omnibus", Claims(ItemClaim("P527", "QChild", "1.5"))),
                ["QChild"] = Entity("QChild", "Short Fiction")
            });

        var manifest = await reconciler.Series.GetManifestAsync("QSeries");

        Assert.Contains(manifest.Items, i => i.Qid == "QCollection" && i.IsCollection);
        var child = Assert.Single(manifest.Items, i => i.Qid == "QChild");
        Assert.True(child.IsExpandedFromCollection);
        Assert.Equal("QCollection", child.ParentCollectionQid);
        Assert.Equal("Omnibus", child.ParentCollectionLabel);
    }

    [Fact]
    public async Task GetManifestAsync_IncludeCollectionsFalse_OmitsCollectionRows()
    {
        using var reconciler = CreateReconciler(
            new()
            {
                ["QSeries"] = Entity("QSeries", "Series", Claims(ItemClaim("P527", "QCollection", "1"))),
                ["QCollection"] = Entity("QCollection", "Omnibus", Claims(ItemClaim("P527", "QChild", "1"))),
                ["QChild"] = Entity("QChild", "Short Fiction")
            });

        var manifest = await reconciler.Series.GetManifestAsync(new SeriesManifestRequest
        {
            SeriesQid = "QSeries",
            IncludeCollections = false
        });

        var child = Assert.Single(manifest.Items);
        Assert.Equal("QChild", child.Qid);
        Assert.True(child.IsExpandedFromCollection);
    }

    [Fact]
    public async Task GetManifestAsync_DecimalAndStringOrdinals_DoNotThrowAndSort()
    {
        using var reconciler = CreateReconciler(
            new()
            {
                ["QSeries"] = Entity("QSeries", "Series", Claims(
                    ItemClaim("P527", "Q3", "special"),
                    ItemClaim("P527", "Q2", "1.5"),
                    ItemClaim("P527", "Q1", "0.1"))),
                ["Q1"] = Entity("Q1", "Point One"),
                ["Q2"] = Entity("Q2", "One Point Five"),
                ["Q3"] = Entity("Q3", "Special")
            });

        var manifest = await reconciler.Series.GetManifestAsync("QSeries");

        Assert.Equal(["Q1", "Q2", "Q3"], manifest.Items.Select(i => i.Qid));
        Assert.Equal(0.1m, manifest.Items[0].ParsedSeriesOrdinal);
        Assert.Equal(1.5m, manifest.Items[1].ParsedSeriesOrdinal);
        Assert.Null(manifest.Items[2].ParsedSeriesOrdinal);
        Assert.Equal("special", manifest.Items[2].RawSeriesOrdinal);
    }

    [Fact]
    public async Task GetManifestAsync_PreviousNextChain_OrdersWhenOrdinalsMissing()
    {
        using var reconciler = CreateReconciler(
            new()
            {
                ["QSeries"] = Entity("QSeries", "Series"),
                ["Q1"] = Entity("Q1", "First", Claims(ItemClaim("P179", "QSeries"), ItemClaim("P156", "Q2"))),
                ["Q2"] = Entity("Q2", "Second", Claims(ItemClaim("P179", "QSeries"), ItemClaim("P155", "Q1")))
            },
            p179: ["Q2", "Q1"]);

        var manifest = await reconciler.Series.GetManifestAsync("QSeries");

        Assert.Equal(["Q1", "Q2"], manifest.Items.Select(i => i.Qid));
        Assert.All(manifest.Items, item => Assert.Equal(SeriesManifestOrderSource.PreviousNextChain, item.OrderSource));
    }

    [Fact]
    public async Task GetManifestAsync_PublicationDateFallback_OrdersByP577()
    {
        using var reconciler = CreateReconciler(
            new()
            {
                ["QSeries"] = Entity("QSeries", "Series"),
                ["Q1"] = Entity("Q1", "Later", Claims(ItemClaim("P179", "QSeries"), DateClaim("P577", "+2020-01-01T00:00:00Z"))),
                ["Q2"] = Entity("Q2", "Earlier", Claims(ItemClaim("P179", "QSeries"), DateClaim("P577", "+2019-01-01T00:00:00Z")))
            },
            p179: ["Q1", "Q2"]);

        var manifest = await reconciler.Series.GetManifestAsync("QSeries");

        Assert.Equal(["Q2", "Q1"], manifest.Items.Select(i => i.Qid));
        Assert.All(manifest.Items, item => Assert.Equal(SeriesManifestOrderSource.PublicationDate, item.OrderSource));
    }

    [Fact]
    public async Task GetManifestAsync_LabelFallback_AddsWarning()
    {
        using var reconciler = CreateReconciler(
            new()
            {
                ["QSeries"] = Entity("QSeries", "Series"),
                ["Q1"] = Entity("Q1", "Beta", Claims(ItemClaim("P179", "QSeries"))),
                ["Q2"] = Entity("Q2", "Alpha", Claims(ItemClaim("P179", "QSeries")))
            },
            p179: ["Q1", "Q2"]);

        var manifest = await reconciler.Series.GetManifestAsync("QSeries");

        Assert.Equal(["Q2", "Q1"], manifest.Items.Select(i => i.Qid));
        Assert.Contains(manifest.Warnings, w => w.Code == "LabelFallbackOnly");
    }

    [Fact]
    public async Task GetManifestAsync_DuplicateAcrossSources_MergesProvenanceAndWarns()
    {
        using var reconciler = CreateReconciler(
            new()
            {
                ["QSeries"] = Entity("QSeries", "Series", Claims(ItemClaim("P527", "Q1", "1"))),
                ["Q1"] = Entity("Q1", "Book One", Claims(ItemClaim("P179", "QSeries", "1")))
            },
            p179: ["Q1"]);

        var manifest = await reconciler.Series.GetManifestAsync("QSeries");

        var item = Assert.Single(manifest.Items);
        Assert.Equal(["P179", "P527"], item.SourceProperties);
        Assert.Contains(manifest.Warnings, w => w.Code == "DuplicateItem" && w.Qid == "Q1");
    }

    [Fact]
    public async Task GetManifestAsync_ConflictingOrdinals_AddsWarning()
    {
        using var reconciler = CreateReconciler(
            new()
            {
                ["QSeries"] = Entity("QSeries", "Series", Claims(ItemClaim("P527", "Q1", "2"))),
                ["Q1"] = Entity("Q1", "Book One", Claims(ItemClaim("P179", "QSeries", "1")))
            },
            p179: ["Q1"]);

        var manifest = await reconciler.Series.GetManifestAsync("QSeries");

        Assert.Contains(manifest.Warnings, w => w.Code == "ConflictingOrdinals" && w.Qid == "Q1");
    }

    [Fact]
    public async Task GetManifestAsync_BrokenPreviousNextChain_AddsWarning()
    {
        using var reconciler = CreateReconciler(
            new()
            {
                ["QSeries"] = Entity("QSeries", "Series"),
                ["Q1"] = Entity("Q1", "Book One", Claims(ItemClaim("P179", "QSeries"), ItemClaim("P156", "QMissing")))
            },
            p179: ["Q1"]);

        var manifest = await reconciler.Series.GetManifestAsync("QSeries");

        Assert.Contains(manifest.Warnings, w => w.Code == "BrokenPreviousNextChain" && w.Qid == "Q1");
    }

    [Fact]
    public async Task GetManifestAsync_MaxDepthAndMaxItems_AddWarnings()
    {
        using var reconciler = CreateReconciler(
            new()
            {
                ["QSeries"] = Entity("QSeries", "Series", Claims(
                    ItemClaim("P527", "QCollection", "1"),
                    ItemClaim("P527", "Q2", "2"),
                    ItemClaim("P527", "Q3", "3"))),
                ["QCollection"] = Entity("QCollection", "Collection", Claims(ItemClaim("P527", "QChild", "1"))),
                ["Q2"] = Entity("Q2", "Second"),
                ["Q3"] = Entity("Q3", "Third"),
                ["QChild"] = Entity("QChild", "Child")
            });

        var manifest = await reconciler.Series.GetManifestAsync(new SeriesManifestRequest
        {
            SeriesQid = "QSeries",
            MaxDepth = 1,
            MaxItems = 2
        });

        Assert.Equal(2, manifest.Items.Count);
        Assert.Contains(manifest.Warnings, w => w.Code == "MaxDepthReached" && w.Qid == "QCollection");
        Assert.Contains(manifest.Warnings, w => w.Code == "MaxItemsReached");
        Assert.Equal(SeriesManifestCompleteness.Truncated, manifest.Completeness);
    }

    private static WikidataReconciler CreateReconciler(
        Dictionary<string, Dictionary<string, object?>> entities,
        IReadOnlyList<string>? p179 = null,
        IReadOnlyList<string>? p361 = null)
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());

            if (uri.Contains("action=query", StringComparison.OrdinalIgnoreCase))
            {
                if (uri.Contains("haswbstatement:P179=QSeries", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.QueryResponse((p179 ?? []).ToArray())));
                if (uri.Contains("haswbstatement:P361=QSeries", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.QueryResponse((p361 ?? []).ToArray())));
            }

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase))
            {
                var ids = ParseIds(uri);
                var payloadEntities = ids
                    .Where(entities.ContainsKey)
                    .Select(id => entities[id])
                    .ToArray();
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(payloadEntities)));
            }

            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        return TestPayloads.CreateReconciler(handler);
    }

    private static string[] ParseIds(string uri)
    {
        var match = Regex.Match(uri, @"[?&]ids=([^&]+)");
        return match.Success
            ? match.Groups[1].Value.Split('|', StringSplitOptions.RemoveEmptyEntries)
            : [];
    }

    private static Dictionary<string, object?> Entity(
        string id,
        string label,
        Dictionary<string, object>? claims = null)
        => TestPayloads.Entity(id, label, claims);

    private static Dictionary<string, object> Claims(params TestPayloads.ClaimSpec[] claims)
        => TestPayloads.ClaimsWithQualifiers(claims);

    private static TestPayloads.ClaimSpec ItemClaim(string propertyId, string targetQid, string? ordinal = null)
        => TestPayloads.Claim(
            propertyId,
            "wikibase-item",
            TestPayloads.ItemDataValue(targetQid),
            qualifiers: ordinal is null
                ? null
                : TestPayloads.Qualifiers(("P1545", "string", TestPayloads.StringDataValue(ordinal))));

    private static TestPayloads.ClaimSpec DateClaim(string propertyId, string time)
        => TestPayloads.Claim(propertyId, "time", TestPayloads.TimeDataValue(time));
}
