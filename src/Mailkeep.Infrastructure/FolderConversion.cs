using Mailkeep.Core;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Infrastructure;

/// <summary>Copies one folder out of the mail database into its own folder file, and checks a folder file
/// against the database it came from. The database is opened read-only in both directions; nothing in it
/// changes. Membership follows what the app shows in the folder: the folder's own rows, plus, for a folder
/// adopted from an import, the imported rows the app does not hide.</summary>
public sealed class FolderConversion(AppPaths paths)
{
    public sealed record Source(long Id, string Store, string Account, string Folder, string SourceKey, bool IsRead,
        string? DateUtc, string? RawPath);
    public sealed record Plan(string FolderKey, string FolderPath, string Kind, string Origin, string? OriginKey,
        int Messages, int Unread, int HiddenOwnedByMailbox, int HiddenAdoptedCopyDeleted, int MissingBlobs, string Target);
    public sealed record Result(bool Executed, Plan Plan, string? File, int Written, FolderBundle.VerifyResult? Verify);
    public sealed record Mismatch(long? LegacyId, long? Seq, string Issue);
    public sealed record Comparison(bool Ok, string File, string FolderKey, int DatabaseMessages, int FileMessages,
        int Compared, Mismatch[] Mismatches);

    /// <summary>Event recorded in a converted file, its detail the database folder key it was copied from.</summary>
    public const string ConvertedEvent = "converted_from_database";

    internal sealed record LocalFolder(string Key, string? Parent, string Name, string Kind, string Origin, string? OriginKey);

    public Result Convert(string keyOrPath, string targetDirectory, bool execute, CancellationToken cancellationToken = default)
    {
        var (folder, path, rows, ownedHidden, adoptedHidden) = Load(keyOrPath, cancellationToken);
        var target = System.IO.Path.Combine(System.IO.Path.GetFullPath(targetDirectory), SafeName(folder.Name), FolderBundle.FileName);
        var missing = rows.Count(row => row.RawPath is null || !new RawBlobStore(paths).IsAvailable(row.RawPath));
        var plan = new Plan(folder.Key, path, folder.Kind, folder.Origin, folder.OriginKey, rows.Count,
            rows.Count(row => !row.IsRead), ownedHidden, adoptedHidden, missing, target);
        if (!execute) return new(false, plan, null, 0, null);
        if (missing > 0) throw new InvalidDataException($"{missing} messages in {path} have no readable blob; nothing written.");
        if (File.Exists(target)) throw new IOException($"{target} already exists; choose another --to or remove it.");

        // Build beside the target and move into place only once complete and verified, so a half-written file
        // never looks like a folder.
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var blobs = new RawBlobStore(paths);
        var written = 0;
        FolderBundle.VerifyResult verify;
        try
        {
            using (var bundle = FolderBundle.Create(temporary, folder.Name, BundleKind(folder.Kind)))
            {
                bundle.RecordEvent(ConvertedEvent, folder.Key);
                bundle.RecordEvent(ArchiveManifest.LayoutEvent, System.Text.Json.JsonSerializer.Serialize(
                    new ArchiveManifest.Layout(path, folder.Kind, folder.Origin, folder.OriginKey)));
                foreach (var row in rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var raw = blobs.ReadBytes(row.RawPath!);
                    bundle.Add(raw, row.IsRead, Origin(row), row.DateUtc, row.Id);
                    written++;
                }
                verify = bundle.Verify(cancellationToken);
            }
            SqliteConnection.ClearAllPools();
            if (!verify.Ok) throw new InvalidDataException("The new folder file failed its own verification; nothing kept: "
                + string.Join("; ", verify.Problems.Select(problem => problem.Issue)));
            Checkpoint(temporary);
            File.Move(temporary, target);
        }
        finally
        {
            foreach (var leftover in new[] { temporary, temporary + "-wal", temporary + "-shm" })
                if (File.Exists(leftover)) File.Delete(leftover);
        }
        return new(true, plan, target, written, verify with { Path = target });
    }

