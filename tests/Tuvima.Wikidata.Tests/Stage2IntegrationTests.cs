namespace Tuvima.Wikidata.Tests;

/// <summary>
/// Integration tests for the v2.2.0 Stage2Service against the live Wikidata API.
/// </summary>
[Trait("Category", "Integration")]
public class Stage2IntegrationTests : IDisposable
{
    private readonly WikidataReconciler _reconciler;

    public Stage2IntegrationTests()
    {
        _reconciler = new WikidataReconciler(new WikidataReconcilerOptions
        {
            UserAgent = "Tuvima.Wikidata.Tests/2.2 (https://github.com/Tuvima/wikidata)"
        });
    }

    [Fact]
    public async Task ResolveAsync_Bridge_ImdbIdResolves()
    {
        // IMDB ID of "Breaking Bad". P345 is well-populated in Wikidata.
        // The specific QID is not pinned because haswbstatement can return multiple hits
        // (episodes, seasons, and the show itself can share an IMDB ID in Wikidata's graph).
        // The library returns the first match — verifying that the BridgeId path fires and
        // returns SOMETHING is enough for an integration smoke test.
        var result = await _reconciler.Stage2.ResolveAsync(Stage2Request.Bridge(
            correlationKey: "row-1",
            bridgeIds: new Dictionary<string, string> { ["imdb"] = "tt0903747" },
            wikidataProperties: new Dictionary<string, string> { ["imdb"] = "P345" }));

        Assert.True(result.Found, "Expected IMDB lookup to resolve");
        Assert.Equal(Stage2MatchedStrategy.BridgeId, result.MatchedBy);
        Assert.Equal("imdb", result.PrimaryBridgeIdType);
        Assert.NotNull(result.Qid);
        Assert.StartsWith("Q", result.Qid);
    }

    [Fact]
    public async Task ResolveAsync_Text_ResolvesBookWithTypeFilter()
    {
        var result = await _reconciler.Stage2.ResolveAsync(Stage2Request.Text(
            correlationKey: "book-1",
            title: "The Hitchhiker's Guide to the Galaxy",
            cirrusSearchTypes: ["Q7725634"], // literary work
            author: "Douglas Adams"));

        Assert.True(result.Found, $"Expected text reconciliation to resolve, got score check");
        Assert.Equal(Stage2MatchedStrategy.TextReconciliation, result.MatchedBy);
        Assert.NotNull(result.Qid);
    }

    [Fact]
    public async Task ResolveAsync_Music_ResolvesAlbumWithArtist()
    {
        var result = await _reconciler.Stage2.ResolveAsync(Stage2Request.Music(
            correlationKey: "album-1",
            albumTitle: "Random Access Memories",
            artist: "Daft Punk"));

        Assert.True(result.Found, "Expected music album resolution");
        Assert.Equal(Stage2MatchedStrategy.MusicAlbum, result.MatchedBy);
        Assert.NotNull(result.Qid);
    }

    [Fact]
    public async Task ResolveAsync_Text_EmptyTypesWithoutOptIn_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await _reconciler.Stage2.ResolveAsync(new TextStage2Request
            {
                CorrelationKey = "bad",
                Title = "Something",
                CirrusSearchTypes = []
            }));
    }

    [Fact]
    public async Task ResolveBatchAsync_MixedStrategies_RoutesEachRequest()
    {
        var bridge = Stage2Request.Bridge(
            correlationKey: "b1",
            bridgeIds: new Dictionary<string, string> { ["imdb"] = "tt0903747" },
            wikidataProperties: new Dictionary<string, string> { ["imdb"] = "P345" });

        var text = Stage2Request.Text(
            correlationKey: "t1",
            title: "The Hitchhiker's Guide to the Galaxy",
            cirrusSearchTypes: ["Q7725634"],
            author: "Douglas Adams");

        var results = await _reconciler.Stage2.ResolveBatchAsync([bridge, text]);

        Assert.Equal(2, results.Count);
        Assert.Contains("b1", results.Keys);
        Assert.Contains("t1", results.Keys);
        Assert.Equal(Stage2MatchedStrategy.BridgeId, results["b1"].MatchedBy);
        Assert.Equal(Stage2MatchedStrategy.TextReconciliation, results["t1"].MatchedBy);
    }

    [Fact]
    public async Task ResolveBatchAsync_DuplicateRequests_Deduplicated()
    {
        // Two requests with the same IMDB ID should group and resolve to the same Qid.
        var r1 = Stage2Request.Bridge(
            correlationKey: "caller-a",
            bridgeIds: new Dictionary<string, string> { ["imdb"] = "tt0903747" },
            wikidataProperties: new Dictionary<string, string> { ["imdb"] = "P345" });

        var r2 = Stage2Request.Bridge(
            correlationKey: "caller-b",
            bridgeIds: new Dictionary<string, string> { ["imdb"] = "tt0903747" },
            wikidataProperties: new Dictionary<string, string> { ["imdb"] = "P345" });

        var results = await _reconciler.Stage2.ResolveBatchAsync([r1, r2]);

        Assert.Equal(2, results.Count);
        Assert.Equal(results["caller-a"].Qid, results["caller-b"].Qid);
    }

    [Fact]
    public async Task ResolveAsync_Bridge_NonexistentId_NotFound()
    {
        var result = await _reconciler.Stage2.ResolveAsync(Stage2Request.Bridge(
            correlationKey: "missing",
            bridgeIds: new Dictionary<string, string> { ["imdb"] = "tt9999999" },
            wikidataProperties: new Dictionary<string, string> { ["imdb"] = "P345" }));

        Assert.False(result.Found);
        Assert.Equal(Stage2MatchedStrategy.NotResolved, result.MatchedBy);
    }

    [Fact]
    public async Task ResolveAsync_Bridge_WithEditionPivot_SmokeTest()
    {
        // Smoke test for the EditionPivotRule code path. Resolves an IMDB ID and supplies
        // both WorkClasses and EditionClasses so the pivot logic runs end-to-end. We don't
        // pin the resulting QID or IsEdition flag because Wikidata's IMDB-ID graph can
        // return multiple candidates (series, seasons, episodes) and the first hit is not
        // deterministic. The test verifies that the pivot path executes without throwing
        // and produces a coherent result (Found=true, valid Qid, MatchedBy=BridgeId).
        var result = await _reconciler.Stage2.ResolveAsync(Stage2Request.Bridge(
            correlationKey: "pivot-smoke",
            bridgeIds: new Dictionary<string, string> { ["imdb"] = "tt0903747" },
            wikidataProperties: new Dictionary<string, string> { ["imdb"] = "P345" },
            editionPivot: new EditionPivotRule
            {
                WorkClasses = ["Q5398426", "Q15416"],              // TV series, TV program
                EditionClasses = ["Q21191270", "Q1261214"]          // TV episode, TV series episode
            }));

        Assert.True(result.Found);
        Assert.Equal(Stage2MatchedStrategy.BridgeId, result.MatchedBy);
        Assert.NotNull(result.Qid);
        // WorkQid should always be populated when Found is true — either the resolved entity
        // itself (when no pivot was needed) or the parent work (when the edition → work pivot fired).
        Assert.NotNull(result.WorkQid);
    }

    public void Dispose() => _reconciler.Dispose();
}
