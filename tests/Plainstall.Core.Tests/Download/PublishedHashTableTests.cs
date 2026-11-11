using System.Diagnostics;
using System.Text;
using Plainstall.Core.Download;

namespace Plainstall.Core.Tests.Download;

public sealed class PublishedHashTableTests
{
    private static readonly string A = new('A', 64);
    private static readonly string B = new('B', 64);
    private static readonly string C = new('C', 64);
    private static readonly PublishedHashTable Page = PublishedHashTable.Parse(Fixture.Read("product-page-x64.html"));

    private static string Repeat(string pair)
    {
        return string.Concat(Enumerable.Repeat(pair, 32));
    }

    [Fact]
    public void Parses_the_rows_of_the_fixture_and_nothing_else()
    {
        Assert.Equal(
            [
                "Arabic 64-bit",
                "English 64-bit",
                "English International 64-bit",
                "German 64-bit",
                "Chinese (Simplified) 64-bit",
                "Brazilian Portuguese 64-bit"
            ],
            Page.Rows.Select(r => r.Label));
        Assert.False(Page.IsEmpty);
        Assert.Equal(1, Page.RejectedRows); // the 63-character row; the row with a "G" is simply not a hash row
        Assert.All(Page.Rows, row => Assert.True(HashHex.IsSha256(row.Sha256)));
        Assert.Equal("German", Page.Rows[3].LanguageName);
    }

    [Fact]
    public void Hashes_are_normalized_to_upper_case()
    {
        Assert.Equal(string.Concat(Enumerable.Repeat("0123456789ABCDEF", 4)),
            Page.FindExpectedHash(Languages.EnglishUs));
    }

    [Fact]
    public void Finds_the_expected_hash_by_localized_language()
    {
        Assert.Equal(Repeat("AA"), Page.FindExpectedHash(Languages.Arabic));
        Assert.Equal(Repeat("CC"), Page.FindExpectedHash(Languages.German)); // label had a <span> inside
        Assert.Equal(Repeat("DD"), Page.FindExpectedHash(Languages.ChineseSimplified)); // hash was wrapped in &nbsp;
        Assert.Equal(Repeat("EE"), Page.FindExpectedHash(Languages.BrazilianPortuguese)); // row without closing tags
    }

    [Fact]
    public void British_english_is_found_through_its_localized_name_not_the_other_one()
    {
        // Language is "English (United Kingdom)", LocalizedLanguage is "English International".
        Assert.Equal(Repeat("BB"), Page.FindExpectedHash(Languages.EnglishInternational));
    }

    [Fact]
    public void Us_english_is_labelled_plain_english()
    {
        Assert.Equal("English 64-bit", PublishedHashTable.LabelFor(Languages.EnglishUs));
        Assert.Equal("English International 64-bit", PublishedHashTable.LabelFor(Languages.EnglishInternational));
        Assert.Equal("German 64-bit", PublishedHashTable.LabelFor(Languages.German));
        Assert.NotNull(Page.FindExpectedHash(Languages.EnglishUs));
        Assert.NotEqual(Page.FindExpectedHash(Languages.EnglishUs),
            Page.FindExpectedHash(Languages.EnglishInternational));
    }

    [Fact]
    public void A_language_without_a_row_has_no_expected_hash()
    {
        Assert.Null(Page.FindExpectedHash(Languages.Japanese));
        Assert.Empty(Page.FindRows(Languages.Japanese));
        Assert.False(Page.IsPublishedFor(Languages.Japanese, Repeat("AA")));
    }

    [Fact]
    public void A_genuine_hash_under_another_language_is_not_accepted_for_this_one()
    {
        Assert.True(Page.IsPublishedFor(Languages.German, Repeat("CC")));
        Assert.True(Page.IsPublishedFor(Languages.German, Repeat("cc"))); // lower case on purpose
        Assert.False(Page.IsPublishedFor(Languages.German, Repeat("AA"))); // that is the Arabic ISO
        Assert.False(Page.IsPublishedFor(Languages.German, null));
    }

    [Theory]
    [InlineData("<tr><td>German 64-bit</td><td>{0}</td></tr>")]
    [InlineData("<TR><TD>German 64-bit</TD><TD>{0}</TD></TR>")]
    [InlineData(
        "<tr class=\"a\" data-x='1'>\r\n\t<td class=\"lang\">\r\n German   64-bit \r\n</td>\r\n<td id=h>\r\n{0}\r\n</td>\r\n</tr>")]
    [InlineData("<tr><td>German&nbsp;64-bit</td><td>&nbsp;{0}&nbsp;</td></tr>")]
    [InlineData("<tr><td>German\u00A064-bit</td><td>{0}\u00A0</td></tr>")]
    [InlineData("<tr><td><strong>German</strong> 64-bit</td><td><span class=\"hash\">{0}</span></td></tr>")]
    [InlineData("<tr><th scope=\"row\">German 64-bit</th><td>{0}</td></tr>")]
    [InlineData("<table><tr><td>German 64-bit<td>{0}</table>")]
    [InlineData("<tr><td>GERMAN 64-BIT</td><td>{0}</td></tr>")]
    [InlineData("<tr><td>German 64 bit</td><td>{0}</td></tr>")]
    [InlineData("<tr><td>German (64-bit)</td><td>{0}</td></tr>")]
    public void Tolerates_markup_variations(string template)
    {
        var table = PublishedHashTable.Parse(string.Format(template, C));

        Assert.Single(table.Rows);
        Assert.Equal(C, table.FindExpectedHash(Languages.German));
        Assert.Equal(0, table.RejectedRows);
    }

