using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Plainstall.Core.Registry;

/// <summary>The few registry and token functions that .NET does not expose.</summary>
internal static partial class NativeRegistry
{
    public const int ErrorSuccess = 0;
    public const int ErrorFileNotFound = 2;
    public const int ErrorPathNotFound = 3;
    public const int ErrorAccessDenied = 5;

    public const int KeyQueryValue = 0x0001;
    public const int KeyAllAccess = 0xF003F;
    public const int RegOptionBackupRestore = 0x00000004;

    private const uint SePrivilegeEnabled = 0x00000002;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;

    // The int cast makes the value negative, so the implicit widening to IntPtr sign-extends it to
    // 0xFFFFFFFF80000002 on 64-bit, which is the value Windows uses for HKEY_LOCAL_MACHINE.
    public static readonly IntPtr HkeyLocalMachine = unchecked((int)0x80000002);

    [LibraryImport("advapi32.dll", EntryPoint = "RegLoadKeyW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int RegLoadKey(IntPtr hKey, string subKey, string file);

    [LibraryImport("advapi32.dll", EntryPoint = "RegUnLoadKeyW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int RegUnLoadKey(IntPtr hKey, string subKey);

    [LibraryImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int RegOpenKeyEx(SafeRegistryHandle hKey, string subKey, int options, int samDesired,
        out SafeRegistryHandle result);

    [LibraryImport("advapi32.dll", EntryPoint = "RegCreateKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int RegCreateKeyEx(SafeRegistryHandle hKey, string subKey, int reserved, string? @class,
        int options,
        int samDesired, IntPtr securityAttributes, out SafeRegistryHandle result, out int disposition);

    [LibraryImport("advapi32.dll")]
    public static partial int RegFlushKey(SafeRegistryHandle hKey);

    /// <summary>Deletes the key the handle refers to. The key must have no subkeys.</summary>
    [LibraryImport("ntdll.dll")]
    public static partial int NtDeleteKey(SafeRegistryHandle keyHandle);

    [LibraryImport("ntdll.dll")]
    public static partial int RtlNtStatusToDosError(int status);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(IntPtr processHandle, uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustTokenPrivileges(SafeAccessTokenHandle tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        in TokenPrivilegesSingle newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    /// <summary>
    ///     Enables one privilege in the process token and returns the Win32 error: 0 when it is enabled,
    ///     1300 (not all assigned) when the account does not hold it, anything else when a call failed.
    ///     An elevated administrator holds the backup and restore privileges, but they start out disabled.
    /// </summary>
    public static int EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
            return Marshal.GetLastPInvokeError();
        using (token)
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) return Marshal.GetLastPInvokeError();
            var state = new TokenPrivilegesSingle { PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            // Success or not, the last error tells: the call also succeeds when the privilege was not
            // assigned, and then sets 1300.
            _ = AdjustTokenPrivileges(token, false, in state, 0, IntPtr.Zero, IntPtr.Zero);
            return Marshal.GetLastPInvokeError();
        }
    }

    public static string Describe(int win32Error)
    {
        return $"{new Win32Exception(win32Error).Message} (error {win32Error})";
    }

    // ---- token privileges ----

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivilegesSingle
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }
}

/// <summary>Privileges Plainstall needs for offline servicing.</summary>
public static class ServicingPrivileges
{
    public const string Backup = "SeBackupPrivilege";
    public const string Restore = "SeRestorePrivilege";
    public const string Security = "SeSecurityPrivilege";
    public const string TakeOwnership = "SeTakeOwnershipPrivilege";

    /// <summary>
    ///     Enables backup, restore, security and take-ownership in this process. DISM child processes
    ///     inherit them enabled, which avoids error 1314 on some hosts. Returns the names that could not
    ///     be enabled. A non-empty result means the process is not elevated.
    /// </summary>
    public static IReadOnlyList<string> EnableAll()
    {
        string[] names = [Backup, Restore, Security, TakeOwnership];
        return [.. names.Where(name => NativeRegistry.EnablePrivilege(name) != NativeRegistry.ErrorSuccess)];
    }
}