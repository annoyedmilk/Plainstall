using System.Globalization;
using System.Text.Json;
using Plainstall.App.Services;
using Plainstall.Core;
using Plainstall.Core.Download;
using Plainstall.Core.Media;
using Plainstall.Core.Output.Usb;
using Plainstall.Core.Pipeline;
using Plainstall.Core.Profiles;
using Plainstall.Core.Release;

namespace Plainstall.App.Rendering;

// Sample data for the page renderer and the app tests: no network, no disk, no administrator rights. Everything here
// is made up and only has to look like what the real services return.

internal sealed class SampleHostInfo(bool elevated) : IHostInfo
{
    public bool IsElevated { get; } = elevated;
    public CultureInfo DisplayCulture { get; } = CultureInfo.GetCultureInfo("en-US");
    public Version AppVersion { get; } = new(0, 1, 0);
}

internal sealed class SampleDownloadClient : IMicrosoftDownloadClient
{
    public static readonly IReadOnlyList<WindowsLanguage> Languages =
    [
        new("1", "Arabic", "Arabic"), new("2", "Brazilian Portuguese", "Brazilian Portuguese"),
        new("3", "Chinese (Simplified)", "Chinese (Simplified)"),
        new("4", "Czech", "Czech"), new("5", "Danish", "Danish"), new("6", "Dutch", "Dutch"),
        new("7", "English", "English (United States)"),
        new("8", "English (United Kingdom)", "English International"), new("9", "French", "French"),
        new("10", "French Canadian", "French Canadian"),
        new("11", "German", "German"), new("12", "Italian", "Italian"), new("13", "Japanese", "Japanese"),
        new("14", "Polish", "Polish"),
        new("15", "Spanish", "Spanish"), new("16", "Swedish", "Swedish"), new("17", "Turkish", "Turkish"),
        new("18", "Ukrainian", "Ukrainian")
    ];

    public bool Fail { get; set; }

    public Task<WindowsRelease> GetReleaseAsync(CancellationToken cancellationToken)
    {
        if (Fail)
            throw new PlainstallException(ErrorKind.Network,
                "Microsoft's download service could not be reached. Check the internet connection and try again.");
        return Task.FromResult(new WindowsRelease("3113", Languages, PublishedHashTable.Empty)
        {
            ProductDisplayName = "Windows 11 Client - Build 26300.9457",
            Build = "26300.9457"
        });
    }

    public Task<DownloadTicket> GetDownloadTicketAsync(WindowsRelease release, WindowsLanguage language,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException("The sample client does not download.");
    }
}

internal sealed class SampleMediaReader : IMediaReader
{
    public string? FailWith { get; set; }

    public Task<MediaInfo> ReadAsync(string isoPath, CancellationToken cancellationToken)
    {
        return FailWith is not null
            ? throw new PlainstallException(ErrorKind.InvalidInput, FailWith)
            : Task.FromResult(SampleMedia.Create());
    }
}

internal static class SampleMedia
{
    public static MediaInfo Create()
    {
        var version = new Version(10, 0, 26300, 9457);
        var editions = new[]
        {
            Edition(1, "Core", "Windows 11 Home"), Edition(2, "CoreN", "Windows 11 Home N"),
            Edition(3, "CoreSingleLanguage", "Windows 11 Home Single Language"),
            Edition(4, "Education", "Windows 11 Education"), Edition(5, "Professional", "Windows 11 Pro"),
            Edition(6, "ProfessionalN", "Windows 11 Pro N"),
            Edition(7, "ProfessionalEducation", "Windows 11 Pro Education"),
            Edition(8, "ProfessionalWorkstation", "Windows 11 Pro for Workstations")
        };
        var setup = new WimImageInfo
            { Index = 2, Name = "Microsoft Windows Setup (amd64)", Version = version, ArchitectureCode = 9 };
        return new MediaInfo
        {
            InstallImage = new WimInfo
            {
                Path = @"E:\sources\install.wim", FileSize = 6_400_000_000, ImageCount = editions.Length,
                Images = editions
            },
            BootImage = new WimInfo { Path = @"E:\sources\boot.wim", ImageCount = 2, BootIndex = 2, Images = [setup] },
            Language = "en-US",
            Version = version,
            SetupImage = setup,
            HasBiosBootLoader = true,
            TotalBytes = 7_300_000_000
        };

        WimImageInfo Edition(int index, string id, string name)
        {
            return new WimImageInfo
            {
                Index = index, EditionId = id, Name = name, Version = version, ArchitectureCode = 9,
                InstallationType = "Client", DefaultLanguage = "en-US", Languages = ["en-US"]
            };
        }
    }
}

