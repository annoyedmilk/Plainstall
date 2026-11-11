using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Plainstall.Core.Download;

/// <summary>
///     Microsoft's public Windows 11 download service, reduced to the two questions Plainstall asks it.
///     An interface so that the pickers, the cache logic and the pipeline can be tested without a network.
/// </summary>
public interface IMicrosoftDownloadClient
{
    /// <summary>What Microsoft offers right now: languages, product name, published hashes.</summary>
    Task<WindowsRelease> GetReleaseAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     A fresh signed link for one language. Links expire (after 24 hours at the time of writing), so
    ///     this is called again whenever a download has to continue with a new link.
    /// </summary>
    Task<DownloadTicket> GetDownloadTicketAsync(WindowsRelease release, WindowsLanguage language,
        CancellationToken cancellationToken);
}

/// <summary>
///     Talks to Microsoft's download page and its connector API, the same calls a browser makes when a
///     person picks a language on the page. Only Microsoft hosts are contacted, every redirect hop is
///     checked, and nothing is hard-coded that Microsoft changes between releases: the product id, the
///     languages and the hashes are read from the live service on every run.
/// </summary>
public sealed partial class MicrosoftDownloadClient : IMicrosoftDownloadClient, IDisposable
{
    /// <summary>Largest page or JSON answer that is read. The real page is a fraction of this.</summary>
    public const int MaxResponseBytes = 4 * 1024 * 1024;

    private const string DownloadPage = "https://www.microsoft.com/en-us/software-download/windows11";
    private const string ConnectorBase = "https://www.microsoft.com/software-download-connector/api/";
    private const string Profile = "606624d44113";
    private const string OrgId = "y6jn8c31";
    private const string InstanceId = "560dc9f3-1aa5-4a2f-b63c-9e18f8d0e175";

    /// <summary>
    ///     A session is reused for the link request only this long after it was announced. Older ones are
    ///     replaced, because a link is often asked for again hours later, when a download resumes.
    /// </summary>
    internal static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(10);

    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    private readonly DownloadTransport _transport;

    /// <param name="handler">
    ///     The HTTP handler to send through, or null for Plainstall's own. An injected handler is not
    ///     disposed and must not follow redirects by itself.
    /// </param>
    /// <param name="timeProvider">The clock for session ages and retry waits, or null for the system clock.</param>
    /// <param name="logger">Where the client logs what it does, or null for no log.</param>
    /// <param name="options">Timeouts and retry limits of the transport, or null for the defaults.</param>
    public MicrosoftDownloadClient(
        HttpMessageHandler? handler = null,
        TimeProvider? timeProvider = null,
        ILogger<MicrosoftDownloadClient>? logger = null,
        TransportOptions? options = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<MicrosoftDownloadClient>.Instance;
        _transport = new DownloadTransport(handler, _time, options ?? new TransportOptions(), _logger);
    }

    /// <summary>Releases the connection pool when the client created its own handler.</summary>
    public void Dispose()
    {
        _transport.Dispose();
    }

    /// <inheritdoc />
    public async Task<WindowsRelease> GetReleaseAsync(CancellationToken cancellationToken)
    {
        var page = new Uri(DownloadPage);

        string html;
        try
        {
            html = await _transport.GetStringAsync(page, HostAllowlist.Catalog, MaxResponseBytes, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TransportException ex) when (ex is
                                            {
                                                Failure: TransportFailure.HttpStatus, Kind: ErrorKind.ServiceRefused
                                            })
        {
            throw Refused(
                $"Microsoft's download page could not be opened (error {(int?)ex.StatusCode}). " +
                "Microsoft may be blocking your network or region, or the page has moved.",
                ex);
        }

        var parsed = ProductPage.Parse(html);
        if (parsed.EditionId is null)
        {
            _logger.LogError(
                "The download page has no product list (select#product-edition with a numeric option). Page length: {Length}.",
                html.Length);
            throw Refused(
                "Microsoft's download page looks different from what Plainstall expects, so it cannot find the Windows 11 download on it. " +
                DownloadMessages.ServiceChanged);
        }

        if (parsed.Hashes.IsEmpty)
            _logger.LogWarning(
                "The download page has no hash table. Downloads cannot be checked against Microsoft's published hashes.");
        else if (parsed.Hashes.RejectedRows != 0)
            _logger.LogWarning(
                "The hash table on the download page has {Count} rows that are not valid SHA-256 values. They are ignored.",
                parsed.Hashes.RejectedRows);

        var unreachable = new List<string>();
        var session = await OpenSessionAsync(unreachable, cancellationToken).ConfigureAwait(false);
        var languages = await GetLanguagesAsync(parsed.EditionId, session, unreachable, cancellationToken)
            .ConfigureAwait(false);

        var product = languages.Select(l => l.ProductDisplayName).FirstOrDefault(p => !string.IsNullOrEmpty(p));
        var build = product is null ? null :
            BuildRegex().Match(product) is { Success: true } m ? m.Groups[1].Value : null;
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "Microsoft offers {Product} in {Count} languages, with {Hashes} published hashes.",
                product ?? parsed.EditionName ?? "Windows 11", languages.Count, parsed.Hashes.Rows.Count);

        return new WindowsRelease(parsed.EditionId, languages, parsed.Hashes)
        {
            EditionName = parsed.EditionName,
            ProductDisplayName = product,
            Build = build,
            Session = session
        };
    }

