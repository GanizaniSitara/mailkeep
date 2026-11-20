using System.Security.Cryptography;
using System.Text;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Tests;

public sealed class OeDraftConversionReportTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "mailkeep-oe-draft-report-" + Guid.NewGuid().ToString("N")));
    public OeDraftConversionReportTests() => new MailDatabase(_paths, new RawBlobStore(_paths)).Initialize();

    [Fact]
    public void ReportsOnlyOeDraftsAndOutboxPreservingIndependentCopiesAndRecipients()
    {
        Add("archive", "OE Classic Main", "Outbox", "a");
        Add("archive", "OE Classic Main", "Outbox", "b");
        Add("oe_archive", "OE Classic", "Drafts", "c");
        Add("archive", "Other import", "Outbox", "d");
        Add("live", "OE Classic", "Outbox", "e");
        Add("archive", "OE Classic", "Inbox", "f");
        var before = Snapshot();
        var report = new OeDraftConversionReport(_paths).Run();
        Assert.Equal("read_only_no_conversion", report.Mode);
        Assert.Equal(3, report.Candidates.Length);
        Assert.Equal(3, report.Candidates.Select(candidate => candidate.Id).Distinct().Count());
        Assert.All(report.Candidates, candidate =>
        {
            Assert.Equal(["sender@example.test"], candidate.From);
            Assert.Equal(["recipient@example.test"], candidate.To);
            Assert.Equal(["cc@example.test"], candidate.Cc);
            Assert.Equal(["bcc@example.test"], candidate.Bcc);
            Assert.True(candidate.HasText);
            Assert.NotNull(candidate.Sha256);
            Assert.Empty(candidate.Issues);
        });
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void MissingBlobsAndDeletedAdoptionsRequireReviewAndNeverRestoreMail()
    {
        var deleted = Add("archive", "OE Classic", "Drafts", "a");
        Add("archive", "OE Classic", "Outbox", "b", "absent.eml");
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO local_deletions VALUES('live',$account,'deleted',$key,'2026-10-06')";
            command.Parameters.AddWithValue("$account", MailContract.LocalCopyAccount);
            command.Parameters.AddWithValue("$key", MailContract.AdoptedCopyKeyPrefix + deleted);
            command.ExecuteNonQuery();
        }
        var before = Snapshot();
        var report = new OeDraftConversionReport(_paths).Run();
        Assert.Contains(report.Candidates, row => row.Id == deleted && row.Issues.Contains("deletion_evidence_do_not_restore"));
        Assert.Contains(report.Candidates, row => row.Issues.Contains("missing_or_unreadable_blob") && row.Sha256 is null);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void MissingRootAndCancellationDoNotInitializeStore()
    {
        var missing = new AppPaths(Path.Combine(_paths.Root, "absent"));
        Assert.Throws<FileNotFoundException>(() => new OeDraftConversionReport(missing).Run());
        Assert.Throws<OperationCanceledException>(() => new OeDraftConversionReport(_paths).Run(new CancellationToken(true)));
        Assert.False(Directory.Exists(missing.Root));
    }

    private long Add(string source, string account, string folder, string key, string? raw = null)
    {
        raw ??= new RawBlobStore(_paths).Put(Encoding.ASCII.GetBytes("Message-ID: <draft@example.test>\r\nFrom: sender@example.test\r\nTo: recipient@example.test\r\nCc: cc@example.test\r\nBcc: bcc@example.test\r\nSubject: Draft\r\n\r\nDraft body"));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO messages(source,account,folder,source_key,subject,raw_path) VALUES($source,$account,$folder,$key,'Draft',$raw) RETURNING id";
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$folder", folder);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$raw", raw);
        return (long)command.ExecuteScalar()!;
    }
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _paths.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
    private string Snapshot() => string.Join("\n", Directory.GetFiles(_paths.Root, "*", SearchOption.AllDirectories)
        .Where(file => !file.EndsWith("-wal") && !file.EndsWith("-shm")).Order()
        .Select(file => file + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))));
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(_paths.Root, true); }
}
