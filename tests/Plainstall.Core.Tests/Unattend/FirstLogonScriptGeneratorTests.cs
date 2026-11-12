using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Plainstall.Core.Catalog;
using Plainstall.Core.Pipeline;
using Plainstall.Core.Profiles;
using Plainstall.Core.Unattend;

namespace Plainstall.Core.Tests.Unattend;

public partial class FirstLogonScriptGeneratorTests
{
    private const string ClassicMenuKey =
        @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32";

    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string TestKey = @"Software\Plainstall Test";

    // The entry id only appears in error messages, never in a script.
    private const string EntryId = "test-entry";

    // ---- the only commands the typical scripts may call ----------------------------------------

    private static readonly string[] UserCommands =
    [
        "Add-Content", "Add-Type", "Get-Date", "Invoke-PlainstallCommand", "Invoke-PlainstallStep", "New-Item",
        "Out-Null", "Out-String",
        "Remove-PlainstallRegistryKey", "Remove-PlainstallRegistryValue", "Set-PlainstallRegistryValue",
        "Test-Path", "Where-Object", "Write-PlainstallLog", "<dynamic>", "Join-Path"
    ];

    private static readonly string[] MachineCommands =
    [
        "Add-AppxPackage", "Add-Content", "Find-PlainstallWinget", "Get-Command", "Get-Date",
        "Invoke-PlainstallCommand",
        "Invoke-PlainstallStep", "Join-Path", "New-Item", "Out-Null", "Out-String", "Select-Object", "Start-Process",
        "Start-Sleep", "Test-Path", "Test-PlainstallInternet", "Wait-PlainstallCondition", "Where-Object", "Write-Host",
        "Write-PlainstallLog", "<dynamic>"
    ];

    [GeneratedRegex(@"(?m)^\s*exit\b")]
    private static partial Regex ExitLine();

    [GeneratedRegex(@"\bwhile\b|\bdo \{|\bfor \(")]
    private static partial Regex LoopKeyword();

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static BuildPlan Plan(IEnumerable<PlannedAction> actions, params string[] winget)
    {
        return new BuildPlan
        {
            Entries = [],
            Actions = [.. actions],
            Notes = [],
            WingetInstalls = winget
        };
    }

    private static PlannedAction Set(string title, string key, string name, RegistryValueType type, string jsonData)
    {
        return new PlannedAction(EntryId, title,
            new FirstLogonRegistryAction
                { Op = FirstLogonRegistryOp.Set, Key = key, Name = name, Type = type, Data = Json(jsonData) });
    }

    private static PlannedAction Delete(string title, string key, string? name)
    {
        return new PlannedAction(EntryId, title,
            new FirstLogonRegistryAction { Op = FirstLogonRegistryOp.Delete, Key = key, Name = name });
    }

    private static PlannedAction Command(string title, FirstLogonScope scope, string summary, string command)
    {
        return new PlannedAction(EntryId, title,
            new FirstLogonCommandAction { Scope = scope, Summary = summary, Command = command });
    }

    /// <summary>A plan that uses every kind of first sign-in action once, with ordinary data.</summary>
    private static BuildPlan TypicalPlan()
    {
        return Plan(
            [
                Set("Dark theme", PersonalizeKey, "AppsUseLightTheme", RegistryValueType.Dword, "0"),
                Set("Classic context menu", ClassicMenuKey, "", RegistryValueType.String, "\"\""),
                Set("Hide Home", @"Software\Classes\CLSID\{f874310e-b6b7-47dc-bc84-b9e6b38f5903}", "",
                    RegistryValueType.String, "\"CLSID_MSGraphHomeFolder\""),
                Set("All value types", TestKey, "Largest dword", RegistryValueType.Dword, "4294967295"),
                Set("All value types", TestKey, "Largest qword", RegistryValueType.Qword, "18446744073709551615"),
                Set("All value types", TestKey, "Expandable", RegistryValueType.ExpandString,
                    "\"%USERPROFILE%\\\\Desktop\""),
                Set("All value types", TestKey, "Several", RegistryValueType.MultiString, "[\"one\", \"two words\"]"),
                Set("All value types", TestKey, "None", RegistryValueType.MultiString, "[]"),
                Set("All value types", TestKey, "Bytes", RegistryValueType.Binary, "\"00ff10AB\""),
                Set("All value types", TestKey, "No bytes", RegistryValueType.Binary, "\"\""),
                Set("It's a title with a quote", TestKey, "It's a name", RegistryValueType.String, "\"It's data\""),
                Delete("Remove a value", TestKey, "Old value"),
                Delete("Remove a default value", TestKey, ""),
                Delete("Remove a key", TestKey + @"\Old key", null),
                Command("No Store suggestions in search", FirstLogonScope.User, "Block the Store suggestion database",
                    "$d = Join-Path $env:LOCALAPPDATA 'Packages\\Example'; New-Item -ItemType Directory -Force -Path $d | Out-Null"),
                Command("OneDrive", FirstLogonScope.Machine, "Remove the OneDrive setup entry",
                    @"Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Example' -Name 'OneDriveSetup' -ErrorAction SilentlyContinue"),
                // Offline actions are not first sign-in work and must not show up in either script.
                new PlannedAction("offline", "Offline tweak", new RegistrySetAction
                {
                    Hive = RegistryHiveId.MachineSoftware, Key = @"Policies\OfflineOnly", Name = "OfflineOnly",
                    Type = RegistryValueType.Dword, Data = Json("1")
                }),
                new PlannedAction("offline-app", "Offline app",
                    new AppxRemoveProvisionedAction { Package = "Offline.Only" })
            ],
            "Mozilla.Firefox", "7zip.7zip", "Notepad++.Notepad++");
    }

