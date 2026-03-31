using System.Text.RegularExpressions;

namespace Tuvima.WikidataReconciliation;

/// <summary>
/// Built-in query cleaning functions for use with <see cref="ReconciliationRequest.Cleaners"/>.
/// Each method returns a <c>Func&lt;string, string&gt;</c> that can be added to the cleaners pipeline.
/// </summary>
public static class QueryCleaners
{
    private static readonly Regex ParentheticalPattern = new(@"\s*\([^)]*\)\s*", RegexOptions.Compiled);
    private static readonly Regex SubtitlePattern = new(@"\s*[:]\s+.*$", RegexOptions.Compiled);
    private static readonly Regex DashSubtitlePattern = new(@"\s+[-\u2013\u2014]\s+.*$", RegexOptions.Compiled);
    private static readonly Regex SeriesPattern = new(@"\b[Ss]\d{1,2}[Ee]\d{1,2}\b", RegexOptions.Compiled);
    private static readonly Regex VolumePattern = new(@"\b[Vv]ol\.?\s*\d+\b", RegexOptions.Compiled);

    /// <summary>
    /// Removes parenthetical text such as "(Unabridged)", "(2nd Edition)", "(Remastered)".
    /// </summary>
    public static Func<string, string> StripParenthetical() =>
        s => ParentheticalPattern.Replace(s, " ").Trim();

    /// <summary>
    /// Removes subtitle text after a colon (e.g., "Dune: Part Two" becomes "Dune").
    /// </summary>
    public static Func<string, string> StripSubtitle() =>
        s => SubtitlePattern.Replace(s, "").Trim();

    /// <summary>
    /// Removes subtitle text after a dash/en-dash/em-dash (e.g., "Title - A Novel" becomes "Title").
    /// </summary>
    public static Func<string, string> StripDashSubtitle() =>
        s => DashSubtitlePattern.Replace(s, "").Trim();

    /// <summary>
    /// Removes series/episode designators like "S01E02", "s2e10".
    /// </summary>
    public static Func<string, string> StripSeriesNotation() =>
        s => SeriesPattern.Replace(s, "").Trim();

    /// <summary>
    /// Removes volume designators like "Vol. 3", "vol2".
    /// </summary>
    public static Func<string, string> StripVolumeNotation() =>
        s => VolumePattern.Replace(s, "").Trim();

    /// <summary>
    /// Applies all built-in cleaners in sequence.
    /// </summary>
    public static IReadOnlyList<Func<string, string>> All() =>
    [
        StripParenthetical(),
        StripSeriesNotation(),
        StripVolumeNotation(),
        StripSubtitle(),
        StripDashSubtitle()
    ];
}
