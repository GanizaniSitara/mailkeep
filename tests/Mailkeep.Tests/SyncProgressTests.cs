using Mailkeep.Core;
using Mailkeep.Infrastructure;

namespace Mailkeep.Tests;

public sealed class SyncProgressTests
{
    [Fact]
    public async Task SyncAllReportsAStartAndACompletionForEveryAccount()
    {
        using var temp = new TempDir();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        services.Accounts.Save([Account("alpha", "one@example.invalid"),
            Account("beta", "two@example.invalid"), Account("gamma", "three@example.invalid")]);

        var ticks = new List<SyncProgress>();
        var results = await services.Sync.SyncAllAsync(false, progress: new ImmediateProgress(ticks));

        Assert.Equal(3, results.Count);
        // Unreachable hosts: the accounts all fail, and a failed account must still count as done.
        Assert.All(results, result => Assert.False(result.Ok));
        lock (ticks)
        {
            Assert.Equal(3, ticks.Count(tick => tick.Started));
            Assert.Equal(3, ticks.Count(tick => !tick.Started));
            Assert.Equal(3, ticks.Where(tick => !tick.Started).Max(tick => tick.Done));
            Assert.All(ticks, tick => Assert.Equal(3, tick.Total));
            Assert.Contains(ticks, tick => tick.Address == "two@example.invalid");
        }
    }

    private static MailAccount Account(string name, string address) => new(name, "custom", address,
        "INBOX", "Connected", "127.0.0.1", null, "unused-password", new KeepAliveSettings(),
        // Port 1 refuses immediately, so the failure path is fast and no network is touched.
        MailboxModes.Connected, new IncomingServerSettings(MailProtocols.Imap, "127.0.0.1", 1));

    /// <summary>Progress&lt;T&gt; posts through a sync context the test does not have; this reports inline.</summary>
    private sealed class ImmediateProgress(List<SyncProgress> ticks) : IProgress<SyncProgress>
    {
        public void Report(SyncProgress value) { lock (ticks) ticks.Add(value); }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mailkeep-sync-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
