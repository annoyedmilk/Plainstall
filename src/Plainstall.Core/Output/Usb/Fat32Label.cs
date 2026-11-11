using System.Globalization;
using System.Text;

namespace Plainstall.Core.Output.Usb;

/// <summary>
///     Turns any text into a label FAT32 accepts. Format refuses a bad label after the disk was already
///     cleared, so the label is made valid before anything is erased.
/// </summary>
public static class Fat32Label
{
    /// <summary>The label used when nothing usable is left of the requested one.</summary>
    public const string Default = "PLAINSTALL";

    /// <summary>A FAT volume label is stored in an 11-byte field of the boot sector.</summary>
    public const int MaxLength = 11;

    /// <summary>
    ///     Punctuation FAT allows in labels. Everything else (<c>* ? . , ; : / \ | + = &lt; &gt; [ ] "</c> and
    ///     control characters) is rejected by Windows.
    /// </summary>
    private const string AllowedPunctuation = " !#$%&'()-@^_`{}~";

    /// <summary>
    ///     Upper case, accents removed (É becomes E), characters FAT forbids dropped, at most 11 characters,
    ///     no leading or trailing spaces. Only ASCII is kept: other characters depend on the PC's code page.
    /// </summary>
    public static string Sanitize(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return Default;
        var builder = new StringBuilder(label.Length);
        var upperWithoutAccents = label.Normalize(NormalizationForm.FormD)
            .Where(raw => CharUnicodeInfo.GetUnicodeCategory(raw) != UnicodeCategory.NonSpacingMark)
            .Select(char.ToUpperInvariant);
        foreach (var c in upperWithoutAccents)
            if (char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || AllowedPunctuation.Contains(c))
                builder.Append(c);
            else if (char.IsWhiteSpace(c)) builder.Append(' ');

        var result = builder.ToString().Trim();
        if (result.Length > MaxLength) result = result[..MaxLength].TrimEnd();
        return result.Length == 0 ? Default : result;
    }
}