using Tuvima.WikidataReconciliation.Internal;

namespace Tuvima.WikidataReconciliation.Tests;

public class FuzzyMatcherTests
{
    [Fact]
    public void ExactMatch_Returns100()
    {
        Assert.Equal(100, FuzzyMatcher.TokenSortRatio("Douglas Adams", "Douglas Adams"));
    }

    [Fact]
    public void CaseInsensitive_Returns100()
    {
        Assert.Equal(100, FuzzyMatcher.TokenSortRatio("douglas adams", "Douglas Adams"));
    }

    [Fact]
    public void TokenReordering_Returns100()
    {
        // Token sort ratio sorts tokens alphabetically before comparing
        Assert.Equal(100, FuzzyMatcher.TokenSortRatio("Adams Douglas", "Douglas Adams"));
    }

    [Fact]
    public void SimilarStrings_ReturnsHighScore()
    {
        var score = FuzzyMatcher.TokenSortRatio("Douglas Adam", "Douglas Adams");
        Assert.True(score >= 90, $"Expected >= 90 but got {score}");
    }

    [Fact]
    public void DifferentStrings_ReturnsLowScore()
    {
        var score = FuzzyMatcher.TokenSortRatio("Douglas Adams", "Isaac Newton");
        Assert.True(score < 50, $"Expected < 50 but got {score}");
    }

    [Fact]
    public void EmptyString_Returns0()
    {
        Assert.Equal(0, FuzzyMatcher.TokenSortRatio("", "Douglas Adams"));
        Assert.Equal(0, FuzzyMatcher.TokenSortRatio("Douglas Adams", ""));
        Assert.Equal(0, FuzzyMatcher.TokenSortRatio("", ""));
    }

    [Fact]
    public void NullOrWhitespace_Returns0()
    {
        Assert.Equal(0, FuzzyMatcher.TokenSortRatio("  ", "Douglas Adams"));
        Assert.Equal(0, FuzzyMatcher.TokenSortRatio("Douglas Adams", "   "));
    }

    [Fact]
    public void LevenshteinDistance_KnownPairs()
    {
        Assert.Equal(0, FuzzyMatcher.LevenshteinDistance("kitten", "kitten"));
        Assert.Equal(3, FuzzyMatcher.LevenshteinDistance("kitten", "sitting"));
        Assert.Equal(1, FuzzyMatcher.LevenshteinDistance("cat", "bat"));
        Assert.Equal(3, FuzzyMatcher.LevenshteinDistance("abc", ""));
    }

    [Fact]
    public void UnicodeNormalization_HandlesAccents()
    {
        // NFC normalization should handle composed vs decomposed forms
        var score = FuzzyMatcher.TokenSortRatio("café", "cafe\u0301");
        Assert.Equal(100, score);
    }
}
