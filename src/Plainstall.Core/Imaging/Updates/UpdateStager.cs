using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Plainstall.Core.Imaging.Updates;

/// <summary>One update copied into the work folder, ready for DISM.</summary>
/// <param name="Number">1, 2, 3 ... in install order; also the name of its folder (001, 002 ...).</param>
/// <param name="Position">Where the user listed it, counting from 1.</param>
/// <param name="AlsoListedAs">Other profile entries with exactly the same content, which are not staged again.</param>
public sealed record StagedUpdate(
    int Number,
    int Position,
    string SourcePath,
    string StagedPath,
    long SizeBytes,
    string Sha256,
    IReadOnlyList<string> AlsoListedAs)
{
    public string FileName => Path.GetFileName(StagedPath);
}

/// <summary>The staged updates in install order, and notes for the build report.</summary>
public sealed record UpdateStagingResult(IReadOnlyList<StagedUpdate> Updates, IReadOnlyList<string> Notes);

/// <summary>Copies one file. Reports the number of bytes copied so far. Replaceable so tests can simulate a bad copy.</summary>
public delegate Task UpdateFileCopy(string source, string destination, IProgress<long> bytesCopied,
    CancellationToken cancellationToken);

/// <summary>
///     Checks the updates a profile lists and copies each into its own numbered folder
///     (<c>001\name.msu</c>, <c>002\name.cab</c> ...) under a staging root. One folder per package, because
///     DISM looks next to a package for related files and must not pick up a neighbour the user did not list.
///     Each copy is verified against the original by SHA-256, so what DISM reads is what the user chose,
///     even when the original sits on a network share or a USB stick that is pulled out later.
///     <para>
///         What this does NOT do, so nobody assumes more: it downloads nothing; it does not resolve or check
///         dependencies between updates; it does not sort servicing stack updates before cumulative updates or
///         checkpoint updates before the update that needs them (the user's order is the install order, exactly);
///         and the updates are meant for the install image only, not for boot.wim and not for WinRE. Only
///         <c>.msu</c> and <c>.cab</c> files are accepted; <c>.psf</c> companions and unpacked folders are not.
///     </para>
/// </summary>
public sealed partial class UpdateStager
{
    /// <summary>Smaller files cannot be Windows updates: they are unfinished downloads or saved web pages.</summary>
    public const long MinimumBytes = 4 * 1024;

    /// <summary>Larger than any Windows update package Microsoft publishes, with a wide margin.</summary>
    public const long MaximumBytes = 16L << 30;

    /// <summary>Plainstall builds x64 images only.</summary>
    private const string ImageArchitecture = "x64";

    public static readonly IReadOnlyList<string> AllowedExtensions = [".msu", ".cab"];

    private readonly UpdateFileCopy _copy;
    private readonly ILogger _logger;

    public UpdateStager(ILogger? logger = null, UpdateFileCopy? copy = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _copy = copy ?? CopyFileAsync;
    }

