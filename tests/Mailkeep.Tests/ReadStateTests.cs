using Mailkeep.TestFixtures;
using System.Net;
using System.Net.Sockets;
using Mailkeep.Core;
using Mailkeep.Infrastructure;

namespace Mailkeep.Tests;

public sealed class ReadStateTests
{
    [Fact]
    public async Task PermanentLocalDeleteBypassesDeletedItemsAndPreventsReimport()
    {
        using var fixture = new Fixture();
        fixture.Add(MailContract.LocalCopyAccount, "copy:purge", null);
        fixture.Add(MailContract.LocalCopyAccount, "copy:keep", null);
        var messages = fixture.Services.Database.ListMessages();
        var purge = messages.Single(message => message.SourceKey == "copy:purge");
        var keep = messages.Single(message => message.SourceKey == "copy:keep");
        var content = fixture.Services.Database.GetMessage(purge.Id)!;
        Assert.Equal(1, (await fixture.Services.Sync.DeleteManyAsync([purge], permanently: true)).Deleted);
        Assert.Null(fixture.Services.Database.GetMessage(purge.Id));
        Assert.Empty(fixture.Services.Database.ListMessagesForAccounts([MailContract.LocalCopyAccount], LocalFolderKinds.Deleted, 10));
        Assert.Empty(fixture.Services.Database.Search("purge").Results);
        Assert.NotNull(fixture.Services.Database.GetMessage(keep.Id));
        Assert.Equal(0, fixture.Services.Database.Upsert(new MessageInput(purge.Source, purge.Account, purge.Folder,
            purge.SourceKey, null, 0, purge.Subject, purge.Sender, purge.SenderName, purge.Recipients, purge.Date,
            purge.Size, purge.IsRead, purge.AttachmentCount, content.BodyText, content.BodyHtml, null)));
        await fixture.Services.Sync.DeleteManyAsync([keep]);
        Assert.Equal(LocalFolderKinds.Deleted, fixture.Services.Database.GetMessage(keep.Id)!.Summary.Folder);
    }

