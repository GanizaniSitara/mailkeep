using System.Text;
using System.Text.Json;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Tests;

public sealed class ImportRetirementTests : IDisposable
{
    private readonly MailkeepServices _services = new(Path.Combine(Path.GetTempPath(), "mailkeep-retirement-" + Guid.NewGuid().ToString("N")));
    private readonly string[] _accounts = ["OE Classic Testing", "OE Classic Testing2", "OE Classic Testing3"];

    public ImportRetirementTests() => _services.Database.Initialize();

    [Fact]
    public void RetirementRetainsEveryOccurrenceAndOnlyRemovesNamedBranches()
    {
        var ids = _accounts.Select(account => Add(account)).ToArray();
        var unrelated = Add("OE Classic Testing20");
        _services.Database.AdoptMessages([unrelated], "inbox");
        using (var c = Open())
        using (var command = c.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO message_marks VALUES($id,'replied','2026-01-01');
                INSERT INTO outgoing_origins VALUES('unsent',$id,'forwarded');
                INSERT INTO settings(key,value) VALUES('startup.archive-counts',$saved);
                """;
            command.Parameters.AddWithValue("$id", ids[0]);
            command.Parameters.AddWithValue("$saved", JsonSerializer.Serialize(new
            {
                Folders = _accounts.Append("OE Classic Testing20").Select(account => new { Account = account, Folder = "Inbox", Total = 1, Unread = 1 }),
                ArchiveUnread = _accounts.Select(account => new { Account = account, Folder = "Inbox", Total = 0, Unread = 1 })
            }));
            command.ExecuteNonQuery();
        }
        var retire = new ImportRetirement(_services);
        var before = Scalar("SELECT count(*) FROM messages");
        var preview = retire.Run(_accounts, "deleted");
        Assert.False(preview.Executed);
        Assert.Empty(preview.Plan.Refusals);
        Assert.Equal(ids, preview.Plan.MessageIds);
        Assert.Equal(before, Scalar("SELECT count(*) FROM messages"));

        var result = retire.Run(_accounts, "deleted", execute: true);

        Assert.Equal(3, result.Cleanup!.Removed);
        Assert.True(File.Exists(result.Cleanup.Backup));
        Assert.Equal(3, result.RemovedDeclarations);
        foreach (var id in ids)
        {
            var retained = _services.Database.GetMessage(id)!;
            Assert.Equal("live", retained.Summary.Source);
            Assert.Equal("deleted", retained.Summary.Folder);
            Assert.True(retained.Summary.IsRead);
            Assert.Contains("Preserved body", retained.BodyText);
        }
        var first = _services.Database.GetMessage(ids[0])!.Summary.Id;
        Assert.Equal(first, Scalar("SELECT message_id FROM outgoing_origins"));
        Assert.True(_services.Database.MessageMarks([first])[first].Replied);
        Assert.Equal(1L, Scalar("SELECT count(*) FROM messages WHERE account='OE Classic Testing20'"));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM archive_folders WHERE account='OE Classic Testing20'"));
        Assert.Equal(3L, Scalar("SELECT count(*) FROM adoption_cleanup"));
        var saved = (string)Scalar("SELECT value FROM settings WHERE key='startup.archive-counts'")!;
        Assert.Contains("Testing20", saved);
        Assert.DoesNotContain("Testing2\"", saved);
        Assert.Empty(_services.Database.FolderCounts("archive").Where(folder => _accounts.Contains(folder.Account)));
        Assert.Equal("ok", Scalar("PRAGMA quick_check"));
    }

    [Theory]
    [InlineData("converted")]
    [InlineData("deleted-copy")]
    [InlineData("mirrored")]
    public void UnsafeImportRefusesBeforeFilingAnything(string issue)
    {
        var id = Add(_accounts[0]);
        using (var c = Open())
        using (var command = c.CreateCommand())
        {
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$key", MailContract.AdoptedCopyKeyPrefix + id);
            command.Parameters.AddWithValue("$origin", _accounts[0] + "|Inbox");
            command.CommandText = issue switch
            {
                "converted" => "UPDATE messages SET converted_draft_id='draft' WHERE id=$id",
                "deleted-copy" => "INSERT INTO local_deletions(source,account,folder,source_key,deleted_at) VALUES('live','__mailkeep_local_copy__','deleted',$key,'2026-01-01')",
                _ => "INSERT INTO local_folders(folder_key,name,kind,sort_order,origin,origin_key) VALUES('user:mirror','Inbox mirror','user',9,'archive',$origin)"
            };
            command.ExecuteNonQuery();
        }
        var retire = new ImportRetirement(_services);
        Assert.NotEmpty(retire.Run([_accounts[0]], "deleted").Plan.Refusals);
        Assert.Throws<InvalidOperationException>(() => retire.Run([_accounts[0]], "deleted", execute: true));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM messages"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM adoption_cleanup"));
        Assert.Empty(Directory.GetFiles(_services.Paths.DataDirectory, "*.bak"));
    }

    private long Add(string account)
    {
        var raw = _services.Blobs.Put(Encoding.UTF8.GetBytes("From: fixture@example.test\r\nSubject: Same bytes\r\n\r\nPreserved body\r\n"));
        var id = _services.Database.Upsert(new("archive", account, "Inbox", "same", null, 0, "Same bytes", "fixture@example.test",
            "Fixture", [], DateTimeOffset.UnixEpoch, 100, true, 0, "Preserved body", null, raw));
        _services.Database.CreateArchiveFolder("archive", account, "Inbox");
        return id;
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection($"Data Source={_services.Paths.DatabasePath};Pooling=False");
        c.Open();
        return c;
    }

    private object? Scalar(string sql)
    {
        using var c = Open();
        using var command = c.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(_services.Paths.Root, true); }
}
