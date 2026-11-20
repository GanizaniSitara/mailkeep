using Mailkeep.Core;
using MailKit;
using MailKit.Search;
using MimeKit;

namespace Mailkeep.Infrastructure;

internal sealed record ConnectedCachedMessage(long Id, uint Uid, uint Validity, string Key, bool IsRead, bool PendingRead);
internal sealed record ConnectedSnapshot(long RemoteOperationId, IReadOnlyList<ConnectedCachedMessage> Messages,
    IReadOnlySet<string> SuppressedKeys);

public sealed partial class MailDatabase
{
    internal ConnectedSnapshot ReadConnectedSnapshot(MailAccount account)
    {
        if (!account.IsConnected) throw new ArgumentException("Connected account required.");
        using var connection = OpenProjection();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT coalesce(max(id),0) FROM remote_operations";
        var revision = (long)query.ExecuteScalar()!;
        query.Parameters.AddWithValue("$account", account.Name);
        query.Parameters.AddWithValue("$folder", account.FolderLabel);
        query.CommandText = "SELECT source_key FROM remote_operations WHERE account=$account AND folder=$folder AND kind='read' AND completed_at IS NULL";
        var pending = new HashSet<string>(StringComparer.Ordinal);
        using (var rows = query.ExecuteReader()) while (rows.Read()) pending.Add(rows.GetString(0));
        query.CommandText = "SELECT source_key FROM local_deletions WHERE source='live' AND account=$account AND folder=$folder";
        var suppressed = new HashSet<string>(StringComparer.Ordinal);
        using (var rows = query.ExecuteReader()) while (rows.Read()) suppressed.Add(rows.GetString(0));
        query.CommandText = """
            SELECT id,server_uid,uidvalidity,source_key,is_read FROM messages
            WHERE source='live' AND account=$account AND folder=$folder AND server_uid>0
              AND source_key=CAST(uidvalidity AS TEXT)||':'||CAST(server_uid AS TEXT)
              AND converted_draft_id IS NULL
            """;
        var messages = new List<ConnectedCachedMessage>();
        using (var rows = query.ExecuteReader())
            while (rows.Read()) messages.Add(new(rows.GetInt64(0), checked((uint)rows.GetInt64(1)),
                checked((uint)rows.GetInt64(2)), rows.GetString(3), rows.GetBoolean(4), pending.Contains(rows.GetString(3))));
        return new(revision, messages, suppressed);
    }

    internal void ReconcileConnectedSnapshot(MailAccount account, ConnectedSnapshot snapshot, uint validity,
        IReadOnlySet<uint> present, IReadOnlyDictionary<uint, bool> readStates)
    {
        if (!account.IsConnected) throw new ArgumentException("Connected account required.");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var message in snapshot.Messages)
        {
            using var query = connection.CreateCommand();
            query.Transaction = transaction;
            query.Parameters.AddWithValue("$id", message.Id);
            query.Parameters.AddWithValue("$account", account.Name);
            query.Parameters.AddWithValue("$folder", account.FolderLabel);
            query.Parameters.AddWithValue("$uid", message.Uid);
            query.Parameters.AddWithValue("$validity", message.Validity);
            query.Parameters.AddWithValue("$key", message.Key);
            const string sameRow = "source='live' AND account=$account AND folder=$folder AND id=$id AND server_uid=$uid AND uidvalidity=$validity AND source_key=$key";
            if (message.Validity != validity || !present.Contains(message.Uid))
            {
                // Server absence retires a cache row, not a permanent local deletion intent.
                // Restrict to the exact rows observed before networking; a concurrent filing or
                // newer sync must never lose its changed/new rows to an older server snapshot.
                query.CommandText = $"DELETE FROM messages_fts WHERE rowid IN (SELECT id FROM messages WHERE {sameRow}); DELETE FROM messages WHERE {sameRow}";
                query.ExecuteNonQuery();
            }
            else if (!message.PendingRead && readStates.TryGetValue(message.Uid, out var isRead))
            {
                query.Parameters.AddWithValue("$read", isRead);
                query.Parameters.AddWithValue("$before", message.IsRead);
                query.Parameters.AddWithValue("$revision", snapshot.RemoteOperationId);
                query.CommandText = $"""
                    UPDATE messages SET is_read=$read WHERE {sameRow} AND is_read=$before AND is_read<>$read
                      AND NOT EXISTS(SELECT 1 FROM remote_operations WHERE account=$account AND folder=$folder
                        AND source_key=$key AND kind='read' AND (completed_at IS NULL OR id>$revision))
                    """;
                query.ExecuteNonQuery();
            }
        }
        transaction.Commit();
        FolderStorage.Flush();
    }
}

public sealed partial class MailSyncService
{
    private async Task<(int Scanned, uint MaxUid)> SyncConnectedMailboxAsync(MailAccount account,
        IMailFolder folder, CancellationToken cancellationToken, IProgress<ReceiveProgress>? progress, Action messageStored)
    {
        var snapshot = _database.ReadConnectedSnapshot(account);
        var present = await folder.SearchAsync(SearchQuery.All, cancellationToken);
        var flags = present.Count == 0 ? [] : await folder.FetchAsync(present,
            MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, cancellationToken);
        var readStates = flags.Where(message => message.Flags.HasValue).ToDictionary(message => message.UniqueId.Id,
            message => message.Flags!.Value.HasFlag(MessageFlags.Seen));
        var cached = snapshot.Messages.Where(message => message.Validity == folder.UidValidity).Select(message => message.Uid).ToHashSet();
        var missing = present.Where(uid => !cached.Contains(uid.Id)
            && !snapshot.SuppressedKeys.Contains($"{folder.UidValidity}:{uid.Id}")).OrderBy(uid => uid.Id).ToArray();
        var downloaded = 0;
        progress?.Report(new(account.Name, account.Address, "Downloading", 0, missing.Length));
        foreach (var uid in missing)
        {
            await using var stream = await folder.GetStreamAsync(uid, cancellationToken);
            using var raw = new MemoryStream();
            await stream.CopyToAsync(raw, cancellationToken);
            raw.Position = 0;
            using var message = await MimeMessage.LoadAsync(raw, cancellationToken);
            raw.Position = 0;
            if (await _database.UpsertMimeMessageAsync(MailContract.LiveSource, account.Name, account.FolderLabel,
                $"{folder.UidValidity}:{uid.Id}", uid.Id, folder.UidValidity, message, raw,
                readStates.GetValueOrDefault(uid.Id), cancellationToken) > 0) messageStored();
            progress?.Report(new(account.Name, account.Address, "Downloading", ++downloaded, missing.Length));
        }
        cancellationToken.ThrowIfCancellationRequested();
        // No database transaction is held while awaiting the server. Only a completed,
        // successful inventory/download pass can retire stale cache entries.
        _database.ReconcileConnectedSnapshot(account, snapshot, folder.UidValidity,
            present.Select(uid => uid.Id).ToHashSet(), readStates);
        return (present.Count, present.Count == 0 ? 0 : present.Max(uid => uid.Id));
    }
}
