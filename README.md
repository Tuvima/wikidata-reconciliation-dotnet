# Tuvima.WikidataReconciliation

A .NET library for reconciling data against Wikidata entities. The first .NET Wikidata reconciliation library.

## Features

- **Dual search**: Uses both `wbsearchentities` (label/alias autocomplete) and `action=query&list=search` (full-text) concurrently, then merges results. This finds items like "1984" (the novel) that label-only search misses.
- **Fuzzy matching**: Token-sort-ratio using Levenshtein distance with Unicode normalization.
- **Property-based scoring**: Weighted scoring with support for string, item, quantity, time, URL, coordinate, and external-id matching.
- **Type filtering**: Filter by P31 (instance of) with support for type exclusion.
- **Rank-aware**: Respects Wikidata statement ranks (preferred > normal > deprecated).
- **Custom Wikibase support**: Configurable API endpoint and type/subclass property IDs.
- **Zero dependencies**: Only requires `System.Text.Json` (built into .NET).
- **AOT compatible**: Source-generated JSON serialization for trimming and AOT support.

## Installation

```
dotnet add package Tuvima.WikidataReconciliation
```

## Quick Start

```csharp
using Tuvima.WikidataReconciliation;

// Simple reconciliation
using var reconciler = new WikidataReconciler();
var results = await reconciler.ReconcileAsync("Douglas Adams");
// results[0].Id == "Q42", results[0].Name == "Douglas Adams"

// With type constraint (Q5 = human)
var results = await reconciler.ReconcileAsync("Douglas Adams", "Q5");

// With properties for better scoring
var results = await reconciler.ReconcileAsync(new ReconciliationRequest
{
    Query = "Douglas Adams",
    Type = "Q5",
    Properties =
    [
        new PropertyConstraint("P27", "Q145"),  // country of citizenship: UK
    ]
});

// Batch reconciliation
var results = await reconciler.ReconcileBatchAsync([
    new ReconciliationRequest { Query = "Douglas Adams" },
    new ReconciliationRequest { Query = "Albert Einstein" },
]);
```

## Configuration

```csharp
var reconciler = new WikidataReconciler(new WikidataReconcilerOptions
{
    ApiEndpoint = "https://www.wikidata.org/w/api.php",
    Language = "en",
    UserAgent = "MyApp/1.0 (contact@example.com)",
    Timeout = TimeSpan.FromSeconds(30),
    TypePropertyId = "P31",      // configurable for custom Wikibase
    PropertyWeight = 0.4,        // weight for property matches (label = 1.0)
    AutoMatchThreshold = 95,     // score threshold for auto-match
    AutoMatchScoreGap = 10,      // min gap over second-best for auto-match
});
```

## Scoring Algorithm

Based on [openrefine-wikibase](https://github.com/wetneb/openrefine-wikibase) by Antonin Delpeuch (MIT License):

- **Label score**: Best fuzzy match (token-sort-ratio) across all labels and aliases
- **Property scores**: Type-specific matching (exact for IDs, fuzzy for strings, log-decay for quantities, precision-aware for dates, distance-based for coordinates)
- **Weighted average**: `(label * 1.0 + sum(property * 0.4)) / (1.0 + 0.4 * numProperties)`
- **Auto-match**: Score > (95 - 5 * numProperties) with 10+ point gap over second-best

## Target Frameworks

- .NET 8.0 (LTS)
- .NET 10.0

## License

MIT - See [LICENSE](LICENSE) and [NOTICE](NOTICE) for attribution details.
