using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Plainstall.Core.Media;

namespace Plainstall.Core.Output;

public sealed record IsoWriteRequest
{
    /// <summary>The staged media folder whose content becomes the ISO.</summary>
    public required string MediaRoot { get; init; }

    /// <summary>The ISO file to create.</summary>
    public required string OutputPath { get; init; }

    /// <summary>A trusted oscdimg.exe, from <see cref="OscdimgLocator.LocateAsync" /> called right before.</summary>
    public required string OscdimgPath { get; init; }

    /// <summary>Wanted volume label; sanitized by <see cref="OscdimgArguments.SanitizeLabel" />.</summary>
    public string? VolumeLabel { get; init; }

    /// <summary>
    ///     True replaces an existing file at <see cref="OutputPath" />. False (the default) refuses, so
    ///     the host can ask the user first.
    /// </summary>
    public bool Overwrite { get; init; }
}

/// <summary>A finished ISO file.</summary>
public sealed record IsoWriteResult(string Path, long SizeBytes, string Sha256);

/// <summary>Builds a bootable ISO file from a media folder with oscdimg.</summary>
/// <remarks>
///     oscdimg writes to <c>&lt;output&gt;.partial</c>. Only after the file passed its checks is it
///     renamed, so a file with the final name is always a complete ISO and a cancelled or failed run
///     leaves nothing behind that looks usable.
/// </remarks>
public sealed partial class IsoWriter
{
    public const int SectorSize = 2048;

    /// <summary>Suffix of the file oscdimg writes before the result is verified and renamed.</summary>
    public const string PartialSuffix = ".partial";

    private static readonly string[] EfiBootImages =
        [MediaInventory.EfiBootImagePath, MediaInventory.EfiBootImageNoPromptPath];

    private readonly Func<string, long> _freeSpaceProbe;
    private readonly ILogger _logger;

    private readonly IProcessRunner _processRunner;

    public IsoWriter(IProcessRunner processRunner, ILogger? logger = null)
        : this(processRunner, logger, null)
    {
    }

