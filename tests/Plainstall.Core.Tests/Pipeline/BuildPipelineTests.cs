using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Plainstall.Core.Catalog;
using Plainstall.Core.Imaging;
using Plainstall.Core.Media;
using Plainstall.Core.Output;
using Plainstall.Core.Output.Usb;
using Plainstall.Core.Pipeline;
using Plainstall.Core.Profiles;
using Plainstall.Core.Registry;
using Plainstall.Core.Tests.Imaging;
using Plainstall.Core.Tests.Imaging.Drivers;
using Plainstall.Core.Tests.Media;
using Plainstall.Core.Tests.Output;
using Plainstall.Core.Tests.Output.Usb;
using Plainstall.Core.Unattend;

namespace Plainstall.Core.Tests.Pipeline;

/// <summary>
///     Runs the whole pipeline with every outside dependency replaced: a folder of synthetic media, a fake
///     DISM, registry "hives" under HKCU and a fake oscdimg. What is real: the planner, the executors, the
///     answer file, the journal, the stage order and the cleanup.
/// </summary>
public sealed class BuildPipelineTests : IDisposable
{
    private static readonly HostEnvironment DefaultHost = new()
    {
        IsElevated = true,
        WorkDriveFormat = "NTFS",
        WorkDriveFreeBytes = 500L << 30,
        WorkDriveIsNetwork = false,
        WindowsDirectory = @"C:\Windows",
        ProgramFilesDirectories = [@"C:\Program Files", @"C:\Program Files (x86)"],
        UserProfileRoot = @"C:\Users"
    };

    private readonly FakeHives _hives = new();
    private readonly List<FakeMountedImage> _images = [];
    private readonly FakeImagingService _imaging = new() { HoldsWimWhileMounted = true };
    private readonly RecordingLogger _log = new();
    private readonly string _mediaFolder;
    private readonly List<BuildProgress> _progress = [];
    private readonly TempDirectory _temp = new();
    private readonly FakeWimTool _wimTool = new();
    private Action<FakeMountedImage>? _onMount;
    private FakeUsbWriter? _usbWriter;

    public BuildPipelineTests()
    {
        _mediaFolder = _temp.File("media-source");
        SyntheticWim.Install26H2().WriteTo(Path.Combine(_mediaFolder, "sources", "install.wim"));
        SyntheticWim.Boot26H2().WriteTo(Path.Combine(_mediaFolder, "sources", "boot.wim"));
        foreach (var file in new[]
                 {
                     "setup.exe", "bootmgr", @"boot\etfsboot.com", @"efi\microsoft\boot\efisys.bin",
                     @"sources\setup.exe"
                 })
        {
            var path = Path.Combine(_mediaFolder, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file);
            File.SetAttributes(path, FileAttributes.ReadOnly);
        }

        _imaging.OnMount = (wim, _, mountPath) =>
        {
            var image = new FakeMountedImage(mountPath);
            if (Path.GetFileName(wim).StartsWith("install", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var app in new[]
                         {
                             "Clipchamp.Clipchamp", "Microsoft.BingNews", "Microsoft.OutlookForWindows", "MSTeams",
                             "Microsoft.WindowsStore", "Microsoft.Paint", "Microsoft.GetHelp"
                         }) image.AddApp(app);
                image.Features.Add(new ComponentInfo("Recall", ComponentState.Installed));
                Directory.CreateDirectory(Path.Combine(mountPath, "Windows", "System32"));
                File.WriteAllText(Path.Combine(mountPath, "Windows", "System32", "OneDriveSetup.exe"), "x");
            }

            _onMount?.Invoke(image);
            _images.Add(image);
            return image;
        };
    }

    private string WorkFolder => _temp.File("work");
    private string IsoPath => _temp.File(@"out\plainstall.iso");

    public void Dispose()
    {
        _hives.Dispose();
        foreach (var file in Directory.Exists(_mediaFolder)
                     ? Directory.EnumerateFiles(_mediaFolder, "*", SearchOption.AllDirectories)
                     : []) File.SetAttributes(file, FileAttributes.Normal);
        _temp.Dispose();
    }

    /// <param name="host">The facts of every drive. Default: <see cref="DefaultHost" />.</param>
    /// <param name="source">Where the media comes from. Default: the synthetic media folder.</param>
    /// <param name="oscdimg">Gives the ISO tool. Default: a fake tool that the fake runner "runs".</param>
    /// <param name="hosts">The facts of the drive of each folder. Replaces <paramref name="host" /> when given.</param>
    private BuildPipeline Pipeline(HostEnvironment? host = null, ISourceProvider? source = null,
        IOscdimgProvider? oscdimg = null, Func<string, HostEnvironment>? hosts = null)
    {
        return new BuildPipeline(new PipelineServices
        {
            Catalog = CatalogLoader.LoadEmbedded(),
            Source = source ?? new GivenSource(_mediaFolder),
            MediaOpener = new MediaOpener(),
            ImagingFactory = _ => _imaging,
            WimToolFactory = _ => _wimTool,
            Hives = _hives,
            Oscdimg = oscdimg ?? new FakeOscdimg(_temp.File("oscdimg.exe")),
            ProcessRunner = new FakeOscdimgRunner(),
            UsbWriterFactory = _usbWriter is null ? null : (_, _) => _usbWriter,
            HostFacts = () => new HostFacts("en-US", "de-CH", "0807:00000807", "W. Europe Standard Time"),
            HostEnvironment = hosts ?? (_ => host ?? DefaultHost)
        }, _log);
    }

    private BuildRequest Request(Profile? profile = null, bool keepWork = true, string? password = null,
        bool iso = true)
    {
        return new BuildRequest
        {
            Profile = profile ?? ProfileFactory.FromPreset(CatalogLoader.LoadEmbedded().DefaultPreset),
            WorkDirectory = WorkFolder,
            IsoOutputPath = iso ? IsoPath : null,
            KeepWorkFolder = keepWork,
            AccountPassword = password,
            AppVersion = "0.0.0-test"
        };
    }

    private Task<BuildResult> RunAsync(BuildRequest request, CancellationToken token, HostEnvironment? host = null)
    {
        return Pipeline(host).RunAsync(request, new Sync(_progress.Add), token);
    }

