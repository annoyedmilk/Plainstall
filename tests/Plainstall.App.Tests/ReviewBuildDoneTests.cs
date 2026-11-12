using System.Globalization;
using Plainstall.App.Rendering;
using Plainstall.App.Resources;
using Plainstall.App.Services;
using Plainstall.App.ViewModels;
using Plainstall.Core.Output.Usb;
using Plainstall.Core.Pipeline;
using Plainstall.Core.Profiles;
using Plainstall.Core.Unattend;

namespace Plainstall.App.Tests;

public class ReviewTests
{
    private static async Task<TestWizard> ReviewOf(Func<TestWizard, Profile>? profile = null, bool elevated = true)
    {
        var w = new TestWizard(elevated);
        if (profile is not null) w.Session.LoadProfile(profile(w));
        await w.Review.OnShownAsync();
        return w;
    }

    [Fact]
    public async Task The_summary_of_recommended_names_what_goes_and_what_stays()
    {
        var w = await ReviewOf();
        Assert.Contains("Clipchamp", w.Review.RemovedApps);
        Assert.Contains("OneDrive", w.Review.RemovedApps);
        Assert.DoesNotContain("Calculator", w.Review.RemovedApps);
        Assert.DoesNotContain("Microsoft Edge", w.Review.RemovedApps);
        Assert.Contains("No advertising ID", w.Review.Changes);
        // Setup options are described in their own section, not as changes.
        Assert.DoesNotContain(w.Review.Changes, c => c.StartsWith("Local account", StringComparison.Ordinal));
        Assert.Contains(Strings.Review_AccountLocalAsk, w.Review.SetupLines);
        Assert.Contains(Strings.Review_PrivacySkipped, w.Review.SetupLines);
        Assert.Contains(Strings.Review_BuildAssumed, w.Review.Notes);
        Assert.False(w.Review.HasPlanErrors);
        Assert.Contains("Windows 11 Pro", w.Review.WindowsText);
    }

    [Fact]
    public async Task Notes_about_entries_left_out_for_this_windows_build_are_shown()
    {
        var w = new TestWizard { Windows = { UseOwnIso = true } };
        var media = SampleMedia.Create();
        w.Session.IsoPath = @"C:\old.iso";
        w.Session.Media = media with
        {
            InstallImage = media.InstallImage with
            {
                Images = [.. media.Editions.Select(e => e with { Version = new Version(10, 0, 22621, 1) })]
            }
        };
        w.Session.Editor.Update(p => p with { Tweaks = p.Tweaks with { Enable = ["no-sticky-keys-prompt"] } });
        await w.Review.OnShownAsync();
        Assert.Contains(w.Review.Notes, n => n.Contains("No Sticky Keys prompt") && n.Contains("26100"));
    }

    [Fact]
    public async Task A_protected_package_is_an_error_in_plain_words_with_a_way_back()
    {
        var w = await ReviewOf(t => t.Preset("recommended") with
        {
            Apps = new AppSelection { Preset = "recommended", Remove = ["Microsoft.WindowsStore"] }
        });

        var error = Assert.Single(w.Review.PlanErrors);
        Assert.StartsWith("'Microsoft.WindowsStore' is protected", error);
        Assert.False(w.Review.CanContinue);
        Assert.Equal(Strings.Review_BlockedPlanErrors, w.Review.ContinueBlockedReason);

        w.Review.FixProblemsCommand.Execute(null);
        Assert.Equal(WizardStep.Customize, w.Main.CurrentStep);
    }

