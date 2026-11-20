using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using MimeKit;

namespace Mailkeep.Infrastructure;

/// <summary>One folder as one self-describing SQLite file (docs/folder-store-redesign.md). The message bytes,
/// read state and event log are the truth; the header and search tables are caches that can be rebuilt from
/// the bytes. Nothing outside the file is needed to open, list, read or verify it.</summary>
public sealed partial class FolderBundle : IDisposable
{
    /// <summary>"MKFD", stamped in the SQLite header so any tool can tell a Mailkeep folder from other SQLite.</summary>
    public const int ApplicationId = 0x4D4B4644;
    public const int FormatVersion = 1;
    public const string FileName = "folder.mkdb";

    public sealed record Meta(string Format, string Uuid, string Name, string Kind, string CreatedAt, long NextSeq);
    public sealed record Summary(long Seq, string Sha256, long Size, bool IsRead, string? DeletedAt, string Subject,
        string Sender, string SenderName, string Recipients, string? DateUtc, string? MessageId, int AttachmentCount,
        string Snippet, string Origin, long? LegacyId);
    public sealed record Message(Summary Summary, byte[] Raw);
    public sealed record Problem(long? Seq, string Issue);
    public sealed record VerifyResult(bool Ok, string Path, Meta Folder, int Messages, int Deleted, string Integrity,
        int HashesChecked, int CacheRows, Problem[] Problems);

    private const string Schema = """
        CREATE TABLE folder (
            format     TEXT NOT NULL,
            uuid       TEXT NOT NULL,
            name       TEXT NOT NULL,
            kind       TEXT NOT NULL,
            created_at TEXT NOT NULL,
            next_seq   INTEGER NOT NULL
        );
        -- Truth: the message bytes exactly as received, never rewritten.
        CREATE TABLE messages (
            seq         INTEGER PRIMARY KEY,
            sha256      TEXT NOT NULL,
            raw         BLOB NOT NULL,
            size        INTEGER NOT NULL,
            received_at TEXT
        );
        -- Truth: what the reader did with each message. Never written into the message bytes.
        CREATE TABLE state (
            seq         INTEGER PRIMARY KEY REFERENCES messages(seq),
            is_read     INTEGER NOT NULL DEFAULT 0,
            flagged     INTEGER NOT NULL DEFAULT 0,
            deleted_at  TEXT,
            origin      TEXT NOT NULL,
            added_at    TEXT NOT NULL,
            copied_from TEXT,
            legacy_id   INTEGER
        );
        -- Truth: append-only history, for undo and for answering "where did this message go".
        CREATE TABLE events (
            id     INTEGER PRIMARY KEY AUTOINCREMENT,
            seq    INTEGER,
            ev     TEXT NOT NULL,
            at     TEXT NOT NULL,
            detail TEXT
        );
        -- Cache: parsed from messages.raw; RebuildCaches recreates it.
        CREATE TABLE headers (
            seq              INTEGER PRIMARY KEY REFERENCES messages(seq),
            subject          TEXT NOT NULL DEFAULT '',
            sender           TEXT NOT NULL DEFAULT '',
            sender_name      TEXT NOT NULL DEFAULT '',
            recipients       TEXT NOT NULL DEFAULT '',
            date_utc         TEXT,
            message_id       TEXT,
            attachment_count INTEGER NOT NULL DEFAULT 0,
            snippet          TEXT NOT NULL DEFAULT ''
        );
        CREATE INDEX idx_headers_date ON headers(date_utc DESC);
        -- Cache: full-text search over the same parse.
        CREATE VIRTUAL TABLE fts USING fts5(subject, sender, recipients, body_text,
            tokenize='unicode61 remove_diacritics 2');
        """;

    private readonly SqliteConnection _connection;

    private FolderBundle(SqliteConnection connection, string path)
    {
        _connection = connection;
        Path = path;
    }

    public string Path { get; }

