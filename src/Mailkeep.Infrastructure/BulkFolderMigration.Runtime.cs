using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Infrastructure;

public sealed partial class BulkFolderMigration
{
    private FileStream OperationLease()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try { return new(Path.Combine(BundleDirectory, ".operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            // A folder writer can spend up to SQLite's 30-second busy timeout
            // waiting for its file. Other writers must not fail after only five.
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 && clock.Elapsed < TimeSpan.FromSeconds(35))
            { Thread.Sleep(25); }
        }
    }

    private static void InstallBindings(SqliteConnection c, IEnumerable<Output> outputs)
    {
        PrepareBindingSchema(c);
        using var tx = c.BeginTransaction();
        foreach (var output in outputs)
        {
            using var insert = c.CreateCommand(); insert.Transaction = tx;
            insert.CommandText = "INSERT INTO bulk_bindings(uuid,source,account,folder) VALUES($uuid,$source,$account,$folder)";
            insert.Parameters.AddWithValue("$uuid", output.Uuid); insert.Parameters.AddWithValue("$source", output.Folder.Source);
            insert.Parameters.AddWithValue("$account", output.Folder.Account); insert.Parameters.AddWithValue("$folder", output.Folder.Path);
            insert.ExecuteNonQuery();
        }
        tx.Commit();
        InstallTriggers(c);
    }

    private static void PrepareBindingSchema(SqliteConnection c)
    {
        Scalar(c, """
            CREATE TABLE IF NOT EXISTS bulk_bindings(uuid TEXT PRIMARY KEY,source TEXT NOT NULL,account TEXT NOT NULL,folder TEXT NOT NULL,created INTEGER NOT NULL DEFAULT 1,logical INTEGER NOT NULL DEFAULT 0,UNIQUE(source,account,folder));
            CREATE TABLE IF NOT EXISTS bulk_journal(id INTEGER PRIMARY KEY AUTOINCREMENT,uuid TEXT NOT NULL,message_id INTEGER NOT NULL,metadata TEXT NOT NULL,removed INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS bulk_hierarchy(singleton INTEGER PRIMARY KEY CHECK(singleton=1),metadata TEXT NOT NULL,pending INTEGER NOT NULL);
            """);
        if (!HasColumn(c, "bulk_bindings", "logical"))
            Scalar(c, "ALTER TABLE bulk_bindings ADD COLUMN logical INTEGER NOT NULL DEFAULT 0");
        Scalar(c, "INSERT OR IGNORE INTO bulk_hierarchy VALUES(1,$metadata,0)", ("$metadata", Hierarchy(c)));
    }

