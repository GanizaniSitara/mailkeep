using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Infrastructure;

public sealed partial class BulkFolderMigration
{
    public int RebuildProjection()
    {
        CheckAvailable(paths);
        Flush();
        if (!Directory.Exists(BundleDirectory)) return 0;
        using var lease = OperationLease();
        var files = Directory.EnumerateFiles(BundleDirectory, FolderBundle.FileName, SearchOption.AllDirectories).Order().ToArray();
        var claims = new List<(string Source, string Account, string Folder, bool Logical)>();
        // Validate every authoritative file before changing any projection.
        foreach (var file in files)
        {
            using var bundle = FolderBundle.Open(file);
            if (!bundle.Verify().Ok) throw new InvalidDataException("Portable folder verification failed: " + file);
            if (bundle.Folder().Uuid != Path.GetFileName(Path.GetDirectoryName(file)))
                throw new InvalidDataException("Portable folder UUID differs: " + file);
            using var source = Connect(file);
            var logical = Convert.ToInt32(Scalar(source, "PRAGMA user_version")) == FolderBundle.LogicalFormatVersion;
            using var binding = source.CreateCommand(); binding.CommandText = "SELECT source,account,folder FROM binding";
            using var r = binding.ExecuteReader();
            if (!r.Read()) throw new InvalidDataException("Portable binding missing.");
            var claim = (Source: r.GetString(0), Account: r.GetString(1), Folder: r.GetString(2), Logical: logical);
            if (claims.Any(x => x.Source == claim.Source && x.Folder == claim.Folder
                && (x.Logical || claim.Logical || x.Account == claim.Account)))
                throw new InvalidDataException("More than one portable file claims the same folder location.");
            claims.Add(claim);
        }
        using var c = Connect(paths.DatabasePath, true);
        // Logical files carry the writer choice; recovery must not depend on retained pilot staging markers.
        if (claims.Any(claim => claim.Logical))
            Scalar(c, "INSERT OR IGNORE INTO settings(key,value) VALUES('folder-storage.unified','1')");
        var consolidated = Path.Combine(paths.Root, "pilot-consolidation", "complete");
        if (File.Exists(consolidated))
        {
            var id = File.ReadAllText(consolidated);
            if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid consolidation marker.");
            var existing = Scalar(c, "SELECT value FROM settings WHERE key='folder-storage.pilot-consolidated'");
            if (existing is string value && value != id) throw new InvalidDataException("Consolidation marker differs from the projection.");
            Scalar(c, "INSERT OR IGNORE INTO settings(key,value) VALUES('folder-storage.pilot-consolidated',$id)", ("$id", id));
        }
        InstallBindings(c, []);
        var columns = MessageColumns(c);
        using var tx = c.BeginTransaction();
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = "CREATE TEMP TABLE bulk_rebuild_deleted(id INTEGER,source TEXT,account TEXT,folder TEXT,source_key TEXT,deleted_at TEXT)";
        q.ExecuteNonQuery();
        var triggerSql = new List<string>();
        q.CommandText = "SELECT sql FROM sqlite_master WHERE type='trigger' AND name LIKE 'bulk_%' ORDER BY name";
        using (var r = q.ExecuteReader()) while (r.Read()) triggerSql.Add(r.GetString(0));
        q.CommandText = "SELECT name FROM sqlite_master WHERE type='trigger' AND name LIKE 'bulk_%'";
        var names = new List<string>(); using (var r = q.ExecuteReader()) while (r.Read()) names.Add(r.GetString(0));
        foreach (var name in names) { q.CommandText = "DROP TRIGGER \"" + name.Replace("\"", "\"\"") + "\""; q.ExecuteNonQuery(); }
        var count = 0; long maxId = 0;
        foreach (var file in files)
        {
            using var source = Connect(file);
            var logical = Convert.ToInt32(Scalar(source, "PRAGMA user_version")) == FolderBundle.LogicalFormatVersion;
            maxId = Math.Max(maxId, Convert.ToInt64(Scalar(source, "SELECT coalesce(max(message_id),0) FROM local_occurrences")));
            using (var deleted = source.CreateCommand())
            {
                deleted.CommandText = "SELECT o.message_id,json_extract(o.metadata,'$.source'),json_extract(o.metadata,'$.account'),json_extract(o.metadata,'$.folder'),json_extract(o.metadata,'$.source_key'),s.deleted_at FROM local_occurrences o JOIN state s USING(seq) WHERE s.deleted_at IS NOT NULL";
                using var rows = deleted.ExecuteReader();
                while (rows.Read())
                {
                    q.Parameters.Clear(); q.CommandText = "INSERT INTO bulk_rebuild_deleted VALUES($id,$source,$account,$folder,$key,$at)";
                    var fields = new[] { "$id", "$source", "$account", "$folder", "$key", "$at" };
                    for (var i = 0; i < fields.Length; i++) q.Parameters.AddWithValue(fields[i], rows.GetValue(i));
                    q.ExecuteNonQuery();
                }
            }
            using var query = source.CreateCommand();
            query.CommandText = "SELECT source,account,folder,hierarchy FROM binding";
            string sourceKind, account, folder, hierarchy;
            using (var r = query.ExecuteReader())
            {
                if (!r.Read()) throw new InvalidDataException("Portable binding missing.");
                sourceKind = r.GetString(0); account = r.GetString(1); folder = r.GetString(2); hierarchy = r.GetString(3);
            }
            if (logical && (sourceKind != "live" || account != "" || !HasColumn(source, "binding", "logical")
                || Convert.ToInt32(Scalar(source, "SELECT logical FROM binding")) != 1))
                throw new InvalidDataException("Logical folder binding is invalid.");
            if (logical && HasTable(source, "local_folder")
                && Scalar(source, "SELECT metadata FROM local_folder WHERE singleton=1") is string pilotMetadata)
                new WritableFolderStorage(paths, new RawBlobStore(paths)).RestorePortableRetirements(c, tx, folder, pilotMetadata);
            var uuid = Path.GetFileName(Path.GetDirectoryName(file)!);
            if (BoundFile(uuid) != file) throw new InvalidDataException("Portable path differs.");
            q.Parameters.Clear(); q.CommandText = "INSERT OR IGNORE INTO bulk_bindings(uuid,source,account,folder,logical) VALUES($uuid,$source,$account,$folder,$logical)";
            q.Parameters.AddWithValue("$uuid", uuid); q.Parameters.AddWithValue("$source", sourceKind);
            q.Parameters.AddWithValue("$account", account); q.Parameters.AddWithValue("$folder", folder); q.Parameters.AddWithValue("$logical", logical); q.ExecuteNonQuery();
            q.Parameters.Clear(); q.CommandText = "PRAGMA defer_foreign_keys=ON"; q.ExecuteNonQuery();
            using (var tree = JsonDocument.Parse(hierarchy))
                foreach (var table in new[] { "local_folders", "archive_folders" })
                    foreach (var row in tree.RootElement.GetProperty(table).EnumerateArray())
                    {
                        var fields = row.EnumerateObject().ToArray();
                        q.Parameters.Clear();
                        q.CommandText = $"INSERT OR IGNORE INTO {table}({string.Join(',', fields.Select(f => f.Name))}) VALUES({string.Join(',', fields.Select(f => "$" + f.Name))})";
                        foreach (var field in fields) q.Parameters.AddWithValue("$" + field.Name, Value(field.Value));
                        q.ExecuteNonQuery();
                    }
            q.Parameters.Clear();
            q.CommandText = """
                DELETE FROM messages_fts WHERE rowid IN (SELECT id FROM messages WHERE source=$source AND ($logical=1 OR account=$account) AND folder=$folder);
                DELETE FROM message_marks WHERE message_id IN (SELECT id FROM messages WHERE source=$source AND ($logical=1 OR account=$account) AND folder=$folder);
                DELETE FROM messages WHERE source=$source AND ($logical=1 OR account=$account) AND folder=$folder;
                """;
            q.Parameters.AddWithValue("$source", sourceKind); q.Parameters.AddWithValue("$account", account); q.Parameters.AddWithValue("$folder", folder); q.Parameters.AddWithValue("$logical", logical); q.ExecuteNonQuery();
            query.CommandText = "SELECT o.message_id,o.metadata,m.raw FROM local_occurrences o JOIN messages m USING(seq) JOIN state s USING(seq) WHERE s.deleted_at IS NULL ORDER BY o.message_id";
            using var records = query.ExecuteReader();
            while (records.Read())
            {
                using var json = JsonDocument.Parse(records.GetString(1)); var row = json.RootElement;
                var id = records.GetInt64(0);
                if (row.GetProperty("id").GetInt64() != id || row.GetProperty("source").GetString() != sourceKind
                    || (!logical && row.GetProperty("account").GetString() != account) || row.GetProperty("folder").GetString() != folder)
                    throw new InvalidDataException("Occurrence binding differs.");
                q.Parameters.Clear(); q.CommandText = "SELECT count(*) FROM messages WHERE id=$id"; q.Parameters.AddWithValue("$id", id);
                if (Convert.ToInt64(q.ExecuteScalar()) != 0) throw new InvalidDataException("Portable ID belongs to another folder.");
                var raw = (byte[])records.GetValue(2);
                WritableFolderStorage.ValidateImportOrigins(row, raw);
                var relative = RawBlobStore.RelativePath(FolderBundle.Sha256(raw));
                if (relative != row.GetProperty("raw_path").GetString()) throw new InvalidDataException("Portable MIME path differs.");
                q.Parameters.Clear(); q.CommandText = $"INSERT INTO messages({string.Join(',', columns.Select(n => "\"" + n + "\""))}) VALUES({string.Join(',', columns.Select(n => "$" + n))})";
                foreach (var column in columns) q.Parameters.AddWithValue("$" + column, Value(row.GetProperty(column)));
                q.ExecuteNonQuery();
                q.CommandText = "INSERT INTO messages_fts(rowid,subject,sender,recipients,body_text) VALUES($id,$subject,$sender,$recipients_json,$body_text)"; q.ExecuteNonQuery();
                new WritableFolderStorage(paths, new RawBlobStore(paths)).RestoreImportOrigins(c, tx, id, row);
                foreach (var mark in row.GetProperty("marks").EnumerateArray())
                {
                    q.Parameters.Clear(); q.CommandText = "INSERT INTO message_marks VALUES($id,$kind,$at)";
                    q.Parameters.AddWithValue("$id", id); q.Parameters.AddWithValue("$kind", mark.GetProperty("kind").GetString()!);
                    q.Parameters.AddWithValue("$at", mark.GetProperty("marked_at").GetString()!); q.ExecuteNonQuery();
                }
                count++;
            }
        }
        // A removed occurrence can be a move. Only IDs absent from every restored destination
        // become deletion markers; adopted originals must remain hidden after permanent deletion.
        q.Parameters.Clear(); q.CommandText = "INSERT OR IGNORE INTO local_deletions SELECT source,account,folder,source_key,deleted_at FROM bulk_rebuild_deleted d WHERE NOT EXISTS(SELECT 1 FROM messages m WHERE m.id=d.id); DROP TABLE bulk_rebuild_deleted";
        q.ExecuteNonQuery();
        q.CommandText = "UPDATE sqlite_sequence SET seq=max(seq,$max) WHERE name='messages'; INSERT INTO sqlite_sequence(name,seq) SELECT 'messages',$max WHERE NOT EXISTS(SELECT 1 FROM sqlite_sequence WHERE name='messages');";
        q.Parameters.AddWithValue("$max", maxId); q.ExecuteNonQuery();
        q.Parameters.Clear(); foreach (var sql in triggerSql) { q.CommandText = sql; q.ExecuteNonQuery(); }
        // Folder rows were restored with the tree triggers disabled. The next
        // flush must snapshot that restored tree, not the pre-rebuild defaults.
        q.CommandText = "UPDATE bulk_hierarchy SET metadata=$metadata,pending=1 WHERE singleton=1";
        q.Parameters.AddWithValue("$metadata", Hierarchy(c)); q.ExecuteNonQuery();
        // Existing catalog locations may predate this recovered projection.
        q.Parameters.Clear();
        q.CommandText = """
            UPDATE settings SET value=CAST(value AS INTEGER)+1 WHERE key='search.operational.change-sequence';
            INSERT INTO search_archive_changes
                SELECT id,(SELECT CAST(value AS INTEGER) FROM settings WHERE key='search.operational.change-sequence')
                FROM messages WHERE source='archive'
                ON CONFLICT(message_id) DO UPDATE SET revision=excluded.revision;
            """;
        q.ExecuteNonQuery();
        tx.Commit(); return count;
    }

    private static object Value(JsonElement value) => value.ValueKind switch
    { JsonValueKind.Null => DBNull.Value, JsonValueKind.Number => value.GetInt64(), _ => value.GetString()! };
}
