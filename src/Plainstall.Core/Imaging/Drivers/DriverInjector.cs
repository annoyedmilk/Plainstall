using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Plainstall.Core.Imaging.Drivers;

public enum DriverPackageStatus
{
    /// <summary>Every driver INF of the package was added, and no INF failed.</summary>
    Applied,

    /// <summary>Some INFs were added and at least one failed. See <see cref="DriverInjector" /> for what that means.</summary>
    PartiallyApplied,

    /// <summary>Nothing was tried, for a reason that is not an error.</summary>
    Skipped,

    /// <summary>No INF of the package could be added.</summary>
    Failed
}

/// <summary>What happened to one INF.</summary>
public sealed record DriverInfOutcome(InfFile Inf, ActionOutcome Outcome);

/// <summary>What happened to one package, with the outcome of each of its INFs.</summary>
public sealed record DriverPackageOutcome(
    DriverPackage Package,
    DriverPackageStatus Status,
    string? Reason,
    IReadOnlyList<DriverInfOutcome> Infs)
{
    /// <summary>
    ///     For the build log. A partly added package counts as failed: the user asked for the whole driver,
    ///     and the reason names the INFs that are missing.
    /// </summary>
    public ActionOutcome ToActionOutcome()
    {
        return Status switch
        {
            DriverPackageStatus.Applied => ActionOutcome.Applied,
            DriverPackageStatus.Skipped => ActionOutcome.Skipped(Reason ?? "skipped"),
            _ => ActionOutcome.Failed(Reason ?? "the driver could not be added")
        };
    }
}

/// <summary>The result of adding a set of driver packages to one image.</summary>
public sealed record DriverInjectionResult(IReadOnlyList<DriverPackageOutcome> Packages)
{
    /// <summary>Number of INFs DISM accepted.</summary>
    public int InfsAdded => Packages.Sum(p => p.Infs.Count(i => i.Outcome.Status == ActionStatus.Applied));

    /// <summary>
    ///     True when at least one INF went in. When false for the Setup image and nothing else changed there,
    ///     the caller should discard that mount instead of committing it, so boot.wim stays as Microsoft ships it.
    /// </summary>
    public bool AnythingAdded => InfsAdded > 0;
}

/// <summary>Progress while adding drivers: which INF, and how far through all INFs.</summary>
public readonly record struct DriverInjectionProgress(int InfNumber, int InfCount, string PackageName, string InfName)
{
    public int Percent => InfCount == 0 ? 100 : Math.Clamp((InfNumber - 1) * 100 / InfCount, 0, 100);
}

/// <summary>
///     Adds driver packages to a mounted image, one INF at a time through <see cref="IMountedImage.AddDriver" />,
///     so that one bad driver is recorded and skipped and the rest still go in. Unsigned drivers are not
///     forced: DISM rejects them on a 64-bit image and that rejection is recorded as a failure.
///     <para>
///         Order: packages in the given order; inside a package, base INFs before extension INFs, each by name,
///         because an extension INF only means something next to its base driver.
///     </para>
///     <para>
///         <b>A package that fails half-way.</b> When one INF of a package fails, the INFs added before it stay in
///         the image. The INFs after it are still tried. DISM imports each INF into the driver store of the image
///         as a separate unit, so a failed INF probably leaves nothing behind. (A test of this needs an elevated,
///         mounted image, so it is not verified here.) The image is then consistent, but the driver can work only
///         in part, for example a base driver without its companion component. <see cref="IMountedImage" /> cannot
///         remove a driver, so this class cannot roll back a package. Thus the image is kept, and the package is
///         reported as failed (<see cref="DriverPackageOutcome.ToActionOutcome" />). The build then ends
///         "completed with failures", and the reason names the missing INFs. The user can install a missing driver
///         later in Windows.
///     </para>
///     <para>
///         Must run while no registry hive of the image is loaded by Plainstall: DISM writes the image's SYSTEM
///         and DRIVERS hives itself when it adds a driver.
///     </para>
/// </summary>
public sealed class DriverInjector
{
    private readonly IMountedImage _image;
    private readonly ILogger _logger;

