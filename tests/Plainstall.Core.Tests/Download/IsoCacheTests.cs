using Plainstall.Core.Download;
using static Plainstall.Core.Tests.Download.DownloadTestData;

namespace Plainstall.Core.Tests.Download;

public sealed class IsoCacheTests : IDisposable
{
    private readonly IsoCache _cache;
    private readonly TempDirectory _temp = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 10, 14, 0, 0, TimeSpan.Zero));

    public IsoCacheTests()
    {
        _cache = new IsoCache(Path.Combine(_temp.Path, "cache"), _time);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _temp.Dispose();
    }

    private CachedIso? Newest(WindowsLanguage language)
    {
        var all = _cache.FindAll(language);
        return all.Count > 0 ? all[0] : null;
    }

    private static WindowsRelease Release(params (WindowsLanguage Language, byte[] Content)[] published)
    {
        return new WindowsRelease(
            "3813",
            [.. published.Select(p => p.Language)],
            HashTable(
                [.. published.Select(p => (PublishedHashTable.LabelFor(p.Language), HashHex.Sha256Of(p.Content)))]));
    }

    [Fact]
    public void An_empty_or_missing_cache_has_no_entries()
    {
        Assert.Empty(_cache.List());
        Assert.Null(Newest(Languages.German));
        Assert.False(Directory.Exists(_cache.Folder)); // looking does not create the folder
    }

    [Fact]
    public async Task An_added_iso_is_found_with_everything_that_was_recorded()
    {
        var content = Bytes(1);
        var added = await _cache.SeedAsync("Windows11_Client_x64_de-de_26300_9457.iso", content, Languages.German);

        var found = Newest(Languages.German);

        Assert.NotNull(found);
        Assert.Equal(added, found);
        Assert.Equal(_cache.GetPathFor("Windows11_Client_x64_de-de_26300_9457.iso"), found.Path);
        Assert.Equal(HashHex.Sha256Of(content), found.Sha256);
        Assert.Equal("German", found.Language);
        Assert.Equal(content.Length, found.SizeBytes);
        Assert.Equal("26300.9457", found.Build);
        Assert.Equal("Windows 11 Client - Build 26300.9457", found.ProductName);
        Assert.True(found.VerifiedAgainstMicrosoft);
        Assert.Equal(_time.GetUtcNow(), found.DownloadedAt);
        Assert.True(File.Exists(IsoCache.SidecarPathFor(found.Path)));
        Assert.True(await _cache.VerifyAsync(found, null, Ct));
    }

    [Fact]
    public async Task Another_language_is_a_miss()
    {
        await _cache.SeedAsync("de-x64.iso", Bytes(1), Languages.German);

        Assert.Null(Newest(Languages.French));
        Assert.NotNull(Newest(Languages.German));
    }

    [Fact]
    public async Task Languages_are_told_apart_by_their_localized_name()
    {
        await _cache.SeedAsync("en-us.iso", Bytes(1), Languages.EnglishUs);
        await _cache.SeedAsync("en-gb.iso", Bytes(2), Languages.EnglishInternational);
        await _cache.SeedAsync("pt-pt.iso", Bytes(3), Languages.Portuguese);

        Assert.EndsWith("en-us.iso", Newest(Languages.EnglishUs)!.Path);
        Assert.EndsWith("en-gb.iso", Newest(Languages.EnglishInternational)!.Path);
        Assert.EndsWith("pt-pt.iso", Newest(Languages.Portuguese)!.Path);
        Assert.Null(Newest(Languages.BrazilianPortuguese));
    }

    [Fact]
    public async Task The_newest_entry_comes_first()
    {
        await _cache.SeedAsync("old.iso", Bytes(1), Languages.German, "26300.9000");
        _time.Advance(TimeSpan.FromDays(30));
        await _cache.SeedAsync("new.iso", Bytes(2), Languages.German);

        Assert.Equal(["new.iso", "old.iso"], _cache.FindAll(Languages.German).Select(e => Path.GetFileName(e.Path)));
        Assert.Equal(2, _cache.List().Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ this is not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"version\":2,\"sha256\":\"x\"}")]
    [InlineData(
        "{\"version\":1,\"file\":\"de.iso\",\"sha256\":\"ABC\",\"sizeBytes\":5000,\"language\":\"German\",\"downloadedAt\":\"2026-11-10T14:00:00Z\"}")]
    [InlineData(
        "{\"version\":1,\"file\":\"de.iso\",\"sha256\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"sizeBytes\":5000,\"language\":\"\",\"downloadedAt\":\"2026-11-10T14:00:00Z\"}")]
    [InlineData(
        "{\"version\":1,\"file\":\"de.iso\",\"sha256\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"sizeBytes\":\"big\",\"language\":\"German\",\"downloadedAt\":\"2026-11-10T14:00:00Z\"}")]
    [InlineData(
        "{\"version\":1,\"file\":\"de.iso\",\"sha256\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"sizeBytes\":5000,\"language\":\"German\",\"downloadedAt\":\"yesterday\"}")]
    [InlineData(
        "{\"version\":1,\"file\":\"other.iso\",\"sha256\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"sizeBytes\":5000,\"language\":\"German\",\"downloadedAt\":\"2026-11-10T14:00:00Z\"}")]
    public async Task A_damaged_description_is_a_miss_and_never_an_error(string sidecarText)
    {
        var added = await _cache.SeedAsync("de.iso", Bytes(1), Languages.German);
        await File.WriteAllTextAsync(IsoCache.SidecarPathFor(added.Path), sidecarText, Ct);

        Assert.Null(Newest(Languages.German));
        Assert.Empty(_cache.List());
        Assert.True(File.Exists(added.Path)); // looking does not delete anything
    }

    [Fact]
    public async Task A_missing_description_is_a_miss()
    {
        var added = await _cache.SeedAsync("de.iso", Bytes(1), Languages.German);
        File.Delete(IsoCache.SidecarPathFor(added.Path));

        Assert.Null(Newest(Languages.German));
    }

    [Fact]
    public async Task A_missing_iso_is_a_miss()
    {
        var added = await _cache.SeedAsync("de.iso", Bytes(1), Languages.German);
        File.Delete(added.Path);

        Assert.Null(Newest(Languages.German));
        Assert.False(await _cache.VerifyAsync(added, null, Ct));
    }

    [Fact]
    public async Task A_file_of_another_size_fails_the_quick_check()
    {
        var added = await _cache.SeedAsync("de.iso", Bytes(1), Languages.German);
        await File.WriteAllBytesAsync(added.Path, Bytes(1, 4000), Ct); // truncated

        Assert.Null(Newest(Languages.German));
        Assert.False(await _cache.VerifyAsync(added, null, Ct));
    }

    [Fact]
    public async Task A_changed_file_of_the_same_size_passes_the_quick_check_and_fails_the_full_one()
    {
        var added = await _cache.SeedAsync("de.iso", Bytes(1), Languages.German);
        var altered = Bytes(1);
        altered[2500] ^= 0x01; // one flipped bit
        await File.WriteAllBytesAsync(added.Path, altered, Ct);
        var progress = new SyncProgress<TransferProgress>();

        var quick = Newest(Languages.German);
        Assert.NotNull(quick); // this is why the full check exists
        Assert.False(await _cache.VerifyAsync(quick, progress, Ct));
        Assert.Equal(new TransferProgress(5000, 5000), progress.Values[^1]);
    }

    [Fact]
    public async Task A_file_from_outside_the_cache_is_moved_in()
    {
        var content = Bytes(1);
        var outside = _temp.File("downloaded.iso");
        await File.WriteAllBytesAsync(outside, content, Ct);

        var added = await _cache.AddAsync(outside, new IsoDescription(HashHex.Sha256Of(content), "German"), Ct);

        Assert.Equal(_cache.GetPathFor("downloaded.iso"), added.Path);
        Assert.False(File.Exists(outside));
        Assert.Equal(content, await File.ReadAllBytesAsync(added.Path, Ct));
        Assert.False(added.VerifiedAgainstMicrosoft);
        Assert.Null(added.Build);
        Assert.Equal(added, Newest(Languages.German));
    }

    [Fact]
    public async Task Adding_never_overwrites_a_cached_file()
    {
        var existing = await _cache.SeedAsync("de.iso", Bytes(1), Languages.German);
        var outside = _temp.File("de.iso");
        await File.WriteAllBytesAsync(outside, Bytes(2), Ct);

        var ex = await Assert.ThrowsAnyAsync<PlainstallException>(() =>
            _cache.AddAsync(outside, new IsoDescription(HashHex.Sha256Of(Bytes(2)), "German"), Ct));

        Assert.Equal(ErrorKind.InvalidInput, ex.Kind);
        Assert.Equal(Bytes(1), await File.ReadAllBytesAsync(existing.Path, Ct));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task Adding_validates_its_input()
    {
        var iso = _temp.File("x.iso");
        await File.WriteAllBytesAsync(iso, Bytes(1), Ct);
        var text = _temp.File("x.txt");
        await File.WriteAllBytesAsync(text, Bytes(1), Ct);
        var hash = HashHex.Sha256Of(Bytes(1));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _cache.AddAsync(iso, new IsoDescription("nope", "German"), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _cache.AddAsync(iso, new IsoDescription(hash, " "), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _cache.AddAsync(text, new IsoDescription(hash, "German"), Ct));
        Assert.ThrowsAny<ArgumentException>(() => _cache.GetPathFor("..\\outside.iso"));
        Assert.ThrowsAny<ArgumentException>(() => _cache.GetPathFor("sub/inside.iso"));
    }

    [Fact]
    public async Task Remove_deletes_the_iso_and_its_description_and_nothing_outside_the_cache()
    {
        var added = await _cache.SeedAsync("de.iso", Bytes(1), Languages.German);
        var outside = _temp.File("outside.iso");
        await File.WriteAllBytesAsync(outside, Bytes(3), Ct);

        _cache.Remove(added);
        Assert.Throws<ArgumentException>(() => _cache.Remove(added with { Path = outside }));

        Assert.Empty(Directory.GetFiles(_cache.Folder));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task Freshness_is_current_while_microsoft_lists_the_same_hash_for_the_language()
    {
        var content = Bytes(1);
        var cached = await _cache.SeedAsync("de.iso", content, Languages.German);
        var release = Release((Languages.German, content), (Languages.French, Bytes(9)));

        Assert.Equal(CacheFreshness.Current, IsoCache.GetFreshness(cached, release, Languages.German));
    }

    [Fact]
    public async Task Freshness_is_superseded_when_microsoft_lists_another_hash_for_the_language()
    {
        var cached = await _cache.SeedAsync("de.iso", Bytes(1), Languages.German, "26300.9000");
        var release = Release((Languages.German, Bytes(2)));

        Assert.Equal(CacheFreshness.Superseded, IsoCache.GetFreshness(cached, release, Languages.German));
    }

    [Fact]
    public async Task A_hash_listed_under_another_language_does_not_make_the_entry_current()
    {
        var content = Bytes(1);
        var cached = await _cache.SeedAsync("de.iso", content, Languages.German);
        var release = Release((Languages.French, content), (Languages.German, Bytes(2)));

        Assert.Equal(CacheFreshness.Superseded, IsoCache.GetFreshness(cached, release, Languages.German));
    }

    [Fact]
    public async Task Freshness_is_unknown_without_a_row_for_the_language()
    {
        var content = Bytes(1);
        var cached = await _cache.SeedAsync("de.iso", content, Languages.German);

        var noRow = Release((Languages.French, Bytes(2)));
        var emptyTable = new WindowsRelease("3813", [Languages.German], PublishedHashTable.Empty);

        Assert.Equal(CacheFreshness.Unknown, IsoCache.GetFreshness(cached, noRow, Languages.German));
        Assert.Equal(CacheFreshness.Unknown, IsoCache.GetFreshness(cached, emptyTable, Languages.German));
    }

    [Fact]
    public async Task Files_that_are_not_cache_entries_are_ignored()
    {
        await _cache.SeedAsync("de.iso", Bytes(1), Languages.German);
        await File.WriteAllTextAsync(Path.Combine(_cache.Folder, "notes.txt"), "hello", Ct);
        await File.WriteAllBytesAsync(Path.Combine(_cache.Folder, "half.iso.part"), Bytes(4), Ct);
        await File.WriteAllTextAsync(Path.Combine(_cache.Folder, "half.iso.part.json"),
            "{\"url\":\"https://x/half.iso\"}", Ct);
        await File.WriteAllBytesAsync(Path.Combine(_cache.Folder, "orphan.iso"), Bytes(5), Ct);

        Assert.Single(_cache.List());
    }
}