    [Fact]
    public async Task A_stick_must_be_confirmed_by_name_before_the_build_can_start()
    {
        var w = await ReviewOf();
        Assert.True(w.Review.WantsUsb);
        Assert.Equal(4, w.Review.UsbCandidates.Count);
        Assert.Equal(Strings.Review_BlockedNoStick, w.Review.ContinueBlockedReason);

        var ineligible = w.Review.UsbCandidates.Single(c => !c.IsEligible);
        Assert.Contains("larger than 256 GB", ineligible.ReasonText);
        // The size in the title and the size in the reason use the same (decimal) units.
        Assert.Equal("4.0 TB", ineligible.SizeText);
        Assert.Contains("holds 4.0 TB", ineligible.ReasonText);
        w.Review.SelectedUsb = ineligible;
        Assert.Null(w.Review.SelectedUsb);

        w.Review.SelectedUsb = w.Review.UsbCandidates.First(c => c.Title == "SanDisk Ultra");
        Assert.Equal("32.0 GB", w.Review.SelectedUsb!.SizeText);
        Assert.Contains("SanDisk Ultra", w.Review.EraseConfirmText);
        Assert.Contains("E: HOLIDAY", w.Review.EraseConfirmText);
        Assert.Equal(Strings.Review_BlockedNotConfirmed, w.Review.ContinueBlockedReason);

        w.Review.EraseConfirmed = true;
        Assert.True(w.Review.CanContinue);

        // Another stick needs its own confirmation.
        w.Review.SelectedUsb = w.Review.UsbCandidates.First(c => c.Title == "Kingston DataTraveler");
        Assert.False(w.Review.EraseConfirmed);
        Assert.False(w.Review.CanContinue);
    }

    [Fact]
    public async Task A_stick_that_windows_calls_an_external_drive_needs_one_more_confirmation()
    {
        var w = await ReviewOf();
        var external = w.Review.UsbCandidates.Single(c => c.Title == "General USB Flash Drive");
        Assert.True(external.IsEligible);
        Assert.Equal(Strings.Review_UsbExternalNote, external.ExternalNoteText);

        w.Review.SelectedUsb = external;
        Assert.True(w.Review.NeedsExternalConfirmation);
        w.Review.EraseConfirmed = true;
        Assert.Equal(Strings.Review_BlockedExternalNotConfirmed, w.Review.ContinueBlockedReason);

        w.Review.ExternalDriveConfirmed = true;
        Assert.Null(w.Review.ContinueBlockedReason);
        var start = w.Review.CreateStart(null);
        Assert.Same(external.Disk, start.Request.UsbDisk);
        Assert.True(start.Request.UsbExternalDriveConfirmed);

        // A normal stick needs neither the extra confirmation nor carries it into the request.
        w.Review.SelectedUsb = w.Review.UsbCandidates.First(c => c.Title == "SanDisk Ultra");
        Assert.False(w.Review.NeedsExternalConfirmation);
        Assert.False(w.Review.ExternalDriveConfirmed);
        w.Review.EraseConfirmed = true;
        Assert.False(w.Review.CreateStart(null).Request.UsbExternalDriveConfirmed);
    }

    /// <summary>With administrator rights the confirmed stick goes into the request. Without them the build cannot start.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_confirmed_stick_goes_into_the_request_when_elevated(bool elevated)
    {
        var w = await ReviewOf(elevated: elevated);
        w.Review.SelectedUsb = w.Review.UsbCandidates.First(c => c.IsEligible);
        w.Review.EraseConfirmed = true;
        if (!elevated)
        {
            Assert.False(w.Review.CanContinue);
            Assert.Equal(Strings.Review_BlockedNotElevated, w.Review.ContinueBlockedReason);
            Assert.True(w.Main.ShowElevationBanner);
            return;
        }

        var start = w.Review.CreateStart(null);
        Assert.Same(w.Review.SelectedUsb!.Disk, start.Request.UsbDisk);
        Assert.Null(start.Request.IsoOutputPath);
    }

    [Fact]
    public async Task Too_little_space_blocks_the_build_and_says_why()
    {
        var w = new TestWizard { Preflight = { LowSpace = true } };
        await w.Review.OnShownAsync();
        Assert.True(w.Review.HasSpaceProblems);
        Assert.Contains("Not enough free space", Assert.Single(w.Review.SpaceProblems));
    }

    [Fact]
    public async Task The_space_check_counts_the_update_files_like_the_build_does()
    {
        using var temp = new TempFolder();
        var update = Path.Combine(temp.Path, "windows11.0-kb5070001-x64.msu");
        await File.WriteAllBytesAsync(update, new byte[4096], TestContext.Current.CancellationToken);
        var missing = Path.Combine(temp.Path, "not-there.msu");

        var w = await ReviewOf(t => t.Preset("recommended") with { Updates = [update, missing] });

        Assert.Equal(4096, w.Session.UpdateBytes);
        Assert.Equal(Preflight.RequiredBytes(w.Session.MediaBytes, 4096), w.Preflight.CheckedBytes);
        Assert.True(w.Preflight.CheckedBytes > Preflight.RequiredBytes(w.Session.MediaBytes));
    }

