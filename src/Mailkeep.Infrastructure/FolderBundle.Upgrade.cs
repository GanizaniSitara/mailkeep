using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Infrastructure;

public sealed partial class FolderBundle
{
    /// <summary>Upgrades an unpublished copy, retaining all MIME, tombstones, receipts and history.
    /// Active projection metadata must agree with every field recorded by the pilot.</summary>
    public static void UpgradePilotCopy(string sourceFile, string destination, Binding binding,
        IReadOnlyDictionary<long, string> currentMetadata)
    {
        if (!binding.Logical || binding.Source != "live" || binding.Account != "")
            throw new ArgumentException("Pilot upgrade requires a logical local folder binding.", nameof(binding));
        if (File.Exists(destination)) throw new IOException("Upgrade destination already exists.");
        using var source = Open(sourceFile);
        if (source.Folder().Format != "mailkeep-folder/1" || !source.Verify().Ok)
            throw new InvalidDataException("Pilot source verification failed.");
        using (var description = source._connection.CreateCommand())
        {
            description.CommandText = "SELECT metadata FROM local_folder WHERE singleton=1";
            using var json = JsonDocument.Parse((string?)description.ExecuteScalar() ?? throw new InvalidDataException("Pilot folder description is missing."));
            if (json.RootElement.GetProperty("Key").GetString() != binding.Folder)
                throw new InvalidDataException("Pilot folder identity differs.");
            WritableFolderStorage.ValidatePortableRetirements(json.RootElement);
        }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(destination))!);
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString());
        target.Open(); source._connection.BackupDatabase(target);
        using var tx = target.BeginTransaction();
        using var q = target.CreateCommand(); q.Transaction = tx;
        q.CommandText = "SELECT o.message_id,o.metadata,m.sha256,s.is_read,m.received_at FROM local_occurrences o JOIN state s USING(seq) JOIN messages m USING(seq) WHERE s.deleted_at IS NULL";
        var active = new List<(long Id, string Metadata, string Sha, int Read, string? Date)>();
        using (var r = q.ExecuteReader()) while (r.Read()) active.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.IsDBNull(4) ? null : r.GetString(4)));
        if (active.Count != currentMetadata.Count) throw new InvalidDataException("Pilot active message count differs from its projection.");
        foreach (var row in active)
        {
            if (!currentMetadata.TryGetValue(row.Id, out var metadata)) throw new InvalidDataException("Pilot active message is absent from its projection.");
            using var old = JsonDocument.Parse(row.Metadata); using var current = JsonDocument.Parse(metadata);
            foreach (var field in old.RootElement.EnumerateObject())
                if (!current.RootElement.TryGetProperty(field.Name, out var value) || !JsonElement.DeepEquals(field.Value, value))
                    throw new InvalidDataException($"Pilot metadata differs for message {row.Id}, field {field.Name}.");
            if (current.RootElement.GetProperty("source").GetString() != "live"
                || current.RootElement.GetProperty("folder").GetString() != binding.Folder
                || current.RootElement.GetProperty("id").GetInt64() != row.Id
                || current.RootElement.GetProperty("is_read").GetInt32() != row.Read
                || current.RootElement.GetProperty("date_utc").GetString() != row.Date
                || System.IO.Path.GetFileNameWithoutExtension(current.RootElement.GetProperty("raw_path").GetString()) != row.Sha)
                throw new InvalidDataException("Pilot occurrence identity or MIME content address differs.");
            q.CommandText = "UPDATE local_occurrences SET metadata=$metadata WHERE message_id=$id";
            q.Parameters.Clear(); q.Parameters.AddWithValue("$metadata", metadata); q.Parameters.AddWithValue("$id", row.Id); q.ExecuteNonQuery();
        }
        q.Parameters.Clear();
        q.CommandText = """
            CREATE TABLE binding(singleton INTEGER PRIMARY KEY CHECK(singleton=1),source TEXT NOT NULL,account TEXT NOT NULL,folder TEXT NOT NULL,hierarchy TEXT NOT NULL,logical INTEGER NOT NULL CHECK(logical=1));
            INSERT INTO binding VALUES(1,'live','',$folder,$hierarchy,1);
            PRAGMA user_version=3;
            UPDATE folder SET format='mailkeep-folder/3';
            """;
        q.Parameters.AddWithValue("$folder", binding.Folder); q.Parameters.AddWithValue("$hierarchy", binding.Hierarchy); q.ExecuteNonQuery();
        tx.Commit();
        q.Transaction = null; q.Parameters.Clear(); q.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE"; q.ExecuteNonQuery();
    }
}