    [Theory]
    [InlineData("CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC")] // 63
    [InlineData("CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC")] // 65
    [InlineData("CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC")] // 64 with a space inside
    [InlineData("CCCCCCCC-CCCCCCCC-CCCCCCCC-CCCCCCCC-CCCCCCCC-CCCCCCCC-CCCCCCCC-CCCCCCCC")]
    [InlineData("CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC<br>CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC")] // a line break inside
    public void A_hash_that_is_not_exactly_64_hex_characters_is_rejected_and_counted(string cell)
    {
        var table = PublishedHashTable.Parse($"<table><tr><td>German 64-bit</td><td>{cell}</td></tr></table>");

        Assert.Empty(table.Rows);
        Assert.True(table.IsEmpty);
        Assert.Equal(1, table.RejectedRows);
        Assert.Null(table.FindExpectedHash(Languages.German));
    }

    [Theory]
    [InlineData("GCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC")] // not hex
    [InlineData("\uFF21CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC")] // full-width A
    [InlineData("see the release notes")]
    [InlineData("")]
    public void Cells_that_are_not_hashes_are_just_not_rows(string cell)
    {
        var table = PublishedHashTable.Parse($"<table><tr><td>German 64-bit</td><td>{cell}</td></tr></table>");

        Assert.Empty(table.Rows);
        Assert.Equal(0, table.RejectedRows);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<html><body><p>No table here.</p></body></html>")]
    [InlineData("<table><tr><td>one cell</td></tr></table>")]
    public void A_page_without_a_table_gives_an_empty_table(string? html)
    {
        var table = PublishedHashTable.Parse(html);

        Assert.True(table.IsEmpty);
        Assert.Empty(table.Rows);
        Assert.Null(table.FindExpectedHash(Languages.German));
    }

    [Fact]
    public void Rows_inside_html_comments_are_ignored()
    {
        var table = PublishedHashTable.Parse(
            $"<table><!-- <tr><td>German 64-bit</td><td>{A}</td></tr> --><tr><td>German 64-bit</td><td>{B}</td></tr></table>");

        Assert.Equal(B, table.FindExpectedHash(Languages.German));
    }

    [Fact]
    public void The_same_label_with_two_different_hashes_has_no_single_expected_hash()
    {
        var table = PublishedHashTable.Parse(
            $"<table><tr><td>German 64-bit</td><td>{A}</td></tr><tr><td>German 64-bit</td><td>{B}</td></tr></table>");

        Assert.Null(table.FindExpectedHash(Languages.German));
        Assert.Equal(2, table.FindRows(Languages.German).Count);
        Assert.True(table.IsPublishedFor(Languages.German, A));
        Assert.True(table.IsPublishedFor(Languages.German, B));
        Assert.False(table.IsPublishedFor(Languages.German, C));
    }

    [Fact]
    public void The_same_row_twice_is_still_one_expected_hash()
    {
        var table = PublishedHashTable.Parse(
            $"<table><tr><td>German 64-bit</td><td>{A}</td></tr><tr><td>German 64-bit</td><td>{A.ToLowerInvariant()}</td></tr></table>");

        Assert.Equal(A, table.FindExpectedHash(Languages.German));
    }

    [Fact]
    public void A_large_page_is_parsed_quickly()
    {
        // 4 MiB of markup with many rows and many unclosed tags: must stay linear.
        var html = new StringBuilder();
        for (var i = 0; html.Length < 4 * 1024 * 1024; i++)
            html.Append("<div><tr><td>Language ").Append(i).Append(" 64-bit<td>").Append(A)
                .Append("</tr><p>filler filler filler filler\n");

        var watch = Stopwatch.StartNew();
        var table = PublishedHashTable.Parse(html.ToString());
        watch.Stop();

        Assert.True(table.Rows.Count > 10_000, $"only {table.Rows.Count} rows");
        // A generous limit: the tests run in parallel on a busy PC. Quadratic behaviour would take minutes.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"took {watch.Elapsed}");
    }

    [Fact]
    public void The_empty_table_answers_every_question_with_no()
    {
        Assert.True(PublishedHashTable.Empty.IsEmpty);
        Assert.Null(PublishedHashTable.Empty.FindExpectedHash(Languages.German));
        Assert.False(PublishedHashTable.Empty.IsPublishedFor(Languages.German, A));
    }
}