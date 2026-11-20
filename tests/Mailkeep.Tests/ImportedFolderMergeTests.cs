using System.Text;
using Mailkeep.Core;
using Mailkeep.Infrastructure;

namespace Mailkeep.Tests;

public sealed class ImportedFolderMergeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mailkeep-merge-" + Guid.NewGuid().ToString("N"));
    private readonly MailkeepServices _services;
    private readonly LocalFolder _from, _into;
    public ImportedFolderMergeTests()
    {
        _services = new(_root); _services.Database.Initialize();
        var parent = _services.Database.CreateLocalFolder("ToDos", null);
        _from = _services.Database.CreateLocalFolder("Medium", null, "archive", "OE|Medium");
        var into = _services.Database.CreateLocalFolder("Medium", parent.Key, "archive", "OE|ToDos/Medium");
        _into = _services.Database.ListLocalFolders().Single(f => f.Key == into.Key);
    }
    private long Add(string folder, string key, bool read = true, string? payload = null)
    {
        var raw = Encoding.UTF8.GetBytes("From: fixture@example.invalid\r\nSubject: " + key + "\r\n\r\nBody " + (payload ?? key) + "\r\n");
        return _services.Database.Upsert(new("archive", "OE", folder, key, null, 0, key, "fixture@example.invalid", "Fixture", [],
            DateTimeOffset.UnixEpoch, raw.Length, read, 0, "Body " + key, null, _services.Blobs.Put(raw)));
    }
    [Fact]
    public void LegacyRootMergeRefusesPortableFoldersBeforeCreatingAnotherBackup()
    {
        var id = Add("Medium", "a");
        var migration = new BulkFolderMigration(_services.Paths);
        migration.Run(migration.Preview().Confirmation, publish: true);
        Assert.Throws<InvalidOperationException>(() => new ImportedFolderMerge(_services.Paths).Preview(_from.Key, _into.Key));
        Assert.Equal("Medium", _services.Database.GetMessage(id)!.Summary.Folder);
        Assert.Empty(Directory.GetFiles(_services.Paths.DataDirectory, "*.bak"));
    }

    [Fact]
    public void MergePreservesOriginalIdsBytesStateAndDestinationThenRemovesOnlyRoot()
    {
        var a = Add("Medium", "a"); var b = Add("Medium", "b", false); var existing = Add("ToDos/Medium", "existing");
        var bytes = new[] { a, b, existing }.ToDictionary(id => id, id => File.ReadAllBytes(_services.Database.GetMessage(id)!.RawPath!));
        var merge = new ImportedFolderMerge(_services.Paths); var plan = merge.Preview(_from.Key, _into.Key);
        Assert.Equal(new[] { a, b }, plan.MessageIds);
        var result = merge.Apply(_from.Key, _into.Key, plan.Confirmation);
        Assert.Equal(2, result.Moved); Assert.True(File.Exists(result.Backup));
        Assert.DoesNotContain(_services.Database.ListLocalFolders(), f => f.Key == _from.Key);
        Assert.Contains(_services.Database.ListLocalFolders(), f => f == _into);
        foreach (var id in bytes.Keys)
        {
            var content = _services.Database.GetMessage(id)!;
            Assert.Equal("ToDos/Medium", content.Summary.Folder);
            Assert.Equal(bytes[id], File.ReadAllBytes(content.RawPath!));
        }
        Assert.False(_services.Database.GetMessage(b)!.Summary.IsRead);
        Assert.Equal(3, _services.Database.Search("Body", "archive").Count);
    }
    [Fact]
    public void MergeRefusesStalePlanDestinationCollisionAndAdoptedCopies()
    {
        var id = Add("Medium", "a"); var merge = new ImportedFolderMerge(_services.Paths);
        var plan = merge.Preview(_from.Key, _into.Key); Add("Medium", "b");
        Assert.Throws<InvalidOperationException>(() => merge.Apply(_from.Key, _into.Key, plan.Confirmation));
        _services.Database.AdoptMessages([id], _into.Key);
        Assert.Throws<InvalidOperationException>(() => merge.Preview(_from.Key, _into.Key));
        Assert.Contains(_services.Database.ListLocalFolders(), f => f.Key == _from.Key);
    }
    [Fact]
    public void MergeRefusesSourceKeyCollisionAndChildren()
    {
        Add("Medium", "a"); Add("ToDos/Medium", "a", payload: "different");
        var merge = new ImportedFolderMerge(_services.Paths);
        Assert.Throws<InvalidDataException>(() => merge.Preview(_from.Key, _into.Key));
        _services.Database.CreateLocalFolder("Child", _from.Key);
        Assert.Throws<InvalidOperationException>(() => merge.Preview(_from.Key, _into.Key));
    }
    [Fact]
    public void IdenticalDestinationCopyWinsAndOldIdAndReferenceResolveToArchive()
    {
        var id = Add("Medium", "a"); var retained = Add("ToDos/Medium", "a", false);
        var merge = new ImportedFolderMerge(_services.Paths); var plan = merge.Preview(_from.Key, _into.Key);
        Assert.Equal(retained, plan.Duplicates[id]);
        var result = merge.Apply(_from.Key, _into.Key, plan.Confirmation);
        Assert.Equal(0, result.Moved); Assert.Equal(1, result.DuplicateOccurrencesRemoved);
        Assert.Equal(retained, _services.Database.GetMessage(id)!.Summary.Id);
        Assert.False(_services.Database.GetMessage(id)!.Summary.IsRead);
        var reference = "msg_" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("archive\nOE\nMedium\na")))[..24];
        var locator = _services.Catalog.Resolve(reference)!;
        Assert.Equal("archive", locator.Location.Source); Assert.Equal(retained, locator.LocalId);
        Assert.Single(_services.Database.ListHeads("archive", "OE", "ToDos/Medium"));
        Assert.Single(_services.Database.Search("Body", "archive").Results);
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_root, true); }
}
