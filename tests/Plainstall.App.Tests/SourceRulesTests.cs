using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Plainstall.App.Resources;
using Plainstall.App.Services;
using Plainstall.App.ViewModels;
using Plainstall.Core.Catalog;
using Plainstall.Core.Profiles;

namespace Plainstall.App.Tests;

/// <summary>Rules about the app's sources that a reviewer would otherwise have to check by hand.</summary>
public partial class SourceRulesTests
{
    private static string SourceRoot => Path.Combine(AppContext.BaseDirectory, "AppSources");

    private static IEnumerable<string> SourceFiles(params string[] patterns)
    {
        return patterns.SelectMany(p => Directory.EnumerateFiles(SourceRoot, p, SearchOption.AllDirectories));
    }

    [Fact]
    public void Every_resource_string_has_a_property_and_every_property_a_string()
    {
        var resx = XDocument.Load(Path.Combine(SourceRoot, "Resources", "Strings.resx"));
        var keys = resx.Root!.Elements("data").Select(d => (string)d.Attribute("name")!).ToHashSet();
        var properties = typeof(Strings).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(string)).Select(p => p.Name).ToHashSet();
        Assert.Empty(keys.Except(properties));
        Assert.Empty(properties.Except(keys));
        Assert.All(properties,
            p => Assert.False(string.IsNullOrWhiteSpace((string?)typeof(Strings).GetProperty(p)!.GetValue(null)), p));
    }

    [Fact]
    public void Every_string_used_in_xaml_exists()
    {
        var used = SourceFiles("*.xaml")
            .SelectMany(f => XamlStatic().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)).Distinct();
        foreach (var key in used) Assert.True(typeof(Strings).GetProperty(key) is not null, key);
    }

    [Fact]
    public void Xaml_has_no_literal_user_visible_text()
    {
        string[] textAttributes =
        [
            "Text", "Content", "Header", "ToolTip", "Title", "PlaceholderText", "OnContent", "OffContent",
            "AutomationProperties.Name", "AutomationProperties.HelpText"
        ];
        var problems = new List<string>();
        foreach (var file in SourceFiles("*.xaml"))
        {
            var doc = XDocument.Load(file);
            foreach (var element in doc.Descendants())
            {
                problems.AddRange(element.Attributes()
                    .Where(a => textAttributes.Contains(a.Name.LocalName) && !a.Value.StartsWith('{') &&
                                a.Value.Any(char.IsLetter))
                    .Select(a =>
                        $"{Path.GetFileName(file)}: {element.Name.LocalName}.{a.Name.LocalName}=\"{a.Value}\""));
                if (!element.HasElements && element.Value.Any(char.IsLetter) &&
                    element.Name.LocalName is "TextBlock" or "Run" or "Button")
                    problems.Add($"{Path.GetFileName(file)}: text inside {element.Name.LocalName}");
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void Interactive_elements_have_an_automation_name()
    {
        string[] interactive =
        [
            "Button", "ToggleSwitch", "CheckBox", "RadioButton", "ComboBox", "TextBox", "PasswordBox", "ListBox",
            "ToggleButton", "HyperlinkButton", "Expander", "TabItem"
        ];
        // An attribute named AutomationProperties.Name counts in any namespace, so only the local name is compared.
        var missing = SourceFiles("*.xaml")
            .SelectMany(file => XDocument.Load(file).Descendants()
                .Where(e => interactive.Contains(e.Name.LocalName) &&
                            e.Attributes().All(a => a.Name.LocalName != "AutomationProperties.Name"))
                .Select(e =>
                    $"{Path.GetFileName(file)}: {e.Name.LocalName} {(string?)e.Attribute("Content") ?? (string?)e.Attribute("Command")}"))
            .ToList();
        Assert.Empty(missing);
    }

    /// <summary>Checks every source file, test file, test data file and script under src and tests.</summary>
    [Fact]
    public void No_em_dash_anywhere()
    {
        var root = RepoRoot();
        string[] folders = ["src", "tests"];
        string[] patterns = ["*.cs", "*.xaml", "*.resx", "*.json", "*.csproj", "*.manifest", "*.ps1", "*.xml", "*.txt"];
        var offenders = folders
            .SelectMany(folder => patterns.SelectMany(p =>
                Directory.EnumerateFiles(Path.Combine(root, folder), p, SearchOption.AllDirectories)))
            .Select(f => Path.GetRelativePath(root, f))
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Where(f => File.ReadAllText(Path.Combine(root, f)).Contains('\u2014'))
            .ToList();
        Assert.Empty(offenders);
    }

    /// <summary>The folder of Plainstall.slnx, above the folder of the test binaries.</summary>
    private static string RepoRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "Plainstall.slnx")))
                return folder.FullName;
        throw new InvalidOperationException("Plainstall.slnx was not found above the test binaries.");
    }

    [Fact]
    public void Nothing_mentions_arm64()
    {
        var offenders = SourceFiles("*.cs", "*.xaml", "*.resx", "*.json", "*.csproj")
            .Where(f => File.ReadAllText(f).Contains("arm64", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void View_models_use_no_wpf_types()
    {
        string[] wpfAssemblies = ["PresentationFramework", "PresentationCore", "WindowsBase", "Wpf.Ui", "System.Xaml"];
        var problems = new List<string>();
        var types = typeof(MainViewModel).Assembly.GetTypes()
            .Where(t => t.Namespace is "Plainstall.App.ViewModels" or "Plainstall.App.Services" && !IsWpfService(t));
        foreach (var type in types)
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                     BindingFlags.Static | BindingFlags.DeclaredOnly;
            var used = type.GetFields(all).Select(f => f.FieldType)
                .Concat(type.GetProperties(all).Select(p => p.PropertyType))
                .Concat(type.GetMethods(all)
                    .SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)))
                .Concat(type.GetConstructors(all).SelectMany(c => c.GetParameters().Select(p => p.ParameterType)))
                .Append(type.BaseType ?? typeof(object));
            problems.AddRange(used.SelectMany(Flatten)
                .Where(t => wpfAssemblies.Contains(t.Assembly.GetName().Name))
                .Select(t => $"{type.Name} uses {t.FullName}"));
        }

        Assert.Empty(problems.Distinct());
        return;

        static bool IsWpfService(Type t)
        {
            return t == typeof(WpfDispatcher) || t == typeof(DialogService) || t == typeof(ShellService);
        }

        static IEnumerable<Type> Flatten(Type t)
        {
            // Arrays, pointers and references count as the type they hold.
            while (t.HasElementType) t = t.GetElementType()!;
            return t.IsGenericType ? t.GetGenericArguments().SelectMany(Flatten).Append(t) : [t];
        }
    }

    [Fact]
    public void Release_asks_for_administrator_rights_and_debug_does_not()
    {
        var project = XDocument.Load(Path.Combine(SourceRoot, "Plainstall.App.csproj"));
        var manifests = project.Descendants("ApplicationManifest").ToList();
        Assert.Contains(manifests,
            m => ((string?)m.Attribute("Condition"))!.Contains("== 'Release'") && m.Value == "app.manifest");
        Assert.Contains("requireAdministrator", File.ReadAllText(Path.Combine(SourceRoot, "app.manifest")));
        Assert.Contains("asInvoker", File.ReadAllText(Path.Combine(SourceRoot, "app.Debug.manifest")));
    }

    [Fact]
    public void Boot_keys_are_data()
    {
        var keys = BootKeyData.Load();
        Assert.True(keys.Count >= 10);
        Assert.All(keys, k => Assert.NotEmpty(k.BootMenu));
    }

    [Fact]
    public void Settings_never_hold_a_password_and_survive_a_round_trip()
    {
        using var temp = new TempFolder();
        var store = new SettingsStore(new ProfileSerializer(), NullLogger<SettingsStore>.Instance, temp.Path);
        Assert.Equal(new AppSettings(), store.Load());
        Assert.Null(store.LoadLastProfile());

        store.Save(new AppSettings { Theme = ThemeChoice.Dark, WorkFolder = @"E:\Work", LastIsoPath = @"E:\w.iso" });
        var profile = ProfileFactory.FromPreset(CatalogLoader.LoadEmbedded().DefaultPreset);
        profile = profile with
        {
            Setup = profile.Setup with
            {
                LocalAccount = new LocalAccountOptions { Name = "Anna", AskPassword = true, Password = "leak" }
            }
        };
        store.SaveLastProfile(profile);

        Assert.Equal(ThemeChoice.Dark, store.Load().Theme);
        Assert.Equal("Anna", store.LoadLastProfile()!.Setup.LocalAccount!.Name);
        foreach (var file in Directory.EnumerateFiles(temp.Path)) Assert.DoesNotContain("leak", File.ReadAllText(file));
    }

    [GeneratedRegex(@"res:Strings\.([A-Za-z0-9_]+)")]
    private static partial Regex XamlStatic();
}