using System.CommandLine;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Plainstall.Core;
using Plainstall.Core.Catalog;
using Plainstall.Core.Download;
using Plainstall.Core.Media;
using Plainstall.Core.Output.Usb;
using Plainstall.Core.Pipeline;
using Plainstall.Core.Profiles;

namespace Plainstall.Cli;

/// <summary>
///     The command-line host. It parses arguments, wires the real services and prints results. Plainstall.Core
///     makes every decision about a Windows image.
/// </summary>
internal static class Program
{
    // The exit codes of Plainstall.Cli.
    private const int Success = 0;
    private const int Failed = 1;
    private const int InvalidInput = 2;
    private const int Cancelled = 3;
    private const int PreflightRefused = 4;

    private static readonly string Version =
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.0.0";

    private static readonly JsonSerializerOptions
        JsonLine = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    ///     Ctrl+C only asks the command to stop, and Plainstall waits until it did. A build finishes its current step,
    ///     releases what is mounted or loaded and writes its report. By default System.CommandLine ends the process
    ///     two seconds after Ctrl+C, in the middle of that cleanup.
    /// </summary>
    private static readonly InvocationConfiguration Invocation =
        new() { ProcessTerminationTimeout = Timeout.InfiniteTimeSpan };

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var root = new RootCommand("Plainstall makes a clean Windows 11 installer from the official ISO.")
        {
            BuildCommand(), PlanCommand(), InspectCommand(), DownloadCommand(), DisksCommand(), CatalogCommand(),
            ProfileCommand(), CleanupCommand()
        };
        return await root.Parse(args).InvokeAsync(Invocation).ConfigureAwait(false);
    }

    // ---- build ---------------------------------------------------------------------------------

    private static Command BuildCommand()
    {
        var profileOption = new Option<FileInfo>("--profile")
            { Description = "The profile to build (*.plainstall.json).", Required = true };
        var sourceOption = new Option<string?>("--source")
        {
            Description =
                "A Windows 11 ISO, or a folder with its content. Without it the ISO is downloaded from Microsoft."
        };
        var isoOption = new Option<string?>("--iso") { Description = "Write an ISO to this path." };
        var usbOption = new Option<int?>("--usb")
        {
            Description =
                "Write a USB stick: the disk number from 'Plainstall.Cli disks'. EVERYTHING on that disk is erased."
        };
        var eraseSerialOption = new Option<string?>("--erase-disk-serial")
        {
            Description =
                "Confirms erasing the --usb disk without a question. Give the serial number that 'Plainstall.Cli disks' shows for it. For scripts."
        };
        var externalDriveOption = new Option<bool>("--external-drive")
        {
            Description =
                "Confirms that the --usb disk is a USB stick, although Windows reports it as an external drive. Without it such a disk is refused."
        };
        var workOption = new Option<string?>("--work")
            { Description = "Work folder (NTFS, about twice the ISO size free). Default: under ProgramData." };
        var passwordOption = new Option<bool>("--password-stdin")
            { Description = "Read the account password from standard input (one line)." };
        var keepOption = new Option<bool>("--keep-work")
            { Description = "Keep the work folder with the prepared installation files." };
        var overwriteOption = new Option<bool>("--overwrite") { Description = "Replace an existing output file." };
        var jsonOption = new Option<bool>("--json") { Description = "Print progress and the result as JSON lines." };
        var vmTestOption = new Option<bool>("--vm-test")
        {
            Description =
                "FOR VIRTUAL MACHINE TESTS ONLY. The media then erases disk 0 of whatever PC boots it and signs in once, without asking."
        };

        var command = new Command("build", "Build installation media from a profile.")
        {
            profileOption, sourceOption, isoOption, usbOption, eraseSerialOption, externalDriveOption, workOption,
            passwordOption, keepOption, overwriteOption, jsonOption, vmTestOption
        };
        command.SetAction(async (parse, cancellationToken) =>
        {
            var json = parse.GetValue(jsonOption);
            return await RunAsync(json, async () =>
            {
                var paths = PlainstallPaths.Default;
                var (profile, warnings) = new ProfileSerializer().Load(parse.GetRequiredValue(profileOption).FullName);
                foreach (var warning in warnings)
                    await Console.Error.WriteLineAsync("Note: " + warning).ConfigureAwait(false);
                var iso = parse.GetValue(isoOption);
                var usbNumber = parse.GetValue(usbOption);
                if (iso is null && usbNumber is null && !parse.GetValue(keepOption))
                    throw new PlainstallException(ErrorKind.InvalidInput,
                        "Nothing to produce. Give --iso <file>, --usb <disk number>, or --keep-work to keep the prepared installation files.");

                string? password = null;
                if (parse.GetValue(passwordOption))
                    password = await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false) ?? "";
                else if (profile.Setup.LocalAccount is { AskPassword: true })
                    throw new PlainstallException(ErrorKind.InvalidInput,
                        "This profile asks for an account password. Give it on standard input with --password-stdin.");
                if (password is not null && !json)
                    await Console.Error
                        .WriteLineAsync(
                            "Note: the password is written into the answer file on the media in a form that can be decoded. Keep the media safe and change the password after installation.")
                        .ConfigureAwait(false);

                using var running = BuildLock.Take();
                var work = Path.GetFullPath(parse.GetValue(workOption) ?? paths.DefaultWork);
                var logPath = Path.Combine(paths.Logs, $"plainstall-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                using var fileLog = new FileLoggerProvider(logPath);
                using var loggers = new LoggerFactory([fileLog],
                    new LoggerFilterOptions { MinLevel = LogLevel.Information });
                var services = DefaultServices.Create(paths, loggers, parse.GetValue(sourceOption));
                var pipeline = new BuildPipeline(services, loggers.CreateLogger("Build"));

                // The stick is read and confirmed before the build starts. The writer compares it again before it erases.
                DiskFacts? usbDisk = null;
                var externalDrive = parse.GetValue(externalDriveOption);
                if (usbNumber is { } number)
                {
                    var usbContext = new UsbSelectionContext
                    {
                        PathsInUse =
                        [
                            .. UsbPaths.RootsOf(work), .. iso is null ? [] : UsbPaths.RootsOf(Path.GetFullPath(iso)),
                            .. parse.GetValue(sourceOption) is { } given
                                ? UsbPaths.RootsOf(Path.GetFullPath(given))
                                : UsbPaths.RootsOf(paths.Cache)
                        ],
                        AllowExternalDrives = externalDrive
                    };
                    usbDisk = await ConfirmUsbDiskAsync(DefaultServices.CreateUsbDiskService(loggers), number,
                            usbContext,
                            parse.GetValue(eraseSerialOption), !json && !parse.GetValue(passwordOption),
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                var request = new BuildRequest
                {
                    Profile = profile,
                    WorkDirectory = work,
                    SourcePath = parse.GetValue(sourceOption) is { } source ? Path.GetFullPath(source) : null,
                    IsoOutputPath = iso is null ? null : Path.GetFullPath(iso),
                    UsbDisk = usbDisk,
                    UsbExternalDriveConfirmed = usbDisk is not null && externalDrive,
                    OverwriteOutput = parse.GetValue(overwriteOption),
                    AccountPassword = password,
                    KeepWorkFolder = parse.GetValue(keepOption),
                    VmTest = parse.GetValue(vmTestOption),
                    AppVersion = Version
                };
                if (request.VmTest && !json)
                    await Console.Error
                        .WriteLineAsync(
                            "WARNING: --vm-test media erases disk 0 of any PC that boots it, without asking. Use it in a virtual machine only.")
                        .ConfigureAwait(false);

                // Said once, at the first Ctrl+C. The build stops between steps, never in the middle of one.
                await using var stopping = cancellationToken.Register(() =>
                {
                    if (!json)
                        Console.Error.WriteLine(
                            "Stopping after the current step. Plainstall then cleans up and writes the report. This can take several minutes. Wait until it ends.");
                });
                var result = await pipeline.RunAsync(request, new ConsoleProgress(json), cancellationToken)
                    .ConfigureAwait(false);
                var (report, reportPath, isoPath) = result;
                if (json)
                {
                    WriteJson(new
                    {
                        type = "result", result = report.Result, error = report.Error, iso = isoPath,
                        usb = report.Output?.Usb, report = reportPath, log = logPath, warnings = report.Warnings
                    });
                }
                else
                {
                    Console.WriteLine();
                    foreach (var warning in report.Warnings) Console.WriteLine("Warning: " + warning);
                    Console.WriteLine(report.Result switch
                    {
                        "succeeded" => "Done." + (isoPath is null ? "" : " ISO: " + isoPath),
                        "completed-with-failures" => "Finished, but some changes could not be applied." +
                                                     (isoPath is null ? "" : " ISO: " + isoPath),
                        "cancelled" => "Cancelled.",
                        _ => "The build failed: " + report.Error
                    });
                    if (report.Output?.Usb is { } stick)
                        Console.WriteLine($"USB stick: {stick.Disk} ({stick.VolumeRoot})" + (stick.Ejected
                            ? ". It can be unplugged."
                            : ". Use \"Safely Remove Hardware\" before unplugging it."));
                    if (request.KeepWorkFolder && report.Output?.StagingFolder is { } staging)
                        Console.WriteLine("Installation files: " + staging);
                    Console.WriteLine("Report: " + reportPath);
                    Console.WriteLine("Log:    " + logPath);
                }

                return report.Result switch
                {
                    "succeeded" => Success,
                    "cancelled" => Cancelled,
                    _ => ExitCodeFor(result.ErrorKind)
                };
            }).ConfigureAwait(false);
        });
        return command;
    }

    /// <summary>
    ///     Reads the disk, refuses it when it is not a stick Plainstall may erase, shows what is on it, and
    ///     returns it only after the user confirmed: by typing ERASE, or in a script by giving its serial number.
    /// </summary>
    private static async Task<DiskFacts> ConfirmUsbDiskAsync(IUsbDiskService disks, int number,
        UsbSelectionContext context,
        string? confirmedSerial, bool canAsk, CancellationToken cancellationToken)
    {
        var disk = await disks.GetAsync(number, cancellationToken).ConfigureAwait(false)
                   ?? throw new PlainstallException(ErrorKind.InvalidInput,
                       $"There is no disk {number}. See 'Plainstall.Cli disks'.");
        var verdict = UsbDiskRules.Evaluate(disk, context);
        if (!verdict.IsEligible)
        {
            var hint = verdict.IsExternalDrive && !context.AllowExternalDrives
                ? Environment.NewLine +
                  "If this disk is a USB stick whose content you do not need, add --external-drive."
                : "";
            throw new PlainstallException(ErrorKind.InvalidInput,
                $"Disk {number} ({disk.FriendlyName}) cannot be used:" + Environment.NewLine +
                string.Join(Environment.NewLine, verdict.Reasons.Select(r => "  - " + r)) + hint);
        }

        if (confirmedSerial is not null)
        {
            if (string.IsNullOrWhiteSpace(disk.SerialNumber) || !string.Equals(disk.SerialNumber.Trim(),
                    confirmedSerial.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new PlainstallException(ErrorKind.InvalidInput,
                    $"--erase-disk-serial does not match disk {number} ({disk.FriendlyName}, serial number {(string.IsNullOrWhiteSpace(disk.SerialNumber) ? "none" : disk.SerialNumber.Trim())}). Nothing was erased.");
            return disk;
        }

        if (!canAsk || Console.IsInputRedirected)
            throw new PlainstallException(ErrorKind.InvalidInput,
                "Erasing a disk needs a confirmation, and this session cannot ask for one. Add --erase-disk-serial with the serial number 'Plainstall.Cli disks' shows for the stick.");

        Console.WriteLine();
        Console.WriteLine(
            $"USB stick to erase: disk {disk.Number}, {disk.FriendlyName}, {UsbSizes.Format(disk.SizeBytes)}");
        PrintVolumes(disk);
        if (verdict.IsExternalDrive)
            Console.WriteLine(
                "Windows reports this disk as an external drive. You confirmed with --external-drive that it is a USB stick.");
        Console.WriteLine("EVERYTHING on this disk will be erased. This cannot be undone.");
        Console.Write("Type ERASE to continue, anything else to stop: ");
        return string.Equals(Console.ReadLine()?.Trim(), "ERASE", StringComparison.Ordinal)
            ? disk
            : throw new OperationCanceledException();
    }

    private static void PrintVolumes(DiskFacts disk)
    {
        if (disk.Volumes.Count == 0) Console.WriteLine("    (no readable volume)");
        foreach (var volume in disk.Volumes)
            Console.WriteLine(
                $"    {volume.DriveLetter ?? "no letter",-9} {(string.IsNullOrEmpty(volume.Label) ? "(no name)" : volume.Label),-14} {volume.FileSystem ?? "unknown",-7} {UsbSizes.Format(volume.SizeBytes)}");
    }

    // ---- disks ---------------------------------------------------------------------------------

    private static Command DisksCommand()
    {
        var command = new Command("disks",
            "List the USB disks and say which of them can be written as a USB stick. Changes nothing.");
        command.SetAction(async (_, cancellationToken) => await RunAsync(false, async () =>
        {
            using var loggers = LoggerFactory.Create(_ => { });
            var paths = PlainstallPaths.Default;
            var context = new UsbSelectionContext
                { PathsInUse = [.. UsbPaths.RootsOf(paths.DefaultWork), .. UsbPaths.RootsOf(paths.Cache)] };
            var candidates = await DefaultServices.CreateUsbDiskService(loggers).ListAsync(context, cancellationToken)
                .ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                Console.WriteLine("No USB disk found. Plug in a USB stick and try again.");
                return Success;
            }

            foreach (var (disk, isEligible, reasons) in candidates)
            {
                Console.WriteLine(
                    $"Disk {disk.Number}: {disk.FriendlyName}, {UsbSizes.Format(disk.SizeBytes)}, serial number {(string.IsNullOrWhiteSpace(disk.SerialNumber) ? "none" : disk.SerialNumber.Trim())}");
                PrintVolumes(disk);
                Console.WriteLine(isEligible
                    ? "    Can be used with: build --usb " + disk.Number
                    : "    Cannot be used:");
                foreach (var reason in reasons) Console.WriteLine("      - " + reason);
            }

            return Success;
        }).ConfigureAwait(false));
        return command;
    }

    // ---- plan ----------------------------------------------------------------------------------

    private static Command PlanCommand()
    {
        var profileOption = new Option<FileInfo>("--profile")
            { Description = "The profile to resolve.", Required = true };
        var sourceOption = new Option<string?>("--source")
            { Description = "A Windows 11 ISO or media folder. Its build and editions decide what applies." };
        var buildOption = new Option<int>("--build")
            { Description = "Windows build to plan for when no --source is given.", DefaultValueFactory = _ => 26300 };
        var jsonOption = new Option<bool>("--json") { Description = "Print the plan as JSON." };
        var command = new Command("plan", "Show what a build would do, without changing anything.")
            { profileOption, sourceOption, buildOption, jsonOption };
        command.SetAction(async (parse, cancellationToken) => await RunAsync(parse.GetValue(jsonOption), async () =>
        {
            var profile = new ProfileSerializer().Load(parse.GetRequiredValue(profileOption).FullName).Profile;
            var catalog = CatalogLoader.LoadEmbedded();
            ImageFacts facts;
            if (parse.GetValue(sourceOption) is { } source)
            {
                var media = await ReadMediaAsync(source, cancellationToken).ConfigureAwait(false);
                var edition = media.SelectEdition(profile.Windows.Edition);
                facts = new ImageFacts(edition.Build, edition.EditionId);
            }
            else
            {
                facts = new ImageFacts(parse.GetValue(buildOption), profile.Windows.Edition);
            }

            var plan = BuildPlanner.Create(profile, catalog, facts);
            if (parse.GetValue(jsonOption))
            {
                WriteJson(new
                {
                    image = new { facts.Build, edition = facts.EditionId, arch = MediaInfo.ArchitectureName },
                    entries = plan.Entries.Select(e => e.Id),
                    actions = plan.Actions.Select(a => new
                        { entry = a.EntryId, kind = a.Action.KindName, target = a.Action.Target }),
                    notes = plan.Notes.Select(n => n.Message),
                    install = plan.WingetInstalls
                });
                return Success;
            }

            Console.WriteLine(
                $"Plan for '{profile.Name}' on {facts.EditionId}, build {facts.Build}, {MediaInfo.ArchitectureName}");
            foreach (var group in plan.Entries.GroupBy(e => e.Kind))
            {
                Console.WriteLine();
                Console.WriteLine(group.Key == CatalogKind.Apps ? "Apps to remove:" : "Changes:");
                foreach (var entry in group)
                    Console.WriteLine($"  [{entry.Risk.ToString().ToLowerInvariant(),-8}] {entry.Title}  ({entry.Id})");
            }

            if (plan.WingetInstalls.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Apps to install at first sign-in: " + string.Join(", ", plan.WingetInstalls));
            }

            foreach (var note in plan.Notes) Console.WriteLine("Note: " + note.Message);
            Console.WriteLine();
            Console.WriteLine($"{plan.Entries.Count} entries, {plan.Actions.Count} single changes.");
            return Success;
        }).ConfigureAwait(false));
        return command;
    }

    // ---- inspect -------------------------------------------------------------------------------

    private static Command InspectCommand()
    {
        var sourceArgument = new Argument<string>("source")
            { Description = "A Windows 11 ISO, or a folder with its content." };
        var jsonOption = new Option<bool>("--json") { Description = "Print as JSON." };
        var command =
            new Command("inspect", "Show the editions, build, language and architecture of installation media.")
                { sourceArgument, jsonOption };
        command.SetAction(async (parse, cancellationToken) => await RunAsync(parse.GetValue(jsonOption), async () =>
        {
            var media = await ReadMediaAsync(parse.GetRequiredValue(sourceArgument), cancellationToken)
                .ConfigureAwait(false);
            if (parse.GetValue(jsonOption))
            {
                WriteJson(new
                {
                    version = media.Version.ToString(),
                    arch = MediaInfo.ArchitectureName,
                    language = media.Language,
                    installImage = new
                    {
                        file = Path.GetFileName(media.InstallImage.Path), bytes = media.InstallImage.FileSize,
                        compression = media.InstallImage.Compression.ToString()
                    },
                    setupImage = new { media.SetupImage.Index, media.SetupImage.Name, build = media.SetupImage.Build },
                    biosBoot = media.HasBiosBootLoader,
                    editions = media.Editions.Select(e => new { e.Index, e.Name, e.EditionId })
                });
                return Success;
            }

            Console.WriteLine($"Windows {media.Version}, {MediaInfo.ArchitectureName}, {media.Language}");
            Console.WriteLine(
                $"Install image: {Path.GetFileName(media.InstallImage.Path)}, {UsbSizes.Format(media.InstallImage.FileSize)}, {media.InstallImage.Compression}");
            Console.WriteLine(
                $"Setup image:   index {media.SetupImage.Index}, \"{media.SetupImage.Name}\", build {media.SetupImage.Build}");
            Console.WriteLine($"Boots on:      UEFI{(media.HasBiosBootLoader ? " and legacy BIOS" : "")}");
            Console.WriteLine("Editions:");
            foreach (var edition in media.Editions)
                Console.WriteLine($"  {edition.Index,2}  {edition.EditionId,-26} {edition.Name}");
            return Success;
        }).ConfigureAwait(false));
        return command;
    }

    private static async Task<MediaInfo> ReadMediaAsync(string source, CancellationToken cancellationToken)
    {
        await using var handle = await new MediaOpener().OpenAsync(source, NullLogger.Instance, cancellationToken)
            .ConfigureAwait(false);
        return MediaInventory.Read(handle.RootPath, cancellationToken: cancellationToken);
    }

    // ---- download ------------------------------------------------------------------------------

    private static Command DownloadCommand()
    {
        var languageOption = new Option<string>("--language")
        {
            Description = "\"auto\" (this PC's display language) or a tag such as de-DE.",
            DefaultValueFactory = _ => "auto"
        };
        var listOption = new Option<bool>("--list") { Description = "Only list the languages Microsoft offers." };
        var command = new Command("download", "Download the current Windows 11 ISO from Microsoft and verify it.")
            { languageOption, listOption };
        command.SetAction(async (parse, cancellationToken) => await RunAsync(false, async () =>
        {
            var paths = PlainstallPaths.Default;
            using var loggers = LoggerFactory.Create(_ => { });

            if (parse.GetValue(listOption))
            {
                using var client = new MicrosoftDownloadClient();
                var release = await client.GetReleaseAsync(cancellationToken).ConfigureAwait(false);
                Console.WriteLine(
                    $"{release.ProductDisplayName ?? release.EditionName} ({MediaInfo.ArchitectureName})");
                foreach (var language in release.Languages) Console.WriteLine("  " + language.LocalizedLanguage);
                return Success;
            }

            var tag = parse.GetValue(languageOption) ?? "auto";
            var culture = tag == "auto" ? CultureInfo.CurrentUICulture : CultureInfo.GetCultureInfo(tag);
            var provider = DefaultServices.CreateIsoProvider(paths, loggers);
            var progress = new TransferPrinter();
            var result = await provider.GetIsoAsync(culture, progress, cancellationToken).ConfigureAwait(false);
            progress.Finish();
            Console.WriteLine(result.Path);
            Console.WriteLine("SHA-256: " + result.Sha256);
            Console.WriteLine("The file is the same as the file that Microsoft publishes (SHA-256 checksum).");
            return Success;
        }).ConfigureAwait(false));
        return command;
    }

    // ---- catalog -------------------------------------------------------------------------------

    private static Command CatalogCommand()
    {
        var list = new Command("list", "List every app and tweak in the catalog.");
        list.SetAction(_ => Run(() =>
        {
            var catalog = CatalogLoader.LoadEmbedded();
            foreach (var (title, entries, categories) in new[]
                     {
                         ("APPS THAT CAN BE REMOVED", catalog.Apps, catalog.AppCategories),
                         ("TWEAKS", catalog.Tweaks, catalog.TweakCategories)
                     })
            {
                Console.WriteLine(title);
                foreach (var category in categories)
                {
                    Console.WriteLine();
                    Console.WriteLine("  " + category.Title);
                    foreach (var entry in entries.Where(e => e.Category == category.Id))
                    {
                        var presets = entry.BoundTo is not null ? "setup option" :
                            entry.DefaultIn.Count == 0 ? "opt-in" : string.Join(",", entry.DefaultIn);
                        Console.WriteLine(
                            $"    {entry.Id,-32} {entry.Risk.ToString().ToLowerInvariant(),-8} {entry.Title}  [{presets}]");
                    }
                }

                Console.WriteLine();
            }

            return Success;
        }));

        var idArgument = new Argument<string>("id") { Description = "A catalog id, for example 'onedrive'." };
        var show = new Command("show", "Show one catalog entry with everything it changes.") { idArgument };
        show.SetAction(parse => Run(() =>
        {
            var id = parse.GetRequiredValue(idArgument);
            var entry = CatalogLoader.LoadEmbedded().FindEntry(id)
                        ?? throw new PlainstallException(ErrorKind.InvalidInput,
                            $"There is no catalog entry '{id}'. See 'Plainstall.Cli catalog list'.");
            Console.WriteLine($"{entry.Title}  ({entry.Id})");
            Console.WriteLine(entry.Description);
            if (entry.Details is not null) Console.WriteLine(entry.Details);
            Console.WriteLine(
                $"Risk: {entry.Risk.ToString().ToLowerInvariant()}   Presets: {(entry.DefaultIn.Count == 0 ? "none (opt-in)" : string.Join(", ", entry.DefaultIn))}");
            if (entry.MinBuild is not null || entry.MaxBuild is not null)
                Console.WriteLine(
                    $"Windows builds: {entry.MinBuild?.ToString(CultureInfo.InvariantCulture) ?? "any"} to {entry.MaxBuild?.ToString(CultureInfo.InvariantCulture) ?? "any"}");
            if (entry.Reinstall is { } reinstall)
                Console.WriteLine(
                    $"Get it back: {reinstall.Method}{(reinstall.Id is null ? "" : " " + reinstall.Id)}{(reinstall.Note is null ? "" : " (" + reinstall.Note + ")")}");
            Console.WriteLine("Changes:");
            foreach (var action in entry.Actions) Console.WriteLine($"  {action.KindName,-24} {action.Target}");
            if (entry.Source.Count > 0)
                Console.WriteLine("Source: " + string.Join(", ", entry.Source.Select(s => s.Name)));
            return Success;
        }));

        var directoryArgument = new Argument<string?>("directory")
        {
            Description = "A folder with apps.json, tweaks.json and presets.json. Default: the built-in catalog.",
            Arity = ArgumentArity.ZeroOrOne
        };
        var validate = new Command("validate", "Check a catalog for mistakes.") { directoryArgument };
        validate.SetAction(parse => Run(() =>
        {
            var catalog = parse.GetValue(directoryArgument) is { } directory
                ? CatalogLoader.LoadFromDirectory(directory)
                : CatalogLoader.LoadEmbedded();
            Console.WriteLine(
                $"The catalog is valid: {catalog.Apps.Count} apps, {catalog.Tweaks.Count} tweaks, {catalog.Presets.Count} presets.");
            return Success;
        }));

        return new Command("catalog", "Look at the catalog of apps and tweaks.") { list, show, validate };
    }

    // ---- profile -------------------------------------------------------------------------------

    private static Command ProfileCommand()
    {
        var presetOption = new Option<string>("--preset")
            { Description = "recommended, privacy-max, gaming or minimal.", DefaultValueFactory = _ => "recommended" };
        var outOption = new Option<FileInfo>("--out")
            { Description = "File to write (*.plainstall.json).", Required = true };
        var nameOption = new Option<string?>("--name") { Description = "A name for the profile." };
        var create = new Command("new", "Create a profile from a preset.") { presetOption, outOption, nameOption };
        create.SetAction(parse => Run(() =>
        {
            var presetId = parse.GetValue(presetOption)!;
            var preset = CatalogLoader.LoadEmbedded().FindPreset(presetId)
                         ?? throw new PlainstallException(ErrorKind.InvalidInput,
                             $"There is no preset '{presetId}'. Use recommended, privacy-max, gaming or minimal.");
            var file = parse.GetRequiredValue(outOption);
            if (file.Exists)
                throw new PlainstallException(ErrorKind.InvalidInput, $"'{file.FullName}' already exists.");
            // The same form as the app writes: "Plainstall 0.1.0".
            new ProfileSerializer().Save(ProfileFactory.FromPreset(preset, parse.GetValue(nameOption)), file.FullName,
                "Plainstall " + Version);
            Console.WriteLine("Created " + file.FullName);
            return Success;
        }));

        var fileArgument = new Argument<FileInfo>("file") { Description = "A profile file." };
        var validate = new Command("validate", "Check a profile, and convert it if it is from an older version.")
            { fileArgument };
        validate.SetAction(parse => Run(() =>
        {
            var loaded = new ProfileSerializer().Load(parse.GetRequiredValue(fileArgument).FullName);
            // Planning against the catalog catches unknown ids, protected packages and conflicts as well.
            var plan = BuildPlanner.Create(loaded.Profile, CatalogLoader.LoadEmbedded(),
                new ImageFacts(26300, loaded.Profile.Windows.Edition));
            foreach (var message in loaded.Warnings.Concat(plan.Notes.Select(n => n.Message)))
                Console.WriteLine("Note: " + message);
            Console.WriteLine($"'{loaded.Profile.Name}' is valid (profile schema {loaded.Profile.Schema}).");
            return Success;
        }));

        return new Command("profile", "Create and check profiles.") { create, validate };
    }

    // ---- cleanup -------------------------------------------------------------------------------

    private static Command CleanupCommand()
    {
        var workOption = new Option<string?>("--work")
            { Description = "The work folder to clean. Default: the standard work folder. Not used with --all." };
        var allOption = new Option<bool>("--all")
        {
            Description =
                "Also delete the files that Plainstall keeps on this PC: downloaded Windows files, tools and logs."
        };
        var command = new Command("cleanup",
                "Release what an interrupted build left behind: mounted images, loaded registry files, the work folder.")
            { workOption, allOption };
        command.SetAction(parse => Run(() =>
        {
            var paths = PlainstallPaths.Default;
            var work = parse.GetValue(workOption) ?? paths.DefaultWork;
            var all = parse.GetValue(allOption);
            if (!all && !Directory.Exists(work))
            {
                Console.WriteLine("Nothing to clean up.");
                return Success;
            }

            var imaging = StoredData.TryStartDism(paths);
            var report = all ? StoredData.Delete(paths, imaging) : StoredData.CleanLeftovers(work, imaging);
            foreach (var line in report.Done) Console.WriteLine(line);
            foreach (var note in report.Notes) Console.WriteLine("Note: " + note);
            foreach (var problem in report.Problems) Console.Error.WriteLine("Problem: " + problem);
            if (report.Done.Count == 0 && report.Success) Console.WriteLine("Nothing to clean up.");
            return report.Success ? Success : Failed;
        }));
        return command;
    }

    // ---- shared --------------------------------------------------------------------------------

    private static int Run(Func<int> action)
    {
        return RunAsync(false, () => Task.FromResult(action())).GetAwaiter().GetResult();
    }

    /// <summary>Turns the engine's exceptions into a plain message and an exit code. No stack traces for expected errors.</summary>
    private static async Task<int> RunAsync(bool json, Func<Task<int>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Report("Cancelled.", Cancelled);
            return Cancelled;
        }
        catch (PlainstallException ex)
        {
            var code = ExitCodeFor(ex.Kind);
            Report(ex.Message, code);
            return code;
        }
        catch (CultureNotFoundException ex)
        {
            Report($"'{ex.InvalidCultureName}' is not a language tag Windows knows.", InvalidInput);
            return InvalidInput;
        }

        void Report(string message, int code)
        {
            if (json)
                WriteJson(new { type = "error", message, exitCode = code });
            else
                Console.Error.WriteLine(message);
        }
    }

    /// <summary>The exit code for an error of this kind. Without a kind, the exit code is <see cref="Failed" />.</summary>
    private static int ExitCodeFor(ErrorKind? kind)
    {
        return kind switch
        {
            ErrorKind.InvalidInput => InvalidInput,
            ErrorKind.Preflight => PreflightRefused,
            ErrorKind.Cancelled => Cancelled,
            _ => Failed
        };
    }

    private static void WriteJson(object value)
    {
        Console.WriteLine(JsonSerializer.Serialize(value, JsonLine));
    }

    /// <summary>Build progress: one line per stage and action for people, JSON lines for scripts.</summary>
    private sealed class ConsoleProgress(bool json) : IProgress<BuildProgress>
    {
        private readonly Lock _gate = new();
        private string? _action;
        private int _lastPercent = -1;
        private int _stage;

        public void Report(BuildProgress value)
        {
            lock (_gate)
            {
                if (json)
                {
                    WriteJson(new
                    {
                        type = "progress", stage = value.StageNumber, stages = value.StageCount, name = value.StageName,
                        action = value.Action, percent = value.Percent
                    });
                    return;
                }

                if (value.StageNumber != _stage)
                {
                    EndPercentLine();
                    _stage = value.StageNumber;
                    _action = null;
                    Console.WriteLine($"[{value.StageNumber}/{value.StageCount}] {value.StageName}");
                }

                if (value.Action is not null && value.Action != _action)
                {
                    EndPercentLine();
                    _action = value.Action;
                    Console.WriteLine("      " + value.Action);
                }

                if (value.Percent is not { } percent || percent == _lastPercent || Console.IsOutputRedirected) return;
                _lastPercent = percent;
                Console.Write($"\r      {percent,3}%");
            }
        }

        private void EndPercentLine()
        {
            if (_lastPercent >= 0 && !Console.IsOutputRedirected) Console.WriteLine();
            _lastPercent = -1;
        }
    }

    private sealed class TransferPrinter : IProgress<WindowsIsoProgress>
    {
        private int _lastPercent = -1;
        private WindowsIsoPhase? _phase;

        public void Report(WindowsIsoProgress value)
        {
            if (value.Phase != _phase)
            {
                Finish();
                _phase = value.Phase;
                Console.Error.WriteLine(value.Phase switch
                {
                    WindowsIsoPhase.ContactingMicrosoft => "Asking Microsoft for the current Windows 11 ISO...",
                    WindowsIsoPhase.CheckingCache => "Checking the ISO that Plainstall downloaded before...",
                    WindowsIsoPhase.Downloading => "Downloading the ISO from Microsoft...",
                    _ => "Comparing the file with the checksum that Microsoft publishes..."
                });
            }

            if (value.Transfer is not { BytesTotal: > 0 } transfer || Console.IsErrorRedirected) return;
            var percent = (int)(transfer.BytesDone * 100 / transfer.BytesTotal.Value);
            if (percent == _lastPercent) return;
            _lastPercent = percent;
            // Decimal units, like the USB stick sizes (UsbSizes.Format).
            Console.Error.Write(
                $"\r  {percent,3}%  {transfer.BytesDone / 1e9:0.00} of {transfer.BytesTotal.Value / 1e9:0.00} GB");
        }

        public void Finish()
        {
            if (_lastPercent >= 0 && !Console.IsErrorRedirected) Console.Error.WriteLine();
            _lastPercent = -1;
        }
    }
}