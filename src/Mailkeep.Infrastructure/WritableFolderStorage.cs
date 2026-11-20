using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Infrastructure;

/// <summary>The common portable-folder writer. Messages in the coordinator are a rebuildable projection.</summary>
public sealed partial class WritableFolderStorage(AppPaths paths, RawBlobStore blobs)
{
    public const string MarkerName = "local-folder-storage.json"; // Old-file upgrade metadata only.
    public string DirectoryPath => Path.Combine(paths.Root, "bound-folders");
    public bool Enabled => Directory.Exists(DirectoryPath);
    private static readonly string[] Columns = ["id", "source", "account", "folder", "source_key", "server_uid", "uidvalidity",
        "subject", "sender", "sender_name", "recipients_json", "date_utc", "size", "is_read", "attachment_count", "body_text", "body_html", "raw_path"];
    public sealed record Registration(string Key, string Uuid, string File, bool Created);
    public sealed record Status(bool Enabled, int Pending, Registration[] Folders, string? OnlyNewRootFolder = null, string? Recovery = null);

    internal void Initialize()
    {
        var oldTriggers = false;
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = paths.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5 }.ToString()))
        {
            c.Open();
            if (BulkFolderMigration.HasTable(c, "folder_storage_registry")
                && Convert.ToInt64(BulkFolderMigration.Scalar(c, "SELECT count(*) FROM folder_storage_registry")) != 0
                || BulkFolderMigration.HasTable(c, "folder_storage_journal")
                && Convert.ToInt64(BulkFolderMigration.Scalar(c, "SELECT count(*) FROM folder_storage_journal")) != 0)
                throw new InvalidDataException("Legacy pilot storage requires an offline upgrade; the retired writer cannot open it. Run folder storage consolidate-pilots before reopening.");
            oldTriggers = Convert.ToInt64(BulkFolderMigration.Scalar(c,
                "SELECT count(*) FROM sqlite_master WHERE type='trigger' AND name GLOB 'fs_*'")) != 0;
        }
        if (oldTriggers) { using var c = Open(); RetirePilotWriter(c); }
        Flush();
    }

    public Status Inspect()
    {
        if (!File.Exists(paths.DatabasePath))
            return new(Enabled, 0, [], Recovery: Enabled ? "Rebuild the portable projection before use." : null);
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = paths.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        if (!BulkFolderMigration.HasTable(c, "bulk_bindings")) return new(false, 0, []);
        using var q = c.CreateCommand();
        // Account-scoped format-2 files still participate in the imported/local merged view.
        // Only a logical folder owns the entire visible list and can replace that view.
        q.CommandText = "SELECT folder,uuid,created FROM bulk_bindings WHERE source='live' AND "
            + (BulkFolderMigration.HasColumn(c, "bulk_bindings", "logical") ? "logical=1" : "0")
            + " ORDER BY folder,uuid";
        var folders = new List<Registration>();
        using (var rows = q.ExecuteReader())
            while (rows.Read())
            {
                var uuid = rows.GetString(1);
                if (!Guid.TryParseExact(uuid, "N", out _)) throw new InvalidDataException("Invalid portable folder UUID.");
                folders.Add(new(rows.GetString(0), uuid, Path.Combine(DirectoryPath, uuid, FolderBundle.FileName), rows.GetBoolean(2)));
            }
        q.CommandText = "SELECT count(*) FROM bulk_journal";
        return new(true, Convert.ToInt32(q.ExecuteScalar()), folders.ToArray());
    }

    internal void CheckProjectionStorage(SqliteConnection c)
    {
        if (BulkFolderMigration.HasTable(c, "bulk_bindings"))
            new BulkFolderMigration(paths).CheckProjectionStorage(c);
    }

    public int Flush()
    {
        BulkFolderMigration.CheckAvailable(paths);
        try { return new BulkFolderMigration(paths).Flush(); }
        catch (Exception error) when (error is IOException or SqliteException)
        {
            var pending = 0;
            try
            {
                using var c = Open();
                if (BulkFolderMigration.HasTable(c, "bulk_journal"))
                    pending = Convert.ToInt32(BulkFolderMigration.Scalar(c, "SELECT count(*) FROM bulk_journal"));
            }
            catch (SqliteException) { }
            if (pending > 0) throw new FolderStorageRecoveryException(error);
            throw;
        }
    }

    public int RebuildProjection() => new BulkFolderMigration(paths).RebuildProjection();

    private static readonly string[] TriggerNames = ["fs_import_guard", "fs_import_update_guard", "fs_folder_guard", "fs_folder_add", "fs_folder_update", "fs_folder_delete", "fs_message_add", "fs_message_update", "fs_message_delete", "fs_message_guard", "fs_message_guard_update", "fs_draft_guard", "fs_mark_add", "fs_mark_delete"];


    internal static void RetirePilotWriter(SqliteConnection c)
    {
        var present = new HashSet<string>();
        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT name FROM sqlite_master WHERE type='trigger' AND name GLOB 'fs_*'";
            using var r = q.ExecuteReader(); while (r.Read()) present.Add(r.GetString(0));
        }
        foreach (var name in TriggerNames.Where(present.Contains))
            BulkFolderMigration.Scalar(c, "DROP TRIGGER IF EXISTS " + name);
        if (BulkFolderMigration.HasTable(c, "folder_storage_registry")
            && Convert.ToInt64(BulkFolderMigration.Scalar(c, "SELECT count(*) FROM folder_storage_registry")) != 0)
            BulkFolderMigration.Scalar(c, "DELETE FROM folder_storage_registry");
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Snapshot(string alias) => "json_object(" + string.Join(',', Columns.Select(c => $"'{c}',{alias}.{c}"))
        + $",'marks',json((SELECT json_group_array(json_object('kind',kind,'marked_at',marked_at)) FROM message_marks WHERE message_id={alias}.id))"
        + $",'import_origins',json((SELECT value FROM settings WHERE key='folder-storage.origins:'||{alias}.id)))";
    private FileStream Lease()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try { return new(Path.Combine(DirectoryPath, ".operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 && clock.Elapsed < TimeSpan.FromSeconds(35))
            { Thread.Sleep(25); }
        }
    }
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000";
        command.ExecuteNonQuery();
        return connection;
    }
}
