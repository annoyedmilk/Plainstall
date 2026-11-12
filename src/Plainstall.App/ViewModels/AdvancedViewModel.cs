using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plainstall.App.Resources;
using Plainstall.App.Services;
using Plainstall.Core.Catalog;
using Plainstall.Core.Profiles;

namespace Plainstall.App.ViewModels;

public sealed partial class InstallOptionViewModel : ObservableObject
{
    private readonly Action<InstallOptionViewModel, bool> _changed;
    private bool _loading;

    internal InstallOptionViewModel(InstallSuggestion suggestion, Action<InstallOptionViewModel, bool> changed)
    {
        Suggestion = suggestion;
        _changed = changed;
    }

    public InstallSuggestion Suggestion { get; }
    public string WingetId => Suggestion.WingetId;
    public string Title => Suggestion.Title;
    public string Description => Suggestion.Description;

    [ObservableProperty] public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value)
    {
        if (!_loading) _changed(this, value);
    }

    internal void Load(bool selected)
    {
        _loading = true;
        IsSelected = selected;
        _loading = false;
    }
}

public sealed class InstallGroupViewModel(string title, IReadOnlyList<InstallOptionViewModel> items)
{
    public string Title { get; } = title;
    public IReadOnlyList<InstallOptionViewModel> Items { get; } = items;
}

/// <summary>The Advanced section: apps to install at the first sign-in, drivers, update packages, extra cleanup and the image check.</summary>
public sealed partial class AdvancedViewModel : ObservableObject
{
    public const int MaxInstalls = 50;
    private const string BrowserCategory = "browsers";
    private const string EdgeEntryId = "edge";
    private readonly IDialogService _dialogs;

    private readonly WizardSession _session;

    private bool _loading;

    public AdvancedViewModel(WizardSession session, IDialogService dialogs)
    {
        _session = session;
        _dialogs = dialogs;
        InstallGroups =
        [
            .. session.Installs.Categories
                .Select(c => new InstallGroupViewModel(c.Title,
                [
                    .. session.Installs.Entries.Where(e => e.Category == c.Id)
                        .Select(e => new InstallOptionViewModel(e, OnSuggestionChanged))
                ]))
                .Where(g => g.Items.Count > 0)
        ];
        LoadFromProfile();
    }

    public IReadOnlyList<InstallGroupViewModel> InstallGroups { get; }

