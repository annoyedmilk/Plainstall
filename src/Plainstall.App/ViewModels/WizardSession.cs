using System.Globalization;
using Plainstall.Core.Catalog;
using Plainstall.Core.Download;
using Plainstall.Core.Media;
using Plainstall.Core.Pipeline;
using Plainstall.Core.Profiles;

namespace Plainstall.App.ViewModels;

/// <summary>
///     What the wizard pages share: the profile being edited, and the choices that are deliberately not part
///     of a profile because they belong to this PC (the ISO file, what was read from it, Microsoft's list).
/// </summary>
public sealed class WizardSession
{
    /// <summary>The build planned for when the download has not happened yet and the real build is unknown.</summary>
    public const int AssumedDownloadBuild = 26300;

    /// <summary>
    ///     A little more than the current x64 ISO (about 9 GB), used for space checks before the real size is known.
    /// </summary>
    public const long AssumedMediaBytes = Preflight.AssumedMediaBytes;

    public WizardSession(Catalog catalog, InstallSuggestions installs)
    {
        Catalog = catalog;
        Installs = installs;
        Editor = new ProfileEditor(catalog, ProfileFactory.FromPreset(catalog.DefaultPreset));
    }

    public Catalog Catalog { get; }
    public InstallSuggestions Installs { get; }
    public ProfileEditor Editor { get; }
    public Profile Profile => Editor.Profile;

    /// <summary>The ISO the user picked, when the profile's source is <see cref="WindowsSource.Iso" />.</summary>
    public string? IsoPath { get; set; }

    /// <summary>What was read from <see cref="IsoPath" />.</summary>
    public MediaInfo? Media { get; set; }

    /// <summary>Microsoft's current offer, once the language list was loaded.</summary>
    public WindowsRelease? Release { get; set; }

    /// <summary>The language picked from <see cref="Release" />, for the summary.</summary>
    public WindowsLanguage? DownloadLanguage { get; set; }

    /// <summary>Where builds work. Set from the app settings at start.</summary>
    public string WorkFolder { get; set; } = PlainstallPaths.Default.DefaultWork;

    /// <summary>Where a download of the ISO goes.</summary>
    public string CacheFolder { get; set; } = PlainstallPaths.Default.Cache;

    /// <summary>True when the user opened a profile file or the last profile, rather than starting from a preset card.</summary>
    public bool StartedFromFile { get; private set; }

    /// <summary>True when the image build is only assumed because nothing has been downloaded or read yet.</summary>
    public bool ImageBuildIsAssumed =>
        Profile.Windows.Source == WindowsSource.Download && ParseBuild(Release?.Build) is null;

    public long MediaBytes => Profile.Windows.Source == WindowsSource.Iso && Media is { } media
        ? media.TotalBytes
        : AssumedMediaBytes;

    /// <summary>
    ///     The size of the profile's update files that exist on this PC. A missing file adds nothing: the build
    ///     refuses it before the space check.
    /// </summary>
    public long UpdateBytes => Profile.Updates.Where(File.Exists).Sum(file => new FileInfo(file).Length);

    public event EventHandler? ProfileReplaced;

    /// <summary>A fresh profile from a preset. The Windows choices made so far are kept.</summary>
    public void StartFromPreset(Preset preset)
    {
        var windows = Profile.Windows;
        Editor.Replace(ProfileFactory.FromPreset(preset) with { Windows = windows });
        StartedFromFile = false;
        ProfileReplaced?.Invoke(this, EventArgs.Empty);
    }

    public void LoadProfile(Profile profile)
    {
        Editor.Replace(profile with
        {
            Setup = profile.Setup with
            {
                LocalAccount = profile.Setup.LocalAccount is { } a ? a with { Password = null } : null
            }
        });
        StartedFromFile = true;
        ProfileReplaced?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>What the planner needs to know about the image, as far as it is known before the build.</summary>
    public ImageFacts CurrentImageFacts()
    {
        if (Profile.Windows.Source != WindowsSource.Iso || Media is not { } media)
            return new ImageFacts(ParseBuild(Release?.Build) ?? AssumedDownloadBuild, Profile.Windows.Edition);
        var edition = media.Editions.FirstOrDefault(e =>
            string.Equals(e.EditionId, Profile.Windows.Edition, StringComparison.OrdinalIgnoreCase));
        return new ImageFacts(edition?.Build ?? media.Build, edition?.EditionId ?? Profile.Windows.Edition);
    }

    private static int? ParseBuild(string? build)
    {
        return build is not null && int.TryParse(build.Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }
}