using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Plainstall.Core.Catalog;
using Plainstall.Core.Imaging;
using Plainstall.Core.Imaging.Drivers;
using Plainstall.Core.Imaging.Updates;
using Plainstall.Core.Media;
using Plainstall.Core.Output;
using Plainstall.Core.Output.Usb;
using Plainstall.Core.Profiles;
using Plainstall.Core.Registry;
using Plainstall.Core.Unattend;

namespace Plainstall.Core.Pipeline;

/// <summary>One build: a profile, where to work, and what to produce.</summary>
public sealed record BuildRequest
{
    public required Profile Profile { get; init; }
    public required string WorkDirectory { get; init; }

    /// <summary>
    ///     The ISO file or media folder the user gave as the source. Null when Plainstall downloads the ISO. The
    ///     build reads the source through <see cref="PipelineServices.Source" />. This path is only checked against
    ///     the work folder and the output.
    /// </summary>
    public string? SourcePath { get; init; }

    /// <summary>Where to write the ISO. Null when no ISO is wanted.</summary>
    public string? IsoOutputPath { get; init; }

    /// <summary>The USB stick to write, exactly as the user confirmed it. Null when no stick is wanted.</summary>
    public DiskFacts? UsbDisk { get; init; }

    /// <summary>
    ///     True when Windows reports <see cref="UsbDisk" /> as an external drive and the user confirmed that it is a
    ///     USB stick that may be erased. Without it such a disk is refused.
    /// </summary>
    public bool UsbExternalDriveConfirmed { get; init; }

    public bool OverwriteOutput { get; init; }

    /// <summary>The account password, when the profile asks for one. Never read from the profile.</summary>
    public string? AccountPassword { get; init; }

    public bool KeepWorkFolder { get; init; }

    /// <summary>
    ///     For virtual machine tests only: the answer file then wipes disk 0 and signs the account in once,
    ///     without asking. Media built this way must never be booted on a real PC.
    /// </summary>
    public bool VmTest { get; init; }

    public required string AppVersion { get; init; }
}

public sealed record BuildResult(BuildReport Report, string ReportPath, string? IsoPath)
{
    /// <summary>
    ///     The kind of the error that stopped the build. Null when the build did not stop with a
    ///     <see cref="PlainstallException" />: it succeeded, it was cancelled, or an unexpected error stopped it.
    /// </summary>
    public ErrorKind? ErrorKind { get; init; }

    /// <summary>
    ///     False when the cleanup at the end did not finish and something may still be mounted or loaded. Not part
    ///     of the report file: the report lists the reason in its warnings.
    /// </summary>
    public bool CleanupFinished { get; init; } = true;
}

/// <summary>
///     Runs a build from profile to output, stage by stage. Every mount and every loaded hive is recorded
///     in the journal before it is taken and released in a <c>finally</c> block, so a failure or a cancel
///     leaves nothing behind. A cancel request is honored between actions and stages, never in the middle
///     of a DISM operation.
/// </summary>
public sealed class BuildPipeline
{
    public static readonly IReadOnlyList<string> StageNames =
    [
        "Check this PC",
        "Get the Windows image",
        "Look at the image",
        "Copy the installation files",
        "Customize Windows",
        "Prepare Windows Setup",
        "Write the answer file",
        "Pack the image",
        "Create the output",
        "Write the report"
    ];

    private static readonly RegistryHiveId[] InstallHives =
    [
        RegistryHiveId.MachineSoftware, RegistryHiveId.MachineSystem, RegistryHiveId.DefaultUser,
        RegistryHiveId.SystemUser
    ];

    private readonly ILogger _logger;

    private readonly PipelineServices _services;

