using Plainstall.Core.Imaging.Drivers;

namespace Plainstall.Core.Tests.Imaging.Drivers;

public sealed class DriverSelectorTests
{
    private static DriverSelection Select(params DriverPackage[] packages)
    {
        return DriverSelector.Select(packages);
    }

    [Fact]
    public void Among_duplicates_the_highest_version_wins()
    {
        var old = TestPackages.Make(@"C:\x\old", driverVer: "07/18/2024,1.0.0.0");
        var current = TestPackages.Make(@"C:\x\new", driverVer: "07/18/2024,1.0.0.2");
        var middle = TestPackages.Make(@"C:\x\mid", driverVer: "07/18/2024,1.0.0.1");

        var selection = Select(old, current, middle);

        Assert.Equal([current], selection.Selected);
        Assert.Equal([old, middle], selection.Dropped.Select(d => d.Package));
        Assert.All(selection.Dropped.Select(d => d.Reason),
            reason => Assert.Equal(DriverDropReason.OlderDuplicate, reason));
        Assert.All(selection.Dropped, d => Assert.Contains(@"the copy in 'C:\x\new'", d.Note));
        Assert.Contains("1.0.0.2", selection.Dropped[0].Note);
    }

    [Fact]
    public void The_date_ranks_before_the_version()
    {
        var newerDate = TestPackages.Make(@"C:\x\a", driverVer: "08/01/2024,1.0.0.0");
        var higherVersion = TestPackages.Make(@"C:\x\b", driverVer: "07/01/2024,9.0.0.0");
        Assert.Equal([newerDate], Select(higherVersion, newerDate).Selected);
    }

    [Fact]
    public void When_versions_tie_the_date_decides_and_on_a_full_tie_the_first_wins()
    {
        var earlier = TestPackages.Make(@"C:\x\a", driverVer: "07/01/2024,2.0.0.0");
        var later = TestPackages.Make(@"C:\x\b", driverVer: "07/02/2024,2.0.0.0");
        Assert.Equal([later], Select(earlier, later).Selected);

        var first = TestPackages.Make(@"C:\x\first", driverVer: "07/02/2024,2.0");
        var second = TestPackages.Make(@"C:\x\second", driverVer: "07/02/2024,2.0.0.0");
        Assert.Equal([first], Select(first, second).Selected);
    }

    [Fact]
    public void Different_providers_with_the_same_inf_name_are_both_kept()
    {
        var contoso = TestPackages.Make(@"C:\x\a", provider: "Contoso", driverVer: "07/01/2024,1.0");
        var fabrikam = TestPackages.Make(@"C:\x\b", provider: "Fabrikam", driverVer: "07/02/2024,2.0");
        var selection = Select(contoso, fabrikam);
        Assert.Equal([contoso, fabrikam], selection.Selected);
        Assert.Empty(selection.Dropped);
    }

    [Fact]
    public void Inf_name_and_provider_compare_case_insensitively()
    {
        var a = TestPackages.Make(@"C:\x\a", "Contoso.INF", "CONTOSO", "07/01/2024,1.0");
        var b = TestPackages.Make(@"C:\x\b", "contoso.inf", "contoso", "07/02/2024,1.0");
        Assert.Equal([b], Select(a, b).Selected);
    }

    [Fact]
    public void When_no_duplicate_has_a_readable_version_all_are_kept()
    {
        var a = TestPackages.Make(@"C:\x\a", driverVer: "garbage");
        var b = TestPackages.Make(@"C:\x\b", driverVer: null);
        var selection = Select(a, b);
        Assert.Equal([a, b], selection.Selected);
        Assert.Empty(selection.Dropped);
        Assert.Contains(selection.Notes, n => n.Contains("All of them are added"));
    }

    [Fact]
    public void When_some_duplicates_have_a_readable_version_the_readable_highest_wins()
    {
        var unreadable = TestPackages.Make(@"C:\x\a", driverVer: "garbage");
        var readable = TestPackages.Make(@"C:\x\b", driverVer: "01/01/2020,1.0");
        var selection = Select(unreadable, readable);
        Assert.Equal([readable], selection.Selected);
        Assert.Contains("could not be read", selection.Dropped.Single().Note);
    }

    [Theory]
    [InlineData(InfArchitectures.Arm64, "arm64")]
    [InlineData(InfArchitectures.X86, "x86")]
    public void A_driver_for_another_architecture_is_dropped_with_a_note(InfArchitectures architecture, string name)
    {
        var foreign = TestPackages.Make(@"C:\x\foreign", architectures: architecture);
        var native = TestPackages.Make(@"C:\x\native", "other.inf");

        var selection = Select(foreign, native);

        Assert.Equal([native], selection.Selected);
        var dropped = Assert.Single(selection.Dropped);
        Assert.Equal(DriverDropReason.WrongArchitecture, dropped.Reason);
        Assert.Contains($"made for {name}", dropped.Note);
        Assert.Contains("this Windows image is x64", dropped.Note);
    }

    [Fact]
    public void The_export_folder_architecture_wins_over_the_inf_decorations()
    {
        var exported = TestPackages.Make(@"C:\x\e.inf_arm64_0123456789abcdef",
            architectures: InfArchitectures.Amd64 | InfArchitectures.Arm64,
            folderArchitecture: InfArchitectures.Arm64);
        Assert.Equal(DriverDropReason.WrongArchitecture, Select(exported).Dropped.Single().Reason);
    }

