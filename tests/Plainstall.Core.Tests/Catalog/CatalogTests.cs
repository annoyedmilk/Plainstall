using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Plainstall.Core.Catalog;
using Plainstall.Core.Tests.Unattend;

namespace Plainstall.Core.Tests.Catalog;

public class CatalogTests
{
    private static readonly Core.Catalog.Catalog Embedded = CatalogLoader.LoadEmbedded();

    // The schema library registers each schema under its $id and refuses to load the same one twice.
    private static readonly ConcurrentDictionary<string, Lazy<JsonSchema>> Schemas = new();

    [Fact]
    public void Embedded_catalog_loads_and_passes_every_rule()
    {
        Assert.Empty(CatalogValidator.Validate(Embedded));
        Assert.NotEmpty(Embedded.Apps);
        Assert.NotEmpty(Embedded.Tweaks);
        Assert.Equal(4, Embedded.Presets.Count);
        Assert.Equal("recommended", Embedded.DefaultPreset.Id);
    }

    [Fact]
    public void Embedded_catalog_is_the_same_as_the_files_in_the_repo()
    {
        var fromDisk = CatalogLoader.LoadFromDirectory(TestFiles.CatalogDirectory);
        Assert.Equal(fromDisk.AllEntries.Select(e => e.Id), Embedded.AllEntries.Select(e => e.Id));
    }

    [Theory]
    [InlineData("apps.json", "catalog.schema.json")]
    [InlineData("tweaks.json", "catalog.schema.json")]
    [InlineData("presets.json", "presets.schema.json")]
    public void Catalog_files_match_their_json_schema(string file, string schema)
    {
        AssertMatchesSchema(File.ReadAllText(TestFiles.CatalogFile(file)), schema, file);
    }

    [Fact]
    public void Example_profiles_match_the_profile_schema()
    {
        var examples = TestFiles.ExampleProfiles().ToList();
        Assert.NotEmpty(examples);
        foreach (var path in examples)
            AssertMatchesSchema(File.ReadAllText(path), "profile.schema.json", Path.GetFileName(path));
    }

