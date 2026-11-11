using Plainstall.Core.Output.Usb;
using Plainstall.Core.Tests.Media;

namespace Plainstall.Core.Tests.Output.Usb;

public sealed class UsbWriterTests : IDisposable
{
    /// <summary>Small enough that a 200 KB synthetic install.wim counts as "too large for FAT32".</summary>
    private const long SmallFileLimit = 64 * 1024;

    private static readonly string[] HappyPathCalls =
    [
        "GetDisk", "Clear", "InitializeMbr", "CreatePartition", "WaitForVolume", "Format", "EnsureDriveLetter", "Flush",
        "Eject"
    ];

    private readonly DiskFacts _confirmed = TestDisks.Stick();
    private readonly FakeDiskOperations _disks;
    private readonly RecordingJournal _journal = new();
    private readonly string _media;
    private readonly RecordingProgress _progress = new();

    private readonly TempDirectory _scratch = new();
    private readonly FakeWimTool _wim = new();

    public UsbWriterTests()
    {
        _media = TestMedia.Create(_scratch);
        _disks = new FakeDiskOperations(_confirmed, _scratch.Combine("stick"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------ refused before the erase

    /// <summary>Each failing scenario, with a part of the message it must give ("" when any message is right).</summary>
    public static TheoryData<string, string> PrecheckFailures => new()
    {
        { "confirmed disk changed serial", "serial number" },
        { "same number different unique id", "" },
        { "different size", "" },
        { "disk unplugged", "" },
        { "disk became read-only", "write-protected" },
        { "disk became offline", "" },
        { "disk is now the system disk", "" },
        { "confirmed disk has no unique id", "" },
        { "stick holds a path in use", "" },
        { "stick holds the media folder", "" },
        { "context needs more space than the stick has", "" },
        { "media too large for the stick", "room for only" },
        { "oversized install.esd", "" },
        { "oversized other file", "" },
        { "oversized install.wim in ESD format", "" },
        { "media folder missing", "" },
        { "not installation media", "" },
        { "cancelled before start", "" },
        { "cancelled while checking", "" },
        { "journal cannot record", "" }
    };

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_scratch.Path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        _scratch.Dispose();
    }

    private UsbWriter Writer(UsbWriterOptions? options = null)
    {
        return new UsbWriter(_disks, _wim, _journal, options: options);
    }

    private UsbWriteRequest Request(string label = "PLAINSTALL", UsbSelectionContext? context = null)
    {
        return new UsbWriteRequest
        {
            ConfirmedDisk = _confirmed,
            MediaRoot = _media,
            Context = context ?? new UsbSelectionContext { RequiredBytes = UsbSizes.RequiredFat32Bytes(_media) },
            VolumeLabel = label
        };
    }

    private string OnStick(params string[] parts)
    {
        return Path.Combine([_disks.VolumeFolder, .. parts]);
    }

    private void MakeInstallImageLarge(uint flags = SyntheticWim.FlagCompression | SyntheticWim.FlagLzx)
    {
        new SyntheticWim { PayloadBytes = 200_000, Flags = flags }.AddImage("Windows 11 Pro", "Professional")
            .WriteTo(Path.Combine(_media, "sources", "install.wim"));
    }

    // ------------------------------------------------------------ happy paths

    [Fact]
    public async Task Happy_path_runs_the_disk_steps_in_order_and_copies_everything()
    {
        var result = await Writer().WriteAsync(Request(), _progress, Ct);

        Assert.Equal(HappyPathCalls, _disks.Calls);
        Assert.Equal(["EraseStarting 3", "Finished 3"], _journal.Entries);
        Assert.False(result.ImageWasSplit);
        Assert.True(result.WasEjected);
        Assert.Empty(result.Warnings);
        Assert.Equal(_disks.VolumePath, result.VolumeRoot);
        Assert.Empty(_wim.SplitCalls);

        foreach (var source in Directory.EnumerateFiles(_media, "*", SearchOption.AllDirectories))
        {
            var copy = OnStick(Path.GetRelativePath(_media, source));
            Assert.True(File.Exists(copy), copy);
            Assert.Equal(await File.ReadAllBytesAsync(source, Ct), await File.ReadAllBytesAsync(copy, Ct));
        }

        Assert.True(Directory.Exists(OnStick("support", "empty")));
        Assert.Equal(
            Directory.EnumerateFiles(_media, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length),
            result.BytesWritten);

        var phases = _progress.Reports.Select(p => p.Phase).Distinct().ToList();
        Assert.Equal(
        [
            UsbWritePhase.Checking, UsbWritePhase.Erasing, UsbWritePhase.Formatting, UsbWritePhase.Copying,
            UsbWritePhase.Verifying, UsbWritePhase.Finishing
        ], phases);
        Assert.Equal(100, _progress.Reports.Last(p => p.Phase == UsbWritePhase.Copying).Percent);
    }

    [Fact]
    public async Task A_large_install_wim_is_split_straight_onto_the_stick()
    {
        MakeInstallImageLarge();
        File.SetAttributes(Path.Combine(_media, "sources", "install.wim"), FileAttributes.ReadOnly);
        _disks.DriveRoot = null;

        var result = await Writer(new UsbWriterOptions { MaxFileBytes = SmallFileLimit })
            .WriteAsync(Request(), _progress, Ct);

        Assert.True(result.ImageWasSplit);
        Assert.Equal(
        [
            "GetDisk", "Clear", "InitializeMbr", "CreatePartition", "WaitForVolume", "Format", "EnsureDriveLetter",
            "EnsureDriveLetter", "Flush", "Eject"
        ], _disks.Calls);
        var call = Assert.Single(_wim.SplitCalls);
        Assert.Equal(Path.Combine(_media, "sources", "install.wim"), call.WimFile);
        Assert.Equal(OnStick("sources", "install.swm"), call.SwmFile);
        Assert.Equal(3800, call.PartSizeMb);
        Assert.False(File.Exists(OnStick("sources", "install.wim")));
        Assert.True(File.Exists(OnStick("sources", "install.swm")));
        Assert.True(File.Exists(OnStick("sources", "install2.swm")));
        Assert.Contains(_progress.Reports, p => p is { Phase: UsbWritePhase.SplittingImage, Percent: 100 });
        Assert.Equal(["EraseStarting 3", "Finished 3"], _journal.Entries);
    }

    [Fact]
    public async Task The_split_is_given_the_drive_letter_when_Windows_assigned_one()
    {
        MakeInstallImageLarge();
        _disks.DriveRoot = _disks.VolumePath; // stands in for "E:\": a second name for the same folder
        await Writer(new UsbWriterOptions { MaxFileBytes = SmallFileLimit }).WriteAsync(Request(), null, Ct);
        Assert.Equal(Path.Combine(_disks.VolumePath, "sources", "install.swm"), Assert.Single(_wim.SplitCalls).SwmFile);
    }

    [Fact]
    public async Task A_64_GB_stick_gets_a_32_GB_partition()
    {
        var big = TestDisks.Stick(64_000_000_000);
        _disks.Disk = big;
        await Writer().WriteAsync(Request() with { ConfirmedDisk = big }, null, Ct);
        Assert.True(_disks.PartitionRequested);
        Assert.Equal(UsbSizes.MaxFat32PartitionBytes, _disks.RequestedPartitionSize);
    }

    [Fact]
    public async Task A_16_GB_stick_gets_one_partition_over_the_whole_disk()
    {
        await Writer().WriteAsync(Request(), null, Ct);
        Assert.True(_disks.PartitionRequested);
        Assert.Null(_disks.RequestedPartitionSize);
    }

    [Theory]
    [InlineData("my stick: Win 11!", "MY STICK WI")]
    [InlineData("Plainstall", "PLAINSTALL")]
    [InlineData("", "PLAINSTALL")]
    [InlineData("Größe.Ärger", "GROEARGER")]
    public async Task The_label_is_sanitized_before_formatting(string requested, string expected)
    {
        await Writer().WriteAsync(Request(requested), null, Ct);
        Assert.Equal(expected, _disks.FormattedLabel);
    }

    [Fact]
    public async Task Read_only_source_files_are_copied_writable()
    {
        var setup = Path.Combine(_media, "setup.exe");
        File.SetAttributes(setup, FileAttributes.ReadOnly);
        await Writer().WriteAsync(Request(), null, Ct);
        Assert.Equal(0, (int)(File.GetAttributes(OnStick("setup.exe")) & FileAttributes.ReadOnly));
        Assert.NotEqual(0, (int)(File.GetAttributes(setup) & FileAttributes.ReadOnly));
    }

    [Fact]
    public async Task A_stick_that_cannot_be_ejected_is_still_complete_with_a_warning()
    {
        _disks.EjectResult = false;
        var result = await Writer().WriteAsync(Request(), null, Ct);
        Assert.False(result.WasEjected);
        Assert.Contains(result.Warnings, w => w.Contains("Safely Remove"));
        Assert.Equal(["EraseStarting 3", "Finished 3"], _journal.Entries);
    }

    [Fact]
    public async Task Files_are_copied_through_a_volume_name_path()
    {
        var roots = UsbPaths.RootsOf(_disks.VolumeFolder);
        var volumeName = roots.FirstOrDefault(r => r.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase));
        Assert.SkipWhen(volumeName is null,
            "The temp folder's volume has no volume name (network or substituted drive).");
        var full = Path.GetFullPath(_disks.VolumeFolder);
        _disks.VolumePath = volumeName + full[3..] + @"\";

        var result = await Writer().WriteAsync(Request(), null, Ct);

        Assert.StartsWith(@"\\?\Volume{", result.VolumeRoot);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(_media, "sources", "boot.wim"), Ct),
            await File.ReadAllBytesAsync(OnStick("sources", "boot.wim"), Ct));
    }

    /// <summary>Sets up one failing scenario and returns the call that should fail.</summary>
    private Func<Task> Arrange(string scenario)
    {
        var options = new UsbWriterOptions();
        var request = Request();
        var token = Ct;
        IProgress<UsbWriteProgress> progress = _progress;
        switch (scenario)
        {
            case "confirmed disk changed serial":
                _disks.Disk = _confirmed with { SerialNumber = "0000OTHER" };
                break;
            case "same number different unique id":
                _disks.Disk = _confirmed with { UniqueId = @"USBSTOR\DISK&VEN_KINGSTON\123&0" };
                break;
            case "different size":
                _disks.Disk = _confirmed with { SizeBytes = 32_000_000_000 };
                break;
            case "disk unplugged":
                _disks.Disk = null;
                break;
            case "disk became read-only":
                _disks.Disk = _confirmed with { IsReadOnly = true };
                break;
            case "disk became offline":
                _disks.Disk = _confirmed with { IsOffline = true };
                break;
            case "disk is now the system disk":
                _disks.Disk = _confirmed with { IsSystem = true, IsBoot = true };
                break;
            case "confirmed disk has no unique id":
                _disks.Disk = _confirmed with { UniqueId = null };
                request = request with { ConfirmedDisk = _confirmed with { UniqueId = null } };
                break;
            case "stick holds a path in use":
                request = request with { Context = request.Context with { PathsInUse = [@"F:\Plainstall\work"] } };
                break;
            case "stick holds the media folder":
                var letter = Path.GetPathRoot(Path.GetFullPath(_media))![..2];
                _disks.Disk = _confirmed with
                {
                    Volumes = [.. _confirmed.Volumes, new VolumeFacts { DriveLetter = letter }]
                };
                break;
            case "context needs more space than the stick has":
                request = request with { Context = request.Context with { RequiredBytes = 40_000_000_000 } };
                break;
            case "media too large for the stick":
                options = options with { UsableBytesOnDisk = _ => 10_000 };
                break;
            case "oversized install.esd":
                File.Delete(Path.Combine(_media, "sources", "install.wim"));
                File.WriteAllBytes(Path.Combine(_media, "sources", "install.esd"), new byte[200_000]);
                options = options with { MaxFileBytes = SmallFileLimit };
                break;
            case "oversized other file":
                File.WriteAllBytes(Path.Combine(_media, "sources", "huge.cab"), new byte[200_000]);
                options = options with { MaxFileBytes = SmallFileLimit };
                break;
            case "oversized install.wim in ESD format":
                MakeInstallImageLarge(SyntheticWim.FlagCompression | SyntheticWim.FlagLzms);
                options = options with { MaxFileBytes = SmallFileLimit };
                break;
            case "media folder missing":
                request = request with { MediaRoot = _scratch.Combine("nowhere") };
                break;
            case "not installation media":
                File.Delete(Path.Combine(_media, "sources", "boot.wim"));
                break;
            case "cancelled before start":
                token = new CancellationToken(true);
                break;
            case "cancelled while checking":
                var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
                token = cts.Token;
                _progress.OnReport = p =>
                {
                    if (p.Phase == UsbWritePhase.Checking) cts.Cancel();
                };
                break;
            case "journal cannot record":
                _journal.ThrowOnEraseStarting = true;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        var writer = Writer(options);
        return () => writer.WriteAsync(request, progress, token);
    }

    /// <summary>The most important property of the writer: a failed check never reaches a destructive call.</summary>
    [Theory]
    [MemberData(nameof(PrecheckFailures))]
    public async Task No_failing_check_ever_reaches_a_destructive_call(string scenario, string expected)
    {
        var act = Arrange(scenario);

        var ex = await Assert.ThrowsAnyAsync<Exception>(act);

        Assert.True(ex is PlainstallException or OperationCanceledException, $"{scenario}: {ex}");
        Assert.IsNotType<UsbStickNotUsableException>(ex);
        Assert.Equal(0, _disks.DestructiveCalls);
        // At most the disk was read again; nothing else was asked of Windows.
        Assert.All(_disks.Calls, c => Assert.Equal("GetDisk", c));
        Assert.Empty(_journal.Entries);
        Assert.Empty(_wim.SplitCalls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_disks.VolumeFolder));
        if (ex is PlainstallException { Kind: not ErrorKind.Cancelled } pe)
            Assert.Contains("Nothing was erased", pe.Message);
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public async Task An_oversized_esd_is_refused_before_the_erase_with_a_clear_message()
    {
        var act = Arrange("oversized install.esd");
        var ex = await Assert.ThrowsAsync<PlainstallException>(act);
        Assert.Contains(@"sources\install.esd", ex.Message);
        Assert.Contains("4 GB", ex.Message);
        Assert.Empty(_disks.Calls);
    }

    [Fact]
    public async Task An_esd_compressed_install_wim_is_refused_because_it_cannot_be_split()
    {
        var act = Arrange("oversized install.wim in ESD format");
        var ex = await Assert.ThrowsAsync<PlainstallException>(act);
        Assert.Contains("cannot be split", ex.Message);
        Assert.Contains("ESD", ex.Message);
        Assert.Empty(_disks.Calls);
    }

    // ------------------------------------------------------------ failures after the erase

    [Fact]
    public async Task A_failure_during_format_says_the_stick_is_not_usable()
    {
        _disks.FailOn = "Format";
        var ex = await Assert.ThrowsAsync<UsbStickNotUsableException>(() => Writer().WriteAsync(Request(), null, Ct));
        Assert.Contains("Formatting", ex.Message);
        Assert.Contains("not usable", ex.Message);
        Assert.Contains("write it again", ex.Message);
        Assert.Equal(["GetDisk", "Clear", "InitializeMbr", "CreatePartition", "WaitForVolume", "Format"], _disks.Calls);
        Assert.Equal(["EraseStarting 3"], _journal.Entries);
    }

    [Fact]
    public async Task A_failure_during_copy_says_the_stick_is_not_usable()
    {
        // A folder where setup.exe has to go makes creating that file fail.
        _disks.AfterFormat = volume => Directory.CreateDirectory(Path.Combine(volume, "setup.exe"));
        var ex = await Assert.ThrowsAsync<UsbStickNotUsableException>(() => Writer().WriteAsync(Request(), null, Ct));
        Assert.Contains("Copying", ex.Message);
        Assert.Contains("not usable", ex.Message);
        Assert.Equal(ErrorKind.Output, ex.Kind);
        Assert.DoesNotContain("Flush", _disks.Calls);
        Assert.DoesNotContain("Finished 3", _journal.Entries);
    }

    [Fact]
    public async Task Cancel_during_copy_stops_and_says_the_stick_is_incomplete()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Action cancel = cts.Cancel;
        _progress.OnReport = p =>
        {
            if (p.Phase == UsbWritePhase.Copying) cancel();
        };
        var ex = await Assert.ThrowsAsync<UsbStickNotUsableException>(() =>
            Writer().WriteAsync(Request(), _progress, cts.Token));
        Assert.Equal(ErrorKind.Cancelled, ex.Kind);
        Assert.Contains("incomplete", ex.Message);
        Assert.Contains("cannot be used", ex.Message);
        Assert.Contains("Clear", _disks.Calls);
        Assert.DoesNotContain("Flush", _disks.Calls);
        Assert.DoesNotContain("Eject", _disks.Calls);
        Assert.Equal(["EraseStarting 3"], _journal.Entries);
        Assert.False(File.Exists(OnStick("sources", "boot.wim")));
    }

    [Fact]
    public async Task A_changed_byte_in_boot_wim_fails_verification()
    {
        var tampered = false;
        _progress.OnReport = p =>
        {
            if (p is not { Phase: UsbWritePhase.Verifying, Percent: 0 } || tampered) return;

            tampered = true;
            var path = OnStick("sources", "boot.wim");
            var bytes = File.ReadAllBytes(path);
            bytes[^1] ^= 0xFF;
            File.WriteAllBytes(path, bytes);
        };
        var ex = await Assert.ThrowsAsync<UsbStickNotUsableException>(() =>
            Writer().WriteAsync(Request(), _progress, Ct));
        Assert.Equal(ErrorKind.Verification, ex.Kind);
        Assert.Contains(@"sources\boot.wim", ex.Message);
        Assert.Contains("not usable", ex.Message);
        Assert.DoesNotContain("Flush", _disks.Calls);
    }

    [Fact]
    public async Task A_shortened_file_fails_verification()
    {
        _progress.OnReport = p =>
        {
            if (p is not { Phase: UsbWritePhase.Verifying, Percent: 0 }) return;

            using var stream = new FileStream(OnStick("setup.exe"), FileMode.Open);
            stream.SetLength(10);
        };
        var ex = await Assert.ThrowsAsync<UsbStickNotUsableException>(() =>
            Writer().WriteAsync(Request(), _progress, Ct));
        Assert.Contains("setup.exe", ex.Message);
    }

    [Fact]
    public async Task A_split_that_lost_data_fails_verification()
    {
        MakeInstallImageLarge();
        _wim.WriteTinyParts = true;
        var ex = await Assert.ThrowsAsync<UsbStickNotUsableException>(() =>
            Writer(new UsbWriterOptions { MaxFileBytes = SmallFileLimit }).WriteAsync(Request(), null, Ct));
        Assert.Contains("add up to", ex.Message);
    }

    [Fact]
    public async Task A_missing_split_part_fails_verification()
    {
        MakeInstallImageLarge();
        _wim.Parts = 5;
        _progress.OnReport = p =>
        {
            if (p is { Phase: UsbWritePhase.Verifying, Percent: 0 }) File.Delete(OnStick("sources", "install5.swm"));
        };
        var ex = await Assert.ThrowsAsync<UsbStickNotUsableException>(() =>
            Writer(new UsbWriterOptions { MaxFileBytes = SmallFileLimit }).WriteAsync(Request(), _progress, Ct));
        Assert.Contains("should have 5 parts", ex.Message);
    }
}