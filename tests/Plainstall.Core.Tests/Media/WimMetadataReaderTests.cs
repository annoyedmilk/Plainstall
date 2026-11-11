using System.IO.Pipes;
using System.Text;
using Plainstall.Core.Media;

namespace Plainstall.Core.Tests.Media;

public class WimMetadataReaderTests
{
    private static WimInfo Read(SyntheticWim wim)
    {
        return WimMetadataReader.Read(new MemoryStream(wim.Build()), "test.wim");
    }

    private static PlainstallException ReadFails(SyntheticWim wim)
    {
        var ex = Assert.Throws<PlainstallException>(() => Read(wim));
        Assert.Equal(ErrorKind.Imaging, ex.Kind);
        Assert.Contains("test.wim", ex.Message);
        return ex;
    }

    [Fact]
    public void Multi_edition_install_image_is_read_completely()
    {
        var info = Read(SyntheticWim.Install26H2());

        Assert.Equal(WimCompression.Lzx, info.Compression);
        Assert.True(info.CanBeSplit);
        Assert.False(info.IsSplit);
        Assert.Equal(1, info.PartNumber);
        Assert.Equal(1, info.TotalParts);
        Assert.Equal(0, info.BootIndex);
        Assert.Equal(11, info.ImageCount);
        Assert.Equal(11, info.Images.Count);
        Assert.Equal(Enumerable.Range(1, 11), info.Images.Select(i => i.Index));

        var pro = info.Images[5];
        Assert.Equal(6, pro.Index);
        Assert.Equal("Windows 11 Pro", pro.Name);
        Assert.Equal("Professional", pro.EditionId);
        Assert.Equal("Client", pro.InstallationType);
        Assert.Equal(9, pro.ArchitectureCode);
        Assert.True(pro.IsX64);
        Assert.Equal("x64", pro.ArchitectureName);
        Assert.Equal(new Version(10, 0, 26300, 9457), pro.Version);
        Assert.Equal(26300, pro.Build);
        Assert.Equal(["en-US"], pro.Languages);
        Assert.Equal("en-US", pro.DefaultLanguage);

        Assert.Equal("Core", info.Images[0].EditionId);
        Assert.Equal("ProfessionalWorkstationN", info.Images[10].EditionId);
    }

    [Fact]
    public void Boot_image_with_two_images_reports_its_boot_index()
    {
        var info = Read(SyntheticWim.Boot26H2());

        Assert.Equal(2, info.ImageCount);
        Assert.Equal(2, info.BootIndex);
        Assert.Equal("Microsoft Windows PE (amd64)", info.Images[0].Name);
        Assert.Equal("Microsoft Windows Setup (amd64)", info.Images[1].Name);
        Assert.Equal("WindowsPE", info.Images[1].InstallationType);
        Assert.Equal(26100, info.Images[1].Build);
    }

    [Fact]
    public void Reads_from_a_file_path()
    {
        using var folder = new TempDirectory();
        var path = SyntheticWim.Install26H2().WriteTo(folder.Combine("install.wim"));

        var info = WimMetadataReader.Read(path);

        Assert.Equal(path, info.Path);
        Assert.Equal(new FileInfo(path).Length, info.FileSize);
        Assert.Equal(11, info.Images.Count);
    }

    [Theory]
    [InlineData(0u, WimCompression.None)]
    [InlineData(SyntheticWim.FlagCompression | SyntheticWim.FlagXpress, WimCompression.Xpress)]
    [InlineData(SyntheticWim.FlagCompression | SyntheticWim.FlagLzx, WimCompression.Lzx)]
    [InlineData(SyntheticWim.FlagCompression | SyntheticWim.FlagLzms, WimCompression.Lzms)]
    [InlineData(SyntheticWim.FlagCompression | 0x200000u, WimCompression.Unknown)]
    public void Compression_comes_from_the_header_flags(uint flags, WimCompression expected)
    {
        var wim = SyntheticWim.Install26H2();
        wim.Flags = flags;

        Assert.Equal(expected, Read(wim).Compression);
    }

