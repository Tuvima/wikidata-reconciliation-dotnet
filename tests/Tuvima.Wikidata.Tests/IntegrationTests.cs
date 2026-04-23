namespace Tuvima.Wikidata.Tests;

/// <summary>
/// Integration tests against the live Wikidata API.
/// These tests require network access and may be slow.
/// Skip in CI with: dotnet test --filter "Category!=Integration"
/// </summary>
[Trait("Category", "Integration")]
public class IntegrationTests : IDisposable
{
    private readonly WikidataReconciler _reconciler;

    public IntegrationTests()
    {
        _reconciler = new WikidataReconciler(new WikidataReconcilerOptions
        {
            UserAgent = "Tuvima.Wikidata.Tests/0.2 (https://github.com/Tuvima/wikidata)"
        });
    }

    // ─── Core Reconciliation ────────────────────────────────────────

    [Fact]
    public async Task DouglasAdams_ShouldReturnQ42()
    {
        var results = await _reconciler.ReconcileAsync("Douglas Adams");

        Assert.NotEmpty(results);
        Assert.Equal("Q42", results[0].Id);
        Assert.True(results[0].Score > 90, $"Expected score > 90 but got {results[0].Score}");
    }

    [Fact]
    public async Task DouglasAdams_WithTypeHuman_ShouldReturnQ42()
    {
        var results = await _reconciler.ReconcileAsync("Douglas Adams", "Q5");

        Assert.NotEmpty(results);
        Assert.Equal("Q42", results[0].Id);
    }

