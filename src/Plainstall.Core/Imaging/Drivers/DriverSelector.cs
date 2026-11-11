namespace Plainstall.Core.Imaging.Drivers;

public enum DriverDropReason
{
    /// <summary>The folder has <c>.inf</c> files, but none of them is a driver INF (for example only <c>autorun.inf</c>).</summary>
    NoDriverInf,

    /// <summary>Built for another processor architecture than the image.</summary>
    WrongArchitecture,

    /// <summary>Another copy of the same driver with a newer (or equal) <c>DriverVer</c> is used instead.</summary>
    OlderDuplicate
}

/// <summary>A package that is not added, with the reason in plain words.</summary>
public sealed record DroppedDriver(DriverPackage Package, DriverDropReason Reason, string Note);

/// <summary>
///     The packages to add, in a stable order, and what was left out and why. Everything in
///     <see cref="Selected" /> goes into the install image.
/// </summary>
public sealed record DriverSelection(
    IReadOnlyList<DriverPackage> Selected,
    IReadOnlyList<DroppedDriver> Dropped,
    IReadOnlyList<string> Notes)
{
    /// <summary>
    ///     What goes into the Setup image of boot.wim: only disk controller drivers, so Windows Setup can see
    ///     the disk, and of those packages only the storage INFs. Companion INFs of a storage package
    ///     (control panels, extension INFs, software components) are not needed to see a disk, and network,
    ///     USB or graphics drivers in Setup have caused failures that are hard to trace.
    /// </summary>
    public IReadOnlyList<DriverPackage> ForSetupImage()
    {
        return
        [
            .. Selected
                .Where(p => p.IsStorage)
                .Select(p => p with { Infs = [.. p.Infs.Where(i => i.IsStorage)] })
        ];
    }
}

/// <summary>
///     Decides which driver packages to add. Pure: it reads nothing from disk and changes nothing on disk.
///     A dropped package stays in the user's folder; selection is a list, not a file operation.
///     <para>
///         Rules, in this order:
///     </para>
///     <list type="number">
///         <item>INFs that are not driver INFs are left out of a package; a package with none left is dropped.</item>
///         <item>
///             A package for another architecture than the image is dropped. When the architecture cannot be
///             told (no export folder name, no decorated <c>[Manufacturer]</c> entry), the package is kept and DISM
///             decides.
///         </item>
///         <item>
///             Duplicates are removed. Two packages are the same driver when they have the same INF file
///             names with the same providers (case-insensitive). Among duplicates the highest <c>DriverVer</c> wins, by date first, then by
///             version; on a tie the one that came first in the input wins. When no copy in a group has a readable
///             <c>DriverVer</c>, all copies are kept rather than guessing which is newer.
///         </item>
///     </list>
///     The result keeps the input order, so the caller decides the order by the order of the folders it scans.
/// </summary>
public static class DriverSelector
{
    public static DriverSelection Select(IEnumerable<DriverPackage> packages)
    {
        // Plainstall builds x64 images only.
        const InfArchitectures target = InfArchitectures.Amd64;
        var dropped = new List<DroppedDriver>();
        var notes = new List<string>();
        var candidates = new List<DriverPackage>();

        foreach (var package in packages)
        {
            var driverInfs = package.Infs.Where(i => i.LooksLikeDriverInf).ToList();
            if (driverInfs.Count == 0)
            {
                dropped.Add(new DroppedDriver(package, DriverDropReason.NoDriverInf,
                    $"'{package.Folder}' was left out: its .inf files are not driver files ({string.Join(", ", package.Infs.Select(i => i.FileName))})."));
                continue;
            }

            var usable = driverInfs.Count == package.Infs.Count ? package : package with { Infs = driverInfs };

            var architectures = usable.Architectures;
            if (architectures != InfArchitectures.None && (architectures & target) == 0)
            {
                dropped.Add(new DroppedDriver(usable, DriverDropReason.WrongArchitecture,
                    $"'{usable.Folder}' was left out: it is made for {architectures.Describe()}, and this Windows image is x64."));
                continue;
            }

            candidates.Add(usable);
        }

        var groups = candidates
            .Select((package, index) => (package, index))
            .GroupBy(x => DuplicateKey(x.package), StringComparer.OrdinalIgnoreCase);
        var losers = new HashSet<DriverPackage>(ReferenceEqualityComparer.Instance);
        foreach (var group in groups)
        {
            var members = group.OrderBy(x => x.index).Select(x => x.package).ToList();
            if (members.Count == 1) continue;
            var readable = members.Where(m => m.DriverVer is not null).ToList();
            if (readable.Count == 0)
            {
                notes.Add(
                    $"{members.Count} copies of the driver {DisplayName(members[0])} were found, and none says which version it is. All of them are added.");
                continue;
            }

            // First of the highest: MaxBy keeps the earliest element among equals.
            var winner = readable.MaxBy(m => m.DriverVer)!;
            foreach (var member in members.Where(m => !ReferenceEquals(m, winner)))
            {
                losers.Add(member);
                var why = member.DriverVer is null
                    ? "its version could not be read"
                    : $"it is version {member.DriverVer}";
                dropped.Add(new DroppedDriver(member, DriverDropReason.OlderDuplicate,
                    $"'{member.Folder}' was left out: {why}, and the copy in '{winner.Folder}' is version {winner.DriverVer}."));
            }
        }

        var selected = candidates.Where(c => !losers.Contains(c)).ToList();
        return new DriverSelection(selected, dropped, [.. dropped.Select(d => d.Note), .. notes]);
    }

    /// <summary>INF names with their providers.</summary>
    private static string DuplicateKey(DriverPackage package)
    {
        return string.Join(";", package.Infs
            .Select(i => i.FileName + "|" + (i.Provider ?? i.ProviderText ?? ""))
            .Order(StringComparer.OrdinalIgnoreCase));
    }

    private static string DisplayName(DriverPackage package)
    {
        return string.Join(", ", package.Infs.Select(i => i.FileName))
               + (package.Infs.Select(i => i.Provider).FirstOrDefault(p => p is not null) is { } provider
                   ? $" from {provider}"
                   : "");
    }
}