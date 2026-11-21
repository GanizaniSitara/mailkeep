using System.Text.Json.Serialization;

namespace Mailkeep.Core;

public static class MailboxModes
{
    public const string Connected = "connected";
    public const string LocalKeep = "local_keep";
}

public static class MailProtocols
{
    public const string Imap = "imap";
    public const string Pop3 = "pop3";
}

public static class MailSecurityModes
{
    public const string SslOnConnect = "ssl_on_connect";
    public const string StartTls = "starttls";
    public const string Auto = "auto";
    public const string None = "none";
}

public sealed record IncomingServerSettings(
    [property: JsonPropertyName("protocol")] string Protocol,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("security")] string Security = MailSecurityModes.SslOnConnect);

public sealed record OutgoingServerSettings(
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("security")] string Security = MailSecurityModes.StartTls);

public sealed record OAuthCredential(
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("client_secret")] string ClientSecret,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("scopes")] IReadOnlyList<string>? Scopes = null);

public sealed record KeepAliveSettings(
    [property: JsonPropertyName("enabled")] bool Enabled = true,
    [property: JsonPropertyName("interval_hours")] int IntervalHours = 24);

public sealed record MailAccount(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("mailbox")] string Mailbox,
    [property: JsonPropertyName("folder_label")] string FolderLabel,
    [property: JsonPropertyName("host")] string? Host,
    [property: JsonPropertyName("oauth")] OAuthCredential? OAuth,
    [property: JsonPropertyName("app_password")] string? AppPassword,
    [property: JsonPropertyName("keepalive")] KeepAliveSettings? KeepAlive,
    [property: JsonPropertyName("mailbox_mode")] string? MailboxMode = null,
    [property: JsonPropertyName("incoming")] IncomingServerSettings? Incoming = null,
    [property: JsonPropertyName("username")] string? Username = null)
{
    public KeepAliveSettings EffectiveKeepAlive => KeepAlive ?? new();
    public string EffectiveMailboxMode => MailboxMode ?? MailboxModes.Connected;
    public bool IsConnected => EffectiveMailboxMode == MailboxModes.Connected;
    public string EffectiveProtocol => Incoming?.Protocol ?? MailProtocols.Imap;
    public string EffectiveUsername => string.IsNullOrWhiteSpace(Username) ? Address : Username;
}

public static class SendingIdentityStates
{
    public const string NeedsAuthorization = "needs_authorization";
    public const string Ready = "ready";
    public const string FailedTest = "failed_test";
}

public sealed record SendingIdentity(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("oe_account_id")] string? OeAccountId,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("reply_to")] string? ReplyTo,
    [property: JsonPropertyName("is_default")] bool IsDefault,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("smtp")] OutgoingServerSettings Smtp,
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("oauth")] OAuthCredential? OAuth,
    [property: JsonPropertyName("password")] string? Password,
    [property: JsonPropertyName("status")] string Status = SendingIdentityStates.NeedsAuthorization,
    [property: JsonPropertyName("last_test_error")] string? LastTestError = null)
{
    public string EffectiveUsername => string.IsNullOrWhiteSpace(Username) ? Email : Username;
    public bool HasCredential => OAuth is not null || !string.IsNullOrWhiteSpace(Password);
    public bool IsReady => Status == SendingIdentityStates.Ready && HasCredential;
    public bool SupportsOAuth => Provider.Equals("gmail", StringComparison.OrdinalIgnoreCase)
        || Provider.Equals("outlook", StringComparison.OrdinalIgnoreCase)
        || Provider.Equals("microsoft", StringComparison.OrdinalIgnoreCase);
}

public sealed record SendingIdentityTest(string Id, string Email, bool Ok, string Status, string? Error);

/// <summary>What an OE Classic import took and what it deliberately left behind, so neither is silent.</summary>
public sealed record SenderImport(int Imported, IReadOnlyList<string> Skipped);

public sealed record MessageSummary(
    long Id,
    string Source,
    string Account,
    string Folder,
    string SourceKey,
    long? ServerUid,
    string Subject,
    string Sender,
    string SenderName,
    IReadOnlyList<string> Recipients,
    DateTimeOffset? Date,
    long Size,
    bool IsRead,
    int AttachmentCount,
    string Snippet,
    bool IsNew = false,
    string? MessageRef = null,
    IReadOnlyList<MessageLocation>? Locations = null,
    AttachmentMatch? AttachmentMatch = null)
{
    [JsonIgnore]
    public bool IsLocalSentCopy => Source == MailContract.LiveSource && ServerUid is null
        && SourceKey.StartsWith("sent:", StringComparison.Ordinal);
    [JsonIgnore]
    public bool IsLocalStoredCopy => Source == MailContract.LiveSource && Account == MailContract.LocalCopyAccount
        && ServerUid is null && SourceKey.StartsWith("copy:", StringComparison.Ordinal);
    public string DisplaySender => string.IsNullOrWhiteSpace(SenderName) ? Sender : SenderName;
    public string DisplaySize => Size < 1024 ? $"{Size} B" : $"{Math.Ceiling(Size / 1024d):N0} KB";
    public string AttachmentMark => AttachmentCount > 0 ? "📎" : "";
}