    [Fact]
    public async Task A_recommended_build_runs_every_stage_and_leaves_a_clean_journal()
    {
        var (report, reportPath, isoPath) = await RunAsync(Request(), TestContext.Current.CancellationToken);

        Assert.True(report.Result == "succeeded",
            report.Error + string.Join("; ",
                report.Actions.Where(a => a.Result == "failed").Select(a => $"{a.Kind} {a.Target}: {a.Reason}")));
        Assert.Equal(Enumerable.Range(1, 10), _progress.Select(p => p.StageNumber).Distinct());

        // Source facts in the report.
        Assert.Equal("Professional", report.Source!.Edition);
        Assert.Equal("10.0.26300.9457", report.Source.Build);
        Assert.Equal("x64", report.Source.Arch);

        // The edition was exported alone, serviced, and exported again.
        Assert.Equal(6, _wimTool.Exports[0].SourceIndex);
        Assert.Equal(1, _wimTool.Exports[1].SourceIndex);
        var layout = new WorkLayout(WorkFolder);
        var final = WimMetadataReader.Read(Path.Combine(layout.MediaSources, "install.wim"));
        Assert.Equal("Professional", Assert.Single(final.Images).EditionId);
        Assert.False(File.Exists(layout.WorkingWim));

        // Apps: removed, kept, and never the Store.
        var install = _images[0];
        var left = install.Packages.Select(p => p.DisplayName).ToList();
        Assert.DoesNotContain("Clipchamp.Clipchamp", left);
        Assert.DoesNotContain("Microsoft.OutlookForWindows", left);
        Assert.Contains("Microsoft.WindowsStore", left);
        Assert.Contains("Microsoft.Paint", left);
        Assert.Contains("Microsoft.GetHelp", left);
        // OneDrive is stopped by its Run value. Its installer is a system file and stays, so sfc finds nothing.
        Assert.True(File.Exists(Path.Combine(layout.InstallMount, "Windows", "System32", "OneDriveSetup.exe")));
        Assert.Contains("cleanup", install.Calls);
        Assert.DoesNotContain("cleanup resetbase", install.Calls);
        Assert.DoesNotContain("scanhealth", install.Calls);

        // Registry: installed system, default user, and the Setup image for the hardware bypass.
        Assert.Equal(1,
            _hives.Read(layout.InstallMount, RegistryHiveId.MachineSoftware, @"Microsoft\Windows\CurrentVersion\OOBE",
                "BypassNRO"));
        Assert.Equal(1,
            _hives.Read(layout.InstallMount, RegistryHiveId.MachineSystem, @"ControlSet001\Control\FileSystem",
                "LongPathsEnabled"));
        Assert.Equal(0,
            _hives.Read(layout.InstallMount, RegistryHiveId.DefaultUser,
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideFileExt"));
        Assert.Equal(1,
            _hives.Read(layout.BootMount, RegistryHiveId.SetupSystem, @"Setup\LabConfig", "BypassTPMCheck"));
        Assert.Null(
            _hives.Read(layout.InstallMount, RegistryHiveId.MachineSystem, @"Setup\LabConfig", "BypassTPMCheck"));

        // First-logon scripts are in the image and every new account is set to run the user script once.
        Assert.True(File.Exists(Path.Combine(layout.InstallMount, FirstLogonScriptGenerator.UserScriptImagePath)));
        Assert.NotNull(_hives.Read(layout.InstallMount, RegistryHiveId.DefaultUser,
            FirstLogonScriptGenerator.RunOnceKey, FirstLogonScriptGenerator.RunOnceValueName));

        // Media: answer file without a product key or disk choice, edition file, untouched boot loader files.
        var answer = await File.ReadAllTextAsync(Path.Combine(layout.Media, "autounattend.xml"),
            TestContext.Current.CancellationToken);
        Assert.Contains("/IMAGE/INDEX", answer);
        Assert.DoesNotContain("ProductKey", answer);
        Assert.DoesNotContain("DiskConfiguration", answer);
        Assert.Contains("HideOnlineAccountScreens", answer);
        Assert.Contains("Professional",
            await File.ReadAllTextAsync(Path.Combine(layout.MediaSources, "ei.cfg"),
                TestContext.Current.CancellationToken));
        Assert.False(File.GetAttributes(Path.Combine(layout.Media, "setup.exe")).HasFlag(FileAttributes.ReadOnly));

        // Both images were committed, nothing was discarded, nothing is left open.
        Assert.Equal([layout.InstallMount, layout.BootMount], _imaging.Committed);
        Assert.Empty(_imaging.Discarded);
        Assert.Empty(_imaging.Mounted);
        Assert.True(BuildJournal.TryLoad(layout)!.IsClean);
        Assert.Equal(0, _hives.OpenCount);
        Assert.All(_images, i => Assert.True(i.Disposed));

        // Output and report.
        Assert.True(File.Exists(IsoPath));
        Assert.Equal(IsoPath, isoPath);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(IsoPath)!, BuildReport.FileName), reportPath);
        var saved = BuildReport.FromJson(await File.ReadAllTextAsync(reportPath,
            TestContext.Current.CancellationToken));
        Assert.Equal(report.Actions.Count, saved.Actions.Count);
        Assert.Contains(saved.Actions,
            a => a is { Kind: "appx.removeProvisioned", Target: "Clipchamp.Clipchamp", Result: "applied" });
        Assert.Contains(saved.Actions, a => a is { Kind: "appx.removeProvisioned", Result: "skipped" });
        Assert.Equal(64 * 2048, saved.Output!.Iso!.SizeBytes);
    }

    [Fact]
    public async Task The_work_folder_is_removed_after_a_build_unless_asked_to_keep_it()
    {
        var result = await RunAsync(Request(keepWork: false), TestContext.Current.CancellationToken);
        Assert.Equal("succeeded", result.Report.Result);
        Assert.Null(result.ErrorKind);
        Assert.False(Directory.Exists(WorkFolder));
        Assert.True(File.Exists(IsoPath));
    }

    private static Profile WithScanHealth()
    {
        return ProfileFactory.FromPreset(CatalogLoader.LoadEmbedded().DefaultPreset) with
        {
            Verify = new VerifyOptions { ScanHealth = true }
        };
    }

