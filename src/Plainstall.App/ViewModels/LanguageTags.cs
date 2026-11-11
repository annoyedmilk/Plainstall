using System.Globalization;
using Plainstall.Core.Download;
using Plainstall.Core.Unattend;

namespace Plainstall.App.ViewModels;

/// <summary>
///     Turns a language picked from Microsoft's list into the language tag a profile stores. Microsoft lists
///     names, the profile stores tags, and the engine maps tags back to names with <see cref="LanguageMatcher" />.
///     So a tag is accepted only when the matcher maps it back to exactly the picked language.
/// </summary>
public static class LanguageTags
{
    // Tried first so that the stored tag is the usual one (de-DE rather than de-AT). Each one is still checked.
    private static readonly string[] Preferred =
    [
        "en-US", "en-GB", "pt-BR", "pt-PT", "zh-CN", "zh-TW", "fr-FR", "fr-CA", "es-ES", "es-MX", "sr-Latn-RS", "nb-NO",
        "ar-SA", "bg-BG", "hr-HR", "cs-CZ", "da-DK", "nl-NL", "et-EE", "fi-FI", "de-DE", "el-GR", "he-IL", "hu-HU",
        "it-IT", "ja-JP", "ko-KR", "lv-LV", "lt-LT", "pl-PL", "ro-RO", "ru-RU", "sk-SK", "sl-SI", "sv-SE", "th-TH",
        "tr-TR", "uk-UA"
    ];

    /// <summary>A tag that maps back to <paramref name="language" />, or null when there is none.</summary>
    public static string? TagFor(WindowsLanguage language, IReadOnlyList<WindowsLanguage> offered)
    {
        var candidates = Preferred.Concat(CultureInfo.GetCultures(CultureTypes.SpecificCultures).Select(c => c.Name)
            .Order(StringComparer.Ordinal));

        foreach (var tag in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!SetupResolver.IsLanguageTag(tag)) continue;
            CultureInfo culture;
            try
            {
                culture = CultureInfo.GetCultureInfo(tag);
            }
            catch (CultureNotFoundException)
            {
                continue;
            }

            var match = LanguageMatcher.Match(culture, offered);
            if (match.Kind == LanguageMatchKind.Exact && match.Language == language) return tag;
        }

        return null;
    }
}