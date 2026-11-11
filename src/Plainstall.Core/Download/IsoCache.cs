using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Plainstall.Core.Download;

/// <summary>
///     A downloaded ISO in the cache, as described by the small JSON file next to it. The description is
///     a claim, not a fact: <see cref="IsoCache.VerifyAsync" /> is what proves the file still matches it.
/// </summary>
public sealed record CachedIso(
    string Path,
    string Sha256,
    string Language,
    long SizeBytes,
    DateTimeOffset DownloadedAt)
{
    /// <summary>Build number at download time, for example "26300.9457". Null when the service did not report one.</summary>
    public string? Build { get; init; }

    /// <summary>Product name at download time, for example "Windows 11 Client - Build 26300.9457".</summary>
    public string? ProductName { get; init; }

    /// <summary>True when the hash was found in Microsoft's published table for this language when the file was downloaded.</summary>
    public bool VerifiedAgainstMicrosoft { get; init; }
}

/// <summary>What is known about a new cache entry besides the file itself.</summary>
public sealed record IsoDescription(string Sha256, string Language)
{
    /// <summary>Build number, when known.</summary>
    public string? Build { get; init; }

    /// <summary>Product name, when known.</summary>
    public string? ProductName { get; init; }

    /// <summary>True when the hash was found in Microsoft's published table for this language.</summary>
    public bool VerifiedAgainstMicrosoft { get; init; }
}

/// <summary>How a cached ISO compares with what Microsoft publishes today.</summary>
public enum CacheFreshness
{
    /// <summary>Microsoft still lists this exact file for the language.</summary>
    Current,

    /// <summary>Microsoft lists a different file for the language now: a newer build has replaced the cached one.</summary>
    Superseded,

    /// <summary>The current table has no row for the language, so nothing can be said.</summary>
    Unknown
}

/// <summary>
///     The folder of finished downloads. A 5 to 10 GB download is worth keeping, but only as long as the
///     file is provably the one that was downloaded: every entry carries its SHA-256, and an entry is
///     only handed out after a size check, with a full hash check available before it is used.
/// </summary>
public sealed class IsoCache
{
    private const string SidecarSuffix = ".json";
    private const int SidecarVersion = 1;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private readonly ILogger _logger;

    private readonly TimeProvider _time;

    public IsoCache(string folder, TimeProvider? timeProvider = null, ILogger<IsoCache>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        Folder = Path.GetFullPath(folder);
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<IsoCache>.Instance;
    }

    /// <summary>The cache folder. It is created on first use.</summary>
    public string Folder { get; }

