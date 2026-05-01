# ASP.NET Core Integration

Host a W3C Reconciliation Service API compatible with OpenRefine and Google Sheets.

## Installation

```
dotnet add package Tuvima.Wikidata.AspNetCore
```

## DI Registration

```csharp
services.AddWikidataReconciliation(options =>
{
    options.Language = "en";
    options.UserAgent = "MyApp/1.0 (contact@example.com)";
});
```

As of v2.0, `AddWikidataReconciliation` also registers each **sub-service** as a singleton, so consumers can inject a narrow slice of the API instead of depending on the whole facade:

```csharp
public sealed class MyEntityPipeline(
    Tuvima.Wikidata.Services.LabelsService labels,
    Tuvima.Wikidata.Services.AuthorsService authors,
    Tuvima.Wikidata.Services.BridgeResolutionService bridge)
{
    public async Task<string?> ResolveBookAsync(string isbn)
    {
        var result = await bridge.ResolveAsync(new BridgeResolutionRequest
        {
            CorrelationKey = isbn,
            MediaKind = BridgeMediaKind.Book,
            BridgeIds = new Dictionary<string, string> { ["isbn13"] = isbn },
            RollupTarget = BridgeRollupTarget.CanonicalWork
        });

        return result.Found ? result.SelectedCandidate?.Label : null;
    }
}
```

All focused sub-services (`ReconciliationService`, `EntityService`, `WikipediaService`, `EditionService`, `ChildrenService`, `AuthorsService`, `LabelsService`, `PersonsService`, `BridgeResolutionService`) resolve from the same root `WikidataReconciler`, so they share the same `HttpClient`, options, provider-safe HTTP pipeline, cache hook, diagnostics object, and host limiters.

## Endpoint Mapping

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

## Endpoints

| Endpoint | Purpose |
|---|---|
| `GET /api/reconcile` | Service manifest (name, capabilities, default types) |
| `POST /api/reconcile` | Reconciliation queries (single or batch) |
| `GET /api/reconcile/suggest/entity?prefix=...` | Entity autocomplete |
| `GET /api/reconcile/suggest/property?prefix=...` | Property autocomplete |
| `GET /api/reconcile/suggest/type?prefix=...` | Type/class autocomplete |
| `GET /api/reconcile/preview?id=Q42` | HTML preview card (thumbnail, description, link) |

All endpoints respect the `Accept-Language` header — a French browser automatically gets French labels without extra configuration.

As of v2.6.0, POST batch reconciliation fans out independent queries in parallel, while the shared reconciler request sender still enforces provider-safe per-host limits. The root `WikidataReconciler.Diagnostics` object can be injected to include request counts, cache hits/misses, retries, 429s, throttled waits, and typed provider failures in integration reports.

## Manual Registration (No Companion Package)

Register the facade manually with zero extra dependencies:

```csharp
services.AddHttpClient("Wikidata", c =>
    c.DefaultRequestHeaders.UserAgent.ParseAdd("MyApp/1.0 (contact@example.com)"));

services.AddSingleton(sp => new WikidataReconciler(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("Wikidata"),
    new WikidataReconcilerOptions { Language = "en" }));
```

To also inject individual sub-services, add delegating registrations:

```csharp
services.AddSingleton(sp => sp.GetRequiredService<WikidataReconciler>().Reconcile);
services.AddSingleton(sp => sp.GetRequiredService<WikidataReconciler>().Entities);
services.AddSingleton(sp => sp.GetRequiredService<WikidataReconciler>().Labels);
services.AddSingleton(sp => sp.GetRequiredService<WikidataReconciler>().Authors);
services.AddSingleton(sp => sp.GetRequiredService<WikidataReconciler>().Bridge);
// …plus Wikipedia, Editions, Children, Persons as needed
```

Or just call `AddWikidataReconciliation()` from the companion package — that does all of this for you.
