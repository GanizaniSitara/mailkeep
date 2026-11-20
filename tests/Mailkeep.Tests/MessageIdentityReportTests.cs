using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Tests;

public sealed class MessageIdentityReportTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "mailkeep-identity-" + Guid.NewGuid().ToString("N")));

    public MessageIdentityReportTests() => new MailDatabase(_paths, new RawBlobStore(_paths)).Initialize();

    [Fact]
    public void IdenticalBytesChooseMailkeepFolderAndReadStateWithoutChangingStore()
    {
        var bytes = Mime("same@example.test", "same body");
        var old = Add("archive", "OE Classic", "Inbox", "old", bytes);
        var current = Add("live", "local", "Tax", "new", bytes, isRead: true);
        var before = Snapshot();
        var report = Run();
        var group = Assert.Single(report.ByteMatches);
        Assert.Equal("mailkeep_wins", group.Disposition);
        Assert.Equal([old], group.OeRowsSuperseded);
        var retained = Assert.Single(group.MailkeepRowsRetained);
        Assert.Equal(current, retained.Id);
        Assert.Equal("Tax", retained.Folder);
        Assert.True(retained.IsRead);
        Assert.Equal(1, report.SupersededOeRows);
        Assert.Equal(before, Snapshot());
        Assert.Equal(JsonSerializer.Serialize(report), JsonSerializer.Serialize(Run()));
    }

    [Fact]
    public void ReusedMessageIdWithDifferentBytesIsConflictNotDuplicate()
    {
        Add("archive", "OE Classic", "Inbox", "a", Mime("same@example.test", "original"));
        Add("live", "local", "Tax", "b", Mime("same@example.test", "changed"));
        var report = Run();
        Assert.Empty(report.ByteMatches);
        Assert.Single(report.MessageIdConflicts);
        Assert.Equal(0, report.SupersededOeRows);
    }

    [Fact]
    public void CopiesWithinMailkeepAreRetainedAndConflictingStatesNeedReview()
    {
        var bytes = Mime("copy@example.test", "body");
        Add("archive", "OE Classic", "Inbox", "oe", bytes);
        Add("live", "local", "Tax", "copy1", bytes);
        Add("live", "other", "Inbox", "copy2", bytes, isRead: true);
        var group = Assert.Single(Run().ByteMatches);
        Assert.Equal("review_conflicting_mailkeep_state", group.Disposition);
        Assert.Equal(2, group.MailkeepRowsRetained.Length);
        Assert.Empty(group.OeRowsSuperseded);
    }

    [Fact]
    public void SameFolderCopiesAndOtherArchivesAreNotAutomaticallyCollapsed()
    {
        var bytes = Mime("copy@example.test", "body");
        Add("archive", "Other archive", "Inbox", "a", bytes);
        Add("live", "local", "Tax", "copy1", bytes);
        Add("live", "local", "Tax", "copy2", bytes);
        var group = Assert.Single(Run().ByteMatches);
        Assert.Equal("same_bytes_review_copies", group.Disposition);
        Assert.Equal(2, group.MailkeepRowsRetained.Length);
        Assert.Empty(group.OeRowsSuperseded);
    }

    [Fact]
    public void DeletedAdoptionLinksBackToOeEvenAfterLiveCopyWasRemoved()
    {
        var id = Add("archive", "OE Classic", "Inbox", "oe", Mime("deleted@example.test", "body"));
        DeleteEvidence("live", MailContract.LocalCopyAccount, "deleted", MailContract.AdoptedCopyKeyPrefix + id);
        DeleteEvidence("live", "local", "deleted", "unrecoverable-old-key");
        var report = Run();
        Assert.Equal(id, Assert.Single(report.DeletionLinkedRows).Id);
        Assert.Single(report.Deletions, row => row.LinkedRows.Length == 0);
        Assert.Single(report.Deletions, row => row.LinkedRows.SequenceEqual([id]));
    }

    [Fact]
    public void ContradictoryDeletionAndSurvivingCopyAreFlagged()
    {
        var bytes = Mime("deleted@example.test", "body");
        var id = Add("archive", "OE Classic", "Inbox", "oe", bytes);
        Add("live", "local", "Tax", "live", bytes);
        DeleteEvidence("live", MailContract.LocalCopyAccount, "deleted", MailContract.AdoptedCopyKeyPrefix + id);
        var group = Assert.Single(Run().ByteMatches);
        Assert.Equal("review_conflicting_mailkeep_state", group.Disposition);
        Assert.Empty(group.OeRowsSuperseded);
    }

    [Fact]
    public void MissingHeadersStillAllowByteMatchingButMissingBlobsDoNot()
    {
        var bytes = Encoding.ASCII.GetBytes("Subject: no id\r\n\r\nbody");
        Add("archive", "OE Classic", "Inbox", "a", bytes);
        Add("live", "local", "Tax", "b", bytes);
        Add("live", "local", "Inbox", "missing", Mime("missing@example.test", "body"), pathOverride: "missing.eml");
        Add("live", "local", "Inbox", "outside", bytes, pathOverride: "../outside.eml");
        var report = Run();
        Assert.Equal(2, report.HeaderlessRows);
        Assert.Equal(2, report.VerifiedRows);
        Assert.Equal("mailkeep_wins", Assert.Single(report.ByteMatches).Disposition);
        Assert.Contains(report.Issues, row => row.Evidence.Issue == "missing_blob");
        Assert.Contains(report.Issues, row => row.Evidence.Issue == "blob_path_outside_store");
    }

    [Fact]
    public void ActualBytesAreHashedRatherThanTrustingBlobFilename()
    {
        var bytes = Mime("same@example.test", "original");
        Add("archive", "OE Classic", "Inbox", "a", bytes);
        var path = new RawBlobStore(_paths).Put(bytes);
        File.WriteAllBytes(new RawBlobStore(_paths).FullPath(path), Mime("same@example.test", "replacement"));
        Add("live", "local", "Tax", "b", Mime("same@example.test", "original"), pathOverride: "different.eml");
        File.WriteAllBytes(Path.Combine(_paths.BlobDirectory, "different.eml"), bytes);
        var report = Run();
        Assert.Empty(report.ByteMatches);
        Assert.Single(report.MessageIdConflicts);
    }

    [Fact]
    public void DeletedItemsPlacementWinsAndLinksOriginalFolderTombstone()
    {
        var bytes = Mime("deleted@example.test", "body");
        Add("archive", "OE Classic", "Inbox", "oe", bytes);
        var live = Add("live", "local", LocalFolderKinds.Deleted, "live", bytes);
        DeleteEvidence("live", "local", "Inbox", "live");
        var report = Run();
        Assert.Equal(live, Assert.Single(report.DeletionLinkedRows).Id);
        var group = Assert.Single(report.ByteMatches);
        Assert.Equal("mailkeep_wins", group.Disposition);
        Assert.Equal(LocalFolderKinds.Deleted, Assert.Single(group.MailkeepRowsRetained).Folder);
    }

    [Fact]
    public void MultipleMessageIdHeadersAreFlaggedAndNeverUsedForHeaderMatching()
    {
        var bytes = Encoding.ASCII.GetBytes("Message-ID: <a@example.test>\r\nMessage-ID: <b@example.test>\r\n\r\nbody");
        Add("archive", "OE Classic", "Inbox", "a", bytes);
        var report = Run();
        Assert.Equal(1, report.VerifiedRows);
        Assert.Equal("invalid_or_multiple_message_id", Assert.Single(report.Issues).Evidence.Issue);
        Assert.Empty(report.MessageIdConflicts);
    }

    [Fact]
    public void CancelledAndMissingStoreReportsDoNotCreateOrModifyFiles()
    {
        var before = Snapshot();
        Assert.Throws<OperationCanceledException>(() => new MessageIdentityReport(_paths).Run(new CancellationToken(true)));
        var absent = new AppPaths(Path.Combine(_paths.Root, "does-not-exist"));
        Assert.Throws<FileNotFoundException>(() => new MessageIdentityReport(absent).Run());
        Assert.False(Directory.Exists(absent.Root));
        Assert.Equal(before, Snapshot());
    }

    private MessageIdentityReport.Report Run() => new MessageIdentityReport(_paths).Run();

    private long Add(string source, string account, string folder, string key, byte[] bytes,
        bool isRead = false, string? pathOverride = null)
    {
        var blob = pathOverride ?? new RawBlobStore(_paths).Put(bytes);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO messages(source,account,folder,source_key,is_read,raw_path)
            VALUES($source,$account,$folder,$key,$read,$raw) RETURNING id
            """;
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$folder", folder);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$read", isRead);
        command.Parameters.AddWithValue("$raw", blob);
        return (long)command.ExecuteScalar()!;
    }

    private void DeleteEvidence(string source, string account, string folder, string key)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO local_deletions VALUES($source,$account,$folder,$key,'2026-10-04')";
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$folder", folder);
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = _paths.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private string Snapshot()
    {
        SqliteConnection.ClearAllPools();
        return string.Join("\n", Directory.GetFiles(_paths.Root, "*", SearchOption.AllDirectories)
            // A read-only connection to a WAL database can create shared-memory/empty WAL
            // coordination files. Neither is mail content; the DB and every blob must match.
            .Where(file => !file.EndsWith("-shm", StringComparison.Ordinal) && !file.EndsWith("-wal", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).Select(file => file + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))));
    }

    private static byte[] Mime(string id, string body) => Encoding.ASCII.GetBytes(
        $"Message-ID: <{id}>\r\nFrom: sender@example.test\r\nSubject: Fixture\r\n\r\n{body}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_paths.Root, recursive: true);
    }
}
