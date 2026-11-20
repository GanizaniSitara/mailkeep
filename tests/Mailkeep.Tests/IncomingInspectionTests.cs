using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Mailkeep.TestFixtures;

namespace Mailkeep.Tests;

public sealed class IncomingInspectionTests
{
    [Fact]
    public async Task ImapInspectionCountsWithoutReadingOrChangingMailOrBaseline()
    {
        await using var server = new ImapFixture(false, 1);
        var root = Path.Combine(Path.GetTempPath(), "mailkeep-inspection-" + Guid.NewGuid().ToString("N"));
        try
        {
            var services = new MailkeepServices(root); services.Database.Initialize();
            var account = new MailAccount("fixture", "custom", "fixture@example.invalid", "INBOX", "inbox", null, null,
                "fake-password", new KeepAliveSettings(), MailboxModes.LocalKeep,
                new IncomingServerSettings(MailProtocols.Imap, "127.0.0.1", server.Port, MailSecurityModes.None));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Assert.Equal(2, await services.Sync.InspectImapAsync(account, timeout.Token));
            Assert.Contains("STATUS", server.Commands);
            Assert.All(server.Commands, command => Assert.Contains(command, new[] { "CAPABILITY", "AUTHENTICATE", "LOGIN", "LIST", "STATUS", "LOGOUT" }));
            Assert.Null(services.Database.GetSyncState(account.Name, account.Mailbox));
            Assert.False(services.Database.HasSetting("imap_baseline:fixture"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