    [Fact]
    public async Task The_health_check_runs_after_the_cleanup_and_before_the_image_is_saved()
    {
        var result = await RunAsync(Request(WithScanHealth()), TestContext.Current.CancellationToken);

        Assert.Equal("succeeded", result.Report.Result);
        var calls = _images[0].Calls;
        Assert.True(calls.IndexOf("scanhealth") > calls.IndexOf("cleanup"));
        Assert.Single(calls, "scanhealth");
        Assert.Contains(result.Report.Actions,
            a => a is { Entry: "verify", Kind: "image.scanHealth", Result: "applied" });
        Assert.Single(_imaging.Committed, new WorkLayout(WorkFolder).InstallMount);
    }

    [Theory]
    [InlineData(ImageHealth.Repairable)]
    [InlineData(ImageHealth.NotRepairable)]
    public async Task Damage_found_by_the_health_check_stops_the_build_and_discards_the_image(ImageHealth health)
    {
        _onMount = image => image.Health = health;

        var result = await RunAsync(Request(WithScanHealth(), false), TestContext.Current.CancellationToken);

        Assert.Equal("failed", result.Report.Result);
        Assert.Equal(ErrorKind.Imaging, result.ErrorKind);
        Assert.Contains("ScanHealth", result.Report.Error);
        Assert.Contains(result.Report.Actions,
            a => a is { Entry: "verify", Kind: "image.scanHealth", Result: "failed" });
        Assert.Equal([new WorkLayout(WorkFolder).InstallMount], _imaging.Discarded);
        Assert.Empty(_imaging.Committed);
        Assert.False(File.Exists(IsoPath));
    }

    [Fact]
    public async Task A_failure_while_servicing_discards_the_mount_and_releases_every_hive()
    {
        _hives.FailOnOpen = true;

        var result = await RunAsync(Request(keepWork: false), TestContext.Current.CancellationToken);

        Assert.Equal("failed", result.Report.Result);
        Assert.Equal(ErrorKind.Registry, result.ErrorKind);
        Assert.Contains("registry", result.Report.Error, StringComparison.OrdinalIgnoreCase);
        var layout = new WorkLayout(WorkFolder);
        Assert.Equal([layout.InstallMount], _imaging.Discarded);
        Assert.Empty(_imaging.Committed);
        Assert.Empty(_imaging.Mounted);
        Assert.False(Directory.Exists(WorkFolder));
        Assert.False(File.Exists(IsoPath));
        Assert.True(File.Exists(result.ReportPath));

        // The reason is in the log before the discard starts, because a discard can take many minutes.
        var reason = _log.Lines.FindIndex(l =>
            l.StartsWith("Error: Changing the Windows image failed", StringComparison.Ordinal));
        var discard = _log.Lines.FindIndex(l =>
            l.StartsWith("Information: Discarding the mounted image", StringComparison.Ordinal));
        Assert.True(reason >= 0 && reason < discard, string.Join(Environment.NewLine, _log.Lines));
    }

    [Fact]
    public async Task A_cancel_request_stops_between_actions_and_never_commits_a_half_serviced_image()
    {
        using var cancel = new CancellationTokenSource();
        Action stop = cancel.Cancel;
        _onMount = image => image.OnFirstCall = stop;

        var result = await RunAsync(Request(keepWork: false), cancel.Token);

        Assert.Equal("cancelled", result.Report.Result);
        Assert.Null(result.ErrorKind);
        Assert.Empty(_imaging.Committed);
        Assert.Single(_imaging.Discarded);
        Assert.Empty(_imaging.Mounted);
        Assert.Equal(0, _hives.OpenCount);
        Assert.False(File.Exists(IsoPath));
        // Cleanup and the report are not cancellable.
        Assert.False(Directory.Exists(WorkFolder));
        Assert.Contains(_progress, p => p.StageNumber == 10);
    }

    [Fact]
    public async Task One_change_that_cannot_be_applied_does_not_stop_the_build_but_is_reported()
    {
        _onMount = image => image.StubbornPackages.Add("Clipchamp.Clipchamp_1.0.0.0_x64__8wekyb3d8bbwe");

        var result = await RunAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal("completed-with-failures", result.Report.Result);
        Assert.Contains(result.Report.Actions, a => a is { Target: "Clipchamp.Clipchamp", Result: "failed" });
        Assert.Contains(result.Report.Warnings, w => w.Contains("could not be applied"));
        Assert.True(File.Exists(IsoPath));
    }

    [Fact]
    public async Task Leftovers_of_an_interrupted_build_block_a_new_one()
    {
        var layout = new WorkLayout(WorkFolder);
        layout.Create();
        BuildJournal.Create(layout).MountStarting(layout.WorkingWim, 1, layout.InstallMount);

        // The pipeline's own cleanup must not run for a journal it did not create.
        var result = await RunAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal("failed", result.Report.Result);
        Assert.Equal(ErrorKind.Preflight, result.ErrorKind);
        Assert.Contains("Plainstall.Cli cleanup", result.Report.Error);
        Assert.Empty(_imaging.Discarded);
        Assert.True(Directory.Exists(WorkFolder));
    }

