using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Plainstall.App.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Plainstall.App.Themes;

/// <summary>The palette of the app. Every colour is from Sanzo Wada's "A Dictionary of Color Combinations".</summary>
public static class BrandColors
{
    public static readonly Color Paper = Color.FromRgb(0xF5, 0xEC, 0xC2); // Sulphur Yellow
    public static readonly Color Ink = Color.FromRgb(0x11, 0x13, 0x14); // Black
    public static readonly Color HelvetiaBlue = Color.FromRgb(0x00, 0x5B, 0x8D);
    public static readonly Color Blue = Color.FromRgb(0x00, 0x6E, 0xB8);
    public static readonly Color Carmine = Color.FromRgb(0xCC, 0x12, 0x36);
    public static readonly Color YellowOcher = Color.FromRgb(0xE2, 0xB5, 0x40);
    public static readonly Color WarmGray = Color.FromRgb(0xA1, 0xA3, 0x9A);

    public static Color WithAlpha(this Color color, byte alpha)
    {
        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }
}

/// <summary>
///     Applies the WPF-UI theme and then the brand on top of it. The brand replaces WPF-UI's own accent and
///     text colours so that the contrast rules of the brand hold. On paper or white, text is Ink or Helvetia
///     Blue. On Ink (dark), text is Sulphur Yellow, Yellow Ocher or Warm Gray. High contrast is left to Windows.
/// </summary>
public sealed class ThemeSwitcher : IThemeSwitcher
{
    private ThemeChoice _choice = ThemeChoice.System;
    private bool _listening;

    public void Apply(ThemeChoice choice)
    {
        _choice = choice;
        if (choice == ThemeChoice.System && !_listening)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _listening = true;
        }

        ApplyCurrent();
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_choice == ThemeChoice.System && e.Category is UserPreferenceCategory.General
                or UserPreferenceCategory.Color
                or UserPreferenceCategory.Accessibility) Application.Current?.Dispatcher.BeginInvoke(ApplyCurrent);
    }

    private void ApplyCurrent()
    {
        if (SystemParameters.HighContrast)
        {
            ApplicationThemeManager.Apply(ApplicationTheme.HighContrast, WindowBackdropType.None, false);
            return;
        }

        var dark = _choice switch
        {
            ThemeChoice.Dark => true,
            ThemeChoice.Light => false,
            _ => ApplicationThemeManager.GetSystemTheme() is SystemTheme.Dark or SystemTheme.Glow
                or SystemTheme.CapturedMotion
        };
        ApplicationThemeManager.Apply(dark ? ApplicationTheme.Dark : ApplicationTheme.Light, WindowBackdropType.None,
            false);
        BrandTheme.Apply(Application.Current.Resources, dark);
        foreach (Window window in Application.Current.Windows) BrandTheme.UsePageBackground(window);
    }
}

