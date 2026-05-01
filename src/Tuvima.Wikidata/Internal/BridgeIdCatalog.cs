using System.Globalization;
using System.Text.RegularExpressions;

namespace Tuvima.Wikidata.Internal;

internal static partial class BridgeIdCatalog
{
    private static readonly StringComparer KeyComparer = StringComparer.OrdinalIgnoreCase;

    public static IReadOnlyList<ResolvedBridgeIdentifier> Normalize(BridgeResolutionRequest request)
    {
        var result = new List<ResolvedBridgeIdentifier>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (rawKey, rawValue) in request.BridgeIds)
        {
            if (string.IsNullOrWhiteSpace(rawKey) || string.IsNullOrWhiteSpace(rawValue))
                continue;

            var key = NormalizeKey(rawKey);
            var value = rawValue.Trim();
            var propertyId = ResolvePropertyId(key, request.MediaKind, request.CustomWikidataProperties);
            if (string.IsNullOrWhiteSpace(propertyId))
                continue;

            var normalizedValue = NormalizeValue(key, value);
            if (string.IsNullOrWhiteSpace(normalizedValue))
                continue;

            var identity = $"{propertyId}|{normalizedValue}";
            if (!seen.Add(identity))
                continue;

            result.Add(new ResolvedBridgeIdentifier(
                rawKey,
                key,
                propertyId,
                normalizedValue,
                value,
                IsCustom: request.CustomWikidataProperties?.ContainsKey(rawKey) == true ||
                          request.CustomWikidataProperties?.ContainsKey(key) == true));
        }

