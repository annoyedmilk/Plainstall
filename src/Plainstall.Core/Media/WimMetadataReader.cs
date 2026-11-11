using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Plainstall.Core.Media;

/// <summary>
///     Reads the header and the XML data of a WIM file directly. No DISM and no admin rights are needed,
///     so the inventory works in the non-elevated commands (<c>plan</c>, <c>inspect</c>) and in unit tests,
///     and it reads <c>.esd</c> files just as well, because the XML data is never compressed.
/// </summary>
/// <remarks>
///     The file is treated as untrusted input: every offset and size taken from the header is checked
///     against the real file length before anything is read or allocated.
/// </remarks>
public static class WimMetadataReader
{
    /// <summary>Size of the version 1 WIM header. The header states its own size and must agree.</summary>
    public const int HeaderSize = 208;

    /// <summary>
    ///     Upper limit for the XML data. Microsoft's 11-edition install.wim carries about 30 KB of it,
    ///     so anything near this limit is damage or an attempt to make us allocate a lot of memory.
    /// </summary>
    public const int MaxXmlBytes = 4 * 1024 * 1024;

    private const int OffsetHeaderSize = 8;
    private const int OffsetFlags = 16;
    private const int OffsetPartNumber = 40;
    private const int OffsetTotalParts = 42;
    private const int OffsetImageCount = 44;
    private const int OffsetXmlResource = 72;
    private const int OffsetBootIndex = 120;

    private const uint FlagCompression = 0x0000_0002;
    private const uint FlagCompressXpress = 0x0002_0000;
    private const uint FlagCompressLzx = 0x0004_0000;
    private const uint FlagCompressLzms = 0x0008_0000;

    /// <summary>Resource flag: the resource data is compressed. Never true for XML data written by Microsoft's tools.</summary>
    private const byte ResourceFlagCompressed = 0x04;

    private static ReadOnlySpan<byte> Magic => "MSWIM\0\0\0"u8;

