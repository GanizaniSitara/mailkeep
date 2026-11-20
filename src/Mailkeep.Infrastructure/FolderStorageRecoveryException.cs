namespace Mailkeep.Infrastructure;

/// <summary>Committed coordinator changes still need durable folder journal replay.</summary>
public sealed class FolderStorageRecoveryException(Exception innerException) : IOException(
    "Mailbox changes are committed but still await folder-file recovery. Check their current folders; do not repeat the original command. " +
    "Release applications locking the folder files, then reopen Mailkeep to finish recovery without new copies.", innerException);
