# Tuvima.WikidataReconciliation

A .NET library that connects your data to [Wikidata](https://www.wikidata.org/) and [Wikipedia](https://www.wikipedia.org/). It matches text (names, titles, places) to Wikidata entities, pulls back structured data like dates, identifiers, and images, and retrieves Wikipedia article content — summaries, section listings, and full section text.

**In plain English:** You have a spreadsheet with author names, book titles, or company names. This library figures out which Wikidata item each one refers to, gives you a confidence score, and then lets you enrich your data with everything Wikidata and Wikipedia know about those entities — birth dates, nationalities, ISBN numbers, profile images, plot summaries, biographical details, and more.

This is the first .NET Wikidata reconciliation library, filling a gap in the ecosystem where only Python and JavaScript implementations previously existed.

## Who Is This For?

- **Data engineers** cleaning and linking datasets to structured identifiers
- **App developers** building search, autocomplete, or knowledge-powered features
- **Library/archive systems** matching catalog records to authority files (VIAF, ISNI, LoC)
- **Research teams** enriching study data with Wikidata's 100M+ items
- **Content platforms** pulling plot summaries, biographies, or descriptions from Wikipedia
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
| A name with diacritics like "Shōgun" | Matches regardless of accents with diacritic-insensitive mode |
| A work like "Hitchhiker's Guide" | All editions and translations, filterable by type (audiobook, paperback, etc.) |
| A query in Japanese and English | Multi-language search that finds the best match across both languages |
| Cached entity data | Lightweight staleness check — only re-fetch what actually changed |

## What is Reconciliation?

Reconciliation is the process of matching messy, real-world text (like "Douglas Adams" or "1984") to structured entities in a knowledge base. For example:

| Input text | Matched entity | QID | Score |
|---|---|---|---|
| `"Douglas Adams"` | Douglas Adams | [Q42](https://www.wikidata.org/wiki/Q42) | 100 |
| `"United States of America"` | United States of America | [Q30](https://www.wikidata.org/wiki/Q30) | 100 |
| `"1984"` (with type: literary work) | Nineteen Eighty-Four | [Q208460](https://www.wikidata.org/wiki/Q208460) | 67 |

## Installation

```
dotnet add package Tuvima.WikidataReconciliation
```

**Targets:** .NET 8.0 (LTS) and .NET 10.0

**Dependencies:** None beyond `System.Text.Json` (built into .NET).

## Quick Start

```csharp
using Tuvima.WikidataReconciliation;

using var reconciler = new WikidataReconciler();

// Simple lookup by name
var results = await reconciler.ReconcileAsync("Douglas Adams");

Console.WriteLine(results[0].Id);          // "Q42"
Console.WriteLine(results[0].Name);        // "Douglas Adams"
Console.WriteLine(results[0].Description); // "English author and humourist (1952-2001)"
Console.WriteLine(results[0].Score);       // 100
Console.WriteLine(results[0].Match);       // true (confident auto-match)
```

## Usage

### Filter by Type

Constrain results to entities of a specific type using their P31 (instance of) value:

```csharp
// Only match humans (Q5)
var results = await reconciler.ReconcileAsync("Douglas Adams", "Q5");

// Only match literary works (Q7725634)
var results = await reconciler.ReconcileAsync("1984", "Q7725634");
```

### Add Property Constraints

Supply known property values to improve scoring accuracy. Each property constraint boosts or penalizes candidates based on how well they match:

```csharp
var results = await reconciler.ReconcileAsync(new ReconciliationRequest
{
    Query = "Douglas Adams",
    Type = "Q5",                    // human
    Limit = 5,
    Properties =
    [
        new PropertyConstraint("P27", "Q145"),         // country of citizenship: United Kingdom
        new PropertyConstraint("P569", "1952-03-11"),  // date of birth
    ]
});
```

Property values can be:

| Data type | Example value | Description |
|---|---|---|
| Item (QID) | `"Q145"` | Exact entity match |
| String | `"Douglas Adams"` | Fuzzy string match (token-sort-ratio) |
| External ID | `"118500902"` | Exact match (e.g., GND identifier) |
| Date | `"1952-03-11"` | Precision-aware (year, month, or full date) |
| Quantity | `"42"` | Log-decay curve for numeric proximity |
| URL | `"https://example.com"` | Scheme-normalized exact match |
| Coordinates | `"51.5074,-0.1278"` | Distance-based (score decreases to 0 at 1 km) |

### Exclude Types

Remove candidates of specific types from results:

```csharp
var results = await reconciler.ReconcileAsync(new ReconciliationRequest
{
    Query = "Cambridge",
    ExcludeTypes = ["Q17442446"],  // exclude Wikimedia internal items
});
```

### Property Paths

Chain properties to match against related entities. For example, match a person's country of citizenship via their city of birth:

```csharp
var results = await reconciler.ReconcileAsync(new ReconciliationRequest
{
    Query = "Douglas Adams",
    Properties =
    [
        new PropertyConstraint("P19", "Q350"),   // place of birth: Cambridge (direct)
        new PropertyConstraint("P19/P17", "Q145"), // place of birth -> country: UK (chained)
    ]
});
```

Property paths use `/` to chain properties. The library resolves each segment by fetching intermediate entities from the API.

### Batch Reconciliation

Reconcile multiple queries with automatic concurrency limiting (default: 5 concurrent requests):

```csharp
var results = await reconciler.ReconcileBatchAsync([
    new ReconciliationRequest { Query = "Douglas Adams", Type = "Q5" },
    new ReconciliationRequest { Query = "Albert Einstein", Type = "Q5" },
    new ReconciliationRequest { Query = "Nineteen Eighty-Four", Type = "Q7725634" },
]);

// results[0] -> Douglas Adams matches
// results[1] -> Albert Einstein matches
// results[2] -> Nineteen Eighty-Four matches
```

### Streaming Batch Reconciliation

For large datasets, use `ReconcileBatchStreamAsync` to process results as they arrive via `IAsyncEnumerable`. This reduces memory pressure and enables progress reporting:

```csharp
var requests = LoadThousandsOfRequests();
var completed = 0;

await foreach (var (index, results) in reconciler.ReconcileBatchStreamAsync(requests))
{
    completed++;
    Console.WriteLine($"[{completed}/{requests.Count}] {requests[index].Query} -> {results[0].Id}");
    SaveResult(index, results);
}
```

### Suggest / Autocomplete

For interactive UIs with type-ahead search. Three suggest methods cover entities, properties, and types:

```csharp
// Suggest entities
var entities = await reconciler.SuggestAsync("Douglas");
// Q42: Douglas Adams - English author and humourist (1952-2001)

// Suggest properties (for building property picker UIs)
var properties = await reconciler.SuggestPropertiesAsync("date");
// P569: date of birth
// P570: date of death
// P577: publication date

// Suggest types (for building type filter UIs)
var types = await reconciler.SuggestTypesAsync("book");
// Q571: book
// Q7725634: literary work
```

### Fetch Entity Data (Data Extension)

After reconciliation, fetch full entity data including claims with qualifiers:

```csharp
var entities = await reconciler.GetEntitiesAsync(["Q42"]);
var adams = entities["Q42"];

Console.WriteLine(adams.Label);       // "Douglas Adams"
Console.WriteLine(adams.Description); // "English author and humourist (1952-2001)"

// Access claims with typed values
foreach (var claim in adams.Claims["P31"])
{
    Console.WriteLine($"Instance of: {claim.Value?.EntityId}"); // "Q5"
}

// Access qualifiers (e.g., educated at with start/end dates)
foreach (var claim in adams.Claims["P69"])
{
    Console.WriteLine($"Educated at: {claim.Value?.EntityId}");
    if (claim.Qualifiers.TryGetValue("P580", out var startDates))
        Console.WriteLine($"  Start: {startDates[0].RawValue}");
}
```

Fetch only specific properties for efficiency:

```csharp
var props = await reconciler.GetPropertiesAsync(["Q42", "Q30"], ["P27", "P569"]);
var citizenship = props["Q42"]["P27"][0].Value?.EntityId; // "Q145" (UK)
```

Entity-valued properties automatically include human-readable labels:

```csharp
var props = await reconciler.GetPropertiesAsync(["Q42"], ["P27"]);
var country = props["Q42"]["P27"][0].Value;
// country.EntityId    → "Q145"
// country.EntityLabel → "United Kingdom"
```

### Wikipedia URLs

Resolve entities to validated Wikipedia article links:

```csharp
var urls = await reconciler.GetWikipediaUrlsAsync(["Q42", "Q30"]);
// urls["Q42"] = "https://en.wikipedia.org/wiki/Douglas_Adams"
// urls["Q30"] = "https://en.wikipedia.org/wiki/United_States"

var deUrls = await reconciler.GetWikipediaUrlsAsync(["Q42"], "de");
// deUrls["Q42"] = "https://de.wikipedia.org/wiki/Douglas_Adams"
```

Only returns URLs for entities that actually have a Wikipedia article in the requested language.

### Wikipedia Summaries

Fetch article summaries (first paragraph, description, thumbnail) from Wikipedia:

```csharp
var summaries = await reconciler.GetWikipediaSummariesAsync(["Q42", "Q937"]);

foreach (var s in summaries)
{
    Console.WriteLine($"{s.Title}: {s.Extract}");
    Console.WriteLine($"  Thumbnail: {s.ThumbnailUrl}");
    Console.WriteLine($"  Read more: {s.ArticleUrl}");
}
// Douglas Adams: Douglas Noël Adams was an English author, humourist, and screenwriter...
```

Supports any Wikipedia language edition:

```csharp
var deSummaries = await reconciler.GetWikipediaSummariesAsync(["Q42"], "de");
```

### Reverse Lookup by External ID

Find a Wikidata entity by its ISBN, IMDB ID, ORCID, or any other external identifier — no fuzzy matching needed:

```csharp
// Find entity by VIAF ID
var results = await reconciler.LookupByExternalIdAsync("P214", "113230702");
// results[0].Id == "Q42" (Douglas Adams)

// Find entity by ISBN-13
var results = await reconciler.LookupByExternalIdAsync("P212", "978-0-345-39180-3");

// Find entity by IMDB ID
var results = await reconciler.LookupByExternalIdAsync("P345", "tt0371724");
```

### Property Labels

Resolve property IDs to human-readable names:

```csharp
var labels = await reconciler.GetPropertyLabelsAsync(["P569", "P27", "P31"]);
// labels["P569"] = "date of birth"
// labels["P27"]  = "country of citizenship"
// labels["P31"]  = "instance of"
```

### Entity Images

Fetch Wikimedia Commons image URLs for entities:

```csharp
var urls = await reconciler.GetImageUrlsAsync(["Q42", "Q937"]);
// urls["Q42"]  = "https://commons.wikimedia.org/wiki/Special:FilePath/Douglas_Adams_San_Dimas_1.jpg"
// urls["Q937"] = "https://commons.wikimedia.org/wiki/Special:FilePath/Einstein_1921_by_F_Schmutzer_-_restoration.jpg"
```

You can also build Commons URLs from any `WikidataValue`:

```csharp
var imageValue = entity.Claims["P18"][0].Value;
var imageUrl = imageValue?.ToCommonsImageUrl();
```

### Value Formatting

`WikidataValue` objects have a `ToDisplayString()` method for human-readable output:

```csharp
var dob = entity.Claims["P569"][0].Value!;
Console.WriteLine(dob.ToDisplayString()); // "11 March 1952"

var coords = entity.Claims["P625"][0].Value!;
Console.WriteLine(coords.ToDisplayString()); // "51.5074, -0.1278"
```

### Staleness Detection

Every entity fetch includes revision metadata for free — use it to detect when cached data is outdated:

```csharp
// Initial fetch — LastRevisionId and Modified come automatically
var entities = await reconciler.GetEntitiesAsync(["Q42", "Q5"]);
var cached = entities.ToDictionary(e => e.Key, e => (Entity: e.Value, Rev: e.Value.LastRevisionId));

// Later — one ultra-lightweight call checks all entities at once (no labels/claims fetched)
var currentRevs = await reconciler.GetRevisionIdsAsync(cached.Keys.ToList());
var stale = currentRevs.Where(r => cached[r.Key].Rev != r.Value.RevisionId).ToList();

// Only re-fetch the ones that actually changed
if (stale.Count > 0)
{
    var refreshed = await reconciler.GetEntitiesAsync(stale.Select(s => s.Key).ToList());
    // update cache with refreshed entities...
}
```

### Wikipedia Section Content

Fetch specific sections from Wikipedia articles — plot summaries, career details, themes, or any other section:

```csharp
// Get table of contents for an entity
var sections = await reconciler.GetWikipediaSectionsAsync(["Q208460"]); // 1984 (novel)
var toc = sections["Q208460"];

foreach (var section in toc)
    Console.WriteLine($"{section.Number} [{section.Level}] {section.Title}");
// 1 [2] Plot summary
// 1.1 [3] Epilogue
// 2 [2] Characters
// ...

// Fetch a specific section's content as plain text
var plotIndex = toc.First(s => s.Title == "Plot summary").Index;
var plot = await reconciler.GetWikipediaSectionContentAsync("Q208460", plotIndex);
Console.WriteLine(plot);
// "As the narrative opens on April 4th, 1984..."
```

The library returns the table of contents with section names, levels, and indices — you decide which sections matter for your use case. Section content is returned as clean plain text with HTML tags, footnotes, and tables stripped.

### Entity Change Monitoring

Get detailed edit history for watched entities (useful for audit logs or understanding what changed):

```csharp
var changes = await reconciler.GetRecentChangesAsync(
    ["Q42", "Q30"], since: DateTimeOffset.UtcNow.AddDays(-7));

foreach (var change in changes)
    Console.WriteLine($"{change.EntityId} changed at {change.Timestamp} by {change.User}");
```

### Direct QID Lookup

If you already have a QID, you can pass it directly to retrieve entity details with a perfect score:

```csharp
var results = await reconciler.ReconcileAsync("Q42");
// results[0].Id == "Q42", results[0].Name == "Douglas Adams", results[0].Score == 100
```

### Change the Search Language

Search labels and aliases in a specific language:

```csharp
var results = await reconciler.ReconcileAsync(new ReconciliationRequest
{
    Query = "Frankreich",
    Language = "de",
});
// Finds Q142 (France) via its German label
```

The library uses a language fallback chain: if a label/description is missing in the requested language, it tries the subtag parent ("de-ch" falls back to "de"), then "mul" (multilingual), then "en" (English).

### Multi-Type Filtering with CirrusSearch

Filter by multiple types (OR logic) with CirrusSearch for better recall. Also override the subclass walk depth per-request:

```csharp
var results = await reconciler.ReconcileAsync(new ReconciliationRequest
{
    Query = "Shogun",
    Types = ["Q5398426", "Q15416"],  // TV series OR TV program
    TypeHierarchyDepth = 3,          // walk P279 up to 3 levels
});
```

### Multi-Language Search

Search in multiple languages concurrently. Results are deduplicated by QID:

```csharp
var results = await reconciler.ReconcileAsync(new ReconciliationRequest
{
    Query = "千と千尋の神隠し",
    Languages = ["ja", "en"],
});
```

### Diacritic-Insensitive Search

Match entities regardless of accents and diacritical marks:

```csharp
var results = await reconciler.ReconcileAsync(new ReconciliationRequest
{
    Query = "Shogun",
    DiacriticInsensitive = true,  // matches "Shōgun"
});
```

### Query Pre-Cleaning

Strip noise from queries before search using built-in or custom cleaners:

```csharp
var results = await reconciler.ReconcileAsync(new ReconciliationRequest
{
    Query = "The Hitchhiker's Guide to the Galaxy (Unabridged)",
    Cleaners = [QueryCleaners.StripParenthetical()],
});

// Or use all built-in cleaners at once
var results = await reconciler.ReconcileAsync(new ReconciliationRequest
{
    Query = "Dune: Part Two S01E03 (Special Edition)",
    Cleaners = QueryCleaners.All(),
});
```

### Entity Label Resolution

Auto-resolve entity-valued claims to human-readable labels:

```csharp
var entities = await reconciler.GetEntitiesAsync(["Q42"], resolveEntityLabels: true);
var adams = entities["Q42"];

foreach (var claim in adams.Claims["P27"])
{
    // EntityLabel is auto-populated: "United Kingdom" instead of just "Q145"
    Console.WriteLine($"Citizenship: {claim.Value?.EntityLabel}"); // "United Kingdom"
    Console.WriteLine($"Display: {claim.Value?.ToDisplayString()}"); // "United Kingdom"
}
```

### Work-to-Edition Pivoting

Navigate between works and their editions/translations:

```csharp
// Get all editions of a work
var editions = await reconciler.GetEditionsAsync("Q190192"); // Hitchhiker's Guide

// Filter to audiobook editions only
var audiobooks = await reconciler.GetEditionsAsync("Q190192",
    filterTypes: ["Q122731938"]); // audiobook edition

// Find the parent work from an edition
var work = await reconciler.GetWorkForEditionAsync("Q15228");
```

### Wikipedia Summary Language Fallback

Fetch summaries with automatic fallback to other language editions:

```csharp
// Uses default fallback chain: requested → subtag parent → "en"
var summaries = await reconciler.GetWikipediaSummariesAsync(["Q42"], "de", fallbackLanguages: null);

// Or specify custom fallback languages
var summaries = await reconciler.GetWikipediaSummariesAsync(["Q42"], "ja",
    fallbackLanguages: ["zh", "en"]);

// Check which language was actually used
Console.WriteLine(summaries[0].Language); // "de", "en", etc.
```

### Pseudonym Detection

Find pen names and pseudonyms for authors:

```csharp
// From a book entity — finds authors via P50, then checks P742
var pseudonyms = await reconciler.GetAuthorPseudonymsAsync("Q190192");

// Or from an author entity directly
var pseudonyms = await reconciler.GetAuthorPseudonymsAsync("Q42");
foreach (var p in pseudonyms)
{
    Console.WriteLine($"{p.AuthorLabel}: {string.Join(", ", p.Pseudonyms)}");
}
```

### Cancellation

All async methods accept a `CancellationToken`:

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
var results = await reconciler.ReconcileAsync("Douglas Adams", cts.Token);
```

### Score Breakdown (Explainability)

Every result includes a detailed `Breakdown` explaining how the score was computed. Use this to build custom trust rules:

```csharp
var results = await reconciler.ReconcileAsync(new ReconciliationRequest
{
    Query = "Douglas Adams",
    Type = "Q5",
    Properties = [new PropertyConstraint("P27", "Q145")]
});

var b = results[0].Breakdown!;
Console.WriteLine($"Label match:    {b.LabelScore}");        // 100
Console.WriteLine($"P27 match:      {b.PropertyScores["P27"]}"); // 100
Console.WriteLine($"Type matched:   {b.TypeMatched}");        // true
Console.WriteLine($"Weighted score: {b.WeightedScore}");      // 100
Console.WriteLine($"Type penalty:   {b.TypePenaltyApplied}"); // false

// Custom trust rule: only accept if date of birth is an exact match
if (b.PropertyScores.TryGetValue("P569", out var dobScore) && dobScore == 100)
    AcceptMatch(results[0]);
```

## Configuration

```csharp
var reconciler = new WikidataReconciler(new WikidataReconcilerOptions
{
    // API endpoint (default: Wikidata)
    ApiEndpoint = "https://www.wikidata.org/w/api.php",

    // Search language (default: "en", overridable per-request)
    Language = "en",

    // User-Agent header (required by Wikimedia policy — identify your app)
    UserAgent = "MyApp/1.0 (contact@example.com)",

    // HTTP timeout (default: 30 seconds)
    Timeout = TimeSpan.FromSeconds(30),

    // Type property (default: "P31" for Wikidata — custom Wikibase may use different IDs)
    TypePropertyId = "P31",

    // Scoring tuning
    PropertyWeight = 0.4,        // weight for each property match (label match = 1.0)
    AutoMatchThreshold = 95,     // minimum score for auto-match confidence
    AutoMatchScoreGap = 10,      // minimum gap over second-best candidate

    // Resilience (rate limiting & retries)
    MaxConcurrency = 5,          // max parallel API requests during batch operations
    MaxRetries = 3,              // retry attempts on HTTP 429 with exponential backoff

    // Type hierarchy (P279 subclass walking)
    TypeHierarchyDepth = 0,      // 0 = direct P31 match only (default, fast)
                                  // 5 = walk up to 5 levels of P279 (subclass of)
                                  // e.g., "novel" matches "literary work" at depth 1

    // Display-friendly labels (include Wikipedia sitelink titles in scoring)
    IncludeSitelinkLabels = false,  // opt-in: matches "Frankenstein" vs formal label

    // Unique identifier shortcut (score 100 when a unique ID matches exactly)
    // UniqueIdProperties = new HashSet<string> { "P213", "P214", ... } // defaults included
});
```

### Bring Your Own HttpClient

For connection pooling, custom handlers, or dependency injection:

```csharp
// With IHttpClientFactory (recommended for long-lived applications)
var httpClient = httpClientFactory.CreateClient("Wikidata");
using var reconciler = new WikidataReconciler(httpClient, options);
```

When you pass your own `HttpClient`, the reconciler will not dispose it. When the reconciler creates its own (via the parameterless or options-only constructors), it owns and disposes the client.

### Caching

The library deliberately does not include a built-in cache to avoid stale data issues (a [known problem](https://github.com/wetneb/openrefine-wikibase/issues/146) in the upstream Python implementation). Instead, use .NET's standard `HttpClient` middleware pattern to add caching at the HTTP layer:

```csharp
// Example: in-memory caching via a DelegatingHandler
public class CachingHandler : DelegatingHandler
{
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _ttl;

    public CachingHandler(IMemoryCache cache, TimeSpan ttl)
    {
        _cache = cache;
        _ttl = ttl;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var key = request.RequestUri?.ToString() ?? "";
        if (_cache.TryGetValue(key, out HttpResponseMessage? cached))
            return cached!;

        var response = await base.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
            _cache.Set(key, response, _ttl);
        return response;
    }
}

// Wire it up
var cache = new MemoryCache(new MemoryCacheOptions());
var handler = new CachingHandler(cache, TimeSpan.FromMinutes(30))
{
    InnerHandler = new HttpClientHandler()
};
var httpClient = new HttpClient(handler);
using var reconciler = new WikidataReconciler(httpClient, options);
```

This gives you full control over TTL, storage backend, and invalidation strategy.

### ASP.NET Core Integration

Install the companion package for DI registration and W3C API hosting:

```
dotnet add package Tuvima.WikidataReconciliation.AspNetCore
```

Register with dependency injection:

```csharp
services.AddWikidataReconciliation(options =>
{
    options.Language = "en";
    options.UserAgent = "MyApp/1.0 (contact@example.com)";
});
```

Host a W3C Reconciliation Service API endpoint (compatible with OpenRefine and Google Sheets):

```csharp
app.MapReconciliation("/api/reconcile", options =>
{
    options.ServiceName = "My Wikidata Service";
    options.DefaultTypes =
    [
        new("Q5", "Human"),
        new("Q515", "City"),
        new("Q7725634", "Literary work")
    ];
});
```

This registers the full W3C spec endpoints:

| Endpoint | Purpose |
|---|---|
| `GET /api/reconcile` | Service manifest (name, capabilities, default types) |
| `POST /api/reconcile` | Reconciliation queries (single or batch) |
| `GET /api/reconcile/suggest/entity?prefix=...` | Entity autocomplete |
| `GET /api/reconcile/suggest/property?prefix=...` | Property autocomplete |
| `GET /api/reconcile/suggest/type?prefix=...` | Type/class autocomplete |
| `GET /api/reconcile/preview?id=Q42` | HTML preview card (thumbnail, description, link) |

All endpoints respect the `Accept-Language` header — a French browser automatically gets French labels without any extra configuration.

Or register manually without the companion package (zero extra dependencies):

```csharp
services.AddHttpClient("Wikidata", c =>
    c.DefaultRequestHeaders.UserAgent.ParseAdd("MyApp/1.0 (contact@example.com)"));

services.AddSingleton(sp => new WikidataReconciler(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("Wikidata"),
    new WikidataReconcilerOptions { Language = "en" }));
```

### Custom Wikibase Instances

The library works with any Wikibase instance, not just Wikidata. Point it at your custom endpoint and configure the type property ID:

```csharp
var reconciler = new WikidataReconciler(new WikidataReconcilerOptions
{
    ApiEndpoint = "https://my-wikibase.example.com/w/api.php",
    TypePropertyId = "P1",  // your instance's "instance of" property
});
```

## How It Works

The reconciliation pipeline has four stages:

### 1. Dual Search

Two MediaWiki API searches run concurrently:

- **`wbsearchentities`** (autocomplete): Matches labels and aliases directly. Fast and precise for well-known names.
- **`action=query&list=search`** (full-text): Searches across all entity content. Finds items like "1984" where the label ("Nineteen Eighty-Four") differs from the query.

Results are merged (full-text first, then autocomplete) and deduplicated. This dual strategy is critical for recall. Queries are truncated at 250 characters to avoid silent failures from the MediaWiki API.

### 2. Entity Fetching

Candidate entities are fetched via `wbgetentities` in batches of up to 50, retrieving labels, descriptions, aliases, and claims in the requested language. The library respects the Wikidata statement rank hierarchy:

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

If a type constraint was specified and the entity has no type claims, the score is halved.

The **auto-match** flag is set on the top result when:
- Score > (95 - 5 * number of properties), AND
- Score > second-best score + 10

### 4. Type Filtering

Candidates are checked against the requested type (P31 direct match) and excluded types. By default, the library uses direct P31 matching for speed. Set `TypeHierarchyDepth` to walk the P279 (subclass of) hierarchy — for example, with depth 3, a "novel" (Q8261) entity matches a query for "literary work" (Q7725634) because novel is a subclass of literary work. The subclass hierarchy is cached in memory within the reconciler's lifetime to avoid redundant API calls.

## Result Object

Each `ReconciliationResult` contains:

| Property | Type | Description |
|---|---|---|
| `Id` | `string` | Wikidata entity ID (e.g., `"Q42"`) |
| `Name` | `string` | Entity label in the requested language |
| `Description` | `string?` | Entity description in the requested language |
| `Score` | `double` | Confidence score from 0 to 100 |
| `Match` | `bool` | `true` if this is a confident automatic match |
| `Types` | `IReadOnlyList<string>?` | P31 (instance of) type IDs, if available |
| `MatchedLabel` | `string?` | The label/alias text that best matched the query (may be in a different language than `Name`) |
| `Breakdown` | `ScoreBreakdown?` | Detailed scoring breakdown (see [Score Breakdown](#score-breakdown-explainability)) |

The `ScoreBreakdown` contains:

| Property | Type | Description |
|---|---|---|
| `LabelScore` | `double` | Best fuzzy match score across labels/aliases in all languages (0-100) |
| `MatchedLabel` | `string?` | The label/alias text that produced the best fuzzy match |
| `PropertyScores` | `IReadOnlyDictionary<string, double>` | Per-property match scores, keyed by property ID |
| `TypeMatched` | `bool?` | Whether entity matched the type constraint (`null` if none) |
| `WeightedScore` | `double` | Weighted formula result before any type penalty |
| `TypePenaltyApplied` | `bool` | Whether the score was halved due to missing type |
| `UniqueIdMatch` | `bool` | Whether score was set to 100 via a unique identifier match |

Results are sorted by score descending, with QID number as a tiebreaker (lower QID = older, more established entity).

## What's New by Version

### v0.9.0

- **Public EntityLabel setter** — `WikidataValue.EntityLabel` is now a public setter (was `internal set`). Consumers can set entity labels directly without needing workaround methods like `RehydrateEntityLabelsAsync` for custom label resolution scenarios.

### v0.8.0

- **Automatic entity label resolution in GetPropertiesAsync** — `GetPropertiesAsync` now automatically resolves `EntityLabel` for all entity-reference property values (e.g., P50 author → "Frank Herbert" instead of raw QID "Q44413"). Labels are batch-fetched and respect the `language` parameter with fallback. Eliminates the need for consumers to make a separate `GetEntitiesAsync` call to resolve labels. Breaking change: the `resolveEntityLabels` parameter from v0.7.0 has been removed since resolution is now always enabled.

### v0.7.0

- **Entity label resolution for GetPropertiesAsync** — new `resolveEntityLabels` parameter on `GetPropertiesAsync` auto-resolves entity-valued claims to human-readable labels, matching the existing behavior on `GetEntitiesAsync`. Previously, entity references (e.g., P179 series → Q5765655) returned only raw QIDs with null `EntityLabel`.

### v0.6.0

- **Type-filtered search** — when types are specified, CirrusSearch `haswbstatement:P31=QID` runs at query time for dramatically better type recall. New `Types` property accepts multiple types with OR logic. Per-request `TypeHierarchyDepth` override for P279 subclass walking.
- **Multi-language reconciliation** — new `Languages` property searches concurrently in multiple languages and deduplicates by QID. Solves the multilingual matching problem without multiple API calls.
- **Entity label resolution** — `GetEntitiesAsync(qids, resolveEntityLabels: true)` auto-resolves entity-valued claims (e.g., P50 author → Q42) to human-readable labels in the requested language. `WikidataValue.EntityLabel` property and improved `ToDisplayString()`.
- **Work-to-edition pivoting** — `GetEditionsAsync` follows P747 (has edition or translation) with optional P31 type filtering. `GetWorkForEditionAsync` navigates the reverse direction via P629.
- **Diacritic-aware search** — `DiacriticInsensitive` flag strips accents so "Shōgun" matches "Shogun". Runs additional ASCII-normalized searches for better recall.
- **Display-friendly labels** — `IncludeSitelinkLabels` option adds Wikipedia sitelink titles to the scoring pool. Matches common names like "Frankenstein" instead of "Frankenstein; or, The Modern Prometheus".
- **Wikipedia summary language fallback** — new overload tries multiple language editions, returning the first available. `WikipediaSummary.Language` indicates which edition was used.
- **Query pre-cleaning** — `Cleaners` pipeline strips noise like "(Unabridged)", "S01E02", "Vol. 3" before search. Built-in `QueryCleaners` presets included.
- **Pseudonym detection** — `GetAuthorPseudonymsAsync` finds P742 pseudonyms for authors, navigating through P50 author claims.
- **Caching infrastructure** — `CachingDelegatingHandler` abstract base class provides a zero-dependency template for HTTP-level caching with any backend.

### v0.5.0

- **Wikipedia section content** — new `GetWikipediaSectionsAsync` returns the table of contents for Wikipedia articles, and `GetWikipediaSectionContentAsync` fetches specific sections as clean plain text. Pull plot summaries, career details, themes, or any section — generalized, not tied to any entity type.
- **Staleness detection** — `WikidataEntityInfo` now includes `LastRevisionId` and `Modified` on every entity fetch (zero extra API calls). New `GetRevisionIdsAsync` method provides an ultra-lightweight way to check if cached entities have changed — returns only revision IDs and timestamps without fetching labels, claims, or descriptions.

### v0.4.0

- **Cross-language label scoring** — the scorer now compares your query against labels and aliases in every language, not just English. Searching "Die Verwandlung" now correctly finds Q184222 (The Metamorphosis) with a high score, instead of scoring near 0% against only the English label.
- **MatchedLabel property** — each result now tells you which label or alias text actually matched the query. Useful when the best match came from a different language than the display name.

### v0.3.0

- **External ID lookup** — find entities by ISBN, IMDB ID, VIAF, ORCID, or any other external identifier, without fuzzy matching
- **Value formatting** — `ToDisplayString()` on claim values gives human-readable output (e.g., "11 March 1952" for dates, "51.5074, -0.1278" for coordinates)
- **Property labels** — resolve property IDs like P569 to names like "date of birth"
- **Entity images** — get Wikimedia Commons image URLs from P18 claims
- **Wikipedia summaries** — fetch article summaries with thumbnail and description from the Wikipedia REST API
- **W3C Reconciliation API** — ASP.NET Core middleware that hosts a full W3C-compatible endpoint, including entity/property/type suggest and HTML preview cards
- **Accept-Language support** — W3C endpoints automatically use the browser's language
- **Entity change monitoring** — check if watched entities have been modified recently, useful for cache invalidation
- **maxlag support** — every API request includes the Wikimedia maxlag parameter for polite bot behavior

### v0.2.0

- **Data extension** — fetch full entity data (labels, descriptions, aliases, claims) after reconciliation
- **Qualifiers** — access qualifier values on claims (e.g., start/end dates on "educated at")
- **P279 subclass matching** — optionally walk the "subclass of" hierarchy so "novel" matches "literary work"
- **Specific property fetching** — fetch only the properties you need instead of everything
- **Wikipedia URLs** — resolve entities to Wikipedia article links in any language
- **Batch reconciliation** — reconcile many queries in parallel with configurable concurrency
- **Exclude types** — filter out unwanted entity types from results
- **Custom Wikibase support** — point the library at any Wikibase instance, not just Wikidata

### v0.1.0

- **Core reconciliation** — match text to Wikidata entities using dual search (autocomplete + full-text)
- **Fuzzy matching** — token-sort-ratio scoring based on Levenshtein distance
- **Type filtering** — constrain results to entities of a specific P31 type
- **Property constraints** — boost scoring with known property values (items, strings, dates, quantities, coordinates, URLs)
- **Property paths** — chain properties like "P19/P17" (place of birth → country)
- **Score breakdown** — detailed explanation of how each score was computed
- **Unique ID shortcut** — instant score of 100 when an authority ID (VIAF, ISNI, etc.) matches exactly
- **Streaming batch** — `IAsyncEnumerable` results for large datasets with progress reporting
- **Suggest/autocomplete** — entity search for interactive type-ahead UIs
- **Retry with backoff** — automatic retry on HTTP 429 with exponential backoff
- **Zero dependencies** — only uses `System.Text.Json` built into .NET
- **AOT compatible** — works with native AOT compilation and trimming

## Acknowledgements

The reconciliation algorithms in this library (dual-search strategy, scoring formula, fuzzy matching approach, type checking, and property matching) are based on [openrefine-wikibase](https://github.com/wetneb/openrefine-wikibase) by [Antonin Delpeuch](https://github.com/wetneb), licensed under the MIT License.

> Antonin Delpeuch. "A survey of OpenRefine reconciliation services." [arXiv:1906.08092](https://arxiv.org/abs/1906.08092)

The configurable Wikibase endpoint support was informed by the [nfdi4culture fork](https://gitlab.com/nfdi4culture/openrefine-reconciliation-services/openrefine-wikibase).

This is an independent C# implementation. No code was copied from the original Python project. The algorithms were re-implemented from the documented behavior and public API specifications. See the [NOTICE](NOTICE) file for full attribution details.

## License

MIT. See [LICENSE](LICENSE) for the full text.
