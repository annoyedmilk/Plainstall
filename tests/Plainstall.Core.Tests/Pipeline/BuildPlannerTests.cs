using Plainstall.Core.Catalog;
using Plainstall.Core.Pipeline;
using Plainstall.Core.Profiles;
using Plainstall.Core.Unattend;

namespace Plainstall.Core.Tests.Pipeline;

public class BuildPlannerTests
{
    private static readonly Core.Catalog.Catalog Catalog = CatalogLoader.LoadEmbedded();
    private static readonly ImageFacts Win26H2 = new(26300, "Professional");
    private static readonly ImageFacts Win22H2 = new(22621, "Professional");

    private static Profile Preset(string id)
    {
        return ProfileFactory.FromPreset(Catalog.FindPreset(id)!);
    }

    private static Profile None()
    {
        return Preset("recommended") with
        {
            Apps = new AppSelection { Preset = "none" },
            Tweaks = new TweakSelection { Preset = "none" },
            Setup = new SetupOptions { LocalAccount = null, SkipPrivacyPages = false, BypassHardwareChecks = false }
        };
    }

    private static IEnumerable<string> Packages(BuildPlan plan)
    {
        return plan.Actions.Select(a => a.Action).OfType<AppxRemoveProvisionedAction>().Select(a => a.Package);
    }

    [Theory]
    [InlineData("recommended")]
    [InlineData("privacy-max")]
    [InlineData("gaming")]
    [InlineData("minimal")]
    public void Every_preset_builds_a_plan_for_current_and_old_media(string preset)
    {
        foreach (var image in new[] { Win26H2, Win22H2 })
        {
            var plan = BuildPlanner.Create(Preset(preset), Catalog, image);
            Assert.NotEmpty(plan.Actions);
            // The Store, winget and Terminal survive every preset.
            Assert.DoesNotContain(Packages(plan), Catalog.IsProtectedPackage);
        }
    }

    [Fact]
    public void An_empty_selection_gives_an_empty_plan()
    {
        var plan = BuildPlanner.Create(None(), Catalog, Win26H2);
        Assert.Empty(plan.Actions);
        Assert.Empty(plan.Entries);
        Assert.False(plan.TouchesSetupImage);
        Assert.Empty(plan.WingetInstalls);
    }

