using Microsoft.Extensions.Logging;
using Plainstall.Core.Imaging;
using Plainstall.Core.Output.Usb;

namespace Plainstall.Core.Pipeline;

/// <summary>
///     Shows that a build or a cleanup runs in this Windows session. A cleanup takes it too, so it never releases or
///     deletes the files of a build that still runs. Windows releases it when the process stops, also after a crash.
/// </summary>
public sealed class BuildLock : IDisposable
{
    public const string Busy =
        "Another Plainstall build or cleanup runs on this PC. Wait until it is done. Then try again.";

    private const string Name = @"Local\Plainstall.Build";
    private readonly Semaphore _semaphore;

    private BuildLock(Semaphore semaphore)
    {
        _semaphore = semaphore;
    }

    public void Dispose()
    {
        _semaphore.Release();
        _semaphore.Dispose();
    }

    /// <summary>Takes the lock. Returns null when another build or cleanup has it.</summary>
    public static BuildLock? TryTake()
    {
        Semaphore semaphore;
        try
        {
            semaphore = new Semaphore(1, 1, Name);
        }
        catch (UnauthorizedAccessException)
        {
            // A process of another account made the lock and holds it.
            return null;
        }

        if (semaphore.WaitOne(0)) return new BuildLock(semaphore);
        semaphore.Dispose();
        return null;
    }

    /// <summary>Takes the lock, or stops with <see cref="Busy" /> when another build or cleanup has it.</summary>
    public static BuildLock Take()
    {
        return TryTake() ?? throw new PlainstallException(ErrorKind.Preflight, Busy);
    }
}

/// <summary>
///     The files that Plainstall keeps below <see cref="PlainstallPaths.DataRoot" />: downloaded Windows files, tools,
///     logs, work files and the report of the last build without an ISO. The next build downloads or makes again what
///     it needs.
/// </summary>
public static class StoredData
{
    private static readonly EnumerationOptions AllFiles = new()
    {
        RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint
    };

    /// <summary>The folders that Plainstall makes. Other folders below the data root are never touched.</summary>
    public static IReadOnlyList<string> Folders(PlainstallPaths paths)
    {
        return [paths.DefaultWork, paths.Cache, paths.Tools, paths.Logs, paths.Scratch];
    }

    /// <summary>
    ///     The report that a build without an ISO writes next to the standard work folder, in the data root.
    /// </summary>
    public static string ReportFile(PlainstallPaths paths)
    {
        return Path.Combine(paths.DataRoot, BuildReport.FileName);
    }

    /// <summary>The size of all files in <see cref="Folders" /> and of <see cref="ReportFile" />, in bytes.</summary>
    public static long Size(PlainstallPaths paths)
    {
        var report = new FileInfo(ReportFile(paths));
        return Measure(() => report.Exists ? report.Length : 0) + Folders(paths).Where(Directory.Exists)
            .Sum(folder => Measure(() => new DirectoryInfo(folder).EnumerateFiles("*", AllFiles).Sum(f => f.Length)));

        static long Measure(Func<long> size)
        {
            try
            {
                return size();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file changed while Plainstall counted. The size is only for display.
                return 0;
            }
        }
    }

    /// <summary>Starts DISM for a cleanup. Returns null when this process does not have administrator rights.</summary>
    public static IImagingService? TryStartDism(PlainstallPaths paths, ILogger? logger = null)
    {
        try
        {
            return new DismImagingService(Path.Combine(paths.Logs, "dism-cleanup.log"), paths.Scratch, logger);
        }
        catch (PlainstallException ex) when (ex.Kind == ErrorKind.Preflight)
        {
            return null;
        }
    }

    /// <summary>
    ///     Releases what an interrupted build left in <paramref name="workFolder" />. It does nothing while a build
    ///     runs. <paramref name="imaging" /> comes from <see cref="TryStartDism" />. When it is null, mounted images
    ///     are reported, not released. This method disposes it.
    /// </summary>
    public static CleanupReport CleanLeftovers(string workFolder, IImagingService? imaging, ILogger? logger = null)
    {
        try
        {
            using var running = BuildLock.TryTake();
            return running is null
                ? new CleanupReport([], [BuildLock.Busy])
                : new LeftoverCleaner(imaging, logger).Clean(new WorkLayout(workFolder));
        }
        finally
        {
            (imaging as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    ///     Deletes <see cref="Folders" /> and <see cref="ReportFile" />. First it releases what an interrupted build left in the standard work folder.
    ///     It deletes nothing while a build runs, without DISM, or while a Windows image is mounted below the data root.
    ///     <paramref name="imaging" /> comes from <see cref="TryStartDism" />. This method disposes it before it deletes
    ///     the logs, because DISM keeps its log file open.
    /// </summary>
    public static CleanupReport Delete(PlainstallPaths paths, IImagingService? imaging, ILogger? logger = null)
    {
        using var running = BuildLock.TryTake();
        CleanupReport leftovers;
        IReadOnlyList<MountedImageInfo> mounted;
        long before;
        try
        {
            if (running is null) return new CleanupReport([], [BuildLock.Busy]);
            if (imaging is null)
                return new CleanupReport([],
                    ["Deleting the Plainstall files needs administrator rights. Start Plainstall as administrator."]);
            before = Size(paths);
            leftovers = new LeftoverCleaner(imaging, logger).Clean(new WorkLayout(paths.DefaultWork));
            mounted = imaging.GetMountedImages();
        }
        finally
        {
            (imaging as IDisposable)?.Dispose();
        }

        if (!leftovers.Success) return leftovers;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.DataRoot)) + Path.DirectorySeparatorChar;
        var inUse = mounted
            .Where(m => Path.GetFullPath(m.MountPath).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            .Select(m => $"A Windows image is mounted at {m.MountPath}. Plainstall did not delete its files.")
            .ToList();
        if (inUse.Count > 0) return new CleanupReport(leftovers.Done, inUse) { Notes = leftovers.Notes };

        var problems = new List<string>();
        foreach (var folder in new[] { paths.Cache, paths.Tools, paths.Logs, paths.Scratch }.Where(Directory.Exists))
            try
            {
                foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", AllFiles))
                    file.Attributes = FileAttributes.Normal;
                Directory.Delete(folder, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"Plainstall could not delete all files in {folder}: {ex.Message}");
            }

        var reportFile = ReportFile(paths);
        try
        {
            if (File.Exists(reportFile))
            {
                File.SetAttributes(reportFile, FileAttributes.Normal);
                File.Delete(reportFile);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"Plainstall could not delete {reportFile}: {ex.Message}");
        }

        try
        {
            if (Directory.Exists(paths.DataRoot) && !Directory.EnumerateFileSystemEntries(paths.DataRoot).Any())
                Directory.Delete(paths.DataRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An empty folder that stays is not a problem.
        }

        var freed = Math.Max(0, before - Size(paths));
        return new CleanupReport(
            [.. leftovers.Done, $"Deleted {UsbSizes.Format(freed)} of Plainstall files in {paths.DataRoot}."], problems)
        {
            Notes = leftovers.Notes
        };
    }
}