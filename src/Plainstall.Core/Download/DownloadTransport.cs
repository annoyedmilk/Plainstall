using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Plainstall.Core.Download;

/// <summary>
///     How patient the download code is. The defaults follow what works against Microsoft's service in
///     the field: a few quick retries for errors that go away, none for errors that do not.
/// </summary>
public sealed record TransportOptions
{
    /// <summary>Requests per call, the first one included, before a passing error becomes final.</summary>
    public int MaxAttempts { get; init; } = 4;

    /// <summary>Timeouts are retried less often: a server that is silent twice is not coming back soon.</summary>
    public int MaxTimeoutAttempts { get; init; } = 2;

    /// <summary>Wait before the first retry. It doubles for each further retry (1, 2, 4 seconds).</summary>
    public TimeSpan FirstRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>A server may ask for a longer pause with Retry-After. It is honoured up to this long.</summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a server may take to start answering, and how long a small answer may take to arrive.</summary>
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Redirects followed per request. Every one of them is checked against the host list first.</summary>
    public int MaxRedirects { get; init; } = 5;

    /// <summary>Replaces the real wait between retries. Tests set this so that they do not sleep.</summary>
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }
}

/// <summary>What kind of thing went wrong on the wire. Decides whether a retry makes sense.</summary>
internal enum TransportFailure
{
    Connection,
    Timeout,
    Certificate,
    AddressRefused,
    RedirectRefused,
    TooManyRedirects,
    ResponseTooLarge,
    HttpStatus
}

/// <summary>A failed request, already phrased for the user and naming the host only.</summary>
internal sealed class TransportException : PlainstallException
{
    public TransportException(TransportFailure failure, ErrorKind kind, string message, Exception? inner = null)
        : base(kind, message, inner)
    {
        Failure = failure;
    }

    public TransportFailure Failure { get; }

    public HttpStatusCode? StatusCode { get; init; }

    public TimeSpan? RetryAfter { get; init; }

    public string Host { get; init; } = string.Empty;

    public bool IsTimeout => Failure == TransportFailure.Timeout || StatusCode == HttpStatusCode.RequestTimeout;

    public bool IsRetryable => Failure switch
    {
        TransportFailure.Connection or TransportFailure.Timeout => true,
        TransportFailure.HttpStatus => StatusCode is { } status && DownloadTransport.IsRetryableStatus(status),
        _ => false
    };
}

/// <summary>Texts shared by every error of this module, so the same advice is worded the same way everywhere.</summary>
internal static class DownloadMessages
{
    public const string OwnIsoOffer =
        "If the problem continues, use a browser to download the Windows 11 ISO from microsoft.com/software-download/windows11. " +
        "Then select \"Use my ISO\" in Plainstall.";

    public const string ServiceChanged =
        "Microsoft probably changed the download page or service. You may need a newer version of Plainstall.";
}

/// <summary>
///     Sends GET requests the way this module needs them sent: host list checked on every hop,
///     redirects followed by hand, one fixed Referer header, errors that name the host and nothing else.
/// </summary>
internal sealed class DownloadTransport : IDisposable
{
    public static readonly Uri Referer = new("https://www.microsoft.com/software-download/windows11");

    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public DownloadTransport(HttpMessageHandler? handler, TimeProvider timeProvider, TransportOptions options,
        ILogger logger)
    {
        if (handler is not null) RejectAutoRedirect(handler);

        _http = new HttpClient(handler ?? CreateDefaultHandler(), handler is null)
        {
            // Timeouts are applied per phase with the injected clock, not by HttpClient.
            Timeout = Timeout.InfiniteTimeSpan
        };
        TimeProvider = timeProvider;
        Options = options;
        _logger = logger;
    }

    public TimeProvider TimeProvider { get; }

    public TransportOptions Options { get; }

    public void Dispose()
    {
        _http.Dispose();
    }

    /// <summary>
    ///     The handler Plainstall uses when none is injected. Redirects are off, because every hop has
    ///     to pass the host list before it is requested. Compression is off, because byte ranges and
    ///     lengths of the ISO must mean what they say.
    /// </summary>
    public static HttpMessageHandler CreateDefaultHandler()
    {
        return new TlsFallbackHandler(CreateSocketsHandler(SslProtocols.None),
            () => CreateSocketsHandler(SslProtocols.Tls12));
    }