    [Fact]
    public void Recommended_removes_promo_apps_and_keeps_everyday_tools_and_edge()
    {
        var plan = BuildPlanner.Create(Preset("recommended"), Catalog, Win26H2);
        var packages = Packages(plan).ToList();
        Assert.Contains("Clipchamp.Clipchamp", packages);
        Assert.Contains("Microsoft.OutlookForWindows", packages);
        // Phone Link and Mobile devices stay: many home users connect their phone. Privacy first removes them.
        foreach (var kept in new[]
                 {
                     "Microsoft.WindowsCalculator", "Microsoft.Windows.Photos", "Microsoft.Paint", "Microsoft.GetHelp",
                     "MicrosoftCorporationII.QuickAssist", "Microsoft.GamingApp", "Microsoft.XboxGamingOverlay",
                     "Microsoft.YourPhone", "MicrosoftWindows.CrossDevice"
                 }) Assert.DoesNotContain(kept, packages);
        var privacy = Packages(BuildPlanner.Create(Preset("privacy-max"), Catalog, Win26H2)).ToList();
        Assert.Contains("Microsoft.YourPhone", privacy);
        Assert.Contains("MicrosoftWindows.CrossDevice", privacy);
        Assert.DoesNotContain(plan.Entries, e => e.Id == "edge");
        Assert.DoesNotContain(plan.Actions.Select(a => a.Action).OfType<FileDeleteAction>(),
            a => a.Path.Contains("Edge", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Setup_options_switch_their_bound_entries()
    {
        var on = BuildPlanner.Create(None() with
        {
            Setup = new SetupOptions
            {
                LocalAccount = new LocalAccountOptions { Name = null }, SkipPrivacyPages = true,
                BypassHardwareChecks = true
            }
        }, Catalog, Win26H2);
        Assert.Equal(["setup-hardware-bypass", "setup-local-account", "setup-skip-privacy-pages"],
            on.Entries.Select(e => e.Id).Order());
        Assert.True(on.TouchesSetupImage);
        Assert.Contains(on.Actions.Select(a => a.Action).OfType<RegistrySetAction>(),
            a => a is { Hive: RegistryHiveId.SetupSystem, Name: "BypassTPMCheck" });
        Assert.Contains(on.Actions.Select(a => a.Action).OfType<RegistrySetAction>(), a => a.Name == "BypassNRO");

        var off = BuildPlanner.Create(None(), Catalog, Win26H2);
        Assert.False(off.TouchesSetupImage);
    }

    [Fact]
    public void A_bound_entry_cannot_be_switched_on_as_a_tweak()
    {
        var profile = None() with
        {
            Tweaks = new TweakSelection { Preset = "none", Enable = ["setup-hardware-bypass"] }
        };
        var ex = Assert.Throws<PlainstallException>(() => BuildPlanner.Create(profile, Catalog, Win26H2));
        Assert.Contains("Setup option", ex.Message);
    }

    [Fact]
    public void Enable_and_disable_adjust_the_preset()
    {
        var profile = Preset("recommended");
        profile = profile with
        {
            Tweaks = profile.Tweaks with { Enable = ["dark-theme"], Disable = ["show-file-extensions"] }
        };
        var plan = BuildPlanner.Create(profile, Catalog, Win26H2);
        Assert.Contains(plan.Entries, e => e.Id == "dark-theme");
        Assert.DoesNotContain(plan.Entries, e => e.Id == "show-file-extensions");
        Assert.Contains(plan.Actions, a => a.Action is FirstLogonRegistryAction);
    }

    [Fact]
    public void Keep_accepts_a_catalog_id_or_a_package_name()
    {
        var byId = Preset("recommended");
        byId = byId with { Apps = byId.Apps with { Keep = ["outlook"] } };
        Assert.DoesNotContain("Microsoft.OutlookForWindows", Packages(BuildPlanner.Create(byId, Catalog, Win26H2)));

        // Keeping one package of an entry that removes several leaves the others.
        var byPackage = Preset("recommended");
        byPackage = byPackage with { Apps = byPackage.Apps with { Keep = ["MSTeams"] } };
        var plan = BuildPlanner.Create(byPackage, Catalog, Win26H2);
        Assert.DoesNotContain("MSTeams", Packages(plan));
        Assert.Contains("MicrosoftTeams", Packages(plan));
        Assert.Contains(plan.Notes, n => n.Message.Contains("MSTeams is kept"));
        Assert.Contains(plan.Entries, e => e.Id == "teams");
    }

    [Fact]
    public void An_app_whose_every_package_is_kept_by_name_is_not_in_the_plan()
    {
        var single = Preset("recommended");
        single = single with { Apps = single.Apps with { Keep = ["Clipchamp.Clipchamp"] } };
        var plan = BuildPlanner.Create(single, Catalog, Win26H2);
        Assert.DoesNotContain("Clipchamp.Clipchamp", Packages(plan));
        Assert.DoesNotContain(plan.Entries, e => e.Id == "clipchamp");
        Assert.Contains(plan.Notes, n => n.Message.Contains("Clipchamp.Clipchamp is kept"));

        var both = Preset("recommended");
        both = both with { Apps = both.Apps with { Keep = ["MSTeams", "microsoftteams"] } };
        Assert.DoesNotContain(BuildPlanner.Create(both, Catalog, Win26H2).Entries, e => e.Id == "teams");
    }

    [Fact]
    public void Remove_by_package_name_puts_the_catalog_entry_in_the_plan()
    {
        var profile = Preset("recommended");
        profile = profile with { Apps = profile.Apps with { Remove = ["Microsoft.GetHelp"] } };
        var plan = BuildPlanner.Create(profile, Catalog, Win26H2);
        Assert.Contains("Microsoft.GetHelp", Packages(plan));
        Assert.Contains(plan.Entries, e => e.Id == Catalog.FindAppByPackage("Microsoft.GetHelp")!.Id);
    }

    [Fact]
    public void Remove_accepts_a_catalog_id_a_known_package_or_an_unknown_package()
    {
        var profile = None() with
        {
            Apps = new AppSelection
                { Preset = "none", Remove = ["paint", "Microsoft.WindowsCamera", "Contoso.OemTool"] }
        };
        var plan = BuildPlanner.Create(profile, Catalog, Win26H2);
        Assert.Equal(["Contoso.OemTool", "Microsoft.Paint", "Microsoft.WindowsCamera"], Packages(plan).Order());
        Assert.Contains(plan.Actions, a => a.EntryId == BuildPlanner.AdHocEntryPrefix + "Contoso.OemTool");
        Assert.Contains(plan.Notes, n => n.Message.Contains("Contoso.OemTool is not in the catalog"));
    }

    [Theory]
    [InlineData("Microsoft.WindowsStore")]
    [InlineData("Microsoft.DesktopAppInstaller")]
    [InlineData("Microsoft.WindowsTerminal")]
    [InlineData("Microsoft.VCLibs.140.00")]
    public void Protected_packages_cannot_be_removed_even_by_hand(string package)
    {
        var profile = None() with { Apps = new AppSelection { Preset = "none", Remove = [package] } };
        var ex = Assert.Throws<PlainstallException>(() => BuildPlanner.Create(profile, Catalog, Win26H2));
        Assert.Equal(ErrorKind.InvalidInput, ex.Kind);
        Assert.Contains("protected", ex.Message);
    }

    [Fact]
    public void Entries_and_actions_outside_the_image_build_are_left_out_with_a_note()
    {
        var plan26 = BuildPlanner.Create(Preset("recommended"), Catalog, Win26H2);
        Assert.DoesNotContain(plan26.Entries, e => e.Id == "hide-chat-icon");
        Assert.Contains(plan26.Notes, n => n.EntryId == "hide-chat-icon");
        Assert.Contains(plan26.Entries, e => e.Id == "taskbar-end-task");
        // The Teams auto-install value that only exists up to 23H2 is dropped as a single action.
        Assert.DoesNotContain(plan26.Actions.Select(a => a.Action).OfType<RegistrySetAction>(),
            a => a.Name == "ConfigureChatAutoInstall");

        var plan22 = BuildPlanner.Create(Preset("recommended"), Catalog, Win22H2);
        Assert.Contains(plan22.Entries, e => e.Id == "hide-chat-icon");
        Assert.DoesNotContain(plan22.Entries, e => e.Id == "taskbar-end-task");
        Assert.Contains(plan22.Actions.Select(a => a.Action).OfType<RegistrySetAction>(),
            a => a.Name == "ConfigureChatAutoInstall");
    }

    [Fact]
    public void A_missing_requirement_is_an_error_not_a_silent_fix()
    {
        // 'outlook' requires the auto-install block. Switch that off.
        var profile = Preset("recommended");
        profile = profile with { Tweaks = profile.Tweaks with { Disable = ["no-outlook-devhome-autoinstall"] } };
        var ex = Assert.Throws<PlainstallException>(() => BuildPlanner.Create(profile, Catalog, Win26H2));
        Assert.Contains("no-outlook-devhome-autoinstall", ex.Message);
        Assert.Contains("Outlook", ex.Message);
    }

    [Fact]
    public void Unknown_ids_from_another_catalog_version_are_ignored_with_a_note()
    {
        var profile = Preset("minimal");
        profile = profile with
        {
            Tweaks = profile.Tweaks with { Enable = ["tweak-from-the-future"], Disable = ["tweak-from-the-past"] }
        };
        var plan = BuildPlanner.Create(profile, Catalog, Win26H2);
        Assert.Contains(plan.Notes, n => n.EntryId == "tweak-from-the-future");
        Assert.Contains(plan.Notes, n => n.EntryId == "tweak-from-the-past");
    }

    [Fact]
    public void An_app_id_under_tweaks_and_a_tweak_id_under_apps_are_errors()
    {
        var a = None() with { Tweaks = new TweakSelection { Preset = "none", Enable = ["clipchamp"] } };
        Assert.Throws<PlainstallException>(() => BuildPlanner.Create(a, Catalog, Win26H2));
        var b = None() with { Apps = new AppSelection { Preset = "none", Remove = ["dark-theme"] } };
        Assert.Throws<PlainstallException>(() => BuildPlanner.Create(b, Catalog, Win26H2));
    }

    [Fact]
    public void Winget_installs_are_carried_into_the_plan()
    {
        var profile = None() with
        {
            Apps = new AppSelection { Preset = "none", Install = ["Mozilla.Firefox", "7zip.7zip"] }
        };
        var plan = BuildPlanner.Create(profile, Catalog, Win26H2);
        Assert.Equal(["Mozilla.Firefox", "7zip.7zip"], plan.WingetInstalls);
    }

    /// <summary>
    ///     Machine.ps1 and its FirstLogonCommands entry in the answer file come only from the gaming preset's power
    ///     plan. The other presets keep the answer file without a first sign-in command.
    /// </summary>
    [Theory]
    [InlineData("recommended", false)]
    [InlineData("privacy-max", false)]
    [InlineData("gaming", true)]
    [InlineData("minimal", false)]
    public void Only_the_gaming_preset_runs_the_elevated_first_sign_in_script(string preset, bool runs)
    {
        var machine = FirstLogonScriptGenerator.GenerateMachineScript(
            BuildPlanner.Create(Preset(preset), Catalog, Win26H2));

        Assert.Equal(runs, machine is not null);
        if (machine is not null)
            Assert.Contains(
                "\r\nInvoke-PlainstallCommand 'High performance power plan: Switch to the High performance power plan' ",
                machine.Text);
    }

    [Fact]
    public void The_plan_is_deterministic()
    {
        var first = BuildPlanner.Create(Preset("privacy-max"), Catalog, Win26H2);
        var second = BuildPlanner.Create(Preset("privacy-max"), Catalog, Win26H2);
        Assert.Equal(first.Actions.Select(a => (a.EntryId, a.Action.KindName, a.Action.Target)),
            second.Actions.Select(a => (a.EntryId, a.Action.KindName, a.Action.Target)));
    }
}