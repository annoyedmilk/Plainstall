using Plainstall.Core.Output;

namespace Plainstall.Core.Tests.Output;

public class OscdimgArgumentsTests
{
    private const string Efi = @"efi\microsoft\boot\efisys.bin";
    private const string EfiNoPrompt = @"efi\microsoft\boot\efisys_noprompt.bin";

    private static OscdimgArgumentOptions Options(
        bool hasBiosBootLoader = true,
        string efiBootImage = Efi,
        string mediaRoot = @"C:\work\media",
        string outputPath = @"D:\out\windows.iso.partial",
        string? label = "WIN11")
    {
        return new OscdimgArgumentOptions
        {
            MediaRoot = mediaRoot,
            OutputPath = outputPath,
            HasBiosBootLoader = hasBiosBootLoader,
            EfiBootImage = efiBootImage,
            VolumeLabel = label
        };
    }

    [Fact]
    public void X64_media_with_bios_loader_gets_both_boot_entries()
    {
        var arguments = OscdimgArguments.Build(Options());

        Assert.Equal(
            [
                "-m",
                "-o",
                "-u2",
                "-udfver102",
                "-lWIN11",
                @"-bootdata:2#p0,e,bboot\etfsboot.com#pEF,e,befi\microsoft\boot\efisys.bin",
                @"C:\work\media",
                @"D:\out\windows.iso.partial"
            ],
            arguments);
    }

    [Fact]
    public void X64_media_without_bios_loader_gets_the_uefi_entry_only()
    {
        var arguments = OscdimgArguments.Build(Options(false));

        Assert.Equal(@"-bootdata:1#pEF,e,befi\microsoft\boot\efisys.bin", arguments[5]);
        Assert.DoesNotContain(arguments, a => a.Contains("etfsboot", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Noprompt_efi_image_is_used_when_it_is_the_one_given()
    {
        var arguments = OscdimgArguments.Build(Options(efiBootImage: EfiNoPrompt));

        Assert.Equal(@"-bootdata:2#p0,e,bboot\etfsboot.com#pEF,e,befi\microsoft\boot\efisys_noprompt.bin",
            arguments[5]);
    }

    [Fact]
    public void Paths_with_spaces_stay_single_unquoted_arguments()
    {
        var arguments = OscdimgArguments.Build(Options(
            mediaRoot: @"C:\Program Data\Plain stall\work\media",
            outputPath: @"D:\My ISOs\windows 11.iso.partial"));

        // The process runner quotes each list item as a whole; quotes added here would end up inside the paths.
        Assert.Equal(8, arguments.Count);
        Assert.Equal(@"C:\Program Data\Plain stall\work\media", arguments[6]);
        Assert.Equal(@"D:\My ISOs\windows 11.iso.partial", arguments[7]);
        Assert.DoesNotContain(arguments, a => a.Contains('"'));
    }

    [Theory]
    [InlineData(@"C:\Users\C# Dev\work\media", @"D:\out\windows.iso.partial")]
    [InlineData(@"C:\work\media", @"D:\ISO #2\windows.iso.partial")]
    [InlineData(@"C:\a b\media, new", @"D:\out\windows.iso.partial")]
    public void Boot_files_are_named_relative_to_the_media_root_whatever_the_folders_are_called(string mediaRoot,
        string outputPath)
    {
        // '#' separates the entries inside -bootdata and cannot be escaped, so the media folder's own path must stay out of it.
        var arguments = OscdimgArguments.Build(Options(mediaRoot: mediaRoot, outputPath: outputPath));

        Assert.Equal(@"-bootdata:2#p0,e,bboot\etfsboot.com#pEF,e,befi\microsoft\boot\efisys.bin", arguments[5]);
        Assert.Equal(mediaRoot, arguments[6]);
        Assert.Equal(outputPath, arguments[7]);
    }

    [Theory]
    [InlineData("media", @"D:\out\windows.iso", @"efi\microsoft\boot\efisys.bin")] // relative media root
    [InlineData(@"C:\work\media", "windows.iso",
        @"efi\microsoft\boot\efisys.bin")] // relative output: oscdimg runs elsewhere
    [InlineData(@"C:\work\media", @"D:\out\windows.iso",
        @"C:\work\media\efi\microsoft\boot\efisys.bin")] // absolute boot image
    [InlineData(@"C:\work\media", @"D:\out\windows.iso", @"efi\micro#soft\efisys.bin")]
    [InlineData(@"C:\work\media", @"D:\out\windows.iso", @"..\elsewhere\efisys.bin")]
    [InlineData(@"C:\work\media", @"D:\out\windows.iso", "")]
    public void Paths_of_the_wrong_kind_are_a_programming_error(string mediaRoot, string outputPath,
        string efiBootImage)
    {
        Assert.Throws<ArgumentException>(() =>
            OscdimgArguments.Build(Options(mediaRoot: mediaRoot, outputPath: outputPath, efiBootImage: efiBootImage)));
    }

    [Theory]
    [InlineData(@"C:\work\media\", @"C:\work\media")]
    [InlineData(@"C:\work\media", @"C:\work\media")]
    [InlineData(@"E:\", @"E:\")] // a drive root keeps its backslash
    public void Trailing_backslash_of_the_media_root_is_removed(string mediaRoot, string expectedSource)
    {
        var arguments = OscdimgArguments.Build(Options(mediaRoot: mediaRoot));

        Assert.Equal(expectedSource, arguments[6]);
    }

    [Theory]
    [InlineData("WIN11", "WIN11")]
    [InlineData("win11 pro", "WIN11_PRO")]
    [InlineData("  Plainstall Windows 11  ", "PLAINSTALL_WINDOWS_11")]
    [InlineData("CCCOMA_X64FRE_EN-US_DV9", "CCCOMA_X64FRE_EN-US_DV9")]
    [InlineData("a/b\\c:d*e?f\"g<h>i|j", "A_B_C_D_E_F_G_H_I_J")]
    [InlineData("Fenêtre 11", "FEN_TRE_11")]
    [InlineData("a   b", "A_B")]
    [InlineData("__a__", "A")]
    [InlineData("-o", "O")] // must not look like another switch
    [InlineData("!!!", "PLAINSTALL")]
    [InlineData("", "PLAINSTALL")]
    [InlineData(null, "PLAINSTALL")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", "ABCDEFGHIJKLMNOPQRSTUVWXYZ012345")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ01234 6789", "ABCDEFGHIJKLMNOPQRSTUVWXYZ01234")]
    public void Label_is_sanitized(string? label, string expected)
    {
        var sanitized = OscdimgArguments.SanitizeLabel(label);

        Assert.Equal(expected, sanitized);
        Assert.InRange(sanitized.Length, 1, OscdimgArguments.MaxLabelLength);
        Assert.Equal("-l" + expected, OscdimgArguments.Build(Options(label: label))[4]);
    }
}