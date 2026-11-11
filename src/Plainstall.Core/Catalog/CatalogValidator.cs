using System.Text.RegularExpressions;

namespace Plainstall.Core.Catalog;

/// <summary>
///     The catalog rules that a JSON Schema cannot express: unique ids, known references, data that fits
///     its registry type, no protected package, presets that are consistent on their own. Returns every
///     problem instead of stopping at the first, so a catalog author sees the whole list.
/// </summary>
public static partial class CatalogValidator
{
    public static readonly IReadOnlyList<string> KnownSetupBindings =
        ["setup.localAccount", "setup.skipPrivacyPages", "setup.bypassHardwareChecks"];

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^ControlSet\d{3}(\\|$)", RegexOptions.IgnoreCase)]
    private static partial Regex NumberedControlSet();

    [GeneratedRegex(@"^Software\\Classes(\\|$)", RegexOptions.IgnoreCase)]
    private static partial Regex SoftwareClasses();

    [GeneratedRegex("^(HKEY_|HKLM|HKCU|HKU|HKCR)", RegexOptions.IgnoreCase)]
    private static partial Regex RootKeyPrefix();

    public static IReadOnlyList<string> Validate(Catalog catalog)
    {
        var problems = new List<string>();
        var presetIds = catalog.Presets.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);

        if (catalog.Presets.Select(p => p.Id).Distinct().Count() != catalog.Presets.Count)
            problems.Add("presets.json: preset ids must be unique.");
        if (catalog.Presets.Count(p => p.IsDefault) != 1)
            problems.Add("presets.json: exactly one preset must have isDefault = true.");

        var seenIds = new Dictionary<string, CatalogKind>(StringComparer.Ordinal);
        problems.AddRange(catalog.AllEntries
            .Where(entry => !seenIds.TryAdd(entry.Id, entry.Kind))
            .Select(entry => $"{entry.Id}: the id is used more than once."));

        var seenPackages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seenValues = new Dictionary<string, (string EntryId, object Value)>(StringComparer.OrdinalIgnoreCase);

        ValidateFile(catalog.Apps, catalog.AppCategories, CatalogKind.Apps);
        ValidateFile(catalog.Tweaks, catalog.TweakCategories, CatalogKind.Tweaks);

        // A preset must be consistent on its own.
        foreach (var preset in catalog.Presets)
        {
            var members = catalog.AllEntries.Where(e => e.DefaultIn.Contains(preset.Id)).ToDictionary(e => e.Id);
            foreach (var entry in members.Values)
            {
                problems.AddRange(entry.Requires
                    .Where(r => !members.ContainsKey(r))
                    .Select(required =>
                        $"preset {preset.Id}: {entry.Id} requires {required}, which is not in the preset."));
                problems.AddRange(entry.Conflicts
                    .Where(members.ContainsKey)
                    .Select(conflict =>
                        $"preset {preset.Id}: {entry.Id} conflicts with {conflict}, and both are in the preset."));
            }
        }

        return problems;

        void ValidateFile(IReadOnlyList<CatalogEntry> entries, IReadOnlyList<CatalogCategory> categories,
            CatalogKind kind)
        {
            var file = kind == CatalogKind.Apps ? "apps.json" : "tweaks.json";
            var categoryIds = categories.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            if (categoryIds.Count != categories.Count) problems.Add($"{file}: category ids must be unique.");
            problems.AddRange(categoryIds
                .Where(c => entries.All(e => e.Category != c))
                .Select(unused => $"{file}: category {unused} has no entries."));

            foreach (var entry in entries)
            {
                var where = $"{file}: {entry.Id}";
                if (!IdPattern().IsMatch(entry.Id) || entry.Id.Length > 48)
                    problems.Add($"{where}: ids are lowercase kebab-case, at most 48 characters.");
                if (string.IsNullOrWhiteSpace(entry.Title) || string.IsNullOrWhiteSpace(entry.Description))
                    problems.Add($"{where}: title and description are required.");
                if (!categoryIds.Contains(entry.Category)) problems.Add($"{where}: unknown category {entry.Category}.");
                problems.AddRange(entry.DefaultIn
                    .Where(p => !presetIds.Contains(p))
                    .Select(preset => $"{where}: unknown preset {preset} in defaultIn."));
                problems.AddRange(entry.Requires.Concat(entry.Conflicts)
                    .Where(r => catalog.FindEntry(r) is null)
                    .Select(reference => $"{where}: reference to unknown id {reference}."));
                if (entry.BoundTo is not null)
                {
                    if (!KnownSetupBindings.Contains(entry.BoundTo))
                        problems.Add($"{where}: unknown boundTo value {entry.BoundTo}.");
                    if (entry.DefaultIn.Count > 0)
                        problems.Add($"{where}: an entry with boundTo must have an empty defaultIn.");
                }

                if (entry is { MinBuild: { } min, MaxBuild: { } max } && min > max)
                    problems.Add($"{where}: minBuild is greater than maxBuild.");
                var reinstallProblem = (kind, entry.Reinstall) switch
                {
                    (CatalogKind.Apps, null) => "app entries need a reinstall note",
                    (CatalogKind.Tweaks, not null) => "reinstall is for app entries only",
                    _ => null
                };
                if (reinstallProblem is not null) problems.Add($"{where}: {reinstallProblem}.");
                if (entry.Actions.Count == 0) problems.Add($"{where}: at least one action is required.");

                for (var i = 0; i < entry.Actions.Count; i++)
                    ValidateAction(entry, entry.Actions[i], $"{where}: action {i + 1}", kind);
            }
        }