        return result;
    }

    public static IReadOnlyList<string> GetKnownPropertyIds(BridgeMediaKind mediaKind)
    {
        var properties = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "P345",  // IMDb ID
            "P5749", // Amazon Standard Identification Number
            "P5905", // Comic Vine ID
            "P675",  // Google Books ID
            "P648",  // Open Library ID
            "P2969", // Goodreads work ID
            "P212",  // ISBN-13
            "P957"   // ISBN-10
        };

        IEnumerable<string> mediaProperties = mediaKind switch
        {
            BridgeMediaKind.Movie => new[] { "P4947", "P6398", "P9586" },
            BridgeMediaKind.TvSeries => new[] { "P4983", "P4835", "P9751" },
            BridgeMediaKind.TvSeason => new[] { "P6381" },
            BridgeMediaKind.TvEpisode => new[] { "P7043", "P9750" },
            BridgeMediaKind.MusicAlbum => new[] { "P2281", "P436", "P5813" },
            BridgeMediaKind.MusicRelease => new[] { "P2281", "P5813", "P436" },
            BridgeMediaKind.MusicRecording or BridgeMediaKind.MusicTrack => new[] { "P10110", "P4404" },
            BridgeMediaKind.MusicWork => new[] { "P435" },
            BridgeMediaKind.Book or BridgeMediaKind.Audiobook => new[] { "P6395" },
            BridgeMediaKind.App => new[] { "P3861" },
            _ => new[] { "P4947", "P4983", "P4835", "P7043", "P2281", "P2850", "P10110", "P435", "P436", "P5813", "P4404", "P6395", "P9586", "P9751", "P9750", "P6381", "P6398" }
        };

        foreach (var propertyId in mediaProperties)
        {
            properties.Add(propertyId);
        }

        return properties.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? ResolvePropertyId(
        string normalizedKey,
        BridgeMediaKind mediaKind,
        IReadOnlyDictionary<string, string>? customProperties)
    {
        if (customProperties is not null)
        {
            if (customProperties.TryGetValue(normalizedKey, out var propertyId) && IsPropertyId(propertyId))
                return propertyId.Trim().ToUpperInvariant();

            foreach (var (key, value) in customProperties)
            {
                if (KeyComparer.Equals(NormalizeKey(key), normalizedKey) && IsPropertyId(value))
                    return value.Trim().ToUpperInvariant();
            }
        }

        if (IsPropertyId(normalizedKey))
            return normalizedKey.ToUpperInvariant();

        return normalizedKey switch
        {
            "isbn13" => "P212",
            "isbn10" => "P957",
            "isbn" => mediaKind == BridgeMediaKind.Book || mediaKind == BridgeMediaKind.Audiobook ? "P212" : "P212",
            "imdb" or "imdb_id" => "P345",
            "tmdb_movie" or "tmdb_movie_id" => "P4947",
            "tmdb_tv" or "tmdb_show" or "tmdb_series" or "tmdb_tv_id" or "tmdb_show_id" or "tmdb_series_id" => "P4983",
            "tmdb" or "tmdb_id" => mediaKind is BridgeMediaKind.TvSeries or BridgeMediaKind.TvSeason or BridgeMediaKind.TvEpisode ? "P4983" : "P4947",
            "tvdb" or "tvdb_id" or "tvdb_series_id" => mediaKind == BridgeMediaKind.TvEpisode ? "P7043" : "P4835",
            "tvdb_episode" or "tvdb_episode_id" => "P7043",
            "openlibrary" or "open_library" or "openlibrary_id" or "open_library_id" => "P648",
            "googlebooks" or "google_books" or "googlebooks_id" or "google_books_id" => "P675",
            "musicbrainz_work" or "musicbrainz_work_id" or "mb_work" or "mb_work_id" => "P435",
            "musicbrainz_release_group" or "musicbrainz_release_group_id" or "mb_release_group" or "mb_release_group_id" => "P436",
            "musicbrainz_release" or "musicbrainz_release_id" or "mb_release" or "mb_release_id" => "P5813",
            "musicbrainz_recording" or "musicbrainz_recording_id" or "mb_recording" or "mb_recording_id" => "P4404",
            "musicbrainz" or "musicbrainz_id" or "mbid" => ResolveGenericMusicBrainzProperty(mediaKind),
            "comicvine" or "comic_vine" or "comicvine_id" or "comic_vine_id" => "P5905",
            "apple_books" or "apple_books_id" => "P6395",
            "apple_music_album" or "apple_music_album_id" or "apple_music_collection" or "apple_music_collection_id" => "P2281",
            "apple_music_artist" or "apple_music_artist_id" or "apple_artist" or "apple_artist_id" => "P2850",
            "apple_music_track" or "apple_music_track_id" => "P10110",
            "apple_music" or "apple_music_id" => mediaKind is BridgeMediaKind.MusicRecording or BridgeMediaKind.MusicTrack ? "P10110" : "P2281",
            "apple_tv_movie" or "apple_tv_movie_id" => "P9586",
            "apple_tv_show" or "apple_tv_show_id" => "P9751",
            "apple_tv_episode" or "apple_tv_episode_id" => "P9750",
            "itunes_tv_season" or "itunes_tv_season_id" or "apple_tv_season" or "apple_tv_season_id" => "P6381",
            "itunes_movie" or "itunes_movie_id" or "apple_itunes" or "apple_itunes_id" => mediaKind == BridgeMediaKind.App ? "P3861" : "P6398",
            "app_store" or "app_store_id" or "app_store_app_id" => "P3861",
            "asin" or "amazon_asin" => "P5749",
            "goodreads" or "goodreads_id" or "goodreads_work" or "goodreads_work_id" => "P2969",
            "librarything_work" or "librarything_work_id" => "P1085",
            _ => null
        };
    }

    private static string ResolveGenericMusicBrainzProperty(BridgeMediaKind mediaKind)
    {
        return mediaKind switch
        {
            BridgeMediaKind.MusicRecording or BridgeMediaKind.MusicTrack => "P4404",
            BridgeMediaKind.MusicRelease => "P5813",
            BridgeMediaKind.MusicWork => "P435",
            _ => "P436"
        };
    }

    private static string NormalizeKey(string key)
    {
        return key.Trim()
            .Replace("-", "_", StringComparison.Ordinal)
            .Replace(" ", "_", StringComparison.Ordinal)
            .ToLowerInvariant();
    }

    private static string NormalizeValue(string key, string value)
    {
        return key switch
        {
            "isbn" or "isbn10" or "isbn13" => NormalizeIsbn(value),
            "imdb" or "imdb_id" => NormalizeImdb(value),
            "tmdb" or "tmdb_id" or "tmdb_movie" or "tmdb_movie_id" or "tmdb_tv" or "tmdb_tv_id" or "tmdb_show" or "tmdb_show_id" or "tmdb_series" or "tmdb_series_id" => NormalizeDigits(value),
            "tvdb" or "tvdb_id" or "tvdb_series_id" or "tvdb_episode" or "tvdb_episode_id" => NormalizeDigits(value),
            "apple_books" or "apple_books_id" or "apple_music" or "apple_music_id" or "apple_music_album" or "apple_music_album_id" or "apple_music_collection" or "apple_music_collection_id" or "apple_music_artist" or "apple_music_artist_id" or "apple_artist" or "apple_artist_id" or "apple_music_track" or "apple_music_track_id" or "apple_tv_movie" or "apple_tv_movie_id" or "apple_tv_show" or "apple_tv_show_id" or "apple_tv_episode" or "apple_tv_episode_id" or "itunes_tv_season" or "itunes_tv_season_id" or "itunes_movie" or "itunes_movie_id" or "apple_itunes" or "apple_itunes_id" or "app_store" or "app_store_id" or "app_store_app_id" => NormalizeDigits(value),
            "musicbrainz" or "musicbrainz_id" or "mbid" or "musicbrainz_work" or "musicbrainz_work_id" or "musicbrainz_release_group" or "musicbrainz_release_group_id" or "musicbrainz_release" or "musicbrainz_release_id" or "musicbrainz_recording" or "musicbrainz_recording_id" => value.Trim().ToLowerInvariant(),
            "openlibrary" or "open_library" or "openlibrary_id" or "open_library_id" => value.Trim().ToUpperInvariant(),
            _ => value.Trim()
        };
    }

    private static string NormalizeIsbn(string value)
    {
        var normalized = new string(value.Where(c => char.IsDigit(c) || c is 'X' or 'x').ToArray());
        return normalized.ToUpperInvariant();
    }

    private static string NormalizeImdb(string value)
    {
        var trimmed = value.Trim().ToLowerInvariant();
        if (trimmed.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        return trimmed.All(char.IsDigit)
            ? $"tt{trimmed.PadLeft(7, '0')}"
            : trimmed;
    }

    private static string NormalizeDigits(string value)
    {
        var match = DigitsRegex().Match(value);
        return match.Success ? match.Value : value.Trim();
    }

    private static bool IsPropertyId(string value)
    {
        return PropertyIdRegex().IsMatch(value.Trim());
    }

    [GeneratedRegex(@"^P\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PropertyIdRegex();

    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant)]
    private static partial Regex DigitsRegex();
}

internal sealed record ResolvedBridgeIdentifier(
    string RawKey,
    string NormalizedKey,
    string PropertyId,
    string NormalizedValue,
    string OriginalValue,
    bool IsCustom)
{
    public string LookupKey => $"{PropertyId}|{NormalizedValue}";
}
