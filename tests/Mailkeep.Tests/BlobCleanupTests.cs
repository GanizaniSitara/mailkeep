using System.Text;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Tests;

public sealed class BlobCleanupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mailkeep-blob-cleanup-" + Guid.NewGuid().ToString("N"));
    private readonly MailkeepServices services;
    private readonly byte[] raw = Encoding.UTF8.GetBytes("From: fixture@example.invalid\r\nSubject: Synthetic cleanup\r\n\r\nInvented body");
    public BlobCleanupTests() { services = new(root); services.Database.Initialize(); }
    private long Add(string key, string path) => services.Database.Upsert(new("live", "fixture", "inbox", key, null, 0,
        "Synthetic cleanup", "fixture@example.invalid", "Fixture", [], DateTimeOffset.UnixEpoch, raw.Length, false, 0, "Invented body", null, path));

    [Fact]
    public void IdleDiscoveryCleansLegacyOrphansButPreservesReferencedAndPortableMime()
    {
        var path = services.Blobs.Put(raw); var file = services.Blobs.FullPath(path);
        var orphan = services.Blobs.Put(Encoding.UTF8.GetBytes("invented legacy orphan"));
        var orphanFile = services.Blobs.FullPath(orphan);
        Add("retained", path);
        var migration = new BulkFolderMigration(services.Paths);
        migration.Run(migration.Preview().Confirmation, publish: true);
        var result = services.Database.SweepUnreferencedBlobs(discoverOrphans: true);
        Assert.Null(result.Warning);
        Assert.Equal(1, result.Removed); Assert.False(File.Exists(orphanFile)); Assert.True(File.Exists(file));
        services.Database.DeleteMessageLocally(services.Database.ListMessages()[0].Id, true);
        Assert.True(File.Exists(file)); // The portable history still references its exact MIME.
    }

    [Fact]
    public void PermanentDeletionRemovesOnlyTheLastUnreferencedCopy()
    {
        var path = services.Blobs.Put(raw); var file = services.Blobs.FullPath(path);
        var first = Add("one", path); var second = Add("two", path);
        Assert.True(services.Database.DeleteMessageLocally(first, true)); Assert.True(File.Exists(file));
        Assert.True(services.Database.DeleteMessageLocally(second, true)); Assert.False(File.Exists(file));
        Assert.Equal(0, services.Database.SweepUnreferencedBlobs().Pending);
    }

    [Fact]
    public void OutgoingDraftReferencesSurviveReceivedMessageDeletion()
    {
        var path = services.Blobs.Put(raw); var file = services.Blobs.FullPath(path);
        var id = Add("received", path);
        var draft = services.Database.SaveDraft("fixture", [], [], [], "Synthetic", "Invented", rawPath: path);
        services.Database.DeleteMessageLocally(id, true); Assert.True(File.Exists(file));
        Assert.True(services.Database.DeleteDraft(draft.Id)); Assert.False(File.Exists(file));
    }

    [Fact]
    public void MissingArchiveEvidenceDefersCleanupAndAReadableManifestProtectsItsMime()
    {
        var path = services.Blobs.Put(raw); var file = services.Blobs.FullPath(path);
        var location = Path.Combine(root, "archive"); Directory.CreateDirectory(location);
        var bundleFile = Path.Combine(location, "fixture", FolderBundle.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(bundleFile)!);
        using (var bundle = FolderBundle.Create(bundleFile, "Fixture", "local")) bundle.Add(raw, false, "fixture");
        ArchiveManifest.Register(location, bundleFile);
        new ArchiveStore(services.Paths).Add(location);
        services.Database.DeleteMessageLocally(Add("archive-copy", path), true);
        Assert.True(File.Exists(file));
        Directory.Move(location, location + "-unavailable");
        Assert.NotNull(services.Database.SweepUnreferencedBlobs().Warning); Assert.True(File.Exists(file));
        new ArchiveStore(services.Paths).Remove(location);
        Assert.Equal(1, services.Database.SweepUnreferencedBlobs().Removed); Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task CleanupDefersRatherThanRacingAnUnpublishedBlobReference()
    {
        var path = services.Blobs.Put(raw); var file = services.Blobs.FullPath(path);
        services.Database.RemoveMessage(Add("old", path));
        using var held = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var writer = Task.Run(() => services.Blobs.WithReferences(() =>
        {
            held.Set(); release.Wait();
            return Add("new", services.Blobs.Put(raw));
        }));
        try
        {
            Assert.True(held.Wait(TimeSpan.FromSeconds(5)));
            Assert.NotNull(services.Database.SweepUnreferencedBlobs().Warning);
            Assert.True(File.Exists(file));
        }
        finally { release.Set(); await writer; }
        Assert.True(File.Exists(file)); Assert.Equal(0, services.Database.SweepUnreferencedBlobs().Removed);
    }

    [Fact]
    public void PreviewCleanupIsScopedToItsDisposableStore()
    {
        var directory = PreviewFiles.HtmlDirectory(services.Paths); Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(services.Paths.PreviewDirectory, "attachments", "1"));
        File.WriteAllText(Path.Combine(directory, "message-1.html"), "synthetic");
        File.WriteAllText(Path.Combine(services.Paths.PreviewDirectory, "attachments", "1", "fake.txt"), "synthetic");
        PreviewFiles.Cleanup(services.Paths, includeLegacyShared: true);
        Assert.Empty(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(services.Paths.PreviewDirectory, "*", SearchOption.AllDirectories));
        Directory.Delete(directory);
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
}
