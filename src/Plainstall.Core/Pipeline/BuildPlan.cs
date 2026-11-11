using Plainstall.Core.Catalog;
using Plainstall.Core.Profiles;

namespace Plainstall.Core.Pipeline;

/// <summary>What the planner needs to know about the Windows image being customized.</summary>
public sealed record ImageFacts(int Build, string EditionId);

/// <summary>One concrete change that the build will make, and the entry it comes from.</summary>
public sealed record PlannedAction(string EntryId, string EntryTitle, CatalogAction Action);

/// <summary>Something the planner left out or wants the user to know. Never an error.</summary>
public sealed record PlanNote(string? EntryId, string Message);

/// <summary>
///     The resolved result of a profile against a catalog and an image: what will be done, in catalog
///     order, and what was left out and why. Produced before anything is mounted.
/// </summary>
public sealed record BuildPlan
{
    /// <summary>Catalog entries that are switched on and apply to this image.</summary>
    public required IReadOnlyList<CatalogEntry> Entries { get; init; }

    public required IReadOnlyList<PlannedAction> Actions { get; init; }
    public required IReadOnlyList<PlanNote> Notes { get; init; }

    /// <summary>winget package ids or Microsoft Store ids to install at the first sign-in.</summary>
    public required IReadOnlyList<string> WingetInstalls { get; init; }

    /// <summary>True when any action needs the Setup image in boot.wim to be serviced.</summary>
    public bool TouchesSetupImage => Actions.Any(a => IsSetupImageAction(a.Action));

    /// <summary>True for an action that changes the registry of the Setup image, not of the installed Windows.</summary>
    public static bool IsSetupImageAction(CatalogAction action)
    {
        return action is RegistrySetAction { Hive: RegistryHiveId.SetupSystem }
            or RegistryDeleteAction { Hive: RegistryHiveId.SetupSystem };
    }
}

/// <summary>Turns a profile into a <see cref="BuildPlan" />. A pure function: no I/O, no clock, no environment.</summary>
public static class BuildPlanner
{
    public const string AdHocEntryPrefix = "package:";

    /// <summary>
    ///     Resolves the profile. Throws <see cref="PlainstallException" /> (InvalidInput) when the selection
    ///     cannot be built: a protected package, a missing requirement, a conflict.
    /// </summary>
    public static BuildPlan Create(Profile profile, Catalog.Catalog catalog, ImageFacts image)
    {
        var notes = new List<PlanNote>();
        var errors = new List<string>();
        var selected = new HashSet<string>(StringComparer.Ordinal);
        var keptPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var adHocPackages = new List<string>();

        // 1. Preset members.
        AddPreset(profile.Apps.Preset, catalog.Apps, "apps.preset");
        AddPreset(profile.Tweaks.Preset, catalog.Tweaks, "tweaks.preset");

        // 2. Overrides for tweaks.
        foreach (var id in profile.Tweaks.Enable)
            switch (catalog.FindEntry(id))
            {
                case null:
                    notes.Add(new PlanNote(id,
                        $"tweaks.enable: '{id}' is not in this version's catalog and is ignored."));
                    break;
                case { Kind: CatalogKind.Apps }:
                    errors.Add($"tweaks.enable: '{id}' is an app. List it under apps.remove.");
                    break;
                case { BoundTo: not null } bound:
                    errors.Add(
                        $"tweaks.enable: '{id}' is controlled by the Setup option {bound.BoundTo}, not by a switch.");
                    break;
                default:
                    selected.Add(id);
                    break;
            }

        foreach (var id in profile.Tweaks.Disable)
        {
            if (catalog.FindEntry(id) is null)
                notes.Add(new PlanNote(id, $"tweaks.disable: '{id}' is not in this version's catalog and is ignored."));
            selected.Remove(id);
        }

        // 3. Overrides for apps. Items are catalog ids or package names.
        foreach (var reference in profile.Apps.Remove)
            if (catalog.FindEntry(reference) is { Kind: CatalogKind.Apps } byId)
                selected.Add(byId.Id);
            else if (catalog.FindEntry(reference) is { Kind: CatalogKind.Tweaks })
                errors.Add($"apps.remove: '{reference}' is a tweak. List it under tweaks.enable.");
            else if (catalog.FindAppByPackage(reference) is { } byPackage)
                selected.Add(byPackage.Id);
            else if (catalog.IsProtectedPackage(reference))
                errors.Add(
                    $"apps.remove: '{reference}' is protected. Plainstall never removes it, because the Store, winget, Terminal, Windows Security or other apps depend on it.");
            else if (reference.Contains('.'))
                adHocPackages.Add(reference);
            else
                notes.Add(new PlanNote(reference,
                    $"apps.remove: '{reference}' is neither a catalog app nor a package name and is ignored."));

        foreach (var reference in profile.Apps.Keep)
            if (catalog.FindEntry(reference) is { Kind: CatalogKind.Apps } byId)
            {
                selected.Remove(byId.Id);
            }
            else
            {
                // A package name protects that one package, also inside an entry that removes several.
                keptPackages.Add(reference);
                adHocPackages.RemoveAll(p => string.Equals(p, reference, StringComparison.OrdinalIgnoreCase));
            }

        // 4. Entries that belong to Setup options.
        foreach (var entry in catalog.AllEntries)
            if (entry.BoundTo is { } option && IsSetupOptionOn(option, profile.Setup))
                selected.Add(entry.Id);

        // 5. Leave out what does not fit this image.
        var entries = new List<CatalogEntry>();
        var notApplicable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in catalog.AllEntries.Where(e => selected.Contains(e.Id)))
            if (!BuildFits(entry.MinBuild, entry.MaxBuild, image.Build, out var why))
            {
                notes.Add(new PlanNote(entry.Id, $"'{entry.Title}' is left out: {why}."));
                notApplicable.Add(entry.Id);
            }
            else if (entry.Editions is { } editions &&
                     !editions.Contains(image.EditionId, StringComparer.OrdinalIgnoreCase))
            {
                notes.Add(new PlanNote(entry.Id,
                    $"'{entry.Title}' is left out: it does not apply to the {image.EditionId} edition."));
                notApplicable.Add(entry.Id);
            }
            else if (KeepsEveryPackage(entry, keptPackages))
            {
                // The profile keeps each package of this app by name, so the entry changes nothing.
                notes.AddRange(entry.Actions.OfType<AppxRemoveProvisionedAction>()
                    .Where(a => BuildFits(a.MinBuild, a.MaxBuild, image.Build, out _))
                    .Select(appx => new PlanNote(entry.Id, $"{appx.Package} is kept, as the profile asks.")));
            }
            else
            {
                entries.Add(entry);
            }

