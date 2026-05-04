namespace Tuvima.Wikidata;

/// <summary>
/// Describes the strongest evidence used to order a series manifest item.
/// </summary>
public enum SeriesManifestOrderSource
{
    Unknown,
    SeriesOrdinal,
    PreviousNextChain,
    PublicationDate,
    LabelFallback,
    Mixed
}
