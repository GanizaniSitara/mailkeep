using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Tests;

public sealed class ArchiveRestoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mailkeep-restore-" + Guid.NewGuid().ToString("N"));
    private readonly MailkeepServices _target;
    private string Location => Path.Combine(_directory, "archive");
    public ArchiveRestoreTests()
    {
        _target = new MailkeepServices(Path.Combine(_directory, "target"));
        _target.Database.Initialize();
    }

    private string Bundle(string path, bool empty = false)
    {
        var file = Path.Combine(Location, path.Replace('/', Path.DirectorySeparatorChar), FolderBundle.FileName);
        using (var bundle = FolderBundle.Create(file, path.Split('/')[^1], "local"))
        {
            bundle.RecordEvent(ArchiveManifest.LayoutEvent, JsonSerializer.Serialize(new ArchiveManifest.Layout(path, "user", "archive", "fixture|" + path)));
            if (!empty)
            {
                var raw = Encoding.UTF8.GetBytes("From: Fixture <fixture@example.test>\r\nTo: other@example.test\r\nSubject: Portable body\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=b\r\n\r\n--b\r\nContent-Type: text/plain\r\n\r\nRetained content\r\n--b\r\nContent-Type: text/plain\r\nContent-Disposition: attachment; filename=note.txt\r\n\r\nattachment bytes\r\n--b--\r\n");
                bundle.Add(raw, true, "archive:fixture/" + path + "/key", "2020-01-02T03:04:05Z", 42);
                bundle.Add(raw, false, "archive:fixture/" + path + "/second", "2020-01-02T03:04:05Z", 43);
            }
        }
        SqliteConnection.ClearAllPools();
        ArchiveManifest.Register(Location, file);
        return file;
    }

    [Fact]
    public void TreeHasRealParentsAndRestoreRebuildsItWithoutOriginalDatabaseOrBlobs()
    {
        var a = Bundle("Online Archive - user@work.example/2023/Team/A");
        Bundle("Online Archive - user@work.example/2023/Team/Empty", empty: true);
        Bundle("Online Archive - user@work.example/2024/Team/A");
        var store = new ArchiveStore(_target.Paths);
        var archive = store.Add(Location);
        var tree = Assert.Single(ArchiveTree.Build(store.Folders(archive)));
        Assert.Equal("Online Archive - user@work.example", tree.Name);
        Assert.Equal(["2023", "2024"], tree.Children.Select(node => node.Name));
        Assert.Equal(["A", "Empty"], tree.Children[0].Children.Single().Children.Select(node => node.Name));
        var hashes = Directory.GetFiles(Location, "*", SearchOption.AllDirectories)
            .Where(file => Path.GetFileName(file) is FolderBundle.FileName or ArchiveManifest.FileName)
            .ToDictionary(file => file, Hash);
        var restore = new ArchiveRestore(_target);
        var before = _target.Database.ListLocalFolders().Count;
        var preview = restore.Run(Location, includeChildren: true);
        Assert.Equal(4, preview.Plan.Messages);
        Assert.Equal(before, _target.Database.ListLocalFolders().Count);
        Assert.False(File.Exists(Path.Combine(_target.Paths.Root, "archive-tree.json")));

        var result = restore.Run(Location, includeChildren: true, execute: true);

        Assert.Equal(4, result.Restored);
        var paths = new MailFiling(_target).Folders().Select(folder => folder.Path).ToArray();
        Assert.Contains("Online Archive - user@work.example/2023/Team/Empty", paths);
        Assert.Contains("Online Archive - user@work.example/2024/Team/A", paths);
        var messages = _target.Database.ListMessages(MailContract.LocalCopyAccount);
        Assert.Equal(4, messages.Count);
        Assert.Equal(2, messages.Count(message => message.IsRead));
        Assert.All(messages, message => Assert.Equal(DateTimeOffset.Parse("2020-01-02T03:04:05Z"), message.Date));
        var content = _target.Database.GetMessage(messages[0].Id)!;
        Assert.Contains("Retained content", content.BodyText);
        Assert.Equal("attachment bytes", File.ReadAllText(_target.Database.ExtractAttachment(content.Summary.Id, 0)).Trim());
        using var original = FolderBundle.Open(a);
        Assert.Equal(original.Raw(1), File.ReadAllBytes(_target.Blobs.FullPath(_target.Database.GetMessage(messages[0].Id)!.RawPath!)));
        Assert.All(hashes, pair => Assert.Equal(pair.Value, Hash(pair.Key)));
    }

    [Fact]
    public void FolderAndTreeSelectionsStayScopedAndRepeatedRestoreDoesNotResetLaterFiling()
    {
        Bundle("Root/Parent");
        Bundle("Root/Parent/Child");
        Bundle("Root/Other");
        var restore = new ArchiveRestore(_target);
        Assert.Equal(2, restore.Run(Location, "Root/Parent").Plan.Messages);
        Assert.Equal(4, restore.Run(Location, "Root/Parent", includeChildren: true).Plan.Messages);
        restore.Run(Location, "Root/Parent", execute: true);
        Assert.DoesNotContain(new MailFiling(_target).Folders(), folder => folder.Path.EndsWith("/Child"));
        var messages = _target.Database.ListMessages(MailContract.LocalCopyAccount);
        _target.Database.MoveMessages([messages[0].Id], "deleted");
        var repeat = restore.Run(Location, "Root/Parent", execute: true);
        Assert.Equal((0, 2), (repeat.Restored, repeat.AlreadyRestored));
        Assert.Equal("deleted", _target.Database.GetMessage(messages[0].Id)!.Summary.Folder);
        var subtree = restore.Run(Location, "Root/Parent", includeChildren: true, execute: true);
        Assert.Equal((2, 2), (subtree.Restored, subtree.AlreadyRestored));
        Assert.DoesNotContain(new MailFiling(_target).Folders(), folder => folder.Path == "Root/Other");
    }

    [Fact]
    public void MissingOrCorruptFileRefusesTheWholeTreeBeforeWriting()
    {
        Bundle("Root/A");
        var b = Bundle("Root/B");
        File.Move(b, b + ".missing");
        Assert.Throws<FileNotFoundException>(() => new ArchiveRestore(_target).Run(Location, "Root", true, execute: true));
        Assert.Empty(_target.Database.ListMessages());
        File.Move(b + ".missing", b);
        using (var c = new SqliteConnection($"Data Source={b};Pooling=False"))
        {
            c.Open();
            using var command = c.CreateCommand();
            command.CommandText = "UPDATE messages SET raw=X'01' WHERE seq=1";
            command.ExecuteNonQuery();
        }
        Assert.Throws<InvalidDataException>(() => new ArchiveRestore(_target).Run(Location, "Root", true, execute: true));
        Assert.Empty(_target.Database.ListMessages());
    }

    [Fact]
    public void ConflictDetachedFolderAndInterruptedRestoreLeaveExistingMailboxIntact()
    {
        Bundle("Root/A");
        var restore = new ArchiveRestore(_target);
        var root = _target.Database.CreateLocalFolder("Root", null);
        _target.Database.CreateLocalFolder("A", root.Key);
        Assert.Throws<InvalidOperationException>(() => restore.Run(Location, "Root/A", execute: true));
        var store = new ArchiveStore(_target.Paths);
        store.Add(Location);
        store.SetFolderAttached(Location, "Root/A", false);
        Assert.Throws<InvalidOperationException>(() => restore.Run(Location, "Root/A", into: "Different", execute: true));
        store.SetFolderAttached(Location, "Root/A", true);
        var before = new MailFiling(_target).Folders();
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => restore.Run(Location, "Root/A", into: "Different", execute: true,
            token: cancellation.Token, progress: _ => cancellation.Cancel()));
        Assert.Equal(before, new MailFiling(_target).Folders());
        Assert.Empty(_target.Database.ListMessages());
        Assert.Equal(2, restore.Run(Location, "Root/A", into: "Different", execute: true).Restored);
    }

    [Fact]
    public void CacheLossDoesNotPreventRestoreAndPurgedRestoredMailIsNotResurrected()
    {
        var file = Bundle("Root/A");
        using (var c = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            c.Open();
            using var command = c.CreateCommand();
            command.CommandText = "DELETE FROM headers; DELETE FROM fts";
            command.ExecuteNonQuery();
        }
        var restore = new ArchiveRestore(_target);
        Assert.Equal(2, restore.Run(Location, "Root/A", execute: true).Restored);
        using (var c = new SqliteConnection($"Data Source={_target.Paths.DatabasePath};Pooling=False"))
        {
            c.Open();
            using var command = c.CreateCommand();
            command.CommandText = "DELETE FROM messages";
            command.ExecuteNonQuery();
        }
        Assert.Throws<InvalidOperationException>(() => restore.Run(Location, "Root/A", execute: true));
        Assert.Empty(_target.Database.ListMessages());
    }

    [Fact]
    public void ReArchivingRefusesUnsupportedReplyMarksWithoutLosingThem()
    {
        Bundle("Root/A");
        var restored = new ArchiveMoveBack(_target).Run(Location, "Root/A", execute: true);
        var id = _target.Database.ListMessages()[0].Id;
        using (var connection = new SqliteConnection($"Data Source={_target.Paths.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO message_marks(message_id,kind,marked_at) VALUES($id,'replied','2026-10-26T00:00:00Z')";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
        var archive = new FolderArchive(_target.Paths);
        Assert.Contains(archive.Move(restored.FolderKeys.Single(), Location, false).Plan.Refusals, text => text.Contains("marks"));
        Assert.Throws<InvalidOperationException>(() => archive.Move(restored.FolderKeys.Single(), Location, true, backup: false));
        Assert.Equal(2, _target.Database.ListMessages().Count);
        Assert.Single(_target.Database.MessageMarks([id]));
    }

    [Fact]
    public void MoveBackPreservesTreeAndLeavesUnselectedArchiveFoldersAlone()
    {
        Bundle("Root/Branch/A");
        Bundle("Root/Branch/Empty", empty: true);
        Bundle("Root/Other");
        var moved = new ArchiveMoveBack(_target).Run(Location, "Root/Branch", true, execute: true);
        Assert.Equal(2, moved.Restored);
        Assert.Equal("Root/Other", ArchiveManifest.Read(Location).Folders.Single().OriginalPath);
        Assert.Contains(new MailFiling(_target).Folders(), folder => folder.Path == "Root/Branch/Empty");
        Assert.DoesNotContain(new MailFiling(_target).Folders(), folder => folder.Path == "Root/Other");
    }

    [Fact]
    public void RoundTripPreservesReadChangesNewMailAndOriginalAliases()
    {
        var root = _target.Database.CreateLocalFolder("Root", null);
        var folder = _target.Database.CreateLocalFolder("A", root.Key);
        var bytes = Encoding.UTF8.GetBytes("From: a@example.test\r\nSubject: Round trip\r\n\r\nbody");
        var raw = _target.Blobs.Put(bytes);
        var original = _target.Database.Upsert(new("live", MailContract.LocalCopyAccount, folder.Key, "copy:roundtrip", null, 0,
            "Round trip", "a@example.test", "", [], null, bytes.Length, false, 0, "body", null, raw));
        Directory.CreateDirectory(Location);
        new FolderArchive(_target.Paths).Move(folder.Key, Location, true, backup: false);
        var reference = "msg_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"local\n{MailContract.LocalCopyAccount}\n{folder.Key}\ncopy:roundtrip")))[..24];
        for (var iteration = 0; iteration < 3; iteration++)
        {
            var restored = new ArchiveMoveBack(_target).Run(Location, "Root/A", execute: true);
            var retained = _target.Database.GetMessage(original)!;
            Assert.Equal(retained.Summary.Id, _target.Catalog.Resolve(reference)!.LocalId);
            Assert.Equal(bytes, File.ReadAllBytes(retained.RawPath!));
            if (iteration > 0) Assert.True(retained.Summary.IsRead);
            _target.Database.SetReadStates([retained.Summary.Id], true);
            if (iteration == 0)
                _target.Database.Upsert(new("live", MailContract.LocalCopyAccount, restored.FolderKeys.Single(), "copy:new-mail", null, 0,
                    "New mail", "a@example.test", "", [], null, bytes.Length, true, 0, "body", null, raw));
            Assert.Equal(2, new FolderArchive(_target.Paths).Move(restored.FolderKeys.Single(), Location, true, backup: false).Removed);
            Assert.Empty(_target.Database.ListMessages());
            Assert.Equal(2, ArchiveManifest.Read(Location).Folders.Single().Messages);
        }
    }

    [Fact]
    public void MoveBackRemovesArchiveEntryAndCanBeArchivedAndRestoredAgain()
    {
        Bundle("Root/A");
        var move = new ArchiveMoveBack(_target);
        var preview = move.Run(Location, "Root/A");
        Assert.False(preview.Executed);
        Assert.Empty(_target.Database.ListMessages());
        var restored = move.Run(Location, "Root/A", execute: true);
        Assert.Equal(2, restored.Restored);
        Assert.Empty(ArchiveManifest.Read(Location).Folders);
        var store = new ArchiveStore(_target.Paths);
        Assert.Empty(store.Folders(store.Add(Location)));
        Assert.Single(Directory.GetFiles(Location, "*.bak", SearchOption.AllDirectories));
        var firstIds = _target.Database.ListMessages().Select(message => message.Id).Order().ToArray();
        var rearchive = new FolderArchive(_target.Paths).Move(restored.FolderKeys.Single(), Location, true, backup: false);
        Assert.Equal(2, rearchive.Removed);
        Assert.Empty(_target.Database.ListMessages());
        Assert.Single(ArchiveManifest.Read(Location).Folders);
        Assert.Equal(2, move.Run(Location, "Root/A", execute: true).Restored);
        Assert.Equal(2, _target.Database.ListMessages().Count);
        Assert.All(firstIds, id => Assert.NotNull(_target.Database.GetMessage(id)));
        Assert.Empty(ArchiveManifest.Read(Location).Folders);
    }

    [Fact]
    public void MoveBackOfAnExistingCopyDoesNotDuplicateOrResetLaterReadState()
    {
        Bundle("Root/A");
        new ArchiveRestore(_target).Run(Location, "Root/A", execute: true);
        var ids = _target.Database.ListMessages().Select(message => message.Id).Order().ToArray();
        _target.Database.SetReadStates([ids[1]], true);
        var result = new ArchiveMoveBack(_target).Run(Location, "Root/A", execute: true);
        Assert.Equal((0, 2), (result.Restored, result.AlreadyRestored));
        Assert.Equal(ids, _target.Database.ListMessages().Select(message => message.Id).Order());
        Assert.True(_target.Database.GetMessage(ids[1])!.Summary.IsRead);
        Assert.Empty(ArchiveManifest.Read(Location).Folders);
    }

    [Fact]
    public void InterruptedMoveBackResumesAfterPartialArchiveRenames()
    {
        var a = Bundle("Root/A");
        var b = Bundle("Root/B");
        var move = new ArchiveMoveBack(_target);
        using (var locked = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(() => move.Run(Location, "Root", true, execute: true));
        Assert.Equal(4, _target.Database.ListMessages().Count);
        Assert.False(File.Exists(a));
        Assert.True(File.Exists(b));
        Assert.NotNull(ArchiveMoveBack.PendingPlan(Location));
        Assert.Throws<InvalidOperationException>(() => new ArchiveRestore(_target).Run(Location, "Root", true));
        var preview = move.Run(Location, "Root", true);
        Assert.False(preview.Executed);
        Assert.True(File.Exists(b));
        move.Run(Location, "Root", true, execute: true);
        Assert.Null(ArchiveMoveBack.PendingPlan(Location));
        Assert.Empty(ArchiveManifest.Read(Location).Folders);
        Assert.Equal(4, _target.Database.ListMessages().Count);
        Assert.Equal(2, Directory.GetFiles(Location, "*.bak", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void MoveBackRefusesToHideArchiveIfAnExistingLocalCopyFailsItsByteCheck()
    {
        var file = Bundle("Root/A");
        new ArchiveRestore(_target).Run(Location, "Root/A", execute: true);
        var message = _target.Database.ListMessages()[0];
        var blob = _target.Database.GetMessage(message.Id)!.RawPath!;
        var bytes = File.ReadAllBytes(blob);
        try
        {
            File.WriteAllBytes(blob, [1]);
            Assert.Throws<IOException>(() => new ArchiveMoveBack(_target).Run(Location, "Root/A", execute: true));
            Assert.True(File.Exists(file));
            Assert.NotEmpty(ArchiveManifest.Read(Location).Folders);
        }
        finally { File.WriteAllBytes(blob, bytes); }
        new ArchiveMoveBack(_target).Run(Location, "Root/A", execute: true);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void OldArchiveWindowIdsCannotReadAReplacementFolderAtTheSamePath()
    {
        Bundle("Root/A");
        var store = new ArchiveStore(_target.Paths);
        var archive = store.Add(Location);
        var oldId = store.List(store.Folders(archive).Single())[0].Id;
        var restored = new ArchiveMoveBack(_target).Run(Location, "Root/A", execute: true);
        new FolderArchive(_target.Paths).Move(restored.FolderKeys.Single(), Location, true, backup: false);
        var newId = store.List(store.Folders(archive).Single())[0].Id;
        Assert.NotEqual(oldId, newId);
        Assert.Throws<KeyNotFoundException>(() => store.Get(oldId));
        Assert.NotNull(store.Get(newId));
    }

    [Fact]
    public void ReArchivingCopiedFolderExplainsCollisionBeforeRemovingAnything()
    {
        var file = Bundle("Root/A");
        var before = Hash(file);
        var copy = new ArchiveRestore(_target).Run(Location, "Root/A", execute: true);
        var archive = new FolderArchive(_target.Paths);
        var preview = archive.Move(copy.FolderKeys.Single(), Location, execute: false);
        Assert.Contains(preview.Plan.Refusals, text => text.Contains("archive already contains this folder"));
        Assert.Throws<InvalidOperationException>(() => archive.Move(copy.FolderKeys.Single(), Location, execute: true, backup: false));
        Assert.Equal(2, _target.Database.ListMessages().Count);
        Assert.Equal(before, Hash(file));
    }

    [Fact]
    public void OriginalArchivedIdAndReferenceResolveAfterRestoringToSameMailbox()
    {
        var root = _target.Database.CreateLocalFolder("Root", null);
        var folder = _target.Database.CreateLocalFolder("A", root.Key);
        var raw = _target.Blobs.Put(Encoding.UTF8.GetBytes("From: a@example.test\r\nSubject: Alias\r\n\r\nbody"));
        var id = _target.Database.Upsert(new("live", MailContract.LocalCopyAccount, folder.Key, "copy:fixture", null, 0, "Alias", "a@example.test", "", [], null, 50, true, 0, "body", null, raw));
        Directory.CreateDirectory(Location);
        new FolderArchive(_target.Paths).Move(folder.Key, Location, true, backup: false);
        var restored = new ArchiveRestore(_target).Run(Location, "Root/A", execute: true);
        Assert.Equal(1, restored.Restored);
        var message = _target.Database.GetMessage(id)!;
        Assert.NotEqual(id, message.Summary.Id);
        var reference = "msg_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"local\n{MailContract.LocalCopyAccount}\n{folder.Key}\ncopy:fixture")))[..24];
        Assert.Equal(message.Summary.Id, _target.Catalog.Resolve(reference)!.LocalId);
    }

    private static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(_directory, true); }
}
