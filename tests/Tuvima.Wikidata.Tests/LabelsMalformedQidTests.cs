namespace Tuvima.Wikidata.Tests;

/// <summary>
/// Unit tests for the v2.3.0 malformed-QID pre-filter in <see cref="Services.LabelsService"/>.
/// Verifies the filter semantics without hitting the network — when every input is invalid,
/// the service returns an empty dictionary instead of making a doomed API call.
/// </summary>
public class LabelsMalformedQidTests
{
    [Fact]
    public async Task GetBatchAsync_AllMalformed_ReturnsEmptyDictionaryWithoutApiCall()
    {
        using var reconciler = new WikidataReconciler();

        var result = await reconciler.Labels.GetBatchAsync(
            ["not-a-qid", "42", "", "Q", "QABC", "P42"]);

        // All inputs fail the Q\d+ syntactic filter → empty result, no HTTP call attempted.
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetBatchAsync_EmptyString_FiltersOut()
    {
        using var reconciler = new WikidataReconciler();

        var result = await reconciler.Labels.GetBatchAsync([""]);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetBatchAsync_BareQ_FiltersOut()
    {
        using var reconciler = new WikidataReconciler();

        // "Q" alone (no digits) is not a valid QID.
        var result = await reconciler.Labels.GetBatchAsync(["Q"]);

        Assert.Empty(result);
    }
}
