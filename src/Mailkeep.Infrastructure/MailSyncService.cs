using Mailkeep.Core;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace Mailkeep.Infrastructure;

public sealed partial class MailSyncService
{
    private readonly AccountStore _accounts;
    private readonly MailDatabase _database;
    private readonly OAuthClient _oauth;

    public MailSyncService(AccountStore accounts, MailDatabase database, OAuthClient oauth)
    {
        _accounts = accounts;
        _database = database;
        _oauth = oauth;
    }

    /// <summary>Whether an unattended pass should collect this account: its keep-alive is on and its interval
    /// has passed since the last fetch, or it has never been fetched.</summary>
    public bool IsDue(MailAccount account, DateTimeOffset now)
    {
        // Local Mail accounts used to be left off the timer while another client was still collecting the
        // same POP mailbox. This is the only client now, so they keep to their own interval like the rest.
        var keepAlive = account.EffectiveKeepAlive;
        if (!keepAlive.Enabled) return false;
        var state = _database.GetSyncState(account.Name, account.Mailbox);
        // Local Mail is the mail being read day to day, so it is collected every quarter of an hour rather than
        // on its stored hours. The timer also ticks every quarter of an hour and the last fetch is stamped a
        // little after the tick that made it, so without the slack every other tick would find it not yet due.
        var interval = account.IsConnected ? TimeSpan.FromHours(keepAlive.IntervalHours) : LocalMailInterval - LocalMailSlack;
        return state?.LastFetchAt is null || now - state.LastFetchAt >= interval;
    }

    public static readonly TimeSpan LocalMailInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LocalMailSlack = TimeSpan.FromMinutes(2);

    public Task<IReadOnlyList<SyncResult>> SyncAllAsync(bool dueOnly, CancellationToken cancellationToken = default,
        IProgress<SyncProgress>? progress = null, bool? connectedAccounts = null)
        => SyncAllAsync(dueOnly, null, cancellationToken, progress, connectedAccounts);

    public async Task<IReadOnlyList<SyncResult>> SyncAllAsync(bool dueOnly, IProgress<ReceiveProgress>? receiveProgress,
        CancellationToken cancellationToken = default, IProgress<SyncProgress>? progress = null, bool? connectedAccounts = null)
    {
        var now = DateTimeOffset.UtcNow;
        var accounts = _accounts.Load().Where(account =>
            (connectedAccounts is null || account.IsConnected == connectedAccounts)
            && (!dueOnly || IsDue(account, now))).ToArray();
        using var slots = new SemaphoreSlim(4);
        var done = 0;
        var results = await Task.WhenAll(accounts.Select(async account =>
        {
            await slots.WaitAsync(cancellationToken);
            try
            {
                progress?.Report(new SyncProgress(account.Name, account.Address, done, accounts.Length, true));
                var result = await SyncAsync(account, "scheduler", cancellationToken, receiveProgress);
                progress?.Report(new SyncProgress(account.Name, account.Address, Interlocked.Increment(ref done), accounts.Length, false));
                return result;
            }
            finally { slots.Release(); }
        }));
        return results;
    }

    public Task<SyncResult> SyncAsync(string accountName, string source = "desktop", CancellationToken cancellationToken = default)
        => SyncAsync(accountName, null, source, cancellationToken);

