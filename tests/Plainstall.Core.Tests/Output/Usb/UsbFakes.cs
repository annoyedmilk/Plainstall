using Plainstall.Core.Imaging;
using Plainstall.Core.Output.Usb;
using Plainstall.Core.Tests.Media;

namespace Plainstall.Core.Tests.Output.Usb;

/// <summary>
///     Stands in for the Storage API. Records every call, refuses a call whose identity does not match its
///     disk (as the real implementation does), and uses a temp folder as the stick's volume.
/// </summary>
internal sealed class FakeDiskOperations : IDiskOperations
{
    private static readonly string[] DestructiveOperations = ["Clear", "InitializeMbr", "CreatePartition", "Format"];

    public FakeDiskOperations(DiskFacts? disk, string volumeFolder)
    {
        Disk = disk;
        VolumeFolder = volumeFolder;
        Directory.CreateDirectory(volumeFolder);
        VolumePath = volumeFolder.EndsWith('\\') ? volumeFolder : volumeFolder + '\\';
    }

    /// <summary>The disk as it is "now". Change it to simulate a stick that changed after confirmation.</summary>
    public DiskFacts? Disk { get; set; }

    /// <summary>All disks, for the disk service. Defaults to <see cref="Disk" />.</summary>
    public List<DiskFacts>? AllDisks { get; set; }

    public string VolumeFolder { get; }

    /// <summary>The path handed to the writer as the new volume. Defaults to <see cref="VolumeFolder" />.</summary>
    public string VolumePath { get; set; }

    public string? DriveRoot { get; set; }

    public List<string> Calls { get; } = [];

    public string? FailOn { get; set; }

    public bool PartitionRequested { get; private set; }

    public long? RequestedPartitionSize { get; private set; }

    public string? FormattedLabel { get; private set; }

    public bool EjectResult { get; set; } = true;

    /// <summary>Runs right after the fake format, with the volume folder; used to plant obstacles for the copy.</summary>
    public Action<string>? AfterFormat { get; set; }

    public int DestructiveCalls => Calls.Count(DestructiveOperations.Contains);

    public Task<IReadOnlyList<DiskFacts>> GetDisksAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add("GetDisks");
        IReadOnlyList<DiskFacts> disks = AllDisks ?? (Disk is null ? [] : [Disk]);
        return Task.FromResult(disks);
    }

    public Task<DiskFacts?> GetDiskAsync(int diskNumber, CancellationToken cancellationToken = default)
    {
        Calls.Add("GetDisk");
        return Task.FromResult(Disk is { } d && d.Number == diskNumber ? d : null);
    }

    public Task ClearAsync(DiskIdentity disk, CancellationToken cancellationToken = default)
    {
        Record("Clear", disk);
        Wipe();
        return Task.CompletedTask;
    }

    public Task InitializeMbrAsync(DiskIdentity disk, CancellationToken cancellationToken = default)
    {
        Record("InitializeMbr", disk);
        return Task.CompletedTask;
    }

    public Task<UsbPartition> CreateActivePartitionAsync(DiskIdentity disk, long? sizeBytes,
        CancellationToken cancellationToken = default)
    {
        Record("CreatePartition", disk);
        PartitionRequested = true;
        RequestedPartitionSize = sizeBytes;
        return Task.FromResult(new UsbPartition(1, UsbSizes.PartitionOffsetBytes,
            sizeBytes ?? disk.SizeBytes - UsbSizes.PartitionOffsetBytes));
    }

    public Task<UsbVolume> WaitForVolumeAsync(DiskIdentity disk, UsbPartition partition, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Record("WaitForVolume", disk);
        return Task.FromResult(new UsbVolume(partition.PartitionNumber, partition.OffsetBytes, VolumePath, null));
    }

    public Task FormatFat32Async(DiskIdentity disk, UsbVolume volume, string label, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Record("Format", disk);
        FormattedLabel = label;
        Wipe();
        AfterFormat?.Invoke(VolumeFolder);
        return Task.CompletedTask;
    }

    public Task<UsbVolume> EnsureDriveLetterAsync(DiskIdentity disk, UsbVolume volume,
        CancellationToken cancellationToken = default)
    {
        Record("EnsureDriveLetter", disk);
        return Task.FromResult(volume with { DriveRoot = DriveRoot });
    }

    public Task FlushAsync(DiskIdentity disk, UsbVolume volume, CancellationToken cancellationToken = default)
    {
        Record("Flush", disk);
        return Task.CompletedTask;
    }

    public Task<bool> EjectAsync(DiskIdentity disk, CancellationToken cancellationToken = default)
    {
        Record("Eject", disk);
        return Task.FromResult(EjectResult);
    }

    private void Record(string operation, DiskIdentity identity)
    {
        Calls.Add(operation);
        if (Disk is null) throw new PlainstallException(ErrorKind.Output, $"Fake: no disk for {operation}.");
        var differences = identity.DifferencesFrom(Disk.Number, Disk.UniqueId, Disk.SerialNumber, Disk.SizeBytes);
        if (differences.Count > 0)
            throw new PlainstallException(ErrorKind.Output,
                $"Fake refused {operation}: {string.Join("; ", differences)}");
        if (FailOn == operation) throw new PlainstallException(ErrorKind.Output, $"Simulated failure in {operation}.");
    }

    private void Wipe()
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(VolumeFolder))
            if (Directory.Exists(entry))
            {
                Directory.Delete(entry, true);
            }
            else
            {
                File.SetAttributes(entry, FileAttributes.Normal);
                File.Delete(entry);
            }
    }
}

