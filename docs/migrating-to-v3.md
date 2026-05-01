# Migrating to v3.0

v3.0 is a breaking release for bridge/identity workflows. The older public Stage2 compatibility layer has been removed and replaced by `reconciler.Bridge`.

## What Was Removed

- `WikidataReconciler.Stage2`
- `Stage2Service`
- `IStage2Request`
- `BridgeStage2Request`
- `MusicStage2Request`
- `TextStage2Request`
- `Stage2Request`
- `Stage2Result`
- `Stage2MatchedStrategy`
- `EditionPivotRule`
- `RankingHint`

## Replacement

Use `BridgeResolutionRequest` for bridge ID, title fallback, music, comics, book, audiobook, TV, and movie identity requests.

```csharp
var result = await reconciler.Bridge.ResolveAsync(new BridgeResolutionRequest
{
    CorrelationKey = "row-42",
    MediaKind = BridgeMediaKind.Book,
    BridgeIds = new Dictionary<string, string>
    {
        ["isbn13"] = "9780441172719",
        ["open_library_id"] = "OL893415W"
    },
    Title = "Dune",
    Creator = "Frank Herbert",
    RollupTarget = BridgeRollupTarget.ReturnWorkAndEdition
});
```

The result now carries:

- `SelectedCandidate` and `Candidates`
- typed `Status` and `FailureKind`
- `Diagnostics`
- `CanonicalRollup`
- `Series`
- `Relationships`

## Consumer Ownership

`Tuvima.Wikidata` resolves Wikidata/Wikipedia identity, ranks candidates, performs canonical work rollups, extracts relationships, and reports diagnostics. Applications still own ingestion orchestration, local persistence, retail API calls, artwork/file storage, and product-specific merge decisions.
