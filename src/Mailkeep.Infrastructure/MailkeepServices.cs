namespace Mailkeep.Infrastructure;

public sealed class MailkeepServices
{
    public MailkeepServices(string? root = null)
    {
        Paths = new AppPaths(root);
        BulkFolderMigration.CheckAvailable(Paths);
        Blobs = new RawBlobStore(Paths);
        Database = new MailDatabase(Paths, Blobs);
        Accounts = new AccountStore(Paths);
        Identities = new SendingIdentityStore(Paths);
        Catalog = new SearchCatalog(Paths, Accounts);
        OAuth = new OAuthClient(accounts: Accounts, identities: Identities);
        Sync = new MailSyncService(Accounts, Database, OAuth);
        Send = new MailSendService(Identities, Database, Blobs, new SmtpOutgoingTransport(OAuth));
    }

    public AppPaths Paths { get; }
    public RawBlobStore Blobs { get; }
    public MailDatabase Database { get; }
    public AccountStore Accounts { get; }
    public SendingIdentityStore Identities { get; }
    public SearchCatalog Catalog { get; }
    public OAuthClient OAuth { get; }
    public MailSyncService Sync { get; }
    public MailSendService Send { get; }
}