    [GeneratedRegex("(?<![a-z0-9])(?<arch>x64|amd64|arm64|x86)(?![a-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArchitectureToken();

    /// <summary>
    ///     The problems with the listed files, in plain words, without copying anything. Empty when all are
    ///     usable. Cheap enough for the preflight step and for a profile editor.
    /// </summary>
    public static IReadOnlyList<string> Check(IReadOnlyList<string> packages)
    {
        var problems = new List<string>();
        for (var i = 0; i < packages.Count; i++)
        {
            var path = packages[i];
            var label = $"Update {i + 1}";
            if (string.IsNullOrWhiteSpace(path))
            {
                problems.Add($"{label} in the profile is empty.");
                continue;
            }

            var extension = Path.GetExtension(path);
            if (!AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add(
                    $"{label} ('{path}') is not a Windows update package. Only .msu and .cab files can be added.");
                continue;
            }

            if (Directory.Exists(path))
            {
                problems.Add($"{label} ('{path}') is a folder. Give the .msu or .cab file itself.");
                continue;
            }

            if (!File.Exists(path))
            {
                problems.Add(
                    $"{label} ('{path}') was not found. Check the path, or download the update again from the Microsoft Update Catalog.");
                continue;
            }

            try
            {
                var length = new FileInfo(path).Length;
                switch (length)
                {
                    case < MinimumBytes:
                        problems.Add(
                            $"{label} ('{path}') is only {length} bytes, too small to be a Windows update. It is probably an unfinished download or a saved web page.");
                        continue;
                    case > MaximumBytes:
                        problems.Add(
                            $"{label} ('{path}') is larger than {MaximumBytes >> 30} GB, which no Windows update is. Check that this is the right file.");
                        continue;
                }

                if (ReadHeader(path) is { } header && HeaderProblem(header, extension) is { } problem)
                    problems.Add($"{label} ('{path}') {problem}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{label} ('{path}') could not be read: {ex.Message}");
            }
        }

        return problems;
    }

    /// <summary>
    ///     Checks every file first and throws one error listing all problems, before anything is copied. Then
    ///     copies in the user's order. Files with the same content are staged once, at the first position.
    ///     A file whose name marks it for another processor architecture than the image (for example
    ///     <c>-arm64_</c> for an x64 image) is not staged and gets a note: it cannot be installed there.
    /// </summary>
    /// <param name="packages">The update files from the profile, in the order they are to be installed.</param>
    /// <param name="stagingRoot">Must not exist or be empty. Nothing outside it is written.</param>
    /// <param name="progress">
    ///     Bytes read and written. The total starts at three passes over every file (hash, copy, verify)
    ///     and shrinks when a duplicate or a wrong-architecture file is found, so the percentage only grows.
    /// </param>
    /// <param name="cancellationToken">Stops the staging between files and during a hash or a copy.</param>
    public async Task<UpdateStagingResult> StageAsync(IReadOnlyList<string> packages, string stagingRoot,
        IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var problems = Check(packages);
        if (problems.Count > 0)
            throw new PlainstallException(ErrorKind.InvalidInput,
                "Some of the updates in the profile cannot be used:" + Environment.NewLine +
                string.Join(Environment.NewLine, problems.Select(p => "  - " + p)));
        var root = Path.GetFullPath(stagingRoot);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new PlainstallException(ErrorKind.Internal, $"The update staging folder '{root}' is not empty.");
        Directory.CreateDirectory(root);

        var sources = packages.Select(p => new FileInfo(Path.GetFullPath(p))).ToList();
        var meter = new StagingProgress(progress, sources.Sum(f => f.Length) * 3);

        var notes = new List<string>();
        var staged = new List<(StagedUpdate Update, List<string> Duplicates)>();
        var byHash = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < sources.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = sources[i];
            var position = i + 1;

            if (MadeFor(source.Name) is { } madeFor &&
                !madeFor.Equals(ImageArchitecture, StringComparison.OrdinalIgnoreCase))
            {
                meter.LeaveOut(source.Length * 3);
                var note =
                    $"Update {position} ('{source.Name}') was left out: it is made for {madeFor}, and this Windows image is {ImageArchitecture}.";
                notes.Add(note);
                _logger.LogWarning("{Note}", note);
                continue;
            }

            var start = meter.Done;
            var hash = await HashHex.Sha256OfFileAsync(source.FullName, meter.HashPass(start, null), cancellationToken)
                .ConfigureAwait(false);
            meter.MoveTo(start + source.Length);

            if (byHash.TryGetValue(hash, out var index))
            {
                meter.LeaveOut(source.Length * 2);
                var first = staged[index].Update;
                staged[index].Duplicates.Add(source.FullName);
                var note =
                    $"Update {position} ('{source.FullName}') has the same content as update {first.Position} ('{first.SourcePath}'). It is added once, at position {first.Position}.";
                notes.Add(note);
                if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("{Note}", note);
                continue;
            }

            var number = staged.Count + 1;
            var folder = Path.Combine(root, number.ToString("000", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);
            var destination = Path.Combine(folder, source.Name);

            start = meter.Done;
            await _copy(source.FullName, destination, meter.CopyPass(start, source.Length), cancellationToken)
                .ConfigureAwait(false);
            meter.MoveTo(start + source.Length);
            // Files copied from read-only media keep the read-only flag, and DISM may need to open them for writing.
            File.SetAttributes(destination, FileAttributes.Normal);

            start = meter.Done;
            var copyHash = await HashHex
                .Sha256OfFileAsync(destination, meter.HashPass(start, source.Length), cancellationToken)
                .ConfigureAwait(false);
            meter.MoveTo(start + source.Length);
            if (!HashHex.Equal(hash, copyHash))
            {
                TryDelete(destination);
                throw new PlainstallException(ErrorKind.Verification,
                    $"The copy of '{source.Name}' in the work folder is not the same as the original (SHA-256 {hash} expected, {copyHash} found). "
                    + "The disk may have a problem, or the file changed while it was copied. Start the build again. If this happens again, copy the update to another drive first.");
            }

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Staged update {Number}: {Source} ({Bytes} bytes, SHA-256 {Hash})", number,
                    source.FullName, source.Length, hash);
            byHash[hash] = staged.Count;
            staged.Add((new StagedUpdate(number, position, source.FullName, destination, source.Length, hash, []), []));
        }

        return new UpdateStagingResult([.. staged.Select(s => s.Update with { AlsoListedAs = [.. s.Duplicates] })],
            notes);
    }

    /// <summary>
    ///     The architecture a Microsoft Update Catalog file name names, for example
    ///     <c>windows11.0-kb5043080-arm64_...msu</c>. Null when the name says nothing or names more than one.
    /// </summary>
    internal static string? MadeFor(string fileName)
    {
        var found = ArchitectureToken().Matches(Path.GetFileNameWithoutExtension(fileName))
            .Select(m =>
                m.Groups["arch"].Value.ToLowerInvariant() is "amd64"
                    ? "x64"
                    : m.Groups["arch"].Value.ToLowerInvariant())
            .Distinct()
            .ToList();
        return found.Count == 1 ? found[0] : null;
    }

    private static byte[]? ReadHeader(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[16];
        var read = stream.ReadAtLeast(buffer, buffer.Length, false);
        return read == 0 ? null : buffer[..read];
    }

    /// <summary>
    ///     A <c>.cab</c> always starts with <c>MSCF</c>. For <c>.msu</c> only an obvious web page is rejected:
    ///     Plainstall does not depend on the inner format of an MSU, which has changed between Windows versions.
    /// </summary>
    private static string? HeaderProblem(byte[] header, string extension)
    {
        var start = header.AsSpan();
        if (start.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) start = start[3..];
        var firstText = start.TrimStart(" \t\r\n"u8);
        if (firstText.Length > 0 && firstText[0] == (byte)'<')
            return
                "is a web page saved under an update's name, not an update. Download it again from the Microsoft Update Catalog.";
        if (extension.Equals(".cab", StringComparison.OrdinalIgnoreCase) && !header.AsSpan().StartsWith("MSCF"u8))
            return "is not a valid .cab file. Download it again from the Microsoft Update Catalog.";
        return null;
    }

    private static async Task CopyFileAsync(string source, string destination, IProgress<long> bytesCopied,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1 << 20, FileOptions.Asynchronous);
        var buffer = new byte[1 << 20];
        long copied = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;
            bytesCopied.Report(copied);
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not delete the bad copy {Path}: {Message}", path, ex.Message);
        }
    }

    /// <summary>Bytes done out of a total that shrinks when a file is left out, reported after every change.</summary>
    private sealed class StagingProgress(IProgress<TransferProgress>? target, long total)
    {
        private long _total = total;

        public long Done { get; private set; }

        /// <summary>Takes bytes out of the total, for a file that is not copied after all.</summary>
        public void LeaveOut(long bytes)
        {
            _total -= bytes;
            Report();
        }

        public void MoveTo(long done)
        {
            Done = done;
            Report();
        }

        /// <summary>
        ///     Progress of one hash pass that started at <paramref name="start" />, capped at <paramref name="cap" /> bytes
        ///     when given.
        /// </summary>
        public InlineProgress<TransferProgress> HashPass(long start, long? cap)
        {
            return new InlineProgress<TransferProgress>(t =>
                MoveTo(start + (cap is { } limit ? Math.Min(t.BytesDone, limit) : t.BytesDone)));
        }

        /// <summary>Progress of one copy pass that started at <paramref name="start" />, capped at <paramref name="cap" /> bytes.</summary>
        public InlineProgress<long> CopyPass(long start, long cap)
        {
            return new InlineProgress<long>(b => MoveTo(start + Math.Min(b, cap)));
        }

        private void Report()
        {
            target?.Report(new TransferProgress(Done, _total));
        }
    }
}