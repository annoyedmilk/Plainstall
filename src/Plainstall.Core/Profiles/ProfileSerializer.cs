using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Plainstall.Core.Catalog;

namespace Plainstall.Core.Profiles;

/// <summary>Result of opening a profile: the profile and anything the user should be told about it.</summary>
public sealed record ProfileLoadResult(Profile Profile, IReadOnlyList<string> Warnings);

/// <summary>One step of schema migration: takes the JSON of version N and returns the JSON of version N + 1.</summary>
public delegate JsonObject ProfileMigrationStep(JsonObject profile);

/// <summary>Reads, migrates, validates and writes <c>*.plainstall.json</c> files.</summary>
public sealed partial class ProfileSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    private static readonly string[] PresetIds = ["recommended", "privacy-max", "gaming", "minimal", "none"];

    /// <summary>
    ///     Names that already exist on every Windows PC as an account or a group. Creating a second one
    ///     fails in the oobeSystem pass, at the very end of the installation. English names only: the
    ///     translated names of other Windows languages are not known here.
    /// </summary>
    private static readonly string[] ReservedAccountNames =
    [
        "Administrator", "Guest", "DefaultAccount", "WDAGUtilityAccount", "defaultuser0",
        "Administrators", "Users", "Guests", "Power Users", "SYSTEM", "LOCAL SERVICE", "NETWORK SERVICE", "NONE"
    ];

    private readonly int _currentSchema;
    private readonly IReadOnlyDictionary<int, ProfileMigrationStep> _migrations;

    /// <summary>The serializer for the real schema history. Schema 1 is the first version, so there are no steps yet.</summary>
    public ProfileSerializer()
        : this(new Dictionary<int, ProfileMigrationStep>(), Profile.CurrentSchema)
    {
    }

    /// <summary>For tests: a serializer with an injected migration chain.</summary>
    internal ProfileSerializer(IReadOnlyDictionary<int, ProfileMigrationStep> migrations, int currentSchema)
    {
        _migrations = migrations;
        _currentSchema = currentSchema;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false));
        return options;
    }

    public ProfileLoadResult Load(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PlainstallException(ErrorKind.InvalidInput,
                $"The profile '{path}' could not be opened: {ex.Message}", ex);
        }

        return Parse(json);
    }

    public ProfileLoadResult Parse(string json)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json,
                       documentOptions: new JsonDocumentOptions
                           { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                   ?? throw new PlainstallException(ErrorKind.InvalidInput, "The profile is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new PlainstallException(ErrorKind.InvalidInput, $"The profile is not valid JSON: {ex.Message}", ex);
        }

        if (root["schema"] is not JsonValue schemaNode || !schemaNode.TryGetValue<int>(out var schema) || schema < 1)
            throw new PlainstallException(ErrorKind.InvalidInput,
                "The profile has no valid \"schema\" number. It may not be a Plainstall profile.");
        if (schema > _currentSchema)
            throw new PlainstallException(ErrorKind.InvalidInput,
                $"This profile was saved by a newer version of Plainstall (profile schema {schema}, this version understands up to {_currentSchema}). Update Plainstall to open it.");

        var warnings = new List<string>();
        var loadedFrom = schema;
        while (schema < _currentSchema)
        {
            if (!_migrations.TryGetValue(schema, out var step))
                throw new PlainstallException(ErrorKind.Internal,
                    $"No migration from profile schema {schema} to {schema + 1} is known.");
            root = step(root);
            schema++;
            root["schema"] = schema;
        }

        if (loadedFrom != schema)
            warnings.Add(
                $"The profile was converted from schema {loadedFrom} to {schema}. Save it to keep the new format.");

        if (root["setup"]?["localAccount"] is JsonObject account && account["password"] is not null)
            throw new PlainstallException(ErrorKind.InvalidInput,
                "The profile contains a password. Plainstall never stores passwords in a profile: remove the \"password\" value and set \"askPassword\": true instead. Plainstall asks for it when the build starts.");

        Profile profile;
        try
        {
            profile = root.Deserialize<Profile>(JsonOptions)
                      ?? throw new PlainstallException(ErrorKind.InvalidInput, "The profile is empty.");
        }
        catch (JsonException ex)
        {
            throw new PlainstallException(ErrorKind.InvalidInput, $"The profile could not be read: {ex.Message}", ex);
        }

        var problems = Validate(profile);
        return problems.Count > 0
            ? throw PlainstallException.WithList(ErrorKind.InvalidInput, "The profile is not valid:", problems)
            : new ProfileLoadResult(profile, warnings);
    }

    public string ToJson(Profile profile, string? createdWith = null)
    {
        var toWrite = profile with
        {
            Schema = _currentSchema,
            CreatedWith = createdWith ?? profile.CreatedWith,
            Setup = profile.Setup with
            {
                // Belt and braces: a password can never leave through this method.
                LocalAccount = profile.Setup.LocalAccount is null
                    ? null
                    : profile.Setup.LocalAccount with { Password = null }
            }
        };
        return JsonSerializer.Serialize(toWrite, JsonOptions) + Environment.NewLine;
    }

    public void Save(Profile profile, string path, string? createdWith = null)
    {
        AtomicFile.WriteAllText(path, ToJson(profile, createdWith));
    }

    [GeneratedRegex("^(auto|[a-z]{2,3}(-[A-Za-z]{4})?(-[A-Z]{2}|-[0-9]{3})?)$")]
    private static partial Regex AutoOrTag();

    [GeneratedRegex("^(auto|ask|[a-z]{2,3}(-[A-Za-z]{4})?(-[A-Z]{2}|-[0-9]{3})?)$")]
    private static partial Regex AutoAskOrTag();

    [GeneratedRegex("^[A-Za-z]+$")]
    private static partial Regex Letters();

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex CatalogId();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-]*$")]
    private static partial Regex AppReference();

    [GeneratedRegex("^(?![0-9]+$)[A-Za-z0-9-]{1,15}$")]
    private static partial Regex ComputerName();

    [GeneratedRegex("^[^\"/\\\\\\[\\]:;|=,+*?<>@]+$")]
    private static partial Regex AccountName();

    /// <summary>True when Windows itself already uses <paramref name="name" /> for an account or a group.</summary>
    public static bool IsReservedAccountName(string name)
    {
        return ReservedAccountNames.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     The rules of profile.schema.json, checked in code so that the CLI and the app give
    ///     the same plain messages without a JSON Schema library at run time.
    /// </summary>
    public static IReadOnlyList<string> Validate(Profile profile)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 80)
            problems.Add("\"name\" must be 1 to 80 characters.");

        var w = profile.Windows;
        if (!AutoOrTag().IsMatch(w.Language))
            problems.Add($"windows.language '{w.Language}' must be \"auto\" or a language tag such as de-DE.");
        if (!Letters().IsMatch(w.Edition))
            problems.Add($"windows.edition '{w.Edition}' must be an edition id such as Professional or Core.");

        if (!PresetIds.Contains(profile.Apps.Preset))
            problems.Add($"apps.preset '{profile.Apps.Preset}' is not a known preset.");
        if (!PresetIds.Contains(profile.Tweaks.Preset))
            problems.Add($"tweaks.preset '{profile.Tweaks.Preset}' is not a known preset.");
        CheckList(profile.Apps.Keep, AppReference().IsMatch, "apps.keep", "a catalog id or a package name");
        CheckList(profile.Apps.Remove, AppReference().IsMatch, "apps.remove", "a catalog id or a package name");
        CheckList(profile.Apps.Install, InstallIds.IsValid, "apps.install",
            "a winget package id such as Mozilla.Firefox or a Microsoft Store id such as 9NKSQGP7F2NH");
        if (profile.Apps.Install.Count > 50) problems.Add("apps.install can hold at most 50 packages.");
        CheckList(profile.Tweaks.Enable, CatalogId().IsMatch, "tweaks.enable", "a catalog id");
        CheckList(profile.Tweaks.Disable, CatalogId().IsMatch, "tweaks.disable", "a catalog id");

        var s = profile.Setup;
        if (s.LocalAccount is { } account)
        {
            if (account.Password is not null)
                problems.Add("setup.localAccount.password must be null. Passwords are never stored in a profile.");
            if (account.Name is { } name)
            {
                // The same rules as the answer file, so a name Windows Setup refuses is found before the build.
                if (name.Length is 0 or > 20 || !AccountName().IsMatch(name) || name.Trim() != name ||
                    name.EndsWith('.') || name.Any(char.IsControl))
                    problems.Add(
                        $"setup.localAccount.name '{name}' is not a valid Windows account name (1 to 20 characters, none of \" / \\ [ ] : ; | = , + * ? < > @).");
                else if (IsReservedAccountName(name))
                    problems.Add(
                        $"setup.localAccount.name '{name}' is already used by Windows itself. Choose another name.");
                else if (string.Equals(name, s.ComputerName, StringComparison.OrdinalIgnoreCase))
                    problems.Add(
                        $"setup.localAccount.name '{name}' is the same as setup.computerName. Windows does not accept the same name for both.");
            }

            if (account is { Name: null, AskPassword: true })
                problems.Add(
                    "setup.localAccount.askPassword needs an account name. When Setup asks for the name, it asks for the password too.");
        }

        if (s.ComputerName is { } computer && !ComputerName().IsMatch(computer))
            problems.Add(
                $"setup.computerName '{computer}' must be 1 to 15 letters, digits or hyphens, and not only digits.");
        if (!AutoAskOrTag().IsMatch(s.Region))
            problems.Add($"setup.region '{s.Region}' must be \"auto\", \"ask\" or a tag such as de-CH.");
        if (!AutoAskOrTag().IsMatch(s.Keyboard))
            problems.Add($"setup.keyboard '{s.Keyboard}' must be \"auto\", \"ask\" or a tag such as de-CH.");
        if (string.IsNullOrWhiteSpace(s.Timezone) || s.Timezone.Length > 64)
            problems.Add("setup.timezone must be \"auto\", \"ask\" or a Windows time zone id.");

        problems.AddRange(profile.Updates
            .Where(update => !update.EndsWith(".msu", StringComparison.OrdinalIgnoreCase) &&
                             !update.EndsWith(".cab", StringComparison.OrdinalIgnoreCase))
            .Select(update => $"updates: '{update}' is not an .msu or .cab file."));
        if (profile.Updates.Distinct(StringComparer.OrdinalIgnoreCase).Count() != profile.Updates.Count)
            problems.Add("updates: the same file is listed twice.");
        if (profile.Drivers.Folders.Any(string.IsNullOrWhiteSpace))
            problems.Add("drivers.folders: empty entries are not allowed.");

        return problems;

        void CheckList(IReadOnlyList<string> items, Func<string, bool> isValid, string field, string expected)
        {
            problems.AddRange(items.Where(i => !isValid(i))
                .Select(item => $"{field}: '{item}' is not {expected}."));
            if (items.Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Count)
                problems.Add($"{field}: the same item is listed twice.");
        }
    }
}