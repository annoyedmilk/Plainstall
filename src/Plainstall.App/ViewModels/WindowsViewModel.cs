using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Plainstall.App.Resources;
using Plainstall.App.Services;
using Plainstall.Core;
using Plainstall.Core.Download;
using Plainstall.Core.Media;
using Plainstall.Core.Profiles;

namespace Plainstall.App.ViewModels;

public sealed record LanguageOption(WindowsLanguage Language)
{
    public string DisplayName => Language.DisplayName;
}

public sealed record EditionOption(string EditionId, string Title);

public sealed partial class WindowsViewModel : WizardPageViewModel
{
    private const string PreferredEdition = "Professional";

    /// <summary>The editions on Microsoft's Windows 11 ISO that people choose between. Shown before anything is downloaded.</summary>
    internal static readonly IReadOnlyList<EditionOption> DownloadEditions =
    [
        new("Core", Strings.Edition_Home),
        new("Professional", Strings.Edition_Pro),
        new("Education", Strings.Edition_Education),
        new("ProfessionalEducation", Strings.Edition_ProEducation),
        new("ProfessionalWorkstation", Strings.Edition_ProWorkstations)
    ];

    private readonly IDialogService _dialogs;
    private readonly IHostInfo _host;
    private readonly ILogger _logger;
    private readonly IMediaReader _media;
    private readonly IMicrosoftDownloadClient _microsoft;

    private readonly WizardSession _session;
    private readonly ISettingsStore _settings;

    private string? _automaticNote;
    private CancellationTokenSource? _languageLoad;
    private bool _syncing;

    public WindowsViewModel(WizardSession session, IMicrosoftDownloadClient microsoft, IMediaReader media,
        IDialogService dialogs,
        ISettingsStore settings, IHostInfo host, ILogger<WindowsViewModel>? logger = null)
    {
        _session = session;
        _microsoft = microsoft;
        _media = media;
        _dialogs = dialogs;
        _settings = settings;
        _host = host;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        session.ProfileReplaced += (_, _) => SyncFromProfile();
        SyncFromProfile();
    }

    public override WizardStep Step => WizardStep.Windows;

    // ---- source --------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseOwnIso))]
    public partial bool UseDownload { get; set; } = true;

    public bool UseOwnIso
    {
        get => !UseDownload;
        set => UseDownload = !value;
    }

    // ---- download ------------------------------------------------------------------------------

    public ObservableCollection<LanguageOption> Languages { get; } = [];

