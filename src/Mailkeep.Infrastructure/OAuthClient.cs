using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mailkeep.Core;

namespace Mailkeep.Infrastructure;

public sealed class OAuthClient
{
    private const string GoogleClientId = "406964657835-aq8lmia8j95dhl1a2bvharmfk3t1hgqj.apps.googleusercontent.com";
    private const string GoogleClientSecret = "kSmqreRr0qwBWJgbf5Y-PjSU";
    private const string MicrosoftClientId = "9e5f94bc-e8a4-4e73-b8be-63364c29d753";
    private readonly HttpClient _http;
    private readonly AccountStore? _accounts;
    private readonly SendingIdentityStore? _identities;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _refresh = new(1);
    private readonly Dictionary<string, CachedToken> _tokens = new();
    private sealed record CachedToken(string RefreshToken, string AccessToken, DateTimeOffset ExpiresAt);

    public OAuthClient(HttpClient? http = null, AccountStore? accounts = null, SendingIdentityStore? identities = null,
        TimeProvider? clock = null)
    {
        _http = http ?? new HttpClient();
        _accounts = accounts;
        _identities = identities;
        _clock = clock ?? TimeProvider.System;
    }

    public Task<string> AccessTokenAsync(MailAccount account, CancellationToken cancellationToken = default)
        => AccessTokenAsync(account.Provider, account.Name, account.OAuth, account.EffectiveProtocol, false, cancellationToken);

    public Task<string> AccessTokenAsync(SendingIdentity identity, CancellationToken cancellationToken = default)
        => AccessTokenAsync(identity.Provider, identity.Id, identity.OAuth, null, true, cancellationToken);

    private async Task<string> AccessTokenAsync(string providerName, string name, OAuthCredential? oauth,
        string? protocol, bool includeSmtp, CancellationToken cancellationToken)
    {
        await _refresh.WaitAsync(cancellationToken);
        try
        {
        using var lease = await RefreshLeaseAsync(cancellationToken);
        if (includeSmtp && _identities is not null) oauth = _identities.Require(name).OAuth;
        else if (!includeSmtp && _accounts is not null)
            oauth = _accounts.Load().FirstOrDefault(account => account.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.OAuth;
        if (oauth is null) throw new InvalidOperationException($"{name} has no OAuth credential.");
        var requestedRefreshToken = oauth.RefreshToken;
        var key = string.Join('\0', providerName, name, protocol, includeSmtp.ToString(), oauth.ClientId);
        if (_tokens.TryGetValue(key, out var cached) && cached.RefreshToken == oauth.RefreshToken && cached.ExpiresAt > _clock.GetUtcNow())
            return cached.AccessToken;
        var provider = Provider(providerName);
        var scopes = oauth.Scopes is { Count: > 0 } ? oauth.Scopes : ScopesFor(providerName, protocol, includeSmtp);
        var form = new Dictionary<string, string>
        {
            ["client_id"] = oauth.ClientId,
            ["refresh_token"] = oauth.RefreshToken,
            ["grant_type"] = "refresh_token"
        };
        if (!string.IsNullOrWhiteSpace(oauth.ClientSecret)) form["client_secret"] = oauth.ClientSecret;
        if (IsMicrosoft(providerName)) form["scope"] = string.Join(' ', scopes);
        var token = await PostTokenAsync(provider.TokenEndpoint, form, cancellationToken);
        if (!string.IsNullOrWhiteSpace(token.RefreshToken) && token.RefreshToken != oauth.RefreshToken)
        {
            _accounts?.RotateRefreshToken(oauth, token.RefreshToken);
            _identities?.RotateRefreshToken(oauth, token.RefreshToken);
            oauth = oauth with { RefreshToken = token.RefreshToken };
        }
        if (token.ExpiresIn > 0)
            _tokens[key] = new(_accounts is null && _identities is null ? requestedRefreshToken : oauth.RefreshToken,
                token.AccessToken, _clock.GetUtcNow().AddSeconds(Math.Max(0, token.ExpiresIn - Math.Min(30, token.ExpiresIn / 10))));
        return token.AccessToken;
        }
        finally { _refresh.Release(); }
    }

    private async Task<FileStream?> RefreshLeaseAsync(CancellationToken token)
    {
        var store = _accounts?.StorePath ?? _identities?.StorePath;
        if (store is null) return null;
        var path = Path.Combine(Path.GetDirectoryName(store)!, ".oauth-refresh.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var deadline = _clock.GetUtcNow().AddSeconds(30);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 && _clock.GetUtcNow() < deadline)
            { await Task.Delay(25, token); }
        }
    }

    public Task<OAuthCredential> AuthorizeAsync(string providerName, string address,
        string? protocol = MailProtocols.Imap, bool includeSmtp = false, CancellationToken cancellationToken = default)
        => AuthorizeAsync(providerName, address, protocol, includeSmtp, null, cancellationToken);

    public async Task<OAuthCredential> AuthorizeAsync(string providerName, string address,
        string? protocol, bool includeSmtp, Action<string>? trace, CancellationToken cancellationToken = default)
    {
        var provider = Provider(providerName);
        var scopes = ScopesFor(providerName, protocol, includeSmtp);
        var verifierBytes = RandomNumberGenerator.GetBytes(64);
        var verifier = Base64Url(verifierBytes);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(24));
        var port = FreePort();
        var redirect = $"http://localhost:{port}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirect);
        listener.Start();

        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = provider.ClientId,
            ["redirect_uri"] = redirect,
            ["response_type"] = "code",
            ["scope"] = string.Join(' ', scopes),
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["login_hint"] = address
        };
        if (providerName.Equals("gmail", StringComparison.OrdinalIgnoreCase))
        {
            parameters["access_type"] = "offline";
            parameters["prompt"] = "consent";
        }

