namespace Plainstall.Core.Output.Usb;

/// <summary>A volume on a disk, as shown to the user before the disk is erased.</summary>
public sealed record VolumeFacts
{
    /// <summary>For example "E:". Null when the volume has no drive letter.</summary>
    public string? DriveLetter { get; init; }

    public string? Label { get; init; }
    public string? FileSystem { get; init; }
    public long SizeBytes { get; init; }

    /// <summary>
    ///     The volume's own name, for example <c>\\?\Volume{...}\</c>. It identifies the volume even when it
    ///     has no drive letter or is mounted in a folder, so a path in use can be matched against it.
    /// </summary>
    public string? VolumePath { get; init; }
}

/// <summary>
///     What is known about a physical disk. Gathered once and then passed around as a value, so the
///     rules that decide whether a disk may be erased can be tested without touching a disk.
/// </summary>
public sealed record DiskFacts
{
    /// <summary>The disk number Windows uses (Disk Management, diskpart).</summary>
    public required int Number { get; init; }

    /// <summary>Make and model, for example "SanDisk Ultra".</summary>
    public required string FriendlyName { get; init; }

    public string? SerialNumber { get; init; }

    /// <summary>An identifier that stays the same when the disk number changes.</summary>
    public string? UniqueId { get; init; }

    public required long SizeBytes { get; init; }

    /// <summary>"USB", "NVMe", "SATA" and so on, as the Storage API reports it.</summary>
    public required string BusType { get; init; }

    /// <summary>True for memory sticks and card readers; false for USB hard drives and SSD enclosures.</summary>
    public required bool IsRemovableMedia { get; init; }

    public required bool IsSystem { get; init; }
    public required bool IsBoot { get; init; }
    public bool IsOffline { get; init; }
    public bool IsReadOnly { get; init; }

    /// <summary>True when a page file, a crash dump or a hibernation file lives on the disk.</summary>
    public bool HoldsWindowsFiles { get; init; }

    public IReadOnlyList<VolumeFacts> Volumes { get; init; } = [];
}

/// <summary>Paths that must never be on the target disk: erasing it would destroy the build's own input.</summary>
public sealed record UsbSelectionContext
{
    /// <summary>Drive roots or volume paths in use by this build: the work folder, the source ISO, the cache.</summary>
    public IReadOnlyList<string> PathsInUse { get; init; } = [];

    /// <summary>Space the installation files need on the stick.</summary>
    public long RequiredBytes { get; init; }

    /// <summary>
    ///     Also offers USB disks that Windows reports as an external drive and not as removable media. Some USB
    ///     sticks report themselves that way. Set it only for a picker that then asks the user to confirm that
    ///     the disk is a stick, or after the user confirmed exactly that.
    /// </summary>
    public bool AllowExternalDrives { get; init; }
}

/// <summary>A disk together with the verdict whether it may be offered as a target, and why not if not.</summary>
public sealed record UsbCandidate(DiskFacts Disk, bool IsEligible, IReadOnlyList<string> Reasons)
{
    /// <summary>
    ///     True when Windows reports the disk as an external drive and not as a USB stick or memory card. Such a
    ///     disk may only be erased after the user confirmed that it is a stick.
    /// </summary>
    public bool IsExternalDrive => !Disk.IsRemovableMedia;
}

/// <summary>Lists disks for the USB picker. Reading disk information needs administrator rights.</summary>
public interface IUsbDiskService
{
    /// <summary>
    ///     Every disk that could plausibly be meant (anything on the USB bus), each with its verdict.
    ///     Disks that are not eligible are returned too, so the app can say why a stick is not offered.
    /// </summary>
    Task<IReadOnlyList<UsbCandidate>> ListAsync(UsbSelectionContext context,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the current facts of one disk. Null when no disk has this number any more.</summary>
    Task<DiskFacts?> GetAsync(int diskNumber, CancellationToken cancellationToken = default);
}

/// <summary>What to write, and onto which disk the user confirmed.</summary>
public sealed record UsbWriteRequest
{
    /// <summary>The facts the user saw and confirmed. They are compared with the disk again right before erasing.</summary>
    public required DiskFacts ConfirmedDisk { get; init; }

    /// <summary>The prepared installation files (the content of the future stick).</summary>
    public required string MediaRoot { get; init; }

    public required UsbSelectionContext Context { get; init; }
    public string VolumeLabel { get; init; } = Fat32Label.Default;
}

public enum UsbWritePhase
{
    Checking,
    Erasing,
    Formatting,
    Copying,
    SplittingImage,
    Verifying,
    Finishing
}

public sealed record UsbWriteProgress(UsbWritePhase Phase, int? Percent = null, string? Detail = null);

public sealed record UsbWriteResult
{
    public required DiskFacts Disk { get; init; }

    /// <summary>Volume path or drive letter root of the new partition.</summary>
    public required string VolumeRoot { get; init; }

    public required long BytesWritten { get; init; }

    /// <summary>True when install.wim was larger than FAT32 allows and was written as install.swm parts.</summary>
    public required bool ImageWasSplit { get; init; }

    /// <summary>
    ///     True when Windows confirmed that the stick can be unplugged. False means every file is written and
    ///     flushed, but a program still had the stick open, so the user should use "Safely Remove" first.
    /// </summary>
    public bool WasEjected { get; init; }

    /// <summary>Things the user should know that did not make the stick unusable.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>Erases a confirmed USB stick and writes installation media onto it.</summary>
public interface IUsbWriter
{
    Task<UsbWriteResult> WriteAsync(UsbWriteRequest request, IProgress<UsbWriteProgress>? progress = null,
        CancellationToken cancellationToken = default);
}