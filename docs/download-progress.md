# Local Mail download progress

The desktop client's Local Mail receive passes open a modeless progress window.
It lists the participating accounts, including accounts waiting for a receive
slot, with connection/sign-in/checking states and message counts once the server
has identified new messages. Counts advance after message storage succeeds.
First-contact baselining is explicitly described as recording existing mail,
not downloading it. This does not alter the baseline or collection policy.

The window closes automatically when the pass succeeds. If any account fails,
it stays open on Errors (or reopens there if it was hidden), without activating
the window. Results remain available through View > Download progress. Select a row
to read/copy its full detail in the lower pane. Hide or the close box during a
receive hides only the progress window; collection continues. View > Download
progress reopens it, centred in the working area of Mailkeep's screen. When finished, Close dismisses it. A later Local Mail pass
replaces the previous window; the Fetch Log keeps historical results. The main
client refuses to exit during receiving rather than abandoning in-flight writes.

The window uses ShowWithoutActivation, including automatic receive passes.
It does not activate or reposition other applications. Connected-only refreshes
retain their existing status/error behavior. Standalone CLI and scheduled CLI
processes do not publish progress into the desktop window. This feature adds
no cross-process channel, receive cancellation, byte-per-second display or ETA.

The receive service exposes additive progress overloads; the old public
signatures remain for existing CLI callers. Network and store work launched by
the GUI runs on workers, with progress marshalled back to the UI context.
Late reports cannot update a replaced, completed or disposed progress window.

Validation (11 November 2026): 64 focused receive/mail/queue tests passed.
The independent Beta guest passed download-progress, receive-scope,
receive-redraw, startup-reading and compose-send across two runs on identical
product DLLs. POP and IMAP peers serve invented MIME over loopback only.
Held downloads verify responsive UI timers, Hide/reopen, no foreground theft,
account failure details, successful storage, duplicate-free repeat collection
and no server deletion. Retrieved wide/portrait screenshots were inspected;
secondary offsets and portrait sizes are simulated on one 96-DPI guest display.
These are not real-provider, TLS-certificate or physical multi-monitor tests.

An initial IMAP fixture lacked the READ-ONLY response flag; correcting the fake
server made the focused tests pass. The first GUI test expected raw server refusal
text; MailKit returns Authentication failed, so the corrected check compares the
window with the recorded fetch error. Neither correction changed product logic.
No owner mail, credential, live account operation, deployment or restart was used.

12 November initial follow-up: completion automatically hid the window, retaining
results for an explicit View > Download progress request. Positioning is explicit
in the working area of the screen containing Mailkeep; modeless CenterParent did
not centre the previous build. The updated guest regression checks screen-centred
placement, automatic dismissal for mixed results, single-account and automatic
passes, retained results and unchanged foreground ownership.

Further owner feedback on 12 November used the official [OE Classic receive
window](https://www.oeclassic.com/ws/p/pg/oe-classic-screenshot-02.png) as the
layout reference. The desktop window now has a current-operation heading,
overall account progress bar, Tasks/Errors tabs and collapsible details.
Explicitly reopened completed results have a distinct title. The owner subsequently
refined dismissal to success-only; failures stay open, matching OE Classic's
documented behavior. This is a layout reference, not a claim of full
OE Classic parity or a test of its installed client. Hide continues the pass;
receive cancellation is not implemented.

Inbox arrival behavior now depends on the actual replacement rows rather than
an automatic-receive flag. If a genuinely new first row appears in newest-first
Inbox, an idle list scrolls to the top while keeping its selected message and
preview. Recent mail-pane input (60 seconds), focused reading, multiple selected
messages, open composers or modal windows preserve position. Mouse movement and
the Send/Receive toolbar click alone do not count as mail navigation. Custom
sorts, unchanged refreshes, deletions and filling older rows at the bottom do
not trigger this scroll. There is no delayed jump once the user stops working.

Restoring the same Inbox selection does not reload its existing reading pane,
reset a text selection or cancel an image update. A real selection change and
genuine multi-selection still update/clear the preview normally. Arrival decisions
are made when rows render, so interaction while a receive/recount waits is honored.
