using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Plainstall.Core.Release;

/// <summary>
///     Asks the GitHub Releases API whether a newer Plainstall has been published.
/// </summary>
/// <remarks>
///     The request carries nothing about the user or the PC: no token, no identifier, only a generic
///     product name, which GitHub requires. Every failure is swallowed, because being offline, behind a
///     proxy or rate-limited is normal and must never look like an error to the person using the tool.
/// </remarks>
public sealed class GitHubUpdateChecker : IUpdateChecker, IDisposable
{
    public const string ApiHost = "api.github.com";
    public const string PageHost = "github.com";
    public const int MaxResponseBytes = 512 * 1024;
    public const int MaxRedirects = 3;
    private static readonly Uri Endpoint = new($"https://{ApiHost}/repos/annoyedmilk/Plainstall/releases/latest");

    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public GitHubUpdateChecker(HttpMessageHandler? handler = null, TimeSpan? timeout = null, ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        // Redirects are followed by hand, so that a redirect can never lead to another host.
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false },
            true)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(5)
        };
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    public async Task<UpdateInfo?> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        try
        {
            var json = await GetAsync(currentVersion, cancellationToken).ConfigureAwait(false);
            return json is null ? null : Parse(json, currentVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException
                                       or JsonException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("The update check did not complete: {Message}", ex.Message);
            return null;
        }
    }

    private async Task<string?> GetAsync(Version currentVersion, CancellationToken cancellationToken)
    {
        var uri = Endpoint;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Plainstall", currentVersion.ToString()));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                if (location is null) return null;
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (!IsAllowed(next, ApiHost))
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("The update check was redirected away from {Host} and was dropped.", ApiHost);
                    return null;
                }

                uri = next;
                continue;
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                // 404: no release published yet. 403 or 429: rate limit. Neither is the user's problem.
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("The update check was answered with HTTP {Status}.", (int)response.StatusCode);
                return null;
            }

            if (response.Content.Headers.ContentLength > MaxResponseBytes) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes) return null;
                buffer.Write(chunk, 0, read);
            }

            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }

        return null;
    }

    internal static UpdateInfo? Parse(string json, Version currentVersion)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (IsTrue(root, "draft") || IsTrue(root, "prerelease")) return null;
        if (!TryParseVersion(Text(root, "tag_name"), out var latest) ||
            Normalize(latest) <= Normalize(currentVersion)) return null;
        // The link is shown to the user, so it must really point at GitHub.
        if (!Uri.TryCreate(Text(root, "html_url"), UriKind.Absolute, out var page) ||
            !IsAllowed(page, PageHost)) return null;
        return new UpdateInfo(latest, page);
    }

    /// <summary>Reads "v1.2.3", "1.2" or "1.2.3-rc.1+build": the numeric part decides, the rest is ignored.</summary>
    internal static bool TryParseVersion(string? tag, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        var end = text.IndexOfAny(['-', '+', ' ']);
        if (end >= 0) text = text[..end];
        if (!text.Contains('.')) text += ".0";
        if (!Version.TryParse(text, out var parsed)) return false;
        version = parsed;
        return true;
    }

    /// <summary>Makes 1.2 and 1.2.0.0 compare as equal: missing parts count as zero.</summary>
    private static Version Normalize(Version v)
    {
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
    }

    private static bool IsAllowed(Uri uri, string host)
    {
        return uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
               && string.Equals(uri.IdnHost, host, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Text(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool IsTrue(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    }
}