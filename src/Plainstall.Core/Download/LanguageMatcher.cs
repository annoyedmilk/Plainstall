using System.Globalization;

namespace Plainstall.Core.Download;

/// <summary>How close the chosen ISO language is to the language that was asked for.</summary>
public enum LanguageMatchKind
{
    /// <summary>Microsoft offers an ISO in exactly this language.</summary>
    Exact,

    /// <summary>No ISO for this regional variant, but one for the same language (French for French Canadian, for example).</summary>
    Related,

    /// <summary>No ISO in this language at all. English (United States) is used instead.</summary>
    Fallback
}

/// <summary>
///     The result of mapping a PC's display language to an ISO language. <see cref="Message" /> is null
///     for an exact match; otherwise it is a sentence for the user that says what was chosen and why.
/// </summary>
public sealed record LanguageMatch(WindowsLanguage Language, LanguageMatchKind Kind, string? Message);

/// <summary>
///     Maps the Windows display language of the build PC to one of the languages Microsoft offers.
///     Microsoft publishes names, not language tags, so the mapping is a table of names. It is matched
///     against the localized name first, because that is the stable one: the other name field has
///     changed before (British English went from "English International" to "English (United Kingdom)").
/// </summary>
public static class LanguageMatcher
{
    private static readonly string[] UnitedStates = ["English (United States)", "English"];
    private static readonly string[] EnglishInternational = ["English International", "English (United Kingdom)"];

