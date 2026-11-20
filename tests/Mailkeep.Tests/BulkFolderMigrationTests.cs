using System.Text;
using System.Text.Json;
using Mailkeep.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Tests;

public sealed class BulkFolderMigrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mailkeep-bulk-test-" + Guid.NewGuid().ToString("N"));
    private readonly MailkeepServices services;
    private readonly Dictionary<long, byte[]> originals = [];
    public BulkFolderMigrationTests()
    {
        services = new(root); services.Database.Initialize();
        services.Database.CreateLocalFolder("Parent @", null, "archive", "OE|Parent @");
        for (var i = 0; i < 20; i++)
        {
            var raw = Encoding.UTF8.GetBytes($"From: test@example.invalid\r\nSubject: Subject {i}\r\n\r\nBody {i}\r\n");
            var id = services.Database.Upsert(new("archive", i % 2 == 0 ? "OE" : "Work", "Parent @/Child " + i % 3,
                "source:" + i, null, 0, "Subject " + i, "test@example.invalid", "Test", [], DateTimeOffset.UnixEpoch,
                raw.Length, i % 2 == 0, 0, "Body " + i, null, services.Blobs.Put(raw)));
            originals[id] = raw;
        }
    }
    private BulkFolderMigration Migration => new(services.Paths);

    [Fact]
    public void PortableReadingRecoversMissingAndCorruptCacheWithoutChangingIdentity()
    {
        var paths = originals.Keys.ToDictionary(id => id, id => services.Database.GetMessage(id)!.RawPath!);
        Migration.Run(Migration.Preview().Confirmation, publish: true);
        foreach (var file in paths.Values) File.Delete(file);
        var message = services.Database.GetMessage(1)!;
        Assert.Equal(1, message.Summary.Id);
        Assert.False(File.Exists(paths[1])); // Display reads directly; filename compatibility is explicitly lazy.
        Assert.Equal(originals[1], File.ReadAllBytes(message.RawPath!));
        File.WriteAllText(message.RawPath!, "a corrupt disposable cache is not the original");
        Assert.Equal(originals[1], File.ReadAllBytes(services.Database.GetMessage(1)!.RawPath!));
        Assert.All(paths.Where(pair => pair.Key != 1), pair => Assert.False(File.Exists(pair.Value)));
    }

    [Fact]
    public void RebuildDoesNotRecreateTheWholeRawCacheAndFilingStillWorks()
    {
        var paths = originals.Keys.Select(id => services.Database.GetMessage(id)!.RawPath!).ToArray();
        Migration.Run(Migration.Preview().Confirmation, publish: true);
        foreach (var file in paths) File.Delete(file);
        Assert.Equal(20, Migration.RebuildProjection());
        Assert.All(paths, file => Assert.False(File.Exists(file)));
        Assert.Equal(1, services.Database.AdoptMessages([1], "inbox"));
        Assert.Equal(originals[1], File.ReadAllBytes(services.Database.GetMessage(1)!.RawPath!));
        Assert.Equal(20L, Sql("SELECT count(*) FROM messages"));
    }

    [Fact]
    public void ReadingPortableContentDoesNotNeedAWritableCacheDirectory()
    {
        Migration.Run(Migration.Preview().Confirmation, publish: true);
        Directory.Delete(services.Paths.BlobDirectory, recursive: true);
        File.WriteAllText(services.Paths.BlobDirectory, "fixture blocks cache-directory creation");
        var content = services.Database.GetMessage(1)!;
        Assert.Equal(1, content.Summary.Id);
        Assert.Contains("Body", content.BodyText);
        Assert.Null(content.RawPath);
        Assert.True(File.Exists(services.Paths.BlobDirectory));
    }

    [Fact]
    public void FilingBoundImportMovesTheSameOccurrenceWithoutHiddenOriginal()
    {
        Migration.Run(Migration.Preview().Confirmation, publish: true);
        Sql("INSERT INTO message_marks VALUES(1,'flag','2026-11-05')");
        services.Database.FolderStorage.Flush();
        services.Catalog.SyncArchive(force: true);
        var oldReference = services.Catalog.Search("Subject", "archive", 100).Results.First(x => x.Id == 1).MessageRef!;
        Assert.Equal(1, services.Database.AdoptMessages([1], "inbox"));
        Assert.Equal(20L, Sql("SELECT count(*) FROM messages"));
        Assert.Equal("live", Sql("SELECT source FROM messages WHERE id=1"));
        Assert.Equal("inbox", services.Database.GetMessage(1)!.Summary.Folder);
        Assert.Equal(originals[1], File.ReadAllBytes(services.Database.GetMessage(1)!.RawPath!));
        Assert.Equal(1L, Sql("SELECT count(*) FROM message_marks WHERE message_id=1"));
        Assert.Equal(1L, services.Catalog.Resolve(oldReference)!.LocalId);
        services.Catalog.SyncOperational();
        Assert.DoesNotContain(services.Catalog.Search("Subject", "archive", 100).Results, m => m.Id == 1);
        Assert.Single(services.Catalog.Search("Subject", "local", 100).Results);
        Assert.Equal(20, Migration.RebuildProjection());
        Assert.Equal("inbox", services.Database.GetMessage(1)!.Summary.Folder);
        Assert.Equal(1L, services.Catalog.Resolve(oldReference)!.LocalId);
        Assert.Equal(1, services.Database.AdoptMessages([1], "deleted"));
        Assert.Equal(20L, Sql("SELECT count(*) FROM messages"));
        Assert.Equal("deleted", services.Database.GetMessage(1)!.Summary.Folder);
        Assert.Equal(0L, Sql("SELECT count(*) FROM bulk_journal"));
    }
    private object? Sql(string sql)
    {
        using var c = new SqliteConnection($"Data Source={services.Paths.DatabasePath};Pooling=False"); c.Open();
        using var q = c.CreateCommand(); q.CommandText = sql; return q.ExecuteScalar();
    }

    [Fact]
    public void InterruptedBoundMoveReplaysWithoutCreatingAnotherMessage()
    {
        Migration.Run(Migration.Preview().Confirmation, publish: true);
        Sql("CREATE TRIGGER fail_move_ack BEFORE DELETE ON bulk_journal BEGIN SELECT RAISE(ABORT,'fixture'); END");
        Assert.Throws<FolderStorageRecoveryException>(() => services.Database.AdoptMessages([1], "inbox"));
        Assert.Equal(20L, Sql("SELECT count(*) FROM messages"));
        Assert.Equal("inbox", Sql("SELECT folder FROM messages WHERE id=1"));
        Sql("DROP TRIGGER fail_move_ack");
        var reopened = new MailkeepServices(root); reopened.Database.Initialize();
        Assert.Equal(0L, Sql("SELECT count(*) FROM bulk_journal"));
        Assert.Equal(1, reopened.Database.AdoptMessages([1], "inbox"));
        Assert.Equal(20, Migration.RebuildProjection());
        Assert.Equal("inbox", reopened.Database.GetMessage(1)!.Summary.Folder);
    }

    [Fact]
    public void BoundMoveRejectsCorruptPortableBytesWithoutChangingLocationOrAliases()
    {
        Migration.Run(Migration.Preview().Confirmation, publish: true);
        var uuid = (string)Sql("SELECT b.uuid FROM bulk_bindings b JOIN messages m ON b.source=m.source AND b.account=m.account AND b.folder=m.folder WHERE m.id=1")!;
        using (var c = new SqliteConnection($"Data Source={Path.Combine(root, "bound-folders", uuid, FolderBundle.FileName)};Pooling=False"))
        {
            c.Open(); using var q = c.CreateCommand();
            q.CommandText = "UPDATE messages SET raw=X'626164' WHERE seq=(SELECT seq FROM local_occurrences WHERE message_id=1)";
            q.ExecuteNonQuery();
        }
        Assert.Throws<InvalidDataException>(() => services.Database.AdoptMessages([1], "inbox"));
        Assert.Equal("archive", Sql("SELECT source FROM messages WHERE id=1"));
        Assert.Equal(0L, Sql("SELECT count(*) FROM adoption_cleanup"));
        Assert.Equal(0L, Sql("SELECT count(*) FROM bulk_journal"));
    }

    [Fact]
    public void ExplicitCopyOfBoundImportStillCreatesAnIndependentOccurrence()
    {
        Migration.Run(Migration.Preview().Confirmation, publish: true);
        Assert.Equal(1, services.Database.CopyMessagesToLocal([1], "inbox"));
        Assert.Equal(21L, Sql("SELECT count(*) FROM messages"));
        Assert.Equal("archive", Sql("SELECT source FROM messages WHERE id=1"));
        Assert.Equal(21, Migration.RebuildProjection());
    }

    [Fact]
    public void MovedImportReferenceSurvivesArchiveAndRestoreToANewId()
    {
        Migration.Run(Migration.Preview().Confirmation, publish: true);
        services.Catalog.SyncArchive(force: true);
        var reference = services.Catalog.Search("Subject", "archive", 100).Results.First(m => m.Id == 1).MessageRef!;
        var destination = services.Database.CreateLocalFolder("Moved import", null);
        services.Database.AdoptMessages([1], destination.Key);
        var location = Path.Combine(root, "detached");
        Directory.CreateDirectory(location);
        Assert.Equal(1, new FolderArchive(services.Paths).Move(destination.Key, location, true, backup: false).Removed);
        var restored = new ArchiveMoveBack(services).Run(location, "Moved import", execute: true);
        var retained = services.Database.ListMessages().Single(m => m.Folder == restored.FolderKeys.Single());
        Assert.NotEqual(1, retained.Id);
        Assert.Equal(retained.Id, services.Catalog.Resolve(reference)!.LocalId);
        Assert.Equal(originals[1], File.ReadAllBytes(services.Database.GetMessage(retained.Id)!.RawPath!));
    }

    [Fact]
    public void BulkConversionPreservesIdsLocationsAndSearchWithoutAdoption()
    {
        var before = (long)Sql("SELECT count(*) FROM messages_fts")!;
        var p = Migration.Preview(); Assert.Equal(20, p.Messages);
        var state = Migration.Run(p.Confirmation);
        Assert.Equal("verified", state.Phase);
        Assert.Throws<IOException>(() => new MailkeepServices(root));
        Assert.Equal(20L, Sql("SELECT count(*) FROM messages WHERE source='archive'"));
        Assert.Equal("complete", Migration.Publish().Phase);
        var reopened = new MailkeepServices(root); reopened.Database.Initialize();
        Assert.Equal(20, reopened.Database.ListHeads("archive").Count);
        Assert.Equal(before, Sql("SELECT count(*) FROM messages_fts"));
        Assert.Equal(0L, Sql("SELECT count(*) FROM adoption_cleanup"));
        foreach (var (id, raw) in originals)
            Assert.Equal(raw, File.ReadAllBytes(reopened.Database.GetMessage(id)!.RawPath!));
        foreach (var output in state.Outputs)
        {
            using var b = FolderBundle.Open(Path.Combine(root, "bound-folders", output.Uuid, FolderBundle.FileName));
            Assert.True(b.Verify().Ok); Assert.Equal(output.Folder.Messages, b.List().Count);
        }
    }

    [Fact]
    public void FailedSourceAndChangedCandidateNeverPublish()
    {
        File.WriteAllText(services.Database.GetMessage(originals.Keys.First())!.RawPath!, "corrupt");
        var p = Migration.Preview();
        Assert.Throws<InvalidDataException>(() => Migration.Run(p.Confirmation));
        Assert.Equal("failed", Migration.Status()!.Phase);
        Assert.Throws<InvalidOperationException>(() => Migration.Publish());
        Assert.Equal("aborted", Migration.Recover().Phase);
        Assert.Equal(20, new MailkeepServices(root).Database.ListHeads("archive").Count);
    }

    [Fact]
    public void StalePlanAndLiveRootRefuseBeforeWriting()
    {
        var p = Migration.Preview(); Sql("UPDATE messages SET is_read=1-is_read WHERE id=1");
        Assert.Throws<InvalidOperationException>(() => Migration.Run(p.Confirmation));
        Assert.Null(Migration.Status());
        Assert.Throws<InvalidOperationException>(() => new BulkFolderMigration(new AppPaths()).Run("unapproved"));
    }

    [Fact]
    public void AdoptAndCopyPersistBeforeReturningAndRemainHiddenAfterRebuild()
    {
        var target = services.Database.CreateLocalFolder("Destination", null);
        Migration.Run(Migration.Preview().Confirmation, publish: true);
        Assert.Equal(1, services.Database.AdoptMessages([1], target.Key));
        Assert.Equal(0L, Sql("SELECT count(*) FROM bulk_journal"));
        Assert.Equal(1, services.Database.AdoptMessages([1], "inbox"));
        Assert.Equal(0L, Sql("SELECT count(*) FROM bulk_journal"));
        Assert.Equal(20, Migration.RebuildProjection());
        Assert.Equal(1L, Sql("SELECT count(*) FROM messages WHERE source='live'"));
        Assert.Equal(19, services.Database.VisibleArchiveFolderCounts(["__mailkeep_local_copy__"]).Values.Sum(c => c.Total));
    }

    [Fact]
    public void NativeMovesAndFolderRenamesRemainRecoverable()
    {
        var a = services.Database.CreateLocalFolder("Native A", null);
        var b = services.Database.CreateLocalFolder("Native B", null);
        var raw = originals.Values.First();
        var id = services.Database.Upsert(new("live", "__mailkeep_local_copy__", a.Key, "native", null, 0,
            "Native", "test@example.invalid", "Test", [], DateTimeOffset.UnixEpoch, raw.Length, false, 0,
            "Native body", null, services.Blobs.Put(raw)));
        var p = Migration.Preview(); Migration.Run(p.Confirmation, publish: true);
        Assert.Equal(1, services.Database.MoveMessage(id, b.Key));
        services.Database.RenameLocalFolder(b.Key, "Renamed B");
        Assert.Equal(0L, Sql("SELECT count(*) FROM bulk_journal"));
        Assert.Equal(21, Migration.RebuildProjection());
        Assert.Equal(b.Key, services.Database.GetMessage(id)!.Summary.Folder);
        Assert.Equal("Renamed B", Sql($"SELECT name FROM local_folders WHERE folder_key='{b.Key}'"));
        Assert.Equal(raw, File.ReadAllBytes(services.Database.GetMessage(id)!.RawPath!));
    }

    [Fact]
    public void ImportedReimportCannotResurrectDeletedPortableOccurrence()
    {
        var p = Migration.Preview(); Migration.Run(p.Confirmation, publish: true);
        Sql("DELETE FROM messages WHERE id=1"); services.Database.FolderStorage.Flush();
        Sql("INSERT INTO messages(id,source,account,folder,source_key,raw_path) VALUES(1,'archive','OE','Parent @/Child 0','source:0','ignored.eml')");
        Assert.Equal(19L, Sql("SELECT count(*) FROM messages"));
        Assert.Equal(19, Migration.RebuildProjection());
        Assert.Equal(19L, Sql("SELECT count(*) FROM messages"));
    }

    [Fact]
    public void ChangedCandidateAndChangedSourceRefusePublication()
    {
        var p = Migration.Preview(); Migration.Run(p.Confirmation);
        using (var c = new SqliteConnection($"Data Source={Path.Combine(root, "bulk-migration", "candidate.db")};Pooling=False"))
        {
            c.Open(); using var q = c.CreateCommand(); q.CommandText = "UPDATE messages SET subject='wrong' WHERE id=1"; q.ExecuteNonQuery();
        }
        Assert.Throws<InvalidDataException>(() => Migration.Publish());
        Assert.Equal("verified", Migration.Status()!.Phase);
        Assert.False(File.Exists(Path.Combine(root, "bulk-migration", "original.db")));
    }

    [Theory]
    [InlineData("UPDATE messages SET raw=x'00' WHERE seq=1")]
    [InlineData("UPDATE local_occurrences SET metadata=json_set(metadata,'$.subject','wrong') WHERE message_id=1")]
    [InlineData("UPDATE state SET is_read=0 WHERE seq=1")]
    [InlineData("UPDATE state SET deleted_at='wrong' WHERE seq=1")]
    [InlineData("UPDATE binding SET hierarchy='{}'")]
    [InlineData("UPDATE binding SET account='wrong'")]
    [InlineData("UPDATE folder SET uuid='wrong'")]
    public void ChangedPortableTruthRefusesPublication(string change)
    {
        var p = Migration.Preview(); var state = Migration.Run(p.Confirmation);
        var output = state.Outputs.Single(o => o.Folder.Account == "OE" && o.Folder.Path == "Parent @/Child 0");
        using var c = new SqliteConnection($"Data Source={Path.Combine(root, "bulk-migration", output.Uuid, FolderBundle.FileName)};Pooling=False");
        c.Open(); using var q = c.CreateCommand(); q.CommandText = change; q.ExecuteNonQuery();
        Assert.Throws<InvalidDataException>(() => Migration.Publish());
        Assert.False(File.Exists(Path.Combine(root, "bulk-migration", "original.db")));
    }

    [Theory]
    [InlineData("DROP TRIGGER bulk_update")]
    [InlineData("DROP INDEX idx_messages_unconverted_counts")]
    public void ChangedSchemaRefusesPublication(string change)
    {
        var p = Migration.Preview(); Migration.Run(p.Confirmation);
        using var c = new SqliteConnection($"Data Source={Path.Combine(root, "bulk-migration", "candidate.db")};Pooling=False");
        c.Open(); using var q = c.CreateCommand(); q.CommandText = change; q.ExecuteNonQuery();
        Assert.Throws<InvalidDataException>(() => Migration.Publish());
        Assert.False(File.Exists(Path.Combine(root, "bulk-migration", "original.db")));
    }

    [Fact]
    public void MissingStorageDirectoryRefusesStaleProjection()
    {
        var p = Migration.Preview(); Migration.Run(p.Confirmation, publish: true);
        Directory.Move(Path.Combine(root, "bound-folders"), Path.Combine(root, "missing-folders"));
        Assert.Throws<DirectoryNotFoundException>(() => services.Database.FolderStorage.Flush());
    }

    [Theory]
    [InlineData("files-published")]
    [InlineData("database-published")]
    public void InterruptedPublicationRecoversWithoutDuplicateOccurrences(string point)
    {
        var p = Migration.Preview(); Migration.Run(p.Confirmation);
        Assert.Throws<OperationCanceledException>(() => Migration.Publish(stage => { if (stage == point) throw new OperationCanceledException(); }));
        Assert.Throws<IOException>(() => new MailkeepServices(root));
        Assert.Equal("complete", Migration.Recover().Phase);
        Assert.Equal(20, new MailkeepServices(root).Database.ListHeads("archive").Count);
        Assert.Equal(20L, Sql("SELECT count(*) FROM messages"));
    }

    [Fact]
    public void JournalAckFailureReplaysIdempotently()
    {
        var p = Migration.Preview(); var state = Migration.Run(p.Confirmation); Migration.Publish();
        Sql("UPDATE messages SET is_read=1-is_read WHERE id=1; CREATE TRIGGER fail_bulk_ack BEFORE DELETE ON bulk_journal BEGIN SELECT RAISE(ABORT,'fixture'); END");
        Assert.ThrowsAny<Exception>(() => services.Database.FolderStorage.Flush());
        Assert.Equal(1L, Sql("SELECT count(*) FROM bulk_journal"));
        Sql("DROP TRIGGER fail_bulk_ack");
        services.Database.FolderStorage.Flush();
        Assert.Equal(0L, Sql("SELECT count(*) FROM bulk_journal"));
        var output = state.Outputs.Single(x => x.Folder.Account == "OE" && x.Folder.Path == "Parent @/Child 0");
        using var c = new SqliteConnection($"Data Source={Path.Combine(root, "bound-folders", output.Uuid, FolderBundle.FileName)};Pooling=False"); c.Open();
        using var q = c.CreateCommand(); q.CommandText = "SELECT count(*) FROM local_applied";
        Assert.Equal(1L, q.ExecuteScalar());
    }

    [Fact]
    public void LostProjectionDatabaseRebuildsIdsHierarchyAndMarks()
    {
        Sql("INSERT INTO message_marks VALUES(1,'flag','2026-11-04T00:00:00Z')");
        var p = Migration.Preview(); Migration.Run(p.Confirmation, publish: true);
        File.Move(services.Paths.DatabasePath, services.Paths.DatabasePath + ".lost");
        var reopened = new MailkeepServices(root); reopened.Database.Initialize();
        Assert.Equal(20, new BulkFolderMigration(reopened.Paths).RebuildProjection());
        Assert.Equal(originals.Keys.Order(), reopened.Database.ListHeads("archive").Select(m => m.Id).Order());
        Assert.Equal(1L, Sql("SELECT count(*) FROM message_marks WHERE message_id=1 AND kind='flag'"));
        Assert.Equal(1L, Sql("SELECT count(*) FROM local_folders WHERE name='Parent @'"));
    }

    [Fact]
    public void LostProjectionDoesNotResurrectPermanentlyDeletedAdoptedMail()
    {
        Migration.Run(Migration.Preview().Confirmation, publish: true);
        services.Database.AdoptMessages([1], "inbox");
        var adopted = (long)Sql("SELECT id FROM messages WHERE source='live'")!;
        Assert.True(services.Database.DeleteMessageLocally(adopted, permanently: true));
        File.Move(services.Paths.DatabasePath, services.Paths.DatabasePath + ".lost");
        var reopened = new MailkeepServices(root); reopened.Database.Initialize();
        Assert.Equal(19, new BulkFolderMigration(reopened.Paths).RebuildProjection());
        Assert.Equal(19, reopened.Database.VisibleArchiveFolderCounts(["__mailkeep_local_copy__"]).Values.Sum(c => c.Total));
        Assert.DoesNotContain(reopened.Database.ListHeads("live"), m => m.Id == adopted);
    }

    [Fact]
    public void RebuildNeverReusesDeletedMessageIdentity()
    {
        Migration.Run(Migration.Preview().Confirmation, publish: true);
        Sql("DELETE FROM messages WHERE id=20"); services.Database.FolderStorage.Flush();
        File.Move(services.Paths.DatabasePath, services.Paths.DatabasePath + ".lost");
        var reopened = new MailkeepServices(root); reopened.Database.Initialize();
        Assert.Equal(19, new BulkFolderMigration(reopened.Paths).RebuildProjection());
        Assert.True((long)Sql("SELECT seq FROM sqlite_sequence WHERE name='messages'")! >= 20);
    }

    [Fact]
    public void RebuildRestoresMessagesFromPortableTruth()
    {
        var p = Migration.Preview(); Migration.Run(p.Confirmation); Migration.Publish();
        Sql("DROP TRIGGER bulk_delete; DELETE FROM messages; DELETE FROM messages_fts");
        Assert.Equal(20, Migration.RebuildProjection());
        Assert.Equal(20, services.Database.ListHeads("archive").Count);
        foreach (var (id, raw) in originals) Assert.Equal(raw, File.ReadAllBytes(services.Database.GetMessage(id)!.RawPath!));
    }

    [Fact]
    public async Task ConcurrentReadAndMoveFlushWaitsForTheOtherWriter()
    {
        Migration.Run(Migration.Preview().Confirmation, publish: true);
        Sql("UPDATE messages SET is_read=0 WHERE id=1");
        using var lease = new FileStream(Path.Combine(root, "bound-folders", ".operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var flush = Task.Run(() => services.Database.FolderStorage.Flush());
        Assert.NotSame(flush, await Task.WhenAny(flush, Task.Delay(200)));
        lease.Dispose();
        await flush.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0L, Sql("SELECT count(*) FROM bulk_journal"));
    }

    [Fact]
    public void ReadStateDoesNotRereadImmutableMimeCache()
    {
        Migration.Run(Migration.Preview().Confirmation, publish: true);
        var file = services.Database.GetMessage(1)!.RawPath!;
        File.Move(file, file + ".unavailable");
        Sql("UPDATE messages SET is_read=0 WHERE id=1");
        Assert.Equal(1, services.Database.FolderStorage.Flush());
        Assert.Equal(0L, Sql("SELECT count(*) FROM bulk_journal"));
        Assert.Equal(20, Migration.RebuildProjection());
        Assert.False(File.Exists(file));
        Assert.Equal(originals[1], File.ReadAllBytes(services.Database.GetMessage(1)!.RawPath!));
    }

    [Fact]
    public void ReadStateJournalReplaysToPortableFileAndRejectsMissingTruth()
    {
        var p = Migration.Preview(); var state = Migration.Run(p.Confirmation); Migration.Publish();
        var id = originals.Keys.First();
        Sql($"UPDATE messages SET is_read=1-is_read WHERE id={id}");
        Assert.Equal(1L, Sql("SELECT count(*) FROM bulk_journal"));
        services.Database.FolderStorage.Flush();
        Assert.Equal(0L, Sql("SELECT count(*) FROM bulk_journal"));
        var output = state.Outputs.Single(x => x.Folder.Account == "OE" && x.Folder.Path == "Parent @/Child 0");
        var file = Path.Combine(root, "bound-folders", output.Uuid, FolderBundle.FileName);
        using (var bundle = FolderBundle.Open(file)) Assert.False(bundle.Get(id)!.Summary.IsRead);
        File.Move(file, file + ".missing");
        Assert.Throws<FileNotFoundException>(() => services.Database.FolderStorage.Flush());
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
}