    private static void InstallTriggers(SqliteConnection c)
    {
        string Match(string row) => BindingMatch(c, row);
        string Queue(string row, int removed) => $"INSERT INTO bulk_journal(uuid,message_id,metadata,removed) SELECT b.uuid,{row}.id,{Snapshot(c, row)},{removed} FROM bulk_bindings b WHERE {Match(row)};";
        var oldManaged = HasTable(c, "folder_storage_registry") ? " AND NEW.folder NOT IN (SELECT folder_key FROM folder_storage_registry)" : "";
        var local = "NEW.source='live' AND EXISTS(SELECT 1 FROM local_folders WHERE folder_key=NEW.folder AND kind NOT IN ('drafts','outbox'))" + oldManaged;
        var logicalEnabled = HasColumn(c, "bulk_bindings", "logical") && HasTable(c, "settings")
            && (Scalar(c, "SELECT value FROM settings WHERE key='folder-storage.pilot-consolidated'") is string
                || Equals(Scalar(c, "SELECT value FROM settings WHERE key='folder-storage.unified'"), "1"));
        var logicalEnrollment = logicalEnabled ? """
            INSERT OR IGNORE INTO bulk_bindings(uuid,source,account,folder,created,logical)
            SELECT lower(hex(randomblob(16))),'live','',NEW.folder,0,1
            WHERE NEW.source='live' AND NOT EXISTS(SELECT 1 FROM bulk_bindings WHERE source='live' AND folder=NEW.folder);
            """ : "";
        Scalar(c, $"""
            CREATE TRIGGER IF NOT EXISTS bulk_bind_insert BEFORE INSERT ON messages
            WHEN {local} AND NEW.raw_path IS NOT NULL
            BEGIN {logicalEnrollment} INSERT OR IGNORE INTO bulk_bindings(uuid,source,account,folder,created)
            SELECT lower(hex(randomblob(16))),NEW.source,NEW.account,NEW.folder,0 WHERE NOT EXISTS(SELECT 1 FROM bulk_bindings b WHERE {Match("NEW")}); END;
            CREATE TRIGGER IF NOT EXISTS bulk_bind_update BEFORE UPDATE ON messages
            WHEN NEW.raw_path IS NOT NULL AND (({local}) OR (NEW.source='archive' AND EXISTS(SELECT 1 FROM bulk_bindings b WHERE {Match("OLD")})))
            BEGIN {logicalEnrollment} INSERT OR IGNORE INTO bulk_bindings(uuid,source,account,folder,created)
            SELECT lower(hex(randomblob(16))),NEW.source,NEW.account,NEW.folder,0 WHERE NOT EXISTS(SELECT 1 FROM bulk_bindings b WHERE {Match("NEW")}); END;
            CREATE TRIGGER IF NOT EXISTS bulk_insert AFTER INSERT ON messages BEGIN {Queue("NEW", 0)} END;
            CREATE TRIGGER IF NOT EXISTS bulk_update AFTER UPDATE ON messages BEGIN
                INSERT INTO bulk_journal(uuid,message_id,metadata,removed) SELECT b.uuid,OLD.id,{Snapshot(c, "OLD")},1 FROM bulk_bindings b
                WHERE {Match("OLD")} AND (OLD.source<>NEW.source OR OLD.account<>NEW.account OR OLD.folder<>NEW.folder);
                {Queue("NEW", 0)}
            END;
            CREATE TRIGGER IF NOT EXISTS bulk_delete AFTER DELETE ON messages BEGIN {Queue("OLD", 1)} END;
            CREATE TRIGGER IF NOT EXISTS bulk_mark_insert AFTER INSERT ON message_marks BEGIN
                INSERT INTO bulk_journal(uuid,message_id,metadata,removed) SELECT b.uuid,m.id,{Snapshot(c)},0
                FROM messages m JOIN bulk_bindings b ON {Match("m")} WHERE m.id=NEW.message_id;
            END;
            CREATE TRIGGER IF NOT EXISTS bulk_mark_delete AFTER DELETE ON message_marks BEGIN
                INSERT INTO bulk_journal(uuid,message_id,metadata,removed) SELECT b.uuid,m.id,{Snapshot(c)},0
                FROM messages m JOIN bulk_bindings b ON {Match("m")} WHERE m.id=OLD.message_id;
            END;
            CREATE TRIGGER IF NOT EXISTS bulk_mime_guard BEFORE UPDATE ON messages
            WHEN EXISTS(SELECT 1 FROM bulk_bindings b WHERE {Match("OLD")}) AND (OLD.raw_path IS NOT NEW.raw_path OR OLD.id<>NEW.id)
            BEGIN SELECT RAISE(ABORT,'Portable MIME and message identity are immutable'); END;
            CREATE TRIGGER IF NOT EXISTS bulk_insert_guard BEFORE INSERT ON messages
            WHEN NEW.raw_path IS NULL AND EXISTS(SELECT 1 FROM bulk_bindings b WHERE {Match("NEW")})
            BEGIN SELECT RAISE(ABORT,'Portable messages require complete MIME bytes'); END;
            CREATE TRIGGER IF NOT EXISTS bulk_reimport_guard BEFORE INSERT ON messages
            WHEN NEW.source='archive' AND EXISTS(SELECT 1 FROM bulk_bindings b WHERE {Match("NEW")})
            BEGIN SELECT RAISE(IGNORE); END;
            """);
        if (logicalEnabled)
            Scalar(c, """
                CREATE TRIGGER IF NOT EXISTS bulk_logical_folder AFTER INSERT ON local_folders
                WHEN NEW.kind NOT IN ('drafts','outbox')
                BEGIN INSERT INTO bulk_bindings(uuid,source,account,folder,created,logical)
                VALUES(lower(hex(randomblob(16))),'live','',NEW.folder_key,0,1); END;
                CREATE TRIGGER IF NOT EXISTS bulk_local_import_guard BEFORE INSERT ON messages
                WHEN NEW.source='archive' AND EXISTS(SELECT 1 FROM local_folders f JOIN bulk_bindings b ON b.folder=f.folder_key
                    WHERE b.logical=1 AND b.source='live' AND f.origin='archive' AND f.origin_key=NEW.account||'|'||NEW.folder)
                BEGIN SELECT RAISE(IGNORE); END;
                """);
        var treeSql = "json_object('local_folders',json((SELECT json_group_array(json_object('folder_key',folder_key,'parent_key',parent_key,'name',name,'kind',kind,'sort_order',sort_order,'origin',origin,'origin_key',origin_key)) FROM local_folders)),"
            + "'archive_folders',json((SELECT json_group_array(json_object('source',source,'account',account,'folder',folder)) FROM archive_folders)))";
        foreach (var table in new[] { "local_folders", "archive_folders" })
            foreach (var action in new[] { "INSERT", "UPDATE", "DELETE" })
                Scalar(c, $"CREATE TRIGGER IF NOT EXISTS bulk_tree_{table}_{action} AFTER {action} ON {table} BEGIN UPDATE bulk_hierarchy SET metadata={treeSql},pending=1; END");
    }