internal sealed class SampleLeftovers : ILeftoverService
{
    public bool Leftovers { get; set; }

    public bool HasLeftovers(string workFolder)
    {
        return Leftovers;
    }

    /// <summary>The size of the stored files, in bytes. 0 hides the row on the Welcome page.</summary>
    public long StoredBytes { get; set; }

    /// <summary>When set, deleting the stored files fails with this problem.</summary>
    public string? DeleteProblem { get; set; }

    /// <summary>The notes that each cleanup reports, such as a USB stick that was not finished.</summary>
    public IReadOnlyList<string> Notes { get; set; } = [];

    /// <summary>When set, each cleanup and each deletion waits for this task, so a test can look at the page meanwhile.</summary>
    public Task? Hold { get; set; }

    public async Task<CleanupReport> CleanAsync(string workFolder)
    {
        if (Hold is not null) await Hold;
        Leftovers = false;
        return new CleanupReport(["Released the registry file SOFTWARE."], []) { Notes = Notes };
    }

    public long StoredDataBytes()
    {
        return StoredBytes;
    }

    public async Task<CleanupReport> DeleteStoredDataAsync()
    {
        if (Hold is not null) await Hold;
        if (DeleteProblem is not null) return new CleanupReport([], [DeleteProblem]) { Notes = Notes };
        var deleted = $"Deleted {UsbSizes.Format(StoredBytes)} of Plainstall files.";
        StoredBytes = 0;
        return new CleanupReport([deleted], []) { Notes = Notes };
    }
}

internal sealed class SamplePreflight : IPreflightService
{
    public bool LowSpace { get; set; }

    /// <summary>The space that the last check asked for.</summary>
    public long? CheckedBytes { get; private set; }

    public SpaceCheck Check(string workFolder, long requiredBytes)
    {
        CheckedBytes = requiredBytes;
        return LowSpace
            ? new SpaceCheck(requiredBytes, 8L << 30,
            [
                $"Not enough free space on C:\\: about {requiredBytes >> 30} GB are needed, 8 GB are free. Free up space or choose a work folder on another drive."
            ])
            : new SpaceCheck(requiredBytes, 212L << 30, []);
    }
}

internal sealed class SampleLocales : ILocaleCatalog
{
    public IReadOnlyList<LocaleOption> Regions { get; } =
    [
        new("de-CH", "German (Switzerland) (de-CH)"), new("de-DE", "German (Germany) (de-DE)"),
        new("en-GB", "English (United Kingdom) (en-GB)"),
        new("en-US", "English (United States) (en-US)"), new("fr-CH", "French (Switzerland) (fr-CH)"),
        new("fr-FR", "French (France) (fr-FR)")
    ];

    public IReadOnlyList<LocaleOption> Keyboards => Regions;

    public IReadOnlyList<TimeZoneOption> TimeZones { get; } =
    [
        new("GMT Standard Time", "(UTC+00:00) Dublin, Edinburgh, Lisbon, London"),
        new("W. Europe Standard Time", "(UTC+01:00) Amsterdam, Berlin, Bern, Rome, Stockholm, Vienna"),
        new("Pacific Standard Time", "(UTC-08:00) Pacific Time (US & Canada)")
    ];
}

/// <summary>Settings kept in memory. Records what was saved, so tests can check that no password got in.</summary>
internal sealed class MemorySettingsStore : ISettingsStore
{
    private readonly ProfileSerializer _serializer = new();
    private string? _lastProfileJson;

    public AppSettings Settings { get; private set; } = new();
    public List<string> Written { get; } = [];

    public AppSettings Load()
    {
        return Settings;
    }

    public void Save(AppSettings settings)
    {
        Settings = settings;
        Written.Add(JsonSerializer.Serialize(settings));
    }

    public Profile? LoadLastProfile()
    {
        return _lastProfileJson is null ? null : _serializer.Parse(_lastProfileJson).Profile;
    }