    [Fact]
    public void A_multi_architecture_package_and_an_amd64_export_of_the_same_driver_are_duplicates()
    {
        var vendor = TestPackages.Make(@"C:\vendor", architectures: InfArchitectures.Amd64 | InfArchitectures.Arm64,
            driverVer: "09/01/2024,2.0");
        var exported = TestPackages.Make(@"C:\export\contoso.inf_amd64_0123456789abcdef",
            folderArchitecture: InfArchitectures.Amd64, driverVer: "01/01/2024,1.0");
        Assert.Equal([vendor], Select(exported, vendor).Selected);
    }

    [Fact]
    public void A_package_whose_architecture_is_unknown_is_kept()
    {
        var unknown = TestPackages.Make(@"C:\x\filter", architectures: InfArchitectures.None);
        Assert.Equal([unknown], Select(unknown).Selected);
    }

    [Fact]
    public void A_folder_with_only_non_driver_infs_is_dropped()
    {
        var autorun = TestPackages.Make(@"C:\cd", "autorun.inf", driverInf: false);
        var dropped = Assert.Single(Select(autorun).Dropped);
        Assert.Equal(DriverDropReason.NoDriverInf, dropped.Reason);
    }

    [Fact]
    public void The_result_keeps_the_input_order()
    {
        var packages = Enumerable.Range(0, 8)
            .Select(i => TestPackages.Make($@"C:\x\{(char)('h' - i)}", $"d{(char)('h' - i)}.inf"))
            .ToArray();
        var duplicateOfThird = TestPackages.Make(@"C:\x\zz", packages[2].Infs[0].FileName, driverVer: "01/01/2000,0.1");

        var selection = Select([.. packages, duplicateOfThird]);

        Assert.Equal(packages, selection.Selected);
        Assert.Equal(selection.Selected, DriverSelector.Select([.. packages, duplicateOfThird]).Selected);
    }

    [Fact]
    public void Selection_never_touches_the_disk()
    {
        using var temp = new TempDirectory();
        var older = SyntheticInf.Write(temp.File("old"), "contoso.inf", SyntheticInf.Text(driverVer: "01/01/2020,1.0"));
        SyntheticInf.Write(temp.File("new"), "contoso.inf", SyntheticInf.Text(driverVer: "01/01/2024,1.0"));
        var scan = DriverScanner.Scan(temp.Path, cancellationToken: TestContext.Current.CancellationToken);

        var selection = DriverSelector.Select(scan.Packages);

        Assert.Equal(temp.File("new"), selection.Selected.Single().Folder);
        Assert.True(File.Exists(older));
    }
}

public sealed class StorageDetectionTests
{
    [Theory]
    [InlineData("SCSIAdapter")]
    [InlineData("scsiadapter")]
    [InlineData("HDC")]
    [InlineData("hdc")]
    public void By_class_name(string infClass)
    {
        var inf = SyntheticInf.Parse(SyntheticInf.Text(infClass, null));
        Assert.True(inf.IsStorage);
    }

    [Theory]
    [InlineData("{4D36E97B-E325-11CE-BFC1-08002BE10318}")]
    [InlineData("{4d36e97b-e325-11ce-bfc1-08002be10318}")]
    [InlineData("4D36E97B-E325-11CE-BFC1-08002BE10318")]
    [InlineData("\"{4D36E97B-E325-11CE-BFC1-08002BE10318}\"")]
    [InlineData("\"4d36e97b-e325-11ce-bfc1-08002be10318\"")]
    [InlineData("{4D36E96A-E325-11CE-BFC1-08002BE10318}")]
    [InlineData("4d36e96a-e325-11ce-bfc1-08002be10318")]
    [InlineData("\"{4d36E96A-e325-11CE-bfc1-08002BE10318}\"")]
    public void By_class_guid_in_any_spelling(string guid)
    {
        // The class name says something else on purpose: the GUID alone must be enough.
        var inf = SyntheticInf.Parse(SyntheticInf.Text("Unknown", guid));
        Assert.True(inf.IsStorage);
    }

    [Fact]
    public void A_network_driver_is_not_storage()
    {
        var inf = SyntheticInf.Parse(SyntheticInf.Text());
        Assert.False(inf.IsStorage);
    }

    [Fact]
    public void The_setup_image_gets_only_the_storage_infs_of_storage_packages()
    {
        var storageInf = SyntheticInf.Parse(SyntheticInf.Text("SCSIAdapter", null)) with
        {
            Path = @"C:\rst\iaStorVD.inf"
        };
        var companion = SyntheticInf.Parse(SyntheticInf.Text("SoftwareComponent", null)) with
        {
            Path = @"C:\rst\iaStorHsaComponent.inf"
        };
        var rst = new DriverPackage { Folder = @"C:\rst", Infs = [companion, storageInf] };
        var network = TestPackages.Make(@"C:\net", "net.inf");

        var selection = DriverSelector.Select([rst, network]);

        Assert.Equal(2, selection.Selected.Count);
        var setup = Assert.Single(selection.ForSetupImage());
        Assert.Equal(["iaStorVD.inf"], setup.Infs.Select(i => i.FileName));
    }
}