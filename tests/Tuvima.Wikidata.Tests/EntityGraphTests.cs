using Tuvima.Wikidata.Graph;

namespace Tuvima.Wikidata.Tests;

public class EntityGraphTests
{
    private static EntityGraph BuildDuneGraph()
    {
        var nodes = new[]
        {
            new GraphNode { Qid = "Q1", Label = "Paul Atreides", Type = "Character", WorkQids = ["W1", "W2"] },
            new GraphNode { Qid = "Q2", Label = "Duke Leto", Type = "Character", WorkQids = ["W1"] },
            new GraphNode { Qid = "Q3", Label = "Lady Jessica", Type = "Character", WorkQids = ["W1", "W2"] },
            new GraphNode { Qid = "Q4", Label = "Baron Harkonnen", Type = "Character", WorkQids = ["W1"] },
            new GraphNode { Qid = "Q5", Label = "Alia Atreides", Type = "Character", WorkQids = ["W2"] },
            new GraphNode { Qid = "Q6", Label = "Leto II", Type = "Character", WorkQids = ["W2", "W3"] },
            new GraphNode { Qid = "Q7", Label = "Arrakis", Type = "Location", WorkQids = ["W1"] },
        };

        var edges = new[]
        {
            new GraphEdge { SubjectQid = "Q1", Relationship = "father", ObjectQid = "Q2" },
            new GraphEdge { SubjectQid = "Q1", Relationship = "mother", ObjectQid = "Q3" },
            new GraphEdge { SubjectQid = "Q2", Relationship = "child", ObjectQid = "Q1" },
            new GraphEdge { SubjectQid = "Q3", Relationship = "child", ObjectQid = "Q1" },
            new GraphEdge { SubjectQid = "Q3", Relationship = "child", ObjectQid = "Q5" },
            new GraphEdge { SubjectQid = "Q1", Relationship = "child", ObjectQid = "Q6" },
            new GraphEdge { SubjectQid = "Q2", Relationship = "enemy", ObjectQid = "Q4" },
            new GraphEdge { SubjectQid = "Q1", Relationship = "residence", ObjectQid = "Q7" },
        };

        return new EntityGraph(nodes, edges);
    }

    [Fact]
    public void Constructor_CountsNodesAndEdges()
    {
        var graph = BuildDuneGraph();

        Assert.Equal(7, graph.NodeCount);
        Assert.Equal(8, graph.EdgeCount);
    }

    [Fact]
    public void Constructor_EmptyInputs_CreatesEmptyGraph()
    {
        var graph = new EntityGraph([], []);

        Assert.Equal(0, graph.NodeCount);
        Assert.Equal(0, graph.EdgeCount);
    }

    [Fact]
    public void Constructor_SkipsEdgesWithUnknownNodes()
    {
        var nodes = new[] { new GraphNode { Qid = "Q1", Label = "A" } };
        var edges = new[] { new GraphEdge { SubjectQid = "Q1", Relationship = "r", ObjectQid = "Q999" } };

        var graph = new EntityGraph(nodes, edges);

        Assert.Equal(1, graph.NodeCount);
        Assert.Equal(0, graph.EdgeCount);
    }

    [Fact]
    public void Constructor_DuplicateNodes_FirstWins()
    {
        var nodes = new[]
        {
            new GraphNode { Qid = "Q1", Label = "First" },
            new GraphNode { Qid = "Q1", Label = "Second" },
        };

        var graph = new EntityGraph(nodes, []);

        Assert.Equal(1, graph.NodeCount);
    }

    // FindPaths tests

    [Fact]
    public void FindPaths_DirectConnection_ReturnsSinglePath()
    {
        var graph = BuildDuneGraph();

        var paths = graph.FindPaths("Q1", "Q2");

        Assert.Single(paths);
        Assert.Equal(["Q1", "Q2"], paths[0]);
    }

    [Fact]
    public void FindPaths_SameNode_ReturnsTrivialPath()
    {
        var graph = BuildDuneGraph();

        var paths = graph.FindPaths("Q1", "Q1");

        Assert.Single(paths);
        Assert.Equal(["Q1"], paths[0]);
    }

    [Fact]
    public void FindPaths_NoPath_ReturnsEmpty()
    {
        var nodes = new[]
        {
            new GraphNode { Qid = "Q1" },
            new GraphNode { Qid = "Q2" },
        };
        var graph = new EntityGraph(nodes, []);

        var paths = graph.FindPaths("Q1", "Q2");

        Assert.Empty(paths);
    }

