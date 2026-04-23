# Graph Module

The `Tuvima.Wikidata.Graph` namespace provides lightweight in-memory entity graph traversal. It replaces heavy graph database dependencies (like dotNetRDF) for common entity relationship operations using pure C# with adjacency lists.

**Zero dependencies.** AOT compatible. Thread-safe for concurrent reads.

## Building a Graph

The graph module operates on caller-provided data — it doesn't touch databases or APIs. Load your entities and relationships from wherever they're stored and pass them in:

```csharp
using Tuvima.Wikidata.Graph;

var nodes = new[]
{
    new GraphNode { Qid = "Q937618", Label = "Paul Atreides", Type = "Character" },
    new GraphNode { Qid = "Q312545", Label = "Duke Leto", Type = "Character" },
    new GraphNode { Qid = "Q578770", Label = "Lady Jessica", Type = "Character" },
    new GraphNode
    {
        Qid = "Q190800", Label = "Dune", Type = "Work",
        WorkQids = ["Q190800"]
    },
};

var edges = new[]
{
    new GraphEdge { SubjectQid = "Q937618", Relationship = "father", ObjectQid = "Q312545" },
    new GraphEdge { SubjectQid = "Q937618", Relationship = "mother", ObjectQid = "Q578770" },
    new GraphEdge { SubjectQid = "Q312545", Relationship = "child", ObjectQid = "Q937618" },
};

var graph = new EntityGraph(nodes, edges);
Console.WriteLine($"Nodes: {graph.NodeCount}, Edges: {graph.EdgeCount}");
```

## Data Model

### GraphNode

| Property | Type | Description |
|---|---|---|
| `Qid` | `string` (required) | Wikidata QID (e.g. "Q937618") |
| `Label` | `string?` | Human-readable label |
| `Type` | `string?` | Discriminator (e.g. "Character", "Location") |
| `WorkQids` | `IReadOnlyList<string>?` | Works this entity appears in (for cross-media detection) |

### GraphEdge

| Property | Type | Description |
|---|---|---|
| `SubjectQid` | `string` (required) | Source entity QID |
| `Relationship` | `string` (required) | Edge type (e.g. "father", "member_of") |
| `ObjectQid` | `string` (required) | Target entity QID |
| `Confidence` | `double` | Optional weight (default: 1.0) |
| `ContextWorkQid` | `string?` | Optional work providing context |

## Pathfinding

Find all paths between two entities using BFS:

```csharp
var paths = graph.FindPaths("Q937618", "Q312545", maxHops: 4);

foreach (var path in paths)
    Console.WriteLine(string.Join(" -> ", path));
// Q937618 -> Q312545
```

Returns paths as ordered lists of QIDs, shortest first. Empty if no path exists.

## Family Trees

Build a family tree centered on a character:

```csharp
var tree = graph.GetFamilyTree("Q937618", generations: 3);

// Key 0 = center character
// Negative keys = ancestors (-1 = parents, -2 = grandparents)
// Positive keys = descendants (1 = children, 2 = grandchildren)

foreach (var (gen, qids) in tree.OrderBy(kv => kv.Key))
    Console.WriteLine($"Generation {gen}: {string.Join(", ", qids)}");
```

Configurable relationship types:

```csharp
var tree = graph.GetFamilyTree("Q937618",
    generations: 3,
    parentRelationships: new HashSet<string> { "father", "mother", "adoptive_parent" },
    childRelationships: new HashSet<string> { "child", "adopted_child" });
```

Default parent relationships: `{"father", "mother"}`. Default child relationships: `{"child"}`.

As of v2.5.0, the direction rules are strict:

- Ancestors = outgoing parent edges or incoming child edges.
- Descendants = outgoing child edges or incoming parent edges.

## Cross-Media Entity Detection

Find entities appearing in multiple works:

```csharp
var crossMedia = graph.FindCrossMediaEntities(minWorks: 2);

foreach (var qid in crossMedia)
    Console.WriteLine($"{qid} appears in 2+ works");
```

Uses the `WorkQids` property on each `GraphNode`.

## Neighbors

Get all entities directly connected to a given entity:

```csharp
var neighbors = graph.GetNeighbors("Q937618");

foreach (var (qid, relationship, direction) in neighbors)
    Console.WriteLine($"{qid} ({relationship}, {direction})");
// Q312545 (father, Outgoing)
// Q578770 (mother, Outgoing)
```

## Subgraph Extraction

Extract a focused subgraph (ego graph) around an entity:

```csharp
var subgraph = graph.GetSubgraph("Q937618", radius: 2);
Console.WriteLine($"Subgraph: {subgraph.NodeCount} nodes, {subgraph.EdgeCount} edges");
```

Returns a new `EntityGraph` containing only nodes and edges within the specified radius.

## Design Principles

1. **No database dependency.** The caller provides data; the library doesn't know about persistence.
2. **No RDF, no SPARQL.** Pure C# with adjacency lists. Operations are BFS traversals.
3. **Zero new dependencies.** Uses only `System.Collections` and `System.Linq`.
4. **Immutable after construction.** Built once, thread-safe for concurrent reads.
5. **Synchronous API.** Graph operations are pure in-memory computation — no async needed.
6. **Configurable relationship types.** `GetFamilyTree` accepts which relationship names count as parent/child edges.