    /// <summary>Opens <paramref name="path" /> for reading (shared with other readers) and returns its metadata.</summary>
    /// <exception cref="PlainstallException">The file cannot be opened or is not a valid WIM file.</exception>
    public static WimInfo Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                FileOptions.RandomAccess);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new PlainstallException(ErrorKind.Imaging,
                $"Could not open the Windows image file '{path}': {ex.Message} Check that the file exists and that no other program is changing it.",
                ex);
        }

        using (stream)
        {
            return Read(stream, path);
        }
    }

    /// <summary>
    ///     Reads the metadata from a seekable stream. <paramref name="path" /> is used for messages and stored in the
    ///     result.
    /// </summary>
    public static WimInfo Read(Stream stream, string path)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(path);
        if (!stream.CanSeek) throw new ArgumentException("The stream must be seekable.", nameof(stream));

        try
        {
            return ReadCore(stream, path);
        }
        catch (IOException ex)
        {
            throw new PlainstallException(ErrorKind.Imaging,
                $"Could not read the Windows image file '{path}' ({ex.Message.Trim()}). " +
                "The file, or the ISO it is in, is probably incomplete or damaged. If it came from a download, download it again and check its hash.",
                ex);
        }
    }

    private static WimInfo ReadCore(Stream stream, string path)
    {
        var fileLength = stream.Length;
        if (fileLength < HeaderSize) throw Damaged(path, "it is too small to be a Windows image");

        Span<byte> header = stackalloc byte[HeaderSize];
        stream.Position = 0;
        stream.ReadExactly(header);

        if (!header[..Magic.Length].SequenceEqual(Magic))
            throw new PlainstallException(ErrorKind.Imaging,
                $"'{path}' is not a Windows image file (.wim or .esd): it does not start with the WIM signature. " +
                "If it came from a download, download it again.");

        var statedHeaderSize = BinaryPrimitives.ReadUInt32LittleEndian(header[OffsetHeaderSize..]);
        if (statedHeaderSize != HeaderSize)
            throw Damaged(path, $"its header claims a size of {statedHeaderSize} bytes instead of {HeaderSize}");

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(header[OffsetFlags..]);
        int partNumber = BinaryPrimitives.ReadUInt16LittleEndian(header[OffsetPartNumber..]);
        int totalParts = BinaryPrimitives.ReadUInt16LittleEndian(header[OffsetTotalParts..]);
        var imageCount = BinaryPrimitives.ReadUInt32LittleEndian(header[OffsetImageCount..]);
        var bootIndex = BinaryPrimitives.ReadUInt32LittleEndian(header[OffsetBootIndex..]);

        // Resource header: 7 bytes stored size, 1 byte flags, 8 bytes offset, 8 bytes original size.
        var xmlResource = header.Slice(OffsetXmlResource, 24);
        var sizeAndFlags = BinaryPrimitives.ReadUInt64LittleEndian(xmlResource);
        var xmlSize = sizeAndFlags & 0x00FF_FFFF_FFFF_FFFF;
        var xmlFlags = (byte)(sizeAndFlags >> 56);
        var xmlOffset = BinaryPrimitives.ReadUInt64LittleEndian(xmlResource[8..]);
        var xmlOriginalSize = BinaryPrimitives.ReadUInt64LittleEndian(xmlResource[16..]);

        if (xmlSize == 0) throw Damaged(path, "it contains no image description");
        if ((xmlFlags & ResourceFlagCompressed) != 0 || xmlOriginalSize != xmlSize)
            throw Damaged(path, "its image description is stored in a way Plainstall cannot read");
        if (xmlSize > MaxXmlBytes)
            throw Damaged(path,
                $"its image description claims to be {xmlSize:N0} bytes long, which is far more than any real Windows image needs");

        // Compared as unsigned 64-bit values, so a huge offset cannot wrap around into a valid one.
        var length = (ulong)fileLength;
        if (xmlOffset < HeaderSize || xmlOffset > length || xmlSize > length - xmlOffset)
            throw Damaged(path,
                "its image description lies outside the file, so the file is probably cut off or was not downloaded completely");
        if (imageCount > int.MaxValue || bootIndex > imageCount)
            throw Damaged(path, "its header holds impossible image numbers");

        var xmlBytes = new byte[(int)xmlSize];
        stream.Position = (long)xmlOffset;
        stream.ReadExactly(xmlBytes);

        var images = ParseImages(xmlBytes, path);

        // Microsoft's tools keep the two counts equal. A difference means the file was damaged or edited by hand.
        if (images.Count != (int)imageCount)
            throw Damaged(path, $"its header announces {imageCount} image(s) but its description lists {images.Count}");
        if (images.Where((image, i) => image.Index != i + 1).Any())
            throw Damaged(path, "its images are not numbered 1, 2, 3 and so on");

        return new WimInfo
        {
            Path = path,
            FileSize = fileLength,
            Compression = CompressionOf(flags),
            ImageCount = (int)imageCount,
            BootIndex = (int)bootIndex,
            PartNumber = partNumber,
            TotalParts = totalParts,
            Images = images
        };
    }

    private static WimCompression CompressionOf(uint flags)
    {
        if ((flags & FlagCompression) == 0) return WimCompression.None;
        // LZMS first: an ESD must never be mistaken for a mountable WIM.
        if ((flags & FlagCompressLzms) != 0) return WimCompression.Lzms;
        if ((flags & FlagCompressLzx) != 0) return WimCompression.Lzx;
        return (flags & FlagCompressXpress) != 0 ? WimCompression.Xpress : WimCompression.Unknown;
    }

    private static List<WimImageInfo> ParseImages(byte[] xmlBytes, string path)
    {
        if (xmlBytes.Length % 2 != 0) throw Damaged(path, "its image description is not valid UTF-16 text");

        // The data is UTF-16 LE and normally starts with the byte order mark FF FE.
        var start = xmlBytes is [0xFF, 0xFE, ..] ? 2 : 0;
        string text;
        try
        {
            text = new UnicodeEncoding(false, false, true)
                .GetString(xmlBytes, start, xmlBytes.Length - start);
        }
        catch (DecoderFallbackException ex)
        {
            throw Damaged(path, "its image description is not valid UTF-16 text", ex);
        }

        XDocument document;
        try
        {
            // A DTD is how XML bombs and external entities get in. Real WIM data never has one.
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxXmlBytes,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                CloseInput = true
            };
            using var reader = XmlReader.Create(new StringReader(text), settings);
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw Damaged(path, "its image description is not well-formed XML", ex);
        }

        var root = document.Root;
        if (root is null || root.Name.LocalName != "WIM")
            throw Damaged(path, "its image description has an unexpected layout");

        var images = new List<WimImageInfo>();
        foreach (var element in root.Elements("IMAGE"))
        {
            if (!int.TryParse(element.Attribute("INDEX")?.Value, NumberStyles.None, CultureInfo.InvariantCulture,
                    out var index) || index < 1) throw Damaged(path, "one of its images has no valid number");
            images.Add(ParseImage(element, index));
        }

        images.Sort((a, b) => a.Index.CompareTo(b.Index));
        return images;
    }

    private static WimImageInfo ParseImage(XElement image, int index)
    {
        var windows = image.Element("WINDOWS");
        var languages = windows?.Element("LANGUAGES");
        var version = windows?.Element("VERSION");

        var languageList = languages?.Elements("LANGUAGE")
            .Select(e => e.Value.Trim())
            .Where(v => v.Length > 0)
            .ToArray() ?? [];
        var defaultLanguage = Text(languages, "DEFAULT");

        return new WimImageInfo
        {
            Index = index,
            Name = Text(image, "NAME"),
            EditionId = Text(windows, "EDITIONID"),
            InstallationType = Text(windows, "INSTALLATIONTYPE"),
            ArchitectureCode = Number(windows, "ARCH"),
            Version = ParseVersion(version),
            Languages = languageList,
            DefaultLanguage = defaultLanguage.Length > 0 ? defaultLanguage : null
        };
    }

    private static Version? ParseVersion(XElement? version)
    {
        if (Number(version, "MAJOR") is not { } major || Number(version, "MINOR") is not { } minor ||
            Number(version, "BUILD") is not { } build) return null;
        return new Version(major, minor, build, Number(version, "SPBUILD") ?? 0);
    }

    private static string Text(XElement? parent, string name)
    {
        return parent?.Element(name)?.Value.Trim() ?? "";
    }

    private static int? Number(XElement? parent, string name)
    {
        return int.TryParse(Text(parent, name), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static PlainstallException Damaged(string path, string reason, Exception? inner = null)
    {
        return new PlainstallException(ErrorKind.Imaging,
            $"The Windows image file '{path}' is damaged or incomplete: {reason}. " +
            "If it came from a download, download the ISO again and check its hash.", inner);
    }
}