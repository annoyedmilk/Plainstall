using Plainstall.App.Rendering;
using Plainstall.App.Services;
using Plainstall.App.ViewModels;
using Plainstall.Core.Catalog;
using Plainstall.Core.Pipeline;
using Plainstall.Core.Profiles;

namespace Plainstall.App.Tests;

/// <summary>Runs posted work at once, so view models behave deterministically in tests.</summary>
internal sealed class ImmediateDispatcher : IUiDispatcher
{
    public void Post(Action action)
    {
        action();
    }
}

internal sealed class RecordingThemeSwitcher : IThemeSwitcher
{
    public List<ThemeChoice> Applied { get; } = [];

    public void Apply(ThemeChoice choice)
    {
        Applied.Add(choice);
    }
}

/// <summary>A build that waits until the test finishes it, and keeps what it was given.</summary>
internal sealed class ControlledBuildRunner : IBuildRunner
{
    private readonly TaskCompletionSource<BuildOutcome> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public BuildStart? Start { get; private set; }
    public IProgress<BuildProgress>? Progress { get; private set; }
    public IProgress<string>? Log { get; private set; }
    public CancellationToken Token { get; private set; }
    public int Runs { get; private set; }

    public Task<BuildOutcome> RunAsync(BuildStart start, IProgress<BuildProgress> progress, IProgress<string> log,
        CancellationToken cancellationToken)
    {
        Runs++;
        Start = start;
        Progress = progress;
        Log = log;
        Token = cancellationToken;
        cancellationToken.Register(() => _done.TrySetResult(SampleOutcomes.Cancelled(start.Request.Profile)));
        return _done.Task;
    }

    public void Finish(BuildOutcome outcome)
    {
        _done.TrySetResult(outcome);
    }
}

/// <summary>The whole wizard wired with fakes: no WPF, no network, no disk, no administrator rights.</summary>
internal sealed class TestWizard
{
    public TestWizard(bool elevated = true, Catalog? catalog = null)
    {
        Catalog = catalog ?? CatalogLoader.LoadEmbedded();
        Session = new WizardSession(Catalog, InstallSuggestions.LoadEmbedded()) { WorkFolder = @"D:\Plainstall-work" };
        Host = new SampleHostInfo(elevated);
        Files = new ProfileFiles(Dialogs, Settings, Host, new ProfileSerializer());
        Welcome = new WelcomeViewModel(Session, Files, Settings, Leftovers);
        Windows = new WindowsViewModel(Session, Microsoft, Media, Dialogs, Settings, Host);
        Customize = new CustomizeViewModel(Session, Files, new SampleLocales(), Dialogs, Dispatcher);
        Review = new ReviewViewModel(Session, Preflight, Usb, Dialogs, Host, Navigator);
        Build = new BuildViewModel(Session, Runner, Dispatcher, Settings);
        Done = new DoneViewModel(Session, Shell, Files, Navigator, BootKeyData.Load());
        Main = new MainViewModel(Session, Navigator, Welcome, Windows, Customize, Review, Build, Done, Host, Updates,
            Settings, Theme, Shell);
    }

    public Catalog Catalog { get; }
    public WizardSession Session { get; }
    public SampleHostInfo Host { get; }
    public ImmediateDispatcher Dispatcher { get; } = new();
    public SampleDialogs Dialogs { get; } = new();
    public MemorySettingsStore Settings { get; } = new();
    public SampleLeftovers Leftovers { get; } = new();
    public SampleDownloadClient Microsoft { get; } = new();
    public SampleMediaReader Media { get; } = new();
    public SamplePreflight Preflight { get; } = new();
    public SampleUsbDisks Usb { get; } = new();
    public SampleShell Shell { get; } = new();
    public SampleUpdateChecker Updates { get; } = new();
    public ControlledBuildRunner Runner { get; } = new();
    public RecordingThemeSwitcher Theme { get; } = new();
    public WizardNavigator Navigator { get; } = new();
    public ProfileFiles Files { get; }

    public WelcomeViewModel Welcome { get; }
    public WindowsViewModel Windows { get; }
    public CustomizeViewModel Customize { get; }
    public ReviewViewModel Review { get; }
    public BuildViewModel Build { get; }
    public DoneViewModel Done { get; }
    public MainViewModel Main { get; }

    public Profile Profile => Session.Profile;

    public CatalogEntry Entry(string id)
    {
        return Catalog.FindEntry(id) ?? throw new ArgumentException(id);
    }

    public Profile Preset(string id)
    {
        return ProfileFactory.FromPreset(Catalog.FindPreset(id)!);
    }
}

internal static class TestCatalogs
{
    /// <summary>A small catalog with a conflict and a requirement, which the real catalog does not have.</summary>
    public static Catalog WithConflict()
    {
        return CatalogLoader.FromJson(
            """
            {
              "catalogVersion": 1, "kind": "apps",
              "categories": [ { "id": "promo", "title": "Promotions" } ],
              "protectedPackages": [ "Contoso.Store" ],
              "entries": [
                { "id": "app-a", "title": "App A", "description": "An app.", "category": "promo", "risk": "safe", "defaultIn": [],
                  "reinstall": { "method": "store" }, "actions": [ { "kind": "appx.removeProvisioned", "package": "Contoso.AppA" } ] }
              ]
            }
            """,
            """
            {
              "catalogVersion": 1, "kind": "tweaks",
              "categories": [ { "id": "look", "title": "Look" } ],
              "entries": [
                { "id": "dark-start", "title": "Dark start", "description": "A.", "category": "look", "risk": "safe", "defaultIn": [], "conflicts": [ "light-start" ],
                  "actions": [ { "kind": "registry.set", "hive": "machineSoftware", "key": "Software\\Contoso", "name": "Dark", "type": "dword", "data": 1 } ] },
                { "id": "light-start", "title": "Light start", "description": "B.", "category": "look", "risk": "safe", "defaultIn": [ "recommended" ],
                  "actions": [ { "kind": "registry.set", "hive": "machineSoftware", "key": "Software\\Contoso", "name": "Light", "type": "dword", "data": 1 } ] },
                { "id": "dark-icons", "title": "Dark icons", "description": "C.", "category": "look", "risk": "moderate", "defaultIn": [], "requires": [ "dark-start" ],
                  "actions": [ { "kind": "registry.set", "hive": "machineSoftware", "key": "Software\\Contoso", "name": "Icons", "type": "dword", "data": 1 } ] }
              ]
            }
            """,
            """
            {
              "catalogVersion": 1,
              "presets": [
                { "id": "recommended", "title": "Recommended", "tagline": "T.", "description": "D.", "isDefault": true,
                  "setup": { "localAccount": true, "skipPrivacyPages": true, "bypassHardwareChecks": true } }
              ]
            }
            """);
    }
}