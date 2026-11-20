using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mailkeep.Core;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Infrastructure;

/// <summary>Removes only reviewed hidden originals, retaining the visible Local Mail records and durable aliases.</summary>
public sealed class AdoptionCleanup(AppPaths paths)
{
    public sealed record Result(string Backup, int Removed, long[] RetainedIds);

    internal static void EnsureSchema(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS adoption_cleanup (
                original_id INTEGER PRIMARY KEY, retained_id INTEGER NOT NULL,
                source TEXT NOT NULL, account TEXT NOT NULL, folder TEXT NOT NULL, source_key TEXT NOT NULL,
                message_ref TEXT NOT NULL UNIQUE, sha256 TEXT NOT NULL, removed_at TEXT NOT NULL,
                UNIQUE(source,account,folder,source_key)
            );
            CREATE TRIGGER IF NOT EXISTS preserve_cleaned_adoption BEFORE INSERT ON messages
            WHEN EXISTS (SELECT 1 FROM adoption_cleanup r WHERE r.source=NEW.source AND r.account=NEW.account
                AND r.folder=NEW.folder AND r.source_key=NEW.source_key)
            BEGIN SELECT RAISE(IGNORE); END;
            CREATE TRIGGER IF NOT EXISTS preserve_cleaned_adoption_folder BEFORE INSERT ON archive_folders
            WHEN EXISTS (SELECT 1 FROM adoption_cleanup r WHERE r.source=NEW.source AND r.account=NEW.account AND r.folder=NEW.folder)
              AND NOT EXISTS (SELECT 1 FROM messages m WHERE m.source=NEW.source AND m.account=NEW.account AND m.folder=NEW.folder)
            BEGIN SELECT RAISE(IGNORE); END;
            """;
        command.ExecuteNonQuery();
    }

    internal static long ResolveId(SqliteConnection connection, long id)
    {
        using var command = connection.CreateCommand();
        var seen = new HashSet<long>();
        for (var depth = 0; depth < 16; depth++)
        {
            if (!seen.Add(id)) throw new InvalidDataException("Message aliases contain a cycle.");
            long? next = null;
            foreach (var table in new[] { "adoption_cleanup", "archive_restore_aliases" })
            {
                command.CommandText = $"SELECT count(*) FROM sqlite_master WHERE name='{table}'";
                command.Parameters.Clear();
                if (Convert.ToInt64(command.ExecuteScalar()) == 0) continue;
                command.CommandText = $"SELECT retained_id FROM {table} WHERE original_id=$id";
                command.Parameters.AddWithValue("$id", id);
                // A location alias keeps the same ID; a later archive restore may still redirect it.
                if (command.ExecuteScalar() is long retained && retained != id) { next = retained; break; }
            }
            if (next is null) return id;
            id = next.Value;
        }
        throw new InvalidDataException("Message alias chain is too deep.");
    }

    internal static CatalogLocator? ResolveReference(AppPaths paths, string reference)
    {
        if (!File.Exists(paths.DatabasePath)) return null;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = paths.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        long? retained = null;
        foreach (var table in new[] { "adoption_cleanup", "archive_restore_aliases" })
        {
            command.CommandText = $"SELECT count(*) FROM sqlite_master WHERE name='{table}'";
            command.Parameters.Clear();
            if (Convert.ToInt64(command.ExecuteScalar()) == 0) continue;
            command.CommandText = $"SELECT retained_id FROM {table} WHERE message_ref=$ref";
            command.Parameters.AddWithValue("$ref", reference);
            if (command.ExecuteScalar() is long id) { retained = id; break; }
        }
        if (retained is null) return null;
        command.CommandText = "SELECT account,folder,source_key,id,source FROM messages WHERE id=$id";
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$id", ResolveId(connection, retained.Value));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(new(reader.GetString(4) == MailContract.LiveSource ? MailContract.LocalSource : reader.GetString(4), reader.GetString(0), reader.GetString(1), reader.GetString(2)), reader.GetInt64(3)) : null;
    }

    public Result Apply(AdoptionMigrationReport.Report approved, CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? originalAccounts = null)
        => ApplyCore(approved, cancellationToken, originalAccounts, null);

    // Explicit owner-directed maintenance option; the default still makes and checks a fresh backup.
    public Result ApplyUsingExistingBackup(AdoptionMigrationReport.Report approved, string existingBackup,
        CancellationToken cancellationToken = default, IReadOnlyCollection<string>? originalAccounts = null)
        => ApplyCore(approved, cancellationToken, originalAccounts, Path.GetFullPath(existingBackup));

    private Result ApplyCore(AdoptionMigrationReport.Report approved, CancellationToken cancellationToken,
        IReadOnlyCollection<string>? originalAccounts, string? existingBackup)
    {
        if (approved.Mode is not ("read_only_no_merge" or "read_only_selected_pairs") || approved.Links.Length == 0
            || approved.Links.Any(link => link.Verdict != "matching_bytes_mailkeep_state_wins" || link.Original is null || link.Copies.Length != 1))
            throw new InvalidDataException("An exact reviewed original/copy report is required.");
        var current = new AdoptionMigrationReport(paths).Run(cancellationToken, originalAccounts,
            approved.Mode == "read_only_selected_pairs" ? approved.Links.Select(link => link.Original!.Id).ToArray() : null);
        if (JsonSerializer.Serialize(current.Links) != JsonSerializer.Serialize(approved.Links))
            throw new InvalidDataException("The pairs changed; review a fresh report.");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = paths.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        var backup = existingBackup ?? paths.DatabasePath + ".before-hidden-cleanup-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".bak";
        if (existingBackup is not null)
        {
            if (string.Equals(backup, Path.GetFullPath(paths.DatabasePath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The live database is not a backup.");
            if (!File.Exists(backup)) throw new FileNotFoundException("Existing backup not found.", backup);
            using var prior = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = backup, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            prior.Open();
            foreach (var link in approved.Links)
            {
                var original = link.Original!;
                using var check = prior.CreateCommand();
                check.CommandText = "SELECT source,account,folder,source_key,raw_path FROM messages WHERE id=$id";
                check.Parameters.AddWithValue("$id", original.Id);
                using var row = check.ExecuteReader();
                if (!row.Read() || row.GetString(0) != original.Source || row.GetString(1) != original.Account
                    || row.GetString(2) != original.Folder || row.GetString(3) != original.SourceKey || row.GetString(4) != original.RawPath)
                    throw new InvalidDataException("Existing backup does not contain a reviewed original.");
            }
        }
        else
        {
            using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Pooling = false }.ToString());
            destination.Open();
            connection.BackupDatabase(destination);
            using var check = destination.CreateCommand();
            check.CommandText = "PRAGMA quick_check";
            if (!Equals(check.ExecuteScalar(), "ok")) throw new InvalidDataException("Backup failed integrity check.");
        }
        using var transaction = connection.BeginTransaction();
        EnsureSchema(connection, transaction);
        foreach (var link in approved.Links)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var original = link.Original!;
            var copy = link.Copies[0];
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT count(*) FROM messages o JOIN messages c ON c.id=$copy
                JOIN local_folders f ON f.folder_key=c.folder
                WHERE o.id=$original AND o.source=$source AND o.account=$account AND o.folder=$folder AND o.source_key=$key
                AND o.raw_path=$raw AND o.converted_draft_id IS NULL AND o.is_read=$originalRead
                AND c.source='live' AND c.account=$local AND c.source_key=$adoption AND c.folder=$copyFolder
                AND c.is_read=$copyRead AND c.raw_path=$copyRaw AND c.converted_draft_id IS NULL
                AND o.subject=c.subject AND o.sender=c.sender AND o.date_utc IS c.date_utc
                """;
            command.Parameters.AddWithValue("$copy", copy.Id);
            command.Parameters.AddWithValue("$original", original.Id);
            command.Parameters.AddWithValue("$source", original.Source);
            command.Parameters.AddWithValue("$account", original.Account);
            command.Parameters.AddWithValue("$folder", original.Folder);
            command.Parameters.AddWithValue("$key", original.SourceKey);
            command.Parameters.AddWithValue("$raw", original.RawPath!);
            command.Parameters.AddWithValue("$originalRead", original.IsRead);
            command.Parameters.AddWithValue("$local", MailContract.LocalCopyAccount);
            command.Parameters.AddWithValue("$adoption", link.AdoptionKey);
            command.Parameters.AddWithValue("$copyFolder", copy.Folder);
            command.Parameters.AddWithValue("$copyRead", copy.IsRead);
            command.Parameters.AddWithValue("$copyRaw", copy.RawPath!);
            if (Convert.ToInt64(command.ExecuteScalar()) != 1) throw new InvalidDataException("A reviewed pair changed or is not hidden.");
            foreach (var state in new[] { original, copy })
            {
                using var raw = new RawBlobStore(paths).OpenRead(state.RawPath!);
                if (Convert.ToHexStringLower(SHA256.HashData(raw)) != state.Sha256)
                    throw new InvalidDataException("Reviewed email bytes changed.");
            }
            new WritableFolderStorage(paths, new RawBlobStore(paths)).PreserveCleanedAdoption(connection, transaction, original.Id, copy.Id);
            var reference = "msg_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{original.Source}\n{original.Account}\n{original.Folder}\n{original.SourceKey}")))[..24];
            command.CommandText = """
                INSERT INTO adoption_cleanup VALUES($original,$copy,$source,$account,$folder,$key,$ref,$hash,$now);
                INSERT OR IGNORE INTO message_marks(message_id,kind,marked_at)
                    SELECT $copy,kind,marked_at FROM message_marks WHERE message_id=$original;
                DELETE FROM message_marks WHERE message_id=$original;
                UPDATE outgoing_origins SET message_id=$copy WHERE message_id=$original;
                DELETE FROM messages_fts WHERE rowid=$original;
                DELETE FROM messages WHERE id=$original;
                UPDATE messages SET is_read=is_read WHERE id=$copy;
                """;
            command.Parameters.AddWithValue("$ref", reference);
            command.Parameters.AddWithValue("$hash", original.Sha256!);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        new WritableFolderStorage(paths, new RawBlobStore(paths)).Flush();
        return new(backup, approved.Links.Length, approved.Links.Select(link => link.Copies[0].Id).ToArray());
    }
}
