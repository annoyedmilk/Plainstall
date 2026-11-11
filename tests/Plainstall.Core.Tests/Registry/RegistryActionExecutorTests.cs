using System.Text.Json;
using Microsoft.Win32;
using Plainstall.Core.Catalog;
using Plainstall.Core.Registry;

namespace Plainstall.Core.Tests.Registry;

/// <summary>
///     Runs the executor against ordinary keys under HKCU, one subtree per "hive". These tests do not load
///     a real offline hive, because that needs administrator rights.
/// </summary>
public sealed class RegistryActionExecutorTests : IDisposable
{
    private const string BasePath = @"Software\Plainstall.Tests";

    // ---- scheduled tasks, stored the way the 26H2 install image stores them ----

    private const string Cache = RegistryActionExecutor.TaskCachePath;

    /// <summary>The index keys a task can be listed under, next to Tree and Tasks.</summary>
    private static readonly string[] TaskIndexes = ["Boot", "Logon", "Maintenance", "Plain"];

    private readonly RegistryActionExecutor _executor;
    private readonly Dictionary<RegistryHiveId, RegistryKey> _hiveKeys = [];
    private readonly RegistryKey _sandbox;
    private readonly string _sandboxName = Guid.NewGuid().ToString("N");

    public RegistryActionExecutorTests()
    {
        _sandbox = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{BasePath}\{_sandboxName}", true);
        foreach (var hive in Enum.GetValues<RegistryHiveId>())
            _hiveKeys[hive] = _sandbox.CreateSubKey(hive.ToString(), true);
        _executor = new RegistryActionExecutor(Root, retryDelay: TimeSpan.Zero);

        // What an offline SYSTEM hive looks like: a Select key and numbered control sets, no CurrentControlSet.
        Root(RegistryHiveId.MachineSystem).SetValue("Select", "Current", 1, RegistryValueKind.DWord);
        Root(RegistryHiveId.MachineSystem)
            .SetValue(@"ControlSet001\Services\DiagTrack", "Start", 2, RegistryValueKind.DWord);
    }

    public void Dispose()
    {
        foreach (var key in _hiveKeys.Values) key.Dispose();
        _sandbox.Dispose();
        using var parent = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(BasePath, true);
        parent?.DeleteSubKeyTree(_sandboxName, false);
    }

