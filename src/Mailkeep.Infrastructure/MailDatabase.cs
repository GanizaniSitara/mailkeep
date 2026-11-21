using System.Globalization;
using System.Text.Json;
using Mailkeep.Core;
using Microsoft.Data.Sqlite;
using MimeKit;

namespace Mailkeep.Infrastructure;

public sealed partial class MailDatabase
{
    private const string Schema = """
        PRAGMA foreign_keys = ON;
        CREATE TABLE IF NOT EXISTS messages (
            id               INTEGER PRIMARY KEY AUTOINCREMENT,
            source           TEXT NOT NULL,
            account          TEXT NOT NULL,
            folder           TEXT NOT NULL,
            source_key       TEXT NOT NULL,
            server_uid       INTEGER,
            uidvalidity      INTEGER NOT NULL DEFAULT 0,
            subject          TEXT NOT NULL DEFAULT '',
            sender           TEXT NOT NULL DEFAULT '',
            sender_name      TEXT NOT NULL DEFAULT '',
            recipients_json  TEXT NOT NULL DEFAULT '[]',
            date_utc         TEXT,
            size             INTEGER NOT NULL DEFAULT 0,
            is_read          INTEGER NOT NULL DEFAULT 0,
            attachment_count INTEGER NOT NULL DEFAULT 0,
            body_text        TEXT NOT NULL DEFAULT '',
            body_html        TEXT,
            raw_path         TEXT,
            UNIQUE(source, account, folder, source_key)
        );
        CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(
            subject, sender, recipients, body_text,
            tokenize='unicode61 remove_diacritics 2'
        );
        CREATE TABLE IF NOT EXISTS sync_state (
            account       TEXT NOT NULL,
            folder        TEXT NOT NULL,
            uidvalidity   INTEGER NOT NULL DEFAULT 0,
            max_uid       INTEGER NOT NULL DEFAULT 0,
            last_fetch_at TEXT,
            seen_max_id   INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(account, folder)
        );
        CREATE TABLE IF NOT EXISTS fetch_log (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            account     TEXT NOT NULL,
            address     TEXT NOT NULL DEFAULT '',
            source      TEXT NOT NULL DEFAULT '',
            started_at  TEXT NOT NULL,
            finished_at TEXT,
            duration_ms INTEGER NOT NULL DEFAULT 0,
            new_count   INTEGER NOT NULL DEFAULT 0,
            scanned     INTEGER NOT NULL DEFAULT 0,
            max_uid     INTEGER NOT NULL DEFAULT 0,
            ok          INTEGER NOT NULL DEFAULT 1,
            error       TEXT
        );
        CREATE TABLE IF NOT EXISTS settings (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        -- Coalesce changes by message, but retain a monotonically increasing revision so
        -- concurrent catalog readers can acknowledge their own snapshot without losing writes.
        CREATE TABLE IF NOT EXISTS search_operational_changes (
            message_id INTEGER PRIMARY KEY,
            revision INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_search_operational_revision ON search_operational_changes(revision);
        CREATE TABLE IF NOT EXISTS search_archive_changes (
            message_id INTEGER PRIMARY KEY,
            revision INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_search_archive_revision ON search_archive_changes(revision);
        INSERT OR IGNORE INTO settings VALUES('search.operational.epoch',lower(hex(randomblob(16))));
        INSERT OR IGNORE INTO settings VALUES('search.operational.change-sequence','0');
        CREATE TRIGGER IF NOT EXISTS messages_catalog_insert AFTER INSERT ON messages WHEN NEW.source='live'
        BEGIN
            UPDATE settings SET value=CAST(value AS INTEGER)+1 WHERE key='search.operational.change-sequence';
            INSERT INTO search_operational_changes SELECT NEW.id,CAST(value AS INTEGER) FROM settings WHERE key='search.operational.change-sequence'
            ON CONFLICT(message_id) DO UPDATE SET revision=excluded.revision;
        END;
        CREATE TRIGGER IF NOT EXISTS messages_catalog_update AFTER UPDATE ON messages WHEN OLD.source='live' OR NEW.source='live'
        BEGIN
            UPDATE settings SET value=CAST(value AS INTEGER)+1 WHERE key='search.operational.change-sequence';
            INSERT INTO search_operational_changes SELECT OLD.id,CAST(value AS INTEGER) FROM settings WHERE key='search.operational.change-sequence'
            ON CONFLICT(message_id) DO UPDATE SET revision=excluded.revision;
            INSERT INTO search_operational_changes SELECT NEW.id,CAST(value AS INTEGER) FROM settings WHERE key='search.operational.change-sequence'
            ON CONFLICT(message_id) DO UPDATE SET revision=excluded.revision;
        END;
        CREATE TRIGGER IF NOT EXISTS messages_catalog_delete AFTER DELETE ON messages WHEN OLD.source='live'
        BEGIN
            UPDATE settings SET value=CAST(value AS INTEGER)+1 WHERE key='search.operational.change-sequence';
            INSERT INTO search_operational_changes SELECT OLD.id,CAST(value AS INTEGER) FROM settings WHERE key='search.operational.change-sequence'
            ON CONFLICT(message_id) DO UPDATE SET revision=excluded.revision;
        END;
        CREATE TRIGGER IF NOT EXISTS messages_search_update AFTER UPDATE ON messages
        WHEN (OLD.source='live' OR NEW.source='live') AND (
            OLD.source IS NOT NEW.source OR OLD.account IS NOT NEW.account OR OLD.folder IS NOT NEW.folder
            OR OLD.subject IS NOT NEW.subject OR OLD.sender IS NOT NEW.sender OR OLD.sender_name IS NOT NEW.sender_name
            OR OLD.recipients_json IS NOT NEW.recipients_json OR OLD.date_utc IS NOT NEW.date_utc
            OR OLD.size IS NOT NEW.size OR OLD.is_read IS NOT NEW.is_read
            OR OLD.attachment_count IS NOT NEW.attachment_count OR OLD.body_text IS NOT NEW.body_text
            OR OLD.raw_path IS NOT NEW.raw_path OR OLD.source_key IS NOT NEW.source_key)
        BEGIN
            INSERT INTO settings(key,value) VALUES('search.operational.revision','1')
            ON CONFLICT(key) DO UPDATE SET value=CAST(value AS INTEGER)+1;
        END;
        CREATE TABLE IF NOT EXISTS local_deletions (
            source      TEXT NOT NULL,
            account     TEXT NOT NULL,
            folder      TEXT NOT NULL,
            source_key  TEXT NOT NULL,
            deleted_at  TEXT NOT NULL,
            PRIMARY KEY(source, account, folder, source_key)
        );
        CREATE TABLE IF NOT EXISTS pop_seen (
            account       TEXT NOT NULL,
            uidl          TEXT NOT NULL,
            first_seen_at TEXT NOT NULL,
            PRIMARY KEY(account, uidl)
        );
        CREATE TABLE IF NOT EXISTS local_folders (
            folder_key  TEXT PRIMARY KEY,
            parent_key  TEXT,
            name        TEXT NOT NULL,
            kind        TEXT NOT NULL,
            sort_order  INTEGER NOT NULL,
            origin      TEXT NOT NULL DEFAULT 'native',
            origin_key  TEXT,
            FOREIGN KEY(parent_key) REFERENCES local_folders(folder_key)
        );
        -- An imported folder is normally just the path its messages carry, which leaves no way to have an
        -- empty one. This records the folders that exist regardless of what is in them, so a subfolder can be
        -- made before there is anything to put in it.
        CREATE TABLE IF NOT EXISTS archive_folders (
            source  TEXT NOT NULL,
            account TEXT NOT NULL,
            folder  TEXT NOT NULL,
            PRIMARY KEY(source, account, folder)
        );
        CREATE TABLE IF NOT EXISTS outgoing_messages (
            id              TEXT PRIMARY KEY,
            account         TEXT NOT NULL DEFAULT '', -- legacy storage column; sender is sending_identity_id
            sending_identity_id TEXT,
            to_json         TEXT NOT NULL DEFAULT '[]',
            cc_json         TEXT NOT NULL DEFAULT '[]',
            bcc_json        TEXT NOT NULL DEFAULT '[]',
            subject         TEXT NOT NULL DEFAULT '',
            body_text       TEXT NOT NULL DEFAULT '',
            body_html       TEXT,
            status          TEXT NOT NULL DEFAULT 'draft',
            raw_path        TEXT,
            attempt_count   INTEGER NOT NULL DEFAULT 0,
            created_at      TEXT NOT NULL,
            updated_at      TEXT NOT NULL,
            next_attempt_at TEXT,
            sent_at         TEXT,
            last_error      TEXT
        );
        CREATE TABLE IF NOT EXISTS draft_folders (
            draft_id TEXT PRIMARY KEY,
            folder_key TEXT NOT NULL,
            FOREIGN KEY(draft_id) REFERENCES outgoing_messages(id) ON DELETE CASCADE,
            FOREIGN KEY(folder_key) REFERENCES local_folders(folder_key)
        );
        -- The address book: one row per address, so a picker never offers the same person twice.
        CREATE TABLE IF NOT EXISTS contacts (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            name        TEXT NOT NULL DEFAULT '',
            email       TEXT NOT NULL COLLATE NOCASE UNIQUE,
            notes       TEXT NOT NULL DEFAULT '',
            origin      TEXT NOT NULL DEFAULT 'native',
            origin_key  TEXT,
            created_at  TEXT NOT NULL,
            updated_at  TEXT NOT NULL
        );
        -- Which message a reply or forward was written from. Kept beside the outgoing message rather than in it,
        -- and turned into a mark on that message only once the reply or forward has actually been delivered.
        CREATE TABLE IF NOT EXISTS outgoing_origins (
            outgoing_id TEXT PRIMARY KEY,
            message_id  INTEGER NOT NULL,
            kind        TEXT NOT NULL
        );
        -- Addresses mail has been sent to, offered as the address lines are typed in as OE's autocomplete cache
        -- does. Removed rows are kept, so a seed cannot bring them back; sending to the address again does.
        CREATE TABLE IF NOT EXISTS address_history (
            email      TEXT PRIMARY KEY COLLATE NOCASE,
            uses       INTEGER NOT NULL DEFAULT 0,
            last_used  TEXT NOT NULL,
            removed    INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS message_marks (
            message_id  INTEGER NOT NULL,
            kind        TEXT NOT NULL,
            marked_at   TEXT NOT NULL,
            PRIMARY KEY(message_id, kind)
        );
        CREATE INDEX IF NOT EXISTS idx_messages_account_date ON messages(account, date_utc DESC);
        CREATE INDEX IF NOT EXISTS idx_messages_raw_path ON messages(raw_path);
        CREATE INDEX IF NOT EXISTS idx_messages_source_folder ON messages(source, folder);
        -- Covers the per-folder counts behind the folder tree. Every column the query needs is in the
        -- index, so counting a quarter of a million archived messages never touches the table itself.
        CREATE INDEX IF NOT EXISTS idx_messages_source_account_folder
            ON messages(source, account, folder, is_read);
        -- Opening a folder: the newest first, and only that folder's. Without the date on the end SQLite
        -- reached instead for the index on (account, date) and walked the whole account in date order
        -- discarding everything not in the folder — so a folder holding two old messages, in an account
        -- holding two hundred thousand, cost as much as opening the account itself.
        CREATE INDEX IF NOT EXISTS idx_messages_source_account_folder_date
            ON messages(source, account, folder, date_utc DESC);
        -- Just the messages carrying attachments, so finding them is a lookup rather than a read of every row.
        CREATE INDEX IF NOT EXISTS idx_messages_with_attachments ON messages(source, folder, date_utc) WHERE attachment_count > 0;
        CREATE INDEX IF NOT EXISTS idx_fetch_log_started ON fetch_log(started_at DESC);
        CREATE INDEX IF NOT EXISTS idx_outgoing_status_due ON outgoing_messages(status,next_attempt_at,created_at);
        CREATE TABLE IF NOT EXISTS blob_gc_candidates(raw_path TEXT PRIMARY KEY,last_checked INTEGER NOT NULL DEFAULT 0);
        CREATE TRIGGER IF NOT EXISTS blob_gc_message_delete AFTER DELETE ON messages WHEN OLD.raw_path IS NOT NULL
        BEGIN INSERT OR IGNORE INTO blob_gc_candidates(raw_path) VALUES(OLD.raw_path); END;
        CREATE TRIGGER IF NOT EXISTS blob_gc_message_replace AFTER UPDATE OF raw_path ON messages
        WHEN OLD.raw_path IS NOT NULL AND OLD.raw_path IS NOT NEW.raw_path
        BEGIN INSERT OR IGNORE INTO blob_gc_candidates(raw_path) VALUES(OLD.raw_path); END;
        CREATE TRIGGER IF NOT EXISTS blob_gc_outgoing_delete AFTER DELETE ON outgoing_messages WHEN OLD.raw_path IS NOT NULL
        BEGIN INSERT OR IGNORE INTO blob_gc_candidates(raw_path) VALUES(OLD.raw_path); END;
        CREATE TRIGGER IF NOT EXISTS blob_gc_outgoing_replace AFTER UPDATE OF raw_path ON outgoing_messages
        WHEN OLD.raw_path IS NOT NULL AND OLD.raw_path IS NOT NEW.raw_path
        BEGIN INSERT OR IGNORE INTO blob_gc_candidates(raw_path) VALUES(OLD.raw_path); END;
        """;

    private readonly AppPaths _paths;
    private readonly RawBlobStore _blobs;

    public MailDatabase(AppPaths paths, RawBlobStore blobs)
    {
        _paths = paths;
        _blobs = blobs;
        FolderStorage = new WritableFolderStorage(paths, blobs);
    }

    public WritableFolderStorage FolderStorage { get; }

    public void Initialize()
    {
        _paths.EnsureDirectories();
        using var connection = Open(flushFolders: false);
        using var command = connection.CreateCommand();
        command.CommandText = Schema;
        command.ExecuteNonQuery();
        EnsureRemoteQueue(connection);
        EnsureOutgoingIdentityColumn(connection);
        EnsureOutgoingHtmlColumn(connection);
        // Native drafts keep their editable body, recipients and queue state in outgoing_messages.
        // Filing changes only their folder link; the portable received-mail writer does not own them.
        // The old conversion guard blocked this supported operation even for already-filed drafts.
        using (var draftFiling = connection.CreateCommand())
        {
            draftFiling.CommandText = "DROP TRIGGER IF EXISTS bulk_logical_draft_guard";
            draftFiling.ExecuteNonQuery();
        }
        OeDraftConversion.EnsureSchema(connection);
        AdoptionCleanup.EnsureSchema(connection);
        EnsureStandardLocalFolders(connection);
        RenameLegacyArchiveSource(connection);
        FolderStorage.Initialize();
    }

