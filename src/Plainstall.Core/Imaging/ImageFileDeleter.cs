using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Plainstall.Core.Imaging;

/// <summary>
///     Deletes files and folders inside a mounted image the way a backup program would: by opening each
///     entry with backup semantics and marking it for deletion through the handle.
/// </summary>
/// <remarks>
///     Many files in an image belong to TrustedInstaller. The usual workaround, taking ownership and
///     rewriting access lists, is slow and changes permissions that hard-linked copies share. With the
///     restore privilege enabled, a backup-semantics open is granted delete access without touching them.
/// </remarks>
public static partial class ImageFileDeleter
{
    private const uint Delete = 0x00010000;
    private const uint FileShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const int FileDispositionInfoEx = 21;
    private const uint DispositionDelete = 0x1;
    private const uint DispositionPosixSemantics = 0x2;
    private const uint DispositionIgnoreReadOnly = 0x10;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle file, int informationClass,
        in uint information, uint bufferSize);

    /// <summary>
    ///     Resolves a path from the catalog against the image root and refuses anything that would end up
    ///     outside it. The catalog validator already rejects such paths; this is the second lock.
    /// </summary>
    public static string ResolveInside(string imageRoot, string relativePath)
    {
        var root = Path.GetFullPath(imageRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || full.Length <= root.Length)
            throw new PlainstallException(ErrorKind.InvalidInput,
                $"The path '{relativePath}' points outside the image.");
        return full;
    }

    /// <summary>Deletes one file. Returns false when it does not exist.</summary>
    public static bool DeleteFile(string path)
    {
        if (!File.Exists(path)) return false;
        DeleteEntry(path);
        return true;
    }

    /// <summary>
    ///     Deletes a folder with its content. Links (junctions, symbolic links) are removed as links and
    ///     never followed, so nothing outside the folder can be reached through them. Returns false when
    ///     the folder does not exist.
    /// </summary>
    public static bool DeleteDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists) return false;
        DeleteDirectoryCore(info);
        return true;
    }

    private static void DeleteDirectoryCore(DirectoryInfo directory)
    {
        if (directory.LinkTarget is null)
            foreach (var entry in directory.EnumerateFileSystemInfos())
                if (entry is DirectoryInfo child)
                    DeleteDirectoryCore(child);
                else
                    DeleteEntry(entry.FullName);

        DeleteEntry(directory.FullName);
    }

    private static void DeleteEntry(string path)
    {
        using var handle = CreateFile(LongPath(path), Delete, FileShareAll, IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid) throw Failure(path, Marshal.GetLastPInvokeError());
        var flags = DispositionDelete | DispositionPosixSemantics | DispositionIgnoreReadOnly;
        if (!SetFileInformationByHandle(handle, FileDispositionInfoEx, in flags, sizeof(uint)))
            throw Failure(path, Marshal.GetLastPInvokeError());
    }

    private static string LongPath(string path)
    {
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : @"\\?\" + path;
    }

    private static PlainstallException Failure(string path, int error)
    {
        return new PlainstallException(ErrorKind.Imaging,
            $"Could not delete '{path}': {new Win32Exception(error).Message} (error {error})");
    }
}