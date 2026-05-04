namespace Tuvima.Wikidata.Tests;

/// <summary>
/// Integration tests for the generic series manifest service against the live Wikidata API.
/// </summary>
[Trait("Category", "Integration")]
public class SeriesManifestIntegrationTests : IDisposable
{
    private readonly WikidataReconciler _reconciler;

    public SeriesManifestIntegrationTests()
    {
        _reconciler = new WikidataReconciler(new WikidataReconcilerOptions
        {
            UserAgent = "Tuvima.Wikidata.Tests/3.0.1 (https://github.com/Tuvima/wikidata)"
        });
    }

    [Fact]
    public async Task GetManifestAsync_TheExpanse_ReturnsShapeAndProvenance()
    {
        var manifest = await _reconciler.Series.GetManifestAsync(new SeriesManifestRequest
        {
            SeriesQid = "Q19610143",
            MaxItems = 100
        });

        Assert.Equal("Q19610143", manifest.SeriesQid);
        Assert.NotEmpty(manifest.Items);
        Assert.Contains(manifest.Items, item => item.Label?.Contains("Leviathan Wakes", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(manifest.Items, item => item.Label?.Contains("Caliban", StringComparison.OrdinalIgnoreCase) == true);
        Assert.All(manifest.Items, item => Assert.NotEmpty(item.SourceProperties));
        Assert.True(
            manifest.Items.Any(item => item.IsExpandedFromCollection || item.IsCollection || item.Relationships.Count > 0) ||
            manifest.Warnings.Count > 0,
            "Expected relationship provenance or warnings to make partial Wikidata coverage visible.");
    }

    public void Dispose() => _reconciler.Dispose();
}
