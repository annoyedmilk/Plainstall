using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Plainstall.Core.Download;

/// <summary>What <see cref="WindowsIsoProvider" /> is busy with, for a progress display.</summary>
public enum WindowsIsoPhase
{
    /// <summary>Reading Microsoft's download page and language list.</summary>
    ContactingMicrosoft,

    /// <summary>Hashing a cached ISO to make sure it is still intact.</summary>
    CheckingCache,

    /// <summary>Downloading the ISO.</summary>
    Downloading,

    /// <summary>Hashing the finished download.</summary>
    Verifying
}

/// <summary>
///     Progress of <see cref="WindowsIsoProvider.GetIsoAsync" />. <see cref="Transfer" /> is null in phases without a
///     byte count.
/// </summary>
public readonly record struct WindowsIsoProgress(WindowsIsoPhase Phase, TransferProgress? Transfer);

/// <summary>A ready-to-use ISO and how far it can be trusted.</summary>
public sealed record WindowsIsoResult(string Path, string Sha256, bool VerifiedAgainstMicrosoft);

/// <summary>
///     The one call the pipeline and the hosts need: "give me a verified Windows ISO for this language".
///     It hides the order of the steps, and the order is important. Look in the cache. Trust a cached file
///     only after its hash is correct. Prefer what Microsoft publishes today. Verify what was downloaded.
/// </summary>
public sealed class WindowsIsoProvider
{
    private readonly IsoCache _cache;
    private readonly IMicrosoftDownloadClient _client;
    private readonly ResumableDownloader _downloader;
    private readonly ILogger _logger;

    public WindowsIsoProvider(
        IMicrosoftDownloadClient client,
        ResumableDownloader downloader,
        IsoCache cache,
        ILogger<WindowsIsoProvider>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(cache);
        _client = client;
        _downloader = downloader;
        _cache = cache;
        _logger = logger ?? NullLogger<WindowsIsoProvider>.Instance;
    }

    /// <summary>The folder of the cache, where a download goes.</summary>
    public string CacheFolder => _cache.Folder;

    /// <summary>
    ///     Returns an ISO in the offered language that fits <paramref name="culture" /> best, from the cache or
    ///     as a new download. Throws <see cref="PlainstallException" /> with a message for the user when that
    ///     is not possible. A cancel leaves a partial download in the cache folder. The next call continues it.
    /// </summary>
    public async Task<WindowsIsoResult> GetIsoAsync(CultureInfo culture, IProgress<WindowsIsoProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(culture);

        WindowsRelease release;
        progress?.Report(new WindowsIsoProgress(WindowsIsoPhase.ContactingMicrosoft, null));
        try
        {
            release = await _client.GetReleaseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PlainstallException ex) when (ex.Kind is ErrorKind.Network or ErrorKind.ServiceRefused)
        {
            var cached = await FindWithoutServiceAsync(culture, progress, cancellationToken).ConfigureAwait(false);
            if (cached is null) throw;

            _logger.LogWarning("Microsoft's service is not available ({Kind}). Using the cached ISO {File}.",
                ex.Kind, Path.GetFileName(cached.Path));
            return cached;
        }

        if (release.Languages.Count == 0)
            throw new PlainstallException(
                ErrorKind.ServiceRefused,
                "Microsoft's download service returned no languages. " + DownloadMessages.OwnIsoOffer);

        var (language, _, message) = LanguageMatcher.Match(culture, release.Languages);
        if (message is not null) _logger.LogWarning("{Message}", message);

        var rows = release.Hashes.FindRows(language);
        var expected = release.Hashes.FindExpectedHash(language);
        var cacheProgress = Relay.Create(progress, WindowsIsoPhase.CheckingCache);

        // 1. The cache.
        var superseded = new List<CachedIso>();
        foreach (var entry in _cache.FindAll(language))
        {
            var freshness = IsoCache.GetFreshness(entry, release, language);
            if (freshness == CacheFreshness.Superseded)
            {
                superseded.Add(entry);
                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation("Cached ISO {File} is an older build than the one Microsoft offers now.",
                        Path.GetFileName(entry.Path));

                continue;
            }

            // A file that the current table does not list is trusted only when it was verified at download time.
            if (freshness != CacheFreshness.Current && !entry.VerifiedAgainstMicrosoft) continue;

            if (!await _cache.VerifyAsync(entry, cacheProgress, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogWarning("Cached ISO {File} no longer matches its recorded SHA-256. Removing it.",
                    Path.GetFileName(entry.Path));
                _cache.Remove(entry);
                continue;
            }

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Using cached ISO {File} ({Freshness}).", Path.GetFileName(entry.Path),
                    freshness);

            return new WindowsIsoResult(entry.Path, entry.Sha256, true);
        }

        // 2. The download.
        if (rows.Count == 0)
        {
            _logger.LogError("The published hash table has no row for \"{Label}\" ({Rows} rows in total).",
                PublishedHashTable.LabelFor(language), release.Hashes.Rows.Count);
            throw new PlainstallException(
                ErrorKind.ServiceRefused,
                $"Microsoft's download page does not show a checksum for {language.DisplayName}, so Plainstall could not make sure that " +
                "the download is the original file. " + DownloadMessages.ServiceChanged + " " +
                DownloadMessages.OwnIsoOffer);
        }

        progress?.Report(new WindowsIsoProgress(WindowsIsoPhase.Downloading, null));
        var ticket = await TicketFactory(cancellationToken).ConfigureAwait(false);
        var destination = _cache.GetPathFor(ticket.FileName);

        var product = language.ProductDisplayName ?? release.ProductDisplayName;

        if (File.Exists(destination))
        {
            // A file with the right name that the cache did not vouch for. It is ours (the cache folder
            // holds nothing else), so either it proves itself by its hash or it makes room.
            string? existing = null;
            if (expected is not null)
                existing = await HashHex.Sha256OfFileAsync(destination, cacheProgress, cancellationToken)
                    .ConfigureAwait(false);

            if (existing is not null && HashHex.Equal(existing, expected))
            {
                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation(
                        "{File} is already in the cache folder and matches Microsoft's published hash.",
                        ticket.FileName);

                var adopted = await _cache.AddAsync(destination, Describe(existing), CancellationToken.None)
                    .ConfigureAwait(false);
                RemoveSuperseded(superseded, adopted);
                return new WindowsIsoResult(adopted.Path, adopted.Sha256, true);
            }

            _logger.LogWarning(
                "{File} in the cache folder is not a file Plainstall can vouch for. Replacing it with a new download.",
                ticket.FileName);
            DeleteFromCache(destination);
        }

        var download = await _downloader.DownloadAsync(
            new DownloadRequest(destination, TicketFactory)
            {
                InitialTicket = ticket,
                ExpectedSha256 = expected,
                VerifyProgress = Relay.Create(progress, WindowsIsoPhase.Verifying)
            },
            Relay.Create(progress, WindowsIsoPhase.Downloading),
            cancellationToken).ConfigureAwait(false);

        // With one row, the downloader has compared the hash already. When the page lists this language
        // more than once, any of its rows is acceptable. Nothing else is.
        if (!release.Hashes.IsPublishedFor(language, download.Sha256))
        {
            DeleteFromCache(download.Path);
            throw new PlainstallException(
                ErrorKind.Verification,
                "The downloaded file is different from the file that Microsoft publishes. Plainstall deleted it. " +
                "Start the download again. " + DownloadMessages.OwnIsoOffer);
        }

        var added = await _cache.AddAsync(download.Path, Describe(download.Sha256), CancellationToken.None)
            .ConfigureAwait(false);
        RemoveSuperseded(superseded, added);
        return new WindowsIsoResult(added.Path, added.Sha256, true);

        Task<DownloadTicket> TicketFactory(CancellationToken token)
        {
            return _client.GetDownloadTicketAsync(release, language, token);
        }

        IsoDescription Describe(string sha256)
        {
            return new IsoDescription(sha256, language.DisplayName)
            {
                Build = release.Build,
                ProductName = product,
                VerifiedAgainstMicrosoft = true
            };
        }
    }

