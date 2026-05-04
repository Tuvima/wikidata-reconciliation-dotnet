# Series Manifest Retrieval

`reconciler.Series.GetManifestAsync(...)` builds an ordered, provenance-rich manifest of works linked to a Wikidata series entity. It is generic enough for books, comics, films, television, music, games, and other media where Wikidata uses the same relationship properties.

```csharp
using var reconciler = new WikidataReconciler();

var manifest = await reconciler.Series.GetManifestAsync("Q19610143"); // The Expanse

foreach (var item in manifest.Items)
{
    Console.WriteLine($"{item.RawSeriesOrdinal}: {item.Label} ({item.OrderSource})");
    Console.WriteLine($"  source: {string.Join(", ", item.SourceProperties)}");
}
```

## Properties Used

The manifest service uses existing Wikidata and Wikimedia infrastructure in the library. It does not use SPARQL.

| Property | Use |
|---|---|
| P179 | Incoming `part of the series` links from works to the series |
| P361 | Incoming `part of` links from works to the series |
| P527 | Outgoing `has part` links from the series or collection items |
| P1545 | `series ordinal` qualifiers for ordering |
| P155 | `follows` previous-work chain |
| P156 | `followed by` next-work chain |
| P577 | publication date fallback |

## Request Options

```csharp
var manifest = await reconciler.Series.GetManifestAsync(new SeriesManifestRequest
{
    SeriesQid = "Q19610143",
    Language = "en",
    IncludeCollections = true,
    ExpandCollections = true,
    IncludePublicationDate = true,
    IncludeDescriptions = false,
    MaxDepth = 2,
    MaxItems = 500
});
```

Collection expansion is factual only: if a discovered item has P527 children, it is treated as collection-like and can be expanded. The library does not decide whether short fiction should be collapsed, hidden, or displayed as missing; consuming applications own that product behavior.

## Ordering Confidence

Each `SeriesManifestItem.OrderSource` explains the strongest evidence used for ordering:

| Source | Meaning |
|---|---|
| `SeriesOrdinal` | P1545 ordinal was found and used |
| `PreviousNextChain` | P155/P156 chain positioned the item |
| `PublicationDate` | P577 publication date was used |
| `LabelFallback` | label sort was the only usable ordering evidence |
| `Mixed` | item was ordered with fallback evidence in a mixed-evidence manifest |
| `Unknown` | no useful ordering evidence was available |

`RawSeriesOrdinal` preserves the original Wikidata qualifier value. `ParsedSeriesOrdinal` is populated when the value can be parsed as a decimal, so values like `0.1`, `1.5`, `6.5`, and `9.5` sort correctly while string ordinals remain safe to inspect.

## Warnings

Wikidata series data can be incomplete or modeled inconsistently. Always inspect `manifest.Warnings` and `manifest.Completeness`.

Common warning codes include:

- `NoChildrenFound`
- `MissingOrdinals`
- `ConflictingOrdinals`
- `BrokenPreviousNextChain`
- `PreviousNextConflictsWithOrdinal`
- `MaxDepthReached`
- `MaxItemsReached`
- `DuplicateItem`
- `LabelFallbackOnly`

## The Expanse Example

```csharp
var manifest = await reconciler.Series.GetManifestAsync("Q19610143");

Console.WriteLine($"{manifest.SeriesLabel}: {manifest.Items.Count} items");
foreach (var warning in manifest.Warnings)
    Console.WriteLine($"{warning.Code}: {warning.Message}");
```

The Expanse is a useful real-world example because Wikidata may include novels, novellas, collections, and relationship links with varying completeness. The service returns the factual evidence it can find and exposes warnings/provenance so applications can choose how to present partial coverage.