    /// <summary>
    ///     Where a download with this file name belongs, so that it lands on the cache drive and needs no copy
    ///     afterwards.
    /// </summary>
    public string GetPathFor(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (fileName != Path.GetFileName(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("A plain file name is required, without a folder.", nameof(fileName));

        return Path.Combine(Folder, fileName);
    }

    /// <summary>The name of the description file for an ISO.</summary>
    public static string SidecarPathFor(string isoPath)
    {
        return isoPath + SidecarSuffix;
    }

    /// <summary>
    ///     Every entry whose description is readable and whose file exists with the recorded size, newest
    ///     first. Broken descriptions are skipped, not repaired: a cache miss costs a download, a wrong hit
    ///     costs a broken installation.
    /// </summary>
    public IReadOnlyList<CachedIso> List()
    {
        var entries = new List<CachedIso>();
        if (!Directory.Exists(Folder)) return entries;

        IEnumerable<string> files;
        try
        {
            files = [.. Directory.EnumerateFiles(Folder, "*.iso" + SidecarSuffix, SearchOption.TopDirectoryOnly)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The ISO cache folder could not be read: {Reason}", ex.Message);
            return entries;
        }

        foreach (var sidecar in files)
        {
            var iso = sidecar[..^SidecarSuffix.Length];
            var entry = ReadSidecar(sidecar, iso);
            if (entry is null)
            {
                _logger.LogWarning("Ignoring cache entry {File}: its description is missing parts or damaged.",
                    Path.GetFileName(iso));
                continue;
            }

            try
            {
                var info = new FileInfo(iso);
                if (!info.Exists)
                {
                    _logger.LogWarning("Ignoring cache entry {File}: the ISO itself is gone.", info.Name);
                    continue;
                }

                if (info.Length != entry.SizeBytes)
                {
                    _logger.LogWarning(
                        "Ignoring cache entry {File}: its size is {Actual} bytes, the description says {Expected}.",
                        info.Name, info.Length, entry.SizeBytes);
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            entries.Add(entry);
        }

        entries.Sort((a, b) => b.DownloadedAt.CompareTo(a.DownloadedAt));
        return entries;
    }

    /// <summary>All entries for a language that pass the quick checks, newest first.</summary>
    public IReadOnlyList<CachedIso> FindAll(WindowsLanguage language)
    {
        ArgumentNullException.ThrowIfNull(language);
        var key = HtmlText.NameKey(language.LocalizedLanguage);
        var otherKey = HtmlText.NameKey(language.Language);
        return [.. List().Where(e => Matches(e, key, otherKey))];
    }

    /// <summary>The full check: reads the whole file and compares its SHA-256 with the recorded one.</summary>
    public async Task<bool> VerifyAsync(CachedIso entry, IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            var info = new FileInfo(entry.Path);
            if (!info.Exists || info.Length != entry.SizeBytes) return false;

            var actual = await HashHex.Sha256OfFileAsync(entry.Path, progress, cancellationToken).ConfigureAwait(false);
            return HashHex.Equal(actual, entry.Sha256);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Cached ISO {File} could not be read: {Reason}", Path.GetFileName(entry.Path),
                ex.Message);
            return false;
        }
    }

    /// <summary>
    ///     Records a finished ISO. A file that is already in the cache folder stays where it is; a file
    ///     from elsewhere is moved in (never over an existing file). The hash in the description is taken
    ///     on trust from the caller, who has just computed it.
    /// </summary>
    public Task<CachedIso> AddAsync(string isoPath, IsoDescription description, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(isoPath);
        ArgumentNullException.ThrowIfNull(description);
        if (!HashHex.IsSha256(description.Sha256))
            throw new ArgumentException("The SHA-256 must be exactly 64 hex characters.", nameof(description));

        if (string.IsNullOrWhiteSpace(description.Language))
            throw new ArgumentException("The language name is required.", nameof(description));

        var source = Path.GetFullPath(isoPath);

        // Moving a file in from another drive copies several gigabytes, so this does not run on the caller's thread.
        return source.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)
            ? Task.Run(() => Add(source, description), cancellationToken)
            : throw new ArgumentException("Only .iso files belong in the cache.", nameof(isoPath));
    }

    private CachedIso Add(string source, IsoDescription description)
    {
        try
        {
            if (!File.Exists(source))
                throw new FileNotFoundException("The ISO to add to the cache does not exist.", source);

            Directory.CreateDirectory(Folder);
            var target = Path.Combine(Folder, Path.GetFileName(source));
            if (!string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(target))
                    throw new PlainstallException(
                        ErrorKind.InvalidInput,
                        $"The download cache already holds a file named \"{Path.GetFileName(target)}\". Plainstall does not overwrite files.");

                // A description left over from an earlier file of this name must not describe the new one.
                File.Delete(SidecarPathFor(target));
                File.Move(source, target, false);
            }

            var entry = new CachedIso(
                target,
                description.Sha256.ToUpperInvariant(),
                description.Language.Trim(),
                new FileInfo(target).Length,
                _time.GetUtcNow())
            {
                Build = description.Build,
                ProductName = description.ProductName,
                VerifiedAgainstMicrosoft = description.VerifiedAgainstMicrosoft
            };
            WriteSidecar(SidecarPathFor(target), entry);
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Added {File} to the ISO cache.", Path.GetFileName(target));
            return entry;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PlainstallException(
                ErrorKind.Preflight,
                $"Plainstall could not store the download in its cache folder \"{Folder}\": {ex.Message}",
                ex);
        }
    }

    /// <summary>
    ///     Deletes an entry: the description first, so that a half-finished removal never leaves a description without a
    ///     file to check.
    /// </summary>
    public void Remove(CachedIso entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var path = Path.GetFullPath(entry.Path);
        if (!string.Equals(Path.GetDirectoryName(path), Folder, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The entry is not in this cache folder.", nameof(entry));

        foreach (var file in new[] { SidecarPathFor(path), path })
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Could not delete {File} from the ISO cache: {Reason}", Path.GetFileName(file),
                    ex.Message);
            }
    }

    /// <summary>
    ///     Compares a cached ISO with the hashes Microsoft publishes now. "Superseded" is how a newer build
    ///     shows up: the language is still listed, but with a different hash than the cached file has.
    /// </summary>
    public static CacheFreshness GetFreshness(CachedIso entry, WindowsRelease current, WindowsLanguage language)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(language);
        var rows = current.Hashes.FindRows(language);
        if (rows.Count == 0) return CacheFreshness.Unknown;

        return current.Hashes.IsPublishedFor(language, entry.Sha256)
            ? CacheFreshness.Current
            : CacheFreshness.Superseded;
    }

