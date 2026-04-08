# Tuvima.Wikidata

A .NET library for working with [Wikidata](https://www.wikidata.org/) and [Wikipedia](https://www.wikipedia.org/). It matches text to Wikidata entities, fetches structured data, retrieves Wikipedia content, and provides lightweight in-memory entity graph traversal — all with zero external dependencies.

**In plain English:** You have author names, book titles, or company names. This library figures out which Wikidata item each one refers to, gives you a confidence score, and then lets you enrich your data with everything Wikidata and Wikipedia know about those entities. It can also map relationships between entities — family trees, connection paths, and cross-media appearances — without needing a heavy graph database.

This is the first .NET Wikidata reconciliation library, filling a gap in the ecosystem where only Python and JavaScript implementations previously existed.

## Who Is This For?

- **Data engineers** cleaning and linking datasets to structured identifiers
- **App developers** building search, autocomplete, or knowledge-powered features
- **Library/archive systems** matching catalog records to authority files (VIAF, ISNI, LoC)
- **Research teams** enriching study data with Wikidata's 100M+ items
- **Content platforms** pulling plot summaries, biographies, or descriptions from Wikipedia
- **Media applications** traversing entity relationships — family trees, character connections, cross-media appearances
- **Anyone** who needs to go from messy text to structured, linked data

## What Can It Do?

| You have... | The library gives you... |
|---|---|
| A name like "Douglas Adams" | The Wikidata ID (Q42), confidence score, and auto-match flag |
| A matched entity (Q42) | Date of birth, nationality, works, identifiers, Wikipedia link, profile image |
| A Wikipedia article | Section table of contents, and any section's content as plain text |
| An ISBN or IMDB ID | The matching Wikidata entity, without fuzzy matching |
| A list of 10,000 names | Parallel batch processing with progress streaming |
| A prefix like "Doug..." | Autocomplete suggestions for interactive UIs |
| A name with diacritics like "Shogun" | Matches regardless of accents with diacritic-insensitive mode |
| A work like "Hitchhiker's Guide" | All editions and translations, filterable by type (audiobook, paperback, etc.) |
| A query in Japanese and English | Multi-language search that finds the best match across both languages |
| Cached entity data | Lightweight staleness check — only re-fetch what actually changed |
| A set of related entities | Pathfinding, family trees, and cross-media entity detection via in-memory graph |

## Packages

| Package | Purpose |
|---|---|
| [`Tuvima.Wikidata`](https://www.nuget.org/packages/Tuvima.Wikidata) | Core library — reconciliation, entity data, Wikipedia content, graph traversal |
| [`Tuvima.Wikidata.AspNetCore`](https://www.nuget.org/packages/Tuvima.Wikidata.AspNetCore) | ASP.NET Core middleware for hosting a W3C Reconciliation Service API |

## Installation

```
dotnet add package Tuvima.Wikidata
```

**Targets:** .NET 8.0 (LTS) and .NET 10.0

**Dependencies:** None beyond `System.Text.Json` (built into .NET). AOT compatible and trimmable.

## Quick Start

```csharp
using Tuvima.Wikidata;

using var reconciler = new WikidataReconciler();

// Match text to a Wikidata entity
var results = await reconciler.ReconcileAsync("Douglas Adams");

Console.WriteLine(results[0].Id);          // "Q42"
Console.WriteLine(results[0].Name);        // "Douglas Adams"
Console.WriteLine(results[0].Description); // "English author and humourist (1952-2001)"
Console.WriteLine(results[0].Score);       // 100
Console.WriteLine(results[0].Match);       // true (confident auto-match)
```

```csharp
using Tuvima.Wikidata.Graph;

// Build an entity graph from your data
var graph = new EntityGraph(nodes, edges);

// Find how two characters are connected
var paths = graph.FindPaths("Q937618", "Q312545");

// Build a family tree
var tree = graph.GetFamilyTree("Q937618", generations: 3);

// Find characters appearing in multiple works
var crossMedia = graph.FindCrossMediaEntities(minWorks: 2);
```

## Features

### Reconciliation
Match text to Wikidata entities with dual-search, fuzzy matching, type filtering, property constraints, and confidence scoring.

[Reconciliation guide](docs/reconciliation.md) — type filtering, property constraints, batch processing, streaming, scoring breakdown

### Entity Data & Wikipedia Content
Fetch structured entity data, Wikipedia summaries and sections, images, revision history, editions, and child entities.

[Entity data guide](docs/entity-data.md) — entity fetching, Wikipedia content, staleness detection, edition discovery, child entities

### Graph Traversal
Lightweight in-memory entity graph for pathfinding, family trees, cross-media detection, and subgraph extraction. Pure C# with adjacency lists — no RDF or SPARQL dependencies.

[Graph module guide](docs/graph.md) — building graphs, pathfinding, family trees, cross-media entities, subgraphs

### ASP.NET Core Integration
Host a W3C Reconciliation Service API compatible with OpenRefine and Google Sheets.

[ASP.NET Core guide](docs/aspnetcore.md) — DI registration, endpoint mapping, service manifest

### Configuration
Tune scoring, concurrency, language, type hierarchy, and HTTP behavior.

[Configuration guide](docs/configuration.md) — all options, caching, custom HttpClient, custom Wikibase instances

## Architecture

The reconciliation pipeline has four stages: dual search, entity fetching, weighted scoring, and type filtering.

[Architecture overview](docs/architecture.md) — pipeline stages, internal components, design decisions

## What's New in v2.1.0

Additive release — no breaking changes.

- **`reconciler.Persons.SearchAsync(...)`** — role-aware person search with an internal role → occupation mapping table. Nine `PersonRole` values cover author, narrator, director, actor, voice actor, composer, performer, artist, and screenwriter. `Performer` and `Artist` roles automatically include Q215380 (musical group) + Q5741069 (ensemble) in the type filter. Year and work hints feed property constraints; musical groups can be expanded to their P527 members on demand.

```csharp
var result = await reconciler.Persons.SearchAsync(new PersonSearchRequest
{
    Name = "Daft Punk",
    Role = PersonRole.Performer,   // defaults IncludeMusicalGroups = true
    ExpandGroupMembers = true
});

if (result.Found && result.IsGroup)
{
    Console.WriteLine($"Group {result.CanonicalName} has {result.GroupMembers?.Count ?? 0} members");
}
```

**Deferred to v2.2.0:** the Stage 2 resolver with discriminated `IStage2Request` hierarchy, edition pivoting, and bridge/music/text batch grouping.

## What's New in v2.0.0

`WikidataReconciler` is now a thin facade that exposes seven focused sub-services as properties. Each sub-service owns a slice of the API and can be injected independently in DI. All v1 top-level methods remain as delegating shims, so existing call sites compile unchanged.

```csharp
using var reconciler = new WikidataReconciler();

// Reconciliation + suggest
var matches = await reconciler.Reconcile.ReconcileAsync("Douglas Adams", "Q5");

// Entity + Wikipedia data
var entity = await reconciler.Entities.GetEntitiesAsync(["Q42"]);
var summaries = await reconciler.Wikipedia.GetWikipediaSummariesAsync(["Q42"]);

// NEW: structured label lookup
var label = await reconciler.Labels.GetAsync("Q42", language: "de");

// NEW: multi-author string resolution with pen-name detection
var authors = await reconciler.Authors.ResolveAsync(new AuthorResolutionRequest
{
    RawAuthorString = "Neil Gaiman & Terry Pratchett",
    DetectPseudonyms = true
});

// NEW: structured TV/music/comic/book manifest builder
var seasons = await reconciler.Children.GetChildEntitiesAsync(new ChildEntityRequest
{
    ParentQid = "Q3577037",                // Breaking Bad
    Kind = ChildEntityKind.TvSeasonsAndEpisodes,
    MaxPrimary = 10,
    MaxTotal = 200
});
```

**Breaking changes** — see [`docs/migrating-to-v2.md`](docs/migrating-to-v2.md) for full migration steps:

- `ReconciliationRequest.Type` (singular) removed — use `Types = ["Q5"]`
- `PropertyConstraint.Value` (singular) removed — use `new PropertyConstraint("P569", "1952-03-11")` or `Values = [...]`
- `GetAuthorPseudonymsAsync` + `PseudonymInfo` deleted — use `Authors.ResolveAsync(...)`
- `GetChildEntitiesAsync(parent, "^P179", ...)` renamed to `Children.TraverseChildrenAsync(parent, "P179", Direction.Incoming, ...)` — the `^P` string prefix is gone, use the `Direction` enum
- `Direction` moved from `Tuvima.Wikidata.Graph` to the root `Tuvima.Wikidata` namespace (enclosing-namespace rule means existing code keeps working)

**Deferred to follow-up releases:**

- v2.1.0 — `Persons.SearchAsync` (role-aware person search, musical group handling, year/companion hints)
- v2.2.0 — `Stage2.ResolveBatchAsync` (unified bridge/music/text resolver with discriminated request types and edition pivoting)

See the [changelog](docs/changelog.md) for the full version history.

## Acknowledgements

The reconciliation algorithms in this library (dual-search strategy, scoring formula, fuzzy matching approach, type checking, and property matching) are based on [openrefine-wikibase](https://github.com/wetneb/openrefine-wikibase) by [Antonin Delpeuch](https://github.com/wetneb), licensed under the MIT License.

> Antonin Delpeuch. "A survey of OpenRefine reconciliation services." [arXiv:1906.08092](https://arxiv.org/abs/1906.08092)

The configurable Wikibase endpoint support was informed by the [nfdi4culture fork](https://gitlab.com/nfdi4culture/openrefine-reconciliation-services/openrefine-wikibase).

The graph module's use cases (pathfinding, family trees, cross-media entity detection) were originally implemented using [dotNetRDF](https://github.com/dotnetrdf/dotnetrdf) in a consuming application. This module provides a lightweight, dependency-free reimplementation of those specific operations.

This is an independent C# implementation. No code was copied from the original projects. See the [NOTICE](NOTICE) file for full attribution details.

## License

MIT. See [LICENSE](LICENSE) for the full text.
