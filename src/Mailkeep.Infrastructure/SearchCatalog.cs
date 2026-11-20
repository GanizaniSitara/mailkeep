using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mailkeep.Core;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Infrastructure;

public sealed record CatalogDocument(
    string Source,
    string Account,
    string Folder,
    string SourceKey,
    long? LocalId,
    string Subject,
    string Sender,
    string SenderName,
    IReadOnlyList<string> Recipients,
    DateTimeOffset? Date,
    long Size,
    bool IsRead,
    int AttachmentCount,
    string BodyText,
    string? RfcMessageId = null,
    string? RawPath = null);

public sealed record CatalogStatus(string Source, string Generation, int Count, DateTimeOffset IndexedAt);
public sealed record CatalogLocator(MessageLocation Location, long? LocalId);

/// <summary>How far indexing has got. A sync reports every folder it reads and then hands over to the index,
/// which said nothing at all until it finished — half an hour of silence that reads as a hang. Total is zero
/// when the work is not countable in advance.</summary>
public sealed record CatalogProgress(string Stage, int Done, int Total);

public sealed partial class SearchCatalog
{
    private sealed record CatalogHit(string Fingerprint, MessageSummary Summary, double Rank);

    private const string Schema = """
        PRAGMA foreign_keys = ON;
        CREATE TABLE IF NOT EXISTS documents (
            id               INTEGER PRIMARY KEY AUTOINCREMENT,
            message_ref      TEXT NOT NULL UNIQUE,
            source           TEXT NOT NULL,
            account          TEXT NOT NULL,
            folder           TEXT NOT NULL,
            source_key       TEXT NOT NULL,
            local_id         INTEGER,
            fingerprint      TEXT NOT NULL,
            rfc_message_id   TEXT,
            subject          TEXT NOT NULL DEFAULT '',
            sender           TEXT NOT NULL DEFAULT '',
            sender_name      TEXT NOT NULL DEFAULT '',
            recipients_json  TEXT NOT NULL DEFAULT '[]',
            date_utc         TEXT,
            size             INTEGER NOT NULL DEFAULT 0,
            is_read          INTEGER NOT NULL DEFAULT 0,
            attachment_count INTEGER NOT NULL DEFAULT 0,
            body_text        TEXT NOT NULL DEFAULT ''
        );
        CREATE VIRTUAL TABLE IF NOT EXISTS documents_fts USING fts5(
            subject, sender, recipients, body_text,
            tokenize='unicode61 remove_diacritics 2'
        );
        CREATE TABLE IF NOT EXISTS source_state (
            source      TEXT PRIMARY KEY,
            generation  TEXT NOT NULL,
            count       INTEGER NOT NULL,
            indexed_at  TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_catalog_source ON documents(source);
        CREATE INDEX IF NOT EXISTS idx_catalog_fingerprint ON documents(fingerprint);
        CREATE INDEX IF NOT EXISTS idx_catalog_local_id ON documents(local_id);
        -- Every incremental pass finds its rows by source and store id: one per message read elsewhere, one
        -- batch per message gone. Without this the only usable index is on source alone, which for the archive
        -- is a quarter of a million rows scanned for each of them - the reason a sync of five thousand read
        -- flags ran for an hour saying nothing.
        CREATE INDEX IF NOT EXISTS idx_catalog_local ON documents(source, local_id);
        """;

    private readonly AppPaths _paths;
    private readonly AccountStore _accounts;
    private readonly AttachmentCatalogIndexer _attachments;

    public SearchCatalog(AppPaths paths, AccountStore accounts)
    {
        _paths = paths;
        _accounts = accounts;
        _attachments = new AttachmentCatalogIndexer(paths);
    }

    public void Initialize() => InitializeCore(30);

