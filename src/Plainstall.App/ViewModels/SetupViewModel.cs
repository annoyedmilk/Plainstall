using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Plainstall.App.Resources;
using Plainstall.App.Services;
using Plainstall.Core.Profiles;

namespace Plainstall.App.ViewModels;

public enum ChoiceMode
{
    /// <summary>Same as this PC.</summary>
    Auto,

    /// <summary>Windows Setup asks (time zone: Windows picks one from the region).</summary>
    Ask,

    /// <summary>A value chosen here.</summary>
    Value
}

public sealed record ChoiceModeOption(ChoiceMode Mode, string Title);

/// <summary>The Setup tab: what Windows Setup asks, and what it is told in advance.</summary>
public sealed partial class SetupViewModel : ObservableObject
{
    private readonly WizardSession _session;
    private bool _loading;

    public SetupViewModel(WizardSession session, ILocaleCatalog locales)
    {
        _session = session;
        // Nothing may be written to the profile until it has been read.
        _loading = true;
        Regions = [.. locales.Regions];
        Keyboards = [.. locales.Keyboards];
        TimeZones = [.. locales.TimeZones];
        RegionModes =
        [
            new ChoiceModeOption(ChoiceMode.Auto, Strings.Setup_SameAsThisPc),
            new ChoiceModeOption(ChoiceMode.Ask, Strings.Setup_AskDuringSetup),
            new ChoiceModeOption(ChoiceMode.Value, Strings.Setup_ChooseValue)
        ];
        KeyboardModes = RegionModes;
        TimeZoneModes =
        [
            new ChoiceModeOption(ChoiceMode.Auto, Strings.Setup_SameAsThisPc),
            new ChoiceModeOption(ChoiceMode.Ask, Strings.Setup_TimeZoneFromRegion),
            new ChoiceModeOption(ChoiceMode.Value, Strings.Setup_ChooseValue)
        ];
        RegionMode = RegionModes[0];
        KeyboardMode = KeyboardModes[0];
        TimeZoneMode = TimeZoneModes[0];
        LoadFromProfile();
    }

