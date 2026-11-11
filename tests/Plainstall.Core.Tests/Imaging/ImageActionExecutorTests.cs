using System.Diagnostics;
using Plainstall.Core.Catalog;
using Plainstall.Core.Imaging;

namespace Plainstall.Core.Tests.Imaging;

public sealed class ImageActionExecutorTests : IDisposable
{
    private readonly ImageActionExecutor _executor;
    private readonly FakeMountedImage _image;
    private readonly TempDirectory _temp = new();

    public ImageActionExecutorTests()
    {
        _image = new FakeMountedImage(_temp.Path);
        _executor = new ImageActionExecutor(_image);
    }

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void An_app_is_removed_in_every_version_and_architecture_by_exact_name()
    {
        _image.AddApp("Microsoft.Paint");
        _image.AddApp("Microsoft.Paint", arch: "neutral");
        _image.AddApp("Microsoft.PaintStudio");
        _image.AddApp("Contoso.Microsoft.Paint");

        var outcome = _executor.Apply(new AppxRemoveProvisionedAction { Package = "microsoft.paint" });

        Assert.Equal(ActionStatus.Applied, outcome.Status);
        // A match on a part of the name would also remove the two look-alikes.
        Assert.Equal(["Contoso.Microsoft.Paint", "Microsoft.PaintStudio"],
            _image.Packages.Select(p => p.DisplayName).Order());
    }

    [Fact]
    public void An_app_that_is_not_in_the_image_is_skipped_not_failed()
    {
        _image.AddApp("Microsoft.Paint");
        var outcome = _executor.Apply(new AppxRemoveProvisionedAction { Package = "Microsoft.Windows.AIHub" });
        Assert.Equal(ActionStatus.Skipped, outcome.Status);
        Assert.Empty(_image.Calls);
    }

    [Fact]
    public void A_removal_that_dism_accepts_but_does_not_perform_is_reported_as_failed()
    {
        _image.AddApp("Microsoft.Paint");
        _image.StubbornPackages.Add(_image.Packages[0].PackageName);
        var outcome = _executor.Apply(new AppxRemoveProvisionedAction { Package = "Microsoft.Paint" });
        Assert.Equal(ActionStatus.Failed, outcome.Status);
    }

    [Fact]
    public void A_capability_is_matched_by_its_base_name()
    {
        _image.Capabilities.Add(new ComponentInfo("App.StepsRecorder~~~~0.0.1.0", ComponentState.Installed));
        _image.Capabilities.Add(new ComponentInfo("App.StepsRecorderPlus~~~~0.0.1.0", ComponentState.Installed));

        var outcome = _executor.Apply(new CapabilityRemoveAction { Name = "App.StepsRecorder" });

        Assert.Equal(ActionStatus.Applied, outcome.Status);
        Assert.Equal(ComponentState.NotPresent, _image.Capabilities[0].State);
        Assert.Equal(ComponentState.Installed, _image.Capabilities[1].State);
        Assert.False(_executor.ServicingPending);
    }

    [Fact]
    public void A_capability_removal_that_stays_pending_is_applied_with_a_note_and_blocks_file_deletion()
    {
        _image.Capabilities.Add(new ComponentInfo("Media.WindowsMediaPlayer~~~~0.0.12.0", ComponentState.Installed));
        _image.DeferredCapabilities.Add("Media.WindowsMediaPlayer~~~~0.0.12.0");
        File.WriteAllText(_temp.File("victim.txt"), "x");

        var capability = _executor.Apply(new CapabilityRemoveAction { Name = "Media.WindowsMediaPlayer" });
        var file = _executor.Apply(new FileDeleteAction { Path = "victim.txt" });

        Assert.Equal(ActionStatus.Applied, capability.Status);
        Assert.Contains("first starts", capability.Reason);
        Assert.True(_executor.ServicingPending);
        Assert.Equal(ActionStatus.Skipped, file.Status);
        Assert.True(File.Exists(_temp.File("victim.txt")));
    }

