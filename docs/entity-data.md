# Entity Data & Wikipedia Content

Fetch structured entity data, Wikipedia summaries and sections, images, revision history, editions, and child entities.

> **v2.0.0 note.** Every method on this page is now owned by a focused sub-service on `WikidataReconciler`. The examples below use both forms — `reconciler.Entities.GetEntitiesAsync(...)` is the v2 path, and `reconciler.GetEntitiesAsync(...)` is a v1 delegation shim that still compiles. New code should prefer the sub-service form for clearer dependencies and DI.

## Fetch Entity Data

After reconciliation, fetch full entity data including claims with qualifiers:

```csharp
var entities = await reconciler.Entities.GetEntitiesAsync(["Q42"]);
var adams = entities["Q42"];

Console.WriteLine(adams.Label);       // "Douglas Adams"
Console.WriteLine(adams.Description); // "English author and humourist (1952-2001)"

// Access claims with typed values
foreach (var claim in adams.Claims["P31"])
    Console.WriteLine($"Instance of: {claim.Value?.EntityId}"); // "Q5"

// Access qualifiers (e.g., educated at with start/end dates)
foreach (var claim in adams.Claims["P69"])
{
    Console.WriteLine($"Educated at: {claim.Value?.EntityId}");
    if (claim.Qualifiers.TryGetValue("P580", out var startDates))
        Console.WriteLine($"  Start: {startDates[0].RawValue}");
}
```

### Specific Properties

Fetch only the properties you need:

```csharp
var props = await reconciler.Entities.GetPropertiesAsync(["Q42", "Q30"], ["P27", "P569"]);
var citizenship = props["Q42"]["P27"][0].Value?.EntityId; // "Q145" (UK)
```

Entity-valued properties automatically include human-readable labels:

```csharp
var props = await reconciler.Entities.GetPropertiesAsync(["Q42"], ["P27"]);
var country = props["Q42"]["P27"][0].Value;
// country.EntityId    -> "Q145"
// country.EntityLabel -> "United Kingdom"
```

### Entity Label Resolution

Auto-resolve entity-valued claims to human-readable labels:

```csharp
var entities = await reconciler.Entities.GetEntitiesAsync(["Q42"], resolveEntityLabels: true);
foreach (var claim in entities["Q42"].Claims["P27"])
    Console.WriteLine($"Citizenship: {claim.Value?.EntityLabel}"); // "United Kingdom"
```

### Single-Entity Label Lookup (v2.0)

When you already have a QID and only need its display label, use `reconciler.Labels`:

```csharp
var label = await reconciler.Labels.GetAsync("Q42");                   // "Douglas Adams"
var german = await reconciler.Labels.GetAsync("Q42", language: "de");  // fallback chain applies

// Batch: returned dictionary contains every valid input QID
var labels = await reconciler.Labels.GetBatchAsync(["Q42", "Q5", "Q183"]);
// labels["Q42"] = "Douglas Adams"
// labels["Q5"]  = "human"
// null values mean "entity exists but has no label in this language"
// absent keys mean "entity does not exist or the input was malformed"
```

## Wikipedia URLs

Resolve entities to validated Wikipedia article links:

```csharp
var urls = await reconciler.Wikipedia.GetWikipediaUrlsAsync(["Q42", "Q30"]);
// urls["Q42"] = "https://en.wikipedia.org/wiki/Douglas_Adams"

var deUrls = await reconciler.Wikipedia.GetWikipediaUrlsAsync(["Q42"], "de");
// deUrls["Q42"] = "https://de.wikipedia.org/wiki/Douglas_Adams"
```

## Wikipedia Summaries

Fetch article summaries (first paragraph, description, thumbnail):

```csharp
var summaries = await reconciler.Wikipedia.GetWikipediaSummariesAsync(["Q42", "Q937"]);
foreach (var s in summaries)
{
    Console.WriteLine($"{s.Title}: {s.Extract}");
    Console.WriteLine($"  Thumbnail: {s.ThumbnailUrl}");
    Console.WriteLine($"  Read more: {s.ArticleUrl}");
}
```

As of v2.6.0, summaries are fetched in provider-safe batches and mapped back to the originating QID. Missing sitelinks and missing summary pages are recorded in `reconciler.Diagnostics` with typed `WikidataFailureKind` values instead of requiring consumers to infer provider behavior from exception strings.

