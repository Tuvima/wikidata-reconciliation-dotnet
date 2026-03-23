namespace Tuvima.WikidataReconciliation.Tests;

/// <summary>
/// Integration tests against the live Wikidata API.
/// These tests require network access and may be slow.
/// Skip in CI with: dotnet test --filter "Category!=Integration"
/// </summary>
[Trait("Category", "Integration")]
public class IntegrationTests : IDisposable
{
    private readonly WikidataReconciler _reconciler;

    public IntegrationTests()
    {
        _reconciler = new WikidataReconciler(new WikidataReconcilerOptions
        {
            UserAgent = "Tuvima.WikidataReconciliation.Tests/0.1 (https://github.com/Tuvima/wikidata-reconciliation-dotnet)"
        });
    }

    [Fact]
    public async Task DouglasAdams_ShouldReturnQ42()
    {
        var results = await _reconciler.ReconcileAsync("Douglas Adams");

        Assert.NotEmpty(results);
        Assert.Equal("Q42", results[0].Id);
        Assert.True(results[0].Score > 90, $"Expected score > 90 but got {results[0].Score}");
    }

    [Fact]
    public async Task DouglasAdams_WithTypeHuman_ShouldReturnQ42()
    {
        var results = await _reconciler.ReconcileAsync("Douglas Adams", "Q5");

        Assert.NotEmpty(results);
        Assert.Equal("Q42", results[0].Id);
    }

    [Fact]
    public async Task Novel1984_WithType_ShouldFindNovel()
    {
        // This is the key test case — "1984" needs full-text search to find the novel
        // Q7725634 = "literary work" (Nineteen Eighty-Four's P31)
        var results = await _reconciler.ReconcileAsync(new ReconciliationRequest
        {
            Query = "1984",
            Type = "Q7725634", // literary work
            Limit = 10
        });

        Assert.NotEmpty(results);
        // Q208460 is "Nineteen Eighty-Four" the novel by George Orwell
        Assert.Contains(results, r => r.Id == "Q208460");
    }

    [Fact]
    public async Task Novel1984_WithoutType_ShouldFindNovel()
    {
        // Even without type, dual search should find the novel
        var results = await _reconciler.ReconcileAsync(new ReconciliationRequest
        {
            Query = "1984",
            Limit = 10
        });

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Id == "Q208460");
    }

    [Fact]
    public async Task UnitedStatesOfAmerica_ShouldReturnQ30()
    {
        var results = await _reconciler.ReconcileAsync("United States of America");

        Assert.NotEmpty(results);
        Assert.Equal("Q30", results[0].Id);
    }

    [Fact]
    public async Task DirectQidLookup_ShouldReturnEntity()
    {
        var results = await _reconciler.ReconcileAsync("Q42");

        Assert.NotEmpty(results);
        Assert.Equal("Q42", results[0].Id);
        Assert.Equal("Douglas Adams", results[0].Name);
    }

    [Fact]
    public async Task BatchReconciliation_ShouldReturnAllResults()
    {
        var requests = new List<ReconciliationRequest>
        {
            new() { Query = "Douglas Adams" },
            new() { Query = "United States of America" },
            new() { Query = "Albert Einstein" }
        };

        var results = await _reconciler.ReconcileBatchAsync(requests);

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.NotEmpty(r));
    }

    [Fact]
    public async Task DouglasAdams_WithProperties_ShouldScoreHigher()
    {
        var resultsWithProps = await _reconciler.ReconcileAsync(new ReconciliationRequest
        {
            Query = "Douglas Adams",
            Type = "Q5",
            Properties =
            [
                new PropertyConstraint("P27", "Q145") // country of citizenship: United Kingdom
            ]
        });

        var resultsWithoutProps = await _reconciler.ReconcileAsync("Douglas Adams", "Q5");

        Assert.NotEmpty(resultsWithProps);
        Assert.Equal("Q42", resultsWithProps[0].Id);

        // The version with properties should still find Q42
        Assert.Equal("Q42", resultsWithoutProps[0].Id);
    }

    public void Dispose()
    {
        _reconciler.Dispose();
    }
}