    /// <summary>The same shape of plan for any text, to compare scripts that differ only in their data.</summary>
    private static BuildPlan PlanWithText(string s)
    {
        return Plan(
            [
                Set(s, $@"Software\Plainstall Test\{s}\Key", s, RegistryValueType.String, JsonSerializer.Serialize(s)),
                Set(s, $@"Software\Plainstall Test\{s}\Key", s, RegistryValueType.ExpandString,
                    JsonSerializer.Serialize(s)),
                Set(s, $@"Software\Plainstall Test\{s}\Key", s, RegistryValueType.MultiString,
                    JsonSerializer.Serialize(new[] { s, s })),
                Delete(s, $@"Software\Plainstall Test\{s}\Key", s),
                Delete(s, $@"Software\Plainstall Test\{s}\Key", null),
                Command(s, FirstLogonScope.User, s, "x" + s),
                Command(s, FirstLogonScope.Machine, s, "x" + s)
            ],
            "Mozilla.Firefox");
    }

    private static string UserText(BuildPlan plan)
    {
        return FirstLogonScriptGenerator.GenerateUserScript(plan)!.Text;
    }

    private static string MachineText(BuildPlan plan)
    {
        return FirstLogonScriptGenerator.GenerateMachineScript(plan)!.Text;
    }

    private static string[] StepLines(string script)
    {
        return [.. script.Split("\r\n").Where(l => l.StartsWith("Invoke-Plainstall", StringComparison.Ordinal))];
    }

    // ---- nothing to do ---------------------------------------------------------------------------

    [Fact]
    public void Returns_null_for_a_script_that_would_have_nothing_to_do()
    {
        var empty = Plan([]);
        Assert.Null(FirstLogonScriptGenerator.GenerateUserScript(empty));
        Assert.Null(FirstLogonScriptGenerator.GenerateMachineScript(empty));
        Assert.Equal(new FirstLogonScripts(null, null), FirstLogonScriptGenerator.Generate(empty));

        var offlineOnly = Plan([new PlannedAction("a", "A", new AppxRemoveProvisionedAction { Package = "Some.App" })]);
        Assert.Null(FirstLogonScriptGenerator.GenerateUserScript(offlineOnly));
        Assert.Null(FirstLogonScriptGenerator.GenerateMachineScript(offlineOnly));

        var userOnly = Plan([Set("Dark theme", PersonalizeKey, "AppsUseLightTheme", RegistryValueType.Dword, "0")]);
        Assert.NotNull(FirstLogonScriptGenerator.GenerateUserScript(userOnly));
        Assert.Null(FirstLogonScriptGenerator.GenerateMachineScript(userOnly));

        var machineCommandOnly = Plan([Command("T", FirstLogonScope.Machine, "S", "Get-Date")]);
        Assert.Null(FirstLogonScriptGenerator.GenerateUserScript(machineCommandOnly));
        Assert.NotNull(FirstLogonScriptGenerator.GenerateMachineScript(machineCommandOnly));

        var wingetOnly = Plan([], "Mozilla.Firefox");
        Assert.Null(FirstLogonScriptGenerator.GenerateUserScript(wingetOnly));
        Assert.NotNull(FirstLogonScriptGenerator.GenerateMachineScript(wingetOnly));
    }

    // ---- expected files --------------------------------------------------------------------------

    [Fact]
    public void User_script_matches_the_expected_file()
    {
        Expected.AssertMatches("user-script.ps1.txt", UserText(TypicalPlan()));
    }

    [Fact]
    public void Machine_script_matches_the_expected_file()
    {
        Expected.AssertMatches("machine-script.ps1.txt", MachineText(TypicalPlan()));
    }

    [Fact]
    public void Machine_script_without_apps_matches_the_expected_file()
    {
        var plan = Plan([
            Command("OneDrive", FirstLogonScope.Machine, "Remove the OneDrive setup entry", "Get-Date | Out-Null")
        ]);

        Expected.AssertMatches("machine-script-no-apps.ps1.txt", MachineText(plan));
    }

    // ---- what goes where -------------------------------------------------------------------------

    [Fact]
    public void User_script_holds_registry_actions_and_user_commands_in_plan_order()
    {
        var lines = StepLines(UserText(TypicalPlan()));

        // 15 steps from the plan, then the theme refresh, because the plan sets dark mode.
        Assert.Equal(16, lines.Length);
        Assert.StartsWith("Invoke-PlainstallStep 'Show the new colors at once'", lines[15]);
        Assert.Equal(
            $@"Invoke-PlainstallStep 'Dark theme: set HKCU\{PersonalizeKey}\AppsUseLightTheme' {{ Set-PlainstallRegistryValue '{PersonalizeKey}' 'AppsUseLightTheme' 'DWord' '0' }}",
            lines[0]);
        // The default value: an empty name.
        Assert.Equal(
            $@"Invoke-PlainstallStep 'Classic context menu: set HKCU\{ClassicMenuKey}\(Default)' {{ Set-PlainstallRegistryValue '{ClassicMenuKey}' '' 'String' '' }}",
            lines[1]);
        Assert.EndsWith(
            "{ Set-PlainstallRegistryValue 'Software\\Plainstall Test' 'Largest dword' 'DWord' '4294967295' }",
            lines[3]);
        Assert.EndsWith(
            "{ Set-PlainstallRegistryValue 'Software\\Plainstall Test' 'Largest qword' 'QWord' '18446744073709551615' }",
            lines[4]);
        Assert.EndsWith(
            @"{ Set-PlainstallRegistryValue 'Software\Plainstall Test' 'Expandable' 'ExpandString' '%USERPROFILE%\Desktop' }",
            lines[5]);
        Assert.EndsWith(
            "{ Set-PlainstallRegistryValue 'Software\\Plainstall Test' 'Several' 'MultiString' @('one', 'two words') }",
            lines[6]);
        Assert.EndsWith("{ Set-PlainstallRegistryValue 'Software\\Plainstall Test' 'None' 'MultiString' @() }",
            lines[7]);
        Assert.EndsWith("{ Set-PlainstallRegistryValue 'Software\\Plainstall Test' 'Bytes' 'Binary' '00FF10AB' }",
            lines[8]);
        Assert.EndsWith("{ Set-PlainstallRegistryValue 'Software\\Plainstall Test' 'No bytes' 'Binary' '' }", lines[9]);
        Assert.Equal(
            @"Invoke-PlainstallStep 'It''s a title with a quote: set HKCU\Software\Plainstall Test\It''s a name' { Set-PlainstallRegistryValue 'Software\Plainstall Test' 'It''s a name' 'String' 'It''s data' }",
            lines[10]);
        Assert.EndsWith("{ Remove-PlainstallRegistryValue 'Software\\Plainstall Test' 'Old value' }", lines[11]);
        Assert.EndsWith("{ Remove-PlainstallRegistryValue 'Software\\Plainstall Test' '' }", lines[12]);
        Assert.EndsWith(@"{ Remove-PlainstallRegistryKey 'Software\Plainstall Test\Old key' }", lines[13]);
        Assert.Equal(
            "Invoke-PlainstallCommand 'No Store suggestions in search: Block the Store suggestion database' '$d = Join-Path $env:LOCALAPPDATA ''Packages\\Example''; New-Item -ItemType Directory -Force -Path $d | Out-Null'",
            lines[14]);
    }

