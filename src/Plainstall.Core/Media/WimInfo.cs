namespace Plainstall.Core.Media;

/// <summary>How the file data inside a WIM is compressed, taken from the header flags.</summary>
public enum WimCompression
{
    /// <summary>The compression flag is not set.</summary>
    None,
    Xpress,
    Lzx,

    /// <summary>
    ///     The "recovery" compression of <c>.esd</c> files. Such a file can be exported from, but DISM can
    ///     neither mount nor split it, which is why callers need to know before they try.
    /// </summary>
    Lzms,

    /// <summary>The file says it is compressed, with a method this reader does not know.</summary>
    Unknown
}

/// <summary>One image (one edition, or one of the two boot images) as described in the WIM's XML data.</summary>
public sealed record WimImageInfo
{
    /// <summary>1-based index, the number DISM calls <c>/Index</c>.</summary>
    public required int Index { get; init; }

    /// <summary>Display name, for example "Windows 11 Pro". Localized, so never used to select an edition.</summary>
    public string Name { get; init; } = "";

    /// <summary>The language-neutral edition name, for example "Professional" or "Core". Empty when the image has none.</summary>
    public string EditionId { get; init; } = "";

    /// <summary>"Client" for desktop Windows, "Server" or "Server Core" for server images, "WindowsPE" for boot images.</summary>
    public string InstallationType { get; init; } = "";

    /// <summary>The raw <c>WINDOWS/ARCH</c> number: 0 = x86, 9 = x64, 12 = ARM64. Null when the image does not say.</summary>
    public int? ArchitectureCode { get; init; }

    /// <summary>
    ///     True for x64, the only architecture Plainstall works with. False for ARM64, x86, unknown codes and images
    ///     without the value.
    /// </summary>
    public bool IsX64 => ArchitectureCode == 9;

    /// <summary>A name for messages: "x64", "arm64", "x86 (32-bit)" or "unknown".</summary>
    public string ArchitectureName => ArchitectureCode switch
    {
        9 => "x64",
        12 => "arm64",
        0 => "x86 (32-bit)",
        null => "unknown",
        _ => $"unknown (code {ArchitectureCode})"
    };

    /// <summary>Major, minor, build and update revision, for example 10.0.26300.9457. Null when the image has no version.</summary>
    public Version? Version { get; init; }

    /// <summary>The build number, for example 26300. Zero when the image has no version.</summary>
    public int Build => Version?.Build ?? 0;

    public IReadOnlyList<string> Languages { get; init; } = [];

    /// <summary>The default language tag, for example "en-US". Null when the image names none.</summary>
    public string? DefaultLanguage { get; init; }
}

/// <summary>What the header and the XML data of a <c>.wim</c>, <c>.esd</c> or <c>.swm</c> file say about it.</summary>
public sealed record WimInfo
{
    public required string Path { get; init; }

    public long FileSize { get; init; }

    public WimCompression Compression { get; init; }

    /// <summary>
    ///     Number of images according to the header. The reader guarantees that <see cref="Images" /> has this many
    ///     entries.
    /// </summary>
    public int ImageCount { get; init; }

    /// <summary>Index of the image a PC boots when the file is used as a boot image; 0 when none is marked.</summary>
    public int BootIndex { get; init; }

    public int PartNumber { get; init; } = 1;

    public int TotalParts { get; init; } = 1;

    /// <summary>True for one part of a split set (<c>install.swm</c>, <c>install2.swm</c> and so on).</summary>
    public bool IsSplit => TotalParts > 1 || PartNumber > 1;

    /// <summary>False for LZMS (ESD-style) files, which DISM cannot split, and for files that are already split.</summary>
    public bool CanBeSplit => Compression != WimCompression.Lzms && !IsSplit;

    /// <summary>The images, ordered by index.</summary>
    public IReadOnlyList<WimImageInfo> Images { get; init; } = [];
}