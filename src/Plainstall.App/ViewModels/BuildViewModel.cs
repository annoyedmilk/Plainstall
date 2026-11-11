using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Plainstall.App.Resources;
using Plainstall.App.Services;
using Plainstall.Core;
using Plainstall.Core.Pipeline;
using Plainstall.Core.Unattend;

namespace Plainstall.App.ViewModels;

public enum StageState
{
    Pending,
    Current,
    Done,
    Skipped,
    Failed
}

public sealed partial class StageViewModel(int number, string name) : ObservableObject
{
    public int Number { get; } = number;
    public string Name { get; } = name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(AutomationName))]
    public partial StageState State { get; set; }

    /// <summary>The state in words, so it is never told by colour or icon alone.</summary>
    public string StateText => State switch
    {
        StageState.Current => Strings.Stage_Current,
        StageState.Done => Strings.Stage_Done,
        StageState.Skipped => Strings.Stage_Skipped,
        StageState.Failed => Strings.Stage_Failed,
        _ => Strings.Stage_Pending
    };

    public string AutomationName =>
        string.Format(CultureInfo.CurrentCulture, Strings.Stage_AutomationName, Number, Name, StateText);
}

/// <summary>Delivers progress on the UI thread through <see cref="IUiDispatcher" />.</summary>
internal sealed class DispatchedProgress<T>(IUiDispatcher dispatcher, Action<T> handler) : IProgress<T>
{
    public void Report(T value)
    {
        dispatcher.Post(() => handler(value));
    }
}

public sealed partial class BuildViewModel : WizardPageViewModel
{
    /// <summary>Lines kept in the visible log. The full log is in the file.</summary>
    public const int MaxLogLines = 2000;

    private static readonly string[] StageTitles =
    [
        Strings.Stage1, Strings.Stage2, Strings.Stage3, Strings.Stage4, Strings.Stage5,
        Strings.Stage6, Strings.Stage7, Strings.Stage8, Strings.Stage9, Strings.Stage10
    ];

    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly IBuildRunner _runner;

    private readonly WizardSession _session;
    private readonly ISettingsStore _settings;
    private CancellationTokenSource? _cancel;
    private Func<string?, BuildStart>? _createStart;
    private int _lastStage;
    private int _stageBeforeReport;

    public BuildViewModel(WizardSession session, IBuildRunner runner, IUiDispatcher dispatcher, ISettingsStore settings,
        ILogger<BuildViewModel>? logger = null)
    {
        _session = session;
        _runner = runner;
        _dispatcher = dispatcher;
        _settings = settings;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        var count = BuildPipeline.StageNames.Count;
        for (var i = 0; i < count; i++)
            // The app's own words, so they can be translated. A stage the app does not know keeps the engine's name.
            Stages.Add(new StageViewModel(i + 1,
                i < StageTitles.Length ? StageTitles[i] : BuildPipeline.StageNames[i]));
    }

    public override WizardStep Step => WizardStep.Build;

    public override bool CanContinue => false;

    public ObservableCollection<StageViewModel> Stages { get; } = [];

    public ObservableCollection<string> LogLines { get; } = [];

    [ObservableProperty] public partial string CurrentStageText { get; private set; } = "";

    [ObservableProperty] public partial string? CurrentAction { get; private set; }

    /// <summary>0 to 100, or null when the step cannot tell. The bar then moves without a number.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIndeterminate), nameof(PercentValue), nameof(PercentText))]
    public partial int? Percent { get; private set; }

    public bool IsIndeterminate => IsRunning && Percent is null;
    public double PercentValue => Percent ?? 0;

    public string? PercentText =>
        Percent is { } p ? string.Format(CultureInfo.CurrentCulture, Strings.Build_Percent, p) : null;

