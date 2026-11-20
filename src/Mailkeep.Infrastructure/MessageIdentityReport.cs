using System.Diagnostics;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Mailkeep.Core;
using Microsoft.Data.Sqlite;
using MimeKit;

namespace Mailkeep.Infrastructure;

/// <summary>Evidence only. Does not initialize, backfill, merge or send anything.</summary>
public sealed class MessageIdentityReport(AppPaths paths)
{
    public sealed record Occurrence(long Id, string Source, string Account, string Folder,
        string SourceKey, bool IsRead, string? RawPath, string[] DeletionEvidence);
    public sealed record BlobEvidence(string? Sha256, string? MessageId, string? Issue);
    public sealed record Entry(Occurrence Message, BlobEvidence Evidence);
    public sealed record ByteGroup(string Sha256, string Disposition, long[] OeRowsSuperseded,
        Occurrence[] MailkeepRowsRetained, Occurrence[] Occurrences);
    public sealed record HeaderConflict(string MessageId, Entry[] Occurrences);
    public sealed record Deletion(string Source, string Account, string Folder, string SourceKey,
        string DeletedAt, long[] LinkedRows);
    public sealed record FolderSummary(string Source, string Account, string Folder, int Rows,
        int VerifiedRows, int SupersededOeRows, int DeletedRows);
    public sealed record Report(int Version, string Authority, int TotalRows, int VerifiedRows,
        int UniqueBlobPaths, int HeaderlessRows, int SupersededOeRows,
        ByteGroup[] ByteMatches, HeaderConflict[] MessageIdConflicts, Entry[] Issues,
        Occurrence[] DeletionLinkedRows, Deletion[] Deletions, FolderSummary[] Folders,
        Dictionary<string, long> OutgoingCounts, string[] Limitations);