        void ValidateAction(CatalogEntry entry, CatalogAction action, string where, CatalogKind kind)
        {
            if (action is { MinBuild: { } min, MaxBuild: { } max } && min > max)
                problems.Add($"{where}: minBuild is greater than maxBuild.");

            switch (action)
            {
                case RegistrySetAction set:
                    ValidateKey(set.Key, set.Hive, where);
                    if (IsForbiddenValue(set.Name))
                        problems.Add($"{where}: GlobalUserDisabled breaks Search and Start and must never be written.");
                    var value = RegistryData.TryConvert(set.Type, set.Data, out var error);
                    if (value is null)
                    {
                        problems.Add($"{where}: {error}.");
                        break;
                    }

                    var valueKey = $"{set.Hive}|{set.Key}|{set.Name}|{set.MinBuild}|{set.MaxBuild}";
                    if (seenValues.TryGetValue(valueKey, out var earlier))
                    {
                        if (!RegistryData.ValuesEqual(earlier.Value, value))
                            problems.Add($"{where}: {set.Target} is set to a different value by {earlier.EntryId}.");
                    }
                    else
                    {
                        seenValues[valueKey] = (entry.Id, value);
                    }

                    break;

                case RegistryDeleteAction delete:
                    ValidateKey(delete.Key, delete.Hive, where);
                    break;

                case AppxRemoveProvisionedAction appx:
                    if (kind != CatalogKind.Apps) problems.Add($"{where}: app removal belongs in apps.json.");
                    if (catalog.IsProtectedPackage(appx.Package))
                        problems.Add($"{where}: {appx.Package} is a protected package.");
                    if (!seenPackages.TryAdd(appx.Package, entry.Id))
                        problems.Add(
                            $"{where}: package {appx.Package} is already removed by {seenPackages[appx.Package]}.");
                    break;

                case FileDeleteAction file:
                    ValidateImagePath(file.Path, where);
                    break;

                case TaskDeleteAction task:
                    ValidateImagePath(task.Path, where);
                    break;

                case FirstLogonRegistryAction firstLogon:
                    if (RootKeyPrefix().IsMatch(firstLogon.Key) || firstLogon.Key.StartsWith('\\') ||
                        firstLogon.Key.EndsWith('\\'))
                        problems.Add(
                            $"{where}: the key is relative to HKCU, without a root name or outer backslashes.");
                    if (firstLogon.Name is not null && IsForbiddenValue(firstLogon.Name))
                        problems.Add($"{where}: GlobalUserDisabled breaks Search and Start and must never be written.");
                    if (firstLogon.Op == FirstLogonRegistryOp.Set)
                    {
                        if (firstLogon.Name is null || firstLogon.Type is null || firstLogon.Data is null)
                            problems.Add($"{where}: a set operation needs name, type and data.");
                        else if (RegistryData.TryConvert(firstLogon.Type.Value, firstLogon.Data.Value, out var flError)
                                 is null) problems.Add($"{where}: {flError}.");
                    }

                    break;

                case FirstLogonCommandAction command:
                    if (string.IsNullOrWhiteSpace(command.Command) || string.IsNullOrWhiteSpace(command.Summary))
                        problems.Add($"{where}: command and summary are required.");
                    break;
            }
        }

        void ValidateKey(string key, RegistryHiveId hive, string where)
        {
            if (key.Length == 0 || key.StartsWith('\\') || key.EndsWith('\\') || RootKeyPrefix().IsMatch(key))
                problems.Add($"{where}: the key is relative to the hive, without a root name or outer backslashes.");
            switch (hive)
            {
                case RegistryHiveId.MachineSystem or RegistryHiveId.SetupSystem when NumberedControlSet().IsMatch(key):
                    problems.Add($"{where}: use CurrentControlSet, not a numbered control set.");
                    break;
                case RegistryHiveId.DefaultUser when SoftwareClasses().IsMatch(key):
                    problems.Add(
                        $"{where}: Software\\Classes is not part of the Default user's NTUSER.DAT. Use firstLogon.registry.");
                    break;
            }
        }

        void ValidateImagePath(string path, string where)
        {
            if (path.Length == 0 || Path.IsPathRooted(path) || path.Contains(':') || path.StartsWith('\\') ||
                path.StartsWith('/')
                || path.Split('\\', '/').Contains(".."))
                problems.Add($"{where}: the path is relative to the image root and must not contain '..' or a drive.");
        }

        static bool IsForbiddenValue(string name)
        {
            return name.Equals("GlobalUserDisabled", StringComparison.OrdinalIgnoreCase);
        }
    }
}