using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mailkeep.Core;

namespace Mailkeep.Infrastructure;

public sealed class AccountStore
{
    private readonly AppPaths _paths;
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public AccountStore(AppPaths paths)
    {
        _paths = paths;
    }

    internal string StorePath => _paths.AccountsPath;

    internal void RotateRefreshToken(OAuthCredential before, string refreshToken)
        => EncryptedStoreFile.Locked(_paths.AccountsPath, () =>
        {
            var accounts = Load().Select(account => account.OAuth is { } oauth
                && oauth.ClientId == before.ClientId && oauth.RefreshToken == before.RefreshToken
                ? account with { OAuth = oauth with { RefreshToken = refreshToken } } : account).ToArray();
            if (accounts.Any(account => account.OAuth?.ClientId == before.ClientId && account.OAuth.RefreshToken == refreshToken)) SaveLocked(accounts);
            return true;
        });

    public IReadOnlyList<MailAccount> Load()
        => EncryptedStoreFile.Locked(_paths.AccountsPath, LoadLocked);

    private IReadOnlyList<MailAccount> LoadLocked()
    {
        if (!File.Exists(_paths.AccountsPath))
        {
            return [];
        }

        return Read(_paths.AccountsPath);
    }

    /// <summary>Reads an account file that is not this store's own — a backup, or another store being
    /// imported. Decryption is user-scoped, so this only works for files written by the same user.</summary>
    public IReadOnlyList<MailAccount> ReadFrom(string path) => File.Exists(path) ? Read(path) : [];

    public int EnsureMailboxModes()
        => EncryptedStoreFile.Locked(_paths.AccountsPath, EnsureMailboxModesLocked);

    private int EnsureMailboxModesLocked()
    {
        var accounts = Load();
        var missing = accounts.Count(account => string.IsNullOrWhiteSpace(account.MailboxMode));
        if (missing > 0)
            Save(accounts.Select(account => string.IsNullOrWhiteSpace(account.MailboxMode)
                ? account with { MailboxMode = MailboxModes.Connected }
                : account));
        return missing;
    }

    public int EnsureServerSettings()
        => EncryptedStoreFile.Locked(_paths.AccountsPath, EnsureServerSettingsLocked);

    private int EnsureServerSettingsLocked()
    {
        var accounts = Load();
        var missing = accounts.Count(account => account.Incoming is null);
        if (missing > 0) Save(accounts);
        return missing;
    }

    public void Save(IEnumerable<MailAccount> accounts)
        => EncryptedStoreFile.Locked(_paths.AccountsPath, () => { SaveLocked(accounts); return true; });

    private void SaveLocked(IEnumerable<MailAccount> accounts)
    {
        _paths.EnsureDirectories();
        var normalized = accounts.Select(Normalize).OrderBy(account => account.Name).ToArray();
        var duplicate = normalized.GroupBy(account => (account.Address.Trim().ToLowerInvariant(), account.EffectiveMailboxMode))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException($"{duplicate.First().Address} already has a {duplicate.First().EffectiveMailboxMode} receiving account.");
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new AccountEnvelope(4, normalized), _json);
        var encrypted = WindowsDpapi.Protect(payload);
        EncryptedStoreFile.Write(_paths.AccountsPath, encrypted);
    }

    public void Upsert(MailAccount account)
        => EncryptedStoreFile.Locked(_paths.AccountsPath, () => { UpsertLocked(account); return true; });

    private void UpsertLocked(MailAccount account)
    {
        var accounts = Load().ToList();
        var index = accounts.FindIndex(a => string.Equals(a.Name, account.Name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            accounts[index] = account;
        }
        else
        {
            accounts.Add(account);
        }
        Save(accounts);
    }

    public bool Remove(string name)
        => EncryptedStoreFile.Locked(_paths.AccountsPath, () => RemoveLocked(name));

    private bool RemoveLocked(string name)
    {
        var accounts = Load().ToList();
        var removed = accounts.RemoveAll(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            Save(accounts);
        }
        return removed > 0;
    }

    public bool SetKeepAlive(string name, bool enabled, int intervalHours)
        => EncryptedStoreFile.Locked(_paths.AccountsPath, () => SetKeepAliveLocked(name, enabled, intervalHours));

    private bool SetKeepAliveLocked(string name, bool enabled, int intervalHours)
    {
        var accounts = Load().ToList();
        var index = accounts.FindIndex(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        accounts[index] = accounts[index] with
        {
            KeepAlive = new KeepAliveSettings(enabled, Math.Max(1, intervalHours))
        };
        Save(accounts);
        return true;
    }

    private IReadOnlyList<MailAccount> Read(string path)
    {
        var decrypted = WindowsDpapi.Unprotect(File.ReadAllBytes(path));
        var envelope = JsonSerializer.Deserialize<AccountEnvelope>(decrypted, _json);
        if (envelope?.Accounts is not null)
        {
            return envelope.Accounts;
        }

        var legacy = JsonSerializer.Deserialize<LegacyAccountEnvelope>(decrypted, _json);
        return legacy?.Accounts ?? [];
    }

    private static MailAccount Normalize(MailAccount account)
    {
        var mode = string.IsNullOrWhiteSpace(account.MailboxMode) ? MailboxModes.Connected : account.MailboxMode;
        var incoming = account.Incoming ?? DefaultIncoming(account);
        if (mode is not (MailboxModes.Connected or MailboxModes.LocalKeep))
            throw new InvalidDataException($"Unsupported mailbox mode '{mode}'.");
        if (incoming.Protocol is not (MailProtocols.Imap or MailProtocols.Pop3))
            throw new InvalidDataException($"Unsupported incoming protocol '{incoming.Protocol}'.");
        if (mode == MailboxModes.Connected && incoming.Protocol != MailProtocols.Imap)
            throw new InvalidDataException("Connected Accounts require IMAP. POP3 is available only for Local Mail.");
        if (string.IsNullOrWhiteSpace(incoming.Host) || incoming.Port is < 1 or > 65535)
            throw new InvalidDataException("Incoming server host and port are invalid.");
        return account with { MailboxMode = mode, Incoming = incoming };
    }

    private static IncomingServerSettings DefaultIncoming(MailAccount account)
    {
        var host = account.Host ?? account.Provider.ToLowerInvariant() switch
        {
            "gmail" => "imap.gmail.com",
            "outlook" or "hotmail" => "outlook.office365.com",
            _ => throw new InvalidDataException($"Account {account.Name} needs an incoming server host.")
        };
        return new IncomingServerSettings(MailProtocols.Imap, host, 993);
    }

    private sealed record AccountEnvelope(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("accounts")] IReadOnlyList<MailAccount> Accounts);

    private sealed record LegacyAccountEnvelope(
        [property: JsonPropertyName("accounts")] IReadOnlyList<MailAccount> Accounts);
}