### Language Fallback

```csharp
var summaries = await reconciler.Wikipedia.GetWikipediaSummariesAsync(["Q42"], "ja",
    fallbackLanguages: ["zh", "en"]);
Console.WriteLine(summaries[0].Language); // actual language used
```

## Wikipedia Section Content

Fetch specific sections from Wikipedia articles:

```csharp
// Get table of contents
var sections = await reconciler.Wikipedia.GetWikipediaSectionsAsync(["Q208460"]); // 1984 (novel)
var toc = sections["Q208460"];

foreach (var section in toc)
    Console.WriteLine($"{section.Number} [{section.Level}] {section.Title}");

// Fetch a specific section as plain text (heading auto-stripped)
var plotIndex = toc.First(s => s.Title == "Plot summary").Index;
var plot = await reconciler.Wikipedia.GetWikipediaSectionContentAsync("Q208460", plotIndex);

// Fetch a section with all subsections as a structured list
var content = await reconciler.Wikipedia.GetWikipediaSectionWithSubsectionsAsync("Q83495", plotIndex);
// content[0] = { Title: "Plot", Content: "The story follows..." }
// content[1] = { Title: "Season 1", Content: "Walter White is a..." }
```

## Property Labels

Resolve property IDs to human-readable names:

```csharp
var labels = await reconciler.Entities.GetPropertyLabelsAsync(["P569", "P27", "P31"]);
// labels["P569"] = "date of birth"
```

## Entity Images

Fetch Wikimedia Commons image URLs:

```csharp
var urls = await reconciler.Entities.GetImageUrlsAsync(["Q42", "Q937"]);

// Or build URLs from any WikidataValue
var imageUrl = entity.Claims["P18"][0].Value?.ToCommonsImageUrl();
```

## Value Formatting

```csharp
var dob = entity.Claims["P569"][0].Value!;
Console.WriteLine(dob.ToDisplayString()); // "11 March 1952"

var coords = entity.Claims["P625"][0].Value!;
Console.WriteLine(coords.ToDisplayString()); // "51.5074, -0.1278"
```

## Staleness Detection

Check if cached entities have been modified:

```csharp
// Initial fetch — LastRevisionId and Modified come automatically
var entities = await reconciler.Entities.GetEntitiesAsync(["Q42", "Q5"]);
var cached = entities.ToDictionary(e => e.Key, e => (Entity: e.Value, Rev: e.Value.LastRevisionId));

// Later — lightweight check (no labels/claims fetched)
var currentRevs = await reconciler.Entities.GetRevisionIdsAsync(cached.Keys.ToList());
var stale = currentRevs.Where(r => cached[r.Key].Rev != r.Value.RevisionId).ToList();

// Only re-fetch what changed
if (stale.Count > 0)
{
    var refreshed = await reconciler.Entities.GetEntitiesAsync(stale.Select(s => s.Key).ToList());
}
```

## Entity Change Monitoring

Get detailed edit history for watched entities:

```csharp
var changes = await reconciler.Entities.GetRecentChangesAsync(
    ["Q42", "Q30"], since: DateTimeOffset.UtcNow.AddDays(-7));

foreach (var change in changes)
    Console.WriteLine($"{change.EntityId} changed at {change.Timestamp} by {change.User}");
```

As of v2.5.0, `GetRecentChangesAsync` follows continuation tokens, so long lookback windows are no longer truncated at the first page.

## Work-to-Edition Pivoting

Navigate between works and their editions/translations:

```csharp
var editions = await reconciler.Editions.GetEditionsAsync("Q190192"); // Hitchhiker's Guide
var audiobooks = await reconciler.Editions.GetEditionsAsync("Q190192", filterTypes: ["Q122731938"]);
var work = await reconciler.Editions.GetWorkForEditionAsync("Q15228");
```

For declarative edition-pivoting (e.g., "resolve an ISBN and automatically walk back to the work"), use the Stage 2 service — see `docs/migrating-to-v2.md` and `EditionPivotRule`.

## Child Entity Traversal (v2.0)

Discover child entities linked to a parent via any relationship property. The generic traversal now uses the `Direction` enum instead of the v1 `^` string prefix:

