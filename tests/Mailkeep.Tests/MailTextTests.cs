using Mailkeep.Infrastructure;

namespace Mailkeep.Tests;

public sealed class MailTextTests
{
    private const string Marketing = """
        <html><head><style type="text/css">
        .btn { background-color: #0a66c2; padding: 12px 24px; border-radius: 4px; }
        @media only screen and (max-width:480px) { .col { width: 100% !important; } }
        </style><script>var track = 1;</script></head>
        <body><table><tr><td>
        <p>Hi Rad,</p>
        <p>There are <b>3&nbsp;new jobs</b> for &quot;Enterprise Architect&quot;.</p>
        <a class="btn" href="https://example.invalid/jobs">See&nbsp;jobs</a>
        </td></tr></table></body></html>
        """;

    /// <summary>What a letter written in HTML has to look like when it is read as text: the words, and not
    /// the page's instructions for drawing them.</summary>
    [Fact]
    public void ReadingAnHtmlLetterKeepsTheWordsAndDropsTheStylesheet()
    {
        var text = MailText.FromHtml(Marketing);

        Assert.Contains("Hi Rad,", text);
        Assert.Contains("3 new jobs", text);          // the entity is decoded, not left as &nbsp;
        Assert.Contains("\"Enterprise Architect\"", text);
        Assert.DoesNotContain("background-color", text);
        Assert.DoesNotContain("@media", text);
        Assert.DoesNotContain("var track", text);
        Assert.DoesNotContain("&nbsp;", text);
        Assert.DoesNotContain("<p>", text);
    }

    /// <summary>Mail laid out in tables converts to line after line of nothing, which pushes the letter off
    /// the bottom of the reading pane.</summary>
    [Fact]
    public void RunsOfEmptyLinesDoNotSurviveTheConversion()
    {
        var text = MailText.FromHtml("<div>One</div>" + string.Concat(Enumerable.Repeat("<div></div>", 40)) + "<div>Two</div>");

        Assert.Contains("One", text);
        Assert.Contains("Two", text);
        Assert.DoesNotContain("\n\n\n", text.Replace("\r\n", "\n"));
    }

    /// <summary>The shape every marketing mail actually arrives in. A void element has no closing tag, so
    /// treating one as an opened element left the rest of the message hidden and the reading pane empty —
    /// which is what a 109KB LinkedIn mail showed: nothing at all.</summary>
    [Fact]
    public void AVoidElementInTheHeadDoesNotSwallowTheMessage()
    {
        const string html = """
            <!DOCTYPE html><html><head>
            <meta http-equiv="Content-Type" content="text/html; charset=utf-8">
            <meta name="viewport" content="width=device-width">
            <link rel="stylesheet" href="https://example.invalid/mail.css">
            <title>You are now open to work</title>
            <style>.a { color: #fff; }</style>
            </head><body><p>You are now open to work.</p><img src="https://example.invalid/pixel.gif"><br>
            <p>We can help you get noticed.</p></body></html>
            """;

        var text = MailText.FromHtml(html);

        Assert.Contains("You are now open to work.", text);
        Assert.Contains("We can help you get noticed.", text);
        Assert.DoesNotContain("color: #fff", text);
        Assert.DoesNotContain("width=device-width", text);
    }

    /// <summary>The invisible padding a mail house puts in front of the letter to fill the preview line in
    /// an inbox list. A browser draws nothing for it; a reading pane drew a row of marks.</summary>
    [Fact]
    public void ThePreviewPaddingAMailHouseHidesInFrontOfTheLetterIsNotShown()
    {
        var padding = new string('\u034F', 60);
        var text = MailText.FromHtml("<div>Update your profile</div><div>" + padding + "</div><div>Liam Novak</div>");

        Assert.Contains("Update your profile", text);
        Assert.Contains("Liam Novak", text);
        // Ordinal: a combining joiner carries no collation weight, so a culture-aware comparison
        // finds one in any string at all, including a string that no longer has one.
        Assert.DoesNotContain("\u034F", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingInMeansNothingOut()
    {
        Assert.Equal("", MailText.FromHtml(null));
        Assert.Equal("", MailText.FromHtml("   "));
    }
}
