using System.Text;
using Plainstall.Core.Output;

namespace Plainstall.Core.Tests.Output;

public class IsoWriterTests
{
    /// <summary>What an earlier build left at the output path.</summary>
    private static readonly byte[] EarlierOutput = [.. "an earlier ISO"u8];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A file with the size and the volume descriptor of a disc image, and nothing else.</summary>
    internal static byte[] FakeIso(string identifier = "CD001", int sector = 16, int sectors = 64)
    {
        var bytes = new byte[sectors * IsoWriter.SectorSize];
        bytes[sector * IsoWriter.SectorSize] = 1;
        Encoding.ASCII.GetBytes(identifier).CopyTo(bytes, sector * IsoWriter.SectorSize + 1);
        return bytes;
    }

    private static FakeProcessRunner OscdimgThatWrites(byte[] content, params string[] lines)
    {
        return new FakeProcessRunner((request, _) =>
        {
            File.WriteAllBytes(request.Arguments[^1], content);
            return new ProcessResult(0, lines);
        });
    }

    [Fact]
    public async Task Success_writes_to_a_partial_file_verifies_and_renames()
    {
        using var fixture = new Fixture();
        var iso = FakeIso();
        var seenPartialDuringRun = false;
        var partialPath = fixture.PartialPath;
        var outputPath = fixture.OutputPath;
        var runner = new FakeProcessRunner((request, _) =>
        {
            File.WriteAllBytes(request.Arguments[^1], iso);
            seenPartialDuringRun = File.Exists(partialPath) && !File.Exists(outputPath);
            request.OnLine?.Invoke("Scanning source tree");
            request.OnLine?.Invoke("5% complete");
            request.OnLine?.Invoke("5% complete");
            request.OnLine?.Invoke("0% complete"); // oscdimg starts over for its second pass
            request.OnLine?.Invoke("100% complete");
            return new ProcessResult(0, ["Done."]);
        });
        var percents = new List<int>();

        var result =
            await new IsoWriter(runner).WriteAsync(fixture.Request(), new InlineProgress<int>(percents.Add), Token);

        Assert.Equal(fixture.OutputPath, result.Path);
        Assert.Equal(iso.Length, result.SizeBytes);
        Assert.Equal(HashHex.Sha256Of(iso), result.Sha256);
        Assert.Equal(iso, await File.ReadAllBytesAsync(fixture.OutputPath, Token));
        Assert.True(seenPartialDuringRun);
        Assert.False(File.Exists(fixture.PartialPath));
        Assert.Equal([5, 100], percents);

        var request = Assert.Single(runner.Requests);
        Assert.Equal(fixture.OscdimgPath, request.FileName);
        Assert.True(request.KillOnCancel);
        Assert.Equal(fixture.MediaRoot, request.WorkingDirectory); // the boot files are named relative to it
        Assert.Equal(
            [
                "-m",
                "-o",
                "-u2",
                "-udfver102",
                "-lMY_LABEL",
                @"-bootdata:2#p0,e,bboot\etfsboot.com#pEF,e,befi\microsoft\boot\efisys.bin",
                fixture.MediaRoot,
                fixture.PartialPath
            ],
            request.Arguments);
    }

    [Fact]
    public async Task Boot_files_are_taken_from_the_media_folder()
    {
        using var fixture = new Fixture();
        File.Delete(Path.Combine(fixture.MediaRoot, @"boot\etfsboot.com"));
        File.Move(
            Path.Combine(fixture.MediaRoot, @"efi\microsoft\boot\efisys.bin"),
            Path.Combine(fixture.MediaRoot, @"efi\microsoft\boot\efisys_noprompt.bin"));
        var runner = OscdimgThatWrites(FakeIso());

        await new IsoWriter(runner).WriteAsync(fixture.Request(), null, Token);

        Assert.Equal(@"-bootdata:1#pEF,e,befi\microsoft\boot\efisys_noprompt.bin", runner.Requests[0].Arguments[5]);
    }

