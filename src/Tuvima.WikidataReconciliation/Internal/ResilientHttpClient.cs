using System.Net;

namespace Tuvima.WikidataReconciliation.Internal;

/// <summary>
/// Wraps HttpClient with retry-on-429 logic using exponential backoff.
/// </summary>
internal sealed class ResilientHttpClient
{
    private readonly HttpClient _httpClient;
    private readonly int _maxRetries;

    public ResilientHttpClient(HttpClient httpClient, int maxRetries)
    {
        _httpClient = httpClient;
        _maxRetries = maxRetries;
    }

    public async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < _maxRetries)
            {
                // Use Retry-After header if available, otherwise exponential backoff
                var delay = response.Headers.RetryAfter?.Delta
                    ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
