using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Plainstall.Core.Output;

public sealed record OscdimgLocatorOptions
{
    /// <summary>Plainstall's tools folder. A downloaded copy is kept here; the folder is created when needed.</summary>
    public required string ToolsFolder { get; init; }

    /// <summary>
    ///     The ADK's "Deployment Tools" folder. Null means the standard place under
    ///     <c>%ProgramFiles(x86)%\Windows Kits\10</c>. Tests point this at an empty folder so that the
    ///     result does not depend on what is installed on the PC.
    /// </summary>
    public string? AdkDeploymentToolsFolder { get; init; }

    /// <summary>False forbids the download; locating then fails when neither the ADK nor the cache has the tool.</summary>
    public bool AllowDownload { get; init; } = true;

    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    ///     The SHA-256 a downloaded or cached copy must have. Always <see cref="OscdimgLocator.PinnedSha256" />
    ///     in the product; internal so that only tests can change it, because no test file can have the real hash.
    /// </summary>
    internal string ExpectedSha256 { get; init; } = OscdimgLocator.PinnedSha256;
}

/// <summary>
///     Finds oscdimg.exe, Microsoft's tool for writing bootable ISO files, or downloads it from
///     Microsoft's symbol server. Windows does not include the tool and its license does not allow
///     shipping it with Plainstall, so it is fetched on first use.
/// </summary>
/// <remarks>
///     Trust rules:
///     <list type="bullet">
///         <item>
///             A file whose SHA-256 equals <see cref="PinnedSha256" /> is accepted. The symbol-server copy
///             has no signature inside it, so for that file the pinned hash is the only possible check.
///         </item>
///         <item>
///             Any other file (the ADK copy, which changes with every ADK release) must carry a valid
///             signature by Microsoft, embedded or through a Windows security catalog.
///         </item>
///         <item>
///             Nothing is trusted because of where it lies. Call <see cref="LocateAsync" /> right before
///             every use: the file is hashed again each time.
///         </item>
///     </list>
/// </remarks>
public sealed class OscdimgLocator
{
    public const string FileName = "oscdimg.exe";

    /// <summary>The symbol-server address of oscdimg 2.56, file version 10.0.26100.</summary>
    public const string DownloadUrl =
        "https://msdl.microsoft.com/download/symbols/oscdimg.exe/688CABB065000/oscdimg.exe";

    /// <summary>SHA-256 of the file behind <see cref="DownloadUrl" /> (143,360 bytes).</summary>
    public const string PinnedSha256 = "2000160B2C5044691B2F9A0AC308E5207F273D4880A572457AF16D05886BA861";

    /// <summary>The real file is 140 KB. Anything larger is not the file we asked for and is not read to the end.</summary>
    public const int MaxDownloadBytes = 1024 * 1024;

    public const int MaxRedirects = 5;

