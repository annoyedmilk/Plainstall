using System.Buffers.Binary;
using System.Security;
using System.Text;

namespace Plainstall.Core.Tests.Media;

/// <summary>
///     Builds a small file that has a real WIM header and real WIM XML data but no image content.
///     That is all the metadata reader looks at, so tests need neither DISM nor a multi-gigabyte file.
/// </summary>
internal sealed class SyntheticWim
{
    public const uint FlagCompression = 0x2;
    public const uint FlagSpanned = 0x8;
    public const uint FlagXpress = 0x20000;
    public const uint FlagLzx = 0x40000;
    public const uint FlagLzms = 0x80000;

    public byte[] Magic { get; set; } = [.. "MSWIM\0\0\0"u8];
    public uint HeaderSize { get; set; } = 208;
    public uint Flags { get; set; } = FlagCompression | FlagLzx;
    public ushort PartNumber { get; set; } = 1;
    public ushort TotalParts { get; set; } = 1;
    public uint BootIndex { get; set; }
    public List<string> Images { get; } = [];

    /// <summary>Replaces the generated XML text when set.</summary>
    public string? XmlText { get; set; }

    /// <summary>Replaces the encoded XML bytes when set.</summary>
    public byte[]? XmlBytes { get; set; }

    public bool WriteByteOrderMark { get; set; } = true;

    /// <summary>Resource flags of the XML data. Real files use 0x02 (metadata); 0x04 would mean compressed.</summary>
    public byte XmlResourceFlags { get; set; } = 0x02;

    public uint? ImageCountInHeader { get; set; }
    public ulong? XmlOffsetInHeader { get; set; }
    public ulong? XmlSizeInHeader { get; set; }
    public ulong? XmlOriginalSizeInHeader { get; set; }

    /// <summary>Stand-in for the compressed file data that sits between the header and the XML in a real file.</summary>
    public int PayloadBytes { get; set; } = 512;

    /// <summary>Cuts the finished file to this length when set.</summary>
    public int? TruncateTo { get; set; }

    public SyntheticWim AddImage(
        string name,
        string? editionId,
        string installationType = "Client",
        int? arch = 9,
        int? build = 26300,
        int spBuild = 9457,
        string? language = "en-US")
    {
        var index = Images.Count + 1;
        var xml = new StringBuilder();
        xml.Append($"<IMAGE INDEX=\"{index}\"><DIRCOUNT>30000</DIRCOUNT><FILECOUNT>110000</FILECOUNT>");
        xml.Append("<TOTALBYTES>20000000000</TOTALBYTES><HARDLINKBYTES>8000000000</HARDLINKBYTES>");
        xml.Append("<CREATIONTIME><HIGHPART>0x01DC3F00</HIGHPART><LOWPART>0x12345678</LOWPART></CREATIONTIME>");
        xml.Append("<WINDOWS>");
        if (arch is not null) xml.Append($"<ARCH>{arch}</ARCH>");
        xml.Append("<PRODUCTNAME>Microsoft® Windows® Operating System</PRODUCTNAME>");
        if (editionId is not null) xml.Append($"<EDITIONID>{SecurityElement.Escape(editionId)}</EDITIONID>");
        xml.Append($"<INSTALLATIONTYPE>{SecurityElement.Escape(installationType)}</INSTALLATIONTYPE>");
        xml.Append("<PRODUCTTYPE>WinNT</PRODUCTTYPE><PRODUCTSUITE>Terminal Server</PRODUCTSUITE>");
        if (language is not null)
            xml.Append($"<LANGUAGES><LANGUAGE>{language}</LANGUAGE><DEFAULT>{language}</DEFAULT></LANGUAGES>");
        if (build is not null)
            xml.Append(
                $"<VERSION><MAJOR>10</MAJOR><MINOR>0</MINOR><BUILD>{build}</BUILD><SPBUILD>{spBuild}</SPBUILD><SPLEVEL>0</SPLEVEL></VERSION>");
        xml.Append("<SYSTEMROOT>WINDOWS</SYSTEMROOT></WINDOWS>");
        xml.Append(
            $"<NAME>{SecurityElement.Escape(name)}</NAME><DESCRIPTION>{SecurityElement.Escape(name)}</DESCRIPTION>");
        xml.Append($"<FLAGS>{SecurityElement.Escape(editionId ?? "")}</FLAGS>");
        xml.Append($"<DISPLAYNAME>{SecurityElement.Escape(name)}</DISPLAYNAME></IMAGE>");
        Images.Add(xml.ToString());
        return this;
    }

