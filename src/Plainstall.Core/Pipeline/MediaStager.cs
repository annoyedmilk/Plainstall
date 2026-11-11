using System.Text;

namespace Plainstall.Core.Pipeline;

/// <summary>Copies installation media into the work folder and prepares the edition files.</summary>
public static class MediaStager
{
    private static readonly string[] InstallImageNames = ["install.wim", "install.esd"];

    /// <summary>
    ///     Copies everything except the install image, which is exported separately as a single edition.
    ///     Files copied from an ISO arrive read-only. The attribute is cleared, because DISM cannot mount or
    ///     split a read-only file. Returns the number of bytes copied.
    /// </summary>
    public static async Task<long> CopyMediaAsync(string sourceRoot, string targetRoot,
        IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var source = new DirectoryInfo(sourceRoot);
        if (!source.Exists)
            throw new PlainstallException(ErrorKind.InvalidInput,
                $"The installation media folder '{sourceRoot}' does not exist.");

        var files = source.EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(f => !IsInstallImage(source, f))
            .ToList();
        var total = files.Sum(f => f.Length);
        long done = 0;
        var buffer = new byte[1 << 20];

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source.FullName, file.FullName);
            var target = Path.Combine(targetRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            await using (var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                             buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None,
                             buffer.Length, FileOptions.Asynchronous))
            {
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    done += read;
                    progress?.Report(new TransferProgress(done, total));
                }
            }

            File.SetAttributes(target, FileAttributes.Normal);
            File.SetLastWriteTimeUtc(target, file.LastWriteTimeUtc);

            if (new FileInfo(target).Length != file.Length)
                throw new PlainstallException(ErrorKind.Output, $"'{relative}' was not copied completely.");
        }

        // Empty folders matter too (the media has a few), so mirror the tree.
        foreach (var directory in source.EnumerateDirectories("*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(targetRoot,
                Path.GetRelativePath(source.FullName, directory.FullName)));
        return done;
    }

    private static bool IsInstallImage(DirectoryInfo root, FileInfo file)
    {
        return InstallImageNames.Contains(file.Name, StringComparer.OrdinalIgnoreCase)
               && string.Equals(Path.GetRelativePath(root.FullName, file.DirectoryName!), "sources",
                   StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Writes <c>sources\ei.cfg</c> naming the edition and removes <c>sources\PID.txt</c>, so that a product
    ///     key stored in a PC's firmware for another edition does not send Setup down the wrong path.
    ///     The channel of an existing <c>ei.cfg</c> is kept. Consumer media has none and gets Retail.
    /// </summary>
    public static void WriteEditionFiles(string mediaRoot, string editionId)
    {
        var sources = Path.Combine(mediaRoot, "sources");
        Directory.CreateDirectory(sources);
        var eiCfg = Path.Combine(sources, "ei.cfg");

        var channel = "Retail";
        var volumeLicense = "0";
        if (File.Exists(eiCfg))
        {
            var existing = ParseEiCfg(File.ReadAllLines(eiCfg));
            if (existing.TryGetValue("Channel", out var c) && c is "OEM" or "Retail" or "Volume") channel = c;
            if (existing.TryGetValue("VL", out var v) && v is "0" or "1") volumeLicense = v;
        }

        File.WriteAllText(eiCfg, $"[EditionID]\r\n{editionId}\r\n[Channel]\r\n{channel}\r\n[VL]\r\n{volumeLicense}\r\n",
            Encoding.ASCII);

        foreach (var name in new[] { "PID.txt", "pid.txt" })
        {
            var pid = Path.Combine(sources, name);
            if (!File.Exists(pid)) continue;
            File.SetAttributes(pid, FileAttributes.Normal);
            File.Delete(pid);
        }
    }

    internal static Dictionary<string, string> ParseEiCfg(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? section = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
            }
            else if (section is not null)
            {
                result[section] = line;
                section = null;
            }
        }

        return result;
    }
}