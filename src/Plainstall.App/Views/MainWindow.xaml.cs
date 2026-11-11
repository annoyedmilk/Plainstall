using System.ComponentModel;
using System.Windows;
using Plainstall.App.Resources;
using Plainstall.App.Themes;
using Plainstall.App.ViewModels;

namespace Plainstall.App.Views;

public partial class MainWindow
{
    public MainWindow(MainViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    public MainViewModel ViewModel => (MainViewModel)DataContext;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        BrandTheme.UsePageBackground(this);
    }

    /// <summary>Keeps the window open while a build or a cleanup runs, so it is not stopped in the middle of a step.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (ViewModel.CloseBlockedReason is { } reason)
        {
            e.Cancel = true;
            MessageBox.Show(this, reason, Strings.App_Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        base.OnClosing(e);
    }
}