    [Fact]
    public async Task Non_zero_exit_code_fails_and_leaves_no_file()
    {
        using var fixture = new Fixture();
        var runner = new FakeProcessRunner((request, _) =>
        {
            File.WriteAllBytes(request.Arguments[^1], FakeIso());
            return new ProcessResult(1,
            [
                "50% complete", "ERROR: Could not write to the target",
                "Error 112: There is not enough space on the disk."
            ]);
        });

        var ex = await Assert.ThrowsAsync<PlainstallException>(() =>
            new IsoWriter(runner).WriteAsync(fixture.Request(), null, Token));

        Assert.Equal(ErrorKind.Output, ex.Kind);
        Assert.Contains("exit code 1", ex.Message);
        Assert.Contains("Error 112", ex.Message);
        Assert.DoesNotContain("50% complete", ex.Message);
        Assert.False(File.Exists(fixture.OutputPath));
        Assert.False(File.Exists(fixture.PartialPath));
    }

    [Fact]
    public async Task Existing_output_is_not_overwritten_unless_told_to()
    {
        using var fixture = new Fixture();
        fixture.Folder.WriteFile(@"out put\windows.iso", EarlierOutput);
        var runner = OscdimgThatWrites(FakeIso());

        var ex = await Assert.ThrowsAsync<PlainstallException>(() =>
            new IsoWriter(runner).WriteAsync(fixture.Request(), null, Token));

        Assert.Equal(ErrorKind.Output, ex.Kind);
        Assert.Contains("already exists", ex.Message);
        Assert.Contains(fixture.OutputPath, ex.Message);
        Assert.Empty(runner.Requests);
        Assert.Equal(EarlierOutput, await File.ReadAllBytesAsync(fixture.OutputPath, Token));
    }

    [Fact]
    public async Task Existing_output_is_replaced_when_overwrite_is_set()
    {
        using var fixture = new Fixture();
        fixture.Folder.WriteFile(@"out put\windows.iso", EarlierOutput);
        var iso = FakeIso("NSR02", 17);

        var result = await new IsoWriter(OscdimgThatWrites(iso)).WriteAsync(fixture.Request(true), null, Token);

        Assert.Equal(iso, await File.ReadAllBytesAsync(result.Path, Token));
        Assert.False(File.Exists(fixture.PartialPath));
    }

    [Fact]
    public async Task Existing_output_survives_a_failed_overwrite()
    {
        using var fixture = new Fixture();
        fixture.Folder.WriteFile(@"out put\windows.iso", EarlierOutput);
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(2, ["failed"]));

        await Assert.ThrowsAsync<PlainstallException>(() =>
            new IsoWriter(runner).WriteAsync(fixture.Request(true), null, Token));