    /// <inheritdoc />
    public async Task<DownloadTicket> GetDownloadTicketAsync(WindowsRelease release, WindowsLanguage language,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(language);
        if (!IsSafeId(language.SkuId))
            throw new ArgumentException("The language has no usable SKU id.", nameof(language));

        var unreachable = new List<string>();
        var session = release.Session;
        var reused = session is not null && _time.GetUtcNow() - session.CreatedAt < SessionLifetime &&
                     _time.GetUtcNow() >= session.CreatedAt;
        if (reused)
            try
            {
                return await RequestLinkAsync(language, session!, unreachable, cancellationToken).ConfigureAwait(false);
            }
            catch (ServiceAnswerException ex)
            {
                // The service said no to a session it accepted minutes ago. Sessions are cheap, so
                // ask once more with a new one before telling the user.
                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation(
                        "The link request was refused with the existing session ({Reason}). Trying once with a new session.",
                        ex.ServiceText);
            }

        // A new session repeats the calls the page makes before it asks for links, language list included.
        session = await OpenSessionAsync(unreachable, cancellationToken).ConfigureAwait(false);
        if (IsSafeId(release.ProductEditionId))
            await GetLanguagesAsync(release.ProductEditionId, session, unreachable, cancellationToken)
                .ConfigureAwait(false);

        return await RequestLinkAsync(language, session, unreachable, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Announces a new session id the way the page's scripts do. The answers carry nothing Plainstall
    ///     needs except two values that are passed on. A failure here is remembered and not fatal: the
    ///     request that depends on the session reports the real outcome.
    /// </summary>
    private async Task<ServiceSession> OpenSessionAsync(List<string> unreachable, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("D");
        var session = new ServiceSession(id, _time.GetUtcNow());

        await TryGetAsync(new Uri($"https://vlscppe.microsoft.com/tags?org_id={OrgId}&session_id={id}"), unreachable,
            cancellationToken).ConfigureAwait(false);

        var script = await TryGetAsync(
            new Uri($"https://ov-df.microsoft.com/mdt.js?instanceId={InstanceId}&PageId=si&session_id={id}"),
            unreachable,
            cancellationToken).ConfigureAwait(false);
        if (script is null) return session;

        var w = WRegex().Match(script);
        var rticks = RticksRegex().Match(script);
        if (!w.Success || !rticks.Success)
        {
            _logger.LogWarning(
                "The session script from ov-df.microsoft.com does not contain the expected values (w: {HasW}, rticks: {HasRticks}).",
                w.Success, rticks.Success);
            return session;
        }

        var mdt = _time.GetUtcNow().ToUnixTimeMilliseconds();
        await TryGetAsync(
            new Uri(
                $"https://ov-df.microsoft.com/?session_id={id}&CustomerId={InstanceId}&PageId=si&w={w.Groups[1].Value}&mdt={mdt.ToString(CultureInfo.InvariantCulture)}&rticks={rticks.Groups[1].Value}"),
            unreachable,
            cancellationToken).ConfigureAwait(false);
        return session;
    }

    private async Task<string?> TryGetAsync(Uri uri, List<string> unreachable, CancellationToken cancellationToken)
    {
        try
        {
            return await _transport.GetStringAsync(uri, HostAllowlist.Catalog, MaxResponseBytes, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TransportException ex) when (ex.Failure != TransportFailure.Certificate)
        {
            var host = HostAllowlist.DescribeHost(uri);
            _logger.LogWarning(
                "A session call to {Host} failed ({Failure}). Continuing; the next request will show whether it mattered.",
                host, ex.Failure);
            if (!unreachable.Contains(host)) unreachable.Add(host);

            return null;
        }
    }

    private async Task<List<WindowsLanguage>> GetLanguagesAsync(string editionId, ServiceSession session,
        List<string> unreachable, CancellationToken cancellationToken)
    {
        var uri = new Uri(
            $"{ConnectorBase}getskuinformationbyproductedition?profile={Profile}&productEditionId={editionId}" +
            $"&SKU=undefined&friendlyFileName=undefined&Locale=en-US&sessionID={session.Id}");
        using var json = await GetJsonAsync(uri, "the list of languages", unreachable, cancellationToken)
            .ConfigureAwait(false);
        ThrowIfServiceError(json.RootElement, unreachable);

        var languages = new List<WindowsLanguage>();
        if (TryGetProperty(json.RootElement, "Skus", out var skus) && skus.ValueKind == JsonValueKind.Array)
            foreach (var sku in skus.EnumerateArray())
            {
                if (sku.ValueKind != JsonValueKind.Object) continue;

                var id = Scalar(sku, "Id");
                var name = Clean(Scalar(sku, "Language"));
                var localized = Clean(Scalar(sku, "LocalizedLanguage"));
                if (id is null || !IsSafeId(id) || (name.Length == 0 && localized.Length == 0)) continue;

                if (languages.Exists(l => l.SkuId == id)) continue;

                var product = Clean(Scalar(sku, "ProductDisplayName"));
                languages.Add(new WindowsLanguage(id, name.Length != 0 ? name : localized,
                    localized.Length != 0 ? localized : name)
                {
                    ProductDisplayName = product.Length != 0 ? product : null
                });
            }

        if (languages.Count != 0) return languages;

        _logger.LogError("The language answer has no usable entries in \"Skus\".");
        throw Refused(
            "Microsoft's download service answered, but without a list of languages. " +
            DownloadMessages.ServiceChanged,
            unreachable: unreachable);
    }

    private async Task<DownloadTicket> RequestLinkAsync(
        WindowsLanguage language,
        ServiceSession session,
        List<string> unreachable,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(
            $"{ConnectorBase}GetProductDownloadLinksBySku?profile={Profile}&productEditionId=undefined" +
            $"&SKU={language.SkuId}&friendlyFileName=undefined&Locale=en-US&sessionID={session.Id}");
        using var json = await GetJsonAsync(uri, "the download link", unreachable, cancellationToken)
            .ConfigureAwait(false);
        var root = json.RootElement;
        ThrowIfServiceError(root, unreachable);

        // DownloadType 1 is the x64 ISO. Entries of any other type are ignored.
        const int wantedType = 1;
        Uri? link = null;
        string? refusedHost = null;
        string? refusedReason = null;
        var sawWantedType = false;
        if (TryGetProperty(root, "ProductDownloadOptions", out var options) && options.ValueKind == JsonValueKind.Array)
            foreach (var option in options.EnumerateArray())
            {
                if (option.ValueKind != JsonValueKind.Object ||
                    !int.TryParse(Scalar(option, "DownloadType"), NumberStyles.None, CultureInfo.InvariantCulture,
                        out var type) ||
                    type != wantedType)
                    continue;

                sawWantedType = true;
                var text = Scalar(option, "Uri");
                if (HostAllowlist.Iso.TryParse(text, out var candidate))
                {
                    link = candidate;
                    break;
                }

                // Remember why, without ever keeping the link itself.
                Uri.TryCreate(text, UriKind.Absolute, out var rejected);
                refusedHost = HostAllowlist.DescribeHost(rejected);
                refusedReason = HostAllowlist.Iso.Check(rejected) ?? "the address is not a plain web address";
            }

        if (link is null)
        {
            if (refusedHost is not null)
            {
                _logger.LogError("The service returned a link Plainstall does not accept (host {Host}): {Reason}.",
                    refusedHost, refusedReason);
                throw Refused(
                    $"Microsoft's download service returned a link that Plainstall does not trust ({refusedHost}): {refusedReason}. " +
                    "Plainstall only downloads Windows from Microsoft's own download servers, so it stopped.",
                    unreachable: unreachable);
            }

            _logger.LogError("The link answer has no usable x64 entry (entry of that type present: {Present}).",
                sawWantedType);
            throw Refused(
                $"Microsoft's download service did not offer a Windows 11 download for x64 in {language.DisplayName}. " +
                DownloadMessages.ServiceChanged,
                unreachable: unreachable);
        }

        var expires = ParseExpiry(Scalar(root, "DownloadExpirationDatetime"));
        var fileName = SafeIsoFileName(link, language.SkuId);
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Got a download link for {File} on {Host}, valid until {Expires}.", fileName,
                HostAllowlist.DescribeHost(link), expires);
        return new DownloadTicket(link, fileName, expires);
    }

    private async Task<JsonDocument> GetJsonAsync(Uri uri, string what, List<string> unreachable,
        CancellationToken cancellationToken)
    {
        string body;
        try
        {
            body = await _transport.GetStringAsync(uri, HostAllowlist.Catalog, MaxResponseBytes, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TransportException ex) when (ex is
                                            {
                                                Failure: TransportFailure.HttpStatus, Kind: ErrorKind.ServiceRefused
                                            })
        {
            throw Refused(
                $"Microsoft's download service refused to give out {what} (error {(int?)ex.StatusCode}). " +
                "Microsoft sometimes blocks downloads from certain networks, VPNs or regions. Try again later, or without a VPN.",
                ex,
                unreachable);
        }

        try
        {
            var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
            if (json.RootElement.ValueKind == JsonValueKind.Object) return json;

            json.Dispose();
            throw new JsonException("The top level is not an object.");
        }
        catch (JsonException ex)
        {
            _logger.LogError("The answer for {What} is not the expected JSON ({Length} characters).", what,
                body.Length);
            throw Refused(
                $"Microsoft's download service answered with something Plainstall cannot read when asked for {what}. " +
                "Microsoft may be blocking your network, or has changed the service.",
                ex,
                unreachable);
        }
    }

    private void ThrowIfServiceError(JsonElement root, List<string> unreachable)
    {
        if (!TryGetProperty(root, "Errors", out var errors) || errors.ValueKind != JsonValueKind.Array ||
            errors.GetArrayLength() == 0) return;

        var first = errors[0];
        var text = first.ValueKind == JsonValueKind.Object
            ? Clean(Scalar(first, "Value") ?? Scalar(first, "Message"), 300)
            : Clean(first.ValueKind == JsonValueKind.String ? first.GetString() : null, 300);
        var key = first.ValueKind == JsonValueKind.Object ? Clean(Scalar(first, "Key")) : string.Empty;
        _logger.LogError("Microsoft's download service refused the request. Key: {Key}. Text: {Text}.", key, text);

        var answer = text.Length == 0
            ? string.Empty
            : " Microsoft's answer was: \"" + text + "\"" + (text.EndsWith('.') ? string.Empty : ".");
        var message =
            "Microsoft's download service refused the request. Usually Microsoft does not allow downloads from your network " +
            "or region at the moment. This occurs most frequently with VPNs, proxies and company networks. " +
            "Try again later. If you use a VPN, disconnect it." + answer;

        throw new ServiceAnswerException(WithAdvice(message, unreachable), text.Length != 0 ? text : key);
    }

    private static PlainstallException Refused(string message, Exception? inner = null,
        List<string>? unreachable = null)
    {
        return new PlainstallException(ErrorKind.ServiceRefused, WithAdvice(message, unreachable), inner);
    }

    private static string WithAdvice(string message, List<string>? unreachable)
    {
        var text = new StringBuilder(message);
        if (unreachable is { Count: > 0 })
            text.Append(" Plainstall also could not reach ").Append(string.Join(" and ", unreachable))
                .Append(", which the service needs. An ad blocker or DNS filter on your network may be blocking it.");

        text.Append(' ').Append(DownloadMessages.OwnIsoOffer);
        return text.ToString();
    }

    /// <summary>Property lookup that forgives a change of letter case, which web services do make.</summary>
    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(name, out value)) return true;

            foreach (var property in element.EnumerateObject()
                         .Where(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>A string or number property as text. Null for anything else.</summary>
    private static string? Scalar(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    /// <summary>Text from the service is shown to people and written to logs: no control characters, no surprises in length.</summary>
    private static string Clean(string? text, int maxLength = 100)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var cleaned = new StringBuilder(Math.Min(text.Length, maxLength));
        foreach (var c in text.Trim().Take(maxLength)) cleaned.Append(char.IsControl(c) ? ' ' : c);

        return cleaned.ToString().Trim();
    }

    /// <summary>Ids go into a request address, so only plain characters are let through.</summary>
    private static bool IsSafeId(string? id)
    {
        return id is { Length: > 0 and <= 40 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');
    }

    internal static DateTimeOffset? ParseExpiry(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        text = text.Trim();
        var legacy = LegacyDateRegex().Match(text);
        if (!legacy.Success || !long.TryParse(legacy.Groups[1].Value, NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var ms))
            // No offset in the text means UTC: the service runs on UTC and a wrong guess of a few hours
            // would only make a refresh happen earlier or later, never break a download.
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : null;

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    ///     The file name from the link, made safe to use on disk. The name comes from a server answer,
    ///     so anything unusual in it is replaced; it can never contain a folder or leave the target folder.
    /// </summary>
    internal static string SafeIsoFileName(Uri link, string skuId)
    {
        string raw;
        try
        {
            raw = Uri.UnescapeDataString(link.Segments.Length != 0 ? link.Segments[^1] : string.Empty);
        }
        catch (UriFormatException)
        {
            raw = string.Empty;
        }

        var name = new StringBuilder(raw.Length);
        foreach (var c in raw) name.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');

        var stem = name.ToString();
        if (stem.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];

        stem = stem.Trim('.', '_');
        while (stem.Contains("..", StringComparison.Ordinal)) stem = stem.Replace("..", ".", StringComparison.Ordinal);

        if (stem.Length == 0 || ReservedNameRegex().IsMatch(stem)) stem = $"Windows11_x64_{skuId}";

        if (stem.Length > 120) stem = stem[..120];

        return stem + ".iso";
    }

    [GeneratedRegex(@"Build\s+(\d{4,6}(?:\.\d{1,6})?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex BuildRegex();

    // The script builds an address in pieces, for example: ...&PageId=si&w=8DF3F3CD656E26E";i+="&mdt="+r;i+="&rticks="+1794323463650;
    // "w" is a hex value inside a string. Short matches such as the statement "w=0" elsewhere in the script are not it.
    [GeneratedRegex("(?<![A-Za-z0-9_])w=([0-9A-Fa-f]{6,128})(?![0-9A-Za-z_])", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex WRegex();

    // "rticks" is a number appended to that string (rticks="+123), or written inside it (rticks=123, rticks="123").
    [GeneratedRegex("""rticks\s*=\s*["']?\s*\+?\s*["']?(\d{6,32})(?![0-9A-Za-z_])""", RegexOptions.CultureInvariant,
        2000)]
    private static partial Regex RticksRegex();

    [GeneratedRegex(@"^/Date\((-?\d{1,18})(?:[+-]\d{4})?\)/$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex LegacyDateRegex();

    [GeneratedRegex(@"^(?:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\..*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ReservedNameRegex();

    /// <summary>The service answered with its own error list. Kept apart so that one retry with a new session is possible.</summary>
    private sealed class ServiceAnswerException(string message, string serviceText)
        : PlainstallException(ErrorKind.ServiceRefused, message)
    {
        public string ServiceText { get; } = serviceText;
    }

    /// <summary>What Plainstall reads from the download page.</summary>
    internal sealed record ProductPage(string? EditionId, string? EditionName, PublishedHashTable Hashes)
    {
        public static ProductPage Parse(string html)
        {
            string? id = null;
            string? name = null;
            try
            {
                var text = HtmlText.StripComments(html);
                foreach (var (value, optionText) in HtmlText.SelectOptions(text, "product-edition"))
                {
                    // The first entry is a "Select Download" placeholder with an empty value.
                    if (value.Length is not (> 0 and <= 12) || !value.All(char.IsAsciiDigit)) continue;

                    id = value;
                    name = optionText.Length is > 0 and <= 200 ? optionText : null;
                    break;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                id = null;
            }

            return new ProductPage(id, name, PublishedHashTable.Parse(html));
        }
    }
}