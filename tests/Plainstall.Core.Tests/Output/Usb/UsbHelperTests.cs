using Plainstall.Core.Output.Usb;
using Plainstall.Core.Tests.Media;

namespace Plainstall.Core.Tests.Output.Usb;

public sealed class UsbSizesTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Theory]
    [InlineData(16_000_000_000L, null)]
    [InlineData(32 * GiB + 1024 * 1024, null)] // exactly fits once the 1 MiB offset is taken off
    [InlineData(64_000_000_000L, UsbSizes.MaxFat32PartitionBytes)]
    [InlineData(256_000_000_000L, UsbSizes.MaxFat32PartitionBytes)]
    public void Partition_size_is_the_whole_disk_or_the_32_GB_cap(long disk, long? expected)
    {
        Assert.Equal(expected, UsbSizes.PartitionSizeFor(disk));
        Assert.True(UsbSizes.PlannedPartitionBytes(disk) <= UsbSizes.MaxFat32PartitionBytes);
    }

    [Theory]
    [InlineData(7_000_000_000L, 4096)]
    [InlineData(15_000_000_000L, 8192)]
    [InlineData(32 * GiB, 16384)]
    [InlineData(64 * GiB, 32768)]
    public void Cluster_sizes_follow_the_Windows_defaults(long partition, int expected)
    {
        Assert.Equal(expected, UsbSizes.Fat32ClusterBytes(partition));
    }

    [Fact]
    public void Usable_space_is_a_little_less_than_the_partition()
    {
        var partition = UsbSizes.PlannedPartitionBytes(16_000_000_000);
        var usable = UsbSizes.UsableFat32Bytes(partition);
        Assert.InRange(usable, partition * 99 / 100, partition - 1);
        Assert.Equal(0, usable % UsbSizes.Fat32ClusterBytes(partition));
        Assert.Equal(0, UsbSizes.UsableFat32Bytes(0));
    }

    [Fact]
    public void Required_space_rounds_every_file_to_whole_clusters()
    {
        // 1 byte and 4097 bytes take one and two 4 KiB clusters; the root folder one more; the
        // directory entries of two files fit in one cluster.
        var required =
            UsbSizes.RequiredFat32Bytes([("a.txt", 1), ("b.txt", 4097)], 0, 4096, UsbSizes.Fat32MaxFileBytes);
        Assert.Equal(4096 + 8192 + 4096 + 4096, required);
    }

    [Fact]
    public void Required_space_counts_a_split_image_with_overhead_per_part()
    {
        const long image = 6 * GiB;
        var required =
            UsbSizes.RequiredFat32Bytes([(@"sources\install.wim", image)], 1, 16384, UsbSizes.Fat32MaxFileBytes);
        Assert.True(required > image + 2 * UsbSizes.SplitPartOverheadBytes);
        Assert.True(required < image + 64L * 1024 * 1024);

        // Any other file of that size is just counted; the writer refuses it before the erase.
        var other = UsbSizes.RequiredFat32Bytes([(@"sources\install.esd", image)], 1, 16384,
            UsbSizes.Fat32MaxFileBytes);
        Assert.True(other < required);
    }

    [Fact]
    public void Required_space_of_a_media_folder()
    {
        using var scratch = new TempDirectory();
        var media = TestMedia.Create(scratch);
        var files = Directory.EnumerateFiles(media, "*", SearchOption.AllDirectories).Count();
        Assert.True(UsbSizes.RequiredFat32Bytes(media) >= files * (long)UsbSizes.ConservativeClusterBytes);
        Assert.Throws<PlainstallException>(() => UsbSizes.RequiredFat32Bytes(scratch.Combine("missing")));
    }

    [Theory]
    [InlineData(999L, "999 bytes")]
    [InlineData(850_000_000L, "850 MB")]
    [InlineData(16_000_000_000L, "16.0 GB")]
    [InlineData(1_000_000_000_000L, "1.0 TB")]
    public void Sizes_are_shown_in_decimal_units_like_on_the_package(long bytes, string expected)
    {
        Assert.Equal(expected, UsbSizes.Format(bytes));
    }

    [Fact]
    public void The_FAT32_file_limit_is_4_GiB_minus_one_byte()
    {
        Assert.Equal(4_294_967_295L, UsbSizes.Fat32MaxFileBytes);
    }
}

