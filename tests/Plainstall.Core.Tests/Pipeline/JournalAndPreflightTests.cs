using Plainstall.Core.Catalog;
using Plainstall.Core.Imaging;
using Plainstall.Core.Pipeline;
using Plainstall.Core.Profiles;
using Plainstall.Core.Tests.Imaging;
using Plainstall.Core.Tests.Output.Usb;

namespace Plainstall.Core.Tests.Pipeline;

internal sealed class FakeImagingService : IImagingService
{
    private readonly Dictionary<string, IMountedImage> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileStream> _openWims = new(StringComparer.OrdinalIgnoreCase);
    public List<MountedImageInfo> Mounted { get; } = [];
    public List<string> Discarded { get; } = [];
    public List<string> Committed { get; } = [];
    public Func<string, int, string, IMountedImage>? OnMount { get; set; }

    /// <summary>
    ///     When true, the WIM file of a mount stays open until the unmount, as DISM keeps it open. Then a delete
    ///     or a move of that file fails while it is mounted.
    /// </summary>
    public bool HoldsWimWhileMounted { get; init; }

    /// <summary>Servicing sessions opened and not yet closed, over all mounts.</summary>
    public int OpenSessions => _images.Values.OfType<FakeMountedImage>().Count(i => !i.Disposed);

    public IMountedImage Mount(string wimPath, int index, string mountPath, IProgress<int>? progress = null)
    {
        Mounted.Add(new MountedImageInfo(mountPath, wimPath, index));
        if (HoldsWimWhileMounted)
            _openWims[mountPath] = new FileStream(wimPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var image = OnMount?.Invoke(wimPath, index, mountPath) ?? new FakeMountedImage(mountPath);
        _images[mountPath] = image;
        return image;
    }

    /// <summary>A new session on the same mounted image: the same fake, open again.</summary>
    public IMountedImage Open(string mountPath)
    {
        var image = _images[mountPath];
        (image as FakeMountedImage)?.Reopen();
        return image;
    }

    public void UnmountAndCommit(string mountPath, IProgress<int>? progress = null)
    {
        Committed.Add(mountPath);
        Release(mountPath);
    }

    /// <summary>When true, every discard fails as DISM fails when a file in the mounted image is in use.</summary>
    public bool FailDiscard { get; set; }

    public void UnmountAndDiscard(string mountPath, IProgress<int>? progress = null)
    {
        if (FailDiscard) throw new PlainstallException(ErrorKind.Imaging, "The image could not be discarded (test).");
        Discarded.Add(mountPath);
        Release(mountPath);
    }

    public IReadOnlyList<MountedImageInfo> GetMountedImages()
    {
        return [.. Mounted];
    }

    private void Release(string mountPath)
    {
        Mounted.RemoveAll(m => m.MountPath == mountPath);
        if (!_openWims.Remove(mountPath, out var wim)) return;
        wim.Dispose();
    }
}

public class JournalTests
{
    [Fact]
    public void A_new_journal_is_clean_marks_the_folder_and_survives_a_reload()
    {
        using var temp = new TempDirectory();
        var layout = new WorkLayout(temp.Path);
        layout.Create();

        var journal = BuildJournal.Create(layout);

        Assert.True(journal.IsClean);
        Assert.True(layout.IsOurs);
        Assert.Equal(12, journal.SessionId.Length);
        Assert.Equal(journal.SessionId, BuildJournal.TryLoad(layout)!.SessionId);
        Assert.False(LeftoverCleaner.HasLeftovers(layout));
    }

    [Fact]
    public void Open_entries_are_on_disk_before_the_resource_is_taken_and_gone_after_release()
    {
        using var temp = new TempDirectory();
        var layout = new WorkLayout(temp.Path);
        layout.Create();
        var journal = BuildJournal.Create(layout);

        journal.MountStarting(layout.WorkingWim, 1, layout.InstallMount);
        journal.HiveLoading("Plainstall_x_MachineSoftware", @"C:\m\Windows\System32\config\SOFTWARE");

        var onDisk = BuildJournal.TryLoad(layout)!.State;
        Assert.Single(onDisk.Mounts);
        Assert.Single(onDisk.Hives);
        Assert.True(LeftoverCleaner.HasLeftovers(layout));

        journal.HiveUnloaded("plainstall_x_machinesoftware");
        journal.MountReleased(layout.InstallMount.ToUpperInvariant());

        Assert.True(BuildJournal.TryLoad(layout)!.IsClean);
        Assert.False(File.Exists(layout.Journal + ".tmp"));
    }

