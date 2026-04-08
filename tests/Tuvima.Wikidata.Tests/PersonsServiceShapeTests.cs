namespace Tuvima.Wikidata.Tests;

/// <summary>
/// Contract tests for the v2.1.0 PersonsService — verifies facade wiring, DTO defaults,
/// and the documented role-to-IncludeMusicalGroups default behavior without hitting the network.
/// </summary>
public class PersonsServiceShapeTests
{
    [Fact]
    public void Facade_ExposesPersonsService()
    {
        using var reconciler = new WikidataReconciler();
        Assert.NotNull(reconciler.Persons);
    }

    [Fact]
    public void PersonSearchRequest_DefaultsAreConservative()
    {
        var req = new PersonSearchRequest { Name = "Neil Gaiman" };

        Assert.Equal(PersonRole.Unknown, req.Role);
        Assert.Null(req.IncludeMusicalGroups);
        Assert.False(req.ExpandGroupMembers);
        Assert.Equal(0.80, req.AcceptThreshold);
        Assert.Null(req.BirthYearHint);
        Assert.Null(req.DeathYearHint);
    }

    [Fact]
    public void PersonSearchRequest_AcceptsAllRoles()
    {
        var roles = Enum.GetValues<PersonRole>();
        Assert.Contains(PersonRole.Author, roles);
        Assert.Contains(PersonRole.Narrator, roles);
        Assert.Contains(PersonRole.Director, roles);
        Assert.Contains(PersonRole.Actor, roles);
        Assert.Contains(PersonRole.VoiceActor, roles);
        Assert.Contains(PersonRole.Composer, roles);
        Assert.Contains(PersonRole.Performer, roles);
        Assert.Contains(PersonRole.Artist, roles);
        Assert.Contains(PersonRole.Screenwriter, roles);
    }

    [Fact]
    public async Task SearchAsync_EmptyName_Throws()
    {
        using var reconciler = new WikidataReconciler();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await reconciler.Persons.SearchAsync(new PersonSearchRequest { Name = "   " }));
    }

    [Fact]
    public void PersonSearchResult_DefaultShape_IsNotFound()
    {
        var result = new PersonSearchResult();

        Assert.False(result.Found);
        Assert.Null(result.Qid);
        Assert.Null(result.CanonicalName);
        Assert.False(result.IsGroup);
        Assert.Equal(0.0, result.Score);
        Assert.Empty(result.Occupations);
        Assert.Empty(result.NotableWorks);
        Assert.Null(result.GroupMembers);
    }

    [Fact]
    public void PersonSearchRequest_WithCompanionHints_StoresList()
    {
        var req = new PersonSearchRequest
        {
            Name = "Terry Pratchett",
            CompanionNameHints = ["Neil Gaiman"],
            BirthYearHint = 1948,
            DeathYearHint = 2015
        };

        Assert.NotNull(req.CompanionNameHints);
        Assert.Single(req.CompanionNameHints!);
        Assert.Equal(1948, req.BirthYearHint);
        Assert.Equal(2015, req.DeathYearHint);
    }
}
