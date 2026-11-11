using Microsoft.Extensions.Logging;
using Plainstall.Core.Catalog;
using Plainstall.Core.Download;
using Plainstall.Core.Imaging;
using Plainstall.Core.Output;
using Plainstall.Core.Output.Usb;
using Plainstall.Core.Unattend;

namespace Plainstall.Core.Pipeline;

/// <summary>Where Plainstall keeps its data on this PC.</summary>
/// <remarks>
///     Large and shared data lives under ProgramData, not in the user profile: the tool runs elevated, and
///     when a standard user approves the prompt with another account, the profile folders belong to that
///     other account.
/// </remarks>
public sealed record PlainstallPaths(string DataRoot)
{
    public static PlainstallPaths Default { get; } =
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Plainstall"));

    /// <summary>Downloaded ISOs.</summary>
    public string Cache => Path.Combine(DataRoot, "cache");

    /// <summary>Tools fetched at run time (oscdimg).</summary>
    public string Tools => Path.Combine(DataRoot, "tools");

    public string DefaultWork => Path.Combine(DataRoot, "work");
    public string Logs => Path.Combine(DataRoot, "logs");

    /// <summary>Temporary space for DISM during a cleanup.</summary>
    public string Scratch => Path.Combine(DataRoot, "scratch");
}

/// <summary>Wires the real implementations together. Shared by the command-line tool and the app.</summary>
public static class DefaultServices
{
    public static WindowsIsoProvider CreateIsoProvider(PlainstallPaths paths, ILoggerFactory loggers)
    {
        return new WindowsIsoProvider(
            new MicrosoftDownloadClient(logger: loggers.CreateLogger<MicrosoftDownloadClient>()),
            new ResumableDownloader(logger: loggers.CreateLogger<ResumableDownloader>()),
            new IsoCache(paths.Cache, logger: loggers.CreateLogger<IsoCache>()),
            loggers.CreateLogger<WindowsIsoProvider>());
    }

    /// <summary>Lists the disks for the USB picker. Reading them needs administrator rights.</summary>
    public static IUsbDiskService CreateUsbDiskService(ILoggerFactory loggers)
    {
        return new UsbDiskService(new StorageDiskOperations(loggers.CreateLogger("Usb")));
    }

    /// <param name="paths">The folders Plainstall works in: cache, tools and the work area.</param>
    /// <param name="loggers">Creates the loggers for the services.</param>
    /// <param name="sourceIso">An ISO or media folder the user supplied, or null to download from Microsoft.</param>
    public static PipelineServices Create(PlainstallPaths paths, ILoggerFactory loggers, string? sourceIso)
    {
        var processRunner = new ProcessRunner();
        var log = loggers.CreateLogger("Plainstall");
        return new PipelineServices
        {
            Catalog = CatalogLoader.LoadEmbedded(),
            Source = sourceIso is not null
                ? new GivenSource(sourceIso)
                : new DownloadSource(CreateIsoProvider(paths, loggers)),
            MediaOpener = new MediaOpener(),
            ImagingFactory = layout => new DismImagingService(layout.DismLog, layout.Scratch, log),
            WimToolFactory = layout => new DismExeWimTool(processRunner, layout.Scratch, layout.DismLog, logger: log),
            Hives = new OfflineHiveSessionFactory(),
            Oscdimg = new OscdimgProvider(new OscdimgLocator(
                new OscdimgLocatorOptions { ToolsFolder = paths.Tools },
                logger: log)),
            ProcessRunner = processRunner,
            UsbWriterFactory = (wimTool, journal) =>
                new UsbWriter(new StorageDiskOperations(log), wimTool, journal, log),
            HostFacts = HostFacts.Current,
            HostEnvironment = HostEnvironment.Detect
        };
    }
}