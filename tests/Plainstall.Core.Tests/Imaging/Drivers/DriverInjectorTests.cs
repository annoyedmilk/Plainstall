using Plainstall.Core.Imaging.Drivers;

namespace Plainstall.Core.Tests.Imaging.Drivers;

public sealed class DriverInjectorTests : IDisposable
{
    private readonly FakeMountedImage _image;
    private readonly TempDirectory _temp = new();

    public DriverInjectorTests()
    {
        _image = new FakeMountedImage(_temp.File("mount"));
    }

    public void Dispose()
    {
        _temp.Dispose();
    }

    private IReadOnlyList<DriverPackage> ScanTree()
    {
        SyntheticInf.Write(_temp.File("drivers\\net"), "net.inf", SyntheticInf.Text());
        SyntheticInf.Write(_temp.File("drivers\\rst"), "iaStorVD.inf", SyntheticInf.Text("SCSIAdapter", null));
        SyntheticInf.Write(_temp.File("drivers\\rst"), "aaExt.inf", SyntheticInf.Text("Extension", null));
        SyntheticInf.Write(_temp.File("drivers\\rst"), "iaStorComponent.inf",
            SyntheticInf.Text("SoftwareComponent", null));
        SyntheticInf.Write(_temp.File("drivers\\audio"), "audio.inf", SyntheticInf.Text("MEDIA", catalog: null));
        return DriverSelector
            .Select(DriverScanner.Scan(_temp.File("drivers"), cancellationToken: TestContext.Current.CancellationToken)
                .Packages).Selected;
    }

    [Fact]
    public void All_good_adds_every_inf_with_base_infs_before_extensions()
    {
        var packages = ScanTree();

        var result =
            new DriverInjector(_image).Inject(packages, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                "add-driver audio.inf", "add-driver net.inf", "add-driver iaStorComponent.inf",
                "add-driver iaStorVD.inf", "add-driver aaExt.inf"
            ],
            _image.Calls);
        Assert.Equal(5, result.InfsAdded);
        Assert.True(result.AnythingAdded);
        Assert.All(result.Packages, p => Assert.Equal(ActionStatus.Applied, p.ToActionOutcome().Status));
    }

    [Fact]
    public void One_inf_that_throws_is_recorded_and_the_rest_continue()
    {
        var packages = ScanTree();
        _image.RejectedDrivers.Add("iaStorComponent.inf");
        _image.RejectedDrivers.Add("audio.inf");

        var result =
            new DriverInjector(_image).Inject(packages, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                "add-driver-failed audio.inf", "add-driver net.inf", "add-driver-failed iaStorComponent.inf",
                "add-driver iaStorVD.inf", "add-driver aaExt.inf"
            ],
            _image.Calls);
        var audio = result.Packages.Single(p => p.Package.Name == "audio");
        Assert.Equal(DriverPackageStatus.Failed, audio.Status);
        // No catalog file: the reason says why DISM probably refused it.
        Assert.Contains("not signed", audio.Reason);
        var rst = result.Packages.Single(p => p.Package.Name == "rst");
        Assert.Equal(DriverPackageStatus.PartiallyApplied, rst.Status);
        Assert.Contains("2 of 3", rst.Reason);
        Assert.Contains("iaStorComponent.inf", rst.Reason);
        Assert.Equal(ActionStatus.Failed, rst.ToActionOutcome().Status);
        Assert.Equal([DriverPackageStatus.Failed, DriverPackageStatus.Applied, DriverPackageStatus.PartiallyApplied],
            result.Packages.Select(p => p.Status));
        Assert.True(result.AnythingAdded);
    }

    [Fact]
    public void Storage_only_selection_for_the_setup_image()
    {
        SyntheticInf.Write(_temp.File("drivers\\net"), "net.inf", SyntheticInf.Text());
        SyntheticInf.Write(_temp.File("drivers\\rst"), "iaStorVD.inf", SyntheticInf.Text("SCSIAdapter", null));
        SyntheticInf.Write(_temp.File("drivers\\rst"), "iaStorComponent.inf",
            SyntheticInf.Text("SoftwareComponent", null));
        SyntheticInf.Write(_temp.File("drivers\\ahci"), "ahci.inf",
            SyntheticInf.Text("Weird", "4d36e96a-e325-11ce-bfc1-08002be10318"));
        var selection = DriverSelector.Select(DriverScanner
            .Scan(_temp.File("drivers"), cancellationToken: TestContext.Current.CancellationToken).Packages);

        var result = new DriverInjector(_image).Inject(selection.ForSetupImage(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["add-driver ahci.inf", "add-driver iaStorVD.inf"], _image.Calls);
        Assert.All(result.Packages, p => Assert.Equal(DriverPackageStatus.Applied, p.Status));
    }

    [Fact]
    public void Nothing_to_add_reports_nothing_added()
    {
        var empty = new DriverInjector(_image).Inject([], cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(empty.AnythingAdded);
        Assert.Empty(empty.Packages);

        // A selection without storage drivers gives the Setup image nothing to do.
        SyntheticInf.Write(_temp.File("drivers\\net"), "net.inf", SyntheticInf.Text());
        var selection = DriverSelector.Select(DriverScanner
            .Scan(_temp.File("drivers"), cancellationToken: TestContext.Current.CancellationToken).Packages);
        var setup = new DriverInjector(_image).Inject(selection.ForSetupImage(),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(setup.AnythingAdded);
        Assert.Empty(_image.Calls);
    }

    [Fact]
    public void Every_inf_failing_adds_nothing_and_never_throws()
    {
        var packages = ScanTree();
        foreach (var inf in packages.SelectMany(p => p.Infs)) _image.RejectedDrivers.Add(inf.FileName);

        var result =
            new DriverInjector(_image).Inject(packages, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.AnythingAdded);
        Assert.Equal([DriverPackageStatus.Failed, DriverPackageStatus.Failed, DriverPackageStatus.Failed],
            result.Packages.Select(p => p.Status));
    }

    [Fact]
    public void A_cancel_is_honoured_between_packages()
    {
        var packages = ScanTree();
        using var cts = new CancellationTokenSource();
        var progress = new CancelAfterFirst(cts);

        Assert.Throws<OperationCanceledException>(() =>
            new DriverInjector(_image).Inject(packages, progress, cts.Token));
        // The first package finished; nothing after it was started.
        Assert.Equal(["add-driver audio.inf"], _image.Calls);
    }

    private sealed class CancelAfterFirst(CancellationTokenSource cts) : IProgress<DriverInjectionProgress>
    {
        public void Report(DriverInjectionProgress value)
        {
            cts.Cancel();
        }
    }
}