using System.Collections.Specialized;
using System.Runtime.ExceptionServices;
using System.Windows;
using Plainstall.App.ViewModels;

namespace Plainstall.App.Views;

public partial class BuildPage
{
    private INotifyCollectionChanged? _log;

    public BuildPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WatchLog();
        Loaded += (_, _) => WatchLog();
    }

    /// <summary>
    ///     A PasswordBox cannot be bound, by design. The two values go straight to the view model, which
    ///     hands the password to the build and keeps it nowhere else. The boxes are cleared at once.
    /// </summary>
    private async void OnStartWithPassword(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not BuildViewModel vm) return;
            var password = PasswordInput.Password;
            var confirmation = ConfirmInput.Password;
            PasswordInput.Clear();
            ConfirmInput.Clear();
            await vm.SubmitPassword(password, confirmation);
            if (vm.NeedsPassword) PasswordInput.Focus();
        }
        catch (Exception ex)
        {
            // Raised again on the dispatcher, so the app's handler for the UI thread reports it like any other error.
            _ = Dispatcher.BeginInvoke(new Action(() => ExceptionDispatchInfo.Throw(ex)));
        }
    }

    private void WatchLog()
    {
        if (_log is not null) _log.CollectionChanged -= OnLogChanged;
        _log = (DataContext as BuildViewModel)?.LogLines;
        if (_log is not null) _log.CollectionChanged += OnLogChanged;
    }

    // The newest line stays in view while the build runs.
    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[^1]);
    }
}