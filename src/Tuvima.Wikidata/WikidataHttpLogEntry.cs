using System.Net;

namespace Tuvima.Wikidata;

/// <summary>
/// Lightweight request log event emitted by the shared Wikimedia HTTP pipeline.
/// </summary>
public sealed record WikidataHttpLogEntry(
    string Host,
    string Endpoint,
    Uri RequestUri,
    int Attempt,
    HttpStatusCode? StatusCode,
    TimeSpan Latency,
    bool FromCache,
    WikidataFailureKind? FailureKind);
