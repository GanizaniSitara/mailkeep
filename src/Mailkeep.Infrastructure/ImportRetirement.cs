using System.Text.Json;
using System.Text.Json.Nodes;
using Mailkeep.Core;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Infrastructure;

/// <summary>Files every occurrence from explicitly named imports into Local Mail, then removes their empty
/// branches. Existing adoption cleanup verifies bytes, backs up the database and retains ID aliases and
/// re-import protection. No MIME blobs or Local Mail messages are deleted.</summary>
public sealed class ImportRetirement(MailkeepServices services)
{
    public sealed record Folder(string Account, string Path, int Messages);
    public sealed record Plan(string[] Accounts, string Destination, Folder[] Folders, long[] MessageIds, string[] Refusals);
    public sealed record Result(bool Executed, Plan Plan, AdoptionCleanup.Result? Cleanup, int RemovedDeclarations);

    public Result Run(string[] accounts, string destination, bool execute = false, CancellationToken token = default)
    {
        if (accounts.Length == 0 || accounts.Any(string.IsNullOrWhiteSpace)
            || accounts.Distinct(StringComparer.Ordinal).Count() != accounts.Length)
            throw new ArgumentException("Name each exact imported account once.");
        var target = new MailFiling(services).Folders().SingleOrDefault(folder => folder.Key == destination);
        if (target?.CanMoveInto != true) throw new ArgumentException("Choose a local destination key from folders --local.");
        using var connection = Open(readOnly: true);
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$accounts", JsonSerializer.Serialize(accounts));
        command.Parameters.AddWithValue("$archive", MailContract.ArchiveSource);
        command.Parameters.AddWithValue("$live", MailContract.LiveSource);
        command.Parameters.AddWithValue("$local", MailContract.LocalCopyAccount);
        command.Parameters.AddWithValue("$prefix", MailContract.AdoptedCopyKeyPrefix);
        const string Scope = "source=$archive AND account IN (SELECT value FROM json_each($accounts))";
        command.CommandText = $"SELECT id FROM messages WHERE {Scope} ORDER BY id";
        var ids = new List<long>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) ids.Add(reader.GetInt64(0));
        command.CommandText = $"SELECT account,folder,count(*) FROM messages WHERE {Scope} GROUP BY account,folder "
            + $"UNION ALL SELECT account,folder,0 FROM archive_folders f WHERE {Scope} "
            + "AND NOT EXISTS(SELECT 1 FROM messages m WHERE m.source=f.source AND m.account=f.account AND m.folder=f.folder)";
        var folders = new List<Folder>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) folders.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        var refusals = new List<string>();
        if (accounts.Any(account => !folders.Any(folder => folder.Account == account))) refusals.Add("an imported account is absent");
        long Count(string sql)
        {
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }
        if (Count($"SELECT count(*) FROM messages WHERE {Scope} AND converted_draft_id IS NOT NULL") > 0)
            refusals.Add("converted drafts require a separate review");
        if (Count("SELECT count(*) FROM local_folders f JOIN json_each($accounts) a "
            + "ON substr(f.origin_key,1,length(a.value)+1)=a.value||'|' WHERE f.origin=$archive") > 0)
            refusals.Add("an import is mirrored into the mailbox's own folder tree");
        if (Count($"SELECT count(*) FROM messages o WHERE {Scope} AND EXISTS(SELECT 1 FROM local_deletions d "
            + "WHERE d.source=$live AND d.account=$local AND d.source_key=$prefix||o.id) "
            + "AND NOT EXISTS(SELECT 1 FROM messages c WHERE c.source=$live AND c.account=$local AND c.source_key=$prefix||o.id)") > 0)
            refusals.Add("a previously deleted Local copy must not be restored");
        var plan = new Plan(accounts, target.Key, folders.ToArray(), ids.ToArray(), refusals.ToArray());
        if (!execute) return new(false, plan, null, 0);
        if (refusals.Count > 0) throw new InvalidOperationException("Import not retired: " + string.Join("; ", refusals));
        token.ThrowIfCancellationRequested();
        AdoptionCleanup.Result? cleanup = null;
        if (ids.Count > 0)
        {
            services.Database.AdoptMessages(ids, target.Key);
            var report = new AdoptionMigrationReport(services.Paths).Run(token, accounts);
            if (report.Links.Length != ids.Count || !report.Links.Select(link => link.Original?.Id).ToHashSet().SetEquals(ids.Select(id => (long?)id))
                || report.Links.Any(link => link.Copies.Length != 1 || link.Copies[0].Folder != target.Key))
                throw new InvalidDataException("Copies need review; original branches have not been removed.");
            cleanup = new AdoptionCleanup(services.Paths).Apply(report, token, accounts);
        }
        return new(true, plan, cleanup, RemoveEmptyDeclarations(accounts));
    }

    private int RemoveEmptyDeclarations(string[] accounts)
    {
        using var connection = Open(readOnly: false);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$accounts", JsonSerializer.Serialize(accounts));
        command.Parameters.AddWithValue("$archive", MailContract.ArchiveSource);
        command.CommandText = "SELECT count(*) FROM messages WHERE source=$archive AND account IN (SELECT value FROM json_each($accounts))";
        if (Convert.ToInt64(command.ExecuteScalar()) != 0) throw new InvalidOperationException("An import still holds messages.");
        command.CommandText = "DELETE FROM archive_folders WHERE source=$archive AND account IN (SELECT value FROM json_each($accounts))";
        var removed = command.ExecuteNonQuery();
        command.CommandText = "SELECT value FROM settings WHERE key=$key";
        command.Parameters.AddWithValue("$key", FolderArchive.SavedCountsSetting);
        if (command.ExecuteScalar() is string json && JsonNode.Parse(json) is JsonObject saved)
        {
            foreach (var name in new[] { "Folders", "ArchiveUnread" })
                if (saved[name] is JsonArray entries)
                    foreach (var entry in entries.Where(entry => (string?)entry?["Account"] is { } account
                        && accounts.Contains(account, StringComparer.Ordinal)).ToArray())
                        entries.Remove(entry);
            command.CommandText = "UPDATE settings SET value=$value WHERE key=$key";
            command.Parameters.AddWithValue("$value", saved.ToJsonString());
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        return removed;
    }

    private SqliteConnection Open(bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = services.Paths.DatabasePath, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
}
