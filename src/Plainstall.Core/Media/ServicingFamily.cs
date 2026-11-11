namespace Plainstall.Core.Media;

/// <summary>
///     A group of Windows builds that share one servicing stack and one set of update packages.
/// </summary>
/// <remarks>
///     Windows feature updates that arrive as an "enablement package" raise the build number without
///     changing the code base: 22631 is 22621 with a switch flipped, and 26200 and 26300 are 26100.
///     Microsoft's own 26H2 media pairs a 26300 install image with a 26100 boot image, so anything that
///     compares builds (boot image against install image, host DISM against image) must compare
///     families. Comparing build numbers for equality would reject Microsoft's own ISO.
/// </remarks>
public readonly record struct ServicingFamily
{
    /// <summary>Each row is one family; the first number is the base build that names it.</summary>
    private static readonly int[][] Families =
    [
        [22621, 22631],
        [26100, 26200, 26300]
    ];

    private ServicingFamily(int baseBuild)
    {
        BaseBuild = baseBuild;
    }

    /// <summary>The lowest build of the family. A build that belongs to no known family is its own base.</summary>
    public int BaseBuild { get; }

    /// <summary>The family of a build number such as 26300.</summary>
    public static ServicingFamily Of(int build)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(build);
        foreach (var family in Families)
            if (Array.IndexOf(family, build) >= 0)
                return new ServicingFamily(family[0]);

        return new ServicingFamily(build);
    }

    /// <summary>True when both builds are serviced by the same stack, for example 26100 and 26300.</summary>
    public static bool AreSame(int buildA, int buildB)
    {
        return Of(buildA) == Of(buildB);
    }
}