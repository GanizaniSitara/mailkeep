using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Infrastructure;

public sealed partial class WritableFolderStorage
{
    // Put the retired original's identity in the surviving occurrence before the
    // journal snapshots it, so rebuilding from portable files retains the alias.
    internal void PreserveCleanedAdoption(SqliteConnection c, SqliteTransaction tx, long originalId, long retainedId)
    {
        var metadata = ImportSnapshots(c, tx, "id=$id", ("$id", originalId)).Single();
        using var document = JsonDocument.Parse(metadata);
        var row = document.RootElement;
        var source = row.GetProperty("source").GetString()!;
        var account = row.GetProperty("account").GetString()!;
        var folder = row.GetProperty("folder").GetString()!;
        var key = row.GetProperty("source_key").GetString()!;
        var raw = row.GetProperty("raw_path").GetString()!;
        var prior = ImportScalar(c, tx, "SELECT value FROM settings WHERE key=$key", ("$key", OriginsKey(retainedId))) as string;
        var origins = prior is null ? new List<ImportOrigin>() : JsonSerializer.Deserialize<ImportOrigin[]>(prior)!.ToList();
        if (origins.Any(origin => origin.Id == originalId))
            throw new InvalidDataException("Original provenance already exists; cleanup refused.");
        origins.Add(new(originalId, source, account, folder, key,
            "msg_" + Hash($"{source}\n{account}\n{folder}\n{key}")[..24], Path.GetFileNameWithoutExtension(raw),
            DateTimeOffset.UtcNow.ToString("O"), metadata, PriorImportAliases(c, tx, originalId)));
        WriteImportSetting(c, tx, OriginsKey(retainedId), JsonSerializer.Serialize(origins));
    }
}