    internal void CheckProjectionStorage(SqliteConnection connection)
    {
        if (!Directory.Exists(BundleDirectory))
            throw new DirectoryNotFoundException("Portable folder storage is missing; refusing the stale projection.");
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT uuid FROM bulk_bindings WHERE created=1";
        using var rows = query.ExecuteReader();
        while (rows.Read())
        {
            var file = BoundFile(rows.GetString(0));
            if (!File.Exists(file))
                throw new FileNotFoundException("A portable folder is missing; refusing the stale projection.", file);
        }
    }

    internal int Flush()
    {
        CheckAvailable(paths);
        if (!File.Exists(paths.DatabasePath)) return 0;
        // Match normal coordinator writes: a concurrent account sync may briefly own the database.
        using var c = Connect(paths.DatabasePath, true, timeoutSeconds: 5);
        if (!HasTable(c, "bulk_bindings")) return 0;
        if (!Directory.Exists(BundleDirectory))
            throw new DirectoryNotFoundException("Portable folder storage is missing; refusing the stale projection.");
        using var lease = OperationLease();
        using var tx = c.BeginTransaction();
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = "SELECT uuid,source,account,folder,created," + (HasColumn(c, "bulk_bindings", "logical") ? "logical" : "0") + " FROM bulk_bindings";
        var bindings = new List<(string Uuid, string Source, string Account, string Folder, bool Created, bool Logical)>();
        using (var r = q.ExecuteReader())
            while (r.Read()) bindings.Add((r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetBoolean(4),r.GetBoolean(5)));
        q.CommandText = "SELECT metadata FROM bulk_hierarchy WHERE singleton=1";
        var hierarchy = (string)q.ExecuteScalar()!;
        // Recovery unions these snapshots. Keep unbound folders everywhere, but a
        // bound folder needs only its own row and ancestors in its portable file.
        // This preserves the existing reader contract without copying every sibling.
        using var tree = JsonDocument.Parse(hierarchy);
        var localRows = tree.RootElement.GetProperty("local_folders").EnumerateArray().ToArray();
        var archiveRows = tree.RootElement.GetProperty("archive_folders").EnumerateArray().ToArray();
        var localByKey = localRows.ToDictionary(row => row.GetProperty("folder_key").GetString()!);
        var boundLocal = bindings.Where(b => b.Source == "live").Select(b => b.Folder).ToHashSet();
        var boundArchive = bindings.Where(b => b.Source == "archive").Select(b => (b.Account, b.Folder)).ToHashSet();
        string PortableHierarchy(string source, string account, string folder)
        {
            var ancestors = new HashSet<string>();
            string? key = source == "live" ? folder : null;
            while (key is not null && localByKey.TryGetValue(key, out var row) && ancestors.Add(key))
                key = row.GetProperty("parent_key").GetString();
            return JsonSerializer.Serialize(new
            {
                local_folders = localRows.Where(row => !boundLocal.Contains(row.GetProperty("folder_key").GetString()!)
                    || ancestors.Contains(row.GetProperty("folder_key").GetString()!)),
                archive_folders = archiveRows.Where(row =>
                {
                    var owner = row.GetProperty("account").GetString()!;
                    var path = row.GetProperty("folder").GetString()!;
                    return row.GetProperty("source").GetString() != "archive" || !boundArchive.Contains((owner, path))
                        || source == "archive" && owner == account
                            && (path == folder || folder.StartsWith(path + "/", StringComparison.Ordinal));
                })
            });
        }
        foreach (var binding in bindings)
        {
            var file = BoundFile(binding.Uuid);
            if (!File.Exists(file))
            {
                if (binding.Created) throw new FileNotFoundException("A portable folder is missing; refusing the stale projection.", file);
                FolderBundle.WriteBulk(file, binding.Uuid, new(binding.Source, binding.Account, binding.Folder,
                    PortableHierarchy(binding.Source, binding.Account, binding.Folder), binding.Logical), []);
            }
            if (!binding.Created)
            {
                using var portable = Connect(file);
                if (!Equals(Scalar(portable, "SELECT uuid FROM folder"), binding.Uuid)
                    || (Convert.ToInt32(Scalar(portable, "PRAGMA user_version")) == FolderBundle.LogicalFormatVersion) != binding.Logical
                    || !Equals(Scalar(portable, "SELECT source||char(10)||account||char(10)||folder FROM binding"), binding.Source + "\n" + binding.Account + "\n" + binding.Folder))
                    throw new InvalidDataException("Recovered binding differs.");
                q.Parameters.Clear(); q.CommandText = "UPDATE bulk_bindings SET created=1 WHERE uuid=$uuid";
                q.Parameters.AddWithValue("$uuid", binding.Uuid); q.ExecuteNonQuery(); q.Parameters.Clear();
            }
        }
        var applied = 0;
        while (true)
        {
            q.CommandText = "SELECT id,uuid,message_id,metadata,removed FROM bulk_journal ORDER BY id LIMIT 128";
            var rows = new List<(long Token, string Uuid, long Id, string Metadata, bool Removed)>();
            using (var r = q.ExecuteReader())
                while (r.Read()) rows.Add((r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetBoolean(4)));
            if (rows.Count == 0) break;
            foreach (var group in rows.GroupBy(r => r.Uuid))
            {
                using var bundle = FolderBundle.Open(BoundFile(group.Key), false);
                // Each occurrence keeps its ordered operations; the coordinator remains locked until all files commit.
                var chunk = new List<(string Token, long? Message, string Metadata, byte[]? Raw, bool Removed)>();
                long chunkBytes = 0;
                foreach (var row in group)
                {
                    using var metadata = JsonDocument.Parse(row.Metadata);
                    var relative = metadata.RootElement.GetProperty("raw_path").GetString();
                    byte[]? raw = null;
                    if (!row.Removed && !bundle.HasLocalOccurrence(row.Id))
                    {
                        raw = new RawBlobStore(paths).ReadBytes(relative!);
                        if (FolderBundle.Sha256(raw) != Path.GetFileNameWithoutExtension(relative))
                            throw new InvalidDataException("New portable occurrence MIME differs from its content address.");
                    }
                    chunk.Add(("bulk:" + row.Token, row.Id, row.Metadata, raw, row.Removed));
                    chunkBytes += (raw?.LongLength ?? 0) + row.Metadata.Length * 2L;
                    if (chunkBytes >= 16 * 1024 * 1024)
                    { bundle.ApplyLocalBatch(chunk); chunk.Clear(); chunkBytes = 0; }
                }
                if (chunk.Count != 0) bundle.ApplyLocalBatch(chunk);
            }
            q.Parameters.Clear(); q.CommandText = "DELETE FROM bulk_journal WHERE id<=$last";
            q.Parameters.AddWithValue("$last", rows[^1].Token); q.ExecuteNonQuery(); q.Parameters.Clear();
            applied += rows.Count;
        }
        q.CommandText = "SELECT pending FROM bulk_hierarchy WHERE singleton=1";
        var hierarchyPending = Convert.ToInt64(q.ExecuteScalar()) != 0;
        var scoped = Equals(Scalar(c, "SELECT value FROM settings WHERE key='folder-storage.scoped-hierarchy'"), "1");
        var archiveScoped = Equals(Scalar(c, "SELECT value FROM settings WHERE key='folder-storage.scoped-archive-hierarchy'"), "1");
        if (hierarchyPending || ((!scoped || !archiveScoped) && bindings.Count != 0))
        {
            foreach (var binding in bindings)
            {
                using var portable = Connect(BoundFile(binding.Uuid), true);
                var metadata = PortableHierarchy(binding.Source, binding.Account, binding.Folder);
                if (Equals(Scalar(portable, "SELECT hierarchy FROM binding"), metadata)) continue;
                Scalar(portable, "PRAGMA synchronous=FULL");
                Scalar(portable, "UPDATE binding SET hierarchy=$metadata", ("$metadata", metadata));
            }
            q.CommandText = "UPDATE bulk_hierarchy SET pending=0 WHERE singleton=1"; q.ExecuteNonQuery();
            Scalar(c, "INSERT OR REPLACE INTO settings(key,value) VALUES('folder-storage.scoped-hierarchy','1')");
            Scalar(c, "INSERT OR REPLACE INTO settings(key,value) VALUES('folder-storage.scoped-archive-hierarchy','1')");
        }
        tx.Commit(); return applied;
    }