    public void SaveLastProfile(Profile profile)
    {
        _lastProfileJson = _serializer.ToJson(profile);
        Written.Add(_lastProfileJson);
    }
}

internal sealed class SampleDialogs : IDialogService
{
    public string? IsoToOpen { get; set; } = @"C:\Users\Sample\Downloads\Win11_25H2_English_x64.iso";
    public string? ProfileToSave { get; set; }
    public string? ProfileToOpen { get; set; }
    public SaveFileChoice? IsoToSave { get; set; } = new(@"D:\Plainstall\Plainstall-Recommended.iso", false);
    public string? Folder { get; set; } = @"D:\Drivers\Laptop";
    public IReadOnlyList<string> UpdateFiles { get; set; } = [@"D:\Updates\windows11.0-kb5070001-x64.msu"];

    public string? PickProfileToOpen(string? initialFolder)
    {
        return ProfileToOpen;
    }

    public string? PickProfileSaveLocation(string suggestedFileName, string? initialFolder)
    {
        return ProfileToSave;
    }

    public string? PickIsoToOpen(string? initialPath)
    {
        return IsoToOpen;
    }

    public SaveFileChoice? PickIsoSaveLocation(string suggestedFileName)
    {
        return IsoToSave;
    }

    public string? PickFolder(string? initialFolder)
    {
        return Folder;
    }

    public IReadOnlyList<string> PickUpdateFiles()
    {
        return UpdateFiles;
    }
}

internal sealed class SampleShell : IShellService
{
    public List<string> Opened { get; } = [];

    public void Open(string path)
    {
        Opened.Add(path);
    }

    public void OpenWebPage(Uri uri)
    {
        Opened.Add(uri.AbsoluteUri);
    }
}

internal sealed class SampleUsbDisks : IUsbDiskService
{
    public static IReadOnlyList<UsbCandidate> Candidates { get; } =
    [
        new(new DiskFacts
        {
            Number = 2, FriendlyName = "SanDisk Ultra", SerialNumber = "4C530001", UniqueId = "usb-sandisk",
            SizeBytes = 32_015_679_488, BusType = "USB",
            IsRemovableMedia = true, IsSystem = false, IsBoot = false,
            Volumes =
            [
                new VolumeFacts
                    { DriveLetter = "E:", Label = "HOLIDAY", FileSystem = "FAT32", SizeBytes = 32_000_000_000 }
            ]
        }, true, []),
        new(new DiskFacts
        {
            Number = 3, FriendlyName = "Kingston DataTraveler", SerialNumber = "60A44C42", UniqueId = "usb-kingston",
            SizeBytes = 15_502_147_584, BusType = "USB",
            IsRemovableMedia = true, IsSystem = false, IsBoot = false,
            Volumes =
            [
                new VolumeFacts { DriveLetter = "F:", Label = "", FileSystem = "exFAT", SizeBytes = 15_500_000_000 }
            ]
        }, true, []),
        new(new DiskFacts
        {
            Number = 4, FriendlyName = "General USB Flash Drive", SerialNumber = "0374000004B1",
            UniqueId = "usb-general", SizeBytes = 128_412_909_568, BusType = "USB",
            IsRemovableMedia = false, IsSystem = false, IsBoot = false,
            Volumes =
            [
                new VolumeFacts
                    { DriveLetter = "H:", Label = "USB", FileSystem = "exFAT", SizeBytes = 128_400_000_000 }
            ]
        }, true, []),
        // The reason comes from the engine's own rules, with the context that the Review page uses.
        UsbDiskRules.Evaluate(new DiskFacts
        {
            Number = 1, FriendlyName = "WD Elements Desktop", SerialNumber = "WX12", UniqueId = "usb-wd",
            SizeBytes = 4_000_787_030_016, BusType = "USB",
            IsRemovableMedia = false, IsSystem = false, IsBoot = false,
            Volumes =
            [
                new VolumeFacts
                    { DriveLetter = "G:", Label = "Backup", FileSystem = "NTFS", SizeBytes = 4_000_000_000_000 }
            ]
        }, new UsbSelectionContext { AllowExternalDrives = true })
    ];

    /// <summary>The context of the last list, so a test can see which places the picker protects.</summary>
    public UsbSelectionContext? LastContext { get; private set; }

    public Task<IReadOnlyList<UsbCandidate>> ListAsync(UsbSelectionContext context,
        CancellationToken cancellationToken = default)
    {
        LastContext = context;
        return Task.FromResult(Candidates);
    }

