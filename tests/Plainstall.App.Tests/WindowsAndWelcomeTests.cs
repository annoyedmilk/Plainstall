using System.Globalization;
using Plainstall.App.Rendering;
using Plainstall.App.Resources;
using Plainstall.App.ViewModels;
using Plainstall.Core.Download;
using Plainstall.Core.Profiles;

namespace Plainstall.App.Tests;

public class WindowsTests
{
    [Fact]
    public async Task The_language_list_is_loaded_on_demand_with_this_pcs_language_preselected()
    {
        var w = new TestWizard();
        Assert.False(w.Windows.LanguagesLoaded);
        await w.Windows.OnShownAsync();

        Assert.True(w.Windows.LanguagesLoaded);
        Assert.Equal(SampleDownloadClient.Languages.Count, w.Windows.Languages.Count);
        Assert.Equal("English (United States)", w.Windows.SelectedLanguage!.DisplayName);
        Assert.Equal("auto", w.Profile.Windows.Language);
        Assert.Null(w.Windows.LanguageLoadError);
    }

    [Fact]
    public async Task Picking_another_language_stores_a_tag_the_engine_maps_back()
    {
        var w = new TestWizard();
        await w.Windows.OnShownAsync();
        w.Windows.SelectedLanguage = w.Windows.Languages.First(l => l.DisplayName == "German");
        Assert.Equal("de-DE", w.Profile.Windows.Language);
        Assert.Empty(ProfileSerializer.Validate(w.Profile));

        w.Windows.SelectedLanguage = w.Windows.Languages.First(l => l.DisplayName == "English (United States)");
        Assert.Equal("auto", w.Profile.Windows.Language);
    }

    [Fact]
    public void Every_offered_language_has_a_tag_that_maps_back_to_it()
    {
        foreach (var language in SampleDownloadClient.Languages)
        {
            var tag = LanguageTags.TagFor(language, SampleDownloadClient.Languages);
            Assert.NotNull(tag);
            Assert.Equal(language,
                LanguageMatcher.Match(CultureInfo.GetCultureInfo(tag), SampleDownloadClient.Languages).Language);
        }
    }

    [Fact]
    public async Task A_failed_language_list_says_so_and_offers_the_iso_instead()
    {
        var w = new TestWizard { Microsoft = { Fail = true } };
        await w.Windows.OnShownAsync();

        Assert.NotNull(w.Windows.LanguageLoadError);
        Assert.Contains("could not be reached", w.Windows.LanguageLoadError);
        Assert.False(w.Windows.IsLoadingLanguages);
        w.Windows.UseIsoInsteadCommand.Execute(null);
        Assert.True(w.Windows.UseOwnIso);
        Assert.Equal(WindowsSource.Iso, w.Profile.Windows.Source);

        w.Microsoft.Fail = false;
        await w.Windows.LoadLanguagesCommand.ExecuteAsync(null);
        Assert.Null(w.Windows.LanguageLoadError);
    }

    [Fact]
    public async Task Own_iso_is_read_and_professional_is_preselected()
    {
        var w = new TestWizard { Windows = { UseOwnIso = true } };
        Assert.False(w.Windows.CanContinue);
        Assert.Equal(Strings.Windows_BlockedNoIso, w.Windows.ContinueBlockedReason);

        await w.Windows.BrowseIsoCommand.ExecuteAsync(null);

        Assert.True(w.Windows.HasMedia);
        Assert.Equal(8, w.Windows.Editions.Count);
        Assert.Equal("Professional", w.Windows.SelectedEdition!.EditionId);
        Assert.Equal(w.Dialogs.IsoToOpen, w.Session.IsoPath);
        Assert.Equal(w.Dialogs.IsoToOpen, w.Settings.Settings.LastIsoPath);
        Assert.True(w.Windows.CanContinue);

        w.Windows.SelectedEdition = w.Windows.Editions.First(e => e.EditionId == "Core");
        Assert.Equal("Core", w.Profile.Windows.Edition);
    }

    [Fact]
    public async Task An_unusable_iso_shows_the_engines_message()
    {
        var w = new TestWizard
        {
            Media = { FailWith = "This ISO is for ARM PCs. Plainstall makes x64 installers only." },
            Windows = { UseOwnIso = true }
        };
        await w.Windows.BrowseIsoCommand.ExecuteAsync(null);

        Assert.Equal(w.Media.FailWith, w.Windows.IsoError);
        Assert.False(w.Windows.CanContinue);
    }

    [Fact]
    public void Download_offers_the_common_editions_with_pro_preselected()
    {
        var w = new TestWizard();
        Assert.Contains(w.Windows.Editions, e => e.EditionId == "Core");
        Assert.Equal("Professional", w.Windows.SelectedEdition!.EditionId);
    }
}

