using System.Runtime.InteropServices;
using Plainstall.Core.Media;
using Plainstall.Core.Output;
using Plainstall.Core.Tests.Media;

namespace Plainstall.Core.Tests.Output;

/// <summary>
///     Opt-in tests that talk to Microsoft's symbol server and run the real oscdimg.exe. They are
///     skipped unless <c>PLAINSTALL_TEST_NETWORK=1</c> is set.
/// </summary>
public partial class RealOscdimgTests
{
    public const string NetworkVariable = "PLAINSTALL_TEST_NETWORK";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static void RequireNetwork()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(NetworkVariable) == "1",
            $"Set {NetworkVariable}=1 to run tests that download from Microsoft.");
    }

    /// <summary>A locator that can only download: its ADK folder is empty, so an installed ADK does not change the test.</summary>
    private static OscdimgLocator DownloadOnlyLocator(TempDirectory folder)
    {
        return new OscdimgLocator(new OscdimgLocatorOptions
        {
            ToolsFolder = folder.Combine("tools"),
            AdkDeploymentToolsFolder = folder.Combine("no adk here")
        });
    }

    [Fact]
    public async Task Real_download_delivers_the_pinned_file()
    {
        RequireNetwork();
        using var folder = new TempDirectory();
        var locator = DownloadOnlyLocator(folder);

        var tool = await locator.LocateAsync(Token);

        Assert.Equal(OscdimgLocator.PinnedSha256, await HashHex.Sha256OfFileAsync(tool, null, Token));
        Assert.Equal(143_360, new FileInfo(tool).Length);

        // Why the hash is pinned: this file carries no signature, so only the pinned hash can vouch for it.
        var signature = new MicrosoftSignatureVerifier().Verify(tool);
        Assert.False(signature.IsTrusted, signature.Detail);
        TestContext.Current.TestOutputHelper?.WriteLine($"Signature check of the downloaded file: {signature.Detail}");

        // The second call finds the cached copy.
        Assert.Equal(tool, await locator.LocateAsync(Token));
    }

    /// <summary>
    ///     The whole ISO path with the real tool: boot files from a real Windows ISO, a media folder and
    ///     an output path that both contain spaces and a '#', then oscdimg, verification, and a mount of the result.
    ///     Needs <c>PLAINSTALL_TEST_ISO</c> as well.
    /// </summary>
    [Fact]
    public async Task Real_oscdimg_builds_a_bootable_iso_that_windows_can_mount()
    {
        RequireNetwork();
        var sourceIso = IsoMountTests.RequireTestIso();
        var output = TestContext.Current.TestOutputHelper;
        using var folder = new TempDirectory();
        var tool = await DownloadOnlyLocator(folder).LocateAsync(Token);

        // A small media folder: the real boot files, and stand-ins for everything large.
        var mediaRoot = folder.Combine("work folder #1", "media");
        await using (var source = await IsoMount.AttachAsync(sourceIso, cancellationToken: Token))
        {
            foreach (var relative in new[]
                     {
                         MediaInventory.BiosBootLoaderPath, MediaInventory.EfiBootImagePath, @"boot\bootfix.bin",
                         "bootmgr", "bootmgr.efi"
                     })
            {
                var from = Path.Combine(source.RootPath, relative);
                if (!File.Exists(from)) continue;

                var to = Path.Combine(mediaRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(from, to);
                // Files copied from a disc image arrive read-only, which would keep the temp folder from being deleted.
                File.SetAttributes(to, FileAttributes.Normal);
            }
        }

        var marker = new byte[300_000];
        Random.Shared.NextBytes(marker);
        folder.WriteFile(@"work folder #1\media\sources\marker file.bin", marker);
        folder.WriteFile(@"work folder #1\media\sources\copy of marker.bin", marker);

        var lines = new List<string>();
        var percents = new List<int>();
        var runner = new RecordingRunner(lines);
        var result = await new IsoWriter(runner).WriteAsync(
            new IsoWriteRequest
            {
                MediaRoot = mediaRoot,
                OutputPath = folder.Combine("out put #2", "plainstall test.iso"),
                OscdimgPath = tool,
                VolumeLabel = "Plainstall test"
            },
            new Progress<int>(percents.Add),
            Token);

        foreach (var line in lines) output?.WriteLine("oscdimg: " + line);
        output?.WriteLine($"Result: {result.Path}, {result.SizeBytes:N0} bytes, SHA-256 {result.Sha256}");

        Assert.True(File.Exists(result.Path));
        Assert.False(File.Exists(result.Path + IsoWriter.PartialSuffix));
        Assert.Equal(new FileInfo(result.Path).Length, result.SizeBytes);
        Assert.Equal(0, result.SizeBytes % IsoWriter.SectorSize);
        Assert.Equal(await HashHex.Sha256OfFileAsync(result.Path, null, Token), result.Sha256);

        // The proof that the arguments were understood: Windows mounts the result and the files are in it.
        await using var mounted = await IsoMount.AttachAsync(result.Path, cancellationToken: Token);
        output?.WriteLine($"Mounted the result at {mounted.RootPath} ({mounted.PathKind})");
        Assert.Equal(marker,
            await File.ReadAllBytesAsync(Path.Combine(mounted.RootPath, @"sources\marker file.bin"), Token));
        Assert.True(File.Exists(Path.Combine(mounted.RootPath, MediaInventory.EfiBootImagePath)));
        Assert.True(File.Exists(Path.Combine(mounted.RootPath, MediaInventory.BiosBootLoaderPath)));
        Assert.Equal("PLAINSTALL_TEST", VolumeLabel(mounted.RootPath));
    }

    /// <summary>Reads a volume label through the volume path, which <see cref="DriveInfo" /> cannot do.</summary>
    private static string VolumeLabel(string root)
    {
        var buffer = new char[261];
        return GetVolumeInformation(root, buffer, (uint)buffer.Length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
            IntPtr.Zero, 0)
            ? new string(buffer, 0, Array.IndexOf(buffer, '\0'))
            : "";
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumeInformation(string root, [Out] char[] name, uint nameSize, IntPtr serial,
        IntPtr maxComponent, IntPtr flags, IntPtr fileSystem, uint fileSystemSize);

    /// <summary>Runs the real process and keeps what it printed.</summary>
    private sealed class RecordingRunner(List<string> lines) : IProcessRunner
    {
        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            var result = await new ProcessRunner().RunAsync(request, cancellationToken);
            lines.Add($"exit code {result.ExitCode}; arguments: {string.Join(" | ", request.Arguments)}");
            lines.AddRange(result.Lines);
            return result;
        }
    }
}