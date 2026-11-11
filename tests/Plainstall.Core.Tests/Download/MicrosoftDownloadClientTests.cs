using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Plainstall.Core.Download;

namespace Plainstall.Core.Tests.Download;

public sealed class MicrosoftDownloadClientTests
{
    private const string OwnIsoOffer = "Use my ISO";
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 14, 3, 11, TimeSpan.Zero);
    private readonly RecordedDelays _delays = new();

    private readonly FakeMicrosoftService _service = new();
    private readonly ManualTimeProvider _time = new(Now);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MicrosoftDownloadClient CreateClient(HttpMessageHandler? handler = null, TransportOptions? options = null)
    {
        return new MicrosoftDownloadClient(handler ?? _service.Handler, _time, null,
            options ?? new TransportOptions { Delay = _delays.Wait });
    }

    private static void AssertRefusedWithOffer(PlainstallException ex)
    {
        Assert.Equal(ErrorKind.ServiceRefused, ex.Kind);
        Assert.Contains(OwnIsoOffer, ex.Message);
        Assert.DoesNotContain("mirror", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(((char)0x2014).ToString(), ex.Message); // the long dash is banned from all texts
    }

    // ---- GetReleaseAsync ----

    [Fact]
    public async Task Release_is_read_from_the_page_and_the_language_list()
    {
        using var client = CreateClient();

        var release = await client.GetReleaseAsync(Ct);

        Assert.Equal("3813", release.ProductEditionId);
        Assert.Equal("Windows 11 (multi-edition ISO for x64 devices)", release.EditionName);
        Assert.Equal("Windows 11 Client - Build 26300.9457", release.ProductDisplayName);
        Assert.Equal("26300.9457", release.Build);
        Assert.Equal(6, release.Hashes.Rows.Count);

        Assert.Equal(
            [
                "Arabic", "Brazilian Portuguese", "Chinese (Simplified)", "English (United States)",
                "English International", "German"
            ],
            release.Languages.Select(l => l.LocalizedLanguage));
        var us = release.Languages.Single(l => l.SkuId == "27160");
        Assert.Equal("English", us.Language);
        Assert.Equal("English (United States)", us.DisplayName);
        var uk = release.Languages.Single(l => l.SkuId == "27161"); // the id was a JSON number
        Assert.Equal("English (United Kingdom)", uk.Language);
        Assert.Equal("English International", uk.LocalizedLanguage);

        // Every offered language of the fixture can be verified.
        Assert.All(release.Languages, l => Assert.NotNull(release.Hashes.FindExpectedHash(l)));
    }

    [Fact]
    public async Task Release_runs_the_documented_call_sequence()
    {
        using var client = CreateClient();

        await client.GetReleaseAsync(Ct);

        var requests = _service.Requests;
        Assert.Equal(["page", "tags", "mdt", "ov-df", "skus"],
            requests.Select(r => FakeMicrosoftService.RouteOf(r.Uri)));
        Assert.Equal("https://www.microsoft.com/en-us/software-download/windows11", requests[0].Uri.AbsoluteUri);

        // One session id, a GUID, used by every call after the page.
        var session = requests[1].Query("session_id");
        Assert.True(Guid.TryParseExact(session, "D", out _));
        Assert.Equal("y6jn8c31", requests[1].Query("org_id"));

        Assert.Equal("560dc9f3-1aa5-4a2f-b63c-9e18f8d0e175", requests[2].Query("instanceId"));
        Assert.Equal("si", requests[2].Query("PageId"));
        Assert.Equal(session, requests[2].Query("session_id"));

        Assert.Equal(session, requests[3].Query("session_id"));
        Assert.Equal("560dc9f3-1aa5-4a2f-b63c-9e18f8d0e175", requests[3].Query("CustomerId"));
        Assert.Equal("si", requests[3].Query("PageId"));
        Assert.Equal("8DC1F2A3B4C5D6E7", requests[3].Query("w"));
        Assert.Equal("638990123456789012", requests[3].Query("rticks"));
        Assert.Equal(Now.ToUnixTimeMilliseconds().ToString(), requests[3].Query("mdt"));

        Assert.Equal("/software-download-connector/api/getskuinformationbyproductedition",
            requests[4].Uri.AbsolutePath);
        Assert.Equal("606624d44113", requests[4].Query("profile"));
        Assert.Equal("3813", requests[4].Query("productEditionId"));
        Assert.Equal("undefined", requests[4].Query("SKU"));
        Assert.Equal("undefined", requests[4].Query("friendlyFileName"));
        Assert.Equal("en-US", requests[4].Query("Locale"));
        Assert.Equal(session, requests[4].Query("sessionID"));
    }

    [Fact]
    public async Task The_only_header_sent_is_the_referer()
    {
        using var client = CreateClient();
        var release = await client.GetReleaseAsync(Ct);
        await client.GetDownloadTicketAsync(release, release.Languages[0], Ct);

        Assert.All(_service.Requests, r =>
        {
            Assert.Equal(new Uri("https://www.microsoft.com/software-download/windows11"), r.Referer);
            Assert.Equal(1, r.HeaderCount);
        });
    }

    [Fact]
    public async Task Loose_markup_on_the_page_is_still_read()
    {
        _service.Overrides["page"] = _ => FakeMicrosoftService.Text(Fixture.Read("product-page-loose.html"));
        using var client = CreateClient();

        var release = await client.GetReleaseAsync(Ct);

        Assert.Equal("3816", release.ProductEditionId); // unquoted attribute, upper-case tags
        Assert.Equal("3816", _service.RequestsTo("skus").Single().Query("productEditionId"));
        Assert.Single(release.Hashes.Rows);
    }

    [Theory]
    [InlineData("<html><body><p>Under maintenance</p></body></html>")]
    [InlineData("<select id=\"product-edition\"><option value=\"\">Select Download</option></select>")]
    [InlineData("<select id=\"product-edition\"><option value=\"abc\">Not numeric</option></select>")]
    [InlineData("<select id=\"another\"><option value=\"3813\">Wrong list</option></select>")]
    [InlineData("<!-- <select id=\"product-edition\"><option value=\"3813\">In a comment</option></select> -->")]
    [InlineData("")]
    public async Task A_page_without_the_product_list_is_a_clear_refusal(string html)
    {
        _service.Overrides["page"] = _ => FakeMicrosoftService.Text(html);
        using var client = CreateClient();

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() => client.GetReleaseAsync(Ct));

        AssertRefusedWithOffer(ex);
        Assert.Contains("page", ex.Message);
        Assert.Single(_service.Requests); // nothing else was attempted
    }

    [Fact]
    public async Task A_page_without_a_hash_table_still_gives_a_release_with_an_empty_table()
    {
        _service.Overrides["page"] = _ => FakeMicrosoftService.Text(
            "<select id=\"product-edition\"><option value=\"3813\">Windows 11</option></select>");
        using var client = CreateClient();

        var release = await client.GetReleaseAsync(Ct);

        Assert.True(release.Hashes.IsEmpty);
        Assert.NotEmpty(release.Languages);
    }

    [Fact]
    public async Task A_service_error_in_the_language_answer_is_a_refusal_with_the_own_iso_offer()
    {
        _service.Overrides["skus"] =
            _ => FakeMicrosoftService.Text(Fixture.Read("service-error.json"), "application/json");
        using var client = CreateClient();

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() => client.GetReleaseAsync(Ct));

        AssertRefusedWithOffer(ex);
        Assert.Contains("The request was rejected for this session", ex.Message); // Microsoft's own words are passed on
        Assert.Contains("network or region", ex.Message);
    }

    [Theory]
    [InlineData("{\"Skus\":[]}")]
    [InlineData("{\"Skus\":null}")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("<html>Access Denied</html>")]
    [InlineData("")]
    public async Task A_language_answer_that_does_not_match_the_layout_is_a_refusal(string body)
    {
        _service.Overrides["skus"] = _ => FakeMicrosoftService.Text(body, "application/json");
        using var client = CreateClient();

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() => client.GetReleaseAsync(Ct));

        AssertRefusedWithOffer(ex);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task A_refused_page_is_not_retried(HttpStatusCode status)
    {
        _service.Overrides["page"] = _ => new HttpResponseMessage(status);
        using var client = CreateClient();

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() => client.GetReleaseAsync(Ct));

        AssertRefusedWithOffer(ex);
        Assert.Contains(((int)status).ToString(), ex.Message);
        Assert.Single(_service.Requests);
        Assert.Empty(_delays.Values);
    }

    // ---- redirects ----

    [Fact]
    public async Task A_redirect_to_a_host_that_is_not_allowed_is_refused_and_never_requested()
    {
        _service.Overrides["page"] = _ =>
            FakeMicrosoftService.Redirect("https://www.microsoft.com.evil.example/en-us/software-download/windows11");
        using var client = CreateClient();

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() => client.GetReleaseAsync(Ct));

        AssertRefusedWithOffer(ex);
        Assert.Contains("www.microsoft.com.evil.example", ex.Message);
        Assert.Single(_service.Requests);
        Assert.DoesNotContain(_service.Requests, r => r.Host.Contains("evil", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://www.microsoft.com/en-us/software-download/windows11")] // downgrade to http
    [InlineData("https://www.microsoft.com:8443/en-us/software-download/windows11")]
    [InlineData("https://user@www.microsoft.com/en-us/software-download/windows11")]
    [InlineData("//evil.example/x")] // scheme-relative
    [InlineData("https://software.download.prss.microsoft.com/x.iso")] // an ISO host is not a catalog host
    public async Task Redirects_that_break_any_rule_are_refused(string location)
    {
        _service.Overrides["page"] = _ => FakeMicrosoftService.Redirect(location);
        using var client = CreateClient();

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() => client.GetReleaseAsync(Ct));

        Assert.Equal(ErrorKind.ServiceRefused, ex.Kind);
        Assert.Single(_service.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.SeeOther)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task Redirects_inside_the_allowlist_are_followed_by_hand(HttpStatusCode status)
    {
        var hops = 0;
        _service.Overrides["page"] = request =>
            request.RequestUri!.Query.Contains("moved", StringComparison.Ordinal) || hops++ > 0
                ? null
                : FakeMicrosoftService.Redirect("/en-us/software-download/windows11?moved=1",
                    status); // relative on purpose
        using var client = CreateClient();

        var release = await client.GetReleaseAsync(Ct);

        Assert.Equal("3813", release.ProductEditionId);
        Assert.Equal("https://www.microsoft.com/en-us/software-download/windows11?moved=1",
            _service.Requests[1].Uri.AbsoluteUri);
        Assert.NotNull(_service.Requests[1].Referer);
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public async Task At_most_five_redirects_are_followed(int redirects, bool succeeds)
    {
        _service.Overrides["page"] = request =>
        {
            var hop = int.TryParse(new RecordedRequest(request.RequestUri!, null, null, null, 0).Query("hop"),
                out var n)
                ? n
                : 0;
            return hop < redirects
                ? FakeMicrosoftService.Redirect(
                    $"https://www.microsoft.com/en-us/software-download/windows11?hop={hop + 1}")
                : null;
        };
        using var client = CreateClient();

        if (succeeds)
        {
            var release = await client.GetReleaseAsync(Ct);
            Assert.Equal("3813", release.ProductEditionId);
            Assert.Equal(redirects + 1, _service.RequestsTo("page").Count());
        }
        else
        {
            var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() => client.GetReleaseAsync(Ct));
            Assert.Equal(ErrorKind.ServiceRefused, ex.Kind);
            Assert.Equal(6, _service.Requests.Count); // the first request and five hops; the sixth hop is not followed
        }
    }

    [Fact]
    public async Task A_redirect_without_a_location_is_refused()
    {
        _service.Overrides["page"] = _ => new HttpResponseMessage(HttpStatusCode.Found);
        using var client = CreateClient();

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() => client.GetReleaseAsync(Ct));

        Assert.Equal(ErrorKind.ServiceRefused, ex.Kind);
    }

    [Fact]
    public void A_handler_that_follows_redirects_by_itself_is_rejected()
    {
        using var auto = new HttpClientHandler();
        auto.AllowAutoRedirect = true;
        Assert.Throws<ArgumentException>(() => new MicrosoftDownloadClient(auto));

        using var sockets = new SocketsHttpHandler();
        sockets.AllowAutoRedirect = true;
        Assert.Throws<ArgumentException>(() => new ResumableDownloader(sockets));

        using var manual = new SocketsHttpHandler();
        manual.AllowAutoRedirect = false;
        using var ok = new MicrosoftDownloadClient(manual);
    }

    // ---- transport policy ----

    [Fact]
    public async Task Answers_larger_than_the_cap_are_refused()
    {
        _service.Overrides["page"] = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            // No Content-Length: the cap has to hold while reading.
            Content = new StreamContent(new FaultStream(new byte[MicrosoftDownloadClient.MaxResponseBytes + 1],
                int.MaxValue, FaultStream.Fault.None, chunk: 64 * 1024))
        };
        using var client = CreateClient();

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() => client.GetReleaseAsync(Ct));

        Assert.Equal(ErrorKind.ServiceRefused, ex.Kind);
        Assert.Contains("larger", ex.Message);
        Assert.Single(_service.Requests);
    }

    [Fact]
    public async Task An_answer_of_exactly_the_cap_is_read()
    {
        var page = Fixture.Read("product-page-x64.html");
        _service.Overrides["page"] = _ => FakeMicrosoftService.Text(page + new string(' ',
            MicrosoftDownloadClient.MaxResponseBytes - Encoding.UTF8.GetByteCount(page)));
        using var client = CreateClient();

        var release = await client.GetReleaseAsync(Ct);

        Assert.Equal("3813", release.ProductEditionId);
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task Passing_server_errors_are_retried_with_backoff(int status)
    {
        var failures = status == 408 ? 1 : 3; // a timeout status gets one retry, the others three
        var seen = 0;
        _service.Overrides["page"] = _ => seen++ < failures ? new HttpResponseMessage((HttpStatusCode)status) : null;
        using var client = CreateClient();

        var release = await client.GetReleaseAsync(Ct);

        Assert.Equal("3813", release.ProductEditionId);
        TimeSpan[] expected = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];
        Assert.Equal(expected.Take(failures), _delays.Values);
    }

    [Fact]
    public async Task After_four_attempts_a_busy_server_is_a_network_error()
    {
        _service.Overrides["page"] = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        using var client = CreateClient();

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() => client.GetReleaseAsync(Ct));

        Assert.Equal(ErrorKind.Network, ex.Kind);
        Assert.Contains("www.microsoft.com", ex.Message);
        Assert.Contains("503", ex.Message);
        Assert.Equal(4, _service.Requests.Count);
        Assert.Equal(3, _delays.Values.Count);
    }

    [Fact]
    public async Task Retry_after_is_honoured_up_to_thirty_seconds()
    {
        var seen = 0;
        _service.Overrides["page"] = _ =>
        {
            if (seen++ >= 2) return null;

            var busy = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            busy.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seen == 1 ? 7 : 600));
            return busy;
        };
        using var client = CreateClient();

        await client.GetReleaseAsync(Ct);

        Assert.Equal([TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(30)], _delays.Values);
    }

    [Fact]
    public async Task A_dropped_connection_is_retried()
    {
        var seen = 0;
        _service.Overrides["page"] = _ =>
            seen++ == 0 ? throw new HttpRequestException("Connection reset (simulated).") : null;
        using var client = CreateClient();

        var release = await client.GetReleaseAsync(Ct);

        Assert.Equal("3813", release.ProductEditionId);
        Assert.Equal([TimeSpan.FromSeconds(1)], _delays.Values);
    }

    [Fact]
    public async Task A_rejected_certificate_is_final_and_never_retried()
    {
        _service.Overrides["page"] = _ => throw DownloadTestData.RejectedCertificate();
        using var client = CreateClient();

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() => client.GetReleaseAsync(Ct));

        Assert.Equal(ErrorKind.Network, ex.Kind);
        Assert.Contains("cannot verify", ex.Message);
        Assert.Contains("www.microsoft.com", ex.Message);
        Assert.Single(_service.Requests);
        Assert.Empty(_delays.Values);
    }

    [Fact]
    public async Task A_rejected_certificate_on_a_session_call_stops_the_run_too()
    {
        _service.Overrides["tags"] = _ => throw DownloadTestData.RejectedCertificate();
        using var client = CreateClient();

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() => client.GetReleaseAsync(Ct));

        Assert.Contains("vlscppe.microsoft.com", ex.Message);
        Assert.Empty(_service.RequestsTo("skus"));
    }

    [Fact]
    public async Task A_silent_server_times_out_after_two_attempts()
    {
        var handler = new FakeHttpHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = CreateClient(handler,
            new TransportOptions { Delay = _delays.Wait, ResponseTimeout = TimeSpan.FromMilliseconds(80) });

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() => client.GetReleaseAsync(Ct));

        Assert.Equal(ErrorKind.Network, ex.Kind);
        Assert.Contains("did not answer in time", ex.Message);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_cancel_is_passed_on_as_a_cancel()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Action cancel = cts.Cancel;
        _service.Overrides["mdt"] = _ =>
        {
            cancel();
            return null;
        };
        using var client = CreateClient();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetReleaseAsync(cts.Token));

        Assert.Empty(_service.RequestsTo("skus"));
    }

    // ---- session calls ----

    [Fact]
    public async Task Failing_session_calls_do_not_stop_the_run()
    {
        _service.Overrides["tags"] = _ =>
            throw new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known.");
        _service.Overrides["mdt"] = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        using var client = CreateClient();

        var release = await client.GetReleaseAsync(Ct);

        Assert.Equal(6, release.Languages.Count);
        Assert.Empty(_service.RequestsTo("ov-df")); // without the script there is nothing to send
    }

    [Fact]
    public async Task When_the_service_refuses_after_a_blocked_session_call_the_message_names_the_blocked_host()
    {
        _service.Overrides["tags"] = _ =>
            throw new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known.");
        _service.Overrides["skus"] =
            _ => FakeMicrosoftService.Text(Fixture.Read("service-error.json"), "application/json");
        using var client = CreateClient();

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() => client.GetReleaseAsync(Ct));

        AssertRefusedWithOffer(ex);
        Assert.Contains("vlscppe.microsoft.com", ex.Message);
        Assert.Contains("ad blocker", ex.Message);
    }

    [Theory]
    [InlineData("var u=\"https://ov-df.microsoft.com/?PageId=si&w=ABCDEF0123\";u+=\"&rticks=\"+1794323463650;",
        "ABCDEF0123", "1794323463650")]
    [InlineData("var u='/?w=abcdef0123&x=1';var rticks=\"1794323463650\";", "abcdef0123", "1794323463650")]
    [InlineData("w=0;p=2;var u=\"?a=1&w=8DF3F3CD656E26E\";var q=\"&rticks=1794323463650&z=1\";", "8DF3F3CD656E26E",
        "1794323463650")]
    [InlineData("u += '&rticks=' + 1794323463650; v = 'x&w=00FF00FF00'", "00FF00FF00", "1794323463650")]
    public async Task The_two_session_values_are_found_however_the_script_writes_them(string script, string w,
        string rticks)
    {
        _service.Overrides["mdt"] = _ => FakeMicrosoftService.Text(script, "application/javascript");
        using var client = CreateClient();

        await client.GetReleaseAsync(Ct);

        var followUp = _service.RequestsTo("ov-df").Single();
        Assert.Equal(w, followUp.Query("w"));
        Assert.Equal(rticks, followUp.Query("rticks"));
    }

    [Theory]
    [InlineData("var show=1; var nothing='here';")]
    [InlineData("w=0;h=1;var rticks=\"1794323463650\";")] // no w value, only a statement that looks like one
    [InlineData("var u=\"?w=8DF3F3CD656E26E\";var rticks=computeTicks();")] // no number
    [InlineData("var u=\"?w=8DF3F3CD656E26Z\";u+=\"&rticks=\"+1794323463650;")] // w is not hex
    public async Task A_session_script_without_both_values_skips_the_follow_up_call(string script)
    {
        _service.Overrides["mdt"] = _ => FakeMicrosoftService.Text(script, "application/javascript");
        using var client = CreateClient();

        await client.GetReleaseAsync(Ct);

        Assert.Empty(_service.RequestsTo("ov-df"));
        Assert.Single(_service.RequestsTo("skus"));
    }

    // ---- GetDownloadTicketAsync ----

    [Fact]
    public async Task Ticket_for_x64_takes_the_download_type_1_entry()
    {
        using var client = CreateClient();
        var release = await client.GetReleaseAsync(Ct);
        var german = release.Languages.Single(l => l.LocalizedLanguage == "German");

        var ticket = await client.GetDownloadTicketAsync(release, german, Ct);

        Assert.Equal("software.download.prss.microsoft.com", ticket.Uri.Host);
        Assert.Equal("/dbazure/Windows11_Client_x64_de-de_26300_9457.iso", ticket.Uri.AbsolutePath);
        Assert.Contains("sig=SECRETX64", ticket.Uri.Query);
        Assert.Equal("Windows11_Client_x64_de-de_26300_9457.iso", ticket.FileName);
        Assert.Equal(new DateTimeOffset(2026, 11, 11, 14, 3, 11, TimeSpan.Zero), ticket.ExpiresAt);
        Assert.False(ticket.IsExpired(Now, TimeSpan.FromMinutes(1)));
        Assert.True(ticket.IsExpired(Now.AddHours(24), TimeSpan.Zero));

        var request = _service.RequestsTo("links").Single();
        Assert.Equal("/software-download-connector/api/GetProductDownloadLinksBySku", request.Uri.AbsolutePath);
        Assert.Equal("606624d44113", request.Query("profile"));
        Assert.Equal("undefined", request.Query("productEditionId"));
        Assert.Equal("27165", request.Query("SKU"));
        Assert.Equal("undefined", request.Query("friendlyFileName"));
        Assert.Equal("en-US", request.Query("Locale"));

        // The young session of the release is reused: same id, no second round of session calls.
        Assert.Equal(_service.RequestsTo("skus").Single().Query("sessionID"), request.Query("sessionID"));
        Assert.Single(_service.RequestsTo("tags"));
    }

    [Fact]
    public async Task The_text_form_of_a_ticket_does_not_leak_the_signed_link()
    {
        using var client = CreateClient();
        var release = await client.GetReleaseAsync(Ct);
        var ticket = await client.GetDownloadTicketAsync(release, release.Languages[0], Ct);

        var text = ticket.ToString();
        var interpolated = $"{ticket}";

        Assert.DoesNotContain("SECRET", text);
        Assert.DoesNotContain("TOKEN", text);
        Assert.DoesNotContain("sig=", interpolated);
        Assert.Contains("software.download.prss.microsoft.com", text);
        Assert.Contains(ticket.FileName, text);
    }

    [Fact]
    public async Task An_old_session_is_replaced_before_asking_for_a_link()
    {
        using var client = CreateClient();
        var release = await client.GetReleaseAsync(Ct);
        var firstSession = _service.RequestsTo("skus").Single().Query("sessionID");

        _time.Advance(TimeSpan.FromHours(3)); // a download that resumes much later
        var ticket = await client.GetDownloadTicketAsync(release, release.Languages[0], Ct);

        Assert.NotNull(ticket);
        Assert.Equal(2, _service.RequestsTo("tags").Count());
        Assert.Equal(2, _service.RequestsTo("skus").Count());
        var linkSession = _service.RequestsTo("links").Single().Query("sessionID");
        Assert.NotEqual(firstSession, linkSession);
        Assert.Equal(_service.RequestsTo("tags").Last().Query("session_id"), linkSession);
    }

    [Fact]
    public async Task A_release_that_was_not_made_by_this_client_gets_a_new_session()
    {
        using var client = CreateClient();
        var release = new WindowsRelease("3813", [Languages.German with { SkuId = "27165" }], PublishedHashTable.Empty);

        var ticket = await client.GetDownloadTicketAsync(release, release.Languages[0], Ct);

        Assert.Equal("Windows11_Client_x64_de-de_26300_9457.iso", ticket.FileName);
        Assert.Equal(["tags", "mdt", "ov-df", "skus", "links"],
            _service.Requests.Select(r => FakeMicrosoftService.RouteOf(r.Uri)));
    }

    [Fact]
    public async Task A_refused_link_request_is_tried_once_more_with_a_new_session_then_reported()
    {
        using var client = CreateClient();
        var release = await client.GetReleaseAsync(Ct);
        _service.Overrides["links"] =
            _ => FakeMicrosoftService.Text(Fixture.Read("service-error.json"), "application/json");

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() =>
            client.GetDownloadTicketAsync(release, release.Languages[0], Ct));

        AssertRefusedWithOffer(ex);
        Assert.Contains("The request was rejected for this session", ex.Message);
        Assert.Equal(2, _service.RequestsTo("links").Count());
        Assert.NotEqual(_service.RequestsTo("links").First().Query("sessionID"),
            _service.RequestsTo("links").Last().Query("sessionID"));
    }

    [Fact]
    public async Task A_link_request_that_works_with_the_new_session_succeeds()
    {
        using var client = CreateClient();
        var release = await client.GetReleaseAsync(Ct);
        var seen = 0;
        _service.Overrides["links"] = _ =>
            seen++ == 0 ? FakeMicrosoftService.Text(Fixture.Read("service-error.json"), "application/json") : null;

        var ticket = await client.GetDownloadTicketAsync(release, release.Languages[0], Ct);

        Assert.Equal("Windows11_Client_x64_de-de_26300_9457.iso", ticket.FileName);
        Assert.Equal(2, _service.RequestsTo("links").Count());
        Assert.NotEqual(_service.RequestsTo("links").First().Query("sessionID"),
            _service.RequestsTo("links").Last().Query("sessionID"));
    }

    [Theory]
    [InlineData("https://evil.example/Windows11.iso?sig=SECRETSIG", "evil.example")]
    [InlineData("https://software.download.prss.microsoft.com.evil.example/Windows11.iso?sig=SECRETSIG",
        "evil.example")]
    [InlineData("http://software.download.prss.microsoft.com/Windows11.iso?sig=SECRETSIG", "https")]
    [InlineData("https://software.download.prss.microsoft.com/secretpath/setup.exe?sig=SECRETSIG", ".iso")]
    [InlineData("https://www.microsoft.com/Windows11.iso?sig=SECRETSIG", "www.microsoft.com")]
    [InlineData("not a link at all SECRETSIG", "trust")]
    public async Task A_link_that_fails_the_iso_allowlist_is_refused_without_leaking_it(string link,
        string expectedInMessage)
    {
        using var client = CreateClient();
        var release = await client.GetReleaseAsync(Ct);
        _service.Overrides["links"] = _ => FakeMicrosoftService.Text(
            "{\"ProductDownloadOptions\":[{\"Uri\":" + JsonSerializer.Serialize(link) + ",\"DownloadType\":1}]}",
            "application/json");

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() =>
            client.GetDownloadTicketAsync(release, release.Languages[0], Ct));

        AssertRefusedWithOffer(ex);
        Assert.Contains(expectedInMessage, ex.Message);
        Assert.DoesNotContain("SECRETSIG", ex.Message);
        Assert.DoesNotContain("secretpath", ex.Message);
    }

    [Theory]
    [InlineData("{\"ProductDownloadOptions\":[]}")]
    [InlineData(
        "{\"ProductDownloadOptions\":[{\"Uri\":\"https://software.download.prss.microsoft.com/a.iso\",\"DownloadType\":2}]}")] // another type only
    [InlineData(
        "{\"ProductDownloadOptions\":[{\"Uri\":\"https://software.download.prss.microsoft.com/a.iso\"}]}")] // no type
    [InlineData("{\"ValidationContainer\":{}}")]
    public async Task A_link_answer_without_an_entry_for_the_architecture_is_refused(string body)
    {
        using var client = CreateClient();
        var release = await client.GetReleaseAsync(Ct);
        _service.Overrides["links"] = _ => FakeMicrosoftService.Text(body, "application/json");

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() =>
            client.GetDownloadTicketAsync(release, release.Languages[0], Ct));

        AssertRefusedWithOffer(ex);
        Assert.Contains("x64", ex.Message);
    }

    [Fact]
    public async Task Download_type_may_be_text_and_the_expiry_may_be_missing()
    {
        using var client = CreateClient();
        var release = await client.GetReleaseAsync(Ct);
        _service.Overrides["links"] = _ => FakeMicrosoftService.Text(
            "{\"productDownloadOptions\":[{\"uri\":\"https://download.microsoft.com/a/Win%2011.iso?sig=1\",\"downloadType\":\"1\"}]}",
            "application/json");

        var ticket = await client.GetDownloadTicketAsync(release, release.Languages[0], Ct);

        Assert.Null(ticket.ExpiresAt);
        Assert.False(ticket.IsExpired(DateTimeOffset.MaxValue, TimeSpan.Zero));
        Assert.Equal("Win_11.iso", ticket.FileName);
    }

    [Fact]
    public async Task A_language_with_an_unsafe_id_is_a_programming_error()
    {
        using var client = CreateClient();
        var release = await client.GetReleaseAsync(Ct);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.GetDownloadTicketAsync(release, new WindowsLanguage("1&evil=1", "X", "X"), Ct));
    }

    // ---- small parsers ----

    [Theory]
    [InlineData("2026-11-11T14:03:11Z", "2026-11-11T14:03:11+00:00")]
    [InlineData("2026-11-11T14:03:11", "2026-11-11T14:03:11+00:00")] // no offset: UTC
    [InlineData("2026-11-11T15:03:11+01:00", "2026-11-11T14:03:11+00:00")]
    [InlineData("2026-11-11T14:03:11.1234567Z", "2026-11-11T14:03:11.1234567+00:00")]
    [InlineData("11/11/2026 14:03:11", "2026-11-11T14:03:11+00:00")]
    [InlineData("/Date(1794405791000)/", "2026-11-11T14:03:11+00:00")]
    public void Expiry_is_parsed_in_the_usual_formats(string text, string expected)
    {
        Assert.Equal(DateTimeOffset.Parse(expected, CultureInfo.InvariantCulture),
            MicrosoftDownloadClient.ParseExpiry(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tomorrow")]
    [InlineData("/Date(abc)/")]
    public void An_unreadable_expiry_is_unknown_not_an_error(string? text)
    {
        Assert.Null(MicrosoftDownloadClient.ParseExpiry(text));
    }

    [Theory]
    [InlineData("https://h/dbazure/Windows11_Client_x64_en-us_26300_9457.iso?t=1",
        "Windows11_Client_x64_en-us_26300_9457.iso")]
    [InlineData("https://h/a/b/Win11%20x64.ISO", "Win11_x64.iso")]
    [InlineData("https://h/a/..%2F..%2Fevil.iso", "evil.iso")]
    [InlineData("https://h/a/%2E%2E%5C%2E%2E%5Cevil.iso", "evil.iso")]
    [InlineData("https://h/a/CON.iso", "Windows11_x64_27160.iso")]
    [InlineData("https://h/a/.iso", "Windows11_x64_27160.iso")]
    [InlineData("https://h/a/name%3Astream.iso", "name_stream.iso")]
    public void File_names_from_links_are_made_safe(string link, string expected)
    {
        var name = MicrosoftDownloadClient.SafeIsoFileName(new Uri(link), "27160");

        Assert.Equal(expected, name);
        Assert.Equal(name, Path.GetFileName(name));
        Assert.True(name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
    }

    // ---- TLS fallback ----

    [Fact]
    public void A_reset_during_the_handshake_is_told_apart_from_a_rejected_certificate()
    {
        var reset = new HttpRequestException(
            HttpRequestError.SecureConnectionError,
            "The SSL connection could not be established, see inner exception.",
            new IOException("Unable to read data from the transport connection.", new SocketException(10054)));
        var badCertificate = DownloadTestData.RejectedCertificate();
        var certificateThenReset = new HttpRequestException(
            HttpRequestError.SecureConnectionError, "x", new AuthenticationException("y", new IOException("z")));
        var plainReset = new HttpRequestException(HttpRequestError.ConnectionError, "reset", new IOException("reset"));

        Assert.True(TransportErrors.IsHandshakeReset(reset));
        Assert.False(TransportErrors.IsCertificateFailure(reset));

        Assert.False(TransportErrors.IsHandshakeReset(badCertificate));
        Assert.True(TransportErrors.IsCertificateFailure(badCertificate));

        Assert.False(TransportErrors.IsHandshakeReset(certificateThenReset));
        Assert.False(TransportErrors.IsHandshakeReset(plainReset));
        Assert.False(TransportErrors.IsHandshakeReset(new IOException("not http")));
    }

    [Fact]
    public async Task After_a_handshake_reset_requests_go_through_the_fallback_handler_for_good()
    {
        var primary = new FakeHttpHandler(_ => throw new HttpRequestException(
            HttpRequestError.SecureConnectionError, "handshake", new IOException("reset", new SocketException(10054))));
        var fallback = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var created = 0;
        using var handler = new TlsFallbackHandler(primary, () =>
        {
            created++;
            return fallback;
        });
        using var invoker = new HttpMessageInvoker(handler);

        using var first =
            await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://www.microsoft.com/"), Ct);
        using var second =
            await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://www.microsoft.com/"), Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Single(primary.Requests); // only the request that hit the reset
        Assert.Equal(2, fallback.Requests.Count);
        Assert.Equal(1, created);
    }

    [Fact]
    public async Task A_rejected_certificate_never_switches_to_the_fallback_handler()
    {
        var primary = new FakeHttpHandler(_ => throw DownloadTestData.RejectedCertificate());
        var fallback = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var handler = new TlsFallbackHandler(primary, () => fallback);
        using var invoker = new HttpMessageInvoker(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://www.microsoft.com/"), Ct));

        Assert.Empty(fallback.Requests);
    }

    [Fact]
    public void The_default_handler_does_not_follow_redirects()
    {
        using var handler = DownloadTransport.CreateDefaultHandler();

        // It is accepted by the constructors, which reject handlers that follow redirects themselves.
        using var client = new MicrosoftDownloadClient(handler);
        Assert.IsType<TlsFallbackHandler>(handler);
    }
}