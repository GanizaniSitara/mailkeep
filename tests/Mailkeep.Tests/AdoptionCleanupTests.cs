using System.Security.Cryptography;
using System.Text;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Tests;

public sealed class AdoptionCleanupTests : IDisposable
{
    private readonly MailkeepServices _services = new(Path.Combine(Path.GetTempPath(), "mailkeep-cleanup-" + Guid.NewGuid().ToString("N")));
    private readonly MessageInput _input;
    private readonly long _original;
    private readonly long _copy;

    public AdoptionCleanupTests()
    {
        _services.Database.Initialize();
        var raw = _services.Blobs.Put(Encoding.UTF8.GetBytes("From: fixture@example.invalid\r\nTo: other@example.invalid\r\nSubject: Fixture\r\n\r\nRetained body\r\n"));
        _input = new("archive", "OE Classic", "Inbox", "original-key", null, 0, "Fixture", "fixture@example.invalid",
            "Fixture", [], DateTimeOffset.UnixEpoch, 100, false, 0, "Retained body", null, raw);
        _original = _services.Database.Upsert(_input);
        _services.Database.AdoptMessages([_original], "deleted");
        _copy = Assert.Single(_services.Database.ListMessages(MailContract.LocalCopyAccount)).Id;
        Run($"UPDATE messages SET is_read=1 WHERE id={_copy}; INSERT INTO message_marks VALUES({_original},'replied','2026-10-07'); INSERT INTO outgoing_origins VALUES('fixture-draft',{_original},'forwarded');");
    }

    [Fact]
    public void ExplicitExistingBackupIsReadOnlyAndCreatesNoNewBackup()
    {
        var backup = Path.Combine(_services.Paths.Root, "existing-checked.db");
        using (var source = new SqliteConnection($"Data Source={_services.Paths.DatabasePath};Pooling=False"))
        using (var target = new SqliteConnection($"Data Source={backup};Pooling=False"))
        {
            source.Open(); target.Open(); source.BackupDatabase(target);
        }
        var bytes = File.ReadAllBytes(backup);
        var report = new AdoptionMigrationReport(_services.Paths).Run(default, null, [_original]);
        var result = new AdoptionCleanup(_services.Paths).ApplyUsingExistingBackup(report, backup);
        Assert.Equal(1, result.Removed);
        Assert.Equal(backup, result.Backup);
        Assert.Equal(bytes, File.ReadAllBytes(backup));
        Assert.Empty(Directory.GetFiles(_services.Paths.DataDirectory, "*.before-hidden-cleanup-*.bak"));
        Assert.Equal(_copy, _services.Database.GetMessage(_original)!.Summary.Id);
        Assert.True(_services.Database.GetMessage(_copy)!.Summary.IsRead);
        Assert.Equal(1L, Run("SELECT count(*) FROM messages"));
    }

    [Fact]
    public void ExistingBackupMustExistContainReviewedOriginalAndNotBeLiveDatabase()
    {
        var report = new AdoptionMigrationReport(_services.Paths).Run(default, null, [_original]);
        var cleanup = new AdoptionCleanup(_services.Paths);
        Assert.Throws<InvalidDataException>(() => cleanup.ApplyUsingExistingBackup(report, _services.Paths.DatabasePath));
        Assert.Throws<FileNotFoundException>(() => cleanup.ApplyUsingExistingBackup(report, Path.Combine(_services.Paths.Root, "missing.db")));
        var wrong = Path.Combine(_services.Paths.Root, "wrong.db");
        using (var c = new SqliteConnection($"Data Source={wrong};Pooling=False"))
        {
            c.Open();
            using var q = c.CreateCommand();
            q.CommandText = "CREATE TABLE messages(id INTEGER PRIMARY KEY,source TEXT,account TEXT,folder TEXT,source_key TEXT,raw_path TEXT)";
            q.ExecuteNonQuery();
        }
        Assert.Throws<InvalidDataException>(() => cleanup.ApplyUsingExistingBackup(report, wrong));
        Assert.Equal(2L, Run("SELECT count(*) FROM messages"));
        Assert.Empty(Directory.GetFiles(_services.Paths.DataDirectory, "*.before-hidden-cleanup-*.bak"));
    }

