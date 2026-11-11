using System.Buffers;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Plainstall.Core.Download;

/// <summary>
///     Settings of <see cref="ResumableDownloader" />. The defaults are meant for a 5 to 10 GB file on a home
///     connection.
/// </summary>
public sealed record ResumableDownloaderOptions
{
    /// <summary>Retry limits and waits for the requests themselves.</summary>
    public TransportOptions Transport { get; init; } = new();

    /// <summary>
    ///     How long the server may send nothing in the middle of the file before the attempt counts as
    ///     failed. Without this a dead connection can hang a download for ever.
    /// </summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>A link this close to its expiry is replaced before it is used.</summary>
    public TimeSpan ExpiryMargin { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Free space that must remain on the drive after the download.</summary>
    public long FreeSpaceReserveBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>
    ///     Returns the free bytes available in a folder, or null when that cannot be known (then the check
    ///     is skipped). Null uses the real drive. Tests set this.
    /// </summary>
    public Func<string, long?>? FreeSpaceProbe { get; init; }
}

/// <summary>
///     One download job. The link is not passed in directly but through a factory, because links expire:
///     when the server refuses a link, the downloader asks for a new one and continues the same file.
/// </summary>
public sealed record DownloadRequest(
    string DestinationPath,
    Func<CancellationToken, Task<DownloadTicket>> TicketFactory)
{
    /// <summary>A link the caller already has. Saves one round trip to the service; replaced through the factory when expired.</summary>
    public DownloadTicket? InitialTicket { get; init; }

    /// <summary>The SHA-256 the finished file must have. When it does not, the file is deleted and the download fails.</summary>
    public string? ExpectedSha256 { get; init; }

    /// <summary>Progress of the hash check at the end, which reads the whole file once more.</summary>
    public IProgress<TransferProgress>? VerifyProgress { get; init; }
}

/// <summary>A finished download. <see cref="HashChecked" /> is true when an expected hash was given and it matched.</summary>
public sealed record DownloadResult(string Path, string Sha256, long SizeBytes, bool HashChecked);

/// <summary>
///     Downloads one large file so that an interruption costs minutes, not hours. Data goes to
///     "name.part"; a small "name.part.json" next to it remembers which file on the server the part
///     belongs to. The signed link itself is never stored, so a new link can continue an old part.
///     The file only gets its real name after its SHA-256 has been computed (and compared, when the
///     expected value is known), and a file that fails that check is removed so it cannot be picked up again.
/// </summary>
public sealed class ResumableDownloader : IDisposable
{
    private const int MaxRestarts = 2;
    private const int BufferSize = 256 * 1024;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private readonly ILogger _logger;
    private readonly ResumableDownloaderOptions _options;
    private readonly TimeProvider _time;

    private readonly DownloadTransport _transport;

    /// <param name="handler">
    ///     The HTTP handler to send through, or null for Plainstall's own. An injected handler is not
    ///     disposed and must not follow redirects by itself.
    /// </param>
    /// <param name="timeProvider">The clock for stall detection and retry waits, or null for the system clock.</param>
    /// <param name="logger">Where the downloader logs what it does, or null for no log.</param>
    /// <param name="options">Stall timeout, free space and transport settings, or null for the defaults.</param>
    public ResumableDownloader(
        HttpMessageHandler? handler = null,
        TimeProvider? timeProvider = null,
        ILogger<ResumableDownloader>? logger = null,
        ResumableDownloaderOptions? options = null)
    {
        _options = options ?? new ResumableDownloaderOptions();
        if (_options.StallTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "The stall timeout must be positive.");

        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ResumableDownloader>.Instance;
        _transport = new DownloadTransport(handler, _time, _options.Transport, _logger);
    }

    /// <summary>Releases the connection pool when the downloader created its own handler.</summary>
    public void Dispose()
    {
        _transport.Dispose();
    }

    /// <summary>The name of the partial file for a destination.</summary>
    public static string PartialPathFor(string destinationPath)
    {
        return destinationPath + ".part";
    }

    /// <summary>The name of the small file that describes the partial file.</summary>
    public static string SidecarPathFor(string destinationPath)
    {
        return destinationPath + ".part.json";
    }

