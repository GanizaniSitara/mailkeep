using Mailkeep.Core;
using Mailkeep.Infrastructure;
using MimeKit;

namespace Mailkeep.Tests;

public sealed class AttachmentReadingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mailkeep-tests", Guid.NewGuid().ToString("N"));
    private readonly MailkeepServices _services;

    public AttachmentReadingTests()
    {
        _services = new MailkeepServices(_root);
        _services.Database.Initialize();
    }

    [Theory]
    [InlineData(MailContract.LiveSource)]
    [InlineData(MailContract.ArchiveSource)]
    public void DuplicateNamesExtractDistinctDecodedContentWithoutChangingTheMessage(string source)
    {
        using var message = new MimeMessage();
        message.Body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "The letter" },
            FilePart("report.bin", [0, 255, 13, 10, 128]),
            FilePart("report.bin", [1, 2, 3])
        };
        var (id, raw) = Store(message, source);
        var before = File.ReadAllBytes(raw);
        var content = _services.Database.GetMessage(id)!;
        Assert.Equal(2, content.Attachments.Count);
        var first = _services.Database.ExtractAttachment(id, 0);
        var second = _services.Database.ExtractAttachment(id, 1);
        Assert.NotEqual(first, second);
        Assert.Equal(new byte[] { 0, 255, 13, 10, 128 }, File.ReadAllBytes(first));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(second));
        Assert.Equal(before, File.ReadAllBytes(raw));
        Assert.Throws<ArgumentOutOfRangeException>(() => _services.Database.ExtractAttachment(id, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => _services.Database.ExtractAttachment(id, 2));
    }

    [Theory]
    [InlineData("../../outside.txt", "outside.txt")]
    [InlineData("..\\..\\outside.txt", "outside.txt")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("...", "attachment")]
    public void AttachmentNamesStayInsideThePreviewDirectory(string name, string expected)
    {
        using var message = new MimeMessage { Body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "The letter" }, FilePart(name, [7])
        } };
        var (id, _) = Store(message);
        var output = _services.Database.ExtractAttachment(id, 0);
        Assert.Equal(expected, Path.GetFileName(output));
        Assert.StartsWith(Path.GetFullPath(_services.Paths.PreviewDirectory) + Path.DirectorySeparatorChar, Path.GetFullPath(output));
        Assert.Equal(expected, _services.Database.GetMessage(id)!.Attachments[0].FileName);
    }

    [Fact]
    public void InlineImagesUseIncludedBytesWhileRemoteAndMissingImagesRemainUnchanged()
    {
        var builder = new BodyBuilder
        {
            HtmlBody = "<img src=\"cid:logo%40fixture\"><div background='cid:logo@fixture' style=\"background:url(cid:logo@fixture)\"></div>"
                + "<img src='https://example.invalid/remote.png'><img src='cid:missing'><p>cid:logo@fixture</p>"
        };
        var image = builder.LinkedResources.Add("logo.png", new byte[] { 1, 2, 3 }, new ContentType("image", "png"));
        image.ContentId = "logo@fixture";
        using var message = new MimeMessage { Body = builder.ToMessageBody() };
        var (id, raw) = Store(message);
        var original = File.ReadAllBytes(raw);
        var html = _services.Database.GetMessage(id)!.BodyHtml!;
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(html, "data:image/png;base64,AQID").Count);
        Assert.Contains("https://example.invalid/remote.png", html);
        Assert.Contains("src='cid:missing'", html);
        Assert.Contains("<p>cid:logo@fixture</p>", html);
        Assert.Equal(original, File.ReadAllBytes(raw));
    }

    [Fact]
    public void AContentIdOnANonImageDoesNotBecomeAnEmbeddedImage()
    {
        var builder = new BodyBuilder { HtmlBody = "<img src='cid:document'>" };
        var file = builder.LinkedResources.Add("document.txt", new byte[] { 65 }, new ContentType("text", "plain"));
        file.ContentId = "document";
        using var message = new MimeMessage { Body = builder.ToMessageBody() };
        var (id, _) = Store(message);
        Assert.Contains("src='cid:document'", _services.Database.GetMessage(id)!.BodyHtml);
    }

    [Fact]
    public void AttachedEmailCanBeSavedAsAnEmail()
    {
        using var nested = new MimeMessage { Subject = "Original subject", Body = new TextPart("plain") { Text = "Original body" } };
        using var message = new MimeMessage { Body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "Forwarded below" },
            new MessagePart { Message = nested, ContentDisposition = new ContentDisposition("attachment") }
        } };
        var (id, _) = Store(message);
        var output = _services.Database.ExtractAttachment(id, 0);
        Assert.Equal("message.eml", Path.GetFileName(output));
        using var saved = MimeMessage.Load(output);
        Assert.Equal("Original subject", saved.Subject);
        Assert.Equal("Original body", saved.TextBody.TrimEnd('\r', '\n'));
    }

    [Theory]
    [InlineData(MailContract.LiveSource)]
    [InlineData(MailContract.ArchiveSource)]
    public void ReadingPreservesReplyToMailboxesAndHandlesMissingOriginal(string source)
    {
        using var message = new MimeMessage { Body = new TextPart("plain") { Text = "Reply fixture" } };
        message.ReplyTo.Add(new MailboxAddress("Reply desk", "reply@example.invalid"));
        message.ReplyTo.Add(new MailboxAddress("Second desk", "second@example.invalid"));
        var (id, raw) = Store(message, source);
        var original = File.ReadAllBytes(raw);
        var replyTo = _services.Database.GetMessage(id)!.ReplyTo;
        Assert.Equal(new[] { "reply@example.invalid", "second@example.invalid" }, replyTo.Select(value => MailboxAddress.Parse(value).Address));
        Assert.Equal("Reply desk", MailboxAddress.Parse(replyTo[0]).Name);
        Assert.Equal(original, File.ReadAllBytes(raw));
        File.Delete(raw);
        Assert.Empty(_services.Database.GetMessage(id)!.ReplyTo);
    }

    private (long Id, string Raw) Store(MimeMessage message, string source = MailContract.LiveSource)
    {
        using var bytes = new MemoryStream();
        message.WriteTo(bytes);
        var raw = _services.Blobs.Put(bytes.ToArray());
        var id = _services.Database.Upsert(new MessageInput(source, "attachment-fixture", LocalFolderKinds.Inbox,
            Guid.NewGuid().ToString("N"), null, 0, "Attachment fixture", "sender@example.invalid", "Sender",
            ["recipient@example.invalid"], DateTimeOffset.UtcNow, bytes.Length, false, message.Attachments.Count(),
            message.TextBody ?? "", message.HtmlBody, raw));
        return (id, _services.Blobs.FullPath(raw));
    }

    private static MimePart FilePart(string name, byte[] bytes) => new("application", "octet-stream")
    {
        FileName = name, ContentDisposition = new ContentDisposition("attachment") { FileName = name },
        Content = new MimeContent(new MemoryStream(bytes)), ContentTransferEncoding = ContentEncoding.Base64
    };

    public void Dispose()
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mailkeep-tests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(_root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid test root");
        Directory.Delete(_root, recursive: true);
    }
}
