using System.Security.Cryptography;
using System.Text;
using Mailkeep.Core;

namespace Mailkeep.Infrastructure;

public sealed partial class MailDatabase
{
    public static readonly TimeSpan UncertainSendAge = TimeSpan.FromMinutes(15);
    public const string UncertainSendWarning = "This message may or may not have been sent. Review it before choosing Mark as sent or Return to Outbox.";

    public IReadOnlyList<OutgoingMessage> ListStaleSending()
    {
        Initialize();
        using var connection = OpenProjection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + OutgoingColumns + " FROM outgoing_messages WHERE status='sending' AND updated_at<=$cutoff ORDER BY updated_at";
        command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.Subtract(UncertainSendAge).UtcDateTime.ToString("O"));
        using var reader = command.ExecuteReader();
        var messages = new List<OutgoingMessage>();
        while (reader.Read()) messages.Add(ReadOutgoing(reader) with { LastError = UncertainSendWarning });
        return messages;
    }

    public IReadOnlyList<OutgoingMessage> ListOutbox(int limit = 100)
        => ListOutgoing(OutgoingStates.Outbox, limit).Concat(ListOutgoing(OutgoingStates.SentUnrecorded, limit))
            .Concat(ListStaleSending()).ToArray();

    internal FileStream AcquireOutgoingLease(string id)
    {
        var directory = Path.Combine(_paths.DataDirectory, "send-leases");
        Directory.CreateDirectory(directory);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
        return new FileStream(Path.Combine(directory, key + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    /// <summary>An explicit user decision only. A live sender owns this lease and cannot be requeued.</summary>
    public OutgoingMessage RecoverStaleSending(string id, bool markAsSent)
    {
        using var lease = AcquireOutgoingLease(id);
        using var connection = Open(flushFolders: false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE outgoing_messages SET status=$status,updated_at=$now,next_attempt_at=NULL,last_error=NULL,
                sent_at=CASE WHEN $status='sent_unrecorded' THEN coalesce(sent_at,$now) ELSE NULL END
            WHERE id=$id AND status='sending' AND updated_at<=$cutoff
            RETURNING
            """ + " " + OutgoingColumns;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$status", markAsSent ? OutgoingStates.SentUnrecorded : OutgoingStates.Outbox);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.Subtract(UncertainSendAge).UtcDateTime.ToString("O"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("This is not a stale uncertain send. Reload Outbox before making a decision.");
        return ReadOutgoing(reader);
    }
}
