# AGENTS.md

## Project Overview

**Tuvima.Wikidata** is a .NET library for working with Wikidata and Wikipedia. It matches text (names, titles, places) to Wikidata entities, fetches structured data, retrieves Wikipedia content, and provides lightweight in-memory entity graph traversal. The reconciliation algorithms are based on [openrefine-wikibase](https://github.com/wetneb/openrefine-wikibase) (Python, MIT), independently re-implemented in C#. The graph module was inspired by dotNetRDF usage patterns, reimplemented as dependency-free adjacency list traversals.

Two NuGet packages:
- `Tuvima.Wikidata` — core library, zero external dependencies
- `Tuvima.Wikidata.AspNetCore` — W3C Reconciliation API middleware for ASP.NET Core

## Architecture (v3.0.0)

`WikidataReconciler` is a thin **facade** that owns a shared `ReconcilerContext` (HttpClient, options, search/fetcher/scorer/type-checker collaborators, shared provider-safe HTTP pipeline, response cache hook, diagnostics, and per-host limiters) and exposes focused **sub-services** as properties:

```
WikidataReconciler (facade, owns ReconcilerContext)
├── Reconcile    → ReconciliationService   (reconcile + batch + stream + suggest)
├── Entities     → EntityService           (entities, properties, external ID, labels, images, revisions, changes)
├── Wikipedia    → WikipediaService        (URLs, summaries, sections, subsection extraction)
├── Editions     → EditionService          (P747 editions, P629 work-for-edition)
├── Children     → ChildrenService         (generic TraverseChildrenAsync, ChildEntityManifest builder)
├── Authors      → AuthorsService          (multi-author split + pen-name resolution)
├── Labels       → LabelsService           (single + batch label lookup with fallback chain)
├── Persons      → PersonsService          (role-aware person search with occupation filtering, year/work hints, group expansion)  [v2.1]
└── Bridge       → BridgeResolutionService (bridge IDs, ranked candidates, canonical rollups, relationships, diagnostics)  [v3.0]

Shared internals (Tuvima.Wikidata.Internal):
├── ReconcilerContext           <- shared state for all sub-services
├── WikidataSearchClient        <- dual search: wbsearchentities + full-text
├── WikidataEntityFetcher       <- wbgetentities in provider-safe chunks up to 50, rank-aware
├── ReconciliationScorer        <- weighted label + property scoring
├── TypeChecker                 <- P31 matching + optional P279 subclass walking
│   └── SubclassResolver        <- BFS P279 walker with in-memory cache
├── ResilientHttpClient         <- shared HTTP pipeline: host throttles, retries, Retry-After, cache, coalescing
├── HostRateLimiterRegistry     <- one limiter instance per provider host
├── ProviderRequest             <- canonical request/cache keys and cacheability policy
├── ProviderJson                <- malformed JSON -> typed provider failures
├── EntityMapper                <- internal JSON DTO -> public model mapping
├── FuzzyMatcher                <- token-sort-ratio (Levenshtein-based)
├── PropertyMatcher             <- type-specific matching (items, dates, quantities, coords, URLs)
├── PropertyPath                <- chained paths like "P131/P17"
└── LanguageFallback            <- "de-ch" -> "de" -> "mul" -> "en"

EntityGraph (graph module — Tuvima.Wikidata.Graph namespace)
├── GraphNode                   <- entity node input model (Qid, Label, Type, WorkQids)
├── GraphEdge                   <- relationship edge input model (SubjectQid, Relationship, ObjectQid)
└── EntityGraph                 <- adjacency list graph with BFS traversal methods

Direction enum (Tuvima.Wikidata root namespace, shared by Graph + ChildrenService)
```

All v1 top-level methods on `WikidataReconciler` remain as delegating shims forwarding to the owning sub-service, so v1 call sites keep working. New code should prefer the sub-service properties.

### Reconciliation Pipeline (4 stages)

1. **Dual Search** — `wbsearchentities` (autocomplete) and `action=query&list=search` (full-text) run concurrently, results merged with full-text first. When types are specified, a CirrusSearch `haswbstatement:P31=QID` query also runs for better type recall. Multi-language search runs full-text once per query and fans out only the autocomplete path per language. Diacritic-insensitive mode adds ASCII-normalized search variants.
2. **Entity Fetching** — `wbgetentities` batched (max 50), fetches labels/descriptions/aliases/claims with language fallback. Optionally includes sitelinks for display-friendly label matching.
3. **Scoring** — `score = (label_score * 1.0 + sum(prop_score * 0.4)) / (1.0 + 0.4 * num_properties)`. Type penalty halves score if type requested but entity has no P31. Unique ID shortcut sets score to 100 on exact authority ID match. Diacritic-insensitive scoring strips accents before comparison. Multi-value constraints average the best match score for each constraint value (e.g., 2 of 2 authors match = full score, 1 of 2 = half). Chained property constraints like `P131/P17` are resolved end to end by fetching the intermediate entities and scoring the terminal values.
4. **Type Filtering** — Direct P31 match (multi-type OR logic) or P279 subclass walk (configurable depth, per-request override). Sort by score desc, QID number asc as tiebreaker.

## Project Structure

```
src/
├── Tuvima.Wikidata/                         # Core library
│   ├── WikidataReconciler.cs                # Facade — owns ReconcilerContext, exposes 9 sub-services
│   ├── Direction.cs                         # Enum: Outgoing, Incoming (shared with Graph module)
│   ├── WikidataReconcilerOptions.cs         # Configuration: scoring, language, host rate limits, retry/cache/diagnostics
│   ├── ProviderRateLimitOptions.cs          # Per-host MaxConcurrentRequests, RequestsPerSecond, MaxBatchSize
│   ├── IWikidataResponseCache.cs            # Pluggable raw-response cache hook + canonical cache key
│   ├── InMemoryWikidataResponseCache.cs     # Default process-local response cache
│   ├── WikidataDiagnostics.cs               # Request/cache/throttle/retry/batch/failure telemetry snapshots
│   ├── WikidataFailureKind.cs               # Typed failure categories
│   ├── WikidataProviderException.cs         # Typed provider exception for exhausted/rejected provider calls
│   ├── WikidataFailure.cs                   # Recent typed failure record for diagnostics
│   ├── WikidataHttpLogEntry.cs              # Request logging callback event shape
│   ├── ReconciliationRequest.cs             # Query, Types, ExcludeTypes, Properties, Language/Languages, Limit, DiacriticInsensitive, Cleaners, TypeHierarchyDepth
│   ├── ReconciliationResult.cs              # Id, Name, Description, Score, Match, Types, Breakdown
│   ├── ScoreBreakdown.cs                    # LabelScore, PropertyScores, TypeMatched, UniqueIdMatch
│   ├── SuggestResult.cs                     # Id, Name, Description
│   ├── PropertyConstraint.cs                # PropertyId, Values (always plural in v2)
│   ├── WikidataEntityInfo.cs                # Id, Label, Description, Aliases, Claims, LastRevisionId, Modified
│   ├── WikidataClaim.cs                     # PropertyId, Rank, Value, Qualifiers, QualifierOrder
│   ├── WikidataValue.cs                     # Kind, RawValue, EntityId, EntityLabel, TimePrecision, Amount, Unit, Lat, Lng
│   ├── EntityRevision.cs                    # EntityId, RevisionId, Timestamp (lightweight staleness check)
│   ├── EntityChange.cs                      # EntityId, ChangeType, Timestamp, User, Comment, RevisionId
│   ├── WikipediaSummary.cs                  # EntityId, Title, Extract, Description, ThumbnailUrl, ArticleUrl, Language
│   ├── WikipediaSection.cs                  # Title, Index, Level, Number, Anchor (TOC entry)
│   ├── ChildEntityInfo.cs                   # EntityId, Label, Description, Ordinal, Properties (TraverseChildrenAsync result shape)
│   ├── ChildEntityRequest.cs                # Manifest-builder input: ParentQid, Kind, MaxPrimary, MaxTotal, Language, CustomTraversal
│   ├── ChildEntityKind.cs                   # Preset enum: TvSeasonsAndEpisodes | MusicTracks | ComicIssues | BookSequels | Custom
│   ├── CustomChildTraversal.cs              # Escape hatch for ChildEntityKind.Custom (RelationshipProperty, Direction, ChildTypeFilter, OrdinalProperty, CreatorRoles)
│   ├── ChildEntityManifest.cs               # Structured output: ParentQid, PrimaryCount, TotalCount, Children
│   ├── ChildEntityRef.cs                    # Single child: Qid, Title, Ordinal, Parent, ReleaseDate, Duration, Creators
│   ├── EditionInfo.cs                       # EntityId, Label, Description, Types, Claims (P747 edition data)
│   ├── AuthorResolutionRequest.cs           # Authors.ResolveAsync input: RawAuthorString, WorkQidHint, Language, DetectPseudonyms
│   ├── AuthorResolutionResult.cs            # Authors.ResolveAsync output: Authors, UnresolvedNames
│   ├── ResolvedAuthor.cs                    # Per-author result: OriginalName, Qid, CanonicalName, RealNameQid (v2.4 Pattern 1), Pseudonyms (v2.3 Pattern 2), RealAuthors (v2.4 Pattern 3), Confidence
│   ├── RealAuthor.cs                        # Lightweight real-author ref used in ResolvedAuthor.RealAuthors: Qid, CanonicalName (v2.4)
│   ├── PersonRole.cs                        # Enum for Persons.SearchAsync: Author|Narrator|Director|Actor|VoiceActor|Composer|Performer|Artist|Screenwriter (v2.1)
│   ├── PersonSearchRequest.cs               # Persons.SearchAsync input: Name, Role, TitleHint, WorkQid, IncludeMusicalGroups, BirthYearHint, DeathYearHint, CompanionNameHints, ExpandGroupMembers, AcceptThreshold (v2.1)
│   ├── PersonSearchResult.cs                # Persons.SearchAsync output: Found, Qid, CanonicalName, IsGroup, Score, Occupations, NotableWorks, GroupMembers (v2.1)
│   ├── BridgeResolutionRequest.cs           # High-level bridge/identity request (v3.0)
│   ├── BridgeResolutionResult.cs            # One result per input with selected/ranked candidates, failure, diagnostics (v3.0)
│   ├── BridgeCandidate.cs                   # Ranked candidate shape with QID, labels, matched property, confidence, reasons (v3.0)
│   ├── CanonicalRollup.cs                   # Resolved entity QID, canonical work QID, relationship path (v3.0)
│   ├── BridgeSeriesInfo.cs                  # Series/order extraction output (v3.0)
│   ├── BridgeRelationshipEdge.cs            # Auditable relationship edges with property IDs (v3.0)
│   ├── SectionContent.cs                    # Title, Content (structured section content for subsection handling)
│   ├── QueryCleaners.cs                     # Built-in title pre-cleaning functions
│   ├── CachingDelegatingHandler.cs          # Abstract HTTP caching base class
│   ├── Services/                            # Sub-service facade layer (v2.0.0)
│   │   ├── ReconciliationService.cs         # Reconcile, batch, stream, suggest
│   │   ├── EntityService.cs                 # Entities, properties, external ID, labels, images, revisions, changes
│   │   ├── WikipediaService.cs              # URLs, summaries, sections, subsection extraction
│   │   ├── EditionService.cs                # P747 editions, P629 work-for-edition
│   │   ├── ChildrenService.cs               # TraverseChildrenAsync (generic) + GetChildEntitiesAsync (manifest)
│   │   ├── AuthorsService.cs                # ResolveAsync — multi-author split + pen-name detection
│   │   ├── LabelsService.cs                 # GetAsync, GetBatchAsync with language fallback
│   │   ├── PersonsService.cs                # SearchAsync — role-aware person search (v2.1)
│   │   ├── BridgeResolutionService.cs       # ResolveAsync / ResolveBatchAsync high-level identity bridge (v3.0)
│   ├── Graph/                               # Entity graph traversal module
│   │   ├── EntityGraph.cs                   # Core graph class — adjacency lists, BFS pathfinding, family trees
│   │   ├── GraphNode.cs                     # Entity node input model (Qid, Label, Type, WorkQids)
│   │   └── GraphEdge.cs                     # Relationship edge input model (SubjectQid, Relationship, ObjectQid)
│   ├── Properties/
│   │   └── AssemblyInfo.cs                  # InternalsVisibleTo for tests
│   └── Internal/
│       ├── ReconcilerContext.cs             # Shared state holder for facade and all sub-services
│       ├── WikidataSearchClient.cs          # Dual search + suggest + external ID lookup + type-filtered + multi-language
│       ├── WikidataEntityFetcher.cs         # Entity fetching with rank hierarchy + sitelinks + provider-safe chunking
│       ├── ReconciliationScorer.cs          # Weighted scoring formula + unique ID shortcut
│       ├── TypeChecker.cs                   # P31 type matching (sync + async with P279)
│       ├── SubclassResolver.cs              # P279 hierarchy BFS with ConcurrentDictionary cache
│       ├── ResilientHttpClient.cs           # Shared pipeline: retries, Retry-After, maxlag, cache, coalescing, per-host throttles
│       ├── HostRateLimiter.cs               # Per-host concurrency + request pacing primitive
│       ├── HostRateLimiterRegistry.cs       # Host -> limiter registry (Wikidata, Wikipedia, Commons, default)
│       ├── ProviderRequest.cs               # Canonical request/cache key and cacheability policy
│       ├── ProviderJson.cs                  # Source-gen JSON deserialize wrapper with typed malformed-response failures
│       ├── EntityMapper.cs                  # Internal DTO -> public model mapping
│       ├── HtmlTextExtractor.cs             # Lightweight HTML-to-text for Wikipedia parse output
│       ├── FuzzyMatcher.cs                  # Token-sort-ratio string matching + diacritic stripping
│       ├── PropertyMatcher.cs               # Type-specific value matching
│       ├── PropertyPath.cs                  # "P131/P17" chained property resolution
│       ├── LanguageFallback.cs              # Language fallback chain
│       └── Json/
│           ├── WikidataJsonContext.cs        # Source-generated JSON serialization context
│           ├── WbSearchEntitiesResponse.cs   # wbsearchentities API response
│           ├── WbGetEntitiesResponse.cs      # wbgetentities API response (claims, qualifiers, sitelinks)
│           ├── QuerySearchResponse.cs        # Full-text search API response
│           ├── ParseResponse.cs              # action=parse API response (sections, section content)
│           ├── RevisionQueryResponse.cs      # Revision query API response (staleness detection)
│           ├── RecentChangesResponse.cs      # Recent changes API response
│           └── WikipediaSummaryResponse.cs   # Wikipedia summary REST/batch API response
├── Tuvima.Wikidata.AspNetCore/              # ASP.NET Core companion
│   ├── ReconciliationEndpoints.cs           # W3C API endpoints + suggest + preview + W3C models
│   ├── ReconciliationServiceOptions.cs      # Service name, identifier space, default types
│   └── ServiceCollectionExtensions.cs       # AddWikidataReconciliation() DI registration
tests/
└── Tuvima.Wikidata.Tests/
    ├── IntegrationTests.cs                  # Live Wikidata API tests (Category=Integration)
    ├── BehaviorFeaturesTests.cs             # Unit tests for TitleHint, WorkQidHint, ordinal paths, chained property paths, multi-language shaping
    ├── FacadeShapeTests.cs                  # Facade contract tests (v2.0.0: sub-service exposure, DTO shapes)
    ├── AuthorsSplitterTests.cs              # Unit tests for AuthorsService.SplitAuthors
    ├── EntityGraphTests.cs                  # Unit tests for the graph module
    ├── FuzzyMatcherTests.cs                 # Unit tests for fuzzy matching
    ├── PropertyMatcherTests.cs              # Unit tests for property matching
    ├── LanguageFallbackTests.cs             # Unit tests for language fallback
    ├── ResilienceAndStage2Tests.cs          # Unit tests for cancellation and pagination
    ├── BridgeResolutionServiceTests.cs      # Unit tests for bridge batching, ranking, rollups, and Wikipedia summary failures
    ├── TestHttpMessageHandler.cs            # Test HTTP shim for deterministic service tests
    └── TestPayloads.cs                      # Shared JSON payload builders for service tests
docs/
├── reconciliation.md                        # Reconciliation usage guide
├── entity-data.md                           # Entity data & Wikipedia content guide
├── graph.md                                 # Graph module guide
├── aspnetcore.md                            # ASP.NET Core integration guide
├── configuration.md                         # Configuration options guide
├── architecture.md                          # Architecture overview
├── migrating-to-v2.md                       # v1 → v2.0.0 migration guide
└── changelog.md                             # Version history
```

## Public API Reference

New code should call sub-services via `reconciler.{Service}.{Method}(...)`. All top-level methods listed here remain on `WikidataReconciler` as delegating shims for v1 source-compat.

### `reconciler.Reconcile` — `ReconciliationService`

| Method | Purpose |
|---|---|
| `ReconcileAsync(query)` | Match text to Wikidata entities |
| `ReconcileAsync(query, type)` | Match with type filter (e.g., "Q5" for humans) |
| `ReconcileAsync(ReconciliationRequest)` | Full options: `Types` (plural only in v2), properties, language/languages, limit, exclude types, diacritics, cleaners |
| `ReconcileBatchAsync(requests)` | Parallel batch with concurrency limiting |
| `ReconcileBatchStreamAsync(requests)` | `IAsyncEnumerable` — yields results as they complete |
| `SuggestAsync(prefix)` | Entity autocomplete |
| `SuggestPropertiesAsync(prefix)` | Property autocomplete |
| `SuggestTypesAsync(prefix)` | Type/class autocomplete |

### `reconciler.Entities` — `EntityService`

| Method | Purpose |
|---|---|
| `GetEntitiesAsync(qids)` | Full entity data with claims and qualifiers |
| `GetEntitiesAsync(qids, resolveEntityLabels)` | Full entity data with auto-resolved entity reference labels |
| `GetPropertiesAsync(qids, propertyIds)` | Specific properties with auto-resolved entity labels |
| `LookupByExternalIdAsync(propertyId, value)` | Find entity by ISBN/IMDB/VIAF/ORCID via haswbstatement |
| `GetPropertyLabelsAsync(propertyIds)` | P569 → "date of birth" |
| `GetImageUrlsAsync(qids)` | Wikimedia Commons image URLs from P18 claims |
| `GetRevisionIdsAsync(qids)` | Lightweight staleness check — returns only revision IDs and timestamps |
| `GetRecentChangesAsync(qids, since)` | Detailed entity change history for audit/monitoring, with continuation handling for long windows |

### `reconciler.Wikipedia` — `WikipediaService`

| Method | Purpose |
|---|---|
| `GetWikipediaUrlsAsync(qids)` | QID → Wikipedia article URL via sitelinks |
| `GetWikipediaSummariesAsync(qids)` | Wikipedia article summaries (extract, thumbnail, URL) |
| `GetWikipediaSummariesAsync(qids, lang, fallbacks)` | Wikipedia summaries with language fallback |
| `GetWikipediaSectionsAsync(qids)` | Wikipedia article table of contents |
| `GetWikipediaSectionContentAsync(qid, index)` | Specific Wikipedia section as plain text |
| `GetWikipediaSectionWithSubsectionsAsync(qid, index)` | Section + subsections as structured list of `SectionContent` |

### `reconciler.Editions` — `EditionService`

| Method | Purpose |
|---|---|
| `GetEditionsAsync(workQid, filterTypes?)` | Fetch editions/translations (P747) of a work entity |
| `GetWorkForEditionAsync(editionQid)` | Find parent work (P629) from an edition |

### `reconciler.Children` — `ChildrenService`

| Method | Purpose |
|---|---|
| `TraverseChildrenAsync(parentQid, property, direction)` | Generic parent → child traversal. `Direction.Outgoing` (default) follows the property forward; `Direction.Incoming` finds entities whose property points to parent. Replaces v1 `GetChildEntitiesAsync(string, string, ...)` and the `^P` reverse-prefix convention. |
| `GetChildEntitiesAsync(ChildEntityRequest)` | **NEW.** Builds a structured `ChildEntityManifest` from presets (`TvSeasonsAndEpisodes`, `MusicTracks`, `ComicIssues`, `BookSequels`, `Custom`). Honours `MaxPrimary`, `MaxTotal`, and `CustomChildTraversal.OrdinalProperty`. |

### `reconciler.Authors` — `AuthorsService`

| Method | Purpose |
|---|---|
| `ResolveAsync(AuthorResolutionRequest)` | **NEW.** Splits multi-author strings (handles `" and "`, `" & "`, `"; "`, `", "`, `" with "`, `"、"`, and "Last, First" form), reconciles each name against Q5, optionally applies `WorkQidHint` as `P800` bibliography context, optionally detects pen names via P742, and captures trailing `et al.` in `UnresolvedNames`. Replaces v1 `GetAuthorPseudonymsAsync` + `PseudonymInfo`. |

### `reconciler.Labels` — `LabelsService`

| Method | Purpose |
|---|---|
| `GetAsync(qid, language, withFallbackLanguage)` | **NEW.** Single-entity label lookup with optional language fallback chain. |
| `GetBatchAsync(qids, language, withFallbackLanguage)` | **NEW.** Batch variant returning `IReadOnlyDictionary<string, string?>` — every valid input QID is present in the result dictionary (`null` means the entity exists but has no label in the requested language; an absent key means the entity was missing or the input was invalid). |

### `reconciler.Persons` — `PersonsService` (v2.1)

| Method | Purpose |
|---|---|
| `SearchAsync(PersonSearchRequest)` | **NEW.** Role-aware person search. Reconciles against Q5 (human) + optionally Q215380/Q5741069 (musical groups). Uses an internal `FrozenDictionary<PersonRole, string[]>` to map roles (`Author`, `Narrator`, `Director`, `Actor`, `VoiceActor`, `Composer`, `Performer`, `Artist`, `Screenwriter`) to canonical P106 occupation QIDs. `IncludeMusicalGroups` is `bool?` with per-role defaults (`Performer` and `Artist` default to true). `BirthYearHint`, `DeathYearHint`, and `WorkQid` feed property constraints; `TitleHint` now feeds the notable-work reranking path when explicit `CompanionNameHints` are absent. When `ExpandGroupMembers` is true and the hit is a group, populates `GroupMembers` from P527. |

### `reconciler.Bridge` — `BridgeResolutionService` (v3.0)

| Method | Purpose |
|---|---|
| `ResolveAsync(BridgeResolutionRequest)` | Resolves one bridge/identity request. |
| `ResolveBatchAsync(IReadOnlyList<BridgeResolutionRequest>)` | Resolves many bridge/identity requests, grouping identical external-ID lookups by `(propertyId, normalizedValue)` and returning one `BridgeResolutionResult` per correlation key. |

Key design notes:
- The public Stage2 compatibility layer was removed in v3.0. Use one request shape: `BridgeResolutionRequest`.
- Built-in bridge catalog covers Apple, TMDB, IMDb, TVDB, ISBN, OpenLibrary, Google Books, MusicBrainz, ComicVine, Goodreads, ASIN, and custom caller-supplied property keys.
- Results include selected/ranked candidates, typed success/failure, reason codes, warnings, provider diagnostics, canonical P629/P747 rollup path, series/order data, and relationship edges.
- Tuvima.Wikidata does not call retail APIs; it only uses supplied bridge identifiers as Wikidata external-ID values.

### `reconciler.Diagnostics` — `WikidataDiagnostics` (v2.6)

| Method | Purpose |
|---|---|
| `GetSnapshot()` | Returns request counts by host/endpoint, cache hits/misses, throttled waits, 429 count, retry count, coalesced request count, batch-size metrics, average latency, and recent typed failures. |

Typed provider failures use `WikidataFailureKind` (`NotFound`, `NoSitelink`, `RateLimited`, `TransientNetworkFailure`, `MalformedResponse`, `Cancelled`). Exhausted HTTP/provider failures throw `WikidataProviderException` with the same kind.

### EntityGraph Methods (Tuvima.Wikidata.Graph)

| Method | Purpose |
|---|---|
| `EntityGraph(nodes, edges)` | Build graph from caller-provided nodes and edges |
| `FindPaths(fromQid, toQid, maxHops)` | BFS pathfinding — all paths between two entities |
| `GetFamilyTree(characterQid, generations, parentRels, childRels)` | Ancestor/descendant traversal with configurable relationship types. Ancestors = outgoing parent edges or incoming child edges; descendants = outgoing child edges or incoming parent edges |
| `FindCrossMediaEntities(minWorks)` | Entities appearing in 2+ distinct works |
| `GetNeighbors(qid)` | All directly connected entities with relationship and direction |
| `GetSubgraph(centerQid, radius)` | Extract ego graph around an entity |
| `NodeCount` | Total nodes in the graph |
| `EdgeCount` | Total directed edges in the graph |

### Configuration Options (WikidataReconcilerOptions)

| Option | Default | Description |
|---|---|---|
| `ApiEndpoint` | Wikidata API | Custom Wikibase endpoint support |
| `Language` | `"en"` | Default search language (overridable per-request) |
| `UserAgent` | Library default | Required by Wikimedia policy |
| `Timeout` | 30s | HTTP request timeout |
| `TypePropertyId` | `"P31"` | Instance-of property (custom Wikibase may differ) |
| `PropertyWeight` | 0.4 | Weight per property match (label = 1.0) |
| `AutoMatchThreshold` | 95 | Score threshold for auto-match |
| `AutoMatchScoreGap` | 10 | Min gap over second-best for auto-match |
| `MaxConcurrency` | 5 | Legacy top-level batch concurrency setting retained for compatibility |
| `MaxRetries` | 3 | Retry attempts for transient 408/429/5xx failures |
| `RetryBaseDelay` | 1s | Base exponential-backoff delay when Retry-After is absent |
| `MaxRetryDelay` | 30s | Cap for exponential backoff when Retry-After is absent |
| `RetryJitterRatio` | 0.2 | Extra jitter applied to exponential backoff delays |
| `MaxLag` | 5 | Wikimedia maxlag parameter (seconds) |
| `WikidataRateLimit` | 1 concurrent / 1 RPS / 50 batch | Host policy for `www.wikidata.org` |
| `WikipediaRateLimit` | 2 concurrent / 2 RPS / 50 batch | Host policy for each `*.wikipedia.org` host |
| `CommonsRateLimit` | 1 concurrent / 1 RPS / 50 batch | Host policy for `commons.wikimedia.org` |
| `DefaultRateLimit` | 1 concurrent / 1 RPS / 50 batch | Host policy for custom Wikibase or unknown hosts |
| `EnableRequestCoalescing` | `true` | Share identical in-flight GET requests |
| `EnableResponseCaching` | `true` | Cache successful cacheable raw provider responses |
| `ResponseCache` | `InMemoryWikidataResponseCache` | Pluggable raw-response cache implementation |
| `ResponseCacheTtl` | 12h | TTL for successful cacheable responses |
| `RequestLogger` | `null` | Optional callback for request/cache/retry/failure log entries |
| `TypeHierarchyDepth` | 0 | P279 subclass walk depth (0 = off) |
| `IncludeSitelinkLabels` | `false` | Include Wikipedia sitelink titles in scoring label pool |
| `UniqueIdProperties` | 13 IDs | Properties that trigger score=100 shortcut |

### ASP.NET Core Endpoints (MapReconciliation)

| Endpoint | Purpose |
|---|---|
| `GET /reconcile` | W3C service manifest |
| `POST /reconcile` | Reconciliation queries (single or batch) |
| `GET /reconcile/suggest/entity?prefix=...` | Entity autocomplete |
| `GET /reconcile/suggest/property?prefix=...` | Property autocomplete |
| `GET /reconcile/suggest/type?prefix=...` | Type autocomplete |
| `GET /reconcile/preview?id=Q42` | HTML preview card |

All endpoints respect the `Accept-Language` header.

## Build & Test

```bash
# Build
dotnet build

# Unit tests only
dotnet test --filter "Category!=Integration"

# Integration tests (requires network, hits live Wikidata API)
dotnet test --filter "Category=Integration"

# All tests
dotnet test

# Pack NuGet packages
dotnet pack --configuration Release
```

Test counts: 113 unit tests + 80 integration tests = 193 total.

## Key Design Decisions

- **Zero external dependencies** — only `System.Text.Json` (built into .NET). No FuzzySharp, no Polly, no caching libraries.
- **AOT compatible** — `IsAotCompatible` and `IsTrimmable` set in .csproj. All JSON serialization uses source-generated `JsonSerializerContext` (no reflection).
- **Response cache hook** — the shared HTTP pipeline includes `IWikidataResponseCache` with an in-memory default; consumers can replace it with a durable cache.
- **Dual search** — both `wbsearchentities` and full-text `action=query&list=search` contribute to recall. Multi-language search runs the full-text pass once per query and fans out only the autocomplete path per language.
- **Claim rank hierarchy** — preferred rank values used if available, then normal, deprecated always excluded.
- **Language fallback chain** — exact -> subtag parent -> "mul" -> "en". API requests include all fallback languages.
- **Provider-safe throttling** — every public API on a reconciler shares the same per-host limiter instances; Wikidata defaults to single-flight low-RPS behavior.
- **Retry behavior** — transient 408/429/5xx failures and transport errors are retried with capped exponential backoff and jitter, and `Retry-After` is honored when present.
- **In-flight request coalescing** — identical active requests share one network call and one response.
- **maxlag parameter** — appended to every Wikidata API request per Wikimedia bot etiquette.
- **Chained property paths** — property constraints like `P131/P17` are resolved end to end during scoring by fetching the intermediate entities and evaluating the terminal claim values.
- **Graph module: no RDF** — adjacency lists and BFS, not RDF/SPARQL. The operations (pathfinding, family trees, cross-media) don't require a graph database engine.

## Wikidata API Endpoints Used

| API | Purpose |
|---|---|
| `wbsearchentities` | Autocomplete search by label/alias |
| `action=query&list=search` | Full-text search across entity content |
| `wbgetentities` | Fetch entity data (labels, descriptions, aliases, claims, sitelinks) |
| Wikipedia `action=query&prop=extracts|pageimages|info|description` | Batched article summaries with thumbnails |
| Wikipedia `action=parse` | Section TOC (tocdata) and section content (text) |
| `action=query&prop=revisions` | Lightweight revision ID lookup for staleness detection |
| `action=query&list=recentchanges` | Entity change monitoring |
| CirrusSearch `haswbstatement:` | External ID reverse lookup + type-filtered search |

## CI/CD

GitHub Actions workflow (`.github/workflows/ci.yml`):
- Build matrix: .NET 8.0 and 10.0
- Unit tests run on every push/PR
- Integration tests run with `continue-on-error` (depend on Wikidata availability)
- NuGet pack as build artifact
- Auto-publish to NuGet on every push to main (requires `NUGET_API_KEY` secret)

## Mandatory Rules

1. **Documentation on every feature change.** Every new public method, property, option, or behavior change MUST be reflected in BOTH `AGENTS.md` (architecture, API reference, project structure) AND `README.md` / relevant `docs/` files. Never ship a feature without updating docs.

2. **Version bump on every feature change.** Any commit that adds, removes, or changes public API surface MUST increment the package version in BOTH `.csproj` files (`Tuvima.Wikidata` and `Tuvima.Wikidata.AspNetCore`). Use semantic versioning:
   - **Patch** (x.y.**Z**) — bug fixes, internal refactors, doc-only changes
   - **Minor** (x.**Y**.0) — new features, new public methods/properties/options, backward-compatible additions
   - **Major** (**X**.0.0) — breaking changes to existing public API

3. **Tests must pass.** Run `dotnet build` (0 warnings, 0 errors) and `dotnet test --filter "Category!=Integration"` (all pass) before committing.

## Attribution

Reconciliation algorithms based on [openrefine-wikibase](https://github.com/wetneb/openrefine-wikibase) by Antonin Delpeuch (MIT). Graph module inspired by [dotNetRDF](https://github.com/dotnetrdf/dotnetrdf) usage patterns (MIT). Independent C# implementation — no code copied from either project. See `NOTICE` file.
