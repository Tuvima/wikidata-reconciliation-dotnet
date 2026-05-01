namespace Tuvima.Wikidata.Tests;

[Trait("Category", "Integration")]
public class BridgeIntegrationTests : IDisposable
{
    private readonly WikidataReconciler _reconciler;

    public BridgeIntegrationTests()
    {
        _reconciler = new WikidataReconciler(new WikidataReconcilerOptions
        {
            UserAgent = "Tuvima.Wikidata.Tests/3.0 (https://github.com/Tuvima/wikidata)"
        });
    }

    [Fact]
    public async Task ResolveAsync_Bridge_ImdbIdResolves()
    {
        var result = await _reconciler.Bridge.ResolveAsync(new BridgeResolutionRequest
        {
            CorrelationKey = "row-1",
            MediaKind = BridgeMediaKind.TvSeries,
            Title = "Breaking Bad",
            BridgeIds = new Dictionary<string, string> { ["imdb_id"] = "tt0903747" }
        });

        Assert.True(result.Found, "Expected IMDb lookup to resolve");
        Assert.Equal(BridgeResolutionStrategy.BridgeId, result.MatchedBy);
        Assert.NotNull(result.SelectedCandidate?.Qid);
        Assert.StartsWith("Q", result.SelectedCandidate.Qid);
    }

    [Fact]
    public async Task ResolveAsync_TextFallback_ResolvesBookWithTypeHint()
    {
        var result = await _reconciler.Bridge.ResolveAsync(new BridgeResolutionRequest
        {
            CorrelationKey = "book-1",
            MediaKind = BridgeMediaKind.Book,
            Title = "The Hitchhiker's Guide to the Galaxy"
        });

        Assert.True(result.Found, "Expected text fallback to resolve");
        Assert.Equal(BridgeResolutionStrategy.TextSearch, result.MatchedBy);
        Assert.NotNull(result.SelectedCandidate?.Qid);
    }

    [Fact]
    public async Task ResolveBatchAsync_DuplicateBridgeIds_ReturnSameResult()
    {
        var results = await _reconciler.Bridge.ResolveBatchAsync([
            new BridgeResolutionRequest
            {
                CorrelationKey = "caller-a",
                MediaKind = BridgeMediaKind.TvSeries,
                BridgeIds = new Dictionary<string, string> { ["imdb_id"] = "tt0903747" }
            },
            new BridgeResolutionRequest
            {
                CorrelationKey = "caller-b",
                MediaKind = BridgeMediaKind.TvSeries,
                BridgeIds = new Dictionary<string, string> { ["imdb_id"] = "tt0903747" }
            }
        ]);

        Assert.Equal(2, results.Count);
        Assert.Equal(results["caller-a"].SelectedCandidate?.Qid, results["caller-b"].SelectedCandidate?.Qid);
    }

    [Fact]
    public async Task ResolveAsync_Bridge_NonexistentId_NotFound()
    {
        var result = await _reconciler.Bridge.ResolveAsync(new BridgeResolutionRequest
        {
            CorrelationKey = "missing",
            BridgeIds = new Dictionary<string, string> { ["imdb_id"] = "tt9999999" }
        });

        Assert.False(result.Found);
        Assert.Equal(BridgeResolutionStatus.NotFound, result.Status);
        Assert.Equal(WikidataFailureKind.NotFound, result.FailureKind);
    }

    public void Dispose() => _reconciler.Dispose();
}
