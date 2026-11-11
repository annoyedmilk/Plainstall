using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Plainstall.Core.Registry;

/// <summary>
///     A registry subtree that actions are applied to: the root of a loaded offline hive, or in tests an
///     ordinary key. All paths are relative to it.
/// </summary>
/// <remarks>
///     With <c>backupSemantics</c> keys are opened the way a backup program opens them, so that the access
///     lists inside an offline hive (which name TrustedInstaller or SYSTEM as owner of many keys) do not
///     block the write. That needs the backup and restore privileges, so tests against HKCU turn it off.
/// </remarks>
public sealed class RegistryRoot
{
    private readonly bool _backupSemantics;
    private readonly SafeRegistryHandle _root;

    public RegistryRoot(SafeRegistryHandle root, bool backupSemantics, string displayName)
    {
        _root = root;
        _backupSemantics = backupSemantics;
        DisplayName = displayName;
    }

    public string DisplayName { get; }

    public bool KeyExists(string path)
    {
        var error = NativeRegistry.RegOpenKeyEx(_root, path, 0, NativeRegistry.KeyQueryValue, out var handle);
        handle.Dispose();
        return error switch
        {
            NativeRegistry.ErrorSuccess => true,
            // The key is there. Without backup semantics this process may not read it.
            NativeRegistry.ErrorAccessDenied => true,
            NativeRegistry.ErrorFileNotFound or NativeRegistry.ErrorPathNotFound => false,
            _ => throw Failure("check", path, error)
        };
    }

    /// <summary>
    ///     Opens a key for reading and writing. Returns null when it does not exist and <paramref name="create" /> is
    ///     false.
    /// </summary>
    public RegistryKey? Open(string path, bool create)
    {
        if (!create && !KeyExists(path)) return null;

        int error;
        SafeRegistryHandle handle;
        if (_backupSemantics || create)
            error = NativeRegistry.RegCreateKeyEx(_root, path, 0, null,
                _backupSemantics ? NativeRegistry.RegOptionBackupRestore : 0,
                NativeRegistry.KeyAllAccess, IntPtr.Zero, out handle, out _);
        else
            error = NativeRegistry.RegOpenKeyEx(_root, path, 0, NativeRegistry.KeyAllAccess, out handle);
        if (error == NativeRegistry.ErrorSuccess) return RegistryKey.FromHandle(handle);
        handle.Dispose();
        throw Failure("open", path, error);
    }

    /// <summary>Reads one value. Null when the key or the value does not exist.</summary>
    public (object Value, RegistryValueKind Kind)? GetValue(string path, string name)
    {
        using var key = Open(path, false);
        if (key is null) return null;
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value is null ? null : (value, key.GetValueKind(name));
    }

    public void SetValue(string path, string name, object value, RegistryValueKind kind)
    {
        using var key = Open(path, true)!;
        key.SetValue(name, value, kind);
    }

    /// <summary>Returns false when the key or value was not there.</summary>
    public bool DeleteValue(string path, string name)
    {
        using var key = Open(path, false);
        if (key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is null) return false;
        key.DeleteValue(name, false);
        return true;
    }

    public IReadOnlyList<string> GetSubKeyNames(string path)
    {
        using var key = Open(path, false);
        return key?.GetSubKeyNames() ?? [];
    }

    /// <summary>Deletes a key with everything below it. Returns false when it was not there.</summary>
    public bool DeleteTree(string path)
    {
        if (path.Length == 0) throw new ArgumentException("Refusing to delete the root of a hive.", nameof(path));
        if (!KeyExists(path)) return false;
        DeleteTreeCore(path);
        return true;
    }

    private void DeleteTreeCore(string path)
    {
        using var key = Open(path, false);
        if (key is null) return;
        foreach (var child in key.GetSubKeyNames()) DeleteTreeCore(path + "\\" + child);
        // Deleting through the handle we already hold works even where the parent's access list
        // would refuse a delete by name.
        var status = NativeRegistry.NtDeleteKey(key.Handle);
        if (status != 0) throw Failure("delete", path, NativeRegistry.RtlNtStatusToDosError(status));
    }

    private RegistryException Failure(string verb, string path, int error)
    {
        return new RegistryException(
            $"Could not {verb} the registry key {DisplayName}\\{path}: {NativeRegistry.Describe(error)}", error);
    }
}

/// <summary>A registry operation failed. <see cref="Win32Error" /> lets callers retry transient cases.</summary>
public sealed class RegistryException : PlainstallException
{
    public RegistryException(string message, int win32Error, Exception? inner = null)
        : base(ErrorKind.Registry, message, inner)
    {
        Win32Error = win32Error;
    }

    public int Win32Error { get; }

    public bool IsAccessDenied => Win32Error == NativeRegistry.ErrorAccessDenied;
}