    [Fact]
    public async Task A_download_to_the_drive_of_the_work_folder_needs_room_for_the_iso_too()
    {
        using var temp = new TempFolder();
        var w = new TestWizard
        {
            Session = { WorkFolder = Path.Combine(temp.Path, "work"), CacheFolder = Path.Combine(temp.Path, "cache") }
        };

        await w.Review.OnShownAsync();
        Assert.Equal(Preflight.RequiredBytes(w.Session.MediaBytes) + WizardSession.AssumedMediaBytes,
            w.Preflight.CheckedBytes);

        // The build usually uses an ISO that is in the cache already, and downloads nothing.
        Directory.CreateDirectory(w.Session.CacheFolder);
        await File.WriteAllBytesAsync(Path.Combine(w.Session.CacheFolder, "Win11_English_x64.iso"), [0],
            TestContext.Current.CancellationToken);
        await w.Review.OnShownAsync();
        Assert.Equal(Preflight.RequiredBytes(w.Session.MediaBytes), w.Preflight.CheckedBytes);
    }

    [Fact]
    public async Task An_iso_file_that_exists_now_blocks_the_build_until_it_is_chosen_again()
    {
        using var temp = new TempFolder();
        var iso = Path.Combine(temp.Path, "Plainstall.iso");
        var w = await ReviewOf();
        w.Review.WantsUsb = false;
        w.Dialogs.IsoToSave = new SaveFileChoice(iso, false);
        w.Review.ChooseIsoOutputCommand.Execute(null);
        Assert.True(w.Review.CanContinue);

        // The build wrote the file. The engine refuses the same name again without "replace".
        await File.WriteAllBytesAsync(iso, [0], TestContext.Current.CancellationToken);
        await w.Review.OnShownAsync();
        Assert.False(w.Review.CanContinue);
        Assert.Equal(Strings.Review_BlockedIsoExists, w.Review.ContinueBlockedReason);

        w.Dialogs.IsoToSave = new SaveFileChoice(iso, true);
        w.Review.ChooseIsoOutputCommand.Execute(null);
        Assert.True(w.Review.CanContinue);
        Assert.True(w.Review.CreateStart(null).Request.OverwriteOutput);
    }

    [Fact]
    public async Task The_build_cannot_start_while_the_stick_list_loads()
    {
        var w = new TestWizard();
        var usb = new GatedUsbDisks();
        var review = new ReviewViewModel(w.Session, w.Preflight, usb, w.Dialogs, w.Host, w.Navigator);

        var shown = review.OnShownAsync();
        Assert.True(review.IsLoadingUsb);
        Assert.Equal(Strings.Review_BlockedLoadingUsb, review.ContinueBlockedReason);
        usb.Release();
        await shown;
        review.SelectedUsb = review.UsbCandidates.First(c => c.Title == "SanDisk Ultra");
        review.EraseConfirmed = true;
        Assert.True(review.CanContinue);

        // While the list loads again, the selection can change at any moment.
        var reload = review.RefreshUsbCommand.ExecuteAsync(null);
        Assert.False(review.CanContinue);
        Assert.Equal(Strings.Review_BlockedLoadingUsb, review.ContinueBlockedReason);
        usb.Release();
        await reload;
        Assert.Equal(Strings.Review_BlockedNotConfirmed, review.ContinueBlockedReason);
    }

    [Fact]
    public async Task An_iso_only_build_needs_a_file_name_and_passes_it_to_the_engine()
    {
        var w = await ReviewOf();
        w.Review.WantsUsb = false;
        w.Review.WantsIso = true;
        Assert.Equal(Strings.Review_BlockedNoIsoPath, w.Review.ContinueBlockedReason);
        w.Dialogs.IsoToSave = new SaveFileChoice(@"D:\out\mine.iso", true);
        w.Review.ChooseIsoOutputCommand.Execute(null);
        Assert.True(w.Review.CanContinue);

        var start = w.Review.CreateStart(null);
        Assert.Equal(@"D:\out\mine.iso", start.Request.IsoOutputPath);
        Assert.True(start.Request.OverwriteOutput);
        Assert.Null(start.Request.UsbDisk);
        Assert.Equal(@"D:\Plainstall-work", start.Request.WorkDirectory);
        Assert.Null(start.SourceIsoPath);
        Assert.False(w.Profile.Output.Usb);
        Assert.True(w.Profile.Output.Iso);
    }