    [Fact]
    public void A_feature_is_disabled_only_when_it_is_enabled()
    {
        _image.Features.Add(new ComponentInfo("Recall", ComponentState.Installed));
        _image.Features.Add(new ComponentInfo("WorkFolders-Client", ComponentState.Other));

        Assert.Equal(ActionStatus.Applied, _executor.Apply(new FeatureDisableAction { Name = "Recall" }).Status);
        Assert.Equal(ActionStatus.Skipped,
            _executor.Apply(new FeatureDisableAction { Name = "WorkFolders-Client" }).Status);
        Assert.Equal(ActionStatus.Skipped, _executor.Apply(new FeatureDisableAction { Name = "NoSuchFeature" }).Status);
        Assert.Equal(["disable-feature Recall"], _image.Calls);
    }

    [Fact]
    public void A_disabled_feature_is_enabled()
    {
        _image.Features.Add(new ComponentInfo("Microsoft-Windows-Subsystem-Linux", ComponentState.Other));

        var outcome = _executor.Apply(new FeatureEnableAction { Name = "microsoft-windows-subsystem-linux" });

        Assert.Equal(ActionStatus.Applied, outcome.Status);
        Assert.Equal(ComponentState.Installed, _image.Features[0].State);
        Assert.Equal(["enable-feature Microsoft-Windows-Subsystem-Linux"], _image.Calls);
        Assert.False(_executor.ServicingPending);
    }

    [Fact]
    public void A_feature_that_is_missing_enabled_or_without_its_files_is_skipped_and_not_touched()
    {
        _image.Features.Add(new ComponentInfo("VirtualMachinePlatform", ComponentState.Installed));
        _image.Features.Add(new ComponentInfo("NetFx3", ComponentState.PayloadRemoved));
        _image.Features.Add(new ComponentInfo("Gone", ComponentState.NotPresent));

        // Windows Sandbox does not exist on Home.
        var missing = _executor.Apply(new FeatureEnableAction { Name = "Containers-DisposableClientVM" });
        var enabled = _executor.Apply(new FeatureEnableAction { Name = "VirtualMachinePlatform" });
        var removed = _executor.Apply(new FeatureEnableAction { Name = "NetFx3" });
        var notPresent = _executor.Apply(new FeatureEnableAction { Name = "Gone" });

        Assert.Equal(ActionStatus.Skipped, missing.Status);
        Assert.Equal("not present in this image", missing.Reason);
        Assert.Equal(ActionStatus.Skipped, enabled.Status);
        Assert.Equal("already enabled", enabled.Reason);
        Assert.Equal(ActionStatus.Skipped, removed.Status);
        Assert.Contains("files were removed", removed.Reason);
        Assert.Equal(ActionStatus.Skipped, notPresent.Status);
        Assert.Empty(_image.Calls);
    }

    [Fact]
    public void A_feature_enabled_as_pending_is_applied_with_a_note_and_blocks_file_deletion()
    {
        _image.Features.Add(new ComponentInfo("Containers-DisposableClientVM", ComponentState.Other));
        _image.DeferredFeatures.Add("Containers-DisposableClientVM");
        File.WriteAllText(_temp.File("victim.txt"), "x");

        var feature = _executor.Apply(new FeatureEnableAction { Name = "Containers-DisposableClientVM" });
        var file = _executor.Apply(new FileDeleteAction { Path = "victim.txt" });

        Assert.Equal(ActionStatus.Applied, feature.Status);
        Assert.Contains("first starts", feature.Reason);
        Assert.True(_executor.ServicingPending);
        Assert.Equal(ActionStatus.Skipped, file.Status);
        Assert.True(File.Exists(_temp.File("victim.txt")));
    }

    [Fact]
    public void An_enable_that_dism_accepts_but_does_not_perform_is_reported_as_failed()
    {
        _image.Features.Add(new ComponentInfo("VirtualMachinePlatform", ComponentState.Other));
        _image.StubbornFeatures.Add("VirtualMachinePlatform");

        var outcome = _executor.Apply(new FeatureEnableAction { Name = "VirtualMachinePlatform" });

        Assert.Equal(ActionStatus.Failed, outcome.Status);
        Assert.False(_executor.ServicingPending);
    }

