using System.Collections.ObjectModel;
using System.Globalization;
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

public sealed partial class PresetCardViewModel : ObservableObject
{
    private readonly Action<PresetCardViewModel> _chosen;

    public PresetCardViewModel(Preset preset, Action<PresetCardViewModel> chosen)
    {
        Preset = preset;
        _chosen = chosen;
    }

    public Preset Preset { get; }
    public string Title => Preset.Title;
    public string Tagline => Preset.Tagline;
    public string Description => Preset.Description;

    [ObservableProperty] public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) _chosen(this);
    }
}

public sealed partial class WelcomeViewModel : WizardPageViewModel
{
    private readonly ProfileFiles _files;
    private readonly ILeftoverService _leftovers;
    private readonly WizardSession _session;
    private readonly ISettingsStore _settings;
    private bool _syncing;

    public WelcomeViewModel(WizardSession session, ProfileFiles files, ISettingsStore settings,
        ILeftoverService leftovers)
    {
        _session = session;
        _files = files;
        _settings = settings;
        _leftovers = leftovers;
        Presets = [.. session.Catalog.Presets.Select(p => new PresetCardViewModel(p, OnPresetChosen))];
        SyncCards();
        session.ProfileReplaced += (_, _) => SyncCards();
    }

    public override WizardStep Step => WizardStep.Welcome;