    internal static bool HasColumn(SqliteConnection c, string table, string column)
    {
        using var q = c.CreateCommand(); q.CommandText = "SELECT 1 FROM pragma_table_info($table) WHERE name=$column";
        q.Parameters.AddWithValue("$table", table); q.Parameters.AddWithValue("$column", column);
        return q.ExecuteScalar() is not null;
    }

    internal static string BindingMatch(SqliteConnection c, string row)
        => $"b.source={row}.source AND b.folder={row}.folder AND "
            + (HasColumn(c, "bulk_bindings", "logical")
                ? $"(b.account={row}.account OR (b.logical=1 AND b.source='live'))"
                : $"b.account={row}.account");

    private string BoundFile(string uuid)
    {
        if (!Guid.TryParseExact(uuid, "N", out _)) throw new InvalidDataException("Invalid folder UUID.");
        return Path.Combine(BundleDirectory, uuid, FolderBundle.FileName);
    }

    public RunState Publish(Action<string>? checkpoint = null)
    {
        RequireDisposable();
        using var runLease = RunLease();
        var state = Status() ?? throw new InvalidOperationException("No bulk run exists.");
        if (state.Phase != "verified") throw new InvalidOperationException("Only a verified run may be published.");
        // Revalidate the frozen source before allowing publication after a separate invocation.
        using (var source = Connect(paths.DatabasePath))
        using (var candidate = Connect(Candidate))
        {
            VerifyUnchanged(source, candidate, CancellationToken.None);
            if (SchemaDigest(candidate) != state.SchemaDigest) throw new InvalidDataException("Candidate schema or migration triggers changed.");
        }
        foreach (var output in state.Outputs)
        {
            using var source = Connect(paths.DatabasePath);
            if (VerifyFolder(source, output.Folder, Path.Combine(Work, output.Uuid, FolderBundle.FileName), CancellationToken.None) != output.ContentDigest)
                throw new InvalidDataException("Verified output changed before publication.");
        }
        PreparePublication(state);
        return CompletePublication(state, checkpoint);
    }

