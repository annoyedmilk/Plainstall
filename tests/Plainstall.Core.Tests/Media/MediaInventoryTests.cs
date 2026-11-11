using Plainstall.Core.Media;

namespace Plainstall.Core.Tests.Media;

public class MediaInventoryTests
{
    /// <summary>A media folder laid out like Microsoft's 26H2 x64 ISO, with synthetic image files.</summary>
    private static TempDirectory StandardMedia(SyntheticWim? install = null, SyntheticWim? boot = null)
    {
        var folder = new TempDirectory();
        (install ?? SyntheticWim.Install26H2()).WriteTo(folder.Combine("sources", "install.wim"));
        (boot ?? SyntheticWim.Boot26H2()).WriteTo(folder.Combine("sources", "boot.wim"));
        folder.WriteFile(@"boot\etfsboot.com");
        folder.WriteFile(@"efi\microsoft\boot\efisys.bin");
        folder.WriteFile(@"efi\microsoft\boot\efisys_noprompt.bin");
        folder.WriteFile("setup.exe");
        return folder;
    }

    private static MediaInfo ReadMedia(string rootPath)
    {
        return MediaInventory.Read(rootPath, null, TestContext.Current.CancellationToken);
    }

    private static PlainstallException ReadFails(TempDirectory folder)
    {
        var ex = Assert.Throws<PlainstallException>(() => ReadMedia(folder.Path));
        Assert.Equal(ErrorKind.InvalidInput, ex.Kind);
        return ex;
    }

