# Tuvima.WikidataReconciliation

A .NET library for reconciling text data against [Wikidata](https://www.wikidata.org/) entities. Given a name, title, or description, it finds the best-matching Wikidata item and returns a confidence score.

This is the first .NET Wikidata reconciliation library, filling a gap in the ecosystem where only Python and JavaScript implementations previously existed.

## What is Reconciliation?

Reconciliation is the process of matching messy, real-world text (like "Douglas Adams" or "1984") to structured entities in a knowledge base. For example:

| Input text | Matched entity | QID | Score |
|---|---|---|---|
| `"Douglas Adams"` | Douglas Adams | [Q42](https://www.wikidata.org/wiki/Q42) | 100 |
| `"United States of America"` | United States of America | [Q30](https://www.wikidata.org/wiki/Q30) | 100 |
| `"1984"` (with type: literary work) | Nineteen Eighty-Four | [Q208460](https://www.wikidata.org/wiki/Q208460) | 67 |

This is useful for data cleaning, entity linking, knowledge graph construction, and enriching datasets with structured Wikidata identifiers.

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

For interactive UIs with type-ahead search:

```csharp
var suggestions = await reconciler.SuggestAsync("Douglas");

foreach (var s in suggestions)
    Console.WriteLine($"{s.Id}: {s.Name} - {s.Description}");

// Q42: Douglas Adams - English author and humourist (1952-2001)
// Q485272: Douglas - city in Georgia, United States
// ...
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

Candidates are checked against the requested type (P31 direct match) and excluded types. The library uses direct P31 matching only (no SPARQL subclass traversal) to avoid timeout issues with broad type hierarchies.

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
| `Breakdown` | `ScoreBreakdown?` | Detailed scoring breakdown (see [Score Breakdown](#score-breakdown-explainability)) |

The `ScoreBreakdown` contains:

| Property | Type | Description |
|---|---|---|
| `LabelScore` | `double` | Best fuzzy match score across labels/aliases (0-100) |
| `PropertyScores` | `IReadOnlyDictionary<string, double>` | Per-property match scores, keyed by property ID |
| `TypeMatched` | `bool?` | Whether entity matched the type constraint (`null` if none) |
| `WeightedScore` | `double` | Weighted formula result before any type penalty |
| `TypePenaltyApplied` | `bool` | Whether the score was halved due to missing type |

Results are sorted by score descending, with QID number as a tiebreaker (lower QID = older, more established entity).

## Acknowledgements

The reconciliation algorithms in this library (dual-search strategy, scoring formula, fuzzy matching approach, type checking, and property matching) are based on [openrefine-wikibase](https://github.com/wetneb/openrefine-wikibase) by [Antonin Delpeuch](https://github.com/wetneb), licensed under the MIT License.

> Antonin Delpeuch. "A survey of OpenRefine reconciliation services." [arXiv:1906.08092](https://arxiv.org/abs/1906.08092)

The configurable Wikibase endpoint support was informed by the [nfdi4culture fork](https://gitlab.com/nfdi4culture/openrefine-reconciliation-services/openrefine-wikibase).

This is an independent C# implementation. No code was copied from the original Python project. The algorithms were re-implemented from the documented behavior and public API specifications. See the [NOTICE](NOTICE) file for full attribution details.

## License

MIT. See [LICENSE](LICENSE) for the full text.
