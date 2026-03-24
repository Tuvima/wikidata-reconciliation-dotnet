# CLAUDE.md

## Project Overview

**Tuvima.WikidataReconciliation** is a .NET library that matches text (names, titles, places) to Wikidata entities and fetches structured data from them. It's the first .NET Wikidata reconciliation library. The algorithms are based on [openrefine-wikibase](https://github.com/wetneb/openrefine-wikibase) (Python, MIT), independently re-implemented in C#.

Two NuGet packages:
- `Tuvima.WikidataReconciliation` — core library, zero external dependencies
- `Tuvima.WikidataReconciliation.AspNetCore` — W3C Reconciliation API middleware for ASP.NET Core

## Architecture

```
WikidataReconciler (public entry point)
├── WikidataSearchClient        ← dual search: wbsearchentities + full-text
├── WikidataEntityFetcher       ← wbgetentities in batches of 50, rank-aware
├── ReconciliationScorer        ← weighted label + property scoring
├── TypeChecker                 ← P31 matching + optional P279 subclass walking
│   └── SubclassResolver        ← BFS P279 walker with in-memory cache
├── ResilientHttpClient         ← retry-on-429, exponential backoff, maxlag
├── EntityMapper                ← internal JSON DTO → public model mapping
├── FuzzyMatcher                ← token-sort-ratio (Levenshtein-based)
├── PropertyMatcher             ← type-specific matching (items, dates, quantities, coords, URLs)
├── PropertyPath                ← chained paths like "P131/P17"
└── LanguageFallback            ← "de-ch" → "de" → "mul" → "en"
```

### Reconciliation Pipeline (4 stages)

1. **Dual Search** — `wbsearchentities` (autocomplete) and `action=query&list=search` (full-text) run concurrently, results merged with full-text first
2. **Entity Fetching** — `wbgetentities` batched (max 50), fetches labels/descriptions/aliases/claims with language fallback
3. **Scoring** — `score = (label_score * 1.0 + Σ(prop_score * 0.4)) / (1.0 + 0.4 * num_properties)`. Type penalty halves score if type requested but entity has no P31. Unique ID shortcut sets score to 100 on exact authority ID match.
4. **Type Filtering** — Direct P31 match or P279 subclass walk (configurable depth). Sort by score desc, QID number asc as tiebreaker.

## Project Structure

```
src/
├── Tuvima.WikidataReconciliation/           # Core library
│   ├── WikidataReconciler.cs                # Main entry point — all public methods
│   ├── WikidataReconcilerOptions.cs         # 13 configuration options
│   ├── ReconciliationRequest.cs             # Query, Type, ExcludeTypes, Properties, Language, Limit
│   ├── ReconciliationResult.cs              # Id, Name, Description, Score, Match, Types, Breakdown
│   ├── ScoreBreakdown.cs                    # LabelScore, PropertyScores, TypeMatched, UniqueIdMatch
│   ├── SuggestResult.cs                     # Id, Name, Description
│   ├── PropertyConstraint.cs                # PropertyId, Value
│   ├── WikidataEntityInfo.cs                # Id, Label, Description, Aliases, Claims
│   ├── WikidataClaim.cs                     # PropertyId, Rank, Value, Qualifiers, QualifierOrder
│   ├── WikidataValue.cs                     # Kind, RawValue, EntityId, Time, Quantity, Coords + ToDisplayString()
│   ├── EntityChange.cs                      # EntityId, ChangeType, Timestamp, User, Comment, RevisionId
│   ├── WikipediaSummary.cs                  # EntityId, Title, Extract, Description, ThumbnailUrl, ArticleUrl
│   └── Internal/
│       ├── WikidataSearchClient.cs          # Dual search + suggest + external ID lookup
│       ├── WikidataEntityFetcher.cs         # Entity fetching with rank hierarchy + sitelinks
│       ├── ReconciliationScorer.cs          # Weighted scoring formula + unique ID shortcut
│       ├── TypeChecker.cs                   # P31 type matching (sync + async with P279)
│       ├── SubclassResolver.cs              # P279 hierarchy BFS with ConcurrentDictionary cache
│       ├── ResilientHttpClient.cs           # Retry-on-429, exponential backoff, maxlag
│       ├── EntityMapper.cs                  # Internal DTO → public model mapping
│       ├── FuzzyMatcher.cs                  # Token-sort-ratio string matching
│       ├── PropertyMatcher.cs               # Type-specific value matching
│       ├── PropertyPath.cs                  # "P131/P17" chained property resolution
│       ├── LanguageFallback.cs              # Language fallback chain
│       └── Json/
│           ├── WikidataJsonContext.cs        # Source-generated JSON serialization context
│           ├── WbSearchEntitiesResponse.cs   # wbsearchentities API response
│           ├── WbGetEntitiesResponse.cs      # wbgetentities API response (claims, qualifiers, sitelinks)
│           ├── QuerySearchResponse.cs        # Full-text search API response
│           ├── RecentChangesResponse.cs      # Recent changes API response
│           └── WikipediaSummaryResponse.cs   # Wikipedia REST API response
├── Tuvima.WikidataReconciliation.AspNetCore/ # ASP.NET Core companion
│   ├── ReconciliationEndpoints.cs           # W3C API endpoints + suggest + preview + W3C models
│   ├── ReconciliationServiceOptions.cs      # Service name, identifier space, default types
│   └── ServiceCollectionExtensions.cs       # AddWikidataReconciliation() DI registration
tests/
└── Tuvima.WikidataReconciliation.Tests/
    ├── IntegrationTests.cs                  # Live Wikidata API tests (Category=Integration)
    ├── FuzzyMatcherTests.cs                 # Unit tests for fuzzy matching
    ├── PropertyMatcherTests.cs              # Unit tests for property matching
    └── LanguageFallbackTests.cs             # Unit tests for language fallback
```