    [Fact]
    public void Standard_media_is_described_like_the_real_26H2_iso()
    {
        using var folder = StandardMedia();

        var info = ReadMedia(folder.Path);

        Assert.Equal(11, info.Editions.Count);
        Assert.Equal(folder.Combine("sources", "install.wim"), info.InstallImage.Path);
        Assert.Equal(WimCompression.Lzx, info.InstallImage.Compression);
        Assert.Equal(folder.Combine("sources", "boot.wim"), info.BootImage.Path);
        Assert.Equal("en-US", info.Language);
        Assert.Equal(new Version(10, 0, 26300, 9457), info.Version);
        Assert.Equal(26300, info.Build);
        Assert.True(info.HasBiosBootLoader);

        // Microsoft pairs a 26300 install image with a 26100 Setup image. That must be accepted.
        Assert.Equal(2, info.SetupImage.Index);
        Assert.Equal("Microsoft Windows Setup (amd64)", info.SetupImage.Name);
        Assert.Equal(26100, info.SetupImage.Build);

        var expectedBytes = Directory.EnumerateFiles(folder.Path, "*", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
        Assert.Equal(expectedBytes, info.TotalBytes);
    }

    [Fact]
    public void Root_path_with_a_trailing_backslash_works_like_a_mounted_volume_root()
    {
        using var folder = StandardMedia();

        var info = ReadMedia(folder.Path + Path.DirectorySeparatorChar);

        Assert.Equal(11, info.Editions.Count);
    }

    [Fact]
    public void Install_esd_is_accepted_and_reported_as_lzms()
    {
        using var folder = StandardMedia();
        File.Delete(folder.Combine("sources", "install.wim"));
        var esd = SyntheticWim.Install26H2();
        esd.Flags = SyntheticWim.FlagCompression | SyntheticWim.FlagLzms;
        esd.WriteTo(folder.Combine("sources", "install.esd"));

        var info = ReadMedia(folder.Path);

        Assert.Equal(folder.Combine("sources", "install.esd"), info.InstallImage.Path);
        Assert.Equal(WimCompression.Lzms, info.InstallImage.Compression);
    }

    [Fact]
    public void Install_wim_is_preferred_over_esd_and_swm()
    {
        using var folder = StandardMedia();
        new SyntheticWim().AddImage("Other", "Core").WriteTo(folder.Combine("sources", "install.esd"));
        new SyntheticWim().AddImage("Other", "Core").WriteTo(folder.Combine("sources", "install.swm"));

        var info = ReadMedia(folder.Path);

        Assert.Equal(folder.Combine("sources", "install.wim"), info.InstallImage.Path);
        Assert.Equal(11, info.Editions.Count);
    }

    [Fact]
    public void Split_media_is_refused_with_a_clear_message()
    {
        using var folder = StandardMedia();
        File.Move(folder.Combine("sources", "install.wim"), folder.Combine("sources", "install.swm"));

        var ex = ReadFails(folder);

        Assert.Contains("install.swm", ex.Message);
        Assert.Contains("Split media is not supported as a source", ex.Message);
    }

    [Fact]
    public void Install_wim_that_is_one_part_of_a_split_set_is_refused()
    {
        var install = SyntheticWim.Install26H2();
        install.PartNumber = 1;
        install.TotalParts = 2;
        using var folder = StandardMedia(install);

        Assert.Contains("Split media is not supported as a source", ReadFails(folder).Message);
    }

    [Fact]
    public void Folder_without_an_install_image_is_not_windows_media()
    {
        using var folder = new TempDirectory();
        folder.WriteFile("readme.txt");

        var ex = ReadFails(folder);

        Assert.Contains("does not look like Windows installation media", ex.Message);
        Assert.Contains(@"sources\install.wim", ex.Message);
    }

    [Fact]
    public void Missing_root_folder_is_reported()
    {
        using var folder = new TempDirectory();
        var missing = folder.Combine("nowhere");

        var ex = Assert.Throws<PlainstallException>(() => ReadMedia(missing));

        Assert.Equal(ErrorKind.InvalidInput, ex.Kind);
        Assert.Contains(missing, ex.Message);
    }

    [Fact]
    public void Damaged_install_image_is_reported_as_an_imaging_error()
    {
        using var folder = StandardMedia();
        File.WriteAllBytes(folder.Combine("sources", "install.wim"), new byte[5000]);

        var ex = Assert.Throws<PlainstallException>(() => ReadMedia(folder.Path));

        Assert.Equal(ErrorKind.Imaging, ex.Kind);
    }

    [Fact]
    public void Install_image_without_editions_is_refused()
    {
        using var folder = StandardMedia(new SyntheticWim());

        Assert.Contains("contains no Windows edition", ReadFails(folder).Message);
    }

    [Theory]
    [InlineData("Server")]
    [InlineData("Server Core")]
    [InlineData("WindowsPE")]
    public void Media_that_is_not_a_client_edition_is_refused(string installationType)
    {
        var install = new SyntheticWim().AddImage("Windows Server 2025 Standard", "ServerStandard", installationType,
            build: 26100);
        using var folder = StandardMedia(install);

        var ex = ReadFails(folder);

        Assert.Contains("not a desktop edition of Windows", ex.Message);
        Assert.Contains(installationType, ex.Message);
    }

    [Fact]
    public void One_server_image_among_client_images_is_enough_to_refuse()
    {
        var install = new SyntheticWim()
            .AddImage("Windows 11 Pro", "Professional")
            .AddImage("Windows Server", "ServerStandard", "Server");
        using var folder = StandardMedia(install);

        Assert.Contains("'Windows Server'", ReadFails(folder).Message);
    }

    [Theory]
    [InlineData(19045)] // Windows 10 22H2
    [InlineData(22000)] // Windows 11 21H2
    [InlineData(22620)]
    public void Media_older_than_windows_11_22H2_is_refused(int build)
    {
        using var folder = StandardMedia(new SyntheticWim().AddImage("Windows", "Professional", build: build));

        var ex = ReadFails(folder);

        Assert.Contains($"build {build}", ex.Message);
        Assert.Contains("22621", ex.Message);
    }

    [Theory]
    [InlineData(22621)]
    [InlineData(22631)]
    [InlineData(26100)]
    [InlineData(26200)]
    [InlineData(26300)]
    [InlineData(27000)]
    public void Windows_11_22H2_and_newer_is_accepted(int build)
    {
        using var folder = StandardMedia(new SyntheticWim().AddImage("Windows 11 Pro", "Professional", build: build));

        Assert.Equal(build, ReadMedia(folder.Path).Build);
    }

    [Fact]
    public void Image_without_a_version_is_refused()
    {
        using var folder = StandardMedia(new SyntheticWim().AddImage("Windows 11 Pro", "Professional", build: null));

        Assert.Contains("does not state its Windows version", ReadFails(folder).Message);
    }

    [Theory]
    [InlineData(0, "x86 (32-bit)")]
    [InlineData(12, "arm64")]
    [InlineData(5, "unknown (code 5)")]
    [InlineData(null, "unknown")]
    public void Unsupported_architecture_is_refused(int? arch, string expectedName)
    {
        using var folder = StandardMedia(new SyntheticWim().AddImage("Windows 11 Pro", "Professional", arch: arch));

        var ex = ReadFails(folder);

        Assert.Contains($"for {expectedName} processors", ex.Message);
        Assert.Contains("x64 PCs (Intel and AMD) only", ex.Message);
    }

    [Fact]
    public void One_edition_for_another_architecture_among_x64_ones_is_refused()
    {
        var install = new SyntheticWim()
            .AddImage("Windows 11 Pro", "Professional", arch: 9)
            .AddImage("Windows 11 Pro", "Professional", arch: 12);
        using var folder = StandardMedia(install);

        Assert.Contains("for arm64 processors", ReadFails(folder).Message);
    }

    [Fact]
    public void Missing_boot_wim_is_refused()
    {
        using var folder = StandardMedia();
        File.Delete(folder.Combine("sources", "boot.wim"));

        var ex = ReadFails(folder);

        Assert.Contains(@"sources\boot.wim", ex.Message);
        Assert.Contains("is missing", ex.Message);
    }

    [Fact]
    public void Boot_wim_without_images_is_refused()
    {
        using var folder = StandardMedia(boot: new SyntheticWim());

        Assert.Contains("contains no usable boot image", ReadFails(folder).Message);
    }

    [Fact]
    public void Missing_efi_boot_image_is_refused()
    {
        using var folder = StandardMedia();
        File.Delete(folder.Combine(@"efi\microsoft\boot\efisys.bin"));
        File.Delete(folder.Combine(@"efi\microsoft\boot\efisys_noprompt.bin"));

        var ex = ReadFails(folder);

        Assert.Contains("EFI boot image", ex.Message);
        Assert.Contains("is missing", ex.Message);
    }

    [Fact]
    public void Noprompt_efi_boot_image_alone_is_enough()
    {
        using var folder = StandardMedia();
        File.Delete(folder.Combine(@"efi\microsoft\boot\efisys.bin"));

        Assert.Equal(11, ReadMedia(folder.Path).Editions.Count);
    }

    [Fact]
    public void Missing_bios_boot_loader_is_reported()
    {
        using var folder = StandardMedia();
        File.Delete(folder.Combine(@"boot\etfsboot.com"));

        Assert.False(ReadMedia(folder.Path).HasBiosBootLoader);
    }

    [Fact]
    public void Language_falls_back_to_the_first_listed_language_and_then_to_empty()
    {
        using var noLanguage =
            StandardMedia(new SyntheticWim().AddImage("Windows 11 Pro", "Professional", language: null));
        Assert.Equal("", ReadMedia(noLanguage.Path).Language);

        using var german =
            StandardMedia(new SyntheticWim().AddImage("Windows 11 Pro", "Professional", language: "de-DE"));
        Assert.Equal("de-DE", ReadMedia(german.Path).Language);
    }

    // ---- Setup image selection ----

    private static SyntheticWim BootWith(params string[] names)
    {
        var wim = new SyntheticWim();
        foreach (var name in names) wim.AddImage(name, "WindowsPE", "WindowsPE", build: 26100);
        return wim;
    }

    [Theory]
    [InlineData(new[] { "Microsoft Windows PE (amd64)", "Microsoft Windows Setup (amd64)" }, 2)]
    [InlineData(new[] { "Microsoft Windows Setup (amd64)", "Microsoft Windows PE (amd64)" },
        1)] // by name, not by position
    [InlineData(new[] { "PE", "Recovery", "windows SETUP media" }, 3)] // case-insensitive
    [InlineData(new[] { "Setup A", "Setup B" }, 1)] // the first match
    [InlineData(new[] { "Image one", "Image two", "Image three" }, 2)] // no name says Setup: index 2
    [InlineData(new[] { "", "" }, 2)]
    [InlineData(new[] { "Only image" }, 1)] // a single image is the Setup image
    public void Setup_image_is_chosen_by_name_then_index_two_then_the_only_image(string[] names, int expectedIndex)
    {
        using var folder = StandardMedia(boot: BootWith(names));

        Assert.Equal(expectedIndex, ReadMedia(folder.Path).SetupImage.Index);
    }

    // ---- Edition selection ----

    [Theory]
    [InlineData("Professional", 6)]
    [InlineData("professional", 6)]
    [InlineData("PROFESSIONAL", 6)]
    [InlineData(" Professional ", 6)]
    [InlineData("Core", 1)]
    [InlineData("CoreN", 2)]
    [InlineData("ProfessionalWorkstationN", 11)]
    public void SelectEdition_matches_the_edition_id_ignoring_case(string editionId, int expectedIndex)
    {
        using var folder = StandardMedia();

        Assert.Equal(expectedIndex, ReadMedia(folder.Path).SelectEdition(editionId).Index);
    }

    [Theory]
    [InlineData("Windows 11 Pro")] // the display name is never a match
    [InlineData("Pro")]
    [InlineData("Enterprise")]
    [InlineData("")]
    public void SelectEdition_lists_what_exists_when_the_edition_is_missing(string editionId)
    {
        using var folder = StandardMedia();
        var info = ReadMedia(folder.Path);

        var ex = Assert.Throws<PlainstallException>(() => info.SelectEdition(editionId));

        Assert.Equal(ErrorKind.InvalidInput, ex.Kind);
        Assert.Contains($"no edition '{editionId}'", ex.Message);
        foreach (var edition in info.Editions) Assert.Contains($"{edition.EditionId} ({edition.Name})", ex.Message);
    }

    [Fact]
    public void SelectEdition_never_matches_a_localized_name_that_equals_another_edition_id()
    {
        // A hostile or odd image whose display name is another edition's id must not be picked by that name.
        var install = new SyntheticWim()
            .AddImage("Professional", "Core")
            .AddImage("Windows 11 Pro", "Professional");
        using var folder = StandardMedia(install);

        Assert.Equal(2, ReadMedia(folder.Path).SelectEdition("Professional").Index);
    }

    [Fact]
    public void Cancellation_is_honoured()
    {
        using var folder = StandardMedia();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => MediaInventory.Read(folder.Path, null, cancelled.Token));
    }
}