    [Fact]
    public async Task An_iso_that_would_replace_the_users_windows_iso_blocks_the_build()
    {
        var w = new TestWizard
        {
            Windows = { UseOwnIso = true },
            Session = { IsoPath = @"D:\Downloads\Win11.iso", Media = SampleMedia.Create() }
        };
        await w.Review.OnShownAsync();
        w.Review.WantsUsb = false;

        w.Dialogs.IsoToSave = new SaveFileChoice(@"d:\downloads\WIN11.iso", true);
        w.Review.ChooseIsoOutputCommand.Execute(null);
        Assert.False(w.Review.CanContinue);
        Assert.Equal(Strings.Review_BlockedIsoIsSource, w.Review.ContinueBlockedReason);
        // The engine checks it again, with the source the request names.
        Assert.Equal(@"D:\Downloads\Win11.iso", w.Review.CreateStart(null).Request.SourcePath);

        w.Dialogs.IsoToSave = new SaveFileChoice(@"D:\Downloads\Plainstall.iso", false);
        w.Review.ChooseIsoOutputCommand.Execute(null);
        Assert.True(w.Review.CanContinue);
    }

    [Fact]
    public async Task The_stick_list_protects_the_drive_of_the_iso_file_too()
    {
        var w = await ReviewOf();
        Assert.DoesNotContain(@"E:\", w.Usb.LastContext!.PathsInUse);

        w.Dialogs.IsoToSave = new SaveFileChoice(@"E:\Plainstall.iso", false);
        w.Review.ChooseIsoOutputCommand.Execute(null);

        Assert.Contains(@"E:\", w.Usb.LastContext!.PathsInUse, StringComparer.OrdinalIgnoreCase);

        // Without the ISO the drive is free again.
        w.Review.WantsIso = false;
        Assert.DoesNotContain(@"E:\", w.Usb.LastContext!.PathsInUse, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_new_iso_place_keeps_the_confirmation_of_the_same_stick_but_refresh_does_not()
    {
        var w = await ReviewOf();
        w.Review.SelectedUsb = w.Review.UsbCandidates.First(c => c.Title == "SanDisk Ultra");
        w.Review.EraseConfirmed = true;

        w.Dialogs.IsoToSave = new SaveFileChoice(@"D:\Plainstall\Plainstall.iso", false);
        w.Review.ChooseIsoOutputCommand.Execute(null);

        Assert.Equal("SanDisk Ultra", w.Review.SelectedUsb?.Title);
        Assert.True(w.Review.EraseConfirmed);

        await w.Review.RefreshUsbCommand.ExecuteAsync(null);
        Assert.False(w.Review.EraseConfirmed);
    }

    [Fact]
    public void Time_estimate_grows_with_the_work()
    {
        var plain = TimeEstimate.Minutes(false, false, false, true, false);
        var checkedImage = TimeEstimate.Minutes(false, false, true, true, false);
        var more = TimeEstimate.Minutes(true, true, true, true, true);
        Assert.True(plain.Min < plain.Max);
        Assert.True(checkedImage.Min > plain.Min && checkedImage.Max > plain.Max);
        Assert.True(more.Min > checkedImage.Min && more.Max > checkedImage.Max);
    }

    /// <summary>Gives the sample sticks only when the test releases them, so a test can look at the page meanwhile.</summary>
    private sealed class GatedUsbDisks : IUsbDiskService
    {
        private TaskCompletionSource<IReadOnlyList<UsbCandidate>> _gate = new();

        public Task<IReadOnlyList<UsbCandidate>> ListAsync(UsbSelectionContext context,
            CancellationToken cancellationToken = default)
        {
            return _gate.Task;
        }

        public Task<DiskFacts?> GetAsync(int diskNumber, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<DiskFacts?>(null);
        }

        public void Release()
        {
            var gate = _gate;
            _gate = new TaskCompletionSource<IReadOnlyList<UsbCandidate>>();
            gate.SetResult(SampleUsbDisks.Candidates);
        }
    }
}

public class BuildTests
{
    private static async Task<TestWizard> ReadyToBuild(bool askPassword)
    {
        var w = new TestWizard();
        if (askPassword)
        {
            w.Customize.Setup.AccountName = "Anna";
            w.Customize.Setup.AskPassword = true;
        }

        w.Main.GoTo(WizardStep.Review);
        await w.Review.OnShownAsync();
        w.Review.SelectedUsb = w.Review.UsbCandidates.First(c => c.IsEligible);
        w.Review.EraseConfirmed = true;
        return w;
    }

    [Fact]
    public async Task Without_a_password_option_the_build_starts_at_once()
    {
        var w = await ReadyToBuild(false);
        _ = w.Main.NextCommand.ExecuteAsync(null);

        Assert.Equal(WizardStep.Build, w.Main.CurrentStep);
        Assert.False(w.Build.NeedsPassword);
        Assert.Equal(1, w.Runner.Runs);
        Assert.Null(w.Runner.Start!.Request.AccountPassword);
        w.Runner.Finish(SampleOutcomes.Succeeded(w.Profile));
    }

    [Fact]
    public async Task The_password_is_asked_when_needed_and_goes_only_into_the_request()
    {
        const string password = "Correct-Horse-7";
        var w = await ReadyToBuild(true);
        _ = w.Main.NextCommand.ExecuteAsync(null);

        Assert.True(w.Build.NeedsPassword);
        Assert.Equal(0, w.Runner.Runs);

        await w.Build.SubmitPassword(password, "something else");
        Assert.Equal(Strings.Build_PasswordMismatch, w.Build.PasswordError);
        Assert.Equal(0, w.Runner.Runs);
        await w.Build.SubmitPassword("", "");
        Assert.Equal(Strings.Build_PasswordEmpty, w.Build.PasswordError);

        var run = w.Build.SubmitPassword(password, password);
        Assert.False(w.Build.NeedsPassword);
        Assert.Equal(password, w.Runner.Start!.Request.AccountPassword);
        Assert.Null(w.Runner.Start!.Request.Profile.Setup.LocalAccount!.Password);
        Assert.True(w.Runner.Start!.Request.Profile.Setup.LocalAccount!.AskPassword);

        w.Runner.Finish(SampleOutcomes.Succeeded(w.Profile));
        await run;

        // Nothing that was written anywhere contains it: the settings, the last profile, the session's profile.
        Assert.NotEmpty(w.Settings.Written);
        Assert.All(w.Settings.Written, text => Assert.DoesNotContain(password, text));
        Assert.DoesNotContain(password, new ProfileSerializer().ToJson(w.Profile));
        Assert.DoesNotContain(password, new ProfileSerializer().ToJson(w.Settings.LoadLastProfile()!));
        Assert.Equal(WizardStep.Done, w.Main.CurrentStep);
    }

    [Fact]
    public async Task A_password_that_windows_does_not_accept_is_explained_and_does_not_build()
    {
        var w = await ReadyToBuild(true);
        _ = w.Main.NextCommand.ExecuteAsync(null);
        var tooLong = new string('x', UnattendGenerator.MaxPasswordLength + 1);

        // Not awaited: a build that starts would wait for the test.
        _ = w.Build.SubmitPassword(tooLong, tooLong);

        Assert.Equal(0, w.Runner.Runs);
        Assert.True(w.Build.NeedsPassword);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Build_PasswordTooLong,
            UnattendGenerator.MaxPasswordLength), w.Build.PasswordError);
        w.Runner.Finish(SampleOutcomes.Succeeded(w.Profile));
    }

    [Fact]
    public async Task While_a_build_runs_the_window_does_not_close()
    {
        var w = await ReadyToBuild(false);
        Assert.Null(w.Main.CloseBlockedReason);
        var run = w.Main.NextCommand.ExecuteAsync(null);

        Assert.Equal(Strings.App_CloseWhileBuilding, w.Main.CloseBlockedReason);

        w.Build.CancelCommand.Execute(null);
        await run;
        Assert.Null(w.Main.CloseBlockedReason);
    }

    [Fact]
    public async Task While_a_cleanup_runs_the_window_does_not_close()
    {
        var w = new TestWizard { Leftovers = { Leftovers = true } };
        var hold = new TaskCompletionSource();
        w.Leftovers.Hold = hold.Task;

        var cleaning = w.Welcome.CleanUpCommand.ExecuteAsync(null);
        Assert.True(w.Welcome.IsCleaning);
        Assert.Equal(Strings.App_CloseWhileCleaning, w.Main.CloseBlockedReason);

        hold.SetResult();
        await cleaning;
        Assert.False(w.Welcome.IsCleaning);
        Assert.Null(w.Main.CloseBlockedReason);
    }

    [Fact]
    public async Task Leaving_the_password_prompt_does_not_build()
    {
        var w = await ReadyToBuild(true);
        _ = w.Main.NextCommand.ExecuteAsync(null);
        w.Build.CancelPasswordCommand.Execute(null);
        Assert.Equal(0, w.Runner.Runs);
        Assert.Equal(WizardStep.Done, w.Main.CurrentStep);
        Assert.Equal(DoneKind.Cancelled, w.Done.Kind);
    }

    [Fact]
    public async Task Progress_moves_the_highlight_and_shows_action_and_percent()
    {
        var w = await ReadyToBuild(false);
        _ = w.Main.NextCommand.ExecuteAsync(null);
        var stages = BuildPipeline.StageNames;

        w.Runner.Progress!.Report(new BuildProgress(1, stages.Count, stages[0]));
        w.Runner.Progress.Report(new BuildProgress(4, stages.Count, stages[3], "Copying files", 37));
        w.Runner.Log!.Report("a line");

        Assert.Equal(10, w.Build.Stages.Count);
        Assert.All(w.Build.Stages.Take(3), s => Assert.Equal(StageState.Done, s.State));
        Assert.Equal(StageState.Current, w.Build.Stages[3].State);
        Assert.All(w.Build.Stages.Skip(4), s => Assert.Equal(StageState.Pending, s.State));
        Assert.Equal("Copying files", w.Build.CurrentAction);
        Assert.Equal(37, w.Build.Percent);
        Assert.False(w.Build.IsIndeterminate);
        Assert.Contains("4", w.Build.CurrentStageText);
        Assert.Equal(["a line"], w.Build.LogLines);

        w.Runner.Progress.Report(new BuildProgress(5, stages.Count, stages[4]));
        Assert.Null(w.Build.Percent);
        Assert.True(w.Build.IsIndeterminate);
        Assert.Equal(w.Build.Stages[4].Name, w.Build.CurrentAction);
        w.Runner.Finish(SampleOutcomes.Succeeded(w.Profile));
    }

    [Fact]
    public async Task A_failure_marks_the_step_and_the_skipped_ones()
    {
        var w = await ReadyToBuild(false);
        var run = w.Main.NextCommand.ExecuteAsync(null);
        var stages = BuildPipeline.StageNames;
        w.Runner.Progress!.Report(new BuildProgress(5, stages.Count, stages[4]));
        w.Runner.Progress.Report(new BuildProgress(10, stages.Count, stages[9]));
        w.Runner.Finish(SampleOutcomes.Failed(w.Profile));
        await run;

        Assert.Equal(StageState.Failed, w.Build.Stages[4].State);
        Assert.All(w.Build.Stages.Skip(5).Take(4), s => Assert.Equal(StageState.Skipped, s.State));
        Assert.Equal(StageState.Done, w.Build.Stages[9].State);
        Assert.Equal(DoneKind.Failed, w.Done.Kind);
        Assert.Contains("Not enough free space", w.Done.ErrorText);
    }

    [Fact]
    public async Task Cancel_asks_the_engine_to_stop_and_says_it_waits_for_the_current_step()
    {
        var w = await ReadyToBuild(false);
        var run = w.Main.NextCommand.ExecuteAsync(null);
        Assert.True(w.Build.CancelCommand.CanExecute(null));

        w.Build.CancelCommand.Execute(null);

        Assert.True(w.Runner.Token.IsCancellationRequested);
        await run;
        Assert.Equal(DoneKind.Cancelled, w.Done.Kind);
        Assert.Null(w.Done.ErrorText);
        Assert.False(w.Build.IsRunning);
        Assert.Equal(Strings.Build_Stopping, w.Build.StoppingText);
    }

    [Fact]
    public async Task While_building_the_wizard_cannot_be_left()
    {
        var w = await ReadyToBuild(false);
        _ = w.Main.NextCommand.ExecuteAsync(null);
        Assert.False(w.Main.ShowBack);
        Assert.False(w.Main.ShowNext);
        w.Main.GoTo(WizardStep.Welcome);
        Assert.Equal(WizardStep.Build, w.Main.CurrentStep);
        w.Runner.Finish(SampleOutcomes.Succeeded(w.Profile));
    }

    [Fact]
    public async Task The_last_profile_is_kept_when_a_build_starts()
    {
        var w = await ReadyToBuild(false);
        _ = w.Main.NextCommand.ExecuteAsync(null);
        Assert.Equal(w.Profile.Name, w.Settings.LoadLastProfile()!.Name);
        w.Runner.Finish(SampleOutcomes.Succeeded(w.Profile));
    }
}

public class DoneTests
{
    [Fact]
    public void Success_shows_outputs_boot_keys_and_the_checklist()
    {
        var w = new TestWizard();
        w.Done.Show(SampleOutcomes.Succeeded(w.Profile), "SanDisk Ultra");

        Assert.True(w.Done.IsSuccess);
        Assert.Equal(Strings.Done_SucceededHeadline, w.Done.Headline);
        Assert.Equal(Strings.Done_SucceededMessage, w.Done.Message);
        Assert.Contains(w.Done.Outputs, o => o.Contains("SanDisk Ultra"));
        Assert.Contains(w.Done.Outputs, o => o.Contains(".iso"));
        Assert.Contains(w.Done.BootKeys,
            k => k.Brand == "Dell" && k.Text.Contains("F12") && k.Text.StartsWith("usually", StringComparison.Ordinal));
        Assert.Equal(5, w.Done.Checklist.Count);
        Assert.Single(w.Done.Warnings);
        Assert.Null(w.Done.ErrorText);

        w.Done.OpenLogCommand.Execute(null);
        w.Done.OpenReportFolderCommand.Execute(null);
        Assert.Equal([@"C:\ProgramData\Plainstall\logs\plainstall-20261110-140211.log", @"D:\Plainstall"],
            w.Shell.Opened);
    }

    [Fact]
    public void Success_without_a_stick_does_not_talk_about_a_stick()
    {
        var w = new TestWizard();
        w.Done.Show(SampleOutcomes.Succeeded(w.Profile), null);

        Assert.Equal(Strings.Done_SucceededIsoMessage, w.Done.Message);
        Assert.DoesNotContain(w.Done.Outputs, o => o.Contains("USB"));
    }

    [Fact]
    public void A_cancel_says_whether_everything_was_closed()
    {
        var w = new TestWizard();
        w.Done.Show(SampleOutcomes.Cancelled(w.Profile), null);
        Assert.Equal(Strings.Done_CancelledMessage, w.Done.Message);

        w.Done.Show(SampleOutcomes.Cancelled(w.Profile) with { CleanupFinished = false }, null);
        Assert.Equal(Strings.Done_CancelledNotCleanedMessage, w.Done.Message);
    }

    [Fact]
    public void Failure_shows_the_reports_error()
    {
        var w = new TestWizard();
        w.Done.Show(SampleOutcomes.Failed(w.Profile), null);
        Assert.True(w.Done.IsFailure);
        Assert.Equal(Strings.Done_FailedHeadline, w.Done.Headline);
        Assert.Contains("Not enough free space", w.Done.ErrorText);
        Assert.Empty(w.Done.Outputs);
    }

    /// <summary>With an ISO and a stick, the ISO is written and checked first. A stick that fails later does not undo it.</summary>
    [Fact]
    public void A_failed_or_cancelled_stick_still_names_the_iso_that_was_written()
    {
        const string iso = @"D:\Plainstall\Plainstall-Recommended.iso";
        var w = new TestWizard();
        var expected = string.Format(CultureInfo.CurrentCulture, Strings.Done_IsoWritten, iso);

        w.Done.Show(SampleOutcomes.Failed(w.Profile) with { IsoPath = iso }, "SanDisk Ultra");
        Assert.True(w.Done.IsFailed);
        Assert.Equal([expected], w.Done.Outputs);

        w.Done.Show(SampleOutcomes.Cancelled(w.Profile) with { IsoPath = iso }, "SanDisk Ultra");
        Assert.True(w.Done.IsCancelled);
        Assert.Equal([expected], w.Done.Outputs);
    }
}

public class MainTests
{
    [Fact]
    public async Task Next_and_back_walk_the_steps_and_the_indicator_follows()
    {
        var w = new TestWizard();
        Assert.Equal(WizardStep.Welcome, w.Main.CurrentStep);
        Assert.False(w.Main.ShowBack);
        await w.Main.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardStep.Windows, w.Main.CurrentStep);
        Assert.Equal(StepState.Done, w.Main.Steps[0].State);
        Assert.Equal(StepState.Current, w.Main.Steps[1].State);
        Assert.Equal(StepState.Upcoming, w.Main.Steps[2].State);
        Assert.Contains("2", w.Main.StepText);

        await w.Main.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardStep.Customize, w.Main.CurrentStep);
        Assert.Equal(CustomizeTab.Apps, w.Customize.SelectedTab);

        // Next walks through the four sections of Customize before it leaves the page.
        foreach (var tab in new[] { CustomizeTab.Tweaks, CustomizeTab.Setup, CustomizeTab.Advanced })
        {
            await w.Main.NextCommand.ExecuteAsync(null);
            Assert.Equal(WizardStep.Customize, w.Main.CurrentStep);
            Assert.Equal(tab, w.Customize.SelectedTab);
        }

        Assert.Equal(Strings.Nav_Next, w.Main.NextText);
        await w.Main.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardStep.Review, w.Main.CurrentStep);
        Assert.Equal(Strings.Nav_StartBuild, w.Main.NextText);

