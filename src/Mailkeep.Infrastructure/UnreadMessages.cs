using Mailkeep.Core;

namespace Mailkeep.Infrastructure;

public sealed partial class MailDatabase
{
    /// <summary>Metadata only: no message body, read-state change or catalog refresh.</summary>
    public IReadOnlyList<MessageSummary> ListUnreadMessages(int limit = 10)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 21);
        using var connection = OpenProjection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + MessageColumns + " FROM messages "
            + "WHERE source=$live AND is_read=0 AND converted_draft_id IS NULL "
            + "AND folder <> $deleted COLLATE NOCASE ORDER BY date_utc DESC,id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$live", MailContract.LiveSource);
        command.Parameters.AddWithValue("$deleted", LocalFolderKinds.Deleted);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var result = new List<MessageSummary>();
        while (reader.Read()) result.Add(ReadSummary(reader));
        return result;
    }
}
