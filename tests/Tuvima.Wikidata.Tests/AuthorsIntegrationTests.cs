namespace Tuvima.Wikidata.Tests;

/// <summary>
/// Integration tests for the v2.0.0 AuthorsService against the live Wikidata API.
/// </summary>
[Trait("Category", "Integration")]
public class AuthorsIntegrationTests : IDisposable
{
    private readonly WikidataReconciler _reconciler;

    public AuthorsIntegrationTests()
    {
        _reconciler = new WikidataReconciler(new WikidataReconcilerOptions
        {
            UserAgent = "Tuvima.Wikidata.Tests/2.2 (https://github.com/Tuvima/wikidata)"
        });
    }

    [Fact]
    public async Task ResolveAsync_SingleAuthor_ResolvesToExpectedQid()
    {
        var result = await _reconciler.Authors.ResolveAsync(new AuthorResolutionRequest
        {
            RawAuthorString = "Douglas Adams"
        });

        Assert.Single(result.Authors);
        Assert.Equal("Q42", result.Authors[0].Qid);
        Assert.NotNull(result.Authors[0].CanonicalName);
        Assert.True(result.Authors[0].Confidence > 50.0);
    }

    [Fact]
    public async Task ResolveAsync_TwoAuthorsWithAmpersand_SplitsAndResolvesBoth()
    {
        var result = await _reconciler.Authors.ResolveAsync(new AuthorResolutionRequest
        {
            RawAuthorString = "Neil Gaiman & Terry Pratchett"
        });

        // Split into two names, both resolved to non-null QIDs. We don't pin specific QIDs
        // because reconciler scoring for common names can pick between disambiguations in
        // ways that aren't stable over time.
        Assert.Equal(2, result.Authors.Count);
        Assert.All(result.Authors, a =>
        {
            Assert.NotNull(a.Qid);
            Assert.StartsWith("Q", a.Qid);
            Assert.True(a.Confidence > 50.0);
        });

        // Terry Pratchett (Q46248) is well-disambiguated and should consistently resolve.
        var qids = result.Authors.Select(a => a.Qid).ToHashSet();
        Assert.Contains("Q46248", qids);
    }

    [Fact]
    public async Task ResolveAsync_LastFirstForm_NotSplit()
    {
        // "Tolkien, J. R. R." is a single author in Last, First form.
        var result = await _reconciler.Authors.ResolveAsync(new AuthorResolutionRequest
        {
            RawAuthorString = "Tolkien, J. R. R."
        });

        Assert.Single(result.Authors);
        // After normalization we search for "J. R. R. Tolkien" — expect Q892.
        Assert.Equal("Q892", result.Authors[0].Qid);
    }

    [Fact]
    public async Task ResolveAsync_EtAl_CapturedInUnresolved()
    {
        var result = await _reconciler.Authors.ResolveAsync(new AuthorResolutionRequest
        {
            RawAuthorString = "Douglas Adams et al."
        });

        Assert.Single(result.Authors);
        Assert.Equal("Q42", result.Authors[0].Qid);
        Assert.Contains("et al.", result.UnresolvedNames);
    }

    [Fact]
    public async Task ResolveAsync_UnknownAuthor_EmptyQid()
    {
        var result = await _reconciler.Authors.ResolveAsync(new AuthorResolutionRequest
        {
            RawAuthorString = "Xzqvyt Bjklmfn"
        });

        Assert.Single(result.Authors);
        Assert.Null(result.Authors[0].Qid);
    }

    [Fact]
    public async Task ResolveAsync_StephenKing_PopulatesPseudonymsFromP742()
    {
        // Stephen King (Q39829) has a well-established P742 (pseudonym) = "Richard Bachman".
        var result = await _reconciler.Authors.ResolveAsync(new AuthorResolutionRequest
        {
            RawAuthorString = "Stephen King",
            DetectPseudonyms = true
        });

        Assert.Single(result.Authors);
        var author = result.Authors[0];
        Assert.Equal("Q39829", author.Qid);
        Assert.NotNull(author.Pseudonyms);
        Assert.Contains(author.Pseudonyms!, p => p.Contains("Richard Bachman", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ResolveAsync_WithoutPseudonymDetection_PseudonymsIsNull()
    {
        var result = await _reconciler.Authors.ResolveAsync(new AuthorResolutionRequest
        {
            RawAuthorString = "Stephen King",
            DetectPseudonyms = false
        });

        Assert.Single(result.Authors);
        Assert.Null(result.Authors[0].Pseudonyms);
    }

    public void Dispose() => _reconciler.Dispose();
}