    /// <param name="processRunner">Starts oscdimg and collects its output.</param>
    /// <param name="logger">Where the writer logs what it does, or null for no log.</param>
    /// <param name="freeSpaceProbe">Returns the free bytes available in a folder. Replaced in tests.</param>
    internal IsoWriter(IProcessRunner processRunner, ILogger? logger, Func<string, long>? freeSpaceProbe)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        _processRunner = processRunner;
        _logger = logger ?? NullLogger.Instance;
        _freeSpaceProbe = freeSpaceProbe ?? GetFreeBytes;
    }

    /// <summary>Writes the ISO, verifies it and returns its path, size and SHA-256.</summary>
    /// <param name="request">The media folder, the output file, the oscdimg to run and the volume label.</param>
    /// <param name="progress">Receives the percentage oscdimg reports, 0 to 100.</param>
    /// <param name="cancellationToken">Stops oscdimg and removes the partial file.</param>
    /// <exception cref="PlainstallException">
    ///     The request cannot be carried out, oscdimg failed, or the result did not pass
    ///     verification.
    /// </exception>
    /// <exception cref="OperationCanceledException">Cancelled. oscdimg is stopped and the partial file removed.</exception>
    public async Task<IsoWriteResult> WriteAsync(IsoWriteRequest request, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.MediaRoot, nameof(request));
        ArgumentException.ThrowIfNullOrEmpty(request.OutputPath, nameof(request));
        ArgumentException.ThrowIfNullOrEmpty(request.OscdimgPath, nameof(request));

        var mediaRoot = Path.GetFullPath(request.MediaRoot);
        var outputPath = Path.GetFullPath(request.OutputPath);
        var partialPath = outputPath + PartialSuffix;

        if (!Directory.Exists(mediaRoot))
            throw new PlainstallException(ErrorKind.Internal, $"The media folder '{mediaRoot}' does not exist.");
        if (!File.Exists(request.OscdimgPath))
            throw new PlainstallException(ErrorKind.Internal,
                $"The ISO tool was not found at '{request.OscdimgPath}'.");
        if (IsInside(outputPath, mediaRoot))
            throw new PlainstallException(ErrorKind.InvalidInput,
                $"The ISO file cannot be saved inside the folder it is built from ('{mediaRoot}'). Choose another place for the ISO.");

        var efiBootImage = EfiBootImages.FirstOrDefault(relative => File.Exists(Path.Combine(mediaRoot, relative)))
                           ?? throw new PlainstallException(ErrorKind.Output,
                               $"The media folder '{mediaRoot}' has no EFI boot image ({EfiBootImages[0]}), so a bootable ISO cannot be made from it.");
        var hasBiosBootLoader = File.Exists(Path.Combine(mediaRoot, MediaInventory.BiosBootLoaderPath));

        if (Directory.Exists(outputPath))
            throw new PlainstallException(ErrorKind.InvalidInput,
                $"'{outputPath}' is a folder. Give the ISO a file name, for example '{Path.Combine(outputPath, "windows11.iso")}'.");
        if (File.Exists(outputPath) && !request.Overwrite)
            throw new PlainstallException(ErrorKind.Output,
                $"The file '{outputPath}' already exists. Choose another name, or confirm that it may be replaced.");

        var outputFolder = Path.GetDirectoryName(outputPath)
                           ?? throw new PlainstallException(ErrorKind.InvalidInput,
                               $"'{outputPath}' is not a usable file name for the ISO.");
        var (mediaBytes, largestFile) = MeasureMedia(mediaRoot, cancellationToken);
        try
        {
            Directory.CreateDirectory(outputFolder);
            // A leftover from an interrupted run would only take up the space this run needs.
            File.Delete(partialPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PlainstallException(ErrorKind.Output,
                $"The folder '{outputFolder}' cannot be written to: {ex.Message} Choose another place for the ISO.",
                ex);
        }

        if (CheckTarget(outputFolder, mediaBytes, _freeSpaceProbe(outputFolder), null) is [var problem, ..])
            throw new PlainstallException(ErrorKind.Preflight, problem);

        var arguments = OscdimgArguments.Build(new OscdimgArgumentOptions
        {
            MediaRoot = mediaRoot,
            OutputPath = partialPath,
            HasBiosBootLoader = hasBiosBootLoader,
            EfiBootImage = efiBootImage,
            VolumeLabel = request.VolumeLabel
        });
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Running {Tool} {Arguments}", request.OscdimgPath, string.Join(' ', arguments));

        var lastPercent = -1;
        var progressGate = new Lock();
        var processRequest = new ProcessRequest(request.OscdimgPath, arguments)
        {
            // The boot files in the arguments are named relative to the media folder (see OscdimgArguments.Build).
            WorkingDirectory = mediaRoot,
            // oscdimg only writes the one output file, so stopping it is safe.
            KillOnCancel = true,
            OnLine = line =>
            {
                if (TryParsePercent(line) is { } percent)
                    // oscdimg starts again at 0% for a second pass and its two output streams arrive on
                    // two threads, so only a value higher than the last one is passed on.
                    lock (progressGate)
                    {
                        if (percent <= lastPercent) return;
                        lastPercent = percent;
                        progress?.Report(percent);
                    }
                else if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("oscdimg: {Line}", line);
            }
        };

        try
        {
            var result = await _processRunner.RunAsync(processRequest, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                var tail = string.Join(" ", result.Lines.Where(l => TryParsePercent(l) is null).TakeLast(4));
                throw new PlainstallException(ErrorKind.Output,
                    $"The ISO tool oscdimg failed with exit code {result.ExitCode}. {tail}".TrimEnd() +
                    " No ISO was created. Check the log for details, and that the target disk is not full.");
            }

            VerifyIsoFile(partialPath, largestFile, outputPath);
            var sha256 = await HashHex.Sha256OfFileAsync(partialPath, null, cancellationToken).ConfigureAwait(false);
            var size = new FileInfo(partialPath).Length;
            try
            {
                File.Move(partialPath, outputPath, request.Overwrite);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new PlainstallException(ErrorKind.Output,
                    $"The ISO was built, but could not be saved as '{outputPath}': {ex.Message} Close any program that uses that file. Then try again.",
                    ex);
            }

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Wrote {Path}: {Size} bytes, SHA-256 {Hash}", outputPath, size, sha256);
            return new IsoWriteResult(outputPath, size, sha256);
        }
        catch
        {
            TryDelete(partialPath);
            throw;
        }
    }

    /// <summary>
    ///     The space an ISO of media of this size needs. The ISO holds every file once plus a small file system, so
    ///     the media size with a margin is enough.
    /// </summary>
    public static long RequiredBytes(long mediaBytes)
    {
        return mediaBytes + mediaBytes / 100 + 16L * 1024 * 1024;
    }

    /// <summary>
    ///     The reasons why the ISO of media of this size cannot be saved in a folder: too little free space, or a
    ///     file system that cannot hold a file of 4 GB or more. A fact that is null is not known and not checked.
    /// </summary>
    /// <param name="outputFolder">The folder the ISO goes to, for the messages.</param>
    /// <param name="mediaBytes">The size of all files of the media.</param>
    /// <param name="freeBytes">The free space in the folder, or null when it is not known.</param>
    /// <param name="fileSystem">The file system of the drive, for example NTFS, or null when it is not known.</param>
    /// <param name="otherBytes">Space on the same drive that the build needs at the same time, for example the work folder.</param>
    public static IReadOnlyList<string> CheckTarget(string outputFolder, long mediaBytes, long? freeBytes,
        string? fileSystem, long otherBytes = 0)
    {
        var problems = new List<string>();
        var isoBytes = RequiredBytes(mediaBytes);
        var neededBytes = isoBytes + otherBytes;
        // FAT32 and the older FAT file systems hold files of less than 4 GB only. exFAT does not have this limit.
        if (fileSystem is not null && fileSystem.StartsWith("FAT", StringComparison.OrdinalIgnoreCase) &&
            isoBytes >= 4L << 30)
            problems.Add(
                $"The ISO file cannot be saved in '{outputFolder}': that drive is formatted as {fileSystem}, which cannot hold a file of 4 GB or more. " +
                "Choose a place on an NTFS or exFAT drive.");
        if (freeBytes is { } free && free < neededBytes)
            problems.Add(
                $"There is not enough free space in '{outputFolder}' for the ISO: about {ToGigabytes(neededBytes)} GB are needed, {ToGigabytes(free)} GB are free. " +
                "Free up space or choose another place for the ISO.");
        return problems;
    }

    /// <summary>
    ///     Checks that a file looks like a finished disc image: whole 2048-byte sectors, at least as
    ///     large as the largest file it must contain, and a volume descriptor where the standards put it.
    /// </summary>
    /// <remarks>
    ///     Sectors 16 to 31 hold the volume recognition sequence. Each descriptor there carries a
    ///     five-character identifier at offset 1: <c>CD001</c> for ISO 9660, <c>BEA01</c>, <c>NSR02</c>
    ///     or <c>NSR03</c> for UDF. A file that ends early or was written by something else has none.
    /// </remarks>
    /// <param name="path">The file to check.</param>
    /// <param name="minimumBytes">Size of the largest file the image must contain; the image cannot be smaller.</param>
    /// <param name="displayPath">The name to use in messages when the file is checked under a temporary name.</param>
    /// <exception cref="PlainstallException">The file does not pass.</exception>
    public static void VerifyIsoFile(string path, long minimumBytes = 0, string? displayPath = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        displayPath ??= path;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var length = stream.Length;
            const int firstSector = 16, lastSector = 31;

            if (length < (lastSector + 1) * (long)SectorSize || length % SectorSize != 0)
                throw NotAnIso(displayPath, $"its size of {length:N0} bytes is not that of a disc image");
            if (length < minimumBytes)
                throw NotAnIso(displayPath,
                    $"it is only {length:N0} bytes long, less than the {minimumBytes:N0} bytes of the largest file it should contain");

            var area = new byte[(lastSector - firstSector + 1) * SectorSize];
            stream.Position = firstSector * (long)SectorSize;
            stream.ReadExactly(area);
            for (var sector = 0; sector <= lastSector - firstSector; sector++)
            {
                var identifier = area.AsSpan(sector * SectorSize + 1, 5);
                if (identifier.SequenceEqual("CD001"u8) || identifier.SequenceEqual("BEA01"u8) ||
                    identifier.SequenceEqual("NSR02"u8) || identifier.SequenceEqual("NSR03"u8))
                    return;
            }

            throw NotAnIso(displayPath, "it contains no ISO or UDF file system");
        }
        catch (FileNotFoundException ex)
        {
            throw new PlainstallException(ErrorKind.Verification,
                "The ISO tool oscdimg reported success but wrote no file. Check the log for details. Then try again.",
                ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PlainstallException(ErrorKind.Verification,
                $"The ISO file '{displayPath}' could not be read back for checking: {ex.Message}", ex);
        }
    }

    private static PlainstallException NotAnIso(string path, string reason)
    {
        return new PlainstallException(ErrorKind.Verification,
            $"The ISO file '{path}' did not pass its check: {reason}. An incomplete ISO is not kept. " +
            "Check that the target disk has enough free space and no errors. Then try again.");
    }

    /// <summary>The percentage from an oscdimg progress line such as "37% complete", else null.</summary>
    internal static int? TryParsePercent(string line)
    {
        var match = PercentPattern().Match(line);
        return match.Success &&
               int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture,
                   out var percent) && percent <= 100
            ? percent
            : null;
    }

    [GeneratedRegex(@"^\s*(\d{1,3})% complete", RegexOptions.CultureInvariant)]
    private static partial Regex PercentPattern();

    private static (long Total, long Largest) MeasureMedia(string mediaRoot, CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.None };
        long total = 0, largest = 0;
        try
        {
            foreach (var file in new DirectoryInfo(mediaRoot).EnumerateFiles("*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                total += file.Length;
                largest = Math.Max(largest, file.Length);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PlainstallException(ErrorKind.Output,
                $"Could not read the media folder '{mediaRoot}': {ex.Message}", ex);
        }

        return (total, largest);
    }

    private static bool IsInside(string path, string folder)
    {
        var prefix = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string ToGigabytes(long bytes)
    {
        return (bytes / (1024d * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture);
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not remove the unfinished file {Path}: {Message}", path, ex.Message);
        }
    }

    /// <summary>Free bytes available to the current user in a folder. Works for UNC paths and mounted folders too.</summary>
    private static long GetFreeBytes(string folder)
    {
        if (GetDiskFreeSpaceEx(folder, out var freeBytesAvailable, out _, out _))
            return freeBytesAvailable > long.MaxValue ? long.MaxValue : (long)freeBytesAvailable;
        var error = Marshal.GetLastPInvokeError();
        throw new PlainstallException(ErrorKind.Preflight,
            $"Could not find out how much space is free in '{folder}' (error {error}). Choose another place for the ISO.");
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailable,
        out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);
}