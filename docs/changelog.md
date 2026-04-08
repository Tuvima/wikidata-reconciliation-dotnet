# Changelog

## v2.3.0

Additive release — no breaking changes. Closes out the library behavior and test gaps
identified during v2.0–v2.2 integration testing.

### Library behavior fixes

- **`PersonsService` no longer penalises musical groups with the P106 occupation constraint.** When `IncludeMusicalGroups` is effectively true (set explicitly, or inherited from the `Performer` / `Artist` role defaults), the service now skips the P106 (occupation) constraint entirely. Musical groups don't carry P106 claims, so the constraint was dragging group candidates below the default `AcceptThreshold` of 0.80 even when they were the correct answer. Previously required consumers to lower the threshold to ~0.5 to get groups; now works at the documented default.
- **`PersonsService` companion-hint re-ranking is now live.** `PersonSearchRequest.CompanionNameHints` was a structural signal in v2.1 but not wired to scoring. v2.3 adds a post-reconciliation re-ranking pass: fetches the top candidates' P800 (notable work) claims, resolves labels in one batch call, and boosts each candidate by 10 points per companion hint that fuzzy-matches (token-sort ratio ≥ 75) one of their notable works. One extra API round-trip when hints are set, no-op otherwise.
- **`LabelsService.GetBatchAsync` pre-filters malformed QIDs.** Wikidata's `wbgetentities` API rejects the entire batch if any single title is malformed — one bad input in a 100-QID batch used to drop every label. The service now filters the input to syntactically-valid QIDs (`Q\d+`) before calling the API. Invalid entries are absent from the result dictionary, same semantics as non-existent entities.
- **`Stage2Service.PickBestEdition` uses epsilon comparison for score tiebreaking.** Strict float equality (`a == b`) was replaced with a tolerance-based check (`Math.Abs(a - b) < 1e-9`) for determining when two edition candidates are effectively tied. In practice this changes behavior only in the rare case where two editions produce identical fuzzy-match scores at different float encodings; QID-ascending tiebreaker then kicks in as documented.

### New API surface

- **`ResolvedAuthor.Pseudonyms`** (`IReadOnlyList<string>?`) — when `DetectPseudonyms = true` and the resolved author has P742 (pseudonym) claims, the raw string values are now exposed on the result. Looking up "Stephen King" populates this with `["Richard Bachman"]`. Replaces the lightweight v2.0 stub which only ever returned null. `RealNameQid` is retained on the DTO but currently always null — reserved for a future reverse-lookup implementation that finds the real author given a pseudonym string input.

### Test coverage

Integration test coverage expanded from 28 to 34 live-Wikidata integration tests:

