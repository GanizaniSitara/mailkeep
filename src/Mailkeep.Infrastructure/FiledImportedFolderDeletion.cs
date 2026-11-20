using System.Text.Json;
using Mailkeep.Core;

namespace Mailkeep.Infrastructure;

public sealed partial class WritableFolderStorage
{
    /// <summary>Delete an empty imported view only when every backing occurrence has its explicit,
    /// verified Local Mail counterpart filed elsewhere. Preserve original bytes, provenance and aliases.</summary>
    internal int DeleteFiledImportedFolder(string account, string folder)
    {
        Flush();
        Directory.CreateDirectory(DirectoryPath);
        int removed;
        using (Lease()) removed = DeleteFiledImportedFolderCore(account, folder);
        Flush(); return removed;
    }

    private int DeleteFiledImportedFolderCore(string account, string folder)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        var mirrors = new List<string>();
        using (var command = ImportCommand(c, tx, "SELECT folder_key FROM local_folders WHERE origin='archive' AND origin_key=$origin", ("$origin", account + "|" + folder)))
        using (var reader = command.ExecuteReader()) while (reader.Read()) mirrors.Add(reader.GetString(0));
        var keys = JsonSerializer.Serialize(mirrors);
        if (Convert.ToInt64(ImportScalar(c, tx, "SELECT count(*) FROM messages WHERE source='live' AND folder IN (SELECT value FROM json_each($keys))", ("$keys", keys))) != 0
            || Convert.ToInt64(ImportScalar(c, tx, "SELECT count(*) FROM draft_folders WHERE folder_key IN (SELECT value FROM json_each($keys))", ("$keys", keys))) != 0)
            throw new InvalidOperationException("The folder still holds messages.");
        if (Convert.ToInt64(ImportScalar(c, tx, "SELECT count(*) FROM local_folders WHERE parent_key IN (SELECT value FROM json_each($keys))", ("$keys", keys))) != 0
            || Convert.ToInt64(ImportScalar(c, tx, "SELECT count(*) FROM archive_folders WHERE source='archive' AND account=$account AND substr(folder,1,length($prefix))=$prefix", ("$account", account), ("$prefix", folder + "/"))) != 0
            || Convert.ToInt64(ImportScalar(c, tx, "SELECT count(*) FROM messages WHERE source='archive' AND account=$account AND substr(folder,1,length($prefix))=$prefix", ("$account", account), ("$prefix", folder + "/"))) != 0)
            throw new InvalidOperationException("The folder still holds folders.");
        if (Inspect().Folders.Any(f => mirrors.Contains(f.Key)))
            throw new InvalidOperationException("Delete a converted folder through its ordinary local-folder command.");
        var originals = ImportSnapshots(c, tx, "source='archive' AND account=$account AND folder=$folder", ("$account", account), ("$folder", folder));
        var localAccounts = new List<string>();
        using (var command = ImportCommand(c, tx, "SELECT DISTINCT account FROM messages WHERE source='live' AND (account LIKE 'local:%' OR account=$local)", ("$local", MailContract.LocalCopyAccount)))
        using (var reader = command.ExecuteReader()) while (reader.Read()) localAccounts.Add(reader.GetString(0));
        var accounts = JsonSerializer.Serialize(localAccounts);
        var reviewed = new List<(long Id, long Copy, ImportOrigin Origin)>();
        foreach (var metadata in originals)
        {
            using var original = JsonDocument.Parse(metadata); var row = original.RootElement; var id = row.GetProperty("id").GetInt64();
            var copyKey = MailContract.AdoptedCopyKeyPrefix + id;
            var copies = ImportSnapshots(c, tx, "source='live' AND account=$local AND source_key=$copy AND server_uid IS NULL AND converted_draft_id IS NULL",
                ("$local", MailContract.LocalCopyAccount), ("$copy", copyKey));
            // The merged view also hides independently received mail. A unique header match is only
            // a candidate: verify the MIME below, retaining the original bytes for known variants.
            if (copies.Length == 0)
                copies = ImportSnapshots(c, tx, """
                    id IN (SELECT id FROM messages INDEXED BY idx_messages_account_date
                    WHERE account IN (SELECT value FROM json_each($accounts)) AND source='live'
                    AND server_uid IS NULL AND converted_draft_id IS NULL
                    AND subject=$subject AND sender=$sender AND date_utc IS $date)
                    """, ("$accounts", accounts), ("$subject", row.GetProperty("subject").GetString()!),
                    ("$sender", row.GetProperty("sender").GetString()!),
                    ("$date", (object?)row.GetProperty("date_utc").GetString() ?? DBNull.Value));
            if (copies.Length != 1 || ImportScalar(c, tx, "SELECT converted_draft_id FROM messages WHERE id=$id", ("$id", id)) is not DBNull)
                throw new InvalidOperationException("The folder still holds imported messages without verified copies filed elsewhere.");
            using var copied = JsonDocument.Parse(copies[0]); var copy = copied.RootElement;
            var destination = copy.GetProperty("folder").GetString()!;
            if (mirrors.Contains(destination) || ImportScalar(c, tx, "SELECT 1 FROM local_folders WHERE folder_key=$key", ("$key", destination)) is null)
                throw new InvalidOperationException("The folder still holds messages.");
            var rawPath = row.GetProperty("raw_path").GetString(); var copiedPath = copy.GetProperty("raw_path").GetString();
            if (rawPath is null || copiedPath is null) throw new InvalidDataException("Imported/copy MIME is missing; folder deletion refused.");
            var raw = blobs.ReadBytes(rawPath); var payload = blobs.ReadBytes(copiedPath);
            var sha = FolderBundle.Sha256(raw);
            if (!SameImportedPayload(raw, payload) || Path.GetFileNameWithoutExtension(rawPath) != sha
                || Path.GetFileNameWithoutExtension(copiedPath) != FolderBundle.Sha256(payload))
                throw new InvalidDataException("Filed copy bytes differ; folder deletion refused.");
            var sourceKey = row.GetProperty("source_key").GetString()!;
            var origin = new ImportOrigin(id, "archive", account, folder, sourceKey,
                "msg_" + Hash($"archive\n{account}\n{folder}\n{sourceKey}")[..24], sha, DateTimeOffset.UtcNow.ToString("O"), metadata,
                PriorImportAliases(c, tx, id), raw.SequenceEqual(payload) ? null : Convert.ToBase64String(raw));
            reviewed.Add((id, copy.GetProperty("id").GetInt64(), origin));
        }
        foreach (var item in reviewed)
        {
            var prior = ImportScalar(c, tx, "SELECT value FROM settings WHERE key=$key", ("$key", OriginsKey(item.Copy))) as string;
            var origins = prior is null ? new List<ImportOrigin>() : JsonSerializer.Deserialize<ImportOrigin[]>(prior)!.ToList();
            if (origins.Any(o => o.Id == item.Id)) throw new InvalidDataException("Original provenance already exists; folder deletion refused.");
            origins.Add(item.Origin); WriteImportSetting(c, tx, OriginsKey(item.Copy), JsonSerializer.Serialize(origins));
            WriteImportAlias(c, tx, item.Copy, item.Origin);
            ImportExecute(c, tx, """
                UPDATE outgoing_origins SET message_id=$copy WHERE message_id=$id;
                DELETE FROM messages_fts WHERE rowid=$id;
                DELETE FROM message_marks WHERE message_id=$id;
                DELETE FROM messages WHERE id=$id;
                UPDATE messages SET is_read=is_read WHERE id=$copy;
                """, ("$id", item.Id), ("$copy", item.Copy));
        }
        ImportExecute(c, tx, """
            DELETE FROM archive_folders WHERE source='archive' AND account=$account AND folder=$folder;
            DELETE FROM local_folders WHERE folder_key IN (SELECT value FROM json_each($keys));
            DELETE FROM settings WHERE key='startup.archive-counts';
            """, ("$account", account), ("$folder", folder), ("$keys", keys));
        tx.Commit(); return mirrors.Count;
    }

    // Only known transport differences are accepted, never a header-only match or changed content.
    // A differing original is embedded in the retained message's portable provenance, not discarded.
    private static bool SameImportedPayload(byte[] original, byte[] retained)
    {
        if (original.AsSpan().SequenceEqual(retained)) return true;
        if (original.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) && original.AsSpan(3).SequenceEqual(retained)
            || retained.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) && retained.AsSpan(3).SequenceEqual(original)) return true;
        var shorter = original.Length < retained.Length ? original : retained;
        var longer = original.Length < retained.Length ? retained : original;
        var extra = longer.AsSpan(shorter.Length);
        return longer.AsSpan().StartsWith(shorter)
            && (extra.SequenceEqual("\r\n"u8) || extra.SequenceEqual("\r\n\r\n"u8));
    }
}
