namespace Tuvima.Wikidata.Tests;

/// <summary>
/// Integration tests for the v2.0.0 ChildrenService against the live Wikidata API.
/// </summary>
[Trait("Category", "Integration")]
public class ChildrenIntegrationTests : IDisposable
{
    private readonly WikidataReconciler _reconciler;

    public ChildrenIntegrationTests()
    {
        _reconciler = new WikidataReconciler(new WikidataReconcilerOptions
        {
            UserAgent = "Tuvima.Wikidata.Tests/2.2 (https://github.com/Tuvima/wikidata)"
        });
    }

    [Fact]
    public async Task TraverseChildrenAsync_Forward_ReturnsChildren()
    {
        // Breaking Bad (Q3577037) has P527 pointing to its seasons.
        var children = await _reconciler.Children.TraverseChildrenAsync(
            "Q3577037",
            "P527",
            Direction.Outgoing,
            childTypeFilter: ["Q3464665"]); // TV season

        Assert.NotEmpty(children);
        // Breaking Bad has 5 seasons — some may not be in Wikidata's graph, but we should get at least 3.
        Assert.True(children.Count >= 3, $"Expected at least 3 seasons, got {children.Count}");
    }

    [Fact]
    public async Task TraverseChildrenAsync_Incoming_ReplacesCaretPrefix()
    {
        // v1 used "^P179" string prefix for reverse traversal. v2 uses Direction.Incoming.
        // Q8337 (Harry Potter series) — find issues that have P179 pointing to it.
        var children = await _reconciler.Children.TraverseChildrenAsync(
            "Q8337",
            "P179",
            Direction.Incoming);

        Assert.NotEmpty(children);
    }

    [Fact]
    public async Task GetChildEntitiesAsync_TvPreset_ReturnsManifestWithSeasonsAndEpisodes()
    {
        var manifest = await _reconciler.Children.GetChildEntitiesAsync(new ChildEntityRequest
        {
            ParentQid = "Q3577037", // Breaking Bad
            Kind = ChildEntityKind.TvSeasonsAndEpisodes,
            MaxPrimary = 10,
            MaxTotal = 100
        });

        Assert.Equal("Q3577037", manifest.ParentQid);
        Assert.True(manifest.PrimaryCount > 0, $"Expected seasons, got {manifest.PrimaryCount}");
        Assert.True(manifest.TotalCount <= 100, $"Expected TotalCount <= 100 (cap), got {manifest.TotalCount}");
        Assert.NotEmpty(manifest.Children);
    }

    [Fact]
    public async Task GetChildEntitiesAsync_MaxTotalCap_IsRespected()
    {
        var manifest = await _reconciler.Children.GetChildEntitiesAsync(new ChildEntityRequest
        {
            ParentQid = "Q3577037",
            Kind = ChildEntityKind.TvSeasonsAndEpisodes,
            MaxPrimary = 20,
            MaxTotal = 5 // artificially tight
        });

        Assert.True(manifest.Children.Count <= 5, $"Expected <= 5 children, got {manifest.Children.Count}");
    }

    [Fact]
    public async Task GetChildEntitiesAsync_CustomTraversal_WorksWithoutPreset()
    {
        var manifest = await _reconciler.Children.GetChildEntitiesAsync(new ChildEntityRequest
        {
            ParentQid = "Q3577037",
            Kind = ChildEntityKind.Custom,
            CustomTraversal = new CustomChildTraversal
            {
                RelationshipProperty = "P527",
                Direction = Direction.Outgoing,
                ChildTypeFilter = ["Q3464665"]
            },
            MaxPrimary = 10,
            MaxTotal = 10
        });

        Assert.NotEmpty(manifest.Children);
    }

    [Fact]
    public async Task GetChildEntitiesAsync_MusicTracksPreset_ReturnsManifest()
    {
        // "OK Computer" by Radiohead (Q213754) — well-established album with tracks in Wikidata.
        // Smoke test: verify the preset code path runs, returns a manifest, and doesn't throw.
        // Track count isn't pinned because Wikidata coverage of individual tracks varies.
        var manifest = await _reconciler.Children.GetChildEntitiesAsync(new ChildEntityRequest
        {
            ParentQid = "Q213754",
            Kind = ChildEntityKind.MusicTracks,
            MaxPrimary = 20,
            MaxTotal = 20
        });

        Assert.Equal("Q213754", manifest.ParentQid);
        Assert.NotNull(manifest.Children);
        Assert.True(manifest.Children.Count <= 20);
    }

    [Fact]
    public async Task GetChildEntitiesAsync_BookSequelsPreset_ReturnsManifest()
    {
        // "The Hitchhiker's Guide to the Galaxy" (Q25169) — first novel in a well-known
        // sequel chain. P156 (followed by) should resolve to at least one sequel.
        var manifest = await _reconciler.Children.GetChildEntitiesAsync(new ChildEntityRequest
        {
            ParentQid = "Q25169",
            Kind = ChildEntityKind.BookSequels,
            MaxPrimary = 10,
            MaxTotal = 10
        });

        Assert.Equal("Q25169", manifest.ParentQid);
        Assert.NotNull(manifest.Children);
    }

    public void Dispose() => _reconciler.Dispose();
}
