# Repository privacy boundary

Use invented MIME and reserved example addresses in source and fixtures. Never
commit account stores, tokens, real mail, live message/folder identifiers, mailbox
inventories, personal screenshots, backup files or production deployment diaries.
Private operational evidence belongs outside this repository and may be linked
from the private task corpus. Ignore rules are preventive, not a historical audit.

Before public distribution, inspect every reachable branch/tag and the actual
installer payload. Deleting a current file does not remove earlier revisions.
Git author names/addresses are normal intentional metadata; inspect them before
changing repository visibility. A history rewrite needs a separately reviewed
ref-by-ref plan and coordination with existing checkouts and worktrees.

The November 2026 audit found historical live-mail folder names, counts and local
paths in documentation, and an incidental stack dump. These were removed from
the current documentation; that cleanup does not sanitize historical revisions.
Detailed evidence and prior documents remain outside Git.

The embedded Google desktop OAuth registration is a third-party application
registration, not an owner's mailbox refresh token. Mailkeep-owned Google and
Microsoft registrations remain a release prerequisite. Do not break existing
authorization as part of a documentation cleanup or mistake public desktop-app
constants for proof that all credential handling is safe.

Artwork redistribution rights remain a separate release gate. Identifying some
freely licensed cells does not clear a complete sprite sheet.
