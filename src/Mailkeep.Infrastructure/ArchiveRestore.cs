using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mailkeep.Core;
using Microsoft.Data.Sqlite;
using MimeKit;

namespace Mailkeep.Infrastructure;

/// <summary>Additive, atomic restoration of a selected archive folder or subtree into ordinary Local Mail.
/// The archive is kept intact. Truth bytes/state are read without depending on its disposable header caches.</summary>
public sealed class ArchiveRestore(MailkeepServices services)
{
    public sealed record Plan(string Location, string Selection, bool IncludeChildren, string Into,
        ArchiveManifest.Entry[] Folders, int Messages);
    public sealed record Result(bool Executed, Plan Plan, int Restored, int AlreadyRestored, string[] FolderKeys);

    public Result Run(string location, string selection = "", bool includeChildren = false, string into = "",
        bool execute = false, CancellationToken token = default, Action<int>? progress = null)
    {
        location = Path.GetFullPath(location);
        ArchiveMoveBack.EnsureNoPending(location);
        if (selection.Length > 0) ArchiveManifest.ValidatePath(selection);
        if (into.Length > 0) ArchiveManifest.ValidatePath(into);
        var inventory = ArchiveManifest.Read(location);
        bool Selected(string label) => selection.Length == 0 ? includeChildren
            : label.Equals(selection, StringComparison.OrdinalIgnoreCase)
                || includeChildren && label.StartsWith(selection + "/", StringComparison.OrdinalIgnoreCase);
        var entries = inventory.Folders.Where(entry => Selected(entry.Label)).OrderBy(entry => entry.OriginalPath, StringComparer.OrdinalIgnoreCase).ToArray();
        if (entries.Length == 0) throw new InvalidOperationException("Select an archived folder or tree containing folder files.");
        var actual = Directory.EnumerateFiles(location, FolderBundle.FileName, SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(location, Path.GetDirectoryName(file)!).Replace('\\', '/'))
            .Where(label => label != "." && Selected(label));
        if (actual.Except(entries.Select(entry => entry.Label), StringComparer.OrdinalIgnoreCase).Any())
            throw new InvalidDataException("Archive inventory is out of date; an unrecorded folder would be omitted.");
        var registered = new ArchiveStore(services.Paths).Archives.FirstOrDefault(archive => Path.GetFullPath(archive.Location).Equals(location, StringComparison.OrdinalIgnoreCase));
        if (registered is { Attached: false } || entries.Any(entry => registered?.IsFolderDetached(entry.Label) == true))
            throw new InvalidOperationException("Attach the selected archive and all selected folders before restoring.");
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            var file = ArchiveManifest.ResolveFile(location, entry.File);
            using var bundle = FolderBundle.Open(file);
            if (bundle.Folder().Uuid != entry.Uuid || ArchiveManifest.TruthSignature(file) != entry.Signature)
                throw new InvalidDataException($"{entry.Label}: archive identity or state differs from its inventory.");
            var count = 0;
            foreach (var row in Rows(file)) { token.ThrowIfCancellationRequested(); Validate(row); count++; }
            if (count != entry.Messages) throw new InvalidDataException($"{entry.Label}: message or state rows are missing.");
        }
        var plan = new Plan(location, selection, includeChildren, into, entries, entries.Sum(entry => entry.Messages));
        if (!execute)
        {
            if (File.Exists(services.Paths.DatabasePath))
            {
                using var connection = OpenTarget(readOnly: true);
                ValidateTargets(connection, null, plan, create: false);
            }
            return new(false, plan, 0, 0, []);
        }
        if (!File.Exists(services.Paths.DatabasePath)) services.Database.Initialize();
        using var target = OpenTarget(readOnly: false);
        using var transaction = target.BeginTransaction();
        using (var schema = target.CreateCommand())
        {
            schema.Transaction = transaction;
            schema.CommandText = """
                CREATE TABLE IF NOT EXISTS archive_restore_folders (
                    folder_uuid TEXT PRIMARY KEY, folder_key TEXT NOT NULL, original_path TEXT NOT NULL, restored_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS archive_restore_messages (
                    folder_uuid TEXT NOT NULL, seq INTEGER NOT NULL, message_id INTEGER NOT NULL,
                    sha256 TEXT NOT NULL, origin TEXT NOT NULL, legacy_id INTEGER, restored_at TEXT NOT NULL,
                    PRIMARY KEY(folder_uuid,seq)
                );
                CREATE TABLE IF NOT EXISTS archive_restore_aliases (
                    original_id INTEGER PRIMARY KEY, retained_id INTEGER NOT NULL, message_ref TEXT NOT NULL UNIQUE
                );
                """;
            schema.ExecuteNonQuery();
        }
        var folders = ValidateTargets(target, transaction, plan, create: true);
        var restored = 0;
        var already = 0;
        foreach (var entry in entries)
        {
            var file = ArchiveManifest.ResolveFile(location, entry.File);
            if (ArchiveManifest.TruthSignature(file) != entry.Signature) throw new InvalidDataException("Archive changed after preview.");
            var count = 0;
            foreach (var row in Rows(file))
            {
                token.ThrowIfCancellationRequested();
                Validate(row);
                count++;
                using var command = target.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "SELECT message_id,sha256 FROM archive_restore_messages WHERE folder_uuid=$uuid AND seq=$seq";
                command.Parameters.AddWithValue("$uuid", entry.Uuid);
                command.Parameters.AddWithValue("$seq", row.Seq);
                long? oldId = null;
                using (var reader = command.ExecuteReader())
                    if (reader.Read())
                    {
                        oldId = reader.GetInt64(0);
                        if (reader.GetString(1) != row.Sha) throw new InvalidDataException("Previously restored bytes differ.");
                    }
                if (oldId is { } previous)
                {
                    command.CommandText = "SELECT count(*) FROM messages WHERE id=$id";
                    command.Parameters.AddWithValue("$id", previous);
                    if (Convert.ToInt64(command.ExecuteScalar()) != 1)
                        throw new InvalidOperationException("A previously restored message was removed; it will not be resurrected.");
                    already++;
                    continue; // Later filing/read/deleted state belongs to the mailbox, not this old snapshot.
                }
                var blob = services.Blobs.Put(row.Raw);
                if (FolderBundle.Sha256(services.Blobs.ReadBytes(blob)) != row.Sha)
                    throw new InvalidDataException("A destination blob has different bytes.");
                var id = Insert(target, transaction, folders[entry.Uuid], entry.Uuid, row, blob);
                command.CommandText = "INSERT INTO archive_restore_messages VALUES($uuid,$seq,$id,$sha,$origin,$legacy,$now)";
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$sha", row.Sha);
                command.Parameters.AddWithValue("$origin", row.Origin);
                command.Parameters.AddWithValue("$legacy", (object?)row.Legacy ?? DBNull.Value);
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                command.ExecuteNonQuery();
                RegisterAlias(target, transaction, row, id);
                restored++;
                progress?.Invoke(restored);
            }
            if (count != entry.Messages || ArchiveManifest.TruthSignature(file) != entry.Signature)
                throw new InvalidDataException("Archive changed during restore; mailbox changes rolled back.");
        }
        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return new(true, plan, restored, already, folders.Values.ToArray());
    }

    private Dictionary<string, string> ValidateTargets(SqliteConnection connection, SqliteTransaction? transaction, Plan plan, bool create)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT folder_key,parent_key,name FROM local_folders";
        var local = new Dictionary<string, (string? Parent, string Name)>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) local.Add(reader.GetString(0), (reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2)));
        string PathOf(string key) => local[key].Parent is { } parent ? PathOf(parent) + "/" + local[key].Name : local[key].Name;
        var paths = local.Keys.ToDictionary(PathOf, key => key, StringComparer.OrdinalIgnoreCase);
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name='archive_restore_folders'";
        var mappings = new Dictionary<string, string>();
        if (Convert.ToInt64(command.ExecuteScalar()) > 0)
        {
            command.CommandText = "SELECT folder_uuid,folder_key FROM archive_restore_folders";
            using var reader = command.ExecuteReader();
            while (reader.Read()) mappings.Add(reader.GetString(0), reader.GetString(1));
        }
        var result = new Dictionary<string, string>();
        foreach (var entry in plan.Folders)
        {
            var path = (plan.Into.Length == 0 ? "" : plan.Into + "/") + entry.OriginalPath;
            if (mappings.TryGetValue(entry.Uuid, out var mapped))
            {
                if (!local.ContainsKey(mapped) || !PathOf(mapped).Equals(path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A restored folder was moved or removed; it will not be recreated.");
                result.Add(entry.Uuid, mapped);
                continue;
            }
            if (paths.ContainsKey(path) && entry.Messages > 0)
                throw new InvalidOperationException($"{path} already exists. Restore under another parent to keep its mail untouched.");
            string? parentKey = null;
            var walked = "";
            foreach (var name in path.Split('/'))
            {
                walked = walked.Length == 0 ? name : walked + "/" + name;
                if (!paths.TryGetValue(walked, out var key))
                {
                    key = "user:" + Guid.NewGuid().ToString("N")[..12];
                    if (create)
                    {
                        command.Parameters.Clear();
                        command.CommandText = "INSERT INTO local_folders(folder_key,parent_key,name,kind,sort_order,origin) VALUES($key,$parent,$name,'user',COALESCE((SELECT max(sort_order)+1 FROM local_folders),100),'restored-archive')";
                        command.Parameters.AddWithValue("$key", key);
                        command.Parameters.AddWithValue("$parent", (object?)parentKey ?? DBNull.Value);
                        command.Parameters.AddWithValue("$name", name);
                        command.ExecuteNonQuery();
                    }
                    local.Add(key, (parentKey, name));
                    paths.Add(walked, key);
                }
                parentKey = key;
            }
            result.Add(entry.Uuid, parentKey!);
            if (create)
            {
                command.Parameters.Clear();
                command.CommandText = "INSERT INTO archive_restore_folders VALUES($uuid,$key,$path,$now)";
                command.Parameters.AddWithValue("$uuid", entry.Uuid);
                command.Parameters.AddWithValue("$key", parentKey!);
                command.Parameters.AddWithValue("$path", entry.OriginalPath);
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                command.ExecuteNonQuery();
            }
        }
        return result;
    }

    private sealed record Row(long Seq, string Sha, byte[] Raw, long Size, string? Received, bool Read, bool Flagged, string? Deleted, string Origin, long? Legacy);
    private static IEnumerable<Row> Rows(string file)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT m.seq,m.sha256,m.raw,m.size,m.received_at,s.is_read,s.flagged,s.deleted_at,s.origin,s.legacy_id FROM messages m JOIN state s ON s.seq=m.seq ORDER BY m.seq";
        using var reader = command.ExecuteReader();
        while (reader.Read()) yield return new(reader.GetInt64(0), reader.GetString(1), (byte[])reader.GetValue(2), reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetBoolean(5), reader.GetBoolean(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetInt64(9));
    }

    private static void Validate(Row row)
    {
        if (FolderBundle.Sha256(row.Raw) != row.Sha || row.Size != row.Raw.LongLength) throw new InvalidDataException("Archive MIME bytes fail their hash/size check.");
        if (row.Flagged || row.Deleted is not null) throw new InvalidDataException("Flagged or deleted archive state needs review before restoring.");
    }

    private static long Insert(SqliteConnection connection, SqliteTransaction transaction, string folder, string uuid, Row row, string blob)
    {
        MimeMessage? parsed;
        try { parsed = MimeMessage.Load(new MemoryStream(row.Raw, writable: false)); }
        catch (FormatException) { parsed = null; }
        using var message = parsed;
        var sender = message?.From.Mailboxes.FirstOrDefault();
        var recipients = message?.To.Mailboxes.Concat(message.Cc.Mailboxes).Select(mailbox => mailbox.Address).Distinct().ToArray() ?? [];
        var text = message?.TextBody ?? MailText.FromHtml(message?.HtmlBody);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO messages(source,account,folder,source_key,uidvalidity,subject,sender,sender_name,recipients_json,date_utc,size,is_read,attachment_count,body_text,body_html,raw_path)
            VALUES('live',$account,$folder,$key,0,$subject,$sender,$name,$to,$date,$size,$read,$attachments,$text,$html,$raw) RETURNING id
            """;
        command.Parameters.AddWithValue("$account", MailContract.LocalCopyAccount);
        command.Parameters.AddWithValue("$folder", folder);
        command.Parameters.AddWithValue("$key", $"copy:restored:{uuid}:{row.Seq}");
        command.Parameters.AddWithValue("$subject", message?.Subject ?? "");
        command.Parameters.AddWithValue("$sender", sender?.Address ?? "");
        command.Parameters.AddWithValue("$name", sender?.Name ?? "");
        command.Parameters.AddWithValue("$to", JsonSerializer.Serialize(recipients));
        command.Parameters.AddWithValue("$date", (object?)row.Received ?? DBNull.Value);
        command.Parameters.AddWithValue("$size", row.Size);
        command.Parameters.AddWithValue("$read", row.Read);
        command.Parameters.AddWithValue("$attachments", message?.Attachments.Count() ?? 0);
        command.Parameters.AddWithValue("$text", text.Length > 1_000_000 ? text[..1_000_000] : text);
        command.Parameters.AddWithValue("$html", message?.HtmlBody is { } html ? html.Length > 2_000_000 ? html[..2_000_000] : html : DBNull.Value);
        command.Parameters.AddWithValue("$raw", blob);
        var id = (long)command.ExecuteScalar()!;
        command.CommandText = "INSERT INTO messages_fts(rowid,subject,sender,recipients,body_text) VALUES($id,$subject,$sender,$to,$text)";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
        return id;
    }

    private static void RegisterAlias(SqliteConnection connection, SqliteTransaction transaction, Row row, long id)
    {
        if (row.Legacy is not { } legacy) return;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name='archived_messages'";
        if (Convert.ToInt64(command.ExecuteScalar()) == 0) return;
        command.CommandText = "SELECT source,account,folder,source_key FROM archived_messages WHERE original_id=$id AND archive_seq=$seq AND NOT EXISTS(SELECT 1 FROM messages WHERE id=$id)";
        command.Parameters.AddWithValue("$id", legacy);
        command.Parameters.AddWithValue("$seq", row.Seq);
        string? reference = null;
        using (var reader = command.ExecuteReader())
            if (reader.Read() && row.Origin == $"{reader.GetString(0)}:{reader.GetString(1)}/{reader.GetString(2)}/{reader.GetString(3)}")
                reference = "msg_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                    $"{(reader.GetString(0) == MailContract.LiveSource ? MailContract.LocalSource : reader.GetString(0))}\n{reader.GetString(1)}\n{reader.GetString(2)}\n{reader.GetString(3)}")))[..24];
        if (reference is null) return;
        command.CommandText = "UPDATE archive_restore_aliases SET retained_id=$retained WHERE retained_id=$id; "
            + "INSERT INTO archive_restore_aliases VALUES($id,$retained,$ref)";
        command.Parameters.AddWithValue("$retained", id);
        command.Parameters.AddWithValue("$ref", reference);
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenTarget(bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = services.Paths.DatabasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
}
