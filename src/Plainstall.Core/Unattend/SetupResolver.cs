using System.Text.RegularExpressions;
using Plainstall.Core.Profiles;

namespace Plainstall.Core.Unattend;

/// <summary>
///     The regional answers for Windows Setup after "auto" and "ask" have been worked out.
///     A null value means "Plainstall writes nothing here". Each property tells what Windows then does.
/// </summary>
public sealed record ResolvedSetup
{
    /// <summary>
    ///     Display language for Setup and for the installed Windows. Always the image's own language:
    ///     an answer file can only name a language that is inside the image, and the build PC's display
    ///     language is not, unless it happens to be the same. Written only together with at least one of
    ///     <see cref="UserLocale" /> and <see cref="InputLocale" />.
    /// </summary>
    public required string UiLanguage { get; init; }

    /// <summary>
    ///     Language for programs that do not use Unicode. Kept equal to <see cref="UiLanguage" />, which is
    ///     what the untouched image would use. It is not taken from the region: some regional formats are
    ///     not valid system locales, and an invalid value stops Setup, while a "wrong" one is a setting the
    ///     user can change later. Written only together with <see cref="UiLanguage" />.
    /// </summary>
    public required string SystemLocale { get; init; }

    /// <summary>Regional format, for example <c>de-CH</c>. Null: the "country or region" page of Setup stays.</summary>
    public string? UserLocale { get; init; }

    /// <summary>
    ///     Keyboard, as a language tag or as <c>0409:00000807</c>. Null: the keyboard page of Setup stays.
    /// </summary>
    public string? InputLocale { get; init; }

    /// <summary>Windows time zone id. Null: Windows keeps its own default for the region.</summary>
    public string? TimeZone { get; init; }

    /// <summary>Things the user should know, for example a host value that could not be used. Never an error.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>
    ///     True when the answer file gets the international components at all. With both region and
    ///     keyboard on "ask" nothing is written, so Setup behaves exactly as it does without Plainstall.
    /// </summary>
    public bool HasLocaleSettings => UserLocale is not null || InputLocale is not null;
}

/// <summary>
///     Works out the regional part of the answer file from the profile, the build PC and the image.
///     A pure function: it reads no environment, so the same inputs always give the same answer file.
/// </summary>
public static partial class SetupResolver
{
    /// <summary>Profile value: use the build PC's setting.</summary>
    public const string Auto = "auto";

    /// <summary>Profile value: write nothing and let Windows Setup show its page.</summary>
    public const string Ask = "ask";

    /// <summary>
    ///     Rules:
    ///     <list type="bullet">
    ///         <item>Region: "auto" is the build PC's regional format, "ask" is null, anything else must be a language tag.</item>
    ///         <item>
    ///             Keyboard: "auto" is the build PC's keyboard, "ask" is null, anything else must be a language tag
    ///             (Windows then uses that language's default keyboard).
    ///         </item>
    ///         <item>
    ///             Time zone: "auto" is the build PC's time zone id, anything else is taken as a Windows time zone id.
    ///             "ask" is accepted as "do not set one", because Setup has no time zone page to fall back to and null
    ///             is the only honest meaning. Windows then picks the time zone from the region.
    ///         </item>
    ///         <item>Display language and system locale are always the image's language.</item>
    ///     </list>
    ///     <para>
    ///         Region and keyboard are independent. When only one of them is "ask", the other one is still
    ///         written, together with the display language. Setup hides its regional pages only when it has
    ///         every value, so the page for the missing one is still shown. See <see cref="UnattendGenerator" />
    ///         for the elements this produces.
    ///     </para>
    ///     <para>
    ///         A build PC value that is not usable ("auto" on a PC with a custom locale, for example) is never
    ///         passed on. It becomes null, Setup asks (for the time zone, Windows picks one from the region), and
    ///         a note says why. A wrong explicit value is the
    ///         profile's mistake and is refused, because guessing would hide it.
    ///     </para>
    /// </summary>
    /// <exception cref="PlainstallException">InvalidInput: a region, keyboard or image language that is not a language tag.</exception>
    public static ResolvedSetup Resolve(SetupOptions options, HostFacts host, string imageDefaultLanguage)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(host);