    private void PreparePublication(RunState state)
    {
        // The persistent pending state keeps new clients out; SQLite exclusive access drains old clients.
        using (var source = Connect(paths.DatabasePath, true))
        {
            using var check = source.CreateCommand(); check.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            using (var r = check.ExecuteReader()) if (!r.Read() || r.GetInt32(0) != 0) throw new IOException("Mailbox is still open.");
            if (!Equals(Scalar(source, "PRAGMA journal_mode=DELETE"), "delete")) throw new IOException("Mailbox is still open.");
        }
        Save(state with { Phase = "publishing" });
    }

    private RunState CompletePublication(RunState state, Action<string>? checkpoint = null, bool verifiedFiles = false)
    {
        using (var candidate = Connect(File.Exists(Candidate) ? Candidate : paths.DatabasePath))
            if (SchemaDigest(candidate) != state.SchemaDigest) throw new InvalidDataException("Publication schema or migration triggers changed.");
        Directory.CreateDirectory(BundleDirectory);
        foreach (var output in state.Outputs)
        {
            var target = BoundFile(output.Uuid); var staging = Path.Combine(Work, output.Uuid, FolderBundle.FileName);
            if (!File.Exists(target))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(staging, target);
            }
            if (!verifiedFiles)
            {
                using var source = Connect(File.Exists(Rollback) ? Rollback : paths.DatabasePath);
                if (VerifyFolder(source, output.Folder, target, CancellationToken.None) != output.ContentDigest)
                    throw new InvalidDataException("Publication folder hash differs.");
            }
        }
        checkpoint?.Invoke("files-published");
        if (File.Exists(Candidate)) File.Replace(Candidate, paths.DatabasePath, Rollback);
        else if (!File.Exists(Rollback)) throw new InvalidDataException("Publication database state is incomplete.");
        checkpoint?.Invoke("database-published");
        using (var current = Connect(paths.DatabasePath))
            foreach (var output in state.Outputs)
                if (Convert.ToInt64(Scalar(current, "SELECT count(*) FROM bulk_bindings WHERE uuid=$uuid", ("$uuid", output.Uuid))) != 1)
                    throw new InvalidDataException("Published binding is missing.");
        var complete = state with { Phase = "complete" }; Save(complete); File.Delete(PendingFile); return complete;
    }

    public RunState Recover()
    {
        RequireDisposable();
        using var runLease = RunLease();
        var state = Status() ?? throw new InvalidOperationException("No bulk run exists.");
        if (state.Phase == "publishing") return CompletePublication(state);
        if (state.Phase is "complete" or "aborted") { File.Delete(PendingFile); return state; }
        // Staging never touched the original. Keep all evidence; clear only the startup block.
        var aborted = state with { Phase = "aborted" }; Save(aborted); File.Delete(PendingFile); return aborted;
    }
}
