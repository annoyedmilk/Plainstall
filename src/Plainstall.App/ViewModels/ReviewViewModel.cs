using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plainstall.App.Resources;
using Plainstall.App.Services;
using Plainstall.Core;
using Plainstall.Core.Catalog;
using Plainstall.Core.Output.Usb;
using Plainstall.Core.Pipeline;
using Plainstall.Core.Profiles;

namespace Plainstall.App.ViewModels;

/// <summary>A disk offered in the USB picker. Disks that may not be erased are shown too, greyed, with the reason.</summary>
public sealed class UsbCandidateViewModel(UsbCandidate candidate)
{
    public UsbCandidate Candidate { get; } = candidate;
    public DiskFacts Disk => Candidate.Disk;
    public bool IsEligible => Candidate.IsEligible;

    /// <summary>True when Windows reports this disk as an external drive. Picking it needs one more confirmation.</summary>
    public bool IsExternalDrive => Candidate.IsExternalDrive;

    public string? ExternalNoteText => IsEligible && IsExternalDrive ? Strings.Review_UsbExternalNote : null;
    public string Title => Disk.FriendlyName;

    /// <summary>Decimal units, as on the stick's package and in the reasons of <see cref="UsbDiskRules" />.</summary>
    public string SizeText => UsbSizes.Format(Disk.SizeBytes);

    public string VolumesText => Disk.Volumes.Count == 0
        ? Strings.Review_UsbNoVolumes
        : string.Join(", ", Disk.Volumes.Select(DescribeVolume));

    public string? ReasonText => IsEligible
        ? null
        : string.Format(CultureInfo.CurrentCulture, Strings.Review_UsbNotOffered, string.Join(" ", Candidate.Reasons));

    public string AutomationName => IsEligible
        ? string.Format(CultureInfo.CurrentCulture, Strings.Review_UsbItemName, Title, SizeText, VolumesText)
        : string.Format(CultureInfo.CurrentCulture, Strings.Review_UsbItemNameNotOffered, Title, SizeText, ReasonText);

    /// <summary>"E: STICK" or "STICK" or "no name", so the user recognises the stick they plugged in.</summary>
    internal static string DescribeVolume(VolumeFacts volume)
    {
        var label = string.IsNullOrWhiteSpace(volume.Label) ? Strings.Review_UsbNoLabel : volume.Label;
        return volume.DriveLetter is { Length: > 0 } letter ? letter + " " + label : label;
    }
}

internal static class Sizes
{
    /// <summary>
    ///     Binary units (1 GB here is 2^30 bytes), as Windows Explorer shows free space. Used for the space in the
    ///     work folder. USB stick sizes use <see cref="UsbSizes.Format" /> instead.
    /// </summary>
    public static string Gigabytes(long bytes)
    {
        return string.Format(CultureInfo.CurrentCulture, Strings.Size_Gigabytes,
            Math.Round(bytes / (double)(1L << 30), 1));
    }
}

/// <summary>A rough time range for a build, from experience with typical PCs. Only shown as "about".</summary>
public static class TimeEstimate
{
    public static (int Min, int Max) Minutes(bool download, bool resetBase, bool usb, bool iso)
    {
        var min = 15 + (download ? 10 : 0) + (resetBase ? 10 : 0) + (usb ? 5 : 0) + (iso ? 2 : 0);
        var max = 35 + (download ? 60 : 0) + (resetBase ? 25 : 0) + (usb ? 15 : 0) + (iso ? 5 : 0);
        return (min, max);
    }
}

public sealed partial class ReviewViewModel : WizardPageViewModel
{
    private readonly IDialogService _dialogs;
    private readonly IHostInfo _host;
    private readonly IWizardNavigator _navigator;
    private readonly IPreflightService _preflight;

    private readonly WizardSession _session;
    private readonly IUsbDiskService _usb;
    private bool _loading;

    public ReviewViewModel(WizardSession session, IPreflightService preflight, IUsbDiskService usb,
        IDialogService dialogs, IHostInfo host, IWizardNavigator navigator)
    {
        _session = session;
        _preflight = preflight;
        _usb = usb;
        _dialogs = dialogs;
        _host = host;
        _navigator = navigator;
    }

    public override WizardStep Step => WizardStep.Review;

    // ---- summary -------------------------------------------------------------------------------

    [ObservableProperty] public partial string WindowsText { get; private set; } = "";

