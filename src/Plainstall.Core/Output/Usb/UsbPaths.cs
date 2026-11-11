using System.Runtime.InteropServices;

namespace Plainstall.Core.Output.Usb;

/// <summary>
///     Answers "is this path on that volume?" for the safety rules. A path can name its volume by drive
///     letter (<c>E:\work</c>) or by the volume's own name (<c>\\?\Volume{...}\work</c>); a volume mounted in a
///     folder has only the second, which is why the pipeline should describe its paths with <see cref="RootsOf" />.
/// </summary>
public static partial class UsbPaths
{
    private const string VolumePrefix = "Volume{";

    /// <summary>
    ///     The drive root of <paramref name="path" /> (for example <c>E:\</c>) and, when Windows can tell, the
    ///     name of the volume the path really lives on (for example <c>\\?\Volume{...}\</c>), which is different
    ///     from the letter's volume when a stick is mounted in a folder. Put both into
    ///     <see cref="UsbSelectionContext.PathsInUse" />. Reads only names; needs no administrator rights.
    /// </summary>
    public static IReadOnlyList<string> RootsOf(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var roots = new List<string>();
        if (VolumeNameOf(path) is { } directVolume)
        {
            roots.Add($@"\\?\{directVolume}\");
            return roots;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            roots.Add(path);
            return roots;
        }

        if (DriveLetterOf(full) is { } letter) roots.Add($@"{letter}:\");
        if (OperatingSystem.IsWindows() && ResolveVolumeName(full) is { } volume) roots.Add(volume);
        if (roots.Count == 0) roots.Add(full);
        return roots;
    }

    /// <summary>
    ///     True when <paramref name="path" /> lies on <paramref name="volume" />, judged by drive letter
    ///     (case-insensitive) or by volume name. A path that names neither (a network share) is not on any stick.
    /// </summary>
    public static bool IsOnVolume(string path, VolumeFacts volume)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var pathVolume = VolumeNameOf(path);
        if (pathVolume is not null)
            return volume.VolumePath is { } vp && VolumeNameOf(vp) is { } own
                                               && string.Equals(pathVolume, own, StringComparison.OrdinalIgnoreCase);
        var pathLetter = DriveLetterOf(path);
        var volumeLetter = volume.DriveLetter is { } dl ? DriveLetterOf(dl) : null;
        return pathLetter is not null && volumeLetter is not null && pathLetter == volumeLetter;
    }

    /// <summary>The upper-case drive letter a path starts with, also behind <c>\\?\</c> or <c>\\.\</c>; null otherwise.</summary>
    internal static char? DriveLetterOf(string path)
    {
        var p = StripDevicePrefix(path.Trim());
        if (p is [_, ':', ..] && char.IsAsciiLetter(p[0])) return char.ToUpperInvariant(p[0]);
        return null;
    }

    /// <summary>
    ///     The <c>Volume{guid}</c> part of a path such as <c>\\?\Volume{guid}\sources</c>, or null when the path
    ///     does not start with a volume name.
    /// </summary>
    internal static string? VolumeNameOf(string path)
    {
        var p = path.Trim();
        if (!(p.StartsWith(@"\\?\", StringComparison.Ordinal) ||
              p.StartsWith(@"\\.\", StringComparison.Ordinal))) return null;
        p = p[4..];
        if (!p.StartsWith(VolumePrefix, StringComparison.OrdinalIgnoreCase)) return null;
        var end = p.IndexOf('}');
        return end < 0 ? null : p[..(end + 1)];
    }

    private static string StripDevicePrefix(string path)
    {
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)
            ? path[4..]
            : path;
    }

    /// <summary>
    ///     Asks Windows which volume holds <paramref name="fullPath" />: first the mount point (a drive root or a
    ///     mount folder), then that mount point's volume name. Works for paths that do not exist yet.
    /// </summary>
    private static string? ResolveVolumeName(string fullPath)
    {
        var mountPoint = new char[Math.Max(fullPath.Length + 2, 261)];
        if (!GetVolumePathNameW(fullPath, mountPoint, (uint)mountPoint.Length)) return null;
        var mount = new string(mountPoint, 0,
            Array.IndexOf(mountPoint, '\0') is var n and >= 0 ? n : mountPoint.Length);
        if (!mount.EndsWith('\\')) mount += '\\';
        var volume = new char[64];
        if (!GetVolumeNameForVolumeMountPointW(mount, volume, (uint)volume.Length)) return null;
        var name = new string(volume, 0, Array.IndexOf(volume, '\0') is var m and >= 0 ? m : volume.Length);
        return VolumeNameOf(name) is null ? null : name;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumePathNameW(string fileName, [Out] char[] volumePathName, uint bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumeNameForVolumeMountPointW(string volumeMountPoint, [Out] char[] volumeName,
        uint bufferLength);
}