    public Task<DiskFacts?> GetAsync(int diskNumber, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Candidates.FirstOrDefault(c => c.Disk.Number == diskNumber)?.Disk);
    }
}

internal sealed class SampleUpdateChecker : IUpdateChecker
{
    public Task<UpdateInfo?> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<UpdateInfo?>(new UpdateInfo(new Version(0, 2, 0),
            new Uri("https://example.invalid/plainstall/releases")));
    }
}

/// <summary>A build that reports a few stages with log lines and then waits, so the Build page can be captured mid-run.</summary>
internal sealed class SampleBuildRunner : IBuildRunner
{
    public async Task<BuildOutcome> RunAsync(BuildStart start, IProgress<BuildProgress> progress, IProgress<string> log,
        CancellationToken cancellationToken)
    {
        string[] lines =
        [
            "14:02:11  == Step 1 of 10: Check this PC",
            "14:02:11  Free space on C:\\: 212 GB",
            "14:02:12  == Step 2 of 10: Get the Windows image",
            "14:02:12  The cached ISO is the one Microsoft publishes today. Nothing to download.",
            "14:02:40  == Step 3 of 10: Look at the image",
            "14:02:40  Image: Windows 11 Pro (Professional), build 10.0.26300.9457, x64, en-US",
            "14:02:41  == Step 4 of 10: Copy the installation files",
            "14:06:02  == Step 5 of 10: Customize Windows",
            "14:06:30  Applied appx.removeProvisioned Clipchamp.Clipchamp",
            "14:06:31  Applied appx.removeProvisioned Microsoft.BingNews",
            "14:06:33  Applied appx.removeProvisioned Microsoft.MicrosoftSolitaireCollection"
        ];
        for (var stage = 1; stage <= 5; stage++)
            progress.Report(new BuildProgress(stage, BuildPipeline.StageNames.Count,
                BuildPipeline.StageNames[stage - 1]));
        foreach (var line in lines) log.Report(line);
        progress.Report(new BuildProgress(5, BuildPipeline.StageNames.Count, BuildPipeline.StageNames[4],
            "Removing Microsoft Solitaire Collection", 42));
        await Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(_ => { }, CancellationToken.None,
            TaskContinuationOptions.None, TaskScheduler.Default);
        // Like the engine, finish the current step before stopping, so the page shows "Stopping" for a moment.
        await Task.Delay(1500, CancellationToken.None);
        return SampleOutcomes.Cancelled(start.Request.Profile);
    }
}

internal static class SampleOutcomes
{
    public static BuildOutcome Succeeded(Profile profile)
    {
        return new BuildOutcome(
            new BuildReport
            {
                Plainstall = "0.1.0", CatalogVersion = 1, Started = DateTimeOffset.Now.AddMinutes(-31),
                Finished = DateTimeOffset.Now, Result = "succeeded", Profile = profile,
                Warnings =
                [
                    "Component cleanup was skipped, because some changes only complete when Windows first starts. The image is slightly larger. Nothing is wrong with it."
                ]
            },
            @"D:\Plainstall\build-report.json", @"D:\Plainstall\Plainstall-Recommended.iso",
            @"C:\ProgramData\Plainstall\logs\plainstall-20261110-140211.log");
    }

    public static BuildOutcome Failed(Profile profile)
    {
        return new BuildOutcome(
            new BuildReport
            {
                Plainstall = "0.1.0", CatalogVersion = 1, Started = DateTimeOffset.Now.AddMinutes(-4),
                Finished = DateTimeOffset.Now, Result = "failed", Profile = profile,
                Error =
                    "Not enough free space on C:\\: about 22.6 GB are needed, 8 GB are free. Free up space or choose a work folder on another drive."
            },
            @"C:\ProgramData\Plainstall\build-report.json", null,
            @"C:\ProgramData\Plainstall\logs\plainstall-20261110-140211.log");
    }

    public static BuildOutcome Cancelled(Profile profile)
    {
        return new BuildOutcome(
            new BuildReport
            {
                Plainstall = "0.1.0", CatalogVersion = 1, Started = DateTimeOffset.Now, Result = "cancelled",
                Error = "The build was cancelled.", Profile = profile
            },
            null, null, null);
    }
}