    [ObservableProperty] public partial bool IsLogExpanded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIndeterminate))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsStopping { get; private set; }

    /// <summary>The password prompt is shown: the profile asks for a password and the build waits for it.</summary>
    [ObservableProperty]
    public partial bool NeedsPassword { get; private set; }

    [ObservableProperty] public partial string? PasswordError { get; private set; }

    /// <summary>The stopping text explains why a cancel can take a while.</summary>
    public string StoppingText { get; } = Strings.Build_Stopping;

    /// <summary>Raised when the run is over. The wizard then moves to the Done page.</summary>
    public event EventHandler<BuildOutcome>? Completed;

    /// <summary>
    ///     Gets the page ready for a build. When the profile asks for a password the page waits for
    ///     <see cref="SubmitPassword" />. Otherwise the build starts at once.
    /// </summary>
    public Task BeginAsync(Func<string?, BuildStart> createStart)
    {
        _createStart = createStart;
        Reset();
        if (_session.Profile.Setup.LocalAccount is not { AskPassword: true, Name: not null }) return RunAsync(null);
        NeedsPassword = true;
        return Task.CompletedTask;
    }

    /// <summary>Called by the view with the two password boxes. The password goes to the build and nowhere else.</summary>
    public Task SubmitPassword(string password, string confirmation)
    {
        if (password.Length == 0)
        {
            PasswordError = Strings.Build_PasswordEmpty;
            return Task.CompletedTask;
        }

        if (!string.Equals(password, confirmation, StringComparison.Ordinal))
        {
            PasswordError = Strings.Build_PasswordMismatch;
            return Task.CompletedTask;
        }

        if (password.Length > UnattendGenerator.MaxPasswordLength)
        {
            PasswordError = string.Format(CultureInfo.CurrentCulture, Strings.Build_PasswordTooLong,
                UnattendGenerator.MaxPasswordLength);
            return Task.CompletedTask;
        }

        PasswordError = null;
        NeedsPassword = false;
        return RunAsync(password);
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        IsStopping = true;
        _cancel?.Cancel();
    }

    private bool CanCancel()
    {
        return IsRunning && !IsStopping;
    }

    /// <summary>Leaves the password prompt without building.</summary>
    [RelayCommand]
    private void CancelPassword()
    {
        NeedsPassword = false;
        PasswordError = null;
        var report = new BuildReport
        {
            Plainstall = "",
            CatalogVersion = _session.Catalog.CatalogVersion,
            Started = DateTimeOffset.Now,
            Result = "cancelled",
            Error = Strings.Build_CancelledBeforeStart,
            Profile = _session.Profile
        };
        Completed?.Invoke(this, new BuildOutcome(report, null, null, null));
    }

    private async Task RunAsync(string? password)
    {
        if (_createStart is null) throw new InvalidOperationException("BeginAsync was not called.");
        // From here on the password lives only in the request.
        var start = _createStart(password);
        _cancel = new CancellationTokenSource();
        IsRunning = true;
        try
        {
            _settings.SaveLastProfile(start.Request.Profile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The last profile could not be saved: {Message}", ex.Message);
        }

        BuildOutcome outcome;
        try
        {
            outcome = await _runner.RunAsync(start,
                new DispatchedProgress<BuildProgress>(_dispatcher, ApplyProgress),
                new DispatchedProgress<string>(_dispatcher, AppendLog),
                _cancel.Token);
        }
        catch (Exception ex)
        {
            // The pipeline puts its own failures in the report. This is for failures around it.
            _logger.LogError(ex, "The build could not be run");
            outcome = new BuildOutcome(new BuildReport
            {
                Plainstall = start.Request.AppVersion,
                CatalogVersion = _session.Catalog.CatalogVersion,
                Started = DateTimeOffset.Now,
                Result = ex is OperationCanceledException ? "cancelled" : "failed",
                Error = ex is PlainstallException or OperationCanceledException
                    ? ex.Message
                    : string.Format(CultureInfo.CurrentCulture, Strings.Build_UnexpectedError, ex.Message),
                Profile = start.Request.Profile
            }, null, null, null);
        }
        finally
        {
            IsRunning = false;
            IsStopping = false;
            _cancel.Dispose();
            _cancel = null;
        }

        Finish(outcome);
        Completed?.Invoke(this, outcome);
    }

    internal void ApplyProgress(BuildProgress progress)
    {
        var number = Math.Clamp(progress.StageNumber, 1, Stages.Count);
        if (number == Stages.Count && _lastStage < Stages.Count) _stageBeforeReport = _lastStage;
        // After a failure or a cancel the pipeline jumps to the report stage. The stages in between did not run.
        var jumped = number == Stages.Count && _lastStage < Stages.Count - 1;
        foreach (var stage in Stages)
            if (stage.Number < number)
                stage.State = jumped && stage.Number > _lastStage ? StageState.Skipped
                    : stage.State is StageState.Skipped or StageState.Failed ? stage.State
                    : StageState.Done;
            else
                stage.State = stage.Number == number ? StageState.Current : StageState.Pending;

        _lastStage = number;
        var name = Stages[number - 1].Name;
        CurrentStageText = string.Format(CultureInfo.CurrentCulture, Strings.Build_StageOf, number, Stages.Count, name);
        CurrentAction = progress.Action ?? name;
        Percent = progress.Percent is { } p ? Math.Clamp(p, 0, 100) : null;
    }

    internal void AppendLog(string line)
    {
        LogLines.Add(line);
        while (LogLines.Count > MaxLogLines) LogLines.RemoveAt(0);
    }

    private void Finish(BuildOutcome outcome)
    {
        var succeeded = outcome.Report.Result is "succeeded" or "completed-with-failures";
        foreach (var stage in Stages)
            if (succeeded || (stage.Number == Stages.Count && stage.State == StageState.Current))
                stage.State = StageState.Done;
            else if (stage.State == StageState.Current) stage.State = StageState.Failed;

        if (!succeeded && _stageBeforeReport > 0) Stages[_stageBeforeReport - 1].State = StageState.Failed;
        Percent = succeeded ? 100 : Percent;
    }

    private void Reset()
    {
        foreach (var stage in Stages) stage.State = StageState.Pending;
        _lastStage = 0;
        _stageBeforeReport = 0;
        LogLines.Clear();
        CurrentStageText = "";
        CurrentAction = null;
        Percent = null;
        PasswordError = null;
        NeedsPassword = false;
        IsStopping = false;
    }
}