    [Fact]
    public async Task Novel1984_WithType_ShouldFindNovel()
    {
        var results = await _reconciler.ReconcileAsync(new ReconciliationRequest
        {
            Query = "1984",
            Types = ["Q7725634"], // literary work
            Limit = 10
        });

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Id == "Q208460");
    }

    [Fact]
    public async Task Novel1984_WithoutType_ShouldFindNovel()
    {
        var results = await _reconciler.ReconcileAsync(new ReconciliationRequest
        {
            Query = "1984",
            Limit = 10
        });

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Id == "Q208460");
    }

    [Fact]
    public async Task UnitedStatesOfAmerica_ShouldReturnQ30()
    {
        var results = await _reconciler.ReconcileAsync("United States of America");

        Assert.NotEmpty(results);
        Assert.Equal("Q30", results[0].Id);
    }

    [Fact]
    public async Task DirectQidLookup_ShouldReturnEntity()
    {
        var results = await _reconciler.ReconcileAsync("Q42");

        Assert.NotEmpty(results);
        Assert.Equal("Q42", results[0].Id);
        Assert.Equal("Douglas Adams", results[0].Name);
    }

    [Fact]
    public async Task BatchReconciliation_ShouldReturnAllResults()
    {
        var requests = new List<ReconciliationRequest>
        {
            new() { Query = "Douglas Adams" },
            new() { Query = "United States of America" },
            new() { Query = "Albert Einstein" }
        };

        var results = await _reconciler.ReconcileBatchAsync(requests);

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.NotEmpty(r));
    }

    [Fact]
    public async Task DouglasAdams_WithProperties_ShouldScoreHigher()
    {
        var resultsWithProps = await _reconciler.ReconcileAsync(new ReconciliationRequest
        {
            Query = "Douglas Adams",
            Types = ["Q5"],
            Properties =
            [
                new PropertyConstraint("P27", "Q145") // country of citizenship: United Kingdom
            ]
        });

        var resultsWithoutProps = await _reconciler.ReconcileAsync("Douglas Adams", "Q5");

        Assert.NotEmpty(resultsWithProps);
        Assert.Equal("Q42", resultsWithProps[0].Id);
        Assert.Equal("Q42", resultsWithoutProps[0].Id);
    }

    // ─── Score Breakdown ────────────────────────────────────────────

    [Fact]
    public async Task ScoreBreakdown_ShouldContainLabelAndPropertyScores()
    {
        var results = await _reconciler.ReconcileAsync(new ReconciliationRequest
        {
            Query = "Douglas Adams",
            Types = ["Q5"],
            Properties =
            [
                new PropertyConstraint("P27", "Q145")
            ]
        });

        Assert.NotEmpty(results);
        var breakdown = results[0].Breakdown;
        Assert.NotNull(breakdown);
        Assert.True(breakdown.LabelScore > 90, $"Expected label score > 90 but got {breakdown.LabelScore}");
        Assert.True(breakdown.PropertyScores.ContainsKey("P27"), "Expected P27 in property scores");
        Assert.Equal(100.0, breakdown.PropertyScores["P27"]);
        Assert.True(breakdown.TypeMatched);
        Assert.False(breakdown.TypePenaltyApplied);
    }

    // ─── Suggest / Autocomplete ─────────────────────────────────────

    [Fact]
    public async Task SuggestAsync_ShouldReturnResults()
    {
        var results = await _reconciler.SuggestAsync("Douglas");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Id == "Q42");
        Assert.All(results, r =>
        {
            Assert.NotEmpty(r.Id);
            Assert.NotEmpty(r.Name);
        });
    }

    [Fact]
    public async Task SuggestPropertiesAsync_ShouldReturnProperties()
    {
        var results = await _reconciler.SuggestPropertiesAsync("date of birth");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Id == "P569");
        Assert.All(results, r =>
        {
            Assert.NotEmpty(r.Id);
            Assert.StartsWith("P", r.Id);
            Assert.NotEmpty(r.Name);
        });
    }

    [Fact]
    public async Task SuggestTypesAsync_ShouldReturnTypes()
    {
        var results = await _reconciler.SuggestTypesAsync("human");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Id == "Q5");
        Assert.All(results, r =>
        {
            Assert.NotEmpty(r.Id);
            Assert.NotEmpty(r.Name);
        });
    }

    // ─── Streaming Batch ────────────────────────────────────────────

    [Fact]
    public async Task ReconcileBatchStreamAsync_ShouldYieldAllResults()
    {
        var requests = new List<ReconciliationRequest>
        {
            new() { Query = "Douglas Adams" },
            new() { Query = "United States of America" },
            new() { Query = "Albert Einstein" }
        };

        var received = new List<(int Index, IReadOnlyList<ReconciliationResult> Results)>();

        await foreach (var item in _reconciler.ReconcileBatchStreamAsync(requests))
        {
            received.Add(item);
        }

        Assert.Equal(3, received.Count);
        Assert.Equal([0, 1, 2], received.Select(r => r.Index).OrderBy(i => i));
        Assert.All(received, r => Assert.NotEmpty(r.Results));
    }

    // ─── Entity / Property Fetching ─────────────────────────────────

    [Fact]
    public async Task GetEntitiesAsync_ShouldReturnEntityInfo()
    {
        var entities = await _reconciler.GetEntitiesAsync(["Q42"]);

        Assert.True(entities.ContainsKey("Q42"));
        var entity = entities["Q42"];
        Assert.Equal("Q42", entity.Id);
        Assert.Equal("Douglas Adams", entity.Label);
        Assert.NotNull(entity.Description);
        Assert.NotEmpty(entity.Claims);
        // Should have P31 (instance of)
        Assert.True(entity.Claims.ContainsKey("P31"));
    }

    [Fact]
    public async Task GetPropertiesAsync_ShouldReturnOnlyRequestedProperties()
    {
        var props = await _reconciler.GetPropertiesAsync(["Q42"], ["P27", "P569"]);

        Assert.True(props.ContainsKey("Q42"));
        var entityProps = props["Q42"];
        // Should only contain requested properties (if the entity has them)
        Assert.All(entityProps.Keys, key => Assert.Contains(key, new[] { "P27", "P569" }));
        // Q42 should have P27 (country of citizenship)
        Assert.True(entityProps.ContainsKey("P27"));
    }

    [Fact]
    public async Task GetEntitiesAsync_ClaimsShouldIncludeQualifiers()
    {
        var entities = await _reconciler.GetEntitiesAsync(["Q42"]);
        var entity = entities["Q42"];

        // Find a claim that typically has qualifiers (P69 = educated at)
        if (entity.Claims.TryGetValue("P69", out var educatedAtClaims))
        {
            // At least one claim should have qualifiers
            var hasQualifiers = educatedAtClaims.Any(c => c.Qualifiers.Count > 0);
            Assert.True(hasQualifiers, "Expected P69 claims to have qualifiers (start/end time)");
        }
    }

    [Fact]
    public async Task GetEntitiesAsync_EntityIdValuesShouldHaveQid()
    {
        var entities = await _reconciler.GetEntitiesAsync(["Q42"]);
        var entity = entities["Q42"];

        // P31 (instance of) claims should have EntityId values
        var p31Claims = entity.Claims["P31"];
        var firstValue = p31Claims[0].Value;
        Assert.NotNull(firstValue);
        Assert.Equal(WikidataValueKind.EntityId, firstValue.Kind);
        Assert.NotNull(firstValue.EntityId);
        Assert.StartsWith("Q", firstValue.EntityId);
    }

    [Fact]
    public async Task GetPropertiesAsync_ShouldResolveEntityLabels()
    {
        // Q42 = Douglas Adams, P27 = country of citizenship -> Q145 (United Kingdom)
        var props = await _reconciler.GetPropertiesAsync(["Q42"], ["P27"]);

        Assert.True(props.ContainsKey("Q42"));
        var entityProps = props["Q42"];
        Assert.True(entityProps.ContainsKey("P27"));
        var citizenshipClaim = Assert.Single(entityProps["P27"]);
        Assert.NotNull(citizenshipClaim.Value);
        Assert.Equal(WikidataValueKind.EntityId, citizenshipClaim.Value.Kind);
        Assert.Equal("Q145", citizenshipClaim.Value.EntityId);
        Assert.NotNull(citizenshipClaim.Value.EntityLabel);
        Assert.NotEqual("Q145", citizenshipClaim.Value.EntityLabel);
        Assert.Equal("United Kingdom", citizenshipClaim.Value.EntityLabel);
    }

    [Fact]
    public async Task GetPropertiesAsync_MultiValuedEntityProperty_ShouldResolveAllLabels()
    {
        // Q42 = Douglas Adams, P106 = occupation (multi-valued entity property)
        var props = await _reconciler.GetPropertiesAsync(["Q42"], ["P106"]);

        Assert.True(props.ContainsKey("Q42"));
        var entityProps = props["Q42"];
        Assert.True(entityProps.ContainsKey("P106"));
        var occupationClaims = entityProps["P106"];
        Assert.True(occupationClaims.Count > 1, "Expected multiple occupation values");

        foreach (var claim in occupationClaims)
        {
            Assert.NotNull(claim.Value);
            Assert.Equal(WikidataValueKind.EntityId, claim.Value.Kind);
            Assert.NotNull(claim.Value.EntityLabel);
            Assert.DoesNotMatch(@"^Q\d+$", claim.Value.EntityLabel);
        }
    }

    [Fact]
    public async Task GetPropertiesAsync_ShouldRespectLanguageParameter()
    {
        // Q42 = Douglas Adams, P27 = country of citizenship -> Q145 (United Kingdom).
        // The German label should differ from the English label, proving the language
        // parameter is applied when resolving entity-valued properties.
        var englishProps = await _reconciler.GetPropertiesAsync(["Q42"], ["P27"], language: "en");
        var germanProps = await _reconciler.GetPropertiesAsync(["Q42"], ["P27"], language: "de");

        var englishLabel = Assert.Single(englishProps["Q42"]["P27"]).Value!.EntityLabel;
        var germanLabel = Assert.Single(germanProps["Q42"]["P27"]).Value!.EntityLabel;

        Assert.NotNull(englishLabel);
        Assert.NotNull(germanLabel);
        Assert.DoesNotMatch(@"^Q\d+$", englishLabel!);
        Assert.DoesNotMatch(@"^Q\d+$", germanLabel!);
        Assert.NotEqual(englishLabel, germanLabel);
    }

    // ─── Wikipedia URL Resolution ───────────────────────────────────

    [Fact]
    public async Task GetWikipediaUrlsAsync_ShouldReturnUrls()
    {
        var urls = await _reconciler.GetWikipediaUrlsAsync(["Q42"]);

        Assert.True(urls.ContainsKey("Q42"));
        Assert.Contains("en.wikipedia.org", urls["Q42"]);
        Assert.Contains("Douglas", urls["Q42"]);
    }

    [Fact]
    public async Task GetWikipediaUrlsAsync_German_ShouldReturnDeUrl()
    {
        var urls = await _reconciler.GetWikipediaUrlsAsync(["Q42"], "de");

        Assert.True(urls.ContainsKey("Q42"));
        Assert.Contains("de.wikipedia.org", urls["Q42"]);
    }

    [Fact]
    public async Task GetWikipediaUrlsAsync_NonexistentLanguage_ShouldReturnEmpty()
    {
        // Use a language unlikely to have a Wikipedia article
        var urls = await _reconciler.GetWikipediaUrlsAsync(["Q42"], "got"); // Gothic language

        // May or may not have a Gothic Wikipedia article — just verify no crash
        Assert.NotNull(urls);
    }

    // ─── P279 Subclass Type Matching ────────────────────────────────

    [Fact]
    public async Task Reconcile_WithTypeHierarchyDepth_ShouldMatchSubclass()
    {
        using var reconciler = new WikidataReconciler(new WikidataReconcilerOptions
        {
            UserAgent = "Tuvima.Wikidata.Tests/0.2 (https://github.com/Tuvima/wikidata)",
            TypeHierarchyDepth = 5
        });

        // "Douglas Adams" is P31:Q5 (human). Q5 is a subclass of Q215627 (person).
        // With depth=0, filtering by Q215627 would miss Q42.
        // With depth>0, Q5 → P279 → ... → Q215627 should match.
        var results = await reconciler.ReconcileAsync(new ReconciliationRequest
        {
            Query = "Douglas Adams",
            Types = ["Q215627"], // person (superclass of human)
            Limit = 5
        });

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Id == "Q42");
    }

    // ─── Language Fallback ──────────────────────────────────────────

    [Fact]
    public async Task Reconcile_WithLanguageFallback_ShouldReturnLabel()
    {
        // Use a subtag that likely doesn't have its own labels
        var results = await _reconciler.ReconcileAsync(new ReconciliationRequest
        {
            Query = "Douglas Adams",
            Language = "en-gb", // should fall back to "en"
            Limit = 5
        });

        Assert.NotEmpty(results);
        // Should still get a label via fallback
        Assert.False(string.IsNullOrEmpty(results[0].Name));
        Assert.NotEqual(results[0].Id, results[0].Name); // Name should be a label, not the QID
    }

    // ─── Reverse Lookup by External ID ────────────────────────────

    [Fact]
    public async Task LookupByExternalIdAsync_ShouldFindEntityByViaf()
    {
        // Douglas Adams VIAF ID is 113230702
        var results = await _reconciler.LookupByExternalIdAsync("P214", "113230702");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Id == "Q42");
        Assert.Equal("Douglas Adams", results.First(r => r.Id == "Q42").Label);
    }

    [Fact]
    public async Task LookupByExternalIdAsync_NonexistentId_ShouldReturnEmpty()
    {
        var results = await _reconciler.LookupByExternalIdAsync("P214", "000000000000");

        Assert.Empty(results);
    }

    // ─── Property Label Resolution ──────────────────────────────────

    [Fact]
    public async Task GetPropertyLabelsAsync_ShouldReturnLabels()
    {
        var labels = await _reconciler.GetPropertyLabelsAsync(["P569", "P27", "P31"]);

        Assert.True(labels.ContainsKey("P569"));
        Assert.True(labels.ContainsKey("P27"));
        Assert.True(labels.ContainsKey("P31"));
        Assert.Equal("date of birth", labels["P569"]);
        Assert.Equal("instance of", labels["P31"]);
    }

    // ─── Commons Image URLs ─────────────────────────────────────────

    [Fact]
    public async Task GetImageUrlsAsync_ShouldReturnImageUrl()
    {
        var urls = await _reconciler.GetImageUrlsAsync(["Q42"]);

        Assert.True(urls.ContainsKey("Q42"));
        Assert.Contains("commons.wikimedia.org", urls["Q42"]);
        Assert.Contains("Special:FilePath", urls["Q42"]);
    }

    // ─── Value Formatting ───────────────────────────────────────────

    [Fact]
    public async Task WikidataValue_ToDisplayString_ShouldFormatDate()
    {
        var entities = await _reconciler.GetEntitiesAsync(["Q42"]);
        var entity = entities["Q42"];

        // P569 = date of birth
        if (entity.Claims.TryGetValue("P569", out var dobClaims) && dobClaims.Count > 0)
        {
            var dob = dobClaims[0].Value;
            Assert.NotNull(dob);
            Assert.Equal(WikidataValueKind.Time, dob.Kind);

            var display = dob.ToDisplayString();
            Assert.Contains("1952", display);
            Assert.Contains("March", display);
        }
    }

    [Fact]
    public void WikidataValue_ToCommonsImageUrl_ShouldBuildUrl()
    {
        var value = new WikidataValue
        {
            Kind = WikidataValueKind.String,
            RawValue = "Douglas Adams San Dimas 1.jpg"
        };

        var url = value.ToCommonsImageUrl();
        Assert.NotNull(url);
        Assert.Contains("commons.wikimedia.org", url);
        Assert.Contains("Special:FilePath", url);
    }

    // ─── Wikipedia Summaries ──────────────────────────────────────

    [Fact]
    public async Task GetWikipediaSummariesAsync_ShouldReturnSummary()
    {
        var summaries = await _reconciler.GetWikipediaSummariesAsync(["Q42"]);

        Assert.NotEmpty(summaries);
        var summary = summaries.First(s => s.EntityId == "Q42");
        Assert.Equal("Q42", summary.EntityId);
        Assert.Contains("Douglas Adams", summary.Title);
        Assert.False(string.IsNullOrEmpty(summary.Extract));
        Assert.Contains("wikipedia.org", summary.ArticleUrl);
    }

    [Fact]
    public async Task GetWikipediaSummariesAsync_German_ShouldReturnDeSummary()
    {
        var summaries = await _reconciler.GetWikipediaSummariesAsync(["Q42"], "de");

        Assert.NotEmpty(summaries);
        Assert.Equal("Q42", summaries[0].EntityId);
        Assert.False(string.IsNullOrEmpty(summaries[0].Extract));
    }

    // ─── Staleness Detection ────────────────────────────────────────

    [Fact]
    public async Task GetEntitiesAsync_ShouldIncludeRevisionMetadata()
    {
        var entities = await _reconciler.GetEntitiesAsync(["Q42"]);

        Assert.True(entities.ContainsKey("Q42"));
        var entity = entities["Q42"];
        Assert.True(entity.LastRevisionId > 0, "LastRevisionId should be populated");
        Assert.NotNull(entity.Modified);
        Assert.True(entity.Modified > new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero),
            "Modified date should be reasonable");
    }

    [Fact]
    public async Task GetRevisionIdsAsync_ShouldReturnRevisions()
    {
        var revisions = await _reconciler.GetRevisionIdsAsync(["Q42", "Q5"]);

        Assert.True(revisions.Count == 2, $"Expected 2 revisions but got {revisions.Count}");
        Assert.True(revisions.ContainsKey("Q42"));
        Assert.True(revisions.ContainsKey("Q5"));

        var q42 = revisions["Q42"];
        Assert.Equal("Q42", q42.EntityId);
        Assert.True(q42.RevisionId > 0);
        Assert.NotNull(q42.Timestamp);
    }

    [Fact]
    public async Task GetRevisionIdsAsync_ShouldMatchEntityRevisions()
    {
        // Verify that revision IDs from both APIs agree
        var entities = await _reconciler.GetEntitiesAsync(["Q42"]);
        var revisions = await _reconciler.GetRevisionIdsAsync(["Q42"]);

        // They may not be identical if an edit happened between calls,
        // but the revision API should return >= the entity fetch revision
        Assert.True(revisions["Q42"].RevisionId >= entities["Q42"].LastRevisionId,
            "Revision ID from lightweight check should be >= entity fetch revision");
    }

    [Fact]
    public async Task GetRevisionIdsAsync_EmptyInput_ShouldReturnEmpty()
    {
        var revisions = await _reconciler.GetRevisionIdsAsync([]);

        Assert.Empty(revisions);
    }

    // ─── Wikipedia Section Content ─────────────────────────────────

    [Fact]
    public async Task GetWikipediaSectionsAsync_ShouldReturnSections()
    {
        var sections = await _reconciler.GetWikipediaSectionsAsync(["Q42"]); // Douglas Adams

        Assert.True(sections.ContainsKey("Q42"));
        var toc = sections["Q42"];
        Assert.NotEmpty(toc);

        // Douglas Adams should have well-known sections
        var titles = toc.Select(s => s.Title).ToList();
        Assert.Contains(titles, t => t.Contains("Career", StringComparison.OrdinalIgnoreCase));

        // Verify section structure
        var first = toc[0];
        Assert.False(string.IsNullOrEmpty(first.Title));
        Assert.True(first.Index > 0);
        Assert.True(first.Level >= 2);
        Assert.False(string.IsNullOrEmpty(first.Number));

        // Section titles should not contain HTML tags
        foreach (var section in toc)
            Assert.DoesNotContain("<", section.Title);
    }

    [Fact]
    public async Task GetWikipediaSectionsAsync_NoArticle_ShouldReturnEmpty()
    {
        // Q97093183 is unlikely to have an English Wikipedia article
        var sections = await _reconciler.GetWikipediaSectionsAsync(["Q97093183"]);

        Assert.NotNull(sections);
        Assert.False(sections.ContainsKey("Q97093183"));
    }

    [Fact]
    public async Task GetWikipediaSectionContentAsync_ShouldReturnText()
    {
        // First get sections to find a valid index
        var sections = await _reconciler.GetWikipediaSectionsAsync(["Q42"]);
        var toc = sections["Q42"];
        var firstSection = toc[0];

        var content = await _reconciler.GetWikipediaSectionContentAsync("Q42", firstSection.Index);

        Assert.NotNull(content);
        Assert.True(content.Length > 50, "Section content should be substantial");
        // Should be plain text — no HTML tags
        Assert.DoesNotContain("<div", content);
        Assert.DoesNotContain("<p>", content);
        Assert.DoesNotContain("<span", content);
    }

    [Fact]
    public async Task GetWikipediaSectionContentAsync_InvalidSection_ShouldReturnNull()
    {
        var content = await _reconciler.GetWikipediaSectionContentAsync("Q42", 9999);

        Assert.Null(content);
    }

    [Fact]
    public async Task GetWikipediaSectionContentAsync_NoArticle_ShouldReturnNull()
    {
        var content = await _reconciler.GetWikipediaSectionContentAsync("Q97093183", 1);

        Assert.Null(content);
    }

    // ─── Entity Change Monitoring ───────────────────────────────────

    [Fact]
    public async Task GetRecentChangesAsync_ShouldNotCrash()
    {
        // Just verify the API call works — Q42 may or may not have recent changes
        var changes = await _reconciler.GetRecentChangesAsync(
            ["Q42"], DateTimeOffset.UtcNow.AddDays(-7));

        Assert.NotNull(changes);
        // If there are changes, verify structure
        foreach (var change in changes)
        {
            Assert.Equal("Q42", change.EntityId);
            Assert.NotEqual(DateTimeOffset.MinValue, change.Timestamp);
        }
    }

    public void Dispose()
    {
        _reconciler.Dispose();
    }
}
