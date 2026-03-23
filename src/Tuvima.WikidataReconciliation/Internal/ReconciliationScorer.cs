using System.Text.Json;
using Tuvima.WikidataReconciliation.Internal.Json;

namespace Tuvima.WikidataReconciliation.Internal;

/// <summary>
/// Computes reconciliation scores using the weighted-average algorithm from openrefine-wikibase.
/// Label weight = 1.0, each property weight = 0.4.
/// </summary>
internal sealed class ReconciliationScorer
{
    private readonly WikidataReconcilerOptions _options;

    public ReconciliationScorer(WikidataReconcilerOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// Scores a candidate entity against a reconciliation request.
    /// Returns both the overall score and a detailed breakdown.
    /// </summary>
    public ScoringResult Score(string query, WikidataEntity entity, string language, IReadOnlyList<PropertyConstraint>? properties)
    {
        // Label matching: fuzzy match query against all labels + aliases, take max
        var allLabels = WikidataEntityFetcher.GetAllLabels(entity, language);
        var labelScore = 0;

        foreach (var label in allLabels)
        {
            var score = FuzzyMatcher.TokenSortRatio(query, label);
            if (score > labelScore)
                labelScore = score;
        }

        if (allLabels.Count == 0)
            labelScore = 0;

        var propertyWeight = _options.PropertyWeight;
        var totalWeight = 1.0;
        var weightedSum = labelScore * 1.0;
        var propertyScores = new Dictionary<string, double>();

        // Property matching
        if (properties is { Count: > 0 })
        {
            foreach (var prop in properties)
            {
                var propScore = ScoreProperty(entity, prop);
                propertyScores[prop.PropertyId] = propScore;
                weightedSum += propScore * propertyWeight;
                totalWeight += propertyWeight;
            }
        }

        var weightedScore = weightedSum / totalWeight;

        return new ScoringResult
        {
            Score = weightedScore,
            LabelScore = labelScore,
            PropertyScores = propertyScores,
            WeightedScore = weightedScore
        };
    }

    /// <summary>
    /// Scores a single property constraint against an entity's claims.
    /// Returns the best match across all claim values for the (root) property.
    /// </summary>
    private static int ScoreProperty(WikidataEntity entity, PropertyConstraint constraint)
    {
        // Use the root property for direct scoring (chained paths are resolved at the orchestrator level)
        var path = new PropertyPath(constraint.PropertyId);
        var claimValues = WikidataEntityFetcher.GetClaimValues(entity, path.RootProperty);

        if (claimValues.Count == 0)
            return 0;

        var bestScore = 0;
        foreach (var claimValue in claimValues)
        {
            string? dataType = null;
            if (entity.Claims?.TryGetValue(constraint.PropertyId, out var claims) == true)
            {
                dataType = claims.FirstOrDefault()?.MainSnak?.DataType;
            }

            var score = PropertyMatcher.Match(constraint.Value, claimValue, dataType);
            if (score > bestScore)
                bestScore = score;
        }

        return bestScore;
    }

    /// <summary>
    /// Determines auto-match status for sorted results.
    /// </summary>
    public bool IsAutoMatch(double score, double? secondBestScore, int numProperties)
    {
        var threshold = _options.AutoMatchThreshold - 5.0 * numProperties;
        if (score <= threshold)
            return false;

        if (secondBestScore.HasValue && score <= secondBestScore.Value + _options.AutoMatchScoreGap)
            return false;

        return true;
    }
}

internal sealed record ScoringResult
{
    public double Score { get; init; }
    public double LabelScore { get; init; }
    public Dictionary<string, double> PropertyScores { get; init; } = new();
    public double WeightedScore { get; init; }
}
