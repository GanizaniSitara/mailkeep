using Mailkeep.Core;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;

namespace Mailkeep.Infrastructure;

public sealed partial class MailSyncService
{
    private MailAccount ConnectedMoveAccount(IReadOnlyList<Core.MessageSummary> messages)
    {
        if (messages.Count == 0) throw new InvalidOperationException("Select Connected messages to move.");
        var account = _accounts.Load().SingleOrDefault(a => a.Name.Equals(messages[0].Account, StringComparison.OrdinalIgnoreCase));
        if (account?.IsConnected != true || messages.Any(m => m.Source != MailContract.LiveSource ||
            m.IsLocalSentCopy || m.IsLocalStoredCopy || m.ServerUid is null ||
            !m.Account.Equals(account.Name, StringComparison.OrdinalIgnoreCase) || m.Folder != account.FolderLabel))
            throw new InvalidOperationException("Select messages from one Connected mailbox.");
        foreach (var message in messages)
            if (_database.GetMessage(message.Id)?.Summary is not { } stored ||
                stored.Source != message.Source || stored.Account != message.Account ||
                stored.Folder != message.Folder || stored.SourceKey != message.SourceKey || stored.ServerUid != message.ServerUid)
                throw new InvalidOperationException("The selection has changed. Refresh the mailbox before moving.");
        return account;
    }

    private async Task ConnectForMoveAsync(ImapClient client, MailAccount account, CancellationToken token)
    {
        var incoming = account.Incoming ?? new IncomingServerSettings(MailProtocols.Imap,
            account.Host ?? OAuthClient.DefaultHost(account.Provider), 993);
        await client.ConnectAsync(incoming.Host, incoming.Port, Security(incoming.Security), token);
        if (account.OAuth is not null)
            await client.AuthenticateAsync(new SaslMechanismOAuth2(account.EffectiveUsername,
                await _oauth.AccessTokenAsync(account, token)), token);
        else if (!string.IsNullOrWhiteSpace(account.AppPassword))
            await client.AuthenticateAsync(account.EffectiveUsername, account.AppPassword.Replace(" ", ""), token);
        else throw new InvalidOperationException("Account has neither OAuth nor an app password.");
        if (!client.Capabilities.HasFlag(ImapCapabilities.Move) || !client.Capabilities.HasFlag(ImapCapabilities.UidPlus))
            throw new NotSupportedException("This server does not support direct message moves.");
    }

    public async Task<IReadOnlyList<string>> ConnectedMoveFoldersAsync(IReadOnlyList<Core.MessageSummary> messages,
        CancellationToken cancellationToken = default)
    {
        var account = ConnectedMoveAccount(messages);
        using var client = new ImapClient();
        await ConnectForMoveAsync(client, account, cancellationToken);
        var names = new List<string>();
        foreach (var ns in client.PersonalNamespaces)
            foreach (var folder in await client.GetFoldersAsync(ns, false, cancellationToken))
                if ((folder.Attributes & (FolderAttributes.NoSelect | FolderAttributes.NonExistent)) == 0 &&
                    !folder.FullName.Equals(account.Mailbox, StringComparison.Ordinal)) names.Add(folder.FullName);
        await client.DisconnectAsync(true, cancellationToken);
        return names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    public async Task<int> MoveConnectedAsync(IReadOnlyList<Core.MessageSummary> messages, string destination,
        CancellationToken cancellationToken = default)
    {
        var account = ConnectedMoveAccount(messages);
        if (string.IsNullOrWhiteSpace(destination)) throw new ArgumentException("Choose a server folder.");
        cancellationToken.ThrowIfCancellationRequested();
        if (destination == account.Mailbox) throw new InvalidOperationException("Choose a different server folder.");
        return _database.QueueRemote(account, messages, "move", destination);
    }
}
