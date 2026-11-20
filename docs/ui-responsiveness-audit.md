# UI responsiveness audit — 11 November 2026

A receive-completion recount previously blocked the live window for 1.1–13 seconds.
A private SQLite lock reproduced a 3054 ms receive stall, then a 3154 ms stall
through the general sidebar renderer.

The renderer now requires `SidebarData`. All 15 remaining calls that implicitly
recounted folders have been replaced by asynchronous reads. Newer sidebar loads
supersede older ones; painting is kept on the window thread.

The wider source audit also covered these paths:

| Path | Change | Regression evidence |
| --- | --- | --- |
| Folder creation, rename, move, delete and archive | Writes and recounts run off the UI thread; overlapping folder changes and exit during writes are refused | `ui-blocking`, `folders`, `filed-folder-delete`, `archive-workflow` |
| Accounts, Fetch Log, Outbox and connected refresh | Data reads and completion recounts run on workers | `account-authorize`, `receive-scope`, `compose-send` |
| Address book and autocomplete | Reads, writes and import run on workers; typing uses available suggestions, Check Names awaits loading; late suggestions use current text | `ui-blocking`, `addressing`, `compose-input` |
| HTML reading | Read picture permissions on a worker, then render a per-read snapshot; late reads cannot replace a newer selection | `ui-blocking`, `image-policies`, `startup-reading` |
| Attachments | Extraction and copying run on workers; save captures filenames from the original message | `reading`, `outbox-edit` |
| Archive browsing and management | File scans run on workers; late folder/search results are discarded | `archive-tree`, `archive-workflow` |
| Preferences and Options | Resize/reorder writes coalesce by key; Options captures inputs and awaits its save before closing | `ui-blocking`, `toolbar-find-layout`, `archive-toolbar`, `filing-shortcuts` |

The audit found no synchronous HTTP/IMAP/SMTP operations or process-exit waits
in the interactive WinForms sources. Mailto handoff has a bounded 500 ms pipe
connection outside normal mailbox interaction. The main window intentionally
waits at final close for its layout and preference writes to finish.

This is not a claim that every operation is free of UI-thread I/O. Small scalar
setting reads in dialog construction and local generated-document writes remain;
they have not been established as the measured multi-second recount stalls.
Profile those paths if further delays are reported rather than treating the
absence of a recount as proof that the whole window is always responsive.

A separate compose-window regression initially reproduced a WebView2 focus
exception on both the old and new builds. Follow-up diagnostics established
that the fixture waited for a plain-text box while the working HTML editor was
active. Its timeout then left a modeless composer alive as its STA exited;
the later focus callback obscured the original assertion with a COM exception.
The fixture now waits for the actual seeded document, edits HTML when available,
and closes its composers in `finally` before leaving the STA. A deliberate
assertion failure verifies cleanup preserves the original failure. Both lifecycle
and cleanup checks pass against the unchanged deployed GUI. No product fix or
additional deployment is required for this reproduced fault. The previously
attempted product lifecycle guards remain removed.

No live mailbox was copied or modified for these tests.
