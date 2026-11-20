# Desktop regressions in Mailkeep Beta

## Provider presets — 11 November 2026

The superseding candidate adds Yahoo, AOL, iCloud and Fastmail to both provider
choosers and manual presets. Gmail/Hotmail keep browser authorization; the new
presets use app passwords and remain unverified until their connection tests pass.
Provider changes clear typed passwords. Defaults follow the providers' published
settings:

| Provider | Incoming | Outgoing |
| --- | --- | --- |
| Yahoo | POP3 `pop.mail.yahoo.com:995` or IMAP `imap.mail.yahoo.com:993`, SSL | `smtp.mail.yahoo.com:465`, SSL |
| AOL | POP3 `pop.aol.com:995` or IMAP `imap.aol.com:993`, SSL | `smtp.aol.com:465`, SSL |
| iCloud | IMAP `imap.mail.me.com:993`, SSL; POP disabled | `smtp.mail.me.com:587`, STARTTLS |
| Fastmail | POP3 `pop.fastmail.com:995` or IMAP `imap.fastmail.com:993`, SSL | `smtp.fastmail.com:465`, SSL |

iCloud uses the full email address for SMTP even if an incoming short username is
entered. Local IMAP setup and Properties verification now authenticate and issue
STATUS for the Inbox count without fetching messages or advancing a local baseline.
The loopback IMAP regression permits only authentication, LIST, STATUS and logout;
it and all 48 MailDatabaseTests pass, along with 19 queue/read-state regressions.
The independent Mailkeep Beta VM passed `provider-presets`, `local-pop-smtp`,
`account-authorize` and `compose-input`. Retrieved screenshots were inspected for
all four providers, local/connected layouts, chooser, Properties and compose.
Landscape and portrait secondary offsets use simulated work areas, not physical
multi-monitor/DPI coverage. The guest product DLL hashes match the staged build.
No production deployment or real-provider compatibility is claimed. The earlier
tested `ed9aecf` candidate remains preserved separately. Proton/Bridge is
deferred until requested.

