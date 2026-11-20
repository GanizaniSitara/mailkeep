# Mailkeep

A Windows-first, native .NET mail client in the Outlook Express tradition: a three-pane window over
a local store you own, with search that reaches every message on the machine.

## The solution

- `Mailkeep.WinForms` — the client: folder tree, message list, reading pane, message windows,
  compose, Find Message, accounts and Send/Receive.
- `Mailkeep.Cli` — `mailkeep`, a JSON surface over the same store for scripts and agents.
- `Mailkeep.Infrastructure` — MailKit IMAP/POP3 and OAuth2, DPAPI account storage, SQLite with an
  FTS5 catalogue, and content-addressed raw messages.
- `Mailkeep.Core` — implementation-neutral mail contract and models.

Migration tooling is deliberately **not** here. It lives in a separate repository so the client can
never depend on it; a test fails the build if a reference appears.

## Building

Needs the .NET 10 SDK.

```text
dotnet build Mailkeep.sln
dotnet test Mailkeep.sln
dotnet run --project src\Mailkeep.WinForms
```

For the Windows release, use the ReadyToRun publish profile to reduce startup
compilation. Restore its matching Crossgen2 compiler package first, then publish
to staging rather than over a running application:

```text
dotnet restore src/Mailkeep.WinForms/Mailkeep.WinForms.csproj -r win-x64 -p:PublishReadyToRun=true
dotnet publish src/Mailkeep.WinForms/Mailkeep.WinForms.csproj -c Release -p:PublishProfile=WinX64Release --no-restore -o publish/win-x64.staging
```

On managed workstations, obtain approval before downloading a missing compiler
package. Startup measurements and deployment checks are in
[the desktop regression notes](docs/vm-ui-regressions.md).

## Two kinds of account

Mail is divided into two explicit modes, because the right thing to do on delete differs:

- **Connected Accounts** are server-authoritative IMAP mailboxes. Deleting moves the provider's
  message to Trash — or applies and expunges the IMAP Deleted flag where no Trash folder is
  advertised — so the deletion reaches every other device.
- **Local Mail** downloads new messages while leaving the provider's copy alone. Deleting removes
  only the local message and records a tombstone, so the same server message is not downloaded
  again. Local Mail accounts send as well as receive.

Alongside both sits the **archive**: mail imported from another client. It is read-only, because
whatever produced it remains its owner.

## Send and Receive

Send/Receive delivers anything queued in the Outbox, then checks every account, four at a time.
Progress is visible while it runs — the status bar names the accounts still in flight and counts
completions, and the taskbar button fills, turning red if a pass had failures. Accounts can also
refresh quietly in the background once their keep-alive interval lapses; the cadence is per-account
and the whole behaviour can be switched off.

Outbound mail follows a durable path: save a draft, freeze exact MIME into the Outbox, claim it once
for SMTP, retry failures, and keep a local Sent copy. CLI writes require `--execute`; without it,
`prepare` is a dry-run preview.

Automation should use `mailkeep send MESSAGE_ID --execute` after queueing its own draft.
This attempts only that message, even if other mail is waiting or the selected message is
in retry backoff. Without an ID, `send --execute` sends all due Outbox messages.
`send-self --identity I ... --execute` likewise sends only the message it just created.

## Search

One FTS5 catalogue spans Connected, Local and archived mail, so a single query reaches everything
stored locally. It understands quoted phrases, `AND` / `OR` / `NOT`, trailing-`*` prefixes, and
column filters:

```text
mailkeep search "\"quarterly invoice\""
mailkeep search "subject:invoice OR sender:amazon"
mailkeep search "ship*" --folder "Archive/2023" --limit 50
```

A malformed query never errors: it falls back to a literal search rather than failing in the user's
face. In the client, the same engine sits behind Find Message — criteria per field, a date range, an
attachments switch, and results in the same window — reachable from anywhere by a configurable
system-wide shortcut.

## Agent filing

`mailkeep folders --local` lists writable destination keys and full paths, including empty folders.
Move one numeric message ID or `msg_` search reference with a preview first:

```text
mailkeep move 123 --folder "Filed/Invoices"
mailkeep move 123 --folder "Filed/Invoices" --execute
mailkeep move 456 --folder "Archive" --server --execute
```

Local filing uses the same behavior as the app. Imported mail is adopted into Local Mail;
the original remains retained, and the JSON result gives the writable `result_id`.
Connected mail requires `--server` and moves within that provider account using the existing
IMAP move service. Server previews do not connect; the server validates the destination on execution.
Drafts and Outbox are not filing destinations. A stale search reference is refused; search again
or use the current numeric ID. Preview never initializes or changes the database or search catalog.

## Storage and privacy

Runtime data defaults to `%LOCALAPPDATA%\Mailkeep`. Accounts are encrypted with Windows DPAPI
(CurrentUser); the database is `data\mailkeep-v2.db`; raw RFC822 messages are SHA-256-addressed
under `data\blobs`. None of it belongs in Git. Pass `--root` to work against an isolated store.

HTML mail renders with scripts and web messaging disabled, active elements stripped, a restrictive
content-security policy, and remote resource loading blocked, so opening a message cannot phone
home. Clicked links leave the preview and open in the browser.
