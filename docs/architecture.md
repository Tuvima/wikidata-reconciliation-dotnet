# Architecture

## Component Overview (v2.5.0+)

`WikidataReconciler` is a thin **facade** that owns a shared `ReconcilerContext` (HttpClient, options, collaborator instances, shared resilient request sender, global concurrency limiter) and exposes nine focused **sub-services** as properties. Each sub-service owns a slice of the API surface and can be injected independently in DI without going through the facade.

```
WikidataReconciler (facade, owns ReconcilerContext)
├── Reconcile  → ReconciliationService   reconcile + batch + stream + suggest
├── Entities   → EntityService           entities, properties, external ID, labels, images, revisions, changes
├── Wikipedia  → WikipediaService        URLs, summaries, sections, subsection extraction
├── Editions   → EditionService          P747 editions, P629 work-for-edition
├── Children   → ChildrenService         generic TraverseChildrenAsync + ChildEntityManifest builder
├── Authors    → AuthorsService          multi-author split + pen-name resolution
├── Labels     → LabelsService           single + batch label lookup with fallback chain
├── Persons    → PersonsService          role-aware person search with occupation filtering  [v2.1]
└── Stage2     → Stage2Service           unified bridge/music/text resolver with discriminated requests  [v2.2]

Shared internals (Tuvima.Wikidata.Internal):
├── ReconcilerContext           <- shared state holder for facade and all sub-services
├── WikidataSearchClient        <- dual search: wbsearchentities + full-text
├── WikidataEntityFetcher       <- wbgetentities in batches of 50, rank-aware
├── ReconciliationScorer        <- weighted label + property scoring
├── TypeChecker                 <- P31 matching + optional P279 subclass walking
│   └── SubclassResolver        <- BFS P279 walker with in-memory cache
├── ResilientHttpClient         <- shared request sender: maxlag, retries, Retry-After, real HTTP concurrency cap
├── EntityMapper                <- internal JSON DTO -> public model mapping
├── FuzzyMatcher                <- token-sort-ratio (Levenshtein-based)
├── PropertyMatcher             <- type-specific matching (items, dates, quantities, coords, URLs)
├── PropertyPath                <- chained paths like "P131/P17"
└── LanguageFallback            <- "de-ch" -> "de" -> "mul" -> "en"

EntityGraph (graph module)
├── Adjacency lists             <- outgoing + incoming edge dictionaries
├── BFS pathfinding             <- FindPaths
├── BFS family tree             <- GetFamilyTree
├── LINQ cross-media            <- FindCrossMediaEntities
└── BFS subgraph extraction     <- GetSubgraph
```

All v1 top-level methods on `WikidataReconciler` remain as delegating shims that forward to the owning sub-service, so existing v1 call sites compile unchanged. New code should prefer `reconciler.Reconcile.ReconcileAsync(...)` over `reconciler.ReconcileAsync(...)` for clearer dependencies and DI ergonomics.

## Reconciliation Pipeline (4 Stages)

### 1. Dual Search

Two MediaWiki API searches run concurrently:

- **`wbsearchentities`** (autocomplete): Matches labels and aliases directly. Fast and precise for well-known names.
- **`action=query&list=search`** (full-text): Searches across all entity content. Finds items like "1984" where the label ("Nineteen Eighty-Four") differs from the query.

Results are merged (full-text first, then autocomplete) and deduplicated. When types are specified, a CirrusSearch `haswbstatement:P31=QID` query also runs for better type recall. Multi-language search runs full-text once per query, then fans out only the `wbsearchentities` path per language. Queries are truncated at 250 characters to avoid silent failures from the MediaWiki API.

### 2. Entity Fetching

Candidate entities are fetched via `wbgetentities` in batches of up to 50, retrieving labels, descriptions, aliases, and claims in the requested language with fallback. The library respects the Wikidata statement rank hierarchy:

- **Preferred** rank values are used if available
- **Normal** rank values are used otherwise
- **Deprecated** rank values are always excluded

### 3. Scoring

Each candidate receives a weighted score from 0 to 100:

