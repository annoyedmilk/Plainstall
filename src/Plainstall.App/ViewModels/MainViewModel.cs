using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Plainstall.App.Resources;
using Plainstall.App.Services;
using Plainstall.Core.Release;

namespace Plainstall.App.ViewModels;

public enum StepState
{
    Upcoming,
    Current,
    Done
}

public sealed partial class StepViewModel(WizardStep step, string title) : ObservableObject
{
    public WizardStep Step { get; } = step;
    public int Number => (int)Step + 1;
    public string Title { get; } = title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(AutomationName), nameof(IsCurrent), nameof(IsDone))]
    public partial StepState State { get; set; }

    public bool IsCurrent => State == StepState.Current;
    public bool IsDone => State == StepState.Done;

    public string StateText => State switch
    {
        StepState.Current => Strings.Step_Current,
        StepState.Done => Strings.Step_DoneState,
        _ => Strings.Step_Upcoming
    };

    public string AutomationName =>
        string.Format(CultureInfo.CurrentCulture, Strings.Step_AutomationName, Number, Title, StateText);
}

/// <summary>Forwards "go to step" from the pages to the main view model, which is created after them.</summary>
public sealed class WizardNavigator : IWizardNavigator
{
    public Action<WizardStep>? Handler { get; set; }

    public void GoTo(WizardStep step)
    {
        Handler?.Invoke(step);
    }
}

public sealed record ThemeOption(ThemeChoice Choice, string Title);