        if (!IsLanguageTag(imageDefaultLanguage))
            throw new PlainstallException(ErrorKind.InvalidInput,
                $"The language of the Windows image ('{imageDefaultLanguage}') is not a language tag such as en-US.");

        var notes = new List<string>();

        var userLocale = options.Region switch
        {
            Ask => null,
            Auto => HostValue(host.UserLocale, IsLanguageTag, "regional format",
                "Windows Setup will ask for the region", notes),
            var tag when IsLanguageTag(tag) => tag,
            var other => throw new PlainstallException(ErrorKind.InvalidInput,
                $"setup.region: '{other}' is not valid. Use auto, ask or a tag such as de-CH.")
        };

        var inputLocale = options.Keyboard switch
        {
            Ask => null,
            Auto => HostValue(host.InputLocale, IsInputLocale, "keyboard", "Windows Setup will ask for the keyboard",
                notes),
            var tag when IsLanguageTag(tag) => tag,
            var other => throw new PlainstallException(ErrorKind.InvalidInput,
                $"setup.keyboard: '{other}' is not valid. Use auto, ask or a tag such as de-CH.")
        };

        var timeZone = options.Timezone switch
        {
            Ask => null,
            Auto => HostValue(host.TimeZoneId, IsTimeZoneId, "time zone",
                "Windows picks the time zone from the region", notes),
            var id when IsTimeZoneId(id) => id.Trim(),
            var other => throw new PlainstallException(ErrorKind.InvalidInput,
                $"setup.timezone: '{other}' is not valid. Use auto, ask or a Windows time zone id such as 'W. Europe Standard Time'.")
        };

        return new ResolvedSetup
        {
            UiLanguage = imageDefaultLanguage,
            SystemLocale = imageDefaultLanguage,
            UserLocale = userLocale,
            InputLocale = inputLocale,
            TimeZone = timeZone,
            Notes = notes
        };
    }

    /// <summary>
    ///     True for a tag of the shape the profile schema accepts: <c>de</c>, <c>de-CH</c>, <c>sr-Latn-RS</c>, <c>es-419</c>.
    ///     The keyword "ask" has the shape of a three-letter language code and is excluded by name, so it
    ///     can never be written into an answer file as if it were a language.
    /// </summary>
    public static bool IsLanguageTag(string? value)
    {
        return value is not (null or Auto or Ask) && LanguageTag().IsMatch(value);
    }

    /// <summary>
    ///     True for a value Windows accepts in <c>InputLocale</c>: a language tag, or language id and
    ///     keyboard layout id in hexadecimal (<c>0409:00000409</c>).
    /// </summary>
    public static bool IsInputLocale(string? value)
    {
        return value is not null && (IsLanguageTag(value) || HexInputLocale().IsMatch(value));
    }

    /// <summary>
    ///     A shape check only: 1 to 64 printable characters. Whether Windows knows the id is not checked
    ///     here, because the list of time zones belongs to the Windows being installed, not to this PC.
    /// </summary>
    public static bool IsTimeZoneId(string? value)
    {
        return value?.Trim().Length is >= 1 and <= 64 && value.All(c => c is >= ' ' and not (char)0x7F);
    }

    /// <summary>
    ///     The build PC's value when it can be used, otherwise null and a note. <paramref name="fallback" /> says
    ///     what Windows then does, for example "Windows Setup will ask for the region".
    /// </summary>
    private static string? HostValue(string? value, Func<string?, bool> isValid, string what, string fallback,
        List<string> notes)
    {
        if (isValid(value)) return value!.Trim();
        notes.Add(string.IsNullOrWhiteSpace(value)
            ? $"This PC did not report its {what}, so {fallback}."
            : $"This PC's {what} ('{value}') cannot be written into an answer file, so {fallback}.");
        return null;
    }

    // \A and \z, not ^ and $: in .NET "$" also matches before a final line break.
    [GeneratedRegex(@"\A[a-z]{2,3}(-[A-Za-z]{4})?(-[A-Z]{2}|-[0-9]{3})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageTag();

    [GeneratedRegex(@"\A[0-9A-Fa-f]{4}:[0-9A-Fa-f]{8}\z", RegexOptions.CultureInvariant)]
    private static partial Regex HexInputLocale();
}