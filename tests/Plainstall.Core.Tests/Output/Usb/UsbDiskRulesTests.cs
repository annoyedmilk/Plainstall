using Plainstall.Core.Output.Usb;

namespace Plainstall.Core.Tests.Output.Usb;

public sealed class UsbDiskRulesTests
{
    private static readonly UsbSelectionContext Plain = new() { RequiredBytes = 6_000_000_000 };

    private static (DiskFacts Disk, UsbSelectionContext Context) Case(string name)
    {
        var stick = TestDisks.Stick();
        return name switch
        {
            "ordinary stick" => (stick, Plain),
            "usb ssd enclosure" => (
                stick with { FriendlyName = "Samsung T7", SizeBytes = 200_000_000_000, IsRemovableMedia = false },
                Plain),
            "system disk on usb" => (stick with { IsSystem = true, IsBoot = true, HoldsWindowsFiles = true }, Plain),
            "internal nvme" => (
                stick with
                {
                    BusType = "NVMe", IsRemovableMedia = false, IsSystem = true, IsBoot = true,
                    SizeBytes = 512_000_000_000
                }, Plain),
            "card reader without media" => (stick with { FriendlyName = "Generic SD/MMC", SizeBytes = 0, Volumes = [] },
                Plain),
            "4 GB stick" => (stick with { SizeBytes = 3_900_000_000 }, Plain with { RequiredBytes = 0 }),
            "1 TB stick" => (stick with { SizeBytes = 1_000_000_000_000 }, Plain),
            "write-protected stick" => (stick with { IsReadOnly = true }, Plain),
            "offline disk" => (stick with { IsOffline = true }, Plain),
            "holds the work folder" => (stick, Plain with { PathsInUse = [@"F:\Plainstall\work"] }),
            "holds the source ISO" => (stick,
                Plain with
                {
                    PathsInUse = [TestDisks.StickVolumePath.ToLowerInvariant() + @"Downloads\Win11_26H2.iso"]
                }),
            "too small for the media" => (stick, Plain with { RequiredBytes = 20_000_000_000 }),
            "missing bus type" => (stick with { BusType = "" }, Plain),
            "no unique id" => (stick with { UniqueId = null }, Plain),
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };
    }

    [Fact]
    public void An_ordinary_stick_is_eligible()
    {
        var (disk, context) = Case("ordinary stick");
        var verdict = UsbDiskRules.Evaluate(disk, context);
        Assert.True(verdict.IsEligible);
        Assert.Empty(verdict.Reasons);
        Assert.Same(disk, verdict.Disk);
    }

    [Fact]
    public void A_usb_disk_windows_calls_an_external_drive_is_offered_only_when_allowed()
    {
        // Some USB sticks report themselves as an external hard disk, like a 128 GB "General USB Flash Drive".
        var stick = TestDisks.Stick() with
        {
            FriendlyName = "General USB Flash Drive", SizeBytes = 128_412_909_568, IsRemovableMedia = false
        };

        var refused = UsbDiskRules.Evaluate(stick, Plain);
        var allowed = UsbDiskRules.Evaluate(stick, Plain with { AllowExternalDrives = true });

        Assert.False(refused.IsEligible);
        Assert.Contains(refused.Reasons, r => r.Contains("confirm that it is a USB stick", StringComparison.Ordinal));
        Assert.True(allowed.IsEligible);
        Assert.Empty(allowed.Reasons);
        Assert.True(allowed.IsExternalDrive);
    }

    [Fact]
    public void Allowing_external_drives_keeps_every_other_rule()
    {
        var allow = Plain with { AllowExternalDrives = true };
        var stick = TestDisks.Stick() with { IsRemovableMedia = false };

        Assert.Contains(UsbDiskRules.Evaluate(stick with { SizeBytes = 1_000_000_000_000 }, allow).Reasons,
            r => r.Contains("larger than 256 GB", StringComparison.Ordinal));
        Assert.Contains(UsbDiskRules.Evaluate(stick with { BusType = "NVMe" }, allow).Reasons,
            r => r.Contains("NVMe", StringComparison.Ordinal));
        Assert.Contains(UsbDiskRules.Evaluate(stick with { IsSystem = true, IsBoot = true }, allow).Reasons,
            r => r.Contains("Windows runs from", StringComparison.Ordinal));
        // A stick that reports itself as removable media needs no extra confirmation.
        Assert.False(UsbDiskRules.Evaluate(TestDisks.Stick(), allow).IsExternalDrive);
    }

