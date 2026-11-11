using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Plainstall.App.Services;
using Plainstall.App.Themes;
using Plainstall.App.ViewModels;
using Plainstall.App.Views;
using Plainstall.Core;
using Plainstall.Core.Catalog;
using Plainstall.Core.Download;
using Plainstall.Core.Output.Usb;
using Plainstall.Core.Pipeline;
using Plainstall.Core.Profiles;
using Plainstall.Core.Release;

namespace Plainstall.App.Composition;

/// <summary>Builds the DI container. The real app and the page renderer differ only in <see cref="AddEngineServices" />.</summary>
public static class AppComposition
{
    /// <summary>The container of the real app.</summary>
    public static ServiceProvider BuildForApp(string appLogPath)
    {
        var services = new ServiceCollection();
        AddLogging(services, appLogPath);
        AddEngineServices(services);
        AddApp(services);
        return services.BuildServiceProvider(new ServiceProviderOptions
            { ValidateOnBuild = true, ValidateScopes = true });
    }

    /// <summary>The services that reach the engine, the network or the PC.</summary>
    public static void AddEngineServices(IServiceCollection services)
    {
        services.AddSingleton<IHostInfo, HostInfo>();
        services.AddSingleton<IMicrosoftDownloadClient>(sp =>
            new MicrosoftDownloadClient(logger: sp.GetRequiredService<ILogger<MicrosoftDownloadClient>>()));
        services.AddSingleton<IMediaReader, EngineMediaReader>();
        services.AddSingleton<ILeftoverService, EngineLeftoverService>();
        services.AddSingleton<IPreflightService, EnginePreflightService>();
        services.AddSingleton<IBuildRunner, EngineBuildRunner>();
        services.AddSingleton<ILocaleCatalog, SystemLocaleCatalog>();
        services.AddSingleton<ISettingsStore>(sp => new SettingsStore(sp.GetRequiredService<ProfileSerializer>(),
            sp.GetRequiredService<ILogger<SettingsStore>>()));
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IShellService, ShellService>();
        services.AddSingleton<IReadOnlyList<BootKey>>(_ => BootKeyData.Load());

        // Reading the list of disks needs administrator rights. Without them the Review page shows the engine's message.
        services.AddSingleton<IUsbDiskService>(sp =>
            DefaultServices.CreateUsbDiskService(sp.GetRequiredService<ILoggerFactory>()));
        // The only request that does not go to Microsoft: one anonymous question to the GitHub Releases API.
        services.AddSingleton<IUpdateChecker>(sp =>
            new GitHubUpdateChecker(logger: sp.GetRequiredService<ILogger<GitHubUpdateChecker>>()));
    }

    /// <summary>The view models, the views and the services every mode shares.</summary>
    public static void AddApp(IServiceCollection services)
    {
        services.AddSingleton(_ => CatalogLoader.LoadEmbedded());
        services.AddSingleton(_ => InstallSuggestions.LoadEmbedded());
        services.AddSingleton<ProfileSerializer>();
        services.AddSingleton<IUiDispatcher, WpfDispatcher>();
        services.AddSingleton<IThemeSwitcher, ThemeSwitcher>();
        services.AddSingleton<WizardSession>();
        services.AddSingleton<WizardNavigator>();
        services.AddSingleton<IWizardNavigator>(sp => sp.GetRequiredService<WizardNavigator>());
        services.AddSingleton<ProfileFiles>();
        services.AddSingleton<WelcomeViewModel>();
        services.AddSingleton<WindowsViewModel>();
        services.AddSingleton<CustomizeViewModel>();
        services.AddSingleton<ReviewViewModel>();
        services.AddSingleton<BuildViewModel>();
        services.AddSingleton<DoneViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
    }

    public static void AddLogging(IServiceCollection services, string? appLogPath)
    {
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            if (appLogPath is not null) builder.AddProvider(new FileLoggerProvider(appLogPath));
        });
    }
}