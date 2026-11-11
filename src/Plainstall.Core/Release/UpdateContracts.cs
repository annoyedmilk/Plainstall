namespace Plainstall.Core.Release;

/// <summary>A newer release than the one that is running.</summary>
/// <param name="Version">The version of the newer release.</param>
/// <param name="PageUrl">The release page, for the user to open in a browser. Plainstall never downloads an update itself.</param>
public sealed record UpdateInfo(Version Version, Uri PageUrl);

/// <summary>
///     Asks whether a newer Plainstall exists. This is the only network request Plainstall makes that
///     does not go to Microsoft, and all it leads to is a notice: nothing is downloaded or installed.
/// </summary>
public interface IUpdateChecker
{
    /// <summary>
    ///     Returns the newer release, or null when the running version is current or the check could not
    ///     be made. A failed check is never an error for the user: being offline is normal.
    /// </summary>
    Task<UpdateInfo?> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default);
}