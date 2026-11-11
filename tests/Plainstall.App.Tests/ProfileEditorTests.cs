using Plainstall.App.Resources;
using Plainstall.App.ViewModels;
using Plainstall.Core.Catalog;
using Plainstall.Core.Pipeline;
using Plainstall.Core.Profiles;

namespace Plainstall.App.Tests;

public class ProfileEditorTests
{
    private static readonly ImageFacts Image = new(26300, "Professional");

    [Theory]
    [InlineData("recommended")]
    [InlineData("privacy-max")]
    [InlineData("gaming")]
    [InlineData("minimal")]
    public void Choosing_a_preset_card_gives_exactly_the_preset_profile(string presetId)
    {
        var w = new TestWizard();
        w.Welcome.Presets.First(p => p.Preset.Id == presetId).IsSelected = true;

        var expected = w.Preset(presetId);
        Assert.Equal(new ProfileSerializer().ToJson(expected), new ProfileSerializer().ToJson(w.Profile));
        Assert.Single(w.Welcome.Presets, p => p.IsSelected);
    }

    [Fact]
    public void Recommended_is_selected_at_start()
    {
        var w = new TestWizard();
        Assert.Equal("recommended", Assert.Single(w.Welcome.Presets, p => p.IsSelected).Preset.Id);
        Assert.Equal("recommended", w.Profile.Apps.Preset);
    }

    [Fact]
    public void Switches_become_keep_remove_enable_disable_relative_to_the_preset()
    {
        var w = new TestWizard();
        var editor = w.Session.Editor;

        editor.Set(w.Entry("onedrive"), false); // in Recommended, kept now
        editor.Set(w.Entry("calculator"), true); // not in Recommended, removed now
        editor.Set(w.Entry("no-advertising-id"), false); // in Recommended, off now
        editor.Set(w.Entry("dark-theme"), true); // not in Recommended, on now

        Assert.Equal(["onedrive"], w.Profile.Apps.Keep);
        Assert.Equal(["calculator"], w.Profile.Apps.Remove);
        Assert.Equal(["no-advertising-id"], w.Profile.Tweaks.Disable);
        Assert.Equal(["dark-theme"], w.Profile.Tweaks.Enable);
        Assert.Equal(4, editor.ChangedCount);
    }

    [Fact]
    public void Switching_back_leaves_no_trace()
    {
        var w = new TestWizard();
        var editor = w.Session.Editor;
        editor.Set(w.Entry("onedrive"), false);
        editor.Set(w.Entry("onedrive"), true);
        editor.Set(w.Entry("dark-theme"), true);
        editor.Set(w.Entry("dark-theme"), false);

        Assert.Empty(w.Profile.Apps.Keep);
        Assert.Empty(w.Profile.Apps.Remove);
        Assert.Empty(w.Profile.Tweaks.Enable);
        Assert.Empty(w.Profile.Tweaks.Disable);
        Assert.Equal(0, editor.ChangedCount);
    }

    [Fact]
    public void Reset_to_preset_clears_every_switch_but_keeps_package_names_outside_the_catalog_and_setup()
    {
        var w = new TestWizard();
        w.Session.LoadProfile(w.Preset("recommended") with
        {
            Apps = new AppSelection
            {
                Preset = "recommended", Keep = ["onedrive", "Clipchamp.Clipchamp", "Fabrikam.Tool"],
                Remove = ["calculator", "Microsoft.GetHelp", "Contoso.Thing"], Install = ["Mozilla.Firefox"]
            },
            Tweaks = new TweakSelection
                { Preset = "recommended", Enable = ["dark-theme"], Disable = ["no-advertising-id"] }
        });
        w.Customize.Setup.ComputerName = "KITCHEN";

        w.Customize.ResetToPresetCommand.Execute(null);

        // A package name of a catalog app is that app's switch, so it goes too.
        Assert.Equal(["Fabrikam.Tool"], w.Profile.Apps.Keep);
        Assert.Equal(["Contoso.Thing"], w.Profile.Apps.Remove);
        Assert.Empty(w.Profile.Tweaks.Enable);
        Assert.Empty(w.Profile.Tweaks.Disable);
        Assert.Equal(["Mozilla.Firefox"], w.Profile.Apps.Install);
        Assert.Equal("KITCHEN", w.Profile.Setup.ComputerName);
        Assert.Equal(0, w.Session.Editor.ChangedCount);
        Assert.Contains(Strings.Customize_ResetDone, w.Customize.Messages);
    }

