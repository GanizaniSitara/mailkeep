using Mailkeep.Core;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Infrastructure;

public sealed record RemoteOperation(long Id, string Account, string Mailbox, string Folder,
    string SourceKey, uint Uid, uint UidValidity, string Kind, string? Destination, bool IsRead,
    int Attempts, string? Error, DateTimeOffset RetryAt);
public sealed record RemoteQueueStatus(int Pending, int Failed, string? LastError);

public sealed partial class MailDatabase
{
    private static void EnsureRemoteQueue(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS remote_operations (
                id INTEGER PRIMARY KEY AUTOINCREMENT, account TEXT NOT NULL, mailbox TEXT NOT NULL,
                folder TEXT NOT NULL, source_key TEXT NOT NULL, uid INTEGER NOT NULL, uidvalidity INTEGER NOT NULL,
                kind TEXT NOT NULL, destination TEXT, is_read INTEGER NOT NULL,
                attempts INTEGER NOT NULL DEFAULT 0, error TEXT, retry_at TEXT NOT NULL,
                completed_at TEXT);
            CREATE INDEX IF NOT EXISTS remote_operations_pending ON remote_operations(completed_at,account,mailbox,id);
            """;
        command.ExecuteNonQuery();
    }

    // The intent and its visible local effect share a transaction: neither can survive without the other.
    public int QueueRemote(MailAccount account, IReadOnlyList<MessageSummary> messages, string kind,
        string? destination = null, bool isRead = false)
    {
        if (kind is not ("delete" or "purge" or "move" or "read")) throw new ArgumentException("Invalid remote operation.");
        if (!account.IsConnected) throw new InvalidOperationException("A Connected account is required.");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var count = 0;
        foreach (var message in messages.DistinctBy(m => m.Id))
        {
            if (message.Source != MailContract.LiveSource || message.IsLocalSentCopy || message.IsLocalStoredCopy ||
                message.Account != account.Name || message.Folder != account.FolderLabel ||
                message.ServerUid is not > 0 || !uint.TryParse(message.SourceKey.Split(':')[0], out var validity) ||
                message.SourceKey != $"{validity}:{message.ServerUid}")
                throw new InvalidOperationException("Refresh the Connected selection before changing it.");
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT 1 FROM messages WHERE id=$id AND source=$source AND account=$account AND folder=$folder AND source_key=$key AND server_uid=$uid";
            command.Parameters.AddWithValue("$id", message.Id);
            command.Parameters.AddWithValue("$source", message.Source);
            command.Parameters.AddWithValue("$account", message.Account);
            command.Parameters.AddWithValue("$folder", message.Folder);
            command.Parameters.AddWithValue("$key", message.SourceKey);
            command.Parameters.AddWithValue("$uid", checked((uint)message.ServerUid.Value));
            if (command.ExecuteScalar() is null) throw new InvalidOperationException("The selected message changed. Refresh before trying again.");
            command.Parameters.AddWithValue("$mailbox", account.Mailbox);
            command.Parameters.AddWithValue("$validity", validity);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$destination", (object?)destination ?? DBNull.Value);
            command.Parameters.AddWithValue("$read", isRead);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.CommandText = """
                INSERT INTO remote_operations(account,mailbox,folder,source_key,uid,uidvalidity,kind,destination,is_read,retry_at)
                VALUES($account,$mailbox,$folder,$key,$uid,$validity,$kind,$destination,$read,$now);
                """;
            command.ExecuteNonQuery();
            if (kind == "read") command.CommandText = "UPDATE messages SET is_read=$read WHERE id=$id";
            else command.CommandText = """
                INSERT OR IGNORE INTO local_deletions(source,account,folder,source_key,deleted_at)
                VALUES($source,$account,$folder,$key,$now);
                DELETE FROM messages_fts WHERE rowid=$id;
                DELETE FROM messages WHERE id=$id;
                """;
            command.ExecuteNonQuery();
            count++;
        }
        transaction.Commit();
        return count;
    }

    public IReadOnlyList<RemoteOperation> PendingRemoteOperations()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,account,mailbox,folder,source_key,uid,uidvalidity,kind,destination,is_read,attempts,error,retry_at FROM remote_operations WHERE completed_at IS NULL ORDER BY id";
        using var reader = command.ExecuteReader();
        var result = new List<RemoteOperation>();
        while (reader.Read()) result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), checked((uint)reader.GetInt64(5)), checked((uint)reader.GetInt64(6)), reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetBoolean(9), reader.GetInt32(10),
            reader.IsDBNull(11) ? null : reader.GetString(11), DateTimeOffset.Parse(reader.GetString(12))));
        return result;
    }

    public RemoteQueueStatus RemoteStatus()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*),COALESCE(SUM(error IS NOT NULL),0),
                (SELECT error FROM remote_operations WHERE completed_at IS NULL AND error IS NOT NULL ORDER BY id LIMIT 1)
            FROM remote_operations WHERE completed_at IS NULL
            """;
        using var reader = command.ExecuteReader();
        reader.Read();
        return new(reader.GetInt32(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    public void FinishRemoteOperation(long id, string? error)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE remote_operations SET attempts=attempts+1,error=$error,retry_at=$retry,completed_at=$completed WHERE id=$id
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$retry", DateTimeOffset.UtcNow.AddSeconds(5).ToString("O"));
        command.Parameters.AddWithValue("$completed", error is null ? DateTimeOffset.UtcNow.ToString("O") : DBNull.Value);
        command.ExecuteNonQuery();
    }

    internal FileStream LockRemoteWorker() => new(_paths.DatabasePath + ".remote-worker.lock",
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
}