public static class BrandTheme
{
    public static void Apply(ResourceDictionary resources, bool dark)
    {
        var accentFill = dark ? BrandColors.Blue : BrandColors.HelvetiaBlue;
        var accentHover = dark ? BrandColors.HelvetiaBlue : BrandColors.Blue;
        ApplicationAccentColorManager.Apply(accentFill, accentFill, accentFill, accentHover);

        var text = dark ? BrandColors.Paper : BrandColors.Ink;
        var textSecondary = dark ? BrandColors.WarmGray : BrandColors.Ink.WithAlpha(0xD0);
        var textTertiary = dark ? BrandColors.WarmGray.WithAlpha(0xE0) : BrandColors.Ink.WithAlpha(0xB8);
        var accentText = dark ? BrandColors.YellowOcher : BrandColors.HelvetiaBlue;
        var background = dark ? BrandColors.Ink : BrandColors.Paper;
        var onAccent = Colors.White;

        Set(resources, "ApplicationBackground", background);
        Set(resources, "TextFillColorPrimary", text);
        Set(resources, "TextFillColorSecondary", textSecondary);
        Set(resources, "TextFillColorTertiary", textTertiary);
        Set(resources, "TextPlaceholderColor", textTertiary);
        resources["ApplicationBackgroundColor"] = background;
        SetBrush(resources, "TextControlForeground", text);
        SetBrush(resources, "TextControlPlaceholderForeground", textTertiary);
        Set(resources, "TextOnAccentFillColorPrimary", onAccent);
        Set(resources, "TextOnAccentFillColorSecondary", onAccent.WithAlpha(0xD0));
        SetBrush(resources, "AccentButtonForeground", onAccent);
        SetBrush(resources, "AccentButtonForegroundPointerOver", onAccent);
        SetBrush(resources, "AccentButtonForegroundPressed", onAccent.WithAlpha(0xD0));
        Set(resources, "AccentTextFillColorPrimary", accentText);
        Set(resources, "AccentTextFillColorSecondary", accentText);
        Set(resources, "AccentTextFillColorTertiary", accentText);
        Set(resources, "AccentFillColorDefault", accentFill);
        Set(resources, "AccentFillColorSecondary", accentHover);
        Set(resources, "AccentFillColorTertiary", accentFill.WithAlpha(0xCC));
        Set(resources, "TextControlFocusedBorder", accentFill);
        // WPF-UI resolves these once from its own accent. Setting them makes every control follow the brand.
        foreach (var key in new[]
                 {
                     "ToggleSwitchKnobFillOn", "ToggleSwitchKnobFillOnPointerOver", "ToggleSwitchKnobFillOnPressed",
                     "CheckBoxCheckGlyphForeground"
                 }) SetBrush(resources, key, onAccent);
        foreach (var key in new[]
                 {
                     "ToggleSwitchFillOn", "ToggleSwitchStrokeOn", "ToggleSwitchFillOnPointerOver",
                     "ToggleSwitchStrokeOnPointerOver", "CheckBoxCheckBackgroundFillChecked",
                     "CheckBoxCheckBackgroundFillCheckedPointerOver"
                 })
            SetBrush(resources, key, key.EndsWith("PointerOver", StringComparison.Ordinal) ? accentHover : accentFill);
        SetBrush(resources, "HyperlinkButtonForeground", accentText);
        SetBrush(resources, "HyperlinkButtonForegroundPointerOver", accentText);
        SetBrush(resources, "HyperlinkButtonForegroundPressed", accentText.WithAlpha(0xC0));

        // The app's own keys, used by Brand.xaml.
        Set(resources, "Plainstall.PageBackground", background);
        Set(resources, "Plainstall.Text", text);
        Set(resources, "Plainstall.TextSecondary", textSecondary);
        Set(resources, "Plainstall.CardSurface", dark ? Color.FromRgb(0x1C, 0x1F, 0x20) : Colors.White);
        Set(resources, "Plainstall.CardBorder",
            dark ? BrandColors.WarmGray.WithAlpha(0x50) : BrandColors.WarmGray.WithAlpha(0x90));
        Set(resources, "Plainstall.Accent", accentFill);
        Set(resources, "Plainstall.AccentText", accentText);
        Set(resources, "Plainstall.InfoStripe", dark ? BrandColors.Blue : BrandColors.HelvetiaBlue);
        Set(resources, "Plainstall.CautionStripe", BrandColors.YellowOcher);
        // Carmine is kept for errors and for erasing data, nothing else.
        Set(resources, "Plainstall.Danger", BrandColors.Carmine);
        Set(resources, "Plainstall.WordmarkLetters", dark ? BrandColors.Paper : BrandColors.Ink);
        Set(resources, "Plainstall.WordmarkI", BrandColors.Carmine);
        Set(resources, "Plainstall.WordmarkN", BrandColors.YellowOcher);
        Set(resources, "Plainstall.BadgeModerate", BrandColors.YellowOcher);
        Set(resources, "Plainstall.BadgeModerateText", BrandColors.Ink);
        Set(resources, "Plainstall.BadgeAdvanced", dark ? BrandColors.Paper : BrandColors.Ink);
        Set(resources, "Plainstall.BadgeAdvancedText", dark ? BrandColors.Ink : BrandColors.Paper);
        Set(resources, "Plainstall.BadgeSafeBorder", dark ? BrandColors.WarmGray : BrandColors.Ink.WithAlpha(0x90));
        Set(resources, "Plainstall.StepDone", accentFill);
        Set(resources, "Plainstall.StepUpcoming", dark ? BrandColors.WarmGray.WithAlpha(0x80) : BrandColors.WarmGray);
        Set(resources, "Plainstall.Subtle",
            dark ? Color.FromArgb(0x10, 0xF5, 0xEC, 0xC2) : Color.FromArgb(0x0C, 0x11, 0x13, 0x14));
    }

    private static void SetBrush(ResourceDictionary resources, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }

    /// <summary>WPF-UI gives each window a fixed background when the theme changes. The brand background follows the theme instead.</summary>
    public static void UsePageBackground(Window window)
    {
        window.SetResourceReference(Control.BackgroundProperty, "Plainstall.PageBackgroundBrush");
    }

    /// <summary>Sets a colour and its brush, the way WPF-UI names them ("X" and "XBrush").</summary>
    private static void Set(ResourceDictionary resources, string key, Color color)
    {
        resources[key] = color;
        SetBrush(resources, key + "Brush", color);
    }
}