using System.Globalization;

namespace Plainstall.Core.Output.Usb;

/// <summary>
///     Size arithmetic for a FAT32 installation stick. Kept separate and pure so the numbers that decide
///     "does it fit" can be tested without a stick, and so the app can show the same numbers the writer uses.
/// </summary>
public static class UsbSizes
{
    /// <summary>FAT32 stores a file's length in 32 bits, so no file on the stick can be 4 GiB or larger.</summary>
    public const long Fat32MaxFileBytes = 4L * 1024 * 1024 * 1024 - 1;

    /// <summary>
    ///     Windows' own format code refuses FAT32 volumes larger than 32 GB, so larger sticks get one 32 GB
    ///     partition and the rest stays unallocated. FAT32 is what makes the stick boot on both BIOS and UEFI PCs.
    /// </summary>
    public const long MaxFat32PartitionBytes = 32L * 1024 * 1024 * 1024;

    /// <summary>Windows starts the first partition 1 MiB into the disk; that space is not usable for files.</summary>
    public const long PartitionOffsetBytes = 1024 * 1024;

    /// <summary>
    ///     Part size for a split install image, the value Microsoft's own instructions use. It leaves room under
    ///     the FAT32 limit because DISM may make a part a little larger than asked to keep one resource whole.
    /// </summary>
    public const int SplitPartSizeMb = 3800;

    /// <summary>
    ///     Room each split part needs beyond its share of the image: its own header, a copy of the image
    ///     description and its lookup table. Generous on purpose: running out of space after the erase is worse.
    /// </summary>
    internal const long SplitPartOverheadBytes = 8L * 1024 * 1024;

    /// <summary>
    ///     Bytes of directory entries counted per file. A FAT32 entry is 32 bytes and a long name adds one more
    ///     entry per 13 characters, so 128 covers the names on Windows media.
    /// </summary>
    internal const int DirectoryEntryBytesPerFile = 128;

    /// <summary>
    ///     The cluster size used when the stick is not known yet. It is twice the largest cluster Windows picks
    ///     for FAT32 up to 32 GB (16 KB), so the estimate is never too small for any stick.
    /// </summary>
    public const int ConservativeClusterBytes = 32 * 1024;

    /// <summary>
    ///     The size to ask for when creating the partition: null means "the whole disk", otherwise the 32 GB cap.
    /// </summary>
    public static long? PartitionSizeFor(long diskBytes)
    {
        return diskBytes - PartitionOffsetBytes > MaxFat32PartitionBytes ? MaxFat32PartitionBytes : null;
    }

    /// <summary>The expected size of the partition the writer will create on a disk of this size.</summary>
    public static long PlannedPartitionBytes(long diskBytes)
    {
        return Math.Max(0, Math.Min(diskBytes - PartitionOffsetBytes, MaxFat32PartitionBytes));
    }

    /// <summary>The cluster size Windows chooses by default when it formats a FAT32 volume of this size.</summary>
    public static int Fat32ClusterBytes(long partitionBytes)
    {
        return partitionBytes switch
        {
            <= 8L * 1024 * 1024 * 1024 => 4 * 1024,
            <= 16L * 1024 * 1024 * 1024 => 8 * 1024,
            <= 32L * 1024 * 1024 * 1024 => 16 * 1024,
            _ => 32 * 1024
        };
    }

    /// <summary>
    ///     Space available for files after formatting: the partition minus the two copies of the allocation
    ///     table and the reserved sectors, rounded down to whole clusters, minus a small safety margin.
    /// </summary>
    public static long UsableFat32Bytes(long partitionBytes)
    {
        if (partitionBytes <= 0) return 0;
        long cluster = Fat32ClusterBytes(partitionBytes);
        var clusters = partitionBytes / cluster;
        const long reserved = 32 * 512;
        var tables = 2 * 4 * clusters;
        const long margin = 1024 * 1024;
        var usable = partitionBytes - tables - reserved - margin;
        return Math.Max(0, usable / cluster * cluster);
    }