        // 6. Requirements and conflicts are never resolved silently.
        var active = entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            errors.AddRange(entry.Requires
                .Where(required => !active.Contains(required) && !notApplicable.Contains(required))
                .Select(required =>
                    $"'{entry.Title}' ({entry.Id}) needs '{catalog.FindEntry(required)?.Title ?? required}' ({required}) to be switched on as well."));
            errors.AddRange(entry.Conflicts
                .Where(active.Contains)
                .Select(conflict =>
                    $"'{entry.Title}' ({entry.Id}) cannot be combined with '{catalog.FindEntry(conflict)?.Title ?? conflict}' ({conflict})."));
        }

        if (errors.Count > 0)
            throw PlainstallException.WithList(ErrorKind.InvalidInput, "This profile cannot be built:",
                errors.Distinct());

        // 7. Actions, in catalog order.
        var actions = new List<PlannedAction>();
        foreach (var entry in entries)
        foreach (var action in entry.Actions)
        {
            if (!BuildFits(action.MinBuild, action.MaxBuild, image.Build, out _)) continue;
            if (action is AppxRemoveProvisionedAction appx && keptPackages.Contains(appx.Package))
            {
                notes.Add(new PlanNote(entry.Id, $"{appx.Package} is kept, as the profile asks."));
                continue;
            }

            actions.Add(new PlannedAction(entry.Id, entry.Title, action));
        }

        foreach (var package in adHocPackages.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            actions.Add(new PlannedAction(AdHocEntryPrefix + package, package,
                new AppxRemoveProvisionedAction { Package = package }));
            notes.Add(new PlanNote(null,
                $"{package} is not in the catalog. It is removed as the profile asks. Plainstall does not know its side effects."));
        }

        return new BuildPlan
        {
            Entries = entries,
            Actions = actions,
            Notes = notes,
            WingetInstalls = [.. profile.Apps.Install]
        };

        void AddPreset(string presetId, IReadOnlyList<CatalogEntry> pool, string field)
        {
            if (presetId == "none") return;
            if (catalog.FindPreset(presetId) is null)
            {
                errors.Add($"{field}: '{presetId}' is not a known preset.");
                return;
            }

            foreach (var entry in pool.Where(e => e.DefaultIn.Contains(presetId))) selected.Add(entry.Id);
        }
    }

    /// <summary>
    ///     True when <paramref name="kept" /> names every package that the app entry removes, and the entry does
    ///     nothing else. Then the entry changes nothing and is not part of the plan.
    /// </summary>
    public static bool KeepsEveryPackage(CatalogEntry entry, IReadOnlyCollection<string> kept)
    {
        return entry is { Kind: CatalogKind.Apps, Actions.Count: > 0 } &&
               entry.Actions.All(a => a is AppxRemoveProvisionedAction appx &&
                                      kept.Contains(appx.Package, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>True when the Setup option that a bound entry follows (its <c>boundTo</c>) is on.</summary>
    public static bool IsSetupOptionOn(string boundTo, SetupOptions setup)
    {
        return boundTo switch
        {
            "setup.localAccount" => setup.LocalAccount is not null,
            "setup.skipPrivacyPages" => setup.SkipPrivacyPages,
            "setup.bypassHardwareChecks" => setup.BypassHardwareChecks,
            _ => false
        };
    }

    private static bool BuildFits(int? min, int? max, int build, out string why)
    {
        if (min is { } lower && build < lower)
        {
            why = $"it needs Windows build {lower} or newer, and this image is build {build}";
            return false;
        }

        if (max is { } upper && build > upper)
        {
            why = $"it applies up to Windows build {upper}, and this image is build {build}";
            return false;
        }

        why = "";
        return true;
    }
}