using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Infrastructure;

public sealed partial class MailDatabase
{
    public sealed record BlobSweepResult(int Removed, int Pending, string? Warning = null);

    private void QueueBlobCleanup(string rawPath)
    {
        using var connection = Open(flushFolders: false);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO blob_gc_candidates(raw_path) VALUES($path)";
        command.Parameters.AddWithValue("$path", rawPath); command.ExecuteNonQuery();
    }

    /// <summary>Only deletion/replacement candidates are swept. Unpublished/staging blobs are never guessed to be garbage.</summary>
    public BlobSweepResult SweepUnreferencedBlobs(bool discoverOrphans = false)
    {
        try
        {
            using var connection = Open(flushFolders: false);
            if (!discoverOrphans && Convert.ToInt64(BulkFolderMigration.Scalar(connection, "SELECT count(*) FROM blob_gc_candidates")) == 0) return new(0, 0);
            FolderStorage.Flush();
            // A writer transaction freezes coordinator references. Never wait for a staging writer's
            // blob mutex while holding SQLite: it may be about to publish that blob into a row.
            using var transaction = connection.BeginTransaction();
            return _blobs.WithReferences(() =>
            {
                if (discoverOrphans)
                {
                    using var seed = connection.CreateCommand(); seed.Transaction = transaction;
                    seed.CommandText = "INSERT OR IGNORE INTO blob_gc_candidates(raw_path) VALUES($path)";
                    var path = seed.Parameters.Add("$path", SqliteType.Text);
                    var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
                    foreach (var file in Directory.EnumerateFiles(_paths.BlobDirectory, "*.eml", options))
                    { path.Value = Path.GetRelativePath(_paths.BlobDirectory, file); seed.ExecuteNonQuery(); }
                }
                return Sweep(connection, transaction);
            }, TimeSpan.Zero);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException or JsonException or InvalidDataException)
        {
            return new(0, -1, "Blob cleanup deferred: " + error.Message);
        }
    }

    private BlobSweepResult Sweep(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT raw_path FROM blob_gc_candidates ORDER BY last_checked,raw_path LIMIT 128";
        var candidates = new List<string>();
        using (var rows = command.ExecuteReader()) while (rows.Read()) candidates.Add(rows.GetString(0));

        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name<>'blob_gc_candidates'";
        var tables = new List<string>();
        using (var rows = command.ExecuteReader()) while (rows.Read()) tables.Add(rows.GetString(0));
        static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
        var columns = new List<(string Table, string Column)>();
        foreach (var table in tables)
        {
            command.CommandText = "PRAGMA table_info(" + Quote(table) + ")";
            using var rows = command.ExecuteReader();
            while (rows.Read())
            {
                var column = rows.GetString(1);
                if (column.Contains("raw_path", StringComparison.OrdinalIgnoreCase) || column.Contains("sha256", StringComparison.OrdinalIgnoreCase)
                    || column is "metadata" or "value" or "receipt") columns.Add((table, column));
            }
        }
        var bundles = ReferenceBundles(); // All manifests/bindings must be readable before deleting anything.
        var removed = 0;
        foreach (var candidate in candidates)
        {
            var sha = Path.GetFileNameWithoutExtension(candidate);
            if (sha.Length != 64 || sha.Any(ch => !Uri.IsHexDigit(ch))) continue;
            var referenced = false;
            foreach (var (table, column) in columns)
            {
                command.Parameters.Clear();
                command.CommandText = $"SELECT EXISTS(SELECT 1 FROM {Quote(table)} WHERE instr(lower({Quote(column)}),$sha)>0)";
                command.Parameters.AddWithValue("$sha", sha.ToLowerInvariant());
                if (Convert.ToInt64(command.ExecuteScalar()) != 0) { referenced = true; break; }
            }
            if (!referenced)
                foreach (var bundle in bundles)
                {
                    using var portable = BulkFolderMigration.Connect(bundle, timeoutSeconds: 5);
                    if (Convert.ToInt64(BulkFolderMigration.Scalar(portable, "SELECT EXISTS(SELECT 1 FROM messages WHERE lower(sha256)=$sha)", ("$sha", sha.ToLowerInvariant()))) != 0)
                    { referenced = true; break; }
                }
            if (referenced)
            {
                command.Parameters.Clear();
                command.CommandText = "UPDATE blob_gc_candidates SET last_checked=unixepoch() WHERE raw_path=$path";
                command.Parameters.AddWithValue("$path", candidate); command.ExecuteNonQuery();
                continue;
            }
            var file = _blobs.CacheFile(candidate);
            var exists = File.Exists(file);
            File.Delete(file);
            command.Parameters.Clear(); command.CommandText = "DELETE FROM blob_gc_candidates WHERE raw_path=$path";
            command.Parameters.AddWithValue("$path", candidate); command.ExecuteNonQuery();
            if (exists) removed++;
        }
        command.Parameters.Clear(); command.CommandText = "SELECT count(*) FROM blob_gc_candidates";
        var pending = Convert.ToInt32(command.ExecuteScalar());
        transaction.Commit();
        return new(removed, pending);
    }

    private string[] ReferenceBundles()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bound = Path.Combine(_paths.Root, "bound-folders");
        if (Directory.Exists(bound))
            foreach (var file in Directory.EnumerateFiles(bound, FolderBundle.FileName, SearchOption.AllDirectories)) files.Add(file);
        var registry = Path.Combine(_paths.Root, "archives.json");
        if (File.Exists(registry))
        {
            var archives = JsonSerializer.Deserialize<ArchiveStore.Archive[]>(File.ReadAllText(registry),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Archive registry is empty.");
            foreach (var archive in archives)
            {
                if (!Directory.Exists(archive.Location)) throw new IOException("An archive location is unavailable; its blob references cannot be verified.");
                foreach (var entry in ArchiveManifest.Read(archive.Location).Folders)
                {
                    var file = ArchiveManifest.ResolveFile(archive.Location, entry.File);
                    using var check = BulkFolderMigration.Connect(file, timeoutSeconds: 5);
                    files.Add(file);
                }
            }
        }
        return files.ToArray();
    }
}
