using Mailkeep.Core;

namespace Mailkeep.Tests;

public sealed class MailtoLinkTests
{
    [Fact]
    public void ABareAddressIsTheRecipient()
    {
        var link = MailtoLink.Parse("mailto:someone@example.com");

        Assert.Equal("someone@example.com", link.To);
        Assert.Equal("", link.Subject);
    }

    [Fact]
    public void EveryFieldIsReadAndDecoded()
    {
        var link = MailtoLink.Parse(
            "MAILTO:a@example.com,b@example.com?cc=c%40example.com&bcc=d@example.com&subject=Hello%20there&body=Line%201%0ALine%202");

        Assert.Equal("a@example.com; b@example.com", link.To);
        Assert.Equal("c@example.com", link.Cc);
        Assert.Equal("d@example.com", link.Bcc);
        Assert.Equal("Hello there", link.Subject);
        Assert.Equal("Line 1\r\nLine 2", link.Body);
    }

    /// <summary>RFC 6068 leaves '+' as a plus: reading it as a space would break name+tag addresses.</summary>
    [Fact]
    public void PlusStaysAPlus()
    {
        var link = MailtoLink.Parse("mailto:name+tag@example.com?subject=1+1");

        Assert.Equal("name+tag@example.com", link.To);
        Assert.Equal("1+1", link.Subject);
    }

    [Fact]
    public void ToInTheQueryAddsToThePathRecipients()
    {
        var link = MailtoLink.Parse("mailto:?to=a@example.com&to=b@example.com");

        Assert.Equal("a@example.com; b@example.com", link.To);
    }
}