/// <summary>The window: steps, Back and Next, the banners, the theme.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IHostInfo _host;
    private readonly ILogger _logger;
    private readonly Dictionary<WizardStep, WizardPageViewModel> _pages;
    private readonly WizardSession _session;
    private readonly ISettingsStore _settings;
    private readonly IShellService _shell;
    private readonly IThemeSwitcher _theme;
    private readonly IUpdateChecker _updates;
    private bool _initialized;

    public MainViewModel(WizardSession session, WizardNavigator navigator,
        WelcomeViewModel welcome, WindowsViewModel windows, CustomizeViewModel customize, ReviewViewModel review,
        BuildViewModel build, DoneViewModel done,
        IHostInfo host, IUpdateChecker updates, ISettingsStore settings, IThemeSwitcher theme, IShellService shell,
        ILogger<MainViewModel>? logger = null)
    {
        _session = session;
        _host = host;
        _updates = updates;
        _settings = settings;
        _theme = theme;
        _shell = shell;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        Welcome = welcome;
        Windows = windows;
        Customize = customize;
        Review = review;
        Build = build;
        Done = done;
        _pages =
            new WizardPageViewModel[] { welcome, windows, customize, review, build, done }.ToDictionary(p => p.Step);
        foreach (var page in _pages.Values) page.PropertyChanged += OnPagePropertyChanged;
        Steps =
        [
            new StepViewModel(WizardStep.Welcome, Strings.Step_Welcome),
            new StepViewModel(WizardStep.Windows, Strings.Step_Windows),
            new StepViewModel(WizardStep.Customize, Strings.Step_Customize),
            new StepViewModel(WizardStep.Review, Strings.Step_Review),
            new StepViewModel(WizardStep.Build, Strings.Step_Build),
            new StepViewModel(WizardStep.Done, Strings.Step_Done)
        ];
        ThemeOptions =
        [
            new ThemeOption(ThemeChoice.System, Strings.Theme_System),
            new ThemeOption(ThemeChoice.Light, Strings.Theme_Light),
            new ThemeOption(ThemeChoice.Dark, Strings.Theme_Dark)
        ];
        SelectedTheme = ThemeOptions[0];
        navigator.Handler = GoTo;
        build.Completed += OnBuildCompleted;
        review.WorkFolderChanged += (_, folder) => _settings.Save(_settings.Load() with { WorkFolder = folder });
        CurrentPage = welcome;
        UpdateSteps();
    }

    public WelcomeViewModel Welcome { get; }
    public WindowsViewModel Windows { get; }
    public CustomizeViewModel Customize { get; }
    public ReviewViewModel Review { get; }
    public BuildViewModel Build { get; }
    public DoneViewModel Done { get; }

    public IReadOnlyList<StepViewModel> Steps { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NextText), nameof(NextAutomationName), nameof(ShowBack), nameof(ShowNext),
        nameof(StepText), nameof(ContinueBlockedReason))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(BackCommand))]
    public partial WizardPageViewModel CurrentPage { get; private set; }

    public WizardStep CurrentStep => CurrentPage.Step;

    public string StepText => string.Format(CultureInfo.CurrentCulture, Strings.Step_Of, (int)CurrentStep + 1,
        Steps.Count, Steps[(int)CurrentStep].Title);

    /// <summary>"Next", "Next: Tweaks" while Next opens another section of the same page, or "Start build".</summary>
    public string NextText => CurrentStep == WizardStep.Review ? Strings.Nav_StartBuild :
        CurrentPage.NextSectionTitle is { } section
            ? string.Format(CultureInfo.CurrentCulture, Strings.Nav_NextSection, section)
            : Strings.Nav_Next;

    public string NextAutomationName => CurrentStep == WizardStep.Review ? Strings.Nav_StartBuild :
        CurrentPage.NextSectionTitle is { } section
            ? string.Format(CultureInfo.CurrentCulture, Strings.Nav_NextSectionName, section)
            : Strings.Nav_NextName;

    public bool ShowBack => CurrentStep is not (WizardStep.Welcome or WizardStep.Build or WizardStep.Done);

    public bool ShowNext => CurrentStep is not (WizardStep.Build or WizardStep.Done);

    public string? ContinueBlockedReason => CurrentPage.ContinueBlockedReason;

    public bool ShowElevationBanner => !_host.IsElevated;

    /// <summary>
    ///     Why the window must stay open, or null when it can close. Closing in the middle of a build would stop
    ///     it while an image is mounted or a stick is half written. Closing in the middle of a cleanup would stop it
    ///     while an image is discarded, a registry file is unloaded or files are deleted.
    /// </summary>
    public string? CloseBlockedReason => Build.IsRunning ? Strings.App_CloseWhileBuilding :
        Welcome.IsCleaning ? Strings.App_CloseWhileCleaning : null;

    // ---- update notice --------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(UpdateText))]
    public partial UpdateInfo? Update { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    public partial bool UpdateDismissed { get; private set; }

    public bool HasUpdate => Update is not null && !UpdateDismissed;

    public string? UpdateText => Update is null
        ? null
        : string.Format(CultureInfo.CurrentCulture, Strings.Update_Available, Update.Version.ToString(3));

    // ---- theme ---------------------------------------------------------------------------------

    public IReadOnlyList<ThemeOption> ThemeOptions { get; }

    [ObservableProperty] public partial ThemeOption SelectedTheme { get; set; }

    [RelayCommand]
    private void OpenUpdatePage()
    {
        if (Update is not null) _shell.OpenWebPage(Update.PageUrl);
    }

    [RelayCommand]
    private void DismissUpdate()
    {
        UpdateDismissed = true;
    }

    partial void OnSelectedThemeChanged(ThemeOption value)
    {
        _theme.Apply(value.Choice);
        if (_initialized) _settings.Save(_settings.Load() with { Theme = value.Choice });
    }

    // ---- start ---------------------------------------------------------------------------------

    /// <summary>Reads the settings, prepares the Welcome page and asks once whether a newer version exists.</summary>
    public async Task InitializeAsync()
    {
        var settings = _settings.Load();
        if (settings.WorkFolder is { Length: > 0 } work) _session.WorkFolder = work;
        SelectedTheme = ThemeOptions.First(t => t.Choice == settings.Theme);
        _initialized = true;
        Welcome.Initialize();
        try
        {
            Update = await _updates.CheckAsync(_host.AppVersion, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Being offline is normal. The app then shows no notice.
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("The update check failed: {Message}", ex.Message);
        }
    }

    // ---- navigation ----------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextAsync()
    {
        if (CurrentPage.MoveToNextSection()) return;
        switch (CurrentStep)
        {
            case WizardStep.Review:
                GoTo(WizardStep.Build);
                await Build.BeginAsync(Review.CreateStart);
                break;
            case < WizardStep.Review:
                _pages[CurrentStep + 1].PrepareArrival(true);
                GoTo(CurrentStep + 1);
                break;
        }
    }

    private bool CanGoNext()
    {
        return ShowNext && CurrentPage.CanContinue;
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        if (CurrentPage.MoveToPreviousSection()) return;
        _pages[CurrentStep - 1].PrepareArrival(false);
        GoTo(CurrentStep - 1);
    }

    private bool CanGoBack()
    {
        return ShowBack;
    }

    public void GoTo(WizardStep step)
    {
        if (Build.IsRunning && step != WizardStep.Build) return;
        CurrentPage = _pages[step];
        UpdateSteps();
        _ = ShowPageAsync(CurrentPage);
    }

    private async Task ShowPageAsync(WizardPageViewModel page)
    {
        try
        {
            await page.OnShownAsync();
        }
        catch (Exception ex)
        {
            // A page that fails to refresh must not close the window. The page shows what it has.
            _logger.LogError(ex, "Showing the {Step} page failed", page.Step);
        }

        RefreshNavigation();
    }

    private void OnBuildCompleted(object? sender, BuildOutcome outcome)
    {
        Done.Show(outcome, Review.WantsUsb ? Review.SelectedUsb?.Title : null);
        GoTo(WizardStep.Done);
    }

    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, CurrentPage)) return;
        if (e.PropertyName is nameof(WizardPageViewModel.NextSectionTitle) or null)
        {
            OnPropertyChanged(nameof(NextText));
            OnPropertyChanged(nameof(NextAutomationName));
        }

        if (e.PropertyName is nameof(WizardPageViewModel.CanContinue)
            or nameof(WizardPageViewModel.ContinueBlockedReason) or null) RefreshNavigation();
    }

    private void RefreshNavigation()
    {
        OnPropertyChanged(nameof(ContinueBlockedReason));
        NextCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
    }

    private void UpdateSteps()
    {
        foreach (var step in Steps)
            step.State = step.Step < CurrentStep ? StepState.Done :
                step.Step == CurrentStep ? StepState.Current : StepState.Upcoming;
    }
}