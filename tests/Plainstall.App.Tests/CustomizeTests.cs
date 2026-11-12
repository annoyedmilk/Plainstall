using System.Globalization;
using Plainstall.App.Resources;
using Plainstall.App.ViewModels;
using Plainstall.Core.Catalog;
using Plainstall.Core.Profiles;

namespace Plainstall.App.Tests;

public class CatalogListTests
{
    [Fact]
    public void Every_entry_is_listed_once_under_its_category()
    {
        var w = new TestWizard();
        var apps = w.Customize.Apps;
        Assert.Equal(w.Catalog.Apps.Count(e => e.BoundTo is null), apps.Entries.Count());
        Assert.Equal(apps.Entries.Count(), apps.VisibleCount);
        Assert.All(apps.Groups, g => Assert.All(g.Visible, e => Assert.Equal(g.Title, e.CategoryTitle)));
    }

    [Fact]
    public void Only_the_tweaks_tab_explains_the_managed_by_your_organization_message()
    {
        var w = new TestWizard();
        Assert.True(w.Customize.Tweaks.ShowPolicyNotice);
        Assert.False(w.Customize.Apps.ShowPolicyNotice);
        Assert.Contains("managed by your organization", Strings.Customize_TweaksPolicyNotice);
    }

    [Fact]
    public void Search_matches_title_description_and_details()
    {
        var w = new TestWizard();
        var tweaks = w.Customize.Tweaks;
        tweaks.SearchText = "taskbar";
        var visible = tweaks.Groups.SelectMany(g => g.Visible).ToList();
        Assert.NotEmpty(visible);
        Assert.All(visible, e => Assert.True(
            (e.Title + e.Description + e.Details + e.Id + e.CategoryTitle).Contains("taskbar",
                StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(visible.Count, tweaks.VisibleCount);
        Assert.All(tweaks.Groups.Where(g => g.Visible.Count == 0), g => Assert.False(g.IsVisible));

        tweaks.SearchText = "zzz-nothing";
        Assert.True(tweaks.HasNoMatches);
        tweaks.SearchText = "";
        Assert.Equal(tweaks.Entries.Count(), tweaks.VisibleCount);
    }

    [Theory]
    [InlineData(RiskFilter.Safe, RiskLevel.Safe)]
    [InlineData(RiskFilter.Moderate, RiskLevel.Moderate)]
    [InlineData(RiskFilter.Advanced, RiskLevel.Advanced)]
    public void Risk_filter_shows_one_level(RiskFilter filter, RiskLevel level)
    {
        var w = new TestWizard();
        var apps = w.Customize.Apps;
        apps.SelectedRiskFilter = apps.RiskFilters.First(f => f.Value == filter);
        var visible = apps.Groups.SelectMany(g => g.Visible).ToList();
        Assert.NotEmpty(visible);
        Assert.All(visible, e => Assert.Equal(level, e.Risk));
    }

    [Fact]
    public void Changed_filter_shows_only_switches_that_differ_from_the_preset()
    {
        var w = new TestWizard();
        var apps = w.Customize.Apps;
        apps.Entries.First(e => e.Id == "calculator").IsOn = true;
        apps.Entries.First(e => e.Id == "onedrive").IsOn = false;
        apps.ChangedOnly = true;

        Assert.Equal(["calculator", "onedrive"], apps.Groups.SelectMany(g => g.Visible).Select(e => e.Id).Order());
        Assert.All(apps.Groups.SelectMany(g => g.Visible), e => Assert.True(e.IsChanged));
    }

    [Fact]
    public void Risk_badge_always_says_the_word()
    {
        var w = new TestWizard();
        var texts = w.Customize.Apps.Entries.Concat(w.Customize.Tweaks.Entries).Select(e => e.RiskText).Distinct()
            .Order().ToList();
        Assert.Equal(new[] { Strings.Risk_Advanced, Strings.Risk_Moderate, Strings.Risk_Safe }.Order(), texts);
    }

    [Fact]
    public void Details_show_build_range_source_and_how_to_get_an_app_back()
    {
        var w = new TestWizard();
        var endTask = w.Customize.Tweaks.Entries.First(e => e.Id == "taskbar-end-task");
        Assert.Contains("22631", endTask.BuildRangeText);
        Assert.Contains("Win11Debloat", endTask.SourceText);
        var onedrive = w.Customize.Apps.Entries.First(e => e.Id == "onedrive");
        Assert.NotNull(onedrive.ReinstallText);
        Assert.Equal(Strings.Entry_AllBuilds, onedrive.BuildRangeText);
    }

    [Fact]
    public void Switch_names_tell_what_on_means()
    {
        var w = new TestWizard();
        var app = w.Customize.Apps.Entries.First(e => e.Id == "onedrive");
        Assert.Equal(Strings.Switch_Remove, app.OnText);
        Assert.Contains("OneDrive", app.SwitchName);
        var tweak = w.Customize.Tweaks.Entries.First(e => e.Id == "dark-theme");
        Assert.Equal(Strings.Switch_On, tweak.OnText);
    }
}

public class SectionTests
{
    private static string Summary(string format, CatalogListViewModel list)
    {
        return string.Format(CultureInfo.CurrentCulture, format, list.Entries.Count(e => e.IsOn),
            list.Entries.Count());
    }

    [Fact]
    public void The_four_sections_are_listed_in_order_with_a_summary_each()
    {
        var w = new TestWizard();
        var sections = w.Customize.Sections;
        Assert.Equal([CustomizeTab.Apps, CustomizeTab.Tweaks, CustomizeTab.Setup, CustomizeTab.Advanced],
            sections.Select(s => s.Tab));
        Assert.All(sections, s => Assert.False(string.IsNullOrWhiteSpace(s.Summary)));
        Assert.Equal(Summary(Strings.Customize_SectionAppsSummary, w.Customize.Apps), w.Customize.AppsSection.Summary);
        Assert.Equal(Summary(Strings.Customize_SectionTweaksSummary, w.Customize.Tweaks),
            w.Customize.TweaksSection.Summary);
        Assert.Contains(w.Customize.AppsSection.Summary, w.Customize.AppsSection.AutomationName);
    }

    [Fact]
    public void A_switch_updates_the_count_on_its_section_card()
    {
        var w = new TestWizard();
        var before = w.Customize.AppsSection.Summary;
        w.Customize.Apps.Entries.First(e => e.Id == "calculator").IsOn = true;

        Assert.NotEqual(before, w.Customize.AppsSection.Summary);
        Assert.Equal(Summary(Strings.Customize_SectionAppsSummary, w.Customize.Apps), w.Customize.AppsSection.Summary);
    }
}

public class SetupTests
{
    [Fact]
    public void Starts_from_the_preset()
    {
        var w = new TestWizard();
        var setup = w.Customize.Setup;
        Assert.True(setup.LocalAccount);
        Assert.Equal("", setup.AccountName);
        Assert.True(setup.SkipPrivacyPages);
        Assert.True(setup.BypassHardwareChecks);
        Assert.False(setup.HasErrors);
    }

    [Fact]
    public void Choices_are_written_to_the_profile()
    {
        var w = new TestWizard();
        var setup = w.Customize.Setup;
        setup.AccountName = "Anna";
        setup.AskPassword = true;
        setup.ComputerName = "KITCHEN-PC";
        setup.RegionMode = setup.RegionModes.First(m => m.Mode == ChoiceMode.Value);
        setup.RegionValue = setup.Regions.First(r => r.Tag == "de-CH");
        setup.KeyboardMode = setup.KeyboardModes.First(m => m.Mode == ChoiceMode.Ask);
        setup.TimeZoneMode = setup.TimeZoneModes.First(m => m.Mode == ChoiceMode.Value);
        setup.TimeZoneValue = setup.TimeZones.First(z => z.Id == "W. Europe Standard Time");
        setup.SkipPrivacyPages = false;

        var s = w.Profile.Setup;
        Assert.Equal("Anna", s.LocalAccount!.Name);
        Assert.True(s.LocalAccount.AskPassword);
        Assert.Null(s.LocalAccount.Password);
        Assert.Equal("KITCHEN-PC", s.ComputerName);
        Assert.Equal("de-CH", s.Region);
        Assert.Equal("ask", s.Keyboard);
        Assert.Equal("W. Europe Standard Time", s.Timezone);
        Assert.False(s.SkipPrivacyPages);
        Assert.False(setup.HasErrors);
    }

    [Fact]
    public void Empty_fields_mean_setup_decides()
    {
        var w = new TestWizard();
        w.Customize.Setup.AccountName = "   ";
        w.Customize.Setup.ComputerName = "";
        Assert.Null(w.Profile.Setup.LocalAccount!.Name);
        Assert.Null(w.Profile.Setup.ComputerName);
    }

    [Theory]
    [InlineData("bad/name")]
    [InlineData("a-name-that-is-far-too-long")]
    [InlineData("ends.")]
    public void An_invalid_account_name_is_explained(string name)
    {
        var w = new TestWizard();
        w.Customize.Setup.AccountName = name;
        Assert.Equal(Strings.Setup_AccountNameInvalid, w.Customize.Setup.AccountNameError);
        Assert.False(w.Customize.CanContinue);
        Assert.Equal(Strings.Customize_BlockedSetupErrors, w.Customize.ContinueBlockedReason);
    }

    [Fact]
    public void An_account_name_that_windows_setup_refuses_is_explained()
    {
        var w = new TestWizard();
        w.Customize.Setup.AccountName = "Administrator";
        Assert.Equal(Strings.Setup_AccountNameReserved, w.Customize.Setup.AccountNameError);
        Assert.False(w.Customize.CanContinue);

        w.Customize.Setup.AccountName = "Anna";
        w.Customize.Setup.ComputerName = "anna";
        Assert.Equal(Strings.Setup_AccountNameSameAsComputer, w.Customize.Setup.AccountNameError);
        Assert.False(w.Customize.CanContinue);

        w.Customize.Setup.ComputerName = "Anna-PC";
        Assert.Null(w.Customize.Setup.AccountNameError);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("has space")]
    [InlineData("SIXTEEN-CHARS-AB")]
    public void An_invalid_computer_name_is_explained(string name)
    {
        var w = new TestWizard();
        w.Customize.Setup.ComputerName = name;
        Assert.Equal(Strings.Setup_ComputerNameInvalid, w.Customize.Setup.ComputerNameError);
        Assert.True(w.Customize.Setup.HasErrors);
    }

    [Fact]
    public void A_password_needs_a_named_account()
    {
        var w = new TestWizard();
        var setup = w.Customize.Setup;
        Assert.False(setup.CanAskPassword);
        setup.AccountName = "Anna";
        Assert.True(setup.CanAskPassword);
        setup.AskPassword = true;
        setup.AccountName = "";
        Assert.Equal(Strings.Setup_AskPasswordNeedsName, setup.AskPasswordError);
        setup.AccountName = "Ben";
        Assert.Null(setup.AskPasswordError);
    }

    [Fact]
    public void A_ticked_password_box_can_be_unticked_after_the_name_is_cleared()
    {
        var w = new TestWizard();
        var setup = w.Customize.Setup;
        var changed = new List<string?>();
        setup.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        setup.AccountName = "Anna";
        setup.AskPassword = true;

        changed.Clear();
        setup.AccountName = "";
        Assert.Equal(Strings.Setup_AskPasswordNeedsName, setup.AskPasswordError);
        Assert.False(setup.CanAskPassword);
        Assert.True(setup.CanChangeAskPassword);
        Assert.Contains(nameof(SetupViewModel.CanChangeAskPassword), changed);

        changed.Clear();
        setup.AskPassword = false;
        Assert.Null(setup.AskPasswordError);
        Assert.False(setup.CanChangeAskPassword);
        Assert.Contains(nameof(SetupViewModel.CanChangeAskPassword), changed);
    }

    [Fact]
    public void Turning_the_local_account_off_uses_the_microsoft_pages_and_keeps_the_typed_name()
    {
        var w = new TestWizard();
        var setup = w.Customize.Setup;
        setup.AccountName = "Anna";
        setup.LocalAccount = false;
        Assert.Null(w.Profile.Setup.LocalAccount);
        setup.LocalAccount = true;
        Assert.Equal("Anna", w.Profile.Setup.LocalAccount!.Name);
    }
}

public class AdvancedTests
{
    [Fact]
    public void Picks_and_typed_ids_become_installs()
    {
        var w = new TestWizard();
        var advanced = w.Customize.Advanced;
        advanced.InstallGroups.SelectMany(g => g.Items).First(i => i.WingetId == "VideoLAN.VLC").IsSelected = true;
        advanced.NewWingetId = " Contoso.Notes ";
        advanced.AddWingetIdCommand.Execute(null);

        Assert.Equal(["VideoLAN.VLC", "Contoso.Notes"], w.Profile.Apps.Install);
        Assert.Equal(["Contoso.Notes"], advanced.CustomInstalls);
        Assert.Equal("", advanced.NewWingetId);

        advanced.RemoveCustomInstallCommand.Execute("Contoso.Notes");
        Assert.Equal(["VideoLAN.VLC"], w.Profile.Apps.Install);
    }

    [Theory]
    [InlineData("9WZDNCRFJ3TJ", "9WZDNCRFJ3TJ")]
    [InlineData(" xp89dcgq3k6vld ", "XP89DCGQ3K6VLD")]
    public void A_store_id_is_accepted_and_written_in_capitals(string typed, string stored)
    {
        var w = new TestWizard();
        w.Customize.Advanced.NewWingetId = typed;
        w.Customize.Advanced.AddWingetIdCommand.Execute(null);

        Assert.Null(w.Customize.Advanced.NewWingetIdError);
        Assert.Equal([stored], w.Profile.Apps.Install);
        Assert.Equal([stored], w.Customize.Advanced.CustomInstalls);
    }

    [Theory]
    [InlineData("firefox")]
    [InlineData("Mozilla Firefox")]
    [InlineData(".Firefox")]
    [InlineData("9NKSQGP7F2N")]
    [InlineData("9NKSQGP7F2NH1")]
    public void An_invalid_winget_id_is_refused_with_a_reason(string id)
    {
        var w = new TestWizard();
        w.Customize.Advanced.NewWingetId = id;
        w.Customize.Advanced.AddWingetIdCommand.Execute(null);
        Assert.Equal(Strings.Advanced_WingetIdInvalid, w.Customize.Advanced.NewWingetIdError);
        Assert.Empty(w.Profile.Apps.Install);
    }

    [Fact]
    public void A_duplicate_id_is_refused()
    {
        var w = new TestWizard();
        w.Customize.Advanced.InstallGroups.SelectMany(g => g.Items).First(i => i.WingetId == "Mozilla.Firefox")
            .IsSelected = true;
        w.Customize.Advanced.NewWingetId = "mozilla.firefox";
        w.Customize.Advanced.AddWingetIdCommand.Execute(null);
        Assert.Equal(Strings.Advanced_WingetIdDuplicate, w.Customize.Advanced.NewWingetIdError);
    }

    [Fact]
    public void Removing_edge_without_a_browser_suggests_one_until_a_browser_is_picked()
    {
        var w = new TestWizard();
        Assert.False(w.Customize.Advanced.ShowBrowserSuggestion);
        w.Customize.Apps.Entries.First(e => e.Id == "edge").IsOn = true;
        Assert.True(w.Customize.Advanced.ShowBrowserSuggestion);
        w.Customize.Advanced.InstallGroups.SelectMany(g => g.Items).First(i => i.WingetId == "Brave.Brave").IsSelected =
            true;
        Assert.False(w.Customize.Advanced.ShowBrowserSuggestion);
    }

    [Fact]
    public void Drivers_updates_and_cleanup_are_kept_in_the_profile_in_order()
    {
        var w = new TestWizard();
        var advanced = w.Customize.Advanced;
        w.Dialogs.UpdateFiles = [@"D:\u\one.msu", @"D:\u\two.cab", @"D:\u\readme.txt"];
        advanced.AddUpdatesCommand.Execute(null);
        advanced.MoveUpdateUpCommand.Execute(@"D:\u\two.cab");
        advanced.AddDriverFolderCommand.Execute(null);
        advanced.DriversFromThisPc = true;
        advanced.ResetBase = true;

        Assert.Equal([@"D:\u\two.cab", @"D:\u\one.msu"], w.Profile.Updates);
        Assert.Equal([w.Dialogs.Folder!], w.Profile.Drivers.Folders);
        Assert.True(w.Profile.Drivers.FromThisPc);
        Assert.True(w.Profile.Cleanup.ResetBase);
        Assert.Empty(ProfileSerializer.Validate(w.Profile));
    }
}