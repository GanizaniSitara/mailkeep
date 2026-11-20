namespace Mailkeep.Core;

public static class MailContract
{
    public const string LiveSource = "live";
    public const string ConnectedSource = "connected";
    public const string LocalSource = "local";
    /// <summary>Internal owner for copies deliberately brought from a Connected mailbox into Local Mail.</summary>
    public const string LocalCopyAccount = "__mailkeep_local_copy__";
    /// <summary>Key prefix of a Local Mail copy made from an imported message, followed by that message's id:
    /// the "copy:" part is what marks the row as a copy, the rest says which import it came from.</summary>
    public const string AdoptedCopyKeyPrefix = "copy:adopted:";
    /// <summary>Read-only mail imported from elsewhere. Named for what it is, not for the client it came
    /// from: a store that outlives the migration should not carry that client's name in its data.</summary>
    public const string ArchiveSource = "archive";

    /// <summary>What imported mail was called when only one client could produce it.</summary>
    public const string LegacyArchiveSource = "oe_archive";
    public const int Version = 2;
}
