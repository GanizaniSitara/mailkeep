using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Infrastructure;

public sealed partial class MailDatabase
{
    public sealed record BlobSweepResult(int Removed, int Pending, string? Warning = null);

    private const int BlobSweepBatchSize = 128;

    private sealed record SweepSnapshot(
        long MaxMessageId,
        long OutgoingMaxRowId,
        long OutgoingCount,
        long BulkJournalMaxId,
        long BulkJournalCount,
        long BulkBindingsCount,
        long ArchivedCount,
        long AdoptionCleanupCount,
        long OeDraftConversionsCount,
        long ArchiveRestoreCount,
        long ArchivesRegistryTicks,
        long ArchivesRegistryLength);

    private void QueueBlobCleanup(string rawPath)
    {
        using var connection = Open(flushFolders: false);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO blob_gc_candidates(raw_path) VALUES($path)";
        command.Parameters.AddWithValue("$path", rawPath);
        command.ExecuteNonQuery();
    }

    /// <summary>Only deletion/replacement candidates are swept. Unpublished/staging blobs are never guessed to be garbage.</summary>
    public BlobSweepResult SweepUnreferencedBlobs(bool discoverOrphans = false)
        => SweepUnreferencedBlobs(discoverOrphans, onlyUnchecked: false, recheckAfter: null);

