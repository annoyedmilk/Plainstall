using System.Globalization;
using System.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Plainstall.Core.Catalog;

namespace Plainstall.Core.Registry;

/// <summary>
///     Applies the registry-backed catalog actions (<c>registry.set</c>, <c>registry.delete</c>,
///     <c>task.delete</c>) to a set of registry roots. It never throws for a single action: every
///     outcome is returned, so one stubborn value does not hide what happened to the rest.
/// </summary>
public sealed class RegistryActionExecutor
{
    public const string TaskCachePath = @"Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache";
    private const string CurrentControlSetPrefix = @"CurrentControlSet\";
    private static readonly string[] TaskIndexKeys = ["Boot", "Logon", "Maintenance", "Plain"];
    private readonly Dictionary<RegistryHiveId, string> _controlSets = [];
    private readonly ILogger _logger;
    private readonly TimeSpan _retryDelay;

    private readonly Func<RegistryHiveId, RegistryRoot?> _roots;

    /// <param name="roots">Returns the root for a hive, or null when that hive is not part of this session.</param>
    /// <param name="logger">Receives warnings about failed actions and retries. Null logs nothing.</param>
    /// <param name="retryDelay">Pause between attempts after "access denied". Tests pass zero.</param>
    public RegistryActionExecutor(Func<RegistryHiveId, RegistryRoot?> roots, ILogger? logger = null,
        TimeSpan? retryDelay = null)
    {
        _roots = roots;
        _logger = logger ?? NullLogger.Instance;
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(200);
    }

    public static bool Handles(CatalogAction action)
    {
        return action is RegistrySetAction or RegistryDeleteAction or TaskDeleteAction;
    }

    public ActionOutcome Apply(CatalogAction action)
    {
        try
        {
            return action switch
            {
                RegistrySetAction set => WithRetry(() => Set(set)),
                RegistryDeleteAction delete => WithRetry(() => Delete(delete)),
                TaskDeleteAction task => WithRetry(() => DeleteTask(task)),
                _ => ActionOutcome.Failed($"{action.KindName} is not a registry action.")
            };
        }
        catch (PlainstallException ex)
        {
            _logger.LogWarning("{Kind} {Target}: {Message}", action.KindName, action.Target, ex.Message);
            return ActionOutcome.Failed(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            _logger.LogWarning(ex, "{Kind} {Target} failed", action.KindName, action.Target);
            return ActionOutcome.Failed(ex.Message);
        }
    }

    /// <summary>
    ///     A write to a hive that was just loaded can fail one time with "access denied", also on a key that
    ///     is writable. Thus try again a few times before the failure counts.
    /// </summary>
    private ActionOutcome WithRetry(Func<ActionOutcome> operation)
    {
        const int attempts = 3;
        for (var attempt = 1;; attempt++)
            try
            {
                return operation();
            }
            catch (Exception ex) when (attempt < attempts &&
                                       ex is UnauthorizedAccessException or RegistryException { IsAccessDenied: true })
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("Access denied on attempt {Attempt}, retrying", attempt);
                if (_retryDelay > TimeSpan.Zero) Thread.Sleep(_retryDelay);
            }
    }

    private RegistryRoot RootFor(RegistryHiveId hive)
    {
        return _roots(hive) ??
               throw new PlainstallException(ErrorKind.Internal, $"The {hive} hive is not loaded in this step.");
    }

    /// <summary>
    ///     Replaces a leading <c>CurrentControlSet\</c> with the control set named by <c>Select\Current</c>.
    ///     An offline SYSTEM hive has no CurrentControlSet link: that link only exists in a running Windows.
    /// </summary>
    public string ResolveKey(RegistryHiveId hive, string key)
    {
        if (hive is not (RegistryHiveId.MachineSystem or RegistryHiveId.SetupSystem)
            || !key.StartsWith(CurrentControlSetPrefix, StringComparison.OrdinalIgnoreCase))
            return key;
        if (_controlSets.TryGetValue(hive, out var controlSet))
            return controlSet + key[(CurrentControlSetPrefix.Length - 1)..];
        var current = RootFor(hive).GetValue("Select", "Current");
        if (current is not { Value: int number } || number is < 1 or > 999)
            throw new PlainstallException(ErrorKind.Registry,
                "The image's SYSTEM registry has no valid Select\\Current value, so the active control set is unknown.");
        controlSet = string.Create(CultureInfo.InvariantCulture, $"ControlSet{number:000}");
        _controlSets[hive] = controlSet;
        return controlSet + key[(CurrentControlSetPrefix.Length - 1)..];
    }

