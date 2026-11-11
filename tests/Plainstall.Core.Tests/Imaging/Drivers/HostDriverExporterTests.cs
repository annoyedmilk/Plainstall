using Plainstall.Core.Imaging.Drivers;

namespace Plainstall.Core.Tests.Imaging.Drivers;

public sealed class HostDriverExporterTests : IDisposable
{
    private readonly HostDriverExporter _exporter;
    private readonly FakeProcessRunner _runner = new();
    private readonly TempDirectory _temp = new();

    public HostDriverExporterTests()
    {
        _exporter = new HostDriverExporter(_runner, _temp.File("logs\\dism-export.log"),
            @"C:\Windows\System32\dism.exe");
    }

    private string Destination => _temp.File(@"work\drivers\this-pc");

    public void Dispose()
    {
        _temp.Dispose();
    }

    private static void ExportTwoPackages(ProcessRequest request)
    {
        var destination =
            request.Arguments.Single(a => a.StartsWith("/Destination:", StringComparison.Ordinal))[
                "/Destination:".Length..];
        SyntheticInf.Write(Path.Combine(destination, "e1d.inf_amd64_0123456789abcdef"), "e1d.inf", SyntheticInf.Text());
        SyntheticInf.Write(Path.Combine(destination, "iastorvd.inf_amd64_fedcba9876543210"), "iastorvd.inf",
            SyntheticInf.Text("SCSIAdapter"));
    }

    [Fact]
    public async Task Runs_dism_with_the_expected_arguments_and_never_kills_it()
    {
        _runner.SideEffect = ExportTwoPackages;

        var result = await _exporter.ExportAsync(Destination, cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(_runner.Requests);
        Assert.Equal(@"C:\Windows\System32\dism.exe", request.FileName);
        Assert.Equal(
        [
            "/English", "/Online", "/Export-Driver", "/Destination:" + Destination,
            "/LogPath:" + _temp.File("logs\\dism-export.log")
        ], request.Arguments);
        Assert.False(request.KillOnCancel);
        Assert.Equal(2, result.Packages.Count);
        Assert.True(result.Packages[1].IsStorage);
    }

    [Fact]
    public async Task Reports_progress_from_the_exporting_lines()
    {
        _runner.Output.AddRange([
            "Deployment Image Servicing and Management tool",
            "Exporting 1 of 4 - oem0.inf: The driver package successfully exported.", "Exporting 2 of 4 - oem1.inf: ok"
        ]);
        _runner.SideEffect = ExportTwoPackages;
        var reports = new List<int>();

        await _exporter.ExportAsync(Destination, new InlineProgress<int>(reports.Add),
            TestContext.Current.CancellationToken);

        Assert.Equal([25, 50, 100], reports);
    }

    [Fact]
    public async Task A_non_zero_exit_code_is_explained_and_names_the_log()
    {
        _runner.ExitCode = 740;
        _runner.Output.AddRange([
            "Deployment Image Servicing and Management tool", "Version: 10.0.26100.1", "Error: 740",
            "Elevated permissions are required to run DISM."
        ]);

        var ex = await Assert.ThrowsAsync<PlainstallException>(() =>
            _exporter.ExportAsync(Destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ErrorKind.Imaging, ex.Kind);
        Assert.Contains("exit code 740", ex.Message);
        Assert.Contains("administrator", ex.Message);
        Assert.Contains("Elevated permissions are required", ex.Message);
        Assert.Contains("dism-export.log", ex.Message);
        Assert.DoesNotContain("Deployment Image Servicing", ex.Message);
    }

    [Fact]
    public async Task Zero_exported_infs_is_an_error_with_a_helpful_message()
    {
        _runner.ExitCode = 0;

        var ex = await Assert.ThrowsAsync<PlainstallException>(() =>
            _exporter.ExportAsync(Destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ErrorKind.Preflight, ex.Kind);
        Assert.Contains("has no drivers from other makers", ex.Message);
        Assert.Contains("Switch off \"Include the drivers of this PC\"", ex.Message);
    }

    [Fact]
    public async Task A_non_empty_destination_is_refused_before_dism_runs()
    {
        Directory.CreateDirectory(Destination);
        await File.WriteAllTextAsync(Path.Combine(Destination, "leftover.txt"), "x",
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<PlainstallException>(() =>
            _exporter.ExportAsync(Destination, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(_runner.Requests);
    }
}