    [ObservableProperty] public partial LanguageOption? SelectedLanguage { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadLanguagesCommand))]
    public partial bool IsLoadingLanguages { get; private set; }

    /// <summary>Why the list could not be loaded, in plain words. The page then offers "Use my ISO".</summary>
    [ObservableProperty]
    public partial string? LanguageLoadError { get; private set; }

    /// <summary>Said when the PC's language is not offered and a related one was chosen.</summary>
    [ObservableProperty]
    public partial string? LanguageNote { get; private set; }

    [ObservableProperty] public partial string? ReleaseText { get; private set; }

    public bool LanguagesLoaded => _session.Release is not null;

    // ---- own ISO -------------------------------------------------------------------------------

    [ObservableProperty] public partial string? IsoPath { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BrowseIsoCommand))]
    public partial bool IsReadingIso { get; private set; }

    [ObservableProperty] public partial string? IsoError { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMedia))]
    public partial MediaInfo? Media { get; private set; }

    public bool HasMedia => Media is not null;

    public string? MediaVersionText => Media is null
        ? null
        : string.Format(CultureInfo.CurrentCulture, Strings.Windows_MediaVersion, Media.Version);

    public string? MediaLanguageText => Media is null
        ? null
        : string.Format(CultureInfo.CurrentCulture, Strings.Windows_MediaLanguage, DescribeLanguage(Media.Language));

    public string? MediaEditionsText => Media is null
        ? null
        : string.Format(CultureInfo.CurrentCulture, Strings.Windows_MediaEditions, Media.Editions.Count);

    // ---- edition -------------------------------------------------------------------------------

    public ObservableCollection<EditionOption> Editions { get; } = [];

    [ObservableProperty] public partial EditionOption? SelectedEdition { get; set; }

    public bool HasEditions => Editions.Count > 0;

    // ---- page ----------------------------------------------------------------------------------

    public override bool CanContinue =>
        SelectedEdition is not null && (UseDownload || (Media is not null && !IsReadingIso));

    public override string? ContinueBlockedReason =>
        CanContinue ? null
        : UseOwnIso && Media is null ? Strings.Windows_BlockedNoIso
        : Strings.Windows_BlockedNoEdition;

    partial void OnUseDownloadChanged(bool value)
    {
        if (_syncing) return;
        _session.Editor.Update(p => p with
        {
            Windows = p.Windows with { Source = value ? WindowsSource.Download : WindowsSource.Iso }
        });
        if (!value && IsoPath is null && _settings.Load().LastIsoPath is { } last && File.Exists(last))
        {
            IsoPath = last;
            _ = ReadIsoAsync(last);
        }

        RefreshEditions();
        NotifyCanContinue();
        if (value) _ = EnsureLanguagesAsync();
    }

    partial void OnSelectedLanguageChanged(LanguageOption? value)
    {
        _session.DownloadLanguage = value?.Language;
        if (_syncing || value is null || _session.Release is not { } release) return;
        // The language the engine would pick on its own is stored as "auto", so the profile stays portable.
        var automatic = LanguageMatcher.Match(_host.DisplayCulture, release.Languages).Language;
        var stored = value.Language == automatic
            ? "auto"
            : LanguageTags.TagFor(value.Language, release.Languages) ?? "auto";
        _session.Editor.Update(p => p with { Windows = p.Windows with { Language = stored } });
        LanguageNote = value.Language == automatic ? _automaticNote : null;
    }

    [RelayCommand(CanExecute = nameof(CanLoadLanguages))]
    private Task LoadLanguagesAsync()
    {
        return LoadLanguagesCoreAsync();
    }

    private bool CanLoadLanguages()
    {
        return !IsLoadingLanguages;
    }

    [RelayCommand]
    private void UseIsoInstead()
    {
        UseDownload = false;
    }

    /// <summary>Loads Microsoft's list once, the first time it is needed.</summary>
    public Task EnsureLanguagesAsync()
    {
        return UseDownload && _session.Release is null && !IsLoadingLanguages && LanguageLoadError is null
            ? LoadLanguagesCoreAsync()
            : Task.CompletedTask;
    }

    private async Task LoadLanguagesCoreAsync()
    {
        if (_languageLoad is { } previous) await previous.CancelAsync();
        _languageLoad = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = _languageLoad.Token;
        IsLoadingLanguages = true;
        LanguageLoadError = null;
        try
        {
            var release = await Task.Run(() => _microsoft.GetReleaseAsync(token), token);
            ApplyRelease(release);
        }
        catch (PlainstallException ex)
        {
            LanguageLoadError = string.Format(CultureInfo.CurrentCulture, Strings.Windows_LanguagesFailed, ex.Message);
        }
        catch (OperationCanceledException)
        {
            LanguageLoadError = string.Format(CultureInfo.CurrentCulture, Strings.Windows_LanguagesFailed,
                Strings.Windows_LanguagesTimedOut);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            _logger.LogWarning(ex, "Loading the language list failed");
            LanguageLoadError = string.Format(CultureInfo.CurrentCulture, Strings.Windows_LanguagesFailed,
                Strings.Windows_LanguagesNoConnection);
        }
        finally
        {
            IsLoadingLanguages = false;
            OnPropertyChanged(nameof(LanguagesLoaded));
            NotifyCanContinue();
        }
    }

    private void ApplyRelease(WindowsRelease release)
    {
        _session.Release = release;
        ReleaseText = release.Build is { } build
            ? string.Format(CultureInfo.CurrentCulture, Strings.Windows_ReleaseWithBuild, build)
            : Strings.Windows_ReleaseNoBuild;
        _syncing = true;
        try
        {
            Languages.Clear();
            foreach (var language in release.Languages.OrderBy(l => l.DisplayName, StringComparer.CurrentCulture))
                Languages.Add(new LanguageOption(language));
            if (Languages.Count == 0)
            {
                LanguageLoadError = string.Format(CultureInfo.CurrentCulture, Strings.Windows_LanguagesFailed,
                    Strings.Windows_LanguagesEmpty);
                return;
            }

            var automatic = LanguageMatcher.Match(_host.DisplayCulture, release.Languages);
            _automaticNote = automatic.Message;
            var wanted = automatic;
            if (_session.Profile.Windows.Language is { } stored && stored != "auto")
                try
                {
                    wanted = LanguageMatcher.Match(stored, release.Languages);
                }
                catch (PlainstallException)
                {
                    // A profile from elsewhere can name a language that is not on this list. The PC's language is used then.
                }

            SelectedLanguage = Languages.First(l => l.Language == wanted.Language);
            LanguageNote = wanted == automatic ? automatic.Message : wanted.Message;
        }
        finally
        {
            _syncing = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanBrowseIso))]
    private async Task BrowseIsoAsync()
    {
        var path = _dialogs.PickIsoToOpen(IsoPath ?? _settings.Load().LastIsoPath);
        if (path is null) return;
        IsoPath = path;
        _settings.Save(_settings.Load() with { LastIsoPath = path });
        await ReadIsoAsync(path);
    }

    private bool CanBrowseIso()
    {
        return !IsReadingIso;
    }

    private async Task ReadIsoAsync(string path)
    {
        IsReadingIso = true;
        IsoError = null;
        Media = null;
        _session.IsoPath = path;
        _session.Media = null;
        NotifyMediaText();
        try
        {
            var media = await Task.Run(() => _media.ReadAsync(path, CancellationToken.None));
            Media = media;
            _session.Media = media;
        }
        catch (PlainstallException ex)
        {
            // Engine messages are written for the user, including the ones for media Plainstall cannot use.
            IsoError = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            IsoError = string.Format(CultureInfo.CurrentCulture, Strings.Windows_IsoUnreadable, ex.Message);
        }
        finally
        {
            IsReadingIso = false;
            NotifyMediaText();
            RefreshEditions();
            NotifyCanContinue();
        }
    }

    private void NotifyMediaText()
    {
        OnPropertyChanged(nameof(MediaVersionText));
        OnPropertyChanged(nameof(MediaLanguageText));
        OnPropertyChanged(nameof(MediaEditionsText));
    }

    partial void OnSelectedEditionChanged(EditionOption? value)
    {
        if (!_syncing && value is not null)
            _session.Editor.Update(p => p with { Windows = p.Windows with { Edition = value.EditionId } });
        NotifyCanContinue();
    }

    private void RefreshEditions()
    {
        var options = UseDownload
            ? DownloadEditions
            : Media?.Editions.Where(e => e.EditionId.Length > 0).Select(e => new EditionOption(e.EditionId, e.Name))
                  .DistinctBy(e => e.EditionId, StringComparer.OrdinalIgnoreCase).ToList()
              ?? [];
        var wanted = _session.Profile.Windows.Edition;
        _syncing = true;
        try
        {
            Editions.Clear();
            foreach (var option in options) Editions.Add(option);
            SelectedEdition =
                Editions.FirstOrDefault(e => string.Equals(e.EditionId, wanted, StringComparison.OrdinalIgnoreCase))
                ?? Editions.FirstOrDefault(e => e.EditionId == PreferredEdition)
                ?? Editions.FirstOrDefault();
        }
        finally
        {
            _syncing = false;
        }

        if (SelectedEdition is { } selected &&
            !string.Equals(selected.EditionId, wanted, StringComparison.OrdinalIgnoreCase))
            _session.Editor.Update(p => p with { Windows = p.Windows with { Edition = selected.EditionId } });
        OnPropertyChanged(nameof(HasEditions));
    }

    public override Task OnShownAsync()
    {
        return EnsureLanguagesAsync();
    }

    private void SyncFromProfile()
    {
        _syncing = true;
        try
        {
            UseDownload = _session.Profile.Windows.Source == WindowsSource.Download;
            if (_session.Release is not null && Languages.Count > 0)
            {
                var stored = _session.Profile.Windows.Language;
                var wanted = stored == "auto"
                    ? LanguageMatcher.Match(_host.DisplayCulture, _session.Release.Languages)
                    : TryMatch(stored, _session.Release.Languages);
                if (wanted is not null) SelectedLanguage = Languages.FirstOrDefault(l => l.Language == wanted.Language);
            }
        }
        finally
        {
            _syncing = false;
        }

        OnPropertyChanged(nameof(UseOwnIso));
        RefreshEditions();
        NotifyCanContinue();
    }

    private static LanguageMatch? TryMatch(string tag, IReadOnlyList<WindowsLanguage> offered)
    {
        try
        {
            return LanguageMatcher.Match(tag, offered);
        }
        catch (PlainstallException)
        {
            return null;
        }
    }

    internal static string DescribeLanguage(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return Strings.Windows_LanguageUnknown;
        try
        {
            var culture = CultureInfo.GetCultureInfo(tag);
            return string.Format(CultureInfo.CurrentCulture, Strings.Windows_LanguageWithTag, culture.DisplayName, tag);
        }
        catch (CultureNotFoundException)
        {
            return tag;
        }
    }
}