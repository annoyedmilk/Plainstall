using System.Text;
using Plainstall.Core.Imaging.Drivers;

namespace Plainstall.Core.Tests.Imaging.Drivers;

public sealed class InfFileTests
{
    private const string Path = @"C:\drivers\test.inf";

    [Fact]
    public void Utf16_with_bom_is_read()
    {
        var inf = InfFile.Parse(Path, SyntheticInf.Utf16(SyntheticInf.Text()));

        Assert.True(inf.LooksLikeDriverInf);
        Assert.Equal("Net", inf.Class);
        Assert.Equal(new Guid("4D36E972-E325-11CE-BFC1-08002BE10318"), inf.ClassGuid);
        Assert.Equal("Contoso Ltd.", inf.Provider);
        Assert.Equal(new InfDriverVer(new DateOnly(2024, 7, 18), new Version(1, 2, 3, 4)), inf.DriverVer);
        Assert.Equal("contoso.cat", inf.CatalogFile);
        Assert.Equal(InfArchitectures.Amd64, inf.Architectures);
        Assert.Null(inf.Problem);
    }

    [Fact]
    public void Utf8_with_bom_is_read_including_non_ascii_provider()
    {
        var text = SyntheticInf.Text(mfgString: "\"Müller Geräte GmbH\"");
        var inf = InfFile.Parse(Path, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text)]);
        Assert.Equal("Müller Geräte GmbH", inf.Provider);
        Assert.Equal("Net", inf.Class);
    }

    [Fact]
    public void Ansi_without_bom_is_read_as_windows_1252()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var text = SyntheticInf.Text(mfgString: "\"Müller Geräte\"");
        var inf = InfFile.Parse(Path, Encoding.GetEncoding(1252).GetBytes(text));
        Assert.Equal("Müller Geräte", inf.Provider);
        Assert.Equal(InfArchitectures.Amd64, inf.Architectures);
    }

    [Fact]
    public void Utf16_without_bom_is_recognised()
    {
        var inf = InfFile.Parse(Path, Encoding.Unicode.GetBytes(SyntheticInf.Text()));
        Assert.Equal("Contoso Ltd.", inf.Provider);
    }

    [Theory]
    [InlineData("Class = SCSIAdapter", "SCSIAdapter")]
    [InlineData("Class = \"SCSIAdapter\"", "SCSIAdapter")]
    [InlineData("Class=\"SCSI\"\"Adapter\"", "SCSI\"Adapter")]
    [InlineData("Class = SCSIAdapter ; a comment", "SCSIAdapter")]
    [InlineData("Class = \"Has ; semicolon\" ; real comment", "Has ; semicolon")]
    public void Quoted_and_unquoted_values(string line, string expected)
    {
        var inf = SyntheticInf.Parse($"[Version]\nSignature=\"$Windows NT$\"\n{line}\n");
        Assert.Equal(expected, inf.Class);
    }

    [Fact]
    public void A_provider_token_is_resolved_through_strings()
    {
        var inf = SyntheticInf.Parse(SyntheticInf.Text(mfgString: "\"Contoso, Inc.\""));
        Assert.Equal("Contoso, Inc.", inf.Provider);
        Assert.Equal("%Mfg%", inf.ProviderText);
    }

    [Fact]
    public void A_provider_token_falls_back_to_the_us_english_strings_section()
    {
        var inf = SyntheticInf.Parse(
            "[Version]\nSignature=$Windows NT$\nProvider=%Vendor%\n[Strings.0407]\nVendor=\"Deutsch\"\n[Strings.0409]\nVendor=\"English\"\n");
        Assert.Equal("English", inf.Provider);
    }

    [Fact]
    public void A_provider_token_mixed_with_text_and_a_literal_percent()
    {
        var inf = SyntheticInf.Parse(
            "[Version]\nSignature=$Windows NT$\nProvider=%Vendor% 100%% Labs\n[strings]\nvendor=Contoso\n");
        Assert.Equal("Contoso 100% Labs", inf.Provider);
    }

    [Fact]
    public void Missing_strings_section_leaves_the_provider_unknown()
    {
        var inf = SyntheticInf.Parse("[Version]\nSignature=\"$Windows NT$\"\nClass=Net\nProvider=%ManufacturerName%\n");
        Assert.Null(inf.Provider);
        Assert.Equal("%ManufacturerName%", inf.ProviderText);
        Assert.Equal("Net", inf.Class);
    }

    [Fact]
    public void A_date_only_driver_ver_is_valid()
    {
        var inf = SyntheticInf.Parse(SyntheticInf.Text(driverVer: "3/5/2021"));
        Assert.Equal(new InfDriverVer(new DateOnly(2021, 3, 5), null), inf.DriverVer);
    }

    [Theory]
    [InlineData("13/01/2024,1.0")]
    [InlineData("02/30/2024")]
    [InlineData("2024-07-18,1.0.0.0")]
    [InlineData("07/18/24,1.0")]
    [InlineData("07/18/2024,1.x.0")]
    [InlineData("07/18/2024,1.0.0.0.0")]
    [InlineData("07/18/2024,70000.0")]
    [InlineData("tomorrow")]
    [InlineData("")]
    public void A_malformed_driver_ver_is_unknown(string value)
    {
        var inf = SyntheticInf.Parse(SyntheticInf.Text(driverVer: value));
        Assert.Null(inf.DriverVer);
        Assert.Equal("Net", inf.Class);
    }

    [Fact]
    public void Driver_ver_ranks_by_date_then_version_and_a_short_version_is_padded()
    {
        var older = InfDriverVer.TryParse("01/01/2023,9.9.9.9")!;
        var newer = InfDriverVer.TryParse("01/02/2023,1.0")!;
        Assert.True(newer.CompareTo(older) > 0);
        Assert.Equal(0,
            InfDriverVer.TryParse("01/02/2023,1.0")!.CompareTo(InfDriverVer.TryParse("01/02/2023,1.0.0.0")));
        Assert.Equal(0, InfDriverVer.TryParse("01/02/2023")!.CompareTo(InfDriverVer.TryParse("01/02/2023,0.0.0.0")));
        Assert.True(InfDriverVer.TryParse("01/02/2023,1.0.0.1")!.CompareTo(InfDriverVer.TryParse("01/02/2023")) > 0);
    }

    [Fact]
    public void Comments_blank_lines_and_mixed_case_names()
    {
        const string text = """
                            ; header comment

                            [VERSION]   ; section comment
                               signature = "$Windows NT$"

                            ; between
                            CLASS=hdc
                            classguid = 4d36e96a-e325-11ce-bfc1-08002be10318
                            PROVIDER = %mfg%
                            driverver = 01/02/2023 , 5.6.7.8 ; trailing

                            [manufacturer]
                            %MFG%=Models,ntAMD64

                            [STRINGS]
                            MFG="Fabrikam"
                            """;
        var inf = SyntheticInf.Parse(text);
        Assert.Equal("hdc", inf.Class);
        Assert.Equal(InfFile.HdcClassGuid, inf.ClassGuid);
        Assert.Equal("Fabrikam", inf.Provider);
        Assert.Equal(new Version(5, 6, 7, 8), inf.DriverVer!.Version);
        Assert.Equal(InfArchitectures.Amd64, inf.Architectures);
        Assert.True(inf.IsStorage);
    }

    [Fact]
    public void Line_continuation_joins_lines()
    {
        const string text =
            "[Version]\nSignature=\"$Windows NT$\"\nDriverVer = 07/18/2024,\\\n    1.2.3.4\n[Manufacturer]\n%Mfg% = Models, \\\n NTamd64, \\\n NTarm64\n";
        var inf = SyntheticInf.Parse(text);
        Assert.Equal(new Version(1, 2, 3, 4), inf.DriverVer!.Version);
        Assert.Equal(InfArchitectures.Amd64 | InfArchitectures.Arm64, inf.Architectures);
    }

    [Theory]
    [InlineData("%Mfg% = Models, NTamd64", InfArchitectures.Amd64)]
    [InlineData("%Mfg% = Models, NTarm64.10.0...22000", InfArchitectures.Arm64)]
    [InlineData("%Mfg% = Models, NTx86, NTamd64, NTarm64",
        InfArchitectures.X86 | InfArchitectures.Amd64 | InfArchitectures.Arm64)]
    [InlineData("%Mfg% = Models", InfArchitectures.X86)]
    [InlineData("%Mfg% = Models, NT", InfArchitectures.X86)]
    [InlineData("%Mfg% = Models, NTia64", InfArchitectures.Ia64)]
    [InlineData("\"Contoso\"", InfArchitectures.X86)]
    public void Architecture_detection(string manufacturerLine, InfArchitectures expected)
    {
        var inf = SyntheticInf.Parse(SyntheticInf.Text(manufacturerLine: manufacturerLine));
        Assert.Equal(expected, inf.Architectures);
    }

    [Fact]
    public void Without_a_manufacturer_section_the_architecture_is_unknown()
    {
        var inf = SyntheticInf.Parse(
            "[Version]\nSignature=\"$Windows NT$\"\nClass=ActivityMonitor\n[DefaultInstall.NTamd64]\nCopyFiles=Files\n");
        Assert.Equal(InfArchitectures.None, inf.Architectures);
        Assert.True(inf.LooksLikeDriverInf);
    }

    [Fact]
    public void An_empty_file_gives_unknown_fields()
    {
        var inf = InfFile.Parse(Path, []);
        Assert.False(inf.LooksLikeDriverInf);
        Assert.NotNull(inf.Problem);
        Assert.Null(inf.Class);
    }

    [Fact]
    public void Garbage_text_gives_unknown_fields()
    {
        var inf = InfFile.Parse(Path, "[[[ ]] = = \"unterminated\n[Version\nClass = ?\n]]]\n\\\n\\"u8);
        Assert.False(inf.LooksLikeDriverInf);
        Assert.Null(inf.Class);
        Assert.Null(inf.DriverVer);
    }

    [Fact]
    public void An_autorun_inf_is_not_a_driver_inf()
    {
        var inf = InfFile.Parse(Path, "[autorun]\nopen=setup.exe\nicon=setup.ico\n"u8);
        Assert.False(inf.LooksLikeDriverInf);
    }

    [Fact]
    public void A_nul_heavy_binary_body_does_not_throw()
    {
        var random = new Random(42);
        var bytes = new byte[64 * 1024];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = i % 3 == 0 ? (byte)random.Next(256) : (byte)0;
        var inf = InfFile.Parse(Path, bytes);
        Assert.False(inf.LooksLikeDriverInf);

        // A valid header followed by binary junk keeps what could be read.
        var mixed = InfFile.Parse(Path, [.. "[Version]\r\nSignature=$Windows NT$\r\nClass=Net\r\n"u8, .. bytes]);
        Assert.Equal("Net", mixed.Class);
    }

    [Fact]
    public void Load_reports_a_missing_file_as_a_problem_instead_of_throwing()
    {
        var inf = InfFile.Load(System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"),
            "gone.inf"));
        Assert.NotNull(inf.Problem);
        Assert.False(inf.LooksLikeDriverInf);
    }

    [Fact]
    public void Decorated_catalog_file_is_read_when_the_plain_one_is_missing()
    {
        var inf = SyntheticInf.Parse(
            "[Version]\nSignature=$Windows NT$\nClass=Net\nCatalogFile.NTamd64 = contoso64.cat\n");
        Assert.Equal("contoso64.cat", inf.CatalogFile);
    }
}