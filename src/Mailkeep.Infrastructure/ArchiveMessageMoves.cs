using Mailkeep.Core;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Infrastructure;

public sealed partial class MailDatabase
{
    /// <summary>Files imported occurrences within their existing imported mailbox.</summary>
    public int MoveImportedMessages(IReadOnlyList<long> messageIds, string account, string folder)
    {
        if (messageIds.Count == 0) return 0;
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = """
            SELECT 1 FROM archive_folders WHERE source='archive' AND account=$account
                AND (folder=$folder OR substr(folder,1,length($folder)+1)=$folder||'/')
            UNION ALL SELECT 1 FROM messages WHERE source='archive' AND account=$account
                AND (folder=$folder OR substr(folder,1,length($folder)+1)=$folder||'/') LIMIT 1
            """;
        q.Parameters.AddWithValue("$account", account); q.Parameters.AddWithValue("$folder", folder);
        if (q.ExecuteScalar() is null) throw new InvalidOperationException("The destination imported folder no longer exists.");
        q.CommandText = "SELECT source,account,folder,source_key,converted_draft_id FROM messages WHERE id=$id";
        var idParameter = q.Parameters.Add("$id", SqliteType.Integer);
        var rows = new List<(long Id, string Folder, string Key)>();
        foreach (var id in messageIds.Distinct())
        {
            idParameter.Value = id;
            using var r = q.ExecuteReader();
            if (!r.Read()) throw new KeyNotFoundException("A selected message no longer exists.");
            if (r.GetString(0) != MailContract.ArchiveSource || r.GetString(1) != account || !r.IsDBNull(4))
                throw new InvalidOperationException("Only messages from this imported mailbox can be moved here.");
            if (r.GetString(2) != folder) rows.Add((id, r.GetString(2), r.GetString(3)));
        }
        // Retain emptied folders and suppress a later import of the old location.
        // For portable files, the normal move journal also retains the old occurrence.
        q.Parameters.AddWithValue("$from", ""); q.Parameters.AddWithValue("$key", "");
        q.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        foreach (var row in rows)
        {
            idParameter.Value = row.Id;
            q.Parameters["$from"].Value = row.Folder; q.Parameters["$key"].Value = row.Key;
            q.CommandText = """
                INSERT OR IGNORE INTO archive_folders VALUES('archive',$account,$from);
                INSERT OR IGNORE INTO archive_folders VALUES('archive',$account,$folder);
                INSERT OR IGNORE INTO local_deletions VALUES('archive',$account,$from,$key,$now);
                UPDATE messages SET folder=$folder WHERE id=$id;
                UPDATE settings SET value=CAST(value AS INTEGER)+1 WHERE key='search.operational.change-sequence';
                INSERT INTO search_archive_changes
                    SELECT $id,CAST(value AS INTEGER) FROM settings WHERE key='search.operational.change-sequence'
                    ON CONFLICT(message_id) DO UPDATE SET revision=excluded.revision;
                """;
            try { q.ExecuteNonQuery(); }
            catch (SqliteException error) when (error.SqliteErrorCode == 19)
            {
                throw new InvalidOperationException("The destination already contains one of these message identities. No messages were moved.", error);
            }
        }
        tx.Commit();
        FolderStorage.Flush();
        return rows.Count;
    }
}