    private static SocketsHttpHandler CreateSocketsHandler(SslProtocols protocols)
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            SslOptions = new SslClientAuthenticationOptions { EnabledSslProtocols = protocols }
        };
    }

    public static bool IsRetryableStatus(HttpStatusCode status)
    {
        return (int)status is 408 or 429 or 500 or 502 or 503 or 504;
    }

    public static bool IsRedirect(HttpStatusCode status)
    {
        return (int)status is 301 or 302 or 303 or 307 or 308;
    }

    /// <summary>
    ///     One attempt. Follows redirects and returns the first response that is not a redirect, with only
    ///     its headers read. The caller owns the response. Throws <see cref="TransportException" /> for
    ///     everything except a cancel by the caller.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(
        Uri uri,
        HostAllowlist allowlist,
        Action<HttpRequestMessage>? configure,
        CancellationToken cancellationToken)
    {
        var current = uri;
        for (var hop = 0;; hop++)
        {
            var host = HostAllowlist.DescribeHost(current);
            if (allowlist.Check(current) is { } reason)
            {
                _logger.LogWarning("Refused to contact {Host}: {Reason}.", host, reason);
                throw hop == 0
                    ? new TransportException(
                        TransportFailure.AddressRefused,
                        ErrorKind.ServiceRefused,
                        $"Plainstall was given a download address it does not trust ({host}): {reason}. " +
                        "It only downloads from Microsoft's own servers, so it stopped. " +
                        DownloadMessages.OwnIsoOffer) { Host = host }
                    : new TransportException(
                        TransportFailure.RedirectRefused,
                        ErrorKind.ServiceRefused,
                        $"Microsoft's server sent Plainstall on to another address ({host}) that it does not trust: {reason}. " +
                        "Plainstall only downloads from Microsoft's own servers, so it stopped. " +
                        DownloadMessages.OwnIsoOffer) { Host = host };
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.Referrer = Referer;
            configure?.Invoke(request);

            HttpResponseMessage response;
            using (var timeout = new CancellationTokenSource(Options.ResponseTimeout, TimeProvider))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token))
            {
                try
                {
                    response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException)
                {
                    throw Translate(ex, host, cancellationToken, timeout.Token);
                }
            }

            if (!IsRedirect(response.StatusCode)) return response;

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
                throw new TransportException(
                        TransportFailure.RedirectRefused,
                        ErrorKind.ServiceRefused,
                        $"{host} sent Plainstall elsewhere without saying where. " + DownloadMessages.OwnIsoOffer)
                    { Host = host };

            if (hop >= Options.MaxRedirects)
                throw new TransportException(
                        TransportFailure.TooManyRedirects,
                        ErrorKind.ServiceRefused,
                        $"{host} kept sending Plainstall from one address to the next. " + DownloadMessages.OwnIsoOffer)
                    { Host = host };

            current = location.IsAbsoluteUri ? location : new Uri(current, location);
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("{Host} redirected to {Target}.", host, HostAllowlist.DescribeHost(current));
        }
    }

    /// <summary>
    ///     Fetches a small text answer (a page, a JSON document) with retries. The body is capped, so a
    ///     misbehaving server cannot fill memory. Throws <see cref="TransportException" /> when the final
    ///     answer is not a success.
    /// </summary>
    public async Task<string> GetStringAsync(Uri uri, HostAllowlist allowlist, int maxBytes,
        CancellationToken cancellationToken)
    {
        var failures = 0;
        var timeouts = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TransportException failure;
            try
            {
                using var response = await SendAsync(uri, allowlist, null, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return await ReadBodyAsync(response, maxBytes, HostAllowlist.DescribeHost(uri), cancellationToken)
                        .ConfigureAwait(false);

                failure = StatusFailure(response,
                    HostAllowlist.DescribeHost(response.RequestMessage?.RequestUri ?? uri));
            }
            catch (TransportException ex)
            {
                failure = ex;
            }

            if (!failure.IsRetryable) throw failure;

            failures++;
            if (failure.IsTimeout) timeouts++;

            if (failures >= Options.MaxAttempts || timeouts >= Options.MaxTimeoutAttempts) throw failure;

            var wait = RetryDelay(failures, failure.RetryAfter);
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Request to {Host} failed ({Failure}); trying again in {Seconds:0.#} s.",
                    failure.Host, failure.Failure, wait.TotalSeconds);
            await DelayAsync(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     The wait before retry number <paramref name="failures" /> (1-based), or what the server asked for, within
    ///     limits.
    /// </summary>
    public TimeSpan RetryDelay(int failures, TimeSpan? retryAfter)
    {
        if (retryAfter is { } asked)
        {
            if (asked < TimeSpan.Zero) asked = TimeSpan.Zero;

            return asked > Options.MaxRetryAfter ? Options.MaxRetryAfter : asked;
        }

        var doublings = Math.Clamp(failures - 1, 0, 10);
        return TimeSpan.FromTicks(Options.FirstRetryDelay.Ticks << doublings);
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (Options.Delay is { } custom) return custom(delay, cancellationToken);

        return delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, TimeProvider, cancellationToken);
    }

    public TransportException StatusFailure(HttpResponseMessage response, string host)
    {
        var status = response.StatusCode;
        var code = (int)status;
        TimeSpan? retryAfter = null;
        if (response.Headers.RetryAfter is { } header)
            retryAfter = header.Delta ?? (header.Date is { } date ? date - TimeProvider.GetUtcNow() : null);

        if (IsRetryableStatus(status))
            return new TransportException(
                TransportFailure.HttpStatus,
                ErrorKind.Network,
                $"{host} is busy or has a problem right now (error {code}). Wait a few minutes. Then try again. " +
                DownloadMessages.OwnIsoOffer)
            {
                StatusCode = status,
                RetryAfter = retryAfter,
                Host = host
            };

        return new TransportException(
            TransportFailure.HttpStatus,
            ErrorKind.ServiceRefused,
            $"{host} refused the request (error {code}). Microsoft sometimes blocks downloads from certain networks, VPNs or regions, " +
            "and sometimes changes its download page. Try again later or from another network. " +
            DownloadMessages.OwnIsoOffer)
        {
            StatusCode = status,
            Host = host
        };
    }

    /// <summary>
    ///     Decides what a failed send or read really was. A cancelled connection can surface as an I/O
    ///     error, so the tokens are asked first: the caller's cancel wins, then the timeout, and only
    ///     then is it a connection problem.
    /// </summary>
    public static Exception Translate(Exception ex, string host, CancellationToken caller, CancellationToken timeout)
    {
        if (caller.IsCancellationRequested)
            return new OperationCanceledException("The operation was cancelled.", ex, caller);

        // A rejected certificate stays a rejected certificate, whatever else happened at the same moment.
        if (!TransportErrors.IsCertificateFailure(ex) &&
            (ex is OperationCanceledException || timeout.IsCancellationRequested)) return TimeoutFailure(host, ex);

        return ConnectionFailure(host, ex);
    }

    public static TransportException TimeoutFailure(string host, Exception? inner)
    {
        return new TransportException(
            TransportFailure.Timeout,
            ErrorKind.Network,
            $"{host} did not answer in time. Check your internet connection. Then try again.",
            inner) { Host = host };
    }

    public static TransportException ConnectionFailure(string host, Exception ex)
    {
        if (TransportErrors.IsCertificateFailure(ex))
            return new TransportException(
                TransportFailure.Certificate,
                ErrorKind.Network,
                $"Plainstall cannot verify the secure connection to {host}, so it stopped. " +
                "Usually the date or time on this PC is wrong, or security software or a company network examines encrypted connections. " +
                "Make sure that the date and time are correct. Then try again. If possible, use a different network.",
                ex) { Host = host };

        if (ex is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError })
            return new TransportException(
                TransportFailure.Connection,
                ErrorKind.Network,
                $"Plainstall could not find {host}. Check your internet connection. " +
                "If other websites work, an ad blocker or DNS filter on your network may be blocking this Microsoft address.",
                ex) { Host = host };

        return new TransportException(
            TransportFailure.Connection,
            ErrorKind.Network,
            $"The connection to {host} failed or was interrupted. Check your internet connection. Then try again.",
            ex) { Host = host };
    }

    private async Task<string> ReadBodyAsync(HttpResponseMessage response, int maxBytes, string host,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes) throw TooLarge(host);

        using var timeout = new CancellationTokenSource(Options.ResponseTimeout, TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[16 * 1024];
                int read;
                while ((read = await stream.ReadAsync(chunk, linked.Token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > maxBytes) throw TooLarge(host);

                    buffer.Write(chunk, 0, read);
                }

                // UTF-8 is what the service sends. A reader also drops a byte order mark if there is one.
                buffer.Position = 0;
                using var reader = new StreamReader(buffer, Encoding.UTF8, true);
                return await reader.ReadToEndAsync(linked.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException)
        {
            throw Translate(ex, host, cancellationToken, timeout.Token);
        }
    }

    private static TransportException TooLarge(string host)
    {
        return new TransportException(
            TransportFailure.ResponseTooLarge,
            ErrorKind.ServiceRefused,
            $"{host} sent a much larger answer than expected, so Plainstall stopped reading it. " +
            "Microsoft may have changed its download page. " + DownloadMessages.OwnIsoOffer) { Host = host };
    }

    /// <summary>
    ///     A handler that follows redirects on its own would skip the host check on every hop after the
    ///     first. That is a security bug in the making, so it is refused at construction time.
    /// </summary>
    private static void RejectAutoRedirect(HttpMessageHandler handler)
    {
        for (var current = handler; current is not null;)
        {
            var follows = current switch
            {
                SocketsHttpHandler sockets => sockets.AllowAutoRedirect,
                HttpClientHandler client => client.AllowAutoRedirect,
                _ => false
            };
            if (follows)
                throw new ArgumentException(
                    "The HTTP handler must have AllowAutoRedirect = false. Plainstall follows redirects itself so that every hop is checked against the host list.",
                    nameof(handler));

            current = (current as DelegatingHandler)?.InnerHandler;
        }
    }
}

/// <summary>Reads what a low-level network exception means.</summary>
internal static class TransportErrors
{
    /// <summary>
    ///     True when the other side's certificate or the TLS negotiation was rejected. Never retried and
    ///     never answered with weaker settings: that is the case TLS exists for.
    /// </summary>
    public static bool IsCertificateFailure(Exception ex)
    {
        return Find<AuthenticationException>(ex) is not null;
    }

    /// <summary>
    ///     True when the connection was cut while TLS was still being set up, with no verdict on the
    ///     certificate. Some inspecting firewalls do this to a TLS 1.3 hello and accept TLS 1.2.
    /// </summary>
    public static bool IsHandshakeReset(Exception ex)
    {
        if (ex is not HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError }) return false;

        if (IsCertificateFailure(ex)) return false;

        return Find<SocketException>(ex) is not null || Find<IOException>(ex) is not null;
    }

    private static T? Find<T>(Exception? ex)
        where T : Exception
    {
        for (var depth = 0; ex is not null && depth < 16; ex = ex.InnerException, depth++)
            if (ex is T match)
                return match;

        return null;
    }
}

