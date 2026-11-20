using System.Text;
using System.Text.Json;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Tests;

public sealed class FiledImportedFolderDeletionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mailkeep-filed-delete-" + Guid.NewGuid().ToString("N"));
    private readonly MailkeepServices _services;
    private readonly LocalFolder _friends, _followup;
    private readonly List<long> _originals = [];
    private readonly Dictionary<long, byte[]> _bytes = [];
    private MailDatabase Db => _services.Database;
    public FiledImportedFolderDeletionTests()
    {
        _services = new(_root); Db.Initialize(); var parent = Db.CreateLocalFolder("OE6 Folders", null);
        _friends = Db.CreateLocalFolder("Friends", parent.Key, "archive", "OE|OE6 Folders/Friends");
        _followup = Db.CreateLocalFolder("2027", null); Db.CreateArchiveFolder("archive", "OE", "OE6 Folders/Friends");
        for (var i = 0; i < 10; i++)
        {
            var raw = Encoding.UTF8.GetBytes($"From: fixture@example.invalid\r\nSubject: Friends {i}\r\nMessage-ID: <friends-{i}@example.invalid>\r\n\r\nFriend {i}\r\n");
            var id = Db.Upsert(new("archive", "OE", "OE6 Folders/Friends", "original:" + i, null, 0, "Friends " + i,
                "fixture@example.invalid", "Fixture", [], DateTimeOffset.UnixEpoch.AddDays(i), raw.Length, i % 2 == 0, 0, "Friend " + i, null, _services.Blobs.Put(raw)));
            _originals.Add(id); _bytes[id] = raw;
        }
    }
    private Dictionary<long, long> FileAll() { Db.AdoptMessages(_originals, _followup.Key); return _originals.ToDictionary(id => id, id => Db.AdoptedCopies([id])[id].Id); }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeletingEmptyViewPreservesFiledCopiesProvenanceAliasesAndReimportGuard(bool localEntry)
    {
        var copies = FileAll(); Db.SetReadStates(copies.Values.ToArray(), true);
        if (localEntry) Db.DeleteLocalFolder(_friends.Key); else Db.DeleteArchiveFolder("archive", "OE", "OE6 Folders/Friends");
        Assert.DoesNotContain(Db.ListLocalFolders(), f => f.Key == _friends.Key);
        Assert.DoesNotContain(Db.FolderCounts("archive"), f => f.Folder == "OE6 Folders/Friends");
        Db.CreateArchiveFolder("archive", "OE", "OE6 Folders/Friends");
        Assert.DoesNotContain(Db.FolderCounts("archive"), f => f.Folder == "OE6 Folders/Friends");
        foreach (var id in _originals)
        {
            var message = Db.GetMessage(id)!; Assert.Equal(copies[id], message.Summary.Id); Assert.True(message.Summary.IsRead);
            Assert.Equal(_followup.Key, message.Summary.Folder); Assert.Equal(_bytes[id], File.ReadAllBytes(message.RawPath!));
            using var metadata = JsonDocument.Parse(Db.GetSetting("folder-storage.origins:" + copies[id])!);
            Assert.Equal(id, metadata.RootElement[0].GetProperty("Id").GetInt64());
            Db.Upsert(new("archive", "OE", "OE6 Folders/Friends", "original:" + _originals.IndexOf(id), null, 0, "should not return",
                "fixture@example.invalid", "Fixture", [], DateTimeOffset.UnixEpoch, _bytes[id].Length, false, 0, "", null, _services.Blobs.Put(_bytes[id])));
        }
        Assert.Empty(Db.ListHeads("archive")); Assert.Equal(10, Db.ListHeads("live", folder: _followup.Key).Count);
    }
    [Fact]
    public void MissingOrDifferentCopyRefusesWithoutPartialCleanup()
    {
        Db.AdoptMessages(_originals.Take(9).ToArray(), _followup.Key);
        Assert.Throws<InvalidOperationException>(() => Db.DeleteArchiveFolder("archive", "OE", "OE6 Folders/Friends"));
        Assert.Equal(10, Db.ListHeads("archive").Count); Assert.Contains(Db.ListLocalFolders(), f => f.Key == _friends.Key);
        var copies = FileAll(); var bad = Encoding.UTF8.GetBytes("From: changed@example.invalid\r\n\r\nDifferent");
        using (var c = new SqliteConnection($"Data Source={_services.Paths.DatabasePath};Pooling=False"))
        {
            c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "UPDATE messages SET raw_path=$raw WHERE id=$id";
            cmd.Parameters.AddWithValue("$raw", _services.Blobs.Put(bad)); cmd.Parameters.AddWithValue("$id", copies[_originals[^1]]); cmd.ExecuteNonQuery();
        }
        Assert.Throws<InvalidDataException>(() => Db.DeleteArchiveFolder("archive", "OE", "OE6 Folders/Friends"));
        Assert.Equal(10, Db.ListHeads("archive").Count); Assert.Null(Db.GetSetting("folder-storage.origins:" + copies[_originals[0]]));
    }
    [Fact]
    public void SameFolderCopyOrChildrenStillRefuseDeletion()
    {
        Db.AdoptMessages(_originals, _friends.Key);
        Assert.Throws<InvalidOperationException>(() => Db.DeleteLocalFolder(_friends.Key));
        Db.MoveMessages(Db.ListHeads("live", folder: _friends.Key).Select(m => m.Id).ToArray(), _followup.Key);
        Db.CreateLocalFolder("Child", _friends.Key);
        Assert.Throws<InvalidOperationException>(() => Db.DeleteArchiveFolder("archive", "OE", "OE6 Folders/Friends"));
        Assert.Equal(10, Db.ListHeads("archive").Count);
    }
    [Fact]
    public void PortableDestinationRebuildKeepsReferencesAndOriginalProvenance()
    {
        var copies = FileAll(); Mailkeep.TestFixtures.PortableStorageFixture.BindLocal(_services, _followup.Key);
        Db.DeleteArchiveFolder("archive", "OE", "OE6 Folders/Friends");
        Assert.Equal(0, Db.FolderStorage.Inspect().Pending);
        using (var c = new SqliteConnection($"Data Source={_services.Paths.DatabasePath};Pooling=False"))
        {
            c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "DELETE FROM adoption_cleanup; DELETE FROM settings WHERE key LIKE 'folder-storage.origins:%'"; cmd.ExecuteNonQuery();
        }
        Assert.Equal(10, Db.FolderStorage.RebuildProjection());
        Assert.All(_originals, id => { Assert.Equal(copies[id], Db.GetMessage(id)!.Summary.Id); Assert.Equal(_bytes[id], File.ReadAllBytes(Db.GetMessage(id)!.RawPath!)); });
        Db.CreateArchiveFolder("archive", "OE", "OE6 Folders/Friends");
        Assert.DoesNotContain(Db.FolderCounts("archive"), f => f.Folder == "OE6 Folders/Friends");
    }
    [Theory]
    [InlineData("exact")]
    [InlineData("bom")]
    [InlineData("trailing-lines")]
    public void IndependentlyReceivedCounterpartsKeepBothByteVersionsThroughRebuild(string variant)
    {
        var copies = ReceiveAll(variant);
        Mailkeep.TestFixtures.PortableStorageFixture.BindLocal(_services, _followup.Key);
        Db.DeleteLocalFolder(_friends.Key);
        Assert.DoesNotContain(Db.ListLocalFolders(), f => f.Key == _friends.Key);
        Assert.Empty(Db.ListHeads("archive"));
        foreach (var id in _originals)
        {
            Assert.Equal(copies[id], Db.GetMessage(id)!.Summary.Id);
            using var metadata = JsonDocument.Parse(Db.GetSetting("folder-storage.origins:" + copies[id])!);
            var origin = metadata.RootElement[0];
            var preserved = variant == "exact" ? _bytes[id] : Convert.FromBase64String(origin.GetProperty("RawBase64").GetString()!);
            Assert.Equal(_bytes[id], preserved);
            if (variant != "exact") File.Delete(_services.Blobs.FullPath(_services.Blobs.Put(_bytes[id])));
        }
        using (var c = new SqliteConnection($"Data Source={_services.Paths.DatabasePath};Pooling=False"))
        {
            c.Open(); using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM adoption_cleanup; DELETE FROM settings WHERE key LIKE 'folder-storage.origins:%'"; cmd.ExecuteNonQuery();
        }
        Assert.Equal(10, Db.FolderStorage.RebuildProjection());
        foreach (var id in _originals)
        {
            var kept = Db.GetMessage(id)!;
            Assert.Equal(copies[id], kept.Summary.Id); Assert.Equal(_followup.Key, kept.Summary.Folder);
            Assert.Equal(Variant(_bytes[id], variant), File.ReadAllBytes(kept.RawPath!));
            Assert.Equal(_bytes[id], File.ReadAllBytes(_services.Blobs.FullPath(
                Path.Combine(FolderBundle.Sha256(_bytes[id])[..2], FolderBundle.Sha256(_bytes[id]) + ".eml"))));
        }
        Db.CreateArchiveFolder("archive", "OE", "OE6 Folders/Friends");
        Assert.DoesNotContain(Db.FolderCounts("archive"), f => f.Folder == "OE6 Folders/Friends");
    }
    [Theory]
    [InlineData("different")]
    [InlineData("ambiguous")]
    [InlineData("connected")]
    public void UnverifiedReceivedMatchesRefuseWithoutRemovingAnyOriginal(string problem)
    {
        var copies = ReceiveAll(problem == "different" ? "different" : "exact");
        if (problem == "ambiguous") ReceiveAll("exact", "second:");
        if (problem == "connected")
        {
            using var c = new SqliteConnection($"Data Source={_services.Paths.DatabasePath};Pooling=False"); c.Open();
            using var cmd = c.CreateCommand(); cmd.CommandText = "UPDATE messages SET server_uid=1 WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", copies[_originals[^1]]); cmd.ExecuteNonQuery();
        }
        Assert.ThrowsAny<Exception>(() => Db.DeleteLocalFolder(_friends.Key));
        Assert.Equal(10, Db.ListHeads("archive").Count);
        Assert.Contains(Db.ListLocalFolders(), f => f.Key == _friends.Key);
        Assert.All(copies.Values, id => Assert.Null(Db.GetSetting("folder-storage.origins:" + id)));
    }
    private Dictionary<long, long> ReceiveAll(string variant, string prefix = "received:")
        => _originals.ToDictionary(id => id, id =>
        {
            var i = _originals.IndexOf(id); var raw = Variant(_bytes[id], variant);
            return Db.Upsert(new("live", "local:fixture@example.invalid", _followup.Key, prefix + i, null, 0,
                "Friends " + i, "fixture@example.invalid", "Fixture", [], DateTimeOffset.UnixEpoch.AddDays(i),
                raw.Length, true, 0, "Friend " + i, null, _services.Blobs.Put(raw)));
        });
    private static byte[] Variant(byte[] bytes, string variant) => variant switch
    {
        "bom" => [0xef, 0xbb, 0xbf, .. bytes],
        "trailing-lines" => [.. bytes, 13, 10, 13, 10],
        "different" => [.. bytes, 88],
        _ => bytes
    };
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(_root, true); }
}
