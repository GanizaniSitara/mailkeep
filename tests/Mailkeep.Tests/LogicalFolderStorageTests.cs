using System.Text;
using System.Text.Json;
using Mailkeep.Infrastructure;
using Microsoft.Data.Sqlite;
using Mailkeep.Core;

namespace Mailkeep.Tests;

public sealed class LogicalFolderStorageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mailkeep-logical-" + Guid.NewGuid().ToString("N"));
    private readonly MailkeepServices services;
    public LogicalFolderStorageTests()
    {
        services = new(root);
        services.Database.Initialize();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection($"Data Source={services.Paths.DatabasePath};Pooling=False"); c.Open(); return c;
    }

    [Fact]
    public async Task CommonWriterWaitsForBriefCoordinatorLock()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=DELETE; BEGIN EXCLUSIVE";
        command.ExecuteNonQuery();
        using var started = new ManualResetEventSlim();
        var flush = Task.Run(() => { started.Set(); return services.Database.FolderStorage.Flush(); });
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        finally
        {
            command.CommandText = "COMMIT";
            command.ExecuteNonQuery();
        }
        Assert.Equal(0, await flush);
    }

    private long Add(string folder, string account, int number)
    {
        var raw = Encoding.UTF8.GetBytes($"From: test@example.invalid\r\nSubject: Message {number}\r\n\r\nBody {number}\r\n");
        return services.Database.Upsert(new("live", account, folder, "key:" + number, null, 0,
            "Message " + number, "test@example.invalid", "Test", [], DateTimeOffset.UnixEpoch,
            raw.Length, number % 2 == 0, 0, "Body " + number, null, services.Blobs.Put(raw)));
    }

    [Fact]
    public void AccountScopedPortableFileDoesNotReplaceTheMergedFolderView()
    {
        Add("inbox", "received", 1);
        var migration = new BulkFolderMigration(services.Paths);
        migration.Run(migration.Preview().Confirmation, publish: true);
        Assert.True(services.Database.FolderStorage.Enabled);
        Assert.Empty(services.Database.FolderStorage.Inspect().Folders);
    }

    private static List<Dictionary<string, object?>> Read(SqliteConnection c, string table)
    {
        using var q = c.CreateCommand(); q.CommandText = "SELECT * FROM " + table;
        using var r = q.ExecuteReader(); var rows = new List<Dictionary<string, object?>>();
        while (r.Read()) rows.Add(Enumerable.Range(0, r.FieldCount).ToDictionary(r.GetName, i => r.IsDBNull(i) ? null : r.GetValue(i)));
        return rows;
    }

    private string MakeLogical(string folder)
    {
        using var c = Open();
        var hierarchy = JsonSerializer.Serialize(new { local_folders = Read(c, "local_folders"), archive_folders = Read(c, "archive_folders") });
        var rows = Read(c, "messages").Where(r => (string)r["folder"]! == folder).Select(r =>
        {
            r["marks"] = Array.Empty<object>(); r["import_origins"] = null;
            var raw = File.ReadAllBytes(services.Blobs.FullPath((string)r["raw_path"]!));
            return new FolderBundle.BulkMessage((long)r["id"]!, JsonSerializer.Serialize(r), raw, FolderBundle.Sha256(raw));
        }).ToArray();
        var file = Path.Combine(root, "bound-folders", Guid.NewGuid().ToString("N"), FolderBundle.FileName);
        FolderBundle.WriteBulk(file, Path.GetFileName(Path.GetDirectoryName(file))!, new("live", "", folder, hierarchy, Logical: true), rows);
        return file;
    }

    [Fact]
    public void FiledDraftCanMoveBetweenConvertedFoldersWithoutChangingOrQueueingItsContents()
    {
        services.Identities.Save([new SendingIdentity("draft-fixture", null, "Fixture", "draft@example.invalid", null,
            true, "custom", new OutgoingServerSettings("127.0.0.1", 1), null, null, "fixture", SendingIdentityStates.Ready)]);
        var attachment = Path.Combine(root, "draft.txt");
        File.WriteAllText(attachment, "retained draft attachment");
        var draft = services.Send.SaveDraft("draft-fixture", ["to@example.invalid"], "Saved draft", "Plain body",
            ["cc@example.invalid"], ["bcc@example.invalid"], attachmentPaths: [attachment], bodyHtml: "<p>HTML body</p>");
        services.Database.MoveDrafts([draft.Id], LocalFolderKinds.Inbox);
        var folder = services.Database.CreateLocalFolder("Wednesday fixture", null);
        MakeLogical(LocalFolderKinds.Inbox);
        MakeLogical(folder.Key);
        services.Database.FolderStorage.RebuildProjection();
        // Reproduce an already-upgraded installation carrying the old migration guard.
        using (var c = Open())
        using (var q = c.CreateCommand())
        {
            q.CommandText = """
                CREATE TRIGGER IF NOT EXISTS bulk_logical_draft_guard BEFORE INSERT ON draft_folders
                WHEN EXISTS(SELECT 1 FROM bulk_bindings WHERE logical=1 AND source='live' AND folder=NEW.folder_key)
                BEGIN SELECT RAISE(ABORT,'Filed drafts require an outgoing-state migration'); END
                """;
            q.ExecuteNonQuery();
        }
        var restarted = new MailkeepServices(root);
        restarted.Database.Initialize();
        var original = JsonSerializer.Serialize(draft);
        for (var index = 0; index < 2; index++)
        {
            Assert.Equal(1, restarted.Database.MoveDrafts([draft.Id], folder.Key));
            Assert.Empty(restarted.Database.ListDraftsInFolder(LocalFolderKinds.Inbox));
            Assert.Equal(original, JsonSerializer.Serialize(Assert.Single(restarted.Database.ListDraftsInFolder(folder.Key))));
            Assert.Equal("retained draft attachment", File.ReadAllText(restarted.Database.ExtractOutgoingAttachment(draft.Id, 0)));
            Assert.Null(restarted.Database.ClaimNextOutgoing());
            Assert.Empty(restarted.Database.ListOutgoing(OutgoingStates.Outbox));
            Assert.Throws<InvalidOperationException>(() => restarted.Database.DeleteLocalFolder(folder.Key));
            Assert.Contains(new FolderArchive(restarted.Paths).Move(folder.Key, Path.Combine(root, "archive"), false).Plan.Refusals,
                reason => reason.Contains("draft"));
            Assert.Equal(1, restarted.Database.MoveDrafts([draft.Id], LocalFolderKinds.Inbox));
        }
        Assert.Throws<InvalidOperationException>(() => restarted.Database.MoveDrafts([draft.Id], LocalFolderKinds.Outbox));
        Assert.Equal(1, restarted.Database.MoveDrafts([draft.Id], LocalFolderKinds.Drafts));
        Assert.Equal(original, JsonSerializer.Serialize(restarted.Database.GetOutgoing(draft.Id)));
    }

    [Fact]
    public void DeletingEmptyFolderWritesOnlyItsPortableHierarchyAndSurvivesLostProjection()
    {
        var folders = Enumerable.Range(0, 256)
            .Select(i => services.Database.CreateLocalFolder("Delete scale " + i, null)).ToArray();
        var files = folders.Select(folder => MakeLogical(folder.Key)).ToArray();
        services.Database.FolderStorage.RebuildProjection();
        // Existing full snapshots are narrowed once, before timing routine changes.
        services.Database.FolderStorage.Flush();
        var before = files.Select(file => System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))).ToArray();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        services.Database.DeleteLocalFolder(folders[0].Key);
        Console.WriteLine($"FOLDER DELETE files={files.Length} elapsed_ms={clock.ElapsedMilliseconds}");
        for (var i = 0; i < files.Length; i++)
        {
            var file = files[i];
            if (i != 0) Assert.Equal(before[i], System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)));
            using var c = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False");
            c.Open(); using var q = c.CreateCommand();
            q.CommandText = "SELECT hierarchy FROM binding";
            using var tree = JsonDocument.Parse((string)q.ExecuteScalar()!);
            Assert.DoesNotContain(tree.RootElement.GetProperty("local_folders").EnumerateArray(),
                row => row.GetProperty("folder_key").GetString() == folders[0].Key);
        }
        File.Delete(services.Paths.DatabasePath);
        var reopened = new MailkeepServices(root); reopened.Database.Initialize();
        Assert.Equal(0, reopened.Database.FolderStorage.RebuildProjection());
        Assert.DoesNotContain(reopened.Database.ListLocalFolders(), folder => folder.Key == folders[0].Key);
        Assert.All(folders.Skip(1), expected => Assert.Contains(reopened.Database.ListLocalFolders(), folder => folder.Key == expected.Key));
        reopened.Database.FolderStorage.Flush();
        File.Delete(services.Paths.DatabasePath);
        var recoveredAgain = new MailkeepServices(root); recoveredAgain.Database.Initialize();
        recoveredAgain.Database.FolderStorage.RebuildProjection();
        Assert.All(folders.Skip(1), expected => Assert.Contains(recoveredAgain.Database.ListLocalFolders(), folder => folder.Key == expected.Key));
    }

    [Fact]
    public void FailedHierarchyUpdateRemainsPendingAndCanBeRetried()
    {
        var folders = Enumerable.Range(0, 8)
            .Select(i => services.Database.CreateLocalFolder("Retry hierarchy " + i, null)).ToArray();
        var files = folders.Select(folder => MakeLogical(folder.Key)).ToArray();
        services.Database.FolderStorage.RebuildProjection();
        services.Database.FolderStorage.Flush();
        using (var locked = new SqliteConnection($"Data Source={files[0]};Pooling=False"))
        {
            locked.Open(); using var q = locked.CreateCommand();
            q.CommandText = "BEGIN IMMEDIATE"; q.ExecuteNonQuery();
            Assert.Throws<SqliteException>(() => services.Database.DeleteLocalFolder(folders[0].Key));
            using var c = Open(); using var pending = c.CreateCommand();
            pending.CommandText = "SELECT pending FROM bulk_hierarchy";
            Assert.Equal(1L, pending.ExecuteScalar());
            q.CommandText = "ROLLBACK"; q.ExecuteNonQuery();
        }
        services.Database.FolderStorage.Flush();
        foreach (var file in files)
        {
            using var c = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False");
            c.Open(); using var q = c.CreateCommand();
            q.CommandText = "SELECT hierarchy FROM binding";
            Assert.DoesNotContain(folders[0].Key, (string)q.ExecuteScalar()!);
        }
        services.Database.FolderStorage.RebuildProjection();
        Assert.DoesNotContain(services.Database.ListLocalFolders(), folder => folder.Key == folders[0].Key);
    }

    [Fact]
    public void ScopedHierarchyRetainsAncestorsUnboundFoldersAndUnrelatedLockedFiles()
    {
        var parent = services.Database.CreateLocalFolder("Parent", null);
        var child = services.Database.CreateLocalFolder("Child", parent.Key);
        var sibling = services.Database.CreateLocalFolder("Sibling", null);
        var unbound = services.Database.CreateLocalFolder("Unbound", null);
        var parentFile = MakeLogical(parent.Key);
        var childFile = MakeLogical(child.Key);
        var siblingFile = MakeLogical(sibling.Key);
        services.Database.FolderStorage.RebuildProjection();
        services.Database.FolderStorage.Flush();
        var before = File.ReadAllBytes(siblingFile);
        using (var locked = new SqliteConnection($"Data Source={siblingFile};Pooling=False"))
        {
            locked.Open(); using var q = locked.CreateCommand();
            q.CommandText = "BEGIN IMMEDIATE"; q.ExecuteNonQuery();
            services.Database.RenameLocalFolder(parent.Key, "Renamed parent");
            using (var portable = new SqliteConnection($"Data Source={childFile};Mode=ReadOnly;Pooling=False"))
            {
                portable.Open(); using var hierarchy = portable.CreateCommand(); hierarchy.CommandText = "SELECT hierarchy FROM binding";
                var metadata = (string)hierarchy.ExecuteScalar()!;
                Assert.Contains("Renamed parent", metadata);
                Assert.DoesNotContain(sibling.Key, metadata);
            }
            services.Database.DeleteLocalFolder(child.Key);
            q.CommandText = "ROLLBACK"; q.ExecuteNonQuery();
        }
        Assert.Equal(before, File.ReadAllBytes(siblingFile));
        using (var portable = new SqliteConnection($"Data Source={parentFile};Mode=ReadOnly;Pooling=False"))
        {
            portable.Open(); using var q = portable.CreateCommand(); q.CommandText = "SELECT hierarchy FROM binding";
            var metadata = (string)q.ExecuteScalar()!;
            Assert.Contains("Renamed parent", metadata);
            Assert.Contains(unbound.Key, metadata);
            Assert.DoesNotContain(child.Key, metadata);
        }
        File.Delete(services.Paths.DatabasePath);
        var reopened = new MailkeepServices(root); reopened.Database.Initialize();
        reopened.Database.FolderStorage.RebuildProjection();
        var restored = reopened.Database.ListLocalFolders();
        Assert.Contains(restored, folder => folder.Key == parent.Key && folder.Name == "Renamed parent");
        Assert.Contains(restored, folder => folder.Key == sibling.Key);
        Assert.Contains(restored, folder => folder.Key == unbound.Key);
        Assert.DoesNotContain(restored, folder => folder.Key == child.Key);
    }

    [Fact]
    public void UnifiedRuntimeIgnoresOldMarkersAndFreshRebuildKeepsOneWriter()
    {
        var folder = services.Database.CreateLocalFolder("Unified", null);
        var id = Add(folder.Key, "account-one", 1);
        MakeLogical(folder.Key);
        Assert.Equal(1, services.Database.FolderStorage.RebuildProjection());
        File.WriteAllText(Path.Combine(root, WritableFolderStorage.MarkerName), "retired-marker-is-not-runtime-state");
        File.Delete(services.Paths.DatabasePath);
        var reopened = new MailkeepServices(root); reopened.Database.Initialize();
        Assert.Equal(1, reopened.Database.FolderStorage.RebuildProjection());
        Assert.True(reopened.Database.FolderStorage.Enabled);
        Assert.Equal(id, reopened.Database.GetMessage(id)!.Summary.Id);
        var added = reopened.Database.CreateLocalFolder("New after recovery", null);
        Add(added.Key, "account-two", 2);
        Add(added.Key, "account-three", 3);
        Assert.Single(reopened.Database.FolderStorage.Inspect().Folders.Where(f => f.Key == added.Key));
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='trigger' AND name GLOB 'fs_*'";
        Assert.Equal(0L, q.ExecuteScalar());
    }

    [Fact]
    public void OneFolderKeepsMultipleAccountsThroughWritesMovesAndRebuild()
    {
        var folder = services.Database.CreateLocalFolder("Wednesday", null);
        var first = Add(folder.Key, "account-one", 1);
        var second = Add(folder.Key, "account-two", 2);
        var file = MakeLogical(folder.Key);
        var migration = new BulkFolderMigration(services.Paths);
        Assert.Equal(2, migration.RebuildProjection());
        var third = Add(folder.Key, "account-three", 3);
        services.Database.FolderStorage.Flush();
        using (var c = Open())
        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT count(*) FROM bulk_bindings";
            Assert.Equal(1L, q.ExecuteScalar());
        }
        Assert.Equal(3, migration.RebuildProjection());
        using (var c = Open())
            Assert.Equal(new[] { "account-one", "account-two", "account-three" }, Read(c, "messages").OrderBy(r => r["id"]).Select(r => (string)r["account"]!).ToArray());
        Assert.Equal(1, services.Database.MoveMessage(first, "inbox"));
        Assert.Equal(3, migration.RebuildProjection());
        Assert.Equal("inbox", services.Database.GetMessage(first)!.Summary.Folder);
        Assert.Equal(folder.Key, services.Database.GetMessage(second)!.Summary.Folder);
        Assert.Equal(folder.Key, services.Database.GetMessage(third)!.Summary.Folder);
        using var bundle = FolderBundle.Open(file);
        Assert.True(bundle.Verify().Ok);
        Assert.Equal(2, bundle.List().Count);
    }

    [Fact]
    public void EmptyLogicalFolderSurvivesAndAcceptsItsFirstAccount()
    {
        var folder = services.Database.CreateLocalFolder("Empty", null);
        MakeLogical(folder.Key);
        var migration = new BulkFolderMigration(services.Paths);
        Assert.Equal(0, migration.RebuildProjection());
        var id = Add(folder.Key, "first-account", 4);
        services.Database.FolderStorage.Flush();
        Assert.Equal(1, migration.RebuildProjection());
        Assert.Equal(folder.Key, services.Database.GetMessage(id)!.Summary.Folder);
    }

    [Fact]
    public void PilotUpgradePreservesRemovedBytesAndHistoryAndRejectsStaleMetadata()
    {
        var folder = services.Database.CreateLocalFolder("Pilot Wednesday", null);
        var first = Add(folder.Key, "account-one", 1);
        var second = Add(folder.Key, "account-two", 2);
        var registration = Mailkeep.TestFixtures.PortableStorageFixture.SeedLegacy(services, folder.Key).Single();
        Assert.Equal(1, services.Database.MoveMessage(first, "inbox"));
        Mailkeep.TestFixtures.PortableStorageFixture.RemoveLegacyOccurrence(registration.File, first);
        services.Database.FolderStorage.Flush();
        using var c = Open();
        var hierarchy = JsonSerializer.Serialize(new { local_folders = Read(c, "local_folders"), archive_folders = Read(c, "archive_folders") });
        var active = Read(c, "messages").Where(r => (string)r["folder"]! == folder.Key).ToDictionary(r => (long)r["id"]!, r =>
        {
            r["marks"] = Array.Empty<object>(); r["import_origins"] = null;
            return JsonSerializer.Serialize(r);
        });
        var upgraded = Path.Combine(root, "upgrade", FolderBundle.FileName);
        FolderBundle.UpgradePilotCopy(registration.File, upgraded, new("live", "", folder.Key, hierarchy, true), active);
        using (var before = FolderBundle.Open(registration.File))
        using (var after = FolderBundle.Open(upgraded))
        {
            Assert.True(after.Verify().Ok);
            Assert.Equal("mailkeep-folder/1", before.Folder().Format);
            Assert.Equal("mailkeep-folder/3", after.Folder().Format);
            Assert.Equal(before.Folder().Uuid, after.Folder().Uuid);
            Assert.Equal(before.Verify().HashesChecked, after.Verify().HashesChecked);
            Assert.Equal(1, after.Verify().Deleted);
            Assert.Single(after.List());
        }
        active[second] = active[second].Replace("Body 2", "Wrong body");
        Assert.Throws<InvalidDataException>(() => FolderBundle.UpgradePilotCopy(registration.File,
            Path.Combine(root, "rejected", FolderBundle.FileName), new("live", "", folder.Key, hierarchy, true), active));
    }

    [Fact]
    public void PilotConsolidationRetiresTheOldWriterAndKeepsOneFileForNewAccounts()
    {
        var folder = services.Database.CreateLocalFolder("Mixed accounts", null);
        var empty = services.Database.CreateLocalFolder("Empty child", folder.Key);
        var id = Add(folder.Key, "account-one", 1);
        Add(folder.Key, "account-two", 2);
        Mailkeep.TestFixtures.PortableStorageFixture.SeedLegacy(services);
        var migration = new BulkFolderMigration(services.Paths);
        var plan = migration.PreviewPilots();
        Assert.Equal(2, plan.Folders.Length);
        Assert.Equal(2, plan.Messages);
        var state = migration.ConsolidatePilots(plan.Confirmation);
        Assert.Equal("complete", state.Phase);
        Assert.True(services.Database.FolderStorage.Enabled);
        Assert.True(services.Database.FolderStorage.Inspect().Enabled);
        Assert.Contains(services.Database.FolderStorage.Inspect().Folders, f => f.Key == folder.Key);
        using (var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, WritableFolderStorage.MarkerName))))
            Assert.False(marker.RootElement.GetProperty("enabled").GetBoolean());
        using (var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "pilot-consolidation", "original-pilot-marker.json"))))
            Assert.True(marker.RootElement.GetProperty("enabled").GetBoolean());
        Assert.All(plan.Folders, f => Assert.True(File.Exists(f.File)));
        Assert.Equal(2, migration.RebuildProjection());
        Add(folder.Key, "account-three", 3);
        services.Database.FolderStorage.Flush();
        var newFolder = services.Database.CreateLocalFolder("After consolidation", null);
        Add(newFolder.Key, "account-four", 4);
        Add(newFolder.Key, "account-five", 5);
        services.Database.FolderStorage.Flush();
        using (var c = Open())
        {
            Assert.Empty(Read(c, "folder_storage_registry"));
            Assert.Equal(3, Read(c, "bulk_bindings").Count);
            using var q = c.CreateCommand(); q.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='trigger' AND name GLOB 'fs_*'";
            Assert.Equal(0L, q.ExecuteScalar());
        }
        var reopened = new MailkeepServices(root); reopened.Database.Initialize();
        Assert.True(reopened.Database.FolderStorage.Enabled);
        Assert.Equal(5, migration.RebuildProjection());
        Assert.Equal(folder.Key, reopened.Database.GetMessage(id)!.Summary.Folder);
        Assert.Contains(reopened.Database.ListLocalFolders(), f => f.Key == empty.Key);
        Assert.Equal("complete", migration.RecoverPilots().Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PilotBackupIsCheckedUnderWriteLockAndIncludesUncheckpointedWal(bool uncheckpointed)
    {
        var folder = services.Database.CreateLocalFolder("Backup fixture", null);
        var id = Add(folder.Key, "local:one", 1);
        services.Database.FolderStorage.Flush();
        Mailkeep.TestFixtures.PortableStorageFixture.SeedLegacy(services);
        using var keptOpen = Open();
        using (var q = keptOpen.CreateCommand())
        {
            q.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)"; q.ExecuteNonQuery();
            if (uncheckpointed)
            {
                q.CommandText = "PRAGMA wal_autocheckpoint=0; INSERT INTO settings(key,value) VALUES('backup-fixture','in-wal')";
                q.ExecuteNonQuery();
                Assert.True(new FileInfo(services.Paths.DatabasePath + "-wal").Length > 0);
            }
        }
        var migration = new BulkFolderMigration(services.Paths);
        var checkedBackup = false;
        migration.ConsolidatePilots(migration.PreviewPilots().Confirmation, phase =>
        {
            if (phase is not ("sequential-backup-checked" or "sqlite-backup-checked")) return;
            Assert.Equal(uncheckpointed ? "sqlite-backup-checked" : "sequential-backup-checked", phase);
            var file = Path.Combine(root, "pilot-consolidation", "original.db");
            using var backup = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False");
            backup.Open();
            using var q = backup.CreateCommand();
            q.CommandText = "PRAGMA quick_check"; Assert.Equal("ok", q.ExecuteScalar());
            q.CommandText = "SELECT id FROM messages"; Assert.Equal(id, q.ExecuteScalar());
            if (uncheckpointed)
            {
                q.CommandText = "SELECT value FROM settings WHERE key='backup-fixture'";
                Assert.Equal("in-wal", q.ExecuteScalar());
            }
            else
            {
                using var source = new FileStream(services.Paths.DatabasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var saved = File.OpenRead(file);
                Assert.Equal(System.Security.Cryptography.SHA256.HashData(source), System.Security.Cryptography.SHA256.HashData(saved));
            }
            using var contender = new SqliteConnection($"Data Source={services.Paths.DatabasePath};Pooling=False;Default Timeout=1");
            contender.Open(); using var write = contender.CreateCommand();
            write.CommandText = "INSERT INTO settings(key,value) VALUES('must-not-write','blocked')";
            Assert.Equal(5, Assert.Throws<SqliteException>(() => write.ExecuteNonQuery()).SqliteErrorCode);
            checkedBackup = true;
        });
        Assert.True(checkedBackup);
    }

    [Theory]
    [InlineData("folder-verified", false)]
    [InlineData("files-published", false)]
    [InlineData("database-published", true)]
    public void InterruptedPilotConsolidationRecoversExactlyOneWriter(string checkpoint, bool committed)
    {
        var folder = services.Database.CreateLocalFolder("Interrupted", null);
        var id = Add(folder.Key, "account-one", 1);
        Mailkeep.TestFixtures.PortableStorageFixture.SeedLegacy(services);
        var migration = new BulkFolderMigration(services.Paths);
        Assert.Throws<IOException>(() => migration.ConsolidatePilots(migration.PreviewPilots().Confirmation,
            at => { if (at == checkpoint) throw new IOException("fixture interruption"); }));
        Assert.Throws<IOException>(() => new MailkeepServices(root));
        Assert.Equal(committed ? "complete" : "aborted", migration.RecoverPilots().Phase);
        if (!committed)
        {
            Assert.Throws<InvalidDataException>(() => new MailkeepServices(root).Database.Initialize());
            migration.ConsolidatePilots(migration.PreviewPilots().Confirmation);
        }
        var reopened = new MailkeepServices(root); reopened.Database.Initialize();
        Assert.True(reopened.Database.FolderStorage.Enabled);
        Assert.Equal(folder.Key, reopened.Database.GetMessage(id)!.Summary.Folder);
        Assert.Equal(1, reopened.Database.FolderStorage.RebuildProjection());
        Assert.Equal(folder.Key, reopened.Database.GetMessage(id)!.Summary.Folder);
        using var c = Open();
        Assert.Empty(Read(c, "folder_storage_registry"));
        Assert.Single(Read(c, "bulk_bindings"));
    }

    [Fact]
    public void AbortedUpgradeCanBeRetriedAndAnEmptyConsolidatedStoreRebuilds()
    {
        services.Database.CreateLocalFolder("Empty", null);
        Mailkeep.TestFixtures.PortableStorageFixture.SeedLegacy(services);
        var migration = new BulkFolderMigration(services.Paths);
        Assert.Throws<IOException>(() => migration.ConsolidatePilots(migration.PreviewPilots().Confirmation,
            at => { if (at == "files-published") throw new IOException("fixture interruption"); }));
        Assert.Equal("aborted", migration.RecoverPilots().Phase);
        Assert.Equal("complete", migration.ConsolidatePilots(migration.PreviewPilots().Confirmation).Phase);
        Assert.Equal(0, services.Database.FolderStorage.RebuildProjection());
    }

    [Fact]
    public void ChangedCommittedCopyKeepsRecoveryBlocked()
    {
        var folder = services.Database.CreateLocalFolder("Tampered", null);
        Add(folder.Key, "local:one", 1);
        Mailkeep.TestFixtures.PortableStorageFixture.SeedLegacy(services);
        var migration = new BulkFolderMigration(services.Paths);
        Assert.Throws<IOException>(() => migration.ConsolidatePilots(migration.PreviewPilots().Confirmation,
            at => { if (at == "database-published") throw new IOException("fixture interruption"); }));
        var output = migration.PilotStatus()!.Outputs.Single();
        var file = Path.Combine(root, "bound-folders", output.Folder.Uuid, FolderBundle.FileName);
        using (var c = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            c.Open(); using var q = c.CreateCommand(); q.CommandText = "UPDATE state SET is_read=1-is_read"; q.ExecuteNonQuery();
        }
        Assert.Throws<InvalidDataException>(() => migration.RecoverPilots());
        Assert.Throws<IOException>(() => new MailkeepServices(root));
    }

    [Fact]
    public void ConsolidatedFolderCanBeArchivedRestoredAndArchivedAgain()
    {
        var folder = services.Database.CreateLocalFolder("Archive round trip", null);
        var first = Add(folder.Key, "local:one", 1);
        Add(folder.Key, "local:two", 2);
        var raw = File.ReadAllBytes(services.Database.GetMessage(first)!.RawPath!);
        Mailkeep.TestFixtures.PortableStorageFixture.SeedLegacy(services);
        var migration = new BulkFolderMigration(services.Paths);
        migration.ConsolidatePilots(migration.PreviewPilots().Confirmation);
        var location = Path.Combine(root, "detached"); Directory.CreateDirectory(location);
        Assert.Equal(2, new FolderArchive(services.Paths).Move(folder.Key, location, true, backup: false).Removed);
        var restored = new ArchiveMoveBack(services).Run(location, "Archive round trip", execute: true);
        var key = restored.FolderKeys.Single();
        Assert.Contains(services.Database.FolderStorage.Inspect().Folders, f => f.Key == key);
        var messages = services.Database.ListMessages().Where(m => m.Folder == key).ToArray();
        Assert.Equal(2, messages.Length);
        Assert.Equal(raw, File.ReadAllBytes(services.Database.GetMessage(messages.Single(m => m.Subject == "Message 1").Id)!.RawPath!));
        Assert.Equal(2, migration.RebuildProjection());
        Assert.Equal(2, new FolderArchive(services.Paths).Move(key, location, true, backup: false).Removed);
        Assert.Empty(services.Database.ListMessages());
        Assert.Equal(0, services.Database.FolderStorage.RebuildProjection());
    }

    [Fact]
    public void InconsistentPilotReadStateIsRefusedBeforeChangingWriters()
    {
        var folder = services.Database.CreateLocalFolder("Read state", null);
        Add(folder.Key, "local:one", 1);
        var registration = Mailkeep.TestFixtures.PortableStorageFixture.SeedLegacy(services, folder.Key).Single();
        using (var c = new SqliteConnection($"Data Source={registration.File};Pooling=False"))
        {
            c.Open(); using var q = c.CreateCommand(); q.CommandText = "UPDATE state SET is_read=1-is_read"; q.ExecuteNonQuery();
        }
        var migration = new BulkFolderMigration(services.Paths);
        Assert.Throws<InvalidDataException>(() => migration.ConsolidatePilots(migration.PreviewPilots().Confirmation));
        Assert.Equal("aborted", migration.RecoverPilots().Phase);
        Assert.False(services.Database.FolderStorage.Enabled);
        using var coordinator = Open();
        Assert.Single(Read(coordinator, "folder_storage_registry"));
    }

    public void Dispose() => Directory.Delete(root, true);
}
