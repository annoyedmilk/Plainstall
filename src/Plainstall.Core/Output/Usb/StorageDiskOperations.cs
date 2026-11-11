using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using EnumerationOptions = System.Management.EnumerationOptions;

namespace Plainstall.Core.Output.Usb;

/// <summary>
///     <see cref="IDiskOperations" /> on the Windows Storage Management API (CIM classes <c>MSFT_Disk</c>,
///     <c>MSFT_Partition</c> and <c>MSFT_Volume</c> in <c>root\Microsoft\Windows\Storage</c>), the API behind
///     <c>Clear-Disk</c>, <c>New-Partition</c> and <c>Format-Volume</c>. It is used instead of diskpart because
///     diskpart's <c>format</c> fails on sticks that were never formatted ("no volume selected") and its
///     output is localized. Needs an elevated process.
/// </summary>
/// <remarks>
///     <para>
///         Every method that addresses a disk first re-reads the disk with the given number and refuses
///         unless its unique id, serial number and size match the <see cref="DiskIdentity" />, it is on the USB bus,
///         and Windows does not use it as a system or boot disk. The method is then invoked on exactly the object
///         that passed this check.
///     </para>
///     <para>
///         No drive letter is ever chosen: the volume is addressed by its volume path, and when a letter
///         is wanted Windows is asked to assign one.
///     </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class StorageDiskOperations : IDiskOperations
{
    private const string StorageNamespace = @"\\.\root\Microsoft\Windows\Storage";
    private const string CimNamespace = @"\\.\root\cimv2";

    private const ushort BusTypeUsb = 7;
    private const ushort PartitionStyleMbr = 1;

    /// <summary>MBR partition type 0x0C, FAT32 with LBA addressing.</summary>
    private const ushort MbrTypeFat32 = 12;

    // Return codes, from the value maps of the methods used here.
    private const uint Success = 0;
    private const uint AccessDenied = 40001;
    private const uint CacheOutOfDate = 40003;
    private const uint NotInitialized = 41000;
    private const uint AlreadyInitialized = 41001;
    private const uint AccessPathInUse = 42002;
    private const uint MultipleDriveLetters = 42012;
    private const uint SizeNotSupported = 4097;

    private static readonly Dictionary<uint, string> ReturnCodes = new()
    {
        [1] = "not supported",
        [2] = "unspecified error",
        [3] = "timeout",
        [4] = "failed",
        [5] = "invalid parameter",
        [6] = "the disk is in use",
        [8] = "object not found",
        [4096] = "a background job was started",
        [4097] = "size not supported",
        [40000] = "not enough available capacity",
        [40001] = "access denied",
        [40002] = "not enough resources",
        [40003] = "cache out of date",
        [40004] = "an unexpected I/O error occurred",
        [41000] = "the disk has not been initialized",
        [41001] = "the disk has already been initialized",
        [41002] = "the disk is read only",
        [41003] = "the disk is offline",
        [41004] = "the disk's partition limit has been reached",
        [41006] = "a parameter is not valid for this type of partition",
        [41007] = "OEM partitions are present",
        [41008] = "data partitions are present",
        [41009] = "the operation is not supported on a critical disk",
        [41010] = "the partition type is not valid",
        [41012] = "the offset is not valid",
        [41015] = "there is no media in the device",
        [41016] = "the operation is not supported on a non-empty removable disk",
        [41017] = "the partition layout is invalid",
        [42002] = "the access path is already in use",
        [42007] = "the access path is not valid",
        [42010] = "the operation is not allowed on a system or critical partition",
        [42012] = "the partition already has a drive letter",
        [42013] = "a drive letter cannot be assigned to this type of partition",
        [43001] = "the file system is not supported",
        [43002] = "the volume cannot be quick formatted",
        [43003] = "the number of clusters exceeds 32 bits",
        [43006] = "the drive is read only"
    };

    private static readonly Dictionary<ushort, string> BusTypes = new()
    {
        [1] = "SCSI", [2] = "ATAPI", [3] = "ATA", [4] = "1394", [5] = "SSA", [6] = "Fibre Channel", [7] = "USB",
        [8] = "RAID", [9] = "iSCSI", [10] = "SAS", [11] = "SATA", [12] = "SD", [13] = "MMC", [14] = "Virtual",
        [15] = "File Backed Virtual", [16] = "Storage Spaces", [17] = "NVMe", [18] = "SCM", [19] = "UFS"
    };

    /// <summary>Files that only exist on a volume Windows itself is using.</summary>
    private static readonly string[] WindowsRootFiles = ["pagefile.sys", "swapfile.sys", "hiberfil.sys"];

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RetryPause = TimeSpan.FromSeconds(3);

    private readonly ILogger _logger;

    public StorageDiskOperations(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    // ---------------------------------------------------------------- reading

    public Task<IReadOnlyList<DiskFacts>> GetDisksAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() => ReadDisks(null), cancellationToken);
    }

    public Task<DiskFacts?> GetDiskAsync(int diskNumber, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var disks = ReadDisks(diskNumber);
            if (disks.Count > 1)
                throw new PlainstallException(ErrorKind.Output,
                    $"Windows reports more than one disk with the number {diskNumber}. Plug the stick in again. Then try again.");

            return disks.Count == 0 ? null : disks[0];
        }, cancellationToken);
    }

    // ---------------------------------------------------------------- changing

    public Task ClearAsync(DiskIdentity disk, CancellationToken cancellationToken = default)
    {
        return Run("erase the USB stick", () =>
        {
            var scope = Connect(StorageNamespace);
            for (var attempt = 1;; attempt++)
            {
                uint code;
                string? detail;
                try
                {
                    using var target = VerifiedDisk(scope, disk, "erase");
                    (code, detail) = Invoke(target, "Clear", p =>
                    {
                        p["RemoveData"] = true;
                        p["RemoveOEM"] = true;
                        p["ZeroOutEntireDisk"] = false;
                    });
                }
                catch (ManagementException ex) when (attempt == 1)
                {
                    _logger.LogWarning(ex, "Clear on {Disk} failed; retrying once", disk);
                    Thread.Sleep(RetryPause);
                    continue;
                }

                // A stick that was never initialized has no partition table to remove.
                if (code is Success or NotInitialized)
                {
                    if (_logger.IsEnabled(LogLevel.Information))
                        _logger.LogInformation("Cleared {Disk} (code {Code})", disk, code);

                    return;
                }

                // Clear can answer "device is not ready" right after a stick was plugged in or used; once more after a pause usually works.
                if (attempt != 1 || code is not (2 or 3 or 4 or 6 or 40002 or CacheOutOfDate))
                    throw Failed("erase", disk, code, detail);

                _logger.LogWarning("Clear on {Disk} returned {Code} ({Detail}); retrying once", disk, code, detail);
                Thread.Sleep(RetryPause);
            }
        });
    }

    public Task InitializeMbrAsync(DiskIdentity disk, CancellationToken cancellationToken = default)
    {
        return Run("prepare the USB stick", () =>
        {
            var scope = Connect(StorageNamespace);
            using var target = VerifiedDisk(scope, disk, "initialize");
            var (code, detail) = Invoke(target, "Initialize", p => p["PartitionStyle"] = PartitionStyleMbr);
            switch (code)
            {
                case Success:
                    return;
                case AlreadyInitialized:
                    // Acceptable only if it is already exactly what we want: an empty MBR disk.
                    using (var again = VerifiedDisk(scope, disk, "initialize"))
                    {
                        if (Get<ushort>(again, "PartitionStyle") == PartitionStyleMbr &&
                            Get<uint>(again, "NumberOfPartitions") == 0) return;
                    }

                    break;
            }

            throw Failed("prepare a partition table on", disk, code, detail);
        });
    }

    public Task<UsbPartition> CreateActivePartitionAsync(DiskIdentity disk, long? sizeBytes,
        CancellationToken cancellationToken = default)
    {
        return Run("create the partition", () =>
        {
            var scope = Connect(StorageNamespace);
            using var target = VerifiedDisk(scope, disk, "create a partition on");
            var (code, detail, output) = InvokeWithOutput(target, "CreatePartition", p =>
            {
                if (sizeBytes is { } size)
                    p["Size"] = checked((ulong)size);
                else
                    p["UseMaximumSize"] = true;

                p["IsActive"] = true;
                p["MbrType"] = MbrTypeFat32;
                // Windows may still give the volume a letter on its own; Plainstall never asks for a specific one.
                p["AssignDriveLetter"] = false;
            });
            using var createdOutput = output;
            if (code != Success) throw Failed("create a partition on", disk, code, detail);

            UsbPartition? created = null;
            if (Prop(output, "CreatedPartition") is ManagementBaseObject partition &&
                Get<uint>(partition, "PartitionNumber") is { } number)
            {
                created = new UsbPartition((int)number, (long)(Get<ulong>(partition, "Offset") ?? 0),
                    (long)(Get<ulong>(partition, "Size") ?? 0));
            }
            else
            {
                using var partitions = Query(scope,
                    $"SELECT * FROM MSFT_Partition WHERE DiskNumber = {disk.Number.ToString(CultureInfo.InvariantCulture)}");
                if (partitions.Items.Count == 1 && Get<uint>(partitions.Items[0], "PartitionNumber") is { } only)
                    created = new UsbPartition((int)only, (long)(Get<ulong>(partitions.Items[0], "Offset") ?? 0),
                        (long)(Get<ulong>(partitions.Items[0], "Size") ?? 0));
            }

            if (created is null || created.SizeBytes <= 0 || (sizeBytes is { } wanted && created.SizeBytes > wanted))
                throw new PlainstallException(ErrorKind.Output,
                    $"Windows created a partition on disk {disk.Number} but did not report it as expected.");

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Created partition {Number} at {Offset}, {Size} bytes", created.PartitionNumber,
                    created.OffsetBytes, created.SizeBytes);

            return created;
        });
    }

    public Task<UsbVolume> WaitForVolumeAsync(DiskIdentity disk, UsbPartition partition, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        return Run("find the new partition", () =>
        {
            var scope = Connect(StorageNamespace);
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                using (var target = VerifiedDisk(scope, disk, "find the new partition on"))
                {
                    using var found = FindPartition(scope, disk, partition.PartitionNumber, partition.OffsetBytes);
                    if (found is not null && VolumePathOf(found) is { } volumePath)
                    {
                        using var volume = FindVolume(scope, volumePath);
                        if (volume is not null)
                        {
                            var letter = DriveLetterOf(found);
                            return new UsbVolume(partition.PartitionNumber, partition.OffsetBytes, volumePath,
                                letter is null ? null : $@"{letter}:\");
                        }
                    }

                    if (DateTime.UtcNow > deadline)
                        throw new PlainstallException(ErrorKind.Output,
                            $"Windows did not make the new partition on the USB stick available within {timeout.TotalSeconds:0} seconds.");

                    // A new volume is not visible at once; asking the provider to look again helps it along.
                    TryInvoke(target, "Refresh");
                }

                Thread.Sleep(PollInterval);
            }
        });
    }

    public Task FormatFat32Async(DiskIdentity disk, UsbVolume volume, string label, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        return Run("format the USB stick", () =>
        {
            var scope = Connect(StorageNamespace);
            using (VerifiedDisk(scope, disk, "format"))
            using (VerifiedPartition(scope, disk, volume))
            using (var target = FindVolume(scope, volume.VolumePath) ?? throw new PlainstallException(ErrorKind.Output,
                       $"The new volume {volume.VolumePath} disappeared before it could be formatted."))
            {
                var (code, detail) = Invoke(target, "Format", p =>
                {
                    p["FileSystem"] = "FAT32";
                    p["FileSystemLabel"] = label;
                    p["Full"] = false;
                    // The fresh volume may already be open by Explorer, which offers to format it.
                    p["Force"] = true;
                });
                if (code == SizeNotSupported)
                    throw new PlainstallException(ErrorKind.Output,
                        $"Windows refused to format the partition on disk {disk.Number} as FAT32 because of its size{Suffix(detail)}.");

                if (code != Success) throw Failed("format", disk, code, detail);
            }

            // A freshly formatted volume is not accessible at once: wait until it reports FAT32 and its root can be listed.
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                using (var formatted = FindVolume(scope, volume.VolumePath))
                {
                    if (formatted is not null &&
                        string.Equals(Str(formatted, "FileSystem"), "FAT32", StringComparison.OrdinalIgnoreCase) &&
                        RootIsReadable(volume.VolumePath))
                    {
                        if (_logger.IsEnabled(LogLevel.Information))
                            _logger.LogInformation("Formatted {Volume} as FAT32, label {Label}", volume.VolumePath,
                                label);

                        return;
                    }
                }

                if (DateTime.UtcNow > deadline)
                    throw new PlainstallException(ErrorKind.Output,
                        $"The USB stick was formatted, but Windows did not make it accessible within {timeout.TotalSeconds:0} seconds.");

                Thread.Sleep(PollInterval);
            }
        });
    }

    public Task<UsbVolume> EnsureDriveLetterAsync(DiskIdentity disk, UsbVolume volume,
        CancellationToken cancellationToken = default)
    {
        return Run("look up the drive letter of the USB stick", () =>
        {
            var scope = Connect(StorageNamespace);
            using (VerifiedDisk(scope, disk, "look up the drive letter of"))
            using (var partition = VerifiedPartition(scope, disk, volume))
            {
                if (DriveLetterOf(partition) is { } letter) return volume with { DriveRoot = $@"{letter}:\" };

                var (code, detail) = Invoke(partition, "AddAccessPath", p => p["AssignDriveLetter"] = true);
                if (code is not (Success or AccessPathInUse or MultipleDriveLetters))
                {
                    // Not fatal: the volume path works without a letter.
                    _logger.LogWarning("Windows did not assign a drive letter to {Volume}: {Code} {Detail}",
                        volume.VolumePath, code, detail);
                    return volume with { DriveRoot = null };
                }
            }

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(PollInterval);
                using var partition = VerifiedPartition(scope, disk, volume);
                if (DriveLetterOf(partition) is { } letter) return volume with { DriveRoot = $@"{letter}:\" };
            }

            _logger.LogWarning("No drive letter appeared for {Volume}", volume.VolumePath);
            return volume with { DriveRoot = null };
        });
    }

    public Task FlushAsync(DiskIdentity disk, UsbVolume volume, CancellationToken cancellationToken = default)
    {
        return Run("finish writing the USB stick", () =>
        {
            var scope = Connect(StorageNamespace);
            using (VerifiedDisk(scope, disk, "flush"))
            using (VerifiedPartition(scope, disk, volume))
            using (var target = FindVolume(scope, volume.VolumePath) ?? throw new PlainstallException(ErrorKind.Output,
                       "The USB stick's volume disappeared before everything was written."))
            {
                var (code, detail) = Invoke(target, "Flush");
                if (code != Success) throw Failed("write the last data to", disk, code, detail);
            }
        });
    }

    public Task<bool> EjectAsync(DiskIdentity disk, CancellationToken cancellationToken = default)
    {
        return Run("remove the USB stick safely", () =>
        {
            var scope = Connect(StorageNamespace);
            using (VerifiedDisk(scope, disk, "remove"))
            {
            }

            var drive = ReadDiskDrives(disk.Number).GetValueOrDefault((uint)disk.Number);
            if (drive?.PnpDeviceId is not { Length: > 0 } deviceId)
            {
                _logger.LogWarning("No device id for {Disk}; cannot eject", disk);
                return false;
            }

            if (CM_Locate_DevNode(out var devInst, deviceId, 0) != 0) return false;

            // The disk's parent is the USB device itself; ejecting it is what "Safely Remove Hardware" does.
            if (CM_Get_Parent(out var parent, devInst, 0) != 0) parent = devInst;

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                if (RequestEject(parent, out var vetoType, out var vetoName))
                {
                    if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("Ejected {Disk}", disk);

                    return true;
                }

                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation("Eject of {Disk} vetoed ({VetoType}: {VetoName}), attempt {Attempt}", disk,
                        vetoType, vetoName, attempt);

                Thread.Sleep(PollInterval);
            }

            return false;
        });
    }

    private IReadOnlyList<DiskFacts> ReadDisks(int? only)
    {
        RequireAdministrator("read the list of disks");
        return Wmi<IReadOnlyList<DiskFacts>>("read the list of disks", () =>
        {
            var scope = Connect(StorageNamespace);
            var diskFilter = only is { } n ? $" WHERE Number = {n.ToString(CultureInfo.InvariantCulture)}" : "";
            var partitionFilter =
                only is { } m ? $" WHERE DiskNumber = {m.ToString(CultureInfo.InvariantCulture)}" : "";

            using var disks = Query(scope, "SELECT * FROM MSFT_Disk" + diskFilter);
            if (disks.Items.Count == 0) return [];
            using var partitions = Query(scope, "SELECT * FROM MSFT_Partition" + partitionFilter);
            using var volumes = Query(scope, "SELECT * FROM MSFT_Volume");
            var volumesByPath = new Dictionary<string, ManagementObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var volume in volumes.Items)
                if (Str(volume, "Path") is { } path)
                    volumesByPath.TryAdd(path, volume);

            var drives = ReadDiskDrives(only);
            var windowsFiles = WindowsFileRoots();

            var result = new List<DiskFacts>();
            foreach (var disk in disks.Items)
            {
                // A disk without a number cannot be addressed, so it is never a target.
                if (Get<uint>(disk, "Number") is not { } number) continue;
                var ofDisk = partitions.Items.Where(p => Get<uint>(p, "DiskNumber") == number).ToList();
                drives.TryGetValue(number, out var drive);
                result.Add(Map(disk, number, ofDisk, volumesByPath, drive, windowsFiles));
            }

            return result;
        });
    }

    private static DiskFacts Map(ManagementObject disk, uint number, List<ManagementObject> partitions,
        Dictionary<string, ManagementObject> volumesByPath, DiskDrive? drive, IReadOnlyList<string> windowsFiles)
    {
        var volumes = new List<VolumeFacts>();
        var partitionIsBoot = false;
        var partitionIsSystem = false;
        foreach (var partition in partitions)
        {
            partitionIsBoot |= Get<bool>(partition, "IsBoot") == true;
            partitionIsSystem |= Get<bool>(partition, "IsSystem") == true;
            var volumePath = VolumePathOf(partition);
            var letter = DriveLetterOf(partition);
            if (volumePath is null && letter is null) continue;
            ManagementObject? volume = null;
            if (volumePath is not null) volumesByPath.TryGetValue(volumePath, out volume);
            volumes.Add(new VolumeFacts
            {
                DriveLetter = letter is null ? null : $"{letter}:",
                VolumePath = volumePath,
                Label = volume is null ? null : Str(volume, "FileSystemLabel"),
                FileSystem = volume is null ? null : Str(volume, "FileSystem"),
                SizeBytes = (long)((volume is null ? null : Get<ulong>(volume, "Size")) ??
                                   Get<ulong>(partition, "Size") ?? 0)
            });
        }

        var holdsWindowsFiles =
            volumes.Any(v => windowsFiles.Any(f => UsbPaths.IsOnVolume(f, v)) || RootHoldsWindowsFiles(v));
        var friendlyName = Str(disk, "FriendlyName") ?? Str(disk, "Model") ?? $"Disk {number}";
        return new DiskFacts
        {
            Number = checked((int)number),
            FriendlyName = friendlyName.Trim(),
            SerialNumber = DiskIdentity.Clean(Str(disk, "SerialNumber")),
            UniqueId = DiskIdentity.Clean(Str(disk, "UniqueId")),
            SizeBytes = checked((long)(Get<ulong>(disk, "Size") ?? 0)),
            BusType = Get<ushort>(disk, "BusType") is { } bus && BusTypes.TryGetValue(bus, out var name) ? name : "",
            IsRemovableMedia = drive?.IsRemovableMedia ?? false,
            // A value Windows does not report counts as "yes": a disk whose role is unknown is not erased.
            IsSystem = (Get<bool>(disk, "IsSystem") ?? true) || partitionIsSystem,
            IsBoot = (Get<bool>(disk, "IsBoot") ?? true) || Get<bool>(disk, "BootFromDisk") == true || partitionIsBoot,
            IsOffline = Get<bool>(disk, "IsOffline") ?? false,
            IsReadOnly = Get<bool>(disk, "IsReadOnly") ?? false,
            HoldsWindowsFiles = holdsWindowsFiles,
            Volumes = volumes
        };
    }

    /// <summary>A volume with a page file, swap file or hibernation file in its root is in use by Windows.</summary>
    private static bool RootHoldsWindowsFiles(VolumeFacts volume)
    {
        var root = volume.VolumePath ?? (volume.DriveLetter is { } l ? l + "\\" : null);
        if (root is null) return false;
        foreach (var name in WindowsRootFiles)
            try
            {
                if (File.Exists(Path.Combine(root, name))) return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A volume that cannot be read (no media, raw) holds none of these.
            }

        return false;
    }

    /// <summary>
    ///     Where Windows keeps page files and crash dumps, as drive roots and volume names, so that a disk
    ///     holding one is recognised even when the file is not in the volume root.
    /// </summary>
    private List<string> WindowsFileRoots()
    {
        var paths = new List<string>();
        try
        {
            var scope = Connect(CimNamespace);
            using var pageFiles = Query(scope, "SELECT Name FROM Win32_PageFileUsage");
            paths.AddRange(pageFiles.Items.Select(p => Str(p, "Name")).OfType<string>());
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read the page file locations");
        }

        try
        {
            using var crash =
                Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CrashControl");
            foreach (var value in new[] { "DumpFile", "DedicatedDumpFile", "MinidumpDir" })
                if (crash?.GetValue(value) is string { Length: > 0 } s)
                    paths.Add(Environment.ExpandEnvironmentVariables(s));
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read the crash dump settings");
        }

        return
        [
            .. paths.SelectMany(p =>
            {
                try
                {
                    return UsbPaths.RootsOf(p);
                }
                catch (ArgumentException)
                {
                    return [];
                }
            }).Distinct(StringComparer.OrdinalIgnoreCase)
        ];
    }

    /// <summary>
    ///     Whether a disk is removable media comes from <c>Win32_DiskDrive.MediaType</c>: "Removable Media" for
    ///     sticks and card readers, "External hard disk media" or "Fixed hard disk media" for drives. The
    ///     Storage API has no such property. <c>Index</c> is the same number as <c>MSFT_Disk.Number</c>.
    /// </summary>
    private static Dictionary<uint, DiskDrive> ReadDiskDrives(int? only)
    {
        var scope = Connect(CimNamespace);
        var filter = only is { } n ? $" WHERE Index = {n.ToString(CultureInfo.InvariantCulture)}" : "";
        using var drives = Query(scope, "SELECT Index, MediaType, PNPDeviceID FROM Win32_DiskDrive" + filter);
        var result = new Dictionary<uint, DiskDrive>();
        foreach (var drive in drives.Items)
        {
            if (Get<uint>(drive, "Index") is not { } index) continue;
            var mediaType = Str(drive, "MediaType");
            var removable = mediaType is not null
                            && mediaType.StartsWith("Removable", StringComparison.OrdinalIgnoreCase)
                            && !mediaType.Contains("hard disk", StringComparison.OrdinalIgnoreCase);
            result[index] = new DiskDrive(removable, Str(drive, "PNPDeviceID"));
        }

        return result;
    }

    // ---------------------------------------------------------------- identity checks

    /// <summary>
    ///     The disk with the expected number, after checking it is still the expected disk. The caller invokes
    ///     methods on exactly this object, so nothing can slip in between check and action except the call itself.
    /// </summary>
    private static ManagementObject VerifiedDisk(ManagementScope scope, DiskIdentity expected, string action)
    {
        var found = Query(scope,
            $"SELECT * FROM MSFT_Disk WHERE Number = {expected.Number.ToString(CultureInfo.InvariantCulture)}");
        if (found.Items.Count != 1)
        {
            var count = found.Items.Count;
            found.Dispose();
            throw Refuse(expected, action,
                count == 0
                    ? "no disk has this number any more"
                    : "Windows reports more than one disk with this number");
        }

        var disk = found.Detach(0);
        found.Dispose();
        var differences = new List<string>(expected.DifferencesFrom(
            Get<uint>(disk, "Number") is { } n ? (int)n : -1,
            Str(disk, "UniqueId"),
            Str(disk, "SerialNumber"),
            (long)(Get<ulong>(disk, "Size") ?? 0)));
        if (Get<ushort>(disk, "BusType") != BusTypeUsb) differences.Add("it is not connected by USB");
        if (Get<bool>(disk, "IsSystem") != false || Get<bool>(disk, "IsBoot") != false ||
            Get<bool>(disk, "BootFromDisk") == true)
            differences.Add("Windows uses it as a system or boot disk");
        if (differences.Count == 0) return disk;
        disk.Dispose();
        throw Refuse(expected, action, string.Join("; ", differences));
    }

    /// <summary>The partition the writer created, after checking it still carries the expected volume.</summary>
    private static ManagementObject VerifiedPartition(ManagementScope scope, DiskIdentity disk, UsbVolume volume)
    {
        var partition = FindPartition(scope, disk, volume.PartitionNumber, volume.PartitionOffsetBytes)
                        ?? throw new PlainstallException(ErrorKind.Output,
                            $"The new partition on disk {disk.Number} is gone.");
        if (Strings(partition, "AccessPaths").Contains(volume.VolumePath, StringComparer.OrdinalIgnoreCase))
            return partition;
        partition.Dispose();
        throw new PlainstallException(ErrorKind.Output,
            $"The volume {volume.VolumePath} no longer belongs to the new partition on disk {disk.Number}. Plainstall stopped.");
    }

    private static ManagementObject? FindPartition(ManagementScope scope, DiskIdentity disk, int partitionNumber,
        long offset)
    {
        using var found = Query(scope,
            $"SELECT * FROM MSFT_Partition WHERE DiskNumber = {disk.Number.ToString(CultureInfo.InvariantCulture)} AND PartitionNumber = {partitionNumber.ToString(CultureInfo.InvariantCulture)}");
        for (var i = 0; i < found.Items.Count; i++)
            if ((long)(Get<ulong>(found.Items[i], "Offset") ?? ulong.MaxValue) == offset)
                return found.Detach(i);

        return null;
    }

    /// <summary>Volumes are looked up by their path in C#, not in the query, so no path has to be escaped for WQL.</summary>
    private static ManagementObject? FindVolume(ManagementScope scope, string volumePath)
    {
        using var volumes = Query(scope, "SELECT * FROM MSFT_Volume");
        for (var i = 0; i < volumes.Items.Count; i++)
            if (string.Equals(Str(volumes.Items[i], "Path"), volumePath, StringComparison.OrdinalIgnoreCase))
                return volumes.Detach(i);

        return null;
    }

    private static string? VolumePathOf(ManagementBaseObject partition)
    {
        return Strings(partition, "AccessPaths")
            .FirstOrDefault(p => p.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase));
    }

    private static char? DriveLetterOf(ManagementBaseObject partition)
    {
        if (Prop(partition, "DriveLetter") is char c && char.IsAsciiLetter(c)) return char.ToUpperInvariant(c);
        var root = Strings(partition, "AccessPaths")
            .FirstOrDefault(p => p.Length == 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\');
        return root is null ? null : char.ToUpperInvariant(root[0]);
    }

    private static bool RootIsReadable(string volumePath)
    {
        try
        {
            if (!Directory.Exists(volumePath)) return false;
            // Listing the root proves the file system answers; an empty root is fine.
            _ = Directory.EnumerateFileSystemEntries(volumePath).Any();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- WMI plumbing

    private static Task<T> Run<T>(string action, Func<T> work)
    {
        return Task.Run(() =>
        {
            RequireAdministrator(action);
            return Wmi(action, work);
        });
    }

    private static Task<bool> Run(string action, Action work)
    {
        return Run(action, () =>
        {
            work();
            return true;
        });
    }

    private static T Wmi<T>(string action, Func<T> work)
    {
        try
        {
            return work();
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            throw new PlainstallException(ErrorKind.Preflight,
                $"Windows denied access when Plainstall tried to {action}. Start Plainstall as administrator.", ex);
        }
        catch (ManagementException ex)
        {
            throw new PlainstallException(ErrorKind.Output,
                $"Windows could not {action}: {ex.Message.Trim()} ({ex.ErrorCode}).", ex);
        }
        catch (COMException ex)
        {
            throw new PlainstallException(ErrorKind.Output,
                $"Windows could not {action}: {ex.Message.Trim()} (0x{ex.HResult:X8}).", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new PlainstallException(ErrorKind.Preflight,
                $"Windows denied access when Plainstall tried to {action}. Start Plainstall as administrator.", ex);
        }
    }

    private static void RequireAdministrator(string action)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new PlainstallException(ErrorKind.Preflight,
                $"Plainstall needs administrator rights to {action}. Start it as administrator. Then try again.");
    }

    private static ManagementScope Connect(string path)
    {
        var scope = new ManagementScope(path, new ConnectionOptions
        {
            Impersonation = ImpersonationLevel.Impersonate,
            EnablePrivileges = true
        });
        scope.Connect();
        return scope;
    }

    private static Results Query(ManagementScope scope, string wql)
    {
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql),
            new EnumerationOptions { ReturnImmediately = false, Rewindable = false });
        using var collection = searcher.Get();
        var items = new List<ManagementObject>();
        foreach (var item in collection)
            if (item is ManagementObject o)
                items.Add(o);
            else
                item.Dispose();

        return new Results(items);
    }

    private static (uint Code, string? Detail) Invoke(ManagementObject target, string method,
        Action<ManagementBaseObject>? inputs = null)
    {
        var (code, detail, output) = InvokeWithOutput(target, method, inputs);
        output?.Dispose();
        return (code, detail);
    }

    private static (uint Code, string? Detail, ManagementBaseObject? Output) InvokeWithOutput(ManagementObject target,
        string method, Action<ManagementBaseObject>? inputs = null)
    {
        using var parameters = target.GetMethodParameters(method);
        if (parameters is not null) inputs?.Invoke(parameters);
        var output = target.InvokeMethod(method, parameters, null);
        var code = Prop(output, "ReturnValue") switch
        {
            uint u => u,
            int i => unchecked((uint)i),
            _ => throw new PlainstallException(ErrorKind.Output, $"Windows gave no result for the operation {method}.")
        };
        return (code, ExtendedStatus(output), output);
    }

    private void TryInvoke(ManagementObject target, string method)
    {
        try
        {
            var (code, detail) = Invoke(target, method);
            if (code != Success && _logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("{Method} returned {Code} {Detail}", method, code, detail);
        }
        catch (ManagementException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug(ex, "{Method} failed", method);
        }
    }

    /// <summary>The <c>MSFT_StorageExtendedStatus</c> message the Storage methods return next to their code.</summary>
    private static string? ExtendedStatus(ManagementBaseObject? output)
    {
        return Prop(output, "ExtendedStatus") switch
        {
            ManagementBaseObject status => DiskIdentity.Clean(Prop(status, "Message") as string) ??
                                           DiskIdentity.Clean(Prop(status, "ErrorDescription") as string),
            string text => DiskIdentity.Clean(text),
            _ => null
        };
    }

    private static PlainstallException Failed(string action, DiskIdentity disk, uint code, string? detail)
    {
        if (code == AccessDenied)
            return new PlainstallException(ErrorKind.Preflight,
                $"Windows denied access when Plainstall tried to {action} disk {disk.Number}. Start Plainstall as administrator.");
        var meaning = ReturnCodes.GetValueOrDefault(code, "unknown error");
        return new PlainstallException(ErrorKind.Output,
            $"Windows could not {action} disk {disk.Number}: {meaning} (code {code}){Suffix(detail)}.");
    }

    private static PlainstallException Refuse(DiskIdentity expected, string action, string why)
    {
        return new PlainstallException(ErrorKind.Output,
            $"Plainstall did not {action} disk {expected.Number}, because it is no longer the disk that was checked: {why}.");
    }

    private static string Suffix(string? detail)
    {
        return string.IsNullOrWhiteSpace(detail) ? "" : $" ({detail.Trim().TrimEnd('.')})";
    }

    private static object? Prop(ManagementBaseObject? o, string name)
    {
        if (o is null) return null;
        try
        {
            return o[name];
        }
        catch (ManagementException)
        {
            return null; // The property does not exist on this object.
        }
    }

    private static string? Str(ManagementBaseObject o, string name)
    {
        return Prop(o, name) as string;
    }

    /// <summary>The property as a value of type <typeparamref name="T" />, or null when it is missing or of another type.</summary>
    private static T? Get<T>(ManagementBaseObject o, string name) where T : struct
    {
        return Prop(o, name) is T v ? v : null;
    }

    private static string[] Strings(ManagementBaseObject o, string name)
    {
        return Prop(o, name) as string[] ?? [];
    }

    // ---------------------------------------------------------------- Safe removal (cfgmgr32)

    private static unsafe bool RequestEject(uint devInst, out int vetoType, out string vetoName)
    {
        const int maxPath = 260;
        var buffer = stackalloc char[maxPath];
        var result = CM_Request_Device_Eject(devInst, out vetoType, buffer, maxPath, 0);
        vetoName = new string(buffer);
        return result == 0 && vetoType == 0;
    }

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Locate_DevNode(out uint devInst, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Parent")]
    private static partial uint CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Request_Device_EjectW")]
    private static unsafe partial uint CM_Request_Device_Eject(uint devInst, out int vetoType, char* vetoName,
        uint nameLength, uint flags);

    private sealed record DiskDrive(bool IsRemovableMedia, string? PnpDeviceId);

    /// <summary>Query results that are disposed together, except those handed out with <see cref="Detach" />.</summary>
    private sealed class Results(List<ManagementObject> items) : IDisposable
    {
        private readonly HashSet<int> _detached = [];
        public List<ManagementObject> Items { get; } = items;

        public void Dispose()
        {
            for (var i = 0; i < Items.Count; i++)
                if (!_detached.Contains(i))
                    Items[i].Dispose();
        }

        public ManagementObject Detach(int index)
        {
            _detached.Add(index);
            return Items[index];
        }
    }
}