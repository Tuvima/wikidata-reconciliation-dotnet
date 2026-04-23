# Migrating from Tuvima.Wikidata v1 → v2.0.0

This is a breaking release. The API surface is still fully usable from v1 call sites thanks to delegation shims on the facade, but four specific things will fail to compile and must be updated manually. A fifth (the new facade pattern) is opt-in.

## TL;DR — what breaks at the compiler

| # | Break | Find | Replace |
|---|---|---|---|
| 1 | `ReconciliationRequest.Type` deleted | `Type = "Q5"` inside `new ReconciliationRequest { ... }` | `Types = ["Q5"]` |
| 2 | `PropertyConstraint.Value` deleted | `new PropertyConstraint { PropertyId = "P50", Value = "Q42" }` | `new PropertyConstraint("P50", "Q42")` or `Values = ["Q42"]` |
| 3 | `GetAuthorPseudonymsAsync` deleted | `reconciler.GetAuthorPseudonymsAsync(qid)` | `reconciler.Authors.ResolveAsync(new AuthorResolutionRequest { RawAuthorString = authorName, DetectPseudonyms = true })` |
| 4 | `GetChildEntitiesAsync(string, string, ...)` renamed | `reconciler.GetChildEntitiesAsync(parent, "P527")` | `reconciler.Children.TraverseChildrenAsync(parent, "P527")` |
| 4b | `^P` reverse-traversal prefix removed | `reconciler.GetChildEntitiesAsync(parent, "^P179")` | `reconciler.Children.TraverseChildrenAsync(parent, "P179", Direction.Incoming)` |

Everything else on `WikidataReconciler` stays working via delegation shims. You can migrate to the sub-service properties on your own schedule.

## Break 1 — `ReconciliationRequest.Type` → `Types`

**v1**
```csharp
var results = await reconciler.ReconcileAsync(new ReconciliationRequest
{
    Query = "Douglas Adams",
    Type = "Q5"
});
```

**v2**
```csharp
var results = await reconciler.ReconcileAsync(new ReconciliationRequest
{
    Query = "Douglas Adams",
    Types = ["Q5"]
});
```

Why: v1 had both `Type` and `Types` with "`Types` wins if both set" precedence rules. This was a source of confusion and silent filter misfires. There is now exactly one way to filter by type.

The two-argument convenience overload `reconciler.ReconcileAsync("Douglas Adams", "Q5")` still works — it wraps the type as a single-element list internally.

## Break 2 — `PropertyConstraint.Value` → `Values`

**v1**
```csharp
new PropertyConstraint { PropertyId = "P569", Value = "1952-03-11" }
```

**v2**
```csharp
new PropertyConstraint("P569", "1952-03-11")
// or explicitly:
new PropertyConstraint { PropertyId = "P569", Values = ["1952-03-11"] }
```

Why: same reasoning as Break 1 — `Value` and `Values` coexisting with precedence rules was complexity consumers did not need. The convenience constructor absorbs the common single-value case.

Multi-value constraints are unchanged: `new PropertyConstraint("P50", new[] { "Q1", "Q2", "Q3" })`.

## Break 3 — `GetAuthorPseudonymsAsync` → `Authors.ResolveAsync`

**v1**
```csharp
var pseudonyms = await reconciler.GetAuthorPseudonymsAsync("Q12345");
foreach (var p in pseudonyms)
    Console.WriteLine($"{p.AuthorLabel}: {string.Join(", ", p.Pseudonyms)}");
```

**v2**
```csharp
var result = await reconciler.Authors.ResolveAsync(new AuthorResolutionRequest
{
    RawAuthorString = "Stephen King",
    DetectPseudonyms = true
});

foreach (var author in result.Authors)
{
    Console.WriteLine($"{author.OriginalName} → {author.CanonicalName} ({author.Qid})");
    if (author.RealNameQid is not null)
        Console.WriteLine($"  pen name for {author.RealNameQid}");
}
```

Why: the v1 method required you to already have a QID, and returned a loose `PseudonymInfo` shape. The v2 primitive accepts a raw author string (including multi-author like `"Neil Gaiman & Terry Pratchett"`), resolves each name, and flags pen names inline on each `ResolvedAuthor`. The `PseudonymInfo` DTO has been removed.

If you already have a QID and just want its P742 claims, use `reconciler.Entities.GetPropertiesAsync([qid], ["P742"])`.

## Break 4 — `GetChildEntitiesAsync` split into two methods

