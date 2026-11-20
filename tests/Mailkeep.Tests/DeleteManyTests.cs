using Mailkeep.Core;
using Mailkeep.Infrastructure;

namespace Mailkeep.Tests;

public sealed class DeleteManyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SentCopiesDeleteLocallyWithoutAReceivingAccountOrDespiteAConnectedNameCollision(bool connectedCollision)
    {
        using var temp = new TempDir();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        if (connectedCollision)
            services.Accounts.Save([new MailAccount("sender", "custom", "user@example.invalid", "INBOX", "Connected",
                "imap.example.invalid", null, "secret", new KeepAliveSettings(), MailboxModes.Connected)]);
        services.Database.Upsert(new MessageInput(MailContract.LiveSource, "sender", LocalFolderKinds.Sent,
            "sent:fixture", null, 0, "Sent copy", "user@example.invalid", "Sender", ["recipient@example.invalid"],
            DateTimeOffset.UtcNow, 128, true, 0, "Body", null, null));
        var sent = Assert.Single(services.Database.ListMessages());
        var ticks = new List<DeleteProgress>();

        var outcome = await services.Sync.DeleteManyAsync([sent], new ImmediateProgress(ticks));

        Assert.Equal(1, outcome.Deleted);
        Assert.Equal(0, outcome.Refused);
        var deleted = Assert.Single(services.Database.ListMessages());
        Assert.Equal(LocalFolderKinds.Deleted, deleted.Folder);
        Assert.Equal(sent.Id, deleted.Id);
        Assert.Equal(1, Assert.Single(ticks).Done);
        await services.Sync.DeleteManyAsync([deleted]);
        Assert.Empty(services.Database.ListMessages());
    }

    /// <summary>The wait a deletion of any size used to be: no count, no end in sight. Every message is
    /// accounted for as it goes, and the last tick agrees with what was asked for.</summary>
    [Fact]
    public async Task DeletingASelectionReportsEveryMessageAsItGoes()
    {
        using var temp = new TempDir();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        services.Accounts.Save([
            new MailAccount("local-fixture", "custom", "user@example.invalid", "INBOX", LocalFolderKinds.Inbox,
                "pop.example.invalid", null, "secret", new KeepAliveSettings(), MailboxModes.LocalKeep,
                new IncomingServerSettings(MailProtocols.Pop3, "pop.example.invalid", 995))
        ]);
        for (var index = 0; index < 25; index++)
            services.Database.Upsert(new MessageInput(MailContract.LiveSource, "local-fixture", LocalFolderKinds.Inbox,
                $"key-{index}", null, 0, $"Message {index}", "sender@example.invalid", "Sender",
                ["user@example.invalid"], DateTimeOffset.UtcNow.AddMinutes(-index), 128, false, 0, "Body", null, null));
        var chosen = services.Database.ListMessages().Where(message => message.Folder == LocalFolderKinds.Inbox).ToArray();
        Assert.Equal(25, chosen.Length);

        var ticks = new List<DeleteProgress>();
        var deleted = await services.Sync.DeleteManyAsync(chosen, new ImmediateProgress(ticks));

        Assert.Equal(25, deleted.Deleted);
        Assert.Equal(0, deleted.Refused);
        Assert.Equal(25, ticks.Count);
        Assert.Equal(Enumerable.Range(1, 25), ticks.Select(tick => tick.Done));
        Assert.All(ticks, tick => Assert.Equal(25, tick.Total));
        Assert.All(ticks, tick => Assert.Equal("user@example.invalid", tick.Account));
        Assert.Empty(services.Database.ListMessages().Where(message => message.Folder == LocalFolderKinds.Inbox));
    }

    /// <summary>An archived message has no provider copy to move and no business being deleted, and finding
    /// that out half way through a selection would leave the rest of it already gone.</summary>
    [Fact]
    public async Task ArchivedMailIsRefusedBeforeAnythingIsDeleted()
    {
        using var temp = new TempDir();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        services.Accounts.Save([
            new MailAccount("local-fixture", "custom", "user@example.invalid", "INBOX", LocalFolderKinds.Inbox,
                "pop.example.invalid", null, "secret", new KeepAliveSettings(), MailboxModes.LocalKeep,
                new IncomingServerSettings(MailProtocols.Pop3, "pop.example.invalid", 995))
        ]);
        services.Database.Upsert(new MessageInput(MailContract.LiveSource, "local-fixture", LocalFolderKinds.Inbox,
            "live-1", null, 0, "Live", "sender@example.invalid", "Sender", ["user@example.invalid"],
            DateTimeOffset.UtcNow, 128, false, 0, "Body", null, null));
        services.Database.Upsert(new MessageInput(MailContract.ArchiveSource, "archive-fixture", "Old mail",
            "archived-1", null, 0, "Archived", "sender@example.invalid", "Sender", ["user@example.invalid"],
            DateTimeOffset.UtcNow, 128, true, 0, "Body", null, null));
        var everything = services.Database.ListMessages();

        await Assert.ThrowsAsync<InvalidOperationException>(() => services.Sync.DeleteManyAsync(everything));

        Assert.Equal(everything.Count, services.Database.ListMessages().Count);
    }

    private sealed class ImmediateProgress(List<DeleteProgress> ticks) : IProgress<DeleteProgress>
    {
        public void Report(DeleteProgress value) => ticks.Add(value);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mailkeep-delete-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