    private void InitializeCore(int timeout)
    {
        BulkFolderMigration.CheckAvailable(_paths);
        _paths.EnsureDirectories();
        using var connection = Open(timeout);
        using var command = connection.CreateCommand();
        command.CommandText = Schema;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "documents", "raw_blob_hash", "TEXT");
        EnsureColumn(connection, "documents", "raw_path", "TEXT");
        using (var attachmentSchema = connection.CreateCommand())
        {
            attachmentSchema.CommandText = "CREATE INDEX IF NOT EXISTS idx_documents_raw_blob ON documents(raw_blob_hash)";
            attachmentSchema.ExecuteNonQuery();
        }
        AttachmentCatalogIndexer.EnsureSchema(connection);
        // Catalogs written before imported mail was named generically still hold rows under the old source.
        // Leaving them would double every archived hit in search results.
        using var legacy = connection.CreateCommand();
        legacy.CommandText = """
            SELECT EXISTS(SELECT 1 FROM documents WHERE source=$legacy)
                OR EXISTS(SELECT 1 FROM source_state WHERE source=$legacy)
            """;
        legacy.Parameters.AddWithValue("$legacy", MailContract.LegacyArchiveSource);
        // A no-op DELETE still acquires SQLite's writer lock. Current catalogs
        // need no migration, so searches can read while an index write is pending.
        if (Convert.ToInt64(legacy.ExecuteScalar()) == 0) return;
        legacy.CommandText = """
            DELETE FROM documents_fts WHERE rowid IN (SELECT id FROM documents WHERE source=$legacy);
            DELETE FROM documents WHERE source=$legacy;
            DELETE FROM source_state WHERE source=$legacy;
            """;
        legacy.ExecuteNonQuery();
    }

    /// <summary>Indexes archived mail from this store, the same way live mail is indexed. Archived messages are
    /// ordinary messages that happen to be read-only; nothing outside Mailkeep needs to be present to search them.
    /// </summary>
    public int SyncArchive(bool force = false, Action<CatalogProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Initialize();
        if (!File.Exists(_paths.DatabasePath)) return 0;
        using var source = new SqliteConnection($"Data Source={_paths.DatabasePath};Mode=ReadOnly;Pooling=False");
        source.Open();
        using var cancellation = InterruptOnCancellation(source, cancellationToken);
        using var generationCommand = source.CreateCommand();
        // How many, how new, and how many have been read: enough to notice mail arriving, mail leaving, and
        // mail read in the other client, which is everything a sync can do to the archive. Counted from the
        // index rather than the table, so noticing costs a fraction of a second on a quarter of a million.
        generationCommand.CommandText = "SELECT COUNT(*),COALESCE(MAX(id),0),COALESCE(SUM(is_read),0) FROM messages WHERE source=$source AND converted_draft_id IS NULL";
        generationCommand.Parameters.AddWithValue("$source", MailContract.ArchiveSource);
        using var generationReader = generationCommand.ExecuteReader();
        generationReader.Read();
        var generation = $"{generationReader.GetInt64(0)}:{generationReader.GetInt64(1)}:{generationReader.GetInt64(2)}";
        generationReader.Close();
        if (!force && GetState(MailContract.ArchiveSource)?.Generation == generation) return 0;
        return force
            ? ReplaceArchive(ReadArchive(source, null, cancellationToken), generation, progress, cancellationToken)
            : UpdateArchive(source, generation, progress, cancellationToken);
    }

    /// <summary>Indexes what has changed since the last pass, rather than the archive over again.
    ///
    /// The generation only says whether the archive has changed, not how much of it, so a single new message
    /// used to mean rebuilding the lot: a quarter of a million messages read out with their bodies, held in
    /// memory — four gigabytes of it — and written back into the index one at a time. A sync that followed
    /// twenty minutes of that, having done its own work in fifty seconds.
    ///
    /// What actually changes between two syncs is: messages that arrived, messages that left, and messages
    /// read elsewhere. Each is found by comparing identifiers and read flags — no bodies are read except for
    /// the messages being added.</summary>
    private int UpdateArchive(SqliteConnection source, string generation, Action<CatalogProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Invoke(new CatalogProgress("comparing", 0, 0));
        var present = new Dictionary<long, bool>();
        using (var command = source.CreateCommand())
        {
            command.CommandText = "SELECT id,is_read FROM messages WHERE source=$source AND converted_draft_id IS NULL";
            command.Parameters.AddWithValue("$source", MailContract.ArchiveSource);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                present[reader.GetInt64(0)] = reader.GetInt32(1) != 0;
            }
        }

        var indexed = new Dictionary<long, bool>();
        using var connection = Open();
        cancellationToken.ThrowIfCancellationRequested();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT local_id,is_read FROM documents WHERE source=$source AND local_id IS NOT NULL";
            command.Parameters.AddWithValue("$source", MailContract.ArchiveSource);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                indexed[reader.GetInt64(0)] = reader.GetInt32(1) != 0;
            }
        }

        var gone = indexed.Keys.Where(id => !present.ContainsKey(id)).ToArray();
        var fresh = present.Keys.Where(id => !indexed.ContainsKey(id)).ToArray();
        var reread = present.Where(entry => indexed.TryGetValue(entry.Key, out var was) && was != entry.Value)
            .Select(entry => entry.Key).ToArray();

        using var transaction = connection.BeginTransaction();
        var removed = 0;
        foreach (var batch in Batches(gone))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Run(connection, transaction,
                $"DELETE FROM documents_fts WHERE rowid IN (SELECT id FROM documents WHERE source=$source AND local_id IN ({batch}))");
            Run(connection, transaction, $"DELETE FROM documents WHERE source=$source AND local_id IN ({batch})");
            removed = Math.Min(gone.Length, removed + 400);
            progress?.Invoke(new CatalogProgress("removing", removed, gone.Length));
        }
        // A message read in the other client is read here too, and that is the whole of what changed about it.
        var followed = 0;
        foreach (var id in reread)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE documents SET is_read=$read WHERE source=$source AND local_id=$id";
            command.Parameters.AddWithValue("$read", present[id] ? 1 : 0);
            command.Parameters.AddWithValue("$source", MailContract.ArchiveSource);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
            if (++followed % 500 == 0) progress?.Invoke(new CatalogProgress("read state", followed, reread.Length));
        }
        if (followed > 0) progress?.Invoke(new CatalogProgress("read state", followed, reread.Length));
        var added = 0;
        foreach (var document in ReadArchive(source, fresh, cancellationToken))
        {
            Insert(connection, transaction, document);
            if (++added % 250 == 0) progress?.Invoke(new CatalogProgress("indexing", added, fresh.Length));
        }
        progress?.Invoke(new CatalogProgress("indexing", added, fresh.Length));
        // Writing the transaction out is itself minutes of work on an index this size, and saying so is the
        // difference between waiting and wondering.
        progress?.Invoke(new CatalogProgress("committing", added, fresh.Length));
        cancellationToken.ThrowIfCancellationRequested();
        SetState(connection, transaction, MailContract.ArchiveSource, generation, present.Count);
        transaction.Commit();
        return added;
    }

    /// <summary>The archived messages, or only the ones asked for. Reading a body is the expensive part, so
    /// this is never asked for more than is being indexed.</summary>
    private static IEnumerable<CatalogDocument> ReadArchive(SqliteConnection source, IReadOnlyCollection<long>? ids,
        CancellationToken cancellationToken = default)
    {
        const string Columns = """
            SELECT id,account,folder,source_key,subject,sender,sender_name,recipients_json,date_utc,
                   size,is_read,attachment_count,body_text,raw_path
            FROM messages WHERE source=$source AND converted_draft_id IS NULL
            """;
        foreach (var batch in ids is null ? [null] : Batches(ids).Cast<string?>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var command = source.CreateCommand();
            command.CommandText = batch is null ? Columns : Columns + $" AND id IN ({batch})";
            command.Parameters.AddWithValue("$source", MailContract.ArchiveSource);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var recipients = JsonSerializer.Deserialize<string[]>(reader.GetString(7)) ?? [];
                yield return new CatalogDocument(MailContract.ArchiveSource, reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetInt64(0), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                    recipients, ParseDate(reader, 8), reader.GetInt64(9), reader.GetInt32(10) != 0, reader.GetInt32(11),
                    reader.GetString(12), RawPath: reader.IsDBNull(13) ? null : reader.GetString(13));
            }
        }
    }

    /// <summary>Identifiers as comma-separated lists short enough for one statement. They are the store's own
    /// row ids, so they go into the text of the statement rather than being bound one parameter each.</summary>
    private static IEnumerable<string> Batches(IReadOnlyCollection<long> ids, int size = 400)
    {
        for (var taken = 0; taken < ids.Count; taken += size)
            yield return string.Join(',', ids.Skip(taken).Take(size));
    }

    private static void Run(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$source", MailContract.ArchiveSource);
        command.ExecuteNonQuery();
    }

    public int ReplaceArchive(IEnumerable<CatalogDocument> documents, string generation,
        Action<CatalogProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Initialize();
        if (GetState(MailContract.ArchiveSource)?.Generation == generation) return 0;
        return ReplaceSources([MailContract.ArchiveSource],
            documents.Select(document => document with { Source = MailContract.ArchiveSource }),
            MailContract.ArchiveSource, generation, progress, cancellationToken);
    }

    public SearchEnvelope Search(string query, string source = "all", int limit = 20,
        string? folder = null, string? account = null, SearchFilters? filters = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSenderFilter(filters?.Sender);
        Initialize();
        if (!string.IsNullOrWhiteSpace(filters?.Sender)) EnsureSenderIndex(cancellationToken);
        var translated = FtsQuery.Parse(query);
        folder ??= translated.Folder;
        account ??= translated.Account;
        var hasScope = folder is not null || account is not null || filters?.After is not null
            || filters?.Before is not null || filters?.RequireAttachment == true || HeaderMatch(filters).Length > 0
            || filters?.FolderKeys is not null || !string.IsNullOrWhiteSpace(filters?.Sender);
        if (string.IsNullOrWhiteSpace(query) && !hasScope) return new SearchEnvelope(query, 0, false, []);
        // Scope-only criteria do not need an FTS match expression.
        var match = translated.Match.Length > 0 || hasScope ? translated.Match : FtsQuery.Fallback(query);
        try
        {
            return Search(match, query, source, limit, folder, account, filters, cancellationToken);
        }
        catch (SqliteException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The translation displeased FTS5 — a stray operator, an exotic token. A dumber search that
            // definitely runs beats a syntax error shown to someone who just typed words.
            return Search(FtsQuery.Fallback(query), query, source, limit, folder, account, filters, cancellationToken);
        }
    }

    private SearchEnvelope Search(string match, string query, string source, int limit, string? folder,
        string? account, SearchFilters? filters, CancellationToken cancellationToken)
    {
        using var connection = Open();
        using var cancellation = InterruptOnCancellation(connection, cancellationToken);
        var sourceClause = SourceClause(source);
        var scopeClause = ScopeClause(folder, account, filters);
        var scopeOnly = match.Length == 0;
        var matchScope = scopeOnly ? SearchMatchScope.Message : filters?.MatchScope ?? SearchMatchScope.All;
        var attachmentMatch = AttachmentMatchExpression(match);
        var countParts = new List<string>();
        if (matchScope.HasFlag(SearchMatchScope.Message))
            countParts.Add((scopeOnly ? "SELECT d.fingerprint FROM documents d WHERE 1=1"
                : "SELECT d.fingerprint FROM documents_fts f JOIN documents d ON d.id=f.rowid WHERE documents_fts MATCH $query") + sourceClause + scopeClause);
        if (attachmentMatch.Length > 0 && matchScope.HasFlag(SearchMatchScope.AttachmentName))
            countParts.Add("SELECT d.fingerprint FROM attachment_names_fts f JOIN attachment_occurrences o ON o.id=f.rowid JOIN documents d ON d.raw_blob_hash=o.raw_blob_hash WHERE attachment_names_fts MATCH $attachment_query" + sourceClause + scopeClause);
        if (attachmentMatch.Length > 0 && matchScope.HasFlag(SearchMatchScope.AttachmentContent))
            countParts.Add("SELECT d.fingerprint FROM attachment_text_fts f JOIN attachments a ON a.id=f.rowid JOIN attachment_occurrences o ON o.attachment_id=a.id JOIN documents d ON d.raw_blob_hash=o.raw_blob_hash WHERE attachment_text_fts MATCH $attachment_query" + sourceClause + scopeClause);
        if (countParts.Count == 0) return new SearchEnvelope(query, 0, false, []);
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(DISTINCT fingerprint) FROM (" + string.Join(" UNION ALL ", countParts) + ")";
        count.Parameters.AddWithValue("$query", match);
        count.Parameters.AddWithValue("$attachment_query", attachmentMatch);
        AddSourceParameter(count, source);
        AddScopeParameters(count, folder, account, filters);
        var total = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        cancellationToken.ThrowIfCancellationRequested();

        var hits = new List<CatalogHit>();
        var rowLimit = Math.Clamp(limit, 1, 500) * 6;
        if (matchScope.HasFlag(SearchMatchScope.Message))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var command = SearchCommand(connection, (scopeOnly
                ? """
                SELECT d.message_ref,d.source,d.account,d.folder,d.source_key,d.local_id,d.fingerprint,
                       d.subject,d.sender,d.sender_name,d.recipients_json,d.date_utc,d.size,d.is_read,d.attachment_count,
                       '' AS result_snippet,0 AS rank
                FROM documents d WHERE 1=1
                """
                : """
                SELECT d.*,snippet(documents_fts,3,'','','…',24) AS result_snippet,bm25(documents_fts) AS rank
                FROM documents_fts f JOIN documents d ON d.id=f.rowid
                WHERE documents_fts MATCH $query
                """) + sourceClause + scopeClause + " ORDER BY rank,d.date_utc DESC LIMIT $limit",
                match, attachmentMatch, source, folder, account, filters, rowLimit);
            using var reader = command.ExecuteReader();
            while (reader.Read()) hits.Add(new CatalogHit(reader.GetString(reader.GetOrdinal("fingerprint")),
                ReadSummary(reader, reader.GetString(reader.GetOrdinal("result_snippet"))), reader.GetDouble(reader.GetOrdinal("rank")) - 0.15));
        }
        if (attachmentMatch.Length > 0 && matchScope.HasFlag(SearchMatchScope.AttachmentName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var command = SearchCommand(connection, """
                SELECT d.*,o.id AS occurrence_id,o.filename,o.declared_media_type,
                       snippet(attachment_names_fts,0,'','','…',24) AS result_snippet,
                       0 AS text_was_truncated,bm25(attachment_names_fts) AS rank
                FROM attachment_names_fts f JOIN attachment_occurrences o ON o.id=f.rowid
                JOIN documents d ON d.raw_blob_hash=o.raw_blob_hash
                WHERE attachment_names_fts MATCH $attachment_query
                """ + sourceClause + scopeClause + " ORDER BY rank,d.date_utc DESC LIMIT $limit",
                match, attachmentMatch, source, folder, account, filters, rowLimit);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var attachment = ReadAttachmentMatch(reader, "filename");
                hits.Add(new CatalogHit(reader.GetString(reader.GetOrdinal("fingerprint")),
                    ReadSummary(reader, $"From attachment: {attachment.FileName}", attachment), reader.GetDouble(reader.GetOrdinal("rank")) - 0.05));
            }
        }
        if (attachmentMatch.Length > 0 && matchScope.HasFlag(SearchMatchScope.AttachmentContent))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var command = SearchCommand(connection, """
                SELECT d.*,o.id AS occurrence_id,o.filename,a.detected_media_type AS declared_media_type,
                       snippet(attachment_text_fts,0,'','','…',24) AS result_snippet,
                       a.text_was_truncated,bm25(attachment_text_fts) AS rank
                FROM attachment_text_fts f JOIN attachments a ON a.id=f.rowid
                JOIN attachment_occurrences o ON o.attachment_id=a.id
                JOIN documents d ON d.raw_blob_hash=o.raw_blob_hash
                WHERE attachment_text_fts MATCH $attachment_query
                """ + sourceClause + scopeClause + " ORDER BY rank,d.date_utc DESC LIMIT $limit",
                match, attachmentMatch, source, folder, account, filters, rowLimit);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var attachment = ReadAttachmentMatch(reader, "content");
                hits.Add(new CatalogHit(reader.GetString(reader.GetOrdinal("fingerprint")),
                    ReadSummary(reader, $"From attachment: {attachment.FileName} — {attachment.Snippet}", attachment),
                    reader.GetDouble(reader.GetOrdinal("rank"))));
            }
        }
        var results = hits.GroupBy(hit => hit.Fingerprint).Select(group =>
        {
            var best = group.OrderBy(hit => hit.Rank).ThenBy(hit => SourcePriority(hit.Summary.Source)).First();
            var attachmentCount = group.Where(hit => hit.Summary.AttachmentMatch is not null)
                .Select(hit => hit.Summary.AttachmentMatch!.OccurrenceId).Distinct().Count();
            var primary = best.Summary;
            if (primary.AttachmentMatch is { } attachment)
                primary = primary with { AttachmentMatch = attachment with { MatchingAttachmentCount = attachmentCount } };
            return new
            {
                Rank = group.Min(hit => hit.Rank),
                Summary = primary with { Locations = group.SelectMany(hit => hit.Summary.Locations ?? []).Distinct().ToArray() }
            };
        }).OrderBy(result => result.Rank).ThenByDescending(result => result.Summary.Date).Take(limit).Select(result => result.Summary).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return new SearchEnvelope(query, total, total > results.Length, results);
    }

    private static SqliteCommand SearchCommand(SqliteConnection connection, string sql, string match, string attachmentMatch,
        string source, string? folder, string? account, SearchFilters? filters, int limit)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$query", match);
        command.Parameters.AddWithValue("$attachment_query", attachmentMatch);
        command.Parameters.AddWithValue("$limit", limit);
        AddSourceParameter(command, source);
        AddScopeParameters(command, folder, account, filters);
        return command;
    }

    private static MessageSummary ReadSummary(SqliteDataReader reader, string snippet, AttachmentMatch? attachment = null)
    {
        var recipients = JsonSerializer.Deserialize<string[]>(reader.GetString(reader.GetOrdinal("recipients_json"))) ?? [];
        var location = new MessageLocation(reader.GetString(reader.GetOrdinal("source")), reader.GetString(reader.GetOrdinal("account")),
            reader.GetString(reader.GetOrdinal("folder")), reader.GetString(reader.GetOrdinal("source_key")));
        var localIdOrdinal = reader.GetOrdinal("local_id");
        return new MessageSummary(reader.IsDBNull(localIdOrdinal) ? 0 : reader.GetInt64(localIdOrdinal), location.Source,
            location.Account, location.Folder, location.SourceKey, null, reader.GetString(reader.GetOrdinal("subject")),
            reader.GetString(reader.GetOrdinal("sender")), reader.GetString(reader.GetOrdinal("sender_name")), recipients,
            ParseDate(reader, reader.GetOrdinal("date_utc")), reader.GetInt64(reader.GetOrdinal("size")),
            reader.GetInt32(reader.GetOrdinal("is_read")) != 0, reader.GetInt32(reader.GetOrdinal("attachment_count")),
            snippet, false, reader.GetString(reader.GetOrdinal("message_ref")), [location], attachment);
    }

    private static AttachmentMatch ReadAttachmentMatch(SqliteDataReader reader, string kind) => new(
        reader.GetInt64(reader.GetOrdinal("occurrence_id")), reader.GetString(reader.GetOrdinal("filename")),
        reader.GetString(reader.GetOrdinal("declared_media_type")), kind,
        reader.GetString(reader.GetOrdinal("result_snippet")), reader.GetInt32(reader.GetOrdinal("text_was_truncated")) != 0);

    private static string AttachmentMatchExpression(string match)
        => System.Text.RegularExpressions.Regex.Replace(match,
            @"\b(?:subject|sender|recipients|body_text)\s*:\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

    public CatalogLocator? Resolve(string messageRef)
    {
        if (AdoptionCleanup.ResolveReference(_paths, messageRef) is { } retained) return retained;
        Initialize();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT source,account,folder,source_key,local_id FROM documents WHERE message_ref=$ref";
        command.Parameters.AddWithValue("$ref", messageRef);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new CatalogLocator(new MessageLocation(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetInt64(4))
            : null;
    }

    public IReadOnlyList<CatalogStatus> Status()
    {
        Initialize();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT source,generation,count,indexed_at FROM source_state ORDER BY source";
        using var reader = command.ExecuteReader();
        var result = new List<CatalogStatus>();
        while (reader.Read()) result.Add(new CatalogStatus(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture)));
        return result;
    }

    public int SyncAttachments(int? maxItems = null, Action<CatalogProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Initialize();
        return _attachments.Sync(maxItems, progress, cancellationToken);
    }

    public AttachmentIndexStatus AttachmentStatus()
    {
        Initialize();
        return _attachments.Status();
    }

    public AttachmentIndexEstimate EstimateAttachments(int sampleSize = 1000, Action<CatalogProgress>? progress = null)
    {
        Initialize();
        return _attachments.Estimate(sampleSize, progress);
    }

    public int RebuildAttachmentExtractor(string extractorId, bool execute)
    {
        Initialize();
        return _attachments.RebuildExtractor(extractorId, execute);
    }

    public string OpenAttachment(long occurrenceId)
    {
        Initialize();
        return _attachments.OpenOccurrence(occurrenceId);
    }

    private int ReplaceSources(IReadOnlyList<string> sources, IEnumerable<CatalogDocument> documents,
        string stateSource, string generation, Action<CatalogProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using var connection = Open();
        // Index writes cancel at checkpoints so rollback itself cannot be interrupted.
        cancellationToken.ThrowIfCancellationRequested();
        using var transaction = connection.BeginTransaction();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var deleteFts = connection.CreateCommand();
            deleteFts.Transaction = transaction;
            deleteFts.CommandText = "DELETE FROM documents_fts WHERE rowid IN (SELECT id FROM documents WHERE source=$source)";
            deleteFts.Parameters.AddWithValue("$source", source);
            deleteFts.ExecuteNonQuery();
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM documents WHERE source=$source";
            delete.Parameters.AddWithValue("$source", source);
            delete.ExecuteNonQuery();
        }
        var count = 0;
        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Insert(connection, transaction, document);
            // A rebuild has no total to count towards: the messages are streamed rather than listed first.
            if (++count % 1000 == 0) progress?.Invoke(new CatalogProgress("rebuilding", count, 0));
        }
        progress?.Invoke(new CatalogProgress("committing", count, 0));
        cancellationToken.ThrowIfCancellationRequested();
        SetState(connection, transaction, stateSource, generation, count);
        transaction.Commit();
        return count;
    }

    /// <summary>What has been indexed and as of when, which is how the next pass knows whether to do anything.</summary>
    private static void SetState(SqliteConnection connection, SqliteTransaction transaction, string source,
        string generation, int count)
    {
        using var state = connection.CreateCommand();
        state.Transaction = transaction;
        state.CommandText = """
            INSERT INTO source_state(source,generation,count,indexed_at) VALUES ($source,$generation,$count,$indexed_at)
            ON CONFLICT(source) DO UPDATE SET generation=excluded.generation,count=excluded.count,indexed_at=excluded.indexed_at
            """;
        state.Parameters.AddWithValue("$source", source);
        state.Parameters.AddWithValue("$generation", generation);
        state.Parameters.AddWithValue("$count", count);
        state.Parameters.AddWithValue("$indexed_at", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        state.ExecuteNonQuery();
    }

    private static void Insert(SqliteConnection connection, SqliteTransaction transaction, CatalogDocument document)
    {
        var fingerprint = Fingerprint(document);
        var messageRef = Reference(document);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO documents(message_ref,source,account,folder,source_key,local_id,fingerprint,rfc_message_id,
                                  subject,sender,sender_name,recipients_json,date_utc,size,is_read,attachment_count,body_text,
                                  raw_blob_hash,raw_path)
            VALUES ($ref,$source,$account,$folder,$source_key,$local_id,$fingerprint,$message_id,
                    $subject,$sender,$sender_name,$recipients,$date,$size,$is_read,$attachments,$body,$raw_hash,$raw_path)
            RETURNING id
            """;
        command.Parameters.AddWithValue("$ref", messageRef);
        command.Parameters.AddWithValue("$source", document.Source);
        command.Parameters.AddWithValue("$account", document.Account);
        command.Parameters.AddWithValue("$folder", document.Folder);
        command.Parameters.AddWithValue("$source_key", document.SourceKey);
        command.Parameters.AddWithValue("$local_id", (object?)document.LocalId ?? DBNull.Value);
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        command.Parameters.AddWithValue("$message_id", (object?)document.RfcMessageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$subject", document.Subject);
        command.Parameters.AddWithValue("$sender", document.Sender);
        command.Parameters.AddWithValue("$sender_name", document.SenderName);
        command.Parameters.AddWithValue("$recipients", JsonSerializer.Serialize(document.Recipients));
        command.Parameters.AddWithValue("$date", document.Date?.UtcDateTime.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$size", document.Size);
        command.Parameters.AddWithValue("$is_read", document.IsRead ? 1 : 0);
        command.Parameters.AddWithValue("$attachments", document.AttachmentCount);
        command.Parameters.AddWithValue("$body", document.BodyText);
        command.Parameters.AddWithValue("$raw_hash", (object?)AttachmentCatalogIndexer.BlobHash(document.RawPath ?? "") ?? DBNull.Value);
        command.Parameters.AddWithValue("$raw_path", (object?)document.RawPath ?? DBNull.Value);
        var rowid = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        using var fts = connection.CreateCommand();
        fts.Transaction = transaction;
        fts.CommandText = "INSERT INTO documents_fts(rowid,subject,sender,recipients,body_text) VALUES ($id,$subject,$sender,$recipients,$body)";
        fts.Parameters.AddWithValue("$id", rowid);
        fts.Parameters.AddWithValue("$subject", document.Subject);
        fts.Parameters.AddWithValue("$sender", document.Sender);
        fts.Parameters.AddWithValue("$recipients", string.Join(' ', document.Recipients));
        fts.Parameters.AddWithValue("$body", document.BodyText);
        fts.ExecuteNonQuery();
    }

    private CatalogStatus? GetState(string source) => Status().FirstOrDefault(state => state.Source == source);
    // Dispose the registration before its connection: an interrupt must never race a closed handle.
    private static CancellationTokenRegistration InterruptOnCancellation(SqliteConnection connection, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return token.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle));
    }

    private SqliteConnection Open(int timeout = 30)
    {
        var connection = new SqliteConnection($"Data Source={_paths.SearchCatalogPath};Pooling=False;Default Timeout={timeout}");
        connection.Open();
        return connection;
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string declaration)
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = $"PRAGMA table_info({table})";
        using var reader = inspect.ExecuteReader();
        while (reader.Read())
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase)) return;
        reader.Close();
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {declaration}";
        alter.ExecuteNonQuery();
    }

    private static string Reference(CatalogDocument document) => "msg_" + Hash($"{document.Source}\n{document.Account}\n{document.Folder}\n{document.SourceKey}")[..24];
    private static string Fingerprint(CatalogDocument document) => !string.IsNullOrWhiteSpace(document.RfcMessageId)
        ? Hash("mid:" + document.RfcMessageId.Trim().ToLowerInvariant())
        : Hash($"content:{document.Sender.Trim().ToLowerInvariant()}\n{document.Date?.UtcDateTime:O}\n{document.Subject.Trim().ToLowerInvariant()}\n{document.Size}");
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static int SourcePriority(string source) => source switch { MailContract.LocalSource => 0, MailContract.ArchiveSource => 1, _ => 2 };
    private static string SourceClause(string source) => source.ToLowerInvariant() switch
    {
        "all" => "",
        MailContract.LiveSource or "operational" => " AND d.source IN ('connected','local')",
        MailContract.ConnectedSource or MailContract.LocalSource or MailContract.ArchiveSource => " AND d.source=$source",
        _ => throw new ArgumentException($"Unknown search source '{source}'.")
    };
    private static void AddSourceParameter(SqliteCommand command, string source)
    {
        if (source is MailContract.ConnectedSource or MailContract.LocalSource or MailContract.ArchiveSource)
            command.Parameters.AddWithValue("$source", source);
    }
    private static DateTimeOffset? ParseDate(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);
    private static string ScopeClause(string? folder, string? account, SearchFilters? filters)
    {
        var clause = "";
        if (folder is not null)
            clause += filters?.IncludeSubfolders == true
                ? " AND (d.folder=$folder COLLATE NOCASE OR d.folder LIKE $descendants ESCAPE '\\')"
                : " AND d.folder=$folder COLLATE NOCASE";
        if (account is not null) clause += " AND d.account=$account COLLATE NOCASE";
        if (filters?.FolderKeys is { } keys)
            clause += keys.Count == 0 ? " AND 0" : " AND d.folder COLLATE NOCASE IN ("
                + string.Join(',', Enumerable.Range(0, keys.Count).Select(index => "$folderKey" + index)) + ")";
        if (filters?.After is not null) clause += " AND d.date_utc >= $after";
        if (filters?.Before is not null) clause += " AND d.date_utc < $before";
        if (filters?.RequireAttachment == true) clause += " AND d.attachment_count > 0";
        if (!string.IsNullOrWhiteSpace(filters?.Sender))
            clause += " AND d.id IN (SELECT rowid FROM sender_fragments WHERE sender_fragments MATCH $senderFragment)";
        if (HeaderMatch(filters).Length > 0)
            clause += " AND d.id IN (SELECT rowid FROM documents_fts WHERE documents_fts MATCH $headers)";
        return clause;
    }

    private static string HeaderMatch(SearchFilters? filters)
        => string.Join(" AND ", new[] { ("recipients", filters?.Recipient), ("subject", filters?.Subject) }
            .Where(field => !string.IsNullOrWhiteSpace(field.Item2))
            .Select(field => field.Item1 + " : \"" + field.Item2!.Trim().Replace("\"", "\"\"") + "\""));

    private static void AddScopeParameters(SqliteCommand command, string? folder, string? account, SearchFilters? filters)
    {
        if (folder is not null) command.Parameters.AddWithValue("$folder", folder);
        if (folder is not null && filters?.IncludeSubfolders == true)
            command.Parameters.AddWithValue("$descendants", folder.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "/%");
        if (account is not null) command.Parameters.AddWithValue("$account", account);
        if (filters?.FolderKeys is { } keys)
            for (var index = 0; index < keys.Count; index++) command.Parameters.AddWithValue("$folderKey" + index, keys[index]);
        if (HeaderMatch(filters) is { Length: > 0 } headers) command.Parameters.AddWithValue("$headers", headers);
        if (!string.IsNullOrWhiteSpace(filters?.Sender)) command.Parameters.AddWithValue("$senderFragment", SenderMatch(filters.Sender));
        // Dates compare as text because that is how they are stored: ISO-8601 UTC sorts correctly as strings.
        if (filters?.After is { } after) command.Parameters.AddWithValue("$after", after.UtcDateTime.ToString("O"));
        if (filters?.Before is { } before) command.Parameters.AddWithValue("$before", before.UtcDateTime.ToString("O"));
    }
}

/// <summary>Parent-message criteria applied independently of the searched body or attachment text.</summary>
public sealed record SearchFilters(bool IncludeSubfolders = false, DateTimeOffset? After = null,
    DateTimeOffset? Before = null, bool RequireAttachment = false,
    SearchMatchScope MatchScope = SearchMatchScope.All, string? Sender = null, string? Recipient = null, string? Subject = null,
    IReadOnlyList<string>? FolderKeys = null);
