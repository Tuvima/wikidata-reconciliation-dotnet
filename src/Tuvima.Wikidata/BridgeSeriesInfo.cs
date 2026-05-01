namespace Tuvima.Wikidata;

/// <summary>
/// Normalized series/order metadata extracted from Wikidata claims.
/// </summary>
public sealed class BridgeSeriesInfo
{
    public string? SeriesQid { get; init; }

    public string? SeriesLabel { get; init; }

    public string? Position { get; init; }

    public string? PreviousQid { get; init; }

    public string? NextQid { get; init; }

    public string? SourcePropertyId { get; init; }

    public double Confidence { get; init; }
}
