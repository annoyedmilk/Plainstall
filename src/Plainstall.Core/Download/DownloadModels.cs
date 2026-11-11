namespace Plainstall.Core.Download;

/// <summary>
///     One language Microsoft offers the ISO in. <see cref="LocalizedLanguage" /> is the name shown to
///     people and the name the published hash table uses; <see cref="Language" /> is a second name the
///     service returns that differs for a few entries (British English is "English (United Kingdom)"
///     there and "English International" in <see cref="LocalizedLanguage" />).
/// </summary>
public sealed record WindowsLanguage(string SkuId, string Language, string LocalizedLanguage)
{
    /// <summary>The name to show in a picker.</summary>
    public string DisplayName => LocalizedLanguage.Length != 0 ? LocalizedLanguage : Language;

    /// <summary>The product this language entry belongs to, for example "Windows 11 Client - Build 26300.9457".</summary>
    public string? ProductDisplayName { get; init; }
}

/// <summary>
///     What Microsoft offers right now for x64 PCs: the languages for the picker and
///     the published hashes to verify a download against. It is read from the live page on every run so
///     that a new Windows release needs no Plainstall update.
/// </summary>
public sealed record WindowsRelease(
    string ProductEditionId,
    IReadOnlyList<WindowsLanguage> Languages,
    PublishedHashTable Hashes)
{
    /// <summary>The entry of the download page's product list, for example "Windows 11 (multi-edition ISO for x64 devices)".</summary>
    public string? EditionName { get; init; }

    /// <summary>
    ///     Product name as the service reports it, for example "Windows 11 Client - Build 26300.9457". Null when not
    ///     reported.
    /// </summary>
    public string? ProductDisplayName { get; init; }

    /// <summary>Build number taken from <see cref="ProductDisplayName" />, for example "26300.9457". Null when not reported.</summary>
    public string? Build { get; init; }

    /// <summary>The service session the language list was read with. Reused for the link request while it is young.</summary>
    internal ServiceSession? Session { get; init; }
}

/// <summary>A session id that was announced to Microsoft's service, and when.</summary>
internal sealed record ServiceSession(string Id, DateTimeOffset CreatedAt);

/// <summary>
///     A signed, short-lived link to one ISO. The link carries a token in its query string, so a ticket
///     must never be written to a log or a file as a whole. <see cref="ToString" /> is safe to log.
/// </summary>
public sealed record DownloadTicket(Uri Uri, string FileName, DateTimeOffset? ExpiresAt)
{
    /// <summary>Host of the link, the only part of it that may appear in messages.</summary>
    public string Host => HostAllowlist.DescribeHost(Uri);

    /// <summary>
    ///     True when the link is past its expiry, or will be within <paramref name="margin" />. An unknown expiry counts
    ///     as valid.
    /// </summary>
    public bool IsExpired(DateTimeOffset now, TimeSpan margin)
    {
        return ExpiresAt is { } expires && expires - margin <= now;
    }

    /// <summary>Host and file name only. The record's generated text would print the signed link.</summary>
    public override string ToString()
    {
        return $"DownloadTicket {{ Host = {Host}, FileName = {FileName}, ExpiresAt = {ExpiresAt:u} }}";
    }
}