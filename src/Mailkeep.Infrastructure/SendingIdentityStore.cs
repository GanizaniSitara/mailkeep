using System.Text.Json;
using System.Text.Json.Serialization;
using Mailkeep.Core;

namespace Mailkeep.Infrastructure;

public sealed class SendingIdentityStore
{
    private readonly AppPaths _paths;
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public SendingIdentityStore(AppPaths paths) => _paths = paths;

    public IReadOnlyList<SendingIdentity> Load()
    {
        if (!File.Exists(_paths.SendingIdentitiesPath)) return [];
        var bytes = WindowsDpapi.Unprotect(File.ReadAllBytes(_paths.SendingIdentitiesPath));
        return JsonSerializer.Deserialize<Envelope>(bytes, _json)?.Identities ?? [];
    }

    public SendingIdentity? Find(string id)
        => Load().FirstOrDefault(identity => identity.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public SendingIdentity Require(string id)
        => Find(id) ?? throw new KeyNotFoundException($"Sending identity {id} not found.");

    public void Save(IEnumerable<SendingIdentity> identities)
    {
        _paths.EnsureDirectories();
        var ordered = OneDefault(identities.OrderBy(i => i.Email).ToArray());
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, ordered), _json);
        var temporary = _paths.SendingIdentitiesPath + ".tmp";
        File.WriteAllBytes(temporary, WindowsDpapi.Protect(payload));
        File.Move(temporary, _paths.SendingIdentitiesPath, true);
    }

    public void Upsert(SendingIdentity identity)
    {
        var identities = Load().ToList();
        var index = identities.FindIndex(value => value.Id.Equals(identity.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) identities[index] = identity; else identities.Add(identity);
        if (identity.IsDefault)
            for (var other = 0; other < identities.Count; other++)
                if (!identities[other].Id.Equals(identity.Id, StringComparison.OrdinalIgnoreCase))
                    identities[other] = identities[other] with { IsDefault = false };
        Save(identities);
    }

    public void SetDefault(string id)
    {
        _ = Require(id);
        Save(Load().Select(identity => identity with { IsDefault = identity.Id.Equals(id, StringComparison.OrdinalIgnoreCase) }));
    }

    public bool Remove(string id)
    {
        var identities = Load().ToList();
        if (identities.RemoveAll(identity => identity.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) == 0) return false;
        Save(identities);
        return true;
    }

    /// <summary>
    /// Any change to where or how an identity authenticates invalidates an earlier successful test, so the
    /// identity drops back to needing authorization and cannot send until it has been tested again.
    /// </summary>
    public static SendingIdentity ResetTestIfConnectionChanged(SendingIdentity before, SendingIdentity after)
    {
        var unchanged = before.Smtp == after.Smtp
            && string.Equals(before.Provider, after.Provider, StringComparison.OrdinalIgnoreCase)
            && string.Equals(before.EffectiveUsername, after.EffectiveUsername, StringComparison.OrdinalIgnoreCase)
            && SameCredential(before.OAuth, after.OAuth)
            && string.Equals(before.Password, after.Password, StringComparison.Ordinal);
        return unchanged ? after : after with { Status = SendingIdentityStates.NeedsAuthorization, LastTestError = null };
    }

    /// <summary>Compares what the credential actually authenticates with, by value.
    ///
    /// Reference equality is wrong here and quietly destructive: identities are reloaded from disk on every
    /// save, so an untouched credential arrives as a different object each time and every edit — even just
    /// choosing a default From address — looked like a credential change and revoked the authorization.
    /// Record equality is no good either, because the scopes list would still compare by reference.</summary>
    private static bool SameCredential(OAuthCredential? before, OAuthCredential? after)
    {
        if (before is null || after is null) return before is null && after is null;
        return string.Equals(before.ClientId, after.ClientId, StringComparison.Ordinal)
            && string.Equals(before.ClientSecret, after.ClientSecret, StringComparison.Ordinal)
            && string.Equals(before.RefreshToken, after.RefreshToken, StringComparison.Ordinal);
    }

    // Exactly one identity may be the default; the first flagged wins so a stale file self-heals on save.
    private static IReadOnlyList<SendingIdentity> OneDefault(IReadOnlyList<SendingIdentity> identities)
    {
        var chosen = identities.FirstOrDefault(identity => identity.IsDefault) ?? identities.FirstOrDefault();
        return chosen is null ? identities
            : identities.Select(identity => identity with { IsDefault = ReferenceEquals(identity, chosen) }).ToArray();
    }

    private sealed record Envelope([property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("identities")] IReadOnlyList<SendingIdentity> Identities);
}
