using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Plainstall.Core.Catalog;

namespace Plainstall.Core.Registry;

/// <summary>
///     Records which hives are loaded, so that a crash can be cleaned up on the next start. The pipeline's
///     journal implements this. The entry is written before the load and removed after the unload.
/// </summary>
public interface IHiveJournal
{
    void HiveLoading(string mountName, string hiveFile);

    void HiveUnloaded(string mountName);
}

/// <summary>
///     One registry hive file of an offline image, loaded under <c>HKLM\&lt;mount name&gt;</c> for as long
///     as this object lives. Disposing always unloads it, also when an exception or a cancel is on its way out.
/// </summary>
public sealed class OfflineHive : IDisposable
{
    public const string MountNamePrefix = "Plainstall_";

    private readonly IHiveJournal? _journal;
    private readonly ILogger _logger;
    private bool _disposed;
    private RegistryKey? _rootKey;

    private OfflineHive(string mountName, string hiveFile, RegistryKey rootKey, IHiveJournal? journal, ILogger logger)
    {
        MountName = mountName;
        HiveFile = hiveFile;
        _rootKey = rootKey;
        _journal = journal;
        _logger = logger;
        Root = new RegistryRoot(rootKey.Handle, true, Path.GetFileName(hiveFile));
    }

    public string MountName { get; }
    public string HiveFile { get; }
    public RegistryRoot Root { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_rootKey is not null)
            {
                NativeRegistry.RegFlushKey(_rootKey.Handle);
                _rootKey.Dispose();
                _rootKey = null;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Closing the loaded hive {MountName} reported an error", MountName);
        }

        var error = TryUnload(MountName, _logger);
        if (error == NativeRegistry.ErrorSuccess)
        {
            _journal?.HiveUnloaded(MountName);
            return;
        }

        // The journal entry stays, so the next start can finish the job.
        throw new RegistryException(
            $"The registry file '{HiveFile}' could not be released ({NativeRegistry.Describe(error)}). " +
            "Close programs that may be reading it, such as the Registry Editor, then use 'Clean up' on the Welcome page of the app, or run 'Plainstall.Cli cleanup'.",
            error);
    }

    /// <summary>Path of a hive file inside a mounted image.</summary>
    public static string HiveFilePath(string imageRoot, RegistryHiveId hive)
    {
        return hive switch
        {
            RegistryHiveId.MachineSoftware => Path.Combine(imageRoot, "Windows", "System32", "config", "SOFTWARE"),
            RegistryHiveId.MachineSystem or RegistryHiveId.SetupSystem => Path.Combine(imageRoot, "Windows", "System32",
                "config", "SYSTEM"),
            RegistryHiveId.SystemUser => Path.Combine(imageRoot, "Windows", "System32", "config", "DEFAULT"),
            RegistryHiveId.DefaultUser => Path.Combine(imageRoot, "Users", "Default", "NTUSER.DAT"),
            _ => throw new ArgumentOutOfRangeException(nameof(hive))
        };
    }

    public static string MountNameFor(string sessionId, RegistryHiveId hive)
    {
        return $"{MountNamePrefix}{sessionId}_{hive}";
    }

    /// <summary>Loads a hive file. Needs an elevated process. This method enables the backup and restore privileges.</summary>
    public static OfflineHive Load(string hiveFile, string mountName, IHiveJournal? journal = null,
        ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        if (!File.Exists(hiveFile))
            throw new PlainstallException(ErrorKind.Registry,
                $"The registry file '{hiveFile}' does not exist in the image.");
        foreach (var privilege in new[] { ServicingPrivileges.Backup, ServicingPrivileges.Restore })
        {
            var privilegeError = NativeRegistry.EnablePrivilege(privilege);
            if (privilegeError != NativeRegistry.ErrorSuccess)
                throw new PlainstallException(ErrorKind.Preflight,
                    $"Changing the registry of a Windows image needs administrator rights. Start Plainstall as administrator. ({privilege} could not be enabled: {NativeRegistry.Describe(privilegeError)})");
        }

        journal?.HiveLoading(mountName, hiveFile);
        var error = NativeRegistry.RegLoadKey(NativeRegistry.HkeyLocalMachine, mountName, hiveFile);
        if (error != NativeRegistry.ErrorSuccess)
        {
            journal?.HiveUnloaded(mountName);
            throw new RegistryException(
                $"Could not load the registry file '{hiveFile}': {NativeRegistry.Describe(error)}", error);
        }

        try
        {
            var rootKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(mountName, true)
                          ?? throw new PlainstallException(ErrorKind.Registry,
                              $"The loaded registry file '{hiveFile}' could not be opened.");
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug("Loaded {HiveFile} as HKLM\\{MountName}", hiveFile, mountName);
            return new OfflineHive(mountName, hiveFile, rootKey, journal, logger);
        }
        catch
        {
            TryUnload(mountName, logger);
            journal?.HiveUnloaded(mountName);
            throw;
        }
    }

    /// <summary>
    ///     Unloads a hive by name, with a short retry: an unload fails with "access denied" while any handle
    ///     into the hive is still open, and the garbage collector may not have released ours yet.
    ///     Returns the last Win32 error. "Not found" counts as success.
    /// </summary>
    public static int TryUnload(string mountName, ILogger? logger = null)
    {
        var error = NativeRegistry.ErrorSuccess;
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            error = NativeRegistry.RegUnLoadKey(NativeRegistry.HkeyLocalMachine, mountName);
            if (error is NativeRegistry.ErrorSuccess or NativeRegistry.ErrorFileNotFound
                or NativeRegistry.ErrorPathNotFound) return NativeRegistry.ErrorSuccess;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Thread.Sleep(200 * attempt);
        }

        logger?.LogWarning("Unloading HKLM\\{MountName} failed: {Error}", mountName, NativeRegistry.Describe(error));
        return error;
    }
}