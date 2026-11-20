using Mailkeep.Core;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;

namespace Mailkeep.Infrastructure;

public interface IRemoteOperationTransport
{
    Task ApplyAsync(MailAccount account, IReadOnlyList<RemoteOperation> operations, CancellationToken token);
}

public sealed partial class MailSyncService
{
    /// <summary>Only the running desktop app schedules this pass. CLI mutations persist work for that app.</summary>
    public async Task DrainRemoteOperationsAsync(CancellationToken cancellationToken = default,
        IRemoteOperationTransport? transport = null)
    {
        FileStream workerLock;
        try { workerLock = _database.LockRemoteWorker(); }
        catch (IOException) { return; } // Another app instance is already draining this store.
        using (workerLock)
        {
            var accounts = _accounts.Load();
            using var slots = new SemaphoreSlim(4);
            var folders = _database.PendingRemoteOperations().GroupBy(op => (op.Account, op.Mailbox));
            await Task.WhenAll(folders.Select(async folder =>
            {
                await slots.WaitAsync(cancellationToken);
                try
                {
                    // A failed head blocks later operations in this folder, even while its retry is delayed.
                    var pending = folder.ToArray();
                    for (var index = 0; index < pending.Length;)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var head = pending[index];
                        if (head.RetryAt > DateTimeOffset.UtcNow) break;
                        var batch = pending.Skip(index).TakeWhile(op => op.Kind == head.Kind &&
                            op.Destination == head.Destination && op.IsRead == head.IsRead &&
                            op.UidValidity == head.UidValidity).Take(50).ToArray();
                        var log = _database.LogFetch(head.Account, head.Mailbox,
                            $"remote-queue:{head.Kind}", DateTimeOffset.UtcNow);
                        try
                        {
                            var account = accounts.SingleOrDefault(a => a.Name == head.Account && a.IsConnected && a.Mailbox == head.Mailbox)
                                ?? throw new InvalidOperationException("Queued Connected account is missing or its mailbox changed.");
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            timeout.CancelAfter(TimeSpan.FromSeconds(15));
                            await (transport ?? new ImapRemoteOperationTransport(_oauth)).ApplyAsync(account, batch, timeout.Token);
                            foreach (var op in batch) _database.FinishRemoteOperation(op.Id, null);
                            _database.FinishFetch(log, DateTimeOffset.UtcNow, 0, batch.Length, 0, null);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            _database.FinishFetch(log, DateTimeOffset.UtcNow, 0, 0, 0, "Queue paused on application exit; retained for retry.");
                            throw;
                        }
                        catch (Exception error)
                        {
                            foreach (var op in batch) _database.FinishRemoteOperation(op.Id, error.Message);
                            _database.FinishFetch(log, DateTimeOffset.UtcNow, 0, 0, 0, error.Message);
                            break;
                        }
                        index += batch.Length;
                    }
                }
                finally { slots.Release(); }
            }));
        }
    }
}

internal sealed class ImapRemoteOperationTransport(OAuthClient oauth) : IRemoteOperationTransport
{
    public async Task ApplyAsync(MailAccount account, IReadOnlyList<RemoteOperation> operations, CancellationToken token)
    {
        using var client = new ImapClient();
        var incoming = account.Incoming ?? new IncomingServerSettings(MailProtocols.Imap,
            account.Host ?? OAuthClient.DefaultHost(account.Provider), 993);
        var security = incoming.Security switch
        {
            MailSecurityModes.SslOnConnect => SecureSocketOptions.SslOnConnect,
            MailSecurityModes.StartTls => SecureSocketOptions.StartTls,
            MailSecurityModes.Auto => SecureSocketOptions.Auto,
            _ => throw new InvalidDataException("Unsupported security mode.")
        };
        await client.ConnectAsync(incoming.Host, incoming.Port, security, token);
        if (account.OAuth is not null)
            await client.AuthenticateAsync(new SaslMechanismOAuth2(account.EffectiveUsername, await oauth.AccessTokenAsync(account, token)), token);
        else if (!string.IsNullOrWhiteSpace(account.AppPassword))
            await client.AuthenticateAsync(account.EffectiveUsername, account.AppPassword.Replace(" ", ""), token);
        else throw new InvalidOperationException("Account has neither OAuth nor an app password.");
        var head = operations[0];
        var folder = await client.GetFolderAsync(head.Mailbox, token);
        await folder.OpenAsync(FolderAccess.ReadWrite, token);
        if (folder.UidValidity != head.UidValidity)
            throw new InvalidOperationException("Server UIDVALIDITY changed. Queued work retained; review the account before retrying.");
        var uids = operations.Select(op => new UniqueId(op.Uid)).ToArray();
        if (head.Kind == "read")
        {
            if (head.IsRead) await folder.AddFlagsAsync(uids, MessageFlags.Seen, true, token);
            else await folder.RemoveFlagsAsync(uids, MessageFlags.Seen, true, token);
        }
        else if (head.Kind == "purge")
        {
            if (!client.Capabilities.HasFlag(ImapCapabilities.UidPlus))
                throw new NotSupportedException("UIDPLUS is required to delete only the selected messages.");
            await folder.AddFlagsAsync(uids, MessageFlags.Deleted, true, token);
            await folder.ExpungeAsync(uids, token);
        }
        else
        {
            // Native UID MOVE is safe to retry after a lost acknowledgement: the old UIDs are gone.
            // A COPY+DELETE fallback could duplicate messages and must never be retried automatically.
            if (!client.Capabilities.HasFlag(ImapCapabilities.Move))
                throw new NotSupportedException("This provider requires native IMAP MOVE for safe queued moves.");
            var target = head.Kind == "move"
                ? await client.GetFolderAsync(head.Destination!, token)
                : await MailSyncService.ResolveTrashAsync(client, token)
                    ?? throw new InvalidOperationException("No provider Trash folder found; deletion retained in queue.");
            if (target.FullName == folder.FullName ||
                (target.Attributes & (FolderAttributes.NoSelect | FolderAttributes.NonExistent)) != 0)
                throw new InvalidOperationException("Choose a different selectable server folder.");
            await folder.MoveToAsync(uids, target, token);
        }
        // The command acknowledgement is the commit point; disconnect failure must not undo it.
    }
}
