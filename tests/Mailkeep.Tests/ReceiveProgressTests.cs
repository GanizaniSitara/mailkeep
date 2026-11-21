using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Mailkeep.TestFixtures;

namespace Mailkeep.Tests;

public sealed class ReceiveProgressTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SilentGreetingHasBoundedTimeoutAndDoesNotBlockHealthyAccount(bool pop)
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        using var release = new CancellationTokenSource();
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(release.Token);
            await Task.Delay(Timeout.Infinite, release.Token);
        });
        await using var healthy = new ReceiveFixture(pop);
        var root = Path.Combine(Path.GetTempPath(), "mailkeep-timeout-" + Guid.NewGuid().ToString("N"));
        try
        {
            var services = new MailkeepServices(root); services.Database.Initialize();
            var silent = Account(pop, ((System.Net.IPEndPoint)listener.LocalEndpoint).Port);
            var good = Account(pop, healthy.Port) with { Name = "healthy", Address = "healthy@example.invalid" };
            services.Accounts.Save([silent, good]);
            var progress = new Capture(); var clock = System.Diagnostics.Stopwatch.StartNew();
            var work = services.Sync.SyncAllAsync(false, receiveProgress: progress);
            while (!progress.Ticks.Any(tick => tick.Result is { Account: "healthy", NewCount: 2 }))
            {
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "healthy account waited for the silent server");
                await Task.Delay(20);
            }
            Assert.False(work.IsCompleted);
            var results = await work.WaitAsync(TimeSpan.FromSeconds(35));
            var failed = Assert.Single(results, result => result.Account == silent.Name);
            Assert.False(failed.Ok);
            Assert.True(failed.Error?.Contains("timed out", StringComparison.OrdinalIgnoreCase) == true
                || failed.Error?.Contains("30 seconds") == true, failed.Error);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(40));
            Assert.Equal(2, services.Database.ListMessages().Count);
        }
        finally
        {
            release.Cancel(); listener.Stop();
            try { await peer; } catch (OperationCanceledException) { }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FirstContactDownloadsExistingMailOnceAndReportsOnlyDurablyStoredMessages(bool pop)
    {
        await using var server = new ReceiveFixture(pop) { HoldDownload = true };
        var root = Path.Combine(Path.GetTempPath(), "mailkeep-receive-" + Guid.NewGuid().ToString("N"));
        try
        {
            var services = new MailkeepServices(root); services.Database.Initialize();
            var account = Account(pop, server.Port);
            services.Accounts.Save([account]);
            var progress = new Capture();
            var work = services.Sync.SyncAsync(account.Name, receiveProgress: progress);
            await server.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains(progress.Ticks, tick => tick.Stage == "Downloading" && tick.Downloaded == 0 && tick.Total == 2);
            Assert.DoesNotContain(progress.Ticks, tick => tick.Downloaded > 0 || tick.Result is not null);
            server.ReleaseDownload.TrySetResult();
            var result = await work.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(result.Ok, result.Error); Assert.Equal(2, result.NewCount);
            Assert.Equal([0, 1, 2], progress.Ticks.Where(tick => tick.Stage == "Downloading").Select(tick => tick.Downloaded));
            Assert.Equal(result, progress.Ticks.Last().Result);
            var again = await services.Sync.SyncAsync(account.Name, receiveProgress: progress);
            Assert.True(again.Ok, again.Error); Assert.Equal(0, again.NewCount); Assert.Equal(2, server.Downloads);
            Assert.DoesNotContain("DELE", server.Commands); Assert.DoesNotContain("STORE", server.Commands);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedLoginFinishesWithErrorAndNoDownload(bool pop)
    {
        await using var server = new ReceiveFixture(pop) { RejectLogin = true };
        var root = Path.Combine(Path.GetTempPath(), "mailkeep-receive-" + Guid.NewGuid().ToString("N"));
        try
        {
            var services = new MailkeepServices(root); services.Database.Initialize(); services.Accounts.Save([Account(pop, server.Port)]);
            var progress = new Capture();
            var result = await services.Sync.SyncAsync("receive", receiveProgress: progress).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(result.Ok); Assert.Equal(0, server.Downloads);
            Assert.Equal("Failed", progress.Ticks.Last().Stage); Assert.Equal(result, progress.Ticks.Last().Result);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExplicitBaselineSkipsOnlyExistingMail(bool pop)
    {
        await using var server = new ReceiveFixture(pop);
        var root = Path.Combine(Path.GetTempPath(), "mailkeep-receive-" + Guid.NewGuid().ToString("N"));
        try
        {
            var services = new MailkeepServices(root); services.Database.Initialize(); services.Accounts.Save([Account(pop, server.Port)]);
            var baseline = await services.Sync.BaselineAsync("receive");
            Assert.True(baseline.Ok, baseline.Error);
            Assert.Equal(2, baseline.Scanned);
            Assert.Empty(services.Database.ListMessages());
            Assert.Equal(0, server.Downloads);
            server.Uids = [1, 2, 3];
            var result = await services.Sync.SyncAsync("receive");
            Assert.True(result.Ok, result.Error);
            Assert.Equal(1, result.NewCount);
            Assert.Equal("Download fixture 3", Assert.Single(services.Database.ListMessages()).Subject);
            Assert.Equal(1, server.Downloads);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task RenumberingAfterInterruptedFirstContactReconcilesThePartialDownload()
    {
        await using var server = new ReceiveFixture(false) { RejectDownloadUid = 2 };
        var root = Directory.CreateTempSubdirectory("mailkeep-first-retry-");
        try
        {
            var services = new MailkeepServices(root.FullName);
            services.Database.Initialize();
            services.Accounts.Save([Account(false, server.Port)]);
            Assert.False((await services.Sync.SyncAsync("receive")).Ok);
            var original = Assert.Single(services.Database.ListMessages());
            server.UidValidity = 2;
            server.Uids = [12];
            server.ImapMime[12] = ReceiveFixture.Message(1);
            var result = await services.Sync.SyncAsync("receive");
            Assert.True(result.Ok, result.Error);
            Assert.Equal(0, result.NewCount);
            var recovered = Assert.Single(services.Database.ListMessages());
            Assert.Equal(original.Id, recovered.Id);
            Assert.Equal("2:12", recovered.SourceKey);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            root.Delete(true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenumberedInboxRetainsLocalMailAndReattachesOnlyIdenticalMime(bool portable)
    {
        await using var server = new ReceiveFixture(false);
        var root = Directory.CreateTempSubdirectory("mailkeep-renumber-");
        try
        {
            var services = new MailkeepServices(root.FullName);
            var db = services.Database;
            db.Initialize();
            var account = Account(false, server.Port);
            services.Accounts.Save([account]);
            Assert.Equal(2, (await services.Sync.SyncAsync(account.Name)).NewCount);
            var before = db.ListMessages().ToDictionary(m => m.ServerUid!.Value);
            await services.Sync.SetReadManyAsync(before.Values.ToArray(), true);
            using var raw = new MemoryStream(System.Text.Encoding.ASCII.GetBytes(ReceiveFixture.Message(4)));
            using var local = MimeKit.MimeMessage.Load(raw);
            raw.Position = 0;
            var localId = await db.UpsertMimeMessageAsync(MailContract.LiveSource, account.Name, account.FolderLabel,
                "local-copy", null, 0, local, raw, true);
            var migration = new BulkFolderMigration(services.Paths);
            if (portable) migration.Run(migration.Preview().Confirmation, publish: true);

            server.UidValidity = 2;
            server.Uids = [7, 8, 9, 10];
            server.ImapMime[7] = ReceiveFixture.Message(2);
            server.ImapMime[8] = ReceiveFixture.Message(2).Replace("Invented receive body", "Different body");
            server.ImapMime[9] = ReceiveFixture.Message(2); // Another UID for the same original bytes.
            server.ImapMime[10] = ReceiveFixture.Message(4);
            server.RejectDownloadUid = 8;
            Assert.False((await services.Sync.SyncAsync(account.Name)).Ok);
            server.RejectDownloadUid = null;
            var result = await services.Sync.SyncAsync(account.Name);
            Assert.True(result.Ok, result.Error);
            Assert.Equal(1, result.NewCount);
            if (portable)
            {
                File.Delete(db.GetMessage(before[1].Id)!.RawPath!);
                Assert.Equal(4, migration.RebuildProjection());
            }
            var after = db.ListMessages();
            Assert.Equal(4, after.Count);
            Assert.Equal("local-copy", db.GetMessage(localId)!.Summary.SourceKey);
            var retained = db.GetMessage(before[1].Id)!;
            Assert.Null(retained.Summary.ServerUid);
            Assert.True(retained.Summary.IsRead);
            Assert.Contains("Invented receive body 1", retained.BodyText);
            var rebound = db.GetMessage(before[2].Id)!.Summary;
            Assert.Equal("2:7", rebound.SourceKey);
            Assert.Equal(7, rebound.ServerUid);
            Assert.True(rebound.IsRead);
            Assert.Contains(after, row => row.ServerUid == 8 && !row.IsRead);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            root.Delete(true);
        }
    }

    [Fact]
    public async Task ImapFlagsAreBatchedAndCommittedCheckpointsBoundInterruptedReplay()
    {
        await using var server = new ReceiveFixture(false)
        {
            Uids = Enumerable.Range(1, 30).Select(n => (uint)n).ToArray(), RejectDownloadUid = 27
        };
        server.SeenUids.Add(2);
        var root = Directory.CreateTempSubdirectory("mailkeep-imap-checkpoint-");
        try
        {
            var services = new MailkeepServices(root.FullName); services.Database.Initialize();
            services.Accounts.Save([Account(false, server.Port)]);
            Assert.False((await services.Sync.SyncAsync("receive")).Ok);
            Assert.Equal(1, server.FlagsFetches);
            Assert.Equal(25u, services.Database.GetSyncState("receive", "INBOX")!.MaxUid);
            Assert.Equal(26, services.Database.ListMessages().Count);
            Assert.True(services.Database.ListMessages().Single(message => message.ServerUid == 2).IsRead);
            server.RejectDownloadUid = null;
            var before = server.Downloads;
            Assert.True((await services.Sync.SyncAsync("receive")).Ok);
            Assert.Equal(5, server.Downloads - before); // Only the bounded suffix is replayed.
            Assert.Equal(2, server.FlagsFetches);
            Assert.Equal(30u, services.Database.GetSyncState("receive", "INBOX")!.MaxUid);
            Assert.Equal(30, services.Database.ListMessages().Count);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); root.Delete(true); }
    }

    private static MailAccount Account(bool pop, int port) => new("receive", "custom", "fixture@example.invalid", "INBOX", "Inbox", "127.0.0.1", null,
        "fixture-password", new KeepAliveSettings(), MailboxModes.LocalKeep,
        new IncomingServerSettings(pop ? MailProtocols.Pop3 : MailProtocols.Imap, "127.0.0.1", port, MailSecurityModes.None));
    private sealed class Capture : IProgress<ReceiveProgress>
    {
        private readonly List<ReceiveProgress> _ticks = [];
        public ReceiveProgress[] Ticks { get { lock (_ticks) return _ticks.ToArray(); } }
        public void Report(ReceiveProgress tick) { lock (_ticks) _ticks.Add(tick); }
    }
}