        w.Main.BackCommand.Execute(null);
        Assert.Equal(WizardStep.Customize, w.Main.CurrentStep);
        Assert.Equal(CustomizeTab.Advanced, w.Customize.SelectedTab);
    }

    [Fact]
    public async Task On_customize_next_names_the_next_section_and_back_returns_to_the_previous_one()
    {
        var w = new TestWizard();
        await w.Main.NextCommand.ExecuteAsync(null);
        await w.Main.NextCommand.ExecuteAsync(null);
        var changes = new List<string?>();
        w.Main.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Nav_NextSection, Strings.Customize_TabTweaks),
            w.Main.NextText);
        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, Strings.Nav_NextSectionName, Strings.Customize_TabTweaks),
            w.Main.NextAutomationName);

        // A click on a section card moves the label along.
        w.Customize.SelectedTab = CustomizeTab.Setup;
        Assert.Contains(nameof(MainViewModel.NextText), changes);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Nav_NextSection, Strings.Customize_TabAdvanced),
            w.Main.NextText);

        w.Main.BackCommand.Execute(null);
        Assert.Equal(CustomizeTab.Tweaks, w.Customize.SelectedTab);
        w.Main.BackCommand.Execute(null);
        Assert.Equal(CustomizeTab.Apps, w.Customize.SelectedTab);
        w.Main.BackCommand.Execute(null);
        Assert.Equal(WizardStep.Windows, w.Main.CurrentStep);
    }

    [Fact]
    public async Task Not_elevated_shows_the_banner_and_disables_start_build()
    {
        var w = new TestWizard(false);
        Assert.True(w.Main.ShowElevationBanner);
        w.Main.GoTo(WizardStep.Review);
        await w.Review.OnShownAsync();
        Assert.False(w.Main.NextCommand.CanExecute(null));
        Assert.Equal(Strings.Review_BlockedNotElevated, w.Main.ContinueBlockedReason);
    }

    [Fact]
    public async Task One_update_check_at_start_shows_a_quiet_notice_with_a_link()
    {
        var w = new TestWizard();
        await w.Main.InitializeAsync();
        Assert.True(w.Main.HasUpdate);
        Assert.Contains("0.2.0", w.Main.UpdateText);
        w.Main.OpenUpdatePageCommand.Execute(null);
        Assert.Single(w.Shell.Opened);
        w.Main.DismissUpdateCommand.Execute(null);
        Assert.False(w.Main.HasUpdate);
    }

    [Fact]
    public async Task The_theme_is_remembered()
    {
        var w = new TestWizard();
        await w.Main.InitializeAsync();
        w.Main.SelectedTheme = w.Main.ThemeOptions.First(t => t.Choice == ThemeChoice.Dark);
        Assert.Equal(ThemeChoice.Dark, w.Theme.Applied[^1]);
        Assert.Equal(ThemeChoice.Dark, w.Settings.Settings.Theme);
    }

    [Fact]
    public async Task The_work_folder_comes_from_the_settings()
    {
        var w = new TestWizard();
        w.Settings.Save(new AppSettings { WorkFolder = @"E:\Work" });
        await w.Main.InitializeAsync();
        Assert.Equal(@"E:\Work", w.Session.WorkFolder);
    }
}