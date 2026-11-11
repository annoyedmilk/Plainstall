using System.Net;
using System.Text;
using Plainstall.Core.Release;

namespace Plainstall.Core.Tests.Release;

public class GitHubUpdateCheckerTests
{
    private static FakeHandler Answer(HttpStatusCode status, string body)
    {
        return new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") }));
    }

    private static string Release(string tag, bool draft = false, bool prerelease = false, string? url = null)
    {
        return $$"""
                 { "tag_name": "{{tag}}", "name": "Plainstall {{tag}}", "draft": {{(draft ? "true" : "false")}}, "prerelease": {{(prerelease ? "true" : "false")}},
                   "html_url": "{{url ?? "https://github.com/annoyedmilk/Plainstall/releases/tag/" + tag}}" }
                 """;
    }

    private static Task<UpdateInfo?> Check(FakeHandler handler, string current = "1.0.0")
    {
        return new GitHubUpdateChecker(handler: handler).CheckAsync(Version.Parse(current),
            TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("v1.1.0", "1.0.0", true)]
    [InlineData("1.1.0", "1.0.0", true)]
    [InlineData("v2.0", "1.9.9", true)]
    [InlineData("v1.0.0.1", "1.0.0", true)]
    [InlineData("v1.0.0", "1.0.0", false)]
    [InlineData("v1.0", "1.0.0", false)]
    [InlineData("v1.0.0", "1.0.0.0", false)]
    [InlineData("v0.9.0", "1.0.0", false)]
    [InlineData("v1.1.0-rc.1", "1.0.0", true)]
    [InlineData("release-candidate", "1.0.0", false)]
    [InlineData("", "1.0.0", false)]
    public async Task Only_a_strictly_newer_release_is_reported(string tag, string current, bool expected)
    {
        var info = await Check(Answer(HttpStatusCode.OK, Release(tag)), current);
        Assert.Equal(expected, info is not null);
    }

    [Fact]
    public async Task The_result_carries_version_and_page()
    {
        var info = await Check(Answer(HttpStatusCode.OK, Release("v1.2.3")));
        Assert.Equal(new Version(1, 2, 3), info!.Version);
        Assert.Equal("https://github.com/annoyedmilk/Plainstall/releases/tag/v1.2.3", info.PageUrl.ToString());
    }

    [Fact]
    public async Task Drafts_and_prereleases_are_ignored()
    {
        Assert.Null(await Check(Answer(HttpStatusCode.OK, Release("v9.0.0", true))));
        Assert.Null(await Check(Answer(HttpStatusCode.OK, Release("v9.0.0", prerelease: true))));
    }

    [Theory]
    [InlineData("http://github.com/annoyedmilk/Plainstall/releases/tag/v9")]
    [InlineData("https://github.com.evil.example/annoyedmilk/Plainstall")]
    [InlineData("https://evil.example/github.com/")]
    [InlineData("https://user@github.com/annoyedmilk/Plainstall")]
    [InlineData("https://github.com:8443/annoyedmilk/Plainstall")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a url")]
    public async Task A_link_that_does_not_point_at_github_is_never_passed_on(string url)
    {
        Assert.Null(await Check(Answer(HttpStatusCode.OK, Release("v9.0.0", url: url))));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Error_answers_mean_no_notice_not_an_error(HttpStatusCode status)
    {
        Assert.Null(await Check(Answer(status, "{\"message\":\"nope\"}")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>captive portal</html>")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"tag_name\": 5}")]
    public async Task Garbage_answers_mean_no_notice(string body)
    {
        Assert.Null(await Check(Answer(HttpStatusCode.OK, body)));
    }

    [Fact]
    public async Task An_oversized_answer_is_dropped()
    {
        var huge = "{\"tag_name\":\"v9.0.0\",\"html_url\":\"https://github.com/a/b\",\"padding\":\"" +
                   new string('x', GitHubUpdateChecker.MaxResponseBytes) + "\"}";
        Assert.Null(await Check(Answer(HttpStatusCode.OK, huge)));
    }

    [Fact]
    public async Task Being_offline_means_no_notice()
    {
        var offline = new FakeHandler((_, _) => throw new HttpRequestException("No such host is known."));
        Assert.Null(await Check(offline));
    }

    [Fact]
    public async Task A_slow_server_is_given_up_on()
    {
        var slow = new FakeHandler(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var checker = new GitHubUpdateChecker(handler: slow, timeout: TimeSpan.FromMilliseconds(100));
        Assert.Null(await checker.CheckAsync(new Version(1, 0), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cancelling_is_passed_on_to_the_caller()
    {
        using var cancel = new CancellationTokenSource();
        var cancelAsync = cancel.CancelAsync;
        var handler = new FakeHandler(async (_, token) =>
        {
            await cancelAsync();
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var checker = new GitHubUpdateChecker(handler: handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            checker.CheckAsync(new Version(1, 0), cancel.Token));
    }

    [Fact]
    public async Task A_redirect_inside_the_api_host_is_followed_and_one_to_another_host_is_not()
    {
        var inside = new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/moved")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Release("v9.0.0")) }
            : new HttpResponseMessage(HttpStatusCode.MovedPermanently)
                { Headers = { Location = new Uri("https://api.github.com/repositories/1/releases/moved") } }));
        Assert.NotNull(await Check(inside));
        Assert.Equal(2, inside.Requests.Count);

        var outside = new FakeHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Found)
                { Headers = { Location = new Uri("https://example.com/releases/latest") } }));
        Assert.Null(await Check(outside));
        Assert.Single(outside.Requests);

        var downgrade = new FakeHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Found)
                { Headers = { Location = new Uri("http://api.github.com/repos/a/b/releases/latest") } }));
        Assert.Null(await Check(downgrade));
        Assert.Single(downgrade.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.SeeOther)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task Every_redirect_status_is_followed(HttpStatusCode status)
    {
        var handler = new FakeHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/moved")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Release("v9.0.0")) }
                : new HttpResponseMessage(status)
                    { Headers = { Location = new Uri("https://api.github.com/repositories/1/releases/moved") } }));
        Assert.NotNull(await Check(handler));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Redirects_do_not_loop_for_ever()
    {
        var loop = new FakeHandler((request, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = request.RequestUri } }));
        Assert.Null(await Check(loop));
        Assert.Equal(GitHubUpdateChecker.MaxRedirects + 1, loop.Requests.Count);
    }

    [Fact]
    public async Task The_request_goes_to_the_releases_api_and_says_nothing_about_the_user()
    {
        var handler = Answer(HttpStatusCode.OK, Release("v1.0.0"));
        await Check(handler, "1.4.2");

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.github.com/repos/annoyedmilk/Plainstall/releases/latest",
            request.RequestUri!.ToString());
        Assert.Equal("Plainstall/1.4.2", request.Headers.UserAgent.ToString());
        Assert.Equal("application/vnd.github+json", request.Headers.Accept.ToString());
        // Nothing else: no token, no cookie, no machine or user name.
        Assert.Equal(["Accept", "User-Agent"], request.Headers.Select(h => h.Key).Order());
        Assert.Null(request.Content);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return respond(request, cancellationToken);
        }
    }
}