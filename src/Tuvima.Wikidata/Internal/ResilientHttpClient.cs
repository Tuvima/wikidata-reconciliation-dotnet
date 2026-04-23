using System.Net;
using System.Net.Http.Headers;

namespace Tuvima.Wikidata.Internal;

/// <summary>
/// Wraps HttpClient with retry-on-429 logic using exponential backoff.
/// Appends maxlag parameter to every request for Wikimedia API etiquette.
/// </summary>
internal sealed class ResilientHttpClient
{
    private readonly HttpClient _httpClient;
    private readonly int _maxRetries;
    private readonly int _maxLag;
    private readonly SemaphoreSlim _concurrencyLimiter;

    public ResilientHttpClient(HttpClient httpClient, int maxRetries, int maxLag, SemaphoreSlim concurrencyLimiter)
    {
        _httpClient = httpClient;
        _maxRetries = maxRetries;
        _maxLag = maxLag;
        _concurrencyLimiter = concurrencyLimiter;
    }

    public async Task<string> GetStringAsync(
        string url,
        CancellationToken cancellationToken,
        bool applyMaxLag = true)
    {
        var requestUrl = applyMaxLag ? AppendMaxLag(url) : url;

        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage? response = null;
            TimeSpan? retryDelay = null;
            try
            {
                await _concurrencyLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    response = await _httpClient.GetAsync(requestUrl, cancellationToken).ConfigureAwait(false);

                    if (ShouldRetry(response.StatusCode) && attempt < _maxRetries)
                    {
                        retryDelay = GetRetryDelay(response.Headers.RetryAfter, attempt);
                    }
                    else
                    {
                        response.EnsureSuccessStatusCode();
                        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    response?.Dispose();
                    _concurrencyLimiter.Release();
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < _maxRetries)
            {
                retryDelay = GetRetryDelay(retryAfter: null, attempt);
            }
            catch (HttpRequestException) when (attempt < _maxRetries)
            {
                retryDelay = GetRetryDelay(retryAfter: null, attempt);
            }

            if (retryDelay is not null)
            {
                await Task.Delay(retryDelay.Value, cancellationToken).ConfigureAwait(false);
                continue;
            }
        }
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

    private static TimeSpan GetRetryDelay(RetryConditionHeaderValue? retryAfter, int attempt)
    {
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
            return delta;

        if (retryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
                return delay;
        }

        return TimeSpan.FromSeconds(Math.Pow(2, attempt));
    }

    private string AppendMaxLag(string url)
    {
        if (_maxLag <= 0 || url.Contains("maxlag=", StringComparison.OrdinalIgnoreCase))
            return url;

        var separator = url.Contains('?') ? '&' : '?';
        return $"{url}{separator}maxlag={_maxLag}";
    }
}
