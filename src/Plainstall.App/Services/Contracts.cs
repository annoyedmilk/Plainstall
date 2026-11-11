using System.Globalization;
using Plainstall.Core.Media;
using Plainstall.Core.Pipeline;
using Plainstall.Core.Profiles;

namespace Plainstall.App.Services;

// The seams between the view models and the PC. View models see only these interfaces, so they can be
// tested without WPF, a network, a disk or administrator rights. The WPF and engine implementations are
// next to this file. The fakes for the tests and the page renderer are in Rendering\SampleServices.cs.

public enum ThemeChoice
{
    System,
    Light,
    Dark
}

/// <summary>Small settings that belong to this user and this PC. Never a password.</summary>
public sealed record AppSettings
{
    public ThemeChoice Theme { get; init; } = ThemeChoice.System;

    /// <summary>Null: the default work folder under ProgramData.</summary>
    public string? WorkFolder { get; init; }

    public string? LastIsoPath { get; init; }
    public string? LastProfileFolder { get; init; }
}

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);

    /// <summary>The profile of the last build, or null when there is none or it cannot be read.</summary>
    Profile? LoadLastProfile();

    void SaveLastProfile(Profile profile);
}

/// <summary>The answer of a save dialog for the ISO file.</summary>
/// <param name="ReplaceExisting">True when the file exists and the user agreed to replace it.</param>
public sealed record SaveFileChoice(string Path, bool ReplaceExisting);

public interface IDialogService
{
    string? PickProfileToOpen(string? initialFolder);
    string? PickProfileSaveLocation(string suggestedFileName, string? initialFolder);
    string? PickIsoToOpen(string? initialPath);
    SaveFileChoice? PickIsoSaveLocation(string suggestedFileName);
    string? PickFolder(string? initialFolder);
    IReadOnlyList<string> PickUpdateFiles();
}

public interface IShellService
{
    /// <summary>Opens a file or a folder with the program that Windows uses for it.</summary>
    void Open(string path);

    void OpenWebPage(Uri uri);
}

public interface IThemeSwitcher
{
    void Apply(ThemeChoice choice);
}

/// <summary>Runs work on the UI thread. Tests run it at once.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

public interface IHostInfo
{
    bool IsElevated { get; }

    /// <summary>The language that Windows shows for this user. It decides the preselected download language.</summary>
    CultureInfo DisplayCulture { get; }

    Version AppVersion { get; }
}

/// <summary>Opens the user's own ISO and reads what is on it. Nothing is changed.</summary>
public interface IMediaReader
{
    Task<MediaInfo> ReadAsync(string isoPath, CancellationToken cancellationToken);
}

/// <summary>What an interrupted build left behind in the work folder, and the files that Plainstall keeps on this PC.</summary>
public interface ILeftoverService
{
    bool HasLeftovers(string workFolder);
    Task<CleanupReport> CleanAsync(string workFolder);

    /// <summary>The size of the files that Plainstall keeps on this PC, in bytes. See <see cref="StoredData" />.</summary>
    long StoredDataBytes();

    Task<CleanupReport> DeleteStoredDataAsync();
}

/// <summary>Free space and the other rules a work folder must meet.</summary>
/// <param name="FreeBytes">Null when the drive cannot be read.</param>
public sealed record SpaceCheck(long RequiredBytes, long? FreeBytes, IReadOnlyList<string> Problems);

public interface IPreflightService
{
    SpaceCheck Check(string workFolder, long requiredBytes);
}

public sealed record LocaleOption(string Tag, string DisplayName);

public sealed record TimeZoneOption(string Id, string DisplayName);

/// <summary>Regions, keyboards and time zones the user can pick on the Setup tab.</summary>
public interface ILocaleCatalog
{
    IReadOnlyList<LocaleOption> Regions { get; }
    IReadOnlyList<LocaleOption> Keyboards { get; }
    IReadOnlyList<TimeZoneOption> TimeZones { get; }
}

/// <summary>One build to run: the engine's request and the user's ISO, if any.</summary>
public sealed record BuildStart(BuildRequest Request, string? SourceIsoPath);

/// <param name="LogPath">The log file of the build, or null when none was written.</param>
public sealed record BuildOutcome(BuildReport Report, string? ReportPath, string? IsoPath, string? LogPath)
{
    /// <summary>False when the engine's cleanup at the end of the build did not finish. See <see cref="BuildResult.CleanupFinished" />.</summary>
    public bool CleanupFinished { get; init; } = true;
}

/// <summary>Runs the engine's pipeline off the UI thread.</summary>
public interface IBuildRunner
{
    Task<BuildOutcome> RunAsync(BuildStart start, IProgress<BuildProgress> progress, IProgress<string> log,
        CancellationToken cancellationToken);
}

/// <summary>Brand and keys for the "how to start from the stick" table on the Done page.</summary>
public sealed record BootKey(string Brand, IReadOnlyList<string> BootMenu, IReadOnlyList<string> Setup);