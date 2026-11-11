using System.Text.RegularExpressions;

namespace Plainstall.Core.Imaging.Drivers;

/// <summary>
///     One driver package: a folder that directly contains at least one <c>.inf</c>, with the facts read
///     from those INFs. Files in subfolders belong to the package (INFs often take their binaries from
///     subfolders), but an INF in a subfolder makes that subfolder a package of its own.
/// </summary>
public sealed partial record DriverPackage
{
    /// <summary>Full path of the package folder.</summary>
    public required string Folder { get; init; }

    /// <summary>The INFs directly in <see cref="Folder" />, sorted by file name.</summary>
    public required IReadOnlyList<InfFile> Infs { get; init; }

    /// <summary>
    ///     The architecture in a folder name that <c>dism /Export-Driver</c> creates
    ///     (<c>&lt;inf name&gt;_&lt;arch&gt;_&lt;16 hex digits&gt;</c>), or <see cref="InfArchitectures.None" />.
    ///     It wins over the INF decorations: an INF may list several architectures, but the export holds
    ///     only the files for the one the PC uses.
    /// </summary>
    public InfArchitectures FolderArchitecture { get; init; }

    public string Name => Path.GetFileName(Folder.TrimEnd('\\', '/'));

    /// <summary>True when any INF of the package is a disk controller driver. See <see cref="InfFile.IsStorage" />.</summary>
    public bool IsStorage => Infs.Any(i => i.IsStorage);

    /// <summary>The architectures the package can be installed on, or <see cref="InfArchitectures.None" /> when unknown.</summary>
    public InfArchitectures Architectures =>
        FolderArchitecture != InfArchitectures.None
            ? FolderArchitecture
            : Infs.Aggregate(InfArchitectures.None, (all, inf) => all | inf.Architectures);

    /// <summary>The newest readable <c>DriverVer</c> among the INFs, or null when none is readable.</summary>
    public InfDriverVer? DriverVer => Infs.Select(i => i.DriverVer).Where(v => v is not null).Max();

    /// <summary>
    ///     Reads the architecture out of a <c>dism /Export-Driver</c> folder name, for example
    ///     <c>e1d.inf_amd64_0123456789abcdef</c>.
    /// </summary>
    public static InfArchitectures ParseExportFolderName(string folderName)
    {
        var match = ExportFolderPattern().Match(folderName);
        return match.Success ? InfArchitecturesExtensions.ParseName(match.Groups["arch"].Value) : InfArchitectures.None;
    }

    [GeneratedRegex("^(?<inf>.+?)_(?<arch>amd64|arm64|x86|arm|ia64)_[0-9a-f]{16}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExportFolderPattern();
}