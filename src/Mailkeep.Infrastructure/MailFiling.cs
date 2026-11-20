using Mailkeep.Core;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Infrastructure;

/// <summary>Agent filing follows the application's local/adopted/server boundaries, with a read-only preview.</summary>
public sealed class MailFiling(MailkeepServices services)
{
    public sealed record Folder(string Key, string Name, string Path, string Kind, bool CanMoveInto);
    public sealed record Plan(long Id, string Subject, string Source, string Account, string FromFolder,
        string SourceKey, long? ServerUid, string Destination, string DestinationName, string Action);
    public sealed record Result(bool Executed, int Changed, long? ResultId, Plan Plan, bool RemoteQueued = false);

    public Folder[] Folders()
    {
        using var connection = Open(services.Paths.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT folder_key,parent_key,name,kind FROM local_folders ORDER BY sort_order,name";
        var rows = new Dictionary<string, (string? Parent, string Name, string Kind)>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) rows.Add(reader.GetString(0), (reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        string PathFor(string key, HashSet<string> seen)
        {
            if (!seen.Add(key)) throw new InvalidDataException("Local folder hierarchy contains a cycle.");
            var row = rows[key];
            return row.Parent is null ? row.Name : PathFor(row.Parent, seen) + "/" + row.Name;
        }
        return rows.Select(row => new Folder(row.Key, row.Value.Name, PathFor(row.Key, []), row.Value.Kind,
            row.Value.Kind is not (LocalFolderKinds.Drafts or LocalFolderKinds.Outbox))).ToArray();
    }

    public Plan Preview(string messageId, string destination, bool server = false)
    {
        if (string.IsNullOrWhiteSpace(destination)) throw new ArgumentException("Choose a destination folder.");
        long id;
        (string Source, string Account, string Folder, string Key)? locator = null;
        if (messageId.StartsWith("msg_", StringComparison.Ordinal)
            && AdoptionCleanup.ResolveReference(services.Paths, messageId) is { LocalId: { } retained })
            id = retained;
        else if (messageId.StartsWith("msg_", StringComparison.Ordinal))
        {
            using var catalog = Open(services.Paths.SearchCatalogPath);
            using var resolve = catalog.CreateCommand();
            resolve.CommandText = "SELECT local_id,source,account,folder,source_key FROM documents WHERE message_ref=$ref";
            resolve.Parameters.AddWithValue("$ref", messageId);
            using var reader = resolve.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(0)) throw new KeyNotFoundException("Message reference not found.");
            id = reader.GetInt64(0);
            var catalogSource = reader.GetString(1);
            locator = (catalogSource is MailContract.LocalSource or MailContract.ConnectedSource ? MailContract.LiveSource : catalogSource,
                reader.GetString(2), reader.GetString(3), reader.GetString(4));
        }
        else if (!long.TryParse(messageId, out id)) throw new ArgumentException("move requires a numeric message ID or msg_ reference.");
        using var connection = Open(services.Paths.DatabasePath);
        id = AdoptionCleanup.ResolveId(connection, id);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT source,account,folder,source_key,server_uid,subject,converted_draft_id FROM messages WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        string source, account, from, key, subject;
        long? uid;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new KeyNotFoundException("Message not found.");
            source = reader.GetString(0); account = reader.GetString(1); from = reader.GetString(2); key = reader.GetString(3);
            uid = reader.IsDBNull(4) ? null : reader.GetInt64(4); subject = reader.GetString(5);
            if (!reader.IsDBNull(6)) throw new InvalidOperationException("This imported occurrence was converted to a draft; use its draft instead.");
        }
        if (locator is { } located && located != (source, account, from, key))
            throw new InvalidOperationException("This reference is stale. Search again or use the current numeric message ID.");
        var configured = services.Accounts.Load().SingleOrDefault(a => a.Name.Equals(account, StringComparison.OrdinalIgnoreCase));
        var localCopy = source == MailContract.LiveSource && uid is null
            && (key.StartsWith("sent:", StringComparison.Ordinal) || account == MailContract.LocalCopyAccount && key.StartsWith("copy:", StringComparison.Ordinal));
        var connected = source == MailContract.LiveSource && !localCopy && configured?.IsConnected == true;
        if (server)
        {
            if (!connected || uid is null || from != configured!.FolderLabel)
                throw new InvalidOperationException("--server requires a message from a Connected mailbox.");
            if (destination == configured.Mailbox) throw new InvalidOperationException("Choose a different server folder.");
            return new(id, subject, source, account, from, key, uid, destination, destination, "server_move");
        }
        if (connected) throw new InvalidOperationException("Connected mail requires --server to move on its provider; local filing does not move server mail.");
        if (source != MailContract.ArchiveSource && !(source == MailContract.LiveSource && (localCopy || configured?.IsConnected == false)))
            throw new InvalidOperationException("This message is not owned by a configured Local mailbox.");
        if (source == MailContract.ArchiveSource)
        {
            command.CommandText = """
                SELECT EXISTS(SELECT 1 FROM local_deletions WHERE source=$live AND account=$account AND source_key=$key)
                    AND NOT EXISTS(SELECT 1 FROM messages WHERE source=$live AND account=$account AND source_key=$key)
                """;
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$live", MailContract.LiveSource);
            command.Parameters.AddWithValue("$account", MailContract.LocalCopyAccount);
            command.Parameters.AddWithValue("$key", MailContract.AdoptedCopyKeyPrefix + id);
            if (Convert.ToInt64(command.ExecuteScalar()) != 0)
                throw new InvalidOperationException("The Local copy of this imported message was deleted. Filing must not restore it.");
        }
        var folders = Folders();
        var target = folders.SingleOrDefault(folder => folder.Key == destination);
        if (target is null)
        {
            var matches = folders.Where(folder => folder.Path.Equals(destination, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) throw new ArgumentException("Destination must be a unique local folder key or full path from folders --local.");
            target = matches[0];
        }
        if (!target.CanMoveInto) throw new InvalidOperationException("Messages cannot be filed into Drafts or Outbox.");
        return new(id, subject, source, account, from, key, uid, target.Key, target.Path,
            source == MailContract.ArchiveSource ? "adopt_imported_using_existing_app_behavior" : "local_move");
    }

    public async Task<Result> MoveAsync(string messageId, string destination, bool execute = false, bool server = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = Preview(messageId, destination, server);
        if (!execute) return new(false, 0, null, plan);
        if (server) services.Database.Initialize();
        // Existing stores only. Preview above deliberately bypasses both schema and catalog initialization.
        var current = services.Database.GetMessage(plan.Id)?.Summary ?? throw new KeyNotFoundException("Message no longer exists.");
        if ((current.Source, current.Account, current.Folder, current.SourceKey, current.ServerUid)
            != (plan.Source, plan.Account, plan.FromFolder, plan.SourceKey, plan.ServerUid))
            throw new InvalidOperationException("The message changed before filing. Preview it again.");
        if (server)
            return new(true, await services.Sync.MoveConnectedAsync([current], plan.Destination, cancellationToken), null, plan, true);
        if (plan.Source != MailContract.ArchiveSource)
            return new(true, services.Database.MoveMessages([plan.Id], plan.Destination), plan.Id, plan);
        var changed = services.Database.AdoptMessages([plan.Id], plan.Destination);
        using var connection = Open(services.Paths.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM messages WHERE source=$source AND account=$account AND source_key=$key AND folder=$folder";
        command.Parameters.AddWithValue("$source", MailContract.LiveSource);
        command.Parameters.AddWithValue("$account", MailContract.LocalCopyAccount);
        command.Parameters.AddWithValue("$key", MailContract.AdoptedCopyKeyPrefix + plan.Id);
        command.Parameters.AddWithValue("$folder", plan.Destination);
        return new(true, changed, Convert.ToInt64(command.ExecuteScalar()), plan);
    }

    private static SqliteConnection Open(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Mailkeep store does not exist.", path);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
}
