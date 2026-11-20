using System.Reflection;
using System.Text.Json;
using Mailkeep.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mailkeep.TestFixtures;

/// <summary>Disposable format fixtures, not an alternate runtime writer.</summary>
internal static class PortableStorageFixture
{
    private static SqliteConnection Open(string file)
    {
        var c = new SqliteConnection($"Data Source={file};Pooling=False"); c.Open(); return c;
    }

    private static Dictionary<string, object?>[] Rows(SqliteConnection c, string sql)
    {
        using var q = c.CreateCommand(); q.CommandText = sql; using var r = q.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        while (r.Read()) rows.Add(Enumerable.Range(0, r.FieldCount).ToDictionary(r.GetName, i => r.IsDBNull(i) ? null : r.GetValue(i)));
        return rows.ToArray();
    }

    private static Dictionary<string, object?>[] Messages(SqliteConnection c, string key)
    {
        var rows = Rows(c, "SELECT * FROM messages").Where(r => (string)r["source"]! == "live" && (string)r["folder"]! == key).ToArray();
        foreach (var row in rows)
        {
            row["marks"] = Rows(c, "SELECT kind,marked_at FROM message_marks WHERE message_id=" + (long)row["id"]!);
            using var q = c.CreateCommand(); q.CommandText = "SELECT value FROM settings WHERE key=$key";
            q.Parameters.AddWithValue("$key", "folder-storage.origins:" + row["id"]);
            row["import_origins"] = q.ExecuteScalar() is string json ? JsonSerializer.Deserialize<JsonElement>(json) : null;
        }
        return rows;
    }

    public static string BindLocal(MailkeepServices services, string key)
    {
        using var c = Open(services.Paths.DatabasePath);
        var tree = JsonSerializer.Serialize(new { local_folders = Rows(c, "SELECT * FROM local_folders"), archive_folders = Rows(c, "SELECT * FROM archive_folders") });
        var messages = Messages(c, key).Select(r =>
        {
            var raw = File.ReadAllBytes(services.Blobs.FullPath((string)r["raw_path"]!));
            return new FolderBundle.BulkMessage((long)r["id"]!, JsonSerializer.Serialize(r), raw, FolderBundle.Sha256(raw));
        }).ToArray();
        var uuid = Guid.NewGuid().ToString("N");
        var file = Path.Combine(services.Paths.Root, "bound-folders", uuid, FolderBundle.FileName);
        FolderBundle.WriteBulk(file, uuid, new("live", "", key, tree, Logical: true), messages);
        new BulkFolderMigration(services.Paths).RebuildProjection();
        return file;
    }

    public static WritableFolderStorage.Registration[] SeedLegacy(MailkeepServices services, string? onlyKey = null)
    {
        using var c = Open(services.Paths.DatabasePath);
        using var q = c.CreateCommand();
        q.CommandText = """
            CREATE TABLE IF NOT EXISTS folder_storage_registry(folder_key TEXT PRIMARY KEY,uuid TEXT NOT NULL UNIQUE,created INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS folder_storage_journal(id INTEGER PRIMARY KEY AUTOINCREMENT,token TEXT NOT NULL UNIQUE,folder_key TEXT NOT NULL,message_id INTEGER,metadata TEXT NOT NULL,removed INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS folder_storage_existing(folder_key TEXT PRIMARY KEY,uuid TEXT NOT NULL UNIQUE,status TEXT NOT NULL);
            """;
        q.ExecuteNonQuery();
        var result = new List<WritableFolderStorage.Registration>();
        foreach (var folder in services.Database.ListLocalFolders().Where(f => f.Kind == "user" && f.Origin == "native" && (onlyKey is null || f.Key == onlyKey)))
        {
            var uuid = Guid.NewGuid().ToString("N");
            var file = Path.Combine(services.Paths.Root, "local-folders", uuid, FolderBundle.FileName);
            using var bundle = FolderBundle.Create(file, folder.Name, folder.Kind, uuid);
            Apply(bundle, null, JsonSerializer.Serialize(folder), null, false);
            foreach (var row in Messages(c, folder.Key))
                Apply(bundle, (long)row["id"]!, JsonSerializer.Serialize(row), File.ReadAllBytes(services.Blobs.FullPath((string)row["raw_path"]!)), false);
            q.Parameters.Clear(); q.CommandText = "INSERT INTO folder_storage_registry VALUES($key,$uuid,1)";
            q.Parameters.AddWithValue("$key", folder.Key); q.Parameters.AddWithValue("$uuid", uuid); q.ExecuteNonQuery();
            result.Add(new(folder.Key, uuid, file, true));
        }
        File.WriteAllText(Path.Combine(services.Paths.Root, WritableFolderStorage.MarkerName), "{\"version\":1,\"enabled\":true,\"onlyNewRootFolder\":null}");
        return result.ToArray();
    }

    public static void RemoveLegacyOccurrence(string file, long id)
    {
        string metadata;
        using (var c = Open(file))
        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT metadata FROM local_occurrences WHERE message_id=$id";
            q.Parameters.AddWithValue("$id", id); metadata = (string)q.ExecuteScalar()!;
        }
        using var bundle = FolderBundle.Open(file, false); Apply(bundle, id, metadata, null, true);
    }

    private static void Apply(FolderBundle bundle, long? id, string metadata, byte[]? raw, bool removed)
        => typeof(FolderBundle).GetMethod("ApplyLocal", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(bundle, [Guid.NewGuid().ToString("N"), id, metadata, raw, removed]);
}