    /// <summary>The eleven editions of Microsoft's 26H2 x64 ISO, in its order.</summary>
    public static SyntheticWim Install26H2()
    {
        var wim = new SyntheticWim();
        (string Name, string EditionId)[] editions =
        [
            ("Windows 11 Home", "Core"),
            ("Windows 11 Home N", "CoreN"),
            ("Windows 11 Home Single Language", "CoreSingleLanguage"),
            ("Windows 11 Education", "Education"),
            ("Windows 11 Education N", "EducationN"),
            ("Windows 11 Pro", "Professional"),
            ("Windows 11 Pro N", "ProfessionalN"),
            ("Windows 11 Pro Education", "ProfessionalEducation"),
            ("Windows 11 Pro Education N", "ProfessionalEducationN"),
            ("Windows 11 Pro for Workstations", "ProfessionalWorkstation"),
            ("Windows 11 Pro N for Workstations", "ProfessionalWorkstationN")
        ];
        foreach (var (name, editionId) in editions) wim.AddImage(name, editionId);
        return wim;
    }

    /// <summary>A boot.wim like Microsoft's: Windows PE, then Windows Setup as the boot image, both build 26100.</summary>
    public static SyntheticWim Boot26H2()
    {
        var wim = new SyntheticWim { BootIndex = 2 };
        wim.AddImage("Microsoft Windows PE (amd64)", "WindowsPE", "WindowsPE", build: 26100);
        wim.AddImage("Microsoft Windows Setup (amd64)", "WindowsPE", "WindowsPE", build: 26100);
        return wim;
    }

    public byte[] Build()
    {
        var xmlText = XmlText ?? $"<WIM><TOTALBYTES>123456789</TOTALBYTES>{string.Concat(Images)}</WIM>";
        var xml = XmlBytes;
        if (xml is null)
        {
            var body = Encoding.Unicode.GetBytes(xmlText);
            xml = WriteByteOrderMark ? [0xFF, 0xFE, .. body] : body;
        }

        var xmlOffset = (ulong)(208 + PayloadBytes);
        var file = new byte[208 + PayloadBytes + xml.Length];
        var header = file.AsSpan(0, 208);
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], 0x00010D00);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], 0x8000);
        Guid.NewGuid().TryWriteBytes(header[24..]);
        BinaryPrimitives.WriteUInt16LittleEndian(header[40..], PartNumber);
        BinaryPrimitives.WriteUInt16LittleEndian(header[42..], TotalParts);
        BinaryPrimitives.WriteUInt32LittleEndian(header[44..], ImageCountInHeader ?? (uint)Images.Count);

        // XML resource header at offset 72: 7 bytes size, 1 byte flags, 8 bytes offset, 8 bytes original size.
        var size = XmlSizeInHeader ?? (ulong)xml.Length;
        BinaryPrimitives.WriteUInt64LittleEndian(header[72..],
            (size & 0x00FF_FFFF_FFFF_FFFF) | ((ulong)XmlResourceFlags << 56));
        BinaryPrimitives.WriteUInt64LittleEndian(header[80..], XmlOffsetInHeader ?? xmlOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(header[88..], XmlOriginalSizeInHeader ?? size);
        BinaryPrimitives.WriteUInt32LittleEndian(header[120..], BootIndex);

        Random.Shared.NextBytes(file.AsSpan(208, PayloadBytes));
        xml.CopyTo(file.AsSpan(208 + PayloadBytes));
        return TruncateTo is { } length ? file[..length] : file;
    }

    public string WriteTo(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Build());
        return path;
    }
}