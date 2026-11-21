using Mailkeep.Core;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Mailkeep.Infrastructure;

public sealed record SendResult(string Id, bool Ok, string? Error);

public interface IOutgoingTransport
{
    Task SendAsync(SendingIdentity identity, MimeMessage message, CancellationToken cancellationToken = default);

    /// <summary>Connects and authenticates only, so an identity can be proven before any mail exists.</summary>
    Task VerifyAsync(SendingIdentity identity, CancellationToken cancellationToken = default);
}

public sealed class SmtpOutgoingTransport : IOutgoingTransport
{
    private readonly OAuthClient _oauth;

    public SmtpOutgoingTransport(OAuthClient oauth) => _oauth = oauth;

    public async Task SendAsync(SendingIdentity identity, MimeMessage message, CancellationToken cancellationToken = default)
    {
        if (!identity.IsReady) throw new InvalidOperationException($"Sending identity {identity.Email} is not ready.");
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var slot = await AcquireSlotAsync(identity, cancellationToken);
                using var client = await ConnectAsync(identity, cancellationToken);
                await client.SendAsync(message, cancellationToken);
                // SMTP has accepted the message. A failed QUIT must never cause another delivery.
                try { await client.DisconnectAsync(true, cancellationToken); }
                catch (Exception) { }
                return;
            }
            catch (SmtpCommandException error) when (attempt == 0 && (int)error.StatusCode is >= 400 and < 500
                && error.Message.Contains("4.3.2", StringComparison.Ordinal))
            {
                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
            }
        }
    }

    public async Task VerifyAsync(SendingIdentity identity, CancellationToken cancellationToken = default)
    {
        using var slot = await AcquireSlotAsync(identity, cancellationToken);
        using var client = await ConnectAsync(identity, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }

    private static async Task<FileStream> AcquireSlotAsync(SendingIdentity identity, CancellationToken token)
    {
        var account = identity.Smtp.Host.Trim().ToUpperInvariant() + "\n" + identity.EffectiveUsername.Trim().ToUpperInvariant();
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(account)));
        var path = Path.Combine(Path.GetTempPath(), "mailkeep-smtp-" + key + ".lock");
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
            {
                await Task.Delay(200, token);
            }
        }
    }

    private async Task<SmtpClient> ConnectAsync(SendingIdentity identity, CancellationToken cancellationToken)
    {
        var outgoing = identity.Smtp;
        var client = new SmtpClient();
        try
        {
            await client.ConnectAsync(outgoing.Host, outgoing.Port, Security(outgoing.Security), cancellationToken);
            if (identity.OAuth is not null)
                await client.AuthenticateAsync(new SaslMechanismOAuth2(identity.EffectiveUsername,
                    await _oauth.AccessTokenAsync(identity, cancellationToken)), cancellationToken);
            else
                await client.AuthenticateAsync(identity.EffectiveUsername,
                    identity.Password ?? throw new InvalidOperationException($"Sending identity {identity.Email} has no SMTP credential."), cancellationToken);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static SecureSocketOptions Security(string value) => value switch
    {
        MailSecurityModes.SslOnConnect => SecureSocketOptions.SslOnConnect,
        MailSecurityModes.StartTls => SecureSocketOptions.StartTls,
        MailSecurityModes.Auto => SecureSocketOptions.Auto,
        MailSecurityModes.None => SecureSocketOptions.None,
        _ => throw new InvalidDataException($"Unsupported SMTP security mode '{value}'.")
    };
}

public sealed class MailSendService
{
    private readonly SendingIdentityStore _identities;
    private readonly MailDatabase _database;
    private readonly RawBlobStore _blobs;
    private readonly IOutgoingTransport _transport;

    public MailSendService(SendingIdentityStore identities, MailDatabase database, RawBlobStore blobs, IOutgoingTransport transport)
    {
        _identities = identities;
        _database = database;
        _blobs = blobs;
        _transport = transport;
    }

    public OutgoingMessage SaveDraft(string sendingIdentityId, IReadOnlyList<string> to, string subject, string bodyText,
        IReadOnlyList<string>? cc = null, IReadOnlyList<string>? bcc = null, string? id = null,
        IReadOnlyList<string>? attachmentPaths = null, string? bodyHtml = null)
    {
        RequireIdentity(sendingIdentityId);
        var existing = id is null ? null : _database.GetOutgoing(id);
        if (existing is not null && existing.Status != OutgoingStates.Draft)
            throw new InvalidOperationException("Only drafts can be edited.");
        var rawPath = existing?.RawPath;
        // Snapshot before changing the draft. A missing file leaves the previous draft intact, and
        // later edits or deletion of the originals cannot change the saved attachment bytes.
        if (attachmentPaths is not null)
        {
            rawPath = null;
            if (attachmentPaths.Count > 0)
            {
                var builder = new BodyBuilder { TextBody = "" };
                foreach (var path in attachmentPaths) builder.Attachments.Add(path);
                using var snapshot = new MimeMessage { Body = builder.ToMessageBody() };
                using var bytes = new MemoryStream();
                snapshot.WriteTo(bytes);
                rawPath = _blobs.Put(bytes.ToArray());
            }
        }
        return _database.SaveDraft(sendingIdentityId, to, cc ?? [], bcc ?? [], subject, bodyText, id, rawPath, bodyHtml);
    }

    public bool DiscardDraft(string id) => _database.DeleteDraft(id);

    public OutgoingMessage ReturnToDraft(string id) => _database.ReturnOutgoingToDraft(id);

    /// <summary>
    /// Proves a Sending Identity by authenticating against its SMTP server without sending mail, and records
    /// the verdict so composing and queueing stay blocked until an identity has actually been tested.
    /// </summary>
    public async Task<SendingIdentityTest> TestAsync(string id, CancellationToken cancellationToken = default)
    {
        var identity = RequireIdentity(id);
        if (!identity.HasCredential)
        {
            var unconfigured = identity with
            {
                Status = SendingIdentityStates.NeedsAuthorization,
                LastTestError = "No SMTP credential is configured."
            };
            _identities.Update(id, current => current with { Status = unconfigured.Status, LastTestError = unconfigured.LastTestError });
            return new SendingIdentityTest(identity.Id, identity.Email, false, unconfigured.Status, unconfigured.LastTestError);
        }
        try
        {
            await _transport.VerifyAsync(identity, cancellationToken);
            _identities.Update(id, current => current with { Status = SendingIdentityStates.Ready, LastTestError = null });
            return new SendingIdentityTest(identity.Id, identity.Email, true, SendingIdentityStates.Ready, null);
        }
        catch (Exception exception)
        {
            _identities.Update(id, current => current with { Status = SendingIdentityStates.FailedTest, LastTestError = exception.Message });
            return new SendingIdentityTest(identity.Id, identity.Email, false, SendingIdentityStates.FailedTest, exception.Message);
        }
    }

    public async Task<OutgoingMessage> QueueAsync(string id, CancellationToken cancellationToken = default)
    {
        var draft = _database.GetOutgoing(id) ?? throw new KeyNotFoundException("Draft not found.");
        if (draft.Status != OutgoingStates.Draft) throw new InvalidOperationException("Only a draft can be queued.");
        if (draft.To.Count == 0 && draft.Cc.Count == 0 && draft.Bcc.Count == 0)
            throw new InvalidDataException("At least one recipient is required before queueing.");
        var identity = RequireIdentity(draft.SendingIdentityId);
        if (!identity.IsReady) throw new InvalidOperationException($"Sending identity {identity.Email} needs authorization or a successful test.");
        using var message = BuildMime(draft, identity);
        await using var raw = new MemoryStream();
        await message.WriteToAsync(raw, cancellationToken);
        var rawPath = _blobs.Put(raw.ToArray());
        _database.QueueOutgoing(id, rawPath);
        return _database.GetOutgoing(id)!;
    }

    public async Task<IReadOnlyList<SendResult>> SendPendingAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<SendResult>();
        if (cancellationToken.IsCancellationRequested) return results;
        foreach (var accepted in _database.ListOutgoing(OutgoingStates.SentUnrecorded, 1000))
        {
            if (cancellationToken.IsCancellationRequested) return results;
            results.Add(await FinishAcceptedAsync(accepted));
        }
        while (!cancellationToken.IsCancellationRequested && _database.ClaimNextOutgoing() is { } outgoing)
            results.Add(await SendClaimedAsync(outgoing, cancellationToken));
        return results;
    }

    /// <summary>Explicitly retries only the confirmed messages, once each, regardless of retry delay.</summary>
    public Task<SendResult> CompleteAcceptedAsync(string id)
    {
        var message = _database.GetOutgoing(id);
        if (message is not { Status: OutgoingStates.SentUnrecorded })
            throw new InvalidOperationException("Only an accepted receipt can be recorded without transport.");
        return FinishAcceptedAsync(message);
    }

    public async Task<IReadOnlyList<SendResult>> SendQueuedAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        var results = new List<SendResult>();
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            if (cancellationToken.IsCancellationRequested) break;
            var outgoing = _database.ClaimOutgoing(id);
            if (outgoing is not null)
                results.Add(await SendClaimedAsync(outgoing, cancellationToken));
            else if (_database.GetOutgoing(id) is { Status: OutgoingStates.SentUnrecorded } accepted)
                results.Add(await FinishAcceptedAsync(accepted));
            else
                results.Add(new SendResult(id, false, "The message is no longer waiting in the Outbox."));
        }
        return results;
    }

    private async Task<SendResult> SendClaimedAsync(OutgoingMessage outgoing, CancellationToken cancellationToken)
    {
        using var lease = _database.AcquireOutgoingLease(outgoing.Id);
        var current = _database.GetOutgoing(outgoing.Id);
        if (current is null || current.Status != OutgoingStates.Sending || current.UpdatedAt != outgoing.UpdatedAt)
            return new SendResult(outgoing.Id, false, "The send claim changed before transport started; nothing was transmitted.");
        MimeMessage? message = null;
        try
        {
            var identity = RequireIdentity(outgoing.SendingIdentityId);
            if (outgoing.RawPath is null) throw new InvalidDataException("Queued message has no MIME blob.");
            using (var stream = _blobs.OpenRead(outgoing.RawPath))
                message = await MimeMessage.LoadAsync(stream, cancellationToken);
            await _transport.SendAsync(identity, message, cancellationToken);
        }
        catch (Exception exception)
        {
            message?.Dispose();
            _database.MarkOutgoingFailed(outgoing.Id, exception.Message);
            return new SendResult(outgoing.Id, false, exception.Message);
        }

        using (message) return await FinishAcceptedAsync(outgoing, message);
    }

    private async Task<SendResult> FinishAcceptedAsync(OutgoingMessage outgoing, MimeMessage? message = null)
    {
        // SMTP has accepted delivery. Even cancellation or a permanent storage failure can
        // only delay local recording. Neither this method nor receipt recovery invokes SMTP.
        var receiptRecorded = outgoing.Status == OutgoingStates.SentUnrecorded;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!receiptRecorded)
                {
                    _database.RecordSmtpAcceptance(outgoing.Id);
                    receiptRecorded = true;
                }
                _database.MarkOutgoingSent(outgoing.Id);
                break;
            }
            catch (Exception error)
            {
                if (attempt == 2)
                    return new SendResult(outgoing.Id, true, "SMTP accepted this message. Recording delivery failed: " + error.Message);
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)));
            }
        }

        try
        {
            using var retained = _blobs.OpenRead(outgoing.RawPath!);
            using var recovered = message is null ? MimeMessage.Load(retained) : null;
            retained.Position = 0;
            await _database.UpsertMimeMessageAsync(MailContract.LiveSource, outgoing.SendingIdentityId, LocalFolderKinds.Sent,
                "sent:" + outgoing.Id, null, 0, message ?? recovered!, retained, true);
            return new SendResult(outgoing.Id, true, null);
        }
        catch (Exception exception)
        {
            return new SendResult(outgoing.Id, true, "Sent, but the local Sent copy failed: " + exception.Message);
        }
    }

    private SendingIdentity RequireIdentity(string id)
        => _identities.Load().FirstOrDefault(identity => identity.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
           ?? throw new KeyNotFoundException($"Sending identity {id} not found.");

    private MimeMessage BuildMime(OutgoingMessage draft, SendingIdentity identity)
    {
        var message = new MimeMessage
        {
            Date = DateTimeOffset.UtcNow,
            MessageId = $"<{draft.Id}@mailkeep.local>",
            Subject = draft.Subject,
            // A draft with an HTML body goes as both, the way every client sends a formatted message, so a
            // plain-text reader still gets the words.
            Body = draft.BodyHtml is null
                ? new TextPart("plain") { Text = draft.BodyText }
                : new BodyBuilder { TextBody = draft.BodyText, HtmlBody = draft.BodyHtml }.ToMessageBody()
        };
        message.From.Add(new MailboxAddress(identity.DisplayName, identity.Email));
        if (!string.IsNullOrWhiteSpace(identity.ReplyTo)) message.ReplyTo.Add(MailboxAddress.Parse(identity.ReplyTo));
        Add(message.To, draft.To);
        Add(message.Cc, draft.Cc);
        Add(message.Bcc, draft.Bcc);
        if (draft.RawPath is not null)
        {
            using var snapshot = MimeMessage.Load(_blobs.FullPath(draft.RawPath));
            var body = new Multipart("mixed") { message.Body };
            // Clone MIME parts while their source message is alive; the queued message owns its copies.
            foreach (var attachment in snapshot.Attachments)
            {
                using var bytes = new MemoryStream();
                attachment.WriteTo(bytes);
                bytes.Position = 0;
                body.Add(MimeEntity.Load(bytes));
            }
            message.Body = body;
        }
        return message;
    }

    private static void Add(InternetAddressList list, IEnumerable<string> addresses)
    {
        foreach (var address in addresses) list.Add(MailboxAddress.Parse(address));
    }
}
