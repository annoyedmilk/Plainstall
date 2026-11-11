using System.Globalization;
using System.Text;
using System.Text.Unicode;

namespace Plainstall.Core.Imaging.Drivers;

/// <summary>
///     The processor architectures an INF says it supports, from the decorations of its
///     <c>[Manufacturer]</c> entries (<c>NTamd64</c>, <c>NTarm64</c>, <c>NTx86</c> and so on).
///     <see cref="None" /> means the INF does not say, not that it supports nothing.
/// </summary>
[Flags]
public enum InfArchitectures
{
    None = 0,
    X86 = 1,
    Amd64 = 2,
    Arm64 = 4,
    Arm = 8,
    Ia64 = 16
}

public static class InfArchitecturesExtensions
{
    private static readonly (InfArchitectures Flag, string Name)[] Names =
    [
        (InfArchitectures.X86, "x86"),
        (InfArchitectures.Amd64, "amd64"),
        (InfArchitectures.Arm64, "arm64"),
        (InfArchitectures.Arm, "arm"),
        (InfArchitectures.Ia64, "ia64")
    ];

    /// <summary>Lower-case names as INF files and the driver store write them, for example <c>amd64, arm64</c>.</summary>
    public static string Describe(this InfArchitectures architectures)
    {
        if (architectures == InfArchitectures.None) return "an unknown architecture";
        var names = new List<string>();
        foreach (var (flag, name) in Names)
            if (architectures.HasFlag(flag))
                names.Add(name);

        return string.Join(", ", names);
    }

    /// <summary>Reads one architecture name. Returns <see cref="InfArchitectures.None" /> for anything else.</summary>
    public static InfArchitectures ParseName(string? name)
    {
        foreach (var (flag, known) in Names)
            if (string.Equals(name, known, StringComparison.OrdinalIgnoreCase))
                return flag;

        return InfArchitectures.None;
    }
}

/// <summary>
///     The <c>DriverVer</c> of an INF: a date and an optional four-part version. Windows ranks drivers by
///     date first and by version only when the dates are equal, so <see cref="CompareTo" /> does the same.
///     A missing version counts as 0.0.0.0, which is why a date-only value is valid and comparable.
/// </summary>
public sealed record InfDriverVer(DateOnly Date, Version? Version) : IComparable<InfDriverVer>
{
    private static readonly Version Zero = new(0, 0, 0, 0);

    public int CompareTo(InfDriverVer? other)
    {
        if (other is null) return 1;
        var byDate = Date.CompareTo(other.Date);
        return byDate != 0 ? byDate : (Version ?? Zero).CompareTo(other.Version ?? Zero);
    }

    /// <summary>
    ///     Parses <c>mm/dd/yyyy[,w.x.y.z]</c>. Returns null for anything else, including a readable date
    ///     followed by an unreadable version: a half-understood value would rank the driver wrongly.
    /// </summary>
    public static InfDriverVer? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split(',');
        if (parts.Length > 2) return null;
        var dateParts = parts[0].Trim().Split('/');
        if (dateParts.Length != 3
            || !TryNumber(dateParts[0], 2, out var month)
            || !TryNumber(dateParts[1], 2, out var day)
            || dateParts[2].Trim().Length != 4
            || !TryNumber(dateParts[2], 4, out var year)
            || year < 1
            || month is < 1 or > 12
            || day < 1 || day > DateTime.DaysInMonth(year, month))
            return null;
        var date = new DateOnly(year, month, day);
        if (parts.Length == 1 || parts[1].Trim().Length == 0) return new InfDriverVer(date, null);

        var versionParts = parts[1].Trim().Split('.');
        if (versionParts.Length is < 1 or > 4) return null;
        var numbers = new int[4];
        for (var i = 0; i < versionParts.Length; i++)
        {
            if (TryVersionPart(versionParts[i]) is not { } number) return null;
            numbers[i] = number;
        }

