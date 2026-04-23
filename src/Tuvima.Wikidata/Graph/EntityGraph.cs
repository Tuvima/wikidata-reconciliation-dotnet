namespace Tuvima.Wikidata.Graph;

/// <summary>
/// An immutable, in-memory entity graph built from caller-provided nodes and edges.
/// Supports pathfinding, family tree traversal, cross-media detection, and subgraph extraction.
/// Thread-safe for concurrent reads after construction.
/// </summary>
public sealed class EntityGraph
{
    private static readonly IReadOnlySet<string> DefaultParentRelationships =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "father", "mother" };

    private static readonly IReadOnlySet<string> DefaultChildRelationships =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "child" };

    private readonly Dictionary<string, GraphNode> _nodes;
    private readonly Dictionary<string, List<(string TargetQid, string Relationship)>> _outgoing;
    private readonly Dictionary<string, List<(string TargetQid, string Relationship)>> _incoming;

    /// <summary>Total number of nodes in the graph.</summary>
    public int NodeCount => _nodes.Count;

    /// <summary>Total number of directed edges in the graph.</summary>
    public int EdgeCount { get; }

    /// <summary>
    /// Builds an entity graph from caller-provided nodes and edges.
    /// </summary>
    /// <param name="nodes">Entity nodes. Duplicate QIDs are ignored (first wins).</param>
    /// <param name="edges">Directed relationship edges. Edges referencing unknown QIDs are silently skipped.</param>
    public EntityGraph(IEnumerable<GraphNode> nodes, IEnumerable<GraphEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);

        _nodes = new Dictionary<string, GraphNode>(StringComparer.OrdinalIgnoreCase);
        _outgoing = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase);
        _incoming = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in nodes)
        {
            _nodes.TryAdd(node.Qid, node);
        }

        var edgeCount = 0;
        foreach (var edge in edges)
        {
            if (!_nodes.ContainsKey(edge.SubjectQid) || !_nodes.ContainsKey(edge.ObjectQid))
                continue;

            if (!_outgoing.TryGetValue(edge.SubjectQid, out var outList))
            {
                outList = [];
                _outgoing[edge.SubjectQid] = outList;
            }
            outList.Add((edge.ObjectQid, edge.Relationship));

            if (!_incoming.TryGetValue(edge.ObjectQid, out var inList))
            {
                inList = [];
                _incoming[edge.ObjectQid] = inList;
            }
            inList.Add((edge.SubjectQid, edge.Relationship));

            edgeCount++;
        }

        EdgeCount = edgeCount;
    }

    /// <summary>
    /// Finds all paths between two entities up to <paramref name="maxHops"/> edges using BFS.
    /// Returns paths as ordered lists of QIDs from source to destination.
    /// </summary>
    /// <param name="fromQid">Source entity QID.</param>
    /// <param name="toQid">Destination entity QID.</param>
    /// <param name="maxHops">Maximum path length in edges. Default is 4.</param>
    /// <returns>All discovered paths, shortest first. Empty if no path exists.</returns>
    public IReadOnlyList<IReadOnlyList<string>> FindPaths(string fromQid, string toQid, int maxHops = 4)
    {
        ArgumentNullException.ThrowIfNull(fromQid);
        ArgumentNullException.ThrowIfNull(toQid);

        if (!_nodes.ContainsKey(fromQid) || !_nodes.ContainsKey(toQid))
            return [];

        if (string.Equals(fromQid, toQid, StringComparison.OrdinalIgnoreCase))
            return [[fromQid]];

        var results = new List<IReadOnlyList<string>>();
        var queue = new Queue<List<string>>();
        queue.Enqueue([fromQid]);

        while (queue.Count > 0)
        {
            var path = queue.Dequeue();
            if (path.Count > maxHops + 1)
                break;

            var current = path[^1];
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (target, _) in GetAllNeighborQids(current))
            {
                if (!seen.Add(target))
                    continue;

                if (path.Contains(target, StringComparer.OrdinalIgnoreCase))
                    continue;

                var newPath = new List<string>(path) { target };

                if (string.Equals(target, toQid, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(newPath);
                }
                else if (newPath.Count <= maxHops)
                {
                    queue.Enqueue(newPath);
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Builds a family tree centered on a character entity. Ancestors have negative generation keys,
    /// descendants have positive keys, and generation 0 is the center character.
    /// </summary>
    /// <param name="characterQid">The center character's QID.</param>
    /// <param name="generations">How many generations to traverse in each direction. Default is 3.</param>
    /// <param name="parentRelationships">Relationship types considered "parent" edges. Default: {"father", "mother"}.</param>
    /// <param name="childRelationships">Relationship types considered "child" edges. Default: {"child"}.</param>
    /// <returns>
    /// Dictionary mapping generation number to entity QIDs at that generation.
    /// Key 0 = center character, negative = ancestors, positive = descendants.
    /// </returns>
    public IReadOnlyDictionary<int, IReadOnlyList<string>> GetFamilyTree(
        string characterQid,
        int generations = 3,
        IReadOnlySet<string>? parentRelationships = null,
        IReadOnlySet<string>? childRelationships = null)
    {
        ArgumentNullException.ThrowIfNull(characterQid);

        if (!_nodes.ContainsKey(characterQid))
            return new Dictionary<int, IReadOnlyList<string>>();

        parentRelationships ??= DefaultParentRelationships;
        childRelationships ??= DefaultChildRelationships;

        var result = new Dictionary<int, List<string>>
        {
            [0] = [characterQid]
        };
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { characterQid };

        // Traverse ancestors (negative generations)
        TraverseFamilyDirection(characterQid, -1, generations, result, visited,
            qid => GetAncestorEntities(qid, parentRelationships, childRelationships));

        // Traverse descendants (positive generations)
        TraverseFamilyDirection(characterQid, 1, generations, result, visited,
            qid => GetDescendantEntities(qid, parentRelationships, childRelationships));

        return result.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<string>)kvp.Value);
    }

    /// <summary>
    /// Finds entities that appear in two or more distinct works.
    /// Uses the <see cref="GraphNode.WorkQids"/> property of each node.
    /// </summary>
    /// <param name="minWorks">Minimum number of distinct works required. Default is 2.</param>
    /// <returns>QIDs of entities appearing in at least <paramref name="minWorks"/> works.</returns>
    public IReadOnlyList<string> FindCrossMediaEntities(int minWorks = 2)
    {
        return _nodes.Values
            .Where(n => n.WorkQids is not null && n.WorkQids.Count >= minWorks)
            .Select(n => n.Qid)
            .ToList();
    }

    /// <summary>
    /// Returns all entities directly connected to a given entity, with relationship type and direction.
    /// </summary>
    /// <param name="qid">The entity QID to query.</param>
    /// <returns>List of neighboring entities with their relationship and direction.</returns>
    public IReadOnlyList<(string Qid, string Relationship, Direction Direction)> GetNeighbors(string qid)
    {
        ArgumentNullException.ThrowIfNull(qid);

        if (!_nodes.ContainsKey(qid))
            return [];

        var neighbors = new List<(string, string, Direction)>();

        if (_outgoing.TryGetValue(qid, out var outList))
        {
            foreach (var (target, rel) in outList)
                neighbors.Add((target, rel, Direction.Outgoing));
        }

        if (_incoming.TryGetValue(qid, out var inList))
        {
            foreach (var (source, rel) in inList)
                neighbors.Add((source, rel, Direction.Incoming));
        }

        return neighbors;
    }

    /// <summary>
    /// Extracts a subgraph (ego graph) centered on an entity, including all nodes within
    /// <paramref name="radius"/> hops and the edges between them.
    /// </summary>
    /// <param name="centerQid">Center entity QID.</param>
    /// <param name="radius">Maximum distance from center in hops. Default is 2.</param>
    /// <returns>A new <see cref="EntityGraph"/> containing only the nodes and edges within the radius.</returns>
    public EntityGraph GetSubgraph(string centerQid, int radius = 2)
    {
        ArgumentNullException.ThrowIfNull(centerQid);

        if (!_nodes.ContainsKey(centerQid))
            return new EntityGraph([], []);

        // BFS to find all nodes within radius
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { centerQid };
        var queue = new Queue<(string Qid, int Depth)>();
        queue.Enqueue((centerQid, 0));

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (depth >= radius)
                continue;

            foreach (var (neighbor, _) in GetAllNeighborQids(current))
            {
                if (visited.Add(neighbor))
                    queue.Enqueue((neighbor, depth + 1));
            }
        }

        // Collect nodes and edges within the subgraph
        var subNodes = visited.Select(qid => _nodes[qid]);
        var subEdges = new List<GraphEdge>();

        foreach (var qid in visited)
        {
            if (!_outgoing.TryGetValue(qid, out var outList))
                continue;

            foreach (var (target, rel) in outList)
            {
                if (visited.Contains(target))
                {
                    subEdges.Add(new GraphEdge
                    {
                        SubjectQid = qid,
                        Relationship = rel,
                        ObjectQid = target
                    });
                }
            }
        }

        return new EntityGraph(subNodes, subEdges);
    }

    private IEnumerable<(string Qid, string Relationship)> GetAllNeighborQids(string qid)
    {
        if (_outgoing.TryGetValue(qid, out var outList))
        {
            foreach (var item in outList)
                yield return item;
        }

        if (_incoming.TryGetValue(qid, out var inList))
        {
            foreach (var item in inList)
                yield return item;
        }
    }

    private void TraverseFamilyDirection(
        string startQid,
        int directionSign,
        int maxGenerations,
        Dictionary<int, List<string>> result,
        HashSet<string> visited,
        Func<string, IEnumerable<string>> getRelatedEntities)
    {
        var currentGen = new List<string> { startQid };

        for (var gen = 1; gen <= maxGenerations; gen++)
        {
            var nextGen = new List<string>();

            foreach (var qid in currentGen)
            {
                foreach (var candidate in getRelatedEntities(qid))
                {
                    if (visited.Add(candidate))
                        nextGen.Add(candidate);
                }
            }

            if (nextGen.Count > 0)
                result[gen * directionSign] = nextGen;

            currentGen = nextGen;
        }
    }

    private IEnumerable<string> GetAncestorEntities(
        string qid,
        IReadOnlySet<string> parentRelationships,
        IReadOnlySet<string> childRelationships)
    {
        if (_outgoing.TryGetValue(qid, out var outList))
        {
            foreach (var (target, rel) in outList)
            {
                if (parentRelationships.Contains(rel))
                    yield return target;
            }
        }

        if (_incoming.TryGetValue(qid, out var inList))
        {
            foreach (var (source, rel) in inList)
            {
                if (childRelationships.Contains(rel))
                    yield return source;
            }
        }
    }

    private IEnumerable<string> GetDescendantEntities(
        string qid,
        IReadOnlySet<string> parentRelationships,
        IReadOnlySet<string> childRelationships)
    {
        if (_outgoing.TryGetValue(qid, out var outList))
        {
            foreach (var (target, rel) in outList)
            {
                if (childRelationships.Contains(rel))
                    yield return target;
            }
        }

        if (_incoming.TryGetValue(qid, out var inList))
        {
            foreach (var (source, rel) in inList)
            {
                if (parentRelationships.Contains(rel))
                    yield return source;
            }
        }
    }
}
