using System.Globalization;
using Plainstall.Core.Download;

namespace Plainstall.Core.Tests.Download;

public sealed class LanguageMatcherTests
{
    [Theory]
    // culture, expected LocalizedLanguage
    [InlineData("en-US", "English (United States)")]
    [InlineData("en", "English (United States)")]
    [InlineData("en-GB", "English International")]
    [InlineData("en-AU", "English International")]
    [InlineData("en-IN", "English International")]
    [InlineData("en-CA", "English International")]
    [InlineData("de-DE", "German")]
    [InlineData("de-AT", "German")]
    [InlineData("de-CH", "German")]
    [InlineData("de", "German")]
    [InlineData("fr-FR", "French")]
    [InlineData("fr-BE", "French")]
    [InlineData("fr-CH", "French")]
    [InlineData("fr-CA", "French Canadian")]
    [InlineData("pt-BR", "Brazilian Portuguese")]
    [InlineData("pt-PT", "Portuguese")]
    [InlineData("pt", "Portuguese")]
    [InlineData("zh-CN", "Chinese (Simplified)")]
    [InlineData("zh-SG", "Chinese (Simplified)")]
    [InlineData("zh-Hans", "Chinese (Simplified)")]
    [InlineData("zh-Hans-CN", "Chinese (Simplified)")]
    [InlineData("zh-Hans-HK", "Chinese (Simplified)")]
    [InlineData("zh-TW", "Chinese (Traditional)")]
    [InlineData("zh-HK", "Chinese (Traditional)")]
    [InlineData("zh-MO", "Chinese (Traditional)")]
    [InlineData("zh-Hant", "Chinese (Traditional)")]
    [InlineData("zh-Hant-TW", "Chinese (Traditional)")]
    [InlineData("es-ES", "Spanish")]
    [InlineData("es-AR", "Spanish")]
    [InlineData("es-MX", "Spanish (Mexico)")]
    [InlineData("es-419", "Spanish (Mexico)")]
    [InlineData("sr-Latn-RS", "Serbian Latin")]
    [InlineData("sr-Latn", "Serbian Latin")]
    [InlineData("nb-NO", "Norwegian")]
    [InlineData("ja-JP", "Japanese")]
    [InlineData("uk-UA", "Ukrainian")]
    [InlineData("he-IL", "Hebrew")]
    [InlineData("ar-SA", "Arabic")]
    [InlineData("ar-EG", "Arabic")]
    public void Maps_display_languages_to_offered_languages(string cultureName, string expected)
    {
        var match = LanguageMatcher.Match(CultureInfo.GetCultureInfo(cultureName), Languages.All);

        Assert.Equal(expected, match.Language.LocalizedLanguage);
        Assert.Equal(LanguageMatchKind.Exact, match.Kind);
        Assert.Null(match.Message);
    }

    [Theory]
    [InlineData("ca-ES")] // Catalan: a Windows display language without an ISO
    [InlineData("eu-ES")]
    [InlineData("vi-VN")]
    [InlineData("hi-IN")]
    [InlineData("sr-Cyrl-RS")] // Serbian exists in Latin script only
    [InlineData("is-IS")]
    public void Falls_back_to_us_english_and_says_so(string cultureName)
    {
        var match = LanguageMatcher.Match(CultureInfo.GetCultureInfo(cultureName), Languages.All);

        Assert.Same(Languages.EnglishUs, match.Language);
        Assert.Equal(LanguageMatchKind.Fallback, match.Kind);
        Assert.NotNull(match.Message);
        Assert.Contains("English (United States)", match.Message);
        Assert.Contains("does not offer", match.Message);
    }

    [Fact]
    public void The_invariant_culture_falls_back_too()
    {
        var match = LanguageMatcher.Match(CultureInfo.InvariantCulture, Languages.All);

        Assert.Same(Languages.EnglishUs, match.Language);
        Assert.Equal(LanguageMatchKind.Fallback, match.Kind);
    }

    [Fact]
    public void British_english_is_matched_on_the_localized_name()
    {
        // The service calls it "English (United Kingdom)" in Language and "English International" in
        // LocalizedLanguage. A matcher that only knew one of the two names would miss it.
        var match = LanguageMatcher.Match(CultureInfo.GetCultureInfo("en-GB"), Languages.All);
        Assert.Same(Languages.EnglishInternational, match.Language);

        // And it still works if the service ever swaps the two fields.
        WindowsLanguage swapped = new("6", "English International", "English (United Kingdom)");
        match = LanguageMatcher.Match(CultureInfo.GetCultureInfo("en-GB"), [Languages.EnglishUs, swapped]);
        Assert.Same(swapped, match.Language);
        Assert.Equal(LanguageMatchKind.Exact, match.Kind);
    }

