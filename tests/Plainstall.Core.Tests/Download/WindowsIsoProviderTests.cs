using System.Globalization;
using Plainstall.Core.Download;
using static Plainstall.Core.Tests.Download.DownloadTestData;

namespace Plainstall.Core.Tests.Download;

public sealed class WindowsIsoProviderTests : IDisposable
{
    private const string CurrentFile = "Windows11_Client_x64_de-de_26300_9457.iso";
    private const string OlderFile = "Windows11_Client_x64_de-de_26300_9000.iso";
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    private readonly IsoCache _cache;
    private readonly FakeClient _client;
    private readonly ResumableDownloader _downloader;
    private readonly SyncProgress<WindowsIsoProgress> _progress = new();
    private readonly WindowsIsoProvider _provider;
    private readonly FakeIsoServer _server = new(60_000);

    private readonly TempDirectory _temp = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 10, 14, 0, 0, TimeSpan.Zero));

    public WindowsIsoProviderTests()
    {
        _cache = new IsoCache(Path.Combine(_temp.Path, "cache"), _time);
        _downloader = new ResumableDownloader(_server.Handler, _time, null, new ResumableDownloaderOptions
        {
            Transport = new TransportOptions { Delay = (_, _) => Task.CompletedTask }
        });
        _client = new FakeClient
        {
            Release = ReleaseWith((Languages.German, _server.Sha256), (Languages.EnglishUs, new string('E', 64)),
                (Languages.French, new string('F', 64)))
        };
        _provider = new WindowsIsoProvider(_client, _downloader, _cache);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _downloader.Dispose();
        _temp.Dispose();
    }

    private static WindowsRelease ReleaseWith(params (WindowsLanguage Language, string Sha256)[] published)
    {
        return new WindowsRelease(
            "3813",
            [Languages.German, Languages.EnglishUs, Languages.French, Languages.Japanese],
            HashTable([.. published.Select(p => (PublishedHashTable.LabelFor(p.Language), p.Sha256))]))
        {
            ProductDisplayName = "Windows 11 Client - Build 26300.9457",
            Build = "26300.9457"
        };
    }

    // ---- download ----

    [Fact]
    public async Task Downloads_verifies_and_caches_when_the_cache_is_empty()
    {
        var result = await _provider.GetIsoAsync(German, _progress, Ct);

        Assert.Equal(_cache.GetPathFor(CurrentFile), result.Path);
        Assert.Equal(_server.Content, await File.ReadAllBytesAsync(result.Path, Ct));
        Assert.Equal(_server.Sha256, result.Sha256);
        Assert.True(result.VerifiedAgainstMicrosoft);
        Assert.Same(Languages.German, _client.LastTicketLanguage);

        var cached = Assert.Single(_cache.FindAll(Languages.German));
        Assert.True(cached.VerifiedAgainstMicrosoft);
        Assert.Equal("26300.9457", cached.Build);
        Assert.Equal("Windows 11 Client - Build 26300.9457", cached.ProductName);
        Assert.Equal(_server.Content.Length, cached.SizeBytes);
        Assert.Equal(["Windows11_Client_x64_de-de_26300_9457.iso", "Windows11_Client_x64_de-de_26300_9457.iso.json"],
            Directory.GetFiles(_cache.Folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));

        Assert.Equal(WindowsIsoPhase.ContactingMicrosoft, _progress.Values[0].Phase);
        Assert.Contains(_progress.Values,
            p => p.Phase == WindowsIsoPhase.Downloading &&
                 p.Transfer == new TransferProgress(_server.Content.Length, _server.Content.Length));
        Assert.Contains(_progress.Values, p => p is { Phase: WindowsIsoPhase.Verifying, Transfer: not null });
        Assert.Equal(1, _client.ReleaseCalls);
        Assert.Equal(1, _client.TicketCalls);
    }

    [Fact]
    public async Task The_second_request_is_served_from_the_cache_after_a_full_hash_check()
    {
        var first = await _provider.GetIsoAsync(German, null, Ct);
        var requestsAfterFirst = _server.Requests.Count;

        var second = await _provider.GetIsoAsync(German, _progress, Ct);

        Assert.Equal(first.Path, second.Path);
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.True(second.VerifiedAgainstMicrosoft);
        Assert.Equal(requestsAfterFirst, _server.Requests.Count);
        Assert.Equal(1, _client.TicketCalls);
        Assert.Contains(_progress.Values,
            p => p.Phase == WindowsIsoPhase.CheckingCache &&
                 p.Transfer == new TransferProgress(_server.Content.Length, _server.Content.Length));
        Assert.DoesNotContain(_progress.Values, p => p.Phase == WindowsIsoPhase.Downloading);
    }

    [Fact]
    public async Task A_link_refused_during_the_download_is_replaced_through_the_client()
    {
        _client.Tokens = ["expired", "good"];

        var result = await _provider.GetIsoAsync(German, null, Ct);

        Assert.True(result.VerifiedAgainstMicrosoft);
        Assert.Equal(2, _client.TicketCalls);
    }

    [Fact]
    public async Task A_cancelled_download_stays_in_the_cache_folder_and_is_continued_by_the_next_call()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Action cancel = cts.Cancel;
        _server.Body = (number, bytes) =>
            number == 0 ? new FaultStream(bytes, 20_000, FaultStream.Fault.Hang, cancel) : null;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _provider.GetIsoAsync(German, null, cts.Token));
        Assert.True(File.Exists(_cache.GetPathFor(CurrentFile) + ".part"));
        Assert.Empty(_cache.FindAll(Languages.German));

        var result = await _provider.GetIsoAsync(German, null, Ct);

        Assert.True(result.VerifiedAgainstMicrosoft);
        Assert.Equal(20_000, _server.Requests[^1].RangeFrom);
        Assert.Equal(_server.Content, await File.ReadAllBytesAsync(result.Path, Ct));
    }

    // ---- language ----

    [Theory]
    [InlineData("de-DE")]
    [InlineData("de-AT")]
    public async Task The_display_language_of_the_pc_picks_the_iso_language(string culture)
    {
        await _provider.GetIsoAsync(CultureInfo.GetCultureInfo(culture), null, Ct);

        Assert.Same(Languages.German, _client.LastTicketLanguage);
    }

    [Fact]
    public async Task A_display_language_without_an_iso_falls_back_to_us_english()
    {
        _client.Release = ReleaseWith((Languages.German, new string('D', 64)), (Languages.EnglishUs, _server.Sha256));
        _client.FileName = "Windows11_Client_x64_en-us_26300_9457.iso";

        var result = await _provider.GetIsoAsync(CultureInfo.GetCultureInfo("ca-ES"), null, Ct);

        Assert.True(result.VerifiedAgainstMicrosoft); // against the "English 64-bit" row
        Assert.Same(Languages.EnglishUs, _client.LastTicketLanguage);
    }

    // ---- verification policy ----

    [Fact]
    public async Task Without_a_published_hash_for_the_language_nothing_is_downloaded()
    {
        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() =>
            _provider.GetIsoAsync(CultureInfo.GetCultureInfo("ja-JP"), null, Ct));

        Assert.Equal(ErrorKind.ServiceRefused, ex.Kind);
        Assert.Contains("checksum", ex.Message);
        Assert.Contains("Use my ISO", ex.Message);
        Assert.DoesNotContain("mirror", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, _client.TicketCalls);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task A_download_that_does_not_match_the_published_hash_is_deleted_and_not_cached()
    {
        _client.Release = ReleaseWith((Languages.German, new string('D', 64)));

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() => _provider.GetIsoAsync(German, null, Ct));

        Assert.Equal(ErrorKind.Verification, ex.Kind);
        Assert.Empty(Directory.GetFiles(_cache.Folder));
        Assert.Empty(_cache.FindAll(Languages.German));
    }

    [Fact]
    public async Task A_genuine_iso_of_another_language_is_rejected_for_this_one()
    {
        // The server hands out the file whose hash Microsoft lists under French.
        _client.Release = ReleaseWith((Languages.German, new string('D', 64)), (Languages.French, _server.Sha256));

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() => _provider.GetIsoAsync(German, null, Ct));

        Assert.Equal(ErrorKind.Verification, ex.Kind);
        Assert.Empty(Directory.GetFiles(_cache.Folder));
    }

    [Fact]
    public async Task When_the_page_lists_a_language_twice_either_hash_is_accepted_and_nothing_else()
    {
        _client.Release = _client.Release with
        {
            Hashes = HashTable(("German 64-bit", new string('D', 64)), ("German 64-bit", _server.Sha256))
        };

        var result = await _provider.GetIsoAsync(German, null, Ct);
        Assert.True(result.VerifiedAgainstMicrosoft);

        _cache.Remove(Assert.Single(_cache.FindAll(Languages.German)));
        _client.Release = _client.Release with
        {
            Hashes = HashTable(("German 64-bit", new string('D', 64)), ("German 64-bit", new string('C', 64)))
        };

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() => _provider.GetIsoAsync(German, null, Ct));
        Assert.Equal(ErrorKind.Verification, ex.Kind);
        Assert.Empty(Directory.GetFiles(_cache.Folder));
    }

    // ---- cache ----

    [Fact]
    public async Task A_cached_iso_that_no_longer_hashes_to_its_record_is_removed_and_downloaded_again()
    {
        await _provider.GetIsoAsync(German, null, Ct);
        var path = _cache.GetPathFor(CurrentFile);
        var damaged = await File.ReadAllBytesAsync(path, Ct);
        damaged[30_000] ^= 0x01;
        await File.WriteAllBytesAsync(path, damaged, Ct);
        var requestsBefore = _server.Requests.Count;

        var result = await _provider.GetIsoAsync(German, null, Ct);

        Assert.True(result.VerifiedAgainstMicrosoft);
        Assert.Equal(_server.Content, await File.ReadAllBytesAsync(result.Path, Ct));
        Assert.Equal(requestsBefore + 1, _server.Requests.Count);
    }

    [Fact]
    public async Task An_older_cached_build_is_replaced_by_the_current_one()
    {
        var older = await _cache.SeedAsync(OlderFile, Bytes(77, 60_000), Languages.German, "26300.9000");

        var result = await _provider.GetIsoAsync(German, null, Ct);

        Assert.Equal(_cache.GetPathFor(CurrentFile), result.Path);
        Assert.True(result.VerifiedAgainstMicrosoft);
        Assert.False(File.Exists(older.Path)); // 9 GB of an outdated build do not stay around
        Assert.False(File.Exists(IsoCache.SidecarPathFor(older.Path)));
        Assert.Single(_cache.List());
    }

    [Fact]
    public async Task A_cached_iso_for_another_language_is_not_used()
    {
        await _cache.SeedAsync("Windows11_Client_x64_fr-fr_26300_9457.iso", Bytes(77, 60_000), Languages.French);

        var result = await _provider.GetIsoAsync(German, null, Ct);

        Assert.Equal(_cache.GetPathFor(CurrentFile), result.Path);
        Assert.Equal(2, _cache.List().Count);
    }

    [Fact]
    public async Task A_finished_file_without_a_description_is_adopted_when_its_hash_is_the_published_one()
    {
        // As after a crash between the rename and the write of the description.
        Directory.CreateDirectory(_cache.Folder);
        await File.WriteAllBytesAsync(_cache.GetPathFor(CurrentFile), _server.Content, Ct);

        var result = await _provider.GetIsoAsync(German, null, Ct);

        Assert.True(result.VerifiedAgainstMicrosoft);
        Assert.Empty(_server.Requests);
        Assert.NotEmpty(_cache.FindAll(Languages.German));
    }

    [Fact]
    public async Task A_file_without_a_description_and_with_the_wrong_hash_is_replaced()
    {
        Directory.CreateDirectory(_cache.Folder);
        await File.WriteAllBytesAsync(_cache.GetPathFor(CurrentFile), Bytes(77, 60_000), Ct);

        var result = await _provider.GetIsoAsync(German, null, Ct);

        Assert.Equal(_server.Content, await File.ReadAllBytesAsync(result.Path, Ct));
        Assert.True(result.VerifiedAgainstMicrosoft);
    }

    // ---- service not available ----

    [Theory]
    [InlineData(ErrorKind.Network)]
    [InlineData(ErrorKind.ServiceRefused)]
    public async Task When_microsoft_cannot_be_asked_an_intact_cached_iso_is_used(ErrorKind kind)
    {
        await _provider.GetIsoAsync(German, null, Ct);
        _client.ReleaseError = new PlainstallException(kind, "The service is not available.");
        var requestsBefore = _server.Requests.Count;

        var result = await _provider.GetIsoAsync(German, null, Ct);

        Assert.Equal(_cache.GetPathFor(CurrentFile), result.Path);
        Assert.True(result.VerifiedAgainstMicrosoft); // the verdict recorded at download time
        Assert.Equal(requestsBefore, _server.Requests.Count);
    }

    [Fact]
    public async Task When_microsoft_cannot_be_asked_and_the_cache_has_nothing_the_original_error_is_reported()
    {
        var failure = new PlainstallException(ErrorKind.Network, "The service is not available.");
        _client.ReleaseError = failure;
        await _cache.SeedAsync("Windows11_Client_x64_fr-fr_26300_9457.iso", Bytes(77, 60_000), Languages.French);

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() => _provider.GetIsoAsync(German, null, Ct));

        Assert.Same(failure, ex); // French in the cache is no answer to a German PC
    }

    [Fact]
    public async Task A_damaged_cached_iso_is_not_used_when_microsoft_cannot_be_asked()
    {
        await _provider.GetIsoAsync(German, null, Ct);
        var path = _cache.GetPathFor(CurrentFile);
        var damaged = await File.ReadAllBytesAsync(path, Ct);
        damaged[5] ^= 0x01;
        await File.WriteAllBytesAsync(path, damaged, Ct);
        var failure = new PlainstallException(ErrorKind.Network, "The service is not available.");
        _client.ReleaseError = failure;

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() => _provider.GetIsoAsync(German, null, Ct));

        Assert.Same(failure, ex);
    }

    private sealed class FakeClient : IMicrosoftDownloadClient
    {
        public required WindowsRelease Release { get; set; }

        public PlainstallException? ReleaseError { get; set; }

        public string FileName { get; set; } = CurrentFile;

        /// <summary>The token of each ticket in turn; the last one repeats. Default: always a valid one.</summary>
        public string[] Tokens { get; set; } = ["good"];

        public int ReleaseCalls { get; private set; }

        public int TicketCalls { get; private set; }

        public WindowsLanguage? LastTicketLanguage { get; private set; }

        public Task<WindowsRelease> GetReleaseAsync(CancellationToken cancellationToken)
        {
            ReleaseCalls++;
            return ReleaseError is not null
                ? Task.FromException<WindowsRelease>(ReleaseError)
                : Task.FromResult(Release);
        }

        public Task<DownloadTicket> GetDownloadTicketAsync(WindowsRelease release, WindowsLanguage language,
            CancellationToken cancellationToken)
        {
            var token = Tokens[Math.Min(TicketCalls, Tokens.Length - 1)];
            TicketCalls++;
            LastTicketLanguage = language;
            return Task.FromResult(new DownloadTicket(
                new Uri($"https://{FakeIsoServer.Host}/dbazure/{FileName}?t={token}&sig=SECRETSIGNATURE"),
                FileName,
                null));
        }
    }
}