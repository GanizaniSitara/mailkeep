using Mailkeep.Core;
using Mailkeep.Infrastructure;

namespace Mailkeep.Tests;

public sealed class DraftFilingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mailkeep-draft-filing-" + Guid.NewGuid().ToString("N"));
    private readonly MailkeepServices _services;
    public DraftFilingTests()
    {
        _services = new MailkeepServices(_root);
        _services.Database.Initialize();
        _services.Identities.Save([new SendingIdentity("fixture", null, "Fixture", "sender@example.invalid", null, true, "custom",
            new OutgoingServerSettings("127.0.0.1", 1), null, null, "fixture", SendingIdentityStates.Ready)]);
    }

    [Fact]
    public void FiledDraftRemainsEditableAndRetainsRecipientsHtmlAndAttachmentAcrossRestart()
    {
        var attachment = Path.Combine(_root, "retained.txt");
        File.WriteAllText(attachment, "attachment bytes");
        var draft = _services.Send.SaveDraft("fixture", ["to@example.invalid"], "Draft", "Body", ["cc@example.invalid"], ["bcc@example.invalid"],
            attachmentPaths: [attachment], bodyHtml: "<p>HTML body</p>");
        var folder = _services.Database.CreateLocalFolder("My Stuff", null);
        Assert.Equal(1, _services.Database.MoveDrafts([draft.Id, draft.Id], folder.Key));
        Assert.Empty(_services.Database.ListDraftsInFolder(LocalFolderKinds.Drafts));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(draft),
            System.Text.Json.JsonSerializer.Serialize(Assert.Single(_services.Database.ListDraftsInFolder(folder.Key))));
        var restarted = new MailkeepServices(_root);
        restarted.Database.Initialize();
        var edited = restarted.Send.SaveDraft("fixture", draft.To, "Edited", draft.BodyText, draft.Cc, draft.Bcc, draft.Id, bodyHtml: draft.BodyHtml);
        Assert.Equal(folder.Key, restarted.Database.DraftFolder(draft.Id));
        Assert.Equal(draft.RawPath, edited.RawPath);
        Assert.Equal(draft.To, edited.To); Assert.Equal(draft.Cc, edited.Cc); Assert.Equal(draft.Bcc, edited.Bcc);
        Assert.Equal(draft.BodyHtml, edited.BodyHtml);
        Assert.Equal("attachment bytes", File.ReadAllText(restarted.Database.ExtractOutgoingAttachment(draft.Id, 0)));
        Assert.Null(restarted.Database.ClaimNextOutgoing());
        Assert.Empty(restarted.Database.ListOutgoing(OutgoingStates.Outbox));
        Assert.Equal(1, restarted.Database.MoveDrafts([draft.Id], LocalFolderKinds.Drafts));
        Assert.Empty(restarted.Database.ListDraftsInFolder(folder.Key));
        Assert.Single(restarted.Database.ListDraftsInFolder(LocalFolderKinds.Drafts));
    }

    [Fact]
    public async Task InvalidMixedSelectionRollsBackAndDraggingCannotQueueOrStealQueuedMail()
    {
        var first = Save(); var queued = Save();
        await _services.Send.QueueAsync(queued.Id);
        var folder = _services.Database.CreateLocalFolder("Filed", null);
        Assert.Throws<InvalidOperationException>(() => _services.Database.MoveDrafts([first.Id, queued.Id], folder.Key));
        Assert.Equal(LocalFolderKinds.Drafts, _services.Database.DraftFolder(first.Id));
        Assert.Throws<InvalidOperationException>(() => _services.Database.MoveDrafts([first.Id], LocalFolderKinds.Outbox));
        Assert.Throws<KeyNotFoundException>(() => _services.Database.MoveDrafts([first.Id], "missing"));
        Assert.Equal(OutgoingStates.Draft, _services.Database.GetOutgoing(first.Id)!.Status);
        Assert.Equal(OutgoingStates.Outbox, _services.Database.GetOutgoing(queued.Id)!.Status);
    }

    [Fact]
    public async Task ExplicitQueueAndReturnToDraftResetPlacementWithoutSending()
    {
        var draft = Save(); var folder = _services.Database.CreateLocalFolder("Filed", null);
        _services.Database.MoveDrafts([draft.Id], folder.Key);
        await _services.Send.QueueAsync(draft.Id);
        Assert.Empty(_services.Database.ListDraftsInFolder(folder.Key));
        var returned = _services.Send.ReturnToDraft(draft.Id);
        Assert.Equal(OutgoingStates.Draft, returned.Status);
        Assert.Single(_services.Database.ListDraftsInFolder(LocalFolderKinds.Drafts));
        Assert.Null(_services.Database.ClaimNextOutgoing());
    }

    [Fact]
    public void FolderCannotDisappearWithDraftsAndDiscardCleansUpPlacement()
    {
        var draft = Save(); var folder = _services.Database.CreateLocalFolder("Filed", null);
        _services.Database.MoveDrafts([draft.Id], folder.Key);
        Assert.Throws<InvalidOperationException>(() => _services.Database.DeleteLocalFolder(folder.Key));
        Assert.True(_services.Send.DiscardDraft(draft.Id));
        _services.Database.DeleteLocalFolder(folder.Key);
        Assert.DoesNotContain(_services.Database.ListLocalFolders(), row => row.Key == folder.Key);
    }

    private OutgoingMessage Save() => _services.Send.SaveDraft("fixture", ["to@example.invalid"], "Draft", "Body");
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_root, true); }
}
