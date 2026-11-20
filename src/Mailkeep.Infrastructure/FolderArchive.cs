using System.Text.Json;
using System.Text.Json.Nodes;
using Mailkeep.Core;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Infrastructure;

/// <summary>Moves one folder out of the mail database into a folder file in an archive location. The order is
/// what makes it safe: the file is written and checked message by message against the database first, the
/// database is backed up and the backup checked, and only then are the folder's rows removed, in one
/// transaction. Every removed row is written down with the file and message it now lives in, and a trigger
/// stops a later import from bringing it back. Message bytes on disk are never deleted.</summary>
public sealed class FolderArchive(AppPaths paths)
{
    public sealed record Plan(string FolderKey, string FolderPath, int Visible, int Unread, int HiddenDuplicates,
        int LiveRows, int ImportedRows, string File, bool FileExists, string[] Refusals);
    public sealed record Result(bool Executed, Plan Plan, string? Backup, int Removed, int RemovedHidden,
        FolderConversion.Comparison? Comparison, bool Registered);

    public sealed record EmptyParent(string Key, string? ParentKey, string Name, string Account, string Folder);

    /// <summary>Folder-tree counts written down at close (MainForm.SavedCountsSetting). An archived folder is
    /// struck from them, or the next start would draw it again from the saved copy.</summary>
    public const string SavedCountsSetting = "startup.archive-counts";

    private static readonly string[] StandardKinds = ["inbox", "drafts", "outbox", "sent", "deleted"];

