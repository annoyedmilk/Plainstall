using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Plainstall.Core.Unattend;

namespace Plainstall.Core.Tests.Unattend;

/// <summary>Expected-output files under TestData\Unattend, copied next to the test binaries.</summary>
internal static class Expected
{
    /// <summary>
    ///     Set this environment variable to a folder (normally the repo's
    ///     tests\Plainstall.Core.Tests\TestData\Unattend) and run the tests to write the current output
    ///     there instead of comparing. Then review the diff by hand before committing it.
    /// </summary>
    private const string WriteVariable = "PLAINSTALL_WRITE_EXPECTED";

    public static string PathOf(string name)
    {
        return Path.Combine(AppContext.BaseDirectory, "TestData", "Unattend", name);
    }

    /// <summary>
    ///     Compares an expected file with the generated text. Scripts are compared exactly, apart from the
    ///     line-end style, which a checkout may change. Answer files are compared as XML: elements,
    ///     attributes, values and comments must be the same, while the indentation and the order of the
    ///     attributes in the file may differ, so reformatting the expected file in an editor does not break
    ///     the test. That the generator's own bytes never change is checked separately, by the pinned hashes
    ///     in UnattendGeneratorTests.
    /// </summary>
    public static void AssertMatches(string fileName, string actual)
    {
        if (Environment.GetEnvironmentVariable(WriteVariable) is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, fileName), actual, new UTF8Encoding(false));
            return;
        }

        var path = PathOf(fileName);
        Assert.True(File.Exists(path), $"Expected file is missing: {path}");
        var expected = File.ReadAllText(path);
        if (fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Equal(CanonicalXml(expected), CanonicalXml(actual));
        }
        else
        {
            Assert.Equal(Normalize(expected), Normalize(actual));
        }
    }

    public static string Normalize(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>
    ///     The document without its own whitespace between elements and with the attributes of every element
    ///     sorted by name, written out with one fixed indentation.
    /// </summary>
    private static string CanonicalXml(string text)
    {
        var document = XDocument.Parse(text);
        foreach (var element in document.Descendants())
        {
            element.ReplaceAttributes(element.Attributes().OrderBy(a => a.Name.ToString(), StringComparer.Ordinal)
                .ToList());
        }

        return Normalize(document.ToString(SaveOptions.None));
    }
}

/// <summary>
///     Strings that try to break out of a PowerShell string literal. Every payload is harmless by
///     design (it would only print the word INJECTED), so a failing test can never damage the PC it runs on.
/// </summary>
internal static class Hostile
{
    public const string Marker = "INJECTED";

    public static readonly string[] Strings =
    [
        "'; Write-Output INJECTED; '",
        "''; Write-Output INJECTED; ''",
        "\"; Write-Output INJECTED; \"",
        "$(Write-Output INJECTED)",
        "\"$(Write-Output INJECTED)\"",
        "${env:USERNAME}$env:USERNAME %SystemRoot%",
        "`'; Write-Output INJECTED; `'",
        "a`nb`tc``d`0",
        "line one\nWrite-Output INJECTED",
        "line one\r\nWrite-Output INJECTED\r\n",
        "\rWrite-Output INJECTED",
        "tab\there; Write-Output INJECTED",
        "x; Write-Output INJECTED",
        "x | Write-Output INJECTED & Write-Output INJECTED",
        "x) ; Write-Output INJECTED ; (",
        "x } ; Write-Output INJECTED ; {",
        "'@\nWrite-Output INJECTED\n@'",
        "\"@\nWrite-Output INJECTED\n@\"",
        "#> Write-Output INJECTED <#",
        "# Write-Output INJECTED",
        "--% Write-Output INJECTED",
        "\u2018; Write-Output INJECTED; \u2018",
        "\u2019; Write-Output INJECTED; \u2019",
        "\u201A; Write-Output INJECTED; \u201B",
        "\u201C; Write-Output INJECTED; \u201D",
        "\u201E$(Write-Output INJECTED)\u201E",
        "\u2028Write-Output INJECTED\u2029Write-Output INJECTED",
        "\u0085Write-Output INJECTED",
        "nul\0Write-Output INJECTED",
        "\e[2J\a\u007F",
        "caf\u00E9 \u00FC\u00DF \u65E5\u672C\u8A9E \U0001F600",
        "\uD83D lone surrogate",
        "\\",
        "ends with backslash\\",
        "'",
        "''",
        "'''",
        " ",
        "",
        "-EncodedCommand",
        "@('a'; Write-Output INJECTED)",
        "{0} {1} -f Write-Output INJECTED",
        "[char]0x41 + $(Write-Output INJECTED)"
    ];
}

