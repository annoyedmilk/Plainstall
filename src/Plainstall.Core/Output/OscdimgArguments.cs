using System.Text;
using Plainstall.Core.Media;

namespace Plainstall.Core.Output;

/// <summary>Everything the oscdimg command line depends on. Plain values, so the builder stays a pure function.</summary>
public sealed record OscdimgArgumentOptions
{
    /// <summary>The folder whose content becomes the ISO. A full path.</summary>
    public required string MediaRoot { get; init; }

    /// <summary>The file oscdimg writes. A full path.</summary>
    public required string OutputPath { get; init; }

    /// <summary>True when <c>boot\etfsboot.com</c> exists in the media folder.</summary>
    public required bool HasBiosBootLoader { get; init; }

    /// <summary>
    ///     The EFI boot image relative to the media root, normally <c>efi\microsoft\boot\efisys.bin</c>,
    ///     or <c>efisys_noprompt.bin</c> when that is the only one.
    /// </summary>
    public required string EfiBootImage { get; init; }

    /// <summary>Wanted volume label; it is sanitized. Null or empty gives <see cref="OscdimgArguments.DefaultLabel" />.</summary>
    public string? VolumeLabel { get; init; }
}

/// <summary>Builds the argument list for oscdimg.exe.</summary>
public static class OscdimgArguments
{
    public const string DefaultLabel = "PLAINSTALL";

    /// <summary>The longest label oscdimg accepts.</summary>
    public const int MaxLabelLength = 32;

    /// <summary>
    ///     The arguments, one list item each. They are handed to the process runner as a list, which
    ///     quotes an item when it contains spaces; nothing here adds quotes of its own.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>
    ///             oscdimg must be started with <see cref="OscdimgArgumentOptions.MediaRoot" /> as its working
    ///             directory.
    ///         </b>
    ///         The boot files inside <c>-bootdata</c> are named relative to it. The reason:
    ///         <c>#</c> separates the boot entries, oscdimg knows no way to escape it, and so an absolute path
    ///         with a <c>#</c> in it (a user folder named "C#", for example) makes oscdimg 2.56 fail with
    ///         "Could not open boot sector file". The fixed relative names can never contain one. The media
    ///         folder and the output file are passed as full paths; a <c>#</c> there does no harm.
    ///     </para>
    ///     <list type="bullet">
    ///         <item><c>-m</c>: no size limit (the image is far larger than a CD).</item>
    ///         <item><c>-o</c>: store identical files once.</item>
    ///         <item>
    ///             <c>-u2 -udfver102</c>: a UDF 1.02 file system only. ISO 9660 cannot hold a file of 4 GB or more, and
    ///             install.wim is larger.
    ///         </item>
    ///         <item>
    ///             <c>-bootdata</c>: two boot entries (BIOS, then UEFI) for media that has the BIOS boot
    ///             sector, otherwise the UEFI entry alone.
    ///         </item>
    ///     </list>
    /// </remarks>
    public static IReadOnlyList<string> Build(OscdimgArgumentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Path.IsPathFullyQualified(options.MediaRoot))
            throw new ArgumentException("The media root must be a full path.", nameof(options));
        if (!Path.IsPathFullyQualified(options.OutputPath))
            throw new ArgumentException(
                "The output path must be a full path, because oscdimg runs in another working directory.",
                nameof(options));
        var efi = options.EfiBootImage;
        if (efi.Length == 0 || Path.IsPathRooted(efi) || efi.Contains('#') || efi.Contains(".."))
            throw new ArgumentException("The EFI boot image must be a path relative to the media root.",
                nameof(options));

        var needsBios = options.HasBiosBootLoader;
        var bootData = needsBios
            ? $"-bootdata:2#p0,e,b{MediaInventory.BiosBootLoaderPath}#pEF,e,b{efi}"
            : $"-bootdata:1#pEF,e,b{efi}";

        return
        [
            "-m",
            "-o",
            "-u2",
            "-udfver102",
            "-l" + SanitizeLabel(options.VolumeLabel),
            bootData,
            TrimTrailingSeparator(options.MediaRoot),
            options.OutputPath
        ];
    }

    /// <summary>
    ///     Turns any text into a volume label that is safe everywhere: upper-case letters A to Z, digits,
    ///     underscore and hyphen, at most 32 characters. Each run of other characters becomes one underscore.
    ///     Underscores and hyphens at the start and the end are removed.
    /// </summary>
    public static string SanitizeLabel(string? label)
    {
        var builder = new StringBuilder();
        foreach (var raw in (label ?? "").Trim())
        {
            var c = char.ToUpperInvariant(raw);
            var safe = c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_';
            if (safe)
                builder.Append(c);
            else if (builder.Length > 0 && builder[^1] != '_') builder.Append('_');
        }

        // A label must not start with something that looks like a switch, and trailing filler is noise.
        var cleaned = builder.ToString().Trim('_', '-');
        if (cleaned.Length > MaxLabelLength) cleaned = cleaned[..MaxLabelLength].TrimEnd('_', '-');
        return cleaned.Length == 0 ? DefaultLabel : cleaned;
    }

    private static string TrimTrailingSeparator(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        return trimmed.Length == 0 ? path : trimmed;
    }
}