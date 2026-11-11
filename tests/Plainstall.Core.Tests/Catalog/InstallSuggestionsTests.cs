using Plainstall.Core.Catalog;
using Plainstall.Core.Profiles;

namespace Plainstall.Core.Tests.Catalog;

public class InstallSuggestionsTests
{
    private static readonly InstallSuggestions Embedded = InstallSuggestions.LoadEmbedded();

    [Fact]
    public void The_pick_list_loads_and_matches_its_schema()
    {
        Assert.NotEmpty(Embedded.Entries);
        Assert.All(Embedded.Entries, e => Assert.Contains(Embedded.Categories, c => c.Id == e.Category));
        CatalogTests.AssertMatchesSchema(File.ReadAllText(TestFiles.CatalogFile("installs.json")),
            "installs.schema.json", "installs.json");
    }

    [Fact]
    public void Every_suggested_id_is_accepted_by_a_profile()
    {
        var preset = CatalogLoader.LoadEmbedded().DefaultPreset;
        var profile = ProfileFactory.FromPreset(preset);
        // A profile holds at most 50 installs, and the pick list is longer than that.
        foreach (var chunk in Embedded.Entries.Select(e => e.WingetId).Chunk(50))
        {
            var withChunk = profile with { Apps = profile.Apps with { Install = chunk } };
            Assert.Empty(ProfileSerializer.Validate(withChunk));
        }
    }

    [Fact]
    public void A_browser_is_offered_and_edge_can_be_put_back()
    {
        Assert.Contains(("browsers", "Mozilla.Firefox"), Embedded.Entries.Select(e => (e.Category, e.WingetId)));
        Assert.NotNull(Embedded.Find("microsoft.edge"));
    }

    [Theory]
    [InlineData("Mozilla.Firefox", true)]
    [InlineData("Notepad++.Notepad++", true)]
    [InlineData("7zip.7zip", true)]
    [InlineData("firefox", false)]
    [InlineData("Mozilla.Firefox\n", false)]
    [InlineData("Mozilla.Firefox --override x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("9NKSQGP7F2NH", true)]
    [InlineData("XP89DCGQ3K6VLD", true)]
    [InlineData("9nksqgp7f2nh", false)]
    [InlineData("9NKSQGP7F2N", false)]
    [InlineData("9NKSQGP7F2NHX", false)]
    [InlineData("XP89DCGQ3K6VL", false)]
    [InlineData("8NKSQGP7F2NH", false)]
    [InlineData("9NKSQGP7F2NH\n", false)]
    [InlineData("9NKSQGP7F-NH", false)]
    public void Winget_and_store_ids_are_checked_by_shape(string? id, bool valid)
    {
        Assert.Equal(valid, InstallIds.IsValid(id));
    }

    [Theory]
    [InlineData("9NKSQGP7F2NH", true)]
    [InlineData("XP89DCGQ3K6VLD", true)]
    [InlineData("Mozilla.Firefox", false)]
    [InlineData("9NKSQGP7F2NH.Extra", false)]
    [InlineData(null, false)]
    public void Store_ids_are_told_apart_from_winget_ids(string? id, bool store)
    {
        Assert.Equal(store, InstallIds.IsStoreId(id));
    }

    [Fact]
    public void A_store_id_in_a_profile_is_accepted_and_a_malformed_one_is_refused()
    {
        var preset = CatalogLoader.LoadEmbedded().DefaultPreset;
        var profile = ProfileFactory.FromPreset(preset);
        var good = profile with { Apps = profile.Apps with { Install = ["9NKSQGP7F2NH", "Mozilla.Firefox"] } };
        var bad = profile with { Apps = profile.Apps with { Install = ["9nksqgp7f2nh"] } };

        Assert.Empty(ProfileSerializer.Validate(good));
        Assert.Contains(ProfileSerializer.Validate(bad), p => p.Contains("apps.install", StringComparison.Ordinal));
        CatalogTests.AssertMatchesSchema(new ProfileSerializer().ToJson(good), "profile.schema.json", "store id");
    }

    [Fact]
    public void A_broken_pick_list_is_refused_with_every_problem_named()
    {
        const string json = """
                            { "catalogVersion": 1,
                              "categories": [ { "id": "a", "title": "A" }, { "id": "unused", "title": "U" } ],
                              "entries": [
                                { "wingetId": "Good.App", "title": "Good", "description": "d", "category": "a" },
                                { "wingetId": "good.app", "title": "Twice", "description": "d", "category": "a" },
                                { "wingetId": "bad id", "title": "Bad", "description": "d", "category": "missing" }
                              ] }
                            """;
        var ex = Assert.Throws<PlainstallException>(() => InstallSuggestions.FromJson(json));
        Assert.Contains("listed twice", ex.Message);
        Assert.Contains("neither a winget package id nor a Store product id", ex.Message);
        Assert.Contains("unknown category", ex.Message);
        Assert.Contains("has no entries", ex.Message);
    }
}