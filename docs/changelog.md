# Changelog

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
