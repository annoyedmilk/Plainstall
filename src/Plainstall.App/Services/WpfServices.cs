using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Plainstall.App.Resources;
using Plainstall.Core.Profiles;

namespace Plainstall.App.Services;

public sealed class WpfDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;

    public void Post(Action action)
    {
        _dispatcher.BeginInvoke(action);
    }
}

/// <summary>The standard Windows file dialogs.</summary>
public sealed class DialogService : IDialogService
{
    private static Window? Owner => Application.Current.MainWindow;

    public string? PickProfileToOpen(string? initialFolder)
    {
        var dialog = new OpenFileDialog
        {
            Title = Strings.Dialog_OpenProfileTitle,
            Filter = Strings.Dialog_ProfileFilter + "|*" + Profile.FileExtension + "|" + Strings.Dialog_AllFilesFilter +
                     "|*.*",
            InitialDirectory = ExistingFolder(initialFolder)
        };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? PickProfileSaveLocation(string suggestedFileName, string? initialFolder)
    {
        var dialog = new SaveFileDialog
        {
            Title = Strings.Dialog_SaveProfileTitle,
            Filter = Strings.Dialog_ProfileFilter + "|*" + Profile.FileExtension,
            FileName = suggestedFileName,
            InitialDirectory = ExistingFolder(initialFolder),
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(Owner) != true) return null;
        // The dialog adds only ".json". The profile extension has two parts.
        var path = dialog.FileName;
        return path.EndsWith(Profile.FileExtension, StringComparison.OrdinalIgnoreCase)
            ? path
            : Path.ChangeExtension(path, null) + Profile.FileExtension;
    }

    public string? PickIsoToOpen(string? initialPath)
    {
        var dialog = new OpenFileDialog
        {
            Title = Strings.Dialog_OpenIsoTitle,
            Filter = Strings.Dialog_IsoFilter + "|*.iso",
            InitialDirectory = ExistingFolder(initialPath is null ? null : Path.GetDirectoryName(initialPath))
        };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public SaveFileChoice? PickIsoSaveLocation(string suggestedFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = Strings.Dialog_SaveIsoTitle,
            Filter = Strings.Dialog_IsoFilter + "|*.iso",
            FileName = suggestedFileName,
            DefaultExt = ".iso",
            AddExtension = true,
            OverwritePrompt = true
        };
        // The dialog has already asked whether to replace an existing file.
        return dialog.ShowDialog(Owner) == true
            ? new SaveFileChoice(dialog.FileName, File.Exists(dialog.FileName))
            : null;
    }

    public string? PickFolder(string? initialFolder)
    {
        var dialog = new OpenFolderDialog
            { Title = Strings.Dialog_PickFolderTitle, InitialDirectory = ExistingFolder(initialFolder) };
        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    public IReadOnlyList<string> PickUpdateFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = Strings.Dialog_PickUpdatesTitle,
            Filter = Strings.Dialog_UpdateFilter + "|*.msu;*.cab",
            Multiselect = true
        };
        return dialog.ShowDialog(Owner) == true ? dialog.FileNames : [];
    }

    private static string ExistingFolder(string? folder)
    {
        return folder is not null && Directory.Exists(folder) ? folder : "";
    }
}

public sealed class ShellService : IShellService
{
    public void Open(string path)
    {
        Start(path);
    }

    public void OpenWebPage(Uri uri)
    {
        // Only web pages: the update notice must never become a way to run something.
        if (uri.Scheme == Uri.UriSchemeHttps) Start(uri.AbsoluteUri);
    }

    private static void Start(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            var message = string.Format(CultureInfo.CurrentCulture, Strings.Shell_OpenFailed, target, ex.Message);
            if (Application.Current.MainWindow is { } owner)
                MessageBox.Show(owner, message, Strings.App_Title, MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show(message, Strings.App_Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}