    private static bool Matches(CachedIso entry, string key, string otherKey)
    {
        var entryKey = HtmlText.NameKey(entry.Language);
        return entryKey.Length != 0 && (entryKey == key || (key.Length == 0 && entryKey == otherKey));
    }

    private static void WriteSidecar(string path, CachedIso entry)
    {
        AtomicFile.WriteAllText(path, new JsonObject
        {
            ["version"] = SidecarVersion,
            ["file"] = Path.GetFileName(entry.Path),
            ["sha256"] = entry.Sha256,
            ["sizeBytes"] = entry.SizeBytes,
            ["language"] = entry.Language,
            ["build"] = entry.Build,
            ["product"] = entry.ProductName,
            ["verifiedAgainstMicrosoft"] = entry.VerifiedAgainstMicrosoft,
            ["downloadedAt"] = entry.DownloadedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)
        }.ToJsonString(Indented));
    }

    private static CachedIso? ReadSidecar(string sidecarPath, string isoPath)
    {
        try
        {
            var file = new FileInfo(sidecarPath);
            if (!file.Exists || file.Length > 64 * 1024) return null;

            using var json = JsonDocument.Parse(File.ReadAllBytes(sidecarPath));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var versionNumber) || versionNumber != SidecarVersion)
                return null;

            var sha256 = Text(root, "sha256");
            var language = Text(root, "language");
            if (!HashHex.IsSha256(sha256) || string.IsNullOrWhiteSpace(language) ||
                !root.TryGetProperty("sizeBytes", out var size) || size.ValueKind != JsonValueKind.Number ||
                !size.TryGetInt64(out var sizeBytes) || sizeBytes <= 0 ||
                !DateTimeOffset.TryParse(Text(root, "downloadedAt"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var downloadedAt))
                return null;

            // The description names its file. A description that was copied next to another ISO does not vouch for it.
            if (Text(root, "file") is { } named &&
                !string.Equals(named, Path.GetFileName(isoPath), StringComparison.OrdinalIgnoreCase)) return null;

            return new CachedIso(isoPath, sha256!.ToUpperInvariant(), language, sizeBytes, downloadedAt)
            {
                Build = Text(root, "build"),
                ProductName = Text(root, "product"),
                VerifiedAgainstMicrosoft = root.TryGetProperty("verifiedAgainstMicrosoft", out var verified) &&
                                           verified.ValueKind == JsonValueKind.True
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}