    public ObservableCollection<LocaleOption> Regions { get; }
    public ObservableCollection<LocaleOption> Keyboards { get; }
    public ObservableCollection<TimeZoneOption> TimeZones { get; }
    public IReadOnlyList<ChoiceModeOption> RegionModes { get; }
    public IReadOnlyList<ChoiceModeOption> KeyboardModes { get; }
    public IReadOnlyList<ChoiceModeOption> TimeZoneModes { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAskPassword), nameof(CanChangeAskPassword))]
    public partial bool LocalAccount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAskPassword), nameof(CanChangeAskPassword))]
    public partial string AccountName { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeAskPassword))]
    public partial bool AskPassword { get; set; }

    /// <summary>A password is only asked for a named account: when Setup asks for the name, it asks for the password too.</summary>
    public bool CanAskPassword => LocalAccount && AccountName.Trim().Length > 0;

    /// <summary>
    ///     True when the password box can be changed. A ticked box stays changeable after the name was cleared, so the
    ///     user can untick it and fix the error.
    /// </summary>
    public bool CanChangeAskPassword => CanAskPassword || AskPassword;

    [ObservableProperty] public partial string ComputerName { get; set; } = "";

    [ObservableProperty] public partial bool SkipPrivacyPages { get; set; }

    [ObservableProperty] public partial bool BypassHardwareChecks { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRegionValue))]
    public partial ChoiceModeOption RegionMode { get; set; }

    public bool IsRegionValue => RegionMode.Mode == ChoiceMode.Value;

    [ObservableProperty] public partial LocaleOption? RegionValue { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsKeyboardValue))]
    public partial ChoiceModeOption KeyboardMode { get; set; }

    public bool IsKeyboardValue => KeyboardMode.Mode == ChoiceMode.Value;

    [ObservableProperty] public partial LocaleOption? KeyboardValue { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTimeZoneValue))]
    public partial ChoiceModeOption TimeZoneMode { get; set; }

    public bool IsTimeZoneValue => TimeZoneMode.Mode == ChoiceMode.Value;

    [ObservableProperty] public partial TimeZoneOption? TimeZoneValue { get; set; }

    [ObservableProperty] public partial string? AccountNameError { get; private set; }

    [ObservableProperty] public partial string? AskPasswordError { get; private set; }

    [ObservableProperty] public partial string? ComputerNameError { get; private set; }

    public bool HasErrors =>
        AccountNameError is not null || AskPasswordError is not null || ComputerNameError is not null;

    public event EventHandler? ValidationChanged;

    /// <summary>The plain fields write themselves into the profile as soon as they change.</summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LocalAccount) or nameof(AccountName) or nameof(AskPassword) or nameof(ComputerName)
            or nameof(SkipPrivacyPages) or nameof(BypassHardwareChecks)
            or nameof(RegionValue) or nameof(KeyboardValue) or nameof(TimeZoneValue))
            Apply();
        base.OnPropertyChanged(e);
    }

    partial void OnRegionModeChanged(ChoiceModeOption value)
    {
        if (value.Mode == ChoiceMode.Value && RegionValue is null && !_loading)
            RegionValue = Regions.FirstOrDefault(r => r.Tag == CultureInfo.CurrentCulture.Name) ??
                          Regions.FirstOrDefault();
        Apply();
    }

    partial void OnKeyboardModeChanged(ChoiceModeOption value)
    {
        if (value.Mode == ChoiceMode.Value && KeyboardValue is null && !_loading)
            KeyboardValue = Keyboards.FirstOrDefault(r => r.Tag == CultureInfo.CurrentCulture.Name) ??
                            Keyboards.FirstOrDefault();
        Apply();
    }

    partial void OnTimeZoneModeChanged(ChoiceModeOption value)
    {
        if (value.Mode == ChoiceMode.Value && TimeZoneValue is null && !_loading)
            TimeZoneValue = TimeZones.FirstOrDefault(t => t.Id == TimeZoneInfo.Local.Id) ?? TimeZones.FirstOrDefault();
        Apply();
    }

    public void LoadFromProfile()
    {
        var setup = _session.Profile.Setup;
        _loading = true;
        try
        {
            LocalAccount = setup.LocalAccount is not null;
            AccountName = setup.LocalAccount?.Name ?? (LocalAccount ? "" : AccountName);
            AskPassword = setup.LocalAccount?.AskPassword ?? false;
            ComputerName = setup.ComputerName ?? "";
            SkipPrivacyPages = setup.SkipPrivacyPages;
            BypassHardwareChecks = setup.BypassHardwareChecks;
            (RegionMode, RegionValue) = ReadChoice(setup.Region, RegionModes, Regions, o => o.Tag,
                StringComparison.OrdinalIgnoreCase, s => new LocaleOption(s, s));
            (KeyboardMode, KeyboardValue) = ReadChoice(setup.Keyboard, KeyboardModes, Keyboards, o => o.Tag,
                StringComparison.OrdinalIgnoreCase, s => new LocaleOption(s, s));
            (TimeZoneMode, TimeZoneValue) = ReadChoice(setup.Timezone, TimeZoneModes, TimeZones, o => o.Id,
                StringComparison.Ordinal, s => new TimeZoneOption(s, s));
        }
        finally
        {
            _loading = false;
        }

        Validate();
    }

    private void Apply()
    {
        if (_loading) return;
        var name = AccountName.Trim();
        _session.Editor.Update(p => p with
        {
            Setup = new SetupOptions
            {
                LocalAccount = LocalAccount
                    ? new LocalAccountOptions { Name = name.Length == 0 ? null : name, AskPassword = AskPassword }
                    : null,
                ComputerName = ComputerName.Trim().Length == 0 ? null : ComputerName.Trim(),
                SkipPrivacyPages = SkipPrivacyPages,
                BypassHardwareChecks = BypassHardwareChecks,
                Region = WriteChoice(RegionMode, RegionValue?.Tag),
                Keyboard = WriteChoice(KeyboardMode, KeyboardValue?.Tag),
                Timezone = WriteChoice(TimeZoneMode, TimeZoneValue?.Id)
            }
        });
        Validate();
    }

    /// <summary>The engine's own rules decide. The words come from the app.</summary>
    private void Validate()
    {
        var problems = ProfileSerializer.Validate(_session.Profile);
        var setup = _session.Profile.Setup;
        AccountNameError = problems.Any(p => p.StartsWith("setup.localAccount.name", StringComparison.Ordinal))
            ? setup.LocalAccount?.Name switch
            {
                { } name when ProfileSerializer.IsReservedAccountName(name) => Strings.Setup_AccountNameReserved,
                { } name when string.Equals(name, setup.ComputerName, StringComparison.OrdinalIgnoreCase) =>
                    Strings.Setup_AccountNameSameAsComputer,
                _ => Strings.Setup_AccountNameInvalid
            }
            : null;
        AskPasswordError = problems.Any(p => p.StartsWith("setup.localAccount.askPassword", StringComparison.Ordinal))
            ? Strings.Setup_AskPasswordNeedsName
            : null;
        ComputerNameError = problems.Any(p => p.StartsWith("setup.computerName", StringComparison.Ordinal))
            ? Strings.Setup_ComputerNameInvalid
            : null;
        OnPropertyChanged(nameof(HasErrors));
        ValidationChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string WriteChoice(ChoiceModeOption mode, string? value)
    {
        return mode.Mode switch
        {
            ChoiceMode.Auto => "auto",
            ChoiceMode.Ask => "ask",
            _ => value ?? "auto"
        };
    }

    /// <summary>The mode and the option for a stored value. A value that is not in the list is added to it.</summary>
    private static (ChoiceModeOption, T?) ReadChoice<T>(string stored, IReadOnlyList<ChoiceModeOption> modes,
        ObservableCollection<T> options, Func<T, string> key, StringComparison comparison, Func<string, T> create)
        where T : class
    {
        return stored switch
        {
            "auto" => (modes[0], null),
            "ask" => (modes[1], null),
            _ => (modes[2], options.FirstOrDefault(o => string.Equals(key(o), stored, comparison)) ??
                            Add(options, create(stored)))
        };
    }

    private static T Add<T>(ObservableCollection<T> list, T item)
    {
        list.Add(item);
        return item;
    }
}