internal static class ScriptFiles
{
    /// <summary>Writes a file into the temp folder and returns its path.</summary>
    public static string Write(this TempDirectory folder, string name, byte[] bytes)
    {
        var path = folder.File(name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}

/// <summary>What the real PowerShell parser says about one script file.</summary>
internal sealed record ParsedScript(
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> CommandNames,
    IReadOnlyDictionary<string, int> StructureCounts);

/// <summary>
///     Runs Windows PowerShell 5.1, the engine the generated scripts are written for.
///     The generated scripts themselves are only ever parsed here, never executed.
/// </summary>
internal static class WindowsPowerShell
{
    public static readonly string ExePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0",
        "powershell.exe");

    /// <summary>
    ///     Parses each file with <c>Parser.ParseFile</c> and reports syntax errors plus a summary of the
    ///     syntax tree: every command name (in order of appearance) and how often each kind of node occurs.
    ///     Nodes that only make up string literals are left out of the counts, so two scripts that differ
    ///     only in their data have equal summaries. Code smuggled in through data would change them.
    /// </summary>
    public static IReadOnlyList<ParsedScript> Parse(params string[] scriptPaths)
    {
        var harness = new StringBuilder();
        harness.AppendLine("$paths = " + PowerShellText.ArrayLiteral(scriptPaths));
        harness.AppendLine("""
                           $literalKinds = @('StringConstantExpressionAst', 'ConstantExpressionAst', 'BinaryExpressionAst', 'ConvertExpressionAst',
                               'TypeConstraintAst', 'ParenExpressionAst', 'PipelineAst', 'CommandExpressionAst')
                           foreach ($path in $paths) {
                               $tokens = $null
                               $errors = $null
                               $ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
                               $nodes = $ast.FindAll({ $true }, $true)
                               $names = @($nodes | Where-Object { $_ -is [System.Management.Automation.Language.CommandAst] } | ForEach-Object {
                                   $name = $_.GetCommandName()
                                   if ($null -eq $name) { '<dynamic>' } else { $name }
                               })
                               $counts = @{}
                               foreach ($node in $nodes) {
                                   $kind = $node.GetType().Name
                                   if ($literalKinds -notcontains $kind) { $counts[$kind] = 1 + [int]$counts[$kind] }
                               }
                               $result = [ordered]@{
                                   errors = @($errors | ForEach-Object { '{0} (line {1})' -f $_.Message, $_.Extent.StartLineNumber })
                                   commands = $names
                                   counts = $counts
                               }
                               '@@' + [System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes(($result | ConvertTo-Json -Compress -Depth 4)))
                           }
                           """);

        var lines = Run(harness.ToString()).Where(l => l.StartsWith("@@", StringComparison.Ordinal)).ToList();
        Assert.Equal(scriptPaths.Length, lines.Count);

        return
        [
            .. lines.Select(line =>
            {
                using var json = JsonDocument.Parse(Convert.FromBase64String(line[2..]));
                var root = json.RootElement;
                return new ParsedScript(
                    [.. root.GetProperty("errors").EnumerateArray().Select(e => e.GetString()!)],
                    [.. root.GetProperty("commands").EnumerateArray().Select(e => e.GetString()!)],
                    root.GetProperty("counts").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32()));
            })
        ];
    }

    /// <summary>
    ///     Runs test harness code and returns the lines it printed. The code is saved to a file and loaded
    ///     through a tiny <c>-EncodedCommand</c> bootstrap rather than started as a script file, so the
    ///     test does not depend on the execution policy of the PC it runs on.
    /// </summary>
    public static IReadOnlyList<string> Run(string harnessCode)
    {
        if (!File.Exists(ExePath)) Assert.Skip($"Windows PowerShell 5.1 was not found at {ExePath}.");

        using var folder = new TempDirectory();
        var harnessPath = folder.Write("harness.txt",
            [.. new UTF8Encoding(true).GetPreamble(), .. Encoding.UTF8.GetBytes(harnessCode)]);
        var bootstrap =
            "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'; " +
            "& ([scriptblock]::Create([System.IO.File]::ReadAllText(" + PowerShellText.Literal(harnessPath) + ")))";

        var start = new ProcessStartInfo(ExePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-OutputFormat");
        start.ArgumentList.Add("Text");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap)));

        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            process.Kill(true);
            Assert.Fail("Windows PowerShell did not finish within two minutes.");
        }

        process.WaitForExit();

        var errorText = stderr.GetAwaiter().GetResult();
        var outputText = stdout.GetAwaiter().GetResult();
        // Only the exit code decides. Windows PowerShell may print progress records to the error
        // stream on its first start, and those are not failures.
        Assert.True(process.ExitCode == 0,
            $"Windows PowerShell failed (exit code {process.ExitCode}).{Environment.NewLine}{errorText}{Environment.NewLine}{outputText}");

        return [.. outputText.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0)];
    }
}