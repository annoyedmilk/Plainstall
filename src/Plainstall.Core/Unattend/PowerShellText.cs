using System.Globalization;
using System.Text;

namespace Plainstall.Core.Unattend;

/// <summary>
///     Turns data into Windows PowerShell 5.1 source text in which the data can never become code.
///     This is the one place where catalog and profile strings cross into a generated script, so it is
///     deliberately small and has only two output shapes.
/// </summary>
internal static class PowerShellText
{
    /// <summary>
    ///     Returns a PowerShell expression whose value is exactly <paramref name="value" />.
    ///     <para>
    ///         Printable ASCII is written as a single-quoted literal with <c>'</c> doubled. Inside single
    ///         quotes PowerShell expands nothing: no <c>$variable</c>, no <c>$( )</c>, no backtick escapes.
    ///     </para>
    ///     <para>
    ///         Every other character (line breaks, tabs, control characters and everything outside ASCII)
    ///         is written as its code point, <c>[char]0x000A</c>, joined to the quoted runs with <c>+</c>.
    ///         Two reasons. First, PowerShell also accepts the typographic quotes U+2018 to U+201B as
    ///         single-quote characters, so a literal that only doubles the ASCII quote can be closed by
    ///         data. Keeping those characters out of the quoted text removes that whole class of mistake.
    ///         Second, the script stays 7-bit ASCII and one statement stays on one line, so the result does
    ///         not depend on how the file's encoding or line endings are read.
    ///     </para>
    ///     <para>
    ///         A joined literal is wrapped in parentheses so that it is one argument when used in a command.
    ///         It always starts with a string, never with a <c>[char]</c>, because <c>+</c> takes its meaning
    ///         from the left operand.
    ///     </para>
    /// </summary>
    public static string Literal(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (IsPlain(value)) return Quote(value);

        var result = new StringBuilder("(");
        var run = new StringBuilder();
        var first = true;

        foreach (var c in value)
        {
            if (IsPlain(c))
            {
                run.Append(c);
                continue;
            }

            FlushRun();
            if (first) Append("''");
            Append("[char]0x" + ((int)c).ToString("X4", CultureInfo.InvariantCulture));
        }

        FlushRun();
        return result.Append(')').ToString();

        void Append(string part)
        {
            if (!first) result.Append(" + ");
            result.Append(part);
            first = false;
        }

        void FlushRun()
        {
            if (run.Length == 0) return;
            Append(Quote(run.ToString()));
            run.Clear();
        }
    }

    /// <summary>An array expression, <c>@('a', 'b')</c>, of string literals. Empty input gives <c>@()</c>.</summary>
    public static string ArrayLiteral(IEnumerable<string> values)
    {
        return "@(" + string.Join(", ", values.Select(Literal)) + ")";
    }

    /// <summary>True when every character is printable ASCII (space to tilde).</summary>
    public static bool IsPlain(string value)
    {
        return value.All(IsPlain);
    }

    private static bool IsPlain(char c)
    {
        return c is >= ' ' and <= '~';
    }

    private static string Quote(string plain)
    {
        return "'" + plain.Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}