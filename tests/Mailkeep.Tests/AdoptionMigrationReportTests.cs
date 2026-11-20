using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Tests;

public sealed class AdoptionMigrationReportTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "mailkeep-adoption-report-" + Guid.NewGuid().ToString("N")));

    [Fact]
    public void ExactLinksPreserveMailkeepStateAndDoNotMergeOrRestoreDeletedCopies()
    {
        var services = new MailkeepServices(_paths.Root);
        services.Database.Initialize();
        var raw = services.Blobs.Put([1, 2, 3]);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _paths.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO messages(id,source,account,folder,source_key,raw_path,is_read) VALUES(1,'archive','OE Classic','Inbox','origin',$raw,0),(2,'live',$account,$folder,$key,$raw,1)";
        command.Parameters.AddWithValue("$raw", raw);
        command.Parameters.AddWithValue("$account", MailContract.LocalCopyAccount);
        command.Parameters.AddWithValue("$folder", LocalFolderKinds.Deleted);
        command.Parameters.AddWithValue("$key", MailContract.AdoptedCopyKeyPrefix + "1");
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO local_deletions VALUES('live',$account,'inbox',$key,'2026-10-06')";
        command.ExecuteNonQuery();
        connection.Close();
        SqliteConnection.ClearAllPools();
        var before = File.ReadAllBytes(_paths.DatabasePath);
        var link = Assert.Single(new AdoptionMigrationReport(_paths).Run().Links);
        Assert.Equal("matching_bytes_mailkeep_state_wins", link.Verdict);
        Assert.False(link.Original!.IsRead);
        Assert.True(Assert.Single(link.Copies).IsRead);
        Assert.Equal(LocalFolderKinds.Deleted, link.Copies[0].Folder);
        Assert.Equal(["inbox"], link.DeletedFolders);
        Assert.Equal(before, File.ReadAllBytes(_paths.DatabasePath));
        connection.Open();
        command.CommandText = "DELETE FROM messages WHERE id=2";
        command.ExecuteNonQuery();
        Assert.Equal("deleted_without_survivor_do_not_restore", Assert.Single(new AdoptionMigrationReport(_paths).Run().Links).Verdict);
    }

    [Fact]
    public void MissingStoreDoesNotInitializeAnything()
    {
        Assert.Throws<FileNotFoundException>(() => new AdoptionMigrationReport(_paths).Run());
        Assert.False(Directory.Exists(_paths.Root));
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true); }
}