    [Fact]
    public void FindPaths_UnknownNode_ReturnsEmpty()
    {
        var graph = BuildDuneGraph();

        var paths = graph.FindPaths("Q1", "Q999");

        Assert.Empty(paths);
    }

    [Fact]
    public void FindPaths_MultiHop_FindsPath()
    {
        var graph = BuildDuneGraph();

        // Q4 (Baron) -> Q2 (Leto) -> Q1 (Paul) via enemy + father/child edges
        var paths = graph.FindPaths("Q4", "Q1");

        Assert.NotEmpty(paths);
        Assert.All(paths, p => Assert.True(p.Count <= 5)); // maxHops=4 default
    }

    [Fact]
    public void FindPaths_RespectsMaxHops()
    {
        var graph = BuildDuneGraph();

        var paths = graph.FindPaths("Q4", "Q7", maxHops: 1);

        // Q4 -> Q2 -> Q1 -> Q7 is 3 hops, so maxHops=1 should find nothing
        Assert.Empty(paths);
    }

    // GetFamilyTree tests

    [Fact]
    public void GetFamilyTree_CenterCharacter_AtGenZero()
    {
        var graph = BuildDuneGraph();

        var tree = graph.GetFamilyTree("Q1");

        Assert.True(tree.ContainsKey(0));
        Assert.Equal(["Q1"], tree[0]);
    }

    [Fact]
    public void GetFamilyTree_FindsParents()
    {
        var graph = BuildDuneGraph();

        var tree = graph.GetFamilyTree("Q1", generations: 1);

        Assert.True(tree.ContainsKey(-1));
        var parents = tree[-1];
        Assert.Contains("Q2", parents); // father
        Assert.Contains("Q3", parents); // mother
    }

    [Fact]
    public void GetFamilyTree_FindsChildren()
    {
        var graph = BuildDuneGraph();

        var tree = graph.GetFamilyTree("Q1", generations: 1);

        Assert.True(tree.ContainsKey(1));
        Assert.Contains("Q6", tree[1]); // child
    }

    [Fact]
    public void GetFamilyTree_UnknownNode_ReturnsEmpty()
    {
        var graph = BuildDuneGraph();

        var tree = graph.GetFamilyTree("Q999");

        Assert.Empty(tree);
    }

    [Fact]
    public void GetFamilyTree_CustomRelationships()
    {
        var graph = BuildDuneGraph();

        var tree = graph.GetFamilyTree("Q1",
            generations: 1,
            parentRelationships: new HashSet<string> { "father" }, // mother excluded
            childRelationships: new HashSet<string>());

        Assert.True(tree.ContainsKey(-1));
        var parents = tree[-1];
        Assert.Contains("Q2", parents);
        Assert.DoesNotContain("Q3", parents);
    }

    [Fact]
    public void GetFamilyTree_DescendantsIncludeIncomingParentEdges()
    {
        var nodes = new[]
        {
            new GraphNode { Qid = "Q1", Label = "Child" },
            new GraphNode { Qid = "Q2", Label = "Parent" }
        };
        var edges = new[]
        {
            new GraphEdge { SubjectQid = "Q1", Relationship = "father", ObjectQid = "Q2" }
        };
        var graph = new EntityGraph(nodes, edges);

        var tree = graph.GetFamilyTree("Q2", generations: 1);

        Assert.True(tree.ContainsKey(1));
        Assert.Contains("Q1", tree[1]);
    }

    // FindCrossMediaEntities tests

    [Fact]
    public void FindCrossMediaEntities_DefaultMinWorks_ReturnsMultiWorkEntities()
    {
        var graph = BuildDuneGraph();

        var crossMedia = graph.FindCrossMediaEntities();

        // Q1 (W1,W2), Q3 (W1,W2), Q6 (W2,W3) have 2+ works
        Assert.Contains("Q1", crossMedia);
        Assert.Contains("Q3", crossMedia);
        Assert.Contains("Q6", crossMedia);
        // Q2 (W1 only), Q4 (W1 only), Q5 (W2 only), Q7 (W1 only)
        Assert.DoesNotContain("Q2", crossMedia);
        Assert.DoesNotContain("Q4", crossMedia);
        Assert.DoesNotContain("Q5", crossMedia);
        Assert.DoesNotContain("Q7", crossMedia);
    }

