using System.Text.Json;
using Mailkeep.Core;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Infrastructure;

// Portable provenance and alias codecs shared by the common writer and projection recovery.
public sealed partial class WritableFolderStorage
{
    private sealed record ImportOrigin(long Id, string Source, string Account, string Folder, string SourceKey,
        string MessageRef, string Sha256, string RemovedAt, string Metadata, ImportAlias[]? Aliases = null,
        string? RawBase64 = null);
    private static string OriginsKey(long id) => "folder-storage.origins:" + id;

    private static string[] ImportSnapshots(SqliteConnection c, SqliteTransaction? tx, string where, params (string, object)[] values)
    {
        using var command = ImportCommand(c, tx, $"SELECT {Snapshot("m")} FROM messages m WHERE {where} ORDER BY id", values);
        using var reader = command.ExecuteReader();
        var rows = new List<string>(); while (reader.Read()) rows.Add(reader.GetString(0)); return rows.ToArray();
    }
    private static SqliteCommand ImportCommand(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object)[] values)
    {
        var command = c.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value); return command;
    }
    private static object? ImportScalar(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object)[] values)
    { using var command = ImportCommand(c, tx, sql, values); return command.ExecuteScalar(); }
    private static void ImportExecute(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object)[] values)
    { using var command = ImportCommand(c, tx, sql, values); command.ExecuteNonQuery(); }
    private static void WriteImportSetting(SqliteConnection c, SqliteTransaction tx, string key, string value)
        => ImportExecute(c, tx, "INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$key", key), ("$value", value));
    private static void RefreshImportSearch(SqliteConnection c, SqliteTransaction tx, long id)
        => ImportExecute(c, tx, "DELETE FROM messages_fts WHERE rowid=$id; INSERT INTO messages_fts(rowid,subject,sender,recipients,body_text) SELECT id,subject,sender,recipients_json,body_text FROM messages WHERE id=$id", ("$id", id));
    private static void WriteImportAlias(SqliteConnection c, SqliteTransaction tx, long retained, ImportOrigin origin)
    {
        var existing = ImportScalar(c, tx, "SELECT retained_id FROM adoption_cleanup WHERE original_id=$id", ("$id", origin.Id));
        if (existing is long id && id != retained) throw new InvalidDataException("Import alias points elsewhere.");
        ImportExecute(c, tx, "INSERT OR IGNORE INTO adoption_cleanup VALUES($id,$retained,$source,$account,$folder,$key,$ref,$sha,$at)",
            ("$id", origin.Id), ("$retained", retained), ("$source", origin.Source), ("$account", origin.Account), ("$folder", origin.Folder),
            ("$key", origin.SourceKey), ("$ref", origin.MessageRef), ("$sha", origin.Sha256), ("$at", origin.RemovedAt));
    }
    internal static void ValidateImportOrigins(JsonElement metadata, byte[] raw)
    {
        if (!metadata.TryGetProperty("import_origins", out var origins) || origins.ValueKind == JsonValueKind.Null) return;
        foreach (var origin in origins.Deserialize<ImportOrigin[]>()!)
        {
            using var original = JsonDocument.Parse(origin.Metadata);
            var row = original.RootElement;
            var originalRaw = origin.RawBase64 is null ? raw : Convert.FromBase64String(origin.RawBase64);
            if (!SameImportedPayload(originalRaw, raw) || origin.Sha256 != FolderBundle.Sha256(originalRaw)
                || Path.GetFileNameWithoutExtension(row.GetProperty("raw_path").GetString()) != origin.Sha256
                || origin.Id != row.GetProperty("id").GetInt64()
                || origin.Source != row.GetProperty("source").GetString() || origin.Account != row.GetProperty("account").GetString()
                || origin.Folder != row.GetProperty("folder").GetString() || origin.SourceKey != row.GetProperty("source_key").GetString()
                || origin.MessageRef != "msg_" + Hash($"{origin.Source}\n{origin.Account}\n{origin.Folder}\n{origin.SourceKey}")[..24])
                throw new InvalidDataException("Portable imported provenance differs from its message bytes/identity.");
            ValidatePriorAliases(origin.Aliases, origin.Id, origin.Sha256);
        }
    }
    internal void RestoreImportOrigins(SqliteConnection c, SqliteTransaction tx, long id, JsonElement metadata)
    {
        if (!metadata.TryGetProperty("import_origins", out var origins) || origins.ValueKind == JsonValueKind.Null) return;
        WriteImportSetting(c, tx, OriginsKey(id), origins.GetRawText());
        foreach (var origin in origins.Deserialize<ImportOrigin[]>()!)
        {
            if (origin.RawBase64 is not null) blobs.Put(Convert.FromBase64String(origin.RawBase64));
            WriteImportAlias(c, tx, id, origin);
        }
        foreach (var origin in origins.Deserialize<ImportOrigin[]>()!) RestorePriorAliases(c, tx, origin.Aliases);
    }
}