    [Fact]
    public void Us_english_is_found_by_either_of_its_names()
    {
        WindowsLanguage plain = new("5", "English", "English");
        var match = LanguageMatcher.Match(CultureInfo.GetCultureInfo("en-US"), [Languages.EnglishInternational, plain]);

        Assert.Same(plain, match.Language);
        Assert.Equal(LanguageMatchKind.Exact, match.Kind);
    }

    [Theory]
    [InlineData("fr-CA", "French Canadian", "French")]
    [InlineData("es-MX", "Spanish (Mexico)", "Spanish")]
    [InlineData("pt-BR", "Brazilian Portuguese", "Portuguese")]
    [InlineData("en-GB", "English International", "English (United States)")]
    public void A_missing_regional_variant_gives_the_related_language_with_a_message(string cultureName, string missing,
        string expected)
    {
        List<WindowsLanguage> offered = [.. Languages.All.Where(l => l.LocalizedLanguage != missing)];

        var match = LanguageMatcher.Match(CultureInfo.GetCultureInfo(cultureName), offered);

        Assert.Equal(expected, match.Language.LocalizedLanguage);
        Assert.Equal(LanguageMatchKind.Related, match.Kind);
        Assert.NotNull(match.Message);
        Assert.Contains(expected, match.Message);
    }

    [Fact]
    public void Nynorsk_gets_the_norwegian_iso_as_a_related_language()
    {
        var match = LanguageMatcher.Match(CultureInfo.GetCultureInfo("nn-NO"), Languages.All);

        Assert.Same(Languages.Norwegian, match.Language);
        Assert.Equal(LanguageMatchKind.Related, match.Kind);
    }

    [Fact]
    public void Names_are_compared_without_regard_to_punctuation_and_case()
    {
        WindowsLanguage simplified = new("3", "Chinese Simplified", "chinese  simplified");
        var match = LanguageMatcher.Match(CultureInfo.GetCultureInfo("zh-CN"), [Languages.EnglishUs, simplified]);

        Assert.Same(simplified, match.Language);
    }

    [Fact]
    public void Without_us_english_in_the_list_the_fallback_is_still_some_language()
    {
        var match = LanguageMatcher.Match(CultureInfo.GetCultureInfo("ca-ES"),
            [Languages.German, Languages.EnglishInternational]);
        Assert.Same(Languages.EnglishInternational, match.Language);
        Assert.Equal(LanguageMatchKind.Fallback, match.Kind);

        match = LanguageMatcher.Match(CultureInfo.GetCultureInfo("ca-ES"), [Languages.German]);
        Assert.Same(Languages.German, match.Language);
        Assert.Equal(LanguageMatchKind.Fallback, match.Kind);
        Assert.Contains("German", match.Message);
    }

    [Fact]
    public void An_empty_list_is_a_programming_error()
    {
        Assert.Throws<ArgumentException>(() => LanguageMatcher.Match(CultureInfo.GetCultureInfo("de-DE"), []));
    }

    [Theory]
    [InlineData("de-DE", "German")]
    [InlineData("pt-br", "Brazilian Portuguese")]
    [InlineData("German", "German")]
    [InlineData("  german ", "German")]
    [InlineData("English International", "English International")]
    [InlineData("English (United Kingdom)", "English International")]
    [InlineData("English", "English (United States)")]
    [InlineData("Chinese Simplified", "Chinese (Simplified)")]
    public void Text_input_can_be_a_tag_or_an_offered_name(string text, string expected)
    {
        var match = LanguageMatcher.Match(text, Languages.All);

        Assert.Equal(expected, match.Language.LocalizedLanguage);
        Assert.Equal(LanguageMatchKind.Exact, match.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Klingon")]
    [InlineData("xx-YY-zz-not-a-tag")]
    public void Unknown_text_is_an_input_error_not_a_silent_fallback(string text)
    {
        var ex = Assert.ThrowsAny<PlainstallException>(() => LanguageMatcher.Match(text, Languages.All));
        Assert.Equal(ErrorKind.InvalidInput, ex.Kind);
    }

    [Fact]
    public void FindByName_returns_null_for_names_that_are_not_offered()
    {
        Assert.Null(LanguageMatcher.FindByName(Languages.All, "Klingon"));
        Assert.Null(LanguageMatcher.FindByName(Languages.All, ""));
        Assert.Same(Languages.SerbianLatin, LanguageMatcher.FindByName(Languages.All, "Serbian (Latin)"));
    }
}