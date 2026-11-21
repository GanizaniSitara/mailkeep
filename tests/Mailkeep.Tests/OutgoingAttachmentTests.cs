using Mailkeep.Core;
using Mailkeep.Infrastructure;
using MimeKit;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Tests;

public sealed class OutgoingAttachmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mailkeep-tests", Guid.NewGuid().ToString("N"));
    private readonly MailkeepServices _services;

    public OutgoingAttachmentTests()
    {
        _services = new MailkeepServices(_root);
        _services.Database.Initialize();
        _services.Identities.Save([new SendingIdentity("fixture", null, "Fixture", "sender@example.invalid", null, true, "custom",
            new OutgoingServerSettings("smtp.example.invalid", 465), null, null, "fixture-password", SendingIdentityStates.Ready)]);
    }

    [Fact]
    public void SavedDraftSurvivesOriginalFileDeletionAndEditsPreserveItsSnapshot()
    {
        var path = Original("report.txt", "saved bytes");
        var draft = Save([path]);
        File.WriteAllText(path, "changed later");
        File.Delete(path);
        var reopened = new MailkeepServices(_root);
        var edited = reopened.Send.SaveDraft("fixture", ["to@example.invalid"], "Edited subject", "Edited body", id: draft.Id);
        Assert.Equal(draft.RawPath, edited.RawPath);
        Assert.Equal("Edited body", edited.BodyText);
        Assert.Equal("report.txt", Assert.Single(reopened.Database.GetOutgoingAttachments(draft.Id)).FileName);
        Assert.Equal("saved bytes", File.ReadAllText(reopened.Database.ExtractOutgoingAttachment(draft.Id, 0)));
    }

    [Fact]
    public async Task QueueAndSentCopyRetainAttachmentsAndDoNotSendTwice()
    {
        var first = Original("first.txt", "first bytes");
        var second = Original("second.txt", "second bytes");
        var draft = Save([first, second]);
        File.Delete(first);
        File.Delete(second);
        var transport = new CapturingTransport();
        var sender = new MailSendService(_services.Identities, _services.Database, _services.Blobs, transport);
        var queued = await sender.QueueAsync(draft.Id);
        using (var mime = MimeMessage.Load(_services.Blobs.FullPath(queued.RawPath!)))
        {
            Assert.Equal("Draft body", mime.TextBody.TrimEnd('\r', '\n'));
            Assert.Equal(["first.txt", "second.txt"], mime.Attachments.OfType<MimePart>().Select(part => part.FileName));
        }
        Assert.True(Assert.Single(await sender.SendPendingAsync()).Ok);
        Assert.Empty(await sender.SendPendingAsync());
        Assert.Equal(1, transport.Sends);
        Assert.Equal(["first bytes", "second bytes"], transport.Contents);
        var sent = Assert.Single(_services.Database.ListMessagesForAccounts(["fixture"], LocalFolderKinds.Sent));
        Assert.Equal(2, _services.Database.GetMessage(sent.Id)!.Attachments.Count);
        Assert.Equal("second bytes", File.ReadAllText(_services.Database.ExtractAttachment(sent.Id, 1)));
    }

    [Fact]
    public async Task RemovingAllAttachmentsMakesTheQueuedMessagePlainTextAgain()
    {
        var draft = Save([Original("remove.txt", "discard")]);
        var edited = _services.Send.SaveDraft("fixture", ["to@example.invalid"], "No attachment", "Just words", id: draft.Id, attachmentPaths: []);
        Assert.Null(edited.RawPath);
        Assert.Empty(_services.Database.GetOutgoingAttachments(draft.Id));
        var queued = await _services.Send.QueueAsync(draft.Id);
        using var mime = MimeMessage.Load(_services.Blobs.FullPath(queued.RawPath!));
        Assert.Empty(mime.Attachments);
        Assert.Equal("Just words", mime.TextBody.TrimEnd('\r', '\n'));
    }

    [Fact]
    public async Task RecipientlessDraftIsSavedButCannotBeQueuedUntilAddressed()
    {
        var draft = _services.Send.SaveDraft("fixture", [], "Unfinished", "Work in progress",
            attachmentPaths: [Original("notes.txt", "notes")]);
        Assert.Empty(_services.Database.GetOutgoing(draft.Id)!.To);
        await Assert.ThrowsAsync<InvalidDataException>(() => _services.Send.QueueAsync(draft.Id));
        Assert.Equal(OutgoingStates.Draft, _services.Database.GetOutgoing(draft.Id)!.Status);
        _services.Send.SaveDraft("fixture", ["recipient@example.invalid"], draft.Subject, draft.BodyText, id: draft.Id);
        var queued = await _services.Send.QueueAsync(draft.Id);
        Assert.Equal(OutgoingStates.Outbox, queued.Status);
        Assert.Single(_services.Database.GetOutgoingAttachments(draft.Id));
    }

    [Fact]
    public void MissingAttachmentLeavesThePreviousDraftIntact()
    {
        var draft = Save([Original("retained.txt", "retained")]);
        Assert.Throws<FileNotFoundException>(() => _services.Send.SaveDraft("fixture", ["to@example.invalid"], "Unsaved edit", "Unsaved body",
            id: draft.Id, attachmentPaths: [Path.Combine(_root, "missing.txt")]));
        var retained = _services.Database.GetOutgoing(draft.Id)!;
        Assert.Equal(draft.Subject, retained.Subject);
        Assert.Equal(draft.BodyText, retained.BodyText);
        Assert.Equal(draft.RawPath, retained.RawPath);
        Assert.Equal(draft.UpdatedAt, retained.UpdatedAt);
        Assert.Equal("retained", File.ReadAllText(_services.Database.ExtractOutgoingAttachment(draft.Id, 0)));
    }

    [Fact]
    public async Task ReturningQueuedMailToDraftPreservesBytesAndAllowsEditingAndRequeueing()
    {
        var original = Save([Original("retained.txt", "original bytes")]);
        var queued = await _services.Send.QueueAsync(original.Id);
        Assert.NotNull(_services.Database.ClaimNextOutgoing());
        _services.Database.MarkOutgoingFailed(original.Id, "Fixture failure");
        var draft = _services.Send.ReturnToDraft(original.Id);
        Assert.Equal(OutgoingStates.Draft, draft.Status);
        Assert.Equal(queued.RawPath, draft.RawPath);
        Assert.Equal(original.BodyText, draft.BodyText);
        Assert.Equal(original.To, draft.To);
        Assert.Null(draft.NextAttemptAt);
        Assert.Null(draft.LastError);
        Assert.Equal(0, draft.AttemptCount);
        Assert.Null(_services.Database.ClaimNextOutgoing());
        Assert.Equal("original bytes", File.ReadAllText(_services.Database.ExtractOutgoingAttachment(draft.Id, 0)));
        _services.Send.SaveDraft("fixture", ["changed@example.invalid"], "Edited", "Edited body", id: draft.Id);
        var requeued = await _services.Send.QueueAsync(draft.Id);
        using var mime = MimeMessage.Load(_services.Blobs.FullPath(requeued.RawPath!));
        Assert.Equal("Edited", mime.Subject);
        Assert.Equal("changed@example.invalid", Assert.Single(mime.To.Mailboxes).Address);
        Assert.Equal("Edited body", mime.TextBody.TrimEnd('\r', '\n'));
        Assert.Single(mime.Attachments);
    }

    [Fact]
    public async Task ReturningToDraftCannotStealAClaimedOrSentMessage()
    {
        var draft = Save([]);
        Assert.Throws<InvalidOperationException>(() => _services.Send.ReturnToDraft(draft.Id));
        await _services.Send.QueueAsync(draft.Id);
        Assert.NotNull(_services.Database.ClaimNextOutgoing());
        Assert.Throws<InvalidOperationException>(() => _services.Send.ReturnToDraft(draft.Id));
        Assert.Equal("sending", _services.Database.GetOutgoing(draft.Id)!.Status);
        _services.Database.MarkOutgoingSent(draft.Id);
        Assert.Throws<InvalidOperationException>(() => _services.Send.ReturnToDraft(draft.Id));
        Assert.Equal(OutgoingStates.Sent, _services.Database.GetOutgoing(draft.Id)!.Status);
        Assert.Throws<InvalidOperationException>(() => _services.Send.ReturnToDraft("missing"));
    }

    private string Original(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task ExplicitSendRetriesBackoffOnceAndOnlyTheConfirmedMessages()
    {
        var first = Save([]);
        await _services.Send.QueueAsync(first.Id);
        var transport = new RetryTransport();
        var sender = new MailSendService(_services.Identities, _services.Database, _services.Blobs, transport);
        Assert.False(Assert.Single(await sender.SendPendingAsync()).Ok);
        Assert.True(_services.Database.GetOutgoing(first.Id)!.NextAttemptAt > DateTimeOffset.UtcNow);
        Assert.Empty(await sender.SendPendingAsync());
        var later = Save([]);
        await _services.Send.QueueAsync(later.Id);
        Assert.False(Assert.Single(await sender.SendQueuedAsync([first.Id, first.Id])).Ok);
        Assert.Equal(2, transport.Attempts);
        Assert.Equal(0, _services.Database.GetOutgoing(later.Id)!.AttemptCount);
        Assert.Equal(OutgoingStates.Outbox, _services.Database.GetOutgoing(later.Id)!.Status);
        transport.Fail = false;
        Assert.True(Assert.Single(await sender.SendQueuedAsync([first.Id])).Ok);
        Assert.Equal(3, transport.Attempts);
        Assert.Equal(OutgoingStates.Sent, _services.Database.GetOutgoing(first.Id)!.Status);
        Assert.False(Assert.Single(await sender.SendQueuedAsync([first.Id])).Ok);
        Assert.Equal(3, transport.Attempts);
        Assert.Single(_services.Database.ListMessagesForAccounts(["fixture"], LocalFolderKinds.Sent));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedDeliveryRecoversFailedSentRecordingWithoutAnotherSend(bool retryById)
    {
        var draft = Save([]);
        await _services.Send.QueueAsync(draft.Id);
        using var connection = new SqliteConnection($"Data Source={_services.Paths.DatabasePath};Pooling=False");
        connection.Open();
        using var fault = connection.CreateCommand();
        fault.CommandText = """
            CREATE TRIGGER fail_sent BEFORE UPDATE OF status ON outgoing_messages WHEN NEW.status='sent'
            BEGIN SELECT RAISE(ABORT,'fixture sent recording failure'); END;
            """;
        fault.ExecuteNonQuery();
        var transport = new CapturingTransport();
        var sender = new MailSendService(_services.Identities, _services.Database, _services.Blobs, transport);
        var accepted = Assert.Single(await sender.SendQueuedAsync([draft.Id]));
        Assert.True(accepted.Ok);
        Assert.Contains("fixture sent recording failure", accepted.Error);
        var pending = _services.Database.GetOutgoing(draft.Id)!;
        Assert.Equal(OutgoingStates.SentUnrecorded, pending.Status);
        Assert.NotNull(pending.SentAt);
        Assert.Equal(0, pending.AttemptCount);
        Assert.Null(pending.NextAttemptAt);
        Assert.Null(_services.Database.ClaimOutgoing(draft.Id));
        Assert.Null(_services.Database.ClaimNextOutgoing());

        fault.CommandText = "DROP TRIGGER fail_sent";
        fault.ExecuteNonQuery();
        var reopened = new MailkeepServices(_root);
        var recovery = new MailSendService(reopened.Identities, reopened.Database, reopened.Blobs, transport);
        var recorded = Assert.Single(retryById ? await recovery.SendQueuedAsync([draft.Id]) : await recovery.SendPendingAsync());
        Assert.True(recorded.Ok);
        Assert.Null(recorded.Error);
        var sent = reopened.Database.GetOutgoing(draft.Id)!;
        Assert.Equal(OutgoingStates.Sent, sent.Status);
        Assert.Equal(pending.SentAt, sent.SentAt);
        Assert.Single(reopened.Database.ListMessagesForAccounts(["fixture"], LocalFolderKinds.Sent));
        Assert.Empty(await recovery.SendPendingAsync());
        Assert.Equal(1, transport.Sends);
    }

    [Fact]
    public async Task CancellationDoesNotClaimTheRemainingConfirmedMessages()
    {
        var first = Save([]);
        var second = Save([]);
        await _services.Send.QueueAsync(first.Id);
        await _services.Send.QueueAsync(second.Id);
        using var cancellation = new CancellationTokenSource();
        var transport = new RetryTransport { OnAttempt = cancellation.Cancel };
        var sender = new MailSendService(_services.Identities, _services.Database, _services.Blobs, transport);
        Assert.False(Assert.Single(await sender.SendQueuedAsync([first.Id, second.Id], cancellation.Token)).Ok);
        Assert.Equal(1, transport.Attempts);
        Assert.Equal(0, _services.Database.GetOutgoing(second.Id)!.AttemptCount);
        Assert.Equal(OutgoingStates.Outbox, _services.Database.GetOutgoing(second.Id)!.Status);
        Assert.Empty(await sender.SendPendingAsync(cancellation.Token));
    }

    private sealed class RetryTransport : IOutgoingTransport
    {
        public bool Fail { get; set; } = true;
        public int Attempts { get; private set; }
        public Action? OnAttempt { get; set; }
        public Task VerifyAsync(SendingIdentity identity, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendAsync(SendingIdentity identity, MimeMessage message, CancellationToken cancellationToken = default)
        {
            Attempts++;
            OnAttempt?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (Fail) throw new IOException("Fixture SMTP failure");
            return Task.CompletedTask;
        }
    }

    private OutgoingMessage Save(IReadOnlyList<string> paths)
        => _services.Send.SaveDraft("fixture", ["to@example.invalid"], "Draft subject", "Draft body", attachmentPaths: paths);

    private sealed class CapturingTransport : IOutgoingTransport
    {
        public int Sends { get; private set; }
        public List<string> Contents { get; } = [];
        public Task VerifyAsync(SendingIdentity identity, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendAsync(SendingIdentity identity, MimeMessage message, CancellationToken cancellationToken = default)
        {
            Sends++;
            foreach (var part in message.Attachments.OfType<MimePart>())
            {
                using var bytes = new MemoryStream();
                part.Content.DecodeTo(bytes);
                Contents.Add(System.Text.Encoding.UTF8.GetString(bytes.ToArray()));
            }
            return Task.CompletedTask;
        }
    }

    public void Dispose()
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mailkeep-tests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(_root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid test root");
        Directory.Delete(_root, recursive: true);
    }
}