Settings references: [Yahoo](https://uk.help.yahoo.com/kb/pop-settings-sln4724.html),
[AOL](https://help.aol.com/articles/how-do-i-use-other-email-applications-to-send-and-receive-my-aol-mail),
[iCloud](https://support.apple.com/en-au/102525),
[Fastmail](https://www.fastmail.help/hc/en-us/articles/1500000278342-Server-names-and-ports).
GUI results and 28 screenshots: `beta-vm/results` under that evidence root.
An independent full clone is registered as `Mailkeep Beta` for
dedicated VM testing. Cleanup removed 7.0 GB of disposable
files; compaction succeeded. Actual host allocation with the retained 331 MB
test bundle was 28.94 GB while running, within the 20–30 GB target.
its disposable test rigs/profiles were removed in Beta only.

## Combined custom Local Account setup — 11 November 2026

Custom Local Account creation now captures incoming POP3/IMAP and outgoing SMTP
settings together. SMTP can use the incoming login or a separate username and
password; Properties preserves that separate username. A blank outgoing server
still permits receiving only. Creation inspects POP waiting counts without
downloading/deleting mail, saves the paired sender, and tests SMTP authentication
without sending. A failed SMTP test retains editable settings and keeps sending
blocked. The explicit None security option now maps correctly in the receiving
transport; SSL/STARTTLS defaults are unchanged.

The baseline `local-pop-smtp` fixture failed on missing outgoing controls.
The candidate passed actual loopback POP/SMTP authentication with distinct
credentials after encrypted-store reload: two waiting messages, no RETR/DELE,
no SMTP MAIL/DATA, sender ready only after authentication. Receive-only/login
fallback and Properties username preservation are checked. Account-authorization
selection and HTML/plain compose-input also passed. All 48 MailDatabaseTests pass.

The final fixture repeat passed and produced inspected Add/Properties screenshots
in simulated landscape and portrait work areas, including a secondary offset,
on the single-screen 96-DPI the test VM guest. This is not physical multi-monitor,
high-DPI, TLS certificate or real-provider compatibility testing.

explicit-None failure, a rejected intermediate transfer, and passing
`verified-retry-vm`/`layout-vm` results. No owner account, credential, live mail
operation or production restart was used; live deployment is a separate approval.

## Extended reading and shortcuts fixtures — 11 November 2026

The two outstanding the test VM failures were reproduced against the deployed
product and corrected in the tests only. HTML paste produced `typed&nbsp;pasted`;
the assertion now normalizes that non-breaking space and requires exact text.
No editor or stored HTML behavior changed.

Reading and attachment-opening checks now use the paperclip's filename and
Save attachments menu items instead of obsolete Open/Save buttons. The save
check drives the actual shell folder picker, verifies its exact destination,
and checks both attachment contents and the embedded image in preview and
standalone views. Cross-process edit text is read with WM_GETTEXT, not
GetWindowText. Native edit notifications and the real Select Folder button
avoid accepting the picker's previous shell location.

Full reading passed after these corrections; attachment-open and full shortcuts
passed in the preceding retained run on identical product DLLs. The latter
includes native HTML/plain paste, compose tools, saving and loopback delivery.
These are three passing scenarios across runs, not a claim that the entire UI
suite passed together. Failed intermediate runs remain available.

`menu-vm`. Reading and attachment-opening screenshots were retrieved and
inspected. Guest firewall notifications recurred; no rules were changed.
Production assembly hashes stayed unchanged; no live restart or deployment
was needed.

## External-send reply/forward icons — 10 November 2026

The corrected `marks` scenario reproduced stale icons on the unchanged product
after an independent sender completed delivery. The old fixture both assumed mail
under the now-empty All Local Mail root and forced a folder reload after sending;
it now selects the actual message folder and settles compose refreshes first.

The GUI refreshes only visible message IDs asynchronously on activation and every
five seconds while active. It redraws changed icons without replacing rows,
selection, scroll or preview, retains existing marks if reading fails, and ignores
results belonging to a replaced row set. No backend, schema or MIME reader changed.

the test VM passed activation refresh, active-window timer refresh, failed-read
preservation, rendered arrow pixels, failed-send/draft semantics, loopback immediate
sending, filing and restart. Selection-anchor and startup-reading also passed.
Pane-navigation-legacy passed a bounded repeat on identical product DLLs; its first
failure coincided with a topmost guest firewall notification. The notification was
dismissed without changing rules; notifications reappeared later in the repeat.
No keyboard product code changed. All 48 MailDatabaseTests passed.

baseline, initial mixed GUI run and keyboard repeat. Retrieved icon and rendering
screenshots were inspected. The two-file GUI candidate is staged, not deployed;
the live client and owner store remain unchanged pending restart approval.

## Portable MIME read cutover — 10 November 2026

The 331-test production candidate passed the eight baseline the test VM scenarios
across retained initial and bounded repeat evidence. Bulk workflows and
startup-reading delete their fake raw cache before reading, including rendering
HTML and embedded images. The final reading/keyboard repeat passed all three
checks. Independent audits of both 30-message workflow exports verified exact
MIME, identity/state, empty journals, integrity and old occurrence history.

The generic reading fixture needed complete synthetic MIME and nonpooled setup
connections before conversion. Intermediate keyboard failures occurred while a
guest firewall notification was visible; their cause is not proven, and the
bounded repeat passed without a product keyboard change. No guest firewall
configuration, owner account, real message or external send was involved.
The production app/CLI files were subsequently deployed after owner approval;
authorization tracing remains included. Live hashes, loaded modules and three
real-message MIME/body comparisons passed. Own guest fixtures were removed,
the lease released and the test VM verified off. 

## Prototype-writer retirement candidate — 9 November 2026

The source cleanup passed eight fake-mail scenarios on the background the test VM
guest: bulk-workflow, pilot-consolidation-workflow, bulk-drag-events,
filed-folder-delete, pane-navigation-legacy, selection-anchor, startup-reading
and compose-send. The GUI and Core assemblies match the installed version;
only the backend and test runner changed. No host activation, real account,
external send, install or deployment was involved.

The first run exposed a cleanup regression: account-scoped portable bindings
were incorrectly presented as complete logical folders, hiding the imported
part of a merged list. Status now claims the complete list only for logical
bindings, with a focused regression and a passing drag workflow. Guest firewall
notifications were observed between runs; the keyboard repeat passed without a
keyboard-code or firewall-configuration change. The first keyboard failure's
cause was not established. Both failed and corrected evidence
remain available privately.

An independent Python/SQLite reader checked the corrected exported fixtures:
30 active messages in each of the ordinary and older-file-upgrade workflows,
exact pristine MIME, current IDs/folders/read state, empty journals, integrity
checks and zero prototype triggers. All ten old input occurrences remain in
the upgraded file history. Rendering, rebuilt-folder and focus-row screenshots
were inspected. The runner explicitly flushes the journal before exporting its
consistent fixture snapshots.

`independent-vm-audit.json` and `package-update.json` in the same private root.
This run's guest fixtures and bundle were removed, the lease released and the
guest shut down. Live deployment remains a separate owner-approved milestone.

## Current target — independent Mailkeep Beta

Older sections below are historical evidence, not instructions to wake other VMs.
Use `vm.py up mailkeep-beta --for RUN`
to reserve the background guest; retain and release the returned lease in finally.
The harness enforces the RAM gate and starts with `nogui`; never open or activate
a host VMware window. Beta's NIC is disconnected; shared-folder transfer and
loopback fixtures work without it. Do not change guest networking without approval.

The existing transfer driver defaults to `--vm mailkeep-beta`, loopback VNC and expected console identity `Mailkeep Beta`.
Both VNC typing and screenshot helpers accept `VM_VNC_PORT` and
`VM_VNC_EXPECT_NAME`; set both for Beta. Take and inspect a fresh guest
screenshot proving an owned console prompt before invoking the transfer driver.
It refuses a different guest identity before typing. The default transfer uses
dedicated VMware shared folders: `mailkeep-tests` maps only
the shared transfer directory for disposable bundles/results; read-only
`mailkeep-guest` maps only the guest helper directory for generated guest-script
deployment copies. Each run has a unique directory; results are copied into its
evidence directory before deleting the transfer copy. A ready marker follows the
closed result file because HGFS rename is unreliable on this guest. Retained
script handles are reported explicitly for cleanup after guest disconnect.
No firewall rule is required. The historical HTTP route requires explicit
`--transport http`. No source mail, account store or credentials belong in either
transfer route.

Guest prerequisite inventory uses `_oneshot_mailkeep_win10_inventory.ps1`, through
the same driver. It reads OS, runtime and WebView2 versions without installing
anything. Guest installs require a separate explicit owner approval. Windows 10
parity is not established until the existing synthetic workflow scenarios pass.

On 8 November the integrated product passed the seven baseline scenarios on
the test VM: pilot-consolidation-workflow, bulk-drag-events, filed-folder-delete,
pane-navigation-legacy, selection-anchor, startup-reading and compose-send.
The drag fixture was moved into an unobscured guest area after screenshots proved
a topmost firewall notification intercepted its original path; it now asserts
that both mouse endpoints belong to the fixture window. No firewall rules or
product drag behavior were changed. The same product built successfully and its
362 unit tests passed sequentially at low CPU/I/O priority. A parallel run had
four locking/cancellation failures; all four also passed in isolated runs.
These results do not establish absence of resource-sensitive contention.

The broader reading scenario still expects obsolete Open/Save attachment buttons
instead of the current paperclip menu. Extended shortcuts also failed the HTML
compose-paste assertion on this guest. Those failures are retained separately;
passing the baseline scenarios does not claim the entire GUI suite is green.
All runs used invented mail and loopback services. No host application deployment
or live-mail operation was performed for this source integration.


## Evidence boundary

Use invented MIME, reserved example addresses and loopback services. Keep live-mail inventories, folder keys, subjects, message counts, screenshots and deployment diaries outside Git. Record private evidence on the task rather than copying it into these docs. Never transfer owner mail or credential stores to a test VM.
