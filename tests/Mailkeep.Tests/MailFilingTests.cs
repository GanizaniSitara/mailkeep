using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Tests;

public sealed class MailFilingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mailkeep-filing-" + Guid.NewGuid().ToString("N"));
    private readonly MailkeepServices _services;
    private readonly MailFiling _filing;

    public MailFilingTests()
    {
        _services = new MailkeepServices(_root);
        _services.Database.Initialize();
        _services.Accounts.Save([
            new MailAccount("local", "custom", "local@example.invalid", "INBOX", "inbox", "127.0.0.1", null, null, null, MailboxModes.LocalKeep),
            new MailAccount("remote", "custom", "remote@example.invalid", "INBOX", "remote", "127.0.0.1", null, null, null, MailboxModes.Connected)
        ]);
        _filing = new MailFiling(_services);
    }

    [Fact]
    public async Task PreviewMakesNoWritesAndExecutionPreservesMessageIdContentAndReadState()
    {
        var parent = _services.Database.CreateLocalFolder("Filed", null);
        var child = _services.Database.CreateLocalFolder("Empty", parent.Key);
        var id = Add("live", "local", "inbox", "local-one");
        SqliteConnection.ClearAllPools();
        var before = File.ReadAllBytes(_services.Paths.DatabasePath);
        var preview = await _filing.MoveAsync(id.ToString(), "Filed/Empty");
        Assert.False(preview.Executed);
        Assert.Equal("local_move", preview.Plan.Action);
        Assert.Equal(child.Key, preview.Plan.Destination);
        Assert.Equal(before, File.ReadAllBytes(_services.Paths.DatabasePath));
        Assert.Contains(_filing.Folders(), folder => folder.Key == child.Key && folder.CanMoveInto);
        var result = await _filing.MoveAsync(id.ToString(), child.Key, execute: true);
        Assert.Equal(1, result.Changed);
        Assert.Equal(id, result.ResultId);
        var moved = _services.Database.GetMessage(id)!;
        Assert.Equal(child.Key, moved.Summary.Folder);
        Assert.True(moved.Summary.IsRead);
        Assert.Equal("retained body", moved.BodyText);
        Assert.Equal(0, (await _filing.MoveAsync(id.ToString(), child.Key, execute: true)).Changed);
        Assert.Empty(_services.Database.ListOutgoing());
    }

    [Fact]
    public async Task ImportedFilingUsesExistingAdoptionAndReturnsTheWritableIdWithoutMerging()
    {
        var id = Add("archive", "OE Classic", "Inbox", "imported");
        Assert.Equal("adopt_imported_using_existing_app_behavior", _filing.Preview(id.ToString(), "Inbox").Action);
        var result = await _filing.MoveAsync(id.ToString(), "inbox", execute: true);
        Assert.NotEqual(id, result.ResultId);
        Assert.Equal("archive", _services.Database.GetMessage(id)!.Summary.Source);
        var copy = _services.Database.GetMessage(result.ResultId!.Value)!.Summary;
        Assert.True(copy.IsLocalStoredCopy);
        var later = await _filing.MoveAsync(id.ToString(), "sent", execute: true);
        Assert.Equal(result.ResultId, later.ResultId);
        Assert.Equal(2, _services.Database.ListMessages().Count);
    }

    [Fact]
    public async Task ConnectedMailRequiresExplicitServerModeAndPreviewNeverConnects()
    {
        var id = Add("live", "remote", "remote", "1:42", 42);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _filing.MoveAsync(id.ToString(), "inbox", execute: true));
        var result = await _filing.MoveAsync(id.ToString(), "Archive", server: true);
        Assert.False(result.Executed);
        Assert.Equal("server_move", result.Plan.Action);
        Assert.Equal("remote", _services.Database.GetMessage(id)!.Summary.Folder);
        var local = Add("live", "local", "inbox", "local");
        Assert.Throws<InvalidOperationException>(() => _filing.Preview(local.ToString(), "Archive", server: true));
    }

    [Theory]
    [InlineData("Archive")]
    [InlineData("Deleted Items")]
    public async Task ServerExecutionQueuesWithoutNetworkOrCredentials(string destination)
    {
        var id = Add("live", "remote", "remote", "1:42", 42);
        var result = await _filing.MoveAsync(id.ToString(), destination, execute: true, server: true);
        Assert.True(result.Executed);
        Assert.True(result.RemoteQueued);
        Assert.Equal(1, result.Changed);
        Assert.Null(_services.Database.GetMessage(id));
        Assert.Equal(destination, Assert.Single(_services.Database.PendingRemoteOperations()).Destination);
    }

    [Theory]
    [InlineData("drafts")]
    [InlineData("outbox")]
    [InlineData("does-not-exist")]
    public async Task InvalidDestinationsNeverMoveOrQueueMail(string folder)
    {
        var id = Add("live", "local", "inbox", "local");
        await Assert.ThrowsAnyAsync<Exception>(() => _filing.MoveAsync(id.ToString(), folder, execute: true));
        Assert.Equal("inbox", _services.Database.GetMessage(id)!.Summary.Folder);
        Assert.Empty(_services.Database.ListOutgoing());
    }

    [Fact]
    public async Task CatalogReferencesResolveOnlyTheExactCurrentOccurrence()
    {
        var id = Add("live", "local", "inbox", "local");
        _services.Catalog.Initialize();
        _services.Catalog.SyncOperational();
        var reference = Assert.Single(_services.Catalog.Search("retained", "local", 10).Results).MessageRef!;
        Assert.Equal(id, _filing.Preview(reference, "sent").Id);
        await _filing.MoveAsync(id.ToString(), "sent", execute: true);
        Assert.Throws<InvalidOperationException>(() => _filing.Preview(reference, "inbox"));
    }

    [Fact]
    public async Task DeletedAdoptionCannotBeResurrectedByMovingItsImportedOriginal()
    {
        var id = Add("archive", "OE Classic", "Inbox", "imported");
        var result = await _filing.MoveAsync(id.ToString(), "inbox", execute: true);
        Assert.True(_services.Database.DeleteMessageLocally(result.ResultId!.Value));
        Assert.True(_services.Database.DeleteMessageLocally(result.ResultId.Value));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _filing.MoveAsync(id.ToString(), "inbox", execute: true));
        Assert.Single(_services.Database.ListMessages());
    }

    [Fact]
    public async Task MissingRootDoesNotCreateAnyStore()
    {
        var root = Path.Combine(_root, "absent");
        await Assert.ThrowsAsync<FileNotFoundException>(() => new MailFiling(new MailkeepServices(root)).MoveAsync("1", "inbox"));
        Assert.False(Directory.Exists(root));
    }

    private long Add(string source, string account, string folder, string key, long? uid = null)
        => _services.Database.Upsert(new MessageInput(source, account, folder, key, uid, 1, "Fixture", "sender@example.invalid",
            "Sender", [], DateTimeOffset.UnixEpoch, 10, true, 0, "retained body", null, null));

    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(_root, true); }
}
