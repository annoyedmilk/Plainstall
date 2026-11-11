using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Plainstall.Core.Imaging.Drivers;

/// <summary>
///     Copies the drivers of the PC running Plainstall into a folder with
///     <c>dism.exe /English /Online /Export-Driver</c>. DISM exports only drivers that did not come with
///     Windows (the ones in the driver store marked as third-party), one folder per package, and leaves the
///     PC itself unchanged. Needs administrator rights.
///     <para>
///         The process is never killed on cancel. A cancel is reported after DISM has finished, so the export
///         folder is never left half-written by a killed process.
///     </para>
/// </summary>
public sealed partial class HostDriverExporter
{
    private readonly string _dismPath;
    private readonly string _logFile;
    private readonly ILogger _logger;
    private readonly IProcessRunner _runner;

    /// <param name="runner">Starts dism.exe and collects its output.</param>
    /// <param name="logFile">Where DISM writes its own log. Named in every error message.</param>
    /// <param name="dismPath">The dism.exe to run, or null for the one in the System32 folder.</param>
    /// <param name="logger">Where the exporter logs what it does, or null for no log.</param>
    public HostDriverExporter(IProcessRunner runner, string logFile, string? dismPath = null, ILogger? logger = null)
    {
        _runner = runner;
        _logFile = logFile;
        _dismPath = dismPath ?? Path.Combine(Environment.SystemDirectory, "dism.exe");
        _logger = logger ?? NullLogger.Instance;
    }

    [GeneratedRegex(@"Exporting\s+(\d+)\s+of\s+(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExportingPattern();

    /// <summary>
    ///     Exports into <paramref name="destination" />, which must not exist or be empty (DISM would mix its
    ///     folders with what is there), then scans it. Throws when DISM fails or when nothing was exported.
    /// </summary>
    /// <param name="destination">The folder DISM exports into.</param>
    /// <param name="progress">Percent of packages exported, from DISM's "Exporting n of m" lines.</param>
    /// <param name="cancellationToken">Reported once DISM has finished; the process itself is not stopped.</param>
    public async Task<DriverScanResult> ExportAsync(string destination, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var folder = Path.GetFullPath(destination);
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            throw new PlainstallException(ErrorKind.Internal, $"The driver export folder '{folder}' is not empty.");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_logFile))!);

        var output = new DismExeOutput(TryParseProgress, progress);
        var request = new ProcessRequest(_dismPath, Arguments(folder)) { KillOnCancel = false, OnLine = output.OnLine };

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("dism.exe: export the drivers of this PC to {Folder}", folder);
        var result = await _runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        if (!DismExeOutput.Succeeded(result.ExitCode))
            throw new PlainstallException(ErrorKind.Imaging,
                $"DISM could not export the drivers of this PC {output.Failure(result.ExitCode, _logFile)}"
                + (result.ExitCode is 5 or 740 or 1314 ? " Start Plainstall as administrator." : ""));

        progress?.Report(100);

        var scan = DriverScanner.Scan(folder, _logger, cancellationToken);
        if (scan.InfCount == 0)
            throw new PlainstallException(ErrorKind.Preflight,
                "This PC has no drivers from other makers to copy. Every device uses a driver that comes with Windows, "
                + "so the new installation finds the same drivers by itself. Switch off \"Include the drivers of this PC\" (\"fromThisPc\" in the profile) and start the build again. "
                + $"The DISM log is at '{_logFile}'.");
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Exported {Count} driver package(s) from this PC", scan.Packages.Count);
        return scan;
    }

    internal IReadOnlyList<string> Arguments(string destination)
    {
        return
        [
            "/English",
            "/Online",
            "/Export-Driver",
            $"/Destination:{destination}",
            $"/LogPath:{_logFile}"
        ];
    }

    /// <summary>DISM prints <c>Exporting 3 of 45 - oem2.inf: The driver package successfully exported.</c></summary>
    private static int? TryParseProgress(string line)
    {
        var match = ExportingPattern().Match(line);
        if (!match.Success
            || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var done)
            || !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var total)
            || total <= 0)
            return null;
        return Math.Clamp(done * 100 / total, 0, 100);
    }
}