    private RegistryRoot Root(RegistryHiveId hive)
    {
        return new RegistryRoot(_hiveKeys[hive].Handle, false, hive.ToString());
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static RegistrySetAction Set(RegistryHiveId hive, string key, string name, string type, string dataJson,
        bool ifKeyExists = false)
    {
        return new RegistrySetAction
        {
            Hive = hive, Key = key, Name = name, Type = Enum.Parse<RegistryValueType>(type, true),
            Data = Json(dataJson), IfKeyExists = ifKeyExists
        };
    }

    private object? Read(RegistryHiveId hive, string key, string name)
    {
        return Root(hive).GetValue(key, name)?.Value;
    }

    [Fact]
    public void Set_creates_missing_keys_and_writes_each_type()
    {
        const RegistryHiveId hive = RegistryHiveId.MachineSoftware;
        Assert.Equal(ActionStatus.Applied, _executor.Apply(Set(hive, @"Policies\A\B", "Dword", "dword", "1")).Status);
        Assert.Equal(ActionStatus.Applied,
            _executor.Apply(Set(hive, @"Policies\A\B", "Big", "dword", "4294967295")).Status);
        Assert.Equal(ActionStatus.Applied,
            _executor.Apply(Set(hive, @"Policies\A\B", "Qword", "qword", "4294967296")).Status);
        Assert.Equal(ActionStatus.Applied,
            _executor.Apply(Set(hive, @"Policies\A\B", "Text", "string", "\"0\"")).Status);
        Assert.Equal(ActionStatus.Applied,
            _executor.Apply(Set(hive, @"Policies\A\B", "Expand", "expandString", "\"%SystemRoot%\\\\x\"")).Status);
        Assert.Equal(ActionStatus.Applied,
            _executor.Apply(Set(hive, @"Policies\A\B", "Multi", "multiString", "[\"a\",\"b\"]")).Status);
        Assert.Equal(ActionStatus.Applied,
            _executor.Apply(Set(hive, @"Policies\A\B", "Bin", "binary", "\"0a1b\"")).Status);
        Assert.Equal(ActionStatus.Applied,
            _executor.Apply(Set(hive, @"Policies\A\B", "", "string", "\"default value\"")).Status);

        Assert.Equal(1, Read(hive, @"Policies\A\B", "Dword"));
        Assert.Equal(-1, Read(hive, @"Policies\A\B", "Big"));
        Assert.Equal(4294967296L, Read(hive, @"Policies\A\B", "Qword"));
        Assert.Equal("0", Read(hive, @"Policies\A\B", "Text"));
        Assert.Equal(@"%SystemRoot%\x", Read(hive, @"Policies\A\B", "Expand"));
        Assert.Equal(["a", "b"], (string[])Read(hive, @"Policies\A\B", "Multi")!);
        Assert.Equal(new byte[] { 0x0a, 0x1b }, (byte[])Read(hive, @"Policies\A\B", "Bin")!);
        Assert.Equal("default value", Read(hive, @"Policies\A\B", ""));

        Assert.Equal(RegistryValueKind.String, Root(hive).GetValue(@"Policies\A\B", "Text")!.Value.Kind);
        Assert.Equal(RegistryValueKind.ExpandString, Root(hive).GetValue(@"Policies\A\B", "Expand")!.Value.Kind);
    }

    [Fact]
    public void CurrentControlSet_is_resolved_from_Select_Current()
    {
        Root(RegistryHiveId.MachineSystem).SetValue("Select", "Current", 3, RegistryValueKind.DWord);
        var outcome = _executor.Apply(Set(RegistryHiveId.MachineSystem, @"CurrentControlSet\Control\FileSystem",
            "LongPathsEnabled", "dword", "1"));

        Assert.Equal(ActionStatus.Applied, outcome.Status);
        Assert.Equal(1, Read(RegistryHiveId.MachineSystem, @"ControlSet003\Control\FileSystem", "LongPathsEnabled"));
        Assert.False(Root(RegistryHiveId.MachineSystem).KeyExists("CurrentControlSet"));
        Assert.False(Root(RegistryHiveId.MachineSystem).KeyExists(@"ControlSet001\Control"));
    }

    [Fact]
    public void Keys_outside_CurrentControlSet_are_used_as_written()
    {
        _executor.Apply(Set(RegistryHiveId.MachineSystem, @"Setup\MoSetup", "AllowUpgradesWithUnsupportedTPMOrCPU",
            "dword", "1"));
        Assert.Equal(1, Read(RegistryHiveId.MachineSystem, @"Setup\MoSetup", "AllowUpgradesWithUnsupportedTPMOrCPU"));
    }

    [Fact]
    public void A_missing_Select_value_fails_the_action_instead_of_guessing()
    {
        Root(RegistryHiveId.SetupSystem).SetValue("Other", "x", 1, RegistryValueKind.DWord);
        var outcome =
            _executor.Apply(Set(RegistryHiveId.SetupSystem, @"CurrentControlSet\Control\X", "Y", "dword", "1"));
        Assert.Equal(ActionStatus.Failed, outcome.Status);
        Assert.Contains("Select", outcome.Reason);
    }

    [Fact]
    public void IfKeyExists_writes_into_an_existing_service_and_skips_a_missing_one()
    {
        var existing = _executor.Apply(Set(RegistryHiveId.MachineSystem, @"CurrentControlSet\Services\DiagTrack",
            "Start", "dword", "4", true));
        var missing = _executor.Apply(Set(RegistryHiveId.MachineSystem, @"CurrentControlSet\Services\WSAIFabricSvc",
            "Start", "dword", "3", true));

        Assert.Equal(ActionStatus.Applied, existing.Status);
        Assert.Equal(4, Read(RegistryHiveId.MachineSystem, @"ControlSet001\Services\DiagTrack", "Start"));
        Assert.Equal(ActionStatus.Skipped, missing.Status);
        // No empty stub of a service that this Windows build does not have.
        Assert.False(Root(RegistryHiveId.MachineSystem).KeyExists(@"ControlSet001\Services\WSAIFabricSvc"));
    }

    [Fact]
    public void Delete_removes_a_value_or_a_whole_tree_and_reports_absent_targets_as_skipped()
    {
        const RegistryHiveId hive = RegistryHiveId.DefaultUser;
        var root = Root(hive);
        root.SetValue(@"Software\Microsoft\Windows\CurrentVersion\Run", "OneDriveSetup", "x", RegistryValueKind.String);
        root.SetValue(@"Software\Microsoft\Windows\CurrentVersion\Run", "Other", "y", RegistryValueKind.String);
        root.SetValue(@"Software\CDM\Subscriptions\A\B", "v", 1, RegistryValueKind.DWord);

        var value = _executor.Apply(new RegistryDeleteAction
            { Hive = hive, Key = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "OneDriveSetup" });
        var tree = _executor.Apply(new RegistryDeleteAction { Hive = hive, Key = @"Software\CDM\Subscriptions" });
        var noValue = _executor.Apply(new RegistryDeleteAction
            { Hive = hive, Key = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "Missing" });
        var noKey = _executor.Apply(new RegistryDeleteAction { Hive = hive, Key = @"Software\CDM\SuggestedApps" });

        Assert.Equal(ActionStatus.Applied, value.Status);
        Assert.Equal(ActionStatus.Applied, tree.Status);
        Assert.Equal(ActionStatus.Skipped, noValue.Status);
        Assert.Equal(ActionStatus.Skipped, noKey.Status);
        Assert.Null(Read(hive, @"Software\Microsoft\Windows\CurrentVersion\Run", "OneDriveSetup"));
        Assert.Equal("y", Read(hive, @"Software\Microsoft\Windows\CurrentVersion\Run", "Other"));
        Assert.False(root.KeyExists(@"Software\CDM\Subscriptions"));
        Assert.True(root.KeyExists(@"Software\CDM"));
    }

    private string AddTask(string path, string index = "Plain")
    {
        var id = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}";
        var root = Root(RegistryHiveId.MachineSoftware);
        root.SetValue($@"{Cache}\Tree\{path}", "Id", id, RegistryValueKind.String);
        root.SetValue($@"{Cache}\Tree\{path}", "Index", 3, RegistryValueKind.DWord);
        root.SetValue($@"{Cache}\Tasks\{id}", "Path", "\\" + path, RegistryValueKind.String);
        root.SetValue($@"{Cache}\{index}\{id}", "", "", RegistryValueKind.String);
        return id;
    }

