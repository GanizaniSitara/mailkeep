using System.Security.Cryptography;
using Mailkeep.Core;
using Microsoft.Data.Sqlite;
using MimeKit;

namespace Mailkeep.Infrastructure;

/// <summary>Read-only draft conversion audit report. Does not create drafts or conversion receipts.</summary>
public sealed class OeDraftConversionReport(AppPaths paths)
{
    public sealed record Candidate(long Id, string Source, string Account, string Folder, string SourceKey,
        string? RawPath, string Subject, string[] From, string[] To, string[] Cc, string[] Bcc,
        string? Sha256, bool HasText, bool HasHtml, int Attachments, int InlineParts,
        string[] Issues);
    public sealed record Report(int Version, string Mode, Candidate[] Candidates, string[] Requirements);

    public Report Run(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(paths.DatabasePath)) throw new FileNotFoundException("Mail database does not exist.", paths.DatabasePath);
        var rows = new List<(long Id, string Source, string Account, string Folder, string Key, string? Raw, string Subject, bool Deleted)>();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = paths.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            connection.Open();
            using var transaction = connection.BeginTransaction(deferred: true);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT count(*) FROM pragma_table_info('messages') WHERE name='converted_draft_id'";
            var convertedColumn = Convert.ToInt64(command.ExecuteScalar()) > 0;
            command.CommandText = """
                SELECT m.id,m.source,m.account,m.folder,m.source_key,m.raw_path,m.subject,
                    EXISTS(SELECT 1 FROM local_deletions d WHERE
                        (d.source=m.source AND d.account=m.account AND d.folder=m.folder AND d.source_key=m.source_key)
                        OR (d.source='live' AND d.account=$local AND d.source_key='copy:adopted:' || m.id))
                FROM messages m WHERE m.source IN ('archive','oe_archive')
                    AND (m.account='OE Classic' OR m.account LIKE 'OE Classic %')
                    AND lower(m.folder) IN ('outbox','drafts')
                """ + (convertedColumn ? " AND m.converted_draft_id IS NULL" : "") + " ORDER BY m.id";
            command.Parameters.AddWithValue("$local", MailContract.LocalCopyAccount);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6), reader.GetBoolean(7)));
            }
        }
        var candidates = rows.Select(row =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var issues = new List<string>();
            if (row.Deleted) issues.Add("deletion_evidence_do_not_restore");
            Candidate Result(MimeMessage? message = null, string? hash = null) => new(row.Id, row.Source,
                row.Account, row.Folder, row.Key, row.Raw, row.Subject,
                message?.From.Mailboxes.Select(address => address.Address).ToArray() ?? [],
                message?.To.Mailboxes.Select(address => address.Address).ToArray() ?? [],
                message?.Cc.Mailboxes.Select(address => address.Address).ToArray() ?? [],
                message?.Bcc.Mailboxes.Select(address => address.Address).ToArray() ?? [],
                hash, message?.TextBody is not null, message?.HtmlBody is not null,
                message?.Attachments.Count() ?? 0,
                message?.BodyParts.Count(part => !string.IsNullOrWhiteSpace(part.ContentId)) ?? 0, issues.ToArray());
            if (string.IsNullOrWhiteSpace(row.Raw)) { issues.Add("missing_raw_path"); return Result(); }
            var root = Path.GetFullPath(paths.BlobDirectory) + Path.DirectorySeparatorChar;
            try
            {
                var full = Path.GetFullPath(Path.Combine(root, row.Raw));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { issues.Add("blob_path_outside_store"); return Result(); }
                using var stream = File.OpenRead(full);
                var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
                stream.Position = 0;
                using var message = MimeMessage.Load(stream, cancellationToken);
                if (message.From.Mailboxes.Count() != 1) issues.Add("sending_identity_requires_review");
                if (message.Body is null) issues.Add("missing_body");
                return Result(message, hash);
            }
            catch (IOException) { issues.Add("missing_or_unreadable_blob"); }
            catch (UnauthorizedAccessException) { issues.Add("unreadable_blob"); }
            catch (FormatException) { issues.Add("malformed_mime"); }
            catch (ArgumentException) { issues.Add("invalid_path_or_mime"); }
            return Result();
        }).ToArray();
        return new(1, "read_only_no_conversion", candidates,
            ["Review this exact row list and approve conversion before any live writes.",
             "Create draft-status records only; never queue, send, delete or merge mail.",
             "Preserve original MIME, text/HTML, To/Cc/Bcc, attachments and inline parts; do not infer a sending identity from the default account.",
             "Establish atomic occurrence-to-draft receipts and OE re-import protection before conversion; preserve deleted evidence and independent identical copies.",
             "Retain a consistent database backup and previous publish; no schema or data migration is performed by this report.",
             "Metadata is a read snapshot; MIME is inspected afterwards. Recheck raw hashes and state before any approved conversion."]);
    }
}