    public DriverInjector(IMountedImage image, ILogger? logger = null)
    {
        _image = image;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    ///     Adds every INF of every package. Never throws for a single driver. A cancel request is honoured
    ///     between packages, never inside one or inside a DISM call, and throws <see cref="OperationCanceledException" />.
    /// </summary>
    public DriverInjectionResult Inject(IReadOnlyList<DriverPackage> packages,
        IProgress<DriverInjectionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var total = packages.Sum(p => p.Infs.Count);
        var number = 0;
        var outcomes = new List<DriverPackageOutcome>(packages.Count);
        foreach (var package in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (package.Infs.Count == 0)
            {
                outcomes.Add(new DriverPackageOutcome(package, DriverPackageStatus.Skipped,
                    "the folder has no driver files", []));
                continue;
            }

            var infOutcomes = new List<DriverInfOutcome>();
            foreach (var inf in Ordered(package.Infs))
            {
                number++;
                progress?.Report(new DriverInjectionProgress(number, total, package.Name, inf.FileName));
                infOutcomes.Add(new DriverInfOutcome(inf, AddOne(inf)));
            }

            var outcome = Summarize(package, infOutcomes);
            var level = outcome.Status is DriverPackageStatus.Failed or DriverPackageStatus.PartiallyApplied
                ? LogLevel.Warning
                : LogLevel.Information;
            if (_logger.IsEnabled(level))
                _logger.Log(level, "{Status,-7} driver {Package}{Reason}", outcome.Status, package.Folder,
                    outcome.Reason is null ? "" : " (" + outcome.Reason + ")");
            outcomes.Add(outcome);
        }

        return new DriverInjectionResult(outcomes);
    }

    private static IEnumerable<InfFile> Ordered(IEnumerable<InfFile> infs)
    {
        return infs.OrderBy(i => i.IsExtension).ThenBy(i => i.FileName, StringComparer.OrdinalIgnoreCase);
    }

    private ActionOutcome AddOne(InfFile inf)
    {
        if (!inf.LooksLikeDriverInf)
            return ActionOutcome.Skipped("not a driver file" + (inf.Problem is null ? "" : ": " + inf.Problem));
        if (!File.Exists(inf.Path)) return ActionOutcome.Failed("the file is no longer there");
        try
        {
            _image.AddDriver(inf.Path);
            return ActionOutcome.Applied;
        }
        catch (Exception ex) when (ex is PlainstallException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Adding the driver {Inf} failed: {Message}", inf.Path, ex.Message);
            var unsigned = inf.CatalogFile is null
                ? " This driver has no catalog file, so it is probably not signed. Windows accepts only signed drivers. Plainstall does not force unsigned drivers into the image."
                : "";
            return ActionOutcome.Failed(ex.Message + unsigned);
        }
    }

    private static DriverPackageOutcome Summarize(DriverPackage package, IReadOnlyList<DriverInfOutcome> infs)
    {
        var applied = infs.Where(i => i.Outcome.Status == ActionStatus.Applied).ToList();
        var failed = infs.Where(i => i.Outcome.Status == ActionStatus.Failed).ToList();
        if (failed.Count == 0)
            return applied.Count > 0
                ? new DriverPackageOutcome(package, DriverPackageStatus.Applied, null, infs)
                : new DriverPackageOutcome(package, DriverPackageStatus.Skipped,
                    string.Join("; ", infs.Select(i => $"{i.Inf.FileName}: {i.Outcome.Reason}")), infs);
        var failures = string.Join("; ", failed.Select(i => $"{i.Inf.FileName}: {i.Outcome.Reason}"));
        if (applied.Count == 0) return new DriverPackageOutcome(package, DriverPackageStatus.Failed, failures, infs);
        return new DriverPackageOutcome(package, DriverPackageStatus.PartiallyApplied,
            $"{applied.Count} of {applied.Count + failed.Count} parts were added, so this driver may not work fully. Not added: {failures}",
            infs);
    }
}