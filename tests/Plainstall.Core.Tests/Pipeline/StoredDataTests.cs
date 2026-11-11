using Plainstall.Core.Catalog;
using Plainstall.Core.Imaging;
using Plainstall.Core.Pipeline;
using Plainstall.Core.Profiles;

namespace Plainstall.Core.Tests.Pipeline;

// All tests that take the build lock are in this class, so they do not run at the same time.
public class StoredDataTests
{
    [Fact]
    public void The_build_lock_can_be_taken_only_once_at_a_time()
    {
        using (var first = BuildLock.TryTake())
        {
            Assert.NotNull(first);
            Assert.Null(BuildLock.TryTake());
            Assert.Equal(BuildLock.Busy, Assert.Throws<PlainstallException>(BuildLock.Take).Message);
        }

        using var again = BuildLock.TryTake();
        Assert.NotNull(again);
    }

    [Fact]
    public void Delete_removes_the_plainstall_folders_and_keeps_other_folders()
    {
        using var temp = new TempDirectory();
        var paths = Seed(temp);
        var other = temp.WriteFile(@"data\other\keep.txt");

        var report = StoredData.Delete(paths, new FakeImagingService());

        Assert.True(report.Success, string.Join("; ", report.Problems));
        Assert.All(StoredData.Folders(paths), folder => Assert.False(Directory.Exists(folder), folder));
        Assert.True(File.Exists(other));
        Assert.StartsWith("Deleted 1 KB of Plainstall files", report.Done[^1]);
        Assert.Equal(0, StoredData.Size(paths));
    }

    [Fact]
    public void Delete_removes_the_data_folder_when_it_is_empty_after_the_delete()
    {
        using var temp = new TempDirectory();
        var paths = Seed(temp);

        Assert.True(StoredData.Delete(paths, new FakeImagingService()).Success);

        Assert.False(Directory.Exists(paths.DataRoot));
    }

    [Fact]
    public void The_report_of_a_build_without_an_iso_is_counted_and_deleted()
    {
        using var temp = new TempDirectory();
        var paths = Seed(temp);
        var before = StoredData.Size(paths);
        var reportFile = temp.WriteFile(@"data\build-report.json", new byte[50]);
        // The pipeline writes the report of a build without an ISO to this file.
        var request = new BuildRequest
        {
            Profile = ProfileFactory.FromPreset(CatalogLoader.LoadEmbedded().DefaultPreset),
            WorkDirectory = paths.DefaultWork,
            AppVersion = "1.0.0"
        };
        Assert.Equal(BuildPipeline.ReportPathFor(request, new WorkLayout(paths.DefaultWork)), reportFile);
        Assert.Equal(reportFile, StoredData.ReportFile(paths));

        Assert.Equal(before + 50, StoredData.Size(paths));
        var report = StoredData.Delete(paths, new FakeImagingService());

        Assert.True(report.Success, string.Join("; ", report.Problems));
        Assert.False(File.Exists(reportFile));
        Assert.False(Directory.Exists(paths.DataRoot));
    }

    [Fact]
    public void Delete_deletes_nothing_while_a_build_runs()
    {
        using var temp = new TempDirectory();
        var paths = Seed(temp);
        var before = StoredData.Size(paths);
        var imaging = new FakeImagingService();

        CleanupReport report;
        using (BuildLock.Take()) report = StoredData.Delete(paths, imaging);

        Assert.Equal([BuildLock.Busy], report.Problems);
        Assert.Equal(before, StoredData.Size(paths));
        Assert.True(new WorkLayout(paths.DefaultWork).IsOurs);
    }

    [Fact]
    public void Delete_deletes_nothing_without_dism()
    {
        using var temp = new TempDirectory();
        var paths = Seed(temp);
        var before = StoredData.Size(paths);

        var report = StoredData.Delete(paths, null);

        Assert.Contains("administrator", Assert.Single(report.Problems));
        Assert.Equal(before, StoredData.Size(paths));
    }

    [Fact]
    public void Delete_deletes_nothing_while_an_image_is_mounted_below_the_data_folder()
    {
        using var temp = new TempDirectory();
        var paths = Seed(temp);
        var mount = Path.Combine(paths.Cache, "mount");
        var imaging = new FakeImagingService();
        imaging.Mounted.Add(new MountedImageInfo(mount, @"D:\other.wim", 1));

        var report = StoredData.Delete(paths, imaging);

        Assert.Contains(mount, Assert.Single(report.Problems));
        Assert.True(File.Exists(Path.Combine(paths.Cache, "windows.iso")));
        Assert.Empty(imaging.Discarded);
    }

    [Fact]
    public void Cleaning_leftovers_does_nothing_while_a_build_runs()
    {
        using var temp = new TempDirectory();
        var paths = Seed(temp);

        CleanupReport report;
        using (BuildLock.Take()) report = StoredData.CleanLeftovers(paths.DefaultWork, new FakeImagingService());

        Assert.Equal([BuildLock.Busy], report.Problems);
        Assert.True(Directory.Exists(paths.DefaultWork));
    }

    /// <summary>A data folder with a file in each Plainstall folder and a work folder with the marker.</summary>
    private static PlainstallPaths Seed(TempDirectory temp)
    {
        var paths = new PlainstallPaths(temp.Combine("data"));
        temp.WriteFile(@"data\cache\windows.iso", new byte[400]);
        temp.WriteFile(@"data\tools\oscdimg.exe", new byte[300]);
        temp.WriteFile(@"data\logs\plainstall.log", new byte[200]);
        temp.WriteFile(@"data\scratch\dism.tmp", new byte[100]);
        var work = new WorkLayout(paths.DefaultWork);
        work.Create();
        BuildJournal.Create(work);
        return paths;
    }
}