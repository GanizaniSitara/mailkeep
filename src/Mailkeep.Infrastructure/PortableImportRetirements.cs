using System.Text.Json;
using System.Text.Json.Nodes;
using Mailkeep.Core;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Infrastructure;

// Read/restore historical portable receipts; the former enrollment and rollback writer is retired.
public sealed partial class WritableFolderStorage
{
    private sealed record ImportAlias(long OriginalId, long RetainedId, string Source, string Account, string Folder,
        string SourceKey, string MessageRef, string Sha256, string RemovedAt);
    private sealed record ImportRetirement(long OriginalId, long RetainedId, string Metadata, string RetainedMetadata,
        string RawBase64, string Sha256, string RetainedSha256, string Match, ImportAlias[] Aliases, string? RemovedAt = null);
    private static string RetirementsKey(string key) => "folder-storage.retirements:" + key;

    private static ImportAlias[] PriorImportAliases(SqliteConnection c, SqliteTransaction? tx, long retained)
    {
        var pending = new Queue<long>(); pending.Enqueue(retained);
        var seen = new HashSet<long>(); var result = new List<ImportAlias>();
        while (pending.TryDequeue(out var id))
        {
            if (!seen.Add(id)) throw new InvalidDataException("Import aliases contain a cycle or ambiguity.");
            using var command = ImportCommand(c, tx, "SELECT original_id,retained_id,source,account,folder,source_key,message_ref,sha256,removed_at FROM adoption_cleanup WHERE retained_id=$id ORDER BY original_id", ("$id", id));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var alias = new ImportAlias(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8));
                result.Add(alias);
                if (alias.OriginalId != id) pending.Enqueue(alias.OriginalId);
            }
        }
        return result.ToArray();
    }

    private static void RestorePriorAliases(SqliteConnection c, SqliteTransaction tx, ImportAlias[]? aliases)
    {
        foreach (var alias in aliases ?? [])
        {
            var origin = new ImportOrigin(alias.OriginalId, alias.Source, alias.Account, alias.Folder, alias.SourceKey,
                alias.MessageRef, alias.Sha256, alias.RemovedAt, "{}");
            WriteImportAlias(c, tx, alias.RetainedId, origin);
        }
    }

    private static byte[] RetirementCounterpart(ImportRetirement retired, byte[] original) => retired.Match switch
    {
        "exact" => original,
        "utf8_bom_added" => [0xef, 0xbb, 0xbf, .. original],
        "utf8_bom_removed" when original.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) => original[3..],
        _ => throw new InvalidDataException("Unknown imported counterpart byte relation.")
    };

    private static ImportRetirement[] ReadRetirements(JsonElement folder)
    {
        var rows = folder.TryGetProperty("ImportRetirements", out var property) && property.ValueKind != JsonValueKind.Null
            ? property.Deserialize<ImportRetirement[]>()! : [];
        if (rows.Select(r => r.OriginalId).Distinct().Count() != rows.Length) throw new InvalidDataException("Duplicate imported retirement receipt.");
        foreach (var retired in rows)
        {
            var raw = Convert.FromBase64String(retired.RawBase64);
            using var original = JsonDocument.Parse(retired.Metadata); using var current = JsonDocument.Parse(retired.RetainedMetadata);
            var row = original.RootElement; var canonical = current.RootElement;
            if (!DateTimeOffset.TryParse(retired.RemovedAt, out _) || row.GetProperty("id").GetInt64() != retired.OriginalId || canonical.GetProperty("id").GetInt64() != retired.RetainedId
                || canonical.GetProperty("source").GetString() != "live" || row.GetProperty("source").GetString() != "archive"
                || canonical.GetProperty("folder").GetString() == folder.GetProperty("Key").GetString()
                || FolderBundle.Sha256(raw) != retired.Sha256 || FolderBundle.Sha256(RetirementCounterpart(retired, raw)) != retired.RetainedSha256
                || Path.GetFileNameWithoutExtension(row.GetProperty("raw_path").GetString()) != retired.Sha256
                || Path.GetFileNameWithoutExtension(canonical.GetProperty("raw_path").GetString()) != retired.RetainedSha256)
                throw new InvalidDataException("Imported retirement bytes/identity differ.");
            ValidatePriorAliases(retired.Aliases, retired.OriginalId, retired.Sha256);
        }
        return rows;
    }

    private static void ValidatePriorAliases(ImportAlias[]? aliases, long target, string sha)
    {
        var rows = (aliases ?? []).ToDictionary(a => a.OriginalId);
        foreach (var alias in rows.Values)
        {
            if (alias.Sha256 != sha || alias.MessageRef != "msg_" + Hash($"{alias.Source}\n{alias.Account}\n{alias.Folder}\n{alias.SourceKey}")[..24])
                throw new InvalidDataException("Prior import alias identity differs.");
            var id = alias.OriginalId; var seen = new HashSet<long>();
            while (id != target)
            {
                if (!seen.Add(id) || !rows.TryGetValue(id, out var next)) throw new InvalidDataException("Prior import alias chain differs.");
                id = next.RetainedId;
            }
        }
    }

    private void RestoreRetirements(SqliteConnection c, SqliteTransaction tx, string key, ImportRetirement[] retired)
    {
        if (retired.Length == 0) return;
        WriteImportSetting(c, tx, RetirementsKey(key), JsonSerializer.Serialize(retired));
        foreach (var receipt in retired)
        {
            var raw = Convert.FromBase64String(receipt.RawBase64);
            blobs.Put(raw); blobs.Put(RetirementCounterpart(receipt, raw));
            using var doc = JsonDocument.Parse(receipt.Metadata); var row = doc.RootElement;
            var source = row.GetProperty("source").GetString()!; var account = row.GetProperty("account").GetString()!;
            var folder = row.GetProperty("folder").GetString()!; var sourceKey = row.GetProperty("source_key").GetString()!;
            var origin = new ImportOrigin(receipt.OriginalId, source, account, folder, sourceKey,
                "msg_" + Hash($"{source}\n{account}\n{folder}\n{sourceKey}")[..24], receipt.Sha256, receipt.RemovedAt ?? throw new InvalidDataException("Retirement timestamp is missing."), receipt.Metadata);
            WriteImportAlias(c, tx, receipt.RetainedId, origin); RestorePriorAliases(c, tx, receipt.Aliases);
        }
    }

    internal void RestorePortableRetirements(SqliteConnection c, SqliteTransaction tx, string key, string metadata)
    {
        using var document = JsonDocument.Parse(metadata);
        if (document.RootElement.GetProperty("Key").GetString() != key)
            throw new InvalidDataException("Portable retirement folder identity differs.");
        RestoreRetirements(c, tx, key, ReadRetirements(document.RootElement));
    }

    internal static void ValidatePortableRetirements(JsonElement folder) => ReadRetirements(folder);

}
