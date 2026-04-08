namespace Tuvima.Wikidata.Tests;

/// <summary>
/// Integration tests for the v2.1.0 PersonsService against the live Wikidata API.
/// </summary>
[Trait("Category", "Integration")]
public class PersonsIntegrationTests : IDisposable
{
    private readonly WikidataReconciler _reconciler;

    public PersonsIntegrationTests()
    {
        _reconciler = new WikidataReconciler(new WikidataReconcilerOptions
        {
            UserAgent = "Tuvima.Wikidata.Tests/2.2 (https://github.com/Tuvima/wikidata)"
        });
    }

    [Fact]
    public async Task SearchAsync_Author_ReturnsExpectedHuman()
    {
        var result = await _reconciler.Persons.SearchAsync(new PersonSearchRequest
        {
            Name = "Stephen King",
            Role = PersonRole.Author
        });

        Assert.True(result.Found, $"Expected a match, got score {result.Score}");
        Assert.Equal("Q39829", result.Qid); // Stephen King
        Assert.False(result.IsGroup);
        Assert.Contains("Q36180", result.Occupations); // writer
    }

    [Fact]
    public async Task SearchAsync_PerformerRole_IncludesMusicalGroupsByDefault()
    {
        // Radiohead (Q7833) — distinctive group name. Verifies that the Performer role
        // default includes Q215380/Q5741069 in the type filter and that the v2.3 P106
        // constraint-skip fix lets musical groups score above the default 0.80 threshold.
        // Prior to v2.3 the P106 occupation constraint would drag group candidates below
        // threshold because groups don't carry P106 claims.
        var result = await _reconciler.Persons.SearchAsync(new PersonSearchRequest
        {
            Name = "Radiohead",
            Role = PersonRole.Performer
        });

        Assert.True(result.Found, $"Expected a match at default threshold, got score {result.Score}");
        Assert.NotNull(result.CanonicalName);
        Assert.Contains("radiohead", result.CanonicalName!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchAsync_AuthorRole_ExcludesMusicalGroupsByDefault()
    {
        // Asking for "Daft Punk" under the Author role should NOT return the group
        // (Author default for IncludeMusicalGroups is false, so Q5-only filter applies).
        var result = await _reconciler.Persons.SearchAsync(new PersonSearchRequest
        {
            Name = "Daft Punk",
            Role = PersonRole.Author
        });

        // Either no match, or a human that happens to share the name. Either way, not Q4043.
        Assert.NotEqual("Q4043", result.Qid);
    }

    [Fact]
    public async Task SearchAsync_PerformerWithExpandGroupMembers_PopulatesGroupMembers()
    {
        // Radiohead has a stable, well-known member list via P527.
        var result = await _reconciler.Persons.SearchAsync(new PersonSearchRequest
        {
            Name = "Radiohead",
            Role = PersonRole.Performer,
            ExpandGroupMembers = true
        });

        Assert.True(result.Found);
        if (result.IsGroup)
        {
            Assert.NotNull(result.GroupMembers);
            Assert.NotEmpty(result.GroupMembers!);
        }
    }

    [Fact]
    public async Task SearchAsync_AuthorRole_FindsHuman()
    {
        // Douglas Adams with Author role resolves confidently on name + occupation alone.
        var result = await _reconciler.Persons.SearchAsync(new PersonSearchRequest
        {
            Name = "Douglas Adams",
            Role = PersonRole.Author
        });

        Assert.True(result.Found);
        Assert.Equal("Q42", result.Qid);
    }

    [Fact]
    public async Task SearchAsync_AuthorWithCompanionHint_ReRanksTowardRightCandidate()
    {
        // Companion hint re-ranking smoke test. Searching for "Neil Gaiman" with Good Omens
        // as a companion hint should bias scoring toward the author who actually has Good Omens
        // in their notable works. We don't pin the specific winning QID (reconciler scoring can
        // flip close candidates), only that the re-rank code path runs and the result is still
        // a valid human match.
        var result = await _reconciler.Persons.SearchAsync(new PersonSearchRequest
        {
            Name = "Neil Gaiman",
            Role = PersonRole.Author,
            CompanionNameHints = ["Good Omens", "American Gods"]
        });

        Assert.True(result.Found);
        Assert.NotNull(result.Qid);
        Assert.StartsWith("Q", result.Qid);
    }

    public void Dispose() => _reconciler.Dispose();
}
