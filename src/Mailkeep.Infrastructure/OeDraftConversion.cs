using System.Security.Cryptography;
using System.Text.Json;
using Mailkeep.Core;
using Microsoft.Data.Sqlite;
using MimeKit;

namespace Mailkeep.Infrastructure;

/// <summary>Explicit, backed-up conversion of reviewed OE occurrences. Originals and receipts remain.</summary>
public sealed partial class OeDraftConversion(AppPaths paths)
{
    public sealed record Result(string Backup, int Converted, string[] DraftIds);

    internal static void EnsureSchema(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA table_info(messages)";
        var found = false;
        using (var reader = command.ExecuteReader()) while (reader.Read()) if (reader.GetString(1) == "converted_draft_id") found = true;
        if (!found) { command.CommandText = "ALTER TABLE messages ADD COLUMN converted_draft_id TEXT"; command.ExecuteNonQuery(); }
        command.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_messages_unconverted_counts
                ON messages(source,is_read,account,folder) WHERE converted_draft_id IS NULL;
            CREATE TABLE IF NOT EXISTS oe_draft_conversions (
                message_id INTEGER PRIMARY KEY, source TEXT NOT NULL, account TEXT NOT NULL,
                folder TEXT NOT NULL, source_key TEXT NOT NULL, sha256 TEXT NOT NULL,
                draft_id TEXT NOT NULL UNIQUE, converted_at TEXT NOT NULL,
                UNIQUE(source,account,folder,source_key)
            );
            CREATE TRIGGER IF NOT EXISTS preserve_converted_oe_insert BEFORE INSERT ON messages
            WHEN EXISTS (SELECT 1 FROM oe_draft_conversions r WHERE r.source=NEW.source AND r.account=NEW.account
                AND r.folder=NEW.folder AND r.source_key=NEW.source_key)
            BEGIN SELECT RAISE(IGNORE); END;
            CREATE TRIGGER IF NOT EXISTS preserve_converted_oe_update BEFORE UPDATE ON messages
            WHEN OLD.converted_draft_id IS NOT NULL
            BEGIN SELECT RAISE(IGNORE); END;
            """;
        command.ExecuteNonQuery();
    }

    public Result Apply(OeDraftConversionReport.Report approved, CancellationToken cancellationToken = default)
    {
        if (approved.Mode != "read_only_no_conversion" || approved.Version != 1 || approved.Candidates.Length == 0)
            throw new InvalidDataException("A nonempty reviewed OE draft report is required.");
        if (approved.Candidates.Any(row => row.Issues.Length > 0 || row.Sha256 is null))
            throw new InvalidDataException("Resolve report issues before conversion.");
        var current = new OeDraftConversionReport(paths).Run(cancellationToken);
        if (JsonSerializer.Serialize(current.Candidates) != JsonSerializer.Serialize(approved.Candidates))
            throw new InvalidDataException("OE draft candidates changed; review a fresh report.");
        var identities = new SendingIdentityStore(paths).Load();
        var drafts = approved.Candidates.Select(row =>
        {
            var identity = identities.Where(identity => row.From.Length == 1
                && identity.Email.Equals(row.From[0], StringComparison.OrdinalIgnoreCase)).ToArray();
            if (identity.Length != 1) throw new InvalidDataException($"Message {row.Id} needs an unambiguous sending identity for its original From address.");
            using var message = MimeMessage.Load(Path.Combine(paths.BlobDirectory, row.RawPath!), cancellationToken);
            return (Row: row, Identity: identity[0].Id, Id: "oe_draft_" + row.Id,
                Text: message.TextBody ?? MailText.FromHtml(message.HtmlBody), Html: MailHtml.WithInlineImages(message));
        }).ToArray();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        var backup = paths.DatabasePath + ".before-oe-drafts-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".bak";
        using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Pooling = false }.ToString()))
        {
            destination.Open();
            connection.BackupDatabase(destination);
            using var integrity = destination.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check";
            if (!Equals(integrity.ExecuteScalar(), "ok")) throw new InvalidDataException("Database backup failed integrity check.");
        }
        using var transaction = connection.BeginTransaction();
        EnsureSchema(connection, transaction);
        var now = DateTimeOffset.UtcNow.ToString("O");
        foreach (var draft in drafts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = draft.Row;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT source,account,folder,source_key,raw_path,converted_draft_id FROM messages WHERE id=$message";
            command.Parameters.AddWithValue("$message", row.Id);
            using (var reader = command.ExecuteReader())
                if (!reader.Read() || reader.GetString(0) != row.Source || reader.GetString(1) != row.Account
                    || reader.GetString(2) != row.Folder || reader.GetString(3) != row.SourceKey
                    || reader.GetString(4) != row.RawPath || !reader.IsDBNull(5))
                    throw new InvalidDataException($"Message {row.Id} changed or was already converted.");
            using (var stream = File.OpenRead(Path.Combine(paths.BlobDirectory, row.RawPath!)))
                if (Convert.ToHexStringLower(SHA256.HashData(stream)) != row.Sha256)
                    throw new InvalidDataException($"MIME bytes for message {row.Id} changed.");
            command.CommandText = """
                INSERT INTO outgoing_messages(id,account,sending_identity_id,to_json,cc_json,bcc_json,subject,
                    body_text,body_html,raw_path,status,created_at,updated_at)
                VALUES($draft,$identity,$identity,$to,$cc,$bcc,$subject,$text,$html,$raw,'draft',$now,$now);
                INSERT INTO oe_draft_conversions VALUES($message,$source,$account,$folder,$key,$hash,$draft,$now);
                UPDATE messages SET converted_draft_id=$draft WHERE id=$message;
                """;
            command.Parameters.AddWithValue("$draft", draft.Id);
            command.Parameters.AddWithValue("$identity", draft.Identity);
            command.Parameters.AddWithValue("$to", JsonSerializer.Serialize(row.To));
            command.Parameters.AddWithValue("$cc", JsonSerializer.Serialize(row.Cc));
            command.Parameters.AddWithValue("$bcc", JsonSerializer.Serialize(row.Bcc));
            command.Parameters.AddWithValue("$subject", row.Subject);
            command.Parameters.AddWithValue("$text", draft.Text);
            command.Parameters.AddWithValue("$html", (object?)draft.Html ?? DBNull.Value);
            command.Parameters.AddWithValue("$raw", row.RawPath!);
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$source", row.Source);
            command.Parameters.AddWithValue("$account", row.Account);
            command.Parameters.AddWithValue("$folder", row.Folder);
            command.Parameters.AddWithValue("$key", row.SourceKey);
            command.Parameters.AddWithValue("$hash", row.Sha256!);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        return new(backup, drafts.Length, drafts.Select(draft => draft.Id).ToArray());
    }
}