    [Fact]
    public void Machine_script_holds_machine_commands_then_the_apps()
    {
        var text = MachineText(TypicalPlan());
        var lines = StepLines(text);

        Assert.Equal(
            @"Invoke-PlainstallCommand 'OneDrive: Remove the OneDrive setup entry' 'Remove-ItemProperty -Path ''HKLM:\SOFTWARE\Example'' -Name ''OneDriveSetup'' -ErrorAction SilentlyContinue'",
            Assert.Single(lines));
        Assert.Contains("\r\n$plainstallApps = @('Mozilla.Firefox', '7zip.7zip', 'Notepad++.Notepad++')\r\n", text);
        Assert.True(text.IndexOf(lines[0], StringComparison.Ordinal) <
                    text.IndexOf("$plainstallApps = ", StringComparison.Ordinal));
        // The exact winget call: one exact id, silent, both agreements accepted, one named source.
        Assert.Contains(
            "@('install', '--id', $plainstallApp, '--exact', '--silent', '--accept-package-agreements', '--accept-source-agreements', '--source', $plainstallSource)",
            text);
    }

    [Fact]
    public void Scopes_do_not_leak_into_each_other()
    {
        var user = UserText(TypicalPlan());
        var machine = MachineText(TypicalPlan());

        Assert.DoesNotContain("OneDriveSetup", user);
        Assert.DoesNotContain("winget", user, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Mozilla", user);
        Assert.DoesNotContain("Packages\\Example", machine);
        Assert.DoesNotContain("RegistryValue", machine);
        Assert.DoesNotContain("CurrentUser", machine);
        foreach (var script in new[] { user, machine })
        {
            Assert.DoesNotContain("OfflineOnly", script);
            Assert.DoesNotContain("Offline.Only", script);
        }
    }

    [Fact]
    public void Machine_script_without_apps_has_no_winget_part()
    {
        var text = MachineText(Plan([Command("T", FirstLogonScope.Machine, "S", "Get-Date")]));

        // The header comment may name winget. The code that uses it must be gone.
        Assert.DoesNotContain("winget.exe", text);
        Assert.DoesNotContain("$plainstallApps", text);
        Assert.DoesNotContain("Start-Process", text);
        Assert.DoesNotContain("Add-AppxPackage", text);
        Assert.DoesNotContain("internet", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Start-Sleep", text);
    }

    [Fact]
    public void Scripts_log_run_once_and_end_without_waiting()
    {
        var user = UserText(TypicalPlan());
        var machine = MachineText(TypicalPlan());

        Assert.Contains("$plainstallLogFolder = Join-Path $env:LOCALAPPDATA 'Plainstall\\Logs'", user);
        Assert.Contains(@"$plainstallLogFolder = Join-Path $env:SystemRoot 'Setup\Scripts\Plainstall\Logs'", machine);
        // The folder the machine script logs to is the one the constant names.
        Assert.Contains("$env:SystemRoot '" + FirstLogonScriptGenerator.LogFolderImagePath[@"Windows\".Length..] + "'",
            machine);
        foreach (var script in new[] { user, machine })
        {
            // Run once: the marker is checked before any work and written after it.
            var check = script.IndexOf("if (Test-Path -LiteralPath $plainstallDone) {", StringComparison.Ordinal);
            var firstStep = script.IndexOf("\r\nInvoke-Plainstall", StringComparison.Ordinal);
            var mark = script.IndexOf("[System.IO.File]::WriteAllBytes($plainstallDone", StringComparison.Ordinal);
            Assert.True(check > 0 && check < firstStep && firstStep < mark);
            Assert.EndsWith("exit 0\r\n", script);
        }
    }

    [Fact]
    public void The_done_marker_is_an_empty_file_created_as_the_very_last_step()
    {
        Assert.Equal("Machine.done", FirstLogonScriptGenerator.MachineDoneFileName);

        var user = UserText(TypicalPlan());
        var machine = MachineText(TypicalPlan());

        // The machine marker in the script is the one the constant names.
        Assert.Contains(
            $"$plainstallDone = Join-Path $plainstallLogFolder '{FirstLogonScriptGenerator.MachineDoneFileName}'\r\n",
            machine);
        Assert.Contains("$plainstallDone = Join-Path $env:LOCALAPPDATA 'Plainstall\\User.done'\r\n", user);

        const string ending =
            "try {\r\n" +
            "    [void][System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($plainstallDone))\r\n" +
            "    [System.IO.File]::WriteAllBytes($plainstallDone, [byte[]]::new(0))\r\n" +
            "} catch {\r\n" +
            "    Write-PlainstallLog ('The marker file could not be written: ' + $_.Exception.Message)\r\n" +
            "}\r\n" +
            "exit 0\r\n";
        foreach (var script in new[]
                 {
                     user, machine,
                     // Also without apps, and with nothing but apps.
                     MachineText(Plan([Command("T", FirstLogonScope.Machine, "S", "Get-Date")])),
                     MachineText(Plan([], "Mozilla.Firefox"))
                 })
        {
            // Nothing but "exit" follows the marker, and nothing can skip it: the marker is written
            // once, outside every "if", after the last step and after the last log line.
            Assert.EndsWith(ending, script);
            Assert.Single(Regex.Matches(script, Regex.Escape("WriteAllBytes($plainstallDone")));
            var lastStep = script.LastIndexOf("Invoke-PlainstallStep", StringComparison.Ordinal);
            var finished = script.IndexOf("Write-PlainstallLog ('Finished.", StringComparison.Ordinal);
            var marker = script.IndexOf("WriteAllBytes($plainstallDone", StringComparison.Ordinal);
            Assert.True(lastStep < finished && finished < marker);
            // The only early way out is the "already done" check at the start.
            Assert.Equal(2, ExitLine().Count(script));
        }
    }

    [Theory]
    [InlineData(@"\bRestart-Computer\b")]
    [InlineData(@"\bStop-Computer\b")]
    [InlineData(@"\bshutdown\b")]
    [InlineData(@"\blogoff\b")]
    [InlineData("ExitWindows")]
    [InlineData(@"\bInvoke-WebRequest\b|\biwr\b|\bcurl\b|\bwget\b")]
    [InlineData(@"\bInvoke-RestMethod\b|\birm\b")]
    [InlineData("Start-BitsTransfer|bitsadmin")]
    [InlineData("WebClient|HttpClient|DownloadFile|DownloadString|https?://")]
    [InlineData(@"\bInvoke-Expression\b|\biex\b")]
    [InlineData(@"\bRead-Host\b|\bpause\b|ReadKey|ReadLine")]
    [InlineData("-EncodedCommand|FromBase64String")]
    public void Scripts_never_restart_download_or_wait_for_the_user(string forbiddenPattern)
    {
        // Not the typical plan: its catalog data names a setup program. Here only the template text can match.
        var scripts = FirstLogonScriptGenerator.Generate(Plan(
            [
                Set("Dark theme", PersonalizeKey, "AppsUseLightTheme", RegistryValueType.Dword, "0"),
                Command("T", FirstLogonScope.User, "S", "Get-Date"),
                Command("T", FirstLogonScope.Machine, "S", "Get-Date")
            ],
            "Mozilla.Firefox"));

        Assert.DoesNotMatch(new Regex(forbiddenPattern, RegexOptions.IgnoreCase), scripts.User!.Text);
        Assert.DoesNotMatch(new Regex(forbiddenPattern, RegexOptions.IgnoreCase), scripts.Machine!.Text);
    }

    [Fact]
    public void Waiting_is_always_bounded()
    {
        var text = MachineText(TypicalPlan());

        Assert.Contains($"$plainstallNetworkSeconds = {FirstLogonScriptGenerator.NetworkWaitSeconds}\r\n", text);
        Assert.Contains($"$plainstallWingetSeconds = {FirstLogonScriptGenerator.WingetWaitSeconds}\r\n", text);
        Assert.Contains($"$plainstallInstallSeconds = {FirstLogonScriptGenerator.InstallLimitSeconds}\r\n", text);
        Assert.Contains($"for up to {FirstLogonScriptGenerator.NetworkWaitSeconds / 60} minutes", text);
        // The only loop without a counter is the wait helper, and it has a deadline.
        Assert.Single(LoopKeyword().Matches(text));
        Assert.Contains("if ((Get-Date) -ge $plainstallDeadline) { return $false }", text);
        Assert.Contains("$plainstallProcess.WaitForExit($plainstallInstallSeconds * 1000)", text);
        Assert.DoesNotContain("@@", text);
        Assert.InRange(FirstLogonScriptGenerator.NetworkWaitSeconds, 30, 600);
        Assert.InRange(FirstLogonScriptGenerator.WingetWaitSeconds, 30, 600);
    }

    // ---- escaping --------------------------------------------------------------------------------

    /// <summary>
    ///     Every hostile string is used as key, value name, data, title, summary and command of a plan.
    ///     The real PowerShell parser must find no error, and the syntax tree must be the same as for
    ///     harmless text: the same commands in the same order, the same number of script blocks,
    ///     variables, assignments and every other kind of node. Text that had become code would show up
    ///     as a difference.
    /// </summary>
    [Fact]
    public void Hostile_text_never_changes_the_structure_of_a_script()
    {
        using var folder = new TempDirectory();
        var paths = new List<string>();

        Add("plain", PlanWithText("plain"));
        for (var i = 0; i < Hostile.Strings.Length; i++) Add("hostile" + i, PlanWithText(Hostile.Strings[i]));

        var parsed = WindowsPowerShell.Parse([.. paths]);

        var plainUser = parsed[0];
        var plainMachine = parsed[1];
        Assert.Empty(plainUser.Errors);
        Assert.Empty(plainMachine.Errors);
        // Five registry steps and one command, plus the one call inside Invoke-PlainstallCommand itself.
        Assert.Equal(7, plainUser.CommandNames.Count(n => n is "Invoke-PlainstallStep" or "Invoke-PlainstallCommand"));
        for (var i = 0; i < Hostile.Strings.Length; i++)
        {
            var label = $"hostile string #{i}";
            var user = parsed[2 + 2 * i];
            var machine = parsed[3 + 2 * i];
            Assert.True(user.Errors.Count == 0, $"{label}: {string.Join("; ", user.Errors)}");
            Assert.True(machine.Errors.Count == 0, $"{label}: {string.Join("; ", machine.Errors)}");
            Assert.True(plainUser.CommandNames.SequenceEqual(user.CommandNames),
                $"{label}: the user script calls different commands");
            Assert.True(plainMachine.CommandNames.SequenceEqual(machine.CommandNames),
                $"{label}: the machine script calls different commands");
            Assert.True(SameCounts(plainUser.StructureCounts, user.StructureCounts),
                $"{label}: the user script has a different structure");
            Assert.True(SameCounts(plainMachine.StructureCounts, machine.StructureCounts),
                $"{label}: the machine script has a different structure");
            Assert.DoesNotContain(Hostile.Marker, user.CommandNames);
            Assert.DoesNotContain("Write-Output", user.CommandNames);
            Assert.DoesNotContain("Write-Output", machine.CommandNames);
        }

        return;

        void Add(string label, BuildPlan plan)
        {
            var scripts = FirstLogonScriptGenerator.Generate(plan);
            paths.Add(folder.Write(label + "-user.ps1", scripts.User!.ToBytes()));
            paths.Add(folder.Write(label + "-machine.ps1", scripts.Machine!.ToBytes()));
        }

        static bool SameCounts(IReadOnlyDictionary<string, int> a, IReadOnlyDictionary<string, int> b)
        {
            return a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var count) && count == pair.Value);
        }
    }

    [Fact]
    public void Every_action_is_one_line_and_data_never_starts_a_line()
    {
        foreach (var hostile in Hostile.Strings)
        {
            var scripts = FirstLogonScriptGenerator.Generate(PlanWithText(hostile));

            Assert.Equal(6, StepLines(scripts.User!.Text).Length);
            Assert.Single(StepLines(scripts.Machine!.Text));
            foreach (var script in new[] { scripts.User.Text, scripts.Machine.Text })
            {
                Assert.DoesNotContain(script.Split("\r\n"),
                    line => line.TrimStart().StartsWith("Write-Output", StringComparison.Ordinal));
                Assert.DoesNotContain("\n", script.Replace("\r\n", "", StringComparison.Ordinal));
                Assert.DoesNotContain("\r", script.Replace("\r\n", "", StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void Titles_are_string_data_never_comments()
    {
        var plan = Plan([
            Set("Title\n# with a line break\nWrite-Output INJECTED", TestKey, "n", RegistryValueType.Dword, "1")
        ]);

        var text = UserText(plan);

        Assert.DoesNotContain(text.Split("\r\n"),
            line => line.StartsWith('#') && line.Contains("INJECTED", StringComparison.Ordinal));
        Assert.Contains(
            @"Invoke-PlainstallStep ('Title' + [char]0x000A + '# with a line break' + [char]0x000A + 'Write-Output INJECTED: set HKCU\Software\Plainstall Test\n') {",
            text);
    }

    [Fact]
    public void A_command_is_passed_as_text_and_compiled_inside_its_own_step()
    {
        // Unbalanced braces and a syntax error: pasted into the file as code, this would make the
        // whole script unreadable. As text it is one failed step.
        const string broken = "if ($true) { Write-Output 'never closed' ; } } ) '";
        var text = UserText(Plan(
        [
            Command("Broken", FirstLogonScope.User, "A command with a mistake", broken),
            Command("Fine", FirstLogonScope.User, "A command after it", "Get-Date")
        ]));

        Assert.Contains(
            "Invoke-PlainstallCommand 'Broken: A command with a mistake' 'if ($true) { Write-Output ''never closed'' ; } } ) '''\r\n",
            text);
        Assert.Contains("Invoke-PlainstallCommand 'Fine: A command after it' 'Get-Date'\r\n", text);
        Assert.Contains("Invoke-PlainstallStep $Title { & ([scriptblock]::Create($Code)) }", text);

        using var folder = new TempDirectory();
        var parsed = WindowsPowerShell.Parse(folder.Write("User.ps1", FirstLogonScriptGenerator.GenerateUserScript(Plan(
        [
            Command("Broken", FirstLogonScope.User, "A command with a mistake", broken),
            Command("Fine", FirstLogonScope.User, "A command after it", "Get-Date")
        ]))!.ToBytes()));
        Assert.Empty(parsed[0].Errors);
        Assert.DoesNotContain("Write-Output", parsed[0].CommandNames);
    }

    [Fact]
    public void A_multi_line_command_keeps_its_line_breaks()
    {
        var text = MachineText(Plan([Command("T", FirstLogonScope.Machine, "S", "$a = 1\r\nif ($a) {\n\t'x'\n}")]));

        Assert.Contains(
            "Invoke-PlainstallCommand 'T: S' ('$a = 1' + [char]0x000D + [char]0x000A + 'if ($a) {' + [char]0x000A + [char]0x0009 + '''x''' + [char]0x000A + '}')\r\n",
            text);
    }

    // ---- winget ids ------------------------------------------------------------------------------

    [Theory]
    [InlineData("Mozilla.Firefox")]
    [InlineData("7zip.7zip")]
    [InlineData("Notepad++.Notepad++")]
    [InlineData("Microsoft.VisualStudio.2022.Community")]
    [InlineData("a.b")]
    [InlineData("Foo_Bar.Baz-Qux.1")]
    [InlineData("9NKSQGP7F2NH")]
    [InlineData("XP89DCGQ3K6VLD")]
    public void Accepts_winget_and_store_ids_of_the_schema_shape(string id)
    {
        Assert.True(InstallIds.IsValid(id));
        Assert.Contains($"$plainstallApps = @('{id}')\r\n", MachineText(Plan([], id)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Firefox")]
    [InlineData(".Firefox")]
    [InlineData("Mozilla.")]
    [InlineData("Mozilla..Firefox")]
    [InlineData("Mozilla Firefox")]
    [InlineData("Mozilla.Fire fox")]
    [InlineData("Mozilla.Firefox ")]
    [InlineData("Mozilla.Firefox\n")]
    [InlineData("Mozilla.Firefox\r\nWrite-Output INJECTED")]
    [InlineData("Mozilla.Firefox; Write-Output INJECTED")]
    [InlineData("Mozilla.Firefox'; Write-Output INJECTED; '")]
    [InlineData("Mozilla.Firefox\"")]
    [InlineData("Mozilla.Firefox`")]
    [InlineData("$(Write-Output INJECTED).x")]
    [InlineData("Mozilla.Firefox|x")]
    [InlineData("Mozilla.Firefox&x")]
    [InlineData("--override.x")]
    [InlineData("-e.x")]
    [InlineData("Mozilla.Firefox --override /S")]
    [InlineData("Mozilla.Fir\u00E9fox")]
    [InlineData("Mozilla.Firefox\u2019")]
    [InlineData("Mozilla/Firefox.x")]
    [InlineData("Mozilla\\Firefox.x")]
    [InlineData("9nksqgp7f2nh")]
    [InlineData("9NKSQGP7F2N")]
    [InlineData("9NKSQGP7F2NHX")]
    [InlineData("XP89DCGQ3K6VL")]
    [InlineData("XP89DCGQ3K6VLDX")]
    [InlineData("AB89DCGQ3K6V")]
    [InlineData("9NKSQGP7F2NH\n")]
    [InlineData("9NKSQGP7F2NH; Write-Output INJECTED")]
    [InlineData("9NKSQGP7F2NÉ")]
    public void Refuses_anything_else_as_an_app_id(string id)
    {
        Assert.False(InstallIds.IsValid(id));

        var ex = Assert.Throws<PlainstallException>(() =>
            FirstLogonScriptGenerator.GenerateMachineScript(Plan([], "Mozilla.Firefox", id)));

        Assert.Equal(ErrorKind.InvalidInput, ex.Kind);
        Assert.DoesNotContain("INJECTED", ex.Message);
        Assert.Throws<PlainstallException>(() => FirstLogonScriptGenerator.Generate(Plan([], id)));
    }

    [Fact]
    public void Winget_id_length_is_limited_like_in_the_schema()
    {
        var longest = "A." + new string('b', InstallIds.MaxLength - 2);

        Assert.True(InstallIds.IsValid(longest));
        Assert.False(InstallIds.IsValid(longest + "b"));
        Assert.False(InstallIds.IsValid(null));
    }

    [Fact]
    public void The_same_app_is_installed_once()
    {
        var text = MachineText(Plan([], "Mozilla.Firefox", "7zip.7zip", "mozilla.firefox", "Mozilla.Firefox"));

        Assert.Contains("$plainstallApps = @('Mozilla.Firefox', '7zip.7zip')\r\n", text);
    }

    [Fact]
    public void The_script_checks_each_id_again_with_the_same_pattern()
    {
        var text = MachineText(Plan([], "Mozilla.Firefox"));

        Assert.Contains(@"$plainstallAppPattern = '\A[A-Za-z0-9][A-Za-z0-9+_\-]*(\.[A-Za-z0-9+_\-]+)+\z'" + "\r\n",
            text);
        Assert.Contains(@"$plainstallStorePattern = '\A(9[A-Z0-9]{11}|XP[A-Z0-9]{12})\z'" + "\r\n", text);
        Assert.Contains("if ($plainstallApp -cmatch $plainstallStorePattern) {", text);
        Assert.Contains("} elseif ($plainstallApp -cmatch $plainstallAppPattern) {", text);
    }

    [Fact]
    public void Store_ids_use_the_msstore_source_and_every_other_id_the_winget_source()
    {
        // The patterns exactly as the script holds them, run by Windows PowerShell 5.1 with the same
        // -cmatch checks the script makes. A Store id never contains a dot, so no id matches both.
        var ids = new[]
            { "9NKSQGP7F2NH", "XP89DCGQ3K6VLD", "Mozilla.Firefox", "Notepad++.Notepad++", "9nksqgp7f2nh", "Firefox" };
        var harness = new StringBuilder();
        harness.Append("$plainstallAppPattern = ").AppendLine(PowerShellText.Literal(InstallIds.WingetPatternText));
        harness.Append("$plainstallStorePattern = ").AppendLine(PowerShellText.Literal(InstallIds.StorePatternText));
        harness.Append("foreach ($plainstallApp in ").Append(PowerShellText.ArrayLiteral(ids)).AppendLine(") {");
        harness.AppendLine("""
                               if ($plainstallApp -cmatch $plainstallStorePattern) {
                                   'msstore'
                               } elseif ($plainstallApp -cmatch $plainstallAppPattern) {
                                   'winget'
                               } else {
                                   'skipped'
                               }
                           }
                           """);

        var sources = WindowsPowerShell.Run(harness.ToString());

        Assert.Equal(["msstore", "msstore", "winget", "winget", "skipped", "skipped"], sources);
        Assert.Equal(sources.Select(s => s != "skipped"), ids.Select(InstallIds.IsValid));
    }

    // ---- refused actions -------------------------------------------------------------------------

    [Fact]
    public void Refuses_registry_actions_that_cannot_be_written()
    {
        var noData = new PlannedAction("e", "E",
            new FirstLogonRegistryAction
                { Op = FirstLogonRegistryOp.Set, Key = TestKey, Name = "n", Type = RegistryValueType.Dword });
        var noType = new PlannedAction("e", "E",
            new FirstLogonRegistryAction
                { Op = FirstLogonRegistryOp.Set, Key = TestKey, Name = "n", Data = Json("1") });
        var noName = new PlannedAction("e", "E",
            new FirstLogonRegistryAction
                { Op = FirstLogonRegistryOp.Set, Key = TestKey, Type = RegistryValueType.Dword, Data = Json("1") });
        var wrongData = Set("E", TestKey, "n", RegistryValueType.Dword, "\"one\"");
        var negative = Set("E", TestKey, "n", RegistryValueType.Dword, "-1");
        var oddHex = Set("E", TestKey, "n", RegistryValueType.Binary, "\"ABC\"");
        var notHex = Set("E", TestKey, "n", RegistryValueType.Binary, "\"'; Write-Output INJECTED; '\"");
        var emptyKey = Delete("E", "", null);
        var blankKey = Delete("E", "  ", null);
        var rootedKey = Delete("E", @"\Software\X", null);
        var trailingKey = Set("E", @"Software\X\", "n", RegistryValueType.Dword, "1");

        foreach (var bad in new[]
                 {
                     noData, noType, noName, wrongData, negative, oddHex, notHex, emptyKey, blankKey, rootedKey,
                     trailingKey
                 })
        {
            var ex = Assert.Throws<PlainstallException>(() =>
                FirstLogonScriptGenerator.GenerateUserScript(Plan([bad])));
            Assert.Equal(ErrorKind.InvalidInput, ex.Kind);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    public void Refuses_an_empty_command(string command)
    {
        Assert.Throws<PlainstallException>(() =>
            FirstLogonScriptGenerator.GenerateUserScript(Plan([Command("T", FirstLogonScope.User, "S", command)])));
        Assert.Throws<PlainstallException>(() =>
            FirstLogonScriptGenerator.GenerateMachineScript(Plan([
                Command("T", FirstLogonScope.Machine, "S", command)
            ])));
    }

    // ---- encoding --------------------------------------------------------------------------------

    [Fact]
    public void Bytes_are_utf8_with_a_byte_order_mark_and_the_text_is_ascii_with_crlf()
    {
        var plan = Plan(
            [
                Set("Th\u00E8me sombre \u65E5\u672C\u8A9E \U0001F600", TestKey + "\\Gr\u00F6\u00DFe", "Stra\u00DFe",
                    RegistryValueType.String, "\"\u00C5ngstr\u00F6m \u2019quoted\u2019\""),
                Command("Caf\u00E9", FirstLogonScope.User, "R\u00E9sum\u00E9", "Write-Verbose 'na\u00EFve'"),
                Command("Caf\u00E9", FirstLogonScope.Machine, "R\u00E9sum\u00E9", "Write-Verbose 'na\u00EFve'")
            ],
            "Mozilla.Firefox");

        foreach (var script in new[]
                 {
                     FirstLogonScriptGenerator.GenerateUserScript(plan)!,
                     FirstLogonScriptGenerator.GenerateMachineScript(plan)!
                 })
        {
            var bytes = script.ToBytes();

            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
            Assert.All(bytes[3..], b => Assert.True(b is (byte)'\r' or (byte)'\n' or >= 0x20 and <= 0x7E));
            Assert.Equal(script.Text, Encoding.ASCII.GetString(bytes, 3, bytes.Length - 3));
            Assert.DoesNotContain("\n", script.Text.Replace("\r\n", "", StringComparison.Ordinal));
            Assert.EndsWith("\r\n", script.Text);
            Assert.DoesNotContain("\t", script.Text);
            // Characters outside ASCII are still there, as code points.
            Assert.Contains("('Caf' + [char]0x00E9 + ': R' + [char]0x00E9 + 'sum' + [char]0x00E9)", script.Text);
            Assert.Contains("('Write-Verbose ''na' + [char]0x00EF + 've''')", script.Text);
        }

        var user = FirstLogonScriptGenerator.GenerateUserScript(plan)!.Text;
        Assert.Contains(
            "('Th' + [char]0x00E8 + 'me sombre ' + [char]0x65E5 + [char]0x672C + [char]0x8A9E + ' ' + [char]0xD83D + [char]0xDE00 + ': set HKCU",
            user);
        Assert.Contains("('Stra' + [char]0x00DF + 'e')", user);
        Assert.Contains("('' + [char]0x00C5 + 'ngstr' + [char]0x00F6 + 'm ' + [char]0x2019 + 'quoted' + [char]0x2019)",
            user);
    }

    [Fact]
    public void To_bytes_refuses_text_that_is_not_ascii()
    {
        var ex = Assert.Throws<PlainstallException>(() =>
            new GeneratedScript(FirstLogonScriptGenerator.UserScriptImagePath, "Write-Host 'caf\u00E9'\r\n").ToBytes());

        Assert.Equal(ErrorKind.Internal, ex.Kind);
    }

    // ---- where the scripts live and how they are started -----------------------------------------

    [Fact]
    public void Paths_inside_the_image_are_fixed()
    {
        Assert.Equal(@"Windows\Setup\Scripts\Plainstall", FirstLogonScriptGenerator.ImageFolder);
        Assert.Equal(@"Windows\Setup\Scripts\Plainstall\Machine.ps1", FirstLogonScriptGenerator.MachineScriptImagePath);
        Assert.Equal(@"Windows\Setup\Scripts\Plainstall\User.ps1", FirstLogonScriptGenerator.UserScriptImagePath);
        Assert.Equal(@"Windows\Setup\Scripts\Plainstall\Logs", FirstLogonScriptGenerator.LogFolderImagePath);

        var scripts = FirstLogonScriptGenerator.Generate(TypicalPlan());
        Assert.Equal(FirstLogonScriptGenerator.MachineScriptImagePath, scripts.Machine!.ImageRelativePath);
        Assert.Equal(FirstLogonScriptGenerator.UserScriptImagePath, scripts.User!.ImageRelativePath);
    }

    [Fact]
    public void Command_lines_start_the_right_script_and_respect_the_windows_limits()
    {
        Assert.Equal(
            "powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command \"& ($env:SystemRoot + '\\Setup\\Scripts\\Plainstall\\Machine.ps1')\"",
            FirstLogonScriptGenerator.MachineCommandLine);
        Assert.Equal(
            "powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"& ($env:SystemRoot + '\\Setup\\Scripts\\Plainstall\\User.ps1')\"",
            FirstLogonScriptGenerator.RunOnceValueData);

        // FirstLogonCommands: at most 1024 characters. RunOnce: at most 260.
        Assert.InRange(FirstLogonScriptGenerator.MachineCommandLine.Length, 1, 1024);
        Assert.InRange(FirstLogonScriptGenerator.RunOnceValueData.Length, 1,
            FirstLogonScriptGenerator.RunOnceMaxLength);
        Assert.Equal(260, FirstLogonScriptGenerator.RunOnceMaxLength);

        // The script paths in the command lines are the paths the files are written to.
        Assert.Contains(FirstLogonScriptGenerator.MachineScriptImagePath["Windows".Length..],
            FirstLogonScriptGenerator.MachineCommandLine);
        Assert.Contains(FirstLogonScriptGenerator.UserScriptImagePath["Windows".Length..],
            FirstLogonScriptGenerator.RunOnceValueData);

        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\RunOnce", FirstLogonScriptGenerator.RunOnceKey);
        Assert.False(FirstLogonScriptGenerator.RunOnceValueName.StartsWith('!'));
        Assert.Matches(@"\A[A-Za-z]+\z", FirstLogonScriptGenerator.RunOnceValueName);
    }

    [Fact]
    public void Run_once_action_targets_the_default_users_hive()
    {
        var action = FirstLogonScriptGenerator.CreateRunOnceAction();

        Assert.Equal(RegistryHiveId.DefaultUser, action.Hive);
        Assert.Equal(FirstLogonScriptGenerator.RunOnceKey, action.Key);
        Assert.Equal(FirstLogonScriptGenerator.RunOnceValueName, action.Name);
        Assert.Equal(RegistryValueType.String, action.Type);
        Assert.Equal(FirstLogonScriptGenerator.RunOnceValueData, RegistryData.ToTypedValue(action.Type, action.Data));
        Assert.False(action.IfKeyExists);
    }

    // ---- the real parser -------------------------------------------------------------------------

    /// <summary>
    ///     Every kind of script the generator can produce is read by the parser of Windows PowerShell 5.1
    ///     from the exact bytes that would be written into the image. Parsing does not run anything.
    ///     The typical scripts call only the commands on the allow list, read from the syntax tree. Nothing
    ///     on the list restarts or shuts down, downloads, or waits for a key. Add a new template command to
    ///     the list on purpose.
    /// </summary>
    [Fact]
    public void Every_generated_script_is_valid_windows_powershell()
    {
        using var folder = new TempDirectory();
        var paths = new List<string>();

        var typical = FirstLogonScriptGenerator.Generate(TypicalPlan());
        Add("typical-user.ps1", typical.User);
        Add("typical-machine.ps1", typical.Machine);
        Add("commands-only-machine.ps1",
            FirstLogonScriptGenerator.GenerateMachineScript(Plan([
                Command("T", FirstLogonScope.Machine, "S", "Get-Date")
            ])));
        Add("apps-only-machine.ps1",
            FirstLogonScriptGenerator.GenerateMachineScript(Plan([], "Mozilla.Firefox", "Notepad++.Notepad++")));
        Add("registry-only-user.ps1", FirstLogonScriptGenerator.GenerateUserScript(Plan([Delete("T", TestKey, null)])));
        Add("command-only-user.ps1",
            FirstLogonScriptGenerator.GenerateUserScript(Plan([Command("T", FirstLogonScope.User, "S", "a\r\nb")])));
        var commandLinesStart = paths.Count;
        // The PowerShell part of the two command lines, between the outer quotes.
        foreach (var commandLine in new[]
                     { FirstLogonScriptGenerator.MachineCommandLine, FirstLogonScriptGenerator.RunOnceValueData })
        {
            var inner = commandLine[(commandLine.IndexOf('"') + 1)..commandLine.LastIndexOf('"')];
            Assert.DoesNotContain("\"", inner);
            paths.Add(folder.Write($"command-line-{paths.Count}.ps1", Encoding.ASCII.GetBytes(inner)));
        }

        var parsed = WindowsPowerShell.Parse([.. paths]);

        Assert.Equal(paths.Count, parsed.Count);
        for (var i = 0; i < parsed.Count; i++)
        {
            Assert.True(parsed[i].Errors.Count == 0,
                $"{Path.GetFileName(paths[i])}: {string.Join("; ", parsed[i].Errors)}");
            Assert.NotEmpty(parsed[i].CommandNames);
        }

        Assert.Equal(UserCommands.Order(StringComparer.Ordinal),
            parsed[0].CommandNames.Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(MachineCommands.Order(StringComparer.Ordinal),
            parsed[1].CommandNames.Distinct().Order(StringComparer.Ordinal));
        // A command line is one call of a path that is worked out when it runs, and nothing else.
        Assert.Equal(["<dynamic>"], parsed[commandLinesStart].CommandNames);
        Assert.Equal(["<dynamic>"], parsed[commandLinesStart + 1].CommandNames);
        return;

        void Add(string name, GeneratedScript? script)
        {
            Assert.NotNull(script);
            paths.Add(folder.Write(name, script.ToBytes()));
        }
    }

    // ---- the password ----------------------------------------------------------------------------

    [Fact]
    public void No_script_can_contain_the_account_password()
    {
        const string password = "Correct horse 7!";
        var options = new SetupOptions
        {
            LocalAccount = new LocalAccountOptions { Name = "Anna", AskPassword = true },
            SkipPrivacyPages = true,
            BypassHardwareChecks = false
        };
        var host = new HostFacts("en-US", "en-US", "en-US", "UTC");
        var scripts = FirstLogonScriptGenerator.Generate(TypicalPlan());
        var input = UnattendInput.FromProfile(options, SetupResolver.Resolve(options, host, "en-US"),
            scripts.Machine is not null);
        var answerFile = UnattendGenerator.ToXmlString(UnattendGenerator.Generate(input, password));
        var encoded = UnattendGenerator.EncodePassword(password);

        // The answer file carries the password, in its hidden form. Nothing else does.
        Assert.Contains(encoded, answerFile);
        foreach (var text in new[]
                 {
                     scripts.User!.Text, scripts.Machine!.Text, Encoding.UTF8.GetString(scripts.User.ToBytes()),
                     Encoding.UTF8.GetString(scripts.Machine.ToBytes()),
                     FirstLogonScriptGenerator.MachineCommandLine, FirstLogonScriptGenerator.RunOnceValueData
                 })
        {
            Assert.DoesNotContain(password, text);
            Assert.DoesNotContain(encoded, text);
            Assert.DoesNotContain("Anna", text);
            Assert.DoesNotContain("Password", text, StringComparison.OrdinalIgnoreCase);
        }

        // The generator has no way to receive one: its only input is the plan.
        Assert.All(
            typeof(FirstLogonScriptGenerator).GetMethods()
                .Where(m => m.IsPublic && m.DeclaringType == typeof(FirstLogonScriptGenerator)),
            m => Assert.DoesNotContain(m.GetParameters(),
                p => p.Name!.Contains("password", StringComparison.OrdinalIgnoreCase)));
    }
}