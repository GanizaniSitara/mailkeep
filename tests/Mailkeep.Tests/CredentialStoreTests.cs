using System.Net;
using System.Text;
using Mailkeep.Core;
using Mailkeep.Infrastructure;

namespace Mailkeep.Tests;

public sealed class CredentialStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mailkeep-credential-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly OAuthCredential Credential = new("fixture-client", "", "fixture-refresh", ["fixture-scope"]);
    private static MailAccount Account(int id) => new("fixture" + id, "outlook", $"fixture{id}@example.invalid", "INBOX", "inbox",
        null, Credential, null, new());
    private static SendingIdentity Identity(int id) => new("sender" + id, null, "Fixture", $"sender{id}@example.invalid", null,
        false, "outlook", new("smtp.example.invalid", 587), null, Credential, null);

    [Fact]
    public async Task IndependentStoreWritersDoNotLoseAccountsOrIdentities()
    {
        var paths = new AppPaths(root);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(id => Task.Run(() =>
        {
            new AccountStore(paths).Upsert(Account(id));
            new SendingIdentityStore(paths).Upsert(Identity(id));
        })));
        Assert.Equal(8, new AccountStore(paths).Load().Count);
        Assert.Equal(8, new SendingIdentityStore(paths).Load().Count);
        Assert.Single(new SendingIdentityStore(paths).Load(), identity => identity.IsDefault);
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
    }

    [Fact]
    public async Task RotationPersistsAcrossBothStoresAndAccessTokensExpire()
    {
        var paths = new AppPaths(root);
        var accounts = new AccountStore(paths);
        var identities = new SendingIdentityStore(paths);
        accounts.Upsert(Account(1)); identities.Upsert(Identity(1));
        var clock = new Clock();
        var handler = new Tokens();
        using var http = new HttpClient(handler);
        var oauth = new OAuthClient(http, accounts, identities, clock);
        Assert.All(await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => oauth.AccessTokenAsync(Account(1)))), token => Assert.Equal("access-1", token));
        Assert.Equal(1, handler.Requests);
        Assert.Equal("refresh-1", accounts.Load()[0].OAuth!.RefreshToken);
        Assert.Equal("refresh-1", identities.Load()[0].OAuth!.RefreshToken);
        clock.Now += TimeSpan.FromHours(2);
        Assert.Equal("access-2", await oauth.AccessTokenAsync(Account(1)));
        Assert.Equal(2, handler.Requests);
        Assert.Contains("refresh_token=refresh-1", handler.LastBody);
        Assert.Equal("refresh-2", new AccountStore(paths).Load()[0].OAuth!.RefreshToken);
        identities.SetDefault("sender1");
        Assert.Equal("refresh-2", identities.Require("sender1").OAuth!.RefreshToken);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Tokens : HttpMessageHandler
    {
        public int Requests;
        public string LastBody = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            var n = ++Requests;
            return new(HttpStatusCode.OK) { Content = new StringContent($$"""{"access_token":"access-{{n}}","refresh_token":"refresh-{{n}}","expires_in":3600}""", Encoding.UTF8, "application/json") };
        }
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
