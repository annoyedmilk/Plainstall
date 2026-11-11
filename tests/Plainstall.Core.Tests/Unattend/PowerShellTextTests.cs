using System.Text;
using Plainstall.Core.Unattend;

namespace Plainstall.Core.Tests.Unattend;

public class PowerShellTextTests
{
    [Theory]
    [InlineData("", "''")]
    [InlineData("plain text", "'plain text'")]
    [InlineData("it's", "'it''s'")]
    [InlineData("$(x) `n \"q\" ; | & { }", "'$(x) `n \"q\" ; | & { }'")]
    [InlineData(@"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}",
        @"'Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}'")]
    [InlineData("a\nb", "('a' + [char]0x000A + 'b')")]
    [InlineData("\nb", "('' + [char]0x000A + 'b')")]
    [InlineData("a\r\n", "('a' + [char]0x000D + [char]0x000A)")]
    [InlineData("caf\u00E9", "('caf' + [char]0x00E9)")]
    [InlineData("\u2019", "('' + [char]0x2019)")]
    [InlineData("x\u2019; y", "('x' + [char]0x2019 + '; y')")]
    [InlineData("\U0001F600", "('' + [char]0xD83D + [char]0xDE00)")]
    public void Literal_has_one_of_two_shapes(string value, string expected)
    {
        Assert.Equal(expected, PowerShellText.Literal(value));
    }

    [Fact]
    public void Literal_is_always_one_line_of_printable_ascii()
    {
        foreach (var value in Hostile.Strings)
        {
            var literal = PowerShellText.Literal(value);
            Assert.All(literal, c => Assert.InRange(c, ' ', '~'));
        }
    }

    [Fact]
    public void Literal_never_leaves_a_quote_character_unpaired()
    {
        // Structural check without PowerShell: walk the literal as its own tiny grammar.
        foreach (var value in Hostile.Strings) Assert.Equal(value, Decode(PowerShellText.Literal(value)));
    }

    [Fact]
    public void Array_literal_joins_literals()
    {
        Assert.Equal("@()", PowerShellText.ArrayLiteral([]));
        Assert.Equal("@('a', 'b''c', ('' + [char]0x000A))", PowerShellText.ArrayLiteral(["a", "b'c", "\n"]));
    }

    /// <summary>
    ///     The proof that matters: Windows PowerShell itself evaluates every literal, once as an expression
    ///     and once as a command argument, and must give back exactly the characters that went in.
    ///     Only the literals are evaluated, never a generated script, and every payload is harmless.
    /// </summary>
    [Fact]
    public void Windows_PowerShell_reads_every_literal_back_unchanged()
    {
        var harness = new StringBuilder();
        harness.AppendLine("function Get-Codes { param($Value) '@@' + (([int[]][char[]]([string]$Value)) -join ',') }");
        harness.AppendLine(
            "function Get-ArgumentCodes { param($Value) if ($args.Count -ne 0) { throw 'more than one argument' }; Get-Codes $Value }");
        foreach (var value in Hostile.Strings)
        {
            var literal = PowerShellText.Literal(value);
            harness.AppendLine($"Get-Codes ({literal})");
            harness.AppendLine($"Get-ArgumentCodes {literal}");
        }

        var lines = WindowsPowerShell.Run(harness.ToString());

        Assert.DoesNotContain(lines, l => !l.StartsWith("@@", StringComparison.Ordinal));
        Assert.Equal(Hostile.Strings.Length * 2, lines.Count);
        for (var i = 0; i < Hostile.Strings.Length; i++)
        {
            var expected = "@@" + string.Join(",", Hostile.Strings[i].Select(c => (int)c));
            Assert.Equal(expected, lines[2 * i]);
            Assert.Equal(expected, lines[2 * i + 1]);
        }
    }

    /// <summary>Reads a literal back by the two shapes it may have. Fails on anything else.</summary>
    private static string Decode(string literal)
    {
        var text = literal;
        if (text.StartsWith('('))
        {
            Assert.EndsWith(")", text);
            text = text[1..^1];
        }

        var result = new StringBuilder();
        var position = 0;
        while (true)
        {
            if (text[position] == '\'')
            {
                position++;
                while (true)
                {
                    Assert.True(position < text.Length, "unterminated quoted run");
                    if (text[position] != '\'')
                    {
                        result.Append(text[position++]);
                    }
                    else if (position + 1 < text.Length && text[position + 1] == '\'')
                    {
                        result.Append('\'');
                        position += 2;
                    }
                    else
                    {
                        position++;
                        break;
                    }
                }
            }
            else
            {
                const string prefix = "[char]0x";
                Assert.Equal(prefix, text.Substring(position, prefix.Length));
                result.Append((char)Convert.ToInt32(text.Substring(position + prefix.Length, 4), 16));
                position += prefix.Length + 4;
            }

            if (position == text.Length) return result.ToString();
            Assert.Equal(" + ", text.Substring(position, 3));
            position += 3;
        }
    }
}