    [Fact]
    public void Every_switch_agrees_with_what_the_planner_builds()
    {
        var w = new TestWizard();
        var editor = w.Session.Editor;
        var random = new Random(7);
        // Flip switches the way a user would, then compare with the engine's own reading of the profile.
        foreach (var entry in w.Catalog.AllEntries.Where(e => e.BoundTo is null).OrderBy(_ => random.Next()).Take(40))
            editor.Toggle(entry, !editor.IsOn(entry));
        var plan = BuildPlanner.Create(w.Profile, w.Catalog, Image);
        var planned = plan.Entries.Select(e => e.Id).ToHashSet();
        foreach (var entry in w.Catalog.AllEntries.Where(e => e.BoundTo is null && Applies(e)))
            Assert.True(editor.IsOn(entry) == planned.Contains(entry.Id),
                $"{entry.Id}: switch says {editor.IsOn(entry)}");

        return;

        static bool Applies(CatalogEntry e)
        {
            return (e.MinBuild ?? 0) <= Image.Build && (e.MaxBuild ?? int.MaxValue) >= Image.Build;
        }
    }

    [Fact]
    public void Package_names_in_keep_and_remove_show_as_the_switch_the_planner_reads()
    {
        var w = new TestWizard();
        var editor = w.Session.Editor;
        var getHelp = w.Catalog.FindAppByPackage("Microsoft.GetHelp")!;
        w.Session.LoadProfile(w.Preset("recommended") with
        {
            Apps = new AppSelection
            {
                Preset = "recommended", Keep = ["Clipchamp.Clipchamp", "MSTeams"], Remove = ["Microsoft.GetHelp"]
            }
        });

        Assert.False(editor.IsOn(w.Entry("clipchamp")));
        Assert.True(editor.IsOn(getHelp));
        // Teams removes two packages. With only one of them kept, the other one is still removed.
        Assert.True(editor.IsOn(w.Entry("teams")));
        var planned = BuildPlanner.Create(w.Profile, w.Catalog, Image).Entries.Select(e => e.Id).ToHashSet();
        foreach (var entry in w.Catalog.Apps.Where(e =>
                     e.BoundTo is null && (e.MinBuild ?? 0) <= Image.Build &&
                     (e.MaxBuild ?? int.MaxValue) >= Image.Build))
            Assert.True(editor.IsOn(entry) == planned.Contains(entry.Id),
                $"{entry.Id}: switch says {editor.IsOn(entry)}");

        // A switch flipped by the user replaces the package name.
        editor.Set(w.Entry("clipchamp"), true);
        editor.Set(getHelp, false);
        Assert.True(editor.IsOn(w.Entry("clipchamp")));
        Assert.False(editor.IsOn(getHelp));
        Assert.Equal(["MSTeams"], w.Profile.Apps.Keep);
        Assert.Empty(w.Profile.Apps.Remove);
    }

    [Fact]
    public void Removing_an_app_that_needs_a_tweak_switches_the_tweak_on_and_says_so()
    {
        var w = new TestWizard();
        var editor = w.Session.Editor;
        editor.Set(w.Entry("teams"), false);
        editor.Set(w.Entry("no-teams-autoinstall"), false);

        var result = editor.Toggle(w.Entry("teams"), true);

        Assert.True(result.Applied);
        Assert.True(editor.IsOn(w.Entry("no-teams-autoinstall")));
        var message = Assert.Single(result.Messages);
        Assert.Contains("Microsoft Teams", message);
        Assert.Contains("No automatic Teams", message);
        BuildPlanner.Create(w.Profile, w.Catalog, Image); // and the engine accepts it
    }