    [Fact]
    public async Task The_usb_stick_is_written_last_from_the_prepared_files_and_lands_in_the_report()
    {
        var stick = TestDisks.Stick();
        var isoExisted = false;
        var answerFileExisted = false;
        _usbWriter = new FakeUsbWriter
        {
            OnWrite = () =>
            {
                isoExisted = File.Exists(IsoPath);
                answerFileExisted = File.Exists(Path.Combine(new WorkLayout(WorkFolder).Media, "autounattend.xml"));
            }
        };

        var result = await RunAsync(Request() with { UsbDisk = stick }, TestContext.Current.CancellationToken);
        var report = result.Report;

        Assert.True(report.Result == "succeeded", report.Error);
        var request = _usbWriter.Request!;
        Assert.Same(stick, request.ConfirmedDisk);
        Assert.Equal(new WorkLayout(WorkFolder).Media, request.MediaRoot);
        Assert.True(isoExisted);
        Assert.True(answerFileExisted);
        // The work folder, the ISO and the source are named as places that must not be on the stick.
        Assert.Contains(Path.GetPathRoot(WorkFolder)!, request.Context.PathsInUse, StringComparer.OrdinalIgnoreCase);
        Assert.True(request.Context.RequiredBytes > 0);

        Assert.Equal(new ReportUsb("SanDisk Ultra", stick.SerialNumber, stick.SizeBytes, @"F:\", 4_000_000, false),
            report.Output!.Usb);
        Assert.True(report.Output.Split);
        Assert.NotNull(report.Output.Iso);
        Assert.Contains(report.Warnings, w => w.Contains("Safely Remove"));
        Assert.Contains(_progress, p => p is { StageNumber: 9, Action: "Erasing the USB stick" });
        Assert.Contains(_progress,
            p => p is { StageNumber: 9, Action: "Copying the files to the USB stick", Percent: 50 });
    }

    [Fact]
    public async Task A_stick_that_could_not_be_finished_fails_the_build_but_the_iso_stays()
    {
        var stick = TestDisks.Stick();
        _usbWriter = new FakeUsbWriter
        {
            Failure = new UsbStickNotUsableException(ErrorKind.Output,
                "The USB stick is not usable as it is. It can simply be written again.")
        };

        var result = await RunAsync(Request() with { UsbDisk = stick }, TestContext.Current.CancellationToken);

        Assert.Equal("failed", result.Report.Result);
        Assert.Contains("not usable", result.Report.Error);
        Assert.True(File.Exists(IsoPath));
        Assert.NotNull(result.Report.Output!.Iso);
        Assert.Null(result.Report.Output.Usb);
    }

    [Fact]
    public async Task A_disk_that_is_not_a_usb_stick_is_refused_before_anything_is_created()
    {
        _usbWriter = new FakeUsbWriter();
        var hardDrive = TestDisks.Stick() with { IsRemovableMedia = false };

        var result = await RunAsync(Request() with { UsbDisk = hardDrive }, TestContext.Current.CancellationToken);

        Assert.Equal("failed", result.Report.Result);
        Assert.Equal(ErrorKind.Preflight, result.ErrorKind);
        Assert.StartsWith("The build cannot start", result.Report.Error);
        Assert.Contains("Nothing was erased.", result.Report.Error);
        Assert.Null(_usbWriter.Request);
        Assert.False(Directory.Exists(WorkFolder));
        Assert.Empty(_wimTool.Exports);
    }

    [Fact]
    public async Task An_external_drive_the_user_confirmed_as_a_stick_is_written()
    {
        _usbWriter = new FakeUsbWriter();
        var externalStick = TestDisks.Stick() with
        {
            FriendlyName = "General USB Flash Drive", IsRemovableMedia = false
        };

        var result = await RunAsync(Request() with { UsbDisk = externalStick, UsbExternalDriveConfirmed = true },
            TestContext.Current.CancellationToken);

        Assert.True(result.Report.Result == "succeeded", result.Report.Error);
        Assert.Same(externalStick, _usbWriter.Request!.ConfirmedDisk);
        // The writer checks the disk again before it erases it, and must allow the confirmed external drive.
        Assert.True(_usbWriter.Request.Context.AllowExternalDrives);
    }

    [Fact]
    public async Task A_stick_that_holds_the_work_folder_is_refused()
    {
        _usbWriter = new FakeUsbWriter();
        var stick = TestDisks.Stick();
        var onWorkDrive = stick with
        {
            Volumes = [stick.Volumes[0] with { DriveLetter = Path.GetPathRoot(WorkFolder)![..2] }]
        };

        var result = await RunAsync(Request() with { UsbDisk = onWorkDrive }, TestContext.Current.CancellationToken);

        Assert.Equal("failed", result.Report.Result);
        Assert.StartsWith("The build cannot start", result.Report.Error);
        Assert.Null(_usbWriter.Request);
    }

    // ---- updates and drivers ---------------------------------------------------------------------

    private string UpdateFile(string name)
    {
        var path = _temp.File(@"additions\" + name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[10_000];
        "MSCF"u8.CopyTo(bytes);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string DriverFolder(bool withStorageDriver)
    {
        var root = _temp.File(@"additions\drivers");
        SyntheticInf.Write(Path.Combine(root, "network"), "contosonet.inf", SyntheticInf.Text());
        if (withStorageDriver)
            SyntheticInf.Write(Path.Combine(root, "storage"), "contosostor.inf",
                SyntheticInf.Text("SCSIAdapter", "{4D36E97B-E325-11CE-BFC1-08002BE10318}"));
        return root;
    }

    private static Profile MinimalWith(IReadOnlyList<string> updates, string? driverFolder)
    {
        return ProfileFactory.FromPreset(CatalogLoader.LoadEmbedded().FindPreset("minimal")!) with
        {
            Updates = updates,
            Drivers = new DriverOptions { Folders = driverFolder is null ? [] : [driverFolder] }
        };
    }

    [Fact]
    public async Task Updates_and_drivers_go_in_before_anything_is_removed_and_storage_drivers_reach_setup_too()
    {
        var profile = ProfileFactory.FromPreset(CatalogLoader.LoadEmbedded().DefaultPreset) with
        {
            Updates = [UpdateFile("windows11.0-kb5000001-x64.cab")],
            Drivers = new DriverOptions { Folders = [DriverFolder(true)] }
        };

        var hivesOpenAtFirstAdd = new List<int>();
        _onMount = image => image.OnFirstAdd = () => hivesOpenAtFirstAdd.Add(_hives.OpenCount);
        // DISM keeps the registry files of an image open while a servicing session lasts. Then a load of one
        // of them fails with error 32 (seen on a real 26H2 image). Both images are serviced here.
        var openSessionsAtLoad = new List<int>();
        _hives.OnOpen = () => openSessionsAtLoad.Add(_imaging.OpenSessions);

        var result = await RunAsync(Request(profile), TestContext.Current.CancellationToken);
        var report = result.Report;

        Assert.True(report.Result == "succeeded",
            report.Error + string.Join("; ",
                report.Actions.Where(a => a.Result == "failed").Select(a => $"{a.Kind} {a.Target}: {a.Reason}")));

        // Install image: the update, then both drivers, and only then the first removal.
        var install = _images[0].Calls;
        Assert.Equal(
            ["add-package windows11.0-kb5000001-x64.cab", "add-driver contosonet.inf", "add-driver contosostor.inf"],
            install.Where(c => c.StartsWith("add-", StringComparison.Ordinal)));
        Assert.True(install.FindLastIndex(c => c.StartsWith("add-", StringComparison.Ordinal)) <
                    install.FindIndex(c => c.StartsWith("remove-app", StringComparison.Ordinal)));

        // Setup image: the storage driver only.
        Assert.Equal(["add-driver contosostor.inf"],
            _images[1].Calls.Where(c => c.StartsWith("add-", StringComparison.Ordinal)));

        // No registry file was loaded while DISM added to either image, or while a DISM session was open.
        Assert.Equal([0, 0], hivesOpenAtFirstAdd);
        Assert.Equal([0, 0], openSessionsAtLoad);
        // Component cleanup still runs, in a session opened again after the registry step.
        Assert.True(install.FindIndex(c => c.StartsWith("cleanup", StringComparison.Ordinal)) >
                    install.FindLastIndex(c => c.StartsWith("remove-app", StringComparison.Ordinal)));

        Assert.Contains(report.Actions,
            a => a is
            {
                Entry: "updates", Kind: "update.add", Target: "windows11.0-kb5000001-x64.cab", Result: "applied"
            });
        Assert.Equal(2, report.Actions.Count(a => a is { Entry: "drivers", Kind: "driver.add", Result: "applied" }));
        Assert.Single(report.Actions, a => a is { Entry: "drivers", Kind: "driver.addToSetup", Result: "applied" });
    }

    [Fact]
    public async Task Storage_drivers_open_the_setup_image_even_when_nothing_else_changes_there()
    {
        var result = await RunAsync(Request(MinimalWith([], DriverFolder(true))),
            TestContext.Current.CancellationToken);

        Assert.Equal("succeeded", result.Report.Result);
        var layout = new WorkLayout(WorkFolder);
        Assert.Equal([layout.InstallMount, layout.BootMount], _imaging.Committed);
    }

    /// <summary>
    ///     When no storage driver goes into the Setup image, the build gives a warning and continues. DISM keeps
    ///     the mounted WIM file open, so Plainstall must not delete it before the mount is discarded.
    /// </summary>
    [Fact]
    public async Task Storage_drivers_that_setup_refuses_give_a_warning_and_leave_the_setup_image_as_shipped()
    {
        var layout = new WorkLayout(WorkFolder);
        _onMount = image =>
        {
            if (string.Equals(image.MountPath, layout.BootMount, StringComparison.OrdinalIgnoreCase))
                image.RejectedDrivers.Add("contosostor.inf");
        };
        var before = await File.ReadAllBytesAsync(Path.Combine(_mediaFolder, "sources", "boot.wim"),
            TestContext.Current.CancellationToken);

        var result = await RunAsync(Request(MinimalWith([], DriverFolder(true))),
            TestContext.Current.CancellationToken);

        Assert.True(result.Report.Result == "completed-with-failures", result.Report.Error);
        Assert.Null(result.Report.Error);
        Assert.Contains(result.Report.Warnings,
            w => w.StartsWith("None of the storage drivers", StringComparison.Ordinal));
        Assert.Contains(result.Report.Actions, a => a is { Kind: "driver.addToSetup", Result: "failed" });
        Assert.Equal([layout.InstallMount], _imaging.Committed);
        Assert.Equal([layout.BootMount], _imaging.Discarded);
        Assert.Equal(before,
            await File.ReadAllBytesAsync(Path.Combine(layout.MediaSources, "boot.wim"),
                TestContext.Current.CancellationToken));
        Assert.True(File.Exists(IsoPath));
    }

    /// <summary>Without the hardware bypass and without a storage driver, the Setup image is not opened at all.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Without_setup_work_the_setup_image_is_left_alone(bool withNetworkDriver)
    {
        var before = await File.ReadAllBytesAsync(Path.Combine(_mediaFolder, "sources", "boot.wim"),
            TestContext.Current.CancellationToken);

        var result = await RunAsync(Request(MinimalWith([], withNetworkDriver ? DriverFolder(false) : null)),
            TestContext.Current.CancellationToken);

        Assert.Equal("succeeded", result.Report.Result);
        var layout = new WorkLayout(WorkFolder);
        Assert.Equal([layout.InstallMount], _imaging.Committed);
        Assert.Equal(before,
            await File.ReadAllBytesAsync(Path.Combine(layout.MediaSources, "boot.wim"),
                TestContext.Current.CancellationToken));
        Assert.Equal(withNetworkDriver, _images[0].Calls.Contains("add-driver contosonet.inf"));
    }

    [Fact]
    public async Task An_update_that_cannot_be_added_fails_the_build_and_discards_the_image()
    {
        _onMount = image => image.FailingPackages.Add("windows11.0-kb5000001-x64.cab");

        var result = await RunAsync(Request(MinimalWith([UpdateFile("windows11.0-kb5000001-x64.cab")], null), false),
            TestContext.Current.CancellationToken);

        Assert.Equal("failed", result.Report.Result);
        Assert.Contains(result.Report.Actions, a => a is { Kind: "update.add", Result: "failed" });
        Assert.Empty(_imaging.Committed);
        Assert.Single(_imaging.Discarded);
        Assert.False(File.Exists(IsoPath));
    }

    [Fact]
    public async Task A_missing_update_file_or_driver_folder_stops_the_build_before_anything_is_created()
    {
        var profile = MinimalWith([_temp.File(@"additions\gone.msu")], _temp.File(@"additions\no-such-folder"));

        var result = await RunAsync(Request(profile), TestContext.Current.CancellationToken);

        Assert.Equal("failed", result.Report.Result);
        Assert.Equal(ErrorKind.InvalidInput, result.ErrorKind);
        Assert.Contains("gone.msu", result.Report.Error);
        Assert.Contains("no-such-folder", result.Report.Error);
        Assert.False(Directory.Exists(WorkFolder));
        Assert.Empty(_wimTool.Exports);
    }

    [Fact]
    public async Task Preflight_problems_stop_the_build_before_anything_is_created()
    {
        var result = await RunAsync(Request(), TestContext.Current.CancellationToken,
            DefaultHost with { IsElevated = false });

        Assert.Equal("failed", result.Report.Result);
        Assert.Equal(ErrorKind.Preflight, result.ErrorKind);
        Assert.Contains("administrator", result.Report.Error);
        Assert.False(Directory.Exists(WorkFolder));
        Assert.Empty(_wimTool.Exports);
    }

    /// <summary>The download lands on the drive of the work folder, so the space check before it counts the ISO too.</summary>
    [Fact]
    public async Task A_download_to_the_drive_of_the_work_folder_needs_room_for_the_iso_before_anything_is_created()
    {
        var downloads = _temp.File("cache");
        var source = new DownloadingSource(_mediaFolder, downloads);
        var host = DefaultHost with
        {
            WorkDriveFreeBytes = Preflight.RequiredBytes(Preflight.AssumedMediaBytes) + (1L << 30)
        };

        // No ISO output: the ISO would need its own room on the same drive.
        var result = await Pipeline(host, source).RunAsync(Request(iso: false), new Sync(_progress.Add),
            TestContext.Current.CancellationToken);

        Assert.Equal("failed", result.Report.Result);
        Assert.Equal(ErrorKind.Preflight, result.ErrorKind);
        Assert.Contains("Not enough free space", result.Report.Error);
        Assert.False(source.Asked);
        Assert.False(Directory.Exists(WorkFolder));
        Assert.Empty(_wimTool.Exports);

        // With an ISO in the download folder, the build usually downloads nothing, and the same space is enough.
        Directory.CreateDirectory(downloads);
        await File.WriteAllBytesAsync(Path.Combine(downloads, "Win11_English_x64.iso"), [0],
            TestContext.Current.CancellationToken);
        var again = await Pipeline(host, source).RunAsync(Request(iso: false), new Sync(_progress.Add),
            TestContext.Current.CancellationToken);
        Assert.True(again.Report.Result == "succeeded", again.Report.Error);
    }

    [Fact]
    public async Task The_iso_tool_is_fetched_before_the_long_work_so_a_network_error_loses_nothing()
    {
        var result = await Pipeline(oscdimg: new OfflineOscdimg()).RunAsync(Request(keepWork: false),
            new Sync(_progress.Add), TestContext.Current.CancellationToken);

        Assert.Equal("failed", result.Report.Result);
        Assert.Equal(ErrorKind.Network, result.ErrorKind);
        Assert.Contains("oscdimg.exe", result.Report.Error);
        Assert.Empty(_wimTool.Exports);
        Assert.False(Directory.Exists(WorkFolder));
    }

    [Theory]
    [InlineData("FAT32", 500L << 30, "cannot hold a file of 4 GB or more")]
    [InlineData("NTFS", 1L << 30, "not enough free space")]
    public async Task An_iso_place_that_cannot_hold_the_iso_is_refused_before_anything_is_created(string fileSystem,
        long freeBytes, string reason)
    {
        var isoFolder = Path.GetDirectoryName(IsoPath)!;

        var result = await Pipeline(hosts: HostFor).RunAsync(Request(), new Sync(_progress.Add),
            TestContext.Current.CancellationToken);

        Assert.Equal("failed", result.Report.Result);
        Assert.Equal(ErrorKind.Preflight, result.ErrorKind);
        Assert.Contains(reason, result.Report.Error);
        Assert.Contains(isoFolder, result.Report.Error);
        Assert.False(Directory.Exists(WorkFolder));
        Assert.Empty(_wimTool.Exports);
        return;

        HostEnvironment HostFor(string folder)
        {
            return string.Equals(Path.GetFullPath(folder), isoFolder, StringComparison.OrdinalIgnoreCase)
                ? DefaultHost with { WorkDriveFormat = fileSystem, WorkDriveFreeBytes = freeBytes }
                : DefaultHost;
        }
    }

    [Fact]
    public async Task An_iso_on_the_drive_of_the_work_folder_needs_room_for_both_at_the_same_time()
    {
        // Enough for the work folder alone and enough for the ISO alone, but not for both.
        var free = Preflight.RequiredBytes(Preflight.AssumedMediaBytes) +
                   IsoWriter.RequiredBytes(Preflight.AssumedMediaBytes) / 2;

        var result = await Pipeline(hosts: _ => DefaultHost with { WorkDriveFreeBytes = free })
            .RunAsync(Request(), new Sync(_progress.Add), TestContext.Current.CancellationToken);

        Assert.Equal("failed", result.Report.Result);
        Assert.Equal(ErrorKind.Preflight, result.ErrorKind);
        Assert.Contains("not enough free space", result.Report.Error);
        Assert.False(Directory.Exists(WorkFolder));
    }

    [Fact]
    public async Task An_iso_path_in_the_work_folder_is_refused_because_the_build_deletes_that_folder()
    {
        var request = Request() with { IsoOutputPath = Path.Combine(WorkFolder, "out.iso") };

        var result = await RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("failed", result.Report.Result);
        Assert.Equal(ErrorKind.InvalidInput, result.ErrorKind);
        Assert.Contains("is in the work folder", result.Report.Error);
        Assert.Empty(_wimTool.Exports);
    }

    /// <summary>Kept media from an earlier build lies in the work folder, which the build deletes first.</summary>
    [Fact]
    public async Task A_source_in_the_work_folder_is_refused_because_the_build_deletes_that_folder()
    {
        var layout = new WorkLayout(WorkFolder);
        layout.Create();
        BuildJournal.Create(layout);
        var kept = Path.Combine(layout.Media, "setup.exe");
        await File.WriteAllTextAsync(kept, "kept", TestContext.Current.CancellationToken);

        var result = await RunAsync(Request() with { SourcePath = layout.Media },
            TestContext.Current.CancellationToken);

        Assert.Equal("failed", result.Report.Result);
        Assert.Equal(ErrorKind.InvalidInput, result.ErrorKind);
        Assert.Contains($"'{layout.Media}' is in the work folder", result.Report.Error);
        Assert.True(File.Exists(kept));
        Assert.Empty(_wimTool.Exports);
    }

    [Fact]
    public async Task A_work_folder_in_the_source_media_folder_is_refused()
    {
        var media = _temp.File("media");
        Directory.CreateDirectory(media);

        var result = await RunAsync(
            Request() with { SourcePath = media, WorkDirectory = Path.Combine(media, "work") },
            TestContext.Current.CancellationToken);

        Assert.Equal("failed", result.Report.Result);
        Assert.Equal(ErrorKind.InvalidInput, result.ErrorKind);
        Assert.Contains("is in the Windows media folder", result.Report.Error);
        Assert.Empty(_wimTool.Exports);
    }

    [Fact]
    public async Task An_iso_that_would_replace_the_source_iso_is_refused()
    {
        var source = _temp.File(@"in\Win11.iso");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllTextAsync(source, "the original", TestContext.Current.CancellationToken);
        // The same file, written another way, and with "replace" confirmed.
        var request = Request() with
        {
            SourcePath = source, IsoOutputPath = source.ToUpperInvariant(), OverwriteOutput = true
        };

        var result = await RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("failed", result.Report.Result);
        Assert.Equal(ErrorKind.InvalidInput, result.ErrorKind);
        Assert.Contains("is the Windows ISO that the build reads", result.Report.Error);
        Assert.Equal("the original", await File.ReadAllTextAsync(source, TestContext.Current.CancellationToken));
        Assert.Empty(_wimTool.Exports);
    }

    /// <summary>
    ///     A work folder at the root of a drive or share has no parent folder. Without an ISO the report then goes
    ///     into that root. A trailing separator does not put the report into the work folder, which is deleted.
    /// </summary>
    [Theory]
    [InlineData(@"D:\", @"D:\")]
    [InlineData(@"\\server\share", @"\\server\share")]
    [InlineData(@"D:\Plainstall\work\", @"D:\Plainstall")]
    public void Without_an_iso_the_report_goes_next_to_the_work_folder_or_into_its_root(string work,
        string reportFolder)
    {
        var request = Request(iso: false) with { WorkDirectory = work };

        Assert.Equal(Path.Combine(reportFolder, BuildReport.FileName),
            BuildPipeline.ReportPathFor(request, new WorkLayout(work)));
    }

    [Fact]
    public async Task A_password_that_windows_does_not_accept_stops_the_build_before_anything_is_created()
    {
        var preset = ProfileFactory.FromPreset(CatalogLoader.LoadEmbedded().DefaultPreset);
        var profile = preset with
        {
            Setup = preset.Setup with
            {
                LocalAccount = new LocalAccountOptions { Name = "Anna", AskPassword = true }
            }
        };

        var result = await RunAsync(
            Request(profile, password: new string('x', UnattendGenerator.MaxPasswordLength + 1)),
            TestContext.Current.CancellationToken);

        Assert.Equal("failed", result.Report.Result);
        Assert.Equal(ErrorKind.InvalidInput, result.ErrorKind);
        Assert.Contains("password is too long", result.Report.Error);
        Assert.False(Directory.Exists(WorkFolder));
        Assert.Empty(_wimTool.Exports);
    }

    [Fact]
    public async Task A_cleanup_that_does_not_finish_is_told_in_the_result()
    {
        using var cancel = new CancellationTokenSource();
        Action stop = cancel.Cancel;
        _onMount = image => image.OnFirstCall = stop;
        _imaging.FailDiscard = true;

        var result = await RunAsync(Request(keepWork: false), cancel.Token);

        Assert.Equal("cancelled", result.Report.Result);
        Assert.False(result.CleanupFinished);
        Assert.Contains(result.Report.Warnings,
            w => w.StartsWith("Cleaning up did not finish", StringComparison.Ordinal));

        // Releases the fake mount, so the temporary folder can be deleted.
        _imaging.FailDiscard = false;
        foreach (var mount in _imaging.Mounted.ToList()) _imaging.UnmountAndDiscard(mount.MountPath);
    }

    [Fact]
    public async Task A_finished_cleanup_is_told_in_the_result()
    {
        var result = await RunAsync(Request(keepWork: false), TestContext.Current.CancellationToken);

        Assert.Equal("succeeded", result.Report.Result);
        Assert.True(result.CleanupFinished);
    }

    [Fact]
    public async Task A_password_reaches_the_answer_file_only_and_a_missing_one_is_refused()
    {
        var preset = ProfileFactory.FromPreset(CatalogLoader.LoadEmbedded().DefaultPreset);
        var profile = preset with
        {
            Setup = preset.Setup with
            {
                LocalAccount = new LocalAccountOptions { Name = "Anna", AskPassword = true }
            }
        };

        var missing = await RunAsync(Request(profile, iso: false), TestContext.Current.CancellationToken);
        Assert.Equal("failed", missing.Report.Result);
        Assert.Equal(ErrorKind.InvalidInput, missing.ErrorKind);
        Assert.Contains("password", missing.Report.Error);

        var result = await RunAsync(Request(profile, password: "correct horse", iso: false),
            TestContext.Current.CancellationToken);
        Assert.Equal("succeeded", result.Report.Result);
        var layout = new WorkLayout(WorkFolder);
        var answer = await File.ReadAllTextAsync(Path.Combine(layout.Media, "autounattend.xml"),
            TestContext.Current.CancellationToken);
        Assert.Contains("<Name>Anna</Name>", answer);
        Assert.DoesNotContain("correct horse", answer);
        Assert.DoesNotContain("correct horse",
            await File.ReadAllTextAsync(result.ReportPath, TestContext.Current.CancellationToken));
        Assert.Null(result.IsoPath);
    }

    [Fact]
    public async Task An_edition_that_is_not_on_the_media_is_reported_with_the_ones_that_are()
    {
        var preset = ProfileFactory.FromPreset(CatalogLoader.LoadEmbedded().DefaultPreset);
        var profile = preset with { Windows = preset.Windows with { Edition = "Enterprise" } };

        var result = await RunAsync(Request(profile), TestContext.Current.CancellationToken);

        Assert.Equal("failed", result.Report.Result);
        Assert.Contains("Professional", result.Report.Error);
        Assert.False(Directory.Exists(WorkFolder));
    }

    // ---- USB stick --------------------------------------------------------------------------------

    private sealed class FakeUsbWriter : IUsbWriter
    {
        public UsbWriteRequest? Request { get; private set; }
        public Action? OnWrite { get; init; }
        public Exception? Failure { get; init; }

        public Task<UsbWriteResult> WriteAsync(UsbWriteRequest request, IProgress<UsbWriteProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            OnWrite?.Invoke();
            progress?.Report(new UsbWriteProgress(UsbWritePhase.Erasing));
            progress?.Report(new UsbWriteProgress(UsbWritePhase.Copying, 50));
            if (Failure is not null) throw Failure;
            return Task.FromResult(new UsbWriteResult
            {
                Disk = request.ConfirmedDisk,
                VolumeRoot = @"F:\",
                BytesWritten = 4_000_000,
                ImageWasSplit = true,
                WasEjected = false,
                Warnings = ["Use \"Safely Remove Hardware\" before unplugging the stick."]
            });
        }
    }

    // ---- fakes ---------------------------------------------------------------------------------

    private sealed class Sync(Action<BuildProgress> report) : IProgress<BuildProgress>
    {
        public void Report(BuildProgress value)
        {
            report(value);
        }
    }

    /// <summary>Stands in for dism.exe export: writes a one-image WIM that describes the requested edition.</summary>
    private sealed class FakeWimTool : IWimTool
    {
        public List<(string Source, int SourceIndex, string Destination)> Exports { get; } = [];

        public Task ExportImageAsync(string sourceFile, int sourceIndex, string destinationFile,
            IProgress<int>? progress = null, CancellationToken cancellationToken = default)
        {
            Assert.False(File.Exists(destinationFile), "export into an existing file");
            Exports.Add((sourceFile, sourceIndex, destinationFile));
            var image = WimMetadataReader.Read(sourceFile).Images.Single(i => i.Index == sourceIndex);
            new SyntheticWim().AddImage(image.Name, image.EditionId, image.InstallationType, image.ArchitectureCode,
                    image.Build, image.Version!.Revision, image.DefaultLanguage)
                .WriteTo(destinationFile);
            progress?.Report(100);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> SplitImageAsync(string wimFile, string swmFile, int partSizeMb,
            IProgress<int>? progress = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeOscdimg(string path) : IOscdimgProvider
    {
        public Task<string> GetPathAsync(CancellationToken cancellationToken)
        {
            // The writer checks that the tool exists before starting it.
            File.WriteAllText(path, "not a real tool");
            return Task.FromResult(path);
        }
    }

    /// <summary>A PC without the ADK and without a connection to Microsoft's server.</summary>
    private sealed class OfflineOscdimg : IOscdimgProvider
    {
        public Task<string> GetPathAsync(CancellationToken cancellationToken)
        {
            throw new PlainstallException(ErrorKind.Network,
                "Could not download oscdimg.exe from Microsoft: no connection (test).");
        }
    }

    /// <summary>Gives the synthetic media, but says that a download would go into <paramref name="folder" />.</summary>
    private sealed class DownloadingSource(string media, string folder) : ISourceProvider
    {
        public bool Asked { get; private set; }

        public string DownloadFolder => folder;

        public Task<SourceIso> GetAsync(Profile profile, IProgress<TransferProgress>? progress,
            CancellationToken cancellationToken)
        {
            Asked = true;
            return Task.FromResult(new SourceIso(media, "download", null, false));
        }
    }

    private sealed class FakeOscdimgRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            File.WriteAllBytes(request.Arguments[^1], IsoWriterTests.FakeIso());
            return Task.FromResult(new ProcessResult(0, ["Done."]));
        }
    }

    /// <summary>Registry "hives" as ordinary keys under HKCU, one subtree per image folder and hive.</summary>
    private sealed class FakeHives : IHiveSessionFactory, IDisposable
    {
        private const string BasePath = @"Software\Plainstall.Tests";
        private readonly string _name = "pipeline-" + Guid.NewGuid().ToString("N");
        private readonly RegistryKey _sandbox;

        public FakeHives()
        {
            _sandbox = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{BasePath}\{_name}", true);
        }

        public bool FailOnOpen { get; set; }
        public int OpenCount { get; private set; }

        /// <summary>Runs first in every Open, so a test can look at the state at that moment.</summary>
        public Action? OnOpen { get; set; }

        public void Dispose()
        {
            _sandbox.Dispose();
            using var parent = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(BasePath, true);
            parent?.DeleteSubKeyTree(_name, false);
        }

        public IHiveSession Open(string imageRoot, IReadOnlyCollection<RegistryHiveId> hives, BuildJournal journal,
            ILogger logger)
        {
            OnOpen?.Invoke();
            if (FailOnOpen) throw new RegistryException("Could not load the registry file (test).", 5);
            var session = new Session(this, journal);
            foreach (var hive in hives)
            {
                var key = KeyFor(imageRoot, hive);
                if (hive is RegistryHiveId.MachineSystem or RegistryHiveId.SetupSystem)
                {
                    using var select = key.CreateSubKey("Select");
                    select.SetValue("Current", 1, RegistryValueKind.DWord);
                }

                journal.HiveLoading($"fake_{hive}", imageRoot);
                session.Add(hive, key);
                OpenCount++;
            }

            return session;
        }

        private RegistryKey KeyFor(string imageRoot, RegistryHiveId hive)
        {
            return _sandbox.CreateSubKey($"{Path.GetFileName(imageRoot.TrimEnd('\\'))}_{hive}", true);
        }

        public object? Read(string imageRoot, RegistryHiveId hive, string key, string name)
        {
            using var root = KeyFor(imageRoot, hive);
            return new RegistryRoot(root.Handle, false, hive.ToString()).GetValue(key, name)?.Value;
        }

        private sealed class Session(FakeHives owner, BuildJournal journal) : IHiveSession
        {
            private readonly Dictionary<RegistryHiveId, RegistryKey> _keys = [];

            public RegistryRoot? Root(RegistryHiveId hive)
            {
                return _keys.TryGetValue(hive, out var key)
                    ? new RegistryRoot(key.Handle, false, hive.ToString())
                    : null;
            }

            public void Dispose()
            {
                foreach (var (hive, key) in _keys)
                {
                    key.Dispose();
                    journal.HiveUnloaded($"fake_{hive}");
                    owner.OpenCount--;
                }

                _keys.Clear();
            }

            public void Add(RegistryHiveId hive, RegistryKey key)
            {
                _keys[hive] = key;
            }
        }
    }
}

/// <summary>Keeps every log line in order, as "Level: message".</summary>
internal sealed class RecordingLogger : ILogger
{
    public List<string> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (Lines)
        {
            Lines.Add($"{logLevel}: {formatter(state, exception)}");
        }
    }
}