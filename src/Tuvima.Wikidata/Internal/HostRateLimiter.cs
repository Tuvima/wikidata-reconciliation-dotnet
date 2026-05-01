namespace Tuvima.Wikidata.Internal;

internal sealed class HostRateLimiter : IDisposable
{
    private readonly SemaphoreSlim _concurrency;
    private readonly TimeSpan _minInterval;
    private readonly WikidataDiagnostics _diagnostics;
    private readonly object _gate = new();
    private DateTimeOffset _nextAvailable = DateTimeOffset.MinValue;

    public HostRateLimiter(ProviderRateLimitOptions options, WikidataDiagnostics diagnostics)
    {
        var maxConcurrent = Math.Clamp(options.MaxConcurrentRequests, 1, 1024);
        _concurrency = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        _minInterval = options.RequestsPerSecond <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(1 / options.RequestsPerSecond);
        _diagnostics = diagnostics;
    }

    public async ValueTask<IDisposable> WaitAsync(CancellationToken cancellationToken)
    {
        var wait = ReserveStartSlot();
        if (wait > TimeSpan.Zero)
        {
            _diagnostics.RecordThrottledWait(wait);
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }

        await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(_concurrency);
    }

    private TimeSpan ReserveStartSlot()
    {
        if (_minInterval <= TimeSpan.Zero)
            return TimeSpan.Zero;

        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var startAt = _nextAvailable > now ? _nextAvailable : now;
            _nextAvailable = startAt.Add(_minInterval);
            return startAt - now;
        }
    }

    public void Dispose() => _concurrency.Dispose();

    private sealed class Lease : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private bool _disposed;

        public Lease(SemaphoreSlim semaphore) => _semaphore = semaphore;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _semaphore.Release();
        }
    }
}
