namespace Tuvima.Wikidata;

/// <summary>
/// Pluggable raw-response cache used by the shared HTTP pipeline.
/// </summary>
public interface IWikidataResponseCache
{
    ValueTask<string?> GetAsync(WikidataResponseCacheKey key, CancellationToken cancellationToken = default);

    ValueTask SetAsync(
        WikidataResponseCacheKey key,
        string response,
        TimeSpan ttl,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Canonical cache key metadata for a Wikimedia response.
/// </summary>
public sealed record WikidataResponseCacheKey(string Host, string Endpoint, string Key);