- `AuthorsIntegrationTests` — new tests for `Pseudonyms` population on Stephen King and verifying that `DetectPseudonyms = false` leaves the list null.
- `ChildrenIntegrationTests` — new tests for the `MusicTracks` preset (OK Computer Q213754) and the `BookSequels` preset (Hitchhiker's Guide Q25169).
- `PersonsIntegrationTests` — new companion-hint re-ranking smoke test. Daft Punk / Radiohead tests reverted to the default `AcceptThreshold = 0.80` (previously had to use 0.5 as a workaround for the P106 penalty that this release fixes).
- `Stage2IntegrationTests` — new edition-pivot smoke test that exercises the `EditionPivotRule` code path with both `WorkClasses` and `EditionClasses` configured.

Unit test coverage expanded from 85 to 88: `LabelsMalformedQidTests` adds three tests verifying the new filter semantics (all-malformed batch, empty string, bare "Q").

### Deferred to future releases

- **Reverse pseudonym lookup** — looking up "Richard Bachman" should populate `RealNameQid` with Stephen King's QID. Requires haswbstatement lookup on P742 string values; deferred because the CirrusSearch indexing story for string-typed claim values needs validation.
- **Companion-hint first-page expansion** — the current re-ranking uses the reconciler's existing top-5 result list. A stronger integration would run a wider candidate search when hints are supplied.

## v2.2.2

Patch release — docs-only.

Brings the `docs/` guides in line with the v2.0 facade + sub-services surface. No code changes.

- **`docs/architecture.md`** — replaced the v1 monolithic component diagram with the v2 facade + nine-sub-service layout. Added a design-decisions bullet for the facade pattern and one for Stage 2's discriminated request hierarchy.
- **`docs/entity-data.md`** — every example now calls the sub-service form (`reconciler.Entities.GetEntitiesAsync`, `reconciler.Wikipedia.GetWikipediaSummariesAsync`, etc.). Added sections for `reconciler.Labels`, the `Children.TraverseChildrenAsync` + `GetChildEntitiesAsync` manifest builder, and `Authors.ResolveAsync` (with a callout that the v1 `GetAuthorPseudonymsAsync` + `PseudonymInfo` were removed).
- **`docs/reconciliation.md`** — every example now uses `reconciler.Reconcile.ReconcileAsync(...)`. Every `Type = "Q5"` occurrence was replaced with `Types = ["Q5"]`. Added callouts for the breaking `Type → Types` change, the `reconciler.Stage2` unified resolver, the `reconciler.Labels` shortcut for display-label lookups, and `reconciler.Persons.SearchAsync` for role-aware person resolution.
- **`docs/aspnetcore.md`** — documents that `AddWikidataReconciliation()` now also registers every sub-service as a singleton, so consumers can inject a narrow slice (`LabelsService`, `AuthorsService`, `Stage2Service`, etc.) instead of the whole facade. Added a manual-registration example for the sub-services.

## v2.2.1

Patch release — test-only changes.

- **Integration test coverage for v2.0–v2.2 primitives.** Adds 28 live-Wikidata integration tests across five files: `LabelsIntegrationTests`, `AuthorsIntegrationTests`, `ChildrenIntegrationTests`, `PersonsIntegrationTests`, `Stage2IntegrationTests`. All tests carry `[Trait("Category", "Integration")]` so the default `dotnet test --filter "Category!=Integration"` run remains offline. Tests verify that each primitive actually hits the API, parses results, and returns the documented shape; they deliberately avoid pinning specific QIDs where reconciler scoring can return close-scoring candidates in non-deterministic order.

## v2.2.0

Additive release — no breaking changes on top of v2.1.0.

### New — `reconciler.Stage2` sub-service

A unified Stage 2 resolver that replaces the hand-rolled compose-your-own pattern of
`LookupByExternalIdAsync` + `ReconcileAsync(Types: [...])` + `GetEditionsAsync` every
consumer was re-implementing.

```csharp
// Bridge ID lookup with edition pivoting (e.g. audiobook → work)
var bridge = Stage2Request.Bridge(
    correlationKey: "book-42",
    bridgeIds: new Dictionary<string, string>
    {
        ["isbn13"] = "9780441172719",
        ["openlibrary"] = "OL24229316M"
    },
    wikidataProperties: new Dictionary<string, string>
    {
        ["isbn13"] = "P212",
        ["openlibrary"] = "P648"
    },
    editionPivot: new EditionPivotRule
    {
        WorkClasses = ["Q7725634"],                     // literary work
        EditionClasses = ["Q3331189", "Q122731938"],    // edition, audiobook edition
        PreferEdition = false                           // edition → work pivot only
    });

// Music album
var music = Stage2Request.Music("album-7", "Random Access Memories", "Daft Punk");

// Type-filtered text reconciliation (strict — CirrusSearchTypes required)
var text = Stage2Request.Text("tv-12", "Breaking Bad", ["Q5398426"], author: null);

var results = await reconciler.Stage2.ResolveBatchAsync([bridge, music, text]);
foreach (var (key, result) in results)
{
    Console.WriteLine($"{key}: {result.MatchedBy} → {result.Qid} (IsEdition={result.IsEdition})");
}
```

### Design

- **Discriminated request hierarchy.** `IStage2Request` is a marker interface with three concrete implementations: `BridgeStage2Request`, `MusicStage2Request`, `TextStage2Request`. The service uses pattern matching on the concrete type rather than an auto-detect heuristic — there is no `Stage2Strategy.Auto` enum and no "if Title is non-empty guess Text" logic. Illegal combinations (like "set both BridgeIds and AlbumTitle") are not representable.
- **Strict no-unfiltered-text rule.** `TextStage2Request.CirrusSearchTypes` is `required` and must be non-empty. To explicitly opt into running text reconciliation without a type filter, set `AllowUnfilteredText = true`. Forgetting to provide types throws `ArgumentException` instead of silently resolving nothing.
- **Batch grouping by natural key.** `ResolveBatchAsync` groups identical requests so N callers submitting the same `(isbn13, 9780441172719)` pair share a single API round-trip. Bridge grouping uses the first non-empty ID in preferred order; music uses `(AlbumTitle, Artist)` normalized; text uses `(Title, Author, sorted CirrusSearchTypes)`.
- **Edition pivoting via `EditionPivotRule`.** Media-type-agnostic — callers configure work classes, edition classes, and (optionally) a list of `RankingHint` values for picking the best edition among multiple matches. Ranking uses the library's existing `FuzzyMatcher.TokenSortRatio` against property claim values (or their resolved entity labels for entity-valued properties). Ties are broken by QID number ascending.
- **`Stage2Result` does not leak claims.** The result carries `Qid`, `WorkQid`, `EditionQid`, `IsEdition`, `MatchedBy`, `PrimaryBridgeIdType`, `CollectedBridgeIds`, and `Label`. Consumers who need full entity data should follow up with `reconciler.Entities.GetEntitiesAsync([result.Qid])`.
- **Static factory helpers.** `Stage2Request.Bridge(...)`, `.Music(...)`, `.Text(...)` make dynamic construction ergonomic for consumers that decide the strategy at runtime.

### ASP.NET Core

- `AddWikidataReconciliation()` now also registers `Stage2Service` as a singleton for direct injection.

### Scope notes

- Companion-name hints for `PersonsService.SearchAsync` remain a structural signal without a custom scoring term. Tracked for v2.3.
- The plan's original v1.1.0 scope (as `wikidata-primitives-expansion.md`) is now fully landed across v2.0, v2.1, and v2.2.

## v2.1.0

Additive release — no breaking changes on top of v2.0.0.

### New — `reconciler.Persons` sub-service

A role-aware person search primitive that replaces the hand-rolled `ReconcileAsync(Types: ["Q5"])` + P106 occupation constraints pattern that every consumer was re-implementing.

```csharp
var result = await reconciler.Persons.SearchAsync(new PersonSearchRequest
{
    Name = "Stephen King",
    Role = PersonRole.Author,
    BirthYearHint = 1947,
    WorkQid = "Q208460" // The Shining
});

if (result.Found)
{
    Console.WriteLine($"{result.CanonicalName} ({result.Qid}) — {result.Score:P0}");
}
```

**Features:**

- **Role → occupation mapping** — 9 `PersonRole` values (`Author`, `Narrator`, `Director`, `Actor`, `VoiceActor`, `Composer`, `Performer`, `Artist`, `Screenwriter`) each map to a canonical set of P106 occupation QIDs. The mapping lives in one tested place instead of scattered `switch` statements in consumer code.
- **Musical group defaulting** — `IncludeMusicalGroups` is `bool?` with a role-aware default: `Performer` and `Artist` default to `true` (include Q215380 + Q5741069), all other roles default to `false` (Q5 only). Explicit override still works.
- **Year hints** — `BirthYearHint` and `DeathYearHint` feed P569/P570 soft scoring via `PropertyConstraint`.
- **Work context** — `WorkQid` boosts candidates whose P800 references the work.
- **Group member expansion** — when `ExpandGroupMembers = true` and the resolved entity is a musical group, the result populates `GroupMembers` from P527 (has parts).
- **Accept threshold** — `AcceptThreshold` (default 0.80) controls whether `Found` flips true even when a candidate is returned. Callers can read `Score` to decide their own threshold.

### ASP.NET Core

- `AddWikidataReconciliation()` now also registers `PersonsService` as a singleton for direct injection.

### Deferred

- **Stage 2 resolver** (discriminated `IStage2Request` hierarchy, edition pivoting, bridge/music/text batch grouping) remains planned for **v2.2.0**.
- Companion-name hint scoring is a structural signal today but does not yet drive a custom scoring term. Enhancement tracked for v2.3.

## v2.0.0

**Breaking release** — introduces a facade/sub-service architecture and deletes three deprecated API shapes. See `docs/migrating-to-v2.md` for a step-by-step migration guide.

### New — facade + sub-services

`WikidataReconciler` is now a thin facade that exposes seven focused sub-services as properties. Each sub-service owns a slice of the API surface and can be injected independently:

| Property | Service | Covers |
|---|---|---|
| `reconciler.Reconcile` | `ReconciliationService` | `ReconcileAsync`, `ReconcileBatchAsync`, `ReconcileBatchStreamAsync`, `SuggestAsync`, `SuggestPropertiesAsync`, `SuggestTypesAsync` |
| `reconciler.Entities` | `EntityService` | `GetEntitiesAsync`, `GetPropertiesAsync`, `LookupByExternalIdAsync`, `GetPropertyLabelsAsync`, `GetImageUrlsAsync`, `GetRevisionIdsAsync`, `GetRecentChangesAsync` |
| `reconciler.Wikipedia` | `WikipediaService` | `GetWikipediaUrlsAsync`, `GetWikipediaSummariesAsync`, `GetWikipediaSectionsAsync`, `GetWikipediaSectionContentAsync`, `GetWikipediaSectionWithSubsectionsAsync` |
| `reconciler.Editions` | `EditionService` | `GetEditionsAsync`, `GetWorkForEditionAsync` |
| `reconciler.Children` | `ChildrenService` | `TraverseChildrenAsync` (generic), `GetChildEntitiesAsync` (new manifest builder) |
| `reconciler.Authors` | `AuthorsService` | `ResolveAsync` (new multi-author + pen-name resolver) |
| `reconciler.Labels` | `LabelsService` | `GetAsync`, `GetBatchAsync` (new single/batch label lookup) |

All v1 top-level methods on `WikidataReconciler` remain as delegating shims, so existing v1 call sites compile unchanged. New code should prefer the sub-service properties.

### New — primitives

- **`reconciler.Labels`** — `GetAsync(qid, language, withFallbackLanguage)` and `GetBatchAsync(qids, ...)` replace the manual `GetEntitiesAsync([qid])` + `TryGetValue` dance for pure label lookups. `GetBatchAsync` returns `IReadOnlyDictionary<string, string?>` with every input QID present (no silent-drop).
- **`reconciler.Authors.ResolveAsync(AuthorResolutionRequest)`** — splits multi-author strings on `" and "`, `" & "`, `"; "`, `", "`, `" with "`, and `"、"`, with "Last, First" detection and trailing `et al.` capture. Returns per-author `ResolvedAuthor` entries with optional pen-name information (`RealNameQid`).
- **`reconciler.Children.GetChildEntitiesAsync(ChildEntityRequest)`** — structured manifest builder with presets `TvSeasonsAndEpisodes`, `MusicTracks`, `ComicIssues`, `BookSequels`, and `Custom` escape-hatch. Returns `ChildEntityManifest` with a `Children` list of `ChildEntityRef` (Qid, Title, Ordinal, Parent, ReleaseDate, Duration, Creators). Honours `MaxPrimary` and `MaxTotal` caps deterministically.

### New — ASP.NET Core

- `AddWikidataReconciliation()` now also registers every sub-service (`ReconciliationService`, `EntityService`, `WikipediaService`, `EditionService`, `ChildrenService`, `AuthorsService`, `LabelsService`) so advanced consumers can inject just the slice they need without going through the facade.

### Breaking — removed

- **`ReconciliationRequest.Type` (singular)** — removed. Use `Types` (plural) for every type filter, even when filtering by a single type. v1 had both fields with "`Types` wins" precedence rules, which was a source of confusion.
- **`PropertyConstraint.Value` (singular)** — removed, along with the internal `GetEffectiveValues()` helper. Use `Values` (plural) directly, or the convenience constructor `new PropertyConstraint("P569", "1952-03-11")` which wraps a single value internally.
- **`GetAuthorPseudonymsAsync(string entityQid, ...)`** — removed along with `PseudonymInfo`. Subsumed by `reconciler.Authors.ResolveAsync(...)` which integrates pen-name detection into the multi-author resolution flow.

### Breaking — renamed / moved

- **`GetChildEntitiesAsync(string parentQid, string relationshipProperty, ...)`** — renamed to **`reconciler.Children.TraverseChildrenAsync(parentQid, relationshipProperty, Direction direction = Direction.Outgoing, ...)`**. The `^P` string prefix for reverse traversal is gone; use `Direction.Incoming` instead.
- **`GetChildEntitiesAsync`** (the name) is now taken by the new manifest-builder primitive that accepts a `ChildEntityRequest` and returns a `ChildEntityManifest`.
- **`Direction` enum** — moved from `Tuvima.Wikidata.Graph` to the root `Tuvima.Wikidata` namespace. It is now shared between the graph module and child-entity traversal. For consumers who wrote `using Tuvima.Wikidata.Graph;` and referenced `Direction`, the enum still resolves via C#'s enclosing-namespace rule — no action needed unless you had a fully qualified `Tuvima.Wikidata.Graph.Direction` reference (change to `Tuvima.Wikidata.Direction`).

### Deferred

- **Persons service** (SearchPersonAsync with role → occupation mapping, year hints, group expansion) is planned for **v2.1**.
- **Stage 2 resolver** (ResolveStage2BatchAsync with discriminated Bridge/Music/Text request types and edition pivoting) is planned for **v2.2**.

### Migration

See `docs/migrating-to-v2.md` for before/after snippets and sed-ready find/replace patterns for every break.

## v1.0.0

- **Renamed from `Tuvima.WikidataReconciliation` to `Tuvima.Wikidata`** — package name now reflects the library's full scope: reconciliation, entity data, Wikipedia content, and graph traversal. All public types keep their names; only the namespace changes (`using Tuvima.WikidataReconciliation` becomes `using Tuvima.Wikidata`).
- **Graph module** — new `Tuvima.Wikidata.Graph` namespace with `EntityGraph` for in-memory entity graph traversal. Provides pathfinding (BFS), family tree construction, cross-media entity detection, neighbor lookup, and subgraph extraction. Zero dependencies, AOT compatible, thread-safe. Replaces the need for heavy graph libraries like dotNetRDF for common entity relationship operations.
- **Repository moved** to [github.com/Tuvima/wikidata](https://github.com/Tuvima/wikidata).
- **v1.0.0 stability signal** — the reconciliation API has been production-tested through 10 minor versions.

### Migration from v0.x

1. Update package references:
   - `Tuvima.WikidataReconciliation` -> `Tuvima.Wikidata`
   - `Tuvima.WikidataReconciliation.AspNetCore` -> `Tuvima.Wikidata.AspNetCore`
2. Update namespace imports:
   - `using Tuvima.WikidataReconciliation;` -> `using Tuvima.Wikidata;`
   - `using Tuvima.WikidataReconciliation.AspNetCore;` -> `using Tuvima.Wikidata.AspNetCore;`
3. All public types (`WikidataReconciler`, `ReconciliationRequest`, `ReconciliationResult`, etc.) are unchanged.

## v0.10.0

- **Section heading stripping** — `GetWikipediaSectionContentAsync` now automatically strips the section's own heading from the returned content.
- **Subsection content** — new `GetWikipediaSectionWithSubsectionsAsync` fetches a section and all its nested subsections as a structured list of `SectionContent` objects.
- **Multi-value property constraints** — `PropertyConstraint` now supports a `Values` property for matching against entities with multiple values (e.g., multiple authors).
- **Child entity discovery** — new `GetChildEntitiesAsync` traverses parent-child relationships generically. Supports forward and reverse (`^P179`) traversal, optional P31 type filtering, and automatic ordering.

## v0.9.0

- **Public EntityLabel setter** — `WikidataValue.EntityLabel` is now a public setter.

## v0.8.0

- **Automatic entity label resolution in GetPropertiesAsync** — labels are batch-fetched and respect the language parameter with fallback.

## v0.7.0

- **Entity label resolution for GetPropertiesAsync** — new `resolveEntityLabels` parameter.

## v0.6.0

- **Type-filtered search** — CirrusSearch `haswbstatement:P31=QID` at query time. Multi-type OR logic. Per-request `TypeHierarchyDepth` override.
- **Multi-language reconciliation** — concurrent search in multiple languages, deduplicated by QID.
- **Entity label resolution** — `GetEntitiesAsync(qids, resolveEntityLabels: true)`.
- **Work-to-edition pivoting** — `GetEditionsAsync` and `GetWorkForEditionAsync`.
- **Diacritic-aware search** — `DiacriticInsensitive` flag.
- **Display-friendly labels** — `IncludeSitelinkLabels` option.
- **Wikipedia summary language fallback**.
- **Query pre-cleaning** — `Cleaners` pipeline.
- **Pseudonym detection** — `GetAuthorPseudonymsAsync`.
- **Caching infrastructure** — `CachingDelegatingHandler` abstract base class.

## v0.5.0

- **Wikipedia section content** — `GetWikipediaSectionsAsync` and `GetWikipediaSectionContentAsync`.
- **Staleness detection** — `LastRevisionId`, `Modified`, and `GetRevisionIdsAsync`.

## v0.4.0

- **Cross-language label scoring** — scorer compares against labels in all languages.
- **MatchedLabel property** on results.

## v0.3.0

- **External ID lookup**, **value formatting**, **property labels**, **entity images**, **Wikipedia summaries**.
- **W3C Reconciliation API** — ASP.NET Core middleware.
- **Entity change monitoring**, **maxlag support**.

## v0.2.0

- **Data extension**, **qualifiers**, **P279 subclass matching**, **specific property fetching**.
- **Wikipedia URLs**, **batch reconciliation**, **exclude types**, **custom Wikibase support**.

## v0.1.0

- **Core reconciliation** — dual search, fuzzy matching, type filtering, property constraints, property paths, score breakdown, unique ID shortcut, streaming batch, suggest, retry with backoff.
- **Zero dependencies**, **AOT compatible**.
