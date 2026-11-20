namespace Mailkeep.Infrastructure;

public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = Path.GetFullPath(root ?? Environment.GetEnvironmentVariable("MAILKEEP_DOTNET_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mailkeep"));
    }

    public string Root { get; }
    public string DataDirectory => Path.Combine(Root, "data");
    public string DatabasePath => Path.Combine(DataDirectory, "mailkeep-v2.db");
    public string SearchCatalogPath => Path.Combine(DataDirectory, "mailkeep-search.db");
    public string BlobDirectory => Path.Combine(DataDirectory, "blobs");
    public string PreviewDirectory => Path.Combine(DataDirectory, "previews");
    public string AccountsPath => Path.Combine(Root, "accounts.v2.dpapi");
    public string SendingIdentitiesPath => Path.Combine(Root, "sending-identities.v1.dpapi");

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(BlobDirectory);
        Directory.CreateDirectory(PreviewDirectory);
    }
}
