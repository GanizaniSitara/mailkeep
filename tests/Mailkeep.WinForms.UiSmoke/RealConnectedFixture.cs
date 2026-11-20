using Mailkeep.Core;
using Mailkeep.Infrastructure;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

/// <summary>Explicit opt-in real-provider probe. Only creates, verifies and removes its own marked messages.</summary>
internal sealed class RealConnectedFixture : IDisposable
{
    private readonly MailkeepServices _services;
    private readonly string _marker = "connected-smoke-" + Guid.NewGuid().ToString("N");
    private readonly MailAccount _original;
    public MailAccount Account { get; }
    public RealConnectedFixture(MailkeepServices services, string accountName)
    {
        _services = services;
        _original = new MailkeepServices().Accounts.Load().Single(a => a.Name == accountName && a.IsConnected);
        using var client = Connect();
        if (!client.Capabilities.HasFlag(ImapCapabilities.Move) || !client.Capabilities.HasFlag(ImapCapabilities.UidPlus))
            throw new NotSupportedException("Real-provider fixture requires MOVE and UIDPLUS.");
        var folder = client.GetFolder(client.PersonalNamespaces[0]).Create(_marker, true);
        folder.Open(FolderAccess.ReadWrite);
        Account = _original with { Mailbox = folder.FullName, FolderLabel = "inbox", KeepAlive = new KeepAliveSettings(false) };
        for (var index = 1; index <= 20; index++)
        {
            using var mime = new MimeMessage();
            mime.From.Add(MailboxAddress.Parse(_original.Address));
            mime.To.Add(MailboxAddress.Parse(_original.Address));
            mime.Subject = $"{_marker} disposable queue test {index}";
            mime.MessageId = $"{_marker}-{index}@mailkeep.invalid";
            mime.Headers.Add("X-Mailkeep-Test", _marker);
            mime.Body = new TextPart("plain") { Text = "Disposable connected-smoke integration fixture. No SMTP delivery." };
            var read = index % 2 == 0;
            var uid = folder.Append(mime, read ? MessageFlags.Seen : MessageFlags.None) ?? throw new InvalidOperationException("APPEND did not return UID.");
            services.Database.Upsert(new MessageInput(MailContract.LiveSource, Account.Name, Account.FolderLabel,
                $"{folder.UidValidity}:{uid.Id}", uid.Id, folder.UidValidity, mime.Subject, _original.Address,
                "Mailkeep queue test", [_original.Address], DateTimeOffset.UtcNow.AddMinutes(-index), 10, read, 0,
                "Disposable connected-smoke fixture", null, null));
        }
        services.Accounts.Save([Account with { Incoming = Account.Incoming! with { Host = "192.0.2.1" } }]);
        Console.WriteLine($"Real Connected fixture ready: account={Account.Name}, folder={_marker}, messages=20, test-endpoint=192.0.2.1");
    }

    private ImapClient Connect()
    {
        var client = new ImapClient { Timeout = 30000 };
        try
        {
            var incoming = _original.Incoming!;
            client.Connect(incoming.Host, incoming.Port, SecureSocketOptions.SslOnConnect);
            if (_original.OAuth is not null)
                client.Authenticate(new SaslMechanismOAuth2(_original.EffectiveUsername,
                    _services.OAuth.AccessTokenAsync(_original).GetAwaiter().GetResult()));
            else client.Authenticate(_original.EffectiveUsername, _original.AppPassword!.Replace(" ", ""));
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    public void Reconnect() => _services.Accounts.Save([Account]);

    public int Verify()
    {
        using var client = Connect();
        var folder = client.GetFolder(Account.Mailbox);
        folder.Open(FolderAccess.ReadOnly);
        if (folder.Search(SearchQuery.All).Count != 0) throw new InvalidOperationException("Test source still contains messages.");
        folder.Close();
        var trash = client.GetFolder(SpecialFolder.Trash);
        trash.Open(FolderAccess.ReadOnly);
        return trash.Search(SearchQuery.HeaderContains("X-Mailkeep-Test", _marker)).Count;
    }

    public void Dispose()
    {
        using var client = Connect();
        var trash = client.GetFolder(SpecialFolder.Trash);
        trash.Open(FolderAccess.ReadWrite);
        var testUids = trash.Search(SearchQuery.HeaderContains("X-Mailkeep-Test", _marker));
        if (testUids.Count > 0)
        {
            trash.AddFlags(testUids, MessageFlags.Deleted, true);
            trash.Expunge(testUids);
        }
        trash.Close();
        var folder = client.GetFolder(Account.Mailbox);
        folder.Delete();
        Console.WriteLine("Real-provider fixture cleaned: marked messages and test folder removed.");
    }
}
