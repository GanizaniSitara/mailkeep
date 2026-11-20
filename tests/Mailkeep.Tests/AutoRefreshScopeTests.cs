using Mailkeep.Core;
using Mailkeep.Infrastructure;

namespace Mailkeep.Tests;

/// <summary>Local Mail is POP mail the other client is still collecting; an unattended pass must leave it
/// alone, so that only Send/Receive or a named account on the command line goes near it.</summary>
public sealed class AutoRefreshScopeTests
{
    [Fact]
    public async Task ADueSweepCollectsEveryAccountOnItsOwnInterval()
    {
        using var temp = new TempDir();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        services.Accounts.Save([Account("connected", "one@example.invalid", MailboxModes.Connected, MailProtocols.Imap),
            Account("local", "two@example.invalid", MailboxModes.LocalKeep, MailProtocols.Pop3),
            Account("parked", "three@example.invalid", MailboxModes.LocalKeep, MailProtocols.Pop3)
                with { KeepAlive = new KeepAliveSettings(false) }]);

        var now = DateTimeOffset.UtcNow;
        var accounts = services.Accounts.Load();
        Assert.True(services.Sync.IsDue(accounts.Single(a => a.Name == "connected"), now));
        // Local Mail is on the timer like any other account now that nothing else collects the mailbox.
        Assert.True(services.Sync.IsDue(accounts.Single(a => a.Name == "local"), now));
        Assert.False(services.Sync.IsDue(accounts.Single(a => a.Name == "parked"), now));

        // Port 1 refuses immediately, so the accounts fail fast and no network is touched.
        var due = await services.Sync.SyncAllAsync(true);
        Assert.Equal(["connected", "local"], due.Select(result => result.Account).Order());

        var all = await services.Sync.SyncAllAsync(false);
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public void LocalMailIsDueEveryQuarterHourWhateverItsStoredHours()
    {
        using var temp = new TempDir();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        services.Accounts.Save([Account("connected", "one@example.invalid", MailboxModes.Connected, MailProtocols.Imap),
            Account("local", "two@example.invalid", MailboxModes.LocalKeep, MailProtocols.Pop3)
                with { KeepAlive = new KeepAliveSettings(true, 6) }]);
        var fetched = DateTimeOffset.UtcNow;
        services.Database.SetSyncState("connected", "INBOX", 1, 1, fetched);
        services.Database.SetSyncState("local", "INBOX", 1, 1, fetched);
        var accounts = services.Accounts.Load();
        var local = accounts.Single(a => a.Name == "local");
        var connected = accounts.Single(a => a.Name == "connected");

        Assert.False(services.Sync.IsDue(local, fetched.AddMinutes(10)));
        // The next quarter-hour tick, even though the fetch was stamped a little after the tick before it.
        Assert.True(services.Sync.IsDue(local, fetched.AddMinutes(14)));
        Assert.False(services.Sync.IsDue(connected, fetched.AddMinutes(15)));
    }

    private static MailAccount Account(string name, string address, string mode, string protocol) =>
        new(name, "custom", address, "INBOX", "Mail", "127.0.0.1", null, "unused-password",
            new KeepAliveSettings(), mode, new IncomingServerSettings(protocol, "127.0.0.1", 1));

    [Fact]
    public async Task ManualLocalAndConnectedPassesNeverCheckTheOtherMode()
    {
        using var temp = new TempDir();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        services.Accounts.Save([Account("connected", "one@example.invalid", MailboxModes.Connected, MailProtocols.Imap),
            Account("local", "two@example.invalid", MailboxModes.LocalKeep, MailProtocols.Pop3)
                with { KeepAlive = new KeepAliveSettings(false) }]);
        var local = await services.Sync.SyncAllAsync(false, connectedAccounts: false);
        Assert.Equal("local", Assert.Single(local).Account);
        Assert.DoesNotContain(services.Database.RecentFetches(10), fetch => fetch.Account == "connected");
        var connected = await services.Sync.SyncAllAsync(false, connectedAccounts: true);
        Assert.Equal("connected", Assert.Single(connected).Account);
        services.Accounts.Save([]);
        Assert.Empty(await services.Sync.SyncAllAsync(false, connectedAccounts: false));
        Assert.Empty(await services.Sync.SyncAllAsync(false, connectedAccounts: true));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mailkeep-refresh-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