    /// <summary>ISO languages that need no special handling: two-letter language code to Microsoft's names.</summary>
    private static readonly Dictionary<string, string[]> Simple = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ar"] = ["Arabic"],
        ["bg"] = ["Bulgarian"],
        ["hr"] = ["Croatian"],
        ["cs"] = ["Czech"],
        ["da"] = ["Danish"],
        ["nl"] = ["Dutch"],
        ["et"] = ["Estonian"],
        ["fi"] = ["Finnish"],
        ["de"] = ["German"],
        ["el"] = ["Greek"],
        ["he"] = ["Hebrew"],
        ["iw"] = ["Hebrew"],
        ["hu"] = ["Hungarian"],
        ["it"] = ["Italian"],
        ["ja"] = ["Japanese"],
        ["ko"] = ["Korean"],
        ["lv"] = ["Latvian"],
        ["lt"] = ["Lithuanian"],
        ["nb"] = ["Norwegian", "Norwegian (Bokmal)", "Norwegian Bokmal"],
        ["no"] = ["Norwegian", "Norwegian (Bokmal)", "Norwegian Bokmal"],
        ["pl"] = ["Polish"],
        ["ro"] = ["Romanian"],
        ["ru"] = ["Russian"],
        ["sk"] = ["Slovak"],
        ["sl"] = ["Slovenian"],
        ["sv"] = ["Swedish"],
        ["th"] = ["Thai"],
        ["tr"] = ["Turkish"],
        ["uk"] = ["Ukrainian"]
    };

    /// <summary>
    ///     Chooses the offered language for a display language. Never returns null for a non-empty list:
    ///     when nothing fits, the result is English (United States) with a message that says so.
    /// </summary>
    public static LanguageMatch Match(CultureInfo culture, IReadOnlyList<WindowsLanguage> offered)
    {
        ArgumentNullException.ThrowIfNull(culture);
        ArgumentNullException.ThrowIfNull(offered);
        if (offered.Count == 0) throw new ArgumentException("The list of offered languages is empty.", nameof(offered));

        var (exact, related) = NamesFor(culture);

        if (exact is not null && Find(offered, exact) is { } exactMatch)
            return new LanguageMatch(exactMatch, LanguageMatchKind.Exact, null);

        var wanted = DescribeCulture(culture);
        if (related is not null && Find(offered, related) is { } relatedMatch)
            return new LanguageMatch(
                relatedMatch,
                LanguageMatchKind.Related,
                $"Microsoft does not offer a Windows download in {wanted}. The closest one, {relatedMatch.DisplayName}, will be used.");

        var fallback = Find(offered, UnitedStates) ?? Find(offered, EnglishInternational) ?? offered[0];
        return new LanguageMatch(
            fallback,
            LanguageMatchKind.Fallback,
            $"Microsoft does not offer a Windows download in {wanted}. {fallback.DisplayName} will be used instead. " +
            "You can pick another language from the list, or add your language in Windows Settings after installing.");
    }

    /// <summary>
    ///     Resolves what a person typed or a profile stores: "auto"-style language tags ("de-DE", "pt-BR")
    ///     or one of the offered names ("German"). Unknown text is an input error, not a silent fallback.
    /// </summary>
    public static LanguageMatch Match(string languageTagOrName, IReadOnlyList<WindowsLanguage> offered)
    {
        ArgumentNullException.ThrowIfNull(languageTagOrName);
        ArgumentNullException.ThrowIfNull(offered);

        var text = languageTagOrName.Trim();
        if (FindByName(offered, text) is { } named) return new LanguageMatch(named, LanguageMatchKind.Exact, null);

        CultureInfo culture;
        try
        {
            culture = CultureInfo.GetCultureInfo(text, true);
        }
        catch (Exception ex) when (ex is CultureNotFoundException or ArgumentException)
        {
            throw new PlainstallException(
                ErrorKind.InvalidInput,
                $"\"{Shorten(text)}\" is not a language Plainstall knows. Use a language tag such as en-US or de-DE, " +
                "or one of the language names Microsoft offers.",
                ex);
        }

        if (culture.Name.Length == 0)
            throw new PlainstallException(
                ErrorKind.InvalidInput,
                "No language was given. Use a language tag such as en-US or de-DE, or one of the language names Microsoft offers.");

        return Match(culture, offered);
    }

    /// <summary>Finds an offered language by its name, as shown in a picker. Localized name first, then the second name.</summary>
    public static WindowsLanguage? FindByName(IReadOnlyList<WindowsLanguage> offered, string name)
    {
        ArgumentNullException.ThrowIfNull(offered);
        var key = HtmlText.NameKey(name);
        return key.Length == 0 ? null : Find(offered, [name]);
    }

    // Two passes on purpose. "English" is the second name of the US entry, and must not win over
    // an entry whose localized name is a better match further down the candidate list.
    private static WindowsLanguage? Find(IReadOnlyList<WindowsLanguage> offered, string[] names)
    {
        return FindBy(offered, names, language => language.LocalizedLanguage) ??
               FindBy(offered, names, language => language.Language);
    }

    /// <summary>The first offered language whose chosen name field matches one of the names, trying the names in order.</summary>
    private static WindowsLanguage? FindBy(IReadOnlyList<WindowsLanguage> offered, string[] names,
        Func<WindowsLanguage, string?> nameOf)
    {
        return names
            .Select(HtmlText.NameKey)
            .SelectMany(key => offered.Where(language => HtmlText.NameKey(nameOf(language)) == key))
            .FirstOrDefault();
    }

    /// <summary>The names to look for: the exact ISO language, and a same-language relative when the exact one is missing.</summary>
    private static (string[]? Exact, string[]? Related) NamesFor(CultureInfo culture)
    {
        var parts = culture.Name.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return (null, null);

        var language = parts[0].ToLowerInvariant();

        bool Has(string subtag)
        {
            return parts.Skip(1).Any(p => p.Equals(subtag, StringComparison.OrdinalIgnoreCase));
        }

        switch (language)
        {
            case "en":
                // Only en-US gets the US image. Every other English (GB, AU, IN, CA...) gets the
                // international one, which uses British spelling and day-first dates.
                return parts.Length == 1 || Has("US")
                    ? (UnitedStates, EnglishInternational)
                    : (EnglishInternational, UnitedStates);

            case "pt":
                return Has("BR")
                    ? (["Brazilian Portuguese", "Portuguese (Brazil)"], ["Portuguese", "Portuguese (Portugal)"])
                    : (["Portuguese", "Portuguese (Portugal)"], ["Brazilian Portuguese", "Portuguese (Brazil)"]);

            case "zh":
                // Script decides; where there is no script subtag the region implies it.
                var traditional = Has("Hant") || (!Has("Hans") && (Has("TW") || Has("HK") || Has("MO")));
                return traditional
                    ? (["Chinese (Traditional)", "Chinese Traditional"], null)
                    : (["Chinese (Simplified)", "Chinese Simplified"], null);

            case "fr":
                return Has("CA")
                    ? (["French Canadian", "French (Canada)", "Canadian French"], ["French", "French (France)"])
                    : (["French", "French (France)"], ["French Canadian", "French (Canada)", "Canadian French"]);

            case "es":
                return Has("MX") || Has("419")
                    ? (["Spanish (Mexico)", "Mexican Spanish"], ["Spanish", "Spanish (Spain)"])
                    : (["Spanish", "Spanish (Spain)"], ["Spanish (Mexico)", "Mexican Spanish"]);

            case "sr":
                // Microsoft offers Serbian in Latin script only. A Cyrillic display language has no ISO.
                return Has("Latn") ? (["Serbian Latin", "Serbian (Latin)"], null) : (null, null);

            case "nn":
                // Nynorsk has no ISO of its own; the Norwegian one is Bokmal.
                return (null, Simple["nb"]);

            default:
                return Simple.TryGetValue(language, out var names) ? (names, null) : (null, null);
        }
    }

    private static string DescribeCulture(CultureInfo culture)
    {
        var name = culture.EnglishName;
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase))
            name = culture.Name.Length != 0 ? culture.Name : "this PC's display language";

        return name;
    }

    private static string Shorten(string text)
    {
        return text.Length <= 40 ? text : string.Concat(text.AsSpan(0, 40), "...");
    }
}