using Plainstall.Core.Media;

namespace Plainstall.Core.Tests.Media;

public class IsoMountTests
{
    /// <summary>
    ///     Set this environment variable to the full path of a Windows 11 ISO to run the tests that
    ///     really attach it. They only read, need no admin rights, and detach the image again.
    /// </summary>
    public const string IsoVariable = "PLAINSTALL_TEST_ISO";

    /// <summary>
    ///     Microsoft's 26H2 x64 en-US ISO, whose content was inspected by hand. When the test ISO is this file, its known
    ///     facts are checked too.
    /// </summary>
    private const string KnownIsoName = "Windows11_Client_x64_en-us_26300_9457.iso";

    internal static string RequireTestIso()
    {
        var path = Environment.GetEnvironmentVariable(IsoVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(path),
            $"Set {IsoVariable} to the path of a Windows 11 ISO to run this test.");
        Assert.SkipUnless(File.Exists(path), $"{IsoVariable} points to '{path}', which does not exist.");
        return path;
    }

    [Fact]
    public async Task Missing_iso_file_is_reported_before_windows_is_asked()
    {
        using var folder = new TempDirectory();
        var missing = folder.Combine("missing.iso");

        var ex = await Assert.ThrowsAsync<PlainstallException>(() =>
            IsoMount.AttachAsync(missing, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ErrorKind.InvalidInput, ex.Kind);
        Assert.Contains(missing, ex.Message);
    }

    [Fact]
    public void Volume_and_drive_letter_lookups_find_the_system_drive()
    {
        // The lookups that locate a mounted ISO work on any volume, so the system drive can stand in for one.
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory)!; // "C:\"
        var device = VirtualDiskNative.QueryDosDeviceTarget(systemRoot[..2]); // "\Device\HarddiskVolume3"
        Assert.NotNull(device);

        var letterRoot = IsoMount.FindDriveLetterRoot(device);
        var volumeRoot = IsoMount.FindVolumeRoot(device);

        Assert.Equal(systemRoot, letterRoot, true);
        Assert.NotNull(volumeRoot);
        Assert.StartsWith(@"\\?\Volume{", volumeRoot);
        Assert.EndsWith(@"}\", volumeRoot);
        // The same file, reached without the drive letter.
        var relative = Path.Combine(Environment.SystemDirectory[systemRoot.Length..], "kernel32.dll");
        Assert.True(File.Exists(Path.Combine(volumeRoot, relative)));
    }

    [Fact]
    public void Lookups_return_null_for_a_device_that_does_not_exist()
    {
        Assert.Null(IsoMount.FindVolumeRoot(@"\Device\PlainstallNoSuchDevice"));
        Assert.Null(IsoMount.FindDriveLetterRoot(@"\Device\PlainstallNoSuchDevice"));
        Assert.Null(VirtualDiskNative.QueryDosDeviceTarget("PlainstallNoSuchDevice"));
    }

    [Fact]
    public async Task Real_iso_is_attached_inventoried_and_detached()
    {
        var isoPath = RequireTestIso();
        var output = TestContext.Current.TestOutputHelper;
        var cancellationToken = TestContext.Current.CancellationToken;

        string root;
        await using (var mount = await IsoMount.AttachAsync(isoPath, cancellationToken: cancellationToken))
        {
            root = mount.RootPath;
            output?.WriteLine($"Root path: {mount.RootPath}");
            output?.WriteLine($"Path kind: {mount.PathKind}");
            output?.WriteLine($"Device:    {mount.DevicePath}");
            output?.WriteLine($"Elevated:  {Environment.IsPrivilegedProcess}");

            Assert.EndsWith(@"\", root);
            Assert.True(Directory.Exists(root));
            Assert.Equal(Path.GetFullPath(isoPath), mount.IsoPath);
            Assert.True(File.Exists(Path.Combine(root, "setup.exe")));

            var info = MediaInventory.Read(root, null, cancellationToken);
            output?.WriteLine(
                $"Install:   {info.InstallImage.Path}, {info.InstallImage.FileSize:N0} bytes, {info.InstallImage.Compression}");
            output?.WriteLine($"Version:   {info.Version}, {MediaInfo.ArchitectureName}, {info.Language}");
            foreach (var edition in info.Editions)
                output?.WriteLine(
                    $"  {edition.Index,2}  {edition.EditionId,-26} {edition.Name}  {edition.Version}  {edition.InstallationType}");
            foreach (var image in info.BootImage.Images)
                output?.WriteLine(
                    $"  boot {image.Index}  {image.Name}  {image.Version}{(image.Index == info.BootImage.BootIndex ? "  (boot index)" : "")}");
            output?.WriteLine(
                $"Setup image: {info.SetupImage.Index}; BIOS loader: {info.HasBiosBootLoader}; total {info.TotalBytes:N0} bytes");

            Assert.NotEmpty(info.Editions);
            Assert.True(info.Build >= MediaInventory.MinimumBuild);
            Assert.Same(info.Editions[0], info.SelectEdition(info.Editions[0].EditionId.ToUpperInvariant()));
            Assert.True(info.TotalBytes > info.InstallImage.FileSize);
            Assert.True(info.TotalBytes <= new FileInfo(isoPath).Length);

            if (string.Equals(Path.GetFileName(isoPath), KnownIsoName, StringComparison.OrdinalIgnoreCase))
                AssertMatchesKnownIso(info);
        }

        // Detaching is not instant for every observer; give the volume a moment to disappear.
        for (var i = 0; i < 50 && Directory.Exists(root); i++) await Task.Delay(100, cancellationToken);
        Assert.False(Directory.Exists(root), "The ISO is still mounted after Dispose.");
    }

    [Fact]
    public async Task Real_iso_can_be_attached_twice_in_a_row_and_dispose_is_idempotent()
    {
        var isoPath = RequireTestIso();
        var cancellationToken = TestContext.Current.CancellationToken;

        for (var round = 0; round < 2; round++)
        {
            var mount = await IsoMount.AttachAsync(isoPath, cancellationToken: cancellationToken);
            Assert.True(File.Exists(Path.Combine(mount.RootPath, MediaInventory.BootWimPath)));
            DisposeSynchronouslyTwice(mount);
            await mount.DisposeAsync();
        }
    }

    // The synchronous path is what is tested here, so it stays out of the async test body.
    private static void DisposeSynchronouslyTwice(IsoMount mount)
    {
        mount.Dispose();
        mount.Dispose();
    }

    /// <summary>The facts found by inspecting Microsoft's 26H2 x64 en-US ISO.</summary>
    private static void AssertMatchesKnownIso(MediaInfo info)
    {
        Assert.Equal(8_155_984_950, info.InstallImage.FileSize);
        Assert.Equal(WimCompression.Lzx, info.InstallImage.Compression);
        Assert.EndsWith(@"sources\install.wim", info.InstallImage.Path);
        Assert.Equal(
            [
                "Core", "CoreN", "CoreSingleLanguage", "Education", "EducationN", "Professional", "ProfessionalN",
                "ProfessionalEducation", "ProfessionalEducationN", "ProfessionalWorkstation", "ProfessionalWorkstationN"
            ],
            info.Editions.Select(e => e.EditionId));
        Assert.Equal("Windows 11 Pro", info.SelectEdition("Professional").Name);
        Assert.Equal(6, info.SelectEdition("Professional").Index);
        Assert.All(info.Editions, e => Assert.Equal(new Version(10, 0, 26300, 9457), e.Version));
        Assert.All(info.Editions, e => Assert.Equal("Client", e.InstallationType));
        Assert.Equal("en-US", info.Language);

        Assert.Equal(2, info.BootImage.ImageCount);
        Assert.Equal(2, info.BootImage.BootIndex);
        Assert.Equal("Microsoft Windows PE (amd64)", info.BootImage.Images[0].Name);
        Assert.Equal("Microsoft Windows Setup (amd64)", info.BootImage.Images[1].Name);
        Assert.Equal(2, info.SetupImage.Index);
        Assert.Equal(new Version(10, 0, 26100, 9457), info.SetupImage.Version);
        Assert.True(ServicingFamily.AreSame(info.Build, info.SetupImage.Build));

        Assert.True(info.HasBiosBootLoader);
    }
}