    [Theory]
    [InlineData("usb ssd enclosure", "hard drive or SSD")]
    [InlineData("system disk on usb", "Windows runs from")]
    [InlineData("internal nvme", "NVMe")]
    [InlineData("card reader without media", "no memory card")]
    [InlineData("4 GB stick", "too small")]
    [InlineData("1 TB stick", "larger than 256 GB")]
    [InlineData("write-protected stick", "write-protected")]
    [InlineData("offline disk", "offline")]
    [InlineData("holds the work folder", @"F:\Plainstall\work")]
    [InlineData("holds the source ISO", "Win11_26H2.iso")]
    [InlineData("too small for the media", "need")]
    [InlineData("missing bus type", "did not say how this disk is connected")]
    [InlineData("no unique id", "unique id")]
    public void Each_ineligible_disk_names_its_cause(string name, string expected)
    {
        var (disk, context) = Case(name);
        var verdict = UsbDiskRules.Evaluate(disk, context);
        Assert.False(verdict.IsEligible);
        Assert.Contains(verdict.Reasons, r => r.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_system_disk_on_usb_gets_every_reason()
    {
        var verdict = UsbDiskRules.Evaluate(Case("system disk on usb").Disk, Plain);
        Assert.Contains(verdict.Reasons, r => r == "This is the disk Windows runs from.");
        Assert.Contains(verdict.Reasons, r => r.Contains("start Windows"));
        Assert.Contains(verdict.Reasons, r => r.Contains("page file"));
    }

    [Fact]
    public void All_reasons_are_returned_not_just_the_first()
    {
        var disk = TestDisks.Stick() with
        {
            SizeBytes = 3_900_000_000, IsReadOnly = true, IsRemovableMedia = false, IsOffline = true
        };
        var verdict = UsbDiskRules.Evaluate(disk, Plain with { PathsInUse = ["f:\\work"] });
        Assert.True(verdict.Reasons.Count >= 5, string.Join(Environment.NewLine, verdict.Reasons));
    }

    [Fact]
    public void An_empty_card_reader_is_not_also_called_too_small()
    {
        var verdict = UsbDiskRules.Evaluate(Case("card reader without media").Disk, Plain);
        Assert.DoesNotContain(verdict.Reasons, r => r.Contains("too small"));
        Assert.DoesNotContain(verdict.Reasons, r => r.Contains("need"));
    }

    [Theory]
    [InlineData(@"F:\")]
    [InlineData(@"f:\iso\win11.iso")]
    [InlineData("f:")]
    [InlineData(@"\\?\F:\work")]
    [InlineData(@"\\?\Volume{11111111-2222-3333-4444-555555555555}\cache")]
    [InlineData(@"\\.\VOLUME{11111111-2222-3333-4444-555555555555}\")]
    public void Paths_in_use_are_matched_by_letter_or_volume_name(string path)
    {
        var verdict = UsbDiskRules.Evaluate(TestDisks.Stick(), Plain with { PathsInUse = [path] });
        Assert.False(verdict.IsEligible);
        Assert.Contains(verdict.Reasons, r => r.Contains(path) && r.Contains("this build is using"));
    }

    [Theory]
    [InlineData(@"C:\Plainstall\work")]
    [InlineData(@"\\server\share\Win11.iso")]
    [InlineData(@"\\?\Volume{99999999-2222-3333-4444-555555555555}\")]
    [InlineData("")]
    public void Paths_on_other_volumes_do_not_block_the_stick(string path)
    {
        Assert.True(UsbDiskRules.Evaluate(TestDisks.Stick(), Plain with { PathsInUse = [path] }).IsEligible);
    }

    [Theory]
    [InlineData(7_743_995_904L, true)] // a real "8 GB" stick
    [InlineData(UsbDiskRules.MinimumDiskBytes, true)]
    [InlineData(UsbDiskRules.MinimumDiskBytes - 1, false)]
    [InlineData(UsbDiskRules.MaximumDiskBytes, true)]
    [InlineData(UsbDiskRules.MaximumDiskBytes + 1, false)]
    [InlineData(256_641_603_584L, true)] // a real "256 GB" stick holds a little more than 256 billion bytes
    public void Size_limits(long size, bool eligible)
    {
        var verdict =
            UsbDiskRules.Evaluate(TestDisks.Stick(size), new UsbSelectionContext { RequiredBytes = 1_000_000 });
        Assert.Equal(eligible, verdict.IsEligible);
    }

    [Fact]
    public void Space_on_a_large_stick_is_judged_by_the_32_GB_partition()
    {
        // A 128 GB stick only gets a 32 GB partition, so 40 GB of media does not fit.
        var verdict = UsbDiskRules.Evaluate(TestDisks.Stick(128_000_000_000),
            new UsbSelectionContext { RequiredBytes = 40_000_000_000 });
        Assert.False(verdict.IsEligible);
        Assert.Contains(verdict.Reasons, r => r.Contains("room for only"));
    }

    // ------------------------------------------------------------ same disk

    private static bool SameDisk(DiskFacts confirmed, DiskFacts current)
    {
        return UsbDiskRules.Differences(confirmed, current).Count == 0;
    }

    [Fact]
    public void SameDisk_identical()
    {
        var disk = TestDisks.Stick();
        Assert.True(SameDisk(disk, disk with { Volumes = [] }));
        Assert.Empty(UsbDiskRules.Differences(disk, disk));
    }

    [Fact]
    public void SameDisk_different_serial()
    {
        var disk = TestDisks.Stick();
        Assert.False(SameDisk(disk, disk with { SerialNumber = "AA0000000000" }));
        Assert.Contains(UsbDiskRules.Differences(disk, disk with { SerialNumber = "AA0000000000" }),
            d => d.Contains("serial"));
    }

    [Fact]
    public void SameDisk_different_size()
    {
        var disk = TestDisks.Stick();
        var other = disk with { SizeBytes = 32_000_000_000 };
        Assert.False(SameDisk(disk, other));
        Assert.Contains(UsbDiskRules.Differences(disk, other),
            d => d.Contains("size") && d.Contains("32.0 GB") && d.Contains("16.0 GB"));
    }

    [Fact]
    public void SameDisk_same_number_different_unique_id()
    {
        var disk = TestDisks.Stick();
        var other = disk with { UniqueId = @"USBSTOR\DISK&VEN_SANDISK&PROD_ULTRA&REV_1.00\OTHER&0" };
        Assert.False(SameDisk(disk, other));
        Assert.Contains(UsbDiskRules.Differences(disk, other), d => d.Contains("unique id"));
    }

    [Fact]
    public void SameDisk_serial_missing_on_both_sides_is_accepted_when_the_unique_id_matches()
    {
        var disk = TestDisks.Stick() with { SerialNumber = null };
        Assert.True(SameDisk(disk, disk with { SerialNumber = "  " }));
    }

    [Fact]
    public void SameDisk_serial_missing_on_one_side_is_a_difference()
    {
        var disk = TestDisks.Stick();
        Assert.False(SameDisk(disk, disk with { SerialNumber = null }));
        Assert.False(SameDisk(disk with { SerialNumber = null }, disk));
    }

    [Fact]
    public void SameDisk_without_unique_id_never_matches()
    {
        var disk = TestDisks.Stick() with { UniqueId = null };
        Assert.False(SameDisk(disk, disk));
        Assert.False(SameDisk(TestDisks.Stick(), disk));
    }

    [Fact]
    public void SameDisk_ignores_case_and_padding_but_not_the_model()
    {
        var disk = TestDisks.Stick();
        Assert.True(SameDisk(disk,
            disk with
            {
                SerialNumber = " " + disk.SerialNumber!.ToLowerInvariant() + "  ",
                UniqueId = disk.UniqueId!.ToLowerInvariant()
            }));
        Assert.False(SameDisk(disk, disk with { FriendlyName = "Kingston DataTraveler" }));
    }

    [Fact]
    public void A_disk_identity_needs_a_unique_id()
    {
        Assert.Throws<ArgumentException>(() => new DiskIdentity(3, " ", "x", 1));
        Assert.Throws<ArgumentException>(() => DiskIdentity.Of(TestDisks.Stick() with { UniqueId = null }));
    }

    // ------------------------------------------------------------ disk service

    [Fact]
    public async Task The_disk_service_lists_only_usb_disks_with_their_verdicts()
    {
        using var scratch = new TempDirectory();
        var fake = new FakeDiskOperations(null, scratch.Combine("vol"))
        {
            AllDisks =
            [
                TestDisks.Stick(number: 5) with { IsReadOnly = true },
                TestDisks.Stick(number: 0) with
                {
                    BusType = "NVMe", IsRemovableMedia = false, IsBoot = true, IsSystem = true
                },
                TestDisks.Stick(number: 2)
            ]
        };
        var list = await new UsbDiskService(fake).ListAsync(new UsbSelectionContext { RequiredBytes = 1 },
            TestContext.Current.CancellationToken);
        Assert.Equal([2, 5], list.Select(c => c.Disk.Number));
        Assert.True(list[0].IsEligible);
        Assert.False(list[1].IsEligible);
        Assert.Contains(list[1].Reasons, r => r.Contains("write-protected"));
        Assert.Equal(0, fake.DestructiveCalls);
    }
}