    public ObservableCollection<string> RemovedApps { get; } = [];
    public ObservableCollection<string> Changes { get; } = [];
    public ObservableCollection<string> SetupLines { get; } = [];
    public ObservableCollection<string> Installs { get; } = [];
    public ObservableCollection<string> Notes { get; } = [];
    public ObservableCollection<string> PlanErrors { get; } = [];

    public bool HasPlanErrors => PlanErrors.Count > 0;
    public bool HasPlan => PlanErrors.Count == 0;
    public bool HasInstalls => Installs.Count > 0;
    public bool HasNotes => Notes.Count > 0;

    public string RemovedAppsHeader =>
        string.Format(CultureInfo.CurrentCulture, Strings.Review_RemovedAppsHeader, RemovedApps.Count);

    public string ChangesHeader =>
        string.Format(CultureInfo.CurrentCulture, Strings.Review_ChangesHeader, Changes.Count);

    // ---- space and time ------------------------------------------------------------------------

    [ObservableProperty] public partial string SpaceText { get; private set; } = "";

    public ObservableCollection<string> SpaceProblems { get; } = [];

    public bool HasSpaceProblems => SpaceProblems.Count > 0;

    public string WorkFolder => _session.WorkFolder;

    [ObservableProperty] public partial string TimeText { get; private set; } = "";

    // ---- output --------------------------------------------------------------------------------

    [ObservableProperty] public partial bool WantsUsb { get; set; }

    [ObservableProperty] public partial bool WantsIso { get; set; }

    public ObservableCollection<UsbCandidateViewModel> UsbCandidates { get; } = [];

