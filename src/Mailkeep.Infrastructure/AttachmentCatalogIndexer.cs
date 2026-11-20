using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MimeKit;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Infrastructure;

public sealed record AttachmentIndexStatus(
    int RawBlobsPending,
    int RawBlobsIndexed,
    int RawBlobsFailed,
    int PayloadsIndexed,
    int PayloadsEmpty,
    int PayloadsNeedsOcr,
    int PayloadsSkipped,
    int PayloadsFailed,
    long ExtractedCharacters)
{
    public int MimeScanVersion { get; init; }
    public int ExtractorVersion { get; init; }
    public int MaxInputBytes { get; init; }
    public int MaxTextCharacters { get; init; }
    public int WorkerTimeoutSeconds { get; init; }
    public long CatalogBytes { get; init; }
    public int FilenameSearchableContentUnavailable { get; init; }
    public int UnreferencedOccurrences { get; init; }
    public int UnreferencedPayloads { get; init; }
    public string? LastRunStatus { get; init; }
    public DateTimeOffset? LastRunCompletedAt { get; init; }
    public IReadOnlyDictionary<string, int> Outcomes { get; init; } = new Dictionary<string, int>();
}

public sealed record AttachmentIndexEstimate(
    int CorpusRawBlobs,
    int SampleRawBlobs,
    int DistinctPayloads,
    long DecodedBytes,
    long ExtractedCharacters,
    long SampleFtsBytes,
    long ProjectedRetainedTextBytes,
    long ProjectedFtsBytes,
    double P50Milliseconds,
    double P95Milliseconds,
    IReadOnlyDictionary<string, int> Outcomes);

/// <summary>Builds the attachment-derived side of the search catalogue. Source MIME is opened read-only;
/// every write is confined to mailkeep-search.db or the preview directory.</summary>
internal sealed class AttachmentCatalogIndexer
{
    public const int MimeScanVersion = 1;
    public const int WorkerTimeoutSeconds = 10;
    private const int CandidateBatchSize = 500;
    private static readonly string[] RetryableReasons = ["worker_timeout", "worker_crash", "worker_empty_response"];
    private readonly AppPaths _paths;

    public AttachmentCatalogIndexer(AppPaths paths) => _paths = paths;

    public static void EnsureSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS raw_blob_scans (
                raw_blob_hash      TEXT PRIMARY KEY,
                raw_path           TEXT NOT NULL,
                mime_scan_version  INTEGER NOT NULL,
                status             TEXT NOT NULL,
                reason             TEXT,
                attachment_count   INTEGER NOT NULL DEFAULT 0,
                attempted_at       TEXT NOT NULL,
                completed_at       TEXT,
                duration_ms        INTEGER NOT NULL DEFAULT 0,
                attempt_count      INTEGER NOT NULL DEFAULT 1
            );
            CREATE TABLE IF NOT EXISTS attachments (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                sha256              TEXT NOT NULL UNIQUE,
                detected_media_type TEXT NOT NULL DEFAULT '',
                byte_size           INTEGER NOT NULL,
                extractor_id        TEXT,
                extractor_version   INTEGER,
                status              TEXT NOT NULL,
                reason              TEXT,
                extracted_chars     INTEGER NOT NULL DEFAULT 0,
                text_was_truncated  INTEGER NOT NULL DEFAULT 0,
                attempted_at        TEXT NOT NULL,
                completed_at        TEXT,
                duration_ms         INTEGER NOT NULL DEFAULT 0,
                attempt_count       INTEGER NOT NULL DEFAULT 1
            );
            CREATE TABLE IF NOT EXISTS attachment_text (
                attachment_id INTEGER PRIMARY KEY REFERENCES attachments(id) ON DELETE CASCADE,
                content_text  TEXT NOT NULL
            );
            CREATE VIRTUAL TABLE IF NOT EXISTS attachment_text_fts USING fts5(
                content_text,
                tokenize='unicode61 remove_diacritics 2'
            );
            CREATE TABLE IF NOT EXISTS attachment_occurrences (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                raw_blob_hash       TEXT NOT NULL,
                raw_path            TEXT NOT NULL,
                part_path           TEXT NOT NULL,
                attachment_id       INTEGER REFERENCES attachments(id),
                filename            TEXT NOT NULL DEFAULT 'attachment',
                declared_media_type TEXT NOT NULL DEFAULT '',
                is_inline           INTEGER NOT NULL DEFAULT 0,
                nesting_depth       INTEGER NOT NULL DEFAULT 0,
                status              TEXT NOT NULL DEFAULT 'indexed',
                reason              TEXT,
                UNIQUE(raw_blob_hash, part_path)
            );
            CREATE INDEX IF NOT EXISTS idx_occurrence_blob ON attachment_occurrences(raw_blob_hash);
            CREATE INDEX IF NOT EXISTS idx_occurrence_attachment ON attachment_occurrences(attachment_id);
            CREATE VIRTUAL TABLE IF NOT EXISTS attachment_names_fts USING fts5(
                filename,
                tokenize='unicode61 remove_diacritics 2'
            );
            CREATE TABLE IF NOT EXISTS attachment_runs (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                started_at  TEXT NOT NULL,
                finished_at TEXT,
                status      TEXT NOT NULL,
                discovered  INTEGER NOT NULL DEFAULT 0,
                extracted   INTEGER NOT NULL DEFAULT 0,
                skipped     INTEGER NOT NULL DEFAULT 0,
                failed      INTEGER NOT NULL DEFAULT 0,
                bytes_read  INTEGER NOT NULL DEFAULT 0
            );
            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "raw_blob_scans", "attempt_count", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(connection, "attachments", "attempt_count", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(connection, "attachment_occurrences", "nesting_depth", "INTEGER NOT NULL DEFAULT 0");
    }