```
label_score  = max(token_sort_ratio(query, label) for each label and alias)
prop_score_i = max(type_specific_match(query_value, claim_value) for each claim)

score = (label_score * 1.0 + sum(prop_score_i * 0.4)) / (1.0 + 0.4 * num_properties)
```

If a type constraint was specified and the entity has no type claims, the score is halved. The auto-match flag is set on the top result when the score exceeds the threshold and the gap over the second-best candidate is sufficient.

For multi-value constraints, the property score is the average of the best match for each constraint value (e.g., 2 of 2 authors match = full score, 1 of 2 = half). Chained property constraints such as `P131/P17` are resolved end to end by fetching the intermediate entities and scoring the terminal property values.

### 4. Type Filtering

Candidates are checked against the requested type (P31 direct match) and excluded types. With `TypeHierarchyDepth > 0`, the library walks the P279 (subclass of) hierarchy — for example, a "novel" (Q8261) matches a query for "literary work" (Q7725634) because novel is a subclass of literary work. The subclass hierarchy is cached in memory within the reconciler's lifetime.

## Design Decisions

- **Zero external dependencies** — only `System.Text.Json` (built into .NET). No FuzzySharp, no Polly, no caching libraries.
- **AOT compatible** — `IsAotCompatible` and `IsTrimmable` set in .csproj. All JSON serialization uses source-generated `JsonSerializerContext` (no reflection).
- **No built-in cache** — deliberate; avoids stale data issues. Users add caching via `HttpClient` `DelegatingHandler` pattern.
- **Dual search** — both `wbsearchentities` and full-text search contribute to recall, but multi-language queries run the full-text pass only once and fan out only the autocomplete pass per language.
- **Claim rank hierarchy** — preferred rank values used if available, then normal, deprecated always excluded.
- **Language fallback chain** — exact -> subtag parent -> "mul" -> "en". API requests include all fallback languages.
- **Request-level concurrency limiting** — `SemaphoreSlim` gates actual outbound HTTP requests (default 5), not just top-level batch items, so every service path shares the same cap.
- **Retry behavior** — transient `408`/`429`/`5xx` failures and transport errors are retried with backoff, and `Retry-After` is honored when the remote API asks the client to wait.
- **maxlag parameter** — appended to every Wikidata API request per Wikimedia bot etiquette.
- **Graph module: no RDF** — the graph module uses adjacency lists and BFS, not RDF/SPARQL. The operations (pathfinding, family trees, cross-media detection) don't require a full graph database.
- **Facade + sub-services (v2.0)** — the root `WikidataReconciler` is a thin facade over nine focused sub-services. The shared `ReconcilerContext` ensures all services use the same HttpClient, options, resilient request sender, and global concurrency limiter. Sub-services are constructed once at facade init and exposed as properties; they are also registered individually by `AddWikidataReconciliation()` so DI consumers can inject a narrow slice.
- **Discriminated Stage 2 requests (v2.2)** — the Stage 2 resolver uses a marker interface `IStage2Request` with three sealed concrete implementations (`BridgeStage2Request`, `MusicStage2Request`, `TextStage2Request`) instead of a single struct with mutually-exclusive fields. The strategy is the type; illegal combinations are unrepresentable; `TextStage2Request.CirrusSearchTypes` is `required` and validated non-empty at resolve time.

## Wikidata API Endpoints Used

| API | Purpose |
|---|---|
| `wbsearchentities` | Autocomplete search by label/alias |
| `action=query&list=search` | Full-text search across entity content |
| `wbgetentities` | Fetch entity data (labels, descriptions, aliases, claims, sitelinks) |
| Wikipedia REST API `/page/summary/` | Article summaries with thumbnails |
| Wikipedia `action=parse` | Section TOC (tocdata) and section content (text) |
| `action=query&prop=revisions` | Lightweight revision ID lookup for staleness detection |
| `action=query&list=recentchanges` | Entity change monitoring |
| CirrusSearch `haswbstatement:` | External ID reverse lookup + type-filtered search |