    /// <summary>
    ///     The client for the system's transport, created on first use and shared for the life of the process,
    ///     so repeated downloads reuse connections instead of opening new sockets each time. It never follows
    ///     redirects by itself and keeps no cookies; pooled connections are renewed so DNS changes are seen.
    /// </summary>
    private static readonly Lazy<HttpClient> SystemClient = new(() => CreateClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(30),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }, true));

    private readonly Lazy<HttpClient> _client;
    private readonly ILogger _logger;

    private readonly OscdimgLocatorOptions _options;
    private readonly IFileSignatureVerifier _signatureVerifier;

    /// <param name="options">Where the tools folder and the ADK are, and whether a download is allowed.</param>
    /// <param name="handler">
    ///     HTTP transport for the download; null uses the system's. An injected handler must not follow
    ///     redirects by itself, because every hop has to pass the host check here. It stays owned by the
    ///     caller and is never disposed here.
    /// </param>
    /// <param name="logger">Where the locator logs what it does, or null for no log.</param>
    /// <param name="signatureVerifier">Signature check for the ADK copy; null uses <see cref="MicrosoftSignatureVerifier" />.</param>
    public OscdimgLocator(OscdimgLocatorOptions options, HttpMessageHandler? handler = null, ILogger? logger = null,
        IFileSignatureVerifier? signatureVerifier = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(options.ToolsFolder, nameof(options));
        _options = options;
        _client = handler is null ? SystemClient : new Lazy<HttpClient>(() => CreateClient(handler, false));
        _logger = logger ?? NullLogger.Instance;
        _signatureVerifier = signatureVerifier ?? new MicrosoftSignatureVerifier();
    }

    /// <summary>
    ///     The only hosts this download may touch: the symbol server and the storage host it redirects
    ///     to. Checked on the first request and again on every redirect.
    /// </summary>
    public static IReadOnlyList<string> AllowedHosts { get; } =
    [
        "msdl.microsoft.com",
        "vsblobprodscussu5shard61.blob.core.windows.net"
    ];

    /// <summary>The ADK's Deployment Tools folder at its standard install location.</summary>
    public static string DefaultAdkDeploymentToolsFolder
    {
        get
        {
            var programFiles = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            if (string.IsNullOrEmpty(programFiles))
                programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            return Path.Combine(programFiles, "Windows Kits", "10", "Assessment and Deployment Kit",
                "Deployment Tools");
        }
    }

    /// <summary>Path of the cached copy inside the tools folder, whether or not it exists.</summary>
    public string CachedPath => Path.Combine(_options.ToolsFolder, FileName);

    /// <summary>
    ///     No overall timeout on the client: the download has its own, see
    ///     <see cref="OscdimgLocatorOptions.DownloadTimeout" />.
    /// </summary>
    private static HttpClient CreateClient(HttpMessageHandler handler, bool disposeHandler)
    {
        return new HttpClient(handler, disposeHandler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>
    ///     Returns the path of a trusted oscdimg.exe: the ADK's when installed, else the cached copy, else a
    ///     fresh download.
    /// </summary>
    /// <exception cref="PlainstallException">No trusted copy could be found or obtained.</exception>
    public async Task<string> LocateAsync(CancellationToken cancellationToken = default)
    {
        var adk = Path.Combine(_options.AdkDeploymentToolsFolder ?? DefaultAdkDeploymentToolsFolder, "amd64",
            "Oscdimg", FileName);
        if (File.Exists(adk) && await TryHashAsync(adk, cancellationToken).ConfigureAwait(false) is { } adkHash)
        {
            if (HashHex.Equal(adkHash, _options.ExpectedSha256))
            {
                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation("Using oscdimg from the Windows ADK at {Path} (known file)", adk);
                return adk;
            }

            var signature = _signatureVerifier.Verify(adk);
            if (signature.IsTrusted)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation("Using oscdimg from the Windows ADK at {Path}. {Detail}", adk,
                        signature.Detail);
                return adk;
            }

            _logger.LogWarning("Ignoring {Path}: it is not signed by Microsoft. {Detail}", adk, signature.Detail);
        }

        var cached = CachedPath;
        if (File.Exists(cached))
        {
            var hash = await TryHashAsync(cached, cancellationToken).ConfigureAwait(false);
            if (HashHex.Equal(hash, _options.ExpectedSha256))
            {
                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation("Using the cached oscdimg at {Path}", cached);
                return cached;
            }

            _logger.LogWarning("The cached oscdimg at {Path} is not the expected file (SHA-256 {Hash}); discarding it",
                cached, hash ?? "unreadable");
            TryDelete(cached);
        }

        if (!_options.AllowDownload)
            throw new PlainstallException(ErrorKind.Preflight,
                "The tool oscdimg.exe, which writes ISO files, was not found on this PC and downloading it is switched off. " +
                "Install the Deployment Tools of the Windows ADK from Microsoft, or allow the download.");

        var bytes = await DownloadAsync(cancellationToken).ConfigureAwait(false);

        // The check happens on the bytes in memory: an unverified program never reaches the disk.
        var downloadedHash = HashHex.Sha256Of(bytes);
        if (!HashHex.Equal(downloadedHash, _options.ExpectedSha256))
            throw new PlainstallException(ErrorKind.Verification,
                "The file oscdimg.exe that Microsoft's server delivered is not the file Plainstall expects " +
                $"(SHA-256 {downloadedHash}, expected {_options.ExpectedSha256}). It was not saved. " +
                "Try again later, or install the Deployment Tools of the Windows ADK from Microsoft.");

        await StoreAsync(bytes, cached, cancellationToken).ConfigureAwait(false);
        var storedHash = await TryHashAsync(cached, cancellationToken).ConfigureAwait(false);
        if (!HashHex.Equal(storedHash, _options.ExpectedSha256))
        {
            TryDelete(cached);
            throw new PlainstallException(ErrorKind.Verification,
                $"oscdimg.exe was downloaded correctly, but the copy saved at '{cached}' does not match it. " +
                "Another program may have changed the file. Check the disk and your security software. Then try again.");
        }

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Downloaded oscdimg from Microsoft to {Path}", cached);
        return cached;
    }

    private async Task<byte[]> DownloadAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.DownloadTimeout);

        var client = _client.Value;

        try
        {
            var uri = new Uri(DownloadUrl);
            for (var redirects = 0;; redirects++)
            {
                EnsureAllowed(uri);
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

                if (IsRedirect(response.StatusCode))
                {
                    if (redirects >= MaxRedirects)
                        throw new PlainstallException(ErrorKind.Network,
                            $"The download of oscdimg.exe was redirected more than {MaxRedirects} times and was stopped. Try again later.");
                    var location = response.Headers.Location
                                   ?? throw new PlainstallException(ErrorKind.Network,
                                       "Microsoft's server redirected the download of oscdimg.exe without saying where to. Try again later.");
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    // Signed storage links carry their secret in the query string, so only the host is logged.
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("oscdimg download redirected to host {Host}",
                            uri.IsAbsoluteUri ? uri.Host : "?");
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    throw new PlainstallException(ErrorKind.Network,
                        $"Microsoft's server answered the download of oscdimg.exe with HTTP {(int)response.StatusCode}. " +
                        "Try again later, or install the Deployment Tools of the Windows ADK from Microsoft.");

                if (response.Content.Headers.ContentLength is > MaxDownloadBytes) throw TooLarge();
                return await ReadCappedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PlainstallException(ErrorKind.Network,
                $"The download of oscdimg.exe from Microsoft did not finish within {_options.DownloadTimeout.TotalSeconds:0} seconds. " +
                "Check the internet connection. Then try again.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new PlainstallException(ErrorKind.Network,
                $"Could not download oscdimg.exe from Microsoft: {ex.Message} " +
                "Check the internet connection. Then try again. You can also install the Deployment Tools of the Windows ADK from Microsoft.",
                ex);
        }
    }

    private static async Task<byte[]> ReadCappedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        // The Content-Length header can be absent or wrong, so the limit is enforced while reading.
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxDownloadBytes) throw TooLarge();
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static PlainstallException TooLarge()
    {
        return new PlainstallException(ErrorKind.Verification,
            $"The server sent more than {MaxDownloadBytes / 1024} KB for oscdimg.exe, which is not the file Plainstall expects. Nothing was saved. " +
            "Try again later, or install the Deployment Tools of the Windows ADK from Microsoft.");
    }

    private static bool IsRedirect(HttpStatusCode status)
    {
        return status is
            HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
            HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
    }

    /// <summary>HTTPS, standard port, no user name, and a host on the list. Anything else ends the download.</summary>
    internal static void EnsureAllowed(Uri uri)
    {
        var allowed = uri.IsAbsoluteUri
                      && uri.Scheme == Uri.UriSchemeHttps
                      && uri is { IsDefaultPort: true, UserInfo.Length: 0 }
                      && AllowedHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase);
        if (allowed) return;
        var where = uri.IsAbsoluteUri ? $"{uri.Scheme}://{uri.Host}" : "an address that is not valid";
        throw new PlainstallException(ErrorKind.Verification,
            $"The download of oscdimg.exe was sent to {where}, which is not one of the Microsoft servers Plainstall accepts. " +
            "The download was stopped and nothing was saved. If this keeps happening, your network may be redirecting downloads. " +
            "In that case, install the Deployment Tools of the Windows ADK from Microsoft instead.");
    }

    private async Task StoreAsync(byte[] bytes, string destination, CancellationToken cancellationToken)
    {
        var folder = _options.ToolsFolder;
        var temporary = Path.Combine(folder, $"oscdimg.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(folder);
            // Written under a temporary name and renamed, so a half-written file is never found under the real name.
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destination, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            throw new PlainstallException(ErrorKind.Output,
                $"oscdimg.exe was downloaded but could not be saved to '{folder}': {ex.Message} " +
                "Check that the folder can be written to and that the disk is not full.", ex);
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporary);
            throw;
        }
    }

    private async Task<string?> TryHashAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await HashHex.Sha256OfFileAsync(path, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not read {Path}: {Message}", path, ex.Message);
            return null;
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not delete {Path}: {Message}", path, ex.Message);
        }
    }
}