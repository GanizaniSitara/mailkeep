using System.Text.Json;
using Mailkeep.Core;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Infrastructure;

public sealed partial class WritableFolderStorage
{
    // Converted imports are writable occurrences. Keep their ID and provenance when filing;
    // the old portable occurrence becomes a tombstone through the normal move journal.
    internal bool MoveBoundImport(SqliteConnection c, SqliteTransaction tx, long id, string destination)
    {
        if (!BulkFolderMigration.HasTable(c, "bulk_bindings")) return false;
        using var command = ImportCommand(c, tx, $"""
            SELECT {BulkFolderMigration.Snapshot(c)} FROM messages m
            WHERE m.id=$id AND m.source='archive' AND m.converted_draft_id IS NULL
              AND EXISTS(SELECT 1 FROM bulk_bindings b WHERE {BulkFolderMigration.BindingMatch(c, "m")})
            """, ("$id", id));
        if (command.ExecuteScalar() is not string metadata) return false;
        using var document = JsonDocument.Parse(metadata); var row = document.RootElement;
        var relative = row.GetProperty("raw_path").GetString() ?? throw new InvalidDataException("Imported MIME is missing.");
        var raw = blobs.ReadBytes(relative);
        var sha = FolderBundle.Sha256(raw);
        if (sha != Path.GetFileNameWithoutExtension(relative)) throw new InvalidDataException("Imported MIME hash differs.");
        var source = row.GetProperty("source").GetString()!;
        var account = row.GetProperty("account").GetString()!;
        var folder = row.GetProperty("folder").GetString()!;
        var key = row.GetProperty("source_key").GetString()!;
        var origins = row.TryGetProperty("import_origins", out var previous) && previous.ValueKind != JsonValueKind.Null
            ? previous.Deserialize<ImportOrigin[]>()!.ToList() : new List<ImportOrigin>();
        if (origins.Any(o => o.Id == id)) throw new InvalidDataException("Imported move provenance already exists.");
        var origin = new ImportOrigin(id, source, account, folder, key,
            "msg_" + Hash($"{source}\n{account}\n{folder}\n{key}")[..24], sha,
            DateTimeOffset.UtcNow.ToString("O"), metadata, PriorImportAliases(c, tx, id));
        origins.Add(origin);
        WriteImportSetting(c, tx, OriginsKey(id), JsonSerializer.Serialize(origins));
        WriteImportAlias(c, tx, id, origin);
        ImportExecute(c, tx, """
            UPDATE messages SET source='live',account=$account,folder=$folder,source_key=$key,server_uid=NULL,uidvalidity=0
            WHERE id=$id
            """, ("$account", MailContract.LocalCopyAccount), ("$folder", destination),
            ("$key", MailContract.AdoptedCopyKeyPrefix + id), ("$id", id));
        return true;
    }
}