public class ServicingFamilyTests
{
    [Theory]
    [InlineData(22621, 22621)]
    [InlineData(22631, 22621)]
    [InlineData(26100, 26100)]
    [InlineData(26200, 26100)]
    [InlineData(26300, 26100)]
    [InlineData(22000, 22000)]
    [InlineData(19045, 19045)]
    [InlineData(27000, 27000)]
    public void Of_maps_a_build_to_the_base_build_of_its_family(int build, int expectedBase)
    {
        Assert.Equal(expectedBase, ServicingFamily.Of(build).BaseBuild);
    }

    [Theory]
    [InlineData(22621, 22631, true)]
    [InlineData(26100, 26200, true)]
    [InlineData(26100, 26300, true)]
    [InlineData(26200, 26300, true)]
    [InlineData(26300, 26300, true)]
    [InlineData(22631, 26100, false)]
    [InlineData(22000, 22621, false)]
    [InlineData(26300, 27000, false)]
    public void AreSame_knows_the_enablement_package_builds(int a, int b, bool expected)
    {
        Assert.Equal(expected, ServicingFamily.AreSame(a, b));
        Assert.Equal(expected, ServicingFamily.AreSame(b, a));
    }

    [Fact]
    public void Negative_build_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ServicingFamily.Of(-1));
    }
}