    private bool TaskParts(string path, string id)
    {
        return Root(RegistryHiveId.MachineSoftware).KeyExists($@"{Cache}\Tree\{path}")
               || Root(RegistryHiveId.MachineSoftware).KeyExists($@"{Cache}\Tasks\{id}")
               || TaskIndexes.Any(i => Root(RegistryHiveId.MachineSoftware).KeyExists($@"{Cache}\{i}\{id}"));
    }

    [Fact]
    public void Deleting_a_task_removes_its_tree_entry_its_definition_and_its_index_entry()
    {
        const string appraiser = @"Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser";
        const string startup = @"Microsoft\Windows\Application Experience\StartupAppTask";
        var appraiserId = AddTask(appraiser);
        var startupId = AddTask(startup, "Logon");

        var outcome = _executor.Apply(new TaskDeleteAction { Path = appraiser });

        Assert.Equal(ActionStatus.Applied, outcome.Status);
        Assert.False(TaskParts(appraiser, appraiserId));
        // The neighbour in the same folder is untouched.
        Assert.True(Root(RegistryHiveId.MachineSoftware).KeyExists($@"{Cache}\Tree\{startup}"));
        Assert.True(Root(RegistryHiveId.MachineSoftware).KeyExists($@"{Cache}\Tasks\{startupId}"));
        Assert.True(Root(RegistryHiveId.MachineSoftware).KeyExists($@"{Cache}\Logon\{startupId}"));
    }