    [Fact]
    public void Lzms_file_is_reported_as_not_splittable()
    {
        var wim = SyntheticWim.Install26H2();
        wim.Flags = SyntheticWim.FlagCompression | SyntheticWim.FlagLzms;

        var info = Read(wim);

        Assert.Equal(WimCompression.Lzms, info.Compression);
        Assert.False(info.CanBeSplit);
        Assert.Equal(11, info.Images.Count);
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(2, 3)]
    public void Split_parts_are_reported(int part, int total)
    {
        var wim = SyntheticWim.Install26H2();
        wim.Flags |= SyntheticWim.FlagSpanned;
        wim.PartNumber = (ushort)part;
        wim.TotalParts = (ushort)total;

        var info = Read(wim);

        Assert.True(info.IsSplit);
        Assert.False(info.CanBeSplit);
        Assert.Equal(part, info.PartNumber);
        Assert.Equal(total, info.TotalParts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(207)]
    public void File_shorter_than_the_header_is_rejected(int length)
    {
        var wim = SyntheticWim.Install26H2();
        wim.TruncateTo = length;

        Assert.Contains("too small", ReadFails(wim).Message);
    }

    [Fact]
    public void File_cut_off_inside_the_xml_is_rejected()
    {
        var wim = SyntheticWim.Install26H2();
        wim.TruncateTo = wim.Build().Length - 10;

        Assert.Contains("cut off", ReadFails(wim).Message);
    }

    [Fact]
    public void File_cut_off_right_after_the_header_is_rejected()
    {
        var wim = SyntheticWim.Install26H2();
        wim.TruncateTo = 208;

        Assert.Contains("cut off", ReadFails(wim).Message);
    }

    [Fact]
    public void Wrong_magic_is_rejected()
    {
        var wim = SyntheticWim.Install26H2();
        wim.Magic = [.. "WLPWM\0\0\0"u8];

        Assert.Contains("not a Windows image file", ReadFails(wim).Message);
    }

    [Fact]
    public void Unexpected_header_size_is_rejected()
    {
        var wim = SyntheticWim.Install26H2();
        wim.HeaderSize = 212;

        Assert.Contains("212", ReadFails(wim).Message);
    }

    [Theory]
    [InlineData(1_000_000UL)]
    [InlineData(100UL)] // inside the header
    [InlineData(ulong.MaxValue - 16)] // would wrap around when the size is added
    public void Xml_offset_outside_the_file_is_rejected(ulong offset)
    {
        var wim = SyntheticWim.Install26H2();
        wim.XmlOffsetInHeader = offset;

        Assert.Contains("outside the file", ReadFails(wim).Message);
    }

    [Fact]
    public void Oversized_xml_is_rejected_before_anything_is_allocated()
    {
        var wim = SyntheticWim.Install26H2();
        wim.XmlSizeInHeader = WimMetadataReader.MaxXmlBytes + 2UL;

        Assert.Contains("far more than any real Windows image needs", ReadFails(wim).Message);
    }

    [Fact]
    public void Xml_that_really_is_too_large_is_rejected()
    {
        var wim = new SyntheticWim
        {
            XmlText = $"<WIM><!--{new string('x', WimMetadataReader.MaxXmlBytes / 2)}--></WIM>"
        };

        Assert.Contains("far more than any real Windows image needs", ReadFails(wim).Message);
    }

    [Theory]
    // A DTD (an XML bomb, an external entity) is refused before it is expanded.
    [InlineData(
        "<!DOCTYPE WIM [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;\">]><WIM><IMAGE INDEX=\"1\"><NAME>&b;</NAME></IMAGE></WIM>",
        1u, "not well-formed XML")]
    [InlineData(
        "<!DOCTYPE WIM [<!ENTITY x SYSTEM \"file:///C:/Windows/win.ini\">]><WIM><IMAGE INDEX=\"1\"><NAME>&x;</NAME></IMAGE></WIM>",
        1u, "not well-formed XML")]
    [InlineData("<WIM><IMAGE INDEX=\"1\"><NAME>Windows", 1u, "not well-formed XML")]
    [InlineData("<ESD><IMAGE INDEX=\"1\" /></ESD>", 1u, "unexpected layout")]
    [InlineData("<WIM><IMAGE INDEX=\"1\" /><IMAGE INDEX=\"1\" /></WIM>", 2u, "not numbered")]
    [InlineData("<WIM><IMAGE INDEX=\"1\" /><IMAGE INDEX=\"3\" /></WIM>", 2u, "not numbered")]
    [InlineData("<WIM><IMAGE /></WIM>", 1u, "no valid number")]
    [InlineData("<WIM><IMAGE INDEX=\"0\" /></WIM>", 1u, "no valid number")]
    [InlineData("<WIM><IMAGE INDEX=\"-1\" /></WIM>", 1u, "no valid number")]
    [InlineData("<WIM><IMAGE INDEX=\"one\" /></WIM>", 1u, "no valid number")]
    public void Bad_xml_is_rejected(string xml, uint imageCount, string expected)
    {
        var wim = new SyntheticWim { XmlText = xml, ImageCountInHeader = imageCount };

        Assert.Contains(expected, ReadFails(wim).Message);
    }

    [Fact]
    public void Xml_with_an_odd_number_of_bytes_is_rejected()
    {
        var wim = SyntheticWim.Install26H2();
        wim.XmlBytes = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("<WIM></WIM>"), 0x20];

        Assert.Contains("UTF-16", ReadFails(wim).Message);
    }

