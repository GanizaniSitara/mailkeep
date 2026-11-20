# Connected changes in the background

Connected delete, permanent delete, server move and read-state changes write a
`remote_operations` row and apply their local effect in one SQLite transaction.
Delete/move tombstones prevent an overlapping fetch from resurrecting the old
occurrence. Pending read changes override stale fetched flags. The CLI's explicit
`move --server --execute` uses the same queue and returns `remote_queued: true`;
preview remains read-only.

The UI removes selected rows immediately and shows a saving status while the
local transaction runs off the UI thread. It reports the change as queued only
after that transaction commits. A failed save restores the current list; normal
application exit waits for these local writes, without waiting for the provider.

Connected total badges, the true unread count and the visible folder count change
with the removed rows, before the local write or any server acknowledgement.
The account tooltip and status strip show total/unread explicitly. Unread is
separate from the existing added-since-viewed count. Pending operations refresh
after each local commit and every two seconds even during a slow network attempt.

The running WinForms app checks every two seconds, independently of automatic
receiving. It is the only scheduler: no service, scheduled task or extra sync
process is installed. Closing cancels network work and preserves unacknowledged
operations for the next launch. CLI work therefore waits if the app is closed.

A per-store OS file lock prevents overlapping app instances from delivering the
same queue concurrently. Operations run in insertion order per account/mailbox;
a failed head blocks later work in that mailbox. Independent mailboxes use up to
four slots. Consecutive compatible operations batch up to 50 UIDs. Attempts have
a 15-second deadline and failures retry after five seconds. The status strip
shows pending/retrying counts and the error in its tooltip. `mailkeep log` records
`remote-queue:delete`, `:purge`, `:move` and `:read` attempts; `scanned` counts
acknowledged operations and failed attempts retain their error.

Every operation checks UIDVALIDITY before changing the server. Automatic moves
require native IMAP MOVE: replay after a lost acknowledgement cannot duplicate
mail through a COPY fallback. Permanent deletion requires UIDPLUS and expunges
only the requested UIDs. Missing Trash or unsupported capabilities leave work
pending with an error; there is no mailbox-wide expunge fallback. Completed
queue rows remain for auditing; pending work is never discarded after retries.

## Verification

`dotnet test tests/Mailkeep.Tests/Mailkeep.Tests.csproj --no-restore` covers
restart durability, local/queue rollback, read/move ordering, fetch reconciliation,
concurrent worker exclusion, cancellation, UIDVALIDITY, STORE/MOVE/UID EXPUNGE,
and lost MOVE acknowledgement against a loopback IMAP fixture.

The native `remote-queue` smoke scenario uses a slow/refusing loopback provider
by default. Explicit `--real-account NAME` creates a unique temporary server folder
with 20 marked fixture messages, changes only its isolated account endpoint to
unreachable `192.0.2.1`, exercises the real delete confirmation and list update,
then restores the endpoint and verifies all 20 messages in provider Trash. It
removes only its own marked messages and test folder afterwards. It sends no
SMTP messages, changes no live account configuration and disables no adapter.
`--cli PATH` additionally checks the actual CLI log and saves it beside the
optional screenshot. Account credentials remain in memory/encrypted account
storage and are never printed. This is an explicit opt-in real mailbox write test.
