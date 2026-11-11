using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Plainstall.Core.Imaging;
using Plainstall.Core.Media;

namespace Plainstall.Core.Output.Usb;

/// <summary>
///     Lets the pipeline record that a stick is being written, so that after a crash or power cut the next
///     start can say "the stick in ... was not finished" instead of leaving the user to find out at install time.
/// </summary>
public interface IUsbJournal
{
    /// <summary>Called right before the first destructive step. If this throws, nothing is erased.</summary>
    void EraseStarting(DiskFacts disk);

    /// <summary>
    ///     Called once every file is written, checked and flushed. Not called when writing fails or is
    ///     stopped, so the record stays and keeps saying that the stick is not usable.
    /// </summary>
    void Finished(DiskFacts disk);
}

/// <summary>
///     Writing failed or was stopped after the stick had been erased. The message always says that the stick
///     is not usable and can simply be written again, so nobody tries to install Windows from it.
/// </summary>
public sealed class UsbStickNotUsableException(ErrorKind kind, string message, Exception? inner = null)
    : PlainstallException(kind, message, inner);

/// <summary>Settings for <see cref="UsbWriter" />. The defaults are right for real sticks.</summary>
public sealed record UsbWriterOptions
{
    /// <summary>How long to wait for Windows to show a new volume, and for a formatted volume to become usable.</summary>
    public TimeSpan VolumeTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The largest single file the stick can hold. Only tests change it, to exercise the split path with small files.</summary>
    internal long MaxFileBytes { get; init; } = UsbSizes.Fat32MaxFileBytes;

    /// <summary>Usable space for a disk of a given size. Only tests change it, to exercise "does not fit" without huge files.</summary>
    internal Func<long, long> UsableBytesOnDisk { get; init; } = UsbSizes.UsableBytesOnDisk;
}

/// <summary>
///     Erases a confirmed USB stick and writes installation media onto it: MBR, one active FAT32 partition
///     (at most 32 GB), the files, and <c>install.wim</c> split into <c>install.swm</c> parts when it is too
///     large for FAT32. This layout boots on both UEFI and legacy BIOS PCs.
/// </summary>
/// <remarks>
///     Everything that can be checked is checked before the first destructive call: the media, the file
///     sizes, whether the image can be split, the space, the disk's identity and the safety rules. After the
///     erase has started, any failure or cancel ends in <see cref="UsbStickNotUsableException" />.
/// </remarks>
public sealed partial class UsbWriter : IUsbWriter
{
    private static readonly string[] InstallImageNames = ["install.wim", "install.esd", "install.swm"];
    private static readonly string[] HashedFiles = [@"sources\boot.wim", "autounattend.xml"];

    private readonly IDiskOperations _disks;
    private readonly IUsbJournal _journal;
    private readonly ILogger _logger;
    private readonly UsbWriterOptions _options;
    private readonly IWimTool _wimTool;

    /// <param name="disks">The disk operations; <see cref="StorageDiskOperations" /> in the real program.</param>
    /// <param name="wimTool">Used only to split an <c>install.wim</c> larger than 4 GB.</param>
    /// <param name="journal">Told before the erase starts and after the stick is complete.</param>
    /// <param name="logger">Optional.</param>
    /// <param name="options">Optional; the defaults are right for real sticks.</param>
    public UsbWriter(IDiskOperations disks, IWimTool wimTool, IUsbJournal journal, ILogger? logger = null,
        UsbWriterOptions? options = null)
    {
        _disks = disks ?? throw new ArgumentNullException(nameof(disks));
        _wimTool = wimTool ?? throw new ArgumentNullException(nameof(wimTool));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _logger = logger ?? NullLogger.Instance;
        _options = options ?? new UsbWriterOptions();
    }

    public async Task<UsbWriteResult> WriteAsync(UsbWriteRequest request, IProgress<UsbWriteProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Report(progress, UsbWritePhase.Checking, 0, "Checking the stick and the installation files");

        var plan = await PlanAsync(request, cancellationToken).ConfigureAwait(false);
        Report(progress, UsbWritePhase.Checking, 100, "Checks passed");

        // The last point at which stopping leaves the stick exactly as it was.
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            _journal.EraseStarting(plan.Disk);
        }
        catch (Exception ex)
        {
            throw new PlainstallException(ErrorKind.Internal,
                $"Plainstall could not record that it is about to write the USB stick ({ex.Message}). Nothing was erased.",
                ex);
        }

