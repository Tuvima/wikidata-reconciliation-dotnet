namespace Tuvima.Wikidata;

/// <summary>
/// High-level identity request driven by known provider/authority identifiers plus optional media hints.
/// </summary>
public sealed class BridgeResolutionRequest
{
    /// <summary>Caller-owned key used to map the result back to the input item.</summary>
    public required string CorrelationKey { get; init; }

    /// <summary>Media kind hint used for bridge property selection and candidate ranking.</summary>
    public BridgeMediaKind MediaKind { get; init; } = BridgeMediaKind.Unknown;

    /// <summary>
    /// Known bridge identifiers keyed by provider name, for example isbn13, imdb_id,
    /// tmdb_movie_id, open_library_id, musicbrainz_release_group_id, or comicvine_id.
    /// </summary>
    public IReadOnlyDictionary<string, string> BridgeIds { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Optional caller-supplied mapping from bridge key to Wikidata property ID. This is
    /// used for private/custom bridges and for provider keys not known by this library.
    /// </summary>
    public IReadOnlyDictionary<string, string>? CustomWikidataProperties { get; init; }

    /// <summary>Optional title/name hint used for fallback text search and ranking.</summary>
    public string? Title { get; init; }

    /// <summary>Optional creator/artist/author hint used for diagnostics and future ranking.</summary>
    public string? Creator { get; init; }

    /// <summary>Optional release/publication year hint used for candidate ranking.</summary>
    public int? Year { get; init; }

    /// <summary>Optional series title hint used for diagnostics and future ranking.</summary>
    public string? SeriesTitle { get; init; }

    /// <summary>Optional season number hint for TV/comic style ordering.</summary>
    public int? SeasonNumber { get; init; }

    /// <summary>Optional episode number hint for TV ordering.</summary>
    public int? EpisodeNumber { get; init; }

    /// <summary>Optional issue number hint for comics and periodicals.</summary>
    public string? IssueNumber { get; init; }

    /// <summary>Preferred language for labels and Wikipedia summaries. Defaults to the reconciler language.</summary>
    public string? Language { get; init; }

    /// <summary>Rollup behavior for editions/releases and their canonical works.</summary>
    public BridgeRollupTarget RollupTarget { get; init; } = BridgeRollupTarget.ReturnWorkAndEdition;
}
