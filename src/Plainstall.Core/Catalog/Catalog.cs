using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Plainstall.Core.Catalog;

/// <summary>
///     The whole catalog: apps, tweaks and presets, loaded and checked. Immutable after loading.
/// </summary>
public sealed class Catalog
{
    private readonly Dictionary<string, CatalogEntry> _byId;
    private readonly Dictionary<string, CatalogEntry> _byPackage;

    internal Catalog(CatalogFile apps, CatalogFile tweaks, PresetsFile presets)
    {
        AppCategories = apps.Categories;
        TweakCategories = tweaks.Categories;
        Apps = [.. apps.Entries.Select(e => e with { Kind = CatalogKind.Apps })];
        Tweaks = [.. tweaks.Entries.Select(e => e with { Kind = CatalogKind.Tweaks })];
        Presets = presets.Presets;
        ProtectedPackages = apps.ProtectedPackages;
        CatalogVersion = apps.CatalogVersion;

        _byId = new Dictionary<string, CatalogEntry>(StringComparer.Ordinal);
        foreach (var entry in Apps.Concat(Tweaks))
            // The validator reports duplicates. Here the first one wins.
            _byId.TryAdd(entry.Id, entry);

        _byPackage = new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Apps)
        foreach (var action in entry.Actions.OfType<AppxRemoveProvisionedAction>())
            _byPackage.TryAdd(action.Package, entry);
    }

    public int CatalogVersion { get; }
    public IReadOnlyList<CatalogCategory> AppCategories { get; }
    public IReadOnlyList<CatalogCategory> TweakCategories { get; }
    public IReadOnlyList<CatalogEntry> Apps { get; }
    public IReadOnlyList<CatalogEntry> Tweaks { get; }
    public IReadOnlyList<Preset> Presets { get; }
    public IReadOnlyList<string> ProtectedPackages { get; }

    public IEnumerable<CatalogEntry> AllEntries => Apps.Concat(Tweaks);

    public Preset DefaultPreset => Presets.First(p => p.IsDefault);

    public CatalogEntry? FindEntry(string id)
    {
        return _byId.GetValueOrDefault(id);
    }

    public Preset? FindPreset(string id)
    {
        return Presets.FirstOrDefault(p => p.Id == id);
    }

    /// <summary>The app entry that removes the given package, if any.</summary>
    public CatalogEntry? FindAppByPackage(string packageName)
    {
        return _byPackage.GetValueOrDefault(packageName);
    }

    /// <summary>True when the package is on the list Plainstall refuses to remove.</summary>
    public bool IsProtectedPackage(string packageName)
    {
        foreach (var pattern in ProtectedPackages)
            if (pattern.EndsWith('*'))
            {
                if (packageName.StartsWith(pattern.AsSpan(0, pattern.Length - 1), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (string.Equals(packageName, pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

        return false;
    }
}

/// <summary>Reads the catalog from the embedded files or from a folder, and validates it.</summary>
public static class CatalogLoader
{
    internal static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    private static readonly Lazy<Catalog> Embedded = new(() =>
    {
        var assembly = typeof(CatalogLoader).Assembly;
        return FromJson(
            ReadResource(assembly, "Plainstall.Catalog.apps.json"),
            ReadResource(assembly, "Plainstall.Catalog.tweaks.json"),
            ReadResource(assembly, "Plainstall.Catalog.presets.json"));
    });

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowOutOfOrderMetadataProperties = true,
            ReadCommentHandling = JsonCommentHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false));
        return options;
    }

    /// <summary>The catalog built into this version of Plainstall.</summary>
    public static Catalog LoadEmbedded()
    {
        return Embedded.Value;
    }

    /// <summary>Loads apps.json, tweaks.json and presets.json from a folder. For catalog authors and tests.</summary>
    public static Catalog LoadFromDirectory(string directory)
    {
        return FromJson(
            File.ReadAllText(Path.Combine(directory, "apps.json")),
            File.ReadAllText(Path.Combine(directory, "tweaks.json")),
            File.ReadAllText(Path.Combine(directory, "presets.json")));
    }

    /// <summary>Parses and validates. Throws <see cref="PlainstallException" /> listing every problem found.</summary>
    public static Catalog FromJson(string appsJson, string tweaksJson, string presetsJson)
    {
        var catalog = Parse(appsJson, tweaksJson, presetsJson);
        var problems = CatalogValidator.Validate(catalog);
        return problems.Count > 0
            ? throw PlainstallException.WithList(ErrorKind.InvalidInput, "The catalog is not valid:", problems)
            : catalog;
    }

    /// <summary>
    ///     Parses without semantic validation. <see cref="FromJson" /> validates the result, and the validator tests
    ///     call it on broken catalogs.
    /// </summary>
    internal static Catalog Parse(string appsJson, string tweaksJson, string presetsJson)
    {
        var apps = Deserialize<CatalogFile>(appsJson, "apps.json");
        var tweaks = Deserialize<CatalogFile>(tweaksJson, "tweaks.json");
        var presets = Deserialize<PresetsFile>(presetsJson, "presets.json");
        if (apps.Kind != CatalogKind.Apps)
            throw new PlainstallException(ErrorKind.InvalidInput, "apps.json must have \"kind\": \"apps\".");
        return tweaks.Kind == CatalogKind.Tweaks
            ? new Catalog(apps, tweaks, presets)
            : throw new PlainstallException(ErrorKind.InvalidInput, "tweaks.json must have \"kind\": \"tweaks\".");
    }

    private static T Deserialize<T>(string json, string fileName)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions)
                   ?? throw new PlainstallException(ErrorKind.InvalidInput, $"{fileName} is empty.");
        }
        catch (JsonException ex)
        {
            throw new PlainstallException(ErrorKind.InvalidInput, $"{fileName} could not be read: {ex.Message}", ex);
        }
    }

    private static string ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
                           ?? throw new PlainstallException(ErrorKind.Internal,
                               $"Embedded catalog resource '{name}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}