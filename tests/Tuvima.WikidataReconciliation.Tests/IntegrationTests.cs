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
        var results = await _reconciler.ReconcileAsync(new ReconciliationRequest
        {
            Query = "1984",
            Type = "Q7725634", // literary work
            Limit = 10
        });

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Id == "Q208460");
    }

    [Fact]
    public async Task Novel1984_WithoutType_ShouldFindNovel()
    {
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
        Assert.Equal("Q42", resultsWithoutProps[0].Id);
    }

    [Fact]
    public async Task ScoreBreakdown_ShouldContainLabelAndPropertyScores()
    {
        var results = await _reconciler.ReconcileAsync(new ReconciliationRequest
        {
            Query = "Douglas Adams",
            Type = "Q5",
            Properties =
            [
                new PropertyConstraint("P27", "Q145")
            ]
        });

        Assert.NotEmpty(results);
        var breakdown = results[0].Breakdown;
        Assert.NotNull(breakdown);
        Assert.True(breakdown.LabelScore > 90, $"Expected label score > 90 but got {breakdown.LabelScore}");
        Assert.True(breakdown.PropertyScores.ContainsKey("P27"), "Expected P27 in property scores");
        Assert.Equal(100.0, breakdown.PropertyScores["P27"]);
        Assert.True(breakdown.TypeMatched);
        Assert.False(breakdown.TypePenaltyApplied);
    }

    [Fact]
    public async Task SuggestAsync_ShouldReturnResults()
    {
        var results = await _reconciler.SuggestAsync("Douglas");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Id == "Q42");
        Assert.All(results, r =>
        {
            Assert.NotEmpty(r.Id);
            Assert.NotEmpty(r.Name);
        });
    }

    [Fact]
    public async Task ReconcileBatchStreamAsync_ShouldYieldAllResults()
    {
        var requests = new List<ReconciliationRequest>
        {
            new() { Query = "Douglas Adams" },
            new() { Query = "United States of America" },
            new() { Query = "Albert Einstein" }
        };

        var received = new List<(int Index, IReadOnlyList<ReconciliationResult> Results)>();

        await foreach (var item in _reconciler.ReconcileBatchStreamAsync(requests))
        {
            received.Add(item);
        }

        Assert.Equal(3, received.Count);
        // All indices should be present (0, 1, 2), though order may vary
        Assert.Equal([0, 1, 2], received.Select(r => r.Index).OrderBy(i => i));
        Assert.All(received, r => Assert.NotEmpty(r.Results));
    }

    public void Dispose()
    {
        _reconciler.Dispose();
    }
}
