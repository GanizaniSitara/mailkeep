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

    [Fact]
    public async Task SweepDoesNotHoldCoordinatorWriteLockWhileScanningExternalBundles()
    {
        var retained = services.Blobs.Put(raw);
        var deleteMe = services.Blobs.Put(Encoding.UTF8.GetBytes("From: fixture@example.invalid\r\nSubject: Delete during sweep\r\n\r\nDelete body"));
        var orphan = services.Blobs.Put(Encoding.UTF8.GetBytes("From: fixture@example.invalid\r\nSubject: Orphan\r\n\r\nOrphan body"));
        var orphanFile = services.Blobs.FullPath(orphan);
        Add("retained", retained);
        var deleteId = Add("delete-during-sweep", deleteMe);

        var location = Path.Combine(root, "archive");
        var bundleFile = Path.Combine(location, "fixture", FolderBundle.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(bundleFile)!);
        using (var bundle = FolderBundle.Create(bundleFile, "Fixture", "local")) bundle.Add(raw, false, "fixture");
        ArchiveManifest.Register(location, bundleFile);
        new ArchiveStore(services.Paths).Add(location);

        using var bundleLock = new SqliteConnection($"Data Source={bundleFile};Pooling=False");
        bundleLock.Open();
        using (var mode = bundleLock.CreateCommand())
        {
            mode.CommandText = "PRAGMA locking_mode=EXCLUSIVE; BEGIN EXCLUSIVE;";
            mode.ExecuteNonQuery();
        }

        var sweep = Task.Run(() => services.Database.SweepUnreferencedBlobs(discoverOrphans: true));
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200));
            using var probe = new SqliteConnection($"Data Source={services.Paths.DatabasePath};Pooling=False;Default Timeout=1");
            probe.Open();
            using (var pragma = probe.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout=100;";
                pragma.ExecuteNonQuery();
            }
            using (var tx = probe.BeginTransaction()) tx.Rollback();
            Assert.True(services.Database.DeleteMessageLocally(deleteId, permanently: false));
        }
        finally
        {
            using var unlock = bundleLock.CreateCommand();
            unlock.CommandText = "COMMIT; PRAGMA locking_mode=NORMAL;";
            try { unlock.ExecuteNonQuery(); } catch { }
            bundleLock.Close();
            await sweep.WaitAsync(TimeSpan.FromSeconds(10));
        }

        var result = await sweep;
        Assert.Null(result.Warning);
        Assert.Equal(1, result.Removed);
        Assert.False(File.Exists(orphanFile));
        Assert.Equal(0, result.Pending);
    }

    [Fact]
    public void LiveReferencedCandidatesAreDrainedAndRetiredImportSettingsAreSkipped()
    {
        var livePath = services.Blobs.Put(raw);
        var liveFile = services.Blobs.FullPath(livePath);
        var draftBytes = Encoding.UTF8.GetBytes("From: draft@example.invalid\r\nSubject: Draft\r\n\r\nDraft body");
        var draftPath = services.Blobs.Put(draftBytes);
        var draftFile = services.Blobs.FullPath(draftPath);
        var orphanBytes = Encoding.UTF8.GetBytes("From: orphan@example.invalid\r\nSubject: Orphan\r\n\r\nOrphan body");
        var orphanPath = services.Blobs.Put(orphanBytes);
        var orphanFile = services.Blobs.FullPath(orphanPath);

        Add("live-1", livePath);
        services.Database.SaveDraft("fixture", [], [], [], "Draft", "Body", rawPath: draftPath);
        // Simulate a retired multi-MB folder-storage.import:* setting that mentions the orphan SHA in audit text.
        var orphanSha = Path.GetFileNameWithoutExtension(orphanPath);
        services.Database.SetSetting("folder-storage.import:user:fixture", new string('x', 500_000) + orphanSha);

        // Pre-seed blob_gc_candidates with live referenced paths (as the pre-fix idle discovery did).
        using (var c = new SqliteConnection($"Data Source={services.Paths.DatabasePath};Pooling=False"))
        {
            c.Open();
            using var q = c.CreateCommand();
            q.CommandText = "INSERT OR IGNORE INTO blob_gc_candidates(raw_path,last_checked) VALUES($a,1),($b,1)";
            q.Parameters.AddWithValue("$a", livePath);
            q.Parameters.AddWithValue("$b", draftPath);
            q.ExecuteNonQuery();
        }

        var result = services.Database.SweepUnreferencedBlobs(discoverOrphans: true);
        Assert.Null(result.Warning);
        Assert.Equal(1, result.Removed);
        Assert.Equal(0, result.Pending);
        Assert.True(File.Exists(liveFile));
        Assert.True(File.Exists(draftFile));
        Assert.False(File.Exists(orphanFile));
    }

    [Fact]
    public async Task ConcurrentInsertDuringReadOnlyBundleScanPreventsDeletion()
    {
        var candidateBytes = Encoding.UTF8.GetBytes("From: race@example.invalid\r\nSubject: Race\r\n\r\nRace body");
        var candidatePath = services.Blobs.Put(candidateBytes);
        var candidateFile = services.Blobs.FullPath(candidatePath);

        var location = Path.Combine(root, "archive");
        var bundleFile = Path.Combine(location, "fixture", FolderBundle.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(bundleFile)!);
        using (var bundle = FolderBundle.Create(bundleFile, "Fixture", "local")) bundle.Add(raw, false, "fixture");
        ArchiveManifest.Register(location, bundleFile);
        new ArchiveStore(services.Paths).Add(location);

        using var bundleLock = new SqliteConnection($"Data Source={bundleFile};Pooling=False");
        bundleLock.Open();
        using (var mode = bundleLock.CreateCommand())
        {
            mode.CommandText = "PRAGMA locking_mode=EXCLUSIVE; BEGIN EXCLUSIVE;";
            mode.ExecuteNonQuery();
        }

        var sweep = Task.Run(() => services.Database.SweepUnreferencedBlobs(discoverOrphans: true));
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200));
            // Insert a message referencing candidatePath while Phase 1 is blocked on the external archive bundle.
            Add("late-arrival", candidatePath);
        }
        finally
        {
            using var unlock = bundleLock.CreateCommand();
            unlock.CommandText = "COMMIT; PRAGMA locking_mode=NORMAL;";
            try { unlock.ExecuteNonQuery(); } catch { }
            bundleLock.Close();
            await sweep.WaitAsync(TimeSpan.FromSeconds(10));
        }

        var result = await sweep;
        Assert.Null(result.Warning);
        Assert.Equal(0, result.Removed);
        Assert.True(File.Exists(candidateFile));
    }

    [Fact]
    public async Task SoftLocalDeleteDoesNotSweepExistingExternalCandidates()
    {
        var path = services.Blobs.Put(raw);
        var file = services.Blobs.FullPath(path);
        var keepBytes = Encoding.UTF8.GetBytes("From: soft@example.invalid\r\nSubject: Soft\r\n\r\nSoft body");
        var keepPath = services.Blobs.Put(keepBytes);
        var softId = Add("soft-delete", keepPath);

        services.Accounts.Save([
            new MailAccount("fixture", "custom", "fixture@example.invalid", "INBOX", LocalFolderKinds.Inbox,
                "pop.example.invalid", null, "secret", new KeepAliveSettings(), MailboxModes.LocalKeep,
                new IncomingServerSettings(MailProtocols.Pop3, "pop.example.invalid", 995))
        ]);

        // Seed an unchecked orphan candidate in blob_gc_candidates.
        using (var c = new SqliteConnection($"Data Source={services.Paths.DatabasePath};Pooling=False"))
        {
            c.Open();
            using var q = c.CreateCommand();
            q.CommandText = "INSERT OR IGNORE INTO blob_gc_candidates(raw_path,last_checked) VALUES($p,0)";
            q.Parameters.AddWithValue("$p", path);
            q.ExecuteNonQuery();
        }

        var summary = services.Database.GetMessage(softId)!.Summary;
        var outcome = await services.Sync.DeleteManyAsync([summary], permanently: false);
        Assert.Equal(1, outcome.Deleted);
        // Soft local delete moves to Deleted Items without deleting rows or running blob GC.
        Assert.True(File.Exists(file));
        Assert.Equal(LocalFolderKinds.Deleted, services.Database.GetMessage(softId)!.Summary.Folder);
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
}
