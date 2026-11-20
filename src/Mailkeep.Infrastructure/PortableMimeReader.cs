using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Infrastructure;

/// <summary>Resolves immutable MIME through the indexed projection to its authoritative folder occurrence.</summary>
internal sealed class PortableMimeReader(AppPaths paths)
{
    public byte[]? Read(string relative)
    {
        if (!File.Exists(paths.DatabasePath)) return null;
        using var c = BulkFolderMigration.Connect(paths.DatabasePath, timeoutSeconds: 5);
        if (!BulkFolderMigration.HasTable(c, "bulk_bindings")) return null;
        using var q = c.CreateCommand();
        q.CommandText = $"""
            SELECT b.uuid,m.id FROM messages m JOIN bulk_bindings b ON {BulkFolderMigration.BindingMatch(c, "m")}
            WHERE m.raw_path IN ($path,$slash) ORDER BY m.id
            """;
        q.Parameters.AddWithValue("$path", relative);
        q.Parameters.AddWithValue("$slash", relative.Replace('\\', '/'));
        var locations = new List<(string Uuid, long Id)>();
        using (var rows = q.ExecuteReader())
            while (rows.Read()) locations.Add((rows.GetString(0), rows.GetInt64(1)));
        if (locations.Count == 0) return null; // Unconverted/connected mail and outgoing MIME retain their own source.
        var sha = Path.GetFileNameWithoutExtension(relative);
        foreach (var location in locations)
            if (ReadOccurrence(location.Uuid, location.Id, sha) is { } raw) return raw;

        // A move can have committed its projection before the destination file is acknowledged.
        // Its journal identifies the old immutable occurrence; never scan every folder or every message.
        var pending = false;
        foreach (var location in locations)
        {
            q.Parameters.Clear();
            q.CommandText = "SELECT uuid FROM bulk_journal WHERE message_id=$id AND json_extract(metadata,'$.raw_path') IN ($path,$slash) ORDER BY id";
            q.Parameters.AddWithValue("$id", location.Id);
            q.Parameters.AddWithValue("$path", relative);
            q.Parameters.AddWithValue("$slash", relative.Replace('\\', '/'));
            var uuids = new List<string>();
            using (var rows = q.ExecuteReader()) while (rows.Read()) uuids.Add(rows.GetString(0));
            pending |= uuids.Count != 0;
            foreach (var uuid in uuids)
                if (ReadOccurrence(uuid, location.Id, sha) is { } raw) return raw;
        }
        if (pending) return null; // Newly received MIME is staged until the normal writer commits it.
        throw new InvalidDataException("The portable message occurrence is missing; refusing a stale MIME cache.");
    }

    private byte[]? ReadOccurrence(string uuid, long id, string expectedSha)
    {
        if (!Guid.TryParseExact(uuid, "N", out _)) throw new InvalidDataException("Invalid portable folder UUID.");
        var file = Path.Combine(paths.Root, "bound-folders", uuid, FolderBundle.FileName);
        if (!File.Exists(file)) return null; // A pending binding may not have a file yet; caller checks its journal.
        using var c = BulkFolderMigration.Connect(file, timeoutSeconds: 5);
        if (!Equals(BulkFolderMigration.Scalar(c, "SELECT uuid FROM folder"), uuid))
            throw new InvalidDataException("Portable folder identity differs.");
        using var q = c.CreateCommand();
        q.CommandText = "SELECT m.raw,m.sha256 FROM local_occurrences o JOIN messages m USING(seq) WHERE o.message_id=$id";
        q.Parameters.AddWithValue("$id", id);
        using var row = q.ExecuteReader();
        if (!row.Read()) return null;
        var raw = (byte[])row.GetValue(0);
        if (!row.GetString(1).Equals(expectedSha, StringComparison.OrdinalIgnoreCase)
            || !Convert.ToHexStringLower(SHA256.HashData(raw)).Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Portable MIME content hash differs.");
        return raw;
    }
}
