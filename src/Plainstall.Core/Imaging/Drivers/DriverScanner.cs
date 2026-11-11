using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Plainstall.Core.Imaging.Drivers;

/// <summary>What a scan found, plus plain-English notes about folders that could not be read.</summary>
public sealed record DriverScanResult(IReadOnlyList<DriverPackage> Packages, IReadOnlyList<string> Notes)
{
    public int InfCount => Packages.Sum(p => p.Infs.Count);
}

/// <summary>
///     Finds driver packages under a folder. A package is a folder that directly contains at least one
///     <c>.inf</c>. This fits both shapes that occur:
///     <list type="bullet">
///         <item>
///             <c>dism /Export-Driver</c> output, one folder per package named
///             <c>&lt;inf&gt;_&lt;arch&gt;_&lt;16 hex&gt;</c>.
///         </item>
///         <item>
///             Folders users unpack from a vendor download, of any depth. Nested packages are handled by
///             giving every folder only the INFs directly inside it: <c>Chipset\a.inf</c> and
///             <c>Chipset\x64\b.inf</c> are two packages. Because drivers are added one INF at a time, nothing is
///             added twice, and each INF still finds the files it refers to in its subfolders.
///         </item>
///     </list>
///     Junctions and symbolic links below the root are not followed, so a link back up the tree cannot loop.
///     The order is stable: the root first, then subfolders depth first in name order.
/// </summary>
public static class DriverScanner
{
    public static DriverScanResult Scan(string root, ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        logger ??= NullLogger.Instance;
        var full = Path.GetFullPath(root);
        if (!Directory.Exists(full))
            throw new PlainstallException(ErrorKind.InvalidInput,
                File.Exists(full)
                    ? $"The driver location '{root}' is a file. Choose the folder that contains the driver files."
                    : $"The driver folder '{root}' does not exist. Check the path in the profile.");

        var packages = new List<DriverPackage>();
        var notes = new List<string>();
        var stack = new Stack<string>();
        stack.Push(full);
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = stack.Pop();
            List<string> infs;
            List<string> subfolders;
            try
            {
                var entries = new DirectoryInfo(folder).EnumerateFileSystemInfos().ToList();
                infs =
                [
                    .. entries
                        .Where(e => e is FileInfo &&
                                    Path.GetExtension(e.Name).Equals(".inf", StringComparison.OrdinalIgnoreCase))
                        .Select(e => e.FullName)
                        .Order(StringComparer.OrdinalIgnoreCase)
                ];
                subfolders =
                [
                    .. entries
                        .Where(e => e is DirectoryInfo && !e.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        .Select(e => e.FullName)
                        .Order(StringComparer.OrdinalIgnoreCase)
                ];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Could not read the driver folder {Folder}: {Message}", folder, ex.Message);
                notes.Add($"The folder '{folder}' could not be read and was skipped: {ex.Message}");
                continue;
            }

            if (infs.Count > 0)
                packages.Add(new DriverPackage
                {
                    Folder = folder,
                    Infs = [.. infs.Select(InfFile.Load)],
                    FolderArchitecture = DriverPackage.ParseExportFolderName(Path.GetFileName(folder))
                });
            for (var i = subfolders.Count - 1; i >= 0; i--) stack.Push(subfolders[i]);
        }

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Found {Packages} driver package(s) with {Infs} INF file(s) in {Root}",
                packages.Count, packages.Sum(p => p.Infs.Count), full);
        return new DriverScanResult(packages, notes);
    }
}