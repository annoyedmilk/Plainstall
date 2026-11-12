using System.Text.Json.Nodes;
using Plainstall.Core.Catalog;
using Plainstall.Core.Profiles;
using Plainstall.Core.Tests.Catalog;

namespace Plainstall.Core.Tests.Profiles;

public class ProfileTests
{
    private const string Minimal = """
                                   {
                                     "schema": 1,
                                     "name": "Test",
                                     "windows": { "source": "iso", "language": "auto", "edition": "Professional" },
                                     "apps": { "preset": "recommended" },
                                     "tweaks": { "preset": "recommended" },
                                     "setup": { "localAccount": null, "skipPrivacyPages": false, "bypassHardwareChecks": false }
                                   }
                                   """;

    private static string With(Action<JsonObject> mutate)
    {
        var node = JsonNode.Parse(Minimal)!.AsObject();
        mutate(node);
        return node.ToJsonString();
    }

    [Fact]
    public void Example_profiles_load()
    {
        var serializer = new ProfileSerializer();
        foreach (var path in TestFiles.ExampleProfiles())
        {
            var result = serializer.Load(path);
            Assert.Equal(Profile.CurrentSchema, result.Profile.Schema);
            Assert.Empty(result.Warnings);
        }
    }

    [Fact]
    public void The_example_profile_has_the_expected_values()
    {
        var path = TestFiles.ExampleProfiles().Single(p => p.EndsWith("moms-laptop.plainstall.json"));
        var profile = new ProfileSerializer().Load(path).Profile;
        Assert.Equal("Mom's laptop", profile.Name);
        Assert.Equal(WindowsSource.Download, profile.Windows.Source);
        Assert.Equal("Professional", profile.Windows.Edition);
        Assert.Equal(["Microsoft.GetHelp"], profile.Apps.Keep);
        Assert.Equal(["dark-theme"], profile.Tweaks.Enable);
        Assert.Equal("Anna", profile.Setup.LocalAccount!.Name);
        Assert.True(profile.Output.Usb);
        Assert.False(profile.Cleanup.ResetBase);
        Assert.True(profile.Verify.ScanHealth);
    }

    [Fact]
    public void Defaults_apply_to_sections_that_are_left_out()
    {
        var profile = new ProfileSerializer().Parse(Minimal).Profile;
        Assert.Null(profile.Setup.LocalAccount);
        Assert.Equal("auto", profile.Setup.Region);
        Assert.Equal("auto", profile.Setup.Timezone);
        Assert.Empty(profile.Updates);
        Assert.False(profile.Drivers.FromThisPc);
        Assert.False(profile.Verify.ScanHealth);
        Assert.True(profile.Output.Usb);
        Assert.False(profile.Output.Iso);
    }

    [Fact]
    public void A_profile_survives_save_and_load_and_the_saved_file_matches_the_schema()
    {
        var serializer = new ProfileSerializer();
        var original = serializer.Parse(With(p =>
        {
            p["setup"]!["localAccount"] = new JsonObject
                { ["name"] = "Anna", ["askPassword"] = true, ["password"] = null };
            p["setup"]!["computerName"] = "ANNA-PC";
            // The Setup tab writes "ask" when Windows is to pick the time zone from the region.
            p["setup"]!["timezone"] = "ask";
            p["apps"]!["install"] = new JsonArray("Mozilla.Firefox", "7zip.7zip");
            p["updates"] = new JsonArray(@"C:\updates\kb1.msu");
        })).Profile;

        var json = serializer.ToJson(original, "0.1.0");
        CatalogTests.AssertMatchesSchema(json, "profile.schema.json", "saved profile");

        var again = serializer.Parse(json).Profile;
        Assert.Equal(original.Name, again.Name);
        Assert.Equal(original.Setup.LocalAccount, again.Setup.LocalAccount);
        Assert.Equal(original.Setup.ComputerName, again.Setup.ComputerName);
        Assert.Equal("ask", again.Setup.Timezone);
        Assert.Equal(original.Apps.Install, again.Apps.Install);
        Assert.Equal(original.Updates, again.Updates);
        Assert.Equal("0.1.0", again.CreatedWith);
    }

    [Fact]
    public void Save_writes_the_file_atomically_and_load_reads_it_back()
    {
        using var temp = new TempDirectory();
        var serializer = new ProfileSerializer();
        var path = temp.File("a.plainstall.json");
        serializer.Save(serializer.Parse(Minimal).Profile, path);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal("Test", serializer.Load(path).Profile.Name);
    }

    [Fact]
    public void A_password_in_the_file_is_refused_with_an_explanation()
    {
        var json = With(p =>
            p["setup"]!["localAccount"] = new JsonObject { ["name"] = "Anna", ["password"] = "hunter2" });
        var ex = Assert.Throws<PlainstallException>(() => new ProfileSerializer().Parse(json));
        Assert.Equal(ErrorKind.InvalidInput, ex.Kind);
        Assert.Contains("never stores passwords", ex.Message);
        Assert.DoesNotContain("hunter2", ex.Message);
    }