        Assert.Equal(EarlierOutput, await File.ReadAllBytesAsync(fixture.OutputPath, Token));
    }

    [Fact]
    public async Task Stale_partial_file_from_an_earlier_run_is_replaced()
    {
        using var fixture = new Fixture();
        fixture.Folder.WriteFile(@"out put\windows.iso.partial", new byte[77]);
        var iso = FakeIso();

        var result = await new IsoWriter(OscdimgThatWrites(iso)).WriteAsync(fixture.Request(), null, Token);

        Assert.Equal(iso.Length, result.SizeBytes);
    }

    public static TheoryData<string, byte[]?> BadResults()
    {
        return new TheoryData<string, byte[]?>
        {
            { "no file", null },
            { "empty file", [] },
            { "no file system", new byte[64 * IsoWriter.SectorSize] },
            { "not whole sectors", [.. FakeIso(), 0] },
            { "too short for a volume descriptor", new byte[16 * IsoWriter.SectorSize] },
            { "descriptor outside sectors 16 to 31", FakeIso(sector: 32) },
            { "smaller than the largest file", FakeIso(sectors: 32) },
            { "unknown identifier", FakeIso("CDXXX") }
        };
    }

    [Theory]
    [MemberData(nameof(BadResults))]
    public async Task Result_that_fails_verification_is_removed(string what, byte[]? content)
    {
        using var fixture = new Fixture();
        // 40 sectors: larger than the 32-sector image of the "smaller than the largest file" case.
        fixture.Folder.WriteFile(@"work folder\media\sources\install.wim", new byte[40 * IsoWriter.SectorSize]);
        var runner = new FakeProcessRunner((request, _) =>
        {
            if (content is not null) File.WriteAllBytes(request.Arguments[^1], content);
            return new ProcessResult(0, []);
        });

        var ex = await Assert.ThrowsAsync<PlainstallException>(() =>
            new IsoWriter(runner).WriteAsync(fixture.Request(), null, Token));

        Assert.Equal(ErrorKind.Verification, ex.Kind);
        Assert.False(File.Exists(fixture.OutputPath), what);
        Assert.False(File.Exists(fixture.PartialPath), what);
    }

    [Theory]
    [InlineData("NTFS", true)]
    [InlineData("exFAT", true)]
    [InlineData("FAT32", false)]
    [InlineData("FAT", false)]
    [InlineData(null, true)]
    public void Only_a_file_system_that_holds_files_of_4_gb_or_more_can_take_the_iso(string? fileSystem, bool fits)
    {
        Assert.Equal(fits, IsoWriter.CheckTarget(@"E:\out", 7L << 30, null, fileSystem).Count == 0);
        // A small ISO fits on FAT32 too.
        Assert.Empty(IsoWriter.CheckTarget(@"E:\out", 1L << 30, null, fileSystem));
    }

    [Fact]
    public void The_iso_needs_the_media_size_with_a_margin()
    {
        var needed = IsoWriter.RequiredBytes(7L << 30);
        Assert.True(needed > 7L << 30);
        Assert.Empty(IsoWriter.CheckTarget(@"E:\out", 7L << 30, needed, "NTFS"));
        Assert.Contains("not enough free space", Assert.Single(IsoWriter.CheckTarget(@"E:\out", 7L << 30, needed - 1,
            "NTFS")));
    }

    [Fact]
    public async Task Too_little_free_space_stops_before_oscdimg_runs()
    {
        using var fixture = new Fixture();
        var runner = OscdimgThatWrites(FakeIso());
        string? probed = null;
        var writer = new IsoWriter(runner, null, folder =>
        {
            probed = folder;
            return 1024;
        });

        var ex = await Assert.ThrowsAsync<PlainstallException>(() => writer.WriteAsync(fixture.Request(), null, Token));

        Assert.Equal(ErrorKind.Preflight, ex.Kind);
        Assert.Contains("not enough free space", ex.Message);
        Assert.Equal(Path.GetDirectoryName(fixture.OutputPath), probed);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task Cancellation_removes_the_partial_file_and_is_passed_on()
    {
        using var fixture = new Fixture();
        var runner = new FakeProcessRunner((request, token) =>
        {
            File.WriteAllBytes(request.Arguments[^1], new byte[5000]);
            throw new OperationCanceledException(token);
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new IsoWriter(runner).WriteAsync(fixture.Request(), null, Token));

        Assert.False(File.Exists(fixture.PartialPath));
        Assert.False(File.Exists(fixture.OutputPath));
    }

    [Fact]
    public async Task Media_without_an_efi_boot_image_is_refused()
    {
        using var fixture = new Fixture();
        File.Delete(Path.Combine(fixture.MediaRoot, @"efi\microsoft\boot\efisys.bin"));
        var runner = OscdimgThatWrites(FakeIso());

        var ex = await Assert.ThrowsAsync<PlainstallException>(() =>
            new IsoWriter(runner).WriteAsync(fixture.Request(), null, Token));

        Assert.Equal(ErrorKind.Output, ex.Kind);
        Assert.Contains("EFI boot image", ex.Message);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task Output_inside_the_media_folder_is_refused()
    {
        using var fixture = new Fixture();
        var runner = OscdimgThatWrites(FakeIso());
        var request = fixture.Request() with { OutputPath = Path.Combine(fixture.MediaRoot, "sources", "out.iso") };

        var ex = await Assert.ThrowsAsync<PlainstallException>(() =>
            new IsoWriter(runner).WriteAsync(request, null, Token));

        Assert.Equal(ErrorKind.InvalidInput, ex.Kind);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task Output_path_that_is_a_folder_is_refused()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.OutputPath);
        var runner = OscdimgThatWrites(FakeIso());

        var ex = await Assert.ThrowsAsync<PlainstallException>(() =>
            new IsoWriter(runner).WriteAsync(fixture.Request(true), null, Token));

        Assert.Equal(ErrorKind.InvalidInput, ex.Kind);
        Assert.Empty(runner.Requests);
    }

    [Theory]
    [InlineData("CD001", 16)]
    [InlineData("BEA01", 16)]
    [InlineData("NSR02", 17)]
    [InlineData("NSR03", 17)]
    [InlineData("NSR02", 31)]
    public void VerifyIsoFile_accepts_every_standard_identifier_in_sectors_16_to_31(string identifier, int sector)
    {
        using var folder = new TempDirectory();
        var path = folder.WriteFile("ok.iso", FakeIso(identifier, sector));

        IsoWriter.VerifyIsoFile(path);
    }

    [Theory]
    [InlineData("CD001", 15)]
    [InlineData("CD001", 32)]
    [InlineData("TEA01", 16)]
    [InlineData("cd001", 16)]
    public void VerifyIsoFile_rejects_anything_else(string identifier, int sector)
    {
        using var folder = new TempDirectory();
        var path = folder.WriteFile("bad.iso", FakeIso(identifier, sector));

        var ex = Assert.Throws<PlainstallException>(() => IsoWriter.VerifyIsoFile(path));

        Assert.Equal(ErrorKind.Verification, ex.Kind);
        Assert.Contains(path, ex.Message);
    }

    [Theory]
    [InlineData("5% complete", 5)]
    [InlineData("100% complete", 100)]
    [InlineData("  37% complete", 37)]
    [InlineData("0% complete", 0)]
    [InlineData("101% complete", null)]
    [InlineData("Scanning source tree complete (1000 files in 80 directories)", null)]
    [InlineData("complete", null)]
    public void Progress_lines_are_recognized(string line, int? expected)
    {
        Assert.Equal(expected, IsoWriter.TryParsePercent(line));
    }

    /// <summary>Stands in for oscdimg: records the request and does whatever the test tells it to.</summary>
    private sealed class FakeProcessRunner(Func<ProcessRequest, CancellationToken, ProcessResult> behaviour)
        : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(behaviour(request, cancellationToken));
        }
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            MediaRoot = Folder.Combine("work folder", "media");
            Folder.WriteFile(@"work folder\media\boot\etfsboot.com");
            Folder.WriteFile(@"work folder\media\efi\microsoft\boot\efisys.bin");
            Folder.WriteFile(@"work folder\media\sources\install.wim", new byte[10_000]);
            Folder.WriteFile(@"work folder\media\setup.exe", new byte[500]);
            OscdimgPath = Folder.WriteFile(@"tools\oscdimg.exe");
            OutputPath = Folder.Combine("out put", "windows.iso");
        }

        public TempDirectory Folder { get; } = new();
        public string MediaRoot { get; }
        public string OutputPath { get; }
        public string OscdimgPath { get; }

        public string PartialPath => OutputPath + IsoWriter.PartialSuffix;

        public void Dispose()
        {
            Folder.Dispose();
        }

        public IsoWriteRequest Request(bool overwrite = false)
        {
            return new IsoWriteRequest
            {
                MediaRoot = MediaRoot,
                OutputPath = OutputPath,
                OscdimgPath = OscdimgPath,
                VolumeLabel = "my label",
                Overwrite = overwrite
            };
        }
    }
}