        return await WriteConfirmedAsync(plan, progress, cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- checks before the erase

    private async Task<WritePlan> PlanAsync(UsbWriteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request.ConfirmedDisk);
        ArgumentNullException.ThrowIfNull(request.Context);
        var confirmed = request.ConfirmedDisk;

        var media = ScanMedia(request.MediaRoot);
        cancellationToken.ThrowIfCancellationRequested();

        var imageToSplit = CheckFileSizes(media);
        cancellationToken.ThrowIfCancellationRequested();

        // The space check uses the confirmed size; the identity check below makes sure the disk still has it.
        var partitionSize = UsbSizes.PartitionSizeFor(confirmed.SizeBytes);
        var cluster = UsbSizes.Fat32ClusterBytes(UsbSizes.PlannedPartitionBytes(confirmed.SizeBytes));
        var required = UsbSizes.RequiredFat32Bytes([.. media.Files.Select(f => (f.RelativePath, f.Length))],
            media.Directories.Count, cluster, _options.MaxFileBytes);
        var usable = _options.UsableBytesOnDisk(confirmed.SizeBytes);
        if (required > usable)
            throw Refused(
                $"The installation files need {UsbSizes.Format(required)} on the stick, but {Describe(confirmed)} has room for only {UsbSizes.Format(usable)}. Use a larger stick.");

        cancellationToken.ThrowIfCancellationRequested();
        var fresh = await _disks.GetDiskAsync(confirmed.Number, cancellationToken).ConfigureAwait(false);
        if (fresh is null)
            throw Refused(
                $"The stick you chose ({Describe(confirmed)}) is no longer connected. Plug it in and choose it again.");

        var differences = UsbDiskRules.Differences(confirmed, fresh);
        if (differences.Count > 0)
        {
            _logger.LogWarning("Disk {Number} changed since it was confirmed: {Differences}", confirmed.Number,
                string.Join("; ", differences));
            throw Refused(
                $"Disk {confirmed.Number} is no longer the stick you confirmed: {string.Join("; ", differences)}. " +
                "Perhaps the stick was unplugged or swapped. Choose the stick again.");
        }

        // The media folder itself must not be on the stick either, whatever the caller put into the context.
        var context = request.Context with
        {
            PathsInUse = [.. request.Context.PathsInUse, .. UsbPaths.RootsOf(media.Root)]
        };
        var verdict = UsbDiskRules.Evaluate(fresh, context);
        if (!verdict.IsEligible)
            throw Refused($"Plainstall will not erase {Describe(fresh)}. {string.Join(" ", verdict.Reasons)}");

        cancellationToken.ThrowIfCancellationRequested();
        var label = Fat32Label.Sanitize(request.VolumeLabel);
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "USB checks passed for disk {Number} ({Name}, {Size}); partition {Partition}, label {Label}, split {Split}",
                fresh.Number, fresh.FriendlyName, UsbSizes.Format(fresh.SizeBytes),
                partitionSize is null ? "whole disk" : UsbSizes.Format(partitionSize.Value), label,
                imageToSplit is not null);

