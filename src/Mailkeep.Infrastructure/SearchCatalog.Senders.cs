using System.Text;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Infrastructure;

public sealed partial class SearchCatalog
{
    public static bool SenderFilterTooShort(string? sender)
        => !string.IsNullOrWhiteSpace(sender) && sender.Trim().EnumerateRunes().Count() < 3;

    internal static void ValidateSenderFilter(string? sender)
    {
        if (SenderFilterTooShort(sender))
            throw new ArgumentException("Enter at least 3 characters in From.");
    }

    private void EnsureSenderIndex(CancellationToken cancellationToken)
    {
        using var connection = Open();
        using var cancellation = InterruptOnCancellation(connection, cancellationToken);
        using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='sender_fragments' AND type='table'";
        if (Convert.ToInt32(exists.ExecuteScalar()) != 0) return;
        using var transaction = connection.BeginTransaction();
        exists.Transaction = transaction;
        // Another process may have completed the one-time header index while we waited.
        if (Convert.ToInt32(exists.ExecuteScalar()) != 0) return;
        using var create = connection.CreateCommand();
        create.Transaction = transaction;
        create.CommandText = """
            CREATE VIRTUAL TABLE sender_fragments USING fts5(
                sender, sender_name, content='documents', content_rowid='id', tokenize='trigram');
            INSERT INTO sender_fragments(sender_fragments) VALUES('rebuild');
            CREATE TRIGGER sender_fragments_insert AFTER INSERT ON documents BEGIN
                INSERT INTO sender_fragments(rowid,sender,sender_name) VALUES(new.id,new.sender,new.sender_name);
            END;
            CREATE TRIGGER sender_fragments_delete AFTER DELETE ON documents BEGIN
                INSERT INTO sender_fragments(sender_fragments,rowid,sender,sender_name)
                    VALUES('delete',old.id,old.sender,old.sender_name);
            END;
            CREATE TRIGGER sender_fragments_update AFTER UPDATE OF sender,sender_name ON documents
                WHEN old.sender IS NOT new.sender OR old.sender_name IS NOT new.sender_name BEGIN
                INSERT INTO sender_fragments(sender_fragments,rowid,sender,sender_name)
                    VALUES('delete',old.id,old.sender,old.sender_name);
                INSERT INTO sender_fragments(rowid,sender,sender_name) VALUES(new.id,new.sender,new.sender_name);
            END;
            """;
        create.ExecuteNonQuery();
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private static string SenderMatch(string sender)
    {
        var literal = "\"" + sender.Trim().Replace("\"", "\"\"") + "\"";
        return "sender : " + literal + " OR sender_name : " + literal;
    }
}
