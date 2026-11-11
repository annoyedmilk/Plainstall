namespace Plainstall.Core.Output.Usb;

/// <summary>
///     Which disk an operation is meant for. Every destructive call carries one, and the implementation
///     compares it with the disk that has this number at that moment, so a stick that was swapped or
///     renumbered after the user confirmed it is refused at every single step, not only at the start.
/// </summary>
public sealed record DiskIdentity
{
    public DiskIdentity(int number, string uniqueId, string? serialNumber, long sizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(number);
        if (string.IsNullOrWhiteSpace(uniqueId))
            // Without a unique id two sticks of the same model and size cannot be told apart.
            throw new ArgumentException(
                "A disk can only be addressed for erasing when Windows reports a unique id for it.", nameof(uniqueId));
        Number = number;
        UniqueId = uniqueId.Trim();
        SerialNumber = string.IsNullOrWhiteSpace(serialNumber) ? null : serialNumber.Trim();
        SizeBytes = sizeBytes;
    }

    public int Number { get; }
    public string UniqueId { get; }
    public string? SerialNumber { get; }
    public long SizeBytes { get; }

    /// <summary>The identity of a disk whose facts were just read. Throws when the facts carry no unique id.</summary>
    public static DiskIdentity Of(DiskFacts disk)
    {
        return new DiskIdentity(disk.Number, disk.UniqueId ?? "", disk.SerialNumber, disk.SizeBytes);
    }

    /// <summary>
    ///     What differs between this identity and the disk now found under the same number, in words for a
    ///     person. Empty means "the same disk". The rules:
    ///     <list type="bullet">
    ///         <item>number, size and unique id must be equal, and the unique id must be present on both sides;</item>
    ///         <item>serial numbers must be equal when both are present;</item>
    ///         <item>
    ///             a serial number on one side only counts as a difference: either it is another device, or
    ///             Windows failed to read it, and neither is a state in which to erase;
    ///         </item>
    ///         <item>
    ///             no serial on both sides is accepted, because many cheap sticks report none; the unique id then
    ///             carries the identity alone.
    ///         </item>
    ///     </list>
    /// </summary>
    public IReadOnlyList<string> DifferencesFrom(int number, string? uniqueId, string? serialNumber, long sizeBytes)
    {
        var differences = new List<string>();
        if (number != Number) differences.Add($"its disk number is now {number} instead of {Number}");
        if (sizeBytes != SizeBytes)
            differences.Add($"its size is now {UsbSizes.Format(sizeBytes)} instead of {UsbSizes.Format(SizeBytes)}");
        var currentId = Clean(uniqueId);
        if (currentId is null)
            differences.Add("Windows no longer reports its unique id");
        else if (!string.Equals(currentId, UniqueId, StringComparison.OrdinalIgnoreCase))
            differences.Add("its unique id is different");
        var currentSerial = Clean(serialNumber);
        if (SerialNumber is not null && currentSerial is not null)
        {
            if (!string.Equals(SerialNumber, currentSerial, StringComparison.OrdinalIgnoreCase))
                differences.Add("its serial number is different");
        }
        else if (SerialNumber is not null)
        {
            differences.Add("Windows no longer reports its serial number");
        }
        else if (currentSerial is not null)
        {
            differences.Add("it now reports a serial number it did not have before");
        }

        return differences;
    }

    internal static string? Clean(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public override string ToString()
    {
        return $"disk {Number} ({UniqueId})";
    }
}

/// <summary>The partition the writer created.</summary>
public sealed record UsbPartition(int PartitionNumber, long OffsetBytes, long SizeBytes);

/// <summary>
///     The new volume on the stick. <see cref="VolumePath" /> (<c>\\?\Volume{...}\</c>) is how the writer
///     addresses it, so no drive letter has to be chosen; <see cref="DriveRoot" /> is the letter Windows
///     assigned by itself, if any, for tools that do not understand volume names.
/// </summary>
public sealed record UsbVolume(int PartitionNumber, long PartitionOffsetBytes, string VolumePath, string? DriveRoot);

/// <summary>
///     Everything the USB writer does to a disk, behind one interface so the writer's safety logic can be
///     tested with a fake that records every call. The real implementation is <see cref="StorageDiskOperations" />.
/// </summary>
/// <remarks>
///     Every method that changes or addresses a disk takes a <see cref="DiskIdentity" />. Implementations must
///     re-read the disk with that number immediately before acting and refuse with a
///     <see cref="PlainstallException" /> when <see cref="DiskIdentity.DifferencesFrom" /> finds anything.
/// </remarks>
public interface IDiskOperations
{
    /// <summary>Facts of every disk Windows knows. Needs administrator rights.</summary>
    Task<IReadOnlyList<DiskFacts>> GetDisksAsync(CancellationToken cancellationToken = default);

    /// <summary>Facts of the disk that has this number now; null when there is none.</summary>
    Task<DiskFacts?> GetDiskAsync(int diskNumber, CancellationToken cancellationToken = default);

    /// <summary>Removes every partition. Afterwards the disk is raw.</summary>
    Task ClearAsync(DiskIdentity disk, CancellationToken cancellationToken = default);

    /// <summary>Writes an empty MBR partition table.</summary>
    Task InitializeMbrAsync(DiskIdentity disk, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Creates one primary partition marked active, of <paramref name="sizeBytes" /> or, when null, of the
    ///     whole disk. No drive letter is chosen.
    /// </summary>
    Task<UsbPartition> CreateActivePartitionAsync(DiskIdentity disk, long? sizeBytes,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Waits until Windows has created the volume for the new partition and returns its volume path. A new
    ///     volume does not appear at once, so this polls for at most <paramref name="timeout" />.
    /// </summary>
    Task<UsbVolume> WaitForVolumeAsync(DiskIdentity disk, UsbPartition partition, TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>Quick-formats the volume as FAT32 and waits until its file system can be used.</summary>
    Task FormatFat32Async(DiskIdentity disk, UsbVolume volume, string label, TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns the volume with its current drive letter, asking Windows to assign one first if it has none.
    ///     Windows picks the letter; none is ever chosen by Plainstall. A volume without a letter is not an error.
    /// </summary>
    Task<UsbVolume> EnsureDriveLetterAsync(DiskIdentity disk, UsbVolume volume,
        CancellationToken cancellationToken = default);

    /// <summary>Writes everything Windows still holds in memory for this volume to the stick.</summary>
    Task FlushAsync(DiskIdentity disk, UsbVolume volume, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Asks Windows to prepare the stick for unplugging ("Safely Remove"). Returns false when Windows
    ///     declined, typically because a program still has the stick open.
    /// </summary>
    Task<bool> EjectAsync(DiskIdentity disk, CancellationToken cancellationToken = default);
}