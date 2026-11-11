using System.Text;
using Plainstall.Core.Imaging.Updates;

namespace Plainstall.Core.Tests.Imaging.Updates;

public sealed class UpdateStagerTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private string Staging => _temp.File("work\\updates");

    public void Dispose()
    {
        _temp.Dispose();
    }

    /// <summary>A file that passes the checks: a CAB header followed by bytes that make the content unique.</summary>
    private string Package(string name, int size = 10_000, byte fill = 1, bool readOnly = false)
    {
        var path = _temp.File("source\\" + name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[size];
        Array.Fill(bytes, fill);
        "MSCF"u8.CopyTo(bytes);
        File.WriteAllBytes(path, bytes);
        if (readOnly) File.SetAttributes(path, FileAttributes.ReadOnly);
        return path;
    }

    private Task<UpdateStagingResult> Stage(IReadOnlyList<string> packages, UpdateStager? stager = null,
        IProgress<TransferProgress>? progress = null)
    {
        return (stager ?? new UpdateStager()).StageAsync(packages, Staging, progress,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Order_is_preserved_and_each_package_gets_its_own_numbered_folder()
    {
        var ssu = Package("ssu-windows11.0-kb5000001-x64.msu", fill: 1);
        var lcu = Package("windows11.0-kb5000003-x64.msu", fill: 2);
        var net = Package("ndp481-kb5000002-x64.cab", fill: 3, readOnly: true);

        var result = await Stage([ssu, lcu, net]);

        Assert.Equal([1, 2, 3], result.Updates.Select(u => u.Number));
        Assert.Equal([1, 2, 3], result.Updates.Select(u => u.Position));
        Assert.Equal(
            [
                Path.Combine(Staging, "001", Path.GetFileName(ssu)),
                Path.Combine(Staging, "002", Path.GetFileName(lcu)), Path.Combine(Staging, "003", Path.GetFileName(net))
            ],
            result.Updates.Select(u => u.StagedPath));
        foreach (var folder in Directory.GetDirectories(Staging)) Assert.Single(Directory.GetFiles(folder));
        Assert.All(result.Updates, u => Assert.Equal(u.Sha256, HashHex.Sha256Of(File.ReadAllBytes(u.SourcePath))));
        Assert.False(File.GetAttributes(result.Updates[2].StagedPath).HasFlag(FileAttributes.ReadOnly));
        File.SetAttributes(net, FileAttributes.Normal);
    }

    [Fact]
    public async Task Missing_files_and_wrong_extensions_are_all_listed_before_anything_is_copied()
    {
        var good = Package("good.msu");
        var exe = Package("setup.exe");

        var ex = await Assert.ThrowsAsync<PlainstallException>(() =>
            Stage([good, _temp.File("source\\gone.msu"), exe, ""]));

        Assert.Equal(ErrorKind.InvalidInput, ex.Kind);
        Assert.Contains("Update 2", ex.Message);
        Assert.Contains("was not found", ex.Message);
        Assert.Contains("Update 3", ex.Message);
        Assert.Contains("Only .msu and .cab", ex.Message);
        Assert.Contains("Update 4", ex.Message);
        Assert.False(Directory.Exists(Staging));
    }

    [Fact]
    public void Implausible_files_are_reported()
    {
        var tiny = Package("tiny.msu", 100);
        var page = _temp.File("source\\page.msu");
        File.WriteAllText(page, "<!DOCTYPE html><html>" + new string(' ', 8000) + "</html>", Encoding.UTF8);
        var fakeCab = _temp.File("source\\fake.cab");
        File.WriteAllBytes(fakeCab, new byte[8000]);
        var folder = _temp.File("source\\folder.msu");
        Directory.CreateDirectory(folder);

        var problems = UpdateStager.Check([tiny, page, fakeCab, folder]);

        Assert.Equal(4, problems.Count);
        Assert.Contains("too small", problems[0]);
        Assert.Contains("web page", problems[1]);
        Assert.Contains("not a valid .cab", problems[2]);
        Assert.Contains("is a folder", problems[3]);
    }

    [Fact]
    public async Task A_corrupt_copy_is_caught_by_the_hash()
    {
        var lcu = Package("windows11.0-kb5000003-x64.msu");
        var stager = new UpdateStager(copy: async (source, destination, progress, ct) =>
        {
            var bytes = await File.ReadAllBytesAsync(source, ct);
            bytes[^1] ^= 0xFF;
            await File.WriteAllBytesAsync(destination, bytes, ct);
            progress.Report(bytes.Length);
        });

        var ex = await Assert.ThrowsAsync<PlainstallException>(() => Stage([lcu], stager));

        Assert.Equal(ErrorKind.Verification, ex.Kind);
        Assert.Contains("is not the same as the original", ex.Message);
        Assert.False(File.Exists(Path.Combine(Staging, "001", Path.GetFileName(lcu))));
    }

    [Fact]
    public async Task Duplicate_content_is_reported_and_staged_once()
    {
        var a = Package("windows11.0-kb5000003-x64.msu", fill: 7);
        var b = Package("copy-of-the-same.msu", fill: 7);
        var c = Package("other.msu", fill: 8);

        var result = await Stage([a, b, c, a]);

        Assert.Equal([Path.GetFileName(a), Path.GetFileName(c)], result.Updates.Select(u => u.FileName));
        Assert.Equal([1, 3], result.Updates.Select(u => u.Position));
        Assert.Equal([1, 2], result.Updates.Select(u => u.Number));
        Assert.Equal([b, a], result.Updates[0].AlsoListedAs);
        Assert.Equal(2, result.Notes.Count(n => n.Contains("same content as update 1")));
    }

    [Fact]
    public async Task Progress_ends_with_done_equal_to_total_and_never_goes_backwards()
    {
        var a = Package("a.msu", 3_000_000);
        var b = Package("b.msu", 2_000_000);
        var c = Package("c.cab", 1_000_000, 2);
        var reports = new List<TransferProgress>();

        await Stage([a, b, c], progress: new InlineProgress<TransferProgress>(reports.Add));

        var last = reports[^1];
        // Three different files: each is hashed, copied and verified.
        Assert.Equal(3L * (3_000_000 + 2_000_000 + 1_000_000), last.BytesTotal);
        Assert.Equal(last.BytesTotal, last.BytesDone);
        for (var i = 1; i < reports.Count; i++)
        {
            Assert.True(reports[i].BytesDone >= reports[i - 1].BytesDone);
            Assert.True(reports[i].BytesDone * 1.0 / reports[i].BytesTotal!.Value >=
                        reports[i - 1].BytesDone * 1.0 / reports[i - 1].BytesTotal!.Value - 1e-9);
        }
    }

    [Fact]
    public async Task Progress_total_shrinks_for_a_duplicate()
    {
        var a = Package("a.msu", 1_000_000, 4);
        var b = Package("b.msu", 1_000_000, 4);
        var reports = new List<TransferProgress>();

        await Stage([a, b], progress: new InlineProgress<TransferProgress>(reports.Add));

        Assert.Equal(6_000_000L, reports[0].BytesTotal);
        // Hash both, copy and verify one.
        Assert.Equal(new TransferProgress(4_000_000, 4_000_000), reports[^1]);
    }

    [Fact]
    public async Task An_update_named_for_another_architecture_is_left_out_with_a_note()
    {
        var arm = Package("windows11.0-kb5000003-arm64_abcdef.msu", fill: 1);
        var x86 = Package("windows10.0-kb4000000-x86.msu", fill: 3);
        var x64 = Package("windows11.0-kb5000003-x64_abcdef.msu", fill: 2);

        var result = await Stage([arm, x86, x64]);

        var staged = Assert.Single(result.Updates);
        Assert.Equal(Path.GetFileName(x64), staged.FileName);
        Assert.Equal(1, staged.Number);
        Assert.Equal(3, staged.Position);
        Assert.Contains(result.Notes, n => n.Contains("made for arm64, and this Windows image is x64"));
        Assert.Contains(result.Notes, n => n.Contains("made for x86"));
    }

    [Theory]
    [InlineData("windows11.0-kb5043080-x64_953449672073f8fb99badb4cc6d5d7849b9c83e8.msu", "x64")]
    [InlineData("windows11.0-kb5043080-arm64_aaaa.msu", "arm64")]
    [InlineData("Windows10.0-KB4000000-X86.cab", "x86")]
    [InlineData("amd64-update.cab", "x64")]
    [InlineData("my-update.msu", null)]
    [InlineData("pick-x64-or-arm64.msu", null)]
    [InlineData("box64thing.msu", null)]
    public void Architecture_from_a_catalog_file_name(string name, string? expected)
    {
        Assert.Equal(expected, UpdateStager.MadeFor(name));
    }
}