    internal static void AssertMatchesSchema(string json, string schemaFile, string what)
    {
        var schema = Schemas.GetOrAdd(schemaFile,
            f => new Lazy<JsonSchema>(() => JsonSchema.FromText(File.ReadAllText(TestFiles.Schema(f))))).Value;
        using var document = JsonDocument.Parse(json);
        var result = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (result.IsValid) return;

        var errors = (result.Details ?? [])
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))
            .Take(20);
        Assert.Fail(
            $"{what} does not match {schemaFile}:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
    }

    [Fact]
    public void Every_app_entry_says_how_to_get_the_app_back()
    {
        Assert.All(Embedded.Apps, e => Assert.NotNull(e.Reinstall));
    }

    [Fact]
    public void No_advanced_entry_is_in_a_preset_and_minimal_holds_only_safe_entries()
    {
        Assert.All(Embedded.AllEntries.Where(e => e.Risk == RiskLevel.Advanced), e => Assert.Empty(e.DefaultIn));
        Assert.All(Embedded.AllEntries.Where(e => e.DefaultIn.Contains("minimal")),
            e => Assert.Equal(RiskLevel.Safe, e.Risk));
    }

    [Fact]
    public void Edge_removal_is_opt_in_and_never_touches_webview()
    {
        var edge = Embedded.FindEntry("edge")!;
        Assert.Empty(edge.DefaultIn);
        Assert.Equal(RiskLevel.Advanced, edge.Risk);
        Assert.DoesNotContain(Embedded.AllEntries.SelectMany(e => e.Actions).OfType<FileDeleteAction>(),
            a => a.Path.Contains("WebView", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Things_the_product_promises_to_keep_are_protected()
    {
        foreach (var package in new[]
                 {
                     "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller",
                     "Microsoft.WindowsTerminal",
                     "Microsoft.SecHealthUI", "Microsoft.VCLibs.140.00", "Microsoft.UI.Xaml.2.8",
                     "Microsoft.NET.Native.Runtime.2.2",
                     "Microsoft.WindowsAppRuntime.1.8", "Microsoft.HEIFImageExtension"
                 })
            Assert.True(Embedded.IsProtectedPackage(package), package);
        Assert.False(Embedded.IsProtectedPackage("Clipchamp.Clipchamp"));
        // The Xbox sign-in pieces are not protected, but nothing in the catalog removes them.
        var removed = Embedded.Apps.SelectMany(e => e.Actions).OfType<AppxRemoveProvisionedAction>()
            .Select(a => a.Package).ToList();
        Assert.DoesNotContain("Microsoft.XboxIdentityProvider", removed);
        Assert.DoesNotContain("Microsoft.Xbox.TCUI", removed);
        Assert.DoesNotContain("Microsoft.XboxSpeechToTextOverlay", removed);
    }

    [Fact]
    public void Values_that_are_easy_to_get_wrong_stay_right()
    {
        var actions = Embedded.AllEntries.SelectMany(e => e.Actions).ToList();
        // The Cross Device package name.
        Assert.Contains(actions.OfType<AppxRemoveProvisionedAction>(),
            a => a.Package == "MicrosoftWindows.CrossDevice");
        Assert.DoesNotContain(actions.OfType<AppxRemoveProvisionedAction>(),
            a => a.Package == "Microsoft.Windows.CrossDevice");
        // Edge first-run pages are hidden with 1, not 0.
        var firstRun = Assert.Single(actions.OfType<RegistrySetAction>(), a => a.Name == "HideFirstRunExperience");
        Assert.Equal(1, firstRun.Data.GetInt32());
        // The classic context menu is applied per user at first sign-in.
        Assert.Contains(actions.OfType<FirstLogonRegistryAction>(),
            a => a.Key.Contains("{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}"));
        // No numbered control set anywhere.
        Assert.DoesNotContain(actions.OfType<RegistrySetAction>(),
            a => a.Key.StartsWith("ControlSet0", StringComparison.OrdinalIgnoreCase));
        // Never GlobalUserDisabled. EnableActivityFeed is not set, because it breaks the clipboard history.
        Assert.DoesNotContain(actions.OfType<RegistrySetAction>(),
            a => a.Name is "GlobalUserDisabled" or "EnableActivityFeed");
        // Service keys are only written when the service exists.
        Assert.All(
            actions.OfType<RegistrySetAction>()
                .Where(a => a.Key.Contains(@"\Services\", StringComparison.OrdinalIgnoreCase)),
            a => Assert.True(a.IfKeyExists));
    }

    [Fact]
    public void Windows_spotlight_on_the_lock_screen_has_one_switch()
    {
        string[] spotlightValues =
            ["ContentDeliveryAllowed", "SubscribedContentEnabled", "SubscribedContent-338387Enabled"];
        var owners = Embedded.AllEntries
            .Where(e => e.Actions.OfType<RegistrySetAction>().Any(a => spotlightValues.Contains(a.Name)))
            .Select(e => e.Id);
        Assert.Equal(["no-content-delivery"], owners);
        Assert.Equal(RiskLevel.Moderate, Embedded.FindEntry("no-content-delivery")!.Risk);
        Assert.Contains("Spotlight", Embedded.FindEntry("no-content-delivery")!.Description);
    }

    [Fact]
    public void Nothing_that_works_against_windows_update_is_in_a_preset()
    {
        Assert.Empty(Embedded.FindEntry("no-reserved-storage")!.DefaultIn);
        Assert.Empty(Embedded.FindEntry("no-restart-while-signed-in")!.DefaultIn);
        Assert.Equal(RiskLevel.Moderate, Embedded.FindEntry("no-restart-while-signed-in")!.Risk);
        Assert.Empty(Embedded.FindEntry("delay-feature-updates")!.DefaultIn);
    }

    [Fact]
    public void The_hardware_bypass_does_not_hide_the_requirements_notice_by_itself()
    {
        var bypass = Embedded.FindEntry("setup-hardware-bypass")!;
        Assert.Equal("setup.bypassHardwareChecks", bypass.BoundTo);
        Assert.False(HidesNotice(bypass));
        var notice = Embedded.FindEntry("hide-unsupported-hardware-notice")!;
        Assert.True(HidesNotice(notice));
        Assert.Null(notice.BoundTo);
        Assert.Empty(notice.DefaultIn);
        return;

        static bool HidesNotice(CatalogEntry e) => e.Actions.OfType<RegistrySetAction>()
            .Any(a => a.Key.Contains("UnsupportedHardwareNotificationCache", StringComparison.Ordinal));
    }

    [Fact]
    public void Developer_features_are_enabled_by_name_and_opt_in()
    {
        Assert.Equal(["Microsoft-Windows-Subsystem-Linux", "VirtualMachinePlatform"],
            Embedded.FindEntry("wsl")!.Actions.OfType<FeatureEnableAction>().Select(a => a.Name));
        Assert.Equal(["Containers-DisposableClientVM"],
            Embedded.FindEntry("windows-sandbox")!.Actions.OfType<FeatureEnableAction>().Select(a => a.Name));
        Assert.All(Embedded.AllEntries.Where(e => e.Actions.OfType<FeatureEnableAction>().Any()),
            e => Assert.Empty(e.DefaultIn));
    }

    [Fact]
    public void Every_first_sign_in_command_is_valid_windows_powershell()
    {
        // The script compiles each command only when it runs, so a syntax error would show up as a failed step on
        // the installed PC. The real parser finds it here.
        var commands = Embedded.AllEntries
            .SelectMany(e => e.Actions.OfType<FirstLogonCommandAction>().Select(a => (e.Id, a.Command)))
            .ToList();
        Assert.Contains(commands, c => c.Id == "high-performance-power-plan");
        using var folder = new TempDirectory();
        var paths = new string[commands.Count];
        for (var i = 0; i < commands.Count; i++)
            paths[i] = folder.Write($"{i}-{commands[i].Id}.ps1",
                [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(commands[i].Command)]);

        var parsed = WindowsPowerShell.Parse(paths);

        for (var i = 0; i < commands.Count; i++)
            Assert.True(parsed[i].Errors.Count == 0, $"{commands[i].Id}: {string.Join("; ", parsed[i].Errors)}");
    }

    [Fact]
    public void The_power_plan_is_switched_by_the_machine_script_and_only_in_the_gaming_preset()
    {
        var entry = Embedded.FindEntry("high-performance-power-plan")!;
        Assert.Equal(["gaming"], entry.DefaultIn);
        var command = Assert.IsType<FirstLogonCommandAction>(Assert.Single(entry.Actions));
        Assert.Equal(FirstLogonScope.Machine, command.Scope);
        // The built-in High performance scheme. A PC without it gets a log line, not a changed plan.
        Assert.Contains("'8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c'", command.Command);
        Assert.Contains("$LASTEXITCODE", command.Command);
    }

    // ---- validator, on deliberately broken catalogs ----

    private static IReadOnlyList<string> ProblemsAfter(Action<JsonObject, JsonObject, JsonObject> mutate)
    {
        var apps = JsonNode.Parse(File.ReadAllText(TestFiles.CatalogFile("apps.json")))!.AsObject();
        var tweaks = JsonNode.Parse(File.ReadAllText(TestFiles.CatalogFile("tweaks.json")))!.AsObject();
        var presets = JsonNode.Parse(File.ReadAllText(TestFiles.CatalogFile("presets.json")))!.AsObject();
        mutate(apps, tweaks, presets);
        return CatalogValidator.Validate(CatalogLoader.Parse(apps.ToJsonString(), tweaks.ToJsonString(),
            presets.ToJsonString()));
    }

    private static JsonObject Entry(JsonObject file, string id)
    {
        return file["entries"]!.AsArray().Select(n => n!.AsObject()).First(e => e["id"]!.GetValue<string>() == id);
    }

    [Fact]
    public void Validator_reports_a_duplicate_id()
    {
        var problems = ProblemsAfter((_, tweaks, _) => Entry(tweaks, "dark-theme")["id"] = "clipchamp");
        Assert.Contains(problems, p => p.Contains("clipchamp") && p.Contains("more than once"));
    }

    /// <summary>One action is added to an entry of apps.json or tweaks.json, and the validator names the problem.</summary>
    [Theory]
    [InlineData("tweaks", "dark-theme",
        """{ "kind": "registry.set", "hive": "defaultUser", "key": "Software\\Microsoft\\Windows\\CurrentVersion\\BackgroundAccessApplications", "name": "GlobalUserDisabled", "type": "dword", "data": 1 }""",
        "GlobalUserDisabled")]
    [InlineData("tweaks", "dark-theme",
        """{ "kind": "registry.set", "hive": "defaultUser", "key": "Software\\Classes\\CLSID\\{x}", "name": "", "type": "string", "data": "" }""",
        "firstLogon.registry")]
    [InlineData("tweaks", "long-paths",
        """{ "kind": "registry.set", "hive": "machineSystem", "key": "ControlSet001\\Control\\FileSystem", "name": "X", "type": "dword", "data": 1 }""",
        "CurrentControlSet")]
    [InlineData("tweaks", "long-paths",
        """{ "kind": "registry.set", "hive": "machineSystem", "key": "CurrentControlSet\\Control\\FileSystem", "name": "X", "type": "dword", "data": "not a number" }""",
        "dword")]
    [InlineData("tweaks", "dark-theme",
        """{ "kind": "registry.set", "hive": "machineSystem", "key": "CurrentControlSet\\Control\\FileSystem", "name": "LongPathsEnabled", "type": "dword", "data": 0 }""",
        "different value")]
    [InlineData("apps", "clipchamp", """{ "kind": "appx.removeProvisioned", "package": "Microsoft.WindowsStore" }""",
        "protected")]
    [InlineData("apps", "onedrive", """{ "kind": "file.delete", "path": "Windows\\..\\..\\outside.txt" }""",
        "relative to the image root")]
    public void Validator_rejects_an_added_action(string file, string entry, string action, string expected)
    {
        var problems = ProblemsAfter((apps, tweaks, _) =>
            Entry(file == "apps" ? apps : tweaks, entry)["actions"]!.AsArray().Add(JsonNode.Parse(action)));
        Assert.Contains(problems, p => p.Contains(expected));
    }

    [Fact]
    public void Validator_rejects_a_preset_whose_entry_requirement_is_outside_the_preset()
    {
        // 'outlook' requires the auto-install block. Take that tweak out of every preset.
        var problems = ProblemsAfter((_, tweaks, _) =>
            Entry(tweaks, "no-outlook-devhome-autoinstall")["defaultIn"] = new JsonArray());
        Assert.Contains(problems, p => p.Contains("requires no-outlook-devhome-autoinstall"));
    }

    [Fact]
    public void Unknown_properties_and_unknown_action_kinds_are_refused_when_loading()
    {
        var apps = File.ReadAllText(TestFiles.CatalogFile("apps.json"));
        var tweaks = File.ReadAllText(TestFiles.CatalogFile("tweaks.json"));
        var presets = File.ReadAllText(TestFiles.CatalogFile("presets.json"));

        var withTypo = tweaks.Replace("\"catalogVersion\": 1", "\"catalogVersion\": 1, \"entrys\": []");
        Assert.Throws<PlainstallException>(() => CatalogLoader.FromJson(apps, withTypo, presets));

        var withUnknownKind = tweaks.Replace("\"kind\": \"registry.set\"", "\"kind\": \"registry.sett\"");
        Assert.Throws<PlainstallException>(() => CatalogLoader.FromJson(apps, withUnknownKind, presets));
    }

    [Theory]
    [InlineData("dword", "5", true)]
    [InlineData("dword", "4294967295", true)]
    [InlineData("dword", "4294967296", false)]
    [InlineData("dword", "-1", false)]
    [InlineData("dword", "\"5\"", false)]
    [InlineData("qword", "4294967296", true)]
    [InlineData("string", "\"0\"", true)]
    [InlineData("string", "0", false)]
    [InlineData("multiString", "[\"a\",\"b\"]", true)]
    [InlineData("multiString", "[\"a\",1]", false)]
    [InlineData("binary", "\"0a1B\"", true)]
    [InlineData("binary", "\"0a1\"", false)]
    public void Registry_data_must_fit_its_type(string type, string json, bool valid)
    {
        using var document = JsonDocument.Parse(json);
        var valueType = Enum.Parse<RegistryValueType>(type, true);
        var value = RegistryData.TryConvert(valueType, document.RootElement, out var error);
        Assert.Equal(valid, value is not null);
        Assert.Equal(valid, error is null);
    }
}