public sealed class Fat32LabelTests
{
    [Theory]
    [InlineData(null, "PLAINSTALL")]
    [InlineData("   ", "PLAINSTALL")]
    [InlineData("***", "PLAINSTALL")]
    [InlineData("Windows 11 Pro", "WINDOWS 11")]
    [InlineData("my.stick:1", "MYSTICK1")]
    [InlineData("Ärger", "ARGER")]
    [InlineData("  lead", "LEAD")]
    [InlineData("a b c d e f g", "A B C D E F")]
    [InlineData("x[y]=z+\"q\"<>|/\\?*,;", "XYZQ")]
    [InlineData("WIN_11-(X)", "WIN_11-(X)")]
    public void Labels_are_made_valid_for_FAT32(string? input, string expected)
    {
        var label = Fat32Label.Sanitize(input);
        Assert.Equal(expected, label);
        Assert.InRange(label.Length, 1, Fat32Label.MaxLength);
    }
}

public sealed class UsbPathsTests
{
    private static readonly VolumeFacts Volume = new() { DriveLetter = "F:", VolumePath = TestDisks.StickVolumePath };

    [Theory]
    [InlineData(@"F:\work", true)]
    [InlineData(@"f:\work", true)]
    [InlineData("f:", true)]
    [InlineData(@"\\?\F:\x", true)]
    [InlineData(@"\\?\volume{11111111-2222-3333-4444-555555555555}\x", true)]
    [InlineData(@"G:\work", false)]
    [InlineData(@"\\server\share", false)]
    [InlineData(@"\\?\Volume{00000000-2222-3333-4444-555555555555}\", false)]
    [InlineData("work", false)]
    public void IsOnVolume(string path, bool expected)
    {
        Assert.Equal(expected, UsbPaths.IsOnVolume(path, Volume));
    }

    [Fact]
    public void A_volume_without_letter_is_matched_only_by_name()
    {
        var hidden = Volume with { DriveLetter = null };
        Assert.False(UsbPaths.IsOnVolume(@"F:\work", hidden));
        Assert.True(UsbPaths.IsOnVolume(TestDisks.StickVolumePath + "work", hidden));
    }

    [Fact]
    public void RootsOf_gives_the_drive_root_and_the_volume_name()
    {
        using var scratch = new TempDirectory();
        var roots = UsbPaths.RootsOf(scratch.Combine("not-created-yet", "file.iso"));
        Assert.Equal(Path.GetPathRoot(scratch.Path)!.ToUpperInvariant(), roots[0]);
        // The volume name is there on any local NTFS volume; it is skipped only on unusual temp folders.
        Assert.All(roots.Skip(1), r => Assert.StartsWith(@"\\?\Volume{", r, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RootsOf_a_volume_name_path_is_that_volume()
    {
        Assert.Equal([TestDisks.StickVolumePath], UsbPaths.RootsOf(TestDisks.StickVolumePath + @"cache\x.iso"));
    }
}

public sealed class UsbVerificationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1024 * 1024)]
    [InlineData(3 * 1024 * 1024 + 123)]
    public async Task The_unbuffered_hash_equals_the_normal_hash(int length)
    {
        using var scratch = new TempDirectory();
        var path = scratch.WriteFile("data.bin", TestMedia.Bytes(length, 9));
        var expected = await HashHex.Sha256OfFileAsync(path, null, TestContext.Current.CancellationToken);
        var actual = UsbVerification.Sha256Unbuffered(path, TestContext.Current.CancellationToken, out var bypassed);
        Assert.Equal(expected, actual);
        Assert.True(bypassed, "NTFS supports unbuffered reads, so the cache should have been bypassed.");
    }

    [Fact]
    public void Split_headers_are_read_without_the_image_description()
    {
        using var scratch = new TempDirectory();
        var part = new SyntheticWim { PartNumber = 2, TotalParts = 3, XmlBytes = [0xFF, 0xFE, 0x41] }.Build();
        var path = scratch.WriteFile("install2.swm", part);
        Assert.Equal((2, 3), UsbVerification.ReadSplitHeader(path));
        Assert.Null(UsbVerification.ReadSplitHeader(scratch.WriteFile("other.swm", new byte[300])));
        Assert.Null(UsbVerification.ReadSplitHeader(scratch.WriteFile("short.swm", new byte[10])));
    }
}