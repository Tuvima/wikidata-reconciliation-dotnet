using Microsoft.Extensions.DependencyInjection;
using Tuvima.Wikidata.Services;

namespace Tuvima.Wikidata.AspNetCore;

/// <summary>
/// Extension methods for registering <see cref="WikidataReconciler"/> and its v2.0.0
/// sub-services with dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="WikidataReconciler"/> as a singleton with a named HttpClient,
        /// along with each sub-service (<see cref="ReconciliationService"/>, <see cref="EntityService"/>,
        /// <see cref="WikipediaService"/>, <see cref="EditionService"/>, <see cref="ChildrenService"/>,
    /// <see cref="AuthorsService"/>, <see cref="LabelsService"/>, <see cref="BridgeResolutionService"/>).
    /// <para>
    /// Advanced consumers can inject a specific sub-service directly without going through the facade.
    /// </para>
    /// </summary>
    public static IServiceCollection AddWikidataReconciliation(
        this IServiceCollection services,
        Action<WikidataReconcilerOptions>? configure = null)
    {
        var options = new WikidataReconcilerOptions();
        configure?.Invoke(options);

        services.AddHttpClient("Tuvima.Wikidata", client =>
        {
            client.Timeout = options.Timeout;
            client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        });

        services.AddSingleton(sp =>
        {
            var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("Tuvima.Wikidata");
            return new WikidataReconciler(httpClient, options);
        });

        // Sub-service registrations — each resolves from the facade so all share the same
        // context (HttpClient, provider-safe HTTP pipeline, diagnostics, cache hook, options).
        services.AddSingleton(sp => sp.GetRequiredService<WikidataReconciler>().Reconcile);
        services.AddSingleton(sp => sp.GetRequiredService<WikidataReconciler>().Entities);
        services.AddSingleton(sp => sp.GetRequiredService<WikidataReconciler>().Wikipedia);
        services.AddSingleton(sp => sp.GetRequiredService<WikidataReconciler>().Editions);
        services.AddSingleton(sp => sp.GetRequiredService<WikidataReconciler>().Children);
        services.AddSingleton(sp => sp.GetRequiredService<WikidataReconciler>().Authors);
        services.AddSingleton(sp => sp.GetRequiredService<WikidataReconciler>().Labels);
        services.AddSingleton(sp => sp.GetRequiredService<WikidataReconciler>().Persons);
        services.AddSingleton(sp => sp.GetRequiredService<WikidataReconciler>().Bridge);

        return services;
    }
}
