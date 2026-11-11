using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Plainstall.Core.Download;

namespace Plainstall.Core.Tests.Download;

public sealed class ResumableDownloaderTests : IDisposable
{
    private readonly RecordedDelays _delays = new();
    private readonly SyncProgress<TransferProgress> _progress = new();
    private readonly FakeIsoServer _server = new();
    private readonly TempDirectory _temp = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 10, 14, 0, 0, TimeSpan.Zero));
    private int _ticketsIssued;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Destination => _temp.File("Windows11_Test_x64_en-us.iso");

    private string Part => ResumableDownloader.PartialPathFor(Destination);

    private string Sidecar => ResumableDownloader.SidecarPathFor(Destination);

    public void Dispose()
    {
        _temp.Dispose();
    }

    private ResumableDownloader CreateDownloader(ResumableDownloaderOptions? options = null)
    {
        return new ResumableDownloader(_server.Handler, _time, null, options ?? Options());
    }

    private ResumableDownloaderOptions Options()
    {
        return new ResumableDownloaderOptions { Transport = new TransportOptions { Delay = _delays.Wait } };
    }

    private DownloadRequest Request(Func<DownloadTicket>? ticket = null, string? expected = null)
    {
        return new DownloadRequest(Destination, _ =>
        {
            _ticketsIssued++;
            return Task.FromResult((ticket ?? (() => _server.Ticket()))());
        })
        {
            ExpectedSha256 = expected
        };
    }

    /// <summary>Downloads with the expected hash of the server's file and checks that the result is that file.</summary>
    private async Task DownloadVerifiedAsync(ResumableDownloaderOptions? options = null,
        IProgress<TransferProgress>? progress = null)
    {
        using var downloader = CreateDownloader(options);
        AssertDownloaded(await downloader.DownloadAsync(Request(expected: _server.Sha256), progress, Ct));
    }

    /// <summary>Puts a partial file and its description in place, as an interrupted earlier run would have left them.</summary>
    private void SeedPartial(int bytes, string? etag = "\"v1\"", long? length = null, string? url = null,
        byte[]? content = null)
    {
        File.WriteAllBytes(Part, (content ?? _server.Content)[..bytes]);
        ResumableDownloader.WriteSidecar(Sidecar, new ResumableDownloader.PartialInfo(
            url ?? $"https://{FakeIsoServer.Host}{FakeIsoServer.FilePath}",
            etag,
            length ?? _server.Content.Length));
    }

    private void AssertDownloaded(DownloadResult result)
    {
        Assert.Equal(Destination, result.Path);
        var written = File.ReadAllBytes(result.Path);
        Assert.Equal(_server.Sha256, result.Sha256);
        Assert.Equal(_server.Content.Length, result.SizeBytes);
        Assert.Equal(_server.Content, written);
        Assert.False(File.Exists(Part), "the partial file should be gone");
        Assert.False(File.Exists(Sidecar), "the description of the partial file should be gone");
    }

    private static void AssertNoSecrets(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            Assert.DoesNotContain("SECRETSIGNATURE", e.Message);
            Assert.DoesNotContain("sig=", e.Message);
            Assert.DoesNotContain("t=good", e.Message);
        }
    }

    // ---- the plain cases ----

    [Fact]
    public async Task Fresh_download_ends_as_the_destination_file_with_its_hash()
    {
        using var downloader = CreateDownloader();

        var result =
            await downloader.DownloadAsync(Request(expected: _server.Sha256.ToLowerInvariant()), _progress, Ct);

        AssertDownloaded(result);
        Assert.True(result.HashChecked);
        var request = Assert.Single(_server.Requests);
        Assert.Null(request.RangeFrom);
        Assert.Null(request.IfRange);
        Assert.Equal(new Uri("https://www.microsoft.com/software-download/windows11"), request.Referer);
        Assert.Equal(1, request.HeaderCount);
        Assert.Equal(1, _ticketsIssued);

        Assert.Equal(new TransferProgress(0, _server.Content.Length), _progress.Values[0]);
        Assert.Equal(new TransferProgress(_server.Content.Length, _server.Content.Length), _progress.Values[^1]);
        Assert.True(_progress.Values.Zip(_progress.Values.Skip(1)).All(p => p.First.BytesDone <= p.Second.BytesDone));
    }

    [Fact]
    public async Task Without_an_expected_hash_the_file_is_kept_and_reported_as_unchecked()
    {
        using var downloader = CreateDownloader();

        var result = await downloader.DownloadAsync(Request(), null, Ct);

        AssertDownloaded(result);
        Assert.False(result.HashChecked);
    }

    [Fact]
    public async Task An_initial_ticket_saves_the_call_to_the_factory()
    {
        using var downloader = CreateDownloader();

        var result = await downloader.DownloadAsync(Request() with { InitialTicket = _server.Ticket() }, null, Ct);

        AssertDownloaded(result);
        Assert.Equal(0, _ticketsIssued);
    }

    [Fact]
    public async Task The_destination_folder_is_created()
    {
        using var downloader = CreateDownloader();
        var nested = Path.Combine(_temp.Path, "a", "b", "file.iso");

        var result = await downloader.DownloadAsync(new DownloadRequest(nested, _ => Task.FromResult(_server.Ticket())),
            null, Ct);

        Assert.Equal(_server.Content, await File.ReadAllBytesAsync(result.Path, Ct));
    }

    [Fact]
    public async Task An_existing_destination_is_never_overwritten()
    {
        await File.WriteAllTextAsync(Destination, "precious", Ct);
        using var downloader = CreateDownloader();

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() => downloader.DownloadAsync(Request(), null, Ct));

        Assert.Equal(ErrorKind.InvalidInput, ex.Kind);
        Assert.Contains("already exists", ex.Message);
        Assert.Equal("precious", await File.ReadAllTextAsync(Destination, Ct));
        Assert.Empty(_server.Requests);
        Assert.Equal(0, _ticketsIssued);
    }

    [Fact]
    public async Task A_destination_that_appears_during_the_download_is_not_overwritten_either()
    {
        _server.Body = (_, bytes) =>
        {
            File.WriteAllText(Destination, "precious");
            return new MemoryStream(bytes);
        };
        using var downloader = CreateDownloader();

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() => downloader.DownloadAsync(Request(), null, Ct));

        Assert.Equal(ErrorKind.InvalidInput, ex.Kind);
        Assert.Equal("precious", await File.ReadAllTextAsync(Destination, Ct));
        Assert.True(File.Exists(Part)); // the download itself is not thrown away
    }

    // ---- resume ----

    [Fact]
    public async Task Resume_with_206_sends_range_and_if_range_and_appends()
    {
        SeedPartial(50_000);

        await DownloadVerifiedAsync(progress: _progress);

        var request = Assert.Single(_server.Requests);
        Assert.Equal(50_000, request.RangeFrom);
        Assert.Equal("\"v1\"", request.IfRange);
        Assert.Equal(new TransferProgress(50_000, _server.Content.Length), _progress.Values[0]);
        Assert.Equal(new TransferProgress(_server.Content.Length, _server.Content.Length), _progress.Values[^1]);
    }

    [Fact]
    public async Task A_new_link_with_another_token_continues_the_same_partial_file()
    {
        // The description stores the address without its query, so the token may differ.
        SeedPartial(50_000);
        _server.ValidToken = "second-link";

        await DownloadVerifiedAsync();

        Assert.Equal(50_000, Assert.Single(_server.Requests).RangeFrom);
    }

    [Fact]
    public async Task When_the_server_ignores_the_range_and_sends_200_the_download_starts_over()
    {
        // The seeded bytes are wrong on purpose: if they survived, the hash would not match.
        SeedPartial(50_000, content: new byte[60_000]);
        _server.HonorRange = false;

        await DownloadVerifiedAsync(progress: _progress);

        Assert.Equal(50_000, Assert.Single(_server.Requests).RangeFrom);
        Assert.Contains(new TransferProgress(0, _server.Content.Length), _progress.Values);
    }

    [Fact]
    public async Task A_416_with_the_matching_total_means_the_partial_file_is_already_complete()
    {
        SeedPartial(_server.Content.Length);

        await DownloadVerifiedAsync(progress: _progress);

        var request = Assert.Single(_server.Requests);
        Assert.Equal(_server.Content.Length, request.RangeFrom);
        Assert.Equal(new TransferProgress(_server.Content.Length, _server.Content.Length), _progress.Values[^1]);
    }

    [Fact]
    public async Task A_416_with_another_total_starts_over()
    {
        // The description claims a total that the server does not confirm.
        SeedPartial(50_000, length: 50_000);
        _server.Intercept = (number, _) =>
        {
            if (number != 0) return null;

            var response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
                { Content = new ByteArrayContent([]) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(_server.Content.Length);
            return response;
        };

        await DownloadVerifiedAsync();

        Assert.Equal(2, _server.Requests.Count);
        Assert.Null(_server.Requests[1].RangeFrom);
    }

    [Theory]
    [InlineData("W/\"v1\"")] // weak
    [InlineData(null)] // none
    [InlineData("v1")] // not a valid tag
    public async Task Without_a_strong_etag_the_partial_file_is_not_continued(string? savedETag)
    {
        SeedPartial(50_000, savedETag);
        _server.ETag = savedETag;

        await DownloadVerifiedAsync();

        var request = Assert.Single(_server.Requests);
        Assert.Null(request.RangeFrom);
        Assert.Null(request.IfRange);
    }

    [Fact]
    public async Task A_changed_etag_makes_the_server_send_the_whole_file_and_the_download_starts_over()
    {
        SeedPartial(50_000, "\"old-build\"", content: new byte[60_000]);

        await DownloadVerifiedAsync();

        var request = Assert.Single(_server.Requests);
        Assert.Equal("\"old-build\"", request.IfRange); // If-Range did its job: the answer was 200, not 206
    }

    [Theory]
    [InlineData("\"old-build\"", 50_000, "\"v1\"")] // a server that ignores If-Range for a file that has changed
    [InlineData("\"v1\"", 40_000, null)] // a part that starts at the wrong byte
    public async Task A_206_that_does_not_fit_the_partial_file_is_not_appended(string savedETag, int from,
        string? answerETag)
    {
        SeedPartial(50_000, savedETag, content: new byte[60_000]);
        _server.Intercept = (number, _) =>
        {
            if (number != 0) return null;

            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                { Content = new ByteArrayContent(_server.Content[from..]) };
            response.Content.Headers.ContentRange =
                new ContentRangeHeaderValue(from, _server.Content.Length - 1, _server.Content.Length);
            if (answerETag is not null) response.Headers.TryAddWithoutValidation("ETag", answerETag);
            return response;
        };

        await DownloadVerifiedAsync();

        Assert.Equal(2, _server.Requests.Count);
        Assert.Null(_server.Requests[1].RangeFrom);
    }

    [Theory]
    [InlineData("https://software.download.prss.microsoft.com/dbazure/Windows11_Other_Build.iso")] // another file
    [InlineData("https://download.microsoft.com/dbazure/Windows11_Test_x64_en-us.iso")] // another host
    public async Task A_partial_file_that_belongs_to_another_address_is_not_continued(string savedUrl)
    {
        SeedPartial(50_000, url: savedUrl, content: new byte[60_000]);

        await DownloadVerifiedAsync();

        Assert.Null(Assert.Single(_server.Requests).RangeFrom);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("{\"etag\":\"\\\"v1\\\"\",\"length\":200000}")] // no url
    public async Task A_damaged_description_means_starting_over(string sidecarText)
    {
        await File.WriteAllBytesAsync(Part, new byte[50_000], Ct);
        await File.WriteAllTextAsync(Sidecar, sidecarText, Ct);

        await DownloadVerifiedAsync();

        Assert.Null(Assert.Single(_server.Requests).RangeFrom);
    }

    [Fact]
    public async Task A_partial_file_larger_than_the_recorded_total_means_starting_over()
    {
        SeedPartial(60_000, length: 50_000, content: new byte[60_000]);

        await DownloadVerifiedAsync();

        Assert.Null(Assert.Single(_server.Requests).RangeFrom);
    }

    [Fact]
    public async Task The_description_stores_the_address_without_the_signed_query()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Action cancel = cts.Cancel;
        _server.Body = (_, bytes) => new FaultStream(bytes, 40_000, FaultStream.Fault.Hang, cancel);
        using var downloader = CreateDownloader();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            downloader.DownloadAsync(Request(), null, cts.Token));

        var text = await File.ReadAllTextAsync(Sidecar, Ct);
        Assert.DoesNotContain("SECRETSIGNATURE", text);
        Assert.DoesNotContain("?", text);
        var info = ResumableDownloader.ReadSidecar(Sidecar);
        Assert.NotNull(info);
        Assert.Equal($"https://{FakeIsoServer.Host}{FakeIsoServer.FilePath}", info.Url);
        Assert.Equal("\"v1\"", info.ETag);
        Assert.Equal(_server.Content.Length, info.Length);
    }

    // ---- interruptions ----

    [Fact]
    public async Task Cancellation_keeps_the_partial_file_and_the_next_run_continues_it()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Action cancel = cts.Cancel;
        _server.Body = (number, bytes) =>
            number == 0 ? new FaultStream(bytes, 40_000, FaultStream.Fault.Hang, cancel) : null;
        using var downloader = CreateDownloader();

        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            downloader.DownloadAsync(Request(expected: _server.Sha256), null, cts.Token));

        Assert.Equal(cts.Token, cancelled.CancellationToken);
        Assert.False(File.Exists(Destination));
        Assert.Equal(40_000, new FileInfo(Part).Length);
        Assert.Equal(_server.Content[..40_000], await File.ReadAllBytesAsync(Part, Ct));
        Assert.True(File.Exists(Sidecar));

        var result = await downloader.DownloadAsync(Request(expected: _server.Sha256), null, Ct);

        AssertDownloaded(result);
        Assert.Equal(2, _server.Requests.Count);
        Assert.Equal(40_000, _server.Requests[1].RangeFrom);
        Assert.Equal("\"v1\"", _server.Requests[1].IfRange);
    }

    [Fact]
    public async Task A_cancel_that_surfaces_as_a_broken_connection_is_still_a_cancel_and_is_not_retried()
    {
        // Cutting a connection can make the pending read fail with an I/O error instead of a cancellation.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Action cancel = cts.Cancel;
        _server.Body = (_, bytes) => new FaultStream(bytes, 40_000, FaultStream.Fault.Throw, cancel);
        using var downloader = CreateDownloader();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            downloader.DownloadAsync(Request(), null, cts.Token));

        Assert.Single(_server.Requests);
        Assert.Empty(_delays.Values);
        Assert.Equal(40_000, new FileInfo(Part).Length);
        Assert.True(File.Exists(Sidecar));
    }

    [Fact]
    public async Task A_cancel_during_the_final_hash_check_keeps_the_finished_partial_file()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var verify = new CancelOnFirstReport(cts);
        using var downloader = CreateDownloader();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            downloader.DownloadAsync(Request(expected: _server.Sha256) with { VerifyProgress = verify }, null,
                cts.Token));

        Assert.False(File.Exists(Destination));
        Assert.Equal(_server.Content.Length, new FileInfo(Part).Length);

        // The next run asks the server only to confirm that nothing is missing (416), then verifies.
        var result = await downloader.DownloadAsync(Request(expected: _server.Sha256), null, Ct);

        AssertDownloaded(result);
        Assert.Equal(_server.Content.Length, _server.Requests[^1].RangeFrom);
    }

    [Fact]
    public async Task A_cancel_before_the_first_request_sends_nothing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var downloader = CreateDownloader();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            downloader.DownloadAsync(Request() with { InitialTicket = _server.Ticket() }, null, cts.Token));

        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task A_stalled_body_fails_after_the_stall_timeout_and_keeps_the_partial_file()
    {
        // First answer: 40 000 bytes, then silence. Every later answer: silence from the start.
        _server.Body = (number, bytes) => new FaultStream(bytes, number == 0 ? 40_000 : 0, FaultStream.Fault.Hang);
        var options = Options() with { StallTimeout = TimeSpan.FromMilliseconds(150) };
        using var downloader = CreateDownloader(options);
        var watch = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() =>
            downloader.DownloadAsync(Request(expected: _server.Sha256), null, Ct));

        Assert.Equal(ErrorKind.Network, ex.Kind);
        Assert.Contains("no data arrived", ex.Message);
        Assert.Contains("keeps the part", ex.Message);
        Assert.Contains(FakeIsoServer.Host, ex.Message);
        AssertNoSecrets(ex);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), "the stall timeout should end the attempt quickly");

        Assert.False(File.Exists(Destination));
        Assert.Equal(40_000, new FileInfo(Part).Length);
        Assert.True(File.Exists(Sidecar));
        // Two timeouts in a row, the second without a single new byte: that is the limit.
        Assert.Equal(2, _server.Requests.Count);
        Assert.Equal(40_000, _server.Requests[1].RangeFrom);
    }

    [Fact]
    public async Task A_slow_but_steady_body_is_not_a_stall()
    {
        // Each read arrives well inside the stall timeout although the whole body takes longer than it.
        _server.Content = _server.Content[..40_000];
        _server.Body = (_, bytes) => new SlowStream(bytes, 2_000, TimeSpan.FromMilliseconds(30));

        await DownloadVerifiedAsync(Options() with { StallTimeout = TimeSpan.FromMilliseconds(500) });

        Assert.Single(_server.Requests);
    }

    [Fact]
    public async Task A_dropped_connection_is_continued_from_where_it_stopped()
    {
        _server.Body = (number, bytes) => number == 0 ? new FaultStream(bytes, 70_000, FaultStream.Fault.Throw) : null;

        await DownloadVerifiedAsync();

        Assert.Equal(2, _server.Requests.Count);
        Assert.Equal(70_000, _server.Requests[1].RangeFrom);
        Assert.Equal([TimeSpan.FromSeconds(1)], _delays.Values);
        Assert.Equal(1, _ticketsIssued); // a drop is not a reason to ask for a new link
    }

    [Fact]
    public async Task A_body_that_ends_early_is_continued_too()
    {
        // The server announces the full length and closes the connection after 30 000 bytes.
        _server.Intercept = (number, _) =>
        {
            if (number != 0) return null;

            var response = new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StreamContent(new MemoryStream(_server.Content[..30_000])) };
            response.Content.Headers.ContentLength = _server.Content.Length;
            response.Headers.TryAddWithoutValidation("ETag", "\"v1\"");
            return response;
        };

        await DownloadVerifiedAsync();

        Assert.Equal(30_000, _server.Requests[1].RangeFrom);
    }

    [Fact]
    public async Task Many_drops_are_survived_as_long_as_each_attempt_brings_new_bytes()
    {
        // Ten drops, far more than the retry limit, but the file grows every time.
        _server.Body = (number, bytes) => number < 10 ? new FaultStream(bytes, 10_000, FaultStream.Fault.Throw) : null;

        await DownloadVerifiedAsync();

        Assert.Equal(11, _server.Requests.Count);
        Assert.All(_delays.Values, d => Assert.Equal(TimeSpan.FromSeconds(1), d));
    }

    [Fact]
    public async Task Drops_without_progress_give_up_after_four_attempts()
    {
        _server.Body = (_, bytes) => new FaultStream(bytes, 0, FaultStream.Fault.Throw);
        using var downloader = CreateDownloader();

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() => downloader.DownloadAsync(Request(), null, Ct));

        Assert.Equal(ErrorKind.Network, ex.Kind);
        Assert.Contains(FakeIsoServer.Host, ex.Message);
        AssertNoSecrets(ex);
        Assert.Equal(4, _server.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], _delays.Values);
    }

    [Fact]
    public async Task Without_a_strong_etag_drops_are_limited_even_when_bytes_arrive()
    {
        // Nothing can be continued, so every attempt starts at zero. That must not go on for ever.
        _server.ETag = null;
        _server.Body = (_, bytes) => new FaultStream(bytes, 10_000, FaultStream.Fault.Throw);
        using var downloader = CreateDownloader();

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() => downloader.DownloadAsync(Request(), null, Ct));

        Assert.Equal(ErrorKind.Network, ex.Kind);
        Assert.DoesNotContain("keeps the part", ex.Message);
        Assert.Equal(4, _server.Requests.Count);
        Assert.All(_server.Requests, r => Assert.Null(r.RangeFrom));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    [InlineData(429)]
    public async Task Passing_server_errors_are_retried(int status)
    {
        _server.Intercept = (number, _) => number < 2 ? new HttpResponseMessage((HttpStatusCode)status) : null;

        await DownloadVerifiedAsync();

        Assert.Equal(3, _server.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], _delays.Values);
    }

    [Fact]
    public async Task Retry_after_is_honoured_but_capped()
    {
        _server.Intercept = (number, _) =>
        {
            if (number >= 2) return null;

            var busy = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            busy.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(number == 0 ? 5 : 3600));
            return busy;
        };
        using var downloader = CreateDownloader();

        await downloader.DownloadAsync(Request(), null, Ct);

        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)], _delays.Values);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(410)]
    [InlineData(501)]
    public async Task Other_error_statuses_are_final(int status)
    {
        _server.Intercept = (_, _) => new HttpResponseMessage((HttpStatusCode)status);
        using var downloader = CreateDownloader();

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() => downloader.DownloadAsync(Request(), null, Ct));

        Assert.Equal(ErrorKind.ServiceRefused, ex.Kind);
        Assert.Contains(status.ToString(), ex.Message);
        Assert.Contains("Use my ISO", ex.Message);
        AssertNoSecrets(ex);
        Assert.Single(_server.Requests);
        Assert.Empty(_delays.Values);
    }

    [Fact]
    public async Task A_rejected_certificate_is_never_retried()
    {
        _server.Intercept = (_, _) => throw DownloadTestData.RejectedCertificate();
        using var downloader = CreateDownloader();

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() => downloader.DownloadAsync(Request(), null, Ct));

        Assert.Equal(ErrorKind.Network, ex.Kind);
        Assert.Contains("cannot verify", ex.Message);
        Assert.Single(_server.Requests);
        Assert.Empty(_delays.Values);
        Assert.Equal(1, _ticketsIssued);
    }

    // ---- links that expire ----

    [Fact]
    public async Task A_403_makes_the_downloader_ask_for_a_new_link_once_and_continue_the_partial_file()
    {
        SeedPartial(50_000);
        string[] tokens = ["expired", "good"];
        using var downloader = CreateDownloader();

        var result =
            await downloader.DownloadAsync(Request(() => _server.Ticket(tokens[_ticketsIssued - 1]), _server.Sha256),
                null, Ct);

        AssertDownloaded(result);
        Assert.Equal(2, _ticketsIssued);
        Assert.Equal(2, _server.Requests.Count);
        Assert.Contains("t=expired", _server.Requests[0].Uri.Query);
        Assert.Contains("t=good", _server.Requests[1].Uri.Query);
        Assert.Equal(50_000, _server.Requests[1].RangeFrom); // the partial file survived the new link
        Assert.Empty(_delays.Values); // a new link is not a retry with a wait
    }

    [Fact]
    public async Task A_link_that_expires_in_the_middle_of_a_download_is_replaced_and_the_download_continues()
    {
        // The connection drops after 60 000 bytes; by then the first link is no longer accepted.
        _server.Body = (number, bytes) =>
            number == 0
                ? new FaultStream(bytes, 60_000, FaultStream.Fault.Throw, () => _server.ValidToken = "second")
                : null;
        string[] tokens = ["good", "second"];
        using var downloader = CreateDownloader();

        var result =
            await downloader.DownloadAsync(Request(() => _server.Ticket(tokens[_ticketsIssued - 1]), _server.Sha256),
                null, Ct);

        AssertDownloaded(result);
        Assert.Equal(2, _ticketsIssued);
        Assert.Equal([null, 60_000, 60_000], _server.Requests.Select(r => r.RangeFrom));
    }

    [Fact]
    public async Task A_second_403_is_reported_and_not_answered_with_a_third_link()
    {
        SeedPartial(50_000);
        using var downloader = CreateDownloader();

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() =>
            downloader.DownloadAsync(Request(() => _server.Ticket("expired")), null, Ct));

        Assert.Equal(ErrorKind.ServiceRefused, ex.Kind);
        Assert.Contains("403", ex.Message);
        Assert.Contains("Use my ISO", ex.Message);
        Assert.Contains("keeps the part", ex.Message);
        AssertNoSecrets(ex);
        Assert.Equal(2, _ticketsIssued);
        Assert.Equal(2, _server.Requests.Count);
        Assert.True(File.Exists(Part));
    }

    [Theory]
    [InlineData(-300)] // past its expiry
    [InlineData(20)] // inside the expiry margin
    public async Task An_initial_ticket_that_expires_soon_is_replaced_before_any_request(int secondsLeft)
    {
        var stale = _server.Ticket("expired", _time.GetUtcNow().AddSeconds(secondsLeft));
        using var downloader = CreateDownloader();

        var result = await downloader.DownloadAsync(Request() with { InitialTicket = stale }, null, Ct);

        AssertDownloaded(result);
        Assert.Equal(1, _ticketsIssued);
        Assert.Contains("t=good", Assert.Single(_server.Requests).Uri.Query);
    }

    [Fact]
    public async Task A_fresh_ticket_is_used_even_if_the_pc_clock_says_it_has_expired()
    {
        // The clock of the PC is a day ahead. Asking for new links in a loop would not help.
        using var downloader = CreateDownloader();

        var result = await downloader.DownloadAsync(
            Request(() => _server.Ticket(expires: _time.GetUtcNow().AddHours(-1))), null, Ct);

        AssertDownloaded(result);
        Assert.Equal(1, _ticketsIssued);
    }

    [Fact]
    public async Task An_error_from_the_ticket_factory_is_passed_on_unchanged()
    {
        var refusal = new PlainstallException(ErrorKind.ServiceRefused, "The service said no.");
        using var downloader = CreateDownloader();

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() =>
            downloader.DownloadAsync(new DownloadRequest(Destination, _ => Task.FromException<DownloadTicket>(refusal)),
                null, Ct));

        Assert.Same(refusal, ex);
        Assert.Empty(_server.Requests);
    }

    // ---- redirects and addresses ----

    [Fact]
    public async Task A_redirect_to_another_iso_host_is_followed_with_the_same_headers()
    {
        SeedPartial(50_000, url: "https://software.download.prss.microsoft.com/dbazure/Windows11_Test_x64_en-us.iso");
        _server.Intercept = (_, request) => request.RequestUri!.Host == FakeIsoServer.Host
            ? FakeMicrosoftService.Redirect(
                "https://download.microsoft.com/cdn/Windows11_Test_x64_en-us.iso?t=good&sig=SECRETSIGNATURE")
            : null;

        await DownloadVerifiedAsync();

        Assert.Equal(["software.download.prss.microsoft.com", "download.microsoft.com"],
            _server.Requests.Select(r => r.Host));
        Assert.Equal([50_000, 50_000], _server.Requests.Select(r => r.RangeFrom));
        Assert.Equal(["\"v1\"", "\"v1\""], _server.Requests.Select(r => r.IfRange));
        Assert.All(_server.Requests.Select(r => r.Referer), Assert.NotNull);
    }

    [Theory]
    [InlineData("https://evil.example/Windows11.iso")]
    [InlineData("https://download.microsoft.com.evil.example/Windows11.iso")]
    [InlineData("http://download.microsoft.com/Windows11.iso")]
    [InlineData("https://download.microsoft.com:8443/Windows11.iso")]
    [InlineData("https://download.microsoft.com/Windows11.exe")]
    [InlineData("https://www.microsoft.com/Windows11.iso")]
    public async Task A_redirect_that_fails_the_iso_allowlist_is_refused_and_never_requested(string location)
    {
        _server.Intercept = (number, _) =>
            number == 0 ? FakeMicrosoftService.Redirect(location + "?sig=SECRETSIGNATURE") : null;
        using var downloader = CreateDownloader();

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() => downloader.DownloadAsync(Request(), null, Ct));

        Assert.Equal(ErrorKind.ServiceRefused, ex.Kind);
        Assert.Contains("Use my ISO", ex.Message);
        AssertNoSecrets(ex);
        Assert.Single(_server.Requests);
        Assert.False(File.Exists(Part));
        Assert.Empty(_delays.Values);
    }

    [Fact]
    public async Task More_than_five_redirects_are_refused()
    {
        _server.Intercept = (number, _) =>
            FakeMicrosoftService.Redirect($"https://download.microsoft.com/hop{number}/Windows11.iso?t=good&");
        using var downloader = CreateDownloader();

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() => downloader.DownloadAsync(Request(), null, Ct));

        Assert.Equal(ErrorKind.ServiceRefused, ex.Kind);
        Assert.Equal(6, _server.Requests.Count);
    }

    [Theory]
    [InlineData("https://evil.example/Windows11.iso?sig=SECRETSIGNATURE")]
    [InlineData("http://software.download.prss.microsoft.com/Windows11.iso?sig=SECRETSIGNATURE")]
    [InlineData("https://software.download.prss.microsoft.com/setup.exe?sig=SECRETSIGNATURE")]
    public async Task A_ticket_that_fails_the_iso_allowlist_is_refused_without_a_request(string link)
    {
        var ticket = new DownloadTicket(new Uri(link), "Windows11.iso", null);
        using var downloader = CreateDownloader();

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() =>
            downloader.DownloadAsync(Request(() => ticket), null, Ct));

        Assert.Equal(ErrorKind.ServiceRefused, ex.Kind);
        AssertNoSecrets(ex);
        Assert.Empty(_server.Requests);
    }

    // ---- verification ----

    [Fact]
    public async Task A_hash_mismatch_deletes_the_file_and_its_description()
    {
        using var downloader = CreateDownloader();
        string wrong = new('0', 64);

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() =>
            downloader.DownloadAsync(Request(expected: wrong), null, Ct));

        Assert.Equal(ErrorKind.Verification, ex.Kind);
        Assert.Contains("deleted", ex.Message);
        Assert.Contains("Start the download again", ex.Message);
        AssertNoSecrets(ex);
        Assert.False(File.Exists(Destination));
        Assert.False(File.Exists(Part));
        Assert.False(File.Exists(Sidecar));
        Assert.Empty(Directory.GetFiles(_temp.Path));
    }

    [Fact]
    public async Task After_a_hash_mismatch_the_next_run_downloads_from_the_start()
    {
        using var downloader = CreateDownloader();
        await Assert.ThrowsAnyAsync<PlainstallException>(() =>
            downloader.DownloadAsync(Request(expected: new string('0', 64)), null, Ct));

        var result = await downloader.DownloadAsync(Request(expected: _server.Sha256), null, Ct);

        AssertDownloaded(result);
        Assert.Equal(2, _server.Requests.Count);
        Assert.All(_server.Requests, r => Assert.Null(r.RangeFrom));
    }

    [Fact]
    public async Task A_resumed_file_with_stale_bytes_fails_its_hash_and_is_deleted()
    {
        // The partial file claims the right ETag but holds wrong bytes (a damaged disk, a meddling tool).
        SeedPartial(50_000, content: new byte[60_000]);
        using var downloader = CreateDownloader();

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() =>
            downloader.DownloadAsync(Request(expected: _server.Sha256), null, Ct));

        Assert.Equal(ErrorKind.Verification, ex.Kind);
        Assert.Empty(Directory.GetFiles(_temp.Path));
    }

    [Fact]
    public async Task An_expected_hash_that_is_not_a_sha256_is_a_programming_error()
    {
        using var downloader = CreateDownloader();

        await Assert.ThrowsAsync<ArgumentException>(() => downloader.DownloadAsync(Request(expected: "abc"), null, Ct));
    }

    [Fact]
    public async Task The_hash_check_reports_its_own_progress()
    {
        var verify = new SyncProgress<TransferProgress>();
        using var downloader = CreateDownloader();

        await downloader.DownloadAsync(Request() with { VerifyProgress = verify }, null, Ct);

        Assert.Equal(new TransferProgress(_server.Content.Length, _server.Content.Length), verify.Values[^1]);
    }

    // ---- disk ----

    [Fact]
    public async Task Too_little_free_space_stops_the_download_before_anything_is_written()
    {
        var options = Options() with { FreeSpaceProbe = _ => 100_000, FreeSpaceReserveBytes = 0 };
        using var downloader = CreateDownloader(options);

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() => downloader.DownloadAsync(Request(), null, Ct));

        Assert.Equal(ErrorKind.Preflight, ex.Kind);
        Assert.Contains("free space", ex.Message);
        Assert.False(File.Exists(Part));
        Assert.False(File.Exists(Destination));
        Assert.Single(_server.Requests);
    }

    [Fact]
    public async Task Free_space_is_measured_against_what_is_still_missing()
    {
        // 150 000 of 200 000 bytes are already there, so 60 000 free bytes are enough.
        SeedPartial(150_000);

        await DownloadVerifiedAsync(Options() with { FreeSpaceProbe = _ => 60_000, FreeSpaceReserveBytes = 0 });
    }

    [Fact]
    public async Task The_reserve_counts_as_needed_space()
    {
        var options = Options() with { FreeSpaceProbe = _ => 250_000, FreeSpaceReserveBytes = 100_000 };
        using var downloader = CreateDownloader(options);

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() => downloader.DownloadAsync(Request(), null, Ct));

        Assert.Equal(ErrorKind.Preflight, ex.Kind);
    }

    [Fact]
    public async Task Unknown_free_space_does_not_block_the_download()
    {
        var options = Options() with { FreeSpaceProbe = _ => null };
        using var downloader = CreateDownloader(options);

        var result = await downloader.DownloadAsync(Request(), null, Ct);

        AssertDownloaded(result);
    }

    [Fact]
    public async Task A_body_without_a_length_is_downloaded_but_cannot_be_continued_later()
    {
        // A stream that cannot seek, so that nothing can work out a Content-Length for it.
        _server.SendContentLength = false;
        _server.Body = (_, bytes) => new FaultStream(bytes, int.MaxValue, FaultStream.Fault.None);

        await DownloadVerifiedAsync(progress: _progress);

        Assert.Null(_progress.Values[0].BytesTotal);
    }

    /// <summary>Cancels as soon as the hash check reports for the first time.</summary>
    private sealed class CancelOnFirstReport(CancellationTokenSource cts) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value)
        {
            cts.Cancel();
        }
    }

    /// <summary>Delivers a few bytes at a time with a pause before each piece.</summary>
    private sealed class SlowStream(byte[] data, int chunk, TimeSpan pause) : MemoryStream(data)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(pause, cancellationToken);
            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], cancellationToken);
        }
    }
}