    [Fact]
    public void Cleanup_discards_only_recorded_mounts_that_are_really_mounted_then_deletes_the_work_folder()
    {
        using var temp = new TempDirectory();
        var layout = new WorkLayout(Path.Combine(temp.Path, "work"));
        layout.Create();
        var journal = BuildJournal.Create(layout);
        journal.MountStarting(layout.WorkingWim, 1, layout.InstallMount);
        journal.MountStarting(layout.BootWork, 2, layout.BootMount);

        var imaging = new FakeImagingService();
        imaging.Mounted.Add(new MountedImageInfo(layout.InstallMount, layout.WorkingWim, 1));
        // Someone else's mount: must never be touched.
        imaging.Mounted.Add(new MountedImageInfo(@"D:\other-tool\mount", @"D:\other.wim", 1));

        var report = new LeftoverCleaner(imaging).Clean(layout);

        Assert.True(report.Success, string.Join("; ", report.Problems));
        Assert.Equal([layout.InstallMount], imaging.Discarded);
        Assert.Contains(imaging.Mounted, m => m.MountPath == @"D:\other-tool\mount");
        Assert.False(Directory.Exists(layout.Root));
    }

    [Fact]
    public void Cleanup_without_dism_reports_the_mount_and_keeps_the_folder()
    {
        using var temp = new TempDirectory();
        var layout = new WorkLayout(Path.Combine(temp.Path, "work"));
        layout.Create();
        BuildJournal.Create(layout).MountStarting(layout.WorkingWim, 1, layout.InstallMount);

        var report = new LeftoverCleaner(null).Clean(layout);

        Assert.False(report.Success);
        Assert.Contains("administrator", report.Problems[0]);
        Assert.True(Directory.Exists(layout.Root));
    }

    [Fact]
    public void A_folder_without_the_marker_is_never_deleted()
    {
        using var temp = new TempDirectory();
        var layout = new WorkLayout(Path.Combine(temp.Path, "not-ours"));
        Directory.CreateDirectory(layout.Root);
        File.WriteAllText(Path.Combine(layout.Root, "holiday-photos.txt"), "x");

        var report = new LeftoverCleaner(new FakeImagingService()).Clean(layout);

        Assert.False(report.Success);
        Assert.True(File.Exists(Path.Combine(layout.Root, "holiday-photos.txt")));
    }

    [Fact]
    public void A_file_in_use_stops_the_deletion_but_the_folder_stays_recognisable()
    {
        // As seen on a real build: DISM still had logs\dism.log open when the work folder was deleted.
        using var temp = new TempDirectory();
        var layout = new WorkLayout(Path.Combine(temp.Path, "work"));
        layout.Create();
        BuildJournal.Create(layout);
        File.WriteAllText(layout.DismLog, "in use");

        CleanupReport report;
        using (new FileStream(layout.DismLog, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            report = new LeftoverCleaner(new FakeImagingService()).Clean(layout);
        }

        Assert.False(report.Success);
        Assert.True(layout.IsOurs);
        // Once the file is free, the next cleanup removes the rest.
        Assert.True(new LeftoverCleaner(new FakeImagingService()).Clean(layout).Success);
        Assert.False(Directory.Exists(layout.Root));
    }

    [Fact]
    public void A_stick_is_on_record_from_the_erase_until_it_is_finished()
    {
        using var temp = new TempDirectory();
        var layout = new WorkLayout(temp.Path);
        layout.Create();
        var journal = BuildJournal.Create(layout);
        var stick = TestDisks.Stick();

        journal.EraseStarting(stick);

        // On disk at once, and not something that blocks the next build: a half-written stick needs no cleanup.
        Assert.Contains("SanDisk Ultra", Assert.Single(LeftoverCleaner.UnfinishedSticks(layout)));
        Assert.False(LeftoverCleaner.HasLeftovers(layout));

        journal.Finished(stick);

        Assert.Empty(LeftoverCleaner.UnfinishedSticks(layout));
    }

    [Fact]
    public void Cleanup_mentions_a_stick_that_was_not_finished_and_still_succeeds()
    {
        using var temp = new TempDirectory();
        var layout = new WorkLayout(Path.Combine(temp.Path, "work"));
        layout.Create();
        BuildJournal.Create(layout).EraseStarting(TestDisks.Stick());

        var report = new LeftoverCleaner(new FakeImagingService()).Clean(layout);

        Assert.True(report.Success);
        Assert.Contains("Write it again", Assert.Single(report.Notes));
        Assert.False(Directory.Exists(layout.Root));
    }
}

public class PreflightTests
{
    private const long Needed = 30L << 30;

    private static HostEnvironment Host(bool elevated = true, string? format = "NTFS", long freeGb = 200,
        bool network = false)
    {
        return new HostEnvironment
        {
            IsElevated = elevated,
            WorkDriveFormat = format,
            WorkDriveFreeBytes = freeGb << 30,
            WorkDriveIsNetwork = network,
            WindowsDirectory = @"C:\Windows",
            ProgramFilesDirectories = [@"C:\Program Files", @"C:\Program Files (x86)"],
            UserProfileRoot = @"C:\Users"
        };
    }

    [Fact]
    public void A_sound_setup_passes()
    {
        Assert.Empty(Preflight.Check(@"C:\ProgramData\Plainstall\work", Host(), Needed));
        Assert.Empty(Preflight.Check(@"D:\work", Host(), Needed));
        Assert.Empty(Preflight.Check(@"C:\Users\anna\plainstall-work", Host(), Needed));
    }

    [Fact]
    public void Missing_admin_rights_are_reported_first_and_plainly()
    {
        var problems = Preflight.Check(@"D:\work", Host(false), Needed);
        Assert.Contains("Run as administrator", problems[0]);
    }

