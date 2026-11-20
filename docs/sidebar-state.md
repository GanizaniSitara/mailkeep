# Remembering the sidebar

The Local Mail / Connected Accounts divider now keeps its distance in pixels
(scaled for DPI), rather than changing proportionally when the window resizes.
The existing `window.layout` setting stores `LocalSplit`; older settings retain
their fraction-based fallback. The divider is clamped to the available space.

The same setting now records the selected connected account by account name or
the local/archive folder by its existing stable folder key. Startup restores that
mailbox instead of always selecting Inbox. A removed account or folder falls back
to Inbox. Restoring a connected selection shows cached mail without triggering a
new selection-driven sync; the existing automatic sync preference still applies.
Account/folder selection and folder expansion changes use the existing one-second
debounced save, and closing saves immediately.

The isolated `sidebar-state` smoke checks resize and restart, connected row
selection, local folder restart, saving before close, and missing-mailbox fallback.
The existing `layout` smoke checks all three dividers, columns, maximized and
minimized state, offscreen bounds and malformed settings. No live mail is mutated
and no external mail service is used as a test fixture.
