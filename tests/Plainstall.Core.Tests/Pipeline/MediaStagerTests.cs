using Plainstall.Core.Pipeline;

namespace Plainstall.Core.Tests.Pipeline;

public class MediaStagerTests
{
    private static void Write(string root, string relative, string content, bool readOnly = false)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        if (readOnly) File.SetAttributes(path, FileAttributes.ReadOnly);
    }

    [Fact]
    public async Task Media_is_copied_without_the_install_image_and_without_read_only_flags()
    {
        using var source = new TempDirectory();
        using var target = new TempDirectory();
        Write(source.Path, "setup.exe", "setup", true);
        Write(source.Path, @"boot\etfsboot.com", "boot", true);
        Write(source.Path, @"sources\boot.wim", "bootwim", true);
        Write(source.Path, @"sources\install.wim", new string('x', 5000), true);
        Write(source.Path, @"sources\sxs\thing.cab", "cab");
        // A file that only shares the name, somewhere else, is ordinary content.
        Write(source.Path, @"support\install.wim", "not the image");
        Directory.CreateDirectory(Path.Combine(source.Path, "empty-folder"));
        var reports = new List<TransferProgress>();

        var copied = await MediaStager.CopyMediaAsync(source.Path, target.Path,
            new InlineProgress<TransferProgress>(reports.Add), TestContext.Current.CancellationToken);

        Assert.True(File.Exists(target.File("setup.exe")));
        Assert.True(File.Exists(target.File(@"sources\boot.wim")));
        Assert.True(File.Exists(target.File(@"sources\sxs\thing.cab")));
        Assert.True(File.Exists(target.File(@"support\install.wim")));
        Assert.False(File.Exists(target.File(@"sources\install.wim")));
        Assert.True(Directory.Exists(target.File("empty-folder")));
        Assert.False(File.GetAttributes(target.File(@"sources\boot.wim")).HasFlag(FileAttributes.ReadOnly));
        Assert.Equal("setup".Length + "boot".Length + "bootwim".Length + "cab".Length + "not the image".Length, copied);
        Assert.Equal(copied, reports[^1].BytesDone);
        Assert.Equal(copied, reports[^1].BytesTotal);

        // Clean up read-only source files so the temp folder can be removed.
        foreach (var file in Directory.EnumerateFiles(source.Path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
    }

    [Fact]
    public async Task An_esd_install_image_is_left_out_as_well()
    {
        using var source = new TempDirectory();
        using var target = new TempDirectory();
        Write(source.Path, @"sources\install.esd", "esd");
        Write(source.Path, "bootmgr", "b");

        await MediaStager.CopyMediaAsync(source.Path, target.Path,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(File.Exists(target.File(@"sources\install.esd")));
        Assert.True(File.Exists(target.File("bootmgr")));
    }

    [Fact]
    public void Edition_files_name_the_edition_and_remove_a_product_key_file()
    {
        using var media = new TempDirectory();
        Write(media.Path, @"sources\PID.txt", "[PID]\r\nValue=XXXXX", true);

        MediaStager.WriteEditionFiles(media.Path, "Professional");

        Assert.Equal("[EditionID]\r\nProfessional\r\n[Channel]\r\nRetail\r\n[VL]\r\n0\r\n",
            File.ReadAllText(media.File(@"sources\ei.cfg")));
        Assert.False(File.Exists(media.File(@"sources\PID.txt")));
    }

    [Fact]
    public void An_existing_channel_is_kept_for_volume_media()
    {
        using var media = new TempDirectory();
        Write(media.Path, @"sources\ei.cfg", "[EditionID]\r\nEnterprise\r\n[Channel]\r\nVolume\r\n[VL]\r\n1\r\n");

        MediaStager.WriteEditionFiles(media.Path, "Enterprise");

        var parsed = MediaStager.ParseEiCfg(File.ReadAllLines(media.File(@"sources\ei.cfg")));
        Assert.Equal("Enterprise", parsed["EditionID"]);
        Assert.Equal("Volume", parsed["Channel"]);
        Assert.Equal("1", parsed["VL"]);
    }
}