    [Fact]
    public void Deleting_a_task_folder_removes_every_task_in_it_including_nested_ones()
    {
        const string folder = @"Microsoft\Windows\Customer Experience Improvement Program";
        var ids = new[]
        {
            AddTask(folder + @"\Consolidator"), AddTask(folder + @"\KernelCeipTask", "Boot"),
            AddTask(folder + @"\UsbCeip"), AddTask(folder + @"\Sub\Nested")
        };
        var outsideId = AddTask(@"Microsoft\Windows\UpdateOrchestrator\Schedule Scan");

        var outcome = _executor.Apply(new TaskDeleteAction { Path = folder, Folder = true });

        Assert.Equal(ActionStatus.Applied, outcome.Status);
        Assert.Contains("4 task", outcome.Reason);
        Assert.False(Root(RegistryHiveId.MachineSoftware).KeyExists($@"{Cache}\Tree\{folder}"));
        Assert.All(ids, id => Assert.False(Root(RegistryHiveId.MachineSoftware).KeyExists($@"{Cache}\Tasks\{id}")));
        // Windows Update's own tasks are never collateral damage.
        Assert.True(TaskParts(@"Microsoft\Windows\UpdateOrchestrator\Schedule Scan", outsideId));
    }

    [Fact]
    public void A_task_that_is_not_in_the_image_is_skipped_and_a_folder_named_as_a_task_is_an_error()
    {
        AddTask(@"Microsoft\Windows\Autochk\Proxy");
        var missing = _executor.Apply(new TaskDeleteAction { Path = @"Microsoft\Windows\Chkdsk\Proxy" });
        var folderAsTask = _executor.Apply(new TaskDeleteAction { Path = @"Microsoft\Windows\Autochk" });

        Assert.Equal(ActionStatus.Skipped, missing.Status);
        Assert.Equal(ActionStatus.Failed, folderAsTask.Status);
        Assert.True(Root(RegistryHiveId.MachineSoftware).KeyExists($@"{Cache}\Tree\Microsoft\Windows\Autochk\Proxy"));
    }

    [Fact]
    public void A_hive_that_is_not_loaded_fails_the_action_without_throwing()
    {
        var executor = new RegistryActionExecutor(_ => null, retryDelay: TimeSpan.Zero);
        var outcome = executor.Apply(Set(RegistryHiveId.MachineSoftware, "A", "B", "dword", "1"));
        Assert.Equal(ActionStatus.Failed, outcome.Status);
    }

    [Fact]
    public void The_root_of_a_hive_can_never_be_deleted()
    {
        Assert.Throws<ArgumentException>(() => Root(RegistryHiveId.MachineSoftware).DeleteTree(""));
    }

    [Fact]
    public void Every_registry_action_of_the_real_catalog_applies_cleanly()
    {
        var catalog = CatalogLoader.LoadEmbedded();
        var actions = catalog.AllEntries.SelectMany(e => e.Actions).Where(RegistryActionExecutor.Handles).ToList();
        Assert.NotEmpty(actions);
        foreach (var action in actions)
        {
            var outcome = _executor.Apply(action);
            Assert.True(outcome.Status != ActionStatus.Failed, $"{action.KindName} {action.Target}: {outcome.Reason}");
        }
    }
}