/// <summary>Stands in for dism.exe /Split-Image: writes parts with real split-WIM headers.</summary>
internal sealed class FakeWimTool : IWimTool
{
    public List<(string WimFile, string SwmFile, int PartSizeMb)> SplitCalls { get; } = [];

    /// <summary>Four parts keep each one under the small file limit the split tests use.</summary>
    public int Parts { get; set; } = 4;

    /// <summary>When set, the parts add up to far less than the image, like a split that silently lost data.</summary>
    public bool WriteTinyParts { get; set; }

    public Task ExportImageAsync(string sourceFile, int sourceIndex, string destinationFile,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<IReadOnlyList<string>> SplitImageAsync(string wimFile, string swmFile, int partSizeMb,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        SplitCalls.Add((wimFile, swmFile, partSizeMb));
        if ((File.GetAttributes(wimFile) & FileAttributes.ReadOnly) != 0)
            throw new PlainstallException(ErrorKind.Imaging, "Fake DISM: cannot split a read-only file.");
        var length = new FileInfo(wimFile).Length;
        var per = length / Parts;
        var created = new List<string>();
        var stem = Path.Combine(Path.GetDirectoryName(swmFile)!, Path.GetFileNameWithoutExtension(swmFile));
        for (var i = 1; i <= Parts; i++)
        {
            var path = i == 1 ? swmFile : $"{stem}{i}.swm";
            var size = WriteTinyParts ? 4096 : i == Parts ? length - per * (Parts - 1) : per;
            var header = new SyntheticWim
            {
                PartNumber = (ushort)i,
                TotalParts = (ushort)Parts,
                Flags = SyntheticWim.FlagCompression | SyntheticWim.FlagLzx | SyntheticWim.FlagSpanned
            }.AddImage("Windows 11 Pro", "Professional").Build();
            var content = new byte[Math.Max(size, 208)];
            header.AsSpan(0, Math.Min(header.Length, content.Length)).CopyTo(content);
            File.WriteAllBytes(path, content);
            created.Add(path);
            progress?.Report(i * 100 / Parts);
        }

        return Task.FromResult<IReadOnlyList<string>>(created);
    }
}

internal sealed class RecordingJournal : IUsbJournal
{
    public List<string> Entries { get; } = [];

    public bool ThrowOnEraseStarting { get; set; }

    public void EraseStarting(DiskFacts disk)
    {
        if (ThrowOnEraseStarting) throw new IOException("journal disk full");
        Entries.Add($"EraseStarting {disk.Number}");
    }

    public void Finished(DiskFacts disk)
    {
        Entries.Add($"Finished {disk.Number}");
    }
}

/// <summary>Collects progress synchronously and lets a test act at a chosen moment.</summary>
internal sealed class RecordingProgress : IProgress<UsbWriteProgress>
{
    public List<UsbWriteProgress> Reports { get; } = [];

    public Action<UsbWriteProgress>? OnReport { get; set; }

    public void Report(UsbWriteProgress value)
    {
        Reports.Add(value);
        OnReport?.Invoke(value);
    }
}

internal static class TestDisks
{
    public const string StickVolumePath = @"\\?\Volume{11111111-2222-3333-4444-555555555555}\";

    /// <summary>An ordinary 16 GB stick with one FAT32 volume on F:.</summary>
    public static DiskFacts Stick(long size = 16_000_000_000, int number = 3)
    {
        return new DiskFacts
        {
            Number = number,
            FriendlyName = "SanDisk Ultra",
            SerialNumber = "4C530001230517102153",
            UniqueId = @"USBSTOR\DISK&VEN_SANDISK&PROD_ULTRA&REV_1.00\4C530001230517102153&0",
            SizeBytes = size,
            BusType = "USB",
            IsRemovableMedia = true,
            IsSystem = false,
            IsBoot = false,
            Volumes =
            [
                new VolumeFacts
                {
                    DriveLetter = "F:", Label = "OLDSTICK", FileSystem = "FAT32",
                    SizeBytes = size - UsbSizes.PartitionOffsetBytes, VolumePath = StickVolumePath
                }
            ]
        };
    }
}

/// <summary>Builds a small but complete-looking installation media folder.</summary>
internal static class TestMedia
{
    public static string Create(TempDirectory scratch, string name = "media", int installPayloadBytes = 512)
    {
        var root = scratch.Combine(name);
        scratch.WriteFile(Path.Combine(name, "bootmgr"), Bytes(1000, 1));
        scratch.WriteFile(Path.Combine(name, "setup.exe"), Bytes(3000, 2));
        scratch.WriteFile(Path.Combine(name, "autounattend.xml"), [.. "<unattend/>"u8]);
        scratch.WriteFile(Path.Combine(name, "efi", "boot", "bootx64.efi"), Bytes(2000, 3));
        scratch.WriteFile(Path.Combine(name, "sources", "setup.dll"), Bytes(5000, 4));
        SyntheticWim.Boot26H2().WriteTo(Path.Combine(root, "sources", "boot.wim"));
        new SyntheticWim { PayloadBytes = installPayloadBytes }.AddImage("Windows 11 Pro", "Professional")
            .WriteTo(Path.Combine(root, "sources", "install.wim"));
        Directory.CreateDirectory(Path.Combine(root, "support", "empty"));
        return root;
    }

    public static byte[] Bytes(int length, byte seed)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++) data[i] = (byte)(seed + i * 7);
        return data;
    }
}