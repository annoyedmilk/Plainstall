using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Plainstall.App.Composition;
using Plainstall.App.Resources;
using Plainstall.App.Services;
using Plainstall.App.ViewModels;
using Plainstall.App.Views;
using Plainstall.Core.Catalog;
using Plainstall.Core.Download;
using Plainstall.Core.Output.Usb;
using Plainstall.Core.Profiles;
using Plainstall.Core.Release;

namespace Plainstall.App.Rendering;

/// <summary>
///     <c>Plainstall.App.exe --render-pages &lt;folder&gt;</c>: opens the main window off-screen with sample
///     services (no network, no disk, no administrator rights), walks every step and every Customize tab in
///     light and dark, and saves each state as a PNG of 1280 x 720. Used to review the pages without clicking.
/// </summary>
internal static class PageRenderer
{
    private const int Width = 1280;
    private const int Height = 720;

    public static async Task<int> RenderAllAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var index = new StringBuilder();
        foreach (var dark in new[] { false, true })
        {
            await RenderMainSetAsync(folder, dark, index);
            await RenderNotElevatedAsync(folder, dark, index);
            await RenderReadmeSetAsync(folder, dark, index);
        }

        await RenderScaledAsync(folder, index);
        await File.WriteAllTextAsync(Path.Combine(folder, "index.txt"), index.ToString());
        return 0;
    }

    private static async Task RenderMainSetAsync(string folder, bool dark, StringBuilder index)
    {
        var theme = dark ? "dark" : "light";
        await using var h = await Harness.CreateAsync(true, dark);
        var main = h.Main;
        var session = h.Get<WizardSession>();

        // 1. Welcome
        await h.ShotAsync(folder, $"{theme}-01-welcome",
            "Welcome: wordmark, the four preset cards (Recommended selected), open/last profile, update notice, the notice about an interrupted build and the size of the stored files.",
            index);
        main.DismissUpdateCommand.Execute(null);
        main.Welcome.Presets.First(p => p.Preset.Id == "privacy-max").IsSelected = true;
        main.Welcome.UseLastProfileCommand.Execute(null);
        await h.SettleAsync();
        await h.ShotAsync(folder, $"{theme}-02-welcome-profile",
            "Welcome after choosing the last used profile: the Gaming card is selected and the status line names the profile.",
            index);
        main.Welcome.Presets.First(p => p.Preset.Id == "recommended").IsSelected = true;
        await main.Welcome.CleanUpCommand.ExecuteAsync(null);

        // 2. Windows
        await h.GoAsync(WizardStep.Windows);
        await h.ShotAsync(folder, $"{theme}-03-windows-download",
            "Windows, 'Download for me': Microsoft's language list loaded (sample), the PC's language preselected, edition Pro.",
            index);
        h.Get<SampleDownloadClient>().Fail = true;
        await main.Windows.LoadLanguagesCommand.ExecuteAsync(null);
        await h.ShotAsync(folder, $"{theme}-04-windows-download-error",
            "Error state: the language list could not be loaded; the page offers 'Try again' and 'Use my ISO instead'.",
            index);
        h.Get<SampleDownloadClient>().Fail = false;
        await main.Windows.LoadLanguagesCommand.ExecuteAsync(null);
        main.Windows.UseOwnIso = true;
        await main.Windows.BrowseIsoCommand.ExecuteAsync(null);
        await h.ShotAsync(folder, $"{theme}-05-windows-own-iso",
            "Windows, 'Use my ISO': the picked file was read (sample) and shows version, language and editions; Pro is preselected.",
            index);
        main.Windows.UseDownload = true;
        await h.SettleAsync();

        // 3. Customize
        await h.GoAsync(WizardStep.Customize);
        var customize = main.Customize;
        customize.SelectedTab = CustomizeTab.Apps;
        var teamsSwitch = customize.Tweaks.Entries.First(e => e.Id == "no-teams-autoinstall");
        teamsSwitch.IsOn = false;
        customize.Apps.Entries.First(e => e.Id == "edge").IsOn = true;
        customize.Apps.Entries.First(e => e.Id == "copilot-app").IsExpanded = true;
        await h.ShotAsync(folder, $"{theme}-06-customize-apps",
            "Customize, Apps: switches by category with risk badges and details buttons; the notice explains that Teams is kept because its requirement was switched off.",
            index);
        customize.DismissMessagesCommand.Execute(null);
        customize.SelectedTab = CustomizeTab.Tweaks;
        customize.Tweaks.SearchText = "taskbar";
        customize.Tweaks.Entries.First(e => e.Id == "taskbar-end-task").IsExpanded = true;
        await h.ShotAsync(folder, $"{theme}-07-customize-tweaks-search",
            "Customize, Tweaks: search for 'taskbar', one entry expanded with details, Windows build range and source credit.",
            index);
        customize.Tweaks.SearchText = "";
        customize.Tweaks.ChangedOnly = true;
        await h.ShotAsync(folder, $"{theme}-08-customize-tweaks-changed",
            "Customize, Tweaks with the 'Only changed from the preset' filter.", index);
        customize.Tweaks.ChangedOnly = false;
        customize.SelectedTab = CustomizeTab.Setup;
        var setup = customize.Setup;
        setup.AccountName = "Anna";
        setup.AskPassword = true;
        setup.ComputerName = "ANNA-LAPTOP";
        setup.RegionMode = setup.RegionModes.First(m => m.Mode == ChoiceMode.Value);
        setup.RegionValue = setup.Regions.First(r => r.Tag == "de-CH");
        await h.ShotAsync(folder, $"{theme}-09-customize-setup",
            "Customize, Setup: local account 'Anna' with a password asked at build time, computer name, region chosen, privacy and hardware switches.",
            index);
        setup.ComputerName = "12345";
        await h.ShotAsync(folder, $"{theme}-10-customize-setup-error",
            "Error state: an invalid computer name; the message is shown under the field and Next is blocked with the reason.",
            index);
        setup.ComputerName = "ANNA-LAPTOP";
        customize.SelectedTab = CustomizeTab.Advanced;
        var advanced = customize.Advanced;
        advanced.InstallGroups.SelectMany(g => g.Items).First(i => i.WingetId == "VideoLAN.VLC").IsSelected = true;
        advanced.InstallGroups.SelectMany(g => g.Items).First(i => i.WingetId == "7zip.7zip").IsSelected = true;
        advanced.NewWingetId = "Mozilla.Thunderbird.de";
        advanced.AddWingetIdCommand.Execute(null);
        advanced.AddDriverFolderCommand.Execute(null);
        advanced.AddUpdatesCommand.Execute(null);
        await h.ShotAsync(folder, $"{theme}-11-customize-advanced",
            "Customize, Advanced: winget installs with one extra id, browser suggestion because Edge is removed, a driver folder and an update package.",
            index);
        advanced.ScanHealth = true;
        await h.ScrollIntoViewAsync<TextBlock>(t => t.Text == Strings.Advanced_ScanHealthText);
        await h.ShotAsync(folder, $"{theme}-11b-customize-advanced-checks",
            "Customize, Advanced scrolled down: ResetBase and the image check (ScanHealth), which is switched on.",
            index);

        // 4. Review
        await h.GoAsync(WizardStep.Review);
        await h.ShotAsync(folder, $"{theme}-12-review-usb-list",
            "Review: plain summary from the planner, USB sticks listed (an ineligible USB hard drive greyed with its reason), space and time.",
            index);
        main.Review.SelectedUsb = main.Review.UsbCandidates.First(c => c.IsEligible);
        await h.ShotAsync(folder, $"{theme}-13-review-confirm-erase",
            "Review, confirm step: a stick is chosen and must be confirmed with a checkbox that names it and says everything on it is erased.",
            index);
        main.Review.EraseConfirmed = true;
        main.Review.ChooseIsoOutputCommand.Execute(null);
        await h.ShotAsync(folder, $"{theme}-14-review-ready",
            "Review, ready: stick confirmed and an ISO file chosen; 'Start build' is enabled.", index);

        // 5. Build
        _ = main.NextCommand.ExecuteAsync(null);
        await h.SettleAsync();
        await h.ShotAsync(folder, $"{theme}-15-build-password",
            "Build, before it starts: the password prompt with the warning that it is stored on the stick in a form that can be decoded.",
            index);
        _ = main.Build.SubmitPassword("sample", "sample");
        main.Build.IsLogExpanded = true;
        await h.ShotAsync(folder, $"{theme}-16-build-running",
            "Build in progress (sample): stage 5 of 10 highlighted, current action, 42 percent, the log expanded, Cancel.",
            index);
        main.Build.CancelCommand.Execute(null);
        await h.ShotAsync(folder, $"{theme}-17-build-stopping",
            "Build after Cancel: 'Plainstall stops after the current step' while the current step finishes.", index);
        await Task.Delay(2000);
        await h.SettleAsync();
        await h.ShotAsync(folder, $"{theme}-18-done-cancelled",
            "Done after a cancel: a neutral message, no error, the way back to Review.", index);

        // 6. Done
        main.Done.Show(SampleOutcomes.Succeeded(session.Profile), "SanDisk Ultra");
        await h.GoAsync(WizardStep.Done);
        await h.ShotAsync(folder, $"{theme}-19-done-success",
            "Done, success: where the results are, the usual boot-menu keys by brand, a first-start checklist, log, report folder and save profile.",
            index);
        main.Done.Show(SampleOutcomes.Failed(session.Profile), null);
        await h.SettleAsync();
        await h.ShotAsync(folder, $"{theme}-20-done-failure",
            "Done, failure: what went wrong (the build report's error), log and back to Review.", index);

        // Planner errors and too little space on Review.
        session.LoadProfile(session.Profile with
        {
            Apps = session.Profile.Apps with { Remove = ["Microsoft.WindowsStore"] }
        });
        h.Get<SamplePreflight>().LowSpace = true;
        await h.GoAsync(WizardStep.Review);
        await h.ShotAsync(folder, $"{theme}-21-review-errors",
            "Error state on Review: the planner refuses a protected package and the work drive is too small; 'Back to Customize' fixes it.",
            index);
    }

    private static async Task RenderNotElevatedAsync(string folder, bool dark, StringBuilder index)
    {
        var theme = dark ? "dark" : "light";
        await using var h = await Harness.CreateAsync(false, dark);
        h.Main.DismissUpdateCommand.Execute(null);
        h.Get<SampleLeftovers>().Leftovers = false;
        h.Main.Welcome.RefreshLeftovers();
        await h.ShotAsync(folder, $"{theme}-22-not-elevated-welcome",
            "Started without administrator rights: the banner on top explains it.", index);
        await h.GoAsync(WizardStep.Review);
        await h.ShotAsync(folder, $"{theme}-23-not-elevated-review",
            "Review when not elevated: 'Start build' is disabled and the footer says why.", index);
    }

    /// <summary>
    ///     The pictures for the README: what a first start looks like, with no update notice, no earlier
    ///     build to clean up and no last profile, and the Customize page before anything was changed.
    /// </summary>
    private static async Task RenderReadmeSetAsync(string folder, bool dark, StringBuilder index)
    {
        var theme = dark ? "dark" : "light";
        await using var h = await Harness.CreateAsync(true, dark, withHistory: false);
        h.Main.DismissUpdateCommand.Execute(null);
        await h.ShotAsync(folder, $"readme-welcome-{theme}",
            "For the README: the Welcome page on a first start, nothing to clean up and no last profile.", index);
        await h.GoAsync(WizardStep.Customize);
        h.Main.Customize.SelectedTab = CustomizeTab.Apps;
        await h.ShotAsync(folder, $"readme-customize-{theme}",
            "For the README: Customize, Apps, as the Recommended preset sets it.", index);
    }

    private static async Task RenderScaledAsync(string folder, StringBuilder index)
    {
        await using var h = await Harness.CreateAsync(true, false, 1.5);
        h.Main.DismissUpdateCommand.Execute(null);
        await h.ShotAsync(folder, "scale150-01-welcome",
            "The Welcome page at 150 % scaling (1920 x 1080 pixels, the same 1280 x 720 layout).", index);
        await h.GoAsync(WizardStep.Customize);
        await h.ShotAsync(folder, "scale150-02-customize-apps", "Customize, Apps at 150 % scaling.", index);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(ServiceProvider services, MainWindow window, double scale)
        {
            Services = services;
            Window = window;
            Scale = scale;
        }

        private ServiceProvider Services { get; }
        private MainWindow Window { get; }
        private double Scale { get; }
        public MainViewModel Main => Window.ViewModel;

        public async ValueTask DisposeAsync()
        {
            Window.Close();
            await Services.DisposeAsync();
        }

        public T Get<T>() where T : notnull
        {
            return Services.GetRequiredService<T>();
        }

        /// <param name="elevated">Whether the sample PC runs the app with administrator rights.</param>
        /// <param name="dark">True for the dark theme.</param>
        /// <param name="scale">Display scaling, 1.0 for 100 %.</param>
        /// <param name="withHistory">
        ///     True: an earlier build left something to clean up and a last profile exists, so the Welcome page
        ///     shows those notices. False: a first start.
        /// </param>
        public static async Task<Harness> CreateAsync(bool elevated, bool dark, double scale = 1.0,
            bool withHistory = true)
        {
            var services = new ServiceCollection();
            AppComposition.AddLogging(services, null);
            services.AddSingleton<IHostInfo>(new SampleHostInfo(elevated));
            Add<IMicrosoftDownloadClient, SampleDownloadClient>(services);
            Add<IMediaReader, SampleMediaReader>(services);
            Add<ILeftoverService, SampleLeftovers>(services);
            Add<IPreflightService, SamplePreflight>(services);
            Add<ILocaleCatalog, SampleLocales>(services);
            Add<ISettingsStore, MemorySettingsStore>(services);
            Add<IDialogService, SampleDialogs>(services);
            Add<IShellService, SampleShell>(services);
            Add<IUsbDiskService, SampleUsbDisks>(services);
            Add<IUpdateChecker, SampleUpdateChecker>(services);
            Add<IBuildRunner, SampleBuildRunner>(services);
            services.AddSingleton<IReadOnlyList<BootKey>>(_ => BootKeyData.Load());
            AppComposition.AddApp(services);
            var provider = services.BuildServiceProvider();

            if (withHistory)
            {
                var catalog = provider.GetRequiredService<Catalog>();
                var leftovers = provider.GetRequiredService<SampleLeftovers>();
                leftovers.Leftovers = true;
                leftovers.StoredBytes = 7_412_000_000;
                provider.GetRequiredService<MemorySettingsStore>()
                    .SaveLastProfile(ProfileFactory.FromPreset(catalog.FindPreset("gaming")!, "Gaming PC"));
            }

            var window = provider.GetRequiredService<MainWindow>();
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000;
            window.Top = -20000;
            window.Width = Width;
            window.Height = Height;
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            var harness = new Harness(provider, window, scale);
            await harness.Main.InitializeAsync();
            harness.Main.SelectedTheme =
                harness.Main.ThemeOptions.First(t => t.Choice == (dark ? ThemeChoice.Dark : ThemeChoice.Light));
            window.Show();
            await harness.SettleAsync();
            return harness;
        }

        private static void Add<TService, TImpl>(IServiceCollection services)
            where TService : class where TImpl : class, TService
        {
            services.AddSingleton<TImpl>();
            services.AddSingleton<TService>(sp => sp.GetRequiredService<TImpl>());
        }

        /// <summary>Lets bindings, layout and the sample services' continuations finish.</summary>
        public async Task SettleAsync()
        {
            for (var i = 0; i < 4; i++)
            {
                await Task.Delay(60);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            }

            Window.UpdateLayout();
        }

        /// <summary>Scrolls the first visible element of type <typeparamref name="T" /> that matches into view.</summary>
        public async Task ScrollIntoViewAsync<T>(Func<T, bool> match) where T : FrameworkElement
        {
            Descendants(Window).OfType<T>().First(e => e.IsVisible && match(e)).BringIntoView();
            await SettleAsync();
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            var pending = new Stack<DependencyObject>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                yield return current;
                for (var i = VisualTreeHelper.GetChildrenCount(current) - 1; i >= 0; i--)
                    pending.Push(VisualTreeHelper.GetChild(current, i));
            }
        }

        public async Task GoAsync(WizardStep step)
        {
            Main.GoTo(step);
            await SettleAsync();
        }

        public async Task ShotAsync(string folder, string name, string description, StringBuilder index)
        {
            // Written before each capture, so a run that stops half-way shows where.
            await File.AppendAllTextAsync(Path.Combine(folder, "render-progress.txt"), name + Environment.NewLine);
            await SettleAsync();
            var pixelsWide = (int)Math.Round(Width * Scale);
            var pixelsHigh = (int)Math.Round(Height * Scale);
            var bitmap = new RenderTargetBitmap(pixelsWide, pixelsHigh, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
            var background = new DrawingVisual();
            using (var context = background.RenderOpen())
            {
                context.DrawRectangle(Window.Background, null, new Rect(0, 0, Width, Height));
            }

            // The page is laid out at exactly 1280 x 720 here, not at the size the window got: Windows keeps a
            // window within the screen, so on a small screen (such as a build server's) the window is narrower.
            var page = (FrameworkElement)Window.Content;
            page.Measure(new Size(Width, Height));
            page.Arrange(new Rect(0, 0, Width, Height));
            bitmap.Render(background);
            bitmap.Render(page);
            page.InvalidateMeasure();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var file = name + ".png";
            await using (var stream = File.Create(Path.Combine(folder, file)))
            {
                encoder.Save(stream);
            }

            index.AppendLine(file + "  " + description);
        }
    }
}