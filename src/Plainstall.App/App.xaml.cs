using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Plainstall.App.Composition;
using Plainstall.App.Rendering;
using Plainstall.App.Resources;
using Plainstall.App.Services;
using Plainstall.App.ViewModels;
using Plainstall.App.Views;
using Plainstall.Core;

namespace Plainstall.App;

public partial class App
{
    private ILogger? _logger;
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var options = AppOptions.Parse(e.Args);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        if (options.RenderPagesFolder is { } folder)
        {
            _ = RenderAsync(folder);
            return;
        }

        var logFolder = Path.Combine(SettingsStore.DefaultFolder, "logs");
        Directory.CreateDirectory(logFolder);
        _services = AppComposition.BuildForApp(Path.Combine(logFolder, $"app-{DateTime.Now:yyyyMMdd-HHmmss}.log"));
        _logger = _services.GetRequiredService<ILogger<App>>();
        if (_logger.IsEnabled(LogLevel.Information))
        {
            var host = _services.GetRequiredService<IHostInfo>();
            _logger.LogInformation("Plainstall {Version} started; elevated: {Elevated}", host.AppVersion,
                host.IsElevated);
        }

        var window = _services.GetRequiredService<MainWindow>();
        var main = _services.GetRequiredService<MainViewModel>();
        MainWindow = window;
        window.Closed += (_, _) => Shutdown();
        window.Show();
        _ = main.InitializeAsync();
    }

    private async Task RenderAsync(string folder)
    {
        int exitCode;
        try
        {
            exitCode = await PageRenderer.RenderAllAsync(folder);
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "render-error.txt"), ex.ToString());
            exitCode = 1;
        }

        Shutdown(exitCode);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // No crash dialog: the user sees a plain message and the app keeps running.
        _logger?.LogError(e.Exception, "Unhandled exception on the UI thread");
        e.Handled = true;
        var message = e.Exception is PlainstallException pe
            ? pe.Message
            : string.Format(CultureInfo.CurrentCulture, Strings.App_UnexpectedError, e.Exception.Message);
        if (MainWindow is { } owner)
            MessageBox.Show(owner, message, Strings.App_Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            MessageBox.Show(message, Strings.App_Title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        _logger?.LogCritical(e.ExceptionObject as Exception, "Unhandled exception; the app has to close");
    }
}

/// <summary>Command-line switches. The only switch, --render-pages, is for reviews and is not shown anywhere.</summary>
internal sealed record AppOptions(string? RenderPagesFolder)
{
    public static AppOptions Parse(IReadOnlyList<string> args)
    {
        string? render = null;
        for (var i = 0; i < args.Count; i++)
            if (args[i].Equals("--render-pages", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                render = Path.GetFullPath(args[++i]);

        return new AppOptions(render);
    }
}