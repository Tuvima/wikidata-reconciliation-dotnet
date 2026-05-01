namespace Tuvima.Wikidata;

/// <summary>
/// Broad media/category hint used by bridge resolution for property selection and ranking.
/// </summary>
public enum BridgeMediaKind
{
    Unknown = 0,
    Book,
    Audiobook,
    ComicSeries,
    ComicIssue,
    Movie,
    TvSeries,
    TvSeason,
    TvEpisode,
    MusicAlbum,
    MusicRelease,
    MusicRecording,
    MusicWork,
    MusicTrack,
    Game,
    App
}