    public async Task<SyncResult> SyncAsync(string accountName, IProgress<ReceiveProgress>? receiveProgress,
        string source = "desktop", CancellationToken cancellationToken = default)
    {
        var account = _accounts.Load().FirstOrDefault(a => string.Equals(a.Name, accountName, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"No account named {accountName}.");
        var result = await SyncAsync(account, source, cancellationToken, receiveProgress);
        return result;
    }

    /// <summary>Marks everything now on the server as already collected, downloading none of it, so collection
    /// starts from this moment. This is an explicit migration option for users who already have the history.</summary>
    public async Task<SyncResult> BaselineAsync(string accountName, CancellationToken cancellationToken = default)
    {
        var account = _accounts.Load().FirstOrDefault(a => string.Equals(a.Name, accountName, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"No account named {accountName}.");
        if (account.EffectiveProtocol == MailProtocols.Pop3)
        {
            var incoming = account.Incoming ?? throw new InvalidOperationException("POP3 account has no incoming server settings.");
            using var pop = new Pop3Client();
            await pop.ConnectAsync(incoming.Host, incoming.Port, Security(incoming.Security), cancellationToken);
            await AuthenticateAsync(pop, account, cancellationToken);
            if (!pop.Capabilities.HasFlag(Pop3Capabilities.UIDL))
                throw new InvalidOperationException("POP3 server does not support UIDL; there is no identifier to record.");
            var uidls = await pop.GetMessageUidsAsync(cancellationToken);
            foreach (var uidl in uidls) _database.RememberPopUidl(account.Name, uidl);
            _database.SetSetting("pop_baseline:" + account.Name.ToLowerInvariant(), DateTimeOffset.UtcNow.ToString("O"));
            _database.SetSyncState(account.Name, account.Mailbox, 0, 0, DateTimeOffset.UtcNow);
            await pop.DisconnectAsync(true, cancellationToken);
            return new SyncResult(account.Name, 0, uidls.Count, 0, null);
        }

        using var client = new ImapClient();
        var settings = account.Incoming ?? new IncomingServerSettings(MailProtocols.Imap,
            account.Host ?? OAuthClient.DefaultHost(account.Provider), 993);
        await client.ConnectAsync(settings.Host, settings.Port, Security(settings.Security), cancellationToken);
        if (account.OAuth is not null)
        {
            var token = await _oauth.AccessTokenAsync(account, cancellationToken);
            await client.AuthenticateAsync(new SaslMechanismOAuth2(account.EffectiveUsername, token), cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(account.AppPassword))
        {
            await client.AuthenticateAsync(account.EffectiveUsername, account.AppPassword.Replace(" ", ""), cancellationToken);
        }
        else
        {
            throw new InvalidOperationException("Account has neither OAuth nor an app password.");
        }
        var mailbox = await client.GetFolderAsync(account.Mailbox, cancellationToken);
        await mailbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
        var present = await mailbox.SearchAsync(SearchQuery.All, cancellationToken);
        var maxUid = present.Count > 0 ? present.Max(uid => uid.Id) : 0;
        _database.SetSyncState(account.Name, account.Mailbox, mailbox.UidValidity, maxUid, DateTimeOffset.UtcNow);
        _database.SetSetting("imap_baseline:" + account.Name.ToLowerInvariant(), DateTimeOffset.UtcNow.ToString("O"));
        await client.DisconnectAsync(true, cancellationToken);
        return new SyncResult(account.Name, 0, present.Count, maxUid, null);
    }

    public async Task<PopInspection> InspectPopAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        if (account.IsConnected || account.EffectiveProtocol != MailProtocols.Pop3)
            throw new InvalidOperationException("POP inspection is available only for Local Mail POP3 accounts.");
        var incoming = account.Incoming ?? throw new InvalidOperationException("POP3 account has no incoming server settings.");
        using var client = new Pop3Client();
        await client.ConnectAsync(incoming.Host, incoming.Port, Security(incoming.Security), cancellationToken);
        await AuthenticateAsync(client, account, cancellationToken);
        if (!client.Capabilities.HasFlag(Pop3Capabilities.UIDL))
            throw new InvalidOperationException("POP3 server does not support UIDL; Mailkeep will not risk duplicate downloads.");
        var uidls = await client.GetMessageUidsAsync(cancellationToken);
        var sizes = await client.GetMessageSizesAsync(cancellationToken);
        var seen = _database.PopSeen(account.Name);
        var newCount = 0;
        long totalBytes = 0;
        for (var index = 0; index < uidls.Count; index++)
        {
            if (seen.Contains(uidls[index])) continue;
            newCount++;
            if (index < sizes.Count) totalBytes += sizes[index];
        }
        await client.DisconnectAsync(true, cancellationToken);
        return new PopInspection(uidls.Count, newCount, totalBytes);
    }

    /// <summary>Verify an IMAP login and count Inbox without fetching bodies, setting flags or advancing the local baseline.</summary>
    public async Task<int> InspectImapAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        if (account.EffectiveProtocol != MailProtocols.Imap) throw new InvalidOperationException("IMAP inspection requires an IMAP account.");
        var incoming = account.Incoming ?? throw new InvalidOperationException("IMAP account has no incoming server settings.");
        using var client = new ImapClient();
        await client.ConnectAsync(incoming.Host, incoming.Port, Security(incoming.Security), cancellationToken);
        if (account.OAuth is not null)
            await client.AuthenticateAsync(new SaslMechanismOAuth2(account.EffectiveUsername,
                await _oauth.AccessTokenAsync(account, cancellationToken)), cancellationToken);
        else
            await client.AuthenticateAsync(account.EffectiveUsername,
                account.AppPassword?.Replace(" ", "") ?? throw new InvalidOperationException("Account has no incoming credential."), cancellationToken);
        var folder = await client.GetFolderAsync(account.Mailbox, cancellationToken);
        await folder.StatusAsync(StatusItems.Count, cancellationToken);
        var count = folder.Count;
        await client.DisconnectAsync(true, cancellationToken);
        return count;
    }

    private async Task AuthenticateAsync(IMailService client, MailAccount account, CancellationToken cancellationToken)
    {
        if (account.OAuth is not null)
        {
            var token = await _oauth.AccessTokenAsync(account, cancellationToken);
            await client.AuthenticateAsync(new SaslMechanismOAuth2(account.EffectiveUsername, token), cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(account.AppPassword))
        {
            await client.AuthenticateAsync(account.EffectiveUsername, account.AppPassword.Replace(" ", ""), cancellationToken);
        }
        else
        {
            throw new InvalidOperationException("Account has neither OAuth nor a password.");
        }
    }

    private async Task ConnectReceiveAsync(IMailService client, MailAccount account,
        CancellationToken cancellationToken, IProgress<ReceiveProgress>? progress)
    {
        client.Timeout = 30_000;
        var incoming = account.Incoming ?? new IncomingServerSettings(MailProtocols.Imap,
            account.Host ?? OAuthClient.DefaultHost(account.Provider), 993);
        using var setup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        setup.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await client.ConnectAsync(incoming.Host, incoming.Port, Security(incoming.Security), setup.Token);
            progress?.Report(new(account.Name, account.Address, "Signing in"));
            await AuthenticateAsync(client, account, setup.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && setup.IsCancellationRequested)
        {
            throw new TimeoutException("The mail server did not finish connecting and signing in within 30 seconds. Try receiving again later.");
        }
    }

    private async Task<SyncResult> SyncAsync(MailAccount account, string source, CancellationToken cancellationToken,
        IProgress<ReceiveProgress>? progress = null)
    {
        var started = DateTimeOffset.UtcNow;
        var logId = _database.LogFetch(account.Name, account.Address, source, started);
        var newCount = 0;
        var scanned = 0;
        uint maxUid = 0;
        string? error = null;
        try
        {
            progress?.Report(new(account.Name, account.Address, "Connecting"));
            if (account.EffectiveProtocol == MailProtocols.Pop3)
            {
                var popResult = await SyncPopAsync(account, cancellationToken, progress);
                newCount = popResult.NewCount;
                scanned = popResult.Scanned;
                _database.SetSyncState(account.Name, account.Mailbox, 0, 0, DateTimeOffset.UtcNow);
                return new SyncResult(account.Name, newCount, scanned, 0, null);
            }
            using var client = new ImapClient();
            await ConnectReceiveAsync(client, account, cancellationToken, progress);

            progress?.Report(new(account.Name, account.Address, "Checking for new messages"));
            var folder = await client.GetFolderAsync(account.Mailbox, cancellationToken);
            await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
            var uidValidity = folder.UidValidity;
            if (account.IsConnected)
            {
                (scanned, maxUid) = await SyncConnectedMailboxAsync(account, folder, cancellationToken, progress, () => newCount++);
                _database.SetSyncState(account.Name, account.Mailbox, uidValidity, maxUid, DateTimeOffset.UtcNow);
                await client.DisconnectAsync(true, cancellationToken);
                return new SyncResult(account.Name, newCount, scanned, maxUid, null);
            }
            var state = _database.GetSyncState(account.Name, account.Mailbox);
            if (state?.UidValidity != uidValidity)
            {
                _database.DetachRenumberedImapMessages(account.Name, account.FolderLabel, uidValidity);
                state = null;
            }

            maxUid = state?.MaxUid ?? 0;
            var start = maxUid == uint.MaxValue ? uint.MaxValue : maxUid + 1;
            var query = SearchQuery.Uids(new UniqueIdRange(new UniqueId(start), UniqueId.MaxValue));
            // A server may include its last UID in an n:* range even when n is above that UID.
            var matches = await folder.SearchAsync(query, cancellationToken);
            var uids = matches.Where(uid => uid.Id > maxUid).OrderBy(uid => uid.Id).ToArray();
            var summaries = uids.Length == 0 ? [] : await folder.FetchAsync(uids,
                MessageSummaryItems.Flags | MessageSummaryItems.UniqueId, cancellationToken);
            var readFlags = summaries.ToDictionary(summary => summary.UniqueId.Id,
                summary => summary.Flags?.HasFlag(MessageFlags.Seen) ?? false);
            progress?.Report(new(account.Name, account.Address, "Downloading", 0, uids.Length));
            foreach (var uid in uids)
            {
                await using var serverStream = await folder.GetStreamAsync(uid, cancellationToken);
                using var raw = new MemoryStream();
                await serverStream.CopyToAsync(raw, cancellationToken);
                raw.Position = 0;
                var message = await MimeMessage.LoadAsync(raw, cancellationToken);
                raw.Position = 0;
                var isRead = readFlags.GetValueOrDefault(uid.Id);
                var storedId = await _database.UpsertMimeMessageAsync(MailContract.LiveSource, account.Name,
                    account.FolderLabel, $"{uidValidity}:{uid.Id}", uid.Id, uidValidity,
                    message, raw, isRead, cancellationToken, reconcileLocalImap: true,
                    checkpointMailbox: (scanned + 1) % 25 == 0 ? account.Mailbox : null);
                if (storedId > 0) newCount++;
                scanned++;
                maxUid = Math.Max(maxUid, uid.Id);
                progress?.Report(new(account.Name, account.Address, "Downloading", scanned, uids.Length));
            }

            _database.SetSyncState(account.Name, account.Mailbox, uidValidity, maxUid, DateTimeOffset.UtcNow);
            await client.DisconnectAsync(true, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            error = exception.Message;
        }
        catch (OperationCanceledException) { error = "Cancelled"; throw; }
        finally
        {
            _database.FinishFetch(logId, DateTimeOffset.UtcNow, newCount, scanned, maxUid, error);
            progress?.Report(new(account.Name, account.Address, error is null ? "Complete" : "Failed",
                Result: new SyncResult(account.Name, newCount, scanned, maxUid, error)));
        }
        return new SyncResult(account.Name, newCount, scanned, maxUid, error);
    }

    private async Task<(int NewCount, int Scanned)> SyncPopAsync(MailAccount account, CancellationToken cancellationToken,
        IProgress<ReceiveProgress>? progress)
    {
        if (account.IsConnected)
            throw new InvalidOperationException("POP3 is available only for Local Mail.");
        var incoming = account.Incoming ?? throw new InvalidOperationException("POP3 account has no incoming server settings.");
        using var client = new Pop3Client();
        await ConnectReceiveAsync(client, account, cancellationToken, progress);
        if (!client.Capabilities.HasFlag(Pop3Capabilities.UIDL))
            throw new InvalidOperationException("POP3 server does not support UIDL; Mailkeep will not risk duplicate downloads.");

        progress?.Report(new(account.Name, account.Address, "Checking for new messages"));
        var uidls = await client.GetMessageUidsAsync(cancellationToken);
        var seen = _database.PopSeen(account.Name);
        var newCount = 0;
        var total = uidls.Count(uidl => !seen.Contains(uidl));
        var downloaded = 0;
        progress?.Report(new(account.Name, account.Address, "Downloading", 0, total));
        for (var index = 0; index < uidls.Count; index++)
        {
            var uidl = uidls[index];
            if (seen.Contains(uidl)) continue;
            await using var serverStream = await client.GetStreamAsync(index, false, cancellationToken);
            using var raw = new MemoryStream();
            await serverStream.CopyToAsync(raw, cancellationToken);
            raw.Position = 0;
            var message = await MimeMessage.LoadAsync(raw, cancellationToken);
            raw.Position = 0;
            var storedId = await _database.UpsertMimeMessageAsync(MailContract.LiveSource, account.Name,
                account.FolderLabel, $"pop:{uidl}", null, 0, message, raw, false, cancellationToken,
                reconcileLocalImap: false, popUidl: uidl);
            if (storedId > 0) newCount++;
            progress?.Report(new(account.Name, account.Address, "Downloading", ++downloaded, total));
        }
        await client.DisconnectAsync(true, cancellationToken);
        return (newCount, uidls.Count);
    }

    public async Task DeleteAsync(long messageId, CancellationToken cancellationToken = default)
    {
        var message = _database.GetMessage(messageId) ?? throw new KeyNotFoundException("Message not found.");
        await DeleteManyAsync([message.Summary], null, cancellationToken);
    }

    /// <summary>Local mail is deleted here. Connected changes and their local effects are committed together;
    /// the app's background worker delivers them without making this action wait for the provider.</summary>
    public async Task<DeleteOutcome> DeleteManyAsync(IReadOnlyList<Core.MessageSummary> messages,
        IProgress<DeleteProgress>? progress = null, CancellationToken cancellationToken = default, bool permanently = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (messages.FirstOrDefault(message => message.Source != MailContract.LiveSource) is not null)
            throw new InvalidOperationException("Archived messages are read-only.");
        var accounts = _accounts.Load();
        var total = messages.Count;
        var done = 0;
        var refused = 0;
        var connectedDeleted = false;
        foreach (var group in messages.GroupBy(message => message.Account, StringComparer.OrdinalIgnoreCase))
        {
            // Sent copies belong to sending identities, which need not have receiving accounts.
            // Their provenance survives filing and also disambiguates a same-named IMAP account.
            foreach (var sent in group.Where(message => message.IsLocalSentCopy || message.IsLocalStoredCopy))
            {
                cancellationToken.ThrowIfCancellationRequested();
                _database.DeleteMessageLocally(sent.Id, permanently);
                done++;
                progress?.Report(new DeleteProgress(sent.Sender, done, total));
            }
            var chosen = group.Where(message => !message.IsLocalSentCopy && !message.IsLocalStoredCopy).ToArray();
            if (chosen.Length == 0) continue;
            var account = accounts.FirstOrDefault(item => item.Name.Equals(group.Key, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException($"Account {group.Key} was not found.");
            if (!account.IsConnected)
            {
                // Local Mail keeps the provider's copy, so this is a row moving to Deleted Items and nothing
                // is dialled at all. Reported per message because there is no batch to wait for.
                foreach (var message in chosen)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _database.DeleteMessageLocally(message.Id, permanently);
                    done++;
                    progress?.Report(new DeleteProgress(account.Address, done, total));
                }
                continue;
            }
            if (chosen.FirstOrDefault(message => message.ServerUid is null) is { } orphan)
                throw new InvalidOperationException($"Connected message {orphan.Id} has no server UID.");
            var queued = _database.QueueRemote(account, chosen, permanently ? "purge" : "delete");
            done += queued;
            if (queued > 0) connectedDeleted = true;
            progress?.Report(new DeleteProgress(account.Address, done, total));
        }
        if (connectedDeleted) _database.SweepUnreferencedBlobs(discoverOrphans: false, onlyUnchecked: true);
        return new DeleteOutcome(done, refused);
    }

    // Gmail, Fastmail and the like advertise SPECIAL-USE or XLIST and GetFolder(SpecialFolder.Trash) just
    // works. Outlook.com / Hotmail advertise neither, so that call throws NotSupportedException — and the
    // caller would then either abort the whole delete or, worse, fall through to Expunge, which on
    // Outlook.com removes the mail for good instead of routing it to Deleted Items. So when the special
    // lookup is unavailable, resolve the bin by name (Outlook.com calls it "Deleted") and only give up —
    // leaving the caller to flag-and-expunge — when no such folder exists at all.
    internal static async Task<IMailFolder?> ResolveTrashAsync(ImapClient client, CancellationToken cancellationToken)
    {
        try
        {
            if (client.GetFolder(SpecialFolder.Trash) is { } special) return special;
        }
        catch (NotSupportedException) { }

        if (client.PersonalNamespaces.Count == 0) return null;
        var root = client.GetFolder(client.PersonalNamespaces[0]);
        foreach (var name in new[] { "Deleted", "Deleted Items", "Trash", "Deleted Messages" })
        {
            try
            {
                return await root.GetSubfolderAsync(name, cancellationToken);
            }
            catch (FolderNotFoundException) { }
        }
        return null;
    }

    private static SecureSocketOptions Security(string mode) => mode switch
    {
        MailSecurityModes.SslOnConnect => SecureSocketOptions.SslOnConnect,
        MailSecurityModes.StartTls => SecureSocketOptions.StartTls,
        MailSecurityModes.Auto => SecureSocketOptions.Auto,
        MailSecurityModes.None => SecureSocketOptions.None,
        _ => throw new InvalidDataException($"Unsupported security mode '{mode}'.")
    };
}
