using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mailkeep.Core;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Infrastructure;

/// <summary>Merge a misplaced imported root folder into an existing nested folder of the same name.</summary>
public sealed class ImportedFolderMerge(AppPaths paths)
{
    public sealed record Plan(string FromKey, string IntoKey, string FromPath, string IntoPath,
        string Account, long[] MessageIds, Dictionary<long, long> Duplicates, long Bytes, string Confirmation);
    public sealed record Result(Plan Plan, string Backup, int Moved, int DuplicateOccurrencesRemoved);

    public Plan Preview(string fromKey, string intoKey)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = paths.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        using var tx = c.BeginTransaction(deferred: true);
        using var command = c.CreateCommand(); command.Transaction = tx;
        object? Scalar(string sql, params (string, object)[] parameters)
        {
            command.CommandText = sql; command.Parameters.Clear();
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            return command.ExecuteScalar();
        }
        command.CommandText = "SELECT folder_key,parent_key,name,kind,sort_order,origin,origin_key FROM local_folders";
        var folders = new Dictionary<string, LocalFolder>();
        using (var reader = command.ExecuteReader()) while (reader.Read())
            folders.Add(reader.GetString(0), new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        var from = folders[fromKey]; var into = folders[intoKey];
        if (from.Kind != LocalFolderKinds.User || into.Kind != LocalFolderKinds.User || from.ParentKey is not null
            || into.ParentKey is null || from.Name != into.Name || from.Origin != "archive" || into.Origin != "archive")
            throw new InvalidOperationException("Choose a misplaced imported root and an existing nested folder of the same name.");
        var source = from.OriginKey!.Split('|', 2); var target = into.OriginKey!.Split('|', 2);
        if (source.Length != 2 || target.Length != 2 || source[0] != target[0] || source[1] != from.Name || source[1] == target[1])
            throw new InvalidOperationException("Imported origins do not describe separate folders in the same account.");
        if (Convert.ToInt64(Scalar("SELECT count(*) FROM local_folders WHERE parent_key=$key", ("$key", fromKey))) != 0
            || Convert.ToInt64(Scalar("SELECT count(*) FROM messages WHERE folder=$key", ("$key", fromKey))) != 0
            || Convert.ToInt64(Scalar("SELECT count(*) FROM draft_folders WHERE folder_key=$key", ("$key", fromKey))) != 0)
            throw new InvalidOperationException("The source must have no children, live mail or filed drafts.");
        if (Convert.ToInt64(Scalar("SELECT count(*) FROM sqlite_master WHERE name='folder_storage_registry'")) != 0
            && Convert.ToInt64(Scalar("SELECT count(*) FROM folder_storage_registry WHERE folder_key IN ($from,$into)", ("$from", fromKey), ("$into", intoKey))) != 0)
            throw new InvalidOperationException("Portable folders need separate handling.");
        if (Convert.ToInt64(Scalar("SELECT count(*) FROM sqlite_master WHERE name='bulk_bindings'")) != 0
            && Convert.ToInt64(Scalar("""
                SELECT count(*) FROM bulk_bindings
                WHERE (source='archive' AND account=$account AND folder IN ($sourceFolder,$targetFolder))
                    OR (source='live' AND folder IN ($from,$into))
                """, ("$account", source[0]), ("$sourceFolder", source[1]), ("$targetFolder", target[1]),
                ("$from", fromKey), ("$into", intoKey))) != 0)
            throw new InvalidOperationException("The old imported-root merge cannot change portable folders.");
        if (Convert.ToInt64(Scalar("SELECT count(*) FROM messages WHERE source='archive' AND account=$account AND folder LIKE $prefix", ("$account", source[0]), ("$prefix", source[1] + "/%"))) != 0)
            throw new InvalidOperationException("Imported children need separate handling.");
        if (Convert.ToInt64(Scalar("SELECT count(*) FROM archive_folders WHERE source='archive' AND account=$account AND folder LIKE $prefix", ("$account", source[0]), ("$prefix", source[1] + "/%"))) != 0)
            throw new InvalidOperationException("Declared imported children need separate handling.");
        command.CommandText = "SELECT id,source_key,raw_path,is_read,date_utc,converted_draft_id FROM messages WHERE source='archive' AND account=$account AND folder=$folder ORDER BY id";
        command.Parameters.Clear(); command.Parameters.AddWithValue("$account", source[0]); command.Parameters.AddWithValue("$folder", source[1]);
        var rows = new List<object[]>();
        using (var reader = command.ExecuteReader()) while (reader.Read())
        { var values = new object[reader.FieldCount]; reader.GetValues(values); rows.Add(values); }
        if (rows.Count is 0 or > 10) throw new InvalidOperationException("This small-folder merge requires one to ten originals.");
        long bytes = 0;
        var duplicates = new Dictionary<long, long>();
        var destinationSnapshots = new List<object[]>();
        foreach (var row in rows)
        {
            var id = (long)row[0];
            if (row[2] is not string raw || row[5] != DBNull.Value) throw new InvalidDataException("Complete original MIME without converted drafts is required.");
            var payload = File.ReadAllBytes(Path.Combine(paths.BlobDirectory, raw)); bytes += payload.Length;
            if (Convert.ToHexStringLower(SHA256.HashData(payload)) != Path.GetFileNameWithoutExtension(raw)) throw new InvalidDataException("Original MIME hash differs.");
            if (Convert.ToInt64(Scalar("SELECT count(*) FROM messages WHERE source='live' AND source_key=$adopted", ("$adopted", MailContract.AdoptedCopyKeyPrefix + id))) != 0)
                throw new InvalidOperationException("An original has an adopted copy; review separately.");
            command.CommandText = "SELECT id,source_key,raw_path,is_read,date_utc,converted_draft_id FROM messages WHERE source='archive' AND account=$account AND folder=$folder AND source_key=$key";
            command.Parameters.Clear(); command.Parameters.AddWithValue("$account", target[0]); command.Parameters.AddWithValue("$folder", target[1]); command.Parameters.AddWithValue("$key", row[1]);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                var existing = new object[reader.FieldCount]; reader.GetValues(existing);
                if (existing[2] is not string existingRaw || existing[5] != DBNull.Value
                    || !File.ReadAllBytes(Path.Combine(paths.BlobDirectory, existingRaw)).SequenceEqual(payload))
                    throw new InvalidDataException("Destination identity exists with different MIME or a converted draft.");
                duplicates.Add(id, (long)existing[0]); destinationSnapshots.Add(existing);
            }
        }
        var intoPath = into.Name; var parent = into.ParentKey; var seen = new HashSet<string> { intoKey };
        while (parent is not null)
        { if (!seen.Add(parent)) throw new InvalidDataException("Cyclic hierarchy."); var folder = folders[parent]; intoPath = folder.Name + "/" + intoPath; parent = folder.ParentKey; }
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { paths.Root, from, into, intoPath, rows, destinationSnapshots }))));
        return new(fromKey, intoKey, source[1], target[1], source[0], rows.Select(r => (long)r[0]).ToArray(), duplicates, bytes, digest);
    }

    public Result Apply(string fromKey, string intoKey, string confirmation)
    {
        var plan = Preview(fromKey, intoKey);
        if (plan.Confirmation != confirmation) throw new InvalidOperationException("The reviewed folder merge changed.");
        var required = new FileInfo(paths.DatabasePath).Length + 256L * 1024 * 1024;
        if (new DriveInfo(Path.GetPathRoot(paths.DatabasePath)!).AvailableFreeSpace < required)
            throw new IOException("Insufficient space for the checked merge backup.");
        var backup = paths.DatabasePath + ".before-import-merge-" + Guid.NewGuid().ToString("N") + ".bak";
        using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = paths.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Pooling = false }.ToString()))
        {
            source.Open(); target.Open(); source.BackupDatabase(target);
            using var check = target.CreateCommand(); check.CommandText = "PRAGMA quick_check";
            if (!Equals(check.ExecuteScalar(), "ok")) throw new InvalidDataException("Merge backup failed integrity check.");
        }
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        c.Open();
        using var command = c.CreateCommand();
        command.CommandText = "PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON"; command.ExecuteNonQuery();
        using var tx = c.BeginTransaction(); command.Transaction = tx;
        if (Preview(fromKey, intoKey).Confirmation != confirmation) throw new InvalidOperationException("Folder contents changed during backup.");
        AdoptionCleanup.EnsureSchema(c, tx);
        foreach (var id in plan.MessageIds)
        {
            command.Parameters.Clear(); command.Parameters.AddWithValue("$id", id);
            if (plan.Duplicates.TryGetValue(id, out var retained))
            {
                command.CommandText = "SELECT source_key,raw_path FROM messages WHERE id=$id";
                string key, raw;
                using (var reader = command.ExecuteReader()) { reader.Read(); key = reader.GetString(0); raw = reader.GetString(1); }
                var reference = "msg_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"archive\n{plan.Account}\n{plan.FromPath}\n{key}")))[..24];
                command.CommandText = """
                    INSERT INTO adoption_cleanup VALUES($id,$retained,'archive',$account,$folder,$key,$ref,$sha,$now);
                    INSERT OR IGNORE INTO message_marks(message_id,kind,marked_at) SELECT $retained,kind,marked_at FROM message_marks WHERE message_id=$id;
                    UPDATE outgoing_origins SET message_id=$retained WHERE message_id=$id;
                    DELETE FROM messages_fts WHERE rowid=$id;
                    DELETE FROM message_marks WHERE message_id=$id;
                    DELETE FROM messages WHERE id=$id;
                    """;
                command.Parameters.AddWithValue("$retained", retained); command.Parameters.AddWithValue("$account", plan.Account);
                command.Parameters.AddWithValue("$folder", plan.FromPath); command.Parameters.AddWithValue("$key", key);
                command.Parameters.AddWithValue("$ref", reference); command.Parameters.AddWithValue("$sha", Path.GetFileNameWithoutExtension(raw));
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); command.ExecuteNonQuery();
            }
            else
            {
                command.CommandText = "UPDATE messages SET folder=$folder WHERE id=$id";
                command.Parameters.AddWithValue("$folder", plan.IntoPath); command.ExecuteNonQuery();
            }
        }
        command.Parameters.Clear();
        command.CommandText = "DELETE FROM archive_folders WHERE source='archive' AND account=$account AND folder=$folder; DELETE FROM local_folders WHERE folder_key=$key; DELETE FROM settings WHERE key='startup.archive-counts'";
        command.Parameters.AddWithValue("$account", plan.Account); command.Parameters.AddWithValue("$folder", plan.FromPath); command.Parameters.AddWithValue("$key", fromKey);
        command.ExecuteNonQuery(); tx.Commit();
        return new(plan, backup, plan.MessageIds.Length - plan.Duplicates.Count, plan.Duplicates.Count);
    }
}
