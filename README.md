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

Current release: `2.6.0`

Current validation: 113 unit tests + 80 live integration tests = 193 total.

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
var results = await reconciler.Reconcile.ReconcileAsync("Douglas Adams");

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
Tune scoring, language, type hierarchy, provider-safe host limits, response caching, diagnostics, and HTTP behavior.

[Configuration guide](docs/configuration.md) — all options, caching, custom HttpClient, custom Wikibase instances

## Architecture

The reconciliation pipeline has four stages: dual search, entity fetching, weighted scoring, and type filtering.

[Architecture overview](docs/architecture.md) — pipeline stages, internal components, design decisions

## What's New in v2.6.0

Provider-safety release for high-volume ingestion.

- **One shared Wikimedia HTTP pipeline.** Wikidata, Wikipedia, and Commons-capable calls flow through the same internal sender for per-host throttling, retries, `Retry-After`, request logging, response-cache hooks, and diagnostics.
- **Safe defaults for Wikidata.** `www.wikidata.org` defaults to one in-flight request and low request pacing. Wikipedia and Commons have separate host limiters, and each public API on a `WikidataReconciler` shares the same limiter instances.
- **In-flight request coalescing.** Identical cacheable requests share one network call while the first one is still running, reducing repeated QID/property/summary bursts during ingestion.
- **Built-in response cache abstraction.** `IWikidataResponseCache` and the default `InMemoryWikidataResponseCache` cache successful entity, label/description, sitelink, Wikipedia summary, and Commons-capable responses. Consumers can plug in a durable provider cache later.
- **Batched Wikipedia summaries.** Summary lookups now use batched MediaWiki API calls and preserve QID-to-summary mapping instead of issuing one REST request per article.
- **Typed provider failures and telemetry.** Exhausted retries throw `WikidataProviderException` with `WikidataFailureKind`, while `reconciler.Diagnostics.GetSnapshot()` reports request counts, cache hits/misses, throttling waits, 429s, retries, batch sizes, average latency, and typed data failures such as missing sitelinks.

Validation for this release: 113 unit tests and 80 live integration tests.

## What's New in v2.5.0

Minor release. The main theme is that the documented capabilities now line up with the real runtime behavior.

- **Chained property constraints are now real end to end.** `new PropertyConstraint("P131/P17", "Q145")` now walks the intermediate entity path during scoring instead of only inspecting the root property.
- **Shared HTTP reliability path.** Wikidata and Wikipedia calls now run through one `ResilientHttpClient` that applies the real outbound `MaxConcurrency` limit, retries transient 408/429/5xx failures, and honors `Retry-After` when the API sends it.
- **`LabelsService` semantics are now documented accurately.** `GetBatchAsync` uses `null` for "entity exists but has no label in that language" and omits keys only when the entity is missing or the input is invalid.
- **Cancellation now propagates correctly.** `WikipediaService` and `AuthorsService` no longer swallow `OperationCanceledException` during soft-fail paths.
- **Long recent-change windows now paginate.** `GetRecentChangesAsync` follows continuation tokens instead of silently truncating after the first 500 rows.
- **Public hints that existed on paper now affect results.** `AuthorResolutionRequest.WorkQidHint`, `PersonSearchRequest.TitleHint`, and `CustomChildTraversal.OrdinalProperty` are all wired into runtime behavior.
- **Stage 2 is smarter and lighter.** Music artist strings now resolve through `PersonsService`, text author strings resolve through `AuthorsService`, bridge-ID resolution skips duplicate lookups, and batch work fans out in parallel while still respecting the shared HTTP limiter.
- **Search fan-out is cheaper.** Multi-language reconciliation now runs full-text search once per query and only fans out the autocomplete path per language.
- **Graph family trees have stricter semantics.** Ancestors now mean outgoing parent edges or incoming child edges; descendants mean outgoing child edges or incoming parent edges.

Validation for this release: 103 unit tests and 80 live integration tests.

## What's New in v2.4.0

Additive release. Closes out pseudonym resolution across all three Wikidata modeling patterns:

