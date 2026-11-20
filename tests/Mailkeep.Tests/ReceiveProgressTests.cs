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
            services.Database.SetSetting((pop ? "pop_baseline:" : "imap_baseline:") + good.Name, "fixture");
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
    public async Task ReportsOnlyDurablyStoredMessagesAndDoesNotDownloadAgain(bool pop)
    {
        await using var server = new ReceiveFixture(pop) { HoldDownload = true };
        var root = Path.Combine(Path.GetTempPath(), "mailkeep-receive-" + Guid.NewGuid().ToString("N"));
        try
        {
            var services = new MailkeepServices(root); services.Database.Initialize();
            var account = Account(pop, server.Port);
            services.Accounts.Save([account]);
            services.Database.SetSetting((pop ? "pop_baseline:" : "imap_baseline:") + account.Name, "fixture");
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
    public async Task FirstContactReportsBaselineRatherThanPretendingToDownload(bool pop)
    {
        await using var server = new ReceiveFixture(pop);
        var root = Path.Combine(Path.GetTempPath(), "mailkeep-receive-" + Guid.NewGuid().ToString("N"));
        try
        {
            var services = new MailkeepServices(root); services.Database.Initialize(); services.Accounts.Save([Account(pop, server.Port)]);
            var progress = new Capture();
            var result = await services.Sync.SyncAsync("receive", receiveProgress: progress).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(result.Ok, result.Error); Assert.Equal(0, result.NewCount); Assert.Equal(0, server.Downloads);
            Assert.Contains(progress.Ticks, tick => tick.Stage.StartsWith("Recording existing"));
            Assert.DoesNotContain(progress.Ticks, tick => tick.Stage == "Downloading");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(root, true); } catch (IOException) { } }
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