    /// <summary>Scans references read-only without holding a coordinator write transaction, then commits only
    /// brief verified updates and deletions. Interactive deletes sweep only newly queued candidates; idle passes
    /// rate-limit re-checks of externally retained blobs.</summary>
    public BlobSweepResult SweepUnreferencedBlobs(bool discoverOrphans, bool onlyUnchecked, TimeSpan? recheckAfter = null)
    {
        try
        {
            BulkFolderMigration.CheckAvailable(_paths);
            if (!File.Exists(_paths.DatabasePath)) return new(0, 0);

            var diskCandidates = discoverOrphans ? DiscoverBlobFiles() : [];

            List<string> queuedCandidates;
            HashSet<string> allQueued;
            SweepSnapshot before;
            Dictionary<string, string> unresolved;
            var dropFromQueue = new List<string>();
            var markCheckedInQueue = new List<string>();

            // Phase 1: all reference discovery and bundle verification run strictly read-only so
            // concurrent deletes, moves, syncs and sends never wait on a long SQLite write lock.
            using (var read = OpenBlobReadOnly())
            {
                if (!BulkFolderMigration.HasTable(read, "blob_gc_candidates")) return new(0, 0);

                queuedCandidates = LoadQueuedCandidates(read, discoverOrphans, onlyUnchecked, recheckAfter);
                if (!discoverOrphans && queuedCandidates.Count == 0)
                {
                    var pending = onlyUnchecked || recheckAfter is not null
                        ? Convert.ToInt32(BulkFolderMigration.Scalar(read, "SELECT count(*) FROM blob_gc_candidates"))
                        : 0;
                    return new(0, pending);
                }
            }

            // Flush any pending portable folder journal outside our coordinator write transaction
            // BEFORE taking the Phase 1 snapshot so the flush itself never invalidates the snapshot.
            FolderStorage.Flush();

            using (var read = OpenBlobReadOnly())
            {
                queuedCandidates = LoadQueuedCandidates(read, discoverOrphans, onlyUnchecked, recheckAfter);
                allQueued = new HashSet<string>(queuedCandidates, StringComparer.OrdinalIgnoreCase);
                before = CaptureSweepSnapshot(read);

                // Merge queued and discovered candidates keyed by lowercase 64-hex SHA.
                unresolved = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var path in queuedCandidates)
                    if (TryExtractSha(path, out var sha)) unresolved.TryAdd(sha, path);
                foreach (var path in diskCandidates)
                    if (TryExtractSha(path, out var sha)) unresolved.TryAdd(sha, path);

                if (unresolved.Count > 0)
                {
                    // Trigger-tracked tables (messages, outgoing_messages) re-enqueue on delete/replace,
                    // so still-referenced rows can be dropped from blob_gc_candidates immediately.
                    var triggerReferenced = FindTriggerTrackedReferences(read, unresolved);
                    foreach (var sha in triggerReferenced)
                    {
                        if (unresolved.Remove(sha, out var path) && allQueued.Contains(path))
                            dropFromQueue.Add(path);
                    }
                }

                if (unresolved.Count > 0)
                {
                    // Check remaining coordinator tables in a single read-only pass (never scanning
                    // retired multi-GB folder-storage.* import logs in settings).
                    var otherReferenced = FindOtherCoordinatorReferences(read, unresolved);
                    foreach (var sha in otherReferenced)
                    {
                        if (unresolved.Remove(sha, out var path) && allQueued.Contains(path))
                            markCheckedInQueue.Add(path);
                    }
                }
            }

            if (unresolved.Count > 0)
            {
                var bundles = ReferenceBundles(); // All manifests/bindings must be readable before deleting anything.
                foreach (var bundle in bundles)
                {
                    if (unresolved.Count == 0) break;
                    foreach (var sha in FindBundleReferences(bundle, unresolved))
                    {
                        if (unresolved.Remove(sha, out var path) && allQueued.Contains(path))
                            markCheckedInQueue.Add(path);
                    }
                }
            }

            var toDeleteAll = unresolved.Values.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            var toDeleteNow = toDeleteAll.Take(BlobSweepBatchSize).ToList();
            var overflowOrphans = toDeleteAll.Skip(BlobSweepBatchSize).Where(p => !allQueued.Contains(p)).ToList();

            if (toDeleteNow.Count == 0 && dropFromQueue.Count == 0 && markCheckedInQueue.Count == 0 && overflowOrphans.Count == 0)
            {
                using var read = OpenBlobReadOnly();
                var pending = Convert.ToInt32(BulkFolderMigration.Scalar(read, "SELECT count(*) FROM blob_gc_candidates"));
                return new(0, pending);
            }

            // Phase 2: brief atomic write transaction. Only acquire the blob-reference mutex when deleting
            // files on disk, and always acquire it OUTER to SQLite (matching Upsert/SaveDraft/QueueAsync).
            if (toDeleteNow.Count == 0)
                return CommitQueueMaintenance(dropFromQueue, markCheckedInQueue, overflowOrphans);

            return _blobs.WithReferences(() =>
                CommitDeletionsAndMaintenance(before, toDeleteNow, dropFromQueue, markCheckedInQueue, overflowOrphans),
                TimeSpan.Zero);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException or JsonException or InvalidDataException)
        {
            return new(0, -1, "Blob cleanup deferred: " + error.Message);
        }
    }

    private BlobSweepResult CommitQueueMaintenance(
        IReadOnlyList<string> dropFromQueue,
        IReadOnlyList<string> markCheckedInQueue,
        IReadOnlyList<string> overflowOrphans)
    {
        using var connection = OpenBlobWrite();
        using var transaction = connection.BeginTransaction();
        ApplyQueueUpdates(connection, transaction, dropFromQueue, markCheckedInQueue, overflowOrphans);
        var pending = CountPendingCandidates(connection, transaction);
        transaction.Commit();
        return new(0, pending);
    }

    private BlobSweepResult CommitDeletionsAndMaintenance(
        SweepSnapshot before,
        IReadOnlyList<string> toDelete,
        IReadOnlyList<string> dropFromQueue,
        IReadOnlyList<string> markCheckedInQueue,
        IReadOnlyList<string> overflowOrphans)
    {
        using var connection = OpenBlobWrite();
        using var transaction = connection.BeginTransaction();

        var snapshotMatches = CaptureSweepSnapshot(connection, transaction) == before;
        var verified = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in toDelete)
            if (TryExtractSha(path, out var sha)) verified.TryAdd(sha, path);

        // Always re-verify trigger-tracked references inside the commit transaction.
        var reappeared = FindTriggerTrackedReferences(connection, verified, transaction);
        var deferred = new List<string>(overflowOrphans);
        var extraDrops = new List<string>(dropFromQueue);
        foreach (var sha in reappeared)
        {
            if (verified.Remove(sha, out var path)) extraDrops.Add(path);
        }

        var removed = 0;
        if (!snapshotMatches)
        {
            // Coordinator or archive registry changed during Phase 1: never delete without fresh proof.
            foreach (var path in verified.Values) deferred.Add(path);
        }
        else
        {
            using var deleteRow = connection.CreateCommand();
            deleteRow.Transaction = transaction;
            deleteRow.CommandText = "DELETE FROM blob_gc_candidates WHERE raw_path=$path";
            var pathParam = deleteRow.Parameters.Add("$path", SqliteType.Text);
            foreach (var candidate in verified.Values)
            {
                var file = _blobs.CacheFile(candidate);
                var exists = File.Exists(file);
                if (exists) File.Delete(file);
                pathParam.Value = candidate;
                deleteRow.ExecuteNonQuery();
                if (exists) removed++;
            }
        }

        ApplyQueueUpdates(connection, transaction, extraDrops, markCheckedInQueue, deferred);
        var pending = CountPendingCandidates(connection, transaction);
        transaction.Commit();
        return new(removed, pending);
    }

    private static void ApplyQueueUpdates(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<string> dropFromQueue,
        IReadOnlyList<string> markCheckedInQueue,
        IReadOnlyList<string> enqueueUnchecked)
    {
        if (dropFromQueue.Count > 0)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM blob_gc_candidates WHERE raw_path=$path";
            var p = command.Parameters.Add("$path", SqliteType.Text);
            foreach (var path in dropFromQueue) { p.Value = path; command.ExecuteNonQuery(); }
        }
        if (markCheckedInQueue.Count > 0)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE blob_gc_candidates SET last_checked=unixepoch() WHERE raw_path=$path";
            var p = command.Parameters.Add("$path", SqliteType.Text);
            foreach (var path in markCheckedInQueue) { p.Value = path; command.ExecuteNonQuery(); }
        }
        if (enqueueUnchecked.Count > 0)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT OR IGNORE INTO blob_gc_candidates(raw_path,last_checked) VALUES($path,0)";
            var p = command.Parameters.Add("$path", SqliteType.Text);
            foreach (var path in enqueueUnchecked) { p.Value = path; command.ExecuteNonQuery(); }
        }
    }

    private static int CountPendingCandidates(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM blob_gc_candidates";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private List<string> DiscoverBlobFiles()
    {
        var files = new List<string>();
        if (!Directory.Exists(_paths.BlobDirectory)) return files;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(_paths.BlobDirectory, "*.eml", options))
            files.Add(Path.GetRelativePath(_paths.BlobDirectory, file));
        return files;
    }

    private static List<string> LoadQueuedCandidates(
        SqliteConnection connection,
        bool discoverOrphans,
        bool onlyUnchecked,
        TimeSpan? recheckAfter)
    {
        using var command = connection.CreateCommand();
        if (discoverOrphans)
        {
            command.CommandText = "SELECT raw_path FROM blob_gc_candidates ORDER BY last_checked,raw_path";
        }
        else if (onlyUnchecked)
        {
            command.CommandText = $"SELECT raw_path FROM blob_gc_candidates WHERE last_checked=0 ORDER BY raw_path LIMIT {BlobSweepBatchSize}";
        }
        else if (recheckAfter is { } interval)
        {
            var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Math.Max(0L, (long)interval.TotalSeconds);
            command.CommandText = $"SELECT raw_path FROM blob_gc_candidates WHERE last_checked=0 OR last_checked<=$cutoff ORDER BY last_checked,raw_path LIMIT {BlobSweepBatchSize}";
            command.Parameters.AddWithValue("$cutoff", cutoff);
        }
        else
        {
            command.CommandText = $"SELECT raw_path FROM blob_gc_candidates ORDER BY last_checked,raw_path LIMIT {BlobSweepBatchSize}";
        }
        var results = new List<string>();
        using var rows = command.ExecuteReader();
        while (rows.Read()) results.Add(rows.GetString(0));
        return results;
    }

    private static HashSet<string> FindTriggerTrackedReferences(
        SqliteConnection connection,
        Dictionary<string, string> unresolved,
        SqliteTransaction? transaction = null)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (unresolved.Count == 0) return found;

        // 1. messages.raw_path via idx_messages_raw_path (covering index seek, never scanning 16 GB table).
        var probePaths = new List<string>(unresolved.Count * 2);
        foreach (var (sha, rawPath) in unresolved)
        {
            var win = $"{sha[..2]}\\{sha}.eml";
            var unix = $"{sha[..2]}/{sha}.eml";
            probePaths.Add(win);
            probePaths.Add(unix);
            if (!rawPath.Equals(win, StringComparison.Ordinal) && !rawPath.Equals(unix, StringComparison.Ordinal))
                probePaths.Add(rawPath);
        }

        const int chunkSize = 250;
        for (var offset = 0; offset < probePaths.Count; offset += chunkSize)
        {
            var count = Math.Min(chunkSize, probePaths.Count - offset);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var names = new string[count];
            for (var i = 0; i < count; i++)
            {
                names[i] = "$p" + i;
                command.Parameters.AddWithValue(names[i], probePaths[offset + i]);
            }
            command.CommandText = $"SELECT DISTINCT raw_path FROM messages WHERE raw_path IN ({string.Join(',', names)})";
            using var rows = command.ExecuteReader();
            while (rows.Read())
                if (!rows.IsDBNull(0) && TryExtractSha(rows.GetString(0), out var sha) && unresolved.ContainsKey(sha))
                    found.Add(sha);
        }

        if (found.Count == unresolved.Count) return found;

        // 2. outgoing_messages.raw_path (small table, single pass).
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT raw_path FROM outgoing_messages WHERE raw_path IS NOT NULL";
            using var rows = command.ExecuteReader();
            while (rows.Read())
            {
                var value = rows.GetString(0);
                if (TryExtractSha(value, out var sha) && unresolved.ContainsKey(sha)) found.Add(sha);
                else ScanHex64Tokens(value, unresolved, found);
            }
        }

        return found;
    }

    private static HashSet<string> FindOtherCoordinatorReferences(
        SqliteConnection connection,
        Dictionary<string, string> unresolved)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (unresolved.Count == 0) return found;

        var tables = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT name FROM sqlite_master
                WHERE type='table'
                  AND name NOT LIKE 'sqlite_%'
                  AND name NOT IN ('blob_gc_candidates','messages','outgoing_messages')
                """;
            using var rows = command.ExecuteReader();
            while (rows.Read()) tables.Add(rows.GetString(0));
        }

        static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

        foreach (var table in tables)
        {
            if (found.Count == unresolved.Count) break;
            var columns = new List<string>();
            using (var info = connection.CreateCommand())
            {
                info.CommandText = "PRAGMA table_info(" + Quote(table) + ")";
                using var rows = info.ExecuteReader();
                while (rows.Read())
                {
                    var column = rows.GetString(1);
                    if (column.Contains("raw_path", StringComparison.OrdinalIgnoreCase)
                        || column.Contains("sha256", StringComparison.OrdinalIgnoreCase)
                        || column is "metadata" or "value" or "receipt")
                    {
                        columns.Add(column);
                    }
                }
            }
            if (columns.Count == 0) continue;

            using var scan = connection.CreateCommand();
            var selectCols = string.Join(',', columns.Select(Quote));
            if (table.Equals("settings", StringComparison.OrdinalIgnoreCase))
            {
                // Retired folder-storage.* import/retirement receipts can total gigabytes and embed
                // their own MIME/aliases; their live references live in adoption_cleanup and bound-folders.
                scan.CommandText = $"SELECT {selectCols} FROM {Quote(table)} WHERE key NOT LIKE 'folder-storage.%'";
            }
            else
            {
                scan.CommandText = $"SELECT {selectCols} FROM {Quote(table)}";
            }

            using var reader = scan.ExecuteReader();
            while (reader.Read())
            {
                for (var i = 0; i < columns.Count; i++)
                {
                    if (reader.IsDBNull(i)) continue;
                    var value = reader.GetString(i);
                    if (value.Length < 64) continue;
                    if (TryExtractSha(value, out var exact) && unresolved.ContainsKey(exact))
                        found.Add(exact);
                    else
                        ScanHex64Tokens(value, unresolved, found);
                }
                if (found.Count == unresolved.Count) break;
            }
        }

        return found;
    }

    private static List<string> FindBundleReferences(string bundleFile, Dictionary<string, string> unresolved)
    {
        var matched = new List<string>();
        using var portable = BulkFolderMigration.Connect(bundleFile, timeoutSeconds: 5);
        using var command = portable.CreateCommand();
        command.CommandText = "SELECT sha256 FROM messages WHERE sha256 IS NOT NULL";
        using var rows = command.ExecuteReader();
        var lookup = unresolved.GetAlternateLookup<ReadOnlySpan<char>>();
        Span<char> lower = stackalloc char[64];
        while (rows.Read())
        {
            var value = rows.GetString(0);
            if (value.Length != 64) continue;
            for (var i = 0; i < 64; i++) lower[i] = char.ToLowerInvariant(value[i]);
            if (lookup.TryGetValue(lower, out _))
            {
                var sha = new string(lower);
                matched.Add(sha);
                if (matched.Count == unresolved.Count) break;
            }
        }
        return matched;
    }

    private SweepSnapshot CaptureSweepSnapshot(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        long ScalarLong(string sql)
        {
            using var q = connection.CreateCommand();
            q.Transaction = transaction;
            q.CommandText = sql;
            var value = q.ExecuteScalar();
            return value is null or DBNull ? 0L : Convert.ToInt64(value);
        }

        bool Has(string table)
        {
            using var q = connection.CreateCommand();
            q.Transaction = transaction;
            q.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$name";
            q.Parameters.AddWithValue("$name", table);
            return Convert.ToInt64(q.ExecuteScalar()) != 0;
        }

        var registryPath = Path.Combine(_paths.Root, "archives.json");
        var registryTicks = 0L;
        var registryLength = 0L;
        if (File.Exists(registryPath))
        {
            var info = new FileInfo(registryPath);
            registryTicks = info.LastWriteTimeUtc.Ticks;
            registryLength = info.Length;
        }

        return new SweepSnapshot(
            MaxMessageId: ScalarLong("SELECT coalesce(max(id),0) FROM messages"),
            OutgoingMaxRowId: ScalarLong("SELECT coalesce(max(rowid),0) FROM outgoing_messages"),
            OutgoingCount: ScalarLong("SELECT count(*) FROM outgoing_messages"),
            BulkJournalMaxId: Has("bulk_journal") ? ScalarLong("SELECT coalesce(max(id),0) FROM bulk_journal") : 0,
            BulkJournalCount: Has("bulk_journal") ? ScalarLong("SELECT count(*) FROM bulk_journal") : 0,
            BulkBindingsCount: Has("bulk_bindings") ? ScalarLong("SELECT count(*) FROM bulk_bindings") : 0,
            ArchivedCount: Has("archived_messages") ? ScalarLong("SELECT count(*) FROM archived_messages") : 0,
            AdoptionCleanupCount: Has("adoption_cleanup") ? ScalarLong("SELECT count(*) FROM adoption_cleanup") : 0,
            OeDraftConversionsCount: Has("oe_draft_conversions") ? ScalarLong("SELECT count(*) FROM oe_draft_conversions") : 0,
            ArchiveRestoreCount: Has("archive_restore_messages") ? ScalarLong("SELECT count(*) FROM archive_restore_messages") : 0,
            ArchivesRegistryTicks: registryTicks,
            ArchivesRegistryLength: registryLength);
    }

    private static bool TryExtractSha(string value, out string sha)
    {
        var candidate = value.Length == 64 ? value : Path.GetFileNameWithoutExtension(value);
        if (candidate.Length == 64)
        {
            Span<char> lower = stackalloc char[64];
            for (var i = 0; i < 64; i++)
            {
                var ch = candidate[i];
                if (!Uri.IsHexDigit(ch)) { sha = ""; return false; }
                lower[i] = char.ToLowerInvariant(ch);
            }
            sha = new string(lower);
            return true;
        }
        sha = "";
        return false;
    }

    private static void ScanHex64Tokens(string text, Dictionary<string, string> unresolved, HashSet<string> found)
    {
        if (text.Length < 64 || unresolved.Count == found.Count) return;
        var lookup = unresolved.GetAlternateLookup<ReadOnlySpan<char>>();
        Span<char> lower = stackalloc char[64];
        var span = text.AsSpan();
        var i = 0;
        while (i < span.Length)
        {
            if (!Uri.IsHexDigit(span[i])) { i++; continue; }
            var start = i;
            while (i < span.Length && Uri.IsHexDigit(span[i])) i++;
            var len = i - start;
            if (len == 64)
            {
                for (var j = 0; j < 64; j++) lower[j] = char.ToLowerInvariant(span[start + j]);
                if (lookup.TryGetValue(lower, out _)) found.Add(new string(lower));
            }
            else if (len > 64)
            {
                var slice = span.Slice(start, len);
                foreach (var sha in unresolved.Keys)
                    if (!found.Contains(sha) && slice.Contains(sha, StringComparison.OrdinalIgnoreCase))
                        found.Add(sha);
            }
        }
    }

    private SqliteConnection OpenBlobReadOnly()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _paths.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        connection.Open();
        return connection;
    }

    private SqliteConnection OpenBlobWrite()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=" + (FolderStorage.Enabled ? "FULL" : "NORMAL") + "; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return connection;
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
                    files.Add(file);
                }
            }
        }
        return files.ToArray();
    }
}