    internal static void EnsureSchema(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS archived_messages (
                original_id INTEGER PRIMARY KEY,
                source TEXT NOT NULL, account TEXT NOT NULL, folder TEXT NOT NULL, source_key TEXT NOT NULL,
                raw_path TEXT, is_read INTEGER NOT NULL, folder_key TEXT NOT NULL,
                archive_file TEXT NOT NULL, archive_seq INTEGER, archived_at TEXT NOT NULL,
                UNIQUE(source,account,folder,source_key)
            );
            CREATE TRIGGER IF NOT EXISTS preserve_archived_message BEFORE INSERT ON messages
            WHEN EXISTS (SELECT 1 FROM archived_messages r WHERE r.source=NEW.source AND r.account=NEW.account
                AND r.folder=NEW.folder AND r.source_key=NEW.source_key)
            BEGIN SELECT RAISE(IGNORE); END;
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>Where the folder's file goes: its path in the tree, below the archive location.</summary>
    public static string TargetFile(string location, string folderPath)
        => Path.Combine([Path.GetFullPath(location), .. folderPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(FolderConversion.SafeName), FolderBundle.FileName]);

    /// <param name="backup">Back up the whole database first (the default). The file is checked against the
    /// database and every removed row is recorded either way; the backup is the extra safety net, and takes
    /// minutes on a large store, so the app lets the reader choose.</param>
    public Result Move(string keyOrPath, string location, bool execute, CancellationToken cancellationToken = default,
        bool backup = true, bool pruneEmptyParents = false)
    {
        using var lease = execute ? ArchiveMoveBack.Lock(location) : null;
        ArchiveMoveBack.EnsureNoPending(location);
        var conversion = new FolderConversion(paths);
        var (folder, path, visible, ownedHidden, adoptedHidden) = conversion.Load(keyOrPath, cancellationToken);
        var file = TargetFile(location, path);
        var (liveRows, importedRows, refusals) = Inspect(folder);
        if (File.Exists(file))
        {
            using var existing = FolderBundle.Open(file);
            if (existing.LastEventDetail(FolderConversion.ConvertedEvent) != folder.Key)
                refusals = [.. refusals, "the archive already contains this folder. Nothing will be overwritten or removed. "
                    + "Archiving a copy restored from Archive to the same place is not supported yet"];
        }
        var plan = new Plan(folder.Key, path, visible.Count, visible.Count(row => !row.IsRead), ownedHidden + adoptedHidden,
            liveRows, importedRows, file, File.Exists(file), refusals);
        if (!execute) return new(false, plan, null, 0, 0, null, false);
        if (refusals.Length > 0) throw new InvalidOperationException("Not archived: " + string.Join("; ", refusals));
        if (!Directory.Exists(location)) throw new DirectoryNotFoundException($"Archive location {location} does not exist.");

        // 1. The file, written and checked before anything in the database changes. A file left by an earlier
        //    run that stopped part-way is reused only if it still matches the database exactly.
        if (!File.Exists(file))
            conversion.Convert(folder.Key, Path.GetDirectoryName(Path.GetDirectoryName(file)!)!, execute: true, cancellationToken);
        var comparison = conversion.Compare(file, cancellationToken);
        if (!comparison.Ok || comparison.Compared != visible.Count || comparison.FolderKey != folder.Key)
            throw new InvalidDataException("The folder file does not match the database; nothing removed: "
                + string.Join("; ", comparison.Mismatches.Select(mismatch => mismatch.Issue).Distinct()));
        ArchiveManifest.Register(location, file);
        Dictionary<long, long> seqs;
        using (var bundle = FolderBundle.Open(file))
            seqs = bundle.List(includeDeleted: true).Where(row => row.LegacyId is not null).ToDictionary(row => row.LegacyId!.Value, row => row.Seq);
        SqliteConnection.ClearAllPools();

        // 2. A checked backup of the whole database, unless the reader chose to skip it.
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = paths.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 30 }.ToString());
        connection.Open();
        var backupFile = backup ? paths.DatabasePath + ".before-archive-" + FolderConversion.SafeName(folder.Name) + "-"
            + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".bak" : null;
        if (backupFile is not null)
        using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupFile, Pooling = false }.ToString()))
        {
            destination.Open();
            connection.BackupDatabase(destination);
            using var check = destination.CreateCommand();
            check.CommandText = "PRAGMA quick_check";
            if (!Equals(check.ExecuteScalar(), "ok")) throw new InvalidDataException("The backup failed its integrity check; nothing removed.");
        }

        // 3. The removal, all or nothing. The folder is read again inside the write lock, so mail that arrived
        //    after the file was checked stops the move instead of being lost.
        using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
        EnsureSchema(connection, transaction);
        var rows = FolderRows(connection, transaction, folder);
        var visibleIds = visible.Select(row => row.Id).ToHashSet();
        if (!visibleIds.SetEquals(seqs.Keys) || !visibleIds.IsSubsetOf(rows.Select(row => row.Id)))
            throw new InvalidDataException("The folder changed after its file was checked; nothing removed.");
        var (_, _, again) = Inspect(folder, connection, transaction);
        if (again.Length > 0) throw new InvalidOperationException("Not archived: " + string.Join("; ", again));
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO archived_messages VALUES($id,$source,$account,$folder,$key,$raw,$read,$folderKey,$file,$seq,$now);
                DELETE FROM message_marks WHERE message_id=$id;
                DELETE FROM messages_fts WHERE rowid=$id;
                DELETE FROM messages WHERE id=$id;
                """;
            command.Parameters.AddWithValue("$id", row.Id);
            command.Parameters.AddWithValue("$source", row.Store);
            command.Parameters.AddWithValue("$account", row.Account);
            command.Parameters.AddWithValue("$folder", row.Folder);
            command.Parameters.AddWithValue("$key", row.SourceKey);
            command.Parameters.AddWithValue("$raw", (object?)row.RawPath ?? DBNull.Value);
            command.Parameters.AddWithValue("$read", row.IsRead);
            command.Parameters.AddWithValue("$folderKey", folder.Key);
            command.Parameters.AddWithValue("$file", file);
            command.Parameters.AddWithValue("$seq", seqs.TryGetValue(row.Id, out var seq) ? seq : DBNull.Value);
            command.Parameters.AddWithValue("$now", now);
            command.ExecuteNonQuery();
        }
        command.Parameters.Clear();
        command.CommandText = "DELETE FROM local_folders WHERE folder_key=$key";
        command.Parameters.AddWithValue("$key", folder.Key);
        command.ExecuteNonQuery();
        if (ImportedFolder(folder) is var (account, archiveFolder))
        {
            command.Parameters.Clear();
            command.CommandText = "DELETE FROM archive_folders WHERE source=$source AND account=$account AND folder=$folder";
            command.Parameters.AddWithValue("$source", MailContract.ArchiveSource);
            command.Parameters.AddWithValue("$account", account);
            command.Parameters.AddWithValue("$folder", archiveFolder);
            command.ExecuteNonQuery();
            StrikeSavedCounts(connection, transaction, account, archiveFolder);
        }
        // Batch plans can include empty parents as later steps; keep their existing behavior unless requested.
        if (pruneEmptyParents) PruneEmptyParents(connection, transaction, folder.Parent, execute: true);
        transaction.Commit();

        // 4. The location joins the archive list, so the folder can be read in the Archive window.
        var store = new ArchiveStore(paths);
        var registered = false;
        if (!store.Archives.Any(archive => string.Equals(Path.GetFullPath(archive.Location), Path.GetFullPath(location), StringComparison.OrdinalIgnoreCase)))
        {
            store.Add(location);
            registered = true;
        }
        return new(true, plan, backupFile, rows.Count, rows.Count - visible.Count, comparison, registered);
    }

    private static (string Account, string Folder)? ImportedFolder(FolderConversion.LocalFolder folder)
        => folder.Origin == MailContract.ArchiveSource && folder.OriginKey?.Split('|', 2) is [var account, var archiveFolder]
            ? (account, archiveFolder) : null;

    /// <summary>Removes only empty imported containers left above archived mail. Preview does not write;
    /// native folders, standard folders and any parent still holding mail, drafts or children are retained.</summary>
    public IReadOnlyList<EmptyParent> PruneEmptyParents(string folderKey, bool execute = false)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = paths.DatabasePath, Mode = execute ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
            Pooling = false, DefaultTimeout = 30 }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: !execute);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='archived_messages'";
        var removed = Convert.ToInt64(command.ExecuteScalar()) == 0 ? []
            : PruneEmptyParents(connection, transaction, folderKey, execute);
        transaction.Commit();
        return removed;
    }

    private static List<EmptyParent> PruneEmptyParents(SqliteConnection connection, SqliteTransaction transaction,
        string? key, bool execute)
    {
        var removed = new List<EmptyParent>();
        while (key is not null)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT parent_key,name,kind,origin,origin_key FROM local_folders WHERE folder_key=$key";
            command.Parameters.AddWithValue("$key", key);
            string? parent, originKey;
            string name;
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read() || reader.GetString(2) != LocalFolderKinds.User
                    || reader.GetString(3) != MailContract.ArchiveSource) break;
                parent = reader.IsDBNull(0) ? null : reader.GetString(0);
                name = reader.GetString(1);
                originKey = reader.IsDBNull(4) ? null : reader.GetString(4);
            }
            if (originKey?.Split('|', 2) is not [var account, var folder]) break;
            command.CommandText = """
                SELECT EXISTS(SELECT 1 FROM archived_messages WHERE source=$archive AND account=$account
                               AND substr(folder,1,length($prefix))=$prefix)
                   AND NOT EXISTS(SELECT 1 FROM messages WHERE source=$live AND folder=$key)
                   AND NOT EXISTS(SELECT 1 FROM messages WHERE source=$archive AND account=$account
                                   AND (folder=$folder OR substr(folder,1,length($prefix))=$prefix))
                   AND NOT EXISTS(SELECT 1 FROM draft_folders WHERE folder_key=$key)
                   AND NOT EXISTS(SELECT 1 FROM local_folders WHERE parent_key=$key
                                   AND folder_key NOT IN (SELECT value FROM json_each($removedKeys)))
                   AND NOT EXISTS(SELECT 1 FROM archive_folders WHERE source=$archive AND account=$account
                                   AND substr(folder,1,length($prefix))=$prefix
                                   AND folder NOT IN (SELECT value FROM json_each($removedPaths)))
                """;
            command.Parameters.AddWithValue("$archive", MailContract.ArchiveSource);
            command.Parameters.AddWithValue("$live", MailContract.LiveSource);
            command.Parameters.AddWithValue("$account", account);
            command.Parameters.AddWithValue("$folder", folder);
            command.Parameters.AddWithValue("$prefix", folder + "/");
            command.Parameters.AddWithValue("$removedKeys", JsonSerializer.Serialize(removed.Select(row => row.Key)));
            command.Parameters.AddWithValue("$removedPaths", JsonSerializer.Serialize(removed.Select(row => row.Folder)));
            if (Convert.ToInt64(command.ExecuteScalar()) == 0) break;
            removed.Add(new(key, parent, name, account, folder));
            if (execute)
            {
                command.CommandText = """
                    DELETE FROM local_folders WHERE folder_key=$key;
                    DELETE FROM archive_folders WHERE source=$archive AND account=$account AND folder=$folder;
                    """;
                command.ExecuteNonQuery();
                StrikeSavedCounts(connection, transaction, account, folder);
            }
            key = parent;
        }
        return removed;
    }

    private (int Live, int Imported, string[] Refusals) Inspect(FolderConversion.LocalFolder folder)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = paths.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return Inspect(folder, connection, null);
    }

    /// <summary>Counts what would be removed and lists every reason not to: a standard folder, subfolders,
    /// drafts filed in it, or a reply or forward still waiting to be sent from one of its messages.</summary>
    private static (int Live, int Imported, string[] Refusals) Inspect(FolderConversion.LocalFolder folder,
        SqliteConnection connection, SqliteTransaction? transaction)
    {
        var refusals = new List<string>();
        if (StandardKinds.Contains(folder.Kind)) refusals.Add($"{folder.Name} is a standard folder");
        var rows = FolderRows(connection, transaction, folder);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        long Count(string sql, params (string Name, object Value)[] values)
        {
            command.Parameters.Clear();
            command.CommandText = sql;
            foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
            return Convert.ToInt64(command.ExecuteScalar());
        }
        if (Count("SELECT count(*) FROM local_folders WHERE parent_key=$key", ("$key", folder.Key)) > 0)
            refusals.Add("it has subfolders");
        if (Count("SELECT count(*) FROM draft_folders WHERE folder_key=$key", ("$key", folder.Key)) > 0)
            refusals.Add("drafts are filed in it");
        var ids = JsonSerializer.Serialize(rows.Select(row => row.Id));
        if (Count("SELECT count(*) FROM message_marks WHERE message_id IN (SELECT value FROM json_each($ids))", ("$ids", ids)) > 0)
            refusals.Add("it contains replied or forwarded marks that archive storage cannot preserve yet");
        if (Count("SELECT count(*) FROM outgoing_origins o JOIN outgoing_messages m ON m.id=o.outgoing_id "
                + "WHERE m.status<>'sent' AND o.message_id IN (SELECT value FROM json_each($ids))", ("$ids", ids)) > 0)
            refusals.Add("an unsent reply or forward was written from one of its messages");
        return (rows.Count(row => row.Store == MailContract.LiveSource), rows.Count(row => row.Store == MailContract.ArchiveSource), refusals.ToArray());
    }

    /// <summary>Every row the folder holds, shown or hidden: the mailbox's own rows filed in it and, for a folder
    /// adopted from an import, all the import's rows for that path. Hidden duplicates go too, or the tree would
    /// make the folder again for them; they are written down like the rest.</summary>
    private static List<FolderConversion.Source> FolderRows(SqliteConnection connection, SqliteTransaction? transaction,
        FolderConversion.LocalFolder folder)
    {
        var rows = new List<FolderConversion.Source>();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        void Read()
        {
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetBoolean(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        const string Columns = "SELECT id,source,account,folder,source_key,is_read,date_utc,raw_path FROM messages ";
        command.CommandText = Columns + "WHERE source=$live AND folder=$key AND converted_draft_id IS NULL";
        command.Parameters.AddWithValue("$live", MailContract.LiveSource);
        command.Parameters.AddWithValue("$key", folder.Key);
        Read();
        if (ImportedFolder(folder) is var (account, archiveFolder))
        {
            command.Parameters.Clear();
            command.CommandText = Columns + "WHERE source=$archive AND account=$account AND folder=$folder AND converted_draft_id IS NULL";
            command.Parameters.AddWithValue("$archive", MailContract.ArchiveSource);
            command.Parameters.AddWithValue("$account", account);
            command.Parameters.AddWithValue("$folder", archiveFolder);
            Read();
        }
        return rows;
    }

    private static void StrikeSavedCounts(SqliteConnection connection, SqliteTransaction transaction, string account, string folder)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT value FROM settings WHERE key=$key";
        command.Parameters.AddWithValue("$key", SavedCountsSetting);
        if (command.ExecuteScalar() is not string json || JsonNode.Parse(json) is not JsonObject saved) return;
        foreach (var list in new[] { "Folders", "ArchiveUnread" })
            if (saved[list] is JsonArray entries)
                foreach (var entry in entries.Where(entry => (string?)entry?["Account"] == account && (string?)entry?["Folder"] == folder).ToArray())
                    entries.Remove(entry);
        command.CommandText = "UPDATE settings SET value=$value WHERE key=$key";
        command.Parameters.AddWithValue("$value", saved.ToJsonString());
        command.ExecuteNonQuery();
    }
}