        // Always four parts: System.Version ranks 1.2 below 1.2.0.0, Windows treats them as equal.
        return new InfDriverVer(date, new Version(numbers[0], numbers[1], numbers[2], numbers[3]));
    }

    public override string ToString()
    {
        return Date.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture) +
               (Version is null ? "" : "," + Version.ToString(4));
    }

    private static bool TryNumber(string text, int maxDigits, out int value)
    {
        text = text.Trim();
        value = 0;
        return text.Length > 0 && text.Length <= maxDigits
                               && text.All(char.IsAsciiDigit)
                               && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>One part of the four-part version: up to five digits, at most 65535. Null for anything else.</summary>
    private static int? TryVersionPart(string text)
    {
        return TryNumber(text, 5, out var number) && number <= 65535 ? number : null;
    }
}

/// <summary>
///     The facts Plainstall needs from a driver <c>.inf</c>: what kind of device it is for, who made it,
///     how new it is and which processors it supports. It is a reader for those few keys, not a full INF
///     parser. It never throws for the content of a file: a damaged or foreign file gives null fields and
///     <see cref="LooksLikeDriverInf" /> false, because one odd file in a user's driver folder must not stop
///     a build.
/// </summary>
public sealed record InfFile
{
    /// <summary>Larger files are not read. The largest real driver INFs are a few megabytes.</summary>
    public const long MaxBytes = 32L << 20;

    /// <summary>Storage controllers (<c>Class=SCSIAdapter</c>). Intel RST and VMD, most RAID and many NVMe drivers.</summary>
    public static readonly Guid ScsiAdapterClassGuid = new("4D36E97B-E325-11CE-BFC1-08002BE10318");

    /// <summary>Hard disk controllers (<c>Class=HDC</c>). AHCI and IDE controllers.</summary>
    public static readonly Guid HdcClassGuid = new("4D36E96A-E325-11CE-BFC1-08002BE10318");

    private static readonly Encoding Ansi = CreateAnsiEncoding();

    public required string Path { get; init; }

    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>True when the file has a <c>[Version]</c> section. Every driver INF has one.</summary>
    public bool HasVersionSection { get; init; }

    /// <summary><c>$Windows NT$</c> in practice. Required in driver INFs, so it helps tell them from <c>autorun.inf</c>.</summary>
    public string? Signature { get; init; }

    /// <summary>The device setup class name, for example <c>Net</c>, <c>SCSIAdapter</c>, <c>Extension</c>.</summary>
    public string? Class { get; init; }

    public Guid? ClassGuid { get; init; }

    /// <summary>The provider with <c>%tokens%</c> resolved. Null when missing or when a token could not be resolved.</summary>
    public string? Provider { get; init; }

    /// <summary>
    ///     The provider exactly as written (unquoted), kept so that two packages with an unresolvable token can still be
    ///     told apart.
    /// </summary>
    public string? ProviderText { get; init; }

    /// <summary>Null when missing or not readable. See <see cref="InfDriverVer.TryParse" />.</summary>
    public InfDriverVer? DriverVer { get; init; }

    /// <summary>
    ///     The signature catalog. A driver package without one is not signed, and Windows refuses unsigned
    ///     drivers on 64-bit systems; Plainstall does not force them in.
    /// </summary>
    public string? CatalogFile { get; init; }

    /// <summary>
    ///     Architectures named by the <c>[Manufacturer]</c> decorations. An undecorated entry or a bare
    ///     <c>NT</c> counts as x86: since Windows Server 2003 SP1, Windows only uses Models sections
    ///     decorated with the architecture on 64-bit systems. <see cref="InfArchitectures.None" /> when the
    ///     INF has no usable <c>[Manufacturer]</c> section, for example a filter driver INF; then nothing is known.
    /// </summary>
    public InfArchitectures Architectures { get; init; }

    /// <summary>Why the file could not be read at all, or null.</summary>
    public string? Problem { get; init; }

    /// <summary>
    ///     A <c>[Version]</c> section with a signature or a device class. Files without one (for example a
    ///     driver CD's <c>autorun.inf</c>) share the extension but are not drivers, and DISM would reject them.
    /// </summary>
    public bool LooksLikeDriverInf =>
        HasVersionSection && (Signature is not null || Class is not null || ClassGuid is not null);

