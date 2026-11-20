using System.Security.Cryptography;
using System.Text;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Tests;

public sealed class FolderBundleTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "mailkeep-bundle-" + Guid.NewGuid().ToString("N")));
    private string Out => Path.Combine(_paths.Root, "out");

    public FolderBundleTests()
    {
        new MailDatabase(_paths, new RawBlobStore(_paths)).Initialize();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO local_folders(folder_key,parent_key,name,kind,sort_order,origin,origin_key)
            VALUES('user:arch',NULL,'Archive','user',1,'archive',NULL),
                  ('user:amz','user:arch','Amazon','user',2,'archive','OE Classic|zz/Amazon')
            """;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void ConvertCopiesVisibleMessagesByteForByteWithoutChangingTheDatabase()
    {
        var a = Add("archive", "OE Classic", "zz/Amazon", "a", Mime("a", "first", "2019-01-01"), isRead: true, date: "2019-01-01T00:00:00Z");
        var b = Add("archive", "OE Classic", "zz/Amazon", "b", Mime("b", "second\r\nFrom the top", "2019-02-01"), date: "2019-02-01T00:00:00Z");
        var filed = Add("live", "local", "user:amz", "c", Mime("c", "filed here", "2019-03-01"), date: "2019-03-01T00:00:00Z");
        // Hidden by the app: the mailbox has its own copy, or the adopted copy was deleted.
        Add("archive", "OE Classic", "zz/Amazon", "owned", Mime("d", "dup", "2019-04-01"), date: "2019-04-01T00:00:00Z", subject: "Same", sender: "s@x.test");
        Add("live", "local", "Inbox", "own", Mime("d2", "dup", "2019-04-01"), date: "2019-04-01T00:00:00Z", subject: "Same", sender: "s@x.test");
        var adopted = Add("archive", "OE Classic", "zz/Amazon", "gone", Mime("e", "gone", "2019-05-01"), date: "2019-05-01T00:00:00Z");
        Tombstone(MailContract.AdoptedCopyKeyPrefix + adopted);
        Add("archive", "OE Classic", "zz/Other", "x", Mime("x", "other folder", "2019-01-01"));

        var before = Snapshot();
        var conversion = new FolderConversion(_paths);
        var preview = conversion.Convert("Archive/Amazon", Out, execute: false);
        Assert.Equal(3, preview.Plan.Messages);
        Assert.Equal(1, preview.Plan.HiddenOwnedByMailbox);
        Assert.Equal(1, preview.Plan.HiddenAdoptedCopyDeleted);
        Assert.False(File.Exists(preview.Plan.Target));

        var result = conversion.Convert("user:amz", Out, execute: true);
        Assert.Equal(before, Snapshot());
        Assert.True(result.Verify!.Ok);
        Assert.Equal(3, result.Written);
        Assert.Equal(Path.Combine(Out, "Amazon", FolderBundle.FileName), result.File);
        Assert.False(File.Exists(result.File + "-wal"));

        using (var bundle = FolderBundle.Open(result.File!))
        {
            var listed = bundle.List();
            Assert.Equal([filed, b, a], listed.Select(row => row.LegacyId!.Value));
            Assert.True(listed.Single(row => row.LegacyId == a).IsRead);
            Assert.False(listed.Single(row => row.LegacyId == b).IsRead);
            Assert.Equal(Blob(b), bundle.Raw(listed.Single(row => row.LegacyId == b).Seq));
            Assert.Equal("Archive/Amazon".Split('/')[1], bundle.Folder().Name);
        }
        var comparison = conversion.Compare(result.File!);
        Assert.True(comparison.Ok, string.Join("; ", comparison.Mismatches.Select(m => m.Issue)));
        Assert.Equal(3, comparison.Compared);
    }

    [Fact]
    public void ConvertRefusesToOverwriteAnExistingFolderFile()
    {
        Add("archive", "OE Classic", "zz/Amazon", "a", Mime("a", "x", "2019-01-01"));
        var conversion = new FolderConversion(_paths);
        conversion.Convert("user:amz", Out, execute: true);
        Assert.Throws<IOException>(() => conversion.Convert("user:amz", Out, execute: true));
    }

    [Fact]
    public void CompareReportsAFolderThatChangedSinceConversion()
    {
        Add("archive", "OE Classic", "zz/Amazon", "a", Mime("a", "x", "2019-01-01"));
        var file = new FolderConversion(_paths).Convert("user:amz", Out, execute: true).File!;
        var later = Add("live", "local", "user:amz", "new", Mime("n", "new", "2019-06-01"));
        var comparison = new FolderConversion(_paths).Compare(file);
        Assert.False(comparison.Ok);
        Assert.Contains(comparison.Mismatches, mismatch => mismatch.LegacyId == later);
    }

    [Fact]
    public void CachesRebuildFromBytesAndStateSurvives()
    {
        var path = Path.Combine(Out, "t", FolderBundle.FileName);
        using (var bundle = FolderBundle.Create(path, "T", "local"))
        {
            bundle.Add(Mime("r", "searchable words", "2020-01-01"), isRead: true, origin: "test");
            bundle.Add(Mime("u", "other", "2020-01-02"), isRead: false, origin: "test");
        }
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM headers; DELETE FROM fts;";
            command.ExecuteNonQuery();
        }
        using (var bundle = FolderBundle.Open(path))
            Assert.False(bundle.Verify().Ok);
        using (var bundle = FolderBundle.Open(path, readOnly: false))
        {
            Assert.Equal(2, bundle.RebuildCaches());
            Assert.True(bundle.Verify().Ok);
            var rows = bundle.List();
            Assert.Equal("Fixture r", rows.Single(row => row.IsRead).Subject);
            Assert.Contains("searchable words", rows.Single(row => row.IsRead).Snippet);
        }
    }

    [Fact]
    public void VerifyCatchesAlteredBytesAndOpenRejectsForeignFiles()
    {
        var path = Path.Combine(Out, "v", FolderBundle.FileName);
        using (var bundle = FolderBundle.Create(path, "V", "local"))
            bundle.Add(Mime("v", "original", "2020-01-01"), isRead: false, origin: "test");
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE messages SET raw=$raw";
            command.Parameters.AddWithValue("$raw", Mime("v", "tampered", "2020-01-01"));
            command.ExecuteNonQuery();
        }
        using (var bundle = FolderBundle.Open(path))
            Assert.Contains(bundle.Verify().Problems, problem => problem.Issue.Contains("sha256"));

        var foreign = Path.Combine(Out, "foreign.db");
        using (var connection = new SqliteConnection($"Data Source={foreign};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE t(x)";
            command.ExecuteNonQuery();
        }
        Assert.Throws<InvalidDataException>(() => FolderBundle.Open(foreign));
    }

    [Fact]
    public void ArchiveListsReadsAndExtractsWithIdsThatCannotReachTheDatabase()
    {
        var location = Path.Combine(Out, "ArchiveA");
        var path = Path.Combine(location, "Shopping", "Amazon", FolderBundle.FileName);
        var withAttachment = Encoding.ASCII.GetBytes(
            "Message-ID: <att@example.test>\r\nFrom: A <a@example.test>\r\nTo: me@example.test\r\nDate: Tue, 01 Jan 2019 00:00:00 +0000\r\n"
            + "Subject: With file\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=\"b\"\r\n\r\n"
            + "--b\r\nContent-Type: text/plain\r\n\r\nSee attached\r\n"
            + "--b\r\nContent-Type: text/plain; name=\"note.txt\"\r\nContent-Disposition: attachment; filename=\"note.txt\"\r\n\r\nhello file\r\n--b--\r\n");
        using (var bundle = FolderBundle.Create(path, "Amazon", "local"))
        {
            bundle.Add(Mime("p", "plain body", "2019-01-02"), isRead: true, origin: "test");
            bundle.Add(withAttachment, isRead: false, origin: "test");
        }
        File.WriteAllText(Path.Combine(location, "not-a-folder.mkdb"), "ignored");
        var before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

        var store = new ArchiveStore(_paths);
        var archive = store.Add(location, "Old mail");
        var folder = Assert.Single(store.Folders(archive));
        Assert.Equal(("Shopping/Amazon", 2, 1), (folder.Label, folder.Count, folder.Unread));
        var rows = store.List(folder);
        Assert.All(rows, row => Assert.True(row.Id < 0 && ArchiveStore.Owns(row.Id) && row.Account == "Old mail"));
        var attached = rows.Single(row => row.Subject == "With file");
        var content = store.Get(attached.Id)!;
        Assert.Contains("See attached", content.BodyText);
        Assert.Equal("Shopping/Amazon", content.Summary.Folder);
        var index = content.Attachments.ToList().FindIndex(file => !file.Inline);
        Assert.Equal("hello file", File.ReadAllText(store.ExtractAttachment(attached.Id, index)).Trim());
        SqliteConnection.ClearAllPools();
        Assert.Equal(before, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }

    [Fact]
    public void ArchiveSearchCoversAttachedArchivesAndNamesTheOnesItSkipped()
    {
        string Archive(string name, string body)
        {
            var location = Path.Combine(Out, name);
            using var bundle = FolderBundle.Create(Path.Combine(location, "Inbox", FolderBundle.FileName), "Inbox", "local");
            bundle.Add(Mime(name, body, "2020-01-01"), isRead: true, origin: "test");
            return location;
        }
        var attached = Archive("Attached", "invoice for the boiler");
        var detached = Archive("Detached", "invoice for the roof");
        var gone = Archive("Gone", "invoice for the car");
        var store = new ArchiveStore(_paths);
        store.Add(attached);
        store.Add(detached);
        store.Add(gone);
        store.SetAttached(detached, false);
        SqliteConnection.ClearAllPools();
        Directory.Delete(gone, recursive: true);

        var result = store.Search("invoice");
        Assert.Contains("boiler", Assert.Single(result.Hits).Snippet);
        Assert.Equal(["Detached (detached)", "Gone (not found)"], result.NotSearched);
        Assert.Empty(store.Search("subject:nothinglikethis").Hits);
        Assert.Single(store.Search("\"unbalanced").Hits.Concat(store.Search("boiler").Hits));
    }

    [Fact]
    public void ArchiveListIsRememberedAndRemovingNeverTouchesDisk()
    {
        var location = Path.Combine(Out, "Kept");
        using (var bundle = FolderBundle.Create(Path.Combine(location, "F", FolderBundle.FileName), "F", "local"))
            bundle.Add(Mime("k", "body", "2020-01-01"), isRead: false, origin: "test");
        var store = new ArchiveStore(_paths);
        store.Add(location, "Kept mail");
        store.SetAttached(location, false);
        var restarted = new ArchiveStore(_paths);
        Assert.Equal(new ArchiveStore.Archive("Kept mail", Path.GetFullPath(location), false), Assert.Single(restarted.Archives));
        restarted.Remove(location);
        Assert.Empty(new ArchiveStore(_paths).Archives);
        Assert.True(File.Exists(Path.Combine(location, "F", FolderBundle.FileName)));
        Assert.Throws<DirectoryNotFoundException>(() => store.Add(Path.Combine(Out, "nowhere")));
    }
    [Fact]
    public void ArchiveMoveRemovesTheWholeFolderOnlyAfterItsFileAndBackupCheckOut()
    {
        var a = Add("archive", "OE Classic", "zz/Amazon", "a", Mime("a", "first", "2019-01-01"), isRead: true, date: "2019-01-01T00:00:00Z");
        var b = Add("archive", "OE Classic", "zz/Amazon", "b", Mime("b", "second", "2019-02-01"), date: "2019-02-01T00:00:00Z");
        var filed = Add("live", "local", "user:amz", "c", Mime("c", "filed here", "2019-03-01"), date: "2019-03-01T00:00:00Z");
        Add("archive", "OE Classic", "zz/Amazon", "owned", Mime("d", "dup", "2019-04-01"), date: "2019-04-01T00:00:00Z", subject: "Same", sender: "s@x.test");
        var own = Add("live", "local", "Inbox", "own", Mime("d2", "dup", "2019-04-01"), date: "2019-04-01T00:00:00Z", subject: "Same", sender: "s@x.test");
        var adopted = Add("archive", "OE Classic", "zz/Amazon", "gone", Mime("e", "gone", "2019-05-01"), date: "2019-05-01T00:00:00Z");
        Tombstone(MailContract.AdoptedCopyKeyPrefix + adopted);
        var other = Add("archive", "OE Classic", "zz/Other", "x", Mime("x", "other folder", "2019-01-01"));
        Exec("INSERT INTO archive_folders(source,account,folder) VALUES('archive','OE Classic','zz/Amazon'),('archive','OE Classic','zz/Other')");
        Exec("""INSERT INTO settings(key,value) VALUES('startup.archive-counts','{"Folders":[{"Account":"OE Classic","Folder":"zz/Amazon","Total":5,"Unread":1},{"Account":"OE Classic","Folder":"zz/Other","Total":1,"Unread":1}],"ArchiveUnread":[{"Account":"OE Classic","Folder":"zz/Amazon","Total":0,"Unread":1}]}')""");
        var location = Path.Combine(Out, "archive");
        Directory.CreateDirectory(location);
        var blobs = Directory.GetFiles(_paths.BlobDirectory, "*", SearchOption.AllDirectories).Length;

        var archive = new FolderArchive(_paths);
        var before = Snapshot();
        var preview = archive.Move("Archive/Amazon", location, execute: false);
        Assert.Equal(before, Snapshot());
        Assert.Empty(preview.Plan.Refusals);
        Assert.Equal((3, 2, 1, 4), (preview.Plan.Visible, preview.Plan.HiddenDuplicates, preview.Plan.LiveRows, preview.Plan.ImportedRows));
        Assert.Equal(Path.Combine(location, "Archive", "Amazon", FolderBundle.FileName), preview.Plan.File);

        var result = archive.Move("user:amz", location, execute: true);
        Assert.Equal((5, 2), (result.Removed, result.RemovedHidden));
        Assert.True(result.Registered);
        Assert.Equal(0L, Scalar("SELECT count(*) FROM messages WHERE folder='user:amz' OR folder='zz/Amazon'"));
        Assert.Equal(2L, Scalar($"SELECT count(*) FROM messages WHERE id IN ({own},{other})"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM local_folders WHERE folder_key='user:amz'"));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM local_folders WHERE folder_key='user:arch'"));
        Assert.Equal("zz/Other", Scalar("SELECT group_concat(folder) FROM archive_folders"));
        var saved = (string)Scalar("SELECT value FROM settings WHERE key='startup.archive-counts'")!;
        Assert.DoesNotContain("zz/Amazon", saved);
        Assert.Contains("zz/Other", saved);
        Assert.Equal(5L, Scalar("SELECT count(*) FROM archived_messages"));
        Assert.Equal(3L, Scalar("SELECT count(*) FROM archived_messages WHERE archive_seq IS NOT NULL"));
        Assert.Equal(blobs, Directory.GetFiles(_paths.BlobDirectory, "*", SearchOption.AllDirectories).Length);

        // The backup holds the folder as it was.
        using (var copy = new SqliteConnection($"Data Source={result.Backup};Mode=ReadOnly;Pooling=False"))
        {
            copy.Open();
            using var command = copy.CreateCommand();
            command.CommandText = "SELECT count(*) FROM messages WHERE folder='user:amz' OR folder='zz/Amazon'";
            Assert.Equal(5L, command.ExecuteScalar());
        }
        // The file is the folder: the three messages the app showed, readable through the archive list.
        using (var bundle = FolderBundle.Open(result.Plan.File))
        {
            Assert.Equal([filed, b, a], bundle.List().Select(row => row.LegacyId!.Value));
            Assert.True(bundle.Verify().Ok);
        }
        SqliteConnection.ClearAllPools();
        var store = new ArchiveStore(_paths);
        Assert.Equal(3, store.Folders(store.Archives.Single()).Single().Count);

        // A later import of the same message is turned away.
        Exec("INSERT INTO messages(source,account,folder,source_key,is_read,raw_path) VALUES('archive','OE Classic','zz/Amazon','a',0,'x')");
        Assert.Equal(0L, Scalar("SELECT count(*) FROM messages WHERE folder='zz/Amazon'"));
    }

    [Fact]
    public void ArchiveMoveRefusesAFolderWithSubfoldersAndChangesNothing()
    {
        Add("archive", "OE Classic", "zz/Amazon", "a", Mime("a", "x", "2019-01-01"));
        var location = Path.Combine(Out, "archive");
        Directory.CreateDirectory(location);
        var before = Snapshot();
        var archive = new FolderArchive(_paths);
        Assert.Contains("it has subfolders", archive.Move("user:arch", location, execute: false).Plan.Refusals);
        Assert.Throws<InvalidOperationException>(() => archive.Move("user:arch", location, execute: true));
        Assert.Equal(before, Snapshot());
        Assert.Empty(Directory.GetFileSystemEntries(location));
    }

    [Fact]
    public void ArchivingTheLastChildRemovesItsEmptyImportedParentsAndSavedCounts()
    {
        Exec("UPDATE local_folders SET origin_key='OE Classic|zz' WHERE folder_key='user:arch'");
        Exec("INSERT INTO archive_folders(source,account,folder) VALUES('archive','OE Classic','zz'),('archive','OE Classic','zz/Amazon')");
        Exec("""INSERT INTO settings(key,value) VALUES('startup.archive-counts','{"Folders":[{"Account":"OE Classic","Folder":"zz","Total":0,"Unread":0}],"ArchiveUnread":[]}')""");
        Add("archive", "OE Classic", "zz/Amazon", "a", Mime("a", "body", "2019-01-01"));
        var location = Path.Combine(Out, "archive");
        Directory.CreateDirectory(location);

        new FolderArchive(_paths).Move("user:amz", location, execute: true, backup: false, pruneEmptyParents: true);

        Assert.Equal(0L, Scalar("SELECT count(*) FROM local_folders WHERE folder_key='user:arch'"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM archive_folders WHERE folder='zz'"));
        Assert.Empty(new MailDatabase(_paths, new RawBlobStore(_paths)).FolderCounts("archive"));
        Assert.DoesNotContain("\"zz\"", (string)Scalar("SELECT value FROM settings WHERE key='startup.archive-counts'")!);
        Assert.Equal(1L, Scalar("SELECT count(*) FROM archived_messages"));
        using var bundle = FolderBundle.Open(FolderArchive.TargetFile(location, "Archive/Amazon"));
        Assert.Single(bundle.List());
    }

    [Theory]
    [InlineData("mail")]
    [InlineData("child")]
    [InlineData("imported-child")]
    [InlineData("draft")]
    [InlineData("native")]
    public void ArchivingAChildKeepsParentsThatStillHaveAUse(string retained)
    {
        Exec("UPDATE local_folders SET origin_key='OE Classic|zz' WHERE folder_key='user:arch'");
        Exec("INSERT INTO archive_folders(source,account,folder) VALUES('archive','OE Classic','zz')");
        Add("archive", "OE Classic", "zz/Amazon", "a", Mime("a", "body", "2019-01-01"));
        switch (retained)
        {
            case "mail": Add("live", "local", "user:arch", "b", Mime("b", "keep", "2019-01-01")); break;
            case "child": Exec("INSERT INTO local_folders(folder_key,parent_key,name,kind,sort_order,origin) VALUES('user:keep','user:arch','Keep','user',3,'local')"); break;
            case "imported-child": Exec("INSERT INTO archive_folders(source,account,folder) VALUES('archive','OE Classic','zz/Keep')"); break;
            case "draft":
                Exec("INSERT INTO outgoing_messages(id,created_at,updated_at) VALUES('draft','2026-01-01','2026-01-01')");
                Exec("INSERT INTO draft_folders(draft_id,folder_key) VALUES('draft','user:arch')");
                break;
            case "native": Exec("UPDATE local_folders SET origin='local' WHERE folder_key='user:arch'"); break;
        }
        var location = Path.Combine(Out, "archive");
        Directory.CreateDirectory(location);

        new FolderArchive(_paths).Move("user:amz", location, execute: true, backup: false, pruneEmptyParents: true);

        Assert.Equal(1L, Scalar("SELECT count(*) FROM local_folders WHERE folder_key='user:arch'"));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM archive_folders WHERE folder='zz'"));
    }

    [Fact]
    public void PruningExistingEmptyParentsIsReadOnlyUntilExecutedAndRequiresArchivedDescendants()
    {
        var archive = new FolderArchive(_paths);
        Assert.Empty(archive.PruneEmptyParents("user:arch"));
        Add("archive", "OE Classic", "zz/Amazon", "a", Mime("a", "body", "2019-01-01"));
        var location = Path.Combine(Out, "archive");
        Directory.CreateDirectory(location);
        archive.Move("user:amz", location, execute: true, backup: false);
        Exec("UPDATE local_folders SET origin_key='OE Classic|zz' WHERE folder_key='user:arch'");
        Exec("INSERT INTO archive_folders(source,account,folder) VALUES('archive','OE Classic','zz')");
        var before = Snapshot();

        var plan = archive.PruneEmptyParents("user:arch");

        Assert.Equal("user:arch", Assert.Single(plan).Key);
        Assert.Equal(before, Snapshot());
        Assert.Equal(plan, archive.PruneEmptyParents("user:arch", execute: true));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM local_folders WHERE folder_key='user:arch'"));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM archived_messages"));
    }

    [Fact]
    public void ArchiveMoveStopsWhenTheFolderChangedSinceItsFileWasWritten()
    {
        Add("archive", "OE Classic", "zz/Amazon", "a", Mime("a", "x", "2019-01-01"));
        var location = Path.Combine(Out, "archive");
        var file = FolderArchive.TargetFile(location, "Archive/Amazon");
        new FolderConversion(_paths).Convert("user:amz", Path.GetDirectoryName(Path.GetDirectoryName(file)!)!, execute: true);
        Add("live", "local", "user:amz", "late", Mime("l", "arrived later", "2019-06-01"));
        var before = Snapshot();
        Assert.Throws<InvalidDataException>(() => new FolderArchive(_paths).Move("user:amz", location, execute: true));
        Assert.Equal(before, Snapshot());
        Assert.False(File.Exists(Path.Combine(_paths.Root, "archives.json")));
    }

    [Fact]
    public void ArchiveMoveFinishesFromAFileLeftByAnEarlierRunThatStopped()
    {
        Add("archive", "OE Classic", "zz/Amazon", "a", Mime("a", "x", "2019-01-01"));
        Add("archive", "OE Classic", "zz/Amazon", "b", Mime("b", "y", "2019-02-01"));
        var location = Path.Combine(Out, "archive");
        var file = FolderArchive.TargetFile(location, "Archive/Amazon");
        new FolderConversion(_paths).Convert("user:amz", Path.GetDirectoryName(Path.GetDirectoryName(file)!)!, execute: true);
        var result = new FolderArchive(_paths).Move("user:amz", location, execute: true);
        Assert.True(result.Plan.FileExists);
        Assert.Equal(2, result.Removed);
        Assert.Equal(0L, Scalar("SELECT count(*) FROM messages WHERE folder='zz/Amazon'"));
    }

    [Fact]
    public void ArchiveMoveCanSkipTheWholeDatabaseBackup()
    {
        Add("archive", "OE Classic", "zz/Amazon", "a", Mime("a", "x", "2019-01-01"));
        var location = Path.Combine(Out, "archive");
        Directory.CreateDirectory(location);
        var result = new FolderArchive(_paths).Move("user:amz", location, execute: true, backup: false);
        Assert.Null(result.Backup);
        Assert.Equal(1, result.Removed);
        Assert.Empty(Directory.GetFiles(_paths.DataDirectory, "*.bak"));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM archived_messages"));
    }

    [Fact]
    public void ADetachedArchiveFolderIsNotOpenedOrSearchedAndCanBeAttachedAgain()
    {
        Add("archive", "OE Classic", "zz/Amazon", "a", Mime("a", "battle bunny", "2019-01-01"));
        var location = Path.Combine(Out, "archive");
        Directory.CreateDirectory(location);
        new FolderArchive(_paths).Move("user:amz", location, execute: true, backup: false);
        SqliteConnection.ClearAllPools();

        var store = new ArchiveStore(_paths);
        var folder = store.Folders(store.Archives.Single()).Single();
        Assert.True(folder.Attached);
        Assert.Single(store.Search("bunny").Hits);

        store.SetFolderAttached(location, folder.Label, attached: false);
        var reread = new ArchiveStore(_paths);
        var detached = reread.Folders(reread.Archives.Single()).Single();
        Assert.False(detached.Attached);
        Assert.Equal(0, detached.Count);
        var search = reread.Search("bunny");
        Assert.Empty(search.Hits);
        Assert.Contains($"archive / {folder.Label} (detached)", search.NotSearched);
        Assert.True(File.Exists(folder.File));

        reread.SetFolderAttached(location, folder.Label, attached: true);
        var again = new ArchiveStore(_paths);
        Assert.Null(again.Archives.Single().DetachedFolders);
        Assert.Single(again.Search("bunny").Hits);
    }

    private void Exec(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private long Add(string source, string account, string folder, string key, byte[] bytes,
        bool isRead = false, string? date = null, string subject = "", string sender = "")
    {
        var blob = new RawBlobStore(_paths).Put(bytes);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO messages(source,account,folder,source_key,is_read,raw_path,date_utc,subject,sender)
            VALUES($source,$account,$folder,$key,$read,$raw,$date,$subject,$sender) RETURNING id
            """;
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$folder", folder);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$read", isRead);
        command.Parameters.AddWithValue("$raw", blob);
        command.Parameters.AddWithValue("$date", (object?)date ?? DBNull.Value);
        command.Parameters.AddWithValue("$subject", subject);
        command.Parameters.AddWithValue("$sender", sender);
        return (long)command.ExecuteScalar()!;
    }

    private void Tombstone(string key)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO local_deletions(source,account,folder,source_key,deleted_at) VALUES($live,$account,'Deleted Items',$key,'2026-01-01')";
        command.Parameters.AddWithValue("$live", MailContract.LiveSource);
        command.Parameters.AddWithValue("$account", MailContract.LocalCopyAccount);
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }

    private byte[] Blob(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT raw_path FROM messages WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        return File.ReadAllBytes(new RawBlobStore(_paths).FullPath((string)command.ExecuteScalar()!));
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_paths.DatabasePath};Pooling=False");
        connection.Open();
        return connection;
    }

    private string Snapshot()
    {
        SqliteConnection.ClearAllPools();
        return string.Join("\n", Directory.GetFiles(_paths.DataDirectory, "*", SearchOption.AllDirectories)
            .Where(file => !file.EndsWith("-shm", StringComparison.Ordinal) && !file.EndsWith("-wal", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).Select(file => file + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))));
    }

    private static byte[] Mime(string id, string body, string date) => Encoding.ASCII.GetBytes(
        $"Message-ID: <{id}@example.test>\r\nFrom: Sender <sender@example.test>\r\nTo: me@example.test\r\n"
        + $"Date: {DateTimeOffset.Parse(date + "T00:00:00Z"):R}\r\nSubject: Fixture {id}\r\n\r\n{body}\r\n");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_paths.Root, recursive: true);
    }
}
