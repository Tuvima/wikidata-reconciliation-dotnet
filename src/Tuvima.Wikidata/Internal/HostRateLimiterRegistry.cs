using System.Collections.Concurrent;

namespace Tuvima.Wikidata.Internal;

internal sealed class HostRateLimiterRegistry : IDisposable
{
    private readonly WikidataReconcilerOptions _options;
    private readonly WikidataDiagnostics _diagnostics;
    private readonly ConcurrentDictionary<string, HostRateLimiter> _limiters = new(StringComparer.OrdinalIgnoreCase);

    public HostRateLimiterRegistry(WikidataReconcilerOptions options, WikidataDiagnostics diagnostics)
    {
        _options = options;
        _diagnostics = diagnostics;
    }

    public ValueTask<IDisposable> WaitAsync(string host, CancellationToken cancellationToken)
    {
        var limiter = _limiters.GetOrAdd(host, CreateLimiter);
        return limiter.WaitAsync(cancellationToken);
    }

    private HostRateLimiter CreateLimiter(string host)
    {
        var policy = GetPolicy(host);
        return new HostRateLimiter(policy, _diagnostics);
    }

    private ProviderRateLimitOptions GetPolicy(string host)
    {
        if (host.Equals("www.wikidata.org", StringComparison.OrdinalIgnoreCase))
            return _options.WikidataRateLimit;

        if (host.Equals("commons.wikimedia.org", StringComparison.OrdinalIgnoreCase))
            return _options.CommonsRateLimit;

        if (host.EndsWith(".wikipedia.org", StringComparison.OrdinalIgnoreCase))
            return _options.WikipediaRateLimit;

        return _options.DefaultRateLimit;
    }

    public void Dispose()
    {
        foreach (var limiter in _limiters.Values)
            limiter.Dispose();
    }
}