    public BuildPipeline(PipelineServices services, ILogger? logger = null)
    {
        _services = services;
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<BuildResult> RunAsync(BuildRequest request, IProgress<BuildProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var started = _services.Time.GetUtcNow();
        var profile = request.Profile;
        var layout = new WorkLayout(request.WorkDirectory);
        var log = new BuildLog();
        var notes = new List<string>();
        var stage = new StageReporter(progress, _logger);
        ReportSource? reportSource = null;
        ReportOutput? reportOutput = null;
        BuildPlan? plan = null;
        IsoWriteResult? iso = null;
        var result = "failed";
        string? error = null;
        ErrorKind? errorKind = null;
        BuildJournal? journal = null;
        IImagingService? imaging = null;

        try
        {
            // ---- 1. preflight ------------------------------------------------------------------
            stage.Begin(1);
            ValidateRequest(request, layout);
            CheckUsbStick(request, layout);
            if (LeftoverCleaner.HasLeftovers(layout))
                throw new PlainstallException(ErrorKind.Preflight,
                    $"An earlier build did not finish. It left a mounted image or a loaded registry file in '{layout.Root}'. Use 'Clean up' on the Welcome page, or run 'Plainstall.Cli cleanup'.");
            if (Directory.Exists(layout.Root) && Directory.EnumerateFileSystemEntries(layout.Root).Any())
            {
                if (!layout.IsOurs)
                    throw new PlainstallException(ErrorKind.Preflight,
                        $"The work folder '{layout.Root}' is not empty and was not created by Plainstall. Choose an empty or new folder.");
                // The old journal goes with the folder, so what it knows about a half-written stick is said now.
                foreach (var unfinished in LeftoverCleaner.UnfinishedSticks(layout)) log.Warn(unfinished);
                layout.Delete();
            }

            var host = _services.HostEnvironment(layout.Root);
            // The size of the media is not known yet. Stage 3 checks again, when the download is done.
            var workBytes = Preflight.RequiredBytes(Preflight.AssumedMediaBytes) +
                            Preflight.DownloadBytes(layout.Root, _services.Source.DownloadFolder);
            Preflight.ThrowIfAny(Preflight.Check(layout.Root, host, workBytes));
            if (request.IsoOutputPath is { } isoTarget)
            {
                // The place of the ISO and the ISO tool are checked now, so that a full drive, a FAT32 drive or a
                // network error does not stop the build at its last step. The writer checks the space again.
                // On the drive of the work folder, the ISO is written while the work folder still holds the media.
                var isoFolder = Path.GetDirectoryName(Path.GetFullPath(isoTarget))!;
                var target = _services.HostEnvironment(isoFolder);
                var known = target is { WorkDriveIsNetwork: false, WorkDriveFormat: not null };
                var sameDrive = string.Equals(Path.GetPathRoot(isoFolder), Path.GetPathRoot(layout.Root),
                    StringComparison.OrdinalIgnoreCase);
                Preflight.ThrowIfAny(IsoWriter.CheckTarget(isoFolder, Preflight.AssumedMediaBytes,
                    known ? target.WorkDriveFreeBytes : null, known ? target.WorkDriveFormat : null,
                    sameDrive ? workBytes : 0));
                stage.Action("Getting the ISO tool");
                await _services.Oscdimg.GetPathAsync(cancellationToken).ConfigureAwait(false);
            }

            layout.Create();
            journal = BuildJournal.Create(layout, _services.Time);
            imaging = _services.ImagingFactory(layout);
            var wimTool = _services.WimToolFactory(layout);

            // ---- 2. acquire ----------------------------------------------------------------------
            stage.Begin(2);
            var source = await _services.Source.GetAsync(profile, stage.Transfer(), cancellationToken)
                .ConfigureAwait(false);
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Source: {Path} ({Kind})", source.Path, source.Kind);

            WimImageInfo edition;
            MediaInfo media;
            await using (var handle = await _services.MediaOpener.OpenAsync(source.Path, _logger, cancellationToken)
                             .ConfigureAwait(false))
            {
                // ---- 3. inventory ----------------------------------------------------------------
                stage.Begin(3);
                media = MediaInventory.Read(handle.RootPath, _logger, cancellationToken);
                edition = media.SelectEdition(profile.Windows.Edition);
                var facts = new ImageFacts(edition.Build, edition.EditionId);
                reportSource = new ReportSource
                {
                    Kind = source.Kind,
                    File = Path.GetFileName(source.Path.TrimEnd('\\')),
                    Sha256 = source.Sha256,
                    VerifiedAgainstMicrosoft = source.VerifiedAgainstMicrosoft,
                    Edition = edition.EditionId,
                    Build = edition.Version?.ToString() ?? edition.Build.ToString(CultureInfo.InvariantCulture),
                    Arch = MediaInfo.ArchitectureName,
                    Language = media.Language
                };
                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation("Image: {Name} ({Edition}), build {Build}, {Arch}, {Language}", edition.Name,
                        edition.EditionId, reportSource.Build, reportSource.Arch, media.Language);

                plan = BuildPlanner.Create(profile, _services.Catalog, facts);
                foreach (var note in plan.Notes)
                {
                    notes.Add(note.Message);
                    if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("Note: {Note}", note.Message);
                }

                var updateBytes = profile.Updates.Sum(u => new FileInfo(u).Length);
                Preflight.ThrowIfAny(Preflight.Check(layout.Root, _services.HostEnvironment(layout.Root),
                    Preflight.RequiredBytes(media.TotalBytes, updateBytes)));

                // ---- 4. stage media --------------------------------------------------------------
                stage.Begin(4);
                await MediaStager.CopyMediaAsync(handle.RootPath, layout.Media, stage.Transfer(), cancellationToken)
                    .ConfigureAwait(false);
                stage.Action("Extracting the " + edition.Name + " edition");
                await wimTool.ExportImageAsync(media.InstallImage.Path, edition.Index, layout.WorkingWim,
                    stage.Percent(), cancellationToken).ConfigureAwait(false);
            }

            GuardImage(layout.WorkingWim, edition, "extracting the edition");
            // Updates and drivers are checked and collected before anything is mounted, so a bad file stops the build early.
            var additions = await PrepareAdditionsAsync(profile, layout, notes, log, stage, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            // ---- 5. service the install image -----------------------------------------------------
            stage.Begin(5);
            ServiceInstallImage(request, plan, additions, layout, journal, imaging, log, stage, cancellationToken);

            // ---- 6. service the Setup image -------------------------------------------------------
            stage.Begin(6);
            if (plan.TouchesSetupImage || additions.SetupDrivers.Count > 0)
                ServiceSetupImage(plan, additions.SetupDrivers, media, layout, journal, imaging, log, stage,
                    cancellationToken);
            else
                _logger.LogInformation("The Setup image needs no change and is left as Microsoft ships it.");
            cancellationToken.ThrowIfCancellationRequested();

            // ---- 7. answer file -------------------------------------------------------------------
            stage.Begin(7);
            WriteAnswerFile(request, plan, media, edition, layout);
            MediaStager.WriteEditionFiles(layout.Media, edition.EditionId);

            // ---- 8. finalize ----------------------------------------------------------------------
            stage.Begin(8);
            var finalImage = Path.Combine(layout.MediaSources, "install.wim");
            // A fresh export drops what servicing deleted. Into a new file: DISM appends to an existing one.
            await wimTool.ExportImageAsync(layout.WorkingWim, 1, finalImage, stage.Percent(), cancellationToken)
                .ConfigureAwait(false);
            GuardImage(finalImage, edition, "packing the image");
            File.Delete(layout.WorkingWim);
            var finalBytes = new FileInfo(finalImage).Length;

            // ---- 9. output ------------------------------------------------------------------------
            stage.Begin(9);
            if (request.IsoOutputPath is { } isoPath)
            {
                // Again right before the use: the tool is checked again each time.
                stage.Action("Checking the ISO tool");
                var oscdimg = await _services.Oscdimg.GetPathAsync(cancellationToken).ConfigureAwait(false);
                stage.Action("Writing " + Path.GetFileName(isoPath));
                iso = await new IsoWriter(_services.ProcessRunner, _logger).WriteAsync(new IsoWriteRequest
                {
                    MediaRoot = layout.Media,
                    OutputPath = Path.GetFullPath(isoPath),
                    OscdimgPath = oscdimg,
                    VolumeLabel = OscdimgArguments.DefaultLabel,
                    Overwrite = request.OverwriteOutput
                }, stage.Percent(), cancellationToken).ConfigureAwait(false);
            }

            reportOutput = new ReportOutput
            {
                Iso = iso is null ? null : new ReportIso(iso.Path, iso.SizeBytes, iso.Sha256),
                StagingFolder = request.KeepWorkFolder ? layout.Media : null,
                InstallImageBytes = finalBytes,
                Split = false
            };

            // The stick comes last: everything that can fail without erasing anything has happened by now.
            if (request.UsbDisk is { } stick)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var writer = _services.UsbWriterFactory!(wimTool, journal);
                var written = await writer.WriteAsync(new UsbWriteRequest
                    {
                        ConfirmedDisk = stick,
                        MediaRoot = layout.Media,
                        Context = new UsbSelectionContext
                        {
                            PathsInUse = PathsInUse(request, layout, source.Path),
                            RequiredBytes = UsbSizes.RequiredFat32Bytes(layout.Media),
                            AllowExternalDrives = request.UsbExternalDriveConfirmed
                        }
                    }, stage.Steps<UsbWriteProgress>(p => UsbPhaseText(p.Phase), p => p.Percent), cancellationToken)
                    .ConfigureAwait(false);
                foreach (var warning in written.Warnings) log.Warn(warning);
                reportOutput = reportOutput with
                {
                    Usb = new ReportUsb(written.Disk.FriendlyName, written.Disk.SerialNumber, written.Disk.SizeBytes,
                        written.VolumeRoot, written.BytesWritten, written.WasEjected),
                    Split = written.ImageWasSplit
                };
                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation("USB stick written: {Name}, {Bytes} bytes, image split: {Split}",
                        written.Disk.FriendlyName, written.BytesWritten, written.ImageWasSplit);
            }

            result = log.FailedCount == 0 ? "succeeded" : "completed-with-failures";
            if (log.FailedCount > 0)
                log.Warn(
                    $"{log.FailedCount} change(s) could not be applied. They are listed in the report with the reason.");
        }
        catch (OperationCanceledException)
        {
            result = "cancelled";
            error = "The build was cancelled.";
            _logger.LogWarning("The build was cancelled.");
        }
        catch (PlainstallException ex)
        {
            error = ex.Message;
            errorKind = ex.Kind;
            _logger.LogError("The build failed: {Message}", ex.Message);
        }
        catch (Exception ex)
        {
            error = $"Unexpected error: {ex.Message}";
            _logger.LogError(ex, "The build failed with an unexpected error");
        }

        // ---- 10. report (and cleanup), whatever happened -------------------------------------------
        stage.Begin(10);
        var cleanupFinished = true;
        if (journal is not null)
        {
            var keep = request.KeepWorkFolder && result is "succeeded" or "completed-with-failures";
            // First release what may still be mounted, then stop DISM, which keeps its log and scratch files in
            // the work folder open, and only then delete the folder.
            var cleanup = new LeftoverCleaner(imaging, _logger).Clean(layout, false);
            (imaging as IDisposable)?.Dispose();
            if (cleanup.Success && !keep) cleanup = new LeftoverCleaner(null, _logger).Clean(layout);
            cleanupFinished = cleanup.Success;
            if (!cleanup.Success)
                log.Warn("Cleaning up did not finish: " + string.Join(" ", cleanup.Problems) +
                         " Use 'Clean up' on the Welcome page of the app, or run 'Plainstall.Cli cleanup'.");
        }

        var report = new BuildReport
        {
            Plainstall = request.AppVersion,
            CatalogVersion = _services.Catalog.CatalogVersion,
            Started = started,
            Finished = _services.Time.GetUtcNow(),
            Result = result,
            Error = error,
            Source = reportSource,
            Profile = profile,
            Actions = log.Actions,
            Notes = notes,
            Warnings = log.Warnings,
            WingetInstalls = plan?.WingetInstalls ?? [],
            Output = reportOutput
        };
        var reportPath = ReportPathFor(request, layout);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            report.Save(reportPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError("The report could not be written to {Path}: {Message}", reportPath, ex.Message);
        }

        return new BuildResult(report, reportPath, iso?.Path)
        {
            ErrorKind = errorKind, CleanupFinished = cleanupFinished
        };
    }

    /// <summary>
    ///     The report goes next to the ISO, or next to the work folder when there is no ISO. A work folder at the
    ///     root of a drive or a share has no folder next to it: the report then goes into that root.
    /// </summary>
    public static string ReportPathFor(BuildRequest request, WorkLayout layout)
    {
        if (request.IsoOutputPath is { } iso)
            return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(iso))!, BuildReport.FileName);
        var work = Path.TrimEndingDirectorySeparator(layout.Root);
        return Path.Combine(Path.GetDirectoryName(work) ?? work, BuildReport.FileName);
    }

    /// <summary>True when the ISO to write is the ISO file the build reads, so writing it would replace the original.</summary>
    public static bool OutputReplacesSource(string isoOutputPath, string? sourcePath)
    {
        return sourcePath is not null && string.Equals(Path.GetFullPath(isoOutputPath), Path.GetFullPath(sourcePath),
            StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateRequest(BuildRequest request, WorkLayout layout)
    {
        // Plainstall deletes the work folder before and after the build, with everything in it.
        var workRoot = Path.TrimEndingDirectorySeparator(layout.Root) + Path.DirectorySeparatorChar;
        // The separator added to each path also catches the work folder itself, for a source folder.
        var inWork = new[] { request.IsoOutputPath, request.SourcePath }.Concat(request.Profile.Updates)
            .Concat(request.Profile.Drivers.Folders)
            .OfType<string>()
            .Where(path => (Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar)
                .StartsWith(workRoot, StringComparison.OrdinalIgnoreCase))
            .Select(path =>
                $"'{path}' is in the work folder '{layout.Root}'. Plainstall deletes that folder with everything in it. Use a place outside the work folder.");
        var problems = ProfileSerializer.Validate(request.Profile).ToList();
        var account = request.Profile.Setup.LocalAccount;
        if (account is { AskPassword: true } && string.IsNullOrEmpty(request.AccountPassword))
            problems.Add("The profile asks for an account password, but none was given.");
        if (account is not { AskPassword: true } && !string.IsNullOrEmpty(request.AccountPassword))
            problems.Add(
                "A password was given, but the profile does not set \"askPassword\": true for a named local account.");
        if (request.AccountPassword is { Length: > UnattendGenerator.MaxPasswordLength })
            problems.Add(
                $"The password is too long. Windows accepts at most {UnattendGenerator.MaxPasswordLength} characters.");
        if (request.VmTest && account?.Name is null)
            problems.Add("The virtual machine test mode needs a named local account in the profile.");
        if (request.IsoOutputPath is { } iso)
        {
            if (!iso.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                problems.Add($"The output file '{iso}' must end in .iso.");
            else if (OutputReplacesSource(iso, request.SourcePath))
                problems.Add(
                    $"The output file '{iso}' is the Windows ISO that the build reads. Choose another name, so that the original ISO stays.");
            else if (File.Exists(iso) && !request.OverwriteOutput)
                problems.Add($"The output file '{iso}' already exists. Choose another name or allow overwriting.");
        }

        problems.AddRange(inWork);
        // A work folder inside a source media folder would copy the media into itself.
        if (request.SourcePath is { } source && Directory.Exists(source) &&
            workRoot.StartsWith(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            problems.Add(
                $"The work folder '{layout.Root}' is in the Windows media folder '{source}'. Use a work folder outside the media folder.");
        problems.AddRange(UpdateStager.Check(request.Profile.Updates));
        problems.AddRange(request.Profile.Drivers.Folders
            .Where(f => !Directory.Exists(f))
            .Select(folder => $"The driver folder '{folder}' does not exist."));
        if (problems.Count > 0)
            throw PlainstallException.WithList(ErrorKind.InvalidInput, "The build cannot start:", problems);
    }

    /// <summary>
    ///     Refuses a stick that cannot be used, before the long part of the build. This judges the facts the
    ///     user confirmed. The writer reads the disk again and compares right before it erases anything.
    /// </summary>
    private void CheckUsbStick(BuildRequest request, WorkLayout layout)
    {
        if (request.UsbDisk is not { } stick) return;
        if (_services.UsbWriterFactory is null)
            throw new PlainstallException(ErrorKind.Preflight,
                "The build cannot start:" + Environment.NewLine + "  - Writing a USB stick is not available here.");
        var verdict = UsbDiskRules.Evaluate(stick,
            new UsbSelectionContext
            {
                PathsInUse = PathsInUse(request, layout, null),
                AllowExternalDrives = request.UsbExternalDriveConfirmed
            });
        if (!verdict.IsEligible)
            throw new PlainstallException(ErrorKind.Preflight,
                $"The build cannot start: the USB stick '{stick.FriendlyName}' (disk {stick.Number}) cannot be used." +
                Environment.NewLine
                + string.Join(Environment.NewLine, verdict.Reasons.Select(r => "  - " + r)) + Environment.NewLine +
                "Nothing was erased.");
    }

    /// <summary>The places this build reads from and writes to. None of them may be on the stick that gets erased.</summary>
    private static List<string> PathsInUse(BuildRequest request, WorkLayout layout, string? sourcePath)
    {
        var paths = new List<string>(UsbPaths.RootsOf(layout.Root));
        if (request.IsoOutputPath is { } iso) paths.AddRange(UsbPaths.RootsOf(iso));
        if (sourcePath is not null) paths.AddRange(UsbPaths.RootsOf(sourcePath));
        return [.. paths.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static string UsbPhaseText(UsbWritePhase phase)
    {
        return phase switch
        {
            UsbWritePhase.Checking => "Checking the USB stick",
            UsbWritePhase.Erasing => "Erasing the USB stick",
            UsbWritePhase.Formatting => "Formatting the USB stick",
            UsbWritePhase.Copying => "Copying the files to the USB stick",
            UsbWritePhase.SplittingImage => "Copying the Windows image to the USB stick in parts",
            UsbWritePhase.Verifying => "Checking the copied files",
            _ => "Finishing the USB stick"
        };
    }

    /// <summary>
    ///     An export must give exactly the edition that went in. Damaged metadata after an export is a known
    ///     way for Setup to fail later with "product key invalid", so it is caught here, at build time.
    /// </summary>
    private static void GuardImage(string wimFile, WimImageInfo expected, string step)
    {
        var info = WimMetadataReader.Read(wimFile);
        var image = info.Images.Count == 1 ? info.Images[0] : null;
        if (image is null
            || !string.Equals(image.EditionId, expected.EditionId, StringComparison.OrdinalIgnoreCase)
            || image.ArchitectureCode != expected.ArchitectureCode
            || !string.Equals(image.InstallationType, expected.InstallationType, StringComparison.OrdinalIgnoreCase)
            || !ServicingFamily.AreSame(image.Build, expected.Build))
            throw new PlainstallException(ErrorKind.Imaging,
                $"After {step}, the image no longer describes itself as {expected.EditionId} build {expected.Build}. The build was stopped, because Windows Setup would reject this image.");
    }

    /// <summary>
    ///     Copies and verifies the chosen updates, reads the driver folders, exports the drivers of this PC when
    ///     asked, and decides which driver packages go in. Nothing is mounted yet.
    /// </summary>
    private async Task<Additions> PrepareAdditionsAsync(Profile profile, WorkLayout layout, List<string> notes,
        BuildLog log, StageReporter stage, CancellationToken cancellationToken)
    {
        var wantsDrivers = profile.Drivers.FromThisPc || profile.Drivers.Folders.Count > 0;
        if (profile.Updates.Count == 0 && !wantsDrivers) return Additions.None;

        IReadOnlyList<StagedUpdate> updates = [];
        if (profile.Updates.Count > 0)
        {
            stage.Action("Checking the updates");
            var staged = await new UpdateStager(_logger)
                .StageAsync(profile.Updates, layout.Updates, stage.Transfer(), cancellationToken).ConfigureAwait(false);
            updates = staged.Updates;
            foreach (var note in staged.Notes) log.Warn(note);
        }

        if (!wantsDrivers) return new Additions(updates, [], []);

        var found = new List<DriverPackage>();

        // Folders the user chose come first: on a tie between equal versions the first copy wins.
        foreach (var folder in profile.Drivers.Folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stage.Action("Reading the drivers in " + folder);
            Take(DriverScanner.Scan(folder, _logger, cancellationToken));
        }

        if (profile.Drivers.FromThisPc)
        {
            stage.Action("Copying the drivers of this PC");
            var exporter = new HostDriverExporter(_services.ProcessRunner,
                Path.Combine(layout.Logs, "dism-driver-export.log"), logger: _logger);
            Take(await exporter.ExportAsync(Path.Combine(layout.Drivers, "this-pc"), stage.Percent(), cancellationToken)
                .ConfigureAwait(false));
        }

        var selection = DriverSelector.Select(found);
        foreach (var note in selection.Notes)
        {
            notes.Add(note);
            if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("Note: {Note}", note);
        }

        if (selection.Selected.Count == 0)
            log.Warn("No usable driver was found in the chosen places, so no driver was added.");
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "Drivers: {Selected} package(s) to add, {Storage} of them also to Windows Setup, {Dropped} left out",
                selection.Selected.Count, selection.ForSetupImage().Count, selection.Dropped.Count);
        return new Additions(updates, selection.Selected, selection.ForSetupImage());

        void Take(DriverScanResult scan)
        {
            found.AddRange(scan.Packages);
            foreach (var note in scan.Notes) log.Warn(note);
        }
    }

    private void ServiceInstallImage(BuildRequest request, BuildPlan plan, Additions additions, WorkLayout layout,
        BuildJournal journal, IImagingService imaging,
        BuildLog log, StageReporter stage, CancellationToken cancellationToken)
    {
        stage.Action("Opening the Windows image");
        journal.MountStarting(layout.WorkingWim, 1, layout.InstallMount);
        var committed = false;
        try
        {
            bool servicingPending;
            FirstLogonScripts scripts;
            using (var image = imaging.Mount(layout.WorkingWim, 1, layout.InstallMount, stage.Percent()))
            {
                var imageActions = new ImageActionExecutor(image, _logger);

                // Updates before everything else: an update can bring removed apps back, so removals come after.
                // Updates and drivers also go in before Plainstall loads a registry file of the image, because
                // DISM writes those files itself.
                if (additions.Updates.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    stage.Action("Adding updates");
                    var installed = new UpdateInstaller(image, _logger).Install(additions.Updates,
                        stage.Map<UpdateInstallProgress>(p => p.OverallPercent), cancellationToken);
                    foreach (var outcome in installed.Outcomes)
                        Record(log, "updates", "update.add", outcome.Update.FileName,
                            outcome.ToActionOutcome());
                    foreach (var note in installed.Notes) log.Warn(note);
                    // A failed update leaves the image with only some of the chosen updates: the build stops here
                    // and the mount is discarded below.
                    installed.ThrowIfFailed();
                    if (installed.ServicingPending) imageActions.MarkServicingPending();
                }

                if (additions.InstallDrivers.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    stage.Action("Adding drivers");
                    var injected = new DriverInjector(image, _logger).Inject(additions.InstallDrivers,
                        stage.Map<DriverInjectionProgress>(p => p.Percent), cancellationToken);
                    foreach (var package in injected.Packages)
                        Record(log, "drivers", "driver.add", package.Package.Name, package.ToActionOutcome());
                }

                // Apps, capabilities and features first. File deletions come after them, because a pending
                // change must block the deletion of files it may still need.
                foreach (var planned in plan.Actions.Where(a =>
                             a.Action is AppxRemoveProvisionedAction or CapabilityRemoveAction or FeatureDisableAction
                                 or FeatureEnableAction))
                    Run(planned, imageActions.Apply, log, stage, cancellationToken);
                imageActions.NotePendingPackages();
                foreach (var planned in plan.Actions.Where(a => a.Action is FileDeleteAction))
                    Run(planned, imageActions.Apply, log, stage, cancellationToken);

                // First-logon scripts are files inside the image.
                scripts = FirstLogonScriptGenerator.Generate(plan);
                foreach (var script in new[] { scripts.Machine, scripts.User })
                {
                    if (script is null) continue;
                    var path = ImageFileDeleter.ResolveInside(image.MountPath, script.ImageRelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllBytes(path, script.ToBytes());
                    if (_logger.IsEnabled(LogLevel.Information))
                        _logger.LogInformation("Wrote {Script}", script.ImageRelativePath);
                }

                if (scripts.Machine is not null || scripts.User is not null)
                    Directory.CreateDirectory(ImageFileDeleter.ResolveInside(image.MountPath,
                        FirstLogonScriptGenerator.LogFolderImagePath));
                servicingPending = imageActions.ServicingPending;
            }

            // Registry and scheduled tasks: one hive session, so each hive is loaded once. The DISM session
            // above is closed first, because DISM keeps the image's registry files open while it lasts.
            cancellationToken.ThrowIfCancellationRequested();
            stage.Action("Opening the image's registry");
            using (var hives = _services.Hives.Open(layout.InstallMount, InstallHives, journal, _logger))
            {
                var registry = new RegistryActionExecutor(hives.Root, _logger);
                foreach (var planned in plan.Actions.Where(a =>
                             RegistryActionExecutor.Handles(a.Action) && !BuildPlan.IsSetupImageAction(a.Action)))
                    Run(planned, registry.Apply, log, stage, cancellationToken);
                if (scripts.User is not null)
                {
                    // Makes every account created later run the per-user script once.
                    var runOnce = new PlannedAction("first-logon", "First sign-in script",
                        FirstLogonScriptGenerator.CreateRunOnceAction());
                    Run(runOnce, registry.Apply, log, stage, cancellationToken);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            stage.Action("Cleaning up the image");
            if (servicingPending)
            {
                log.Warn(
                    "Component cleanup was skipped, because some changes only complete when Windows first starts. The image is slightly larger. Nothing is wrong with it.");
            }
            else
            {
                // A new DISM session, now that the registry files are released again.
                using var image = imaging.Open(layout.InstallMount);
                if (image.CleanupComponents(request.Profile.Cleanup.ResetBase, stage.Percent()) ==
                    CleanupResult.SkippedPendingOperations)
                    log.Warn(
                        "Component cleanup was skipped, because the image has pending operations (0x800F0806). The image is slightly larger. Nothing is wrong with it.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            stage.Action("Saving the Windows image");
            imaging.UnmountAndCommit(layout.InstallMount, stage.Percent());
            committed = true;
            journal.MountReleased(layout.InstallMount);
        }
        catch (Exception ex)
        {
            // Said now, because discarding the image can take many minutes and may be interrupted.
            LogServicingFailure("Windows image", ex);
            throw;
        }
        finally
        {
            if (!committed) Discard(imaging, journal, layout.InstallMount, stage);
        }
    }

    private void ServiceSetupImage(BuildPlan plan, IReadOnlyList<DriverPackage> storageDrivers, MediaInfo media,
        WorkLayout layout, BuildJournal journal, IImagingService imaging,
        BuildLog log, StageReporter stage, CancellationToken cancellationToken)
    {
        // Work on a private copy and swap it in only after a clean commit.
        var staged = Path.Combine(layout.MediaSources, "boot.wim");
        File.Copy(staged, layout.BootWork, true);
        File.SetAttributes(layout.BootWork, FileAttributes.Normal);

        stage.Action("Opening the Setup image");
        journal.MountStarting(layout.BootWork, media.SetupImage.Index, layout.BootMount);
        var committed = false;
        try
        {
            var changed = false;
            using (var image =
                   imaging.Mount(layout.BootWork, media.SetupImage.Index, layout.BootMount, stage.Percent()))
            {
                // Storage drivers only, so Setup can see the disk. They go in before the registry file is loaded.
                if (storageDrivers.Count > 0)
                {
                    stage.Action("Adding storage drivers to Windows Setup");
                    var injected = new DriverInjector(image, _logger).Inject(storageDrivers,
                        stage.Map<DriverInjectionProgress>(p => p.Percent), cancellationToken);
                    foreach (var package in injected.Packages)
                        Record(log, "drivers", "driver.addToSetup", package.Package.Name,
                            package.ToActionOutcome());
                    changed |= injected.AnythingAdded;
                    if (!injected.AnythingAdded)
                        log.Warn(
                            "None of the storage drivers could be added to Windows Setup. If Setup does not show the disk of the PC, use \"Load driver\" there and point it at the driver folder.");
                }
            }

            // The registry file after the DISM session above is closed: DISM keeps it open while the session lasts.
            var setupActions = plan.Actions.Where(a => BuildPlan.IsSetupImageAction(a.Action)).ToList();
            if (setupActions.Count > 0)
            {
                using var hives =
                    _services.Hives.Open(layout.BootMount, [RegistryHiveId.SetupSystem], journal, _logger);
                var registry = new RegistryActionExecutor(hives.Root, _logger);
                foreach (var planned in setupActions) Run(planned, registry.Apply, log, stage, cancellationToken);
                changed = true;
            }

            if (!changed)
            {
                // Nothing went in: the mount is discarded below and boot.wim stays as Microsoft ships it. The
                // private copy stays in the work folder until the cleanup, because DISM keeps it open while it is
                // mounted.
                _logger.LogInformation("The Setup image was not changed and is left as Microsoft ships it.");
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            stage.Action("Saving the Setup image");
            imaging.UnmountAndCommit(layout.BootMount, stage.Percent());
            committed = true;
            journal.MountReleased(layout.BootMount);
        }
        catch (Exception ex)
        {
            LogServicingFailure("Setup image", ex);
            throw;
        }
        finally
        {
            if (!committed) Discard(imaging, journal, layout.BootMount, stage);
        }

        var after = WimMetadataReader.Read(layout.BootWork);
        if (after.ImageCount != media.BootImage.ImageCount || after.BootIndex != media.BootImage.BootIndex)
            throw new PlainstallException(ErrorKind.Imaging,
                "The Setup image changed shape while it was being edited. The build was stopped.");
        File.Move(layout.BootWork, staged, true);
    }

    private void Run(PlannedAction planned, Func<CatalogAction, ActionOutcome> apply, BuildLog log, StageReporter stage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        stage.Action(planned.EntryTitle);
        Record(log, planned.EntryId, planned.Action.KindName, planned.Action.Target, apply(planned.Action));
    }

    private void Record(BuildLog log, string entry, string kind, string target, ActionOutcome outcome)
    {
        log.Record(entry, kind, target, outcome);
        var level = outcome.Status == ActionStatus.Failed ? LogLevel.Warning : LogLevel.Information;
        if (_logger.IsEnabled(level))
            _logger.Log(level, "{Status,-7} {Kind} {Target}{Reason}", outcome.Status, kind, target,
                outcome.Reason is null ? "" : " (" + outcome.Reason + ")");
    }

    private void LogServicingFailure(string image, Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning("Changing the {Image} was cancelled", image);
            return;
        }

        if (_logger.IsEnabled(LogLevel.Error))
            _logger.LogError(exception, "Changing the {Image} failed: {Message}", image, exception.Message);
    }

    private void Discard(IImagingService imaging, BuildJournal journal, string mountPath, StageReporter stage)
    {
        try
        {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Discarding the mounted image at {MountPath}. This can take several minutes.",
                    mountPath);
            stage.Action(
                "Throwing away the changes to the image. This can take several minutes. Do not close this window.");
            imaging.UnmountAndDiscard(mountPath, stage.Percent());
            journal.MountReleased(mountPath);
        }
        catch (PlainstallException ex)
        {
            // The journal keeps the entry. The cleanup at the end, or the next start, tries again.
            _logger.LogWarning("Discarding the mounted image failed: {Message}", ex.Message);
        }
    }

    private void WriteAnswerFile(BuildRequest request, BuildPlan plan, MediaInfo media, WimImageInfo edition,
        WorkLayout layout)
    {
        var profile = request.Profile;
        var resolved = SetupResolver.Resolve(profile.Setup, _services.HostFacts(),
            edition.DefaultLanguage ?? media.Language);
        foreach (var note in resolved.Notes)
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Note: {Note}", note);

        var hasMachineScript = FirstLogonScriptGenerator.GenerateMachineScript(plan) is not null;
        var input = UnattendInput.FromProfile(profile.Setup, resolved, hasMachineScript) with
        {
            VmTest = request.VmTest
        };
        // Generate validates the document and throws when it breaks a rule.
        var document = UnattendGenerator.Generate(input, request.AccountPassword);
        File.WriteAllBytes(Path.Combine(layout.Media, UnattendGenerator.FileName),
            UnattendGenerator.ToXmlBytes(document));
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Answer file:{NewLine}{Xml}", Environment.NewLine,
                UnattendGenerator.ToLogSafeString(document));
    }

    /// <summary>What the user adds to the image on top of the catalog's changes.</summary>
    private sealed record Additions(
        IReadOnlyList<StagedUpdate> Updates,
        IReadOnlyList<DriverPackage> InstallDrivers,
        IReadOnlyList<DriverPackage> SetupDrivers)
    {
        public static readonly Additions None = new([], [], []);
    }

    /// <summary>Turns the stage's inner progress into <see cref="BuildProgress" /> reports.</summary>
    private sealed class StageReporter(IProgress<BuildProgress>? target, ILogger logger)
    {
        private string? _action;
        private int _lastPercent = -1;
        private int _number;

        public void Begin(int number)
        {
            _number = number;
            _action = null;
            _lastPercent = -1;
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("== Step {Number} of {Count}: {Name}", number, StageNames.Count,
                    StageNames[number - 1]);
            Report(null);
        }

        public void Action(string action)
        {
            _action = action;
            _lastPercent = -1;
            Report(null);
        }

        public InlineProgress<int> Percent()
        {
            return new InlineProgress<int>(ReportPercent);
        }

        /// <summary>Progress of a step that reports in its own type.</summary>
        public InlineProgress<T> Map<T>(Func<T, int> toPercent)
        {
            return new InlineProgress<T>(value => ReportPercent(Math.Clamp(toPercent(value), 0, 100)));
        }

        /// <summary>Progress of a step that names its own phases: a new phase becomes the current action.</summary>
        public InlineProgress<T> Steps<T>(Func<T, string> toAction, Func<T, int?> toPercent)
        {
            return new InlineProgress<T>(value =>
            {
                var action = toAction(value);
                if (action != _action) Action(action);
                if (toPercent(value) is { } raw) ReportPercent(Math.Clamp(raw, 0, 100));
            });
        }

        public InlineProgress<TransferProgress> Transfer()
        {
            return new InlineProgress<TransferProgress>(t =>
            {
                if (t.BytesTotal is > 0)
                    ReportPercent((int)Math.Clamp(t.BytesDone * 100 / t.BytesTotal.Value, 0, 100));
            });
        }

        /// <summary>Reports a percent, but only when it is different from the last one.</summary>
        private void ReportPercent(int percent)
        {
            if (percent == _lastPercent) return;
            _lastPercent = percent;
            Report(percent);
        }

        private void Report(int? percent)
        {
            target?.Report(new BuildProgress(_number, StageNames.Count, StageNames[_number - 1], _action, percent));
        }
    }
}