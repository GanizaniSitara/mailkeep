using Mailkeep.Core;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;

namespace Mailkeep.Infrastructure;

public sealed partial class MailSyncService
{
    public async Task<int> SetReadManyAsync(IReadOnlyList<Core.MessageSummary> messages, bool isRead,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (messages.Any(message => message.Source != MailContract.LiveSource))
            throw new InvalidOperationException("Imported messages are read-only here.");
        var accounts = _accounts.Load();
        var selected = messages.DistinctBy(message => message.Id).ToArray();
        foreach (var message in selected.Where(message => !message.IsLocalSentCopy && !message.IsLocalStoredCopy))
            if (!accounts.Any(account => account.Name.Equals(message.Account, StringComparison.OrdinalIgnoreCase)))
                throw new KeyNotFoundException($"Account {message.Account} was not found.");
        var changed = 0;
        foreach (var group in selected.GroupBy(message => message.Account, StringComparer.OrdinalIgnoreCase))
        {
            var account = accounts.FirstOrDefault(account => account.Name.Equals(group.Key, StringComparison.OrdinalIgnoreCase));
            var local = group.Where(message => message.IsLocalSentCopy || message.IsLocalStoredCopy || account?.IsConnected != true).ToArray();
            foreach (var message in local)
            {
                cancellationToken.ThrowIfCancellationRequested();
                changed += SetCachedReadState(message, isRead);
            }
            var remote = group.Where(message => !message.IsLocalSentCopy && !message.IsLocalStoredCopy && account?.IsConnected == true).ToArray();
            if (remote.Length > 0) changed += _database.QueueRemote(account!, remote, "read", isRead: isRead);
        }
        return changed;
    }

    private int SetCachedReadState(Core.MessageSummary message, bool isRead)
        => _database.SetReadState(message.Source, message.Account, message.Folder, message.SourceKey, isRead);

}
