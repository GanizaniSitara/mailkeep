using System.Security.Cryptography;
using System.Text.Json;
using Mailkeep.Core;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Infrastructure;

/// <summary>Exact adoption links and state for review before any folding of message rows.</summary>
public sealed class AdoptionMigrationReport(AppPaths paths)
{
    public sealed record State(long Id, string Source, string Account, string Folder, string SourceKey,
        bool IsRead, string? RawPath, string? Sha256, bool Converted);
    public sealed record Link(string AdoptionKey, State? Original, State[] Copies, string[] DeletedFolders, string Verdict);
    public sealed record Report(string Mode, Link[] Links, string[] Requirements);

    public Report Run(CancellationToken cancellationToken = default, IReadOnlyCollection<string>? originalAccounts = null)
        => Run(cancellationToken, originalAccounts, null);

    public Report Run(CancellationToken cancellationToken, IReadOnlyCollection<string>? originalAccounts,
        IReadOnlyCollection<long>? originalIds)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(paths.DatabasePath)) throw new FileNotFoundException("Mail database does not exist.", paths.DatabasePath);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = paths.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT source_key FROM messages WHERE source=$live AND account=$account AND source_key LIKE $prefix
            UNION SELECT source_key FROM local_deletions WHERE source=$live AND account=$account AND source_key LIKE $prefix
            ORDER BY source_key
            """;
        command.Parameters.AddWithValue("$live", MailContract.LiveSource);
        command.Parameters.AddWithValue("$account", MailContract.LocalCopyAccount);
        command.Parameters.AddWithValue("$prefix", MailContract.AdoptedCopyKeyPrefix + "%");
        var keys = new List<string>();
        if (originalIds is null)
        {
            using var reader = command.ExecuteReader();
            while (reader.Read()) keys.Add(reader.GetString(0));
        }
        else
        {
            if (originalIds.Count == 0 || originalIds.Any(id => id <= 0) || originalIds.Distinct().Count() != originalIds.Count)
                throw new ArgumentException("Name each positive original ID exactly once.");
            keys.AddRange(originalIds.Order().Select(id => MailContract.AdoptedCopyKeyPrefix + id));
        }
        command.CommandText = "SELECT count(*) FROM pragma_table_info('messages') WHERE name='converted_draft_id'";
        var converted = Convert.ToInt64(command.ExecuteScalar()) > 0 ? "converted_draft_id IS NOT NULL" : "0";
        // The unique source/account/folder/key index covers this lookup. Searching
        // id OR source_key would otherwise scan large MIME/body-bearing rows.
        var copyIds = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        command.CommandText = "SELECT id,source_key FROM messages INDEXED BY sqlite_autoindex_messages_1 WHERE source=$live AND account=$account";
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var key = reader.GetString(1);
                if (!copyIds.TryGetValue(key, out var ids)) copyIds[key] = ids = [];
                ids.Add(reader.GetInt64(0));
            }
        var links = new List<Link>();
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var copies = new List<State>();
            State? original = null;
            var validId = long.TryParse(key[MailContract.AdoptedCopyKeyPrefix.Length..], out var id);
            if (originalAccounts is not null)
            {
                command.CommandText = "SELECT account FROM messages WHERE id=$id";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$id", validId ? id : -1);
                if (command.ExecuteScalar() is not string account || !originalAccounts.Contains(account, StringComparer.Ordinal)) continue;
            }
            var rowIds = new List<long>(copyIds.GetValueOrDefault(key) ?? []);
            if (validId) rowIds.Add(id);
            command.CommandText = $"SELECT id,source,account,folder,source_key,is_read,raw_path,{converted} FROM messages WHERE id IN (SELECT value FROM json_each($ids)) ORDER BY id";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", validId ? id : -1);
            command.Parameters.AddWithValue("$live", MailContract.LiveSource);
            command.Parameters.AddWithValue("$account", MailContract.LocalCopyAccount);
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(rowIds));
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    var raw = reader.IsDBNull(6) ? null : reader.GetString(6);
                    var state = new State(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                        reader.GetString(4), reader.GetBoolean(5), raw, Hash(raw), reader.GetBoolean(7));
                    if (validId && state.Id == id) original = state; else copies.Add(state);
                }
            command.CommandText = "SELECT folder FROM local_deletions WHERE source=$live AND account=$account AND source_key=$key ORDER BY folder";
            var deleted = new List<string>();
            using (var reader = command.ExecuteReader()) while (reader.Read()) deleted.Add(reader.GetString(0));
            var verdict = original is null ? "missing_original_requires_review"
                : original.Source is not (MailContract.ArchiveSource or MailContract.LegacyArchiveSource) ? "unexpected_original_source_requires_review"
                : original.Converted ? "already_converted_draft_requires_review"
                : copies.Count > 1 ? "multiple_copies_requires_review"
                : copies.Count == 0 ? "deleted_without_survivor_do_not_restore"
                : original.Sha256 is null || copies[0].Sha256 is null ? "unreadable_blob_requires_review"
                : original.Sha256 != copies[0].Sha256 ? "different_bytes_requires_review"
                : "matching_bytes_mailkeep_state_wins";
            links.Add(new(key, original, copies.ToArray(), deleted.ToArray(), verdict));
        }
        return new(originalIds is null ? "read_only_no_merge" : "read_only_selected_pairs", links.ToArray(),
            ["Separate user approval is required before merging any original/adopted rows.",
             "Preserve Mailkeep folder, read and deleted state; never restore a deleted adoption.",
             "Establish re-import protection before making imported rows writable in place.",
             "Retain numeric ID mappings, marks, reply/forward origins and search references; keep MIME and a verified database backup.",
             "No global Message-ID or byte-hash collapse; independent copies and ambiguous links remain untouched."]);
    }

    private string? Hash(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var root = Path.GetFullPath(paths.BlobDirectory) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(root, raw));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
            using var stream = new RawBlobStore(paths).OpenRead(raw);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentException) { return null; }
    }
}
