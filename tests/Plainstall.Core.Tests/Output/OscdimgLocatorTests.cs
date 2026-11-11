using System.Net;
using Plainstall.Core.Output;

namespace Plainstall.Core.Tests.Output;

public class OscdimgLocatorTests
{
    private const string BlobHost = "vsblobprodscussu5shard61.blob.core.windows.net";
    private const string BlobUrl = "https://" + BlobHost + "/b-1234/ABCDEF.blob?sv=2019-07-07&sig=secret";

    /// <summary>Stands in for the tool. Its hash plays the role of the pinned one in these tests.</summary>
    private static readonly byte[] ToolBytes = [.. "MZ fake oscdimg"u8, .. new byte[2000]];

    private static readonly string ToolHash = HashHex.Sha256Of(ToolBytes);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void Pinned_values_match_the_known_download()
    {
        Assert.Equal("https://msdl.microsoft.com/download/symbols/oscdimg.exe/688CABB065000/oscdimg.exe",
            OscdimgLocator.DownloadUrl);
        Assert.Equal("2000160B2C5044691B2F9A0AC308E5207F273D4880A572457AF16D05886BA861", OscdimgLocator.PinnedSha256);
        Assert.True(HashHex.IsSha256(OscdimgLocator.PinnedSha256));
        Assert.Equal(["msdl.microsoft.com", BlobHost], OscdimgLocator.AllowedHosts);
        Assert.Equal(1024 * 1024, OscdimgLocator.MaxDownloadBytes);
        Assert.Equal(5, OscdimgLocator.MaxRedirects);
        Assert.Equal(OscdimgLocator.PinnedSha256, new OscdimgLocatorOptions { ToolsFolder = "x" }.ExpectedSha256);
    }

    [Fact]
    public async Task Good_download_is_verified_stored_and_returned()
    {
        using var fixture = new Fixture();
        var handler = FakeHandler.ServingLikeMicrosoft(ToolBytes);

        var tool = await new OscdimgLocator(fixture.Options(), handler).LocateAsync(Token);

        Assert.Equal(fixture.CachedPath, tool);
        Assert.Equal(ToolBytes, await File.ReadAllBytesAsync(tool, Token));
        Assert.Equal([fixture.CachedPath], fixture.ToolsFolderFiles()); // no temp file left behind
        Assert.Equal([OscdimgLocator.DownloadUrl, BlobUrl], handler.Requests.Select(u => u.AbsoluteUri));
    }

    [Fact]
    public async Task Cached_copy_is_used_without_network_and_hashed_again()
    {
        using var fixture = new Fixture();
        await new OscdimgLocator(fixture.Options(), FakeHandler.ServingLikeMicrosoft(ToolBytes)).LocateAsync(Token);
        var offline = new FakeHandler();

        var tool = await new OscdimgLocator(fixture.Options(), offline).LocateAsync(Token);

        Assert.Equal(fixture.CachedPath, tool);
        Assert.Empty(offline.Requests);
    }

    [Fact]
    public async Task Download_with_the_wrong_hash_writes_nothing()
    {
        using var fixture = new Fixture();
        var handler = FakeHandler.ServingLikeMicrosoft([.. ToolBytes, 0x00]);

        var ex = await Assert.ThrowsAsync<PlainstallException>(() =>
            new OscdimgLocator(fixture.Options(), handler).LocateAsync(Token));

        Assert.Equal(ErrorKind.Verification, ex.Kind);
        Assert.Contains("was not saved", ex.Message);
        Assert.Empty(fixture.ToolsFolderFiles());
        Assert.False(Directory.Exists(fixture.ToolsFolder)); // not even the folder was created
    }

