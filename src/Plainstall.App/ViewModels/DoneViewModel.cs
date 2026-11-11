using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plainstall.App.Resources;
using Plainstall.App.Services;

namespace Plainstall.App.ViewModels;

public enum DoneKind
{
    Succeeded,
    SucceededWithFailures,
    Failed,
    Cancelled
}

public sealed record BootKeyRow(string Brand, string Text);

public sealed partial class DoneViewModel : WizardPageViewModel
{
    private readonly ProfileFiles _files;
    private readonly IWizardNavigator _navigator;
    private readonly WizardSession _session;
    private readonly IShellService _shell;

    public DoneViewModel(WizardSession session, IShellService shell, ProfileFiles files, IWizardNavigator navigator,
        IReadOnlyList<BootKey> bootKeys)
    {
        _session = session;
        _shell = shell;
        _files = files;
        _navigator = navigator;
        BootKeys =
        [
            .. bootKeys.Select(k => new BootKeyRow(k.Brand, k.Setup.Count == 0
                ? string.Format(CultureInfo.CurrentCulture, Strings.Done_BootKeyUsually, Keys(k.BootMenu))
                : string.Format(CultureInfo.CurrentCulture, Strings.Done_BootKeyUsuallyWithSetup, Keys(k.BootMenu),
                    Keys(k.Setup))))
        ];
        Checklist =
        [
            Strings.Done_Checklist1, Strings.Done_Checklist2, Strings.Done_Checklist3, Strings.Done_Checklist4,
            Strings.Done_Checklist5
        ];
    }

    public override WizardStep Step => WizardStep.Done;

    public override bool CanContinue => false;

    public IReadOnlyList<BootKeyRow> BootKeys { get; }

    public IReadOnlyList<string> Checklist { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSuccess), nameof(IsFailure), nameof(IsFailed), nameof(IsCancelled))]
    public partial DoneKind Kind { get; private set; }

    public bool IsSuccess => Kind is DoneKind.Succeeded or DoneKind.SucceededWithFailures;
    public bool IsFailure => !IsSuccess;
    public bool IsFailed => Kind == DoneKind.Failed;
    public bool IsCancelled => Kind == DoneKind.Cancelled;

    [ObservableProperty] public partial string Headline { get; private set; } = "";

    [ObservableProperty] public partial string Message { get; private set; } = "";

    [ObservableProperty] public partial string? ErrorText { get; private set; }

    public ObservableCollection<string> Warnings { get; } = [];

    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>Where the results are, one line each.</summary>
    public ObservableCollection<string> Outputs { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenLogCommand))]
    public partial string? LogPath { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenReportFolderCommand))]
    public partial string? ReportPath { get; private set; }

    [ObservableProperty] public partial string? SaveMessage { get; private set; }

    private static string Keys(IReadOnlyList<string> keys)
    {
        return string.Join(" " + Strings.Done_KeyOr + " ", keys);
    }

    public void Show(BuildOutcome outcome, string? usbName)
    {
        var report = outcome.Report;
        Kind = report.Result switch
        {
            "succeeded" => DoneKind.Succeeded,
            "completed-with-failures" => DoneKind.SucceededWithFailures,
            "cancelled" => DoneKind.Cancelled,
            _ => DoneKind.Failed
        };
        Headline = Kind switch
        {
            DoneKind.Succeeded => Strings.Done_SucceededHeadline,
            DoneKind.SucceededWithFailures => Strings.Done_PartlyHeadline,
            DoneKind.Cancelled => Strings.Done_CancelledHeadline,
            _ => Strings.Done_FailedHeadline
        };
        Message = Kind switch
        {
            DoneKind.Succeeded => usbName is null ? Strings.Done_SucceededIsoMessage : Strings.Done_SucceededMessage,
            DoneKind.SucceededWithFailures => Strings.Done_PartlyMessage,
            DoneKind.Cancelled => outcome.CleanupFinished
                ? Strings.Done_CancelledMessage
                : Strings.Done_CancelledNotCleanedMessage,
            _ => Strings.Done_FailedMessage
        };
        // A cancel is not an error. The headline says what happened.
        ErrorText = IsFailed ? report.Error : null;
        Warnings.Clear();
        foreach (var warning in report.Warnings) Warnings.Add(warning);
        Outputs.Clear();
        // The engine names the ISO only after it passed its check. A stick that fails after it does not undo it.
        if (outcome.IsoPath is { } iso)
            Outputs.Add(string.Format(CultureInfo.CurrentCulture, Strings.Done_IsoWritten, iso));
        if (IsSuccess && usbName is not null)
            Outputs.Add(string.Format(CultureInfo.CurrentCulture, Strings.Done_UsbWritten, usbName));
        LogPath = outcome.LogPath;
        ReportPath = outcome.ReportPath;
        SaveMessage = null;
        OnPropertyChanged(nameof(HasWarnings));
    }

    [RelayCommand(CanExecute = nameof(HasLog))]
    private void OpenLog()
    {
        if (LogPath is not null) _shell.Open(LogPath);
    }

    private bool HasLog()
    {
        return LogPath is not null;
    }

    [RelayCommand(CanExecute = nameof(HasReport))]
    private void OpenReportFolder()
    {
        if (ReportPath is not null && Path.GetDirectoryName(ReportPath) is { } folder) _shell.Open(folder);
    }

    private bool HasReport()
    {
        return ReportPath is not null;
    }

    [RelayCommand]
    private void SaveProfile()
    {
        var result = _files.Save(_session.Profile);
        if (result is null) return;
        SaveMessage = result.Succeeded
            ? string.Format(CultureInfo.CurrentCulture, Strings.Customize_Saved, result.Path)
            : result.Error;
    }

    [RelayCommand]
    private void BackToReview()
    {
        _navigator.GoTo(WizardStep.Review);
    }

    [RelayCommand]
    private void StartOver()
    {
        _navigator.GoTo(WizardStep.Welcome);
    }
}