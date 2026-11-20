using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mailkeep.Infrastructure;

public sealed partial class BulkFolderMigration
{
    public sealed record PilotFolder(string Key, string Uuid, string File, long Messages, long FileBytes, long ModifiedTicks);
    public sealed record PilotPlan(string Root, string Confirmation, PilotFolder[] Folders, long DatabaseBytes)
    {
        public long Messages => Folders.Sum(f => f.Messages);
        public long RequiredFreeBytes => checked(DatabaseBytes + Folders.Sum(f => f.FileBytes) * 2 + 512L * 1024 * 1024);
    }
    public sealed record PilotOutput(PilotFolder Folder, string Sha256);
    public sealed record PilotState(string Id, string Phase, PilotPlan Plan, PilotOutput[] Outputs, string? Error = null);
    private string PilotWork => Path.Combine(paths.Root, "pilot-consolidation");
    private string PilotPending => Path.Combine(PilotWork, "pending");
    private string PilotComplete => Path.Combine(PilotWork, "complete");
    private string PilotStateFile => Path.Combine(PilotWork, "run.json");

    public PilotState? PilotStatus() => File.Exists(PilotStateFile)
        ? JsonSerializer.Deserialize<PilotState>(File.ReadAllText(PilotStateFile)) : null;

    public PilotPlan PreviewPilots()
    {
        using var c = Connect(paths.DatabasePath);
        var folders = new List<PilotFolder>();
        if (HasTable(c, "folder_storage_registry"))
        {
            using var q = c.CreateCommand();
            q.CommandText = """
                SELECT r.folder_key,r.uuid,r.created,(SELECT count(*) FROM messages m WHERE m.source='live' AND m.folder=r.folder_key)
                FROM folder_storage_registry r ORDER BY r.folder_key
                """;
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                var uuid = r.GetString(1);
                if (!Guid.TryParseExact(uuid, "N", out _)) throw new InvalidDataException("Invalid pilot UUID.");
                var file = Path.Combine(paths.Root, "local-folders", uuid, FolderBundle.FileName);
                var info = new FileInfo(file);
                if (!r.GetBoolean(2) || !info.Exists) throw new InvalidDataException("Pilot publication is incomplete.");
                folders.Add(new(r.GetString(0), uuid, file, r.GetInt64(3), info.Length, info.LastWriteTimeUtc.Ticks));
            }
        }
        var database = new FileInfo(paths.DatabasePath); var wal = new FileInfo(paths.DatabasePath + "-wal");
        var digest = FolderBundle.Sha256(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            paths.Root, Folders = folders, database.Length, DbTicks = database.LastWriteTimeUtc.Ticks,
            WalLength = wal.Exists ? wal.Length : 0L, WalTicks = wal.Exists ? wal.LastWriteTimeUtc.Ticks : 0L
        })));
        return new(paths.Root, digest, folders.ToArray(), database.Length);
    }

    /// <summary>Offline, checked upgrade of every pilot file. Folder bytes and identities are copied;
    /// one coordinator commit switches writers. Before that commit, recovery returns to the old writer.</summary>
    public PilotState ConsolidatePilots(string confirmation, Action<string>? checkpoint = null)
    {
        RequireDisposable(); CheckAvailable(paths);
        using var lease = RunLease();
        var previous = PilotStatus();
        if (previous?.Phase == "aborted") Directory.Move(PilotWork, PilotWork + ".aborted-" + previous.Id);
        else if (previous is not null) throw new InvalidOperationException("A pilot consolidation already exists; inspect or recover it.");
        using var guard = Connect(paths.DatabasePath, true);
        Scalar(guard, "PRAGMA synchronous=FULL; BEGIN IMMEDIATE");
        var active = true;
        PilotState? state = null;
        try
        {
            var plan = PreviewPilots();
            if (plan.Confirmation != confirmation) throw new InvalidOperationException("Mailbox changed; review a fresh pilot plan.");
            if (plan.Folders.Length == 0) throw new InvalidOperationException("No pilot folders remain.");
            if (new DriveInfo(Path.GetPathRoot(paths.Root)!).AvailableFreeSpace < plan.RequiredFreeBytes)
                throw new IOException("Insufficient space for a checked backup and pilot copies.");
            if (Convert.ToInt64(Scalar(guard, "SELECT count(*) FROM folder_storage_journal")) != 0
                || HasTable(guard, "bulk_journal") && Convert.ToInt64(Scalar(guard, "SELECT count(*) FROM bulk_journal")) != 0)
                throw new InvalidOperationException("Pending folder writes must be recovered before consolidation.");
            if (HasTable(guard, "bulk_bindings") && Convert.ToInt64(Scalar(guard,
                "SELECT count(*) FROM bulk_bindings WHERE source='live' AND folder IN (SELECT folder_key FROM folder_storage_registry)")) != 0)
                throw new InvalidDataException("Pilot and bulk writers overlap.");
            foreach (var folder in plan.Folders)
                if (File.Exists(BoundFile(folder.Uuid))) throw new IOException("A pilot publication destination already exists.");
            Directory.CreateDirectory(PilotWork);
            state = new(Guid.NewGuid().ToString("N"), "preparing", plan, []); SavePilot(state);
            DurableText(PilotPending, state.Id);
            var pilotMarker = Path.Combine(paths.Root, WritableFolderStorage.MarkerName);
            if (File.Exists(pilotMarker)) File.Copy(pilotMarker, Path.Combine(PilotWork, "original-pilot-marker.json"));
            using (var source = Connect(paths.DatabasePath))
            {
                var backupFile = Path.Combine(PilotWork, "original.db");
                // The write guard is already held. With no WAL frames there is
                // no checkpoint or writer that can change the database during
                // this sequential copy. Retain SQLite backup for a nonempty WAL.
                var wal = new FileInfo(paths.DatabasePath + "-wal");
                var sequentialBackup = Equals(Scalar(guard, "PRAGMA journal_mode"), "wal") && (!wal.Exists || wal.Length == 0);
                if (sequentialBackup)
                {
                    using var input = new FileStream(paths.DatabasePath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
                    using var output = new FileStream(backupFile, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, 1024 * 1024, FileOptions.SequentialScan);
                    input.CopyTo(output, 1024 * 1024);
                    output.Flush(flushToDisk: true);
                }
                using (var backup = Connect(Path.Combine(PilotWork, "original.db"), true))
                {
                    if (!sequentialBackup) source.BackupDatabase(backup);
                    if (!Equals(Scalar(backup, "PRAGMA quick_check"), "ok")) throw new InvalidDataException("Consolidation backup check failed.");
                }
                checkpoint?.Invoke(sequentialBackup ? "sequential-backup-checked" : "sqlite-backup-checked");
                var hierarchy = Hierarchy(source);
                foreach (var folder in plan.Folders)
                {
                    using var q = source.CreateCommand();
                    q.CommandText = $"SELECT id,{Snapshot(source)} FROM messages m WHERE source='live' AND folder=$folder ORDER BY id";
                    q.Parameters.AddWithValue("$folder", folder.Key);
                    var metadata = new Dictionary<long, string>();
                    using (var r = q.ExecuteReader()) while (r.Read()) metadata.Add(r.GetInt64(0), r.GetString(1));
                    var staging = PilotStaging(folder.Uuid);
                    FolderBundle.UpgradePilotCopy(folder.File, staging, new("live", "", folder.Key, hierarchy, true), metadata);
                    using (var copied = FolderBundle.Open(staging))
                        if (!copied.Verify().Ok || copied.Folder().Uuid != folder.Uuid || copied.List().Count != folder.Messages)
                            throw new InvalidDataException("Upgraded pilot verification failed.");
                    state = state with { Outputs = [.. state.Outputs, new(folder, FileDigest(staging))] }; SavePilot(state);
                    checkpoint?.Invoke("folder-verified");
                }
            }
            state = state with { Phase = "publishing" }; SavePilot(state);
            foreach (var output in state.Outputs)
            {
                var target = BoundFile(output.Folder.Uuid);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(PilotStaging(output.Folder.Uuid), target);
            }
            checkpoint?.Invoke("files-published");
            PrepareBindingSchema(guard);
            DropBulkTriggers(guard);
            WritableFolderStorage.RetirePilotWriter(guard);
            foreach (var output in state.Outputs)
                Scalar(guard, "INSERT INTO bulk_bindings(uuid,source,account,folder,created,logical) VALUES($uuid,'live','',$folder,1,1)",
                    ("$uuid", output.Folder.Uuid), ("$folder", output.Folder.Key));
            Scalar(guard, "INSERT INTO settings(key,value) VALUES('folder-storage.pilot-consolidated',$id)", ("$id", state.Id));
            InstallTriggers(guard);
            Scalar(guard, "COMMIT"); active = false;
            checkpoint?.Invoke("database-published");
            return CompletePilot(state);
        }
        catch (Exception error)
        {
            if (state is not null) SavePilot(state with { Error = error.Message });
            throw;
        }
        finally { if (active) Scalar(guard, "ROLLBACK"); }
    }

    public PilotState RecoverPilots()
    {
        RequireDisposable();
        using var lease = RunLease();
        var state = PilotStatus() ?? throw new InvalidOperationException("No pilot consolidation exists.");
        if (state.Phase is "complete" or "aborted" && !File.Exists(PilotPending)) return state;
        using var c = Connect(paths.DatabasePath, true);
        Scalar(c, "BEGIN IMMEDIATE");
        try
        {
            var committed = Equals(Scalar(c, "SELECT value FROM settings WHERE key='folder-storage.pilot-consolidated'"), state.Id);
            if (committed)
            {
                foreach (var output in state.Outputs)
                    if (!Equals(Scalar(c, "SELECT folder FROM bulk_bindings WHERE uuid=$uuid AND logical=1", ("$uuid", output.Folder.Uuid)), output.Folder.Key)
                        || FileDigest(BoundFile(output.Folder.Uuid)) != output.Sha256)
                        throw new InvalidDataException("Committed consolidation files or bindings differ; recovery refused.");
                return CompletePilot(state);
            }
            // The coordinator transaction did not commit. Remove only unpublished copies
            // from the active directory, retaining them and the original files as evidence.
            foreach (var output in state.Outputs)
            {
                var file = BoundFile(output.Folder.Uuid);
                if (!File.Exists(file)) continue;
                if (HasTable(c, "bulk_bindings") && Convert.ToInt64(Scalar(c, "SELECT count(*) FROM bulk_bindings WHERE uuid=$uuid", ("$uuid", output.Folder.Uuid))) != 0)
                    throw new InvalidDataException("An unpublished consolidation UUID is already bound.");
                if (FileDigest(file) != output.Sha256) throw new InvalidDataException("Unpublished consolidation copy changed.");
                var staging = PilotStaging(output.Folder.Uuid);
                Directory.CreateDirectory(Path.GetDirectoryName(staging)!); File.Move(file, staging);
            }
            var aborted = state with { Phase = "aborted" }; SavePilot(aborted); File.Delete(PilotPending); return aborted;
        }
        finally { Scalar(c, "ROLLBACK"); }
    }

    private PilotState CompletePilot(PilotState state)
    {
        var marker = Path.Combine(paths.Root, WritableFolderStorage.MarkerName);
        if (File.Exists(marker))
        {
            var legacy = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(marker))!.AsObject();
            legacy["enabled"] = false;
            legacy["consolidated"] = state.Id;
            DurableText(marker, legacy.ToJsonString());
        }
        DurableText(PilotComplete, state.Id);
        var complete = state with { Phase = "complete", Error = null }; SavePilot(complete);
        File.Delete(PilotPending); return complete;
    }

    private string PilotStaging(string uuid)
    {
        if (!Guid.TryParseExact(uuid, "N", out _)) throw new InvalidDataException("Invalid pilot UUID.");
        return Path.Combine(PilotWork, "files", uuid, FolderBundle.FileName);
    }

    private static string FileDigest(string file)
    {
        using var stream = File.OpenRead(file); return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void DropBulkTriggers(Microsoft.Data.Sqlite.SqliteConnection c)
    {
        using var q = c.CreateCommand(); q.CommandText = "SELECT name FROM sqlite_master WHERE type='trigger'";
        var names = new List<string>();
        using (var r = q.ExecuteReader()) while (r.Read()) if (r.GetString(0).StartsWith("bulk_", StringComparison.Ordinal)) names.Add(r.GetString(0));
        foreach (var name in names) Scalar(c, "DROP TRIGGER \"" + name.Replace("\"", "\"\"") + "\"");
    }

    private void SavePilot(PilotState state) => DurableText(PilotStateFile, JsonSerializer.Serialize(state, Json));
    private static void DurableText(string file, string value)
    {
        var temporary = file + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { var bytes = Encoding.UTF8.GetBytes(value); stream.Write(bytes); stream.Flush(true); }
        File.Move(temporary, file, true);
    }
}