    /// <summary>The usable space of the partition the writer would create on a disk of this size.</summary>
    public static long UsableBytesOnDisk(long diskBytes)
    {
        return UsableFat32Bytes(PlannedPartitionBytes(diskBytes));
    }

    /// <summary>
    ///     Space the media folder needs on a FAT32 stick: every file rounded up to whole clusters, at least one
    ///     cluster per folder, the directory entries, and, when <c>sources\install.wim</c> is too large for
    ///     FAT32, the split parts that replace it. Pass the result as <see cref="UsbSelectionContext.RequiredBytes" />.
    /// </summary>
    /// <param name="mediaRoot">The prepared media folder.</param>
    /// <param name="clusterBytes">Cluster size to round to. The default is right for any stick up to 32 GB.</param>
    public static long RequiredFat32Bytes(string mediaRoot, int clusterBytes = ConservativeClusterBytes)
    {
        var root = new DirectoryInfo(mediaRoot);
        if (!root.Exists)
            throw new PlainstallException(ErrorKind.InvalidInput,
                $"The installation media folder '{mediaRoot}' does not exist.");
        var files = root.EnumerateFiles("*", SearchOption.AllDirectories)
            .Select(f => (Path.GetRelativePath(root.FullName, f.FullName), f.Length))
            .ToList();
        var directories = root.EnumerateDirectories("*", SearchOption.AllDirectories).Count();
        return RequiredFat32Bytes(files, directories, clusterBytes, Fat32MaxFileBytes);
    }

    internal static long RequiredFat32Bytes(IReadOnlyCollection<(string RelativePath, long Length)> files,
        int directoryCount, int clusterBytes, long maxFileBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clusterBytes);
        long total = 0;
        foreach (var (relativePath, length) in files)
            if (length > maxFileBytes && IsInstallWim(relativePath))
                total += SplitBytes(length, clusterBytes);
            else
                total += RoundUp(length, clusterBytes);

        // The root folder lives in a cluster chain too, so count it with the subfolders.
        total += (long)(directoryCount + 1) * clusterBytes;
        total += RoundUp((long)files.Count * DirectoryEntryBytesPerFile, clusterBytes);
        return total;
    }

    /// <summary>Space the split parts of an install image of this size take, with the per-part overhead.</summary>
    internal static long SplitBytes(long imageBytes, int clusterBytes)
    {
        return RoundUp(imageBytes, clusterBytes) + SplitPartCount(imageBytes) * (SplitPartOverheadBytes + clusterBytes);
    }

    /// <summary>How many parts DISM will make, plus one in case it has to make a part larger than asked.</summary>
    internal static long SplitPartCount(long imageBytes)
    {
        const long partBytes = (long)SplitPartSizeMb * 1024 * 1024;
        return (imageBytes + partBytes - 1) / partBytes + 1;
    }

    internal static bool IsInstallWim(string relativePath)
    {
        return string.Equals(relativePath.Replace('/', '\\'), @"sources\install.wim",
            StringComparison.OrdinalIgnoreCase);
    }

    internal static long RoundUp(long value, long unit)
    {
        return value <= 0 ? 0 : (value + unit - 1) / unit * unit;
    }

    /// <summary>
    ///     A size for people: decimal units, the way sticks are sold ("16 GB" on the package is 16 billion bytes).
    /// </summary>
    public static string Format(long bytes)
    {
        var c = CultureInfo.InvariantCulture;
        return bytes switch
        {
            < 0 => "0 bytes",
            < 1000 => string.Format(c, "{0} bytes", bytes),
            < 1000L * 1000 => string.Format(c, "{0:0} KB", bytes / 1e3),
            < 1000L * 1000 * 1000 => string.Format(c, "{0:0} MB", bytes / 1e6),
            < 1000L * 1000 * 1000 * 1000 => string.Format(c, "{0:0.0} GB", bytes / 1e9),
            _ => string.Format(c, "{0:0.0} TB", bytes / 1e12)
        };
    }
}