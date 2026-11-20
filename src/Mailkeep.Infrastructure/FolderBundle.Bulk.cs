using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using System.Buffers.Binary;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Infrastructure;

public sealed partial class FolderBundle
{
    public const int BulkFormatVersion = 2;
    public const int LogicalFormatVersion = 3;
    public sealed record Binding(string Source, string Account, string Folder, string Hierarchy, bool Logical = false);
    public sealed record BulkMessage(long Id, string Metadata, byte[] Raw, string Sha256);
    public sealed record BulkResult(long Messages, long Bytes, string ContentDigest);

    internal static void DigestOccurrence(IncrementalHash digest, long id, string sha, string metadata)
    {
        Span<byte> number = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(number, id); digest.AppendData(number);
        digest.AppendData(Convert.FromHexString(sha));
        var bytes = Encoding.UTF8.GetBytes(metadata);
        BinaryPrimitives.WriteInt64LittleEndian(number, bytes.LongLength); digest.AppendData(number); digest.AppendData(bytes);
    }

    /// <summary>Creates an unpublished snapshot directly, without adoption, journal replay or MIME parsing.
    /// The caller owns publication; a partial file is never an active mailbox.</summary>
    public static BulkResult WriteBulk(string file, string uuid, Binding binding,
        IEnumerable<BulkMessage> rows, CancellationToken cancellationToken = default)
    {
        if (binding.Logical && (binding.Source != "live" || binding.Account != ""))
            throw new ArgumentException("A logical local folder keeps account provenance on each message.", nameof(binding));
        using var bundle = Create(file, binding.Folder, binding.Source, uuid);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var c = bundle._connection.CreateCommand();
        c.CommandText = """
            PRAGMA synchronous=FULL;
            PRAGMA cache_size=-8192;
            PRAGMA user_version=2;
            UPDATE folder SET format='mailkeep-folder/2';
            CREATE TABLE binding(singleton INTEGER PRIMARY KEY CHECK(singleton=1),source TEXT NOT NULL,account TEXT NOT NULL,folder TEXT NOT NULL,hierarchy TEXT NOT NULL);
            CREATE TABLE local_occurrences(message_id INTEGER PRIMARY KEY,seq INTEGER NOT NULL UNIQUE,metadata TEXT NOT NULL);
            INSERT INTO binding VALUES(1,$source,$account,$folder,$hierarchy);
            """;
        c.Parameters.AddWithValue("$source", binding.Source);
        c.Parameters.AddWithValue("$account", binding.Account);
        c.Parameters.AddWithValue("$folder", binding.Folder);
        c.Parameters.AddWithValue("$hierarchy", binding.Hierarchy);
        c.ExecuteNonQuery();
        c.Parameters.Clear();
        if (binding.Logical)
        {
            c.CommandText = "PRAGMA user_version=3; UPDATE folder SET format='mailkeep-folder/3'; ALTER TABLE binding ADD COLUMN logical INTEGER NOT NULL DEFAULT 1 CHECK(logical=1);";
            c.ExecuteNonQuery();
        }
        c.CommandText = """
            INSERT INTO messages VALUES($id,$sha,$raw,$size,$date);
            INSERT INTO state(seq,is_read,origin,added_at,legacy_id) VALUES($id,$read,$origin,$now,$id);
            INSERT INTO local_occurrences VALUES($id,$id,$metadata);
            INSERT INTO headers VALUES($id,$subject,$sender,$senderName,$recipients,$date,NULL,$attachments,$snippet);
            INSERT INTO fts(rowid,subject,sender,recipients,body_text) VALUES($id,$subject,$sender,$recipients,$body);
            """;
        foreach (var name in new[] { "id", "sha", "raw", "size", "date", "read", "origin", "now", "metadata", "subject", "sender", "senderName", "recipients", "attachments", "snippet", "body" })
            c.Parameters.Add(new SqliteParameter("$" + name, DBNull.Value));
        c.Prepare();
        long count = 0, bytes = 0, chunkBytes = 0, maxId = 0;
        var transaction = bundle._connection.BeginTransaction();
        c.Transaction = transaction;
        try
        {
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var json = JsonDocument.Parse(row.Metadata);
                var m = json.RootElement;
                if (row.Id <= 0 || m.GetProperty("id").GetInt64() != row.Id
                    || m.GetProperty("source").GetString() != binding.Source
                    || (!binding.Logical && m.GetProperty("account").GetString() != binding.Account)
                    || m.GetProperty("folder").GetString() != binding.Folder
                    || Sha256(row.Raw) != row.Sha256)
                    throw new InvalidDataException("Bulk source identity or MIME hash differs.");
                object Field(string name) => m.GetProperty(name).ValueKind switch
                { JsonValueKind.Null => DBNull.Value, JsonValueKind.Number => m.GetProperty(name).GetInt64(), _ => m.GetProperty(name).GetString()! };
                c.Parameters["$id"].Value = row.Id;
                c.Parameters["$sha"].Value = row.Sha256;
                c.Parameters["$raw"].Value = row.Raw;
                c.Parameters["$size"].Value = row.Raw.LongLength;
                c.Parameters["$date"].Value = Field("date_utc");
                c.Parameters["$read"].Value = Field("is_read");
                c.Parameters["$origin"].Value = Field("source_key");
                c.Parameters["$now"].Value = Now();
                c.Parameters["$metadata"].Value = row.Metadata;
                c.Parameters["$subject"].Value = Field("subject");
                c.Parameters["$sender"].Value = Field("sender");
                c.Parameters["$senderName"].Value = Field("sender_name");
                var recipients = string.Join(", ", JsonSerializer.Deserialize<string[]>(m.GetProperty("recipients_json").GetString()!)!);
                c.Parameters["$recipients"].Value = recipients;
                c.Parameters["$attachments"].Value = Field("attachment_count");
                var body = m.GetProperty("body_text").GetString()!;
                c.Parameters["$snippet"].Value = Snippet(body.Length > 1024 ? body[..1024] : body);
                c.Parameters["$body"].Value = body;
                c.ExecuteNonQuery();
                DigestOccurrence(digest, row.Id, row.Sha256, row.Metadata);
                count++; bytes += row.Raw.LongLength; chunkBytes += row.Raw.LongLength + row.Metadata.Length * 2L;
                maxId = Math.Max(maxId, row.Id);
                if (chunkBytes >= 64 * 1024 * 1024 || count % 4096 == 0)
                {
                    transaction.Commit(); transaction.Dispose();
                    transaction = bundle._connection.BeginTransaction(); c.Transaction = transaction; chunkBytes = 0;
                }
            }
            c.Parameters.Clear();
            c.CommandText = "UPDATE folder SET next_seq=$next; INSERT INTO events(seq,ev,at,detail) VALUES(NULL,'bulk-converted',$now,$detail)";
            c.Parameters.AddWithValue("$next", checked(maxId + 1));
            c.Parameters.AddWithValue("$now", Now());
            c.Parameters.AddWithValue("$detail", JsonSerializer.Serialize(new { Messages = count, Bytes = bytes }));
            c.ExecuteNonQuery();
            transaction.Commit();
        }
        finally { transaction.Dispose(); }
        c.Transaction = null;
        c.Parameters.Clear(); c.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;"; c.ExecuteNonQuery();
        return new(count, bytes, Convert.ToHexStringLower(digest.GetHashAndReset()));
    }
}