    [Theory]
    [InlineData(true, 42u, false, false)]
    [InlineData(false, 42u, false, false)]
    [InlineData(true, 43u, false, false)]
    [InlineData(true, 42u, true, false)]
    [InlineData(true, 42u, false, true)]
    public async Task PermanentConnectedDeleteQueuesUntilAcknowledgement(
        bool uidPlus, uint validity, bool rejectStore, bool rejectExpunge)
    {
        using var fixture = new Fixture();
        await using var server = new ImapFixture(rejectStore, validity, move: uidPlus, rejectExpunge: rejectExpunge);
        fixture.Services.Accounts.Save([new MailAccount("connected", "custom", "user@example.invalid", "INBOX", "inbox",
            "127.0.0.1", null, "fixture-secret", new KeepAliveSettings(false), MailboxModes.Connected,
            new IncomingServerSettings(MailProtocols.Imap, "127.0.0.1", server.Port, MailSecurityModes.Auto))]);
        fixture.Add("connected", "42:1", 1);
        fixture.Add("connected", "42:2", 2);
        var selected = fixture.Services.Database.ListMessages().Single(message => message.ServerUid == 1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal(1, (await fixture.Services.Sync.DeleteManyAsync([selected], cancellationToken: timeout.Token, permanently: true)).Deleted);
        Assert.Empty(server.Stores);
        Assert.Single(fixture.Services.Database.PendingRemoteOperations());
        await fixture.Services.Sync.DrainRemoteOperationsAsync(timeout.Token);
        var success = uidPlus && validity == 42 && !rejectStore && !rejectExpunge;
        Assert.Equal(success ? 0 : 1, fixture.Services.Database.RemoteStatus().Pending);
        Assert.Empty(server.Moves);
        Assert.Equal(uidPlus && validity == 42 ? 1 : 0, server.Stores.Count);
        Assert.All(server.Stores, command => Assert.Contains("STORE 1 +FLAGS.SILENT (\\Deleted)", command));
        Assert.All(server.Expunges, command => Assert.EndsWith("UID EXPUNGE 1", command));
        Assert.Equal(2, Assert.Single(fixture.Services.Database.ListMessages()).ServerUid);

    }

    [Theory]
    [InlineData(42u, false)]
    [InlineData(43u, false)]
    [InlineData(42u, true)]
    public async Task ServerMoveQueuesLocalChangesUntilAcknowledged(uint validity, bool rejectSecond)
    {
        using var fixture = new Fixture();
        await using var server = new ImapFixture(false, validity, move: true, rejectSecond: rejectSecond);
        fixture.Services.Accounts.Save([new MailAccount("connected", "custom", "user@example.invalid", "INBOX", "inbox",
            "127.0.0.1", null, "fixture-secret", new KeepAliveSettings(false), MailboxModes.Connected,
            new IncomingServerSettings(MailProtocols.Imap, "127.0.0.1", server.Port, MailSecurityModes.Auto))]);
        fixture.Add("connected", "42:1", 1);
        fixture.Add("connected", "42:2", 2);
        var messages = fixture.Services.Database.ListMessages().OrderBy(m => m.ServerUid).ToArray();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { "Filed" }, await fixture.Services.Sync.ConnectedMoveFoldersAsync(messages, timeout.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Services.Sync.MoveConnectedAsync(messages, "INBOX", timeout.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Services.Sync.MoveConnectedAsync(
            [messages[0] with { SourceKey = "42:999", ServerUid = 999 }], "Filed", timeout.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Services.Sync.MoveConnectedAsync(
            [messages[0] with { Source = MailContract.ArchiveSource }], "Filed", timeout.Token));
        Assert.Empty(server.Moves);
        Assert.Equal(2, await fixture.Services.Sync.MoveConnectedAsync(messages, "Filed", timeout.Token));
        Assert.Empty(fixture.Services.Database.ListMessages());
        await fixture.Services.Sync.DrainRemoteOperationsAsync(timeout.Token);
        Assert.Equal(validity != 42 ? 2 : 0, fixture.Services.Database.RemoteStatus().Pending);
        Assert.Equal(validity != 42 ? 0 : 1, server.Moves.Count);

    }

    [Fact]
    public async Task LocalSelectionAndSentCopiesChangeWithoutAProviderConnection()
    {
        using var fixture = new Fixture();
        fixture.Services.Accounts.Save([new MailAccount("local", "custom", "user@example.invalid", "INBOX", "inbox",
            "pop.example.invalid", null, "secret", new KeepAliveSettings(), MailboxModes.LocalKeep,
            new IncomingServerSettings(MailProtocols.Pop3, "pop.example.invalid", 995))]);
        fixture.Add("local", "pop:one", null);
        fixture.Add("sender", "sent:one", null);
        var messages = fixture.Services.Database.ListMessages();
        Assert.Equal(2, await fixture.Services.Sync.SetReadManyAsync(messages, true));
        Assert.All(fixture.Services.Database.ListMessages(), message => Assert.True(message.IsRead));
        Assert.Equal(0, await fixture.Services.Sync.SetReadManyAsync(messages, true));
        Assert.Equal(2, await fixture.Services.Sync.SetReadManyAsync(messages, false));
        Assert.All(fixture.Services.Database.ListMessages(), message => Assert.False(message.IsRead));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Services.Sync.SetReadManyAsync(
            [messages[0], messages[1] with { Source = MailContract.ArchiveSource }], true));
        Assert.All(fixture.Services.Database.ListMessages(), message => Assert.False(message.IsRead));
    }

    [Theory]
    [InlineData(false, 42u)]
    [InlineData(true, 42u)]
    [InlineData(false, 43u)]
    public async Task ConnectedFlagsAreLocalImmediatelyAndQueueRequiresMatchingUidValidity(bool rejectStore, uint serverValidity)
    {
        using var fixture = new Fixture();
        await using var server = new ImapFixture(rejectStore, serverValidity);
        fixture.Services.Accounts.Save([new MailAccount("connected", "custom", "user@example.invalid", "INBOX", "inbox",
            "127.0.0.1", null, "fixture-secret", new KeepAliveSettings(false), MailboxModes.Connected,
            new IncomingServerSettings(MailProtocols.Imap, "127.0.0.1", server.Port, MailSecurityModes.Auto))]);
        fixture.Add("connected", "42:1", 1);
        fixture.Add("connected", "42:2", 2);
        var messages = fixture.Services.Database.ListMessages();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal(2, await fixture.Services.Sync.SetReadManyAsync(messages, true, timeout.Token));
        Assert.Empty(server.Stores);
        Assert.All(fixture.Services.Database.ListMessages(), message => Assert.True(message.IsRead));
        await fixture.Services.Sync.DrainRemoteOperationsAsync(timeout.Token);
        Assert.Equal(rejectStore || serverValidity != 42 ? 2 : 0, fixture.Services.Database.RemoteStatus().Pending);
        Assert.Equal(serverValidity == 42 ? 1 : 0, server.Stores.Count);
        if (!rejectStore && serverValidity == 42)
        {
            Assert.Equal(2, await fixture.Services.Sync.SetReadManyAsync(messages, false, timeout.Token));
            await fixture.Services.Sync.DrainRemoteOperationsAsync(timeout.Token);
            Assert.All(fixture.Services.Database.ListMessages(), message => Assert.False(message.IsRead));
            Assert.Contains(server.Stores, command => command.Contains("+FLAGS.SILENT (\\Seen)"));
            Assert.Contains(server.Stores, command => command.Contains("-FLAGS.SILENT (\\Seen)"));
        }

    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "mailkeep-read-state-" + Guid.NewGuid().ToString("N"));
        public MailkeepServices Services { get; }
        public Fixture() { Services = new(_root); Services.Database.Initialize(); }
        public void Add(string account, string key, long? uid) => Services.Database.Upsert(new MessageInput(MailContract.LiveSource,
            account, LocalFolderKinds.Inbox, key, uid, uid is null ? 0u : 42u, "Message " + key, "sender@example.invalid", "Sender",
            ["recipient@example.invalid"], DateTimeOffset.UtcNow, 10, false, 0, "Body", null, null));
        public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
    }

}
