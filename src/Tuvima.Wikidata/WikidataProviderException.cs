using System.Net;

namespace Tuvima.Wikidata;

/// <summary>
/// Exception thrown when a Wikimedia provider failure cannot be represented as a normal empty result.
/// </summary>
public sealed class WikidataProviderException : Exception
{
    public WikidataProviderException(
        WikidataFailureKind kind,
        string message,
        HttpStatusCode? statusCode = null,
        Uri? requestUri = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        StatusCode = statusCode;
        RequestUri = requestUri;
    }

    public WikidataFailureKind Kind { get; }

    public HttpStatusCode? StatusCode { get; }

    public Uri? RequestUri { get; }
}
