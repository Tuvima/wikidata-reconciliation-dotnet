using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace Tuvima.Wikidata.Internal;

/// <summary>
/// Shared Wikimedia HTTP pipeline: per-host throttling, retry/backoff, Retry-After,
/// response caching, in-flight coalescing, request logging, and diagnostics.
/// </summary>
internal sealed class ResilientHttpClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly WikidataReconcilerOptions _options;
    private readonly WikidataDiagnostics _diagnostics;
    private readonly HostRateLimiterRegistry _hostLimiters;
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _inFlight = new(StringComparer.Ordinal);
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly SemaphoreSlim? _legacyConcurrencyLimiter;

    public ResilientHttpClient(
        HttpClient httpClient,
        WikidataReconcilerOptions options,
        WikidataDiagnostics diagnostics,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _httpClient = httpClient;
        _options = options;
        _diagnostics = diagnostics;
        _hostLimiters = new HostRateLimiterRegistry(options, diagnostics);
        _delayAsync = delayAsync ?? Task.Delay;
    }

    public ResilientHttpClient(HttpClient httpClient, int maxRetries, int maxLag, SemaphoreSlim concurrencyLimiter)
        : this(
            httpClient,
            new WikidataReconcilerOptions
            {
                MaxRetries = maxRetries,
                MaxLag = maxLag,
                RetryJitterRatio = 0,
                EnableResponseCaching = false,
                WikidataRateLimit = ProviderRateLimitOptions.Unthrottled,
                WikipediaRateLimit = ProviderRateLimitOptions.Unthrottled,
                CommonsRateLimit = ProviderRateLimitOptions.Unthrottled,
                DefaultRateLimit = ProviderRateLimitOptions.Unthrottled
            },
            new WikidataDiagnostics())
    {
        _legacyConcurrencyLimiter = concurrencyLimiter;
    }

    public async Task<string> GetStringAsync(
        string url,
        CancellationToken cancellationToken,
        bool applyMaxLag = true)
    {
        var requestUrl = applyMaxLag ? AppendMaxLag(url) : url;
        var request = ProviderRequest.Create(requestUrl);

        if (!_options.EnableRequestCoalescing)
            return await GetStringCoreAsync(request, cancellationToken).ConfigureAwait(false);

        var lazy = _inFlight.GetOrAdd(
            request.CacheKey.Key,
            _ => new Lazy<Task<string>>(
                () => GetStringCoreAsync(request, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));

        if (lazy.IsValueCreated)
            _diagnostics.RecordCoalescedRequest();

        try
        {
            return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (lazy.IsValueCreated && lazy.Value.IsCompleted)
                _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(request.CacheKey.Key, lazy));
        }
    }

    private async Task<string> GetStringCoreAsync(ProviderRequest request, CancellationToken cancellationToken)
    {
        var cache = _options.EnableResponseCaching && request.IsCacheable()
            ? _options.ResponseCache
            : null;

        if (cache is not null)
        {
            var cached = await cache.GetAsync(request.CacheKey, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                _diagnostics.RecordCacheHit();
                Log(request, attempt: 0, statusCode: null, latency: TimeSpan.Zero, fromCache: true, failureKind: null);
                return cached;
            }

            _diagnostics.RecordCacheMiss();
        }

        for (var attempt = 0; ; attempt++)
        {
            TimeSpan? retryDelay = null;
            var stopwatch = Stopwatch.StartNew();
            HttpStatusCode? statusCode = null;

            try
            {
                using var limiterLease = await WaitForLimiterAsync(request.Host, cancellationToken)
                    .ConfigureAwait(false);

                using var response = await _httpClient
                    .GetAsync(request.Uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                statusCode = response.StatusCode;
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    _diagnostics.RecordRateLimitResponse();

                if (ShouldRetry(response.StatusCode) && attempt < _options.MaxRetries)
                {
                    retryDelay = GetRetryDelay(response.Headers.RetryAfter, attempt);
                    _diagnostics.RecordRetry();
                    stopwatch.Stop();
                    _diagnostics.RecordRequest(request.Host, request.Endpoint, stopwatch.Elapsed);
                    Log(
                        request,
                        attempt,
                        statusCode,
                        stopwatch.Elapsed,
                        fromCache: false,
                        response.StatusCode == HttpStatusCode.TooManyRequests
                            ? WikidataFailureKind.RateLimited
                            : WikidataFailureKind.TransientNetworkFailure);
                }
                else if (!response.IsSuccessStatusCode)
                {
                    throw CreateStatusException(response.StatusCode, request.Uri);
                }
                else
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    stopwatch.Stop();
                    _diagnostics.RecordRequest(request.Host, request.Endpoint, stopwatch.Elapsed);
                    Log(request, attempt, statusCode, stopwatch.Elapsed, fromCache: false, failureKind: null);

                    if (cache is not null)
                    {
                        await cache.SetAsync(
                            request.CacheKey,
                            body,
                            _options.ResponseCacheTtl,
                            cancellationToken).ConfigureAwait(false);
                    }

                    return body;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                _diagnostics.RecordFailure(
                    WikidataFailureKind.Cancelled,
                    request.Endpoint,
                    $"The request to {request.Host}/{request.Endpoint} was cancelled.");
                Log(request, attempt, statusCode, stopwatch.Elapsed, fromCache: false, WikidataFailureKind.Cancelled);
                throw;
            }
            catch (OperationCanceledException) when (attempt < _options.MaxRetries)
            {
                retryDelay = GetRetryDelay(retryAfter: null, attempt);
                _diagnostics.RecordRetry();
                stopwatch.Stop();
                _diagnostics.RecordRequest(request.Host, request.Endpoint, stopwatch.Elapsed);
                Log(request, attempt, statusCode, stopwatch.Elapsed, fromCache: false, WikidataFailureKind.TransientNetworkFailure);
            }
            catch (OperationCanceledException ex)
            {
                stopwatch.Stop();
                _diagnostics.RecordRequest(request.Host, request.Endpoint, stopwatch.Elapsed);
                var providerException = CreateTransientException(request.Uri, ex);
                _diagnostics.RecordFailure(providerException.Kind, request.Endpoint, providerException.Message);
                Log(request, attempt, statusCode, stopwatch.Elapsed, fromCache: false, providerException.Kind);
                throw providerException;
            }
            catch (HttpRequestException) when (attempt < _options.MaxRetries)
            {
                retryDelay = GetRetryDelay(retryAfter: null, attempt);
                _diagnostics.RecordRetry();
                stopwatch.Stop();
                _diagnostics.RecordRequest(request.Host, request.Endpoint, stopwatch.Elapsed);
                Log(request, attempt, statusCode, stopwatch.Elapsed, fromCache: false, WikidataFailureKind.TransientNetworkFailure);
            }
            catch (HttpRequestException ex)
            {
                stopwatch.Stop();
                _diagnostics.RecordRequest(request.Host, request.Endpoint, stopwatch.Elapsed);
                var providerException = CreateTransientException(request.Uri, ex);
                _diagnostics.RecordFailure(providerException.Kind, request.Endpoint, providerException.Message);
                Log(request, attempt, statusCode, stopwatch.Elapsed, fromCache: false, providerException.Kind);
                throw providerException;
            }
            catch (WikidataProviderException ex)
            {
                stopwatch.Stop();
                _diagnostics.RecordRequest(request.Host, request.Endpoint, stopwatch.Elapsed);
                _diagnostics.RecordFailure(ex.Kind, request.Endpoint, ex.Message);
                Log(request, attempt, ex.StatusCode, stopwatch.Elapsed, fromCache: false, ex.Kind);
                throw;
            }

            if (retryDelay is not null)
            {
                await _delayAsync(retryDelay.Value, cancellationToken).ConfigureAwait(false);
                continue;
            }
        }
    }

    private async ValueTask<IDisposable> WaitForLimiterAsync(string host, CancellationToken cancellationToken)
    {
        if (_legacyConcurrencyLimiter is null)
            return await _hostLimiters.WaitAsync(host, cancellationToken).ConfigureAwait(false);

        await _legacyConcurrencyLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new LegacyLease(_legacyConcurrencyLimiter);
    }

    private void Log(
        ProviderRequest request,
        int attempt,
        HttpStatusCode? statusCode,
        TimeSpan latency,
        bool fromCache,
        WikidataFailureKind? failureKind)
    {
        _options.RequestLogger?.Invoke(new WikidataHttpLogEntry(
            request.Host,
            request.Endpoint,
            request.Uri,
            attempt,
            statusCode,
            latency,
            fromCache,
            failureKind));
    }

    private static bool ShouldRetry(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.RequestTimeout => true,
            HttpStatusCode.TooManyRequests => true,
            HttpStatusCode.InternalServerError => true,
            HttpStatusCode.BadGateway => true,
            HttpStatusCode.ServiceUnavailable => true,
            HttpStatusCode.GatewayTimeout => true,
            _ => false
        };
    }

    private TimeSpan GetRetryDelay(RetryConditionHeaderValue? retryAfter, int attempt)
    {
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
            return delta;

        if (retryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
                return delay;
        }

        var baseMilliseconds = Math.Max(1, _options.RetryBaseDelay.TotalMilliseconds);
        var exponential = baseMilliseconds * Math.Pow(2, attempt);
        var capped = Math.Min(exponential, Math.Max(baseMilliseconds, _options.MaxRetryDelay.TotalMilliseconds));

        var jitterRatio = Math.Clamp(_options.RetryJitterRatio, 0, 1);
        if (jitterRatio > 0)
            capped += Random.Shared.NextDouble() * capped * jitterRatio;

        return TimeSpan.FromMilliseconds(capped);
    }

    private static WikidataProviderException CreateStatusException(HttpStatusCode statusCode, Uri requestUri)
    {
        var kind = statusCode switch
        {
            HttpStatusCode.NotFound => WikidataFailureKind.NotFound,
            HttpStatusCode.TooManyRequests => WikidataFailureKind.RateLimited,
            _ => WikidataFailureKind.TransientNetworkFailure
        };

        return new WikidataProviderException(
            kind,
            $"The provider returned HTTP {(int)statusCode} ({statusCode}) for {requestUri.Host}.",
            statusCode,
            requestUri);
    }

    private static WikidataProviderException CreateTransientException(Uri requestUri, Exception innerException)
    {
        return new WikidataProviderException(
            WikidataFailureKind.TransientNetworkFailure,
            $"The provider request to {requestUri.Host} failed after retries were exhausted.",
            requestUri: requestUri,
            innerException: innerException);
    }

    private string AppendMaxLag(string url)
    {
        if (_options.MaxLag <= 0 || url.Contains("maxlag=", StringComparison.OrdinalIgnoreCase))
            return url;

        var separator = url.Contains('?') ? '&' : '?';
        return $"{url}{separator}maxlag={_options.MaxLag}";
    }

    public void Dispose() => _hostLimiters.Dispose();

    private sealed class LegacyLease : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private bool _disposed;

        public LegacyLease(SemaphoreSlim semaphore) => _semaphore = semaphore;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _semaphore.Release();
        }
    }
}