## Public API Reference

### WikidataReconciler Methods

| Method | Purpose |
|---|---|
| `ReconcileAsync(query)` | Match text to Wikidata entities |
| `ReconcileAsync(query, type)` | Match with type filter (e.g., "Q5" for humans) |
| `ReconcileAsync(ReconciliationRequest)` | Full options: type, properties, language, limit, exclude types |
| `ReconcileBatchAsync(requests)` | Parallel batch with concurrency limiting |
| `ReconcileBatchStreamAsync(requests)` | `IAsyncEnumerable` — yields results as they complete |
| `SuggestAsync(prefix)` | Entity autocomplete |
| `SuggestPropertiesAsync(prefix)` | Property autocomplete (wbsearchentities type=property) |
| `SuggestTypesAsync(prefix)` | Type/class autocomplete |
| `GetEntitiesAsync(qids)` | Full entity data with claims and qualifiers |
| `GetPropertiesAsync(qids, propertyIds)` | Specific properties only |
| `GetWikipediaUrlsAsync(qids)` | QID → Wikipedia article URL via sitelinks |
| `GetWikipediaSummariesAsync(qids)` | Wikipedia article summaries (extract, thumbnail, URL) |
| `LookupByExternalIdAsync(propertyId, value)` | Find entity by ISBN/IMDB/VIAF/ORCID via haswbstatement |
| `GetPropertyLabelsAsync(propertyIds)` | P569 → "date of birth" |
| `GetImageUrlsAsync(qids)` | Wikimedia Commons image URLs from P18 claims |
| `GetRecentChangesAsync(qids, since)` | Entity change monitoring for cache invalidation |

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
| `MaxConcurrency` | 5 | Parallel API requests during batch ops |
| `MaxRetries` | 3 | Retry attempts on HTTP 429 |
| `MaxLag` | 5 | Wikimedia maxlag parameter (seconds) |
| `TypeHierarchyDepth` | 0 | P279 subclass walk depth (0 = off) |
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

Test counts: ~21 unit tests + ~31 integration tests = ~52 total.

## Key Design Decisions

- **Zero external dependencies** — only `System.Text.Json` (built into .NET). No FuzzySharp, no Polly, no caching libraries.
- **AOT compatible** — `IsAotCompatible` and `IsTrimmable` set in .csproj. All JSON serialization uses source-generated `JsonSerializerContext` (no reflection).
- **No built-in cache** — deliberate; avoids stale data issues (upstream issue #146). Users add caching via `HttpClient` `DelegatingHandler` pattern.
- **Dual search** — both `wbsearchentities` and full-text `action=query&list=search` run concurrently. Critical for recall (e.g., "1984" finds the novel whose label is "Nineteen Eighty-Four").
- **Claim rank hierarchy** — preferred rank values used if available, then normal, deprecated always excluded.
- **Language fallback chain** — exact → subtag parent → "mul" → "en". API requests include all fallback languages.
- **Concurrency limiting** — `SemaphoreSlim` gates parallel API requests (default 5) to avoid Wikimedia rate limits.
- **maxlag parameter** — appended to every Wikidata API request per Wikimedia bot etiquette.

## Wikidata API Endpoints Used

| API | Purpose |
|---|---|
| `wbsearchentities` | Autocomplete search by label/alias |
| `action=query&list=search` | Full-text search across entity content |
| `wbgetentities` | Fetch entity data (labels, descriptions, aliases, claims, sitelinks) |
| Wikipedia REST API `/page/summary/` | Article summaries with thumbnails |
| `action=query&list=recentchanges` | Entity change monitoring |
| CirrusSearch `haswbstatement:` | External ID reverse lookup |

## CI/CD

GitHub Actions workflow (`.github/workflows/ci.yml`):
- Build matrix: .NET 8.0 and 10.0
- Unit tests run on every push/PR
- Integration tests run with `continue-on-error` (depend on Wikidata availability)
- NuGet pack as build artifact
- Auto-publish to NuGet on every push to main (requires `NUGET_API_KEY` secret)

## Attribution

Algorithms based on [openrefine-wikibase](https://github.com/wetneb/openrefine-wikibase) by Antonin Delpeuch (MIT). Independent C# implementation — no code copied. See `NOTICE` file.
