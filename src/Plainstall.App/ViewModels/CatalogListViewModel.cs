using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Plainstall.App.Resources;
using Plainstall.App.Services;
using Plainstall.Core.Catalog;

namespace Plainstall.App.ViewModels;

public enum RiskFilter
{
    All,
    Safe,
    Moderate,
    Advanced
}

public sealed record RiskFilterOption(RiskFilter Value, string Title);

/// <summary>One switch: an app to remove or a setting to change.</summary>
public sealed partial class EntryViewModel : ObservableObject
{
    private readonly CatalogListViewModel _owner;
    private bool _isOn;

    internal EntryViewModel(CatalogListViewModel owner, CatalogEntry entry, string categoryTitle)
    {
        _owner = owner;
        Entry = entry;
        CategoryTitle = categoryTitle;
    }

    public CatalogEntry Entry { get; }
    public string Id => Entry.Id;
    public string Title => Entry.Title;
    public string Description => Entry.Description;
    public string? Details => Entry.Details;
    public bool HasDetails => !string.IsNullOrWhiteSpace(Entry.Details);
    public string CategoryTitle { get; }
    public RiskLevel Risk => Entry.Risk;
    public bool IsApp => Entry.Kind == CatalogKind.Apps;

    /// <summary>The badge always says the word, so the risk is never told by colour alone.</summary>
    public string RiskText => Entry.Risk switch
    {
        RiskLevel.Safe => Strings.Risk_Safe,
        RiskLevel.Moderate => Strings.Risk_Moderate,
        _ => Strings.Risk_Advanced
    };

    public string BuildRangeText => (Entry.MinBuild, Entry.MaxBuild) switch
    {
        (null, null) => Strings.Entry_AllBuilds,
        ({ } min, null) => string.Format(CultureInfo.CurrentCulture, Strings.Entry_BuildFrom, min),
        (null, { } max) => string.Format(CultureInfo.CurrentCulture, Strings.Entry_BuildUpTo, max),
        ({ } min, { } max) => string.Format(CultureInfo.CurrentCulture, Strings.Entry_BuildRange, min, max)
    };

    public string? SourceText => Entry.Source.Count == 0
        ? null
        : string.Format(CultureInfo.CurrentCulture, Strings.Entry_Source,
            string.Join(", ", Entry.Source.Select(s => s.Name)));

    public string? ReinstallText => Entry.Reinstall is { } r
        ? string.Format(CultureInfo.CurrentCulture, Strings.Entry_Reinstall,
            r.Note ?? (r.Id is null ? r.Method : r.Method + " " + r.Id))
        : null;

    public string OnText => IsApp ? Strings.Switch_Remove : Strings.Switch_On;
    public string OffText => IsApp ? Strings.Switch_Keep : Strings.Switch_Off;

    /// <summary>For an app: it will be removed. For a tweak: it will be applied.</summary>
    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (value != _isOn) _owner.RequestToggle(this, value);
        }
    }

    public string StateText => IsOn ? OnText : OffText;

    [ObservableProperty] public partial bool IsChanged { get; private set; }

    [ObservableProperty] public partial bool IsExpanded { get; set; }

    /// <summary>The name screen readers announce for the switch: title and what the switch means.</summary>
    public string SwitchName => string.Format(CultureInfo.CurrentCulture,
        IsApp ? Strings.Entry_SwitchNameApp : Strings.Entry_SwitchNameTweak, Title);

    public string DetailsButtonName =>
        string.Format(CultureInfo.CurrentCulture, Strings.Entry_DetailsButtonName, Title);

    internal void Sync(ProfileEditor editor)
    {
        var on = editor.IsOn(Entry);
        if (on != _isOn)
        {
            _isOn = on;
            OnPropertyChanged(nameof(StateText));
        }

        // Raised every time: after a refused change the switch must jump back to the stored value.
        OnPropertyChanged(nameof(IsOn));
        IsChanged = editor.IsChangedFromPreset(Entry);
    }

    internal bool Matches(string search, RiskFilter risk, bool changedOnly)
    {
        if (changedOnly && !IsChanged) return false;
        if (risk != RiskFilter.All && risk != ToFilter(Entry.Risk)) return false;
        if (search.Length == 0) return true;
        return Contains(Title) || Contains(Description) || Contains(Details) || Contains(Id) || Contains(CategoryTitle);

        bool Contains(string? text)
        {
            return text is not null && text.Contains(search, StringComparison.CurrentCultureIgnoreCase);
        }
    }

    private static RiskFilter ToFilter(RiskLevel risk)
    {
        return risk switch
        {
            RiskLevel.Safe => RiskFilter.Safe,
            RiskLevel.Moderate => RiskFilter.Moderate,
            _ => RiskFilter.Advanced
        };
    }
}

