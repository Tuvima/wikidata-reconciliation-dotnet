using System.Collections.Concurrent;

namespace Tuvima.Wikidata;

/// <summary>
/// Process-local response cache used by default. Applications can replace it with a durable cache.
/// </summary>
public sealed class InMemoryWikidataResponseCache : IWikidataResponseCache
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public ValueTask<string?> GetAsync(WikidataResponseCacheKey key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_entries.TryGetValue(key.Key, out var entry))
            return ValueTask.FromResult<string?>(null);

        if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _entries.TryRemove(key.Key, out _);
            return ValueTask.FromResult<string?>(null);
        }

        return ValueTask.FromResult<string?>(entry.Response);
    }

    public ValueTask SetAsync(
        WikidataResponseCacheKey key,
        string response,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (ttl <= TimeSpan.Zero)
            return ValueTask.CompletedTask;

        _entries[key.Key] = new Entry(response, DateTimeOffset.UtcNow.Add(ttl));
        return ValueTask.CompletedTask;
    }

    private sealed record Entry(string Response, DateTimeOffset ExpiresAt);
}
