using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Plainstall.Core.Imaging;

/// <summary>
///     Export and split through <c>dism.exe</c>, the two WIM operations DismApi does not offer.
///     The process is never killed on cancel: an interrupted export leaves a damaged file.
/// </summary>
public sealed partial class DismExeWimTool : IWimTool
{
    private readonly string _dismPath;
    private readonly string _logFile;
    private readonly ILogger _logger;
    private readonly IProcessRunner _runner;
    private readonly string _scratchDirectory;

    public DismExeWimTool(IProcessRunner runner, string scratchDirectory, string logFile, string? dismPath = null,
        ILogger? logger = null)
    {
        _runner = runner;
        _scratchDirectory = scratchDirectory;
        _logFile = logFile;
        _dismPath = dismPath ?? Path.Combine(Environment.SystemDirectory, "dism.exe");
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task ExportImageAsync(string sourceFile, int sourceIndex, string destinationFile,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (File.Exists(destinationFile))
            // DISM would append a second image to the existing file instead of replacing it.
            throw new PlainstallException(ErrorKind.Internal, $"Export target '{destinationFile}' already exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationFile))!);

        await RunAsync($"export image {sourceIndex} of {Path.GetFileName(sourceFile)}",
                ExportArguments(sourceFile, sourceIndex, destinationFile), progress, cancellationToken)
            .ConfigureAwait(false);

        if (!File.Exists(destinationFile))
            throw new ImagingException($"DISM reported success, but '{destinationFile}' was not created.", 0);
    }

    public async Task<IReadOnlyList<string>> SplitImageAsync(string wimFile, string swmFile, int partSizeMb,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(swmFile))!;
        Directory.CreateDirectory(folder);
        await RunAsync($"split {Path.GetFileName(wimFile)}", SplitArguments(wimFile, swmFile, partSizeMb), progress,
            cancellationToken).ConfigureAwait(false);

        var stem = Path.GetFileNameWithoutExtension(swmFile);
        var parts = Directory.EnumerateFiles(folder, stem + "*.swm").Order(StringComparer.OrdinalIgnoreCase).ToList();
        return parts.Count != 0
            ? parts
            : throw new ImagingException("DISM reported success, but no .swm files were created.", 0);
    }

    [GeneratedRegex(@"(\d{1,3})[.,]\d%")]
    private static partial Regex ProgressPattern();

    private IReadOnlyList<string> ExportArguments(string sourceFile, int sourceIndex, string destinationFile)
    {
        return
        [
            "/English",
            "/Export-Image",
            $"/SourceImageFile:{sourceFile}",
            $"/SourceIndex:{sourceIndex.ToString(CultureInfo.InvariantCulture)}",
            $"/DestinationImageFile:{destinationFile}",
            "/Compress:max",
            "/CheckIntegrity",
            $"/ScratchDir:{_scratchDirectory}",
            $"/LogPath:{_logFile}"
        ];
    }

    internal IReadOnlyList<string> SplitArguments(string wimFile, string swmFile, int partSizeMb)
    {
        return
        [
            "/English",
            "/Split-Image",
            $"/ImageFile:{wimFile}",
            $"/SWMFile:{swmFile}",
            $"/FileSize:{partSizeMb.ToString(CultureInfo.InvariantCulture)}",
            "/CheckIntegrity",
            $"/ScratchDir:{_scratchDirectory}",
            $"/LogPath:{_logFile}"
        ];
    }

    private async Task RunAsync(string what, IReadOnlyList<string> arguments, IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_scratchDirectory);
        var output = new DismExeOutput(TryParseProgress, progress);
        var request = new ProcessRequest(_dismPath, arguments) { KillOnCancel = false, OnLine = output.OnLine };

        if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("dism.exe: {What}", what);
        var result = await _runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        if (DismExeOutput.Succeeded(result.ExitCode)) return;
        throw new ImagingException($"DISM could not {what} {output.Failure(result.ExitCode, _logFile)}",
            result.ExitCode);
    }

    /// <summary>DISM redraws lines such as <c>[=====   20.0%   ]</c>.</summary>
    internal static int? TryParseProgress(string line)
    {
        if (!line.StartsWith('[') || !line.Contains('%')) return null;
        var match = ProgressPattern().Match(line);
        return match.Success
            ? Math.Clamp(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), 0, 100)
            : null;
    }
}

/// <summary>
///     The output of one dism.exe run. Progress lines go to the progress handler. The last other lines
///     are kept for the error message.
/// </summary>
internal sealed class DismExeOutput(Func<string, int?> parseProgress, IProgress<int>? progress)
{
    private const int TailLines = 12;
    private readonly Queue<string> _tail = new();

    /// <summary>0 is success, 3010 is success that needs a restart.</summary>
    public static bool Succeeded(int exitCode)
    {
        return exitCode is 0 or 3010;
    }

    /// <summary>Called for each line, on the threads of the two output streams.</summary>
    public void OnLine(string line)
    {
        if (parseProgress(line) is { } percent)
        {
            progress?.Report(percent);
            return;
        }

        lock (_tail)
        {
            _tail.Enqueue(line);
            while (_tail.Count > TailLines) _tail.Dequeue();
        }
    }

    /// <summary>The exit code with plain words, the last lines without the DISM banner, and the log path.</summary>
    public string Failure(int exitCode, string logFile)
    {
        string lastLines;
        lock (_tail)
        {
            lastLines = string.Join(" ", _tail.Where(l =>
                !l.StartsWith("Deployment Image Servicing", StringComparison.OrdinalIgnoreCase) &&
                !l.StartsWith("Version:", StringComparison.OrdinalIgnoreCase)));
        }

        var hint = ExplainExitCode(exitCode);
        return
            $"(exit code {exitCode}{(hint is null ? "" : ", " + hint)}). {lastLines.Trim()} The DISM log is at '{logFile}'.";
    }

    /// <summary>Plain words for the exit codes that users actually run into.</summary>
    private static string? ExplainExitCode(int exitCode)
    {
        return exitCode switch
        {
            2 or 3 => "a file or folder was not found",
            5 => "access was denied",
            21 => "the drive is not ready",
            32 => "a file is in use by another program",
            50 => "the request is not supported",
            87 => "this version of DISM does not know one of the options",
            112 => "the disk is full",
            1168 => "an element was not found",
            1392 => "a file or folder is damaged",
            1460 => "the operation timed out",
            740 or 1314 => "administrator rights are missing",
            _ => null
        };
    }
}