/// <summary>
///     Sends through the normal handler until a connection is cut during the TLS handshake, then
///     switches to a TLS 1.2 only handler for good. Certificate checks are the same on both, and a
///     rejected certificate never triggers the switch.
/// </summary>
internal sealed class TlsFallbackHandler : HttpMessageHandler
{
    private readonly Func<HttpMessageHandler> _fallbackFactory;
    private readonly Lock _gate = new();
    private readonly HttpMessageInvoker _primary;
    private HttpMessageInvoker? _fallback;

    public TlsFallbackHandler(HttpMessageHandler primary, Func<HttpMessageHandler> fallbackFactory)
    {
        _primary = new HttpMessageInvoker(primary, true);
        _fallbackFactory = fallbackFactory;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var fallback = Volatile.Read(ref _fallback);
        if (fallback is not null) return await fallback.SendAsync(request, cancellationToken).ConfigureAwait(false);

        try
        {
            return await _primary.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (TransportErrors.IsHandshakeReset(ex))
        {
            return await GetOrCreateFallback().SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _primary.Dispose();
            Volatile.Read(ref _fallback)?.Dispose();
        }

        base.Dispose(disposing);
    }

    private HttpMessageInvoker GetOrCreateFallback()
    {
        lock (_gate)
        {
            return _fallback ??= new HttpMessageInvoker(_fallbackFactory(), true);
        }
    }
}