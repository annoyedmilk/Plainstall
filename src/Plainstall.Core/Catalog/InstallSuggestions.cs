using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Plainstall.Core.Catalog;

/// <summary>One app the user can tick to have installed with winget at the first sign-in.</summary>
public sealed record InstallSuggestion(string WingetId, string Title, string Description, string Category);

/// <summary>
///     The two forms of id winget installs from: a package id of the winget community source
///     (<c>Mozilla.Firefox</c>) or a Microsoft Store product id (<c>9NKSQGP7F2NH</c>, <c>XP89DCGQ3K6VLD</c>).
///     The same rules are in installs.schema.json and in profile.schema.json (apps.install).
/// </summary>
public static partial class InstallIds
{
    /// <summary>Longest id the profile schema accepts.</summary>
    public const int MaxLength = 120;

    // \A and \z instead of ^ and $: in .NET "$" also matches before a final line break, and a line break must
    // never get through. Both patterns are also used, unchanged, by PowerShell in the first sign-in script.

    /// <summary>A winget package id: letters, digits, '+', '_' and '-', with at least one dot.</summary>
    public const string WingetPatternText = @"\A[A-Za-z0-9][A-Za-z0-9+_\-]*(\.[A-Za-z0-9+_\-]+)+\z";

    /// <summary>A Store product id: '9' and 11 more, or 'XP' and 12 more, capital letters and digits only.</summary>
    public const string StorePatternText = @"\A(9[A-Z0-9]{11}|XP[A-Z0-9]{12})\z";

    [GeneratedRegex(WingetPatternText, RegexOptions.CultureInvariant)]
    private static partial Regex WingetPattern();

    [GeneratedRegex(StorePatternText, RegexOptions.CultureInvariant)]
    private static partial Regex StorePattern();

    /// <summary>True for a Microsoft Store product id, which winget installs from its msstore source.</summary>
    public static bool IsStoreId(string? id)
    {
        return id is not null && StorePattern().IsMatch(id);
    }

    /// <summary>True for a winget package id or a Store product id.</summary>
    public static bool IsValid(string? id)
    {
        return id is { Length: >= 1 and <= MaxLength } && (WingetPattern().IsMatch(id) || StorePattern().IsMatch(id));
    }
}

/// <summary>
///     The pick list for "install apps at first sign-in". It only decides what the app offers with a
///     checkbox. A profile may name any winget package id.
/// </summary>
public sealed class InstallSuggestions
{
    private const string ResourceName = "Plainstall.Catalog.installs.json";

    private static readonly Lazy<InstallSuggestions> Embedded = new(() =>
    {
        using var stream = typeof(InstallSuggestions).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new PlainstallException(ErrorKind.Internal,
                               $"Embedded resource '{ResourceName}' is missing.");
        using var reader = new StreamReader(stream);
        return FromJson(reader.ReadToEnd());
    });

    private InstallSuggestions(IReadOnlyList<CatalogCategory> categories, IReadOnlyList<InstallSuggestion> entries)
    {
        Categories = categories;
        Entries = entries;
    }

    public IReadOnlyList<CatalogCategory> Categories { get; }
    public IReadOnlyList<InstallSuggestion> Entries { get; }

    public static InstallSuggestions LoadEmbedded()
    {
        return Embedded.Value;
    }

    public InstallSuggestion? Find(string wingetId)
    {
        return Entries.FirstOrDefault(e => string.Equals(e.WingetId, wingetId, StringComparison.OrdinalIgnoreCase));
    }

    public static InstallSuggestions FromJson(string json)
    {
        FileModel file;
        try
        {
            file = JsonSerializer.Deserialize<FileModel>(json, CatalogLoader.JsonOptions)
                   ?? throw new PlainstallException(ErrorKind.InvalidInput, "installs.json is empty.");
        }
        catch (JsonException ex)
        {
            throw new PlainstallException(ErrorKind.InvalidInput, $"installs.json could not be read: {ex.Message}", ex);
        }

        var problems = new List<string>();
        var categoryIds = file.Categories.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in file.Entries)
        {
            if (!InstallIds.IsValid(entry.WingetId))
                problems.Add($"'{entry.WingetId}' is neither a winget package id nor a Store product id.");
            if (!seen.Add(entry.WingetId)) problems.Add($"'{entry.WingetId}' is listed twice.");
            if (!categoryIds.Contains(entry.Category))
                problems.Add($"'{entry.WingetId}': unknown category {entry.Category}.");
            if (string.IsNullOrWhiteSpace(entry.Title) || string.IsNullOrWhiteSpace(entry.Description))
                problems.Add($"'{entry.WingetId}': title and description are required.");
        }

        problems.AddRange(categoryIds
            .Where(c => file.Entries.All(e => e.Category != c))
            .Select(unused => $"category {unused} has no entries."));
        return problems.Count > 0
            ? throw PlainstallException.WithList(ErrorKind.InvalidInput, "installs.json is not valid:", problems)
            : new InstallSuggestions(file.Categories, file.Entries);
    }

    private sealed record FileModel
    {
        [JsonPropertyName("$schema")] public string? Schema { get; init; }

        public required int CatalogVersion { get; init; }
        public required IReadOnlyList<CatalogCategory> Categories { get; init; }
        public required IReadOnlyList<InstallSuggestion> Entries { get; init; }
    }
}