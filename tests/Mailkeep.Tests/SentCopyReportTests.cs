using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Tests;

public sealed class SentCopyReportTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "mailkeep-sent-report-" + Guid.NewGuid().ToString("N")));

    [Fact]
    public void DistinguishesRetainedFiledDeletedMissingAndConflictingCopiesWithoutWrites()
    {
        var services = new MailkeepServices(_paths.Root);
        services.Database.Initialize();
        var raw = services.Blobs.Put(System.Text.Encoding.ASCII.GetBytes("From: sender@example.invalid\r\n\r\nBody"));
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _paths.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        foreach (var (id, folder) in new[] { ("retained", "sent"), ("filed", "custom"), ("deleted", "deleted"), ("missing", ""), ("conflict", "sent") })
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO outgoing_messages(id,sending_identity_id,to_json,subject,body_text,status,raw_path,created_at,updated_at) VALUES($id,'fixture','[]','Subject','Body','sent',$raw,'2026-10-06','2026-10-06')";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$raw", raw);
            command.ExecuteNonQuery();
            if (folder.Length == 0) continue;
            command.CommandText = "INSERT INTO messages(source,account,folder,source_key,raw_path) VALUES('live','fixture',$folder,'sent:' || $id,$copy)";
            command.Parameters.AddWithValue("$folder", folder);
            command.Parameters.AddWithValue("$copy", id == "conflict" ? services.Blobs.Put([1, 2, 3]) : raw);
            command.ExecuteNonQuery();
            if (id == "deleted")
            {
                command.CommandText = "INSERT INTO local_deletions VALUES('live','fixture','deleted','sent:deleted','2026-10-06')";
                command.ExecuteNonQuery();
            }
        }
        connection.Close();
        SqliteConnection.ClearAllPools();
        var before = File.ReadAllBytes(_paths.DatabasePath);
        var report = new SentCopyReport(_paths).Run();
        Assert.Equal("read_only_no_backfill", report.Mode);
        var verdicts = report.Deliveries.ToDictionary(row => row.Id, row => row.Verdict);
        Assert.Equal("retained_in_sent", verdicts["retained"]);
        Assert.Equal("filed_do_not_restore", verdicts["filed"]);
        Assert.Equal("deleted_do_not_restore", verdicts["deleted"]);
        Assert.Equal("missing_requires_review", verdicts["missing"]);
        Assert.Equal("different_bytes_requires_review", verdicts["conflict"]);
        Assert.Equal(before, File.ReadAllBytes(_paths.DatabasePath));
    }

    [Fact]
    public void MissingStoreAndCancellationDoNotCreateDatabase()
    {
        Assert.Throws<FileNotFoundException>(() => new SentCopyReport(_paths).Run());
        Assert.Throws<OperationCanceledException>(() => new SentCopyReport(_paths).Run(new CancellationToken(true)));
        Assert.False(Directory.Exists(_paths.Root));
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true); }
}