public class WelcomeTests
{
    [Fact]
    public void Leftovers_show_a_notice_that_cleans_up()
    {
        var w = new TestWizard { Leftovers = { Leftovers = true } };
        w.Welcome.Initialize();
        Assert.True(w.Welcome.HasLeftovers);

        w.Welcome.CleanUpCommand.Execute(null);

        Assert.False(w.Welcome.HasLeftovers);
        Assert.Equal(Strings.Welcome_CleanupDone, w.Welcome.CleanupMessage);
    }

    [Fact]
    public void The_notes_of_a_cleanup_are_shown_with_its_result()
    {
        const string note =
            "The USB stick 'SanDisk Ultra' (disk 2) was erased by an earlier run but not completely written. It is not usable as it is. Write it again.";
        var w = new TestWizard { Leftovers = { Leftovers = true, StoredBytes = 7_412_000_000, Notes = [note] } };
        w.Welcome.Initialize();

        w.Welcome.CleanUpCommand.Execute(null);
        Assert.StartsWith(Strings.Welcome_CleanupDone, w.Welcome.CleanupMessage);
        Assert.Contains(note, w.Welcome.CleanupMessage);

        w.Welcome.DeleteStoredDataCommand.Execute(null);
        Assert.StartsWith("Deleted 7.4 GB of Plainstall files.", w.Welcome.StoredDataMessage);
        Assert.Contains(note, w.Welcome.StoredDataMessage);
    }

    [Fact]
    public void No_leftovers_no_notice()
    {
        var w = new TestWizard();
        w.Welcome.Initialize();
        Assert.False(w.Welcome.HasLeftovers);
        Assert.Null(w.Welcome.StoredDataText);
    }

    [Fact]
    public void Stored_files_show_their_size_and_can_be_deleted()
    {
        var w = new TestWizard { Leftovers = { StoredBytes = 7_412_000_000 } };
        w.Welcome.Initialize();
        Assert.Contains("7.4 GB", w.Welcome.StoredDataText);

        w.Welcome.DeleteStoredDataCommand.Execute(null);

        Assert.Null(w.Welcome.StoredDataText);
        Assert.Equal("Deleted 7.4 GB of Plainstall files.", w.Welcome.StoredDataMessage);
        Assert.False(w.Welcome.StoredDataFailed);
    }

    [Fact]
    public void A_failed_delete_shows_the_problem_as_an_error_and_keeps_the_row()
    {
        var w = new TestWizard { Leftovers = { StoredBytes = 7_412_000_000, DeleteProblem = "A build runs." } };
        w.Welcome.Initialize();

        w.Welcome.DeleteStoredDataCommand.Execute(null);

        Assert.True(w.Welcome.StoredDataFailed);
        Assert.Contains("A build runs.", w.Welcome.StoredDataMessage);
        Assert.NotNull(w.Welcome.StoredDataText);
    }

    [Fact]
    public void The_last_profile_is_offered_and_can_be_used()
    {
        var w = new TestWizard();
        w.Settings.SaveLastProfile(w.Preset("gaming") with { Name = "Gaming PC" });
        w.Welcome.Initialize();

        Assert.True(w.Welcome.HasLastProfile);
        Assert.Contains("Gaming PC", w.Welcome.LastProfileText);
        w.Welcome.UseLastProfileCommand.Execute(null);

        Assert.Equal("Gaming PC", w.Profile.Name);
        Assert.Equal("gaming", Assert.Single(w.Welcome.Presets, p => p.IsSelected).Preset.Id);
        Assert.Contains("Gaming PC", w.Welcome.StartingPointText);
    }

    [Fact]
    public void Opening_a_profile_file_that_is_not_valid_shows_the_reason()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "broken.plainstall.json");
        File.WriteAllText(path, "{ \"schema\": 99 }");
        var w = new TestWizard { Dialogs = { ProfileToOpen = path } };

        w.Welcome.OpenProfileCommand.Execute(null);

        Assert.Contains("newer version", w.Welcome.ErrorMessage);
        Assert.Equal("recommended", w.Profile.Apps.Preset);
    }

    [Fact]
    public void A_profile_saved_and_opened_again_is_the_same()
    {
        using var temp = new TempFolder();
        var w = new TestWizard();
        w.Customize.Apps.Entries.First(e => e.Id == "calculator").IsOn = true;
        w.Customize.Setup.ComputerName = "DESK";
        w.Dialogs.ProfileToSave = Path.Combine(temp.Path, "Grandma.plainstall.json");
        w.Customize.SaveProfileCommand.Execute(null);
        Assert.False(w.Customize.SaveFailed);
        Assert.Equal("Grandma", w.Profile.Name);

        var other = new TestWizard { Dialogs = { ProfileToOpen = w.Dialogs.ProfileToSave } };
        other.Welcome.OpenProfileCommand.Execute(null);
        Assert.Null(other.Welcome.ErrorMessage);
        Assert.Equal(new ProfileSerializer().ToJson(w.Profile), new ProfileSerializer().ToJson(other.Profile));
        Assert.True(other.Session.StartedFromFile);
    }
}

internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "plainstall-app-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
        }
    }
}