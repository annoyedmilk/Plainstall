using System.Globalization;
using Plainstall.App.Resources;
using Plainstall.Core.Catalog;
using Plainstall.Core.Pipeline;
using Plainstall.Core.Profiles;

namespace Plainstall.App.ViewModels;

/// <summary>What happened when a switch was flipped, in words for the user.</summary>
/// <param name="Applied">False when the change was refused (a conflict). The switch then stays as it was.</param>
public sealed record ToggleResult(bool Applied, IReadOnlyList<string> Messages);

/// <summary>
///     Owns the profile being edited. Every page changes the profile through this class, and the profile is
///     the only state: a switch is on or off exactly when <see cref="BuildPlanner" /> would treat
///     it so. Overrides are stored relative to the preset, so an entry that matches its preset leaves no trace.
/// </summary>
public sealed class ProfileEditor
{
    private readonly Catalog _catalog;

    public ProfileEditor(Catalog catalog, Profile initial)
    {
        _catalog = catalog;
        Profile = initial;
    }

    public Profile Profile { get; private set; }

    /// <summary>Number of switches that differ from the preset.</summary>
    public int ChangedCount => _catalog.AllEntries.Count(IsChangedFromPreset);

    public event EventHandler? Changed;

    public void Replace(Profile profile)
    {
        Profile = profile;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Update(Func<Profile, Profile> change)
    {
        Replace(change(Profile));
    }

    public bool IsInPreset(CatalogEntry entry)
    {
        var preset = entry.Kind == CatalogKind.Apps ? Profile.Apps.Preset : Profile.Tweaks.Preset;
        return preset != "none" && entry.DefaultIn.Contains(preset);
    }

    /// <summary>For an app: it will be removed. For a tweak: it will be applied.</summary>
    public bool IsOn(CatalogEntry entry)
    {
        if (entry.BoundTo is { } option) return BuildPlanner.IsSetupOptionOn(option, Profile.Setup);
        var inPreset = IsInPreset(entry);
        if (entry.Kind == CatalogKind.Apps)
            // The planner applies remove first and keep after it, so keep wins. A package name under keep keeps
            // only that package, so the app is kept when keep names the app or every package that it removes.
            return !Contains(Profile.Apps.Keep, entry.Id)
                   && !BuildPlanner.KeepsEveryPackage(entry, Profile.Apps.Keep)
                   && (inPreset || Profile.Apps.Remove.Any(item => Names(item, entry)));
        // Likewise enable first, disable after it.
        return !Contains(Profile.Tweaks.Disable, entry.Id) && (inPreset || Contains(Profile.Tweaks.Enable, entry.Id));
    }

    public bool IsChangedFromPreset(CatalogEntry entry)
    {
        return entry.BoundTo is null && IsOn(entry) != IsInPreset(entry);
    }

    /// <summary>Sets one switch without looking at requirements or conflicts.</summary>
    public void Set(CatalogEntry entry, bool on)
    {
        if (entry.BoundTo is not null)
            throw new InvalidOperationException($"{entry.Id} is controlled by a Setup option.");
        var differs = on != IsInPreset(entry);
        if (entry.Kind == CatalogKind.Apps)
        {
            var keep = Without(Profile.Apps.Keep, entry);
            var remove = Without(Profile.Apps.Remove, entry);
            if (differs) (on ? remove : keep).Add(entry.Id);
            Update(p => p with { Apps = p.Apps with { Keep = keep, Remove = remove } });
        }
        else
        {
            var enable = Without(Profile.Tweaks.Enable, entry);
            var disable = Without(Profile.Tweaks.Disable, entry);
            if (differs) (on ? enable : disable).Add(entry.Id);
            Update(p => p with { Tweaks = p.Tweaks with { Enable = enable, Disable = disable } });
        }
    }

    /// <summary>
    ///     Flips a switch the way a person expects: what it needs is switched on with it, what needs it is
    ///     switched off with it, and both are said. A conflict is refused and explained, never resolved.
    /// </summary>
    public ToggleResult Toggle(CatalogEntry entry, bool on)
    {
        if (IsOn(entry) == on) return new ToggleResult(true, []);
        var messages = new List<string>();
        if (on)
        {
            var needed = new List<CatalogEntry>();
            if (!CollectRequirements(entry, needed, messages)) return new ToggleResult(false, messages);
            Set(entry, true);
            foreach (var requirement in needed)
            {
                Set(requirement, true);
                messages.Add(Format(Strings.Rule_RequirementSwitchedOn, Subject(entry), Name(requirement)));
            }
        }
        else
        {
            Set(entry, false);
            SwitchOffDependents(entry, messages);
        }

        return new ToggleResult(true, messages);
    }

    /// <summary>Back to the preset: every switch override is removed. Setup and Advanced choices stay.</summary>
    public void ResetSwitches()
    {
        // Package names that are not in the catalog are not switches. They stay, because the app cannot show them.
        Update(p => p with
        {
            Apps = p.Apps with { Keep = NonCatalogApps(p.Apps.Keep), Remove = NonCatalogApps(p.Apps.Remove) },
            Tweaks = p.Tweaks with { Enable = NonCatalog(p.Tweaks.Enable), Disable = NonCatalog(p.Tweaks.Disable) }
        });
    }

    private bool CollectRequirements(CatalogEntry entry, List<CatalogEntry> needed, List<string> messages)
    {
        if (FindActiveConflict(entry, needed) is { } conflict)
        {
            messages.Add(Format(Strings.Rule_ConflictRefused, Name(entry), Name(conflict)));
            return false;
        }

        foreach (var id in entry.Requires)
        {
            if (_catalog.FindEntry(id) is not { } requirement || IsOn(requirement) ||
                needed.Contains(requirement)) continue;
            if (requirement.BoundTo is not null)
            {
                messages.Add(Format(Strings.Rule_RequirementIsSetupOption, Subject(entry), Name(requirement)));
                return false;
            }

            needed.Add(requirement);
            if (!CollectRequirements(requirement, needed, messages)) return false;
        }

        return true;
    }

    private CatalogEntry? FindActiveConflict(CatalogEntry entry, IReadOnlyCollection<CatalogEntry> alsoOn)
    {
        return _catalog.AllEntries.FirstOrDefault(other =>
            other.Id != entry.Id
            && (IsOn(other) || alsoOn.Contains(other))
            && (entry.Conflicts.Contains(other.Id) || other.Conflicts.Contains(entry.Id)));
    }

    private void SwitchOffDependents(CatalogEntry entry, List<string> messages)
    {
        foreach (var dependent in _catalog.AllEntries
                     .Where(e => e.BoundTo is null && e.Requires.Contains(entry.Id) && IsOn(e)).ToList())
        {
            Set(dependent, false);
            var template = dependent.Kind == CatalogKind.Apps
                ? Strings.Rule_DependentAppKept
                : Strings.Rule_DependentSwitchedOff;
            messages.Add(Format(template, Name(dependent), Name(entry)));
            SwitchOffDependents(dependent, messages);
        }
    }

    private static string Name(CatalogEntry entry)
    {
        return Format(Strings.Rule_QuotedName, entry.Title);
    }

    /// <summary>How an entry is named as the one that needs something: "Removing X" for apps, "X" for tweaks.</summary>
    private static string Subject(CatalogEntry entry)
    {
        return entry.Kind == CatalogKind.Apps ? Format(Strings.Rule_RemovingApp, entry.Title) : Name(entry);
    }

    private static string Format(string template, params object[] args)
    {
        return string.Format(CultureInfo.CurrentCulture, template, args);
    }

    private static bool Contains(IReadOnlyList<string> list, string id)
    {
        return list.Contains(id, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     True when a list item is this entry: its id, or for an app also a package name that the catalog gives
    ///     to this app.
    /// </summary>
    private bool Names(string item, CatalogEntry entry)
    {
        return string.Equals(item, entry.Id, StringComparison.OrdinalIgnoreCase)
               || (entry.Kind == CatalogKind.Apps && _catalog.FindAppByPackage(item)?.Id == entry.Id);
    }

    private List<string> Without(IReadOnlyList<string> list, CatalogEntry entry)
    {
        return [.. list.Where(i => !Names(i, entry))];
    }

    private List<string> NonCatalog(IReadOnlyList<string> list)
    {
        return [.. list.Where(i => _catalog.FindEntry(i) is null)];
    }

    /// <summary>Like <see cref="NonCatalog" />, and the package names of catalog apps go too.</summary>
    private List<string> NonCatalogApps(IReadOnlyList<string> list)
    {
        return [.. NonCatalog(list).Where(i => _catalog.FindAppByPackage(i) is null)];
    }
}