    /// <summary>The cache-only path for when Microsoft cannot be asked. Only an exact language match counts here.</summary>
    private async Task<WindowsIsoResult?> FindWithoutServiceAsync(CultureInfo culture,
        IProgress<WindowsIsoProgress>? progress, CancellationToken cancellationToken)
    {
        // Without Microsoft's list, the languages that are in the cache are the list.
        List<WindowsLanguage> cachedLanguages =
        [
            .. _cache.List()
                .Select(e => e.Language)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => new WindowsLanguage(string.Empty, name, name))
        ];
        if (cachedLanguages.Count == 0) return null;

        var match = LanguageMatcher.Match(culture, cachedLanguages);
        if (match.Kind != LanguageMatchKind.Exact) return null;

        var cacheProgress = Relay.Create(progress, WindowsIsoPhase.CheckingCache);
        foreach (var entry in _cache.FindAll(match.Language))
            if (entry.VerifiedAgainstMicrosoft &&
                await _cache.VerifyAsync(entry, cacheProgress, cancellationToken).ConfigureAwait(false))
                return new WindowsIsoResult(entry.Path, entry.Sha256, true);

        return null;
    }

    private void RemoveSuperseded(List<CachedIso> superseded, CachedIso keep)
    {
        foreach (var old in superseded.Where(old =>
                     !string.Equals(old.Path, keep.Path, StringComparison.OrdinalIgnoreCase)))
        {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Removing the older build {File} from the ISO cache.",
                    Path.GetFileName(old.Path));

            _cache.Remove(old);
        }
    }

    private void DeleteFromCache(string isoPath)
    {
        foreach (var file in new[] { IsoCache.SidecarPathFor(isoPath), isoPath })
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new PlainstallException(
                    ErrorKind.Preflight,
                    $"Plainstall could not remove an unusable file from its cache folder \"{_cache.Folder}\": {ex.Message} " +
                    "Close programs that may be using it. Then try again.",
                    ex);
            }
    }

    /// <summary>Turns the byte progress of one step into the provider's progress with the phase attached.</summary>
    private sealed class Relay(IProgress<WindowsIsoProgress> target, WindowsIsoPhase phase)
        : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value)
        {
            target.Report(new WindowsIsoProgress(phase, value));
        }

        public static Relay? Create(IProgress<WindowsIsoProgress>? target, WindowsIsoPhase phase)
        {
            return target is null ? null : new Relay(target, phase);
        }
    }
}