    public Report Run(CancellationToken cancellationToken = default, Action<string>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(paths.DatabasePath)) throw new FileNotFoundException("Mail database does not exist.", paths.DatabasePath);
        var occurrences = new List<Occurrence>();
        var deletions = new List<Deletion>();
        var outgoing = new Dictionary<string, long>(StringComparer.Ordinal);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString()))
        {
            connection.Open();
            // A deferred read transaction gives all metadata queries the same snapshot.
            using var transaction = connection.BeginTransaction(deferred: true);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT id,source,account,folder,source_key,is_read,raw_path FROM messages ORDER BY id";
            progress?.Invoke("Reading message metadata (read-only snapshot)...");
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    occurrences.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                        reader.GetString(3), reader.GetString(4), reader.GetBoolean(5),
                        reader.IsDBNull(6) ? null : reader.GetString(6), []));
                    if (occurrences.Count % 10000 == 0) progress?.Invoke($"Read {occurrences.Count:N0} message rows...");
                }
            command.CommandText = "SELECT source,account,folder,source_key,deleted_at FROM local_deletions ORDER BY source,account,folder,source_key";
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    deletions.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                        reader.GetString(3), reader.GetString(4), []));
                }
            command.CommandText = "SELECT status,count(*) FROM outgoing_messages GROUP BY status ORDER BY status";
            using (var reader = command.ExecuteReader())
                while (reader.Read()) outgoing.Add(reader.GetString(0), reader.GetInt64(1));
            transaction.Commit();
        }

        // Preserve origin evidence independently of content identity. Tombstones often have no blob.
        var byOrigin = occurrences.ToLookup(row => (row.Source, row.Account, row.Folder, row.SourceKey));
        var liveByKey = occurrences.Where(row => row.Source == MailContract.LiveSource)
            .ToLookup(row => (row.Account, row.SourceKey));
        var oeById = occurrences.Where(IsOe).ToDictionary(row => row.Id);
        var deleted = new Dictionary<long, List<string>>();
        for (var i = 0; i < deletions.Count; i++)
        {
            var deletion = deletions[i];
            var linked = byOrigin[(deletion.Source, deletion.Account, deletion.Folder, deletion.SourceKey)]
                .Select(row => row.Id).ToHashSet();
            // Filing retains the live key while the tombstone retains the old folder.
            // Only link an unambiguous survivor; several same-key rows need review.
            if (linked.Count == 0 && deletion.Source == MailContract.LiveSource)
            {
                var survivors = liveByKey[(deletion.Account, deletion.SourceKey)].Take(2).ToArray();
                if (survivors.Length == 1) linked.Add(survivors[0].Id);
            }
            if (deletion.Source == MailContract.LiveSource && deletion.Account == MailContract.LocalCopyAccount
                && deletion.SourceKey.StartsWith(MailContract.AdoptedCopyKeyPrefix, StringComparison.Ordinal)
                && long.TryParse(deletion.SourceKey[MailContract.AdoptedCopyKeyPrefix.Length..], out var adoptedId)
                && oeById.ContainsKey(adoptedId)) linked.Add(adoptedId);
            deletions[i] = deletion with { LinkedRows = linked.Order().ToArray() };
            foreach (var id in linked)
            {
                if (!deleted.TryGetValue(id, out var evidence)) deleted[id] = evidence = [];
                evidence.Add($"{deletion.Source}/{deletion.Account}/{deletion.Folder}/{deletion.SourceKey}");
            }
        }
        var entries = new List<Entry>(occurrences.Count);
        var cache = new ConcurrentDictionary<string, BlobEvidence>(StringComparer.Ordinal);
        var rawPaths = occurrences.Select(row => row.RawPath).Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!).Distinct(StringComparer.Ordinal).ToArray();
        var timer = Stopwatch.StartNew();
        var progressLock = new object();
        var inspected = 0;
        // Bounded I/O concurrency keeps a large store of small files practical, without
        // opening hundreds of files or retaining message bodies in memory.
        using var buffers = new ThreadLocal<byte[]>(() => new byte[65536]);
        Parallel.ForEach(rawPaths, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken }, rawPath =>
        {
            cache[rawPath] = Inspect(rawPath, buffers.Value!, cancellationToken);
            var count = Interlocked.Increment(ref inspected);
            if (count % 100 == 0 || count == rawPaths.Length)
                lock (progressLock)
                    if (timer.Elapsed.TotalSeconds >= 5 || count == rawPaths.Length)
                    {
                        progress?.Invoke($"Checked {count:N0}/{rawPaths.Length:N0} unique blob paths...");
                        timer.Restart();
                    }
        });
        for (var i = 0; i < occurrences.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = occurrences[i];
            row = row with { DeletionEvidence = deleted.GetValueOrDefault(row.Id)?.ToArray() ?? [] };
            BlobEvidence evidence;
            if (string.IsNullOrWhiteSpace(row.RawPath)) evidence = new(null, null, "missing_raw_path");
            else evidence = cache[row.RawPath];
            entries.Add(new(row, evidence));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var byteMatches = entries.Where(entry => entry.Evidence.Sha256 is not null)
            .GroupBy(entry => entry.Evidence.Sha256!, StringComparer.Ordinal)
            .Where(group => group.Skip(1).Any()).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => Describe(group.Key, group.Select(entry => entry.Message).ToArray())).ToArray();
        var conflicts = entries.Where(entry => entry.Evidence.MessageId is not null)
            .GroupBy(entry => entry.Evidence.MessageId!, StringComparer.Ordinal)
            .Where(group => group.Select(entry => entry.Evidence.Sha256).Distinct().Skip(1).Any())
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new HeaderConflict(group.Key, group.ToArray())).ToArray();
        var superseded = byteMatches.SelectMany(group => group.OeRowsSuperseded).ToHashSet();
        var folders = entries.GroupBy(entry => (entry.Message.Source, entry.Message.Account, entry.Message.Folder))
            .OrderBy(group => group.Key.Source, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Account, StringComparer.Ordinal).ThenBy(group => group.Key.Folder, StringComparer.Ordinal)
            .Select(group => new FolderSummary(group.Key.Source, group.Key.Account, group.Key.Folder, group.Count(),
                group.Count(entry => entry.Evidence.Sha256 is not null), group.Count(entry => superseded.Contains(entry.Message.Id)),
                group.Count(entry => entry.Message.DeletionEvidence.Length > 0))).ToArray();
        return new(1, "Mailkeep wins over byte-identical OE copies; Message-ID alone never authorizes a merge.",
            entries.Count, entries.Count(entry => entry.Evidence.Sha256 is not null), cache.Count,
            entries.Count(entry => entry.Evidence.Sha256 is not null && entry.Evidence.MessageId is null), superseded.Count,
            byteMatches, conflicts, entries.Where(entry => entry.Evidence.Issue is not null).ToArray(),
            entries.Where(entry => entry.Message.DeletionEvidence.Length > 0).Select(entry => entry.Message).ToArray(),
            deletions.ToArray(), folders, outgoing,
            ["Diagnostic snapshot only; no rows, schema, indexes or MIME blobs are modified.",
             "SHA-256 is computed from actual blob bytes, not trusted from filenames. Matching hashes are content evidence, not a migration command.",
             "Only archive/oe_archive accounts named OE Classic or OE Classic <identity> are classified as OE.",
             "Multiple Mailkeep occurrences are retained; conflicting Mailkeep folder/read/deletion states require review.",
             "Deletion history is incomplete: unlinked tombstones cannot be matched by content. Absence of evidence does not prove a message was never deleted.",
             "Deletion links do not override contradictory surviving Mailkeep rows automatically.",
             "Outgoing rows are counted separately; draft blobs may contain only attachments, not complete messages.",
             "Metadata is a consistent database snapshot; blobs are read afterwards. Missing/unreadable blobs are reported; rerun before migration."]);
    }

    private static bool IsOe(Occurrence row) => row.Source is MailContract.ArchiveSource or MailContract.LegacyArchiveSource
        && (row.Account == "OE Classic" || row.Account.StartsWith("OE Classic ", StringComparison.Ordinal));

    private static ByteGroup Describe(string hash, Occurrence[] rows)
    {
        var oe = rows.Where(IsOe).ToArray();
        var live = rows.Where(row => row.Source == MailContract.LiveSource).ToArray();
        var disposition = "same_bytes_review_copies";
        long[] superseded = [];
        if (oe.Length > 0 && live.Length > 0)
        {
            var conflictingState = live.Select(row => (row.Account, row.Folder, row.IsRead,
                Deleted: row.DeletionEvidence.Length > 0)).Distinct().Skip(1).Any();
            // A deletion linked to an old OE representation and an undeleted live row is unresolved.
            var deletionConflict = oe.Any(row => row.DeletionEvidence.Length > 0)
                && live.Any(row => row.DeletionEvidence.Length == 0 && row.Folder != LocalFolderKinds.Deleted);
            disposition = conflictingState || deletionConflict ? "review_conflicting_mailkeep_state" : "mailkeep_wins";
            if (!conflictingState && !deletionConflict) superseded = oe.Select(row => row.Id).ToArray();
        }
        else if (oe.Length > 0 && oe.Any(row => row.DeletionEvidence.Length > 0))
            disposition = "review_deletion_history_do_not_resurrect";
        return new(hash, disposition, superseded, live, rows);
    }

    private BlobEvidence Inspect(string rawPath, byte[] buffer, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(paths.BlobDirectory) + Path.DirectorySeparatorChar;
        string? hash = null;
        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(root, rawPath));
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return new(null, null, "blob_path_outside_store");
            using var stream = new RawBlobStore(paths).OpenRead(rawPath);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int read;
            while ((read = stream.Read(buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hasher.AppendData(buffer, 0, read);
            }
            hash = Convert.ToHexStringLower(hasher.GetHashAndReset());
            stream.Position = 0;
            var headers = HeaderList.Load(stream, cancellationToken);
            var ids = headers.Where(header => header.Id == HeaderId.MessageId).Select(header => header.Value.Trim()).ToArray();
            if (ids.Length == 0) return new(hash, null, "missing_message_id");
            // Preserve case and syntax. Invalid/multiple IDs are not usable identity evidence.
            if (ids.Length != 1 || !ids[0].StartsWith('<') || !ids[0].EndsWith('>')
                || ids[0].Count(ch => ch == '<') != 1 || ids[0].Count(ch => ch == '>') != 1
                || !ids[0].Contains('@') || ids[0].Any(char.IsWhiteSpace))
                return new(hash, null, "invalid_or_multiple_message_id");
            return new(hash, ids[0], null);
        }
        catch (FileNotFoundException) { return new(null, null, "missing_blob"); }
        catch (DirectoryNotFoundException) { return new(null, null, "missing_blob"); }
        catch (UnauthorizedAccessException) { return new(null, null, "unreadable_blob"); }
        catch (IOException) { return new(null, null, "unreadable_blob"); }
        catch (FormatException) { return new(hash, null, "malformed_headers"); }
        catch (ArgumentException) { return new(hash, null, "invalid_blob_path_or_headers"); }
    }
}