    private ActionOutcome Set(RegistrySetAction action)
    {
        var root = RootFor(action.Hive);
        var key = ResolveKey(action.Hive, action.Key);
        if (action.IfKeyExists && !root.KeyExists(key))
            return ActionOutcome.Skipped("the key is not present in this image");

        var (value, kind) = ToRegistryValue(action.Type, RegistryData.ToTypedValue(action.Type, action.Data));
        root.SetValue(key, action.Name, value, kind);

        // Read back: a write that "succeeded" but is not there must not be reported as applied.
        var written = root.GetValue(key, action.Name);
        if (written is null || written.Value.Kind != kind || !RegistryData.ValuesEqual(written.Value.Value, value))
            return ActionOutcome.Failed("the value was written but reads back differently");
        return ActionOutcome.Applied;
    }

    private ActionOutcome Delete(RegistryDeleteAction action)
    {
        var root = RootFor(action.Hive);
        var key = ResolveKey(action.Hive, action.Key);
        if (action.Name is { } name)
            return root.DeleteValue(key, name)
                ? ActionOutcome.Applied
                : ActionOutcome.Skipped("the value is not present in this image");
        if (!root.DeleteTree(key)) return ActionOutcome.Skipped("the key is not present in this image");
        return root.KeyExists(key)
            ? ActionOutcome.Failed("the key is still there after deleting it")
            : ActionOutcome.Applied;
    }

    /// <summary>
    ///     In an install image a scheduled task is registry data only: <c>TaskCache\Tree\&lt;path&gt;</c> holds
    ///     the task's Id, <c>TaskCache\Tasks\{Id}</c> its definition, and one of Boot, Logon, Maintenance,
    ///     Plain an index entry. All three go together, otherwise Task Scheduler is left with half a task.
    /// </summary>
    private ActionOutcome DeleteTask(TaskDeleteAction action)
    {
        var root = RootFor(RegistryHiveId.MachineSoftware);
        var treePath = $@"{TaskCachePath}\Tree\{action.Path}";
        if (!root.KeyExists(treePath)) return ActionOutcome.Skipped("the task is not present in this image");

        var removed = 0;
        if (action.Folder)
        {
            removed += DeleteTaskFolder(root, treePath);
        }
        else
        {
            if (root.GetValue(treePath, "Id") is null)
                return ActionOutcome.Failed(
                    "this path is a task folder, not a task, so the catalog entry needs \"folder\": true");
            removed += DeleteSingleTask(root, treePath);
        }

        return removed > 0
            ? ActionOutcome.AppliedWith($"{removed} task(s) removed")
            : ActionOutcome.Skipped("the folder holds no tasks");
    }

    private static int DeleteTaskFolder(RegistryRoot root, string treePath)
    {
        var removed = root.GetSubKeyNames(treePath)
            .Select(child => treePath + "\\" + child)
            .Sum(childPath => root.GetValue(childPath, "Id") is null
                ? DeleteTaskFolder(root, childPath)
                : DeleteSingleTask(root, childPath));
        root.DeleteTree(treePath);
        return removed;
    }

    private static int DeleteSingleTask(RegistryRoot root, string treePath)
    {
        if (root.GetValue(treePath, "Id") is { Value: string id } && id.StartsWith('{') && id.EndsWith('}'))
        {
            root.DeleteTree($@"{TaskCachePath}\Tasks\{id}");
            foreach (var index in TaskIndexKeys) root.DeleteTree($@"{TaskCachePath}\{index}\{id}");
        }

        root.DeleteTree(treePath);
        return 1;
    }

    internal static (object Value, RegistryValueKind Kind) ToRegistryValue(RegistryValueType type, object typed)
    {
        return type switch
        {
            // RegistryKey.SetValue wants signed integers for DWORD and QWORD.
            RegistryValueType.Dword => (unchecked((int)(uint)typed), RegistryValueKind.DWord),
            RegistryValueType.Qword => (unchecked((long)(ulong)typed), RegistryValueKind.QWord),
            RegistryValueType.String => (typed, RegistryValueKind.String),
            RegistryValueType.ExpandString => (typed, RegistryValueKind.ExpandString),
            RegistryValueType.MultiString => (typed, RegistryValueKind.MultiString),
            RegistryValueType.Binary => (typed, RegistryValueKind.Binary),
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
    }
}