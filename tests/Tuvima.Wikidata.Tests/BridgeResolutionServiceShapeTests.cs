using Tuvima.Wikidata.Internal;

namespace Tuvima.Wikidata.Tests;

public class BridgeResolutionServiceShapeTests
{
    [Fact]
    public void Facade_ExposesBridgeService_AndRemovesStage2Surface()
    {
        using var reconciler = new WikidataReconciler();

        Assert.NotNull(reconciler.Bridge);
        Assert.Null(typeof(WikidataReconciler).GetProperty("Stage2"));

        var assembly = typeof(WikidataReconciler).Assembly;
        Assert.Null(assembly.GetType("Tuvima.Wikidata.IStage2Request"));
        Assert.Null(assembly.GetType("Tuvima.Wikidata.Stage2Result"));
        Assert.Null(assembly.GetType("Tuvima.Wikidata.Services.Stage2Service"));
    }

    [Fact]
    public void BridgeCatalog_NormalizesOfficialProviderMappings()
    {
        var request = new BridgeResolutionRequest
        {
            CorrelationKey = "row-1",
            MediaKind = BridgeMediaKind.MusicAlbum,
            BridgeIds = new Dictionary<string, string>
            {
                ["apple_music_collection_id"] = " 1440833098 ",
                ["open_library_id"] = "ol24229316m",
                ["musicbrainz_release_group_id"] = "E2E16E9C-779E-43BE-832F-76BB0A0F9B84",
                ["comicvine_id"] = "4000-12345"
            }
        };

        var normalized = BridgeIdCatalog.Normalize(request);

        Assert.Contains(normalized, id => id.RawKey == "apple_music_collection_id" && id.PropertyId == "P2281" && id.NormalizedValue == "1440833098");
        Assert.Contains(normalized, id => id.RawKey == "open_library_id" && id.PropertyId == "P648" && id.NormalizedValue == "OL24229316M");
        Assert.Contains(normalized, id => id.RawKey == "musicbrainz_release_group_id" && id.PropertyId == "P436" && id.NormalizedValue == "e2e16e9c-779e-43be-832f-76bb0a0f9b84");
        Assert.Contains(normalized, id => id.RawKey == "comicvine_id" && id.PropertyId == "P5905" && id.NormalizedValue == "4000-12345");
    }

    [Fact]
    public async Task Bridge_EmptyBatch_ReturnsEmptyDictionary()
    {
        using var reconciler = new WikidataReconciler();

        var results = await reconciler.Bridge.ResolveBatchAsync([]);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Bridge_InvalidRequest_ReturnsTypedFailure()
    {
        using var reconciler = new WikidataReconciler();

        var result = await reconciler.Bridge.ResolveAsync(new BridgeResolutionRequest
        {
            CorrelationKey = "bad"
        });

        Assert.False(result.Found);
        Assert.Equal(BridgeResolutionStatus.InvalidRequest, result.Status);
        Assert.Equal(WikidataFailureKind.NotFound, result.FailureKind);
    }
}