    /// <summary>
    ///     Downloads to <see cref="DownloadRequest.DestinationPath" />, continuing an earlier partial file
    ///     when the server confirms it is still the same file. A cancel or a network failure leaves the
    ///     partial file in place for the next call. An existing destination file is never overwritten.
    /// </summary>
    public async Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.TicketFactory);
        if (request.ExpectedSha256 is not null && !HashHex.IsSha256(request.ExpectedSha256))
            throw new ArgumentException("The expected SHA-256 must be exactly 64 hex characters.", nameof(request));

        var destination = Path.GetFullPath(request.DestinationPath);
        var folder = Path.GetDirectoryName(destination)
                     ?? throw new ArgumentException("The destination must be a file inside a folder.", nameof(request));
        var part = PartialPathFor(destination);
        var sidecar = SidecarPathFor(destination);

        ThrowIfDestinationExists(destination);
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PlainstallException(
                ErrorKind.Preflight,
                $"Plainstall cannot create the folder \"{folder}\" for the download: {ex.Message} Choose another folder. Then try again.",
                ex);
        }

        // "fresh" is the last ticket that came straight from the factory. Such a ticket is used even
        // when its expiry looks past, because then the PC clock is the more likely thing to be wrong.
        DownloadTicket? fresh = null;
        var ticket = request.InitialTicket ??
                     (fresh = await GetTicketAsync(request, cancellationToken).ConfigureAwait(false));

        var failures = 0;
        var timeouts = 0;
        var restarts = 0;
        var refreshedAfterRefusal = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(ticket, fresh) && ticket.IsExpired(_time.GetUtcNow(), _options.ExpiryMargin))
            {
                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation("The download link for {File} has expired. Asking for a new one.",
                        ticket.FileName);

                ticket = fresh = await GetTicketAsync(request, cancellationToken).ConfigureAwait(false);
            }

            var attempt = await AttemptAsync(ticket, part, sidecar, progress, cancellationToken).ConfigureAwait(false);
            switch (attempt.Outcome)
            {
                case Outcome.Completed:
                    return await FinishAsync(request, destination, part, sidecar, cancellationToken)
                        .ConfigureAwait(false);

                case Outcome.LinkRefused:
                    if (refreshedAfterRefusal)
                        throw new PlainstallException(
                            ErrorKind.ServiceRefused,
                            $"Microsoft's server ({ticket.Host}) refused the download, even with a new link (error 403). " +
                            "Microsoft sometimes blocks downloads from certain networks, VPNs or regions. Try again later or from another network. " +
                            KeptNote(part, sidecar) + DownloadMessages.OwnIsoOffer);

                    refreshedAfterRefusal = true;
                    if (_logger.IsEnabled(LogLevel.Information))
                        _logger.LogInformation(
                            "{Host} refused the download link (error 403). It has probably expired; asking for a new one.",
                            ticket.Host);

                    ticket = fresh = await GetTicketAsync(request, cancellationToken).ConfigureAwait(false);
                    break;

                case Outcome.Restart:
                    if (++restarts > MaxRestarts)
                        throw new PlainstallException(
                            ErrorKind.Network,
                            $"The file on {ticket.Host} kept changing while Plainstall was downloading it. Wait a few minutes. Then try again. " +
                            DownloadMessages.OwnIsoOffer);

                    if (_logger.IsEnabled(LogLevel.Information))
                        _logger.LogInformation(
                            "The partial download no longer matches the file on {Host}. Starting from the beginning.",
                            ticket.Host);

                    break;

                case Outcome.Retry:
                default:
                    var error = attempt.Error!;
                    if (attempt.MadeProgress)
                    {
                        // The connection is bad but the file is growing, and what arrived is safe on
                        // disk. Giving up now would only make the user press the button again.
                        failures = 0;
                        timeouts = 0;
                    }

                    failures++;
                    if (attempt.IsTimeout) timeouts++;

                    if (failures >= _options.Transport.MaxAttempts || timeouts >= _options.Transport.MaxTimeoutAttempts)
                        throw new PlainstallException(error.Kind, error.Message + KeptNote(part, sidecar, true), error);

                    var wait = _transport.RetryDelay(failures, error.RetryAfter);
                    if (_logger.IsEnabled(LogLevel.Information))
                        _logger.LogInformation(
                            "Download attempt failed ({Failure}) on {Host}; trying again in {Seconds:0.#} s.",
                            error.Failure, ticket.Host, wait.TotalSeconds);

                    await _transport.DelayAsync(wait, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    private static async Task<DownloadTicket> GetTicketAsync(DownloadRequest request,
        CancellationToken cancellationToken)
    {
        return await request.TicketFactory(cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidOperationException("The ticket factory returned no ticket.");
    }

    private async Task<DownloadResult> FinishAsync(DownloadRequest request, string destination, string part,
        string sidecar, CancellationToken cancellationToken)
    {
        var size = new FileInfo(part).Length;
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Download complete ({Size} bytes). Computing the SHA-256.", size);
        var sha256 = await HashHex.Sha256OfFileAsync(part, request.VerifyProgress, cancellationToken)
            .ConfigureAwait(false);

        if (request.ExpectedSha256 is { } expected && !HashHex.Equal(sha256, expected))
        {
            // Keeping the file would mean hashing the same bad bytes again on every later run.
            _logger.LogError("SHA-256 mismatch. Expected {Expected}, got {Actual}. Deleting the downloaded file.",
                expected.ToUpperInvariant(), sha256);
            DeleteQuietly(part);
            DeleteQuietly(sidecar);
            throw new PlainstallException(
                ErrorKind.Verification,
                "The downloaded file is different from the file that Microsoft publishes. Plainstall deleted it. " +
                "Start the download again. If the problem occurs again, your connection or a security program possibly changes the download. " +
                "Try a different network. " + DownloadMessages.OwnIsoOffer);
        }

        try
        {
            File.Move(part, destination, false);
        }
        catch (IOException) when (File.Exists(destination))
        {
            ThrowIfDestinationExists(destination);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PlainstallException(
                ErrorKind.Preflight,
                $"The download finished, but Plainstall could not give the file its final name in \"{Path.GetDirectoryName(destination)}\": {ex.Message} " +
                "The downloaded data is kept. Close programs that may be using the folder. Then try again.",
                ex);
        }

        DeleteQuietly(sidecar);
        return new DownloadResult(destination, sha256, size, request.ExpectedSha256 is not null);
    }

    private async Task<Attempt> AttemptAsync(
        DownloadTicket ticket,
        string part,
        string sidecar,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var resume = ReadResumePoint(ticket, part, sidecar);
        HttpResponseMessage response;
        try
        {
            response = await _transport.SendAsync(
                ticket.Uri,
                HostAllowlist.Iso,
                message =>
                {
                    if (resume is null) return;

                    // If-Range makes the server send the whole file when it has changed, so two
                    // different files can never be glued together.
                    message.Headers.Range = new RangeHeaderValue(resume.Offset, null);
                    message.Headers.IfRange = new RangeConditionHeaderValue(resume.ETag);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (TransportException ex) when (ex.IsRetryable)
        {
            return Attempt.Retry(ex, false);
        }

        using (response)
        {
            var host = HostAllowlist.DescribeHost(response.RequestMessage?.RequestUri ?? ticket.Uri);
            switch ((int)response.StatusCode)
            {
                case 200:
                    if (resume is not null && _logger.IsEnabled(LogLevel.Information))
                        _logger.LogInformation(
                            "{Host} sent the whole file instead of the missing part. Starting from the beginning.",
                            host);

                    return await ReceiveAsync(response, ticket, host, part, sidecar, null, progress, cancellationToken)
                        .ConfigureAwait(false);

                case 206 when resume is not null:
                    var range = response.Content.Headers.ContentRange;
                    var rangeFits = range is { HasRange: true } && range.From == resume.Offset &&
                                    (!range.HasLength || range.Length == resume.Total);
                    var sameFile = response.Headers.ETag is not { } tag || tag.Equals(resume.ETag);
                    if (!rangeFits || !sameFile)
                    {
                        _logger.LogWarning(
                            "{Host} answered the resume request with a part that does not fit (range ok: {Range}, same file: {Same}).",
                            host, rangeFits, sameFile);
                        DiscardPartial(part, sidecar);
                        return Attempt.Restart;
                    }

                    if (_logger.IsEnabled(LogLevel.Information))
                        _logger.LogInformation("Continuing the download at byte {Offset} of {Total}.", resume.Offset,
                            resume.Total);

                    return await ReceiveAsync(response, ticket, host, part, sidecar, resume, progress,
                        cancellationToken).ConfigureAwait(false);

                case 416 when resume is not null:
                    if (resume.Offset == resume.Total &&
                        response.Content.Headers.ContentRange is { HasLength: true } unsatisfied &&
                        unsatisfied.Length == resume.Total)
                    {
                        if (_logger.IsEnabled(LogLevel.Information))
                            _logger.LogInformation("The partial file is already complete ({Total} bytes).",
                                resume.Total);

                        progress?.Report(new TransferProgress(resume.Total, resume.Total));
                        return Attempt.Completed;
                    }

                    DiscardPartial(part, sidecar);
                    return Attempt.Restart;

                case 403:
                    return Attempt.LinkRefused;

                default:
                    var failure = (int)response.StatusCode is >= 200 and < 300
                        ? new TransportException(
                            TransportFailure.HttpStatus,
                            ErrorKind.ServiceRefused,
                            $"{host} answered in a way Plainstall does not understand (status {(int)response.StatusCode}). " +
                            DownloadMessages.OwnIsoOffer)
                        {
                            StatusCode = response.StatusCode,
                            Host = host
                        }
                        : _transport.StatusFailure(response, host);
                    return failure.IsRetryable ? Attempt.Retry(failure, false) : throw failure;
            }
        }
    }

    /// <summary>Writes the body to the partial file, from the start (<paramref name="resume" /> is null) or appending.</summary>
    private async Task<Attempt> ReceiveAsync(
        HttpResponseMessage response,
        DownloadTicket ticket,
        string host,
        string part,
        string sidecar,
        ResumePoint? resume,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var offset = resume?.Offset ?? 0;
        var total = resume?.Total ?? response.Content.Headers.ContentLength;
        var etag = resume?.ETag ?? response.Headers.ETag;
        var resumable = etag is { IsWeak: false } && total > 0;

        EnsureFreeSpace(part, offset, total);

        FileStream file;
        try
        {
            if (resume is null)
            {
                // Order matters for a crash at any point: no description, then an empty part, then
                // the new description. A description never outlives the bytes it describes.
                DeleteQuietly(sidecar);
                file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.Read, 1,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                try
                {
                    WriteSidecar(sidecar, new PartialInfo(UrlWithoutQuery(ticket.Uri), etag?.ToString(), total));
                }
                catch
                {
                    await file.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            else
            {
                file = new FileStream(part, FileMode.Open, FileAccess.Write, FileShare.Read, 1,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                file.Seek(offset, SeekOrigin.Begin);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw LocalFileFailure(part, ex);
        }

        var done = offset;
        long received = 0;
        TransportException? failure = null;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using (file.ConfigureAwait(false))
            {
                progress?.Report(new TransferProgress(done, total));
                var lastReport = _time.GetTimestamp();

                using var stall = new CancellationTokenSource(_options.StallTimeout, _time);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stall.Token);
                try
                {
                    var body = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
                    await using (body.ConfigureAwait(false))
                    {
                        while (true)
                        {
                            stall.CancelAfter(_options.StallTimeout);
                            var read = await body.ReadAsync(buffer.AsMemory(0, BufferSize), linked.Token)
                                .ConfigureAwait(false);
                            stall.CancelAfter(Timeout.InfiniteTimeSpan);
                            if (read == 0) break;

                            try
                            {
                                await file.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None)
                                    .ConfigureAwait(false);
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                                throw LocalFileFailure(part, ex);
                            }

                            done += read;
                            received += read;
                            if (_time.GetElapsedTime(lastReport) >= TimeSpan.FromMilliseconds(100))
                            {
                                progress?.Report(new TransferProgress(done, total));
                                lastReport = _time.GetTimestamp();
                            }

                            cancellationToken.ThrowIfCancellationRequested();
                        }
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException)
                {
                    // A read that is cut off can surface as an I/O error instead of a cancellation,
                    // so the tokens decide what happened, not the exception type.
                    if (cancellationToken.IsCancellationRequested)
                    {
                        if (_logger.IsEnabled(LogLevel.Information))
                            _logger.LogInformation("Download cancelled at byte {Done}. The partial file is kept.",
                                done);

                        throw new OperationCanceledException("The download was cancelled.", ex, cancellationToken);
                    }

                    failure = !TransportErrors.IsCertificateFailure(ex) &&
                              (ex is OperationCanceledException || stall.IsCancellationRequested)
                        ? new TransportException(
                            TransportFailure.Timeout,
                            ErrorKind.Network,
                            $"The download from {host} stopped: no data arrived for {_options.StallTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} seconds. " +
                            "Check your internet connection. Then try again.",
                            ex) { Host = host }
                        : DownloadTransport.ConnectionFailure(host, ex);
                }

                try
                {
                    await file.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw LocalFileFailure(part, ex);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        progress?.Report(new TransferProgress(done, total));
        var madeProgress = resumable && received > 0;
        if (failure is not null)
            return failure.Failure != TransportFailure.Certificate
                ? Attempt.Retry(failure, madeProgress)
                : throw failure;

        if (total is not { } expectedLength || done == expectedLength) return Attempt.Completed;

        if (done <= expectedLength)
            return Attempt.Retry(
                new TransportException(
                        TransportFailure.Connection,
                        ErrorKind.Network,
                        $"The connection to {host} ended before the whole file had arrived. Check your internet connection. Then try again.")
                    { Host = host },
                madeProgress);

        DiscardPartial(part, sidecar);
        return Attempt.Restart;
    }

    /// <summary>
    ///     Decides whether the partial file may be continued with this link. Every condition has to hold;
    ///     otherwise the download starts over, which is slow but always correct.
    /// </summary>
    private ResumePoint? ReadResumePoint(DownloadTicket ticket, string part, string sidecar)
    {
        long length;
        try
        {
            var info = new FileInfo(part);
            if (!info.Exists) return null;

            length = info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (length == 0) return null;

        var saved = ReadSidecar(sidecar);
        var reason = saved switch
        {
            null => "there is no readable description of it",
            _ when !string.Equals(saved.Url, UrlWithoutQuery(ticket.Uri), StringComparison.Ordinal) =>
                "it belongs to a different file on the server",
            { Length: null or <= 0 } => "the total size was not known",
            _ when length > saved.Length => "it is larger than the file on the server",
            _ => null
        };

        EntityTagHeaderValue? etag = null;
        if (reason is null && (!EntityTagHeaderValue.TryParse(saved!.ETag, out etag) || etag.IsWeak))
            // A weak tag only promises "about the same content". That is not enough to join bytes
            // from two requests into one image.
            reason = "the server gave no strong version tag for it";

        if (reason is null) return new ResumePoint(length, etag!, saved!.Length!.Value);

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "The partial file ({Length} bytes) cannot be continued: {Reason}. Starting from the beginning.", length,
                reason);

        return null;
    }

    private void EnsureFreeSpace(string part, long offset, long? total)
    {
        if (total is not { } size) return;

        var folder = Path.GetDirectoryName(part)!;
        var available = (_options.FreeSpaceProbe ?? DefaultFreeSpace)(folder);
        if (available is null) return;

        // What is already in the partial file is either kept (resume) or given back (restart).
        long onDisk = 0;
        try
        {
            var info = new FileInfo(part);
            onDisk = info.Exists ? info.Length : 0;
        }
        catch (IOException)
        {
        }

        var needed = offset > 0 ? size - offset : size - onDisk;
        if (needed + _options.FreeSpaceReserveBytes <= available.Value) return;

        var drive = Path.GetPathRoot(folder) is { Length: > 0 } root ? $"drive {root.TrimEnd('\\')}" : "the drive";
        throw new PlainstallException(
            ErrorKind.Preflight,
            $"There is not enough free space on {drive} for the Windows download. " +
            $"It needs about {FormatSize(needed + _options.FreeSpaceReserveBytes)} and only {FormatSize(available.Value)} are free. " +
            "Free up some space or choose another folder. Then try again.");
    }

    private static long? DefaultFreeSpace(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return null;

            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string FormatSize(long bytes)
    {
        const double gb = 1024d * 1024 * 1024;
        const double mb = 1024d * 1024;
        return bytes >= gb
            ? (bytes / gb).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
            : Math.Max(1, Math.Ceiling(bytes / mb)).ToString("0", CultureInfo.InvariantCulture) + " MB";
    }

    private static void ThrowIfDestinationExists(string destination)
    {
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new PlainstallException(
                ErrorKind.InvalidInput,
                $"A file named \"{Path.GetFileName(destination)}\" already exists in \"{Path.GetDirectoryName(destination)}\". " +
                "Plainstall does not overwrite files. Move or delete it, or choose another folder.");
    }

    private static PlainstallException LocalFileFailure(string part, Exception ex)
    {
        return new PlainstallException(
            ErrorKind.Preflight,
            $"Plainstall could not write the download to \"{Path.GetDirectoryName(part)}\": {ex.Message} " +
            "Check that the drive has free space and that no other program (or a second Plainstall) is using the file. Then try again.",
            ex);
    }

    /// <summary>A sentence for error messages that tells the user the next try will not start from zero, when that is true.</summary>
    private static string KeptNote(string part, string sidecar, bool leadingSpace = false)
    {
        try
        {
            var info = new FileInfo(part);
            if (info is { Exists: true, Length: > 0 } &&
                ReadSidecar(sidecar) is { Length: > 0 } saved &&
                EntityTagHeaderValue.TryParse(saved.ETag, out var tag) && !tag.IsWeak)
            {
                const string note =
                    "Plainstall keeps the part that it already downloaded. The next try continues from there.";
                return leadingSpace ? " " + note : note + " ";
            }
        }
        catch (IOException)
        {
        }

        return string.Empty;
    }

    private void DiscardPartial(string part, string sidecar)
    {
        DeleteQuietly(sidecar);
        DeleteQuietly(part);
    }

    private void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not delete {File}: {Reason}", Path.GetFileName(path), ex.Message);
        }
    }

    /// <summary>The address of the file without its signed query. This, and never the full link, is what gets stored.</summary>
    internal static string UrlWithoutQuery(Uri uri)
    {
        return uri.GetLeftPart(UriPartial.Path);
    }

    internal static void WriteSidecar(string path, PartialInfo info)
    {
        AtomicFile.WriteAllText(path, new JsonObject
        {
            ["version"] = 1,
            ["url"] = info.Url,
            ["etag"] = info.ETag,
            ["length"] = info.Length
        }.ToJsonString(Indented));
    }

    internal static PartialInfo? ReadSidecar(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > 64 * 1024) return null;

            using var json = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String)
                return null;

            var etag = root.TryGetProperty("etag", out var tag) && tag.ValueKind == JsonValueKind.String
                ? tag.GetString()
                : null;
            long? length =
                root.TryGetProperty("length", out var len) && len.ValueKind == JsonValueKind.Number &&
                len.TryGetInt64(out var value)
                    ? value
                    : null;
            return new PartialInfo(url.GetString()!, etag, length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The content of "name.part.json".</summary>
    internal sealed record PartialInfo(string Url, string? ETag, long? Length);

    private sealed record ResumePoint(long Offset, EntityTagHeaderValue ETag, long Total);

    private enum Outcome
    {
        Completed,
        Retry,
        Restart,
        LinkRefused
    }

    private sealed record Attempt(Outcome Outcome, TransportException? Error, bool MadeProgress)
    {
        public static readonly Attempt Completed = new(Outcome.Completed, null, false);
        public static readonly Attempt Restart = new(Outcome.Restart, null, false);
        public static readonly Attempt LinkRefused = new(Outcome.LinkRefused, null, false);

        public bool IsTimeout => Error?.IsTimeout == true;

        public static Attempt Retry(TransportException error, bool madeProgress)
        {
            return new Attempt(Outcome.Retry, error, madeProgress);
        }
    }
}