public sealed partial class CategoryGroupViewModel : ObservableObject
{
    internal CategoryGroupViewModel(CatalogCategory category)
    {
        Title = category.Title;
        Description = category.Description;
    }

    public string Title { get; }
    public string? Description { get; }
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    internal List<EntryViewModel> All { get; } = [];
    public ObservableCollection<EntryViewModel> Visible { get; } = [];

    [ObservableProperty] public partial bool IsVisible { get; internal set; } = true;
}

/// <summary>The Apps or the Tweaks tab: catalog entries grouped by category, with search and filters.</summary>
public sealed partial class CatalogListViewModel : ObservableObject
{
    private readonly IUiDispatcher _dispatcher;
    private readonly Action<IReadOnlyList<string>> _report;
    private readonly WizardSession _session;

    public CatalogListViewModel(WizardSession session, CatalogKind kind, IUiDispatcher dispatcher,
        Action<IReadOnlyList<string>> report)
    {
        _session = session;
        _dispatcher = dispatcher;
        _report = report;
        Kind = kind;
        RiskFilters =
        [
            new RiskFilterOption(RiskFilter.All, Strings.Filter_AllRisks),
            new RiskFilterOption(RiskFilter.Safe, Strings.Risk_Safe),
            new RiskFilterOption(RiskFilter.Moderate, Strings.Risk_Moderate),
            new RiskFilterOption(RiskFilter.Advanced, Strings.Risk_Advanced)
        ];
        SelectedRiskFilter = RiskFilters[0];

        var categories = kind == CatalogKind.Apps ? session.Catalog.AppCategories : session.Catalog.TweakCategories;
        var entries = kind == CatalogKind.Apps ? session.Catalog.Apps : session.Catalog.Tweaks;
        foreach (var category in categories)
        {
            var group = new CategoryGroupViewModel(category);
            // Entries bound to a Setup option are set on the Setup tab, not by a switch.
            foreach (var entry in entries.Where(e => e.Category == category.Id && e.BoundTo is null))
                group.All.Add(new EntryViewModel(this, entry, category.Title));
            if (group.All.Count > 0) Groups.Add(group);
        }

        Refresh();
    }

    public CatalogKind Kind { get; }

    /// <summary>
    ///     The Tweaks tab explains the "managed by your organization" message that Windows shows while policies
    ///     are set, so that nobody mistakes it for a problem.
    /// </summary>
    public bool ShowPolicyNotice => Kind == CatalogKind.Tweaks;

    public ObservableCollection<CategoryGroupViewModel> Groups { get; } = [];

    public IEnumerable<EntryViewModel> Entries => Groups.SelectMany(g => g.All);

    public IReadOnlyList<RiskFilterOption> RiskFilters { get; }

    [ObservableProperty] public partial string SearchText { get; set; } = "";

    [ObservableProperty] public partial RiskFilterOption SelectedRiskFilter { get; set; }

    [ObservableProperty] public partial bool ChangedOnly { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoMatches), nameof(ResultText))]
    public partial int VisibleCount { get; private set; }

    public bool HasNoMatches => VisibleCount == 0;

    public string ResultText => string.Format(CultureInfo.CurrentCulture, Strings.Filter_ResultCount, VisibleCount,
        Entries.Count());

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SearchText) or nameof(SelectedRiskFilter) or nameof(ChangedOnly)) ApplyFilter();
        base.OnPropertyChanged(e);
    }

    /// <summary>Reads every switch from the profile again.</summary>
    public void Refresh()
    {
        foreach (var entry in Entries) entry.Sync(_session.Editor);
        ApplyFilter();
    }

    internal void RequestToggle(EntryViewModel entry, bool on)
    {
        var result = _session.Editor.Toggle(entry.Entry, on);
        if (result.Messages.Count > 0) _report(result.Messages);
        // Posted, so that a refused change reaches the switch after WPF has finished pushing the click.
        _dispatcher.Post(SyncAll);
    }

    private void SyncAll()
    {
        foreach (var e in Entries) e.Sync(_session.Editor);
        // The "changed" filter must not hide a switch the user just flipped while they look at it.
        if (!ChangedOnly) ApplyFilter();
    }

    public void ApplyFilter()
    {
        var search = SearchText.Trim();
        var risk = SelectedRiskFilter.Value;
        var count = 0;
        foreach (var group in Groups)
        {
            var visible = group.All.Where(e => e.Matches(search, risk, ChangedOnly)).ToList();
            if (!visible.SequenceEqual(group.Visible))
            {
                group.Visible.Clear();
                foreach (var entry in visible) group.Visible.Add(entry);
            }

            group.IsVisible = visible.Count > 0;
            count += visible.Count;
        }

        VisibleCount = count;
    }
}