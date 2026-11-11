using Plainstall.Core.Imaging.Drivers;

namespace Plainstall.Core.Tests.Imaging.Drivers;

public sealed class DriverScannerTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void An_export_style_tree_gives_one_package_per_folder_with_its_architecture()
    {
        SyntheticInf.Write(_temp.File("e1d.inf_amd64_0123456789abcdef"), "e1d.inf", SyntheticInf.Text());
        File.WriteAllBytes(Path.Combine(_temp.File("e1d.inf_amd64_0123456789abcdef"), "e1d.sys"), [1, 2, 3]);
        SyntheticInf.Write(_temp.File("iastorvd.inf_amd64_fedcba9876543210"), "iaStorVD.inf",
            SyntheticInf.Text("SCSIAdapter", null));
        SyntheticInf.Write(_temp.File("oddone.inf_arm64_00000000000000aa"), "oddone.inf", SyntheticInf.Text());

        var result = DriverScanner.Scan(_temp.Path, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
        [
            "e1d.inf_amd64_0123456789abcdef", "iastorvd.inf_amd64_fedcba9876543210", "oddone.inf_arm64_00000000000000aa"
        ], result.Packages.Select(p => p.Name));
        Assert.Equal(InfArchitectures.Amd64, result.Packages[0].FolderArchitecture);
        // The folder name wins over the INF decorations: the export holds only arm64 files.
        Assert.Equal(InfArchitectures.Arm64, result.Packages[2].Architectures);
        Assert.False(result.Packages[0].IsStorage);
        Assert.True(result.Packages[1].IsStorage);
        Assert.Equal(3, result.InfCount);
    }

    [Fact]
    public void A_user_style_tree_with_nesting_gives_each_folder_only_its_own_infs()
    {
        var chipset = _temp.File("Vendor\\Chipset");
        SyntheticInf.Write(chipset, "b.inf", SyntheticInf.Text());
        SyntheticInf.Write(chipset, "a.inf", SyntheticInf.Text());
        SyntheticInf.Write(Path.Combine(chipset, "x64"), "c.inf", SyntheticInf.Text());
        Directory.CreateDirectory(Path.Combine(chipset, "Docs"));
        File.WriteAllText(Path.Combine(chipset, "Docs", "readme.txt"), "hello");
        SyntheticInf.Write(_temp.File(@"Vendor\Audio\Win11\x64"), "audio.INF", SyntheticInf.Text());

        var result = DriverScanner.Scan(_temp.Path, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            [Path.Combine(_temp.Path, "Vendor", "Audio", "Win11", "x64"), chipset, Path.Combine(chipset, "x64")],
            result.Packages.Select(p => p.Folder));
        Assert.Equal(["a.inf", "b.inf"], result.Packages[1].Infs.Select(i => i.FileName));
        Assert.Equal(["c.inf"], result.Packages[2].Infs.Select(i => i.FileName));
        Assert.All(result.Packages, p => Assert.Equal(InfArchitectures.None, p.FolderArchitecture));
    }

    [Fact]
    public void A_folder_without_infs_gives_no_packages()
    {
        Directory.CreateDirectory(_temp.File("empty\\deeper"));
        File.WriteAllText(_temp.File("empty\\setup.exe"), "x");
        File.WriteAllText(_temp.File("empty\\notes.info"), "not an inf");

        var result = DriverScanner.Scan(_temp.Path, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.Packages);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void An_inf_directly_in_the_root_makes_the_root_a_package()
    {
        SyntheticInf.Write(_temp.Path, "root.inf", SyntheticInf.Text());
        SyntheticInf.Write(_temp.File("sub"), "sub.inf", SyntheticInf.Text());

        var result = DriverScanner.Scan(_temp.Path, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([Path.GetFullPath(_temp.Path), _temp.File("sub")], result.Packages.Select(p => p.Folder));
        Assert.Equal(["root.inf"], result.Packages[0].Infs.Select(i => i.FileName));
    }

    [Fact]
    public void A_missing_root_is_a_clear_input_error()
    {
        var ex = Assert.Throws<PlainstallException>(() =>
            DriverScanner.Scan(_temp.File("nope"), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(ErrorKind.InvalidInput, ex.Kind);
        Assert.Contains("does not exist", ex.Message);
    }
}