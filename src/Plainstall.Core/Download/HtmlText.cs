using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Plainstall.Core.Download;

/// <summary>
///     The little HTML reading the download page needs: rows, cells, one select box. Not a general
///     parser. It accepts what browsers accept in these places (attributes on tags, omitted closing
///     tags, entities) and gives up quietly on anything else, so a redesigned page shows up as
///     "nothing found" and not as wrong data.
/// </summary>
internal static partial class HtmlText
{
    private const RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant;

    private const int TimeoutMs = 5000;

    [GeneratedRegex(@"<!--.*?(?:-->|\z)", Options, TimeoutMs)]
    private static partial Regex CommentRegex();

    [GeneratedRegex(@"<tr\b[^>]*>(?<inner>.*?)(?=</tr\s*>|<tr\b|</table\b|</tbody\b|</thead\b|</tfoot\b|\z)", Options,
        TimeoutMs)]
    private static partial Regex RowRegex();

    [GeneratedRegex(@"<t[dh]\b[^>]*>(?<inner>.*?)(?=</t[dh]\s*>|<t[dh]\b|\z)", Options, TimeoutMs)]
    private static partial Regex CellRegex();

    [GeneratedRegex(@"<select\b(?<attrs>[^>]*)>(?<inner>.*?)(?=</select\s*>|<select\b|\z)", Options, TimeoutMs)]
    private static partial Regex SelectRegex();

    [GeneratedRegex(@"<option\b(?<attrs>[^>]*)>(?<inner>.*?)(?=</option\s*>|<option\b|</?optgroup\b|\z)", Options,
        TimeoutMs)]
    private static partial Regex OptionRegex();

    [GeneratedRegex("<[^>]*>", Options, TimeoutMs)]
    private static partial Regex TagRegex();

    /// <summary>Removes HTML comments, so that markup someone commented out is not read as live.</summary>
    public static string StripComments(string html)
    {
        return html.Contains("<!--", StringComparison.Ordinal) ? CommentRegex().Replace(html, " ") : html;
    }

    /// <summary>The inner HTML of every table row, in document order.</summary>
    public static IEnumerable<string> Rows(string html)
    {
        foreach (Match match in RowRegex().Matches(html)) yield return match.Groups["inner"].Value;
    }

    /// <summary>The plain text of every cell of one row.</summary>
    public static List<string> Cells(string rowHtml)
    {
        var cells = new List<string>();
        foreach (Match match in CellRegex().Matches(rowHtml)) cells.Add(ToPlainText(match.Groups["inner"].Value));

        return cells;
    }

    /// <summary>The options of the select box with the given id, as (value, text) pairs. Empty when there is no such box.</summary>
    public static List<(string Value, string Text)> SelectOptions(string html, string selectId)
    {
        var options = new List<(string, string)>();
        foreach (Match select in SelectRegex().Matches(html))
        {
            var id = Attribute(select.Groups["attrs"].Value, "id");
            if (!string.Equals(id, selectId, StringComparison.Ordinal)) continue;

            foreach (Match option in OptionRegex().Matches(select.Groups["inner"].Value))
            {
                var value = Attribute(option.Groups["attrs"].Value, "value") ?? string.Empty;
                options.Add((value.Trim(), ToPlainText(option.Groups["inner"].Value)));
            }
        }

        return options;
    }

    /// <summary>Reads one attribute from the inside of a tag. Handles double quotes, single quotes and no quotes.</summary>
    public static string? Attribute(string tagAttributes, string name)
    {
        var i = 0;
        ReadOnlySpan<char> s = tagAttributes;
        while (i < s.Length)
        {
            while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == '/')) i++;

            var nameStart = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '=' && s[i] != '/') i++;

            var attributeName = s[nameStart..i];
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;

            string? value = null;
            if (i < s.Length && s[i] == '=')
            {
                i++;
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;

                if (i < s.Length && (s[i] == '"' || s[i] == '\''))
                {
                    var quote = s[i++];
                    var valueStart = i;
                    while (i < s.Length && s[i] != quote) i++;

                    value = s[valueStart..i].ToString();
                    i++;
                }
                else
                {
                    var valueStart = i;
                    while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;

                    value = s[valueStart..i].ToString();
                }
            }

            if (attributeName.Length == 0)
            {
                if (value is null) i++;

                continue;
            }

            if (attributeName.Equals(name, StringComparison.OrdinalIgnoreCase))
                return WebUtility.HtmlDecode(value ?? string.Empty);
        }

        return null;
    }

    /// <summary>
    ///     Text of an HTML fragment as a person would read it: tags removed, entities decoded, and every
    ///     run of white space (including non-breaking spaces) turned into one space.
    /// </summary>
    public static string ToPlainText(string html)
    {
        var text = html.Contains('<', StringComparison.Ordinal) ? TagRegex().Replace(html, " ") : html;
        text = WebUtility.HtmlDecode(text);

        var result = new StringBuilder(text.Length);
        var pendingSpace = false;
        // U+200B to U+200D and U+FEFF are invisible and not white space to .NET; pages use them as
        // soft line-break hints, so they count as nothing at all.
        foreach (var c in text.Where(c => c is not ('\u200B' or '\u200C' or '\u200D' or '\uFEFF')))
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                pendingSpace = result.Length != 0;
                continue;
            }

            if (pendingSpace)
            {
                result.Append(' ');
                pendingSpace = false;
            }

            result.Append(c);
        }

        return result.ToString();
    }

    /// <summary>
    ///     Letters and digits only, lower case. Used to compare names that Microsoft writes in slightly
    ///     different ways in different places ("Chinese (Simplified)" and "Chinese Simplified").
    /// </summary>
    public static string NameKey(string? name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;

        var decomposed = name.Normalize(NormalizationForm.FormKD);
        var key = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed.Where(char.IsLetterOrDigit)) key.Append(char.ToLowerInvariant(c));

        return key.ToString();
    }
}