    /// <summary>Winget ids and Store ids the user typed that are not on the pick list.</summary>
    public ObservableCollection<string> CustomInstalls { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddWingetIdCommand))]
    public partial string NewWingetId { get; set; } = "";

    [ObservableProperty] public partial string? NewWingetIdError { get; private set; }

    public int InstallCount => _session.Profile.Apps.Install.Count;

    public string InstallCountText => string.Format(CultureInfo.CurrentCulture, Strings.Advanced_InstallCount,
        InstallCount, MaxInstalls);

    /// <summary>True when Edge is removed and nothing else would let the user browse after setup.</summary>
    public bool ShowBrowserSuggestion =>
        _session.Catalog.FindEntry(EdgeEntryId) is { } edge && _session.Editor.IsOn(edge)
                                                            && !_session.Profile.Apps.Install.Any(id =>
                                                                _session.Installs.Find(id) is
                                                                    { Category: BrowserCategory });

    [ObservableProperty] public partial bool DriversFromThisPc { get; set; }

    public ObservableCollection<string> DriverFolders { get; } = [];

    public ObservableCollection<string> Updates { get; } = [];

    [ObservableProperty] public partial bool ResetBase { get; set; }

    [ObservableProperty] public partial bool ScanHealth { get; set; }

    partial void OnDriversFromThisPcChanged(bool value)
    {
        if (!_loading) _session.Editor.Update(p => p with { Drivers = p.Drivers with { FromThisPc = value } });
    }

    partial void OnResetBaseChanged(bool value)
    {
        if (!_loading) _session.Editor.Update(p => p with { Cleanup = new CleanupOptions { ResetBase = value } });
    }

    partial void OnScanHealthChanged(bool value)
    {
        if (!_loading) _session.Editor.Update(p => p with { Verify = new VerifyOptions { ScanHealth = value } });
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NewWingetId))
            // Typing again takes the old complaint away.
            NewWingetIdError = null;
        base.OnPropertyChanged(e);
    }

    [RelayCommand(CanExecute = nameof(CanAddWingetId))]
    private void AddWingetId()
    {
        var id = NewWingetId.Trim();
        // Store web addresses show the product id in small letters. Winget knows it in capitals.
        if (InstallIds.IsStoreId(id.ToUpperInvariant())) id = id.ToUpperInvariant();
        if (!InstallIds.IsValid(id))
        {
            NewWingetIdError = Strings.Advanced_WingetIdInvalid;
            return;
        }

        if (_session.Profile.Apps.Install.Contains(id, StringComparer.OrdinalIgnoreCase))
        {
            NewWingetIdError = Strings.Advanced_WingetIdDuplicate;
            return;
        }

        if (InstallCount >= MaxInstalls)
        {
            NewWingetIdError = string.Format(CultureInfo.CurrentCulture, Strings.Advanced_InstallLimit, MaxInstalls);
            return;
        }

        SetInstalled(id, true);
        NewWingetId = "";
        LoadFromProfile();
    }

    private bool CanAddWingetId()
    {
        return NewWingetId.Trim().Length > 0;
    }

    [RelayCommand]
    private void RemoveCustomInstall(string id)
    {
        SetInstalled(id, false);
        LoadFromProfile();
    }

    [RelayCommand]
    private void AddDriverFolder()
    {
        var folder = _dialogs.PickFolder(DriverFolders.LastOrDefault());
        if (folder is null || DriverFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) return;
        _session.Editor.Update(p => p with { Drivers = p.Drivers with { Folders = [.. p.Drivers.Folders, folder] } });
        LoadFromProfile();
    }

    [RelayCommand]
    private void RemoveDriverFolder(string folder)
    {
        _session.Editor.Update(p => p with
        {
            Drivers = p.Drivers with
            {
                Folders =
                [
                    .. p.Drivers.Folders.Where(f =>
                        !string.Equals(f, folder, StringComparison.OrdinalIgnoreCase))
                ]
            }
        });
        LoadFromProfile();
    }

    [RelayCommand]
    private void AddUpdates()
    {
        var files = _dialogs.PickUpdateFiles()
            .Where(f => f.EndsWith(".msu", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".cab", StringComparison.OrdinalIgnoreCase))
            .Where(f => !_session.Profile.Updates.Contains(f, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0) return;
        _session.Editor.Update(p => p with { Updates = [.. p.Updates, .. files] });
        LoadFromProfile();
    }

    [RelayCommand]
    private void RemoveUpdate(string file)
    {
        _session.Editor.Update(p => p with
        {
            Updates = [.. p.Updates.Where(u => !string.Equals(u, file, StringComparison.OrdinalIgnoreCase))]
        });
        LoadFromProfile();
    }

    /// <summary>Updates are installed in the listed order, so the order can be changed.</summary>
    [RelayCommand]
    private void MoveUpdateUp(string file)
    {
        MoveUpdate(file, -1);
    }

    [RelayCommand]
    private void MoveUpdateDown(string file)
    {
        MoveUpdate(file, +1);
    }

    private void MoveUpdate(string file, int delta)
    {
        var list = _session.Profile.Updates.ToList();
        var index = list.FindIndex(u => string.Equals(u, file, StringComparison.OrdinalIgnoreCase));
        var target = index + delta;
        if (index < 0 || target < 0 || target >= list.Count) return;
        (list[index], list[target]) = (list[target], list[index]);
        _session.Editor.Update(p => p with { Updates = list });
        LoadFromProfile();
    }

    public void LoadFromProfile()
    {
        var profile = _session.Profile;
        _loading = true;
        try
        {
            foreach (var option in InstallGroups.SelectMany(g => g.Items))
                option.Load(profile.Apps.Install.Contains(option.WingetId, StringComparer.OrdinalIgnoreCase));
            Replace(CustomInstalls, profile.Apps.Install.Where(id => _session.Installs.Find(id) is null));
            DriversFromThisPc = profile.Drivers.FromThisPc;
            Replace(DriverFolders, profile.Drivers.Folders);
            Replace(Updates, profile.Updates);
            ResetBase = profile.Cleanup.ResetBase;
            ScanHealth = profile.Verify.ScanHealth;
        }
        finally
        {
            _loading = false;
        }

        OnPropertyChanged(nameof(InstallCount));
        OnPropertyChanged(nameof(InstallCountText));
        OnPropertyChanged(nameof(ShowBrowserSuggestion));
    }

    /// <summary>Called when the Apps tab changes, because removing Edge changes the browser suggestion.</summary>
    public void RefreshSuggestion()
    {
        OnPropertyChanged(nameof(ShowBrowserSuggestion));
    }

    private void OnSuggestionChanged(InstallOptionViewModel option, bool selected)
    {
        if (selected && InstallCount >= MaxInstalls)
        {
            NewWingetIdError = string.Format(CultureInfo.CurrentCulture, Strings.Advanced_InstallLimit, MaxInstalls);
            option.Load(false);
            return;
        }

        SetInstalled(option.WingetId, selected);
        OnPropertyChanged(nameof(InstallCount));
        OnPropertyChanged(nameof(InstallCountText));
        OnPropertyChanged(nameof(ShowBrowserSuggestion));
    }

    private void SetInstalled(string id, bool on)
    {
        _session.Editor.Update(p =>
        {
            var list = p.Apps.Install.Where(i => !string.Equals(i, id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (on) list.Add(id);
            return p with { Apps = p.Apps with { Install = list } };
        });
    }

    private static void Replace(ObservableCollection<string> target, IEnumerable<string> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }
}