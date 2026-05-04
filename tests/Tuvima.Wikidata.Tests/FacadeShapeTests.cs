namespace Tuvima.Wikidata.Tests;

/// <summary>
/// Contract tests for the v2.0.0 facade — verifies sub-services are exposed
/// and returned collections honor their documented shape without hitting the network.
/// </summary>
public class FacadeShapeTests
{
    [Fact]
    public void Facade_ExposesAllSubServices()
    {
        using var reconciler = new WikidataReconciler();

        Assert.NotNull(reconciler.Reconcile);
        Assert.NotNull(reconciler.Entities);
        Assert.NotNull(reconciler.Wikipedia);
        Assert.NotNull(reconciler.Editions);
        Assert.NotNull(reconciler.Children);
        Assert.NotNull(reconciler.Authors);
        Assert.NotNull(reconciler.Labels);
        Assert.NotNull(reconciler.Series);
    }

    [Fact]
    public void Facade_SameSubServiceInstanceAcrossCalls()
    {
        using var reconciler = new WikidataReconciler();

        Assert.Same(reconciler.Reconcile, reconciler.Reconcile);
        Assert.Same(reconciler.Labels, reconciler.Labels);
    }

    [Fact]
    public async Task LabelsService_EmptyInput_ReturnsEmptyDictionary()
    {
        using var reconciler = new WikidataReconciler();

        var result = await reconciler.Labels.GetBatchAsync([]);

        Assert.Empty(result);
    }

    [Fact]
    public async Task AuthorsService_EmptyAfterEtAlStrip_ReturnsNoAuthors()
    {
        using var reconciler = new WikidataReconciler();

        var result = await reconciler.Authors.ResolveAsync(new AuthorResolutionRequest
        {
            RawAuthorString = "et al."
        });

        Assert.Empty(result.Authors);
        Assert.Contains("et al.", result.UnresolvedNames);
    }

    [Fact]
    public void PropertyConstraint_SingleValueConvenienceCtor_WrapsAsValues()
    {
        var c = new PropertyConstraint("P569", "1952-03-11");

        Assert.Equal("P569", c.PropertyId);
        Assert.Single(c.Values);
        Assert.Equal("1952-03-11", c.Values[0]);
    }

    [Fact]
    public void PropertyConstraint_MultiValueCtor_PreservesList()
    {
        var c = new PropertyConstraint("P50", new[] { "Q1", "Q2", "Q3" });

        Assert.Equal(3, c.Values.Count);
    }

    [Fact]
    public void ReconciliationRequest_HasOnlyTypesField()
    {
        var req = new ReconciliationRequest
        {
            Query = "Douglas Adams",
            Types = ["Q5"]
        };

        Assert.Equal("Douglas Adams", req.Query);
        Assert.NotNull(req.Types);
        Assert.Single(req.Types!);
    }

    [Fact]
    public void Direction_AccessibleFromRootNamespace()
    {
        // v2 breaking change: Direction promoted to Tuvima.Wikidata root namespace.
        Direction d = Direction.Incoming;
        Assert.Equal(Direction.Incoming, d);
    }

    [Fact]
    public void ChildEntityRequest_RequiresParentQidAndKind()
    {
        var req = new ChildEntityRequest
        {
            ParentQid = "Q3577037",
            Kind = ChildEntityKind.TvSeasonsAndEpisodes
        };

        Assert.Equal("Q3577037", req.ParentQid);
        Assert.Equal(ChildEntityKind.TvSeasonsAndEpisodes, req.Kind);
        Assert.Equal(20, req.MaxPrimary);
        Assert.Equal(500, req.MaxTotal);
        Assert.True(req.IncludeCreatorProperties);
    }

    [Fact]
    public void CustomChildTraversal_DefaultDirectionIsOutgoing()
    {
        var t = new CustomChildTraversal
        {
            RelationshipProperty = "P527"
        };

        Assert.Equal(Direction.Outgoing, t.Direction);
        Assert.Equal("P1545", t.OrdinalProperty);
    }
}
