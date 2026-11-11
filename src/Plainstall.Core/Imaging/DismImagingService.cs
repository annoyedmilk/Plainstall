using Microsoft.Dism;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Plainstall.Core.Registry;

namespace Plainstall.Core.Imaging;

/// <summary>
///     <see cref="IImagingService" /> on top of DismApi (through the Microsoft.Dism package). DismApi returns
///     structured data and real progress, so nothing here parses console text.
/// </summary>
public sealed class DismImagingService : IImagingService, IDisposable
{
    /// <summary>CBS_E_PENDING: "the operation could not be completed due to pending operations".</summary>
    public const int PendingOperationsHResult = unchecked((int)0x800F0806);

    private static readonly Lock InitLock = new();
    private static bool _initialized;
    private readonly ILogger _logger;

    /// <param name="logFile">Where DISM writes its own log.</param>
    /// <param name="scratchDirectory">Temp space for DISM. Must be on a drive with room for package expansion.</param>
    /// <param name="logger">Where the service logs what it does, or null for no log.</param>
    public DismImagingService(string logFile, string scratchDirectory, ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        var missing = ServicingPrivileges.EnableAll();
        if (missing.Count > 0)
            throw new PlainstallException(ErrorKind.Preflight,
                "Servicing a Windows image needs administrator rights. Start Plainstall as administrator.");

        lock (InitLock)
        {
            if (_initialized) return;
            Directory.CreateDirectory(scratchDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logFile))!);
            try
            {
                DismApi.Initialize(DismLogLevel.LogErrorsWarnings, logFile, scratchDirectory);
            }
            catch (Exception ex) when (ex is DismException or DllNotFoundException or UnauthorizedAccessException)
            {
                throw new PlainstallException(ErrorKind.Imaging, $"DISM could not be started: {ex.Message}", ex);
            }

