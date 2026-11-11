using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace Plainstall.Core.Media;

/// <summary>How the files of an attached ISO are reached.</summary>
public enum IsoMountPathKind
{
    /// <summary>Through the volume name, <c>\\?\Volume{guid}\</c>. No drive letter is used.</summary>
    VolumePath,

    /// <summary>Through a drive letter that Windows assigned, for example <c>F:\</c>.</summary>
    DriveLetter
}

/// <summary>
///     An ISO file attached read-only as a virtual DVD drive, the same thing Explorer does on a double
///     click, but tied to this object: disposing it detaches the image.
/// </summary>
/// <remarks>
///     <para>
///         The image is attached without the permanent-lifetime flag. Windows then detaches it by itself
///         when the handle closes, so even a crash or a killed process leaves nothing mounted.
///     </para>
///     <para>
///         No drive letter is requested. A letter would show up in Explorer, could trigger AutoPlay, and
///         could collide with the letter a USB stick is about to get. The files are read through the
///         volume name instead. Only if that does not work, the image is attached again the ordinary way
///         and the drive letter is used.
///     </para>
/// </remarks>
public sealed class IsoMount : IAsyncDisposable, IDisposable
{
    /// <summary>How long to wait for the volume after an attach. It normally appears within a second.</summary>
    private static readonly TimeSpan AttachTimeout = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly ILogger _logger;
    private SafeFileHandle? _handle;

    private IsoMount(SafeFileHandle handle, string isoPath, string rootPath, IsoMountPathKind pathKind,
        string devicePath, ILogger logger)
    {
        _handle = handle;
        _logger = logger;
        IsoPath = isoPath;
        RootPath = rootPath;
        PathKind = pathKind;
        DevicePath = devicePath;
    }

    /// <summary>Full path of the ISO file.</summary>
    public string IsoPath { get; }

    /// <summary>
    ///     Root folder of the mounted media, ending in a backslash: <c>\\?\Volume{guid}\</c> or
    ///     <c>F:\</c>. Combine it with relative paths such as <c>sources\install.wim</c>.
    /// </summary>
    public string RootPath { get; }

    public IsoMountPathKind PathKind { get; }