**v1 — forward traversal**
```csharp
var episodes = await reconciler.GetChildEntitiesAsync(
    "Q3577037",           // Breaking Bad
    "P527",               // has parts
    childTypeFilter: ["Q21191270"]);
```

**v2**
```csharp
var episodes = await reconciler.Children.TraverseChildrenAsync(
    "Q3577037",
    "P527",
    Direction.Outgoing,
    childTypeFilter: ["Q21191270"]);
```

**v1 — reverse traversal with `^` prefix**
```csharp
var issues = await reconciler.GetChildEntitiesAsync(
    "Q79962",
    "^P179",              // reverse: entities whose P179 points to Q79962
    childTypeFilter: ["Q14406742"]);
```

**v2 — `Direction.Incoming`**
```csharp
var issues = await reconciler.Children.TraverseChildrenAsync(
    "Q79962",
    "P179",
    Direction.Incoming,
    childTypeFilter: ["Q14406742"]);
```

Why: the `^P` string prefix was a stringly-typed convention that clashed with valid property IDs if anyone ever introduced a property starting with `^`, and it was not discoverable in IntelliSense. The new `Direction` enum is the same one used by the graph module — one consistent way to express direction across the library. The enum was moved from `Tuvima.Wikidata.Graph` to the root `Tuvima.Wikidata` namespace; C#'s enclosing-namespace rule means existing `using Tuvima.Wikidata.Graph;` references continue to resolve.

The name `GetChildEntitiesAsync` is now used by the new manifest-builder primitive:

```csharp
var manifest = await reconciler.Children.GetChildEntitiesAsync(new ChildEntityRequest
{
    ParentQid = "Q3577037",
    Kind = ChildEntityKind.TvSeasonsAndEpisodes,
    MaxPrimary = 10,     // at most 10 seasons
    MaxTotal = 200       // at most 200 rows total (seasons + episodes)
});

Console.WriteLine($"Seasons: {manifest.PrimaryCount}, total rows: {manifest.TotalCount}");
foreach (var child in manifest.Children)
{
    var parentLabel = child.Parent is null ? "" : $"S{child.Parent}E{child.Ordinal}: ";
    Console.WriteLine($"{parentLabel}{child.Title}");
}
```

Presets: `TvSeasonsAndEpisodes`, `MusicTracks`, `ComicIssues`, `BookSequels`, `Custom`. For custom traversals, set `Kind = ChildEntityKind.Custom` and supply a `CustomChildTraversal`.

## Opt-in — migrating to sub-services

All v1 top-level methods on `WikidataReconciler` still work. They now forward to the owning sub-service:

```csharp
// both of these do the same thing in v2
await reconciler.ReconcileAsync(request);           // v1 shim, still works
await reconciler.Reconcile.ReconcileAsync(request); // v2 direct call
```

The sub-service call is more discoverable in IntelliSense (smaller surface per type) and lets you inject narrower dependencies in DI:

```csharp
// v2 DI — inject just the service you need
public class MyService(LabelsService labels, AuthorsService authors)
{
    public async Task DoThingAsync(string qid)
    {
        var label = await labels.GetAsync(qid);
        // ...
    }
}
```

`AddWikidataReconciliation()` in the ASP.NET Core package registers every sub-service as a singleton alongside the facade.

## New primitives worth knowing about

- **`Labels.GetBatchAsync(qids)`** returns `IReadOnlyDictionary<string, string?>` with every valid input QID present — `null` means the entity exists but has no label in the requested language, and absence means the entity was missing or the input was invalid. This fixes a v1 silent-drop in `GetPropertyLabelsAsync`.
- **`Authors.ResolveAsync`** splits multi-author strings and flags pen names in one call. Handles `" and "`, `" & "`, `"; "`, `", "`, `" with "`, `"、"`, and `"et al."`. Recognizes `"Last, First"` single-name form.
- **`Children.GetChildEntitiesAsync(ChildEntityRequest)`** bundles TV seasons + episodes (or music tracks, or comic issues, or book sequels) into a structured manifest with ordinal, release date, duration, and creator roles.

## Deferred features

The following primitives from the original plan are **not** in v2.0.0:

- `Persons.SearchAsync` (role-aware person search, musical group handling, year/companion hints) — planned for **v2.1.0**.
- `Stage2.ResolveBatchAsync` (unified bridge/music/text resolver with discriminated request types, edition pivoting) — planned for **v2.2.0**.

Consumers that need these in v2.0.0 should continue composing `LookupByExternalIdAsync` + `ReconcileAsync` manually for the Stage 2 case, and `ReconcileAsync(new ReconciliationRequest { Types = ["Q5"] })` for person search.