    /// <summary>Imported mail used to be labelled with the client it came from. Stores written then are
    /// relabelled here, once, so no one has to remember which name a given database uses.</summary>
    private static void RenameLegacyArchiveSource(SqliteConnection connection)
    {
        foreach (var table in new[] { "messages", "local_deletions" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE {table} SET source=$archive WHERE source=$legacy";
            command.Parameters.AddWithValue("$archive", MailContract.ArchiveSource);
            command.Parameters.AddWithValue("$legacy", MailContract.LegacyArchiveSource);
            command.ExecuteNonQuery();
        }
    }

    private static void EnsureStandardLocalFolders(SqliteConnection connection)
    {
        var folders = new[]
        {
            new LocalFolder(LocalFolderKinds.Inbox, null, "Inbox", LocalFolderKinds.Inbox, 10),
            new LocalFolder(LocalFolderKinds.Drafts, null, "Drafts", LocalFolderKinds.Drafts, 20),
            new LocalFolder(LocalFolderKinds.Outbox, null, "Outbox", LocalFolderKinds.Outbox, 30),
            new LocalFolder(LocalFolderKinds.Sent, null, "Sent", LocalFolderKinds.Sent, 40),
            new LocalFolder(LocalFolderKinds.Deleted, null, "Deleted Items", LocalFolderKinds.Deleted, 50)
        };
        foreach (var folder in folders)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO local_folders(folder_key,parent_key,name,kind,sort_order,origin,origin_key)
                VALUES($key,$parent,$name,$kind,$order,$origin,$origin_key)
                """;
            command.Parameters.AddWithValue("$key", folder.Key);
            command.Parameters.AddWithValue("$parent", (object?)folder.ParentKey ?? DBNull.Value);
            command.Parameters.AddWithValue("$name", folder.Name);
            command.Parameters.AddWithValue("$kind", folder.Kind);
            command.Parameters.AddWithValue("$order", folder.SortOrder);
            command.Parameters.AddWithValue("$origin", folder.Origin);
            command.Parameters.AddWithValue("$origin_key", (object?)folder.OriginKey ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Stores that predate formatted outgoing mail gain the column for it.</summary>
    private static void EnsureOutgoingHtmlColumn(SqliteConnection connection)
    {
        using var columns = connection.CreateCommand();
        columns.CommandText = "PRAGMA table_info(outgoing_messages)";
        using var reader = columns.ExecuteReader();
        var exists = false;
        while (reader.Read()) exists |= reader.GetString(1).Equals("body_html", StringComparison.OrdinalIgnoreCase);
        if (exists) return;
        using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE outgoing_messages ADD COLUMN body_html TEXT";
        alter.ExecuteNonQuery();
    }

    private static void EnsureOutgoingIdentityColumn(SqliteConnection connection)
    {
        using var columns = connection.CreateCommand();
        columns.CommandText = "PRAGMA table_info(outgoing_messages)";
        using var reader = columns.ExecuteReader();
        var exists = false;
        while (reader.Read()) exists |= reader.GetString(1).Equals("sending_identity_id", StringComparison.OrdinalIgnoreCase);
        if (exists) return;
        using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE outgoing_messages ADD COLUMN sending_identity_id TEXT";
        alter.ExecuteNonQuery();
        using var migrate = connection.CreateCommand();
        migrate.CommandText = "UPDATE outgoing_messages SET sending_identity_id=account WHERE sending_identity_id IS NULL";
        migrate.ExecuteNonQuery();
    }

    public Task<long> UpsertMimeMessageAsync(
        string source,
        string account,
        string folder,
        string sourceKey,
        long? serverUid,
        uint uidValidity,
        MimeMessage message,
        Stream raw,
        bool isRead,
        CancellationToken cancellationToken = default)
        => UpsertMimeMessageAsync(source, account, folder, sourceKey, serverUid, uidValidity, message, raw, isRead,
            cancellationToken, reconcileLocalImap: false);

    internal async Task<long> UpsertMimeMessageAsync(
        string source,
        string account,
        string folder,
        string sourceKey,
        long? serverUid,
        uint uidValidity,
        MimeMessage message,
        Stream raw,
        bool isRead,
        CancellationToken cancellationToken,
        bool reconcileLocalImap, string? checkpointMailbox = null)
    {
        if (IsLocallyDeleted(source, account, folder, sourceKey))
        {
            if (checkpointMailbox is not null && serverUid is { } skippedUid)
                SetSyncState(account, checkpointMailbox, uidValidity, checked((uint)skippedUid), DateTimeOffset.UtcNow);
            return 0;
        }
        using var copy = new MemoryStream();
        await raw.CopyToAsync(copy, cancellationToken);
        var bytes = copy.ToArray();
        return _blobs.WithReferences(() =>
        {
        var rawPath = _blobs.Put(bytes);
        var recipients = message.To.Mailboxes.Concat(message.Cc.Mailboxes)
            .Select(a => a.Address).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct().ToArray();
        var sender = message.From.Mailboxes.FirstOrDefault();
        var input = new MessageInput(
            source, account, folder, sourceKey, serverUid, uidValidity,
            message.Subject ?? "", sender?.Address ?? "", sender?.Name ?? "", recipients,
            message.Date == DateTimeOffset.MinValue ? null : message.Date.ToUniversalTime(),
            bytes.LongLength, isRead, message.Attachments.Count(),
            Truncate(string.IsNullOrWhiteSpace(message.TextBody) ? MailText.FromHtml(message.HtmlBody) : message.TextBody, 1_000_000),
            TruncateNullable(message.HtmlBody, 2_000_000), rawPath);
        var result = Upsert(input, reconcileLocalImap, checkpointMailbox);
        if (result == 0) QueueBlobCleanup(rawPath);
        return result;
        });
    }

    public long Upsert(MessageInput input) => Upsert(input, reconcileLocalImap: false);

    private long Upsert(MessageInput input, bool reconcileLocalImap, string? checkpointMailbox = null)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        void Checkpoint()
        {
            if (checkpointMailbox is not null && input.ServerUid is { } uid)
                WriteSyncState(connection, transaction, input.Account, checkpointMailbox, input.UidValidity, checked((uint)uid), DateTimeOffset.UtcNow);
        }
        using (var deleted = connection.CreateCommand())
        {
            deleted.Transaction = transaction;
            deleted.CommandText = """
                SELECT 1 FROM local_deletions WHERE source=$source AND account=$account AND folder=$folder AND source_key=$source_key
                UNION ALL SELECT 1 FROM oe_draft_conversions WHERE source=$source AND account=$account AND folder=$folder AND source_key=$source_key
                UNION ALL SELECT 1 FROM adoption_cleanup WHERE source=$source AND account=$account AND folder=$folder AND source_key=$source_key
                """;
            deleted.Parameters.AddWithValue("$source", input.Source);
            deleted.Parameters.AddWithValue("$account", input.Account);
            deleted.Parameters.AddWithValue("$folder", input.Folder);
            deleted.Parameters.AddWithValue("$source_key", input.SourceKey);
            if (deleted.ExecuteScalar() is not null) { Checkpoint(); transaction.Commit(); return 0; }
        }
        if (reconcileLocalImap)
        {
            // The SHA-256 raw_path covers every MIME byte, including the Message-ID header
            // (or its absence). This works for rows collected before this version too.
            using var match = connection.CreateCommand();
            match.Transaction = transaction;
            match.CommandText = """
                SELECT id,server_uid,uidvalidity FROM messages INDEXED BY idx_messages_raw_path
                WHERE source=$source AND account=$account AND folder=$folder AND raw_path=$raw_path
                ORDER BY (source_key=$source_key) DESC,(uidvalidity<>0) DESC,(server_uid IS NULL) DESC,id LIMIT 1
                """;
            AddMessageParameters(match, input);
            long? detached = null;
            var unchanged = false;
            using (var reader = match.ExecuteReader())
            {
                if (reader.Read())
                {
                    // Preserve the provenance of a local copy, and leave a current attachment in place.
                    if (reader.GetInt64(2) == 0 || !reader.IsDBNull(1)) unchanged = true;
                    else detached = reader.GetInt64(0);
                }
            }
            if (unchanged) { Checkpoint(); transaction.Commit(); return 0; }
            if (detached.HasValue)
            {
                match.CommandText = "UPDATE messages SET source_key=$source_key,server_uid=$server_uid,uidvalidity=$uidvalidity WHERE id=$id";
                match.Parameters.AddWithValue("$id", detached.Value);
                match.ExecuteNonQuery();
                Checkpoint();
                transaction.Commit();
                FolderStorage.Flush();
                return 0;
            }
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO messages (
                source, account, folder, source_key, server_uid, uidvalidity,
                subject, sender, sender_name, recipients_json, date_utc, size,
                is_read, attachment_count, body_text, body_html, raw_path)
            VALUES ($source, $account, $folder, $source_key, $server_uid, $uidvalidity,
                $subject, $sender, $sender_name, $recipients, $date, $size,
                $is_read, $attachments, $body_text, $body_html, $raw_path)
            ON CONFLICT(source, account, folder, source_key) DO UPDATE SET
                server_uid=excluded.server_uid, uidvalidity=excluded.uidvalidity,
                subject=excluded.subject, sender=excluded.sender, sender_name=excluded.sender_name,
                recipients_json=excluded.recipients_json, date_utc=excluded.date_utc,
                size=excluded.size, is_read=COALESCE((SELECT is_read FROM remote_operations
                    WHERE account=excluded.account AND folder=excluded.folder AND source_key=excluded.source_key
                    AND kind='read' AND completed_at IS NULL ORDER BY id DESC LIMIT 1), excluded.is_read),
                attachment_count=excluded.attachment_count, body_text=excluded.body_text,
                body_html=excluded.body_html, raw_path=COALESCE(excluded.raw_path, messages.raw_path)
            RETURNING id;
            """;
        AddMessageParameters(command, input);
        var id = (long)(command.ExecuteScalar() ?? throw new InvalidOperationException("Message upsert returned no id."));

        using var deleteFts = connection.CreateCommand();
        deleteFts.Transaction = transaction;
        deleteFts.CommandText = "DELETE FROM messages_fts WHERE rowid = $id";
        deleteFts.Parameters.AddWithValue("$id", id);
        deleteFts.ExecuteNonQuery();

        using var insertFts = connection.CreateCommand();
        insertFts.Transaction = transaction;
        insertFts.CommandText = """
            INSERT INTO messages_fts(rowid, subject, sender, recipients, body_text)
            VALUES ($id, $subject, $sender, $recipients, $body_text)
            """;
        insertFts.Parameters.AddWithValue("$id", id);
        insertFts.Parameters.AddWithValue("$subject", input.Subject);
        insertFts.Parameters.AddWithValue("$sender", input.Sender);
        insertFts.Parameters.AddWithValue("$recipients", string.Join(' ', input.Recipients));
        insertFts.Parameters.AddWithValue("$body_text", input.BodyText);
        insertFts.ExecuteNonQuery();
        Checkpoint();
        transaction.Commit();
        FolderStorage.Flush();
        return id;
    }

    public IReadOnlyList<MessageSummary> ListMessages(string? account = null, string? source = null, int limit = 500)
    {
        using var connection = OpenProjection();
        using var command = connection.CreateCommand();
        var where = new List<string> { "converted_draft_id IS NULL" };
        if (!string.IsNullOrWhiteSpace(account))
        {
            where.Add("account = $account");
            command.Parameters.AddWithValue("$account", account);
        }
        if (!string.IsNullOrWhiteSpace(source) && source != "all")
        {
            where.Add("source = $source");
            command.Parameters.AddWithValue("$source", source);
        }
        command.CommandText = "SELECT " + MessageColumns + " FROM messages"
            + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
            + " ORDER BY date_utc DESC, id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", Math.Max(limit, 1));
        using var reader = command.ExecuteReader();
        var results = new List<MessageSummary>();
        while (reader.Read())
        {
            results.Add(ReadSummary(reader));
        }
        return results;
    }