    public bool HasNoUsbCandidates => !IsLoadingUsb && UsbCandidates.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoUsbCandidates))]
    [NotifyCanExecuteChangedFor(nameof(RefreshUsbCommand))]
    public partial bool IsLoadingUsb { get; private set; }

    [ObservableProperty] public partial string? UsbError { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EraseConfirmText), nameof(HasSelectedUsb), nameof(NeedsExternalConfirmation))]
    public partial UsbCandidateViewModel? SelectedUsb { get; set; }

    public bool HasSelectedUsb => SelectedUsb is not null;

    /// <summary>True when the chosen disk is one Windows reports as an external drive.</summary>
    public bool NeedsExternalConfirmation => SelectedUsb is { IsExternalDrive: true };

    /// <summary>The user's word that the external drive is a USB stick that may be erased.</summary>
    [ObservableProperty]
    public partial bool ExternalDriveConfirmed { get; set; }

    [ObservableProperty] public partial bool EraseConfirmed { get; set; }

    /// <summary>Names the stick, its size and what is on it now, and says that all of it goes.</summary>
    public string? EraseConfirmText => SelectedUsb is { } usb
        ? string.Format(CultureInfo.CurrentCulture, Strings.Review_EraseConfirm, usb.Title, usb.SizeText,
            usb.VolumesText)
        : null;

    [ObservableProperty] public partial string? IsoOutputPath { get; private set; }

    public bool OverwriteIso { get; private set; }

    // ---- page ----------------------------------------------------------------------------------

    public override bool CanContinue => ContinueBlockedReason is null;

    public override string? ContinueBlockedReason
    {
        get
        {
            if (!_host.IsElevated) return Strings.Review_BlockedNotElevated;
            if (HasPlanErrors) return Strings.Review_BlockedPlanErrors;
            if (HasSpaceProblems) return Strings.Review_BlockedSpace;
            if (!WantsUsb && !WantsIso) return Strings.Review_BlockedNoOutput;
            // While the list loads, the selection can change at any moment.
            if (WantsUsb && IsLoadingUsb) return Strings.Review_BlockedLoadingUsb;
            if (WantsUsb && SelectedUsb is null) return Strings.Review_BlockedNoStick;
            if (WantsUsb && NeedsExternalConfirmation && !ExternalDriveConfirmed)
                return Strings.Review_BlockedExternalNotConfirmed;
            if (WantsUsb && !EraseConfirmed) return Strings.Review_BlockedNotConfirmed;
            if (WantsIso && IsoOutputPath is null) return Strings.Review_BlockedNoIsoPath;
            if (WantsIso && IsoOutputPath is { } output && BuildPipeline.OutputReplacesSource(output, SourceIsoPath))
                return Strings.Review_BlockedIsoIsSource;
            // For example the ISO of the last build: the engine replaces a file only when the user agreed to it.
            if (WantsIso && IsoOutputPath is { } existing && !OverwriteIso && File.Exists(existing))
                return Strings.Review_BlockedIsoExists;
            return null;
        }
    }

    /// <summary>The user's own ISO, or null when Plainstall downloads Windows.</summary>
    private string? SourceIsoPath => _session.Profile.Windows.Source == WindowsSource.Iso ? _session.IsoPath : null;

    [GeneratedRegex(@"^[a-z]+\.[A-Za-z.]+: ")]
    private static partial Regex FieldPrefix();

    [RelayCommand]
    private void FixProblems()
    {
        _navigator.GoTo(WizardStep.Customize);
    }

    [RelayCommand]
    private void ChangeWorkFolder()
    {
        var folder = _dialogs.PickFolder(_session.WorkFolder);
        if (folder is null) return;
        // A folder of its own inside the chosen one, because the whole work folder is deleted after a build.
        _session.WorkFolder = Path.Combine(folder, "Plainstall-work");
        WorkFolderChanged?.Invoke(this, _session.WorkFolder);
        OnPropertyChanged(nameof(WorkFolder));
        RefreshSpace();
        NotifyCanContinue();
    }

    /// <summary>Raised so the app can remember the folder in its settings.</summary>
    public event EventHandler<string>? WorkFolderChanged;

    partial void OnWantsUsbChanged(bool value)
    {
        if (_loading) return;
        _session.Editor.Update(p => p with { Output = p.Output with { Usb = value } });
        if (value && UsbCandidates.Count == 0) _ = RefreshUsbAsync();
        AfterOutputChange();
    }

    partial void OnWantsIsoChanged(bool value)
    {
        if (_loading) return;
        _session.Editor.Update(p => p with { Output = p.Output with { Iso = value } });
        RecheckUsb();
        AfterOutputChange();
    }

    partial void OnSelectedUsbChanged(UsbCandidateViewModel? value)
    {
        if (value is { IsEligible: false })
        {
            // Greyed entries cannot be picked. A selection made with the keyboard is turned back.
            SelectedUsb = null;
            return;
        }

        ExternalDriveConfirmed = false;
        EraseConfirmed = false;
        NotifyCanContinue();
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EraseConfirmed) or nameof(ExternalDriveConfirmed)) NotifyCanContinue();
        base.OnPropertyChanged(e);
    }

    [RelayCommand(CanExecute = nameof(CanRefreshUsb))]
    private Task RefreshUsbAsync()
    {
        return LoadUsbAsync();
    }

    private bool CanRefreshUsb()
    {
        return !IsLoadingUsb;
    }

    [RelayCommand]
    private void ChooseIsoOutput()
    {
        var choice = _dialogs.PickIsoSaveLocation(SuggestedIsoName());
        if (choice is null) return;
        IsoOutputPath = choice.Path;
        OverwriteIso = choice.ReplaceExisting;
        // Switching the ISO on checks the sticks again. Otherwise the new path is checked here.
        if (!WantsIso) WantsIso = true;
        else RecheckUsb();
        NotifyCanContinue();
    }

    /// <summary>The ISO file is a place in use too: a stick that holds it cannot be offered.</summary>
    private void RecheckUsb()
    {
        if (WantsUsb && UsbCandidates.Count > 0) _ = LoadUsbAsync(true);
    }

    /// <param name="keepConfirmation">
    ///     True when only the places in use changed. The confirmations then stay, but only for exactly the same
    ///     stick. Refresh gives false, because the user can have put in a different stick.
    /// </param>
    private async Task LoadUsbAsync(bool keepConfirmation = false)
    {
        IsLoadingUsb = true;
        NotifyCanContinue();
        UsbError = null;
        var previous = SelectedUsb?.Disk.UniqueId ?? SelectedUsb?.Disk.SerialNumber;
        var confirmedDisk = SelectedUsb?.Disk;
        var (eraseConfirmed, externalConfirmed) = (EraseConfirmed, ExternalDriveConfirmed);
        try
        {
            var context = new UsbSelectionContext
            {
                // Drive root and volume name of each place, so a stick mounted in a folder is recognised too.
                PathsInUse =
                [
                    .. new[]
                        {
                            _session.WorkFolder, _session.IsoPath, WantsIso ? IsoOutputPath : null,
                            _session.CacheFolder
                        }
                        .Where(p => !string.IsNullOrWhiteSpace(p)).SelectMany(p => UsbPaths.RootsOf(p!))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                ],
                RequiredBytes = _session.MediaBytes,
                // Offered, but only with the extra confirmation below the list.
                AllowExternalDrives = true
            };
            var list = await _usb.ListAsync(context, CancellationToken.None);
            UsbCandidates.Clear();
            foreach (var candidate in list.OrderByDescending(c => c.IsEligible).ThenBy(c => c.Disk.Number))
                UsbCandidates.Add(new UsbCandidateViewModel(candidate));
            SelectedUsb = UsbCandidates.FirstOrDefault(c =>
                c.IsEligible && previous is not null && (c.Disk.UniqueId ?? c.Disk.SerialNumber) == previous);
            if (keepConfirmation && SelectedUsb is { } same && confirmedDisk is not null &&
                UsbDiskRules.Differences(confirmedDisk, same.Disk).Count == 0)
            {
                EraseConfirmed = eraseConfirmed;
                ExternalDriveConfirmed = externalConfirmed;
            }
        }
        catch (PlainstallException ex)
        {
            UsbError = ex.Message;
        }
        finally
        {
            IsLoadingUsb = false;
            NotifyCanContinue();
        }
    }

    private void AfterOutputChange()
    {
        RefreshTime();
        NotifyCanContinue();
    }

    public override async Task OnShownAsync()
    {
        _loading = true;
        WantsUsb = _session.Profile.Output.Usb;
        WantsIso = _session.Profile.Output.Iso;
        _loading = false;
        BuildSummary();
        RefreshSpace();
        RefreshTime();
        NotifyCanContinue();
        if (WantsUsb) await LoadUsbAsync();
    }

    /// <summary>The engine request for this review. The password exists only here and in the request.</summary>
    public BuildStart CreateStart(string? password)
    {
        return new BuildStart(
            new BuildRequest
            {
                Profile = _session.Profile,
                WorkDirectory = _session.WorkFolder,
                SourcePath = SourceIsoPath,
                IsoOutputPath = WantsIso ? IsoOutputPath : null,
                OverwriteOutput = WantsIso && OverwriteIso,
                UsbDisk = WantsUsb ? SelectedUsb?.Disk : null,
                UsbExternalDriveConfirmed = WantsUsb && NeedsExternalConfirmation && ExternalDriveConfirmed,
                AccountPassword = password,
                AppVersion = _host.AppVersion.ToString(3)
            },
            SourceIsoPath);
    }

    internal void BuildSummary()
    {
        var profile = _session.Profile;
        WindowsText = DescribeWindows();
        RemovedApps.Clear();
        Changes.Clear();
        SetupLines.Clear();
        Installs.Clear();
        Notes.Clear();
        PlanErrors.Clear();

        try
        {
            var plan = BuildPlanner.Create(profile, _session.Catalog, _session.CurrentImageFacts());
            foreach (var entry in plan.Entries.Where(e => e.BoundTo is null))
                (entry.Kind == CatalogKind.Apps ? RemovedApps : Changes).Add(entry.Title);
            foreach (var adHoc in plan.Actions.Where(a =>
                         a.EntryId.StartsWith(BuildPlanner.AdHocEntryPrefix, StringComparison.Ordinal)))
                RemovedApps.Add(adHoc.EntryTitle);
            foreach (var id in plan.WingetInstalls) Installs.Add(_session.Installs.Find(id)?.Title ?? id);
            foreach (var note in plan.Notes) Notes.Add(note.Message);
        }
        catch (PlainstallException ex)
        {
            foreach (var line in ex.Message.Split(Environment.NewLine).Skip(1))
                // The engine names the profile field first ("apps.remove: ..."). People do not need it.
                PlanErrors.Add(FieldPrefix().Replace(line.TrimStart(' ', '-'), ""));
            if (PlanErrors.Count == 0) PlanErrors.Add(ex.Message);
        }

        if (_session.ImageBuildIsAssumed) Notes.Add(Strings.Review_BuildAssumed);

        foreach (var line in DescribeSetup(profile.Setup)) SetupLines.Add(line);

        OnPropertyChanged(nameof(HasPlanErrors));
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(HasInstalls));
        OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(RemovedAppsHeader));
        OnPropertyChanged(nameof(ChangesHeader));
    }

    private string DescribeWindows()
    {
        var profile = _session.Profile;
        if (profile.Windows.Source == WindowsSource.Iso)
        {
            var edition = _session.Media?.Editions.FirstOrDefault(e =>
                string.Equals(e.EditionId, profile.Windows.Edition, StringComparison.OrdinalIgnoreCase));
            return string.Format(CultureInfo.CurrentCulture, Strings.Review_WindowsFromIso,
                edition?.Name ?? profile.Windows.Edition, Path.GetFileName(_session.IsoPath ?? ""),
                edition?.Version?.ToString() ?? _session.Media?.Version.ToString() ?? "");
        }

        var editionTitle =
            WindowsViewModel.DownloadEditions.FirstOrDefault(e => e.EditionId == profile.Windows.Edition)?.Title ??
            profile.Windows.Edition;
        var language = _session.DownloadLanguage?.DisplayName
                       ?? (profile.Windows.Language == "auto"
                           ? Strings.Review_LanguageOfThisPc
                           : WindowsViewModel.DescribeLanguage(profile.Windows.Language));
        return string.Format(CultureInfo.CurrentCulture, Strings.Review_WindowsDownload, editionTitle, language);
    }

    internal static IEnumerable<string> DescribeSetup(SetupOptions setup)
    {
        yield return setup.LocalAccount switch
        {
            null => Strings.Review_AccountMicrosoft,
            { Name: null } => Strings.Review_AccountLocalAsk,
            { Name: { } name, AskPassword: true } => string.Format(CultureInfo.CurrentCulture,
                Strings.Review_AccountLocalNamedPassword, name),
            { Name: { } name } => string.Format(CultureInfo.CurrentCulture, Strings.Review_AccountLocalNamed, name)
        };
        yield return setup.SkipPrivacyPages ? Strings.Review_PrivacySkipped : Strings.Review_PrivacyAsked;
        yield return setup.BypassHardwareChecks ? Strings.Review_HardwareBypassed : Strings.Review_HardwareChecked;
        if (setup.ComputerName is { } computer)
            yield return string.Format(CultureInfo.CurrentCulture, Strings.Review_ComputerName, computer);
        yield return string.Format(CultureInfo.CurrentCulture, Strings.Review_Region, DescribeChoice(setup.Region));
        yield return string.Format(CultureInfo.CurrentCulture, Strings.Review_Keyboard, DescribeChoice(setup.Keyboard));
        yield return string.Format(CultureInfo.CurrentCulture, Strings.Review_TimeZone, setup.Timezone switch
        {
            "auto" => Strings.Setup_SameAsThisPc,
            "ask" => Strings.Setup_TimeZoneFromRegion,
            var id => id
        });
        yield break;

        static string DescribeChoice(string value)
        {
            return value switch
            {
                "auto" => Strings.Setup_SameAsThisPc,
                "ask" => Strings.Setup_AskDuringSetup,
                _ => WindowsViewModel.DescribeLanguage(value)
            };
        }
    }

    private void RefreshSpace()
    {
        // The same rules as the build: updates need room too, because servicing expands them, and so does a download
        // to the drive of the work folder.
        var download = _session.Profile.Windows.Source == WindowsSource.Download;
        var check = _preflight.Check(_session.WorkFolder,
            Preflight.RequiredBytes(_session.MediaBytes, _session.UpdateBytes) +
            Preflight.DownloadBytes(_session.WorkFolder, download ? _session.CacheFolder : null));
        SpaceText = check.FreeBytes is { } free
            ? string.Format(CultureInfo.CurrentCulture, Strings.Review_SpaceText, Sizes.Gigabytes(check.RequiredBytes),
                Sizes.Gigabytes(free))
            : string.Format(CultureInfo.CurrentCulture, Strings.Review_SpaceUnknown,
                Sizes.Gigabytes(check.RequiredBytes));
        SpaceProblems.Clear();
        foreach (var problem in check.Problems) SpaceProblems.Add(problem);
        OnPropertyChanged(nameof(HasSpaceProblems));
    }

    private void RefreshTime()
    {
        var (min, max) = TimeEstimate.Minutes(_session.Profile.Windows.Source == WindowsSource.Download,
            _session.Profile.Cleanup.ResetBase, WantsUsb, WantsIso);
        TimeText = string.Format(CultureInfo.CurrentCulture, Strings.Review_TimeText, min, max);
    }

    private string SuggestedIsoName()
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(_session.Profile.Name.Select(c => invalid.Contains(c) || c == ' ' ? '-' : c).ToArray());
        return "Plainstall-" + name + ".iso";
    }
}