using Plainstall.Core.Profiles;
using Plainstall.Core.Unattend;

namespace Plainstall.Core.Tests.Unattend;

public class SetupResolverTests
{
    /// <summary>A Swiss PC with a German display language and an English Windows image.</summary>
    private static readonly HostFacts SwissHost = new("de-DE", "de-CH", "0807:00000807", "W. Europe Standard Time");

    private static SetupOptions Options(string region = "auto", string keyboard = "auto", string timezone = "auto")
    {
        return new SetupOptions
        {
            LocalAccount = null,
            SkipPrivacyPages = false,
            BypassHardwareChecks = false,
            Region = region,
            Keyboard = keyboard,
            Timezone = timezone
        };
    }

    [Theory]
    // region, keyboard, timezone -> user locale, input locale, time zone (null = Setup asks / not set)
    [InlineData("auto", "auto", "auto", "de-CH", "0807:00000807", "W. Europe Standard Time")]
    [InlineData("ask", "ask", "auto", null, null, "W. Europe Standard Time")]
    [InlineData("ask", "auto", "auto", null, "0807:00000807", "W. Europe Standard Time")]
    [InlineData("auto", "ask", "auto", "de-CH", null, "W. Europe Standard Time")]
    [InlineData("fr-CH", "fr-CH", "Romance Standard Time", "fr-CH", "fr-CH", "Romance Standard Time")]
    [InlineData("en-GB", "ask", "GMT Standard Time", "en-GB", null, "GMT Standard Time")]
    [InlineData("ask", "en-GB", "UTC", null, "en-GB", "UTC")]
    [InlineData("sr-Latn-RS", "es-419", "UTC+12", "sr-Latn-RS", "es-419", "UTC+12")]
    [InlineData("de", "auto", "ask", "de", "0807:00000807", null)]
    [InlineData("ask", "ask", "ask", null, null, null)]
    public void Resolves_auto_ask_and_explicit_values(string region, string keyboard, string timezone,
        string? userLocale, string? inputLocale, string? timeZone)
    {
        var resolved = SetupResolver.Resolve(Options(region, keyboard, timezone), SwissHost, "en-US");

        Assert.Equal(userLocale, resolved.UserLocale);
        Assert.Equal(inputLocale, resolved.InputLocale);
        Assert.Equal(timeZone, resolved.TimeZone);
        Assert.Equal(userLocale is not null || inputLocale is not null, resolved.HasLocaleSettings);
        Assert.Empty(resolved.Notes);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("fr-FR")]
    [InlineData("sr-Latn-RS")]
    public void Display_language_and_system_locale_are_the_image_language_never_the_host_language(string imageLanguage)
    {
        var resolved = SetupResolver.Resolve(Options(), SwissHost, imageLanguage);

        Assert.Equal(imageLanguage, resolved.UiLanguage);
        Assert.Equal(imageLanguage, resolved.SystemLocale);
        Assert.NotEqual(SwissHost.UiLanguage, resolved.UiLanguage);
    }

    [Fact]
    public void Profile_defaults_mean_auto()
    {
        var defaults = new SetupOptions { LocalAccount = null, SkipPrivacyPages = false, BypassHardwareChecks = false };

        var resolved = SetupResolver.Resolve(defaults, SwissHost, "en-US");

        Assert.Equal("de-CH", resolved.UserLocale);
        Assert.Equal("0807:00000807", resolved.InputLocale);
        Assert.Equal("W. Europe Standard Time", resolved.TimeZone);
    }