    [Fact]
    public void Switching_off_a_requirement_keeps_the_app_that_needs_it_and_says_so()
    {
        var w = new TestWizard();
        var result = w.Session.Editor.Toggle(w.Entry("no-teams-autoinstall"), false);

        Assert.True(result.Applied);
        Assert.False(w.Session.Editor.IsOn(w.Entry("teams")));
        Assert.Contains(result.Messages, m => m.Contains("Microsoft Teams") && m.Contains("keeps"));
        BuildPlanner.Create(w.Profile, w.Catalog, Image);
    }

    [Fact]
    public void A_tweak_that_needs_another_one_brings_it_along()
    {
        var w = new TestWizard();
        var result = w.Session.Editor.Toggle(w.Entry("hide-home"), true);

        Assert.True(w.Session.Editor.IsOn(w.Entry("explorer-opens-this-pc")));
        Assert.Contains(result.Messages, m => m.Contains("File Explorer opens 'This PC'"));
    }

    [Fact]
    public void A_conflict_is_refused_and_explained_never_resolved()
    {
        var w = new TestWizard(catalog: TestCatalogs.WithConflict());
        var editor = w.Session.Editor;
        Assert.True(editor.IsOn(w.Entry("light-start")));

        var result = editor.Toggle(w.Entry("dark-start"), true);

        Assert.False(result.Applied);
        Assert.False(editor.IsOn(w.Entry("dark-start")));
        Assert.True(editor.IsOn(w.Entry("light-start")));
        Assert.Contains("Dark start", Assert.Single(result.Messages));
        Assert.Contains("Light start", result.Messages[0]);
        Assert.Empty(w.Profile.Tweaks.Enable);
        Assert.Empty(w.Profile.Tweaks.Disable);
    }

    [Fact]
    public void A_requirement_that_conflicts_refuses_the_whole_change()
    {
        var w = new TestWizard(catalog: TestCatalogs.WithConflict());
        var editor = w.Session.Editor;

        var refused = editor.Toggle(w.Entry("dark-icons"), true);
        Assert.False(refused.Applied);
        Assert.False(editor.IsOn(w.Entry("dark-icons")));
        Assert.False(editor.IsOn(w.Entry("dark-start")));

        editor.Toggle(w.Entry("light-start"), false);
        var accepted = editor.Toggle(w.Entry("dark-icons"), true);
        Assert.True(accepted.Applied);
        Assert.True(editor.IsOn(w.Entry("dark-start")));
        Assert.Single(accepted.Messages);
    }

    [Fact]
    public void A_refused_switch_jumps_back_in_the_list()
    {
        var w = new TestWizard(catalog: TestCatalogs.WithConflict());
        var entry = w.Customize.Tweaks.Entries.First(e => e.Id == "dark-start");
        var raised = new List<string?>();
        entry.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        entry.IsOn = true;

        Assert.False(entry.IsOn);
        Assert.Contains(nameof(EntryViewModel.IsOn), raised);
        Assert.Single(w.Customize.Messages);
    }

    [Fact]
    public void Entries_bound_to_a_setup_option_are_not_switches()
    {
        var w = new TestWizard();
        var all = w.Customize.Apps.Entries.Concat(w.Customize.Tweaks.Entries).Select(e => e.Id).ToList();
        Assert.DoesNotContain("setup-local-account", all);
        Assert.DoesNotContain("setup-hardware-bypass", all);
        Assert.DoesNotContain("setup-skip-privacy-pages", all);
        Assert.Contains("onedrive", all);
    }

    [Fact]
    public void A_loaded_profile_never_keeps_a_password_in_memory()
    {
        var w = new TestWizard();
        var withPassword = w.Preset("recommended") with
        {
            Setup = w.Preset("recommended").Setup with
            {
                LocalAccount = new LocalAccountOptions { Name = "Anna", AskPassword = true, Password = "secret" }
            }
        };
        w.Session.LoadProfile(withPassword);
        Assert.Null(w.Profile.Setup.LocalAccount!.Password);
    }
}