    [Fact]
    public void Files_and_folders_are_deleted_and_missing_ones_are_skipped()
    {
        Directory.CreateDirectory(_temp.File(@"Program Files (x86)\Microsoft\Edge\Application\1.2.3"));
        File.WriteAllText(_temp.File(@"Program Files (x86)\Microsoft\Edge\Application\1.2.3\msedge.exe"), "x");
        var readOnly = _temp.File(@"Program Files (x86)\Microsoft\Edge\readonly.dat");
        File.WriteAllText(readOnly, "x");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        Directory.CreateDirectory(_temp.File(@"Program Files (x86)\Microsoft\EdgeWebView"));
        Directory.CreateDirectory(_temp.File(@"Windows\System32"));
        File.WriteAllText(_temp.File(@"Windows\System32\OneDriveSetup.exe"), "x");

        var folder = _executor.Apply(new FileDeleteAction
            { Path = @"Program Files (x86)\Microsoft\Edge", Recurse = true });
        var file = _executor.Apply(new FileDeleteAction { Path = @"Windows\System32\OneDriveSetup.exe" });
        var missing = _executor.Apply(new FileDeleteAction { Path = @"Windows\SysWOW64\OneDriveSetup.exe" });

        Assert.Equal(ActionStatus.Applied, folder.Status);
        Assert.Equal(ActionStatus.Applied, file.Status);
        Assert.Equal(ActionStatus.Skipped, missing.Status);
        Assert.False(Directory.Exists(_temp.File(@"Program Files (x86)\Microsoft\Edge")));
        Assert.True(Directory.Exists(_temp.File(@"Program Files (x86)\Microsoft\EdgeWebView")));
        Assert.False(File.Exists(_temp.File(@"Windows\System32\OneDriveSetup.exe")));
    }

    [Fact]
    public void A_folder_is_not_deleted_without_recurse()
    {
        Directory.CreateDirectory(_temp.File("Folder"));
        var outcome = _executor.Apply(new FileDeleteAction { Path = "Folder" });
        Assert.Equal(ActionStatus.Failed, outcome.Status);
        Assert.True(Directory.Exists(_temp.File("Folder")));
    }

    [Theory]
    [InlineData(@"..\outside.txt")]
    [InlineData(@"Windows\..\..\outside.txt")]
    [InlineData(@"C:\Windows\notepad.exe")]
    [InlineData("")]
    public void A_path_that_leaves_the_image_is_refused(string path)
    {
        var outcome = _executor.Apply(new FileDeleteAction { Path = path, Recurse = true });
        Assert.Equal(ActionStatus.Failed, outcome.Status);
    }

    [Fact]
    public void Deleting_a_folder_removes_a_junction_inside_it_without_following_it()
    {
        using var outside = new TempDirectory();
        File.WriteAllText(outside.File("precious.txt"), "keep me");
        Directory.CreateDirectory(_temp.File("Victim"));
        var link = _temp.File(@"Victim\link");
        // A junction, because creating one needs no special rights (a symbolic link does).
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe")
               {
                   ArgumentList = { "/c", "mklink", "/J", link, outside.Path },
                   UseShellExecute = false,
                   CreateNoWindow = true,
                   RedirectStandardOutput = true
               })!)
        {
            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);
        }

        Assert.True(File.Exists(Path.Combine(link, "precious.txt")));

        var outcome = _executor.Apply(new FileDeleteAction { Path = "Victim", Recurse = true });

        Assert.Equal(ActionStatus.Applied, outcome.Status);
        Assert.False(Directory.Exists(_temp.File("Victim")));
        Assert.True(File.Exists(outside.File("precious.txt")));
    }

    [Fact]
    public void Pending_update_packages_set_the_pending_flag()
    {
        _image.Updates.Add(new ComponentInfo("Package_for_KB1", ComponentState.Installed));
        _executor.NotePendingPackages();
        Assert.False(_executor.ServicingPending);

        _image.Updates.Add(new ComponentInfo("Package_for_KB2", ComponentState.Pending));
        _executor.NotePendingPackages();
        Assert.True(_executor.ServicingPending);
    }
}