            _initialized = true;
        }
    }

    /// <summary>
    ///     Stops DISM. DismApi is started once per process with the log file and scratch folder of one build, and
    ///     holds both open until it stops, so a build disposes the service before it deletes its work folder.
    ///     The next service starts DISM again with the files of its own build. Plainstall runs one build at a time,
    ///     and every servicing session of a build is closed by then.
    /// </summary>
    public void Dispose()
    {
        lock (InitLock)
        {
            if (!_initialized) return;
            try
            {
                DismApi.Shutdown();
            }
            catch (DismException ex)
            {
                _logger.LogWarning(ex, "DISM could not be stopped cleanly");
            }

            _initialized = false;
        }
    }

    public IMountedImage Mount(string wimPath, int index, string mountPath, IProgress<int>? progress = null)
    {
        Run($"mount image {index} of {Path.GetFileName(wimPath)}", () =>
            DismApi.MountImage(wimPath, mountPath, index, false, DismMountImageOptions.None, Callback(progress)));
        try
        {
            return Open(mountPath);
        }
        catch
        {
            // A mount without a session is useless; do not leave it behind.
            TryDiscard(mountPath);
            throw;
        }
    }

    public void UnmountAndCommit(string mountPath, IProgress<int>? progress = null)
    {
        Run("save the image", () => DismApi.UnmountImage(mountPath, true, Callback(progress)));
    }

    public void UnmountAndDiscard(string mountPath, IProgress<int>? progress = null)
    {
        Run("discard the mounted image", () => DismApi.UnmountImage(mountPath, false, Callback(progress)));
    }

    public IReadOnlyList<MountedImageInfo> GetMountedImages()
    {
        return Run("list mounted images", () => DismApi.GetMountedImages()
            .Select(i => new MountedImageInfo(i.MountPath, i.ImageFilePath, i.ImageIndex))
            .ToList());
    }

    /// <summary>Opens a servicing session on an image that is already mounted.</summary>
    public IMountedImage Open(string mountPath)
    {
        var session = Run("open the mounted image", () => DismApi.OpenOfflineSession(mountPath));
        return new MountedImage(session, mountPath, this);
    }

    private void TryDiscard(string mountPath)
    {
        try
        {
            DismApi.UnmountImage(mountPath, false);
        }
        catch (Exception ex) when (ex is DismException or IOException)
        {
            _logger.LogWarning(ex, "Discarding the mount at {MountPath} failed", mountPath);
        }
    }

    private static DismProgressCallback? Callback(IProgress<int>? progress)
    {
        return progress is null
            ? null
            : p => progress.Report(p.Total > 0 ? (int)Math.Clamp(p.Current * 100L / p.Total, 0, 100) : 0);
    }

    private void Run(string what, Action action)
    {
        Run<object?>(what, () =>
        {
            action();
            return null;
        });
    }

    private T Run<T>(string what, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (DismRebootRequiredException)
        {
            // Offline images cannot reboot; DISM reports 3010 for changes that complete at first start.
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("DISM reported 'restart required' while trying to {What}", what);
            return default!;
        }
        catch (DismException ex)
        {
            throw new ImagingException($"DISM could not {what}: {Describe(ex)}", ex.HResult, ex);
        }
    }

    internal static string Describe(Exception ex)
    {
        var message = ex.Message.Trim();
        var detail = TryLastError();
        if (!string.IsNullOrWhiteSpace(detail) && !message.Contains(detail, StringComparison.OrdinalIgnoreCase))
            message += " " + detail.Trim();
        return $"{message} (0x{ex.HResult:X8})";
    }

    private static string? TryLastError()
    {
        try
        {
            return DismApi.GetLastErrorMessage();
        }
        catch (Exception ex) when (ex is DismException or InvalidOperationException)
        {
            return null;
        }
    }

    private static ComponentState Map(DismPackageFeatureState state)
    {
        return state switch
        {
            DismPackageFeatureState.NotPresent => ComponentState.NotPresent,
            DismPackageFeatureState.Installed => ComponentState.Installed,
            DismPackageFeatureState.InstallPending or DismPackageFeatureState.UninstallPending =>
                ComponentState.Pending,
            DismPackageFeatureState.Removed => ComponentState.PayloadRemoved,
            _ => ComponentState.Other
        };
    }

    private sealed class MountedImage : IMountedImage
    {
        private readonly DismImagingService _owner;
        private readonly DismSession _session;

        public MountedImage(DismSession session, string mountPath, DismImagingService owner)
        {
            _session = session;
            _owner = owner;
            MountPath = mountPath;
        }

        public string MountPath { get; }

        public IReadOnlyList<ProvisionedPackage> GetProvisionedPackages()
        {
            return _owner.Run("list the preinstalled apps", () => DismApi.GetProvisionedAppxPackages(_session)
                .Select(p => new ProvisionedPackage(p.DisplayName, p.PackageName))
                .ToList());
        }

        public void RemoveProvisionedPackage(string packageName)
        {
            _owner.Run($"remove the app {packageName}",
                () => DismApi.RemoveProvisionedAppxPackage(_session, packageName));
        }

        public IReadOnlyList<ComponentInfo> GetCapabilities()
        {
            return _owner.Run("list capabilities", () => DismApi.GetCapabilities(_session)
                .Select(c => new ComponentInfo(c.Name, Map(c.State)))
                .ToList());
        }

        public void RemoveCapability(string capabilityName)
        {
            _owner.Run($"remove the capability {capabilityName}",
                () => DismApi.RemoveCapability(_session, capabilityName));
        }

        public IReadOnlyList<ComponentInfo> GetFeatures()
        {
            return _owner.Run("list features", () => DismApi.GetFeatures(_session)
                .Select(f => new ComponentInfo(f.FeatureName, Map(f.State)))
                .ToList());
        }

        public void DisableFeature(string featureName)
        {
            _owner.Run($"disable the feature {featureName}",
                () => DismApi.DisableFeature(_session, featureName, null!, false));
        }

        public void EnableFeature(string featureName)
        {
            // limitAccess: never contact Windows Update for the files. enableAll: parent features come along.
            // No source paths: only what the image already holds is used.
            _owner.Run($"enable the feature {featureName}",
                () => DismApi.EnableFeature(_session, featureName, true, true));
        }

        public IReadOnlyList<ComponentInfo> GetPackages()
        {
            return _owner.Run("list packages", () => DismApi.GetPackages(_session)
                .Select(p => new ComponentInfo(p.PackageName, Map(p.PackageState)))
                .ToList());
        }

        public void AddPackage(string packagePath, IProgress<int>? progress = null)
        {
            _owner.Run($"add the update {Path.GetFileName(packagePath)}", () =>
                DismApi.AddPackage(_session, packagePath, false, false, Callback(progress)));
        }

        public void AddDriver(string infPath)
        {
            _owner.Run($"add the driver {Path.GetFileName(infPath)}",
                () => DismApi.AddDriver(_session, infPath, false));
        }

        public CleanupResult CleanupComponents(bool resetBase, IProgress<int>? progress = null)
        {
            try
            {
                DismApi.CleanImage(_session, DismCleanImageType.Component,
                    resetBase ? DismCleanImageFlags.ResetBase : DismCleanImageFlags.None, Callback(progress));
                return CleanupResult.Done;
            }
            catch (DismException ex) when (ex.HResult == PendingOperationsHResult)
            {
                return CleanupResult.SkippedPendingOperations;
            }
            catch (DismRebootRequiredException)
            {
                return CleanupResult.Done;
            }
            catch (DismException ex)
            {
                throw new ImagingException($"DISM could not clean up the image: {Describe(ex)}", ex.HResult, ex);
            }
        }

        public void Dispose()
        {
            _session.Dispose();
        }
    }
}

/// <summary>A DISM or WIM operation failed. <see cref="ErrorCode" /> is the HRESULT or the dism.exe exit code.</summary>
public sealed class ImagingException : PlainstallException
{
    public ImagingException(string message, int errorCode, Exception? inner = null)
        : base(ErrorKind.Imaging, message, inner)
    {
        ErrorCode = errorCode;
    }

    public int ErrorCode { get; }
}