    [Fact]
    public void Empty_xml_resource_is_rejected()
    {
        var wim = SyntheticWim.Install26H2();
        wim.XmlSizeInHeader = 0;

        Assert.Contains("no image description", ReadFails(wim).Message);
    }

    [Fact]
    public void Compressed_xml_resource_is_rejected()
    {
        var wim = SyntheticWim.Install26H2();
        wim.XmlResourceFlags = 0x06;

        Assert.Contains("cannot read", ReadFails(wim).Message);
    }

    [Fact]
    public void Xml_whose_original_size_differs_is_rejected()
    {
        var wim = SyntheticWim.Install26H2();
        wim.XmlOriginalSizeInHeader = 999_999;

        Assert.Contains("cannot read", ReadFails(wim).Message);
    }

    [Theory]
    [InlineData(10u)]
    [InlineData(12u)]
    [InlineData(uint.MaxValue)]
    public void Image_count_that_disagrees_with_the_xml_is_rejected(uint countInHeader)
    {
        var wim = SyntheticWim.Install26H2();
        wim.ImageCountInHeader = countInHeader;

        ReadFails(wim);
    }

    [Fact]
    public void Boot_index_beyond_the_image_count_is_rejected()
    {
        var wim = SyntheticWim.Boot26H2();
        wim.BootIndex = 3;

        Assert.Contains("impossible image numbers", ReadFails(wim).Message);
    }

    [Fact]
    public void Images_listed_out_of_order_are_sorted_by_index()
    {
        var wim = new SyntheticWim
        {
            XmlText =
                "<WIM><IMAGE INDEX=\"2\"><NAME>second</NAME></IMAGE><IMAGE INDEX=\"1\"><NAME>first</NAME></IMAGE></WIM>",
            ImageCountInHeader = 2
        };

        Assert.Equal(["first", "second"], Read(wim).Images.Select(i => i.Name));
    }

    [Fact]
    public void Xml_without_a_byte_order_mark_is_still_read()
    {
        var wim = SyntheticWim.Install26H2();
        wim.WriteByteOrderMark = false;

        Assert.Equal(11, Read(wim).Images.Count);
    }

    [Fact]
    public void Image_without_windows_data_gets_empty_values()
    {
        var wim = new SyntheticWim
            { XmlText = "<WIM><IMAGE INDEX=\"1\"><NAME>Data</NAME></IMAGE></WIM>", ImageCountInHeader = 1 };

        var image = Assert.Single(Read(wim).Images);

        Assert.Equal("Data", image.Name);
        Assert.Equal("", image.EditionId);
        Assert.Equal("", image.InstallationType);
        Assert.Null(image.ArchitectureCode);
        Assert.False(image.IsX64);
        Assert.Equal("unknown", image.ArchitectureName);
        Assert.Null(image.Version);
        Assert.Equal(0, image.Build);
        Assert.Empty(image.Languages);
        Assert.Null(image.DefaultLanguage);
    }

    [Theory]
    [InlineData(9, true, "x64")]
    [InlineData(12, false, "arm64")]
    [InlineData(0, false, "x86 (32-bit)")]
    [InlineData(5, false, "unknown (code 5)")]
    public void Architecture_codes_are_mapped(int code, bool isX64, string expectedName)
    {
        var image = Assert.Single(Read(new SyntheticWim().AddImage("Windows", "Professional", arch: code)).Images);

        Assert.Equal(code, image.ArchitectureCode);
        Assert.Equal(isX64, image.IsX64);
        Assert.Equal(expectedName, image.ArchitectureName);
    }

    [Fact]
    public void Wim_without_images_is_valid_and_empty()
    {
        var info = Read(new SyntheticWim());

        Assert.Equal(0, info.ImageCount);
        Assert.Empty(info.Images);
    }

    [Fact]
    public void Missing_file_gives_a_plain_message()
    {
        using var folder = new TempDirectory();
        var path = folder.Combine("nothing.wim");

        var ex = Assert.Throws<PlainstallException>(() => WimMetadataReader.Read(path));

        Assert.Equal(ErrorKind.Imaging, ex.Kind);
        Assert.Contains(path, ex.Message);
    }

    [Fact]
    public void Non_seekable_stream_is_refused()
    {
        using var pipe = new AnonymousPipeServerStream();

        Assert.Throws<ArgumentException>(() => WimMetadataReader.Read(pipe, "pipe"));
    }
}