    [Fact]
    public void SelectedCleanupSurvivesFreshPortableRebuildAndKeepsOtherPairs()
    {
        var other = _services.Database.Upsert(_input with { SourceKey = "other-original" });
        _services.Database.AdoptMessages([other], "inbox");
        var migration = new BulkFolderMigration(_services.Paths);
        migration.Run(migration.Preview().Confirmation, publish: true);
        var report = new AdoptionMigrationReport(_services.Paths).Run(default, null, [_original]);
        Assert.Equal("read_only_selected_pairs", report.Mode);
        Assert.Single(report.Links);
        new AdoptionCleanup(_services.Paths).Apply(report);
        Assert.NotNull(_services.Database.GetMessage(other));
        Assert.Equal(_copy, _services.Database.GetMessage(_original)!.Summary.Id);
        File.Delete(_services.Paths.DatabasePath);
        var reopened = new MailkeepServices(_services.Paths.Root);
        reopened.Database.Initialize();
        new BulkFolderMigration(reopened.Paths).RebuildProjection();
        Assert.Equal(_copy, reopened.Database.GetMessage(_original)!.Summary.Id);
        Assert.Equal("deleted", reopened.Database.GetMessage(_original)!.Summary.Folder);
        Assert.True(reopened.Database.GetMessage(_original)!.Summary.IsRead);
        Assert.True(reopened.Database.MessageMarks([_copy])[_copy].Replied);
        var reference = "msg_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("archive\nOE Classic\nInbox\noriginal-key")))[..24];
        Assert.Equal(_copy, reopened.Catalog.Resolve(reference)!.LocalId);
        Assert.NotNull(reopened.Database.GetMessage(other));
        Assert.Equal(0L, reopened.Database.Upsert(_input));
    }

    [Fact]
    public void SelectedReportDoesNotTurnAnUnknownOrDuplicateIdIntoCleanup()
    {
        var report = new AdoptionMigrationReport(_services.Paths).Run(default, null, [long.MaxValue]);
        Assert.Equal("missing_original_requires_review", Assert.Single(report.Links).Verdict);
        Assert.Throws<InvalidDataException>(() => new AdoptionCleanup(_services.Paths).Apply(report));
        Assert.Throws<ArgumentException>(() => new AdoptionMigrationReport(_services.Paths).Run(default, null, [_original, _original]));
        Assert.Equal(2L, Run("SELECT count(*) FROM messages"));
        Assert.Empty(Directory.GetFiles(_services.Paths.DataDirectory, "*.before-hidden-cleanup-*.bak"));
    }

    [Fact]
    public async Task CleanupPreservesVisibleRecordBytesStateReferencesAndReimportProtection()
    {
        var report = new AdoptionMigrationReport(_services.Paths).Run();
        var result = new AdoptionCleanup(_services.Paths).Apply(report);
        Assert.Equal(1, result.Removed);
        Assert.True(File.Exists(result.Backup));
        Assert.Equal(1L, Run("SELECT count(*) FROM messages"));
        Assert.Equal(_copy, _services.Database.GetMessage(_original)!.Summary.Id);
        Assert.Equal("deleted", _services.Database.GetMessage(_copy)!.Summary.Folder);
        Assert.True(_services.Database.GetMessage(_copy)!.Summary.IsRead);
        Assert.True(_services.Database.MessageMarks([_copy])[_copy].Replied);
        Assert.Equal(_copy, Run("SELECT message_id FROM outgoing_origins"));
        Assert.Equal(0L, _services.Database.Upsert(_input));
        Run("INSERT INTO messages(source,account,folder,source_key) VALUES('archive','OE Classic','Inbox','original-key')");
        Assert.Equal(1L, Run("SELECT count(*) FROM messages"));
        var reference = "msg_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("archive\nOE Classic\nInbox\noriginal-key")))[..24];
        Assert.Equal(_copy, _services.Catalog.Resolve(reference)!.LocalId);
        var moved = await new MailFiling(_services).MoveAsync(reference, "inbox", execute: true);
        Assert.Equal(_copy, moved.ResultId);
        Assert.Equal("inbox", _services.Database.GetMessage(_original)!.Summary.Folder);
        Assert.Throws<InvalidDataException>(() => new AdoptionCleanup(_services.Paths).Apply(report));
        Assert.Empty(_services.Database.ListOutgoing());
        Assert.Equal("ok", Run("PRAGMA quick_check"));
    }

    [Fact]
    public void StaleReportRefusesWithoutRemovingAnything()
    {
        var report = new AdoptionMigrationReport(_services.Paths).Run();
        _services.Database.MoveMessages([_copy], "inbox");
        Assert.Throws<InvalidDataException>(() => new AdoptionCleanup(_services.Paths).Apply(report));
        Assert.Equal(2L, Run("SELECT count(*) FROM messages"));
    }

    [Fact]
    public void MatchingBytesWithDifferentDisplayedIdentityAreNotRemoved()
    {
        Run($"UPDATE messages SET subject='different' WHERE id={_copy}");
        var report = new AdoptionMigrationReport(_services.Paths).Run();
        Assert.Throws<InvalidDataException>(() => new AdoptionCleanup(_services.Paths).Apply(report));
        Assert.Equal(2L, Run("SELECT count(*) FROM messages"));
        Assert.Equal(0L, Run("SELECT count(*) FROM adoption_cleanup"));
    }

    private object? Run(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _services.Paths.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(_services.Paths.Root, true); }
}