    [Fact]
    public void An_arm_pc_is_refused()
    {
        var problems = Preflight.Check(@"D:\work", Host() with { IsArmPc = true }, Needed);
        Assert.Contains("x64 PCs (Intel and AMD) only", Assert.Single(problems));
    }

    [Theory]
    [InlineData("exFAT")]
    [InlineData("FAT32")]
    [InlineData("ReFS")]
    [InlineData(null)]
    public void Only_ntfs_is_accepted_for_the_work_folder(string? format)
    {
        var problems = Preflight.Check(@"E:\work", Host(format: format), Needed);
        Assert.Contains(problems, p => p.Contains("NTFS"));
    }

    [Fact]
    public void A_network_location_is_refused()
    {
        Assert.Contains(Preflight.Check(@"\\server\share\work", Host(network: true, format: null), Needed),
            p => p.Contains("network"));
    }

    [Theory]
    [InlineData(@"D:\")]
    [InlineData(@"C:\Windows\Temp\work")]
    [InlineData(@"C:\Program Files\Plainstall\work")]
    [InlineData(@"C:\Program Files (x86)\x")]
    [InlineData(@"C:\Users")]
    [InlineData(@"C:\Users\anna")]
    public void Dangerous_work_folders_are_refused(string path)
    {
        Assert.NotEmpty(Preflight.Check(path, Host(), Needed));
    }

    [Fact]
    public void Too_little_space_is_reported_with_both_numbers()
    {
        var problem = Assert.Single(Preflight.Check(@"D:\work", Host(freeGb: 12), Needed));
        Assert.Contains("30", problem);
        Assert.Contains("12", problem);
    }

    [Fact]
    public void Required_space_is_twice_the_media_plus_updates_and_a_reserve()
    {
        Assert.Equal((2 * 9L + 4) << 30, Preflight.RequiredBytes(9L << 30));
        Assert.Equal((2 * 9L + 3 + 4) << 30, Preflight.RequiredBytes(9L << 30, 1L << 30));
    }

    [Fact]
    public void Problems_become_one_preflight_error()
    {
        var ex = Assert.Throws<PlainstallException>(() => Preflight.ThrowIfAny(["a", "b"]));
        Assert.Equal(ErrorKind.Preflight, ex.Kind);
        Preflight.ThrowIfAny([]);
    }

    [Fact]
    public void A_download_needs_room_on_the_work_drive_only_when_it_lands_there()
    {
        Assert.Equal(0, Preflight.DownloadBytes(@"D:\work", null));
        Assert.Equal(0, Preflight.DownloadBytes(@"D:\work", @"C:\ProgramData\Plainstall\cache"));

        using var temp = new TempDirectory();
        var downloads = temp.File("cache");
        Assert.Equal(Preflight.AssumedMediaBytes, Preflight.DownloadBytes(temp.File("work"), downloads));
        Directory.CreateDirectory(downloads);
        File.WriteAllText(Path.Combine(downloads, "Win11_English_x64.iso.part"), "x");
        Assert.Equal(Preflight.AssumedMediaBytes, Preflight.DownloadBytes(temp.File("work"), downloads));
        File.WriteAllText(Path.Combine(downloads, "Win11_English_x64.iso"), "x");
        Assert.Equal(0, Preflight.DownloadBytes(temp.File("work"), downloads));
    }
}

public class BuildReportTests
{
    [Fact]
    public void A_report_round_trips_and_never_contains_a_password()
    {
        var catalog = CatalogLoader.LoadEmbedded();
        var profile = ProfileFactory.FromPreset(catalog.DefaultPreset) with { };
        var plan = BuildPlanner.Create(profile, catalog, new ImageFacts(26300, "Professional"));
        var log = new BuildLog();
        var (first, second) = (plan.Actions[0], plan.Actions[1]);
        log.Record(first.EntryId, first.Action.KindName, first.Action.Target, ActionOutcome.Applied);
        log.Record(second.EntryId, second.Action.KindName, second.Action.Target,
            ActionOutcome.Skipped("not present in this image"));
        log.Warn("Cleanup was skipped.");

        var report = new BuildReport
        {
            Plainstall = "0.1.0",
            CatalogVersion = catalog.CatalogVersion,
            Started = DateTimeOffset.UnixEpoch,
            Finished = DateTimeOffset.UnixEpoch.AddMinutes(30),
            Result = "succeeded",
            Profile = profile,
            Actions = log.Actions,
            Warnings = log.Warnings,
            Source = new ReportSource
                { Kind = "iso", File = "win.iso", Edition = "Professional", Build = "26300.9457", Arch = "x64" }
        };

        var json = report.ToJson();
        var again = BuildReport.FromJson(json);

        Assert.Equal(2, again.Actions.Count);
        Assert.Equal("applied", again.Actions[0].Result);
        Assert.Equal("not present in this image", again.Actions[1].Reason);
        Assert.Equal(profile.Name, again.Profile.Name);
        Assert.DoesNotContain("\"password\": \"", json);
        Assert.Equal(0, log.FailedCount);
    }
}