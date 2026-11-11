using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Plainstall.Core.Media;

/// <summary>
///     The Windows functions <see cref="IsoMount" /> needs: virtual disk attach and detach, and the
///     volume lookups that find where the attached image became reachable.
/// </summary>
internal static partial class VirtualDiskNative
{
    internal const uint ErrorSuccess = 0;

    internal const uint VirtualStorageTypeDeviceIso = 1;

    /// <summary>VIRTUAL_DISK_ACCESS_READ: attach read-only, detach, get info. The only access an ISO supports.</summary>
    internal const uint VirtualDiskAccessRead = 0x000D0000;

    internal const uint AttachFlagReadOnly = 0x00000001;
    internal const uint AttachFlagNoDriveLetter = 0x00000002;

    /// <summary>VIRTUAL_STORAGE_TYPE_VENDOR_MICROSOFT.</summary>
    internal static readonly Guid VendorMicrosoft = new("EC984AEC-A0F9-47E9-901F-71415A66345B");

    [LibraryImport("virtdisk.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint OpenVirtualDisk(
        in VirtualStorageType virtualStorageType,
        string path,
        uint virtualDiskAccessMask,
        uint flags,
        IntPtr parameters,
        out SafeFileHandle handle);

    [LibraryImport("virtdisk.dll")]
    internal static partial uint AttachVirtualDisk(
        SafeFileHandle virtualDiskHandle,
        IntPtr securityDescriptor,
        uint flags,
        uint providerSpecificFlags,
        IntPtr parameters,
        IntPtr overlapped);

    [LibraryImport("virtdisk.dll")]
    internal static partial uint DetachVirtualDisk(SafeFileHandle virtualDiskHandle, uint flags,
        uint providerSpecificFlags);

    [LibraryImport("virtdisk.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint GetVirtualDiskPhysicalPath(SafeFileHandle virtualDiskHandle,
        ref uint diskPathSizeInBytes, [Out] char[] diskPath);

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstVolumeW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr FindFirstVolume([Out] char[] volumeName, uint bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextVolumeW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FindNextVolume(IntPtr findVolume, [Out] char[] volumeName, uint bufferLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FindVolumeClose(IntPtr findVolume);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint QueryDosDevice(string deviceName, [Out] char[] targetPath, uint max);

    /// <summary>
    ///     The NT device a DOS device name points at, for example "CdRom1" gives "\Device\CdRom1".
    ///     Null when the name does not exist (yet).
    /// </summary>
    internal static string? QueryDosDeviceTarget(string deviceName)
    {
        var buffer = new char[1024];
        var length = QueryDosDevice(deviceName, buffer, (uint)buffer.Length);
        return length == 0 ? null : FromNullTerminated(buffer);
    }

    /// <summary>All volume names known to the mount manager, each in the form "\\?\Volume{guid}\".</summary>
    internal static List<string> EnumerateVolumeNames()
    {
        var names = new List<string>();
        var buffer = new char[260];
        var find = FindFirstVolume(buffer, (uint)buffer.Length);
        if (find == new IntPtr(-1)) return names;
        try
        {
            do
            {
                names.Add(FromNullTerminated(buffer));
            } while (FindNextVolume(find, buffer, (uint)buffer.Length));
        }
        finally
        {
            FindVolumeClose(find);
        }

        return names;
    }

    internal static string FromNullTerminated(char[] buffer)
    {
        var end = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, end < 0 ? buffer.Length : end);
    }

    // ATTACH_VIRTUAL_DISK_FLAG_PERMANENT_LIFETIME (0x00000004) is deliberately never used: without it
    // Windows detaches the image as soon as the handle closes, which also covers a crash of this process.

    [StructLayout(LayoutKind.Sequential)]
    internal struct VirtualStorageType
    {
        public uint DeviceId;
        public Guid VendorId;
    }
}