# Sending failures

When a queued send fails, Mailkeep shows the failed-message count, confirms that
the messages remain in Outbox, and suggests trying Send/Receive later. The dialog
does not display the mail library's raw exception or guess that a provider has an
outage. Both Send Outbox and immediate sending after compose use this message.

Full returned errors, with outgoing message IDs and a timestamp, are appended to
`errors.log` under the active Mailkeep root. The default Windows path is
`%LOCALAPPDATA%\Mailkeep\errors.log`. The dialog shows the actual path when the
write succeeds, or says that the log could not be written if it fails. Logging is
asynchronous and does not change retry state or transport security settings.

The Fetch Log and `mailkeep log` describe receiving attempts; they are separate
from this UI error log. Raw send errors also remain on the corresponding queued
message for diagnostic inspection.