    /// <summary>
    ///     A disk controller driver, by class name or class GUID. Only these are needed by Windows Setup to see
    ///     the disk. Both are checked because some INFs give only the GUID, some only the name.
    /// </summary>
    public bool IsStorage =>
        string.Equals(Class, "SCSIAdapter", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Class, "HDC", StringComparison.OrdinalIgnoreCase)
        || ClassGuid == ScsiAdapterClassGuid
        || ClassGuid == HdcClassGuid;

    /// <summary>An extension INF adds settings to a base driver. Added after the base INFs of the same package.</summary>
    public bool IsExtension => string.Equals(Class, "Extension", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Reads a file from disk. I/O errors and oversized files give an <see cref="InfFile" /> with
    ///     <see cref="Problem" /> set.
    /// </summary>
    public static InfFile Load(string path)
    {
        try
        {
            var length = new FileInfo(path).Length;
            return length > MaxBytes
                ? new InfFile
                {
                    Path = path,
                    Problem = $"the file is larger than {MaxBytes >> 20} MB, so it is not a normal driver file"
                }
                : Parse(path, File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new InfFile { Path = path, Problem = "the file could not be read: " + ex.Message };
        }
    }

    /// <summary>Parses the bytes of an INF. <paramref name="path" /> is only recorded, never opened.</summary>
    public static InfFile Parse(string path, ReadOnlySpan<byte> content)
    {
        return content.IsEmpty
            ? new InfFile { Path = path, Problem = "the file is empty" }
            : ParseText(path, Decode(content));
    }

    internal static InfFile ParseText(string path, string text)
    {
        var document = InfDocument.Parse(text);
        var hasVersion = document.HasSection("Version");

        var providerRaw = document.First("Version", "Provider");
        var (provider, providerResolved) = providerRaw is null ? (null, false) : document.Resolve(providerRaw);
        var driverVerRaw = document.First("Version", "DriverVer");
        var driverVerText = driverVerRaw is null ? null : document.Resolve(driverVerRaw).Text;
        var classGuidText = Text(document, document.First("Version", "ClassGuid"));
        var catalog = document.First("Version", "CatalogFile")
                      ?? document.FirstWithPrefix("Version", "CatalogFile.");

        return new InfFile
        {
            Path = path,
            HasVersionSection = hasVersion,
            Signature = Text(document, document.First("Version", "Signature")),
            Class = Text(document, document.First("Version", "Class")),
            ClassGuid = Guid.TryParse(classGuidText, out var guid) ? guid : null,
            Provider = providerResolved && !string.IsNullOrWhiteSpace(provider) ? provider : null,
            ProviderText = providerRaw is null ? null : NullIfEmpty(InfDocument.Unquote(providerRaw)),
            DriverVer = InfDriverVer.TryParse(driverVerText),
            CatalogFile = Text(document, catalog),
            Architectures = ReadArchitectures(document),
            Problem = hasVersion ? null : "the file has no [Version] section, so it is not a driver INF"
        };
    }

    private static string? Text(InfDocument document, string? raw)
    {
        return raw is null ? null : NullIfEmpty(document.Resolve(raw).Text);
    }

    private static string? NullIfEmpty(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    ///     <c>%Mfg% = Models, NTamd64, NTarm64.10.0...22000</c>: everything after the Models section name is a
    ///     target decoration <c>NT[arch][.version...]</c>.
    /// </summary>
    private static InfArchitectures ReadArchitectures(InfDocument document)
    {
        var result = InfArchitectures.None;
        foreach (var line in document.Lines("Manufacturer"))
        {
            var fields = line.Key is null ? [] : InfDocument.SplitFields(line.Value);
            if (fields.Count <= 1)
            {
                result |= InfArchitectures.X86;
                continue;
            }

            result = fields.Skip(1)
                .Select(decoration => InfDocument.Unquote(decoration).Trim())
                .Where(d => d.StartsWith("NT", StringComparison.OrdinalIgnoreCase))
                .Select(d => d[2..].Split('.')[0])
                .Aggregate(result,
                    (all, archName) => all | (archName.Length == 0
                        ? InfArchitectures.X86
                        : InfArchitecturesExtensions.ParseName(archName)));
        }

        return result;
    }

    /// <summary>
    ///     INF files are UTF-16 LE with a BOM, UTF-8 with a BOM, or 8-bit text without one. Text without a
    ///     BOM is read as UTF-8 when it is valid UTF-8 and as Windows-1252 otherwise. The keys that matter
    ///     are ASCII in every encoding; only the provider name can differ, and the same bytes always give the
    ///     same text, which is what duplicate detection needs.
    /// </summary>
    internal static string Decode(ReadOnlySpan<byte> content)
    {
        string text;
        if (content.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
            text = Encoding.Unicode.GetString(content[2..]);
        else if (content.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
            text = Encoding.BigEndianUnicode.GetString(content[2..]);
        else if (content.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            text = Encoding.UTF8.GetString(content[3..]);
        else if (LooksLikeUtf16LittleEndian(content))
            text = Encoding.Unicode.GetString(content);
        else if (Utf8.IsValid(content))
            text = Encoding.UTF8.GetString(content);
        else
            text = Ansi.GetString(content);
        return text.Contains('\0') ? text.Replace("\0", "", StringComparison.Ordinal) : text;
    }

    /// <summary>UTF-16 text written without a BOM: ASCII characters leave every second byte zero.</summary>
    private static bool LooksLikeUtf16LittleEndian(ReadOnlySpan<byte> content)
    {
        var sample = content[..Math.Min(content.Length, 1024)];
        if (sample.Length < 8) return false;
        int oddZero = 0, evenZero = 0;
        for (var i = 0; i + 1 < sample.Length; i += 2)
        {
            if (sample[i] == 0) evenZero++;
            if (sample[i + 1] == 0) oddZero++;
        }

        var pairs = sample.Length / 2;
        return oddZero * 10 >= pairs * 7 && evenZero * 10 <= pairs;
    }

    private static Encoding CreateAnsiEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.Latin1;
        }
    }
}

/// <summary>
///     The INF text format as far as Plainstall reads it: sections, <c>key = value</c> lines, <c>;</c>
///     comments outside quotes, a trailing backslash to continue a line, <c>""</c> for a quote inside a
///     quoted string, and <c>%token%</c> strings from <c>[Strings]</c>. Section and key names are
///     case-insensitive. A repeated section adds to the earlier one, as in Windows.
/// </summary>
internal sealed class InfDocument
{
    private readonly List<string> _sectionOrder = [];
    private readonly Dictionary<string, List<InfLine>> _sections = new(StringComparer.OrdinalIgnoreCase);

    public bool HasSection(string name)
    {
        return _sections.ContainsKey(name);
    }

    public IReadOnlyList<InfLine> Lines(string section)
    {
        return _sections.TryGetValue(section, out var lines) ? lines : [];
    }

    /// <summary>The raw value of the first line with this key. Raw: still quoted, tokens not resolved.</summary>
    public string? First(string section, string key)
    {
        return Lines(section)
            .Where(line => string.Equals(line.Key, key, StringComparison.OrdinalIgnoreCase))
            .Select(line => line.Value)
            .FirstOrDefault();
    }

    public string? FirstWithPrefix(string section, string keyPrefix)
    {
        foreach (var line in Lines(section))
            if (line.Key is not null && line.Key.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
                return line.Value;

        return null;
    }

    public static InfDocument Parse(string text)
    {
        var document = new InfDocument();
        List<InfLine>? current = null;
        var pending = new StringBuilder();

        foreach (var rawLine in text.Split('\n'))
        {
            var line = StripComment(rawLine).Trim();
            if (line.EndsWith('\\'))
            {
                pending.Append(line, 0, line.Length - 1);
                continue;
            }

            if (pending.Length > 0)
            {
                line = pending.Append(line).ToString();
                pending.Clear();
            }

            Handle(line.Trim());
        }

        // A continuation on the very last line has nothing to join; keep what it has.
        Handle(pending.ToString().Trim());
        return document;

        void Handle(string line)
        {
            if (line.Length == 0) return;
            if (line[0] == '[')
            {
                var end = line.IndexOf(']');
                // A broken header: ignore what follows until the next good one rather than guess its section.
                current = end > 1 ? document.Section(line[1..end].Trim()) : null;
                return;
            }

            if (current is null) return;
            var equals = IndexOutsideQuotes(line, '=');
            current.Add(equals < 0
                ? new InfLine(null, line)
                : new InfLine(Unquote(line[..equals]), line[(equals + 1)..].Trim()));
        }
    }

    /// <summary>
    ///     Unquotes a raw value and replaces <c>%token%</c> with its text from <c>[Strings]</c>, then the US
    ///     English <c>[Strings.0409]</c>, then any other localized <c>[Strings.xxxx]</c> section.
    ///     <c>Resolved</c> is false when a token was not found; it is then left in the text as written.
    /// </summary>
    public (string Text, bool Resolved) Resolve(string raw)
    {
        var text = Unquote(raw);
        if (!text.Contains('%')) return (text, true);
        var result = new StringBuilder(text.Length);
        var resolved = true;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var close = c == '%' ? text.IndexOf('%', i + 1) : -1;
            if (close < 0)
            {
                result.Append(c);
                i++;
                continue;
            }

            if (close == i + 1)
            {
                result.Append('%');
            }
            else
            {
                var token = text[(i + 1)..close];
                if (LookUpString(token) is { } value)
                {
                    result.Append(value);
                }
                else
                {
                    resolved = false;
                    result.Append('%').Append(token).Append('%');
                }
            }

            i = close + 1;
        }

        return (result.ToString().Trim(), resolved);
    }

    private string? LookUpString(string token)
    {
        return StringSections()
            .SelectMany(Lines)
            .Where(line => string.Equals(line.Key, token, StringComparison.OrdinalIgnoreCase))
            .Select(line => Unquote(line.Value).Trim())
            .FirstOrDefault();
    }

    private IEnumerable<string> StringSections()
    {
        yield return "Strings";
        yield return "Strings.0409";
        foreach (var name in _sectionOrder.Where(name =>
                     name.StartsWith("Strings.", StringComparison.OrdinalIgnoreCase) &&
                     !name.Equals("Strings.0409", StringComparison.OrdinalIgnoreCase))) yield return name;
    }

    /// <summary>Splits at commas that are not inside quotes. Fields stay raw.</summary>
    public static IReadOnlyList<string> SplitFields(string value)
    {
        var fields = new List<string>();
        var start = 0;
        var quoted = false;
        for (var i = 0; i < value.Length; i++)
            switch (value[i])
            {
                case '"':
                    quoted = !quoted;
                    break;
                case ',' when !quoted:
                    fields.Add(value[start..i].Trim());
                    start = i + 1;
                    break;
            }

        fields.Add(value[start..].Trim());
        return fields;
    }

    /// <summary>Removes quotes; <c>""</c> inside a quoted part is one quote. Unquoted parts keep their text.</summary>
    public static string Unquote(string raw)
    {
        if (!raw.Contains('"')) return raw.Trim();
        var result = new StringBuilder(raw.Length);
        var quoted = false;
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c != '"')
            {
                result.Append(c);
            }
            else if (quoted && i + 1 < raw.Length && raw[i + 1] == '"')
            {
                result.Append('"');
                i++;
            }
            else
            {
                quoted = !quoted;
            }
        }

        return result.ToString().Trim();
    }

    private List<InfLine> Section(string name)
    {
        if (_sections.TryGetValue(name, out var lines)) return lines;
        lines = [];
        _sections[name] = lines;
        _sectionOrder.Add(name);
        return lines;
    }

    private static string StripComment(string line)
    {
        var index = IndexOutsideQuotes(line, ';');
        return (index < 0 ? line : line[..index]).TrimEnd('\r');
    }

    private static int IndexOutsideQuotes(string line, char wanted)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
            if (line[i] == '"')
                quoted = !quoted;
            else if (line[i] == wanted && !quoted) return i;

        return -1;
    }

    internal readonly record struct InfLine(string? Key, string Value);
}