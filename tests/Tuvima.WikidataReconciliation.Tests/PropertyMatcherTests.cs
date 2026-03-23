using Tuvima.WikidataReconciliation.Internal;

namespace Tuvima.WikidataReconciliation.Tests;

public class PropertyMatcherTests
{
    [Fact]
    public void MatchDoubles_ExactMatch_Returns100()
    {
        Assert.Equal(100, PropertyMatcher.MatchDoubles(42.0, 42.0));
    }

    [Fact]
    public void MatchDoubles_SmallDifference_ReturnsHighScore()
    {
        var score = PropertyMatcher.MatchDoubles(42.0, 42.001);
        Assert.True(score >= 80, $"Expected >= 80 but got {score}");
    }

    [Fact]
    public void MatchDoubles_LargeDifference_ReturnsLowScore()
    {
        var score = PropertyMatcher.MatchDoubles(1.0, 1000.0);
        Assert.True(score < 30, $"Expected < 30 but got {score}");
    }
}
