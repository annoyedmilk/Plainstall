using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Plainstall.Core.Unattend;

/// <summary>
///     What "auto" means for the Setup options: the regional settings of the PC that builds the stick.
///     It is a plain record so that <see cref="SetupResolver" /> stays a pure function and tests can
///     describe any PC they like. <see cref="Current" /> is the only member that reads the environment.
/// </summary>
/// <param name="UiLanguage">Windows display language of the build PC, for example <c>de-DE</c>.</param>
/// <param name="UserLocale">Regional format of the build PC, for example <c>de-CH</c>.</param>
/// <param name="InputLocale">
///     Keyboard of the build PC in one of the two forms an answer file accepts: a language tag
///     (<c>de-CH</c>, meaning that language's default keyboard) or language id and keyboard layout id
///     in hexadecimal (<c>0409:00000807</c>, here: English input language with the Swiss German layout).
/// </param>
/// <param name="TimeZoneId">Windows time zone id of the build PC, for example <c>W. Europe Standard Time</c>.</param>
public sealed partial record HostFacts(string UiLanguage, string UserLocale, string InputLocale, string TimeZoneId)
{
    private const int LocaleNameMaxLength = 85;
    private const int KeyboardLayoutNameLength = 9;

    /// <summary>
    ///     Reads the four values from the running Windows session.
    ///     <para>
    ///         The values come from Windows, not from <see cref="CultureInfo.CurrentCulture" />, because a host
    ///         application may set the thread cultures to its own UI language, and "auto" must mean the PC's
    ///         settings, not the app's. The .NET cultures are only the fallback when a call fails.
    ///     </para>
    ///     <para>
    ///         The keyboard is reported as language id plus layout id rather than as a tag. A tag can only say
    ///         "the default keyboard of that language", which is wrong for the common case of an English
    ///         display and input language combined with a national keyboard layout.
    ///     </para>
    ///     <para>
    ///         Limit: when Plainstall runs elevated with another account's credentials, these are that
    ///         account's settings, not the settings of the person at the keyboard.
    ///     </para>
    /// </summary>
    public static HostFacts Current()
    {
        var userLocale = ReadUserLocale() ?? CultureInfo.CurrentCulture.Name;
        var uiLanguage = ReadUiLanguage() ?? CultureInfo.CurrentUICulture.Name;
        var inputLocale = ReadInputLocale() ?? userLocale;
        return new HostFacts(uiLanguage, userLocale, inputLocale, TimeZoneInfo.Local.Id);
    }

    private static string? ReadUserLocale()
    {
        try
        {
            var buffer = new char[LocaleNameMaxLength];
            var length = GetUserDefaultLocaleName(buffer, buffer.Length);
            return length > 1 ? new string(buffer, 0, length - 1) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static string? ReadUiLanguage()
    {
        try
        {
            var buffer = new char[LocaleNameMaxLength];
            var length = LcidToLocaleName(GetUserDefaultUILanguage(), buffer, buffer.Length, 0);
            return length > 1 ? new string(buffer, 0, length - 1) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static string? ReadInputLocale()
    {
        try
        {
            // Low word of the handle: the input language. The layout name is the 8-digit layout id.
            var languageId = (ushort)(GetKeyboardLayout(0).ToInt64() & 0xFFFF);
            var layout = new StringBuilder(KeyboardLayoutNameLength);
            if (languageId == 0 || !GetKeyboardLayoutNameW(layout) || layout.Length != 8) return null;
            var value = languageId.ToString("x4", CultureInfo.InvariantCulture) + ":" +
                        layout.ToString().ToLowerInvariant();
            return SetupResolver.IsInputLocale(value) ? value : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetUserDefaultLocaleName([Out] char[] localeName, int localeNameLength);

    [LibraryImport("kernel32.dll")]
    private static partial ushort GetUserDefaultUILanguage();

    [LibraryImport("kernel32.dll", EntryPoint = "LCIDToLocaleName", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int LcidToLocaleName(uint locale, [Out] char[] name, int nameLength, uint flags);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetKeyboardLayout(uint threadId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardLayoutNameW([Out] StringBuilder name);
}