        return new WritePlan(
            fresh,
            DiskIdentity.Of(fresh),
            partitionSize,
            label,
            media,
            [
                .. media.Files.Where(f => !ReferenceEquals(f, imageToSplit)).OrderBy(f => f.Length)
                    .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
            ],
            imageToSplit);
    }

    private static MediaScan ScanMedia(string mediaRoot)
    {
        if (string.IsNullOrWhiteSpace(mediaRoot))
            throw new PlainstallException(ErrorKind.InvalidInput,
                "No folder with installation files was given. Nothing was erased.");
        string root;
        try
        {
            root = Path.GetFullPath(mediaRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new PlainstallException(ErrorKind.InvalidInput,
                $"'{mediaRoot}' is not a valid folder name. Nothing was erased.", ex);
        }

        if (!Directory.Exists(root))
            throw new PlainstallException(ErrorKind.InvalidInput,
                $"The installation files were not found: the folder '{root}' does not exist. Nothing was erased.");

        List<MediaFile> files;
        List<string> directories;
        try
        {
            var info = new DirectoryInfo(root);
            files =
            [
                .. info.EnumerateFiles("*", SearchOption.AllDirectories)
                    .Select(f => new MediaFile(Path.GetRelativePath(root, f.FullName), f.FullName, f.Length))
            ];
            directories =
            [
                .. info.EnumerateDirectories("*", SearchOption.AllDirectories)
                    .Select(d => Path.GetRelativePath(root, d.FullName))
                    .OrderBy(d => d.Length)
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PlainstallException(ErrorKind.InvalidInput,
                $"Plainstall could not read the installation files in '{root}': {ex.Message} Nothing was erased.", ex);
        }

        var missing = new List<string>();
        if (!files.Any(f => Is(f, @"sources\boot.wim"))) missing.Add(@"sources\boot.wim (Windows Setup)");
        if (!files.Any(f => InstallImageNames.Any(n => Is(f, @"sources\" + n))))
            missing.Add(@"sources\install.wim (the Windows image)");
        if (!files.Any(IsUefiBootFile)) missing.Add(@"efi\boot\bootx64.efi (what a UEFI PC starts)");
        if (missing.Count > 0)
            throw new PlainstallException(ErrorKind.InvalidInput,
                $"The folder '{root}' does not look like Windows installation media. Missing: {string.Join(", ", missing)}. Nothing was erased.");
        return new MediaScan(root, files, directories);
    }

    /// <summary>
    ///     Refuses files FAT32 cannot hold, before anything is erased. Only <c>sources\install.wim</c> may be too
    ///     large, and only when it is a normal WIM: DISM cannot split ESD-compressed images. Returns the image
    ///     that has to be split, if any.
    /// </summary>
    private MediaFile? CheckFileSizes(MediaScan media)
    {
        MediaFile? imageToSplit = null;
        var tooLarge = new List<MediaFile>();
        foreach (var file in media.Files.Where(f => f.Length > _options.MaxFileBytes))
        {
            if (!UsbSizes.IsInstallWim(file.RelativePath))
            {
                tooLarge.Add(file);
                continue;
            }

            WimInfo info;
            try
            {
                info = WimMetadataReader.Read(file.FullPath);
            }
            catch (PlainstallException ex)
            {
                throw new PlainstallException(ex.Kind, ex.Message + " Nothing was erased.", ex);
            }

            if (!info.CanBeSplit)
            {
                var why = info.Compression == WimCompression.Lzms
                    ? "it is compressed in the ESD format, which cannot be split"
                    : "it is already one part of a split image";
                throw new PlainstallException(ErrorKind.InvalidInput,
                    $@"sources\install.wim is {UsbSizes.Format(file.Length)}, more than the 4 GB a single file can have on the stick, " +
                    $"and it cannot be split into smaller parts because {why}. Build the media again with Plainstall. " +
                    "Plainstall always writes a normal image that can be split. Nothing was erased.");
            }

            imageToSplit = file;
        }

        if (tooLarge.Count == 0) return imageToSplit;
        var list = string.Join(", ", tooLarge.Select(f => $"{f.RelativePath} ({UsbSizes.Format(f.Length)})"));
        var esdHint = tooLarge.Any(f => Is(f, @"sources\install.esd"))
            ? " Build the media with Plainstall first. Plainstall changes an install.esd into a normal install.wim."
            : "";
        throw new PlainstallException(ErrorKind.InvalidInput,
            $"A USB stick that starts both BIOS and UEFI PCs uses FAT32, where a single file can be at most 4 GB. " +
            $"These files are larger and cannot be split: {list}.{esdHint} Nothing was erased.");
    }

    // ---------------------------------------------------------------- after the erase has started

    private async Task<UsbWriteResult> WriteConfirmedAsync(WritePlan plan, IProgress<UsbWriteProgress>? progress,
        CancellationToken cancellationToken)
    {
        var id = plan.Identity;
        var step = "Erasing the USB stick";
        try
        {
            // The disk steps take seconds and are not interrupted: stopping between them gains nothing,
            // the stick is unusable either way, and a half-done format only confuses Windows.
            if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("Erasing {Disk}", id);
            Report(progress, UsbWritePhase.Erasing, null, $"Erasing {plan.Disk.FriendlyName}");
            await _disks.ClearAsync(id, CancellationToken.None).ConfigureAwait(false);
            await _disks.InitializeMbrAsync(id, CancellationToken.None).ConfigureAwait(false);

            step = "Formatting the USB stick";
            Report(progress, UsbWritePhase.Formatting, null, $"Formatting as FAT32 with the label {plan.Label}");
            var partition = await _disks.CreateActivePartitionAsync(id, plan.PartitionSize, CancellationToken.None)
                .ConfigureAwait(false);
            var volume = await _disks.WaitForVolumeAsync(id, partition, _options.VolumeTimeout, CancellationToken.None)
                .ConfigureAwait(false);
            await _disks.FormatFat32Async(id, volume, plan.Label, _options.VolumeTimeout, CancellationToken.None)
                .ConfigureAwait(false);
            volume = await _disks.EnsureDriveLetterAsync(id, volume, CancellationToken.None).ConfigureAwait(false);
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("New volume {Volume} ({Letter})", volume.VolumePath,
                    volume.DriveRoot ?? "no drive letter");

            step = "Copying the installation files to the USB stick";
            var written = await CopyAsync(plan, volume, progress, cancellationToken).ConfigureAwait(false);

            if (plan.ImageToSplit is { } image)
            {
                step = "Writing the Windows image to the USB stick in parts";
                cancellationToken.ThrowIfCancellationRequested();
                written += await SplitAsync(plan, image, volume, progress, cancellationToken).ConfigureAwait(false);
            }

            step = "Checking the files on the USB stick";
            cancellationToken.ThrowIfCancellationRequested();
            await VerifyAsync(plan, volume, progress, cancellationToken).ConfigureAwait(false);

            step = "Finishing the USB stick";
            Report(progress, UsbWritePhase.Finishing, null, "Making sure everything is on the stick");
            await _disks.FlushAsync(id, volume, CancellationToken.None).ConfigureAwait(false);

            var warnings = new List<string>();
            try
            {
                _journal.Finished(plan.Disk);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The journal could not record that the stick is complete");
            }

            var ejected = false;
            try
            {
                ejected = await _disks.EjectAsync(id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "Safe removal of {Disk} failed", id);
            }

            if (!ejected)
                warnings.Add(
                    "The stick is ready. Windows did not let Plainstall remove the stick safely. Possibly a window or a program still uses it. " +
                    "Use \"Safely Remove Hardware\" in the taskbar before you disconnect the stick.");

            Report(progress, UsbWritePhase.Finishing, 100,
                ejected ? "The stick is ready and can be unplugged" : "The stick is ready");
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("USB stick written: {Bytes} bytes, split {Split}, ejected {Ejected}", written,
                    plan.ImageToSplit is not null, ejected);
            return new UsbWriteResult
            {
                Disk = plan.Disk,
                VolumeRoot = volume.DriveRoot ?? volume.VolumePath,
                BytesWritten = written,
                ImageWasSplit = plan.ImageToSplit is not null,
                WasEjected = ejected,
                Warnings = warnings
            };
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning("Writing {Disk} was stopped during: {Step}", id, step);
            throw new UsbStickNotUsableException(ErrorKind.Cancelled,
                "Writing the USB stick was stopped before it was finished. The stick is incomplete and cannot be used to install Windows. " +
                "You can simply write it again.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Step} failed for {Disk}", step, id);
            var kind = ex is PlainstallException { Kind: not ErrorKind.Cancelled } pe ? pe.Kind : ErrorKind.Output;
            throw new UsbStickNotUsableException(kind,
                $"{step} failed: {AsSentence(ex.Message)} The USB stick is not usable now, because it is only partly written. You can simply write it again.",
                ex);
        }
    }

    private static async Task<long> CopyAsync(WritePlan plan, UsbVolume volume, IProgress<UsbWriteProgress>? progress,
        CancellationToken cancellationToken)
    {
        var root = volume.VolumePath;
        var total = plan.FilesToCopy.Sum(f => f.Length);
        long done = 0;
        var lastPercent = -1;
        Report(progress, UsbWritePhase.Copying, 0, "Copying files");

        // Empty folders are part of the media too.
        foreach (var directory in plan.Media.Directories) Directory.CreateDirectory(Path.Combine(root, directory));

        var buffer = new byte[4 * 1024 * 1024];
        foreach (var file in plan.FilesToCopy)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(root, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            await using (var input = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(target, new FileStreamOptions
                         {
                             // CreateNew: the volume was formatted a moment ago, so an existing file means something is wrong.
                             Mode = FileMode.CreateNew,
                             Access = FileAccess.Write,
                             Share = FileShare.None,
                             BufferSize = 1,
                             Options = FileOptions.Asynchronous,
                             // Reserving the space up front fails early and cleanly when the stick is full.
                             PreallocationSize = file.Length
                         }))
            {
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    done += read;
                    var percent = total == 0 ? 100 : (int)(done * 100 / total);
                    if (percent == lastPercent) continue;
                    lastPercent = percent;
                    Report(progress, UsbWritePhase.Copying, percent, file.RelativePath);
                }
            }

            // Files that came from an ISO are read-only; copies that keep the attribute get in the way of
            // anyone who later edits the stick (an answer file, a driver folder).
            var attributes = File.GetAttributes(target);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(target, attributes & ~FileAttributes.ReadOnly);
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file.FullPath));
        }

        Report(progress, UsbWritePhase.Copying, 100, "All files copied");
        return done;
    }

    private async Task<long> SplitAsync(WritePlan plan, MediaFile image, UsbVolume volume,
        IProgress<UsbWriteProgress>? progress, CancellationToken cancellationToken)
    {
        // DISM refuses to split a read-only file, and files that came from an ISO are read-only.
        var attributes = File.GetAttributes(image.FullPath);
        if ((attributes & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(image.FullPath, attributes & ~FileAttributes.ReadOnly);

        // dism.exe gets the drive letter when Windows assigned one, because volume names are not documented
        // as accepted by /SWMFile. The letter is read again right here, so it is the one this volume has now.
        var current = await _disks.EnsureDriveLetterAsync(plan.Identity, volume, CancellationToken.None)
            .ConfigureAwait(false);
        var targetRoot = current.DriveRoot ?? current.VolumePath;
        Directory.CreateDirectory(Path.Combine(volume.VolumePath, "sources"));
        var swm = Path.Combine(targetRoot, "sources", "install.swm");

        Report(progress, UsbWritePhase.SplittingImage, 0,
            $"Writing install.wim ({UsbSizes.Format(image.Length)}) as parts of {UsbSizes.SplitPartSizeMb} MB");
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Splitting {Image} into {Target}", image.FullPath, swm);
        var splitProgress = new InlineProgress<int>(p => Report(progress, UsbWritePhase.SplittingImage,
            Math.Clamp(p, 0, 100), "Writing the Windows image in parts"));
        await _wimTool.SplitImageAsync(image.FullPath, swm, UsbSizes.SplitPartSizeMb, splitProgress, cancellationToken)
            .ConfigureAwait(false);

        return SplitParts(volume).Sum(p => new FileInfo(p).Length);
    }

    private async Task VerifyAsync(WritePlan plan, UsbVolume volume, IProgress<UsbWriteProgress>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, UsbWritePhase.Verifying, 0, "Checking the files on the stick");
        var problems = new List<string>();
        var root = volume.VolumePath;

        problems.AddRange(plan.Media.Directories
            .Where(directory => !Directory.Exists(Path.Combine(root, directory)))
            .Select(directory => $"the folder {directory} is missing"));

        var count = plan.FilesToCopy.Count;
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = plan.FilesToCopy[i];
            var copy = new FileInfo(Path.Combine(root, file.RelativePath));
            if (!copy.Exists)
                problems.Add($"{file.RelativePath} is missing");
            else if (copy.Length != file.Length)
                problems.Add($"{file.RelativePath} has {copy.Length:N0} bytes instead of {file.Length:N0}");
            if (i % 50 == 49) Report(progress, UsbWritePhase.Verifying, (i + 1) * 50 / count, "Checking file sizes");
        }

        if (plan.ImageToSplit is { } image) VerifySplit(image, volume, problems);

        Report(progress, UsbWritePhase.Verifying, 50, "Comparing checksums");
        foreach (var name in HashedFiles)
        {
            var file = plan.FilesToCopy.FirstOrDefault(f => Is(f, name));
            var copy = file is null ? null : Path.Combine(root, file.RelativePath);
            if (file is null || copy is null || !File.Exists(copy) ||
                new FileInfo(copy).Length != file.Length) continue; // Absent from the media, or already reported above.
            var expected = await HashHex.Sha256OfFileAsync(file.FullPath, null, cancellationToken)
                .ConfigureAwait(false);
            var actual = await Task
                .Run(() => UsbVerification.Sha256Unbuffered(copy, cancellationToken, out _), cancellationToken)
                .ConfigureAwait(false);
            if (!HashHex.Equal(expected, actual))
                problems.Add($"{file.RelativePath} on the stick is different from the original");
        }

        if (problems.Count > 0)
        {
            var shown = string.Join("; ", problems.Take(5)) +
                        (problems.Count > 5 ? $"; and {problems.Count - 5} more" : "");
            throw new PlainstallException(ErrorKind.Verification,
                $"The stick does not hold exactly what was written ({shown}). The stick may be faulty or was unplugged");
        }

        Report(progress, UsbWritePhase.Verifying, 100, "All files checked");
    }

    /// <summary>
    ///     A split image is checked by its parts: <c>install.swm</c> must exist, the part headers must number
    ///     1 to N with N parts present, and the parts together must be about as large as the original. They
    ///     are not compared byte for byte, because DISM writes them in its own layout.
    /// </summary>
    private void VerifySplit(MediaFile image, UsbVolume volume, List<string> problems)
    {
        var first = Path.Combine(volume.VolumePath, "sources", "install.swm");
        if (!File.Exists(first))
        {
            problems.Add(@"sources\install.swm (the Windows image) is missing");
            return;
        }

        var parts = SplitParts(volume);
        var numbers = new HashSet<int>();
        int? total = null;
        foreach (var part in parts)
        {
            var header = UsbVerification.ReadSplitHeader(part);
            if (header is not { } h)
            {
                problems.Add($@"sources\{Path.GetFileName(part)} is not a part of a Windows image");
                continue;
            }

            numbers.Add(h.PartNumber);
            if (total is null)
                total = h.TotalParts;
            else if (total != h.TotalParts) problems.Add("the parts of the Windows image do not belong together");
            if (new FileInfo(part).Length > _options.MaxFileBytes)
                problems.Add($@"sources\{Path.GetFileName(part)} is larger than FAT32 allows");
        }

        if (total is { } n && (numbers.Count != n || parts.Count != n ||
                               Enumerable.Range(1, n).Any(i => !numbers.Contains(i))))
            problems.Add($"the Windows image should have {n} parts on the stick, but {parts.Count} were found");

        var sum = parts.Sum(p => new FileInfo(p).Length);
        var lower = image.Length / 4 * 3;
        var upper = image.Length + (parts.Count + 1) * UsbSizes.SplitPartOverheadBytes;
        if (sum < lower || sum > upper)
            problems.Add(
                $"the parts of the Windows image add up to {UsbSizes.Format(sum)}, but the image is {UsbSizes.Format(image.Length)}");
    }

    private static List<string> SplitParts(UsbVolume volume)
    {
        var sources = Path.Combine(volume.VolumePath, "sources");
        if (!Directory.Exists(sources)) return [];
        return
            [.. Directory.EnumerateFiles(sources, "install*.swm").Where(p => SwmName().IsMatch(Path.GetFileName(p)))];
    }

    [GeneratedRegex(@"^install\d*\.swm$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SwmName();

    // ---------------------------------------------------------------- helpers

    private static bool Is(MediaFile file, string relativePath)
    {
        return string.Equals(file.RelativePath.Replace('/', '\\'), relativePath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUefiBootFile(MediaFile file)
    {
        var relative = file.RelativePath.Replace('/', '\\');
        return string.Equals(Path.GetDirectoryName(relative), @"efi\boot", StringComparison.OrdinalIgnoreCase)
               && Path.GetFileName(relative).StartsWith("boot", StringComparison.OrdinalIgnoreCase)
               && relative.EndsWith(".efi", StringComparison.OrdinalIgnoreCase);
    }

    private static string Describe(DiskFacts disk)
    {
        return $"disk {disk.Number}, {disk.FriendlyName}, {UsbSizes.Format(disk.SizeBytes)}";
    }

    private static PlainstallException Refused(string message)
    {
        return new PlainstallException(ErrorKind.Output, message + " Nothing was erased.");
    }

    private static string AsSentence(string message)
    {
        var text = message.Trim();
        return text.Length == 0 ? "an unknown error occurred." : text.EndsWith('.') ? text : text + ".";
    }

    private static void Report(IProgress<UsbWriteProgress>? progress, UsbWritePhase phase, int? percent, string detail)
    {
        progress?.Report(new UsbWriteProgress(phase, percent, detail));
    }

    private sealed record MediaFile(string RelativePath, string FullPath, long Length);

    private sealed record MediaScan(string Root, IReadOnlyList<MediaFile> Files, IReadOnlyList<string> Directories);

    private sealed record WritePlan(
        DiskFacts Disk,
        DiskIdentity Identity,
        long? PartitionSize,
        string Label,
        MediaScan Media,
        IReadOnlyList<MediaFile> FilesToCopy,
        MediaFile? ImageToSplit);
}