    internal void PrepareLocalStorage()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS local_folder (singleton INTEGER PRIMARY KEY CHECK(singleton=1), metadata TEXT NOT NULL, removed INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS local_occurrences (message_id INTEGER PRIMARY KEY, seq INTEGER NOT NULL UNIQUE, metadata TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS local_applied (operation TEXT PRIMARY KEY);
            """;
        command.ExecuteNonQuery();
    }

    internal void ApplyLocal(string operation, long? messageId, string metadata, byte[]? raw, bool removed)
        => ApplyLocalBatch([(operation, messageId, metadata, raw, removed)]);

    internal void ApplyLocalBatch(IReadOnlyList<(string Token, long? Message, string Metadata, byte[]? Raw, bool Removed)> operations)
    {
        PrepareLocalStorage();
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        foreach (var operation in operations)
        {
            command.Parameters.Clear();
            ApplyLocalCore(command, operation.Token, operation.Message, operation.Metadata, operation.Raw, operation.Removed);
        }
        transaction.Commit();
    }

    private static void ApplyLocalCore(SqliteCommand command, string operation, long? messageId, string metadata, byte[]? raw, bool removed)
    {
        command.CommandText = "INSERT OR IGNORE INTO local_applied(operation) VALUES($operation)";
        command.Parameters.AddWithValue("$operation", operation);
        if (command.ExecuteNonQuery() == 0) return;
        command.Parameters.Clear();
        if (messageId is null)
        {
            command.CommandText = """
                INSERT INTO local_folder VALUES(1,$metadata,$removed) ON CONFLICT(singleton) DO UPDATE SET metadata=excluded.metadata,removed=excluded.removed;
                UPDATE folder SET name=json_extract($metadata,'$.Name'),kind=json_extract($metadata,'$.Kind');
                """;
            command.Parameters.AddWithValue("$metadata", metadata);
            command.Parameters.AddWithValue("$removed", removed);
            command.ExecuteNonQuery();
        }
        else
        {
            command.CommandText = "SELECT seq FROM local_occurrences WHERE message_id=$id";
            command.Parameters.AddWithValue("$id", messageId.Value);
            var existing = command.ExecuteScalar();
            long? seq = existing is long value ? value : null;
            using var doc = System.Text.Json.JsonDocument.Parse(metadata);
            var row = doc.RootElement;
            if (seq is null && !removed)
            {
                if (raw is null) throw new IOException("New folder occurrence has no MIME bytes.");
                command.CommandText = "UPDATE folder SET next_seq=next_seq+1 RETURNING next_seq-1";
                seq = (long)command.ExecuteScalar()!;
                command.Parameters.Clear();
                command.CommandText = """
                    INSERT INTO messages(seq,sha256,raw,size,received_at) VALUES($seq,$sha,$raw,$size,$date);
                    INSERT INTO state(seq,is_read,origin,added_at,legacy_id) VALUES($seq,$read,$origin,$now,$id);
                    INSERT INTO local_occurrences(message_id,seq,metadata) VALUES($id,$seq,$metadata);
                    """;
                command.Parameters.AddWithValue("$seq", seq.Value);
                command.Parameters.AddWithValue("$sha", Sha256(raw));
                command.Parameters.AddWithValue("$raw", raw);
                command.Parameters.AddWithValue("$size", raw.LongLength);
                command.Parameters.AddWithValue("$date", row.GetProperty("date_utc").ValueKind == System.Text.Json.JsonValueKind.Null ? DBNull.Value : row.GetProperty("date_utc").GetString());
                command.Parameters.AddWithValue("$read", row.GetProperty("is_read").GetInt32());
                command.Parameters.AddWithValue("$origin", row.GetProperty("source_key").GetString()!);
                command.Parameters.AddWithValue("$now", Now());
                command.Parameters.AddWithValue("$id", messageId.Value);
                command.Parameters.AddWithValue("$metadata", metadata);
                command.ExecuteNonQuery();
                WriteCaches(command, seq.Value, Parse(raw));
            }
            else if (seq is not null)
            {
                command.Parameters.Clear();
                command.CommandText = "SELECT sha256 FROM messages WHERE seq=$seq";
                command.Parameters.AddWithValue("$seq", seq.Value);
                if (raw is not null && !Equals(command.ExecuteScalar(), Sha256(raw)))
                    throw new InvalidDataException("An existing occurrence's MIME bytes cannot be replaced.");
                command.CommandText = "UPDATE state SET is_read=$read,deleted_at=$deleted WHERE seq=$seq; UPDATE messages SET received_at=$date WHERE seq=$seq; UPDATE local_occurrences SET metadata=$metadata WHERE seq=$seq";
                command.Parameters.AddWithValue("$read", row.GetProperty("is_read").GetInt32());
                command.Parameters.AddWithValue("$deleted", removed ? Now() : DBNull.Value);
                command.Parameters.AddWithValue("$metadata", metadata);
                command.Parameters.AddWithValue("$date", row.GetProperty("date_utc").ValueKind == System.Text.Json.JsonValueKind.Null ? DBNull.Value : row.GetProperty("date_utc").GetString());
                command.ExecuteNonQuery();
            }
        }
        command.Parameters.Clear();
        command.CommandText = "INSERT INTO events(seq,ev,at,detail) VALUES(NULL,$ev,$at,$detail)";
        command.Parameters.AddWithValue("$ev", messageId is null ? "local-folder" : removed ? "local-removed" : "local-updated");
        command.Parameters.AddWithValue("$at", Now());
        command.Parameters.AddWithValue("$detail", metadata);
        command.ExecuteNonQuery();
    }

    internal byte[]? LocalRaw(long messageId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT raw FROM messages JOIN local_occurrences USING(seq) WHERE message_id=$id";
        command.Parameters.AddWithValue("$id", messageId);
        return command.ExecuteScalar() as byte[];
    }

    internal bool HasLocalOccurrence(long messageId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM local_occurrences WHERE message_id=$id";
        command.Parameters.AddWithValue("$id", messageId);
        return command.ExecuteScalar() is not null;
    }

    /// <summary>Makes a new, empty folder file. Refuses to overwrite: a folder file is somebody's mail.</summary>
    public static FolderBundle Create(string path, string name, string kind, string? uuid = null)
    {
        if (File.Exists(path)) throw new IOException($"A folder file already exists at {path}.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA application_id={ApplicationId}; PRAGMA user_version={FormatVersion}; PRAGMA journal_mode=WAL;";
        command.ExecuteNonQuery();
        using var transaction = connection.BeginTransaction();
        command.Transaction = transaction;
        command.CommandText = Schema;
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO folder(format,uuid,name,kind,created_at,next_seq) VALUES($format,$uuid,$name,$kind,$at,1)";
        command.Parameters.AddWithValue("$format", $"mailkeep-folder/{FormatVersion}");
        command.Parameters.AddWithValue("$uuid", uuid ?? Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$at", Now());
        command.ExecuteNonQuery();
        command.Parameters.Clear();
        command.CommandText = "INSERT INTO events(seq,ev,at,detail) VALUES(NULL,'created',$at,$name)";
        command.Parameters.AddWithValue("$at", Now());
        command.Parameters.AddWithValue("$name", name);
        command.ExecuteNonQuery();
        transaction.Commit();
        return new FolderBundle(connection, path);
    }

    /// <summary>Opens an existing folder file, checking it really is one and a version this build understands.</summary>
    public static FolderBundle Open(string path, bool readOnly = true)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Folder file does not exist.", path);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        connection.Open();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA application_id";
            if (Convert.ToInt32(command.ExecuteScalar()) != ApplicationId)
                throw new InvalidDataException($"{path} is not a Mailkeep folder file.");
            command.CommandText = "PRAGMA user_version";
            var version = Convert.ToInt32(command.ExecuteScalar());
            if (version is not (FormatVersion or BulkFormatVersion or LogicalFormatVersion))
                throw new InvalidDataException($"{path} is folder format {version}; this build reads format {FormatVersion}.");
            return new FolderBundle(connection, path);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public Meta Folder()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT format,uuid,name,kind,created_at,next_seq FROM folder";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException($"{Path} has no folder description row.");
        var meta = new Meta(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetInt64(5));
        if (reader.Read()) throw new InvalidDataException($"{Path} has more than one folder description row.");
        return meta;
    }

    /// <summary>Adds one message: bytes, state, header cache, search cache and the event, in one transaction.</summary>
    public long Add(byte[] raw, bool isRead, string origin, string? receivedAt = null, long? legacyId = null)
    {
        var parsed = Parse(raw);
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE folder SET next_seq=next_seq+1 RETURNING next_seq-1";
        var seq = (long)command.ExecuteScalar()!;
        var now = Now();
        command.CommandText = """
            INSERT INTO messages(seq,sha256,raw,size,received_at) VALUES($seq,$sha,$raw,$size,$received);
            INSERT INTO state(seq,is_read,origin,added_at,legacy_id) VALUES($seq,$read,$origin,$now,$legacy);
            INSERT INTO events(seq,ev,at,detail) VALUES($seq,'added',$now,$origin);
            """;
        command.Parameters.AddWithValue("$seq", seq);
        command.Parameters.AddWithValue("$sha", Sha256(raw));
        command.Parameters.AddWithValue("$raw", raw);
        command.Parameters.AddWithValue("$size", raw.LongLength);
        command.Parameters.AddWithValue("$received", (object?)receivedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$read", isRead);
        command.Parameters.AddWithValue("$origin", origin);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$legacy", (object?)legacyId ?? DBNull.Value);
        command.ExecuteNonQuery();
        WriteCaches(command, seq, parsed);
        transaction.Commit();
        return seq;
    }

    /// <summary>Appends a folder-level event (no message), e.g. where the folder was converted from.</summary>
    public void RecordEvent(string ev, string? detail)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO events(seq,ev,at,detail) VALUES(NULL,$ev,$at,$detail)";
        command.Parameters.AddWithValue("$ev", ev);
        command.Parameters.AddWithValue("$at", Now());
        command.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public string? LastEventDetail(string ev)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT detail FROM events WHERE ev=$ev ORDER BY id DESC LIMIT 1";
        command.Parameters.AddWithValue("$ev", ev);
        return command.ExecuteScalar() as string;
    }

    public IReadOnlyList<Summary> List(bool includeDeleted = false, int limit = int.MaxValue)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = SummarySelect + (includeDeleted ? "" : " WHERE s.deleted_at IS NULL")
            + " ORDER BY h.date_utc DESC, m.seq DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", Math.Max(limit, 1));
        using var reader = command.ExecuteReader();
        var results = new List<Summary>();
        while (reader.Read()) results.Add(ReadSummary(reader));
        return results;
    }

    /// <summary>Messages matching an FTS5 MATCH expression, newest first; deleted messages excluded.</summary>
    public IReadOnlyList<Summary> Search(string match, int limit)
        => Search(match, limit, null);

    public IReadOnlyList<Summary> Search(string match, int limit, string? sender)
    {
        SearchCatalog.ValidateSenderFilter(sender);
        using var command = _connection.CreateCommand();
        command.CommandText = SummarySelect + " WHERE s.deleted_at IS NULL"
            + (match.Length == 0 ? "" : " AND m.seq IN (SELECT rowid FROM fts WHERE fts MATCH $match)")
            // Portable archives stay read-only. Their separate, small header cache lets
            // From matching avoid opening or reparsing any MIME bodies.
            + (string.IsNullOrWhiteSpace(sender) ? "" : " AND (h.sender LIKE $sender ESCAPE '\\' OR h.sender_name LIKE $sender ESCAPE '\\')")
            + " ORDER BY h.date_utc DESC, m.seq DESC LIMIT $limit";
        if (match.Length != 0) command.Parameters.AddWithValue("$match", match);
        if (!string.IsNullOrWhiteSpace(sender))
            command.Parameters.AddWithValue("$sender", "%" + sender.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        command.Parameters.AddWithValue("$limit", Math.Max(limit, 1));
        using var reader = command.ExecuteReader();
        var results = new List<Summary>();
        while (reader.Read()) results.Add(ReadSummary(reader));
        return results;
    }

    public Message? Get(long seq)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = SummarySelect + " WHERE m.seq=$seq";
        command.Parameters.AddWithValue("$seq", seq);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var summary = ReadSummary(reader);
        return new Message(summary, Raw(seq)!);
    }

    public byte[]? Raw(long seq)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT raw FROM messages WHERE seq=$seq";
        command.Parameters.AddWithValue("$seq", seq);
        return command.ExecuteScalar() as byte[];
    }

    /// <summary>Checks the file without changing it: SQLite's own integrity check, every message's bytes against
    /// its recorded hash, and that each message has its state and header-cache rows.</summary>
    public VerifyResult Verify(CancellationToken cancellationToken = default)
    {
        var problems = new List<Problem>();
        var meta = Folder();
        using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        var integrity = string.Join("; ", ReadStrings(command));
        if (integrity != "ok") problems.Add(new(null, "integrity_check: " + integrity));
        if (meta.Format != $"mailkeep-folder/{FormatVersion}" && meta.Format != $"mailkeep-folder/{BulkFormatVersion}" && meta.Format != $"mailkeep-folder/{LogicalFormatVersion}") problems.Add(new(null, $"unexpected format '{meta.Format}'"));

        command.CommandText = "SELECT m.seq,m.sha256,m.raw,m.size,s.seq IS NOT NULL,h.seq IS NOT NULL FROM messages m "
            + "LEFT JOIN state s ON s.seq=m.seq LEFT JOIN headers h ON h.seq=m.seq ORDER BY m.seq";
        var messages = 0;
        var cacheRows = 0;
        var maxSeq = 0L;
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var seq = reader.GetInt64(0);
                maxSeq = Math.Max(maxSeq, seq);
                messages++;
                var raw = (byte[])reader.GetValue(2);
                if (Sha256(raw) != reader.GetString(1)) problems.Add(new(seq, "bytes do not match recorded sha256"));
                if (raw.LongLength != reader.GetInt64(3)) problems.Add(new(seq, "size does not match bytes"));
                if (!reader.GetBoolean(4)) problems.Add(new(seq, "no state row"));
                if (reader.GetBoolean(5)) cacheRows++;
                else problems.Add(new(seq, "no header cache row"));
            }
        if (meta.NextSeq <= maxSeq) problems.Add(new(null, $"next_seq {meta.NextSeq} would reuse an existing seq ({maxSeq})"));
        command.CommandText = "SELECT count(*) FROM state WHERE seq NOT IN (SELECT seq FROM messages)";
        var orphans = Convert.ToInt32(command.ExecuteScalar());
        if (orphans > 0) problems.Add(new(null, $"{orphans} state rows without a message"));
        command.CommandText = "SELECT count(*) FROM state WHERE deleted_at IS NOT NULL";
        var deleted = Convert.ToInt32(command.ExecuteScalar());
        return new VerifyResult(problems.Count == 0, Path, meta, messages, deleted, integrity, messages, cacheRows, problems.ToArray());
    }

    /// <summary>Throws away and rebuilds the header and search caches from the message bytes. The truth tables
    /// are not touched, which is the point: losing a cache never loses state.</summary>
    public int RebuildCaches()
    {
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM headers; DELETE FROM fts;";
        command.ExecuteNonQuery();
        var rows = new List<(long Seq, byte[] Raw)>();
        command.CommandText = "SELECT seq,raw FROM messages ORDER BY seq";
        using (var reader = command.ExecuteReader())
            while (reader.Read()) rows.Add((reader.GetInt64(0), (byte[])reader.GetValue(1)));
        foreach (var (seq, raw) in rows)
        {
            command.Parameters.Clear();
            WriteCaches(command, seq, Parse(raw));
        }
        transaction.Commit();
        return rows.Count;
    }

    public void Dispose() => _connection.Dispose();

    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private const string SummarySelect = """
        SELECT m.seq,m.sha256,m.size,s.is_read,s.deleted_at,h.subject,h.sender,h.sender_name,h.recipients,h.date_utc,
               h.message_id,h.attachment_count,h.snippet,s.origin,s.legacy_id
        FROM messages m JOIN state s ON s.seq=m.seq LEFT JOIN headers h ON h.seq=m.seq
        """;

    private static Summary ReadSummary(SqliteDataReader reader) => new(reader.GetInt64(0), reader.GetString(1),
        reader.GetInt64(2), reader.GetBoolean(3), reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? "" : reader.GetString(5), reader.IsDBNull(6) ? "" : reader.GetString(6),
        reader.IsDBNull(7) ? "" : reader.GetString(7), reader.IsDBNull(8) ? "" : reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.IsDBNull(11) ? 0 : reader.GetInt32(11), reader.IsDBNull(12) ? "" : reader.GetString(12),
        reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetInt64(14));

    private sealed record Parsed(string Subject, string Sender, string SenderName, string Recipients, string? DateUtc,
        string? MessageId, int AttachmentCount, string BodyText);

    /// <summary>Header-cache values for one message. Unparseable mail still gets a row, so it stays listable;
    /// its bytes are kept regardless.</summary>
    private static Parsed Parse(byte[] raw)
    {
        try
        {
            using var stream = new MemoryStream(raw, writable: false);
            var message = MimeMessage.Load(stream);
            var from = message.From.Mailboxes.FirstOrDefault();
            var recipients = string.Join(", ", message.To.Mailboxes.Concat(message.Cc.Mailboxes).Select(mailbox => mailbox.Address));
            var date = message.Date == DateTimeOffset.MinValue ? null : message.Date.UtcDateTime.ToString("O");
            var body = message.TextBody ?? MailText.FromHtml(message.HtmlBody);
            return new(message.Subject ?? "", from?.Address ?? "", from?.Name ?? "", recipients, date,
                message.MessageId, message.Attachments.Count(), body ?? "");
        }
        catch (FormatException)
        {
            return new("", "", "", "", null, null, 0, "");
        }
    }

    private static void WriteCaches(SqliteCommand command, long seq, Parsed parsed)
    {
        command.Parameters.Clear();
        command.CommandText = """
            INSERT INTO headers(seq,subject,sender,sender_name,recipients,date_utc,message_id,attachment_count,snippet)
            VALUES($seq,$subject,$sender,$sender_name,$recipients,$date,$message_id,$attachments,$snippet);
            INSERT INTO fts(rowid,subject,sender,recipients,body_text) VALUES($seq,$subject,$sender,$recipients,$body);
            """;
        command.Parameters.AddWithValue("$seq", seq);
        command.Parameters.AddWithValue("$subject", parsed.Subject);
        command.Parameters.AddWithValue("$sender", parsed.Sender);
        command.Parameters.AddWithValue("$sender_name", parsed.SenderName);
        command.Parameters.AddWithValue("$recipients", parsed.Recipients);
        command.Parameters.AddWithValue("$date", (object?)parsed.DateUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("$message_id", (object?)parsed.MessageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$attachments", parsed.AttachmentCount);
        command.Parameters.AddWithValue("$snippet", Snippet(parsed.BodyText));
        command.Parameters.AddWithValue("$body", parsed.BodyText);
        command.ExecuteNonQuery();
    }

    private static string Snippet(string body)
    {
        var flat = string.Join(' ', body.Split((char[])['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= 200 ? flat : flat[..200];
    }

    private static IEnumerable<string> ReadStrings(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values;
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
}