    [Fact]
    public void A_password_can_never_be_written()
    {
        var serializer = new ProfileSerializer();
        var profile = serializer.Parse(Minimal).Profile;
        profile = profile with
        {
            Setup = profile.Setup with
            {
                LocalAccount = new LocalAccountOptions { Name = "Anna", Password = "hunter2" }
            }
        };
        Assert.DoesNotContain("hunter2", serializer.ToJson(profile));
    }

    [Fact]
    public void A_newer_schema_is_refused_and_asks_for_an_update()
    {
        var ex = Assert.Throws<PlainstallException>(() => new ProfileSerializer().Parse(With(p => p["schema"] = 99)));
        Assert.Contains("Update Plainstall", ex.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("{ \"schema\": \"one\" }")]
    [InlineData("{ \"schema\": 0 }")]
    public void Files_that_are_not_profiles_are_refused(string json)
    {
        Assert.Throws<PlainstallException>(() => new ProfileSerializer().Parse(json));
    }

    [Fact]
    public void Unknown_properties_are_refused_so_typos_do_not_pass_silently()
    {
        Assert.Throws<PlainstallException>(() =>
            new ProfileSerializer().Parse(With(p => p["tweeks"] = new JsonObject())));
    }

    [Fact]
    public void There_is_no_architecture_setting_because_only_x64_is_supported()
    {
        Assert.Throws<PlainstallException>(() =>
            new ProfileSerializer().Parse(With(p => p["windows"]!["arch"] = "arm64")));
    }

    [Fact]
    public void Older_schemas_are_migrated_step_by_step()
    {
        // A made-up history: schema 1 had "title", schema 2 renamed it to "name", schema 3 added nothing.
        var steps = new Dictionary<int, ProfileMigrationStep>
        {
            [1] = p =>
            {
                p["name"] = p["title"]!.DeepClone();
                p.Remove("title");
                return p;
            },
            [2] = p => p
        };
        var serializer = new ProfileSerializer(steps, 3);
        var old = With(p =>
        {
            p["title"] = "Old name";
            p.Remove("name");
        });

        var result = serializer.Parse(old);

        Assert.Equal("Old name", result.Profile.Name);
        Assert.Equal(3, result.Profile.Schema);
        Assert.Contains("from schema 1 to 3", Assert.Single(result.Warnings));
    }

    [Theory]
    [InlineData("windows.language", "deutsch")]
    [InlineData("windows.edition", "Pro 11")]
    [InlineData("apps.preset", "everything")]
    [InlineData("setup.computerName", "this-name-is-far-too-long")]
    [InlineData("setup.computerName", "12345")]
    [InlineData("setup.region", "Switzerland")]
    public void Invalid_values_are_reported_with_the_field_name(string field, string value)
    {
        var parts = field.Split('.');
        var json = With(p => p[parts[0]]![parts[1]] = value);
        var ex = Assert.Throws<PlainstallException>(() => new ProfileSerializer().Parse(json));
        Assert.Contains(field, ex.Message);
    }

    [Theory]
    [InlineData("An/na")]
    [InlineData("name@example.com")]
    [InlineData("this account name is too long")]
    [InlineData("Anna.")]
    public void Invalid_account_names_are_refused(string name)
    {
        var json = With(p => p["setup"]!["localAccount"] = new JsonObject { ["name"] = name });
        Assert.Throws<PlainstallException>(() => new ProfileSerializer().Parse(json));
    }

    /// <summary>The answer file refuses these names too. The profile check finds them before the build starts.</summary>
    [Theory]
    [InlineData("Administrator", null)]
    [InlineData("guest", null)]
    [InlineData("Power Users", null)]
    [InlineData("An\u0001na", null)]
    [InlineData("Anna-PC", "ANNA-PC")]
    public void Account_names_that_windows_setup_refuses_are_found_by_the_profile_check(string name,
        string? computerName)
    {
        var profile = ProfileFactory.FromPreset(CatalogLoader.LoadEmbedded().DefaultPreset);
        profile = profile with
        {
            Setup = profile.Setup with
            {
                LocalAccount = new LocalAccountOptions { Name = name }, ComputerName = computerName
            }
        };

        var problem = Assert.Single(ProfileSerializer.Validate(profile));
        Assert.StartsWith("setup.localAccount.name", problem);
    }

    [Theory]
    [InlineData("firefox")]
    [InlineData("Mozilla.Firefox; rm -rf")]
    [InlineData("Mozilla.Firefox --override x")]
    public void Winget_ids_must_look_like_package_ids(string id)
    {
        var json = With(p => p["apps"]!["install"] = new JsonArray(id));
        Assert.Throws<PlainstallException>(() => new ProfileSerializer().Parse(json));
    }

    [Fact]
    public void A_new_profile_from_a_preset_is_valid_and_carries_the_preset_setup_defaults()
    {
        var catalog = CatalogLoader.LoadEmbedded();
        foreach (var preset in catalog.Presets)
        {
            var profile = ProfileFactory.FromPreset(preset);
            Assert.Empty(ProfileSerializer.Validate(profile));
            Assert.Equal(preset.Setup.LocalAccount, profile.Setup.LocalAccount is not null);
            Assert.Equal(preset.Setup.BypassHardwareChecks, profile.Setup.BypassHardwareChecks);
            CatalogTests.AssertMatchesSchema(new ProfileSerializer().ToJson(profile), "profile.schema.json", preset.Id);
        }
    }
}