using System.Text;
using Mailkeep.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Tests;

public sealed class ArchiveMessageMoveTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mailkeep-import-move-" + Guid.NewGuid().ToString("N"));
    private readonly MailkeepServices services;
    public ArchiveMessageMoveTests()
    {
        services = new(root); services.Database.Initialize();
        services.Database.CreateArchiveFolder("archive", "import", "Filed @/Child");
    }
    private long Add(string folder, string key, string account = "import")
    {
        var raw = Encoding.UTF8.GetBytes($"From: fixture@example.invalid\r\nSubject: {key}\r\n\r\nExact body {key}\r\n");
        return services.Database.Upsert(new("archive", account, folder, key, null, 0, key,
            "fixture@example.invalid", "Fixture", [], DateTimeOffset.UnixEpoch, raw.Length, false, 0,
            "Exact body " + key, null, services.Blobs.Put(raw)));
    }
    [Fact]
    public void PortableSiblingMovePreservesIdsBytesStateAndSurvivesRepeatedRecovery()
    {
        var first = Add("Source", "one"); var second = Add("Source", "two");
        var existing = Add("Filed @/Child", "three");
        var original = services.Database.ListMessages().ToDictionary(m => m.Id,
            m => File.ReadAllBytes(services.Database.GetMessage(m.Id)!.RawPath!));
        var migration = new BulkFolderMigration(services.Paths);
        migration.Run(migration.Preview().Confirmation, publish: true);
        services.Catalog.SyncOperational(); services.Catalog.SyncArchive();
        Assert.Equal(2, services.Database.MoveImportedMessages([first, second, first], "import", "Filed @/Child"));
        Assert.Equal(2, services.Catalog.SyncOperational());
        Assert.Empty(services.Catalog.Search("one", "archive", folder: "Source").Results);
        Assert.Equal("Filed @/Child", Assert.Single(services.Catalog.Search("one", "archive").Results).Folder);
        Check(services);
        Assert.Equal(0, Add("Source", "one"));
        // A newly-created empty destination must acquire a portable binding too.
        services.Database.CreateArchiveFolder("archive", "import", "Empty @");
        Assert.Equal(1, services.Database.MoveImportedMessages([first], "import", "Empty @"));
        Assert.Equal(1, services.Database.MoveImportedMessages([first], "import", "Filed @/Child"));
        for (var pass = 0; pass < 2; pass++)
        {
            File.Delete(services.Paths.DatabasePath);
            var recovered = new MailkeepServices(root); recovered.Database.Initialize();
            Assert.Equal(3, recovered.Database.FolderStorage.RebuildProjection());
            recovered.Database.FolderStorage.Flush(); Check(recovered);
            recovered.Catalog.SyncOperational(); recovered.Catalog.SyncArchive();
            Assert.Equal("Filed @/Child", Assert.Single(recovered.Catalog.Search("one", "archive").Results).Folder);
            Assert.Throws<InvalidOperationException>(() => Add("Source", "one"));
            Assert.Equal(3, recovered.Database.ListMessages().Count);
        }
        void Check(MailkeepServices current)
        {
            Assert.Equal(3, current.Database.ListMessages().Count);
            foreach (var id in new[] { first, second, existing })
            {
                var message = current.Database.GetMessage(id)!;
                Assert.Equal(id, message.Summary.Id);
                Assert.Equal("archive", message.Summary.Source);
                Assert.Equal("import", message.Summary.Account);
                Assert.Equal("Filed @/Child", message.Summary.Folder);
                Assert.False(message.Summary.IsRead);
                Assert.Equal(original[id], File.ReadAllBytes(message.RawPath!));
            }
            Assert.Contains(current.Database.FolderCounts("archive"), f => f.Account == "import" && f.Folder == "Source" && f.Total == 0);
        }
    }
    [Fact]
    public void EmptyConvertedArchiveDeletionDoesNotRewriteAnUnrelatedLockedFileAndSurvivesRecovery()
    {
        var moved = Add("Parent/Empty", "moved");
        var kept = Add("Other", "retained");
        for (var i = 0; i < 127; i++) Add("Unrelated " + i, "retained " + i);
        services.Database.CreateArchiveFolder("archive", "import", "Parent");
        services.Database.CreateArchiveFolder("archive", "import", "Unbound empty");
        var migration = new BulkFolderMigration(services.Paths);
        migration.Run(migration.Preview().Confirmation, publish: true);
        services.Database.MoveImportedMessages([moved], "import", "Other");
        string uuid;
        using (var c = new SqliteConnection($"Data Source={services.Paths.DatabasePath};Pooling=False"))
        {
            c.Open(); using var q = c.CreateCommand();
            q.CommandText = "SELECT uuid FROM bulk_bindings WHERE source='archive' AND account='import' AND folder='Other'";
            uuid = (string)q.ExecuteScalar()!;
        }
        var file = Path.Combine(services.Database.FolderStorage.DirectoryPath, uuid, FolderBundle.FileName);
        var before = File.ReadAllBytes(file);
        var unrelated = Directory.GetFiles(services.Database.FolderStorage.DirectoryPath, FolderBundle.FileName, SearchOption.AllDirectories)
            .Where(path => path != file).ToDictionary(path => path, File.ReadAllBytes);
        using (var locked = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            locked.Open(); using var transaction = locked.BeginTransaction();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            services.Database.DeleteArchiveFolder("archive", "import", "Parent/Empty");
            Console.WriteLine($"EMPTY CONVERTED ARCHIVE DELETE elapsed_ms={clock.ElapsedMilliseconds}");
            transaction.Rollback();
        }
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Single(unrelated.Where(pair => !pair.Value.SequenceEqual(File.ReadAllBytes(pair.Key))));
        for (var pass = 0; pass < 2; pass++)
        {
            File.Delete(services.Paths.DatabasePath);
            var recovered = new MailkeepServices(root); recovered.Database.Initialize();
            Assert.Equal(129, recovered.Database.FolderStorage.RebuildProjection());
            recovered.Database.FolderStorage.Flush();
            var folders = recovered.Database.FolderCounts("archive");
            Assert.DoesNotContain(folders, f => f.Folder == "Parent/Empty");
            Assert.Contains(folders, f => f.Folder == "Unbound empty");
            Assert.Contains(folders, f => f.Folder == "Parent");
            Assert.Equal("Other", recovered.Database.GetMessage(moved)!.Summary.Folder);
            Assert.Equal("Other", recovered.Database.GetMessage(kept)!.Summary.Folder);
        }
    }
    [Fact]
    public void InvalidSelectionOrConflictingIdentityRollsBackTheWholeMove()
    {
        var first = Add("Source", "one"); var second = Add("Source", "two");
        Add("Filed @/Child", "two"); var foreign = Add("Source", "foreign", "other");
        Assert.Throws<InvalidOperationException>(() => services.Database.MoveImportedMessages([first, second], "import", "Filed @/Child"));
        Assert.Equal("Source", services.Database.GetMessage(first)!.Summary.Folder);
        Assert.Equal("Source", services.Database.GetMessage(second)!.Summary.Folder);
        Assert.Throws<InvalidOperationException>(() => services.Database.MoveImportedMessages([first, foreign], "import", "Filed @/Child"));
        Assert.Throws<KeyNotFoundException>(() => services.Database.MoveImportedMessages([first, long.MaxValue], "import", "Filed @/Child"));
        Assert.Throws<InvalidOperationException>(() => services.Database.MoveImportedMessages([first], "import", "Missing"));
        Assert.Equal("Source", services.Database.GetMessage(first)!.Summary.Folder);
        Assert.Equal(first, Add("Source", "one"));
    }
    public void Dispose() => Directory.Delete(root, true);
}