- **Solo pen name reverse lookup (Pattern 1)** — looking up "Richard Bachman" now resolves to Stephen King (Q39829) via `haswbstatement:P742`. Both `ResolvedAuthor.Qid` and `ResolvedAuthor.RealNameQid` are populated with the real author's QID.
- **Collective pseudonym expansion (Pattern 3)** — looking up "James S.A. Corey" resolves to the collective pseudonym entity (Q6142591) and populates the new `ResolvedAuthor.RealAuthors` list with Daniel Abraham and Ty Franck. The library walks P527 (has part) on any entity whose P31 includes a pseudonym class (Q16017119, Q4647632, etc.), with Q5 type checking on the parts to filter out non-person members.
- **Pen-name enumeration (Pattern 2)** stayed the same — looking up "Stephen King" still returns his P742 values in `ResolvedAuthor.Pseudonyms` (shipped in v2.3).

```csharp
// Pattern 3: collective pseudonym
var result = await reconciler.Authors.ResolveAsync(new AuthorResolutionRequest
{
    RawAuthorString = "James S.A. Corey",
    DetectPseudonyms = true
});

var author = result.Authors[0];
Console.WriteLine($"Pseudonym entity: {author.Qid}");
foreach (var real in author.RealAuthors!)
    Console.WriteLine($"  real author: {real.CanonicalName} ({real.Qid})");
```

New DTO: `RealAuthor` (lightweight `Qid` + `CanonicalName` ref). Unit tests expanded to 92, integration tests to 37.

## What's New in v2.3.0

Additive release closing out the library behavior gaps identified during v2.0–v2.2 integration testing.

- **`PersonsService` musical-group scoring fix.** The P106 (occupation) constraint is now skipped when `IncludeMusicalGroups` is effectively true, so Performer/Artist role searches resolve groups like Daft Punk and Radiohead above the default `AcceptThreshold = 0.80` without workarounds.
- **`PersonSearchRequest.CompanionNameHints` re-ranking is live.** The v2.1 structural signal is now wired to scoring — candidates get +10 per companion hint that fuzzy-matches one of their P800 (notable work) labels. One extra batch round-trip when hints are set; no-op otherwise.
- **`LabelsService.GetBatchAsync` pre-filters malformed QIDs.** Previously Wikidata's `wbgetentities` would reject the whole batch if any single input was malformed, silently dropping every label. The service now filters to syntactically-valid QIDs before calling the API.
- **`ResolvedAuthor.Pseudonyms`** — new field exposing P742 (pseudonym) string values on the resolved author. Looking up "Stephen King" with `DetectPseudonyms = true` now returns `["Richard Bachman"]`.
- **`Stage2Service.PickBestEdition` uses epsilon comparison** for score tiebreaking instead of strict float equality.

Integration test coverage expanded to 34 live-Wikidata tests (from 28), unit tests to 88 (from 85).

## What's New in v2.2.0

Additive release — no breaking changes. Completes the original plan's primitive expansion.

- **`reconciler.Stage2.ResolveBatchAsync(...)`** — unified Stage 2 resolver for bridge IDs, music albums, and type-filtered text reconciliation. Uses a discriminated marker-interface hierarchy (`BridgeStage2Request`, `MusicStage2Request`, `TextStage2Request`) so the strategy is chosen at compile time, not guessed at runtime. Groups identical requests by natural key across a batch. Supports edition ↔ work pivoting via `EditionPivotRule` with fuzzy-match ranking hints.

```csharp
var bridge = Stage2Request.Bridge(
    correlationKey: "book-42",
    bridgeIds: new Dictionary<string, string> { ["isbn13"] = "9780441172719" },
    wikidataProperties: new Dictionary<string, string> { ["isbn13"] = "P212" },
    editionPivot: new EditionPivotRule
    {
        WorkClasses = ["Q7725634"],
        EditionClasses = ["Q3331189", "Q122731938"]
    });

var text = Stage2Request.Text("tv-12", "Breaking Bad", ["Q5398426"]);

var results = await reconciler.Stage2.ResolveBatchAsync([bridge, text]);
```

The plan's original v1.1.0 primitive expansion is now fully landed across v2.0, v2.1, and v2.2.

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

`WikidataReconciler` is now a thin facade that exposes focused sub-services as properties. Each sub-service owns a slice of the API and can be injected independently in DI. All v1 top-level methods remain as delegating shims, so existing call sites compile unchanged.

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
