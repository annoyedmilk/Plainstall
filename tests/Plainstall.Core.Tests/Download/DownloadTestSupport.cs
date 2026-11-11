using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using Plainstall.Core.Download;

namespace Plainstall.Core.Tests.Download;

/// <summary>Reads the synthetic fixtures. None of them is a copy of a Microsoft page or answer.</summary>
internal static class Fixture
{
    public static string Read(string name)
    {
        return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "Download", name));
    }
}

/// <summary>What a test wants to know about a request after the fact.</summary>
internal sealed record RecordedRequest(Uri Uri, long? RangeFrom, string? IfRange, Uri? Referer, int HeaderCount)
{
    public string Host => Uri.Host;

    public string Query(string name)
    {
        foreach (var pair in Uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq] == name) return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }

        return string.Empty;
    }
}

/// <summary>An HTTP handler that never touches the network. It records every request and answers from a function.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Lock _gate = new();
    private readonly List<RecordedRequest> _requests = [];
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

    public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : this((request, _) => Task.FromResult(respond(request)))
    {
    }

    public FakeHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        _respond = respond;
    }

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var recorded = new RecordedRequest(
            request.RequestUri!,
            request.Headers.Range?.Ranges.FirstOrDefault()?.From,
            request.Headers.IfRange?.EntityTag?.ToString(),
            request.Headers.Referrer,
            request.Headers.Count());
        lock (_gate)
        {
            _requests.Add(recorded);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var response = await _respond(request, cancellationToken).ConfigureAwait(false);
        response.RequestMessage ??= request;
        return response;
    }
}

/// <summary>A clock a test can set. Timers still run on real time, which the tests that need timers keep short.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        return _now;
    }

    public void Advance(TimeSpan by)
    {
        _now += by;
    }
}

/// <summary>Collects reports on the calling thread. Progress&lt;T&gt; would post them to the thread pool, in any order.</summary>
internal sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Lock _gate = new();
    private readonly List<T> _values = [];

    public IReadOnlyList<T> Values
    {
        get
        {
            lock (_gate)
            {
                return [.. _values];
            }
        }
    }

    public void Report(T value)
    {
        lock (_gate)
        {
            _values.Add(value);
        }
    }
}

/// <summary>Test data that several download tests need.</summary>
internal static class DownloadTestData
{
    /// <summary>Random bytes that stand in for an ISO. The same seed gives the same bytes.</summary>
    public static byte[] Bytes(int seed, int size = 5000)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>A hash table as Microsoft's page prints it, read with the real parser.</summary>
    public static PublishedHashTable HashTable(params (string Label, string Sha256)[] rows)
    {
        return PublishedHashTable.Parse(
            "<table>" + string.Concat(rows.Select(r => $"<tr><td>{r.Label}</td><td>{r.Sha256}</td></tr>")) +
            "</table>");
    }

