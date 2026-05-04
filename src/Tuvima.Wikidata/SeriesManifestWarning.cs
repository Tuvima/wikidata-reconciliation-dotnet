namespace Tuvima.Wikidata;

/// <summary>
/// Non-fatal manifest warning about incomplete or ambiguous Wikidata modeling.
/// </summary>
public sealed class SeriesManifestWarning
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string? Qid { get; init; }
}