    [Fact]
    public void FindCrossMediaEntities_HigherThreshold()
    {
        var nodes = new[]
        {
            new GraphNode { Qid = "Q1", WorkQids = ["W1", "W2", "W3"] },
            new GraphNode { Qid = "Q2", WorkQids = ["W1", "W2"] },
            new GraphNode { Qid = "Q3", WorkQids = ["W1"] },
        };
        var graph = new EntityGraph(nodes, []);

        var crossMedia = graph.FindCrossMediaEntities(minWorks: 3);

        Assert.Single(crossMedia);
        Assert.Equal("Q1", crossMedia[0]);
    }

    [Fact]
    public void FindCrossMediaEntities_NodesWithoutWorkQids_Excluded()
    {
        var nodes = new[]
        {
            new GraphNode { Qid = "Q1", WorkQids = null },
            new GraphNode { Qid = "Q2", WorkQids = ["W1", "W2"] },
        };
        var graph = new EntityGraph(nodes, []);

        var crossMedia = graph.FindCrossMediaEntities();

        Assert.Single(crossMedia);
        Assert.Equal("Q2", crossMedia[0]);
    }

    // GetNeighbors tests

    [Fact]
    public void GetNeighbors_ReturnsOutgoingAndIncoming()
    {
        var graph = BuildDuneGraph();

        var neighbors = graph.GetNeighbors("Q1");

        // Outgoing: father->Q2, mother->Q3, child->Q6, residence->Q7
        // Incoming: child from Q2, child from Q3
        Assert.True(neighbors.Count >= 4);
        Assert.Contains(neighbors, n => n.Qid == "Q2" && n.Direction == Direction.Outgoing);
        Assert.Contains(neighbors, n => n.Qid == "Q3" && n.Direction == Direction.Outgoing);
        Assert.Contains(neighbors, n => n.Qid == "Q2" && n.Direction == Direction.Incoming);
    }

    [Fact]
    public void GetNeighbors_UnknownNode_ReturnsEmpty()
    {
        var graph = BuildDuneGraph();

        var neighbors = graph.GetNeighbors("Q999");

        Assert.Empty(neighbors);
    }

    [Fact]
    public void GetNeighbors_IsolatedNode_ReturnsEmpty()
    {
        var nodes = new[] { new GraphNode { Qid = "Q1" } };
        var graph = new EntityGraph(nodes, []);

        var neighbors = graph.GetNeighbors("Q1");

        Assert.Empty(neighbors);
    }

    // GetSubgraph tests

    [Fact]
    public void GetSubgraph_Radius0_ReturnsCenterOnly()
    {
        var graph = BuildDuneGraph();

        var sub = graph.GetSubgraph("Q1", radius: 0);

        Assert.Equal(1, sub.NodeCount);
        Assert.Equal(0, sub.EdgeCount);
    }

    [Fact]
    public void GetSubgraph_Radius1_ReturnsDirectNeighbors()
    {
        var graph = BuildDuneGraph();

        var sub = graph.GetSubgraph("Q1", radius: 1);

        // Q1 connects to Q2, Q3, Q6, Q7 (outgoing) and Q2, Q3 (incoming)
        // So neighbors are Q2, Q3, Q6, Q7 -> 5 nodes total with Q1
        Assert.True(sub.NodeCount >= 4);
        Assert.True(sub.EdgeCount >= 1);
    }

    [Fact]
    public void GetSubgraph_UnknownNode_ReturnsEmptyGraph()
    {
        var graph = BuildDuneGraph();

        var sub = graph.GetSubgraph("Q999");

        Assert.Equal(0, sub.NodeCount);
        Assert.Equal(0, sub.EdgeCount);
    }

    [Fact]
    public void GetSubgraph_LargeRadius_ReturnsFullGraph()
    {
        var graph = BuildDuneGraph();

        var sub = graph.GetSubgraph("Q1", radius: 10);

        Assert.Equal(graph.NodeCount, sub.NodeCount);
    }

    // Case insensitivity tests

    [Fact]
    public void FindPaths_CaseInsensitiveQids()
    {
        var nodes = new[]
        {
            new GraphNode { Qid = "Q1" },
            new GraphNode { Qid = "Q2" },
        };
        var edges = new[] { new GraphEdge { SubjectQid = "Q1", Relationship = "r", ObjectQid = "Q2" } };
        var graph = new EntityGraph(nodes, edges);

        var paths = graph.FindPaths("q1", "q2");

        Assert.Single(paths);
    }
}
