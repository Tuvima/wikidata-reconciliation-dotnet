using System.Collections.Concurrent;

namespace Tuvima.Wikidata;

/// <summary>
/// Thread-safe counters emitted by the shared Wikimedia HTTP pipeline.
/// </summary>
public sealed class WikidataDiagnostics
{
    private readonly ConcurrentDictionary<string, long> _requestCountByHost = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _requestCountByEndpoint = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _failuresByKind = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, BatchAccumulator> _batchMetricsByEndpoint = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<WikidataFailure> _recentFailures = new();
    private long _cacheHits;
    private long _cacheMisses;
    private long _throttledWaits;
    private long _throttledWaitTicks;
    private long _rateLimitResponses;
    private long _retryCount;
    private long _coalescedRequests;
    private long _latencyTicks;
    private long _latencyCount;

    internal void RecordRequest(string host, string endpoint, TimeSpan latency)
    {
        _requestCountByHost.AddOrUpdate(host, 1, static (_, value) => value + 1);
        _requestCountByEndpoint.AddOrUpdate($"{host}:{endpoint}", 1, static (_, value) => value + 1);
        Interlocked.Add(ref _latencyTicks, latency.Ticks);
        Interlocked.Increment(ref _latencyCount);
    }

    internal void RecordCacheHit() => Interlocked.Increment(ref _cacheHits);

    internal void RecordCacheMiss() => Interlocked.Increment(ref _cacheMisses);

    internal void RecordThrottledWait(TimeSpan wait)
    {
        Interlocked.Increment(ref _throttledWaits);
        Interlocked.Add(ref _throttledWaitTicks, wait.Ticks);
    }

    internal void RecordRateLimitResponse() => Interlocked.Increment(ref _rateLimitResponses);

    internal void RecordRetry() => Interlocked.Increment(ref _retryCount);

    internal void RecordCoalescedRequest() => Interlocked.Increment(ref _coalescedRequests);

    internal void RecordBatch(string endpoint, int size)
    {
        if (size <= 0)
            return;

        _batchMetricsByEndpoint
            .GetOrAdd(endpoint, static _ => new BatchAccumulator())
            .Record(size);
    }

    internal void RecordFailure(WikidataFailureKind kind, string? endpoint, string message, string? entityId = null)
    {
        _failuresByKind.AddOrUpdate(kind.ToString(), 1, static (_, value) => value + 1);
        _recentFailures.Enqueue(new WikidataFailure(kind, entityId, endpoint, message));

        while (_recentFailures.Count > 100 && _recentFailures.TryDequeue(out _))
        {
        }
    }

    public WikidataDiagnosticsSnapshot GetSnapshot()
    {
        var latencyCount = Interlocked.Read(ref _latencyCount);
        var latencyTicks = Interlocked.Read(ref _latencyTicks);
        var throttledWaits = Interlocked.Read(ref _throttledWaits);
        var throttledWaitTicks = Interlocked.Read(ref _throttledWaitTicks);

        return new WikidataDiagnosticsSnapshot
        {
            RequestCountByHost = ToDictionary(_requestCountByHost),
            RequestCountByEndpoint = ToDictionary(_requestCountByEndpoint),
            CacheHits = Interlocked.Read(ref _cacheHits),
            CacheMisses = Interlocked.Read(ref _cacheMisses),
            ThrottledWaits = throttledWaits,
            TotalThrottledWait = TimeSpan.FromTicks(throttledWaitTicks),
            RateLimitResponses = Interlocked.Read(ref _rateLimitResponses),
            RetryCount = Interlocked.Read(ref _retryCount),
            CoalescedRequests = Interlocked.Read(ref _coalescedRequests),
            BatchMetricsByEndpoint = _batchMetricsByEndpoint.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.ToMetrics(),
                StringComparer.OrdinalIgnoreCase),
            AverageLatency = latencyCount == 0
                ? TimeSpan.Zero
                : TimeSpan.FromTicks(latencyTicks / latencyCount),
            FailuresByKind = ToDictionary(_failuresByKind),
            RecentFailures = _recentFailures.ToArray()
        };
    }

    private static IReadOnlyDictionary<string, long> ToDictionary(ConcurrentDictionary<string, long> source)
        => source.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

    private sealed class BatchAccumulator
    {
        private long _batchCount;
        private long _totalItems;
        private int _maxBatchSize;

        public void Record(int size)
        {
            Interlocked.Increment(ref _batchCount);
            Interlocked.Add(ref _totalItems, size);

            int current;
            do
            {
                current = _maxBatchSize;
                if (size <= current)
                    return;
            }
            while (Interlocked.CompareExchange(ref _maxBatchSize, size, current) != current);
        }

        public WikidataBatchMetrics ToMetrics()
        {
            var batchCount = Interlocked.Read(ref _batchCount);
            var totalItems = Interlocked.Read(ref _totalItems);
            return new WikidataBatchMetrics(
                batchCount,
                totalItems,
                _maxBatchSize,
                batchCount == 0 ? 0 : (double)totalItems / batchCount);
        }
    }
}

public sealed class WikidataDiagnosticsSnapshot
{
    public IReadOnlyDictionary<string, long> RequestCountByHost { get; init; } = new Dictionary<string, long>();

    public IReadOnlyDictionary<string, long> RequestCountByEndpoint { get; init; } = new Dictionary<string, long>();

    public long CacheHits { get; init; }

    public long CacheMisses { get; init; }

    public long ThrottledWaits { get; init; }

    public TimeSpan TotalThrottledWait { get; init; }

    public long RateLimitResponses { get; init; }

    public long RetryCount { get; init; }

    public long CoalescedRequests { get; init; }

    public IReadOnlyDictionary<string, WikidataBatchMetrics> BatchMetricsByEndpoint { get; init; }
        = new Dictionary<string, WikidataBatchMetrics>();

    public TimeSpan AverageLatency { get; init; }

    public IReadOnlyDictionary<string, long> FailuresByKind { get; init; } = new Dictionary<string, long>();

    public IReadOnlyList<WikidataFailure> RecentFailures { get; init; } = [];
}

public sealed record WikidataBatchMetrics(
    long BatchCount,
    long TotalItems,
    int MaxBatchSize,
    double AverageBatchSize);
