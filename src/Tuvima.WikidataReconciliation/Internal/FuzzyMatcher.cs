using System.Globalization;
using System.Text;

namespace Tuvima.WikidataReconciliation.Internal;

/// <summary>
/// Fuzzy string matching using token-sort-ratio based on Levenshtein distance.
/// Equivalent to fuzzywuzzy's token_sort_ratio in the Python openrefine-wikibase.
/// </summary>
internal static class FuzzyMatcher
{
    /// <summary>
    /// Computes a token-sort-ratio score between two strings (0-100).
    /// Both strings are normalized (lowered, NFC-normalized), tokenized, sorted, then compared.
    /// </summary>
    public static int TokenSortRatio(string s1, string s2)
    {
        if (string.IsNullOrWhiteSpace(s1) || string.IsNullOrWhiteSpace(s2))
            return 0;

        var normalized1 = SortTokens(Normalize(s1));
        var normalized2 = SortTokens(Normalize(s2));

        if (normalized1.Length == 0 || normalized2.Length == 0)
            return 0;

        if (normalized1 == normalized2)
            return 100;

        return LevenshteinRatio(normalized1, normalized2);
    }

    /// <summary>
    /// Computes a token-sort-ratio score with optional diacritic stripping (0-100).
    /// Returns the maximum of diacritic-insensitive and standard comparison.
    /// </summary>
    public static int TokenSortRatio(string s1, string s2, bool stripDiacritics)
    {
        var standard = TokenSortRatio(s1, s2);
        if (!stripDiacritics)
            return standard;

        // Also compare with diacritics removed, return the better score
        var n1 = SortTokens(NormalizeDiacriticInsensitive(s1));
        var n2 = SortTokens(NormalizeDiacriticInsensitive(s2));

        if (n1.Length == 0 || n2.Length == 0)
            return standard;

        var stripped = n1 == n2 ? 100 : LevenshteinRatio(n1, n2);
        return Math.Max(standard, stripped);
    }

    /// <summary>
    /// Computes a simple ratio between two strings without tokenization (0-100).
    /// </summary>
    public static int Ratio(string s1, string s2)
    {
        if (string.IsNullOrWhiteSpace(s1) || string.IsNullOrWhiteSpace(s2))
            return 0;

        var n1 = Normalize(s1);
        var n2 = Normalize(s2);

        if (n1 == n2)
            return 100;

        return LevenshteinRatio(n1, n2);
    }

    /// <summary>
    /// Removes diacritical marks from a string.
    /// "Shōgun" → "Shogun", "Müller" → "Muller", "café" → "cafe".
    /// </summary>
    internal static string RemoveDiacritics(string s)
    {
        var nfd = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(nfd.Length);

        foreach (var ch in nfd)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string Normalize(string s)
    {
        // Lowercase + NFC normalization
        return s.Normalize(NormalizationForm.FormC).ToLower(CultureInfo.InvariantCulture).Trim();
    }

    private static string NormalizeDiacriticInsensitive(string s)
    {
        return RemoveDiacritics(Normalize(s));
    }

    private static string SortTokens(string s)
    {
        var tokens = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        Array.Sort(tokens, StringComparer.Ordinal);
        return string.Join(' ', tokens);
    }

    /// <summary>
    /// Levenshtein ratio: 100 * (1 - distance / maxLen).
    /// Uses Wagner-Fischer with two-row optimization for O(min(m,n)) space.
    /// </summary>
    private static int LevenshteinRatio(string s1, string s2)
    {
        var maxLen = Math.Max(s1.Length, s2.Length);
        if (maxLen == 0)
            return 100;

        var distance = LevenshteinDistance(s1, s2);
        return (int)Math.Round(100.0 * (1.0 - (double)distance / maxLen));
    }

    internal static int LevenshteinDistance(string s1, string s2)
    {
        // Ensure s1 is the shorter string for space optimization
        if (s1.Length > s2.Length)
            (s1, s2) = (s2, s1);

        var m = s1.Length;
        var n = s2.Length;

        var previous = new int[m + 1];
        var current = new int[m + 1];

        for (var i = 0; i <= m; i++)
            previous[i] = i;

        for (var j = 1; j <= n; j++)
        {
            current[0] = j;
            for (var i = 1; i <= m; i++)
            {
                var cost = s1[i - 1] == s2[j - 1] ? 0 : 1;
                current[i] = Math.Min(
                    Math.Min(current[i - 1] + 1, previous[i] + 1),
                    previous[i - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[m];
    }
}