```csharp
// TV series seasons (forward traversal)
var seasons = await reconciler.Children.TraverseChildrenAsync(
    parentQid: "Q3577037",                      // Breaking Bad
    relationshipProperty: "P527",               // has parts
    direction: Direction.Outgoing,              // default
    childTypeFilter: ["Q3464665"],              // TV season
    childProperties: ["P1476", "P1545"]);       // title, ordinal

// Books in a series (reverse traversal)
var books = await reconciler.Children.TraverseChildrenAsync(
    parentQid: "Q8337",                         // Harry Potter series
    relationshipProperty: "P179",               // part of the series
    direction: Direction.Incoming,              // v1 "^P179" prefix replaced by this
    childTypeFilter: ["Q7725634"],              // literary work
    childProperties: ["P1476", "P1545", "P577", "P50"]);
```

Results are ordered by P1545 (ordinal) if available, then P577 (date), then label.

### Structured Manifest Builder (v2.0)

For common cases, use the preset-based `GetChildEntitiesAsync` which returns a `ChildEntityManifest` with typed `ChildEntityRef` rows instead of raw claims:

```csharp
var manifest = await reconciler.Children.GetChildEntitiesAsync(new ChildEntityRequest
{
    ParentQid = "Q3577037",                       // Breaking Bad
    Kind = ChildEntityKind.TvSeasonsAndEpisodes,
    MaxPrimary = 10,                              // cap seasons
    MaxTotal = 200                                // cap total rows
});

Console.WriteLine($"Seasons: {manifest.PrimaryCount}, rows: {manifest.TotalCount}");
foreach (var child in manifest.Children)
{
    var parentLabel = child.Parent is null ? "" : $"S{child.Parent}E{child.Ordinal}: ";
    Console.WriteLine($"{parentLabel}{child.Title} (release: {child.ReleaseDate})");
}
```

Built-in presets: `TvSeasonsAndEpisodes`, `MusicTracks`, `ComicIssues`, `BookSequels`. For arbitrary traversals without a library update, use `ChildEntityKind.Custom` with a `CustomChildTraversal`:

```csharp
var manifest = await reconciler.Children.GetChildEntitiesAsync(new ChildEntityRequest
{
    ParentQid = "Q3577037",
    Kind = ChildEntityKind.Custom,
    CustomTraversal = new CustomChildTraversal
    {
        RelationshipProperty = "P527",
        Direction = Direction.Outgoing,
        ChildTypeFilter = ["Q3464665"],
        OrdinalProperty = "P1545",
        CreatorRoles = new Dictionary<string, string>
        {
            ["Director"] = "P57",
            ["Writer"] = "P58"
        }
    }
});
```

## Author Resolution & Pen Names (v2.0)

Instead of fetching pseudonym claims directly, use the multi-author resolver which handles string splitting, "Last, First" detection, `et al.` markers, and pen-name lookup in one call:

```csharp
var result = await reconciler.Authors.ResolveAsync(new AuthorResolutionRequest
{
    RawAuthorString = "Neil Gaiman & Terry Pratchett",
    DetectPseudonyms = true
});

foreach (var author in result.Authors)
{
    Console.WriteLine($"{author.OriginalName} → {author.CanonicalName} ({author.Qid}) — {author.Confidence:F1}");
    if (author.RealNameQid is not null)
        Console.WriteLine($"  pen name for {author.RealNameQid}");
}

if (result.UnresolvedNames.Count > 0)
    Console.WriteLine($"Unresolved: {string.Join(", ", result.UnresolvedNames)}");
```

Supported separators: `" and "`, `" & "`, `"; "`, `", "`, `" with "`, `"、"`. `"Last, First"` single-author form is detected heuristically and not split. Trailing `"et al."` (with common variants) is captured into `UnresolvedNames`.

`WorkQidHint` adds bibliography context by preferring candidates whose `P800` (notable work) points at the supplied work.

> **v2 breaking note.** The v1 `GetAuthorPseudonymsAsync(qid)` + `PseudonymInfo` DTO have been removed. If you already have a QID and just want its P742 claims, use `reconciler.Entities.GetPropertiesAsync([qid], ["P742"])`.

## Cancellation

All async methods accept a `CancellationToken`:

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
var results = await reconciler.Reconcile.ReconcileAsync("Douglas Adams", cts.Token);
```