    /// <summary>Puts a verified ISO into the cache, as an earlier download does.</summary>
    public static async Task<CachedIso> SeedAsync(this IsoCache cache, string fileName, byte[] content,
        WindowsLanguage language, string build = "26300.9457")
    {
        Directory.CreateDirectory(cache.Folder);
        var path = cache.GetPathFor(fileName);
        await File.WriteAllBytesAsync(path, content, TestContext.Current.CancellationToken);
        return await cache.AddAsync(
            path,
            new IsoDescription(HashHex.Sha256Of(content), language.DisplayName)
            {
                Build = build,
                ProductName = "Windows 11 Client - Build " + build,
                VerifiedAgainstMicrosoft = true
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>What the HTTP stack throws when it does not accept the certificate of the server.</summary>
    public static HttpRequestException RejectedCertificate()
    {
        return new HttpRequestException(
            HttpRequestError.SecureConnectionError,
            "The SSL connection could not be established, see inner exception.",
            new AuthenticationException("The remote certificate is invalid according to the validation procedure."));
    }
}

/// <summary>Records the waits between retries and returns at once, so retry tests take no time.</summary>
internal sealed class RecordedDelays
{
    private readonly List<TimeSpan> _delays = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<TimeSpan> Values
    {
        get
        {
            lock (_gate)
            {
                return [.. _delays];
            }
        }
    }

    public Task Wait(TimeSpan delay, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _delays.Add(delay);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
///     A stream that hands out its data in small pieces and then misbehaves in a chosen way: it ends
///     early, throws, or waits for ever (until the read is cancelled).
/// </summary>
internal sealed class FaultStream : Stream
{
    public enum Fault
    {
        None,
        Throw,
        Hang
    }

    private readonly Action? _beforeFault;
    private readonly int _chunk;

    private readonly byte[] _data;
    private readonly Fault _fault;
    private readonly int _faultAt;
    private int _position;

    public FaultStream(byte[] data, int faultAt, Fault fault, Action? beforeFault = null, int chunk = 4096)
    {
        _data = data;
        _faultAt = Math.Min(faultAt, data.Length);
        _fault = fault;
        _beforeFault = beforeFault;
        _chunk = chunk;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var limit = _fault == Fault.None ? _data.Length : _faultAt;
        if (_position < limit)
        {
            var count = Math.Min(Math.Min(buffer.Length, _chunk), limit - _position);
            _data.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        if (_fault == Fault.None) return 0;

        _beforeFault?.Invoke();
        if (_fault == Fault.Throw) throw new IOException("The connection was reset (simulated).");

        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
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

/// <summary>
///     Behaves like a file server with signed links: it wants a valid token in the query, supports byte
///     ranges with If-Range, and can be told to break in the ways real servers and connections do.
/// </summary>
internal sealed class FakeIsoServer
{
    public const string Host = "software.download.prss.microsoft.com";
    public const string FilePath = "/dbazure/Windows11_Test_x64_en-us.iso";

    private int _count;

    public FakeIsoServer(int size = 200_000, int seed = 1)
    {
        Content = new byte[size];
        new Random(seed).NextBytes(Content);
        Handler = new FakeHttpHandler(Respond);
    }

    public byte[] Content { get; set; }

    public string? ETag { get; set; } = "\"v1\"";

    public bool HonorRange { get; set; } = true;

    public bool SendContentLength { get; set; } = true;

    public string ValidToken { get; set; } = "good";

    /// <summary>Called first with the zero-based request number. A non-null answer replaces the normal one.</summary>
    public Func<int, HttpRequestMessage, HttpResponseMessage?>? Intercept { get; set; }

    /// <summary>
    ///     Called with the request number and the bytes about to be sent. Lets a test swap in a
    ///     <see cref="FaultStream" />.
    /// </summary>
    public Func<int, byte[], Stream?>? Body { get; set; }

    public FakeHttpHandler Handler { get; }

    public IReadOnlyList<RecordedRequest> Requests => Handler.Requests;

    public string Sha256 => HashHex.Sha256Of(Content);

    public DownloadTicket Ticket(string? token = null, DateTimeOffset? expires = null, string path = FilePath)
    {
        return new DownloadTicket(
            new Uri($"https://{Host}{path}?t={token ?? ValidToken}&sig=SECRETSIGNATURE"),
            "Windows11_Test_x64_en-us.iso",
            expires);
    }

    private HttpResponseMessage Respond(HttpRequestMessage request)
    {
        var number = _count++;
        if (Intercept?.Invoke(number, request) is { } replaced) return replaced;

        if (!request.RequestUri!.Query.Contains("t=" + ValidToken + "&", StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("expired") };

        var from = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
        var conditionHolds = request.Headers.IfRange is not { } condition || condition.EntityTag?.ToString() == ETag;
        if (!HonorRange || from is not { } start || !conditionHolds)
            return WithBody(HttpStatusCode.OK, number, Content);

        if (start >= Content.Length)
        {
            var unsatisfiable = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
                { Content = new ByteArrayContent([]) };
            unsatisfiable.Content.Headers.ContentRange = new ContentRangeHeaderValue(Content.Length);
            return unsatisfiable;
        }

        var slice = Content[(int)start..];
        var partial = WithBody(HttpStatusCode.PartialContent, number, slice);
        partial.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, Content.Length - 1, Content.Length);
        return partial;
    }

    private HttpResponseMessage WithBody(HttpStatusCode status, int number, byte[] bytes)
    {
        var stream = Body?.Invoke(number, bytes) ?? new MemoryStream(bytes);
        var response = new HttpResponseMessage(status) { Content = new StreamContent(stream) };
        if (SendContentLength) response.Content.Headers.ContentLength = bytes.Length;

        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        if (ETag is not null) response.Headers.TryAddWithoutValidation("ETag", ETag);

        return response;
    }
}

/// <summary>Answers like Microsoft's page and connector, from the synthetic fixtures. Each route can be replaced per test.</summary>
internal sealed class FakeMicrosoftService
{
    public FakeMicrosoftService()
    {
        Handler = new FakeHttpHandler(Respond);
    }

    public FakeHttpHandler Handler { get; }

    public IReadOnlyList<RecordedRequest> Requests => Handler.Requests;

    /// <summary>
    ///     Replaces answers by route name: "page", "tags", "mdt", "ov-df", "skus", "links". Null from the function means
    ///     "answer normally".
    /// </summary>
    public Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage?>> Overrides { get; } = [];

    public IEnumerable<RecordedRequest> RequestsTo(string route)
    {
        return Requests.Where(r => RouteOf(r.Uri) == route);
    }

    public static string RouteOf(Uri uri)
    {
        var path = uri.AbsolutePath;
        return uri.Host switch
        {
            "www.microsoft.com" when path.Contains("/software-download/windows11", StringComparison.OrdinalIgnoreCase)
                => "page",
            "www.microsoft.com" when path.EndsWith("/getskuinformationbyproductedition",
                StringComparison.OrdinalIgnoreCase) => "skus",
            "www.microsoft.com" when path.EndsWith("/GetProductDownloadLinksBySku", StringComparison.OrdinalIgnoreCase)
                => "links",
            "vlscppe.microsoft.com" when path == "/tags" => "tags",
            "ov-df.microsoft.com" when path == "/mdt.js" => "mdt",
            "ov-df.microsoft.com" when path == "/" => "ov-df",
            _ => "unknown"
        };
    }

    public static HttpResponseMessage Text(string body, string mediaType = "text/html",
        HttpStatusCode status = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
    }

    public static HttpResponseMessage Redirect(string location, HttpStatusCode status = HttpStatusCode.Found)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.TryAddWithoutValidation("Location", location);
        return response;
    }

    private HttpResponseMessage Respond(HttpRequestMessage request)
    {
        var route = RouteOf(request.RequestUri!);
        if (Overrides.TryGetValue(route, out var custom) && custom(request) is { } replaced) return replaced;

        return route switch
        {
            "page" => Text(Fixture.Read("product-page-x64.html")),
            "tags" => Text(string.Empty, "text/plain"),
            "mdt" => Text(Fixture.Read("session-script.js"), "application/javascript"),
            "ov-df" => Text(string.Empty, "text/plain"),
            "skus" => Text(Fixture.Read("sku-information.json"), "application/json"),
            "links" => Text(Fixture.Read("download-links.json"), "application/json"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };
    }
}

/// <summary>A language list shaped like the real one (same two name fields, same quirks), for matcher and cache tests.</summary>
internal static class Languages
{
    public static readonly WindowsLanguage Arabic = new("1", "Arabic", "Arabic");

    public static readonly WindowsLanguage
        BrazilianPortuguese = new("2", "Portuguese (Brazil)", "Brazilian Portuguese");

    public static readonly WindowsLanguage ChineseSimplified = new("3", "Chinese (Simplified)", "Chinese (Simplified)");

    public static readonly WindowsLanguage ChineseTraditional =
        new("4", "Chinese (Traditional)", "Chinese (Traditional)");

    public static readonly WindowsLanguage EnglishUs = new("5", "English", "English (United States)");

    public static readonly WindowsLanguage EnglishInternational =
        new("6", "English (United Kingdom)", "English International");

    public static readonly WindowsLanguage French = new("7", "French", "French");
    public static readonly WindowsLanguage FrenchCanadian = new("8", "French (Canada)", "French Canadian");
    public static readonly WindowsLanguage German = new("9", "German", "German");
    public static readonly WindowsLanguage Norwegian = new("10", "Norwegian", "Norwegian");
    public static readonly WindowsLanguage Portuguese = new("11", "Portuguese (Portugal)", "Portuguese");
    public static readonly WindowsLanguage SerbianLatin = new("12", "Serbian (Latin)", "Serbian Latin");
    public static readonly WindowsLanguage Spanish = new("13", "Spanish", "Spanish");
    public static readonly WindowsLanguage SpanishMexico = new("14", "Spanish (Mexico)", "Spanish (Mexico)");
    public static readonly WindowsLanguage Japanese = new("15", "Japanese", "Japanese");
    public static readonly WindowsLanguage Ukrainian = new("16", "Ukrainian", "Ukrainian");
    public static readonly WindowsLanguage Hebrew = new("17", "Hebrew", "Hebrew");

    public static readonly IReadOnlyList<WindowsLanguage> All =
    [
        Arabic, BrazilianPortuguese, ChineseSimplified, ChineseTraditional, EnglishUs, EnglishInternational, French,
        FrenchCanadian, German, Norwegian, Portuguese, SerbianLatin, Spanish, SpanishMexico, Japanese, Ukrainian, Hebrew
    ];
}