    public int Sync(int? maxItems = null, Action<CatalogProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        BackfillRawPaths(progress, cancellationToken);
        using var connection = OpenCatalog();
        var total = CountCandidates(connection);
        if (maxItems is > 0) total = Math.Min(total, maxItems.Value);
        var runId = StartRun(connection);
        var payloadsBefore = PayloadCounts(connection);
        var done = 0;
        try
        {
            using var worker = AttachmentWorkerClient.TryStart();
            while (done < total)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidates = LoadCandidates(connection, Math.Min(CandidateBatchSize, total - done));
                if (candidates.Count == 0) break;
                foreach (var candidate in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ScanBlob(connection, candidate.Hash, candidate.Path, worker, cancellationToken);
                    done++;
                    if (done % 25 == 0 || done == total)
                        progress?.Invoke(new CatalogProgress("attachments", done, total));
                }
            }
            var after = PayloadCounts(connection);
            FinishRun(connection, runId, "complete", done, payloadsBefore, after);
            return done;
        }
        catch (OperationCanceledException)
        {
            FinishRun(connection, runId, "cancelled", done, payloadsBefore, PayloadCounts(connection));
            throw;
        }
        catch
        {
            FinishRun(connection, runId, "failed", done, payloadsBefore, PayloadCounts(connection));
            throw;
        }
    }

    public AttachmentIndexStatus Status()
    {
        using var connection = OpenCatalog();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM raw_blob_scans WHERE mime_scan_version=$version AND status='indexed'),
              (SELECT COUNT(*) FROM raw_blob_scans WHERE mime_scan_version=$version AND status='failed'),
              (SELECT COUNT(*) FROM attachments WHERE status='indexed'),
              (SELECT COUNT(*) FROM attachments WHERE status='empty'),
              (SELECT COUNT(*) FROM attachments WHERE status='needs_ocr'),
              (SELECT COUNT(*) FROM attachments WHERE status='skipped'),
              (SELECT COUNT(*) FROM attachments WHERE status='failed'),
              (SELECT COALESCE(SUM(extracted_chars),0) FROM attachments)
            """;
        command.Parameters.AddWithValue("$version", MimeScanVersion);
        using var reader = command.ExecuteReader();
        reader.Read();
        var values = new AttachmentIndexStatus(0, reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2),
            reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt64(7));
        reader.Close();
        var outcomes = new Dictionary<string, int>(StringComparer.Ordinal);
        using (var reasons = connection.CreateCommand())
        {
            reasons.CommandText = "SELECT status,COALESCE(reason,''),COUNT(*) FROM attachments GROUP BY status,reason";
            using var outcomeReader = reasons.ExecuteReader();
            while (outcomeReader.Read())
            {
                var key = outcomeReader.GetString(1) is { Length: > 0 } reason
                    ? $"{outcomeReader.GetString(0)}:{reason}"
                    : outcomeReader.GetString(0);
                outcomes[key] = outcomeReader.GetInt32(2);
            }
        }
        var run = LastRun(connection);
        return values with
        {
            RawBlobsPending = CountCandidates(connection),
            MimeScanVersion = MimeScanVersion,
            ExtractorVersion = AttachmentTextExtraction.ExtractorVersion,
            MaxInputBytes = AttachmentTextExtraction.MaxInputBytes,
            MaxTextCharacters = AttachmentTextExtraction.MaxTextCharacters,
            WorkerTimeoutSeconds = WorkerTimeoutSeconds,
            CatalogBytes = CatalogBytes(),
            FilenameSearchableContentUnavailable = ScalarInt(connection, """
                SELECT COUNT(*) FROM attachment_occurrences o
                LEFT JOIN attachments a ON a.id=o.attachment_id
                WHERE o.attachment_id IS NULL OR a.status<>'indexed'
                """),
            UnreferencedOccurrences = ScalarInt(connection, """
                SELECT COUNT(*) FROM attachment_occurrences o
                WHERE NOT EXISTS (SELECT 1 FROM documents d WHERE d.raw_blob_hash=o.raw_blob_hash)
                """),
            UnreferencedPayloads = ScalarInt(connection, """
                SELECT COUNT(*) FROM attachments a
                WHERE NOT EXISTS (
                    SELECT 1 FROM attachment_occurrences o JOIN documents d ON d.raw_blob_hash=o.raw_blob_hash
                    WHERE o.attachment_id=a.id)
                """),
            LastRunStatus = run.Status,
            LastRunCompletedAt = run.CompletedAt,
            Outcomes = outcomes
        };
    }

    public int RebuildExtractor(string extractorId, bool execute)
    {
        if (!AttachmentTextExtraction.HasExtractor(extractorId))
            throw new ArgumentException($"Unknown extractor '{extractorId}'. Expected one of: "
                + string.Join(", ", AttachmentTextExtraction.ExtractorIds));
        using var connection = OpenCatalog();
        using var command = connection.CreateCommand();
        command.CommandText = execute
            ? "UPDATE attachments SET extractor_version=0 WHERE extractor_id=$extractor; SELECT changes()"
            : "SELECT COUNT(*) FROM attachments WHERE extractor_id=$extractor";
        command.Parameters.AddWithValue("$extractor", extractorId);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private int CountCandidates(SqliteConnection connection)
    {
        using var command = CandidateCommand(connection, countOnly: true, CandidateBatchSize);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private List<(string Hash, string Path)> LoadCandidates(SqliteConnection connection, int limit)
    {
        using var command = CandidateCommand(connection, countOnly: false, limit);
        var result = new List<(string Hash, string Path)>(limit);
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add((reader.GetString(0), reader.GetString(1)));
        return result;
    }

    private static SqliteCommand CandidateCommand(SqliteConnection connection, bool countOnly, int limit)
    {
        var command = connection.CreateCommand();
        var selection = countOnly
            ? "SELECT COUNT(*) FROM (SELECT DISTINCT d.raw_blob_hash,d.raw_path"
            : "SELECT DISTINCT d.raw_blob_hash,d.raw_path";
        var ending = countOnly ? ")" : " ORDER BY d.raw_blob_hash LIMIT $limit";
        command.CommandText = selection + "\n" + """
            FROM documents d
            WHERE d.raw_blob_hash IS NOT NULL AND d.raw_path IS NOT NULL AND d.attachment_count > 0
              AND (
                NOT EXISTS (
                  SELECT 1 FROM raw_blob_scans s
                  WHERE s.raw_blob_hash=d.raw_blob_hash AND s.mime_scan_version=$mime_version
                    AND (s.status='indexed' OR (s.status='failed' AND s.attempt_count>=2)))
                OR EXISTS (
                  SELECT 1 FROM attachment_occurrences o JOIN attachments a ON a.id=o.attachment_id
                  WHERE o.raw_blob_hash=d.raw_blob_hash AND (
                    a.extractor_version <> CASE a.extractor_id
                      WHEN 'text' THEN $text_version WHEN 'html' THEN $html_version
                      WHEN 'mime' THEN $mime_extractor_version WHEN 'ooxml' THEN $ooxml_version
                      WHEN 'pdf' THEN $pdf_version WHEN 'ocr' THEN $ocr_version
                      WHEN 'none' THEN CASE WHEN a.status='needs_ocr' THEN 0 ELSE a.extractor_version END
                      ELSE $fallback_version END
                    OR (a.status='failed' AND a.attempt_count<2 AND (
                      a.reason IN ('worker_timeout','worker_crash','worker_empty_response')
                      OR a.reason LIKE 'worker_error:%'))))
              )
            """ + ending;
        command.Parameters.AddWithValue("$mime_version", MimeScanVersion);
        command.Parameters.AddWithValue("$text_version", AttachmentTextExtraction.VersionFor("text"));
        command.Parameters.AddWithValue("$html_version", AttachmentTextExtraction.VersionFor("html"));
        command.Parameters.AddWithValue("$mime_extractor_version", AttachmentTextExtraction.VersionFor("mime"));
        command.Parameters.AddWithValue("$ooxml_version", AttachmentTextExtraction.VersionFor("ooxml"));
        command.Parameters.AddWithValue("$pdf_version", AttachmentTextExtraction.VersionFor("pdf"));
        command.Parameters.AddWithValue("$ocr_version", AttachmentTextExtraction.VersionFor("ocr"));
        command.Parameters.AddWithValue("$fallback_version", AttachmentTextExtraction.ExtractorVersion);
        if (!countOnly) command.Parameters.AddWithValue("$limit", limit);
        return command;
    }

    private sealed record PayloadCount(int Indexed, int Skipped, int Failed);
    private sealed record RunSummary(string? Status, DateTimeOffset? CompletedAt);

    private static PayloadCount PayloadCounts(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM attachments WHERE status='indexed'),
              (SELECT COUNT(*) FROM attachments WHERE status IN ('empty','needs_ocr','skipped')),
              (SELECT COUNT(*) FROM attachments WHERE status='failed')
            """;
        using var reader = command.ExecuteReader();
        reader.Read();
        return new PayloadCount(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    private static long StartRun(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO attachment_runs(started_at,status) VALUES($now,'running') RETURNING id";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void FinishRun(SqliteConnection connection, long runId, string status, int discovered,
        PayloadCount before, PayloadCount after)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE attachment_runs SET finished_at=$now,status=$status,discovered=$discovered,
                extracted=$extracted,skipped=$skipped,failed=$failed
            WHERE id=$id
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$discovered", discovered);
        command.Parameters.AddWithValue("$extracted", Math.Max(0, after.Indexed - before.Indexed));
        command.Parameters.AddWithValue("$skipped", Math.Max(0, after.Skipped - before.Skipped));
        command.Parameters.AddWithValue("$failed", Math.Max(0, after.Failed - before.Failed));
        command.Parameters.AddWithValue("$id", runId);
        command.ExecuteNonQuery();
    }

    private static RunSummary LastRun(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status,finished_at FROM attachment_runs ORDER BY id DESC LIMIT 1";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return new RunSummary(null, null);
        return new RunSummary(reader.GetString(0), reader.IsDBNull(1)
            ? null
            : DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture));
    }

    private long CatalogBytes()
    {
        var total = 0L;
        foreach (var path in new[] { _paths.SearchCatalogPath, _paths.SearchCatalogPath + "-wal", _paths.SearchCatalogPath + "-shm" })
            if (File.Exists(path)) total += new FileInfo(path).Length;
        return total;
    }

    private static int ScalarInt(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public AttachmentIndexEstimate Estimate(int sampleSize = 1000, Action<CatalogProgress>? progress = null)
    {
        if (!File.Exists(_paths.DatabasePath)) return new AttachmentIndexEstimate(0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            new Dictionary<string, int>());
        sampleSize = Math.Clamp(sampleSize, 1, 10_000);
        using var source = new SqliteConnection($"Data Source={_paths.DatabasePath};Mode=ReadOnly;Pooling=False");
        source.Open();
        using var countCommand = source.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(DISTINCT raw_path) FROM messages WHERE raw_path IS NOT NULL AND attachment_count > 0";
        var corpus = Convert.ToInt32(countCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        var paths = new List<string>();
        using (var command = source.CreateCommand())
        {
            command.CommandText = "SELECT DISTINCT raw_path FROM messages WHERE raw_path IS NOT NULL AND attachment_count > 0 ORDER BY raw_path LIMIT $limit";
            command.Parameters.AddWithValue("$limit", sampleSize);
            using var reader = command.ExecuteReader();
            while (reader.Read()) paths.Add(reader.GetString(0));
        }

        using var worker = AttachmentWorkerClient.TryStart();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var texts = new List<string>();
        var timings = new List<double>();
        var outcomes = new Dictionary<string, int>(StringComparer.Ordinal);
        long decodedBytes = 0;
        var completed = 0;
        foreach (var rawPath in paths)
        {
            try
            {
                using var stream = OpenBlob(rawPath);
                if (stream.Length > 100L * 1024 * 1024)
                {
                    Count(outcomes, "raw_blob_too_large");
                    continue;
                }
                var message = MimeMessage.Load(stream);
                foreach (var part in IndexedParts(message))
                {
                    byte[] content;
                    try { content = DecodePart(part); }
                    catch (AttachmentTooLargeException) { Count(outcomes, "too_large"); continue; }
                    var hash = Convert.ToHexStringLower(SHA256.HashData(content));
                    if (!seen.Add(hash)) continue;
                    decodedBytes += content.Length;
                    var stopwatch = Stopwatch.StartNew();
                    var fileName = part.ContentDisposition?.FileName ?? part.ContentType.Name ?? "attachment";
                    var extraction = worker?.Extract(content, fileName, part.ContentType.MimeType, CancellationToken.None)
                        ?? AttachmentTextExtraction.Extract(content, fileName, part.ContentType.MimeType);
                    stopwatch.Stop();
                    timings.Add(stopwatch.Elapsed.TotalMilliseconds);
                    Count(outcomes, extraction.Reason ?? extraction.Status);
                    if (extraction.Status == "indexed") texts.Add(extraction.Text);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or MimeKit.ParseException or InvalidDataException)
            {
                Count(outcomes, "mime_failed");
            }
            finally
            {
                completed++;
                if (completed % 25 == 0 || completed == paths.Count)
                    progress?.Invoke(new CatalogProgress("attachment estimate", completed, paths.Count));
            }
        }

        var sampleFtsBytes = MeasureFts(texts);
        var factor = paths.Count == 0 ? 0d : (double)corpus / paths.Count;
        var ordered = timings.Order().ToArray();
        return new AttachmentIndexEstimate(corpus, paths.Count, seen.Count, decodedBytes, texts.Sum(text => (long)text.Length),
            sampleFtsBytes, (long)Math.Ceiling(texts.Sum(text => Encoding.UTF8.GetByteCount(text)) * factor),
            (long)Math.Ceiling(sampleFtsBytes * factor), Percentile(ordered, 0.50), Percentile(ordered, 0.95), outcomes);
    }

    public string OpenOccurrence(long occurrenceId)
    {
        using var connection = OpenCatalog();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT o.raw_path,o.part_path,o.filename,a.sha256
            FROM attachment_occurrences o JOIN attachments a ON a.id=o.attachment_id
            WHERE o.id=$id
            """;
        command.Parameters.AddWithValue("$id", occurrenceId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException($"Attachment occurrence {occurrenceId} was not found.");
        var rawPath = reader.GetString(0);
        var partIndex = int.Parse(reader.GetString(1), CultureInfo.InvariantCulture) - 1;
        var fileName = SafeFileName(reader.GetString(2));
        var expectedHash = reader.GetString(3);
        using var stream = OpenBlob(rawPath);
        var message = MimeMessage.Load(stream);
        var part = IndexedParts(message).ElementAtOrDefault(partIndex)
            ?? throw new InvalidDataException("The indexed MIME part no longer exists.");
        var content = DecodePart(part);
        var actualHash = Convert.ToHexStringLower(SHA256.HashData(content));
        if (!actualHash.Equals(expectedHash, StringComparison.Ordinal))
            throw new InvalidDataException("The attachment changed since it was indexed; run attachment sync again.");
        _paths.EnsureDirectories();
        var destination = Path.Combine(_paths.PreviewDirectory, $"{occurrenceId}-{fileName}");
        File.WriteAllBytes(destination, content);
        return destination;
    }

    private void BackfillRawPaths(Action<CatalogProgress>? progress, CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.DatabasePath)) return;
        using var catalog = OpenCatalog();
        var missing = new List<long>();
        using (var select = catalog.CreateCommand())
        {
            select.CommandText = "SELECT DISTINCT local_id FROM documents WHERE local_id IS NOT NULL AND raw_path IS NULL AND attachment_count > 0";
            using var reader = select.ExecuteReader();
            while (reader.Read()) missing.Add(reader.GetInt64(0));
        }
        if (missing.Count == 0) return;

        using var source = new SqliteConnection($"Data Source={_paths.DatabasePath};Mode=ReadOnly;Pooling=False");
        source.Open();
        var done = 0;
        foreach (var batch in Batches(missing, 400))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var read = source.CreateCommand();
            read.CommandText = $"SELECT id,raw_path FROM messages WHERE raw_path IS NOT NULL AND id IN ({batch})";
            using var reader = read.ExecuteReader();
            var values = new List<(long Id, string Path)>();
            while (reader.Read()) values.Add((reader.GetInt64(0), reader.GetString(1)));
            using var transaction = catalog.BeginTransaction();
            foreach (var value in values)
            {
                using var update = catalog.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE documents SET raw_path=$path,raw_blob_hash=$hash WHERE local_id=$id";
                update.Parameters.AddWithValue("$path", value.Path);
                update.Parameters.AddWithValue("$hash", (object?)BlobHash(value.Path) ?? DBNull.Value);
                update.Parameters.AddWithValue("$id", value.Id);
                update.ExecuteNonQuery();
            }
            transaction.Commit();
            done += values.Count;
            progress?.Invoke(new CatalogProgress("attachment paths", done, missing.Count));
        }
    }

    private void ScanBlob(SqliteConnection connection, string rawBlobHash, string rawPath, AttachmentWorkerClient? worker,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        try
        {
            using var stream = OpenBlob(rawPath);
            var message = MimeMessage.Load(stream);
            var parts = IndexedParts(message).ToArray();
            using var transaction = connection.BeginTransaction();
            for (var index = 0; index < parts.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IndexPart(connection, transaction, rawBlobHash, rawPath,
                    (index + 1).ToString(CultureInfo.InvariantCulture), parts[index], worker, cancellationToken);
            }
            UpsertScan(connection, transaction, rawBlobHash, rawPath, "indexed", null, parts.Length, started.ElapsedMilliseconds);
            transaction.Commit();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or MimeKit.ParseException
            or InvalidDataException or FormatException)
        {
            using var transaction = connection.BeginTransaction();
            UpsertScan(connection, transaction, rawBlobHash, rawPath, "failed", Sanitize(error.Message), 0, started.ElapsedMilliseconds);
            transaction.Commit();
        }
    }

    internal static IEnumerable<MimeEntity> IndexedParts(MimeMessage message) => message.BodyParts.Where(part =>
        part.IsAttachment || !string.IsNullOrWhiteSpace(part.ContentDisposition?.FileName)
            || !string.IsNullOrWhiteSpace(part.ContentType.Name));

    private static void IndexPart(SqliteConnection connection, SqliteTransaction transaction, string rawBlobHash,
        string rawPath, string partPath, MimeEntity part, AttachmentWorkerClient? worker,
        CancellationToken cancellationToken)
    {
        var fileName = part.ContentDisposition?.FileName ?? part.ContentType.Name ?? "attachment";
        var declared = part.ContentType.MimeType;
        byte[] content;
        try
        {
            content = DecodePart(part);
        }
        catch (AttachmentTooLargeException)
        {
            UpsertOccurrence(connection, transaction, rawBlobHash, rawPath, partPath, null, fileName, declared,
                !string.IsNullOrWhiteSpace(part.ContentId) && !part.IsAttachment, "skipped", "too_large");
            return;
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        var attachmentId = FindAttachment(connection, transaction, hash);
        if (attachmentId is null)
        {
            var stopwatch = Stopwatch.StartNew();
            var extraction = worker?.Extract(content, fileName, declared, cancellationToken)
                ?? AttachmentTextExtraction.Extract(content, fileName, declared, cancellationToken);
            attachmentId = InsertAttachment(connection, transaction, hash, content.Length, extraction, stopwatch.ElapsedMilliseconds);
        }
        UpsertOccurrence(connection, transaction, rawBlobHash, rawPath, partPath, attachmentId, fileName, declared,
            !string.IsNullOrWhiteSpace(part.ContentId) && !part.IsAttachment, "indexed", null);
    }

    private static byte[] DecodePart(MimeEntity part)
    {
        using var destination = new BoundedMemoryStream(AttachmentTextExtraction.MaxInputBytes);
        switch (part)
        {
            case MimePart mimePart when mimePart.Content is not null:
                mimePart.Content.DecodeTo(destination);
                break;
            case MessagePart messagePart when messagePart.Message is not null:
                messagePart.Message.WriteTo(destination);
                break;
            default:
                return [];
        }
        return destination.ToArray();
    }

    private static long? FindAttachment(SqliteConnection connection, SqliteTransaction transaction, string hash)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,extractor_id,extractor_version,status,reason,attempt_count FROM attachments WHERE sha256=$hash";
        command.Parameters.AddWithValue("$hash", hash);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var extractorId = reader.IsDBNull(1) ? null : reader.GetString(1);
        var status = reader.GetString(3);
        // Legacy v1 rows with extractor_id=none are terminal except for the old needs_ocr
        // outcome, which must be handed to the new OCR extractor exactly once.
        var expectedVersion = extractorId == "none" && status != "needs_ocr"
            ? reader.GetInt32(2)
            : extractorId == "none" && status == "needs_ocr" ? 0 : AttachmentTextExtraction.VersionFor(extractorId);
        if (reader.GetInt32(2) != expectedVersion) return null;
        var reason = reader.IsDBNull(4) ? null : reader.GetString(4);
        if (status == "failed" && reader.GetInt32(5) < 2 && IsRetryable(reason)) return null;
        return reader.GetInt64(0);
    }

    private static long InsertAttachment(SqliteConnection connection, SqliteTransaction transaction, string hash, int byteSize,
        AttachmentExtraction extraction, long durationMs)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO attachments(sha256,detected_media_type,byte_size,extractor_id,extractor_version,status,reason,
                                    extracted_chars,text_was_truncated,attempted_at,completed_at,duration_ms,attempt_count)
            VALUES($hash,$media,$bytes,$extractor,$version,$status,$reason,$characters,$truncated,$now,$now,$duration,1)
            ON CONFLICT(sha256) DO UPDATE SET detected_media_type=excluded.detected_media_type,byte_size=excluded.byte_size,
                extractor_id=excluded.extractor_id,extractor_version=excluded.extractor_version,status=excluded.status,
                reason=excluded.reason,extracted_chars=excluded.extracted_chars,text_was_truncated=excluded.text_was_truncated,
                attempted_at=excluded.attempted_at,completed_at=excluded.completed_at,duration_ms=excluded.duration_ms,
                attempt_count=CASE
                  WHEN attachments.extractor_id=excluded.extractor_id
                   AND attachments.extractor_version=excluded.extractor_version
                  THEN attachments.attempt_count+1 ELSE 1 END
            RETURNING id
            """;
        var now = DateTimeOffset.UtcNow.UtcDateTime.ToString("O");
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$media", extraction.DetectedMediaType);
        command.Parameters.AddWithValue("$bytes", byteSize);
        command.Parameters.AddWithValue("$extractor", extraction.ExtractorId);
        command.Parameters.AddWithValue("$version", extraction.ExtractorVersion);
        command.Parameters.AddWithValue("$status", extraction.Status);
        command.Parameters.AddWithValue("$reason", (object?)extraction.Reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$characters", extraction.Text.Length);
        command.Parameters.AddWithValue("$truncated", extraction.Truncated ? 1 : 0);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$duration", durationMs);
        var id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        using var deleteText = connection.CreateCommand();
        deleteText.Transaction = transaction;
        deleteText.CommandText = "DELETE FROM attachment_text_fts WHERE rowid=$id; DELETE FROM attachment_text WHERE attachment_id=$id";
        deleteText.Parameters.AddWithValue("$id", id);
        deleteText.ExecuteNonQuery();
        if (extraction.Status == "indexed")
        {
            using var insertText = connection.CreateCommand();
            insertText.Transaction = transaction;
            insertText.CommandText = "INSERT INTO attachment_text(attachment_id,content_text) VALUES($id,$text); INSERT INTO attachment_text_fts(rowid,content_text) VALUES($id,$text)";
            insertText.Parameters.AddWithValue("$id", id);
            insertText.Parameters.AddWithValue("$text", extraction.Text);
            insertText.ExecuteNonQuery();
        }
        return id;
    }

    private static void UpsertOccurrence(SqliteConnection connection, SqliteTransaction transaction, string rawBlobHash,
        string rawPath, string partPath, long? attachmentId, string fileName, string mediaType, bool inline, string status, string? reason)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO attachment_occurrences(raw_blob_hash,raw_path,part_path,attachment_id,filename,declared_media_type,is_inline,status,reason)
            VALUES($blob,$path,$part,$attachment,$filename,$media,$inline,$status,$reason)
            ON CONFLICT(raw_blob_hash,part_path) DO UPDATE SET raw_path=excluded.raw_path,attachment_id=excluded.attachment_id,
                filename=excluded.filename,declared_media_type=excluded.declared_media_type,is_inline=excluded.is_inline,
                status=excluded.status,reason=excluded.reason
            RETURNING id
            """;
        command.Parameters.AddWithValue("$blob", rawBlobHash);
        command.Parameters.AddWithValue("$path", rawPath);
        command.Parameters.AddWithValue("$part", partPath);
        command.Parameters.AddWithValue("$attachment", (object?)attachmentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$filename", fileName);
        command.Parameters.AddWithValue("$media", mediaType);
        command.Parameters.AddWithValue("$inline", inline ? 1 : 0);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        var id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        using var name = connection.CreateCommand();
        name.Transaction = transaction;
        name.CommandText = "DELETE FROM attachment_names_fts WHERE rowid=$id; INSERT INTO attachment_names_fts(rowid,filename) VALUES($id,$filename)";
        name.Parameters.AddWithValue("$id", id);
        name.Parameters.AddWithValue("$filename", fileName);
        name.ExecuteNonQuery();
    }

    private static void UpsertScan(SqliteConnection connection, SqliteTransaction transaction, string hash, string path,
        string status, string? reason, int count, long durationMs)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO raw_blob_scans(raw_blob_hash,raw_path,mime_scan_version,status,reason,attachment_count,attempted_at,completed_at,duration_ms,attempt_count)
            VALUES($hash,$path,$version,$status,$reason,$count,$now,$now,$duration,1)
            ON CONFLICT(raw_blob_hash) DO UPDATE SET raw_path=excluded.raw_path,mime_scan_version=excluded.mime_scan_version,
                status=excluded.status,reason=excluded.reason,attachment_count=excluded.attachment_count,
                attempted_at=excluded.attempted_at,completed_at=excluded.completed_at,duration_ms=excluded.duration_ms,
                attempt_count=CASE
                  WHEN raw_blob_scans.mime_scan_version=excluded.mime_scan_version AND raw_blob_scans.status='failed'
                  THEN raw_blob_scans.attempt_count+1 ELSE 1 END
            """;
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$version", MimeScanVersion);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$count", count);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$duration", durationMs);
        command.ExecuteNonQuery();
    }

    private Stream OpenBlob(string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) throw new InvalidDataException("Raw blob path must be relative.");
        var root = Path.GetFullPath(_paths.BlobDirectory) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Raw blob path escaped the blob store.");
        return new RawBlobStore(_paths).OpenRead(relativePath);
    }

    private SqliteConnection OpenCatalog()
    {
        var connection = new SqliteConnection($"Data Source={_paths.SearchCatalogPath};Pooling=False");
        connection.Open();
        EnsureSchema(connection);
        return connection;
    }

    private static long MeasureFts(IReadOnlyList<string> texts)
    {
        if (texts.Count == 0) return 0;
        var path = Path.Combine(Path.GetTempPath(), $"mailkeep-attachment-estimate-{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE VIRTUAL TABLE sample_fts USING fts5(content_text,tokenize='unicode61 remove_diacritics 2')";
                command.ExecuteNonQuery();
                using var transaction = connection.BeginTransaction();
                foreach (var text in texts)
                {
                    using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = "INSERT INTO sample_fts(content_text) VALUES($text)";
                    insert.Parameters.AddWithValue("$text", text);
                    insert.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            return new FileInfo(path).Length;
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void Count(IDictionary<string, int> counts, string key)
        => counts[key] = counts.TryGetValue(key, out var value) ? value + 1 : 1;
    private static double Percentile(IReadOnlyList<double> ordered, double percentile)
        => ordered.Count == 0 ? 0 : ordered[(int)Math.Clamp(Math.Ceiling(ordered.Count * percentile) - 1, 0, ordered.Count - 1)];

    internal static string? BlobHash(string rawPath)
    {
        var name = Path.GetFileNameWithoutExtension(rawPath);
        return name.Length == 64 && name.All(Uri.IsHexDigit) ? name.ToLowerInvariant() : null;
    }

    private static IEnumerable<string> Batches(IReadOnlyCollection<long> ids, int size)
    {
        for (var taken = 0; taken < ids.Count; taken += size)
            yield return string.Join(',', ids.Skip(taken).Take(size));
    }

    private static string SafeFileName(string value)
    {
        var fileName = Path.GetFileName(value);
        foreach (var invalid in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(fileName) ? "attachment" : fileName;
    }

    private static string Sanitize(string value) => value.Length <= 240 ? value : value[..240];
    private static bool IsRetryable(string? reason) => reason is not null
        && (RetryableReasons.Contains(reason, StringComparer.Ordinal) || reason.StartsWith("worker_error:", StringComparison.Ordinal));

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

    /// <summary>One long-lived extractor process per run. A hard timeout kills it and the next attachment starts
    /// a fresh worker, so a hostile parser cannot pin the coordinator or lose already committed blobs.</summary>
    private sealed class AttachmentWorkerClient : IDisposable
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(WorkerTimeoutSeconds);
        private readonly string _executable;
        private readonly string? _assembly;
        private Process? _process;

        private AttachmentWorkerClient(string executable, string? assembly)
        {
            _executable = executable;
            _assembly = assembly;
        }

        public static AttachmentWorkerClient? TryStart()
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable)) return null;
            if (Path.GetFileNameWithoutExtension(executable).Equals("Mailkeep.Cli", StringComparison.OrdinalIgnoreCase))
                return new AttachmentWorkerClient(executable, null);
            var assembly = Path.Combine(AppContext.BaseDirectory, "Mailkeep.Cli.dll");
            return Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                   && File.Exists(assembly)
                ? new AttachmentWorkerClient(executable, assembly)
                : null;
        }

        public AttachmentExtraction Extract(byte[] content, string fileName, string mediaType,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                EnsureStarted();
                var request = JsonSerializer.Serialize(new AttachmentWorkerRequest(Convert.ToBase64String(content), fileName, mediaType));
                _process!.StandardInput.WriteLine(request);
                _process.StandardInput.Flush();
                string? response;
                try
                {
                    response = _process.StandardOutput.ReadLineAsync().WaitAsync(Timeout, cancellationToken).GetAwaiter().GetResult();
                }
                catch (TimeoutException)
                {
                    Stop();
                    return new AttachmentExtraction("failed", "", mediaType, "worker",
                        AttachmentTextExtraction.ExtractorVersion, "worker_timeout");
                }
                if (response is null)
                {
                    Stop();
                    return new AttachmentExtraction("failed", "", mediaType, "worker",
                        AttachmentTextExtraction.ExtractorVersion, "worker_crash");
                }
                return JsonSerializer.Deserialize<AttachmentExtraction>(response)
                    ?? new AttachmentExtraction("failed", "", mediaType, "worker",
                        AttachmentTextExtraction.ExtractorVersion, "worker_empty_response");
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or JsonException)
            {
                Stop();
                return new AttachmentExtraction("failed", "", mediaType, "worker",
                    AttachmentTextExtraction.ExtractorVersion, "worker_error: " + Sanitize(error.Message));
            }
        }

        private void EnsureStarted()
        {
            if (_process is { HasExited: false }) return;
            Stop();
            var start = new ProcessStartInfo
            {
                FileName = _executable,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            if (_assembly is not null) start.ArgumentList.Add(_assembly);
            start.ArgumentList.Add("attachment-worker");
            _process = Process.Start(start) ?? throw new InvalidOperationException("Attachment worker did not start.");
        }

        private void Stop()
        {
            if (_process is null) return;
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            finally
            {
                _process.Dispose();
                _process = null;
            }
        }

        public void Dispose()
        {
            if (_process is null) return;
            try
            {
                _process.StandardInput.Close();
                if (!_process.WaitForExit(1000)) _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            finally
            {
                _process.Dispose();
                _process = null;
            }
        }
    }

    private sealed class AttachmentTooLargeException : Exception;

    private sealed class BoundedMemoryStream(int limit) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Length + count > limit) throw new AttachmentTooLargeException();
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Length + buffer.Length > limit) throw new AttachmentTooLargeException();
            base.Write(buffer);
        }
    }
}