    [Theory]
    [InlineData("region", "Auto")]
    [InlineData("region", "ASK")]
    [InlineData("region", "")]
    [InlineData("region", "german")]
    [InlineData("region", "de_CH")]
    [InlineData("region", "DE-ch")]
    [InlineData("region", "de-CH\n")]
    [InlineData("region", "de-CH; Write-Output INJECTED")]
    [InlineData("region", "<UserLocale>")]
    [InlineData("region", "0807:00000807")]
    [InlineData("keyboard", "qwertz")]
    [InlineData("keyboard", "de-CH\r\n")]
    [InlineData("keyboard", "0807:00000807")]
    [InlineData("keyboard", "de-CH;fr-CH")]
    [InlineData("timezone", "")]
    [InlineData("timezone", "   ")]
    [InlineData("timezone", "W. Europe\nStandard Time")]
    [InlineData("timezone", "This time zone id is much longer than any id that Windows has ever used")]
    public void Refuses_explicit_values_that_are_not_valid(string field, string value)
    {
        var options = field switch
        {
            "region" => Options(value),
            "keyboard" => Options(keyboard: value),
            _ => Options(timezone: value)
        };

        var ex = Assert.Throws<PlainstallException>(() => SetupResolver.Resolve(options, SwissHost, "en-US"));

        Assert.Equal(ErrorKind.InvalidInput, ex.Kind);
        Assert.Contains("setup." + field, ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("English")]
    [InlineData("en-US\n")]
    [InlineData("en_US")]
    public void Refuses_an_image_language_that_is_not_a_tag(string imageLanguage)
    {
        var ex = Assert.Throws<PlainstallException>(() => SetupResolver.Resolve(Options(), SwissHost, imageLanguage));

        Assert.Equal(ErrorKind.InvalidInput, ex.Kind);
    }

    [Fact]
    public void A_host_value_that_cannot_be_used_becomes_ask_with_a_note()
    {
        // A custom locale, a keyboard value in no known form, and no time zone at all.
        var oddHost = new HostFacts("en-US", "ca-ES-valencia", "Swiss German", "");

        var resolved = SetupResolver.Resolve(Options(), oddHost, "en-US");

        Assert.Null(resolved.UserLocale);
        Assert.Null(resolved.InputLocale);
        Assert.Null(resolved.TimeZone);
        Assert.False(resolved.HasLocaleSettings);
        Assert.Equal(3, resolved.Notes.Count);
        Assert.Contains(resolved.Notes, n => n.Contains("ca-ES-valencia") && n.Contains("region"));
        Assert.Contains(resolved.Notes, n => n.Contains("keyboard"));
        Assert.Contains(resolved.Notes, n => n.Contains("time zone"));
    }

    [Fact]
    public void A_bad_host_value_does_not_matter_when_the_profile_does_not_use_it()
    {
        var oddHost = new HostFacts("??", "??", "??", "");

        var resolved = SetupResolver.Resolve(Options("fr-CH", "ask", "Romance Standard Time"), oddHost, "fr-FR");

        Assert.Equal("fr-CH", resolved.UserLocale);
        Assert.Null(resolved.InputLocale);
        Assert.Equal("Romance Standard Time", resolved.TimeZone);
        Assert.Empty(resolved.Notes);
    }

    [Fact]
    public void A_host_keyboard_may_be_a_tag_or_a_layout_pair()
    {
        var tagHost = SwissHost with { InputLocale = "de-CH" };
        var pairHost = SwissHost with { InputLocale = "0409:00000807" };

        Assert.Equal("de-CH", SetupResolver.Resolve(Options(), tagHost, "en-US").InputLocale);
        Assert.Equal("0409:00000807", SetupResolver.Resolve(Options(), pairHost, "en-US").InputLocale);
    }

    [Theory]
    [InlineData("en-US", true)]
    [InlineData("de", true)]
    [InlineData("fil-PH", true)]
    [InlineData("zh-Hans-CN", true)]
    [InlineData("es-419", true)]
    [InlineData("EN-US", false)]
    [InlineData("en-us", false)]
    [InlineData("en-US ", false)]
    [InlineData("en-US\n", false)]
    [InlineData("auto", false)]
    // "ask" has the shape of a three-letter language code. It is a keyword and must never pass as a tag.
    [InlineData("ask", false)]
    [InlineData(null, false)]
    public void Language_tag_shape_is_the_profile_schema_pattern(string? value, bool expected)
    {
        Assert.Equal(expected, SetupResolver.IsLanguageTag(value));
    }

    [Theory]
    [InlineData("0409:00000409", true)]
    [InlineData("040c:0000040C", true)]
    [InlineData("de-CH", true)]
    [InlineData("0409:0000409", false)]
    [InlineData("0409-00000409", false)]
    [InlineData("0409:00000409;0407:00000407", false)]
    [InlineData("0409:00000409\n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Input_locale_is_a_tag_or_a_hex_pair(string? value, bool expected)
    {
        Assert.Equal(expected, SetupResolver.IsInputLocale(value));
    }

    [Fact]
    public void Current_host_facts_can_be_resolved()
    {
        var host = HostFacts.Current();

        Assert.False(string.IsNullOrWhiteSpace(host.UiLanguage));
        Assert.False(string.IsNullOrWhiteSpace(host.UserLocale));
        Assert.False(string.IsNullOrWhiteSpace(host.InputLocale));
        Assert.False(string.IsNullOrWhiteSpace(host.TimeZoneId));

        // Whatever this PC is set to, resolving must not throw: unusable values turn into "ask".
        var resolved = SetupResolver.Resolve(Options(), host, "en-US");
        Assert.Equal("en-US", resolved.UiLanguage);
        Assert.NotNull(resolved.TimeZone);
    }
}