        trace?.Invoke("browser_opening");
        OpenInBrave(provider.AuthorizationEndpoint + "?" + FormUrlEncoded(parameters));
        var context = await listener.GetContextAsync().WaitAsync(cancellationToken);
        trace?.Invoke("browser_callback_received");
        var query = context.Request.QueryString;
        var response = "<html><body style='font-family:Segoe UI'><h3>Sign-in response received.</h3><p>Return to Mailkeep for the authorization and server test result.</p></body></html>";
        var responseBytes = Encoding.UTF8.GetBytes(response);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = responseBytes.Length;
        await context.Response.OutputStream.WriteAsync(responseBytes, cancellationToken);
        context.Response.Close();

        if (!string.Equals(query["state"], state, StringComparison.Ordinal))
            throw new InvalidOperationException("OAuth state mismatch.");
        if (!string.IsNullOrWhiteSpace(query["error"]))
            throw new InvalidOperationException(query["error_description"] ?? query["error"]);
        var code = query["code"] ?? throw new InvalidOperationException("Provider returned no authorization code.");

        var form = new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = provider.ClientId,
            ["redirect_uri"] = redirect,
            ["grant_type"] = "authorization_code",
            ["code_verifier"] = verifier
        };
        if (!string.IsNullOrWhiteSpace(provider.ClientSecret)) form["client_secret"] = provider.ClientSecret;
        trace?.Invoke("token_exchange_started");
        var token = await PostTokenAsync(provider.TokenEndpoint, form, cancellationToken);
        trace?.Invoke("token_exchange_completed");
        if (string.IsNullOrWhiteSpace(token.RefreshToken))
            throw new InvalidOperationException("Provider returned no refresh token.");
        return new OAuthCredential(provider.ClientId, provider.ClientSecret, token.RefreshToken, scopes);
    }

    public static IReadOnlyList<string> ScopesFor(string provider, string? protocol, bool includeSmtp = false)
    {
        if (provider.Equals("gmail", StringComparison.OrdinalIgnoreCase)) return ["https://mail.google.com/"];
        if (!IsMicrosoft(provider)) throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported provider.");
        var scopes = new List<string> { "offline_access" };
        if (protocol == MailProtocols.Pop3) scopes.Add("https://outlook.office.com/POP.AccessAsUser.All");
        else if (protocol == MailProtocols.Imap) scopes.Add("https://outlook.office.com/IMAP.AccessAsUser.All");
        if (includeSmtp) scopes.Add("https://outlook.office.com/SMTP.Send");
        return scopes;
    }

    public static string DefaultHost(string provider, string protocol = MailProtocols.Imap) => provider.ToLowerInvariant() switch
    {
        "gmail" => protocol == MailProtocols.Pop3 ? "pop.gmail.com" : "imap.gmail.com",
        "outlook" => "outlook.office365.com",
        "microsoft" => "outlook.office365.com",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported provider.")
    };

    private async Task<TokenResponse> PostTokenAsync(string endpoint, Dictionary<string, string> values, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync(endpoint, new FormUrlEncodedContent(values), cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OAuth token request failed ({(int)response.StatusCode}): {json}");
        return JsonSerializer.Deserialize<TokenResponse>(json) ?? throw new InvalidOperationException("Invalid OAuth token response.");
    }

    private static ProviderDefinition Provider(string provider) => provider.ToLowerInvariant() switch
    {
        "gmail" => new("https://accounts.google.com/o/oauth2/v2/auth", "https://oauth2.googleapis.com/token",
            GoogleClientId, GoogleClientSecret),
        "outlook" or "microsoft" => new("https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
            "https://login.microsoftonline.com/common/oauth2/v2.0/token",
            MicrosoftClientId, ""),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported provider.")
    };

    private static bool IsMicrosoft(string provider)
        => provider.Equals("outlook", StringComparison.OrdinalIgnoreCase)
           || provider.Equals("microsoft", StringComparison.OrdinalIgnoreCase);

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void OpenInBrave(string url)
        => BrowserLauncher.Open(url);

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string FormUrlEncoded(IReadOnlyDictionary<string, string> values)
        => string.Join('&', values.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));

    private sealed record ProviderDefinition(string AuthorizationEndpoint, string TokenEndpoint, string ClientId, string ClientSecret);
    private sealed record TokenResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("access_token")] string AccessToken,
        [property: System.Text.Json.Serialization.JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: System.Text.Json.Serialization.JsonPropertyName("expires_in")] int ExpiresIn = 0);
}
