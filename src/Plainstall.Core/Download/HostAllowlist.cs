using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace Plainstall.Core.Download;

/// <summary>
///     The fixed list of Microsoft hosts Plainstall may talk to. Every request and every redirect hop
///     is checked here before it is sent, so a changed page, a hostile redirect or a typo in a link can
///     never make Plainstall fetch a Windows image from somewhere else.
/// </summary>
public sealed class HostAllowlist
{
    private readonly FrozenSet<string> _hosts;
    private readonly string? _requiredPathSuffix;

    private HostAllowlist(string? requiredPathSuffix, params string[] hosts)
    {
        _requiredPathSuffix = requiredPathSuffix;
        _hosts = hosts.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Hosts of the download page and of the calls that turn a language choice into a link.</summary>
    public static HostAllowlist Catalog { get; } = new(
        null,
        "www.microsoft.com",
        "vlscppe.microsoft.com",
        "ov-df.microsoft.com");

    /// <summary>Hosts that serve the ISO itself. A link must also point at a file ending in ".iso".</summary>
    public static HostAllowlist Iso { get; } = new(
        ".iso",
        "software.download.prss.microsoft.com",
        "software-download.microsoft.com",
        "download.microsoft.com");

    /// <summary>The allowed host names.</summary>
    public IReadOnlyCollection<string> Hosts => _hosts;

    /// <summary>True when the address may be requested.</summary>
    public bool IsAllowed([NotNullWhen(true)] Uri? uri)
    {
        return Check(uri) is null;
    }

    /// <summary>
    ///     Parses and checks a link that came from outside (a JSON field, a redirect header).
    ///     Returns false for anything that is not an absolute, allowed https address.
    /// </summary>
    public bool TryParse(string? text, [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4096) return false;

        // Control characters and backslashes have no place in a link Microsoft hands out. Browsers and
        // URI parsers disagree on what they mean, and that disagreement is how look-alike links work.
        if (text.Any(c => char.IsControl(c) || c == '\\')) return false;

        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var parsed) || !IsAllowed(parsed)) return false;

        uri = parsed;
        return true;
    }

    /// <summary>
    ///     Returns null when the address is allowed, otherwise a short reason. The reason never contains
    ///     the address itself, because download links carry signed tokens that must stay out of logs.
    /// </summary>
    public string? Check(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri) return "the address is not a complete web address";

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
            return "the address does not use https";

        if (!uri.IsDefaultPort) return "the address uses an unusual port";

        if (uri.UserInfo.Length != 0) return "the address contains a user name";

        if (uri.HostNameType != UriHostNameType.Dns) return "the address does not name a host";

        // IdnHost is what the HTTP stack connects to. Comparing it (and not the display form) means a
        // look-alike written with non-Latin letters shows up as "xn--..." and is refused. A trailing
        // dot is refused too: the name would resolve the same way, but it is not on the list as written.
        var host = uri.IdnHost;
        if (!_hosts.Contains(host)) return "the host is not one of the Microsoft download hosts";

        if (_requiredPathSuffix is not null &&
            !uri.AbsolutePath.EndsWith(_requiredPathSuffix, StringComparison.OrdinalIgnoreCase))
            return "the address does not point at an " + _requiredPathSuffix + " file";

        return null;
    }

    /// <summary>
    ///     The host of an address, for messages and logs. Never the path or the query: those can hold
    ///     session ids and signed tokens.
    /// </summary>
    public static string DescribeHost(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri) return "an unknown address";

        var host = uri.IdnHost;
        if (host.Length == 0) return "an unknown address";

        return host.Length <= 80 ? host : string.Concat(host.AsSpan(0, 80), "...");
    }
}