using System.Text.RegularExpressions;

namespace Plainstall.Core.Download;

/// <summary>One row of Microsoft's table: the label as printed ("German 64-bit") and the SHA-256 in upper-case hex.</summary>
public sealed record PublishedHash(string Label, string Sha256)
{
    /// <summary>The label without its "64-bit" ending, which is the language name ("German").</summary>
    public string LanguageName => PublishedHashTable.StripBitness(Label);
}

/// <summary>
///     The "language, SHA-256" table Microsoft prints on the download page. It is the only published
///     source of the expected hash (the link response has none), so this is what makes a download
///     verifiable: a file whose hash is in this table is an unmodified Microsoft ISO.
/// </summary>
public sealed partial class PublishedHashTable
{
    private const string UnitedStatesKey = "englishunitedstates";
    private const string UnitedStatesLabelKey = "english";
    private readonly Dictionary<string, List<PublishedHash>> _byLanguage = new(StringComparer.Ordinal);

    private PublishedHashTable(IReadOnlyList<PublishedHash> rows, int rejectedRows)
    {
        Rows = rows;
        RejectedRows = rejectedRows;
        foreach (var row in rows)
        {
            var key = HtmlText.NameKey(row.LanguageName);
            if (!_byLanguage.TryGetValue(key, out var list)) _byLanguage[key] = list = [];

            if (!list.Exists(existing => HashHex.Equal(existing.Sha256, row.Sha256))) list.Add(row);
        }
    }

    /// <summary>A table with no rows: nothing can be verified against it.</summary>
    public static PublishedHashTable Empty { get; } = new([], 0);

    /// <summary>The rows in page order.</summary>
    public IReadOnlyList<PublishedHash> Rows { get; }

    /// <summary>True when the page had no usable table, which usually means its layout changed.</summary>
    public bool IsEmpty => Rows.Count == 0;

    /// <summary>
    ///     Rows that looked like part of the table but whose second cell was not exactly 64 hex characters.
    ///     More than zero is a hint that the page changed; such rows are never used.
    /// </summary>
    public int RejectedRows { get; }

    /// <summary>
    ///     Reads every table row of the page that consists of a label and a SHA-256. White space, tag
    ///     attributes and non-breaking spaces are tolerated; a hash that is not exactly 64 hex characters
    ///     is not. Returns an empty table when the page has no such rows.
    /// </summary>
    public static PublishedHashTable Parse(string? html)
    {
        if (string.IsNullOrEmpty(html)) return Empty;

        var rows = new List<PublishedHash>();
        var rejected = 0;
        try
        {
            var text = HtmlText.StripComments(html);
            foreach (var rowHtml in HtmlText.Rows(text))
            {
                var cells = HtmlText.Cells(rowHtml);
                if (cells.Count != 2) continue;

                var label = cells[0];
                var hash = cells[1];
                if (HashHex.IsSha256(hash) && label.Length is > 0 and <= 120)
                    rows.Add(new PublishedHash(label, hash.ToUpperInvariant()));
                else if (LooksLikeHashAttempt(hash)) rejected++;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // A page that takes seconds to scan is not the page we know. Treat it as "no table".
            return Empty;
        }

        return rows.Count == 0 && rejected == 0 ? Empty : new PublishedHashTable(rows, rejected);
    }

    /// <summary>
    ///     The rows published for a language. Normally exactly one. More than one (with different hashes)
    ///     means the page lists the same label twice, and a download may match any of them.
    /// </summary>
    public IReadOnlyList<PublishedHash> FindRows(WindowsLanguage language)
    {
        ArgumentNullException.ThrowIfNull(language);
        foreach (var key in LabelKeys(language))
            if (_byLanguage.TryGetValue(key, out var rows))
                return rows;

        return [];
    }

    /// <summary>
    ///     The one hash a download in this language must have, or null when the table has no row for the
    ///     language or more than one.
    /// </summary>
    public string? FindExpectedHash(WindowsLanguage language)
    {
        var rows = FindRows(language);
        return rows.Count == 1 ? rows[0].Sha256 : null;
    }

    /// <summary>
    ///     True when the hash is listed in a row for this language. This is the full check: genuine file, and the
    ///     language that was asked for.
    /// </summary>
    public bool IsPublishedFor(WindowsLanguage language, string? sha256)
    {
        return FindRows(language).Any(row => HashHex.Equal(row.Sha256, sha256));
    }

    /// <summary>
    ///     The label Microsoft prints for a language: its localized name plus " 64-bit", with US English shortened to
    ///     "English".
    /// </summary>
    public static string LabelFor(WindowsLanguage language)
    {
        ArgumentNullException.ThrowIfNull(language);
        var name = HtmlText.NameKey(language.LocalizedLanguage) == UnitedStatesKey ? "English" : language.DisplayName;
        return name + " 64-bit";
    }

    internal static string StripBitness(string label)
    {
        return BitnessRegex().Replace(label, string.Empty).Trim();
    }

    private static IEnumerable<string> LabelKeys(WindowsLanguage language)
    {
        var key = HtmlText.NameKey(language.LocalizedLanguage);
        if (key.Length == 0) yield break;

        yield return key;
        if (key == UnitedStatesKey) yield return UnitedStatesLabelKey;
    }

    private static bool LooksLikeHashAttempt(string cell)
    {
        return cell.Length is >= 32 and <= 160 && HexishRegex().IsMatch(cell);
    }

    [GeneratedRegex(@"[\s,;:(\-]*\b(?:64|32)[\s\-]?bit\b\)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex BitnessRegex();

    [GeneratedRegex(@"^[0-9A-Fa-f\s:\-]+$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex HexishRegex();
}