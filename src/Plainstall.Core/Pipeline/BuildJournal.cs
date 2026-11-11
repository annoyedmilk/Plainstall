using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Plainstall.Core.Imaging;
using Plainstall.Core.Output.Usb;
using Plainstall.Core.Registry;

namespace Plainstall.Core.Pipeline;

/// <summary>Folders and files inside one work folder. Everything a build writes lives below <see cref="Root" />.</summary>
public sealed class WorkLayout
{
    public const string MarkerFileName = ".plainstall-work";
    public const string JournalFileName = "journal.json";

    public WorkLayout(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    /// <summary>A copy of the installation media, later the content of the stick or ISO.</summary>
    public string Media => Path.Combine(Root, "media");

    public string MediaSources => Path.Combine(Media, "sources");

    /// <summary>The single edition being serviced.</summary>
    public string WorkingWim => Path.Combine(Root, "install-work.wim");

    public string InstallMount => Path.Combine(Root, "mount-install");
    public string BootMount => Path.Combine(Root, "mount-boot");
    public string BootWork => Path.Combine(Root, "boot-work.wim");
    public string Scratch => Path.Combine(Root, "scratch");
    public string Logs => Path.Combine(Root, "logs");
    public string DismLog => Path.Combine(Logs, "dism.log");
    public string Updates => Path.Combine(Root, "updates");
    public string Drivers => Path.Combine(Root, "drivers");
    public string Journal => Path.Combine(Root, JournalFileName);
    public string Marker => Path.Combine(Root, MarkerFileName);

    /// <summary>True when the folder was created by Plainstall. Only such folders are ever deleted.</summary>
    public bool IsOurs => File.Exists(Marker);

    /// <summary>
    ///     Deletes the work folder. The marker file goes last, so a deletion that stops half-way because a file is
    ///     still in use leaves a folder that is still recognised as Plainstall's and can be cleaned up later.
    /// </summary>
    public void Delete()
    {
        foreach (var entry in new DirectoryInfo(Root).EnumerateFileSystemInfos())
        {
            if (string.Equals(entry.FullName, Path.GetFullPath(Marker), StringComparison.OrdinalIgnoreCase)) continue;
            if (entry is DirectoryInfo directory)
            {
                directory.Delete(true);
            }
            else
            {
                entry.Attributes = FileAttributes.Normal;
                entry.Delete();
            }
        }

        File.Delete(Marker);
        Directory.Delete(Root);
    }

    public void Create()
    {
        foreach (var folder in new[] { Root, Media, InstallMount, BootMount, Scratch, Logs })
            Directory.CreateDirectory(folder);
    }
}

public sealed record JournalMount(string WimFile, int Index, string MountPath);

public sealed record JournalHive(string MountName, string HiveFile);

/// <summary>A USB stick whose erase has started and whose write has not finished.</summary>
public sealed record JournalUsb(int Number, string FriendlyName, string? SerialNumber, long SizeBytes);

public sealed record JournalState
{
    public required string SessionId { get; init; }
    public required string WorkDirectory { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public List<JournalMount> Mounts { get; init; } = [];
    public List<JournalHive> Hives { get; init; } = [];
    public List<JournalUsb> UsbSticks { get; init; } = [];
}

/// <summary>
///     Written before a resource is taken and after it is released, so that a crash, a cancel or a power
///     cut always leaves a record of what is still mounted or loaded. The next start cleans up from it.
/// </summary>
public sealed class BuildJournal : IHiveJournal, IUsbJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly Lock _gate = new();
    private readonly string _path;
    private readonly JournalState _state;

    private BuildJournal(string path, JournalState state)
    {
        _path = path;
        _state = state;
        SessionId = state.SessionId;
    }

    public JournalState State
    {
        get
        {
            lock (_gate)
            {
                return _state with
                {
                    Mounts = [.. _state.Mounts], Hives = [.. _state.Hives], UsbSticks = [.. _state.UsbSticks]
                };
            }
        }
    }

    public string SessionId { get; }

    /// <summary>True when nothing is recorded as mounted or loaded.</summary>
    public bool IsClean
    {
        get
        {
            lock (_gate)
            {
                return _state.Mounts.Count == 0 && _state.Hives.Count == 0;
            }
        }
    }

    public void HiveLoading(string mountName, string hiveFile)
    {
        Change(s => s.Hives.Add(new JournalHive(mountName, hiveFile)));
    }

