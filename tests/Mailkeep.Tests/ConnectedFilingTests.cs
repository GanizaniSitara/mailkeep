using Mailkeep.Core;
using Mailkeep.Infrastructure;
using MimeKit;

namespace Mailkeep.Tests;

public sealed class ConnectedFilingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mailkeep-tests", Guid.NewGuid().ToString("n"));
    private readonly MailkeepServices _services;

    public ConnectedFilingTests()
    {
        _services = new MailkeepServices(_root);
        _services.Database.Initialize();
    }

    [Fact]
    public async Task ConnectedCopiesBecomeIndependentLocalMailWithMimeSearchReadAndDelete()
    {
        var destination = _services.Database.CreateLocalFolder("Filed locally", null);
        var later = _services.Database.CreateLocalFolder("Filed again", null);
        var first = Store("connected-one", "Unique connected filing words", "one.txt", "first bytes");
        var second = Store("connected-two", "Other connected body", "two.txt", "second bytes");

        Assert.Equal(2, _services.Database.CopyMessagesToLocal([first, second, first], destination.Key));
        Assert.NotNull(_services.Database.GetMessage(first));
        Assert.NotNull(_services.Database.GetMessage(second));
        var copies = _services.Database.ListMessagesForAccounts([MailContract.LocalCopyAccount], destination.Key);
        Assert.Equal(2, copies.Count);
        Assert.All(copies, copy =>
        {
            Assert.True(copy.IsLocalStoredCopy);
            Assert.Null(copy.ServerUid);
        });
        var firstCopy = copies.Single(copy => copy.Subject == "connected-one");
        Assert.Equal("first bytes", File.ReadAllText(_services.Database.ExtractAttachment(firstCopy.Id, 0)));

        Assert.Equal(1, await _services.Sync.SetReadManyAsync([firstCopy], true));
        Assert.True(_services.Database.GetMessage(firstCopy.Id)!.Summary.IsRead);
        Assert.Equal(1, _services.Database.MoveMessages([firstCopy.Id], later.Key));
        var moved = _services.Database.GetMessage(firstCopy.Id)!.Summary;
        Assert.Equal(later.Key, moved.Folder);
        var deleted = await _services.Sync.DeleteManyAsync([moved]);
        Assert.Equal(1, deleted.Deleted);
        Assert.Equal(LocalFolderKinds.Deleted, _services.Database.GetMessage(firstCopy.Id)!.Summary.Folder);

        _services.Catalog.SyncOperational(force: true);
        var hit = Assert.Single(_services.Catalog.Search("Unique connected filing words", MailContract.LocalSource, 10).Results);
        Assert.Equal(firstCopy.Id, hit.Id);
        Assert.DoesNotContain(_services.Catalog.Search("Unique connected filing words", MailContract.ConnectedSource, 10).Results,
            result => result.Id == firstCopy.Id);
    }

    [Fact]
    public void CopyValidationRollsBackTheWholeSelection()
    {
        var destination = _services.Database.CreateLocalFolder("Destination", null);
        var connected = Store("connected", "body", "one.txt", "one");
        var archived = _services.Database.Upsert(new MessageInput(MailContract.ArchiveSource, "archive", "Inbox", "archive-1",
            null, 0, "archive", "sender@example.invalid", "Sender", [], DateTimeOffset.UtcNow, 1, false, 0, "body", null, null));
        Assert.Throws<KeyNotFoundException>(() => _services.Database.CopyMessagesToLocal([connected], "missing"));
        Assert.Throws<InvalidOperationException>(() => _services.Database.CopyMessagesToLocal([connected], LocalFolderKinds.Drafts));
        Assert.Throws<KeyNotFoundException>(() => _services.Database.CopyMessagesToLocal([connected, long.MaxValue], destination.Key));
        Assert.Empty(_services.Database.ListMessagesForAccounts([MailContract.LocalCopyAccount], destination.Key));
        // Imported mail can be copied too — that is how a read-only import becomes the mailbox's own — and
        // the copies are live whatever they were copied from.
        Assert.Equal(2, _services.Database.CopyMessagesToLocal([connected, archived], destination.Key));
        var copies = _services.Database.ListMessagesForAccounts([MailContract.LocalCopyAccount], destination.Key);
        Assert.Equal(2, copies.Count);
        Assert.All(copies, copy => Assert.Equal(MailContract.LiveSource, copy.Source));
    }

    private long Store(string subject, string body, string attachment, string bytes)
    {
        var builder = new BodyBuilder { TextBody = body };
        builder.Attachments.Add(attachment, System.Text.Encoding.UTF8.GetBytes(bytes));
        using var message = new MimeMessage { Subject = subject, Body = builder.ToMessageBody() };
        message.From.Add(new MailboxAddress("Sender", "sender@example.invalid"));
        message.To.Add(MailboxAddress.Parse("reader@example.invalid"));
        using var raw = new MemoryStream();
        message.WriteTo(raw);
        raw.Position = 0;
        return _services.Database.UpsertMimeMessageAsync(MailContract.LiveSource, "connected-fixture", "Inbox",
            "1:" + Guid.NewGuid().ToString("n"), 1, 1, message, raw, false).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mailkeep-tests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(_root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid test root");
        Directory.Delete(_root, true);
    }
}
