using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MimeKit.Text;

namespace Mailkeep.Infrastructure;

/// <summary>A letter written as HTML, read as text. Replacing every tag with a space — which is what this
/// used to be — leaves behind the contents of the parts a reader never sees, so mail laid out by a designer
/// arrived as pages of stylesheet with the letter somewhere inside it, every &amp;nbsp; still spelled out and
/// every paragraph run into the next. The page is read with a real tokenizer rather than a pattern, because a
/// tag holding a quoted "&gt;" is ordinary in mail and fatal to the pattern.</summary>
public static class MailText
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Runs of blank lines: a table-based layout gives an empty line per empty cell, and forty of
    /// them push the letter off the bottom of the reading pane.</summary>
    private static readonly Regex BlankRun = new(@"(\r?\n[ \t]*){3,}", RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex TrailingSpace = new(@"[ \t]+(\r?\n)", RegexOptions.Compiled, RegexTimeout);

    public static string FromHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var text = new StringBuilder(html.Length / 2);
        var tokenizer = new HtmlTokenizer(new StringReader(html));
        // Depth rather than a flag: these elements nest, and one stray closing tag should not turn a
        // stylesheet back into readable text half way down the message.
        var hidden = 0;
        while (tokenizer.ReadNextToken(out var token))
        {
            switch (token.Kind)
            {
                case HtmlTokenKind.Tag:
                    var tag = (HtmlTagToken)token;
                    if (IsHidden(tag.Id))
                    {
                        if (tag.IsEndTag) hidden = Math.Max(0, hidden - 1);
                        else if (!tag.IsEmptyElement) hidden++;
                    }
                    else if (hidden == 0)
                    {
                        if (EndsTheLine(tag.Id)) NewLine(text);
                        else if (SeparatesCells(tag.Id)) Space(text);
                    }
                    break;
                case HtmlTokenKind.Data:
                    if (hidden > 0) break;
                    Append(text, ((HtmlDataToken)token).Data);
                    break;
                // Comments, doctype, CDATA and the body of a script are all things the reader never sees.
            }
        }
        return Tidy(text.ToString());
    }

    /// <summary>What a browser does with the source's own indentation: a run of any whitespace is one space,
    /// and where the line breaks is decided by the tags, not by how the page was typed.</summary>
    private static void Append(StringBuilder text, string data)
    {
        var decoded = data.Contains('&') ? WebUtility.HtmlDecode(data) : data;
        var space = false;
        foreach (var character in decoded)
        {
            if (IsInvisible(character)) continue;
            if (char.IsWhiteSpace(character)) { space = true; continue; }
            if (space) { Space(text); space = false; }
            text.Append(character);
        }
        if (space) Space(text);
    }

    /// <summary>Characters a page carries that a reader is never meant to see: the joiners and
    /// zero-width spaces a mail house pads its preview line with, and soft hyphens. A browser shows
    /// nothing for them; a text pane shows a row of marks where the letter should start.</summary>
    private static bool IsInvisible(char character) => character == '\u034F'
        || char.GetUnicodeCategory(character) == UnicodeCategory.Format;

    private static void Space(StringBuilder text)
    {
        if (text.Length > 0 && text[^1] is not ' ' and not '\n') text.Append(' ');
    }

    private static void NewLine(StringBuilder text)
    {
        while (text.Length > 0 && text[^1] == ' ') text.Length--;
        if (text.Length > 0) text.Append('\n');
    }

    private static string Tidy(string text)
    {
        try
        {
            text = TrailingSpace.Replace(text, "$1");
            text = BlankRun.Replace(text, "\n\n");
        }
        catch (RegexMatchTimeoutException)
        {
            // A page pathological enough to outrun the tidying is still a letter worth showing.
        }
        return text.Replace("\n", Environment.NewLine).Trim();
    }

    /// <summary>Elements whose contents are instructions for drawing the page rather than anything to read.
    /// Only elements that are always closed belong here. Meta, link and base are void — they never have a
    /// closing tag, so counting one as opened would leave the letter hidden for the rest of the message, and
    /// they need no counting anyway: what they carry is in their attributes, which are never read out. Head
    /// is left out for the same reason, its closing tag being optional in a page nobody hand-checks.</summary>
    private static bool IsHidden(HtmlTagId id) => id
        is HtmlTagId.Style or HtmlTagId.Script or HtmlTagId.Title;

    private static bool EndsTheLine(HtmlTagId id) => id
        is HtmlTagId.Br or HtmlTagId.P or HtmlTagId.Div or HtmlTagId.TR or HtmlTagId.LI or HtmlTagId.Table
        or HtmlTagId.UL or HtmlTagId.OL or HtmlTagId.BlockQuote or HtmlTagId.HR or HtmlTagId.Pre
        or HtmlTagId.H1 or HtmlTagId.H2 or HtmlTagId.H3 or HtmlTagId.H4 or HtmlTagId.H5 or HtmlTagId.H6;

    private static bool SeparatesCells(HtmlTagId id) => id is HtmlTagId.TD or HtmlTagId.TH;
}