    public void HiveUnloaded(string mountName)
    {
        Change(s => s.Hives.RemoveAll(h => string.Equals(h.MountName, mountName, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Recorded right before a stick is erased, so an interrupted write is known afterwards.</summary>
    public void EraseStarting(DiskFacts disk)
    {
        Change(s => s.UsbSticks.Add(new JournalUsb(disk.Number, disk.FriendlyName, disk.SerialNumber, disk.SizeBytes)));
    }

    public void Finished(DiskFacts disk)
    {
        Change(s => s.UsbSticks.RemoveAll(u => u.Number == disk.Number && u.SizeBytes == disk.SizeBytes
                                                                       && string.Equals(u.SerialNumber,
                                                                           disk.SerialNumber,
                                                                           StringComparison.OrdinalIgnoreCase)));
    }

    public static BuildJournal Create(WorkLayout layout, TimeProvider? time = null)
    {
        var state = new JournalState
        {
            // Short, because it becomes part of registry key names.
            SessionId = Guid.NewGuid().ToString("N")[..12],
            WorkDirectory = layout.Root,
            StartedUtc = (time ?? TimeProvider.System).GetUtcNow()
        };
        var journal = new BuildJournal(layout.Journal, state);
        File.WriteAllText(layout.Marker, state.SessionId);
        journal.Save();
        return journal;
    }

    /// <summary>Reads the journal of an earlier run. Null when there is none or it cannot be read.</summary>
    public static BuildJournal? TryLoad(WorkLayout layout)
    {
        try
        {
            if (!File.Exists(layout.Journal)) return null;
            var state = JsonSerializer.Deserialize<JournalState>(File.ReadAllText(layout.Journal), JsonOptions);
            return state is null ? null : new BuildJournal(layout.Journal, state);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void MountStarting(string wimFile, int index, string mountPath)
    {
        Change(s => s.Mounts.Add(new JournalMount(wimFile, index, mountPath)));
    }

    public void MountReleased(string mountPath)
    {
        Change(s => s.Mounts.RemoveAll(m => string.Equals(m.MountPath, mountPath, StringComparison.OrdinalIgnoreCase)));
    }

    private void Change(Action<JournalState> change)
    {
        lock (_gate)
        {
            change(_state);
            Save();
        }
    }

    private void Save()
    {
        // A half-written journal would defeat its purpose.
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_state, JsonOptions));
    }
}

/// <summary>What was found and done while cleaning up after an interrupted run.</summary>
public sealed record CleanupReport(IReadOnlyList<string> Done, IReadOnlyList<string> Problems)
{
    public bool Success => Problems.Count == 0;

    /// <summary>Things the user should know that need no cleanup, such as a USB stick that was not finished.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>
///     Releases what an interrupted run left behind: loaded hives, mounted images, the work folder.
///     It touches only what the journal lists and only folders that carry Plainstall's marker file.
/// </summary>
public sealed class LeftoverCleaner
{
    private readonly IImagingService? _imaging;
    private readonly ILogger _logger;

    /// <param name="imaging">Null when DISM is not available (not elevated). Mounts are then reported, not discarded.</param>
    /// <param name="logger">Receives what happens while unloading hives. Null logs nothing.</param>
    public LeftoverCleaner(IImagingService? imaging, ILogger? logger = null)
    {
        _imaging = imaging;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>True when the work folder holds a journal with open entries.</summary>
    public static bool HasLeftovers(WorkLayout layout)
    {
        return BuildJournal.TryLoad(layout) is { IsClean: false };
    }

    /// <summary>
    ///     One sentence per USB stick that an earlier run erased but did not finish writing. Such a stick needs
    ///     no cleanup: it is simply not usable until it is written again.
    /// </summary>
    public static IReadOnlyList<string> UnfinishedSticks(WorkLayout layout)
    {
        return BuildJournal.TryLoad(layout)?.State.UsbSticks
            .Select(u =>
                $"The USB stick '{u.FriendlyName}' (disk {u.Number}) was erased by an earlier run but not completely written. It is not usable as it is. Write it again.")
            .ToList() ?? [];
    }

    public CleanupReport Clean(WorkLayout layout, bool deleteWorkFolder = true, IProgress<int>? progress = null)
    {
        var done = new List<string>();
        var problems = new List<string>();
        var unfinishedSticks = UnfinishedSticks(layout);
        var journal = BuildJournal.TryLoad(layout);

        if (journal is not null)
        {
            var state = journal.State;
            foreach (var hive in state.Hives)
            {
                var error = OfflineHive.TryUnload(hive.MountName, _logger);
                if (error == 0)
                {
                    journal.HiveUnloaded(hive.MountName);
                    done.Add($"Released the registry file {Path.GetFileName(hive.HiveFile)}.");
                }
                else
                {
                    problems.Add(
                        $"The registry file {hive.HiveFile} is still loaded as HKLM\\{hive.MountName}. Close programs that may be using it and try again.");
                }
            }

            if (state.Mounts.Count > 0)
            {
                if (_imaging is null)
                {
                    problems.Add("A Windows image is still mounted. Start Plainstall as administrator to release it.");
                }
                else
                {
                    // Only discard what the system really lists as mounted at a path we recorded.
                    var mounted = _imaging.GetMountedImages();
                    foreach (var mount in state.Mounts)
                    {
                        var live = mounted.Any(m => string.Equals(Path.GetFullPath(m.MountPath).TrimEnd('\\'),
                            Path.GetFullPath(mount.MountPath).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
                        try
                        {
                            if (live)
                            {
                                _imaging.UnmountAndDiscard(mount.MountPath, progress);
                                done.Add($"Discarded the mounted image at {mount.MountPath}.");
                            }

                            journal.MountReleased(mount.MountPath);
                        }
                        catch (PlainstallException ex)
                        {
                            problems.Add($"The image mounted at {mount.MountPath} could not be released: {ex.Message}");
                        }
                    }
                }
            }
        }

        if (!deleteWorkFolder || problems.Count > 0 || !Directory.Exists(layout.Root))
            return new CleanupReport(done, problems) { Notes = unfinishedSticks };

        if (!layout.IsOurs)
            problems.Add($"The folder {layout.Root} was not created by Plainstall and is left alone.");
        else
            try
            {
                layout.Delete();
                done.Add($"Deleted the work folder {layout.Root}.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"The work folder {layout.Root} could not be deleted completely: {ex.Message}");
            }

        return new CleanupReport(done, problems) { Notes = unfinishedSticks };
    }
}