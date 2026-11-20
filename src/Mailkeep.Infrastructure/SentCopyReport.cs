using System.Security.Cryptography;
using Mailkeep.Core;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Infrastructure;

/// <summary>Audits delivered mail without initializing, restoring, moving or sending anything.</summary>
public sealed class SentCopyReport(AppPaths paths)
{
    public sealed record Copy(long Id, string Folder, string? RawPath, string? Sha256);
    public sealed record Delivery(string Id, string Identity, string Subject, string? RawPath,
        string? Sha256, Copy[] Copies, bool Deleted, string Verdict);
    public sealed record Report(string Mode, Delivery[] Deliveries);

    public Report Run(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(paths.DatabasePath)) throw new FileNotFoundException("Mail database does not exist.", paths.DatabasePath);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = paths.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var rows = new List<(string Id, string Identity, string Subject, string? Raw)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id,sending_identity_id,subject,raw_path FROM outgoing_messages WHERE status='sent' ORDER BY id";
            using var reader = command.ExecuteReader();
            while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        var deliveries = new List<Delivery>();
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var copies = new List<Copy>();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT id,folder,raw_path FROM messages WHERE source=$source AND account=$account AND source_key=$key ORDER BY id";
            command.Parameters.AddWithValue("$source", MailContract.LiveSource);
            command.Parameters.AddWithValue("$account", row.Identity);
            command.Parameters.AddWithValue("$key", "sent:" + row.Id);
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    var raw = reader.IsDBNull(2) ? null : reader.GetString(2);
                    copies.Add(new(reader.GetInt64(0), reader.GetString(1), raw, Hash(raw)));
                }
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM local_deletions WHERE source=$source AND account=$account AND source_key=$key)";
            var deleted = Convert.ToInt64(command.ExecuteScalar()) != 0;
            var hash = Hash(row.Raw);
            var verdict = deleted ? "deleted_do_not_restore"
                : copies.Count == 0 ? "missing_requires_review"
                : copies.Count != 1 ? "multiple_occurrences_requires_review"
                : hash is null || copies[0].Sha256 is null ? "unreadable_blob_requires_review"
                : hash != copies[0].Sha256 ? "different_bytes_requires_review"
                : copies[0].Folder == LocalFolderKinds.Sent ? "retained_in_sent"
                : "filed_do_not_restore";
            deliveries.Add(new(row.Id, row.Identity, row.Subject, row.Raw, hash, copies.ToArray(), deleted, verdict));
        }
        return new("read_only_no_backfill", deliveries.ToArray());
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
