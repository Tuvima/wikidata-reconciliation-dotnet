using System.Net;

namespace Tuvima.WikidataReconciliation.Internal;

/// <summary>
/// Wraps HttpClient with retry-on-429 logic using exponential backoff.
/// Appends maxlag parameter to every request for Wikimedia API etiquette.
/// </summary>
internal sealed class ResilientHttpClient
{
    private readonly HttpClient _httpClient;
    private readonly int _maxRetries;
    private readonly int _maxLag;

    public ResilientHttpClient(HttpClient httpClient, int maxRetries, int maxLag = 5)
    {
        _httpClient = httpClient;
        _maxRetries = maxRetries;
        _maxLag = maxLag;
    }

    public async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        var requestUrl = _maxLag > 0
            ? $"{url}&maxlag={_maxLag}"
            : url;

        for (var attempt = 0; ; attempt++)
        {
            using var response = await _httpClient.GetAsync(requestUrl, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < _maxRetries)
            {
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
