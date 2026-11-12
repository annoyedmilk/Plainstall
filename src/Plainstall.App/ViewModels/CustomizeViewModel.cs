using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plainstall.App.Resources;
using Plainstall.App.Services;
using Plainstall.Core.Catalog;

namespace Plainstall.App.ViewModels;

public enum CustomizeTab
{
    Apps,
    Tweaks,
    Setup,
    Advanced
}

/// <summary>One section of the Customize page: a card in the section bar, with a short summary.</summary>
public sealed partial class CustomizeSectionViewModel(CustomizeTab tab, string title) : ObservableObject
{
    public CustomizeTab Tab { get; } = tab;
    public string Title { get; } = title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial string Summary { get; internal set; } = "";

    public string AutomationName =>
        string.Format(CultureInfo.CurrentCulture, Strings.Customize_SectionName, Title, Summary);
}

public sealed partial class CustomizeViewModel : WizardPageViewModel
{
    private readonly ProfileFiles _files;
    private readonly WizardSession _session;

    public CustomizeViewModel(WizardSession session, ProfileFiles files, ILocaleCatalog locales, IDialogService dialogs,
        IUiDispatcher dispatcher)
    {
        _session = session;
        _files = files;
        Apps = new CatalogListViewModel(session, CatalogKind.Apps, dispatcher, Report);
        Tweaks = new CatalogListViewModel(session, CatalogKind.Tweaks, dispatcher, Report);
        Setup = new SetupViewModel(session, locales);
        Advanced = new AdvancedViewModel(session, dialogs);
        Sections =
        [
            new CustomizeSectionViewModel(CustomizeTab.Apps, Strings.Customize_TabApps),
            new CustomizeSectionViewModel(CustomizeTab.Tweaks, Strings.Customize_TabTweaks),
            new CustomizeSectionViewModel(CustomizeTab.Setup, Strings.Customize_TabSetup)
            {
                Summary = Strings.Customize_SectionSetupSummary
            },
            new CustomizeSectionViewModel(CustomizeTab.Advanced, Strings.Customize_TabAdvanced)
            {
                Summary = Strings.Customize_SectionAdvancedSummary
            }
        ];
        UpdateSummaries();
        Setup.ValidationChanged += (_, _) => NotifyCanContinue();
        session.Editor.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(ChangedText));
            UpdateSummaries();
            Advanced.RefreshSuggestion();
        };
        session.ProfileReplaced += (_, _) => ReloadAll();
    }

    public override WizardStep Step => WizardStep.Customize;

    public CatalogListViewModel Apps { get; }
    public CatalogListViewModel Tweaks { get; }
    public SetupViewModel Setup { get; }
    public AdvancedViewModel Advanced { get; }

    /// <summary>The sections in the order the section bar shows them and Next walks through them.</summary>
    public IReadOnlyList<CustomizeSectionViewModel> Sections { get; }

    public CustomizeSectionViewModel AppsSection => Sections[(int)CustomizeTab.Apps];
    public CustomizeSectionViewModel TweaksSection => Sections[(int)CustomizeTab.Tweaks];
    public CustomizeSectionViewModel SetupSection => Sections[(int)CustomizeTab.Setup];
    public CustomizeSectionViewModel AdvancedSection => Sections[(int)CustomizeTab.Advanced];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedTab), nameof(NextSectionTitle))]
    public partial int SelectedTabIndex { get; set; }

    public CustomizeTab SelectedTab
    {
        get => (CustomizeTab)SelectedTabIndex;
        set => SelectedTabIndex = (int)value;
    }

    /// <summary>What the last switch also changed or why it was refused. Newest first.</summary>
    public ObservableCollection<string> Messages { get; } = [];

    public bool HasMessages => Messages.Count > 0;

    public string ChangedText
    {
        get
        {
            var preset = _session.Catalog.FindPreset(_session.Profile.Apps.Preset)?.Title ?? Strings.Welcome_NoPreset;
            var changed = _session.Editor.ChangedCount;
            return changed == 0
                ? string.Format(CultureInfo.CurrentCulture, Strings.Customize_BasedOnPreset, preset)
                : string.Format(CultureInfo.CurrentCulture, Strings.Customize_BasedOnPresetChanged, preset, changed);
        }
    }

    /// <summary>A short line after saving the profile, or the reason it could not be saved.</summary>
    [ObservableProperty]
    public partial string? SaveMessage { get; private set; }

    [ObservableProperty] public partial bool SaveFailed { get; private set; }

    public override bool CanContinue => !Setup.HasErrors;

    public override string? ContinueBlockedReason => Setup.HasErrors ? Strings.Customize_BlockedSetupErrors : null;

    public override string? NextSectionTitle =>
        SelectedTabIndex + 1 < Sections.Count ? Sections[SelectedTabIndex + 1].Title : null;

    public override Task OnShownAsync()
    {
        ReloadAll();
        return Task.CompletedTask;
    }

    public override bool MoveToNextSection()
    {
        if (SelectedTabIndex + 1 >= Sections.Count) return false;
        SelectedTabIndex++;
        return true;
    }

    public override bool MoveToPreviousSection()
    {
        if (SelectedTabIndex <= 0) return false;
        SelectedTabIndex--;
        return true;
    }

    /// <summary>Next from Windows starts at the first section. Back from Review returns to the last one.</summary>
    public override void PrepareArrival(bool forward)
    {
        SelectedTabIndex = forward ? 0 : Sections.Count - 1;
    }

    [RelayCommand]
    private void ResetToPreset()
    {
        _session.Editor.ResetSwitches();
        Apps.Refresh();
        Tweaks.Refresh();
        Messages.Clear();
        Messages.Add(Strings.Customize_ResetDone);
        OnPropertyChanged(nameof(HasMessages));
    }

    [RelayCommand]
    private void SaveProfile()
    {
        if (Setup.HasErrors)
        {
            SaveFailed = true;
            SaveMessage = Strings.Customize_BlockedSetupErrors;
            return;
        }

        var result = _files.Save(_session.Profile);
        if (result is null) return;
        SaveFailed = !result.Succeeded;
        if (result is { Succeeded: true, Profile: { } saved })
        {
            _session.Editor.Replace(saved);
            SaveMessage = string.Format(CultureInfo.CurrentCulture, Strings.Customize_Saved, result.Path);
        }
        else
        {
            SaveMessage = result.Error;
        }
    }

    [RelayCommand]
    private void DismissMessages()
    {
        Messages.Clear();
        OnPropertyChanged(nameof(HasMessages));
    }

    private void Report(IReadOnlyList<string> messages)
    {
        Messages.Clear();
        foreach (var message in messages) Messages.Add(message);
        OnPropertyChanged(nameof(HasMessages));
    }

    private void ReloadAll()
    {
        Apps.Refresh();
        Tweaks.Refresh();
        Setup.LoadFromProfile();
        Advanced.LoadFromProfile();
        OnPropertyChanged(nameof(ChangedText));
        UpdateSummaries();
        NotifyCanContinue();
    }

    /// <summary>Counts from the profile itself, so the cards are right before the switches have synced.</summary>
    private void UpdateSummaries()
    {
        AppsSection.Summary = Count(Apps, Strings.Customize_SectionAppsSummary);
        TweaksSection.Summary = Count(Tweaks, Strings.Customize_SectionTweaksSummary);
        return;

        string Count(CatalogListViewModel list, string format)
        {
            var entries = list.Entries.ToList();
            var on = entries.Count(e => _session.Editor.IsOn(e.Entry));
            return string.Format(CultureInfo.CurrentCulture, format, on, entries.Count);
        }
    }
}