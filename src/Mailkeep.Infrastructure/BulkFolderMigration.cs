using System.Diagnostics;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Infrastructure;

/// <summary>Offline format conversion. The original database and all message identities survive unchanged.
/// Live writes additionally require an explicit opt-in from the offline command.</summary>
public sealed partial class BulkFolderMigration(AppPaths paths, bool allowLive = false)
{
    public sealed record Folder(string Source, string Account, string Path, long Messages, long MimeBytes = 0);
    public sealed record Plan(string Root, string Confirmation, Folder[] Folders, long Messages, long Excluded, long DatabaseBytes)
    {
        public long MimeBytes => Folders.Sum(f => f.MimeBytes);
        // Working DB plus portable payloads, metadata/search copies, journals and headroom.
        // Existing source files and the original DB stay in place; publication only renames them.
        public long RequiredFreeBytes => checked(DatabaseBytes * 4 + MimeBytes * 2 + 1024L * 1024 * 1024);
    }
    public sealed record Output(string Uuid, Folder Folder, long Bytes, string ContentDigest);
    public sealed record Progress(string Phase, long Messages, long Bytes, double ElapsedSeconds);
    public sealed record RunState(string Id, string Phase, Plan Plan, Output[] Outputs, string? Error = null, string? SchemaDigest = null,
        string SchemaDigestFormat = "sha256-length-prefixed-utf8-v1");
    private string Work => Path.Combine(paths.Root, "bulk-migration");
    private string StateFile => Path.Combine(Work, "run.json");
    private string Candidate => Path.Combine(Work, "candidate.db");
    private string Rollback => Path.Combine(Work, "original.db");
    private string PendingFile => Path.Combine(Work, "pending");
    private string BundleDirectory => Path.Combine(paths.Root, "bound-folders");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private FileStream RunLease()
    {
        Directory.CreateDirectory(paths.Root);
        return new FileStream(Path.Combine(paths.Root, ".bulk-migration.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public RunState? Status() => File.Exists(StateFile) ? JsonSerializer.Deserialize<RunState>(File.ReadAllText(StateFile)) : null;

    internal static void CheckAvailable(AppPaths paths)
    {
        if (File.Exists(Path.Combine(paths.Root, "pilot-consolidation", "pending")))
            throw new IOException("Offline pilot consolidation is pending; recover it before opening Mailkeep.");
        if (File.Exists(Path.Combine(paths.Root, "bulk-migration", "pending")))
            throw new IOException("Offline storage conversion is pending. Use folder storage bulk status/recover before opening Mailkeep.");
    }

    private void RequireDisposable()
    {
        if (!allowLive && paths.Root.TrimEnd('\\', '/').Equals(new AppPaths().Root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Live bulk migration requires explicit --allow-live in addition to root and plan confirmations.");
    }

    internal static SqliteConnection Connect(string file, bool write = false, int timeoutSeconds = 1)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = file, Mode = write ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = timeoutSeconds }.ToString());
        c.Open(); return c;
    }

    internal static object? Scalar(SqliteConnection c, string sql, params (string Name, object Value)[] args)
    {
        using var q = c.CreateCommand(); q.CommandText = sql;
        foreach (var (name, value) in args) q.Parameters.AddWithValue(name, value);
        return q.ExecuteScalar();
    }

    internal static bool HasTable(SqliteConnection c, string name)
        => Convert.ToInt64(Scalar(c, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$name", ("$name", name))) != 0;

    private static string Eligibility(SqliteConnection c)
    {
        var where = "m.source IN ('archive','live') AND m.converted_draft_id IS NULL";
        if (HasTable(c, "folder_storage_registry")) where += " AND NOT (m.source='live' AND m.folder IN (SELECT folder_key FROM folder_storage_registry))";
        if (HasTable(c, "bulk_bindings")) where += " AND NOT EXISTS(SELECT 1 FROM bulk_bindings b WHERE " + BindingMatch(c, "m") + ")";
        return where;
    }

    private string[] ConnectedAccounts() => new AccountStore(paths).Load().Where(a => a.IsConnected).Select(a => a.Name).ToArray();

    public Plan Preview()
    {
        using var c = Connect(paths.DatabasePath);
        var connected = ConnectedAccounts().ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var q = c.CreateCommand();
        q.CommandText = $"SELECT source,account,folder,count(*),coalesce(sum(max(size,0)),0) FROM messages m WHERE {Eligibility(c)} GROUP BY source,account,folder ORDER BY source,account,folder";
        var folders = new List<Folder>();
        using (var r = q.ExecuteReader())
            while (r.Read())
                if (r.GetString(0) != "live" || !connected.Contains(r.GetString(1)))
                    folders.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4)));
        var messages = folders.Sum(f => f.Messages);
        var excluded = Convert.ToInt64(Scalar(c, "SELECT count(*) FROM messages")) - messages;
        object Stamp(string name)
        {
            var f = new FileInfo(name); return f.Exists ? new { f.Length, Ticks = f.LastWriteTimeUtc.Ticks } : new { Length = 0L, Ticks = 0L };
        }
        var digest = FolderBundle.Sha256(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { paths.Root, Folders = folders, Db = Stamp(paths.DatabasePath), Wal = Stamp(paths.DatabasePath + "-wal"), Connected = connected.Order().ToArray() })));
        return new(paths.Root, digest, folders.ToArray(), messages, excluded, new FileInfo(paths.DatabasePath).Length);
    }

    internal static string[] MessageColumns(SqliteConnection c)
    {
        using var q = c.CreateCommand(); q.CommandText = "PRAGMA table_info(messages)";
        using var r = q.ExecuteReader(); var columns = new List<string>();
        while (r.Read()) columns.Add(r.GetString(1));
        return columns.ToArray();
    }

    internal static string Snapshot(SqliteConnection c, string alias = "m")
        => "json_object(" + string.Join(',', MessageColumns(c).Select(n => $"'{n}',{alias}.\"{n}\""))
            + $",'marks',json((SELECT json_group_array(json_object('kind',kind,'marked_at',marked_at)) FROM message_marks WHERE message_id={alias}.id))"
            + (HasTable(c, "settings") ? $",'import_origins',json((SELECT value FROM settings WHERE key='folder-storage.origins:'||{alias}.id))" : "") + ")";

    private static string Hierarchy(SqliteConnection c)
    {
        var result = new Dictionary<string, List<Dictionary<string, object?>>>();
        foreach (var table in new[] { "local_folders", "archive_folders" })
        {
            using var q = c.CreateCommand(); q.CommandText = "SELECT * FROM " + table;
            using var r = q.ExecuteReader(); var rows = new List<Dictionary<string, object?>>();
            while (r.Read()) rows.Add(Enumerable.Range(0, r.FieldCount).ToDictionary(r.GetName, i => r.IsDBNull(i) ? null : r.GetValue(i)));
            result[table] = rows;
        }
        return JsonSerializer.Serialize(result);
    }

    private IEnumerable<FolderBundle.BulkMessage> Rows(SqliteConnection source, Folder folder, CancellationToken cancel)
    {
        using var q = source.CreateCommand();
        q.CommandText = $"SELECT id,raw_path,{Snapshot(source)} FROM messages m WHERE source=$source AND account=$account AND folder=$folder AND converted_draft_id IS NULL ORDER BY id";
        q.Parameters.AddWithValue("$source", folder.Source); q.Parameters.AddWithValue("$account", folder.Account); q.Parameters.AddWithValue("$folder", folder.Path);
        using var r = q.ExecuteReader();
        var blobRoot = Path.GetFullPath(paths.BlobDirectory) + Path.DirectorySeparatorChar;
        while (r.Read())
        {
            cancel.ThrowIfCancellationRequested();
            if (r.IsDBNull(1)) throw new InvalidDataException($"Missing MIME: {folder.Account}/{folder.Path}, message {r.GetInt64(0)}");
            var full = Path.GetFullPath(Path.Combine(paths.BlobDirectory, r.GetString(1)));
            if (!full.StartsWith(blobRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("MIME path escapes blob store.");
            yield return new(r.GetInt64(0), r.GetString(2), File.ReadAllBytes(full), Path.GetFileNameWithoutExtension(full));
        }
    }

    public RunState Run(string confirmation, Action<Progress>? progress = null, CancellationToken cancel = default, bool publish = false)
    {
        RequireDisposable();
        using var runLease = RunLease();
        var previous = Status();
        if (previous?.Phase == "aborted") Directory.Move(Work, Work + ".aborted-" + previous.Id);
        else if (previous is not null) throw new InvalidOperationException("A bulk run already exists; inspect or recover it.");
        using var guard = Connect(paths.DatabasePath, true);
        Scalar(guard, "BEGIN IMMEDIATE"); // Freeze every SQLite writer, including older clients.
        var guardActive = true;
        var plan = Preview();
        if (plan.Confirmation != confirmation) throw new InvalidOperationException("Mailbox changed; review a fresh plan.");
        if (plan.Messages == 0) throw new InvalidOperationException("No eligible mail remains.");
        if (new DriveInfo(Path.GetPathRoot(paths.Root)!).AvailableFreeSpace < plan.RequiredFreeBytes)
            throw new IOException($"Insufficient space for staging: require {plan.RequiredFreeBytes:N0} free bytes; source is unchanged.");
        Directory.CreateDirectory(Work);
        var state = new RunState(Guid.NewGuid().ToString("N"), "copying", plan, []);
        Save(state);
        using (var pending = new FileStream(PendingFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough)) pending.Flush(true);
        var watch = Stopwatch.StartNew(); long messages = 0, bytes = 0;
        try
        {
            using var source = Connect(paths.DatabasePath);
            using (var candidate = Connect(Candidate, true))
            {
                source.BackupDatabase(candidate);
                if (!Equals(Scalar(candidate, "PRAGMA quick_check"), "ok")) throw new InvalidDataException("Working copy check failed.");
            }
            progress?.Invoke(new("backup", 0, 0, watch.Elapsed.TotalSeconds));
            state = state with { Phase = "writing" }; Save(state);
            var outputs = new List<Output>(); var hierarchy = Hierarchy(source);
            foreach (var folder in plan.Folders)
            {
                cancel.ThrowIfCancellationRequested();
                var uuid = Guid.NewGuid().ToString("N"); var file = Path.Combine(Work, uuid, FolderBundle.FileName);
                var result = FolderBundle.WriteBulk(file, uuid, new(folder.Source, folder.Account, folder.Path, hierarchy), Rows(source, folder, cancel), cancel);
                if (result.Messages != folder.Messages) throw new InvalidDataException("Folder count changed.");
                progress?.Invoke(new("folder-written", messages + result.Messages, bytes + result.Bytes, watch.Elapsed.TotalSeconds));
                if (VerifyFolder(source, folder, file, cancel) != result.ContentDigest)
                    throw new InvalidDataException("Source/output occurrence digest differs.");
                outputs.Add(new(uuid, folder, result.Bytes, result.ContentDigest));
                messages += result.Messages; bytes += result.Bytes;
                state = state with { Outputs = outputs.ToArray() }; Save(state);
                progress?.Invoke(new("folder-verified", messages, bytes, watch.Elapsed.TotalSeconds));
            }
            using (var candidate = Connect(Candidate, true))
            {
                InstallBindings(candidate, outputs);
                VerifyUnchanged(source, candidate, cancel);
                state = state with { SchemaDigest = SchemaDigest(candidate) };
                if (!Equals(Scalar(candidate, "PRAGMA quick_check"), "ok")) throw new InvalidDataException("Candidate check failed.");
                Scalar(candidate, "PRAGMA wal_checkpoint(TRUNCATE)"); Scalar(candidate, "PRAGMA journal_mode=DELETE");
            }
            state = state with { Phase = "verified" }; Save(state);
            progress?.Invoke(new("verified", messages, bytes, watch.Elapsed.TotalSeconds));
            if (publish)
            {
                source.Dispose();
                Scalar(guard, "ROLLBACK"); guardActive = false; guard.Dispose();
                PreparePublication(state);
                state = state with { Phase = "publishing" };
                state = CompletePublication(state, verifiedFiles: true);
                progress?.Invoke(new("complete", messages, bytes, watch.Elapsed.TotalSeconds));
            }
            return state;
        }
        catch (Exception e)
        {
            if (state.Phase != "complete") Save(state with { Phase = state.Phase == "publishing" ? "publishing" : "failed", Error = e.Message });
            throw;
        }
        finally { if (guardActive) Scalar(guard, "ROLLBACK"); }
    }

    private string VerifyFolder(SqliteConnection source, Folder folder, string file, CancellationToken cancel)
    {
        using var bundle = FolderBundle.Open(file);
        using var c = Connect(file);
        if (bundle.Folder().Uuid != Path.GetFileName(Path.GetDirectoryName(file)))
            throw new InvalidDataException("Portable folder UUID differs.");
        using (var binding = c.CreateCommand())
        {
            binding.CommandText = "SELECT source,account,folder,hierarchy FROM binding";
            using var b = binding.ExecuteReader();
            if (!b.Read() || b.GetString(0) != folder.Source || b.GetString(1) != folder.Account
                || b.GetString(2) != folder.Path || b.GetString(3) != Hierarchy(source) || b.Read())
                throw new InvalidDataException("Portable binding or hierarchy differs.");
        }
        if (!Equals(Scalar(c, "PRAGMA integrity_check"), "ok")) throw new InvalidDataException("Folder integrity failed.");
        using var q = c.CreateCommand();
        q.CommandText = "SELECT o.message_id,o.metadata,m.raw,m.sha256,s.is_read FROM local_occurrences o JOIN messages m USING(seq) JOIN state s USING(seq) WHERE s.deleted_at IS NULL ORDER BY o.message_id";
        using var r = q.ExecuteReader(); long count = 0;
        using var originals = source.CreateCommand();
        originals.CommandText = $"SELECT id,raw_path,{Snapshot(source)} FROM messages m WHERE source=$source AND account=$account AND folder=$folder AND converted_draft_id IS NULL ORDER BY id";
        originals.Parameters.AddWithValue("$source", folder.Source); originals.Parameters.AddWithValue("$account", folder.Account); originals.Parameters.AddWithValue("$folder", folder.Path);
        using var rows = originals.ExecuteReader();
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (rows.Read())
        {
            cancel.ThrowIfCancellationRequested();
            var id = rows.GetInt64(0); var metadata = rows.GetString(2);
            // The source blob's pre-existing content address is checked during its one input read.
            // Independently hash the persisted destination against that address; no second source MIME read.
            var sha = Path.GetFileNameWithoutExtension(rows.GetString(1));
            if (!r.Read() || r.GetInt64(0) != id || r.GetString(1) != metadata
                || FolderBundle.Sha256((byte[])r.GetValue(2)) != sha || r.GetString(3) != sha)
                throw new InvalidDataException("Independent source/output comparison failed.");
            using var meta = JsonDocument.Parse(metadata);
            if (r.GetInt32(4) != meta.RootElement.GetProperty("is_read").GetInt32()) throw new InvalidDataException("Read state differs.");
            FolderBundle.DigestOccurrence(digest, id, sha, metadata);
            count++;
        }
        if (r.Read() || count != folder.Messages) throw new InvalidDataException("Unexpected folder occurrences.");
        foreach (var table in new[] { "messages", "state", "local_occurrences", "headers", "fts" })
            if (Convert.ToInt64(Scalar(c, "SELECT count(*) FROM " + table)) != count)
                throw new InvalidDataException("Unexpected or missing folder rows: " + table);
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    // One sequential comparison of the original tables, including all message fields and ancillary state.
    private static string SchemaDigest(SqliteConnection c)
    {
        using var q = c.CreateCommand();
        q.CommandText = "SELECT type,name,sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY type,name";
        using var rows = q.ExecuteReader();
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[8];
        while (rows.Read())
            for (var i = 0; i < 3; i++)
            {
                var bytes = Encoding.UTF8.GetBytes(rows.GetString(i));
                BinaryPrimitives.WriteInt64LittleEndian(length, bytes.LongLength);
                digest.AppendData(length); digest.AppendData(bytes);
            }
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    private static void VerifyUnchanged(SqliteConnection source, SqliteConnection candidate, CancellationToken cancel)
    {
        using (var schema = source.CreateCommand())
        {
            schema.CommandText = "SELECT type,name,sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY type,name";
            using var rows = schema.ExecuteReader();
            while (rows.Read())
                if (!Equals(Scalar(candidate, "SELECT sql FROM sqlite_master WHERE type=$type AND name=$name",
                    ("$type", rows.GetString(0)), ("$name", rows.GetString(1))), rows.GetString(2)))
                    throw new InvalidDataException("Original schema differs: " + rows.GetString(1));
        }
        using var tables = source.CreateCommand();
        tables.CommandText = "SELECT name FROM pragma_table_list WHERE schema='main' AND type IN ('table','virtual') AND (name NOT LIKE 'sqlite_%' OR name='sqlite_sequence') ORDER BY name";
        var names = new List<string>(); using (var r = tables.ExecuteReader()) while (r.Read()) names.Add(r.GetString(0));
        foreach (var name in names)
        {
            var quoted = "\"" + name.Replace("\"", "\"\"") + "\"";
            var primary = new SortedDictionary<long, string>();
            using (var schema = source.CreateCommand())
            {
                schema.CommandText = "PRAGMA table_info(" + quoted + ")";
                using var r = schema.ExecuteReader();
                while (r.Read()) if (r.GetInt64(5) > 0) primary[r.GetInt64(5)] = "\"" + r.GetString(1).Replace("\"", "\"\"") + "\"";
            }
            using var a = source.CreateCommand(); using var b = candidate.CreateCommand();
            a.CommandText = b.CommandText = "SELECT * FROM " + quoted + " ORDER BY " + (primary.Count == 0 ? "rowid" : string.Join(',', primary.Values));
            if (name == "sqlite_sequence")
            {
                a.CommandText = "SELECT * FROM sqlite_sequence ORDER BY name";
                b.CommandText = "SELECT * FROM sqlite_sequence WHERE name IN (SELECT value FROM json_each($tables)) ORDER BY name";
                b.Parameters.AddWithValue("$tables", Scalar(source, "SELECT json_group_array(name) FROM sqlite_master WHERE type='table'")!);
            }
            using var x = a.ExecuteReader(); using var y = b.ExecuteReader();
            while (x.Read())
            {
                cancel.ThrowIfCancellationRequested();
                if (!y.Read() || x.FieldCount != y.FieldCount) throw new InvalidDataException("Candidate table differs: " + name);
                for (var i = 0; i < x.FieldCount; i++)
                {
                    var l = x.GetValue(i); var r = y.GetValue(i);
                    if (l is byte[] lb && r is byte[] rb ? !lb.AsSpan().SequenceEqual(rb) : !Equals(l, r))
                        throw new InvalidDataException("Candidate value differs: " + name);
                }
            }
            if (y.Read()) throw new InvalidDataException("Candidate has extra rows: " + name);
        }
    }

    private void Save(RunState state)
    {
        var temporary = StateFile + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { JsonSerializer.Serialize(stream, state, Json); stream.Flush(true); }
        File.Move(temporary, StateFile, true);
    }
}