    public ObservableCollection<PresetCardViewModel> Presets { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastProfile), nameof(LastProfileText))]
    public partial Profile? LastProfile { get; private set; }

    public bool HasLastProfile => LastProfile is not null;

    public string? LastProfileText => LastProfile is null
        ? null
        : string.Format(CultureInfo.CurrentCulture, Strings.Welcome_LastProfileText, LastProfile.Name);

    [ObservableProperty] public partial bool HasLeftovers { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CleanUpCommand), nameof(DeleteStoredDataCommand))]
    public partial bool IsCleaning { get; private set; }

    /// <summary>How much Plainstall keeps on this PC, or null when it keeps nothing.</summary>
    [ObservableProperty]
    public partial string? StoredDataText { get; private set; }

    /// <summary>The result of the last deletion of the stored files, in plain words.</summary>
    [ObservableProperty]
    public partial string? StoredDataMessage { get; private set; }

    /// <summary>True when <see cref="StoredDataMessage" /> reports a problem. The page shows it as an error.</summary>
    [ObservableProperty]
    public partial bool StoredDataFailed { get; private set; }

    /// <summary>The result of the last cleanup, in plain words.</summary>
    [ObservableProperty]
    public partial string? CleanupMessage { get; private set; }

    /// <summary>True when <see cref="CleanupMessage" /> reports a problem. The page shows it as an error.</summary>
    [ObservableProperty]
    public partial bool CleanupFailed { get; private set; }

    /// <summary>What happened with the last action on this page: a profile opened, a preset chosen.</summary>
    [ObservableProperty]
    public partial string? StatusMessage { get; private set; }

    [ObservableProperty] public partial string? ErrorMessage { get; private set; }

    /// <summary>Text under the cards that says what the wizard starts from.</summary>
    public string StartingPointText => _session.StartedFromFile
        ? string.Format(CultureInfo.CurrentCulture, Strings.Welcome_StartingFromProfile, _session.Profile.Name)
        : string.Format(CultureInfo.CurrentCulture, Strings.Welcome_StartingFromPreset,
            PresetTitle(_session.Profile.Apps.Preset));

    /// <summary>Reads the last profile, looks for leftovers and measures the stored files. All of these are local reads.</summary>
    public void Initialize()
    {
        LastProfile = _settings.LoadLastProfile();
        RefreshLeftovers();
    }

    public void RefreshLeftovers()
    {
        HasLeftovers = _leftovers.HasLeftovers(_session.WorkFolder);
        var bytes = _leftovers.StoredDataBytes();
        StoredDataText = bytes > 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.Welcome_StoredData, UsbSizes.Format(bytes))
            : null;
    }

    [RelayCommand]
    private void OpenProfile()
    {
        var result = _files.Open();
        if (result is null) return;
        if (!result.Succeeded || result.Profile is null)
        {
            StatusMessage = null;
            ErrorMessage = result.Error;
            return;
        }

        _session.LoadProfile(result.Profile);
        ErrorMessage = null;
        var opened = string.Format(CultureInfo.CurrentCulture, Strings.Welcome_ProfileOpened, result.Profile.Name);
        StatusMessage = result.Warnings.Count == 0
            ? opened
            : opened + Environment.NewLine + string.Join(Environment.NewLine, result.Warnings);
    }

    [RelayCommand]
    private void UseLastProfile()
    {
        if (LastProfile is null) return;
        _session.LoadProfile(LastProfile);
        ErrorMessage = null;
        StatusMessage = string.Format(CultureInfo.CurrentCulture, Strings.Welcome_ProfileOpened, LastProfile.Name);
    }

    [RelayCommand(CanExecute = nameof(CanCleanUp))]
    private async Task CleanUpAsync()
    {
        IsCleaning = true;
        CleanupMessage = null;
        try
        {
            var report = await _leftovers.CleanAsync(_session.WorkFolder);
            CleanupFailed = !report.Success;
            CleanupMessage = WithNotes(report.Success
                ? Strings.Welcome_CleanupDone
                : Strings.Welcome_CleanupProblems + Environment.NewLine +
                  string.Join(Environment.NewLine, report.Problems), report);
        }
        catch (PlainstallException ex)
        {
            CleanupFailed = true;
            CleanupMessage = ex.Message;
        }
        finally
        {
            IsCleaning = false;
            RefreshLeftovers();
        }
    }

    [RelayCommand(CanExecute = nameof(CanCleanUp))]
    private async Task DeleteStoredDataAsync()
    {
        IsCleaning = true;
        StoredDataMessage = null;
        try
        {
            var report = await _leftovers.DeleteStoredDataAsync();
            StoredDataFailed = !report.Success;
            StoredDataMessage = WithNotes(report.Success
                ? string.Join(Environment.NewLine, report.Done)
                : Strings.Welcome_DeleteProblems + Environment.NewLine +
                  string.Join(Environment.NewLine, report.Problems), report);
        }
        catch (PlainstallException ex)
        {
            StoredDataFailed = true;
            StoredDataMessage = ex.Message;
        }
        finally
        {
            IsCleaning = false;
            RefreshLeftovers();
        }
    }

    private bool CanCleanUp()
    {
        return !IsCleaning;
    }

    /// <summary>
    ///     Adds what the user must know after the cleanup, such as a stick that was erased but not written. The
    ///     journal that knew it is gone after the cleanup, so this is the only time it can be said.
    /// </summary>
    private static string WithNotes(string message, CleanupReport report)
    {
        return report.Notes.Count == 0
            ? message
            : message + Environment.NewLine + string.Join(Environment.NewLine, report.Notes);
    }

    private void OnPresetChosen(PresetCardViewModel card)
    {
        if (_syncing) return;
        var hadChanges = _session.StartedFromFile || _session.Editor.ChangedCount > 0;
        _session.StartFromPreset(card.Preset);
        ErrorMessage = null;
        StatusMessage = hadChanges
            ? string.Format(CultureInfo.CurrentCulture, Strings.Welcome_PresetReplacedChanges, card.Title)
            : null;
    }

    private void SyncCards()
    {
        _syncing = true;
        try
        {
            foreach (var card in Presets) card.IsSelected = card.Preset.Id == _session.Profile.Apps.Preset;
        }
        finally
        {
            _syncing = false;
        }

        OnPropertyChanged(nameof(StartingPointText));
    }

    private string PresetTitle(string id)
    {
        return _session.Catalog.FindPreset(id)?.Title ?? Strings.Welcome_NoPreset;
    }
}