    [Theory]
    [InlineData("https://evil.example.com/oscdimg.exe")]
    [InlineData("https://msdl.microsoft.com.evil.example.com/oscdimg.exe")]
    [InlineData("https://evil.blob.core.windows.net/oscdimg.exe")] // another storage account on the same service
    [InlineData("http://" + BlobHost + "/oscdimg.exe")] // not https
    [InlineData("https://" + BlobHost + ":8443/oscdimg.exe")] // not the default port
    [InlineData("https://msdl.microsoft.com@evil.example.com/oscdimg.exe")] // user-info trick
    [InlineData("https://user:pw@" + BlobHost + "/oscdimg.exe")]
    [InlineData("ftp://msdl.microsoft.com/oscdimg.exe")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    public async Task Redirect_to_a_place_that_is_not_allowed_stops_the_download(string target)
    {
        using var fixture = new Fixture();
        var handler = new FakeHandler().Redirect(OscdimgLocator.DownloadUrl, target).Body(target, ToolBytes);

        var ex = await Assert.ThrowsAsync<PlainstallException>(() =>
            new OscdimgLocator(fixture.Options(), handler).LocateAsync(Token));

        Assert.Equal(ErrorKind.Verification, ex.Kind);
        Assert.Contains("not one of the Microsoft servers", ex.Message);
        Assert.Equal([OscdimgLocator.DownloadUrl],
            handler.Requests.Select(u => u.AbsoluteUri)); // the target was never contacted
        Assert.Empty(fixture.ToolsFolderFiles());
    }

    [Fact]
    public async Task Relative_redirect_stays_on_the_allowed_host()
    {
        using var fixture = new Fixture();
        var handler = new FakeHandler()
            .Redirect(OscdimgLocator.DownloadUrl, "/download/other/oscdimg.exe", HttpStatusCode.TemporaryRedirect)
            .Body("https://msdl.microsoft.com/download/other/oscdimg.exe", ToolBytes);

        Assert.Equal(fixture.CachedPath, await new OscdimgLocator(fixture.Options(), handler).LocateAsync(Token));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public async Task At_most_five_redirects_are_followed(int redirects, bool expectSuccess)
    {
        using var fixture = new Fixture();
        var handler = new FakeHandler();
        var url = OscdimgLocator.DownloadUrl;
        for (var i = 1; i <= redirects; i++)
        {
            var next = $"https://{BlobHost}/hop{i}";
            handler.Redirect(url, next);
            url = next;
        }

        handler.Body(url, ToolBytes);
        var locator = new OscdimgLocator(fixture.Options(), handler);

        if (expectSuccess)
        {
            Assert.Equal(fixture.CachedPath, await locator.LocateAsync(Token));
            Assert.Equal(redirects + 1, handler.Requests.Count);
        }
        else
        {
            var ex = await Assert.ThrowsAsync<PlainstallException>(() => locator.LocateAsync(Token));
            Assert.Equal(ErrorKind.Network, ex.Kind);
            Assert.Contains("redirected more than 5 times", ex.Message);
            Assert.Equal(6, handler.Requests.Count);
            Assert.Empty(fixture.ToolsFolderFiles());
        }
    }

    [Fact]
    public async Task Redirect_without_a_location_fails()
    {
        using var fixture = new Fixture();
        var handler = new FakeHandler().Respond(OscdimgLocator.DownloadUrl,
            () => new HttpResponseMessage(HttpStatusCode.Found));

        var ex = await Assert.ThrowsAsync<PlainstallException>(() =>
            new OscdimgLocator(fixture.Options(), handler).LocateAsync(Token));

        Assert.Equal(ErrorKind.Network, ex.Kind);
    }

    [Fact]
    public async Task Oversized_body_with_a_content_length_is_refused_without_reading_it()
    {
        using var fixture = new Fixture();
        var body = new CountingStream(OscdimgLocator.MaxDownloadBytes + 1L);
        var handler = new FakeHandler()
            .Redirect(OscdimgLocator.DownloadUrl, BlobUrl)
            .Respond(BlobUrl, () =>
            {
                var content = new StreamContent(body);
                content.Headers.ContentLength = OscdimgLocator.MaxDownloadBytes + 1L;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            });

        var ex = await Assert.ThrowsAsync<PlainstallException>(() =>
            new OscdimgLocator(fixture.Options(), handler).LocateAsync(Token));

        Assert.Equal(ErrorKind.Verification, ex.Kind);
        Assert.Contains("more than 1024 KB", ex.Message);
        Assert.Equal(0, body.BytesRead);
        Assert.Empty(fixture.ToolsFolderFiles());
    }

    [Fact]
    public async Task Oversized_body_without_a_content_length_is_cut_off_at_the_limit()
    {
        using var fixture = new Fixture();
        var body = new CountingStream(long.MaxValue); // endless
        var handler = new FakeHandler()
            .Redirect(OscdimgLocator.DownloadUrl, BlobUrl)
            .Respond(BlobUrl,
                () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(body) });

        var ex = await Assert.ThrowsAsync<PlainstallException>(() =>
            new OscdimgLocator(fixture.Options(), handler).LocateAsync(Token));

        Assert.Equal(ErrorKind.Verification, ex.Kind);
        Assert.InRange(body.BytesRead, OscdimgLocator.MaxDownloadBytes, OscdimgLocator.MaxDownloadBytes + 256 * 1024);
        Assert.Empty(fixture.ToolsFolderFiles());
    }

    [Fact]
    public async Task Body_of_exactly_the_limit_is_still_read()
    {
        using var fixture = new Fixture();
        var body = new byte[OscdimgLocator.MaxDownloadBytes];
        var options = fixture.Options() with { ExpectedSha256 = HashHex.Sha256Of(body) };

        var tool = await new OscdimgLocator(options, FakeHandler.ServingLikeMicrosoft(body)).LocateAsync(Token);

        Assert.Equal(OscdimgLocator.MaxDownloadBytes, new FileInfo(tool).Length);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Http_error_is_a_network_failure(HttpStatusCode status)
    {
        using var fixture = new Fixture();
        var handler = new FakeHandler().Respond(OscdimgLocator.DownloadUrl, () => new HttpResponseMessage(status));

        var ex = await Assert.ThrowsAsync<PlainstallException>(() =>
            new OscdimgLocator(fixture.Options(), handler).LocateAsync(Token));

        Assert.Equal(ErrorKind.Network, ex.Kind);
        Assert.Contains($"HTTP {(int)status}", ex.Message);
    }

    [Fact]
    public async Task Connection_failure_is_a_network_failure()
    {
        using var fixture = new Fixture();
        var handler = new FakeHandler().Respond(OscdimgLocator.DownloadUrl,
            () => throw new HttpRequestException("No such host is known."));

        var ex = await Assert.ThrowsAsync<PlainstallException>(() =>
            new OscdimgLocator(fixture.Options(), handler).LocateAsync(Token));

        Assert.Equal(ErrorKind.Network, ex.Kind);
        Assert.Contains("No such host is known.", ex.Message);
    }

    [Fact]
    public async Task Cached_file_with_the_wrong_hash_is_discarded_and_downloaded_again()
    {
        using var fixture = new Fixture();
        fixture.Folder.WriteFile(@"tools\oscdimg.exe", [.. "MZ tampered"u8]);
        var handler = FakeHandler.ServingLikeMicrosoft(ToolBytes);

        Assert.Equal(fixture.CachedPath, await new OscdimgLocator(fixture.Options(), handler).LocateAsync(Token));
        Assert.Equal(ToolBytes, await File.ReadAllBytesAsync(fixture.CachedPath, Token));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Cached_file_with_the_wrong_hash_is_deleted_even_when_no_download_is_allowed()
    {
        using var fixture = new Fixture();
        fixture.Folder.WriteFile(@"tools\oscdimg.exe", [.. "MZ tampered"u8]);
        var handler = FakeHandler.ServingLikeMicrosoft(ToolBytes);
        var signatures = new FakeSignatureVerifier(true);

        var ex = await Assert.ThrowsAsync<PlainstallException>(() =>
            new OscdimgLocator(fixture.Options(false), handler, null, signatures).LocateAsync(Token));

        Assert.Equal(ErrorKind.Preflight, ex.Kind);
        Assert.Contains("Windows ADK", ex.Message);
        Assert.False(File.Exists(fixture.CachedPath));
        Assert.Empty(handler.Requests);
        Assert.Empty(signatures.Checked); // a signature never rescues a cached file; only the pinned hash does
    }

    [Fact]
    public async Task Cached_file_is_discarded_when_a_failed_download_follows()
    {
        using var fixture = new Fixture();
        fixture.Folder.WriteFile(@"tools\oscdimg.exe", [.. "MZ tampered"u8]);

        await Assert.ThrowsAsync<PlainstallException>(() =>
            new OscdimgLocator(fixture.Options(), new FakeHandler()).LocateAsync(Token));

        Assert.Empty(fixture.ToolsFolderFiles());
    }

    [Fact]
    public async Task Adk_copy_with_a_microsoft_signature_wins_over_cache_and_download()
    {
        using var fixture = new Fixture();
        var adk = fixture.Folder.WriteFile(@"adk\Deployment Tools\amd64\Oscdimg\oscdimg.exe",
            [.. "MZ newer adk build"u8]);
        fixture.Folder.WriteFile(@"tools\oscdimg.exe", ToolBytes);
        var handler = FakeHandler.ServingLikeMicrosoft(ToolBytes);
        var signatures = new FakeSignatureVerifier(true);

        var tool = await new OscdimgLocator(fixture.Options(), handler, null, signatures).LocateAsync(Token);

        Assert.Equal(adk, tool);
        Assert.Equal([adk], signatures.Checked);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Adk_copy_with_the_pinned_hash_needs_no_signature()
    {
        using var fixture = new Fixture();
        var adk = fixture.Folder.WriteFile(@"adk\Deployment Tools\amd64\Oscdimg\oscdimg.exe", ToolBytes);
        var signatures = new FakeSignatureVerifier(false);

        var tool = await new OscdimgLocator(fixture.Options(false), null, null, signatures).LocateAsync(Token);

        Assert.Equal(adk, tool);
        Assert.Empty(signatures.Checked);
    }

    [Fact]
    public async Task Adk_copy_without_a_valid_signature_is_ignored_and_left_alone()
    {
        using var fixture = new Fixture();
        var adk = fixture.Folder.WriteFile(@"adk\Deployment Tools\amd64\Oscdimg\oscdimg.exe",
            [.. "MZ something else"u8]);
        var handler = FakeHandler.ServingLikeMicrosoft(ToolBytes);
        var signatures = new FakeSignatureVerifier(false);

        var tool = await new OscdimgLocator(fixture.Options(), handler, null, signatures).LocateAsync(Token);

        Assert.Equal(fixture.CachedPath, tool);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal([adk], signatures.Checked);
        Assert.True(File.Exists(adk)); // Plainstall never deletes outside its own folders
    }

    [Fact]
    public async Task Adk_tool_is_taken_from_the_amd64_folder_only()
    {
        using var fixture = new Fixture();
        var amd64 = fixture.Folder.WriteFile(@"adk\Deployment Tools\amd64\Oscdimg\oscdimg.exe", [1]);
        fixture.Folder.WriteFile(@"adk\Deployment Tools\arm64\Oscdimg\oscdimg.exe", [2]);
        var signatures = new FakeSignatureVerifier(true);

        var tool = await new OscdimgLocator(fixture.Options(), null, null, signatures).LocateAsync(Token);

        Assert.Equal(amd64, tool);
        Assert.Equal([amd64], signatures.Checked);
    }

    [Fact]
    public async Task Nothing_found_and_download_switched_off_explains_what_to_do()
    {
        using var fixture = new Fixture();

        var ex = await Assert.ThrowsAsync<PlainstallException>(() =>
            new OscdimgLocator(fixture.Options(false)).LocateAsync(Token));

        Assert.Equal(ErrorKind.Preflight, ex.Kind);
        Assert.Contains("Windows ADK", ex.Message);
    }

    [Fact]
    public async Task Cancellation_is_passed_on_and_writes_nothing()
    {
        using var fixture = new Fixture();
        using var cancelled = new CancellationTokenSource();
        Action cancel = cancelled.Cancel;
        var token = cancelled.Token;
        var handler = new FakeHandler().Respond(OscdimgLocator.DownloadUrl, () =>
        {
            cancel();
            throw new OperationCanceledException(token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new OscdimgLocator(fixture.Options(), handler).LocateAsync(cancelled.Token));

        Assert.Empty(fixture.ToolsFolderFiles());
    }

    [Fact]
    public void Default_adk_folder_is_under_program_files_x86()
    {
        Assert.EndsWith(@"Windows Kits\10\Assessment and Deployment Kit\Deployment Tools",
            OscdimgLocator.DefaultAdkDeploymentToolsFolder);
        Assert.True(Path.IsPathRooted(OscdimgLocator.DefaultAdkDeploymentToolsFolder));
    }

    /// <summary>Answers requests from a table of URL to response and records every URL asked for.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

        public List<Uri> Requests { get; } = [];

        public FakeHandler Redirect(string from, string to, HttpStatusCode status = HttpStatusCode.Found)
        {
            _routes[from] = () =>
            {
                var response = new HttpResponseMessage(status);
                response.Headers.TryAddWithoutValidation("Location", to);
                return response;
            };
            return this;
        }

        public FakeHandler Body(string url, byte[] body)
        {
            return Respond(url,
                () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }

        public FakeHandler Respond(string url, Func<HttpResponseMessage> response)
        {
            _routes[url] = response;
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return _routes.TryGetValue(request.RequestUri!.AbsoluteUri, out var route)
                ? Task.FromResult(route())
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        /// <summary>The usual answer of the symbol server: one redirect to the storage host, then the file.</summary>
        public static FakeHandler ServingLikeMicrosoft(byte[] body)
        {
            return new FakeHandler().Redirect(OscdimgLocator.DownloadUrl, BlobUrl).Body(BlobUrl, body);
        }
    }

    private sealed class FakeSignatureVerifier(bool trusted) : IFileSignatureVerifier
    {
        public List<string> Checked { get; } = [];

        public SignatureCheckResult Verify(string path)
        {
            Checked.Add(path);
            return new SignatureCheckResult(trusted, trusted ? "CN=Microsoft Corporation" : null, false,
                trusted ? "Signed." : "The file has no signature.");
        }
    }

    private sealed class Fixture : IDisposable
    {
        public TempDirectory Folder { get; } = new();
        public string ToolsFolder => Folder.Combine("tools");
        private string AdkFolder => Folder.Combine("adk", "Deployment Tools");
        public string CachedPath => Path.Combine(ToolsFolder, "oscdimg.exe");

        public void Dispose()
        {
            Folder.Dispose();
        }

        public OscdimgLocatorOptions Options(bool allowDownload = true)
        {
            return new OscdimgLocatorOptions
            {
                ToolsFolder = ToolsFolder,
                AdkDeploymentToolsFolder = AdkFolder,
                AllowDownload = allowDownload,
                ExpectedSha256 = ToolHash
            };
        }

        public string[] ToolsFolderFiles()
        {
            return Directory.Exists(ToolsFolder)
                ? Directory.GetFiles(ToolsFolder, "*", SearchOption.AllDirectories)
                : [];
        }
    }

    /// <summary>A stream of zeros that counts how much of it was read.</summary>
    private sealed class CountingStream(long length) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => BytesRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var take = (int)Math.Min(count, length - BytesRead);
            Array.Clear(buffer, offset, take);
            BytesRead += take;
            return take;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>Content that, like a chunked response, does not announce its length.</summary>
    private sealed class UnknownLengthContent(Stream stream) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context)
        {
            return stream.CopyToAsync(target);
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            return Task.FromResult(stream);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}