public class DismExeWimToolTests
{
    [Theory]
    [InlineData("[==========                 18.0%                           ]", 18)]
    [InlineData("[==========================100.0%==========================]", 100)]
    [InlineData("[=                           2,5%                          ]", 2)]
    [InlineData("The operation completed successfully.", null)]
    [InlineData("Exporting image", null)]
    public void Progress_lines_are_recognised(string line, int? expected)
    {
        Assert.Equal(expected, DismExeWimTool.TryParseProgress(line));
    }

    [Fact]
    public async Task Export_passes_each_value_as_its_own_argument_and_never_kills_dism()
    {
        using var temp = new TempDirectory();
        var destination = temp.File(@"out dir\install.wim");
        var runner = new RecordingRunner
            { Output = ["[===  50.0%  ]"], SideEffect = () => File.WriteAllText(destination, "wim") };
        var tool = new DismExeWimTool(runner, temp.File("scratch"), temp.File("dism.log"), "dism.exe");
        var reported = new List<int>();

        await tool.ExportImageAsync(@"C:\media with space\install.wim", 6, destination,
            new InlineProgress<int>(reported.Add), TestContext.Current.CancellationToken);

        var args = runner.Request!.Arguments;
        Assert.Contains("/Export-Image", args);
        Assert.Contains(@"/SourceImageFile:C:\media with space\install.wim", args);
        Assert.Contains("/SourceIndex:6", args);
        Assert.Contains("/Compress:max", args);
        Assert.Contains("/English", args);
        Assert.False(runner.Request.KillOnCancel);
        Assert.Equal([50], reported);
    }

    [Fact]
    public async Task Export_refuses_an_existing_target_because_dism_would_append_to_it()
    {
        using var temp = new TempDirectory();
        await File.WriteAllTextAsync(temp.File("install.wim"), "old", TestContext.Current.CancellationToken);
        var tool = new DismExeWimTool(new RecordingRunner(), temp.File("scratch"), temp.File("dism.log"), "dism.exe");
        await Assert.ThrowsAsync<PlainstallException>(() => tool.ExportImageAsync("a.wim", 1, temp.File("install.wim"),
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_failing_dism_run_reports_the_exit_code_in_plain_words()
    {
        using var temp = new TempDirectory();
        var runner = new RecordingRunner
            { ExitCode = 112, Output = ["Error: 112", "There is not enough space on the disk."] };
        var tool = new DismExeWimTool(runner, temp.File("scratch"), temp.File("dism.log"), "dism.exe");

        var ex = await Assert.ThrowsAsync<ImagingException>(() => tool.SplitImageAsync("install.wim",
            temp.File("install.swm"), 3800, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(112, ex.ErrorCode);
        Assert.Contains("the disk is full", ex.Message);
        Assert.Contains("not enough space", ex.Message);
    }

    [Fact]
    public async Task Split_returns_the_parts_in_order()
    {
        using var temp = new TempDirectory();
        var firstPart = temp.File("install.swm");
        var secondPart = temp.File("install2.swm");
        var thirdPart = temp.File("install3.swm");
        var runner = new RecordingRunner
        {
            SideEffect = () =>
            {
                File.WriteAllText(firstPart, "1");
                File.WriteAllText(secondPart, "2");
                File.WriteAllText(thirdPart, "3");
            }
        };
        var tool = new DismExeWimTool(runner, temp.File("scratch"), temp.File("dism.log"), "dism.exe");

        var parts = await tool.SplitImageAsync("install.wim", temp.File("install.swm"), 3800,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["install.swm", "install2.swm", "install3.swm"], parts.Select(Path.GetFileName));
        Assert.Contains("/FileSize:3800", runner.Request!.Arguments);
    }

    private sealed class RecordingRunner : IProcessRunner
    {
        public ProcessRequest? Request { get; private set; }
        public int ExitCode { get; init; }
        public string[] Output { get; init; } = [];
        public Action? SideEffect { get; init; }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            foreach (var line in Output) request.OnLine?.Invoke(line);
            SideEffect?.Invoke();
            return Task.FromResult(new ProcessResult(ExitCode, Output));
        }
    }
}