    public IReadOnlyList<MessageSummary> ListMessagesForAccounts(IReadOnlyCollection<string> accounts,
        string? folder = null, int limit = 500, string source = MailContract.LiveSource)
    {
        if (accounts.Count == 0) return [];
        using var connection = OpenProjection();
        using var command = connection.CreateCommand();
        var accountParameters = accounts.Select((account, index) =>
        {
            var name = "$account" + index;
            command.Parameters.AddWithValue(name, account);
            return name;
        }).ToArray();
        var where = new List<string>
        {
            "converted_draft_id IS NULL",
            "source = $source",
            "account IN (" + string.Join(',', accountParameters) + ")"
        };
        command.Parameters.AddWithValue("$source", source);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            where.Add("folder = $folder");
            command.Parameters.AddWithValue("$folder", folder);
        }
        command.CommandText = "SELECT " + MessageColumns + " FROM messages WHERE " + string.Join(" AND ", where)
            + " ORDER BY date_utc DESC, id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", Math.Max(limit, 1));
        using var reader = command.ExecuteReader();
        var results = new List<MessageSummary>();
        while (reader.Read()) results.Add(ReadSummary(reader));
        return results;
    }

    public IReadOnlyList<LocalFolder> ListLocalFolders()
    {
        using var connection = OpenProjection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT folder_key,parent_key,name,kind,sort_order,origin,origin_key FROM local_folders ORDER BY sort_order,name";
        using var reader = command.ExecuteReader();
        var folders = new List<LocalFolder>();
        while (reader.Read()) folders.Add(new LocalFolder(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        return folders;
    }

    public IReadOnlyDictionary<string, (int Total, int Unread)> LocalFolderCounts(IReadOnlyCollection<string> localAccounts)
    {
        if (localAccounts.Count == 0) return new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        using var connection = OpenProjection();
        using var command = connection.CreateCommand();
        var accountParameters = localAccounts.Select((account, index) =>
        {
            var name = "$account" + index;
            command.Parameters.AddWithValue(name, account);
            return name;
        }).ToArray();
        command.CommandText = "SELECT folder,COUNT(*),SUM(CASE WHEN is_read=0 THEN 1 ELSE 0 END) FROM messages "
            + "WHERE source=$source AND account IN (" + string.Join(',', accountParameters) + ") GROUP BY folder";
        command.Parameters.AddWithValue("$source", MailContract.LiveSource);
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) result[reader.GetString(0)] = (reader.GetInt32(1), reader.GetInt32(2));
        return result;
    }

    public IReadOnlyDictionary<(string Account, string Folder), (int Total, int Unread)> VisibleArchiveFolderCounts(
        IReadOnlyCollection<string> localAccounts)
    {
        var scoped = localAccounts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var owned = ListHeads(MailContract.LiveSource).Where(head => scoped.Contains(head.Account))
            .Select(head => (head.Subject, head.Sender, head.DateUtc)).ToHashSet();
        var removed = LocalDeletionKeys(MailContract.LiveSource, MailContract.LocalCopyAccount);
        using var connection = OpenProjection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,account,folder,subject,sender,date_utc,is_read FROM messages WHERE source=$archive AND converted_draft_id IS NULL";
        command.Parameters.AddWithValue("$archive", MailContract.ArchiveSource);
        using var reader = command.ExecuteReader();
        var counts = new Dictionary<(string, string), (int Total, int Unread)>();
        while (reader.Read())
        {
            if (owned.Contains((reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? "" : reader.GetString(5)))
                || removed.Contains(MailContract.AdoptedCopyKeyPrefix + reader.GetInt64(0))) continue;
            var key = (reader.GetString(1), reader.GetString(2));
            var count = counts.GetValueOrDefault(key);
            counts[key] = (count.Total + 1, count.Unread + (reader.GetInt32(6) == 0 ? 1 : 0));
        }
        return counts;
    }

    /// <summary>The unread half of VisibleArchiveFolderCounts, which is all the folder tree draws, without reading
    /// the archive row by row to get it. The raw unread counts come from the covering index; then only the imported
    /// rows that stand down are found and taken off: those the mailbox holds its own copy of (looked up from that
    /// copy's date, which the index on account and date answers) and those whose adopted copy was deleted. The full
    /// pass this replaces ran on every delete and every message read and held the window for over a second.</summary>
    public IReadOnlyDictionary<(string Account, string Folder), int> ArchiveUnreadCounts(IReadOnlyCollection<string> localAccounts)
    {
        var counts = new Dictionary<(string, string), int>();
        using var connection = OpenProjection();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT account,folder,COUNT(*) FROM messages WHERE source=$archive AND is_read=0 AND converted_draft_id IS NULL GROUP BY account,folder";
            command.Parameters.AddWithValue("$archive", MailContract.ArchiveSource);
            using var reader = command.ExecuteReader();
            while (reader.Read()) counts[(reader.GetString(0), reader.GetString(1))] = reader.GetInt32(2);
        }
        if (counts.Count == 0) return counts;
        var hidden = new Dictionary<long, (string Account, string Folder)>();
        var archiveAccounts = counts.Keys.Select(key => key.Item1).Distinct(StringComparer.Ordinal).ToArray();
        if (localAccounts.Count > 0)
        {
            // The mailbox's own heads first — a few thousand at most — then, for each, a seek on (account, date)
            // into each imported account. Left to plan the join itself, SQLite walked the archive instead.
            var owned = new HashSet<(string Subject, string Sender, string? Date)>();
            using (var command = connection.CreateCommand())
            {
                var owners = localAccounts.Select((account, index) =>
                {
                    var name = "$owner" + index;
                    command.Parameters.AddWithValue(name, account);
                    return name;
                }).ToArray();
                command.CommandText = "SELECT subject,sender,date_utc FROM messages WHERE source=$live AND account COLLATE NOCASE IN ("
                    + string.Join(',', owners) + ")";
                command.Parameters.AddWithValue("$live", MailContract.LiveSource);
                using var reader = command.ExecuteReader();
                while (reader.Read()) owned.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
            using var lookup = connection.CreateCommand();
            lookup.CommandText = """
                SELECT id,folder,subject,sender FROM messages INDEXED BY idx_messages_account_date
                WHERE account=$account AND date_utc IS $date AND source=$archive AND is_read=0
                """;
            var account = lookup.Parameters.Add("$account", SqliteType.Text);
            var date = lookup.Parameters.Add("$date", SqliteType.Text);
            lookup.Parameters.AddWithValue("$archive", MailContract.ArchiveSource);
            foreach (var group in owned.GroupBy(head => head.Date))
                foreach (var archiveAccount in archiveAccounts)
                {
                    account.Value = archiveAccount;
                    date.Value = (object?)group.Key ?? DBNull.Value;
                    using var reader = lookup.ExecuteReader();
                    while (reader.Read())
                        if (group.Contains((reader.GetString(2), reader.GetString(3), group.Key)))
                            hidden[reader.GetInt64(0)] = (archiveAccount, reader.GetString(1));
                }
        }
        var adopted = LocalDeletionKeys(MailContract.LiveSource, MailContract.LocalCopyAccount)
            .Where(key => key.StartsWith(MailContract.AdoptedCopyKeyPrefix, StringComparison.Ordinal))
            .Select(key => long.TryParse(key[MailContract.AdoptedCopyKeyPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : -1)
            .Where(id => id > 0).ToArray();
        if (adopted.Length > 0)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id,account,folder FROM messages WHERE id IN (SELECT value FROM json_each($ids)) AND source=$archive AND is_read=0";
            command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(adopted));
            command.Parameters.AddWithValue("$archive", MailContract.ArchiveSource);
            using var reader = command.ExecuteReader();
            while (reader.Read()) hidden[reader.GetInt64(0)] = (reader.GetString(1), reader.GetString(2));
        }
        foreach (var key in hidden.Values)
            if (counts.TryGetValue(key, out var count)) counts[key] = count - 1;
        return counts;
    }

    public SearchEnvelope Search(string query, string source = "all", int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new SearchEnvelope(query, 0, false, []);
        }

        var match = ToFtsQuery(query);
        using var connection = OpenProjection();
        var sourceClause = " AND m.converted_draft_id IS NULL" + (source == "all" ? "" : " AND m.source = $source");
        using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM messages_fts f JOIN messages m ON m.id=f.rowid "
            + "WHERE messages_fts MATCH $query" + sourceClause;
        countCommand.Parameters.AddWithValue("$query", match);
        if (source != "all") countCommand.Parameters.AddWithValue("$source", source);
        var count = Convert.ToInt32(countCommand.ExecuteScalar(), CultureInfo.InvariantCulture);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + QualifiedMessageColumns
            + ", snippet(messages_fts, 3, '', '', '…', 18) AS fts_snippet "
            + "FROM messages_fts f JOIN messages m ON m.id=f.rowid "
            + "WHERE messages_fts MATCH $query" + sourceClause
            + " ORDER BY rank, m.date_utc DESC LIMIT $limit";
        command.Parameters.AddWithValue("$query", match);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        if (source != "all") command.Parameters.AddWithValue("$source", source);
        using var reader = command.ExecuteReader();
        var results = new List<MessageSummary>();
        while (reader.Read())
        {
            results.Add(ReadSummary(reader, reader.GetString(reader.GetOrdinal("fts_snippet"))));
        }
        return new SearchEnvelope(query, count, count > results.Count, results);
    }

    public MessageContent? GetMessage(long id) => GetMessage(id, null);

    public MessageContent? GetMessage(long id, Action<string, double>? measure)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try { return ReadMessage(id, measure); }
        finally { measure?.Invoke("total", clock.Elapsed.TotalMilliseconds); }
    }

    private MessageContent? ReadMessage(long id, Action<string, double>? measure)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Mark(string phase)
        {
            measure?.Invoke(phase, clock.Elapsed.TotalMilliseconds);
            clock.Restart();
        }
        using var connection = OpenProjection();
        Mark("projection_open");
        id = AdoptionCleanup.ResolveId(connection, id);
        Mark("alias_lookup");
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + MessageColumns + ", body_text, body_html, raw_path FROM messages WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var storedText = reader.GetString(reader.GetOrdinal("body_text"));
        var bodyHtmlOrdinal = reader.GetOrdinal("body_html");
        var rawOrdinal = reader.GetOrdinal("raw_path");
        var bodyHtml = reader.IsDBNull(bodyHtmlOrdinal) ? null : reader.GetString(bodyHtmlOrdinal);
        var rawRelative = reader.IsDBNull(rawOrdinal) ? null : reader.GetString(rawOrdinal);
        Mark("row_read");
        // The message itself is opened here anyway for its attachments, so the letter is read from it rather
        // than from what was made of it on the way in. Mail already in the store was written down with every
        // tag swapped for a space, which for anything a designer laid out means its stylesheet where its
        // words should be; the message is still whole on disk, so reading it again beats re-importing a
        // quarter of a million of them.
        (IReadOnlyList<AttachmentInfo> attachments, string? rawText, string? rawHtml, IReadOnlyList<string> replyTo)
            = ([], null, null, []);
        if (rawRelative is not null)
        {
            try
            {
                using var stream = _blobs.OpenRead(rawRelative);
                Mark("mime_read");
                (attachments, rawText, rawHtml, replyTo) = ReadRaw(stream);
                Mark("mime_parse");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        bodyHtml = rawHtml ?? bodyHtml;
        // Whitespace, not just absence: mail written as HTML often carries a text part that is empty or a
        // single newline, and taking that as the letter leaves an empty pane in front of a message that has
        // plenty to say.
        var bodyText = !string.IsNullOrWhiteSpace(rawText) ? rawText
            : bodyHtml is null ? storedText
            : MailText.FromHtml(bodyHtml);
        // The whole body is here anyway, so the summary keeps the snippet it has always carried.
        var summary = ReadSummary(reader, bodyText[..Math.Min(240, bodyText.Length)]);
        Mark("summary");
        return new MessageContent(summary, bodyText, bodyHtml, null, attachments)
        {
            ReplyTo = replyTo,
            RawPathProvider = rawRelative is null ? null : () => MimeCachePath(rawRelative)
        };
    }

    private string? MimeCachePath(string relative)
    {
        try { return _blobs.FullPath(relative); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public SyncState? GetSyncState(string account, string folder)
    {
        using var connection = OpenProjection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT account, folder, uidvalidity, max_uid, last_fetch_at, seen_max_id
            FROM sync_state WHERE account=$account AND folder=$folder
            """;
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$folder", folder);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new SyncState(reader.GetString(0), reader.GetString(1), checked((uint)reader.GetInt64(2)),
            checked((uint)reader.GetInt64(3)), ParseDate(reader, 4), reader.GetInt64(5));
    }

    public void SetSyncState(string account, string folder, uint uidValidity, uint maxUid, DateTimeOffset fetchedAt)
    {
        using var connection = Open();
        WriteSyncState(connection, null, account, folder, uidValidity, maxUid, fetchedAt);
    }

    private static void WriteSyncState(SqliteConnection connection, SqliteTransaction? transaction, string account,
        string folder, uint uidValidity, uint maxUid, DateTimeOffset fetchedAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sync_state(account, folder, uidvalidity, max_uid, last_fetch_at)
            VALUES ($account, $folder, $uidvalidity, $max_uid, $last_fetch_at)
            ON CONFLICT(account, folder) DO UPDATE SET
                uidvalidity=excluded.uidvalidity, max_uid=excluded.max_uid,
                last_fetch_at=excluded.last_fetch_at
            """;
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$folder", folder);
        command.Parameters.AddWithValue("$uidvalidity", uidValidity);
        command.Parameters.AddWithValue("$max_uid", maxUid);
        command.Parameters.AddWithValue("$last_fetch_at", fetchedAt.UtcDateTime.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void MarkSeen(string account, string folder)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sync_state(account, folder, seen_max_id)
            VALUES ($account, $folder, COALESCE((SELECT MAX(id) FROM messages WHERE account=$account), 0))
            ON CONFLICT(account, folder) DO UPDATE SET seen_max_id=excluded.seen_max_id
            """;
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$folder", folder);
        command.ExecuteNonQuery();
    }

    public IReadOnlySet<string> PopSeen(string account)
    {
        using var connection = OpenProjection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT uidl FROM pop_seen WHERE account=$account";
        command.Parameters.AddWithValue("$account", account);
        using var reader = command.ExecuteReader();
        var result = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public void RememberPopUidl(string account, string uidl)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO pop_seen(account,uidl,first_seen_at)
            VALUES ($account,$uidl,$first_seen_at)
            """;
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$uidl", uidl);
        command.Parameters.AddWithValue("$first_seen_at", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        command.ExecuteNonQuery();
    }

    internal void DetachRenumberedImapMessages(string account, string folder, uint currentValidity)
    {
        using var connection = Open();
        using var messages = connection.CreateCommand();
        messages.CommandText = """
            UPDATE messages SET server_uid=NULL
            WHERE source='live' AND account=$account AND folder=$folder
              AND uidvalidity<>$current AND server_uid IS NOT NULL
            """;
        messages.Parameters.AddWithValue("$account", account);
        messages.Parameters.AddWithValue("$folder", folder);
        messages.Parameters.AddWithValue("$current", currentValidity);
        messages.ExecuteNonQuery();
        FolderStorage.Flush();
    }

    public bool DeleteMessageLocally(long id, bool permanently = false)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        string source;
        string account;
        string folder;
        string sourceKey;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT source,account,folder,source_key FROM messages WHERE id=$id";
            select.Parameters.AddWithValue("$id", id);
            using var reader = select.ExecuteReader();
            if (!reader.Read()) return false;
            source = reader.GetString(0);
            account = reader.GetString(1);
            folder = reader.GetString(2);
            sourceKey = reader.GetString(3);
        }
        using var tombstone = connection.CreateCommand();
        tombstone.Transaction = transaction;
        tombstone.CommandText = """
            INSERT OR IGNORE INTO local_deletions(source,account,folder,source_key,deleted_at)
            VALUES($source,$account,$folder,$source_key,$deleted_at)
            """;
        tombstone.Parameters.AddWithValue("$source", source);
        tombstone.Parameters.AddWithValue("$account", account);
        tombstone.Parameters.AddWithValue("$folder", folder);
        tombstone.Parameters.AddWithValue("$source_key", sourceKey);
        tombstone.Parameters.AddWithValue("$deleted_at", DateTimeOffset.UtcNow.ToString("O"));
        tombstone.ExecuteNonQuery();
        if (permanently || folder.Equals(LocalFolderKinds.Deleted, StringComparison.OrdinalIgnoreCase))
        {
            using var fts = connection.CreateCommand();
            fts.Transaction = transaction;
            fts.CommandText = "DELETE FROM messages_fts WHERE rowid=$id";
            fts.Parameters.AddWithValue("$id", id);
            fts.ExecuteNonQuery();
            using var remove = connection.CreateCommand();
            remove.Transaction = transaction;
            remove.CommandText = "DELETE FROM messages WHERE id=$id";
            remove.Parameters.AddWithValue("$id", id);
            remove.ExecuteNonQuery();
        }
        else
        {
            using var move = connection.CreateCommand();
            move.Transaction = transaction;
            move.CommandText = "UPDATE messages SET folder=$folder,server_uid=NULL,uidvalidity=0 WHERE id=$id";
            move.Parameters.AddWithValue("$folder", LocalFolderKinds.Deleted);
            move.Parameters.AddWithValue("$id", id);
            move.ExecuteNonQuery();
        }
        transaction.Commit();
        FolderStorage.Flush();
        if (permanently || folder.Equals(LocalFolderKinds.Deleted, StringComparison.OrdinalIgnoreCase))
        {
            SweepUnreferencedBlobs();
            PreviewFiles.Cleanup(_paths);
        }
        return true;
    }

    public bool RemoveMessage(long id)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var fts = connection.CreateCommand();
        fts.Transaction = transaction;
        fts.CommandText = "DELETE FROM messages_fts WHERE rowid=$id";
        fts.Parameters.AddWithValue("$id", id);
        fts.ExecuteNonQuery();
        using var message = connection.CreateCommand();
        message.Transaction = transaction;
        message.CommandText = "DELETE FROM messages WHERE id=$id";
        message.Parameters.AddWithValue("$id", id);
        var removed = message.ExecuteNonQuery() > 0;
        transaction.Commit();
        return removed;
    }

    public long LogFetch(string account, string address, string source, DateTimeOffset startedAt)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO fetch_log(account,address,source,started_at) VALUES($account,$address,$source,$started); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$address", address);
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$started", startedAt.UtcDateTime.ToString("O"));
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    public void FinishFetch(long id, DateTimeOffset finishedAt, int newCount, int scanned, uint maxUid, string? error)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE fetch_log SET finished_at=$finished,
                duration_ms=CAST((julianday($finished)-julianday(started_at))*86400000 AS INTEGER),
                new_count=$new_count, scanned=$scanned, max_uid=$max_uid,
                ok=$ok, error=$error WHERE id=$id
            """;
        command.Parameters.AddWithValue("$finished", finishedAt.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$new_count", newCount);
        command.Parameters.AddWithValue("$scanned", scanned);
        command.Parameters.AddWithValue("$max_uid", maxUid);
        command.Parameters.AddWithValue("$ok", error is null ? 1 : 0);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void ImportFetch(FetchAttempt attempt)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO fetch_log(account,address,source,started_at,finished_at,duration_ms,
                new_count,scanned,max_uid,ok,error)
            VALUES($account,$address,$source,$started,$finished,$duration,$new_count,$scanned,$max_uid,$ok,$error)
            """;
        command.Parameters.AddWithValue("$account", attempt.Account);
        command.Parameters.AddWithValue("$address", attempt.Address);
        command.Parameters.AddWithValue("$source", attempt.Source);
        command.Parameters.AddWithValue("$started", attempt.StartedAt.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$finished", attempt.FinishedAt is null ? DBNull.Value : attempt.FinishedAt.Value.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$duration", attempt.DurationMs);
        command.Parameters.AddWithValue("$new_count", attempt.NewCount);
        command.Parameters.AddWithValue("$scanned", attempt.Scanned);
        command.Parameters.AddWithValue("$max_uid", attempt.MaxUid);
        command.Parameters.AddWithValue("$ok", attempt.Ok ? 1 : 0);
        command.Parameters.AddWithValue("$error", (object?)attempt.Error ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<FetchAttempt> RecentFetches(int limit = 100)
    {
        using var connection = OpenProjection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,account,address,source,started_at,finished_at,duration_ms,
                   new_count,scanned,max_uid,ok,error
            FROM fetch_log ORDER BY id DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        using var reader = command.ExecuteReader();
        var rows = new List<FetchAttempt>();
        while (reader.Read())
        {
            rows.Add(new FetchAttempt(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                ParseDate(reader, 5), reader.GetInt64(6), reader.GetInt32(7), reader.GetInt32(8),
                checked((uint)reader.GetInt64(9)), reader.GetInt32(10) != 0,
                reader.IsDBNull(11) ? null : reader.GetString(11)));
        }
        return rows;
    }

    public IReadOnlyList<(string Source, string Account, string Folder, int Count)> Folders()
    {
        using var connection = OpenProjection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT source,account,folder,COUNT(*) FROM messages GROUP BY source,account,folder ORDER BY source,account,folder";
        using var reader = command.ExecuteReader();
        var rows = new List<(string, string, string, int)>();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        return rows;
    }

    public IReadOnlyDictionary<string, (int Total, int New)> AccountMessageCounts()
    {
        using var connection = OpenProjection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.account,
                   COUNT(*),
                   SUM(CASE WHEN m.id > COALESCE((SELECT MAX(s.seen_max_id) FROM sync_state s WHERE s.account=m.account), 0) THEN 1 ELSE 0 END)
            FROM messages m
            WHERE m.source=$source
            GROUP BY m.account
            """;
        command.Parameters.AddWithValue("$source", MailContract.LiveSource);
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) result[reader.GetString(0)] = (reader.GetInt32(1), reader.GetInt32(2));
        return result;
    }

    public IReadOnlyDictionary<string, int> AccountUnreadCounts()
    {
        using var connection = OpenProjection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT account,SUM(is_read=0) FROM messages WHERE source=$source GROUP BY account";
        command.Parameters.AddWithValue("$source", MailContract.LiveSource);
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) result[reader.GetString(0)] = reader.GetInt32(1);
        return result;
    }

    /// <summary>Removes every message of one source and account, with its search rows and tombstones. Undoing
    /// an import is part of being able to run one — a half-removed account is worse than never importing it.</summary>
    public int DeleteBySource(string source, string account)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var fts = connection.CreateCommand())
        {
            fts.Transaction = transaction;
            fts.CommandText = """
                DELETE FROM messages_fts WHERE rowid IN
                    (SELECT id FROM messages WHERE source=$source AND account=$account)
                """;
            fts.Parameters.AddWithValue("$source", source);
            fts.Parameters.AddWithValue("$account", account);
            fts.ExecuteNonQuery();
        }
        using (var tombstones = connection.CreateCommand())
        {
            tombstones.Transaction = transaction;
            tombstones.CommandText = "DELETE FROM local_deletions WHERE source=$source AND account=$account";
            tombstones.Parameters.AddWithValue("$source", source);
            tombstones.Parameters.AddWithValue("$account", account);
            tombstones.ExecuteNonQuery();
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM messages WHERE source=$source AND account=$account";
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$account", account);
        var removed = command.ExecuteNonQuery();
        transaction.Commit();
        return removed;
    }

    /// <summary>Source keys already held, grouped by folder. An importer asks once and then knows what to skip
    /// without a round trip per candidate. Grouped by folder because that is what identifies a message here:
    /// the same mail filed in two folders is two messages, and a flat set would silently drop the second.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> SourceKeysByFolder(string source, string account)
    {
        using var connection = OpenProjection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT folder, source_key FROM messages WHERE source=$source AND account=$account";
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$account", account);
        using var reader = command.ExecuteReader();
        var folders = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            var folder = reader.GetString(0);
            if (!folders.TryGetValue(folder, out var keys))
            {
                keys = new HashSet<string>(StringComparer.Ordinal);
                folders[folder] = keys;
            }
            ((HashSet<string>)keys).Add(reader.GetString(1));
        }
        return folders;
    }

    /// <summary>Files one message into another folder of the same account.</summary>
    public int MoveMessage(long messageId, string folder)
        => MoveMessages([messageId], folder);

    /// <summary>Creates independent Local Mail copies while leaving the Connected rows and provider alone. An
    /// imported message can be copied too: that is how read-only mail becomes mail of this mailbox's own,
    /// with the import keeping its record. The key names each copy; left to itself, each gets a fresh one.</summary>
    public int CopyMessagesToLocal(IReadOnlyList<long> messageIds, string folder, Func<long, string>? keyFor = null)
    {
        var ids = messageIds.Distinct().ToArray();
        if (ids.Length == 0) return 0;
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        RequireFilingDestination(connection, transaction, folder);
        using var source = connection.CreateCommand();
        source.Transaction = transaction;
        source.CommandText = "SELECT source FROM messages WHERE id=$id";
        var sourceId = source.Parameters.Add("$id", SqliteType.Integer);
        foreach (var id in ids)
        {
            sourceId.Value = id;
            if (source.ExecuteScalar() is not string kind) throw new KeyNotFoundException("A selected message no longer exists.");
            if (kind is not (MailContract.LiveSource or MailContract.ArchiveSource)) throw new InvalidOperationException("Only live or imported messages can be copied.");
        }
        using var copy = connection.CreateCommand();
        copy.Transaction = transaction;
        copy.CommandText = """
            INSERT INTO messages(source,account,folder,source_key,server_uid,uidvalidity,subject,sender,sender_name,
                                 recipients_json,date_utc,size,is_read,attachment_count,body_text,body_html,raw_path)
            SELECT $source,$account,$folder,$key,NULL,0,subject,sender,sender_name,recipients_json,date_utc,size,is_read,
                   attachment_count,body_text,body_html,raw_path FROM messages WHERE id=$id
            RETURNING id
            """;
        copy.Parameters.AddWithValue("$source", MailContract.LiveSource);
        copy.Parameters.AddWithValue("$account", MailContract.LocalCopyAccount);
        copy.Parameters.AddWithValue("$folder", folder);
        var copyKey = copy.Parameters.Add("$key", SqliteType.Text);
        var copyId = copy.Parameters.Add("$id", SqliteType.Integer);
        using var fts = connection.CreateCommand();
        fts.Transaction = transaction;
        fts.CommandText = """
            INSERT INTO messages_fts(rowid,subject,sender,recipients,body_text)
            SELECT $copy,subject,sender,recipients,body_text FROM messages_fts WHERE rowid=$source
            """;
        var ftsCopy = fts.Parameters.Add("$copy", SqliteType.Integer);
        var ftsSource = fts.Parameters.Add("$source", SqliteType.Integer);
        using var marks = connection.CreateCommand();
        marks.Transaction = transaction;
        marks.CommandText = "INSERT OR IGNORE INTO message_marks(message_id,kind,marked_at) SELECT $copy,kind,marked_at FROM message_marks WHERE message_id=$source";
        var marksCopy = marks.Parameters.Add("$copy", SqliteType.Integer);
        var marksSource = marks.Parameters.Add("$source", SqliteType.Integer);
        foreach (var id in ids)
        {
            copyId.Value = id;
            copyKey.Value = keyFor?.Invoke(id) ?? "copy:" + Guid.NewGuid().ToString("n");
            ftsCopy.Value = (long)(copy.ExecuteScalar() ?? throw new InvalidOperationException("Local copy returned no id."));
            ftsSource.Value = id;
            fts.ExecuteNonQuery();
            marksCopy.Value = ftsCopy.Value;
            marksSource.Value = id;
            marks.ExecuteNonQuery();
        }
        transaction.Commit();
        FolderStorage.Flush();
        return ids.Length;
    }

    /// <summary>Files imported messages: each becomes mail of this mailbox's own in the folder, keyed to the
    /// imported row so the import's copy stands down wherever it was. One adopted before is moved, not copied
    /// again.</summary>
    public int AdoptMessages(IReadOnlyList<long> archiveIds, string folder)
    {
        var ids = archiveIds.Distinct().ToArray();
        if (ids.Length == 0) return 0;
        var fresh = new List<long>();
        var moved = 0;
        using (var connection = Open())
        using (var transaction = connection.BeginTransaction())
        {
            RequireFilingDestination(connection, transaction, folder);
            using var existing = connection.CreateCommand();
            existing.Transaction = transaction;
            existing.CommandText = "UPDATE messages SET folder=$folder WHERE source=$live AND account=$account AND source_key=$key RETURNING id";
            existing.Parameters.AddWithValue("$folder", folder);
            existing.Parameters.AddWithValue("$live", MailContract.LiveSource);
            existing.Parameters.AddWithValue("$account", MailContract.LocalCopyAccount);
            var key = existing.Parameters.Add("$key", SqliteType.Text);
            foreach (var id in ids)
            {
                key.Value = MailContract.AdoptedCopyKeyPrefix + id;
                if (existing.ExecuteScalar() is not null || FolderStorage.MoveBoundImport(connection, transaction, id, folder)) moved++;
                else fresh.Add(id);
            }
            transaction.Commit();
        }
        if (fresh.Count == 0) FolderStorage.Flush();
        return moved + CopyMessagesToLocal(fresh, folder, id => MailContract.AdoptedCopyKeyPrefix + id);
    }

    public int MoveMessages(IReadOnlyList<long> messageIds, string folder)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        RequireFilingDestination(connection, transaction, folder);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE messages SET folder=$folder WHERE id=$id AND folder<>$folder";
        command.Parameters.AddWithValue("$folder", folder);
        var idParameter = command.Parameters.Add("$id", SqliteType.Integer);
        using var source = connection.CreateCommand();
        source.Transaction = transaction;
        source.CommandText = "SELECT source FROM messages WHERE id=$id";
        var sourceId = source.Parameters.Add("$id", SqliteType.Integer);
        var moved = 0;
        foreach (var id in messageIds.Distinct())
        {
            sourceId.Value = id;
            if (source.ExecuteScalar() is not string sourceKind) throw new KeyNotFoundException("A selected message no longer exists.");
            if (sourceKind != MailContract.LiveSource) throw new InvalidOperationException("Imported messages are read-only here.");
            idParameter.Value = id;
            try { moved += command.ExecuteNonQuery(); }
            catch (SqliteException error) when (error.SqliteErrorCode == 19)
            {
                throw new InvalidOperationException("The destination already contains one of these messages. No messages were moved.", error);
            }
        }
        transaction.Commit();
        FolderStorage.Flush();
        return moved;
    }

    private static void RequireFilingDestination(SqliteConnection connection, SqliteTransaction transaction, string folder)
    {
        using var destination = connection.CreateCommand();
        destination.Transaction = transaction;
        destination.CommandText = "SELECT kind FROM local_folders WHERE folder_key=$folder";
        destination.Parameters.AddWithValue("$folder", folder);
        if (destination.ExecuteScalar() is not string kind)
            throw new KeyNotFoundException("The destination folder no longer exists.");
        if (kind is LocalFolderKinds.Drafts or LocalFolderKinds.Outbox)
            throw new InvalidOperationException("Messages cannot be filed into Drafts or Outbox.");
    }

    /// <summary>Marks one message read or unread, and reports whether that was a change. Reading a message in
    /// another client is a change worth following, the same as filing it.</summary>
    public int SetReadState(string source, string account, string folder, string sourceKey, bool isRead)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE messages SET is_read=$read
            WHERE source=$source AND account=$account AND folder=$folder AND source_key=$key AND is_read<>$read
            """;
        command.Parameters.AddWithValue("$read", isRead ? 1 : 0);
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$folder", folder);
        command.Parameters.AddWithValue("$key", sourceKey);
        var changed = command.ExecuteNonQuery();
        FolderStorage.Flush();
        return changed;
    }

    /// <summary>The heads of every message in a source, an account or a folder, unbounded: for matching one
    /// store against another, where a page of five hundred is no use.</summary>
    public IReadOnlyList<MessageHead> ListHeads(string source, string? account = null, string? folder = null)
    {
        using var connection = OpenProjection();
        using var command = connection.CreateCommand();
        var where = new List<string> { "source=$source" };
        command.Parameters.AddWithValue("$source", source);
        if (account is not null) { where.Add("account=$account"); command.Parameters.AddWithValue("$account", account); }
        if (folder is not null) { where.Add("folder=$folder"); command.Parameters.AddWithValue("$folder", folder); }
        command.CommandText = "SELECT id,account,folder,subject,sender,date_utc,is_read FROM messages WHERE " + string.Join(" AND ", where);
        using var reader = command.ExecuteReader();
        var heads = new List<MessageHead>();
        while (reader.Read())
            heads.Add(new MessageHead(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.IsDBNull(5) ? "" : reader.GetString(5), reader.GetInt32(6) != 0));
        return heads;
    }

    /// <summary>Marks many messages read or unread by id, and reports how many that changed.</summary>
    public int SetReadStates(IReadOnlyList<long> messageIds, bool isRead)
    {
        if (messageIds.Count == 0) return 0;
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE messages SET is_read=$read WHERE id=$id AND is_read<>$read";
        command.Parameters.AddWithValue("$read", isRead ? 1 : 0);
        var id = command.Parameters.Add("$id", SqliteType.Integer);
        var changed = 0;
        foreach (var messageId in messageIds.Distinct()) { id.Value = messageId; changed += command.ExecuteNonQuery(); }
        transaction.Commit();
        FolderStorage.Flush();
        return changed;
    }

    /// <summary>Which folder each message is in now; a message no longer held is left out.</summary>
    public IReadOnlyDictionary<long, string> MessageFolders(IReadOnlyCollection<long> messageIds)
    {
        var folders = new Dictionary<long, string>();
        if (messageIds.Count == 0) return folders;
        using var connection = OpenProjection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,folder FROM messages WHERE id IN (SELECT value FROM json_each($ids))";
        command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(messageIds));
        using var reader = command.ExecuteReader();
        while (reader.Read()) folders[reader.GetInt64(0)] = reader.GetString(1);
        return folders;
    }

    /// <summary>The mailbox's own copy of each imported message filed so far, and the folder it is in; one never
    /// filed is left out.</summary>
    public IReadOnlyDictionary<long, (long Id, string Folder)> AdoptedCopies(IReadOnlyCollection<long> archiveIds)
    {
        var copies = new Dictionary<long, (long, string)>();
        if (archiveIds.Count == 0) return copies;
        using var connection = OpenProjection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,folder,source_key FROM messages WHERE source=$live AND account=$account AND source_key IN (SELECT $prefix || value FROM json_each($ids))";
        command.Parameters.AddWithValue("$live", MailContract.LiveSource);
        command.Parameters.AddWithValue("$account", MailContract.LocalCopyAccount);
        command.Parameters.AddWithValue("$prefix", MailContract.AdoptedCopyKeyPrefix);
        command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(archiveIds));
        using var reader = command.ExecuteReader();
        while (reader.Read())
            copies[long.Parse(reader.GetString(2)[MailContract.AdoptedCopyKeyPrefix.Length..], CultureInfo.InvariantCulture)] = (reader.GetInt64(0), reader.GetString(1));
        return copies;
    }

    /// <summary>Takes back a move to Deleted Items: the message returns to the folder it was deleted from and
    /// the deletion it left behind is forgotten. One no longer in Deleted Items is left where it is.</summary>
    public bool RestoreDeleted(long id, string folder)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var move = connection.CreateCommand();
        move.Transaction = transaction;
        move.CommandText = "UPDATE messages SET folder=$folder WHERE id=$id AND folder=$deleted RETURNING source,account,source_key";
        move.Parameters.AddWithValue("$folder", folder);
        move.Parameters.AddWithValue("$id", id);
        move.Parameters.AddWithValue("$deleted", LocalFolderKinds.Deleted);
        string source, account, sourceKey;
        using (var reader = move.ExecuteReader())
        {
            if (!reader.Read()) return false;
            source = reader.GetString(0);
            account = reader.GetString(1);
            sourceKey = reader.GetString(2);
        }
        using var forget = connection.CreateCommand();
        forget.Transaction = transaction;
        forget.CommandText = "DELETE FROM local_deletions WHERE source=$source AND account=$account AND folder=$folder AND source_key=$key";
        forget.Parameters.AddWithValue("$source", source);
        forget.Parameters.AddWithValue("$account", account);
        forget.Parameters.AddWithValue("$folder", folder);
        forget.Parameters.AddWithValue("$key", sourceKey);
        forget.ExecuteNonQuery();
        transaction.Commit();
        return true;
    }

    public IReadOnlyDictionary<long, bool> ReadStates(IReadOnlyList<long> messageIds)
    {
        if (messageIds.Count == 0) return new Dictionary<long, bool>();
        using var connection = OpenProjection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,is_read FROM messages WHERE id IN (" +
            string.Join(',', messageIds.Select((_, index) => "$id" + index)) + ")";
        for (var index = 0; index < messageIds.Count; index++) command.Parameters.AddWithValue("$id" + index, messageIds[index]);
        using var reader = command.ExecuteReader();
        var states = new Dictionary<long, bool>();
        while (reader.Read()) states[reader.GetInt64(0)] = reader.GetBoolean(1);
        return states;
    }

    /// <summary>Message and unread counts per folder for one source, which is what a folder list shows.</summary>
    public IReadOnlyList<(string Account, string Folder, int Total, int Unread)> FolderCounts(string source)
    {
        using var connection = OpenProjection();
        using var command = connection.CreateCommand();
        // Folders that hold mail, plus the ones declared empty, so a subfolder made in advance does not
        // vanish the moment the tree is rebuilt.
        command.CommandText = """
            SELECT account, folder, COUNT(*), SUM(CASE WHEN is_read=0 THEN 1 ELSE 0 END)
            FROM messages INDEXED BY idx_messages_unconverted_counts
            WHERE source=$source AND converted_draft_id IS NULL GROUP BY account, folder
            UNION ALL
            SELECT f.account, f.folder, 0, 0 FROM archive_folders f
            WHERE f.source=$source
              AND NOT EXISTS (SELECT 1 FROM messages m
                              WHERE m.source=f.source AND m.account=f.account AND m.folder=f.folder AND m.converted_draft_id IS NULL)
            """;
        command.Parameters.AddWithValue("$source", source);
        using var reader = command.ExecuteReader();
        var folders = new List<(string, string, int, int)>();
        while (reader.Read()) folders.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3)));
        return folders;
    }

    /// <summary>Removes one message from one folder, leaving no tombstone: used when a message has moved
    /// folders elsewhere and this row is the stale copy, not a deletion the user asked for.</summary>
    public int RemoveFromFolder(string source, string account, string folder, string sourceKey)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var fts = connection.CreateCommand())
        {
            fts.Transaction = transaction;
            fts.CommandText = """
                DELETE FROM messages_fts WHERE rowid IN
                    (SELECT id FROM messages WHERE source=$source AND account=$account AND folder=$folder AND source_key=$key)
                """;
            Bind(fts);
            fts.ExecuteNonQuery();
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM messages WHERE source=$source AND account=$account AND folder=$folder AND source_key=$key";
        Bind(command);
        var removed = command.ExecuteNonQuery();
        transaction.Commit();
        return removed;

        void Bind(SqliteCommand command)
        {
            command.Parameters.AddWithValue("$source", source);
            command.Parameters.AddWithValue("$account", account);
            command.Parameters.AddWithValue("$folder", folder);
            command.Parameters.AddWithValue("$key", sourceKey);
        }
    }

    /// <summary>Which folders hold these particular messages, and how many of them. Lets a caller identify a
    /// folder by its contents when its name cannot be trusted.</summary>
    public IReadOnlyDictionary<string, int> FoldersHolding(string source, string account, IReadOnlyCollection<string> sourceKeys)
    {
        if (sourceKeys.Count == 0) return new Dictionary<string, int>();
        using var connection = OpenProjection();
        using var command = connection.CreateCommand();
        var keyParameters = sourceKeys.Select((key, index) =>
        {
            var name = "$key" + index;
            command.Parameters.AddWithValue(name, key);
            return name;
        }).ToArray();
        command.CommandText = $"""
            SELECT folder, COUNT(*) FROM messages
            WHERE source=$source AND account=$account AND source_key IN ({string.Join(',', keyParameters)})
            GROUP BY folder
            """;
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$account", account);
        using var reader = command.ExecuteReader();
        var folders = new Dictionary<string, int>(StringComparer.Ordinal);
        while (reader.Read()) folders[reader.GetString(0)] = reader.GetInt32(1);
        return folders;
    }

    /// <summary>Moves every message of one folder into another name. Used when an import learns the folder's
    /// real place in a hierarchy after the messages themselves have already been read.</summary>
    /// <summary>Creates a folder of one's own. Local folders are real rows, so an empty one is perfectly
    /// legitimate and survives a restart with nothing in it.</summary>
    /// <summary>Makes a folder of the mailbox's own. Origin says where it came from: "native" for one the
    /// reader made, "archive" with the import's account and path for one adopted from an import so that
    /// mail can be filed where the import's mail already sits.</summary>
    public LocalFolder CreateLocalFolder(string name, string? parentKey, string origin = "native", string? originKey = null)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) throw new ArgumentException("A folder needs a name.");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        RequireAvailableFolderName(connection, transaction, parentKey, trimmed);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Keys are stable and independent of the name, so renaming never has to rewrite anything that points here.
        var key = "user:" + Guid.NewGuid().ToString("n")[..12];
        command.CommandText = """
            INSERT INTO local_folders(folder_key,parent_key,name,kind,sort_order,origin,origin_key)
            VALUES($key,$parent,$name,$kind,
                   COALESCE((SELECT MAX(sort_order) FROM local_folders) + 1, 100), $origin, $origin_key)
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$parent", (object?)parentKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$name", trimmed);
        command.Parameters.AddWithValue("$kind", LocalFolderKinds.User);
        command.Parameters.AddWithValue("$origin", origin);
        command.Parameters.AddWithValue("$origin_key", (object?)originKey ?? DBNull.Value);
        command.ExecuteNonQuery();
        transaction.Commit();
        FolderStorage.Flush();
        return new LocalFolder(key, parentKey, trimmed, LocalFolderKinds.User, 0, origin, originKey);
    }

    public int RenameLocalFolder(string folderKey, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) throw new ArgumentException("A folder needs a name.");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        string? parentKey;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT parent_key FROM local_folders WHERE folder_key=$key";
            select.Parameters.AddWithValue("$key", folderKey);
            using var reader = select.ExecuteReader();
            if (!reader.Read()) return 0;
            parentKey = reader.IsDBNull(0) ? null : reader.GetString(0);
        }
        RequireAvailableFolderName(connection, transaction, parentKey, trimmed, folderKey);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE local_folders SET name=$name WHERE folder_key=$key";
        command.Parameters.AddWithValue("$name", trimmed);
        command.Parameters.AddWithValue("$key", folderKey);
        var changed = command.ExecuteNonQuery();
        transaction.Commit();
        FolderStorage.Flush();
        return changed;
    }

    private static void RequireAvailableFolderName(SqliteConnection connection, SqliteTransaction transaction,
        string? parentKey, string name, string? exceptKey = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name FROM local_folders WHERE parent_key IS $parent AND folder_key IS NOT $except";
        command.Parameters.AddWithValue("$parent", (object?)parentKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$except", (object?)exceptKey ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(0), name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"A folder named '{name}' already exists in this location.");
    }

    /// <summary>Re-parents a folder. Passing null makes it a top-level folder again.</summary>
    public int MoveLocalFolder(string folderKey, string? parentKey)
    {
        if (string.Equals(folderKey, parentKey, StringComparison.Ordinal))
            throw new InvalidOperationException("A folder cannot be inside itself.");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        string name;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT name FROM local_folders WHERE folder_key=$key";
            select.Parameters.AddWithValue("$key", folderKey);
            if (select.ExecuteScalar() is not string folderName) return 0;
            name = folderName;
        }
        RequireAvailableFolderName(connection, transaction, parentKey, name, folderKey);
        if (parentKey is not null)
        {
            using var guard = connection.CreateCommand();
            guard.Transaction = transaction;
            guard.CommandText = """
                WITH RECURSIVE subtree(folder_key) AS (
                    SELECT folder_key FROM local_folders WHERE folder_key=$key
                    UNION
                    SELECT child.folder_key FROM local_folders child JOIN subtree ON child.parent_key=subtree.folder_key
                )
                SELECT EXISTS(SELECT 1 FROM local_folders WHERE folder_key=$parent),
                       EXISTS(SELECT 1 FROM subtree WHERE folder_key=$parent)
                """;
            guard.Parameters.AddWithValue("$key", folderKey);
            guard.Parameters.AddWithValue("$parent", parentKey);
            using var reader = guard.ExecuteReader();
            reader.Read();
            if (!reader.GetBoolean(0)) throw new KeyNotFoundException("The destination folder no longer exists.");
            if (reader.GetBoolean(1)) throw new InvalidOperationException("A folder cannot be inside itself or one of its subfolders.");
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE local_folders SET parent_key=$parent WHERE folder_key=$key";
        command.Parameters.AddWithValue("$parent", (object?)parentKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$key", folderKey);
        var changed = command.ExecuteNonQuery();
        transaction.Commit();
        FolderStorage.Flush();
        return changed;
    }

    /// <summary>Deletes an empty folder of one's own. Refuses while it still holds mail or folders, because a
    /// folder disappearing with its contents is not something anyone asks for twice.</summary>
    public void DeleteLocalFolder(string folderKey)
    {
        var imported = ListLocalFolders().SingleOrDefault(f => f.Key == folderKey);
        if (imported is { Origin: MailContract.ArchiveSource, OriginKey: { } origin }
            && origin.Split('|', 2) is [var account, var path]
            && !FolderStorage.Inspect().Folders.Any(f => f.Key == folderKey))
        {
            DeleteArchiveFolder(MailContract.ArchiveSource, account, path);
            return;
        }
        using var connection = Open();
        using var guard = connection.CreateCommand();
        guard.CommandText = """
            SELECT (SELECT COUNT(*) FROM messages WHERE source=$source AND folder=$key)
                       + (SELECT COUNT(*) FROM draft_folders f JOIN outgoing_messages o ON o.id=f.draft_id WHERE f.folder_key=$key AND o.status='draft'),
                   (SELECT COUNT(*) FROM local_folders WHERE parent_key=$key),
                   (SELECT kind FROM local_folders WHERE folder_key=$key)
            """;
        guard.Parameters.AddWithValue("$source", MailContract.LiveSource);
        guard.Parameters.AddWithValue("$key", folderKey);
        using (var reader = guard.ExecuteReader())
        {
            if (!reader.Read() || reader.IsDBNull(2)) throw new KeyNotFoundException("No such folder.");
            if (reader.GetString(2) != LocalFolderKinds.User)
                throw new InvalidOperationException("The standard folders cannot be deleted.");
            if (reader.GetInt32(0) > 0) throw new InvalidOperationException("The folder still holds messages.");
            if (reader.GetInt32(1) > 0) throw new InvalidOperationException("The folder still holds folders.");
        }
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM local_folders WHERE folder_key=$key";
        command.Parameters.AddWithValue("$key", folderKey);
        command.ExecuteNonQuery();
        FolderStorage.Flush();
    }

    /// <summary>Declares an imported folder that holds nothing yet, so one can be made before there is
    /// anything to put in it.</summary>
    public void CreateArchiveFolder(string source, string account, string folder)
    {
        var path = folder.Trim().Trim('/');
        if (path.Length == 0) throw new ArgumentException("A folder needs a name.");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO archive_folders(source,account,folder) VALUES($source,$account,$folder)";
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$folder", path);
        command.ExecuteNonQuery();
    }

    /// <summary>Removes an imported folder that holds nothing. Folders with mail in them are left alone —
    /// the mail is the folder.</summary>
    public int DeleteArchiveFolder(string source, string account, string folder)
    {
        if (source == MailContract.ArchiveSource) return FolderStorage.DeleteFiledImportedFolder(account, folder);
        using var connection = Open();
        using var guard = connection.CreateCommand();
        guard.CommandText = "SELECT COUNT(*) FROM messages WHERE source=$source AND account=$account AND (folder=$folder OR folder LIKE $prefix)";
        guard.Parameters.AddWithValue("$source", source);
        guard.Parameters.AddWithValue("$account", account);
        guard.Parameters.AddWithValue("$folder", folder);
        guard.Parameters.AddWithValue("$prefix", folder + "/%");
        if (Convert.ToInt32(guard.ExecuteScalar()) > 0)
            throw new InvalidOperationException("The folder still holds messages.");
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM archive_folders WHERE source=$source AND account=$account AND (folder=$folder OR folder LIKE $prefix)";
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$folder", folder);
        command.Parameters.AddWithValue("$prefix", folder + "/%");
        return command.ExecuteNonQuery();
    }

    /// <summary>Re-paths an imported folder and everything beneath it. An archive folder is not a row anywhere —
    /// it is the path its messages carry — so nesting one inside another is a rewrite of those paths, and the
    /// children have to come along or they are orphaned under a folder that no longer exists.</summary>
    public int MoveArchiveFolder(string source, string account, string from, string to)
    {
        if (string.Equals(from, to, StringComparison.Ordinal)) return 0;
        if (to.StartsWith(from + "/", StringComparison.Ordinal))
            throw new InvalidOperationException("A folder cannot be moved inside itself.");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE OR IGNORE messages
               SET folder = $to || SUBSTR(folder, LENGTH($from) + 1)
             WHERE source=$source AND account=$account
               AND (folder = $from OR folder LIKE $prefix)
            """;
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        command.Parameters.AddWithValue("$prefix", from + "/%");
        var moved = command.ExecuteNonQuery();

        // The declared-empty folders move with it, or an empty subfolder would be left behind at the old path.
        using var declared = connection.CreateCommand();
        declared.CommandText = """
            UPDATE OR REPLACE archive_folders
               SET folder = $to || SUBSTR(folder, LENGTH($from) + 1)
             WHERE source=$source AND account=$account AND (folder = $from OR folder LIKE $prefix)
            """;
        declared.Parameters.AddWithValue("$source", source);
        declared.Parameters.AddWithValue("$account", account);
        declared.Parameters.AddWithValue("$from", from);
        declared.Parameters.AddWithValue("$to", to);
        declared.Parameters.AddWithValue("$prefix", from + "/%");
        declared.ExecuteNonQuery();
        return moved;
    }

    public int RenameFolder(string source, string account, string from, string to)
    {
        if (string.Equals(from, to, StringComparison.Ordinal)) return 0;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE OR IGNORE messages SET folder=$to
            WHERE source=$source AND account=$account AND folder=$from
            """;
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        return command.ExecuteNonQuery();
    }

    /// <summary>Removes bookkeeping entries sharing a prefix — how a migration is told to start over.</summary>
    public int DeleteSettings(string prefix)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM settings WHERE key LIKE $prefix";
        command.Parameters.AddWithValue("$prefix", prefix + "%");
        return command.ExecuteNonQuery();
    }

    /// <summary>The newest message date per account — what "most recent activity" means for ordering,
    /// independent of whether anything has been read.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> AccountLatestDates()
    {
        using var connection = OpenProjection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT account, MAX(date_utc) FROM messages
            WHERE source=$source AND date_utc IS NOT NULL
            GROUP BY account
            """;
        command.Parameters.AddWithValue("$source", MailContract.LiveSource);
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
            if (DateTimeOffset.TryParse(reader.GetString(1), out var date)) result[reader.GetString(0)] = date;
        return result;
    }

    public IReadOnlyDictionary<string, int> Stats()
    {
        using var connection = OpenProjection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT source,COUNT(*) FROM messages GROUP BY source";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) result[reader.GetString(0)] = reader.GetInt32(1);
        return result;
    }

    public OutgoingMessage SaveDraft(string sendingIdentityId, IReadOnlyList<string> to, IReadOnlyList<string> cc,
        IReadOnlyList<string> bcc, string subject, string bodyText, string? id = null, string? rawPath = null, string? bodyHtml = null)
    {
        Initialize();
        id ??= "out_" + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow.UtcDateTime.ToString("O");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO outgoing_messages(id,account,sending_identity_id,to_json,cc_json,bcc_json,subject,body_text,body_html,raw_path,status,created_at,updated_at)
            VALUES($id,$identity,$identity,$to,$cc,$bcc,$subject,$body,$html,$raw,'draft',$now,$now)
            ON CONFLICT(id) DO UPDATE SET sending_identity_id=excluded.sending_identity_id,to_json=excluded.to_json,cc_json=excluded.cc_json,
                bcc_json=excluded.bcc_json,subject=excluded.subject,body_text=excluded.body_text,body_html=excluded.body_html,raw_path=excluded.raw_path,updated_at=excluded.updated_at
            WHERE outgoing_messages.status='draft'
            """;
        command.Parameters.AddWithValue("$html", (object?)bodyHtml ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$identity", sendingIdentityId);
        command.Parameters.AddWithValue("$to", JsonSerializer.Serialize(to));
        command.Parameters.AddWithValue("$cc", JsonSerializer.Serialize(cc));
        command.Parameters.AddWithValue("$bcc", JsonSerializer.Serialize(bcc));
        command.Parameters.AddWithValue("$subject", subject);
        command.Parameters.AddWithValue("$body", bodyText);
        command.Parameters.AddWithValue("$raw", (object?)rawPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now);
        if (command.ExecuteNonQuery() == 0) throw new InvalidOperationException("Only drafts can be edited.");
        return GetOutgoing(id)!;
    }

    public bool DeleteDraft(string id)
    {
        Initialize();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM outgoing_messages WHERE id=$id AND status='draft'";
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteNonQuery() != 1) return false;
        using var placement = connection.CreateCommand();
        placement.Transaction = transaction;
        placement.CommandText = "DELETE FROM draft_folders WHERE draft_id=$id";
        placement.Parameters.AddWithValue("$id", id);
        placement.ExecuteNonQuery();
        // A discarded reply was never sent, so it takes its record of what it answered with it.
        using var origin = connection.CreateCommand();
        origin.Transaction = transaction;
        origin.CommandText = "DELETE FROM outgoing_origins WHERE outgoing_id=$id";
        origin.Parameters.AddWithValue("$id", id);
        origin.ExecuteNonQuery();
        transaction.Commit();
        SweepUnreferencedBlobs();
        return true;
    }

    /// <summary>Remembers which message a reply or forward was written from. Nothing shows on that message yet:
    /// the mark is made by MarkOutgoingSent, so a reply abandoned, kept as a draft or failing to send leaves none.</summary>
    public void SetOutgoingOrigin(string outgoingId, long messageId, string kind)
    {
        if (kind is not (MessageMarkKinds.Replied or MessageMarkKinds.Forwarded)) throw new ArgumentOutOfRangeException(nameof(kind));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO outgoing_origins(outgoing_id,message_id,kind) VALUES($id,$message,$kind)
            ON CONFLICT(outgoing_id) DO UPDATE SET message_id=excluded.message_id,kind=excluded.kind
            """;
        command.Parameters.AddWithValue("$id", outgoingId);
        command.Parameters.AddWithValue("$message", messageId);
        command.Parameters.AddWithValue("$kind", kind);
        command.ExecuteNonQuery();
    }

    /// <summary>Replied and forwarded marks for the given messages; a message with neither is left out.</summary>
    public IReadOnlyDictionary<long, MessageMarks> MessageMarks(IReadOnlyCollection<long> messageIds)
    {
        var marks = new Dictionary<long, MessageMarks>();
        if (messageIds.Count == 0) return marks;
        using var connection = OpenProjection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT message_id,kind FROM message_marks WHERE message_id IN (SELECT value FROM json_each($ids))";
        command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(messageIds));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var current = marks.GetValueOrDefault(reader.GetInt64(0));
            marks[reader.GetInt64(0)] = reader.GetString(1) == MessageMarkKinds.Replied
                ? current with { Replied = true } : current with { Forwarded = true };
        }
        return marks;
    }

    // ---- address book ---------------------------------------------------------------------------------------

    public IReadOnlyList<Contact> ListContacts()
    {
        using var connection = OpenProjection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,email,notes,origin,origin_key FROM contacts "
            + "ORDER BY CASE WHEN name='' THEN email ELSE name END COLLATE NOCASE, email COLLATE NOCASE";
        using var reader = command.ExecuteReader();
        var contacts = new List<Contact>();
        while (reader.Read())
            contacts.Add(new Contact(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        return contacts;
    }

    /// <summary>Adds a contact, or with an id, changes one. One contact per address: saving a second for an
    /// address already held is refused rather than quietly merged.</summary>
    public Contact SaveContact(Contact contact)
    {
        var email = contact.Email.Trim();
        if (!IsPlainAddress(email)) throw new ArgumentException($"'{email}' is not an email address.");
        var name = contact.Name.Trim();
        var notes = contact.Notes.Trim();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = contact.Id == 0
            ? """
              INSERT INTO contacts(name,email,notes,origin,origin_key,created_at,updated_at)
              VALUES($name,$email,$notes,$origin,$origin_key,$now,$now) RETURNING id
              """
            : "UPDATE contacts SET name=$name,email=$email,notes=$notes,updated_at=$now WHERE id=$id RETURNING id";
        command.Parameters.AddWithValue("$id", contact.Id);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$email", email);
        command.Parameters.AddWithValue("$notes", notes);
        command.Parameters.AddWithValue("$origin", contact.Origin);
        command.Parameters.AddWithValue("$origin_key", (object?)contact.OriginKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        try
        {
            var id = command.ExecuteScalar() as long? ?? throw new KeyNotFoundException("That contact no longer exists.");
            return contact with { Id = id, Name = name, Email = email, Notes = notes };
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException($"{email} is already in the address book.", error);
        }
    }

    public bool DeleteContact(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM contacts WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>Adds the contacts whose address is not already held, leaving every existing contact as it is —
    /// running an import twice, or after editing an imported contact, changes nothing.</summary>
    public (int Added, int AlreadyHeld, int Unusable) ImportContacts(IEnumerable<Contact> contacts)
    {
        var added = 0; var held = 0; var unusable = 0;
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO contacts(name,email,notes,origin,origin_key,created_at,updated_at)
            VALUES($name,$email,$notes,$origin,$origin_key,$now,$now) ON CONFLICT(email) DO NOTHING
            """;
        var name = command.Parameters.Add("$name", SqliteType.Text);
        var email = command.Parameters.Add("$email", SqliteType.Text);
        var notes = command.Parameters.Add("$notes", SqliteType.Text);
        var origin = command.Parameters.Add("$origin", SqliteType.Text);
        var originKey = command.Parameters.Add("$origin_key", SqliteType.Text);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        foreach (var contact in contacts)
        {
            var address = contact.Email.Trim();
            if (!IsPlainAddress(address)) { unusable++; continue; }
            name.Value = contact.Name.Trim();
            email.Value = address;
            notes.Value = contact.Notes.Trim();
            origin.Value = contact.Origin;
            originKey.Value = (object?)contact.OriginKey ?? DBNull.Value;
            if (command.ExecuteNonQuery() == 1) added++; else held++;
        }
        transaction.Commit();
        return (added, held, unusable);
    }

    /// <summary>The addresses mail has been sent to and not removed from the suggestions, most recently used
    /// first. The first call fills the history from the sent folders already in the store.</summary>
    public IReadOnlyList<AddressUse> ListAddressHistory()
    {
        using var connection = Open();
        SeedAddressHistory(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT email,uses,last_used FROM address_history WHERE removed=0 ORDER BY last_used DESC";
        using var reader = command.ExecuteReader();
        var uses = new List<AddressUse>();
        while (reader.Read()) uses.Add(new AddressUse(reader.GetString(0), reader.GetInt32(1), reader.GetString(2)));
        return uses;
    }

    /// <summary>Takes an address out of the suggestions. It comes back only when mail is sent to it again.</summary>
    public bool RemoveFromAddressHistory(string email)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE address_history SET removed=1 WHERE email=$email";
        command.Parameters.AddWithValue("$email", email.Trim());
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>Once per store: every address in a sent folder, with how often and when it was last written to.
    /// The online archive of a former employer is left out — thousands of colleagues nobody writes to now.</summary>
    private static void SeedAddressHistory(SqliteConnection connection)
    {
        using var seeded = connection.CreateCommand();
        seeded.CommandText = "SELECT 1 FROM settings WHERE key='address_history.seeded'";
        if (seeded.ExecuteScalar() is not null) return;
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO address_history(email,uses,last_used)
            SELECT lower(trim(r.value)), COUNT(*), MAX(COALESCE(m.date_utc,''))
            FROM messages m, json_each(m.recipients_json) r
            WHERE (m.folder IN ('sent','Sent','Sent Items') OR m.folder LIKE '%/Sent' OR m.folder LIKE '%/Sent Items')
              AND m.account NOT LIKE 'Online Archive%'
              AND r.value LIKE '%_@_%' AND r.value NOT LIKE '%<%'
            GROUP BY lower(trim(r.value))
            ON CONFLICT(email) DO NOTHING;
            INSERT INTO settings(key,value) VALUES('address_history.seeded','1');
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>Counts a delivered message's recipients as used, bringing back any that had been removed.</summary>
    private static void RecordAddressUse(SqliteConnection connection, string id, string now)
    {
        var recipients = new List<string>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT to_json,cc_json,bcc_json FROM outgoing_messages WHERE id=$id";
            read.Parameters.AddWithValue("$id", id);
            using var reader = read.ExecuteReader();
            if (!reader.Read()) return;
            for (var column = 0; column < 3; column++)
                recipients.AddRange(JsonSerializer.Deserialize<string[]>(reader.GetString(column)) ?? []);
        }
        // A line may hold "Name <address>"; the history keeps the address alone.
        var addresses = recipients
            .Select(recipient => MailboxAddress.TryParse(recipient, out var mailbox) ? mailbox.Address : recipient.Trim())
            .Where(address => address.Contains('@'))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO address_history(email,uses,last_used) VALUES($email,1,$now)
            ON CONFLICT(email) DO UPDATE SET uses=uses+1, last_used=excluded.last_used, removed=0
            """;
        var email = command.Parameters.Add("$email", SqliteType.Text);
        command.Parameters.AddWithValue("$now", now);
        foreach (var address in addresses) { email.Value = address.ToLowerInvariant(); command.ExecuteNonQuery(); }
    }

    /// <summary>A bare address — no name, no brackets — that mail can actually be sent to.</summary>
    private static bool IsPlainAddress(string value)
        => value.Contains('@') && MailboxAddress.TryParse(value, out var parsed) && parsed.Address == value;

    public OutgoingMessage? GetOutgoing(string id)
    {
        Initialize();
        using var connection = OpenProjection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + OutgoingColumns + " FROM outgoing_messages WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadOutgoing(reader) : null;
    }

    public IReadOnlyList<OutgoingMessage> ListOutgoing(string? status = null, int limit = 100)
    {
        Initialize();
        using var connection = OpenProjection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + OutgoingColumns + " FROM outgoing_messages"
            + (string.IsNullOrWhiteSpace(status) ? "" : " WHERE status=$status")
            + " ORDER BY created_at DESC LIMIT $limit";
        if (!string.IsNullOrWhiteSpace(status)) command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        using var reader = command.ExecuteReader();
        var result = new List<OutgoingMessage>();
        while (reader.Read()) result.Add(ReadOutgoing(reader));
        return result;
    }

    public string DraftFolder(string id)
    {
        using var connection = OpenProjection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT folder_key FROM draft_folders WHERE draft_id=$id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as string ?? LocalFolderKinds.Drafts;
    }

    public IReadOnlyList<OutgoingMessage> ListDraftsInFolder(string? folder, int limit = 500)
    {
        using var connection = OpenProjection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + OutgoingColumns + " FROM outgoing_messages WHERE status='draft'"
            + (folder is null ? "" : " AND COALESCE((SELECT folder_key FROM draft_folders WHERE draft_id=outgoing_messages.id),'drafts')=$folder")
            + " ORDER BY created_at DESC LIMIT $limit";
        if (folder is not null) command.Parameters.AddWithValue("$folder", folder);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        using var reader = command.ExecuteReader();
        var result = new List<OutgoingMessage>();
        while (reader.Read()) result.Add(ReadOutgoing(reader));
        return result;
    }

    /// <summary>Changes only placement: filed drafts remain editable and cannot become queued by a move.</summary>
    public int MoveDrafts(IReadOnlyList<string> ids, string folder)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT kind FROM local_folders WHERE folder_key=$folder";
        command.Parameters.AddWithValue("$folder", folder);
        if (command.ExecuteScalar() is not string kind) throw new KeyNotFoundException("The destination folder no longer exists.");
        if (kind == LocalFolderKinds.Outbox) throw new InvalidOperationException("Moving a draft cannot queue it for sending. Use Send from the composer.");
        var changed = 0;
        command.Parameters.Add("$id", SqliteType.Text);
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            command.Parameters["$id"].Value = id;
            command.CommandText = "SELECT status FROM outgoing_messages WHERE id=$id";
            if (command.ExecuteScalar() as string != OutgoingStates.Draft) throw new InvalidOperationException("Only saved drafts can be filed.");
            command.CommandText = "SELECT folder_key FROM draft_folders WHERE draft_id=$id";
            if ((command.ExecuteScalar() as string ?? LocalFolderKinds.Drafts) == folder) continue;
            command.CommandText = folder == LocalFolderKinds.Drafts ? "DELETE FROM draft_folders WHERE draft_id=$id"
                : "INSERT INTO draft_folders(draft_id,folder_key) VALUES($id,$folder) ON CONFLICT(draft_id) DO UPDATE SET folder_key=excluded.folder_key";
            command.ExecuteNonQuery();
            changed++;
        }
        transaction.Commit();
        return changed;
    }

    public void QueueOutgoing(string id, string rawPath)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE outgoing_messages SET status='outbox',raw_path=$raw,updated_at=$now,next_attempt_at=$now,last_error=NULL
            WHERE id=$id AND status='draft'
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$raw", rawPath);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Only a draft can be queued.");
        command.CommandText = "DELETE FROM draft_folders WHERE draft_id=$id";
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public OutgoingMessage? ClaimNextOutgoing()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE outgoing_messages SET status='sending',updated_at=$now
            WHERE id=(SELECT id FROM outgoing_messages
                      WHERE status='outbox' AND (next_attempt_at IS NULL OR next_attempt_at<=$now)
                      ORDER BY created_at LIMIT 1)
              AND status='outbox'
            RETURNING id,sending_identity_id,to_json,cc_json,bcc_json,subject,body_text,status,raw_path,attempt_count,
                      created_at,updated_at,next_attempt_at,sent_at,last_error,body_html
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadOutgoing(reader) : null;
    }

    public OutgoingMessage ReturnOutgoingToDraft(string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE outgoing_messages SET status='draft',updated_at=$now,next_attempt_at=NULL,last_error=NULL,attempt_count=0
            WHERE id=$id AND status='outbox'
            RETURNING id,sending_identity_id,to_json,cc_json,bcc_json,subject,body_text,status,raw_path,attempt_count,
                      created_at,updated_at,next_attempt_at,sent_at,last_error,body_html
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("The message is no longer waiting in the Outbox. It may already be sending or sent.");
        return ReadOutgoing(reader);
    }

    public OutgoingMessage? ClaimOutgoing(string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE outgoing_messages SET status='sending',updated_at=$now
            WHERE id=$id AND status='outbox'
            RETURNING id,sending_identity_id,to_json,cc_json,bcc_json,subject,body_text,status,raw_path,attempt_count,
                      created_at,updated_at,next_attempt_at,sent_at,last_error,body_html
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadOutgoing(reader) : null;
    }

    internal void RecordSmtpAcceptance(string id)
    {
        // Only queue metadata is involved. A held portable-folder lease must not delay this receipt.
        using var connection = Open(flushFolders: false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE outgoing_messages SET status='sent_unrecorded',sent_at=$now,updated_at=$now,next_attempt_at=NULL,last_error=NULL
            WHERE id=$id AND status='sending'
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void MarkOutgoingSent(string id)
    {
        var now = DateTimeOffset.UtcNow.UtcDateTime.ToString("O");
        using var connection = Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE outgoing_messages SET status='sent',sent_at=coalesce(sent_at,$now),updated_at=$now,next_attempt_at=NULL,last_error=NULL
                WHERE id=$id AND status IN ('sending','sent_unrecorded')
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$now", now);
            if (command.ExecuteNonQuery() == 0)
            {
                command.CommandText = "SELECT status FROM outgoing_messages WHERE id=$id";
                if (command.ExecuteScalar() as string == OutgoingStates.Sent) return;
                throw new InvalidOperationException("Outgoing message was not claimed for sending.");
            }
        }
        // Delivered, so the message it answered or passed on is marked so. Only after the sending is recorded,
        // and never allowed to fail it: a failure here would put delivered mail back in the Outbox to go again.
        try
        {
            using var mark = connection.CreateCommand();
            mark.CommandText = """
                INSERT OR IGNORE INTO message_marks(message_id,kind,marked_at)
                SELECT message_id,kind,$now FROM outgoing_origins WHERE outgoing_id=$id
                """;
            mark.Parameters.AddWithValue("$id", id);
            mark.Parameters.AddWithValue("$now", now);
            mark.ExecuteNonQuery();
            RecordAddressUse(connection, id, now);
        }
        catch (Exception error) when (error is SqliteException or JsonException) { }
    }

    public void MarkOutgoingFailed(string id, string error)
    {
        var current = GetOutgoing(id) ?? throw new KeyNotFoundException("Outgoing message not found.");
        var attempts = current.AttemptCount + 1;
        var delay = TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Min(attempts - 1, 5))));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE outgoing_messages SET status='outbox',attempt_count=$attempts,next_attempt_at=$next,
                updated_at=$now,last_error=$error WHERE id=$id AND status='sending'
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$attempts", attempts);
        command.Parameters.AddWithValue("$next", DateTimeOffset.UtcNow.Add(delay).UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$error", error);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Outgoing message was not claimed for sending.");
    }

    public string? GetSetting(string key)
    {
        using var connection = OpenProjection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key=$key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public bool HasSetting(string key)
    {
        using var connection = OpenProjection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM settings WHERE key=$key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() is not null;
    }

    public void SetSetting(string key, string value)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    // Reads use the last committed projection. They must not start or wait for a
    // global folder writer; startup and write completion own journal recovery.
    private SqliteConnection OpenProjection()
    {
        BulkFolderMigration.CheckAvailable(_paths);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _paths.DatabasePath, Mode = SqliteOpenMode.ReadOnly,
            Pooling = false, DefaultTimeout = 5
        }.ToString());
        try
        {
            connection.Open();
            FolderStorage.CheckProjectionStorage(connection);
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private SqliteConnection Open(bool flushFolders = true)
    {
        BulkFolderMigration.CheckAvailable(_paths);
        if (flushFolders) FolderStorage.Flush();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=" + (FolderStorage.Enabled ? "FULL" : "NORMAL") + "; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    /// <summary>Whether this message was deleted here, from whichever folder: a deletion the reader made is
    /// not to be undone by anything that would bring the message back.</summary>
    public bool HasLocalDeletion(string source, string account, string sourceKey)
    {
        using var connection = OpenProjection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM local_deletions WHERE source=$source AND account=$account AND source_key=$source_key";
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$source_key", sourceKey);
        return command.ExecuteScalar() is not null;
    }

    /// <summary>Every key the reader has deleted from one account, whatever folder it was in at the time.</summary>
    public IReadOnlySet<string> LocalDeletionKeys(string source, string account)
    {
        using var connection = OpenProjection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT source_key FROM local_deletions WHERE source=$source AND account=$account";
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$account", account);
        using var reader = command.ExecuteReader();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) keys.Add(reader.GetString(0));
        return keys;
    }

    private bool IsLocallyDeleted(string source, string account, string folder, string sourceKey)
    {
        using var connection = OpenProjection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM local_deletions WHERE source=$source AND account=$account AND folder=$folder AND source_key=$source_key
            UNION ALL SELECT 1 FROM oe_draft_conversions WHERE source=$source AND account=$account AND folder=$folder AND source_key=$source_key
            UNION ALL SELECT 1 FROM adoption_cleanup WHERE source=$source AND account=$account AND folder=$folder AND source_key=$source_key
            """;
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$folder", folder);
        command.Parameters.AddWithValue("$source_key", sourceKey);
        return command.ExecuteScalar() is not null;
    }

    // No body: a listing shows who, what and when, and nothing here reads the message. Taking even the first
    // 240 characters of body_text made SQLite fetch each message's body from the overflow pages it is spilled
    // across — a thousand scattered reads through a twelve-gigabyte file to show a list that never used them.
    // The two callers that do want a snippet — a search hit and a message opened in full — supply their own.
    private static readonly string MessageColumns = """
        id,source,account,folder,source_key,server_uid,subject,sender,sender_name,
        recipients_json,date_utc,size,is_read,attachment_count
        """;

    private static readonly string QualifiedMessageColumns = """
        m.id,m.source,m.account,m.folder,m.source_key,m.server_uid,m.subject,m.sender,m.sender_name,
        m.recipients_json,m.date_utc,m.size,m.is_read,m.attachment_count
        """;

    private const string OutgoingColumns = """
        id,sending_identity_id,to_json,cc_json,bcc_json,subject,body_text,status,raw_path,attempt_count,
        created_at,updated_at,next_attempt_at,sent_at,last_error,body_html
        """;

    private static OutgoingMessage ReadOutgoing(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1),
        JsonSerializer.Deserialize<string[]>(reader.GetString(2)) ?? [],
        JsonSerializer.Deserialize<string[]>(reader.GetString(3)) ?? [],
        JsonSerializer.Deserialize<string[]>(reader.GetString(4)) ?? [],
        reader.GetString(5), reader.GetString(6), reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetInt32(9),
        DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture),
        ParseDate(reader, 12), ParseDate(reader, 13), reader.IsDBNull(14) ? null : reader.GetString(14),
        reader.IsDBNull(15) ? null : reader.GetString(15));

    private static MessageSummary ReadSummary(SqliteDataReader reader, string? snippet = null)
    {
        var recipients = JsonSerializer.Deserialize<string[]>(reader.GetString(reader.GetOrdinal("recipients_json"))) ?? [];
        var uidOrdinal = reader.GetOrdinal("server_uid");
        var bodySnippet = snippet ?? "";
        return new MessageSummary(
            reader.GetInt64(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("source")),
            reader.GetString(reader.GetOrdinal("account")),
            reader.GetString(reader.GetOrdinal("folder")),
            reader.GetString(reader.GetOrdinal("source_key")),
            reader.IsDBNull(uidOrdinal) ? null : reader.GetInt64(uidOrdinal),
            reader.GetString(reader.GetOrdinal("subject")),
            reader.GetString(reader.GetOrdinal("sender")),
            reader.GetString(reader.GetOrdinal("sender_name")),
            recipients,
            ParseDate(reader, reader.GetOrdinal("date_utc")),
            reader.GetInt64(reader.GetOrdinal("size")),
            reader.GetInt32(reader.GetOrdinal("is_read")) != 0,
            reader.GetInt32(reader.GetOrdinal("attachment_count")),
            bodySnippet);
    }

    private static DateTimeOffset? ParseDate(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);

    private static void AddMessageParameters(SqliteCommand command, MessageInput input)
    {
        command.Parameters.AddWithValue("$source", input.Source);
        command.Parameters.AddWithValue("$account", input.Account);
        command.Parameters.AddWithValue("$folder", input.Folder);
        command.Parameters.AddWithValue("$source_key", input.SourceKey);
        command.Parameters.AddWithValue("$server_uid", (object?)input.ServerUid ?? DBNull.Value);
        command.Parameters.AddWithValue("$uidvalidity", input.UidValidity);
        command.Parameters.AddWithValue("$subject", input.Subject);
        command.Parameters.AddWithValue("$sender", input.Sender);
        command.Parameters.AddWithValue("$sender_name", input.SenderName);
        command.Parameters.AddWithValue("$recipients", JsonSerializer.Serialize(input.Recipients));
        command.Parameters.AddWithValue("$date", input.Date?.UtcDateTime.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$size", input.Size);
        command.Parameters.AddWithValue("$is_read", input.IsRead ? 1 : 0);
        command.Parameters.AddWithValue("$attachments", input.AttachmentCount);
        command.Parameters.AddWithValue("$body_text", input.BodyText);
        command.Parameters.AddWithValue("$body_html", (object?)input.BodyHtml ?? DBNull.Value);
        command.Parameters.AddWithValue("$raw_path", (object?)input.RawPath ?? DBNull.Value);
    }

    private static string ToFtsQuery(string query)
        => string.Join(" AND ", query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(term => '"' + term.Replace("\"", "\"\"") + '"'));

    private static string Truncate(string? value, int max) => string.IsNullOrEmpty(value) ? "" : value[..Math.Min(value.Length, max)];
    private static string? TruncateNullable(string? value, int max) => string.IsNullOrEmpty(value) ? null : value[..Math.Min(value.Length, max)];
    /// <summary>One read of the message on disk for everything that comes from it: what it carries, and the
    /// letter in whichever form it was written. A message too damaged to parse is not a message that should
    /// refuse to open — what was written down when it arrived is shown instead.</summary>
    private static (IReadOnlyList<AttachmentInfo> Attachments, string? Text, string? Html, IReadOnlyList<string> ReplyTo) ReadRaw(string rawPath)
    {
        try { using var stream = File.OpenRead(rawPath); return ReadRaw(stream); }
        catch (IOException) { return ([], null, null, []); }
    }

    private static (IReadOnlyList<AttachmentInfo> Attachments, string? Text, string? Html, IReadOnlyList<string> ReplyTo) ReadRaw(Stream stream)
    {
        try
        {
            using var message = MimeMessage.Load(stream);
            return (ReadAttachments(message), message.TextBody, MailHtml.WithInlineImages(message),
                message.ReplyTo.Mailboxes.Select(value => value.ToString()).ToArray());
        }
        catch (Exception exception) when (exception is FormatException or IOException or OutOfMemoryException)
        {
            return ([], null, null, []);
        }
    }

    /// <summary>The reading-pane content of a message held outside this database (a folder file), built by the
    /// same reading of the raw message as GetMessage, so it shows exactly as a stored message would.</summary>
    public static MessageContent ContentFromRaw(MessageSummary summary, string rawPath)
    {
        var (attachments, rawText, rawHtml, replyTo) = ReadRaw(rawPath);
        var bodyText = !string.IsNullOrWhiteSpace(rawText) ? rawText : rawHtml is null ? "" : MailText.FromHtml(rawHtml);
        return new MessageContent(summary with { Snippet = bodyText[..Math.Min(240, bodyText.Length)] }, bodyText, rawHtml,
            rawPath, attachments) { ReplyTo = replyTo };
    }

    public static string ExtractAttachmentFromRaw(string rawPath, int attachmentIndex, string rootDirectory)
    {
        using var message = MimeMessage.Load(rawPath);
        return WriteAttachment(message, attachmentIndex, rootDirectory);
    }

    public string ExtractAttachment(long messageId, int attachmentIndex)
    {
        using var connection = OpenProjection();
        var id = AdoptionCleanup.ResolveId(connection, messageId);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT raw_path FROM messages WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var row = command.ExecuteReader();
        if (!row.Read()) throw new KeyNotFoundException("The message no longer exists.");
        if (row.IsDBNull(0)) throw new InvalidOperationException("The original message is not available.");
        using var stream = _blobs.OpenRead(row.GetString(0));
        using var message = MimeMessage.Load(stream);
        return WriteAttachment(message, attachmentIndex, Path.Combine(_paths.PreviewDirectory, "attachments", messageId.ToString()));
    }

    public IReadOnlyList<AttachmentInfo> GetOutgoingAttachments(string id)
    {
        var outgoing = GetOutgoing(id) ?? throw new KeyNotFoundException("The draft no longer exists.");
        if (outgoing.RawPath is null) return [];
        using var message = MimeMessage.Load(_blobs.FullPath(outgoing.RawPath));
        return ReadAttachments(message);
    }

    public string ExtractOutgoingAttachment(string id, int attachmentIndex)
    {
        var outgoing = GetOutgoing(id) ?? throw new KeyNotFoundException("The draft no longer exists.");
        if (outgoing.RawPath is null) throw new InvalidOperationException("The draft has no attachments.");
        using var message = MimeMessage.Load(_blobs.FullPath(outgoing.RawPath));
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id)));
        return WriteAttachment(message, attachmentIndex, Path.Combine(_paths.PreviewDirectory, "outgoing", key));
    }

    private static string WriteAttachment(MimeMessage message, int attachmentIndex, string rootDirectory)
    {
        var parts = AttachmentParts(message).ToArray();
        if (attachmentIndex < 0 || attachmentIndex >= parts.Length)
            throw new ArgumentOutOfRangeException(nameof(attachmentIndex));
        var entity = parts[attachmentIndex];
        var directory = Path.Combine(rootDirectory, attachmentIndex.ToString());
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, AttachmentFileName(entity));
        using (var output = File.Create(destination))
        {
            if (entity is MimePart { Content: { } mimeContent }) mimeContent.DecodeTo(output);
            else if (entity is MessagePart { Message: { } attachedMessage }) attachedMessage.WriteTo(output);
            else throw new InvalidDataException("This attachment type cannot be saved.");
        }
        AttachmentSafety.MarkInternetZone(destination);
        return destination;
    }

    /// <summary>Copies the pictures out of a set of messages into one folder, oldest mail first. Each picture is
    /// named after the day its message arrived and its own name, and dated as that message is. The same picture
    /// sent again and again — a logo, a signature — is copied once. Pictures already in the folder from an
    /// earlier export are recognised and not copied twice, so an export can be run again to pick up new mail.</summary>
    public PictureExportResult ExportPictures(PictureExportRequest request, IProgress<PictureExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(request.Destination);
        var messages = new List<(long Id, string RawPath, DateTimeOffset? Date, bool HasScanRow, List<(string PartPath, string FileName, bool IsInline, string? Sha256, int ByteSize)> Parts)>();
        
        using (var catalogConn = new SqliteConnection($"Data Source={_paths.SearchCatalogPath};Pooling=False"))
        {
            catalogConn.Open();
            AttachmentCatalogIndexer.EnsureSchema(catalogConn);
        }

        using (var connection = OpenProjection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"ATTACH DATABASE '{_paths.SearchCatalogPath}' AS catalog";
            command.ExecuteNonQuery();

            var where = new List<string> { "m.raw_path IS NOT NULL" };
            if (!request.IncludeInline) where.Add("m.attachment_count > 0");
            if (request.LocalFolders is { } local)
            {
                where.Add("m.source=$live AND m.folder IN (SELECT value FROM json_each($folders))");
                command.Parameters.AddWithValue("$live", MailContract.LiveSource);
                command.Parameters.AddWithValue("$folders", JsonSerializer.Serialize(local));
            }
            else if (request.ArchiveFolder is { } archive)
            {
                where.Add("m.source=$archive AND (m.folder=$path" + (request.IncludeSubfolders ? " OR m.folder LIKE $prefix ESCAPE '!')" : ")"));
                command.Parameters.AddWithValue("$archive", MailContract.ArchiveSource);
                command.Parameters.AddWithValue("$path", archive);
                command.Parameters.AddWithValue("$prefix", archive.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_") + "/%");
            }
            command.CommandText = "SELECT m.id, m.raw_path, m.date_utc, " +
                "s.raw_blob_hash, o.part_path, o.filename, o.declared_media_type, o.is_inline, a.sha256, a.byte_size " +
                "FROM messages m " +
                "LEFT JOIN catalog.raw_blob_scans s ON s.raw_path = m.raw_path " +
                "LEFT JOIN catalog.attachment_occurrences o ON o.raw_path = m.raw_path " +
                "LEFT JOIN catalog.attachments a ON a.id = o.attachment_id " +
                "WHERE " + string.Join(" AND ", where) + " ORDER BY m.date_utc, m.id";
            using var reader = command.ExecuteReader();
            long lastId = -1;
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                if (lastId != id)
                {
                    var dateStr = reader.IsDBNull(2) ? null : reader.GetString(2);
                    var date = dateStr != null && DateTimeOffset.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : (DateTimeOffset?)null;
                    messages.Add((id, reader.GetString(1), date, !reader.IsDBNull(3), new()));
                    lastId = id;
                }
                if (!reader.IsDBNull(4))
                {
                    var mediaType = reader.GetString(6);
                    var fileName = reader.GetString(5);
                    if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !mediaType.Contains("svg", StringComparison.OrdinalIgnoreCase)
                        || PictureExtensions.Contains(Path.GetExtension(fileName).ToLowerInvariant()))
                    {
                        messages[^1].Parts.Add((
                            reader.GetString(4),
                            fileName,
                            reader.GetInt32(7) != 0,
                            reader.IsDBNull(8) ? null : reader.GetString(8),
                            reader.IsDBNull(9) ? 0 : reader.GetInt32(9)
                        ));
                    }
                }
            }
        }
        // What is already there, by content, so a second run adds only what is new.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in Directory.EnumerateFiles(request.Destination))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { using var stream = File.OpenRead(existing); seen.Add(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream))); }
            catch (IOException) { }
        }
        int written = 0, duplicates = 0, small = 0, unreadable = 0;
        for (var index = 0; index < messages.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (_, raw, date, hasScanRow, parts) = messages[index];
            try
            {
                if (request.IncludeInline || !hasScanRow)
                {
                    using var rawStream = _blobs.OpenRead(raw);
                    using var message = MimeMessage.Load(rawStream, cancellationToken);
                    foreach (var part in message.BodyParts.OfType<MimePart>())
                    {
                        if (!IsPicture(part)) continue;
                        var inline = !part.IsAttachment;
                        if (inline && !request.IncludeInline) continue;
                        using var buffer = new MemoryStream();
                        part.Content?.DecodeTo(buffer, cancellationToken);
                        if (buffer.Length == 0) continue;
                        if (buffer.Length < request.MinimumBytes) { small++; continue; }
                        var bytes = buffer.ToArray();
                        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
                        if (!seen.Add(hash)) { duplicates++; continue; }
                        var target = UniquePath(request.Destination, PictureName(part, date));
                        File.WriteAllBytes(target, bytes);
                        if (date is { } when) { File.SetCreationTime(target, when.LocalDateTime); File.SetLastWriteTime(target, when.LocalDateTime); }
                        written++;
                    }
                }
                else
                {
                    if (parts.Count == 0) continue;
                    var neededParts = new List<(string PartPath, string FileName, bool IsInline, string? Sha256, int ByteSize)>();
                    foreach (var part in parts)
                    {
                        if (part.IsInline && !request.IncludeInline) continue;
                        if (part.Sha256 != null && seen.Contains(part.Sha256)) { duplicates++; continue; }
                        if (part.ByteSize < request.MinimumBytes) { small++; continue; }
                        neededParts.Add(part);
                    }
                    if (neededParts.Count == 0) continue;
                    
                    using var rawStream = _blobs.OpenRead(raw);
                    using var message = MimeMessage.Load(rawStream, cancellationToken);
                    var indexedParts = AttachmentCatalogIndexer.IndexedParts(message).ToList();
                    foreach (var needed in neededParts)
                    {
                        var partIndex = int.Parse(needed.PartPath, CultureInfo.InvariantCulture) - 1;
                        if (partIndex < 0 || partIndex >= indexedParts.Count) continue;
                        if (indexedParts[partIndex] is not MimePart entity) continue;
                        
                        using var buffer = new MemoryStream();
                        entity.Content?.DecodeTo(buffer, cancellationToken);
                        if (buffer.Length == 0) continue;
                        var bytes = buffer.ToArray();
                        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
                        if (needed.Sha256 != null && hash != needed.Sha256) continue;
                        if (!seen.Add(hash)) { duplicates++; continue; }
                        var target = UniquePath(request.Destination, PictureName(entity, date));
                        File.WriteAllBytes(target, bytes);
                        if (date is { } when) { File.SetCreationTime(target, when.LocalDateTime); File.SetLastWriteTime(target, when.LocalDateTime); }
                        written++;
                    }
                }
            }
            catch (Exception error) when (error is FormatException or IOException or InvalidDataException or UnauthorizedAccessException or MimeKit.ParseException)
            {
                unreadable++;
            }
            progress?.Report(new PictureExportProgress(index + 1, messages.Count, written));
        }
        return new PictureExportResult(messages.Count, written, duplicates, small, unreadable);
    }

    private static readonly string[] PictureExtensions = [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".heif"];

    private static bool IsPicture(MimePart part)
        => part.ContentType.MediaType.Equals("image", StringComparison.OrdinalIgnoreCase) && !part.ContentType.MediaSubtype.Contains("svg", StringComparison.OrdinalIgnoreCase)
           || PictureExtensions.Contains(Path.GetExtension(part.FileName ?? "").ToLowerInvariant());

    /// <summary>"2026-09-14 holiday.jpg": the day first, so the folder sorts by when the mail came.</summary>
    private static string PictureName(MimePart part, DateTimeOffset? date)
    {
        var name = AttachmentFileName(part);
        if (name == "attachment" || Path.GetExtension(name).Length == 0)
            name = Path.GetFileNameWithoutExtension(name) + "." + (part.ContentType.MediaSubtype.ToLowerInvariant() switch { "jpeg" or "pjpeg" => "jpg", var other => other });
        return (date is { } when ? when.LocalDateTime.ToString("yyyy-MM-dd ", CultureInfo.InvariantCulture) : "") + name;
    }

    private static string UniquePath(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var copy = 2; File.Exists(path); copy++) path = Path.Combine(folder, $"{stem} ({copy}){extension}");
        return path;
    }

    private static IEnumerable<MimeEntity> AttachmentParts(MimeMessage message)
        => message.BodyParts.Where(part => part.IsAttachment || !string.IsNullOrWhiteSpace(part.ContentId));

    private static string AttachmentFileName(MimeEntity part)
    {
        var original = part.ContentDisposition?.FileName ?? part.ContentType.Name
            ?? (part is MessagePart ? "message.eml" : "attachment");
        return AttachmentSafety.SafeFileName(original);
    }

    private static IReadOnlyList<AttachmentInfo> ReadAttachments(MimeMessage message)
    {
        return AttachmentParts(message)
            .Select(p => new AttachmentInfo(
                AttachmentFileName(p),
                p.ContentType.MimeType,
                p is MimePart part ? part.Content?.Stream?.Length ?? 0 : 0,
                !string.IsNullOrWhiteSpace(p.ContentId) && !p.IsAttachment))
            .ToArray();
    }
}

public sealed record MessageInput(
    string Source,
    string Account,
    string Folder,
    string SourceKey,
    long? ServerUid,
    uint UidValidity,
    string Subject,
    string Sender,
    string SenderName,
    IReadOnlyList<string> Recipients,
    DateTimeOffset? Date,
    long Size,
    bool IsRead,
    int AttachmentCount,
    string BodyText,
    string? BodyHtml,
    string? RawPath);

/// <summary>Which pictures to copy out, and where to. No folder at all means all mail.</summary>
public sealed record PictureExportRequest(string Destination, IReadOnlyCollection<string>? LocalFolders = null,
    string? ArchiveFolder = null, bool IncludeSubfolders = true, bool IncludeInline = false, long MinimumBytes = 10 * 1024);

public readonly record struct PictureExportProgress(int Messages, int Total, int Written);

public sealed record PictureExportResult(int Messages, int Written, int Duplicates, int Small, int Unreadable);
