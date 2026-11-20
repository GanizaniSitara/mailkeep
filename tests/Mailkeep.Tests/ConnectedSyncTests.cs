using System.Text;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Mailkeep.TestFixtures;

namespace Mailkeep.Tests;

public sealed class ConnectedSyncTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectedFirstRefreshLoadsExistingInboxInsteadOfBaseliningItAway(bool oldBaseline)
    {
        await using var server = new ReceiveFixture(false);
        using var fixture = new Store(server.Port);
        if (oldBaseline)
        {
            fixture.Services.Database.SetSetting("imap_baseline:connected", "old-client-baseline");
            fixture.Services.Database.SetSyncState("connected", "INBOX", 1, 2, DateTimeOffset.UtcNow);
        }
        var result = await fixture.Services.Sync.SyncAsync("connected");
        Assert.True(result.Ok, result.Error);
        Assert.Equal(2, result.NewCount);
        Assert.Equal(2, fixture.Services.Database.ListMessages(account: "connected").Count);
    }

    [Fact]
    public async Task ExternalMoveAndReadChangeConvergeWithoutChangingLocalMail()
    {
        await using var server = new ReceiveFixture(false);
        using var fixture = new Store(server.Port);
        var services = fixture.Services;
        services.Database.SetSetting("imap_baseline:connected", "old-client-baseline");
        Assert.True((await services.Sync.SyncAsync("connected")).Ok);
        var raw = Encoding.ASCII.GetBytes("From: fixture@example.invalid\r\nSubject: Independent Local Mail\r\n\r\nRetain these bytes.\r\n");
        var path = services.Blobs.Put(raw);
        var copy = services.Database.Upsert(new("live", MailContract.LocalCopyAccount, "inbox", "copy:connected-test",
            null, 0, "Independent Local Mail", "fixture@example.invalid", "Fixture", [], DateTimeOffset.UnixEpoch,
            raw.Length, false, 0, "Retain these bytes.", null, path));
        server.Uids = [2, 3];
        server.SeenUids.Add(2);
        var result = await services.Sync.SyncAsync("connected");
        Assert.True(result.Ok, result.Error);
        var connected = services.Database.ListMessages(account: "connected").OrderBy(message => message.ServerUid).ToArray();
        Assert.Equal(new long?[] { 2, 3 }, connected.Select(message => message.ServerUid));
        Assert.True(connected[0].IsRead);
        Assert.False(services.Database.GetMessage(copy)!.Summary.IsRead);
        Assert.Equal(raw, services.Blobs.ReadBytes(path));
        Assert.All(server.UidCommands, command => Assert.Contains(command, new[] { "SEARCH", "FETCH" }));
    }

    [Theory]
    [InlineData("search")]
    [InlineData("flags")]
    [InlineData("download")]
    public async Task IncompleteRefreshDoesNotRetireCachedMessages(string failure)
    {
        await using var server = new ReceiveFixture(false);
        using var fixture = new Store(server.Port);
        Assert.True((await fixture.Services.Sync.SyncAsync("connected")).Ok);
        server.Uids = [2, 3];
        server.RejectSearch = failure == "search";
        server.RejectFlags = failure == "flags";
        server.RejectDownload = failure == "download";
        var result = await fixture.Services.Sync.SyncAsync("connected");
        Assert.False(result.Ok);
        Assert.Equal(new long?[] { 1, 2 }, fixture.Services.Database.ListMessages(account: "connected").OrderBy(message => message.ServerUid).Select(message => message.ServerUid));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingOrRecentlyCompletedReadIntentWinsOverAnOlderServerSnapshot(bool completed)
    {
        await using var server = new ReceiveFixture(false);
        using var fixture = new Store(server.Port);
        var services = fixture.Services;
        Assert.True((await services.Sync.SyncAsync("connected")).Ok);
        var message = services.Database.ListMessages(account: "connected").Single(message => message.ServerUid == 1);
        server.SeenUids.Add(1);
        server.HoldFlags = true;
        server.FlagsStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = services.Sync.SyncAsync("connected");
        await server.FlagsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            services.Database.QueueRemote(fixture.Account, [message], "read", isRead: true);
            services.Database.QueueRemote(fixture.Account, [message], "read", isRead: false);
            if (completed)
                foreach (var operation in services.Database.PendingRemoteOperations()) services.Database.FinishRemoteOperation(operation.Id, null);
        }
        finally { server.ReleaseFlags.TrySetResult(); }
        Assert.True((await work).Ok);
        Assert.False(services.Database.GetMessage(message.Id)!.Summary.IsRead);
    }

    [Fact]
    public async Task FailedLaterDownloadReportsCommittedMailWithoutRetiringOldCache()
    {
        await using var server = new ReceiveFixture(false);
        using var fixture = new Store(server.Port);
        Assert.True((await fixture.Services.Sync.SyncAsync("connected")).Ok);
        server.Uids = [2, 3, 4];
        server.RejectDownloadUid = 4;
        var result = await fixture.Services.Sync.SyncAsync("connected");
        Assert.False(result.Ok);
        Assert.Equal(1, result.NewCount);
        Assert.Equal(new long?[] { 1, 2, 3 }, fixture.Services.Database.ListMessages(account: "connected")
            .OrderBy(message => message.ServerUid).Select(message => message.ServerUid));
    }

    [Fact]
    public async Task PendingDeletionIsNotDownloadedAgainWhileServerStillContainsIt()
    {
        await using var server = new ReceiveFixture(false);
        using var fixture = new Store(server.Port);
        var database = fixture.Services.Database;
        Assert.True((await fixture.Services.Sync.SyncAsync("connected")).Ok);
        var message = database.ListMessages(account: "connected").Single(message => message.ServerUid == 1);
        database.QueueRemote(fixture.Account, [message], "delete");
        var downloads = server.Downloads;
        Assert.True((await fixture.Services.Sync.SyncAsync("connected")).Ok);
        Assert.Equal(downloads, server.Downloads);
        Assert.Equal(2, Assert.Single(database.ListMessages(account: "connected")).ServerUid);
        Assert.Single(database.PendingRemoteOperations());
    }

    [Fact]
    public async Task EmptyServerInboxRetiresOnlyTheSelectedAccountsCache()
    {
        await using var server = new ReceiveFixture(false);
        using var fixture = new Store(server.Port);
        var other = fixture.Account with { Name = "other", Address = "other@example.invalid" };
        fixture.Services.Accounts.Save([fixture.Account, other]);
        Assert.True((await fixture.Services.Sync.SyncAsync("connected")).Ok);
        Assert.True((await fixture.Services.Sync.SyncAsync("other")).Ok);
        server.Uids = [];
        Assert.True((await fixture.Services.Sync.SyncAsync("connected")).Ok);
        Assert.Empty(fixture.Services.Database.ListMessages(account: "connected"));
        Assert.Equal(2, fixture.Services.Database.ListMessages(account: "other").Count);
        Assert.True((await fixture.Services.Sync.SyncAsync("connected")).Ok);
        Assert.Empty(fixture.Services.Database.ListMessages(account: "connected"));
    }

    [Fact]
    public async Task UidValidityResetReplacesOnlyConnectedCacheOccurrences()
    {
        await using var server = new ReceiveFixture(false);
        using var fixture = new Store(server.Port);
        Assert.True((await fixture.Services.Sync.SyncAsync("connected")).Ok);
        var old = fixture.Services.Database.ListMessages(account: "connected").Select(message => message.Id).ToArray();
        server.UidValidity = 2;
        server.Uids = [1];
        Assert.True((await fixture.Services.Sync.SyncAsync("connected")).Ok);
        Assert.Equal("2:1", Assert.Single(fixture.Services.Database.ListMessages(account: "connected")).SourceKey);
        Assert.All(old, id => Assert.Null(fixture.Services.Database.GetMessage(id)));
    }

    [Fact]
    public async Task LocalDownloadsSurviveServerMovesAndKeepTheirOwnReadState()
    {
        await using var server = new ReceiveFixture(false);
        using var fixture = new Store(server.Port);
        fixture.Services.Accounts.Save([fixture.Account with { MailboxMode = MailboxModes.LocalKeep }]);
        fixture.Services.Database.SetSetting("imap_baseline:connected", "local-collection-baseline");
        Assert.True((await fixture.Services.Sync.SyncAsync("connected")).Ok);
        server.Uids = [2]; server.SeenUids.Add(2);
        Assert.True((await fixture.Services.Sync.SyncAsync("connected")).Ok);
        var kept = fixture.Services.Database.ListMessages(account: "connected");
        Assert.Equal(2, kept.Count);
        Assert.All(kept, message => Assert.False(message.IsRead));
    }

    private sealed class Store : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "mailkeep-connected-sync-" + Guid.NewGuid().ToString("N"));
        public MailkeepServices Services { get; }
        public MailAccount Account { get; }
        public Store(int port)
        {
            Services = new(_root);
            Services.Database.Initialize();
            Account = new("connected", "custom", "fixture@example.invalid", "INBOX", "Connected Inbox", "127.0.0.1", null,
                "fixture-password", new KeepAliveSettings(false), MailboxModes.Connected,
                new IncomingServerSettings(MailProtocols.Imap, "127.0.0.1", port, MailSecurityModes.None));
            Services.Accounts.Save([Account]);
        }
        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }
    }
}
