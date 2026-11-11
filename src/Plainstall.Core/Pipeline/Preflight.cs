using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Plainstall.Core.Pipeline;

/// <summary>Facts about the PC that preflight decides on. Gathered once, so the rules can be tested without a PC.</summary>
public sealed record HostEnvironment
{
    public required bool IsElevated { get; init; }

    /// <summary>File system of the drive that holds the work folder, for example NTFS.</summary>
    public required string? WorkDriveFormat { get; init; }

    public required long WorkDriveFreeBytes { get; init; }
    public required bool WorkDriveIsNetwork { get; init; }
    public required string WindowsDirectory { get; init; }
    public required string[] ProgramFilesDirectories { get; init; }
    public required string UserProfileRoot { get; init; }

    /// <summary>True on a Windows on ARM PC, where Plainstall would only run under x64 emulation.</summary>
    public bool IsArmPc { get; init; }

    public static HostEnvironment Detect(string workDirectory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

        string? format = null;
        long free = 0;
        var network = false;
        var root = Path.GetPathRoot(Path.GetFullPath(workDirectory));
        if (!string.IsNullOrEmpty(root) && !root.StartsWith(@"\\", StringComparison.Ordinal))
            try
            {
                var drive = new DriveInfo(root);
                network = drive.DriveType == DriveType.Network;
                if (drive.IsReady)
                {
                    format = drive.DriveFormat;
                    free = drive.AvailableFreeSpace;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Reported by the rules below as "unknown file system".
            }
        else
            network = true;

        return new HostEnvironment
        {
            IsElevated = elevated,
            WorkDriveFormat = format,
            WorkDriveFreeBytes = free,
            WorkDriveIsNetwork = network,
            WindowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            ProgramFilesDirectories =
            [
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            ],
            UserProfileRoot = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ??
                              @"C:\Users",
            IsArmPc = RuntimeInformation.OSArchitecture
                is Architecture.Arm64 or Architecture.Arm
        };
    }
}

/// <summary>The checks that run before a build touches anything. Each problem says what to do about it.</summary>
public static class Preflight
{
    /// <summary>
    ///     The size of the media before the real size is known. A little more than the current x64 ISO (about 9 GB).
    /// </summary>
    public const long AssumedMediaBytes = 9L << 30;

    /// <summary>
    ///     Space a build needs in the work folder. The media copy, the single-edition working image and the
    ///     final export need about two times the size of the source media. Updates need more room, because
    ///     servicing expands them. A reserve is for the scratch files of DISM.
    /// </summary>
    public static long RequiredBytes(long mediaBytes, long updateBytes = 0)
    {
        return 2 * mediaBytes + 3 * updateBytes + (4L << 30);
    }

    /// <summary>
    ///     Space the download of the ISO takes on the drive of the work folder: <see cref="AssumedMediaBytes" /> when
    ///     the ISO is downloaded into a folder on that drive and the folder holds no ISO yet. Else 0. When the folder
    ///     holds an ISO, the build usually uses it and downloads nothing.
    /// </summary>
    /// <param name="workDirectory">The work folder of the build.</param>
    /// <param name="downloadFolder">The folder the ISO is downloaded into, or null when the build downloads nothing.</param>
    public static long DownloadBytes(string workDirectory, string? downloadFolder)
    {
        if (downloadFolder is null || !string.Equals(Path.GetPathRoot(Path.GetFullPath(workDirectory)),
                Path.GetPathRoot(Path.GetFullPath(downloadFolder)), StringComparison.OrdinalIgnoreCase))
            return 0;
        try
        {
            if (Directory.Exists(downloadFolder) && Directory.EnumerateFiles(downloadFolder, "*.iso")
                    .Any(f => f.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)))
                return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be read counts as empty: the check then asks for more space, not less.
        }

        return AssumedMediaBytes;
    }

    public static IReadOnlyList<string> Check(string workDirectory, HostEnvironment host, long requiredBytes)
    {
        var problems = new List<string>();
        var work = Path.GetFullPath(workDirectory).TrimEnd('\\');

        if (host.IsArmPc)
            problems.Add(
                "This PC has an ARM processor. Plainstall runs on x64 PCs (Intel and AMD) only. Use an x64 PC to make the installer.");

        if (!host.IsElevated)
            problems.Add(
                "Plainstall needs administrator rights to change a Windows image. Start it again with 'Run as administrator'.");

        if (host.WorkDriveIsNetwork)
            problems.Add($"The work folder '{work}' is on a network location. Choose a folder on a local drive.");
        else if (!string.Equals(host.WorkDriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            problems.Add(
                $"The work folder '{work}' is on a drive formatted as {host.WorkDriveFormat ?? "an unknown file system"}. Windows images can only be opened on NTFS. Choose a folder on an NTFS drive.");

        if (Path.GetPathRoot(work)?.TrimEnd('\\') == work)
            problems.Add(
                $"'{work}' is the root of a drive. Choose a folder, for example '{Path.Combine(work + "\\", "Plainstall-work")}'.");
        problems.AddRange(host.ProgramFilesDirectories.Append(host.WindowsDirectory)
            .Where(d => !string.IsNullOrEmpty(d) && IsSameOrInside(work, d))
            .Select(forbidden => $"The work folder must not be inside '{forbidden}'."));
        if (string.Equals(work, host.UserProfileRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetDirectoryName(work), host.UserProfileRoot.TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
            problems.Add($"'{work}' is a user profile folder. Choose a folder inside it, or on another drive.");

        if (host is { WorkDriveIsNetwork: false, WorkDriveFormat: not null } && host.WorkDriveFreeBytes < requiredBytes)
            problems.Add(
                $"Not enough free space on {Path.GetPathRoot(work)}: about {ToGb(requiredBytes)} GB are needed, {ToGb(host.WorkDriveFreeBytes)} GB are free. Free up space or choose a work folder on another drive.");

        return problems;
    }

    public static void ThrowIfAny(IReadOnlyList<string> problems)
    {
        if (problems.Count > 0)
            throw PlainstallException.WithList(ErrorKind.Preflight, "The build cannot start:", problems);
    }

    private static bool IsSameOrInside(string path, string parent)
    {
        var p = Path.GetFullPath(parent).TrimEnd('\\');
        return path.Equals(p, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string ToGb(long bytes)
    {
        return (bytes / (double)(1L << 30)).ToString("0.#", CultureInfo.InvariantCulture);
    }
}