    /// <summary>The virtual drive as Windows names it, for example <c>\\.\CDROM1</c>. For logs.</summary>
    public string DevicePath { get; }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Detaches the image. Safe to call more than once. Never throws.</summary>
    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, null);
        if (handle is null) return;
        Detach(handle, IsoPath, _logger);
        if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("Detached {Iso}", IsoPath);
    }

    /// <summary>
    ///     Attaches <paramref name="isoPath" /> read-only and waits until its files can be read.
    /// </summary>
    /// <param name="isoPath">The ISO file to attach.</param>
    /// <param name="logger">Where the attach and detach are logged, or null for no log.</param>
    /// <param name="cancellationToken">Stops the wait for the volume; an image that was attached is detached again.</param>
    /// <exception cref="PlainstallException">The file is missing, Windows refused to attach it, or no volume appeared in time.</exception>
    public static async Task<IsoMount> AttachAsync(string isoPath, ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(isoPath);
        logger ??= NullLogger.Instance;

        var fullPath = Path.GetFullPath(isoPath);
        if (!File.Exists(fullPath))
            throw new PlainstallException(ErrorKind.InvalidInput, $"The ISO file '{fullPath}' does not exist.");

        var mount = await TryAttachAsync(fullPath, false, logger, cancellationToken).ConfigureAwait(false);
        if (mount is null)
        {
            logger.LogWarning(
                "The ISO was attached but its volume did not become readable without a drive letter; trying again with a drive letter");
            mount = await TryAttachAsync(fullPath, true, logger, cancellationToken).ConfigureAwait(false);
        }

        if (mount is null)
            throw new PlainstallException(ErrorKind.Imaging,
                $"Windows attached the ISO file '{fullPath}', but its contents did not become readable within {AttachTimeout.TotalSeconds:0} seconds. " +
                "Make sure that the file is a complete Windows ISO. To do this, compare its SHA-256 hash with the hash that Microsoft publishes. " +
                "Then try again.");

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Attached {Iso} read-only as {Device}, files at {Root} ({Kind})", fullPath,
                mount.DevicePath, mount.RootPath, mount.PathKind);
        return mount;
    }

    /// <summary>One attach attempt. Returns null when the image attached but no readable volume showed up in time.</summary>
    private static async Task<IsoMount?> TryAttachAsync(string isoPath, bool withDriveLetter, ILogger logger,
        CancellationToken cancellationToken)
    {
        var storageType = new VirtualDiskNative.VirtualStorageType
        {
            DeviceId = VirtualDiskNative.VirtualStorageTypeDeviceIso,
            VendorId = VirtualDiskNative.VendorMicrosoft
        };

        var error = VirtualDiskNative.OpenVirtualDisk(storageType, isoPath, VirtualDiskNative.VirtualDiskAccessRead, 0,
            IntPtr.Zero, out var handle);
        if (error != VirtualDiskNative.ErrorSuccess)
        {
            handle.Dispose();
            throw AttachFailed(isoPath, "open", error);
        }

        var keepHandle = false;
        try
        {
            var flags = VirtualDiskNative.AttachFlagReadOnly |
                        (withDriveLetter ? 0 : VirtualDiskNative.AttachFlagNoDriveLetter);
            error = VirtualDiskNative.AttachVirtualDisk(handle, IntPtr.Zero, flags, 0, IntPtr.Zero, IntPtr.Zero);
            if (error != VirtualDiskNative.ErrorSuccess) throw AttachFailed(isoPath, "attach", error);

            // The volume is created by the mount manager a moment after the attach returns.
            var clock = Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (FindRoot(handle, withDriveLetter) is { } found)
                {
                    keepHandle = true;
                    return new IsoMount(handle, isoPath, found.Root, found.Kind, found.Device, logger);
                }

                if (clock.Elapsed >= AttachTimeout) return null;
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (!keepHandle) Detach(handle, isoPath, logger);
        }
    }

    /// <summary>
    ///     Finds where the attached image is reachable right now, or null when it is not ready yet.
    ///     The virtual drive is matched to its volume by the NT device both names point at.
    /// </summary>
    private static (string Root, IsoMountPathKind Kind, string Device)? FindRoot(SafeFileHandle handle,
        bool allowDriveLetter)
    {
        var buffer = new char[260];
        var sizeInBytes = (uint)(buffer.Length * sizeof(char));
        if (VirtualDiskNative.GetVirtualDiskPhysicalPath(handle, ref sizeInBytes, buffer) !=
            VirtualDiskNative.ErrorSuccess) return null;

        // For an ISO this is "\\.\CDROMn".
        var devicePath = VirtualDiskNative.FromNullTerminated(buffer);
        const string devicePrefix = @"\\.\";
        if (!devicePath.StartsWith(devicePrefix, StringComparison.Ordinal) ||
            devicePath.Length == devicePrefix.Length) return null;
        var target = VirtualDiskNative.QueryDosDeviceTarget(devicePath[devicePrefix.Length..]);
        if (target is null) return null;

        if (FindVolumeRoot(target) is { } volumeRoot) return (volumeRoot, IsoMountPathKind.VolumePath, devicePath);
        if (allowDriveLetter && FindDriveLetterRoot(target) is { } letterRoot)
            return (letterRoot, IsoMountPathKind.DriveLetter, devicePath);
        return null;
    }

    /// <summary>The readable volume path ("\\?\Volume{guid}\") of an NT device such as "\Device\CdRom1", or null.</summary>
    internal static string? FindVolumeRoot(string ntDevice)
    {
        return VirtualDiskNative.EnumerateVolumeNames()
            .Where(volume => volume.StartsWith(@"\\?\", StringComparison.Ordinal) && volume.EndsWith('\\') &&
                             volume.Length >= 6)
            // "\\?\Volume{guid}\" becomes the DOS device name "Volume{guid}".
            .FirstOrDefault(volume =>
                string.Equals(VirtualDiskNative.QueryDosDeviceTarget(volume[4..^1]), ntDevice,
                    StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(volume));
    }

    /// <summary>The readable drive root ("F:\") of an NT device such as "\Device\CdRom1", or null.</summary>
    internal static string? FindDriveLetterRoot(string ntDevice)
    {
        for (var letter = 'A'; letter <= 'Z'; letter++)
        {
            var letterTarget = VirtualDiskNative.QueryDosDeviceTarget($"{letter}:");
            var root = $@"{letter}:\";
            if (string.Equals(letterTarget, ntDevice, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(root)) return root;
        }

        return null;
    }

    private static PlainstallException AttachFailed(string isoPath, string step, uint error)
    {
        var reason = new Win32Exception(unchecked((int)error)).Message;
        return new PlainstallException(ErrorKind.Imaging,
            $"Windows could not {step} the ISO file '{isoPath}' (error {error}: {reason}). " +
            "Check that the file is a complete Windows ISO and is stored on a local disk of this PC. Then try again. " +
            "If Windows reports the file as damaged, download it again.");
    }

    /// <summary>Detaches and closes. A failure is logged only: closing the handle detaches the image anyway.</summary>
    private static void Detach(SafeFileHandle handle, string isoPath, ILogger logger)
    {
        try
        {
            if (handle is not { IsInvalid: false, IsClosed: false }) return;
            var error = VirtualDiskNative.DetachVirtualDisk(handle, 0, 0);
            if (error != VirtualDiskNative.ErrorSuccess)
                logger.LogWarning("Detaching {Iso} reported error {Error}; Windows detaches it when the handle closes",
                    isoPath, error);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or DllNotFoundException or EntryPointNotFoundException)
        {
            logger.LogWarning(ex, "Detaching {Iso} failed", isoPath);
        }
        finally
        {
            handle.Dispose();
        }
    }
}