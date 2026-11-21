using System.Text;
using Mailkeep.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Tests;

public sealed class StorageConcurrencyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mailkeep-storage-lock-" + Guid.NewGuid().ToString("N"));
    private readonly MailkeepServices services;
    private readonly byte[] raw = Encoding.UTF8.GetBytes("From: test@example.invalid\r\nSubject: Lock fixture\r\n\r\nKeep these bytes\r\n");

    public StorageConcurrencyTests()
    {
        services = new(root);
        services.Database.Initialize();
        for (var i = 1; i <= 2; i++)
            services.Database.Upsert(new("live", "local", "inbox", "lock:" + i, null, 0,
                "Lock fixture", "test@example.invalid", "Test", [], DateTimeOffset.UnixEpoch,
                raw.Length, false, 0, "Keep these bytes", null, services.Blobs.Put(raw)));
        var migration = new BulkFolderMigration(services.Paths);
        migration.Run(migration.Preview().Confirmation, publish: true);
    }

    [Fact]
    public async Task EmptyJournalFlushDoesNotTakeHeldOperationLease()
    {
        services.Database.FolderStorage.Flush(); // Complete the one-time hierarchy-format upgrade first.
        using var lease = new FileStream(Path.Combine(root, "bound-folders", ".operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var work = Task.Run(() =>
        {
            Assert.Equal(0, services.Database.FolderStorage.Flush());
            services.Database.SetSetting("empty-journal-test", "committed");
        });
        try { await work.WaitAsync(TimeSpan.FromSeconds(2)); }
        finally { lease.Dispose(); await work.WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.Equal("committed", services.Database.GetSetting("empty-journal-test"));
    }

    [Fact]
    public async Task DelayedFolderWriterDoesNotBlockProjectionReadsAndQueuedMoveCompletes()
    {
        var uuid = (string)Sql("SELECT uuid FROM bulk_bindings WHERE folder='inbox'")!;
        using var blocker = new SqliteConnection($"Data Source={Path.Combine(root, "bound-folders", uuid, FolderBundle.FileName)};Pooling=False");
        blocker.Open();
        using var transaction = blocker.BeginTransaction();
        Sql("UPDATE messages SET is_read=1 WHERE id=1");
        var writer = Task.Run(() => services.Database.FolderStorage.Flush());
        Task<int>? move = null;
        try
        {
            await WaitForWriter();
            var read = Task.Run(() =>
            {
                Assert.NotNull(services.Database.GetMessage(2));
                Assert.Equal(2, services.Database.ListMessages().Count);
                Assert.NotEmpty(services.Database.ListLocalFolders());
                Assert.NotEmpty(services.Database.FolderCounts("live"));
                Assert.NotEmpty(services.Database.ReadStates([1, 2]));
            });
            await read.WaitAsync(TimeSpan.FromSeconds(2));
            move = Task.Run(() => services.Database.MoveMessages([2], "deleted"));
            // Beyond the old lease timeout, within the folder writer's SQLite timeout.
            await Task.Delay(TimeSpan.FromSeconds(6));
            Assert.False(move.IsCompleted);
        }
        finally
        {
            transaction.Rollback();
            await writer.WaitAsync(TimeSpan.FromSeconds(10));
            if (move is not null) Assert.Equal(1, await move.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        Assert.Equal("deleted", services.Database.GetMessage(2)!.Summary.Folder);
        Assert.Equal(0L, Sql("SELECT count(*) FROM bulk_journal"));
        Assert.Equal(2, new BulkFolderMigration(services.Paths).RebuildProjection());
        Assert.All(services.Database.ListMessages(), m => Assert.Equal(raw, File.ReadAllBytes(services.Database.GetMessage(m.Id)!.RawPath!)));
    }

    [Fact]
    public void FailedPostCommitFlushReportsRecoveryAndReadsShowCommittedLocation()
    {
        Sql("CREATE TRIGGER fail_ack BEFORE DELETE ON bulk_journal BEGIN SELECT RAISE(ABORT,'fixture'); END");
        var error = Assert.Throws<FolderStorageRecoveryException>(() => services.Database.MoveMessages([1], "deleted"));
        Assert.Contains("do not repeat", error.Message);
        Assert.Equal("deleted", services.Database.GetMessage(1)!.Summary.Folder);
        Assert.Equal(2L, Sql("SELECT count(*) FROM messages"));
        Assert.True((long)Sql("SELECT count(*) FROM bulk_journal")! > 0);
        Sql("DROP TRIGGER fail_ack");
        var reopened = new MailkeepServices(root);
        reopened.Database.Initialize();
        Assert.Equal(0L, Sql("SELECT count(*) FROM bulk_journal"));
        Assert.Equal(2, new BulkFolderMigration(services.Paths).RebuildProjection());
        Assert.Equal("deleted", reopened.Database.GetMessage(1)!.Summary.Folder);
        Assert.Equal(raw, File.ReadAllBytes(reopened.Database.GetMessage(1)!.RawPath!));
    }

    [Fact]
    public void ProjectionReadStillRefusesMissingAuthoritativeFolder()
    {
        var uuid = (string)Sql("SELECT uuid FROM bulk_bindings WHERE folder='inbox'")!;
        File.Move(Path.Combine(root, "bound-folders", uuid, FolderBundle.FileName), Path.Combine(root, "retained.mkdb"));
        Assert.Throws<FileNotFoundException>(() => services.Database.GetMessage(1));
    }

    private async Task WaitForWriter()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(3))
        {
            try { using var probe = new FileStream(Path.Combine(root, "bound-folders", ".operation.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (FileNotFoundException) { }
            catch (IOException e) when ((e.HResult & 0xffff) == 32) { return; }
            await Task.Delay(10);
        }
        throw new TimeoutException("Background flush did not acquire its lease.");
    }

    private object? Sql(string sql)
    {
        using var c = new SqliteConnection($"Data Source={services.Paths.DatabasePath};Pooling=False");
        c.Open(); using var q = c.CreateCommand(); q.CommandText = sql; return q.ExecuteScalar();
    }

    public void Dispose() => Directory.Delete(root, true);
}