    /// <summary>Reads the folder back from the file alone and compares every message with the database row it
    /// came from: same bytes, same read state, and the same set of messages.</summary>
    public Comparison Compare(string file, CancellationToken cancellationToken = default)
    {
        using var bundle = FolderBundle.Open(file);

        var summaries = bundle.List(includeDeleted: true);
        var legacy = summaries.Where(summary => summary.LegacyId is not null).ToDictionary(summary => summary.LegacyId!.Value);
        var keyOrPath = bundle.LastEventDetail(ConvertedEvent)
            ?? throw new InvalidDataException($"{file} was not converted from the mail database; there is nothing to compare it with.");
        var (folder, _, rows, _, _) = Load(keyOrPath, cancellationToken);
        var blobs = new RawBlobStore(paths);
        var mismatches = new List<Mismatch>();
        var compared = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!legacy.Remove(row.Id, out var summary))
            {
                mismatches.Add(new(row.Id, null, "in the database folder but not in the file"));
                continue;
            }
            compared++;
            var dbBytes = blobs.ReadBytes(row.RawPath!);
            var fileBytes = bundle.Raw(summary.Seq)!;
            if (!dbBytes.AsSpan().SequenceEqual(fileBytes)) mismatches.Add(new(row.Id, summary.Seq, "message bytes differ"));
            if (summary.IsRead != row.IsRead) mismatches.Add(new(row.Id, summary.Seq,
                $"read state differs (database {row.IsRead}, file {summary.IsRead})"));
            if (summary.DeletedAt is not null) mismatches.Add(new(row.Id, summary.Seq, "deleted in the file but visible in the database"));
        }
        foreach (var extra in legacy.Values) mismatches.Add(new(extra.LegacyId, extra.Seq, "in the file but not in the database folder"));
        foreach (var unlinked in summaries.Where(summary => summary.LegacyId is null))
            mismatches.Add(new(null, unlinked.Seq, "no database row recorded for this message"));
        return new(mismatches.Count == 0, file, folder.Key, rows.Count, summaries.Count, compared, mismatches.ToArray());
    }

    internal (LocalFolder Folder, string Path, List<Source> Rows, int OwnedHidden, int AdoptedHidden) Load(
        string keyOrPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.DatabasePath)) throw new FileNotFoundException("Mail database does not exist.", paths.DatabasePath);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        // One snapshot for the folder list, its rows and the hiding evidence.
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        var folders = new Dictionary<string, LocalFolder>(StringComparer.Ordinal);
        command.CommandText = "SELECT folder_key,parent_key,name,kind,origin,origin_key FROM local_folders";
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                folders[reader.GetString(0)] = new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5));
        string PathOf(LocalFolder folder, int depth = 0) => depth > 64
            ? throw new InvalidDataException("Local folder hierarchy contains a cycle.")
            : folder.Parent is { } parent && folders.TryGetValue(parent, out var up) ? PathOf(up, depth + 1) + "/" + folder.Name : folder.Name;
        var matches = folders.Values.Where(folder => folder.Key == keyOrPath
            || string.Equals(PathOf(folder), keyOrPath.Trim('/'), StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0) throw new ArgumentException($"No local folder has key or path '{keyOrPath}'.");
        if (matches.Length > 1) throw new ArgumentException($"'{keyOrPath}' matches {matches.Length} folders; use the folder key.");
        var target = matches[0];

        var rows = new List<Source>();
        void Read(string sql, Action<SqliteCommand> bind)
        {
            command.Parameters.Clear();
            command.CommandText = sql;
            bind(command);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetBoolean(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
        }
        const string Columns = "SELECT id,source,account,folder,source_key,is_read,date_utc,raw_path FROM messages ";
        Read(Columns + "WHERE source=$live AND folder=$key AND converted_draft_id IS NULL", bind =>
        {
            bind.Parameters.AddWithValue("$live", MailContract.LiveSource);
            bind.Parameters.AddWithValue("$key", target.Key);
        });

        var ownedHidden = 0;
        var adoptedHidden = 0;
        if (target.Origin == MailContract.ArchiveSource && target.OriginKey?.Split('|', 2) is [var account, var archiveFolder])
        {
            var imported = new List<Source>();
            var before = rows.Count;
            Read(Columns + "WHERE source=$archive AND account=$account AND folder=$folder AND converted_draft_id IS NULL", bind =>
            {
                bind.Parameters.AddWithValue("$archive", MailContract.ArchiveSource);
                bind.Parameters.AddWithValue("$account", account);
                bind.Parameters.AddWithValue("$folder", archiveFolder);
            });
            imported.AddRange(rows.Skip(before));
            rows.RemoveRange(before, rows.Count - before);

            // The same two rules the folder tree applies (MailDatabase.VisibleArchiveFolderCounts): an import
            // stands down where the mailbox holds its own copy, and where its adopted copy was deleted.
            var removed = new HashSet<string>(StringComparer.Ordinal);
            command.Parameters.Clear();
            command.CommandText = "SELECT source_key FROM local_deletions WHERE source=$live AND account=$account";
            command.Parameters.AddWithValue("$live", MailContract.LiveSource);
            command.Parameters.AddWithValue("$account", MailContract.LocalCopyAccount);
            using (var reader = command.ExecuteReader())
                while (reader.Read()) removed.Add(reader.GetString(0));
            var owned = new HashSet<(string, string, string)>();
            command.Parameters.Clear();
            command.CommandText = "SELECT m.subject,m.sender,COALESCE(m.date_utc,'') FROM messages m "
                + "WHERE m.source=$live AND m.date_utc IN (SELECT date_utc FROM messages WHERE source=$archive AND account=$account AND folder=$folder)";
            command.Parameters.AddWithValue("$live", MailContract.LiveSource);
            command.Parameters.AddWithValue("$archive", MailContract.ArchiveSource);
            command.Parameters.AddWithValue("$account", account);
            command.Parameters.AddWithValue("$folder", archiveFolder);
            using (var reader = command.ExecuteReader())
                while (reader.Read()) owned.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            var heads = new Dictionary<long, (string, string, string)>();
            command.Parameters.Clear();
            command.CommandText = "SELECT id,subject,sender,COALESCE(date_utc,'') FROM messages WHERE source=$archive AND account=$account AND folder=$folder";
            command.Parameters.AddWithValue("$archive", MailContract.ArchiveSource);
            command.Parameters.AddWithValue("$account", account);
            command.Parameters.AddWithValue("$folder", archiveFolder);
            using (var reader = command.ExecuteReader())
                while (reader.Read()) heads[reader.GetInt64(0)] = (reader.GetString(1), reader.GetString(2), reader.GetString(3));
            foreach (var row in imported)
            {
                if (owned.Contains(heads[row.Id])) { ownedHidden++; continue; }
                if (removed.Contains(MailContract.AdoptedCopyKeyPrefix + row.Id)) { adoptedHidden++; continue; }
                rows.Add(row);
            }
        }
        transaction.Commit();
        rows.Sort((left, right) => string.CompareOrdinal(left.DateUtc, right.DateUtc) is var order and not 0 ? order : left.Id.CompareTo(right.Id));
        return (target, PathOf(target), rows, ownedHidden, adoptedHidden);
    }

    /// <summary>Where the message came from, kept with it: the database row's source, account, folder and key.</summary>
    private static string Origin(Source row) => $"{row.Store}:{row.Account}/{row.Folder}/{row.SourceKey}";

    private static string BundleKind(string kind) => kind is "inbox" or "drafts" or "outbox" or "sent" or "deleted" ? kind : "local";

    internal static string SafeName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim(' ', '.');
        return safe.Length == 0 ? "folder" : safe;
    }

    /// <summary>Folds the WAL into the main file so the single .mkdb is the whole folder before it is moved.</summary>
    private static void Checkpoint(string file)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = file, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;";
        command.ExecuteNonQuery();
    }
}