public sealed record AttachmentMatch(
    long OccurrenceId,
    string FileName,
    string MediaType,
    string MatchKind,
    string Snippet,
    bool TextWasTruncated,
    int MatchingAttachmentCount = 1);

[Flags]
public enum SearchMatchScope
{
    Message = 1,
    AttachmentName = 2,
    AttachmentContent = 4,
    All = Message | AttachmentName | AttachmentContent
}

public sealed record MessageLocation(
    string Source,
    string Account,
    string Folder,
    string SourceKey);

public sealed record MessageContent(
    MessageSummary Summary,
    string BodyText,
    string? BodyHtml,
    string? RawPath,
    IReadOnlyList<AttachmentInfo> Attachments)
{
    private string? _rawPath = RawPath;
    public string? RawPath { get => _rawPath ?? RawPathProvider?.Invoke(); init => _rawPath = value; }
    [JsonIgnore]
    public Func<string?>? RawPathProvider { get; init; }
    public IReadOnlyList<string> ReplyTo { get; init; } = [];
}

public sealed record AttachmentInfo(
    string FileName,
    string ContentType,
    long Size,
    bool Inline);

public sealed record SyncState(
    string Account,
    string Folder,
    uint UidValidity,
    uint MaxUid,
    DateTimeOffset? LastFetchAt,
    long SeenMaxId);

public sealed record FetchAttempt(
    long Id,
    string Account,
    string Address,
    string Source,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    long DurationMs,
    int NewCount,
    int Scanned,
    uint MaxUid,
    bool Ok,
    string? Error);

public sealed record SyncResult(
    string Account,
    int NewCount,
    int Scanned,
    uint MaxUid,
    string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>One tick of a multi-account Send/Receive: reported when an account's check starts
/// (Started, Done unchanged) and again when it completes (Done incremented).</summary>
public sealed record SyncProgress(
    string Account,
    string Address,
    int Done,
    int Total,
    bool Started);

/// <summary>Receive progress for one account. Downloaded counts messages safely stored,
/// Total is unknown until the server's new-message list is available.</summary>
public sealed record ReceiveProgress(string Account, string Address, string Stage,
    int Downloaded = 0, int? Total = null, SyncResult? Result = null);

/// <summary>What a deletion actually managed. Refused is the messages the provider would not move — one
/// already gone, say — which are left in the list rather than disappearing from it while still in the
/// mailbox.</summary>
public sealed record DeleteOutcome(int Deleted, int Refused);

/// <summary>One step of a deletion, reported as each batch of messages goes. Account is the address being
/// worked through, so a selection spanning several of them says which one it is on.</summary>
public sealed record DeleteProgress(
    string Account,
    int Done,
    int Total);

public sealed record PopInspection(
    int MessageCount,
    int NewMessageCount,
    long TotalBytes);

/// <summary>Enough of a message to tell whether another store holds the same one, without reading either body.</summary>
public sealed record MessageHead(long Id, string Account, string Folder, string Subject, string Sender, string DateUtc, bool IsRead);

public static class LocalFolderKinds
{
    public const string Inbox = "inbox";
    public const string Drafts = "drafts";
    public const string Outbox = "outbox";
    public const string Sent = "sent";
    public const string Deleted = "deleted";
    public const string User = "user";
}

public sealed record LocalFolder(
    string Key,
    string? ParentKey,
    string Name,
    string Kind,
    int SortOrder,
    string Origin = "native",
    string? OriginKey = null);

public sealed record MigrationResult(
    int AccountsImported,
    int MessagesImported,
    int FetchLogsImported,
    bool AlreadyMigrated,
    string? Warning = null);

public sealed record SearchEnvelope(
    string Query,
    int Count,
    bool Truncated,
    IReadOnlyList<MessageSummary> Results);

public static class OutgoingStates
{
    public const string Draft = "draft";
    public const string Outbox = "outbox";
    public const string Sending = "sending";
    public const string SentUnrecorded = "sent_unrecorded";
    public const string Sent = "sent";
}

public sealed record OutgoingMessage(
    string Id,
    string SendingIdentityId,
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    IReadOnlyList<string> Bcc,
    string Subject,
    string BodyText,
    string Status,
    string? RawPath,
    int AttemptCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset? SentAt,
    string? LastError,
    string? BodyHtml = null);

/// <summary>What a delivered reply or forward leaves on the message it was written from.</summary>
public static class MessageMarkKinds
{
    public const string Replied = "replied";
    public const string Forwarded = "forwarded";
}

public readonly record struct MessageMarks(bool Replied, bool Forwarded);

/// <summary>One address-book entry. Origin says where it came from: typed in here, or read from another client.</summary>
public sealed record Contact(long Id, string Name, string Email, string Notes = "", string Origin = "native", string? OriginKey = null);

/// <summary>An address mail has been sent to: how many times, and when last (ISO-8601 UTC).</summary>
public sealed record AddressUse(string Email, int Uses, string LastUsed);
