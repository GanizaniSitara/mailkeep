# Outlook Express shortcut audit

Audited 4–5 October 2026. Primary reference: [MIT IS&T's Outlook Express 6 guide,
pages 14–15](https://ist.mit.edu/sites/default/files/migration/products/outlookexpress/v6/outlookexpress6_v3.pdf).
This is the university's own OE training guide, rather than a modern Outlook list.
[Microsoft's current Outlook reference](https://support.microsoft.com/en-us/accessibility/outlook/keyboard-shortcuts-for-outlook)
also confirms Alt+S; its other mappings are not substituted for OE's.
Move-to-folder is confirmed by an actual OE menu screenshot in
[University of Bacău's ECDL course](https://www.ugb.ro/piatra/ecdl.pdf).
The user selected Outlook's **F4** as the primary text Find command;
[Microsoft documents F4](https://support.microsoft.com/en-us/outlook/find-and-replace-text-within-an-email-message-or-item).
OE's F3 remains an alias.

| Context | Keys | Mailkeep action/status |
| --- | --- | --- |
| Compose | Alt+S, Ctrl+Enter | Existing Send; validates, then honours immediate-send setting |
| Compose | Ctrl+S | Existing draft save |
| Compose/read | Esc | Close; compose retains save/discard/cancel prompt |
| Main/read | Ctrl+N | New compose |
| Main/read | Ctrl+R, Ctrl+Shift+R | Reply / Reply All |
| Main/read | Ctrl+F | Forward; previous Find conflict removed |
| Main/read/compose | Ctrl+Shift+F | Existing Find Message window |
| Main/read | Ctrl+M | Existing Send/Receive; F5 retained as alias |
| Main/read | Delete, Ctrl+D | Existing delete policy; reader uses its own message |
| Main/read | Shift+Delete | Permanently delete the selection after confirmation, bypassing Deleted Items; reader uses its own message |
| Main/read | Ctrl+P | Existing print |
| Main/read/compose | Ctrl+Shift+B | Existing address book |
| Main/read | Ctrl+I | Local Mail Inbox |
| Main/read | Ctrl+Y | Folder picker over existing local/imported folder tree |
| Main/read | Ctrl+Shift+V | Existing Move to Folder action and its restrictions |
| Main/read | Ctrl+period/comma, with optional Shift | Next/previous in current displayed list |
| Main/read | Ctrl+U | Next unread in current list; previous mark-unread conflict removed |
| Main | Ctrl+O, Enter | Open selection; Enter requires list focus |
| Main | Ctrl+Enter, Ctrl+Q | Mark selection read |
| Main | Ctrl+Shift+U | Mailkeep mark-unread extension |
| Main | Ctrl+A | Select list when list focused; body selection remains native |
| Read/compose | Ctrl+A/C/X/V/Z | Native text editing/selection where applicable |
| Main | Tab/Shift+Tab | Native control focus traversal |
| Compose | Tab from To | Message body by default; Options > Compose can select Cc; accepts highlighted suggestion first |
| Compose | Shift+Tab | Backwards focus traversal; does not accept address suggestions |
| Compose body | Tab | Inserts a tab |
| Main/read/compose | F4, F3 | Find Text in body, with match case and wraparound Find Next |
| Main/read | Alt+Enter | Read-only message properties and original MIME headers; no raw MIME reports unavailable |
| Compose | Ctrl+K, Alt+K | Check Names: exact or prefix matches in contacts/history; ambiguous matches require a choice; Cancel preserves address lines |
| Compose | Ctrl+Shift+S | Insert saved plain-text signature at cursor; configured in Options > Compose |
| Compose | F7 | Review spelling using installed Windows dictionaries; Change/Ignore, Finish/Cancel; rich markup preserved |
| Main/read/compose | F1 | Help topics covering reading, composing, accounts and shortcut/settings behaviour |
| Compose HTML | Ctrl+B/I/U | Existing formatting/native editor keys |
| Main | Alt+digits, Ctrl+Z | Existing filing shortcuts / undo retained |

HTML shortcuts use WebView2's WinForms KeyDown events. Actions are deferred with
BeginInvoke because its browser process is blocked during the callback (documented
in the installed SDK's WinForms XML). Native controls use ProcessCmdKey with exact
modifier matching. Unhandled editing keys retain their native behaviour. Global
search configuration rejects conflicting mail/editing chords; an older conflicting
stored chord is not registered, and the status explains why. Ctrl+Shift+F remains
available in the mail windows.

Text Find uses the existing WebView2 host script channel (`window.find`), which
works with mail-page scripts disabled. The newer SDK Find API requires interfaces
absent from this machine's installed runtime; no runtime upgrade was performed.
[Microsoft's ExecuteScriptAsync documentation](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.executescriptasync)
explicitly confirms host execution works with IsScriptEnabled=false.
F7 uses Windows' local SpellCheckerFactory; no network service or dictionary writes.
Rich-body spelling checks individual text runs and applies replacements within
them, preserving tags and attachments. A word split across inline markup is
checked as separate runs. An edited body snapshot is rejected before applying
corrections. Changing the dictionary restarts the check; Cancel leaves the body
untouched. A missing Windows dictionary reports unavailable without installing one.

## Unavailable features

- Ctrl+G, Ctrl+Shift+A and conversation +/-/arrow commands: newsgroups/grouped
  conversations are not implemented. Native folder-tree arrows retain tree navigation.
- Connected-account folder navigation is not represented in the local/imported
  folder tree; Ctrl+Y does not invent server folders.
- Imported read-only archive rows retain their existing delete restriction.
- Connected permanent deletion requires UIDPLUS; providers without it report
  unavailable rather than expunging other messages or silently moving to Trash.

Shift+Delete removes Local Mail rows and their searchable body entries immediately
and records suppression so the same source message is not imported again. Provider
copies of Local Mail remain untouched. Connected messages are deleted using only
the selected UIDs, with UID validity checked and the local cache removed after
provider acknowledgement. This is permanent mailbox deletion, not secure erasure
of database pages, MIME blob files or backups. Existing drafts use Discard.

The native `permanent-delete` regression exercises the actual Shift+Delete path,
multi-selection, cancellation, attachment access after deletion, reader ownership
and text-editor focus. Loopback IMAP tests cover selected-UID expunge, missing
UIDPLUS, changed UID validity and rejected STORE/EXPUNGE; no live mail is touched.
Reference: [MailKit targeted expunge](https://mimekit.net/docs/html/M_MailKit_Net_Imap_ImapFolder_ExpungeAsync.htm).

Read-window navigation replaces the reader with the adjacent message. A message
outside the current main list has no next/previous there. No additional features,
mail migration or change to sending policy is implied by this shortcut work.

Ctrl+K is Check Names; hyperlink insertion remains on its menu/toolbar.

## Verification

`--scenario shortcuts` in the existing UI smoke harness exercises plain/HTML
Send and Save, native HTML Alt+S, deferred browser handling, immediate/queued
settings with loopback SMTP, Forward versus Find, reader message ownership,
list navigation and read-state commands. It also covers native Tab after To in
plain/HTML bodies for both preferences and suggestion acceptance; backwards
Shift+Tab; Options OK/Cancel and reload; Check Names unique, ambiguous, unknown
and cancellation cases; text Find next/wrap/HTML; signature insertion; F7 correction
and cancellation with preserved rich formatting; F1 topics; reader properties
ownership and header/body separation. The fixture store and invented addresses
are isolated from live mail. Existing filing-shortcut regression protects Alt+digits
and Ctrl+Z. Run against both build output and the published application.

Completed: solution Release build, all 150 unit tests, shortcut regression against
build output and deployed publish, and filing-shortcut regression against the staged
publish. Native HTML Alt+S passed; loopback SMTP received exactly one fixture with
immediate sending, while queued mode made no delivery attempt. Reader reply/forward
and delete retained message ownership after main selection changed. Ctrl+Y and
Ctrl+Shift+V exercised the real pickers; editing focus preserved Delete/Ctrl+A.
