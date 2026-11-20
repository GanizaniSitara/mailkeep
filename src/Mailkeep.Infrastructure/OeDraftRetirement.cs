using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Mailkeep.Infrastructure;

public sealed partial class OeDraftConversion
{
    public sealed record RetirementOriginal(long Id, string Source, string Account, string Folder,
        string SourceKey, string RawPath, string DraftId, string Sha256);
    public sealed record RetirementResult(int Removed, string ExistingBackup, int ReceiptsRetained);

    // Owner-approved disposal of exact hidden originals, never an outgoing draft or raw-cache sweep.
    public RetirementResult RetireOriginals(IReadOnlyCollection<RetirementOriginal> reviewed, string existingBackup,
        CancellationToken cancellationToken = default)
    {
        if (reviewed.Count == 0 || reviewed.Select(r => r.Id).Distinct().Count() != reviewed.Count
            || reviewed.Any(r => r.Id <= 0 || r.Source is not ("archive" or "oe_archive")
                || !(r.Account == "OE Classic" || r.Account.StartsWith("OE Classic ", StringComparison.Ordinal))
                || r.Folder.ToLowerInvariant() is not ("drafts" or "outbox") || r.DraftId != "oe_draft_" + r.Id
                || r.Sha256.Length != 64))
            throw new InvalidDataException("Exact reviewed converted OE originals are required.");
        var backup = Path.GetFullPath(existingBackup);
        if (string.Equals(backup, Path.GetFullPath(paths.DatabasePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The live database is not a backup.");
        if (!File.Exists(backup)) throw new FileNotFoundException("Existing backup not found.", backup);
        using var prior = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = backup, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        prior.Open();
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = paths.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        c.Open();
        using var tx = c.BeginTransaction();
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='bulk_bindings'";
        var bound = Convert.ToInt64(q.ExecuteScalar()) > 0;
        q.CommandText = """
            SELECT count(*) FROM messages INDEXED BY sqlite_autoindex_messages_1
            WHERE source='live' AND account='__mailkeep_local_copy__'
              AND source_key IN (SELECT 'copy:adopted:'||value FROM json_each($ids))
            """;
        q.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(reviewed.Select(r => r.Id)));
        if (Convert.ToInt64(q.ExecuteScalar()) != 0)
            throw new InvalidDataException("A reviewed original still has a visible Local copy.");
        foreach (var row in reviewed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var old = prior.CreateCommand();
            old.CommandText = "SELECT source,account,folder,source_key FROM messages WHERE id=$id";
            old.Parameters.AddWithValue("$id", row.Id);
            using (var r = old.ExecuteReader())
                if (!r.Read() || r.GetString(0) != row.Source || r.GetString(1) != row.Account
                    || r.GetString(2) != row.Folder || r.GetString(3) != row.SourceKey)
                    throw new InvalidDataException("Existing backup does not contain a reviewed original.");
            q.Parameters.Clear();
            q.Parameters.AddWithValue("$id", row.Id);
            q.CommandText = "SELECT source,account,folder,source_key,raw_path,converted_draft_id FROM messages WHERE id=$id";
            using (var r = q.ExecuteReader())
                if (!r.Read() || r.GetString(0) != row.Source || r.GetString(1) != row.Account
                    || r.GetString(2) != row.Folder || r.GetString(3) != row.SourceKey
                    || r.IsDBNull(4) || r.GetString(4) != row.RawPath || r.IsDBNull(5) || r.GetString(5) != row.DraftId)
                    throw new InvalidDataException("A reviewed hidden original changed.");
            q.CommandText = "SELECT source,account,folder,source_key,draft_id,sha256 FROM oe_draft_conversions WHERE message_id=$id";
            using (var r = q.ExecuteReader())
                if (!r.Read() || r.GetString(0) != row.Source || r.GetString(1) != row.Account
                    || r.GetString(2) != row.Folder || r.GetString(3) != row.SourceKey
                    || r.GetString(4) != row.DraftId || r.GetString(5) != row.Sha256)
                    throw new InvalidDataException("A reviewed conversion receipt changed.");
            q.Parameters.AddWithValue("$draft", row.DraftId);
            q.CommandText = """
                SELECT (SELECT count(*) FROM outgoing_messages WHERE id=$draft)
                  +(SELECT count(*) FROM outgoing_origins WHERE message_id=$id)
                """;
            if (Convert.ToInt64(q.ExecuteScalar()) != 0)
                throw new InvalidDataException("A reviewed original still has an active draft, reference or Local copy.");
            if (bound)
            {
                q.Parameters.AddWithValue("$source", row.Source);
                q.Parameters.AddWithValue("$account", row.Account);
                q.Parameters.AddWithValue("$folder", row.Folder);
                q.CommandText = "SELECT count(*) FROM bulk_bindings WHERE source=$source AND account=$account AND folder=$folder";
                if (Convert.ToInt64(q.ExecuteScalar()) != 0)
                    throw new InvalidDataException("A reviewed original belongs to portable storage; use its journal workflow.");
            }
        }
        foreach (var row in reviewed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            q.Parameters.Clear();
            q.Parameters.AddWithValue("$id", row.Id);
            q.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            q.CommandText = """
                INSERT OR IGNORE INTO local_deletions(source,account,folder,source_key,deleted_at)
                  SELECT source,account,folder,source_key,$now FROM messages WHERE id=$id;
                DELETE FROM messages_fts WHERE rowid=$id;
                DELETE FROM message_marks WHERE message_id=$id;
                DELETE FROM messages WHERE id=$id;
                """;
            q.ExecuteNonQuery();
        }
        tx.Commit();
        return new(reviewed.Count, backup, reviewed.Count);
    }
}
