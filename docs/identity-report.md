# Read-only identity report

Run `mailkeep identity report` (optionally `--root PATH`). JSON goes to stdout;
scan progress goes to stderr. Ctrl-C cancels the operation. There is no execute
mode and no application UI change in this step.

The command bypasses ordinary CLI startup initialization and opens the existing
database read-only. Message metadata, deletion records and outgoing counts are
read in one SQLite snapshot. It then hashes each distinct MIME blob from its
actual bytes, with at most eight concurrent reads. It does not change schema,
backfill columns, merge rows, refresh indexes, queue or send mail. SQLite may
create its normal WAL/shared-memory coordination files even for a read-only
connection; these are not changes to the stored messages.

The agreed authority is **Mailkeep over OE Classic for byte-identical copies**.
`byte_matches` lists matching SHA-256 groups with original row IDs, source keys,
account, folder, read state and deletion evidence:

- `mailkeep_wins`: `oe_rows_superseded` identifies older OE representations;
  `mailkeep_rows_retained` shows the surviving folder/state. These are proposals,
  not completed changes. Multiple identical Mailkeep occurrences are retained.
- `review_conflicting_mailkeep_state`: the Mailkeep occurrences disagree, or a
  surviving copy conflicts with deletion evidence. No OE rows are proposed for
  automatic removal in this group.
- `same_bytes_review_copies`: matching bytes without an unambiguous OE/Mailkeep
  authority pair. No automatic collapse is proposed.
- `review_deletion_history_do_not_resurrect`: inspect the linked deletion records
  before restoring or merging any occurrence.

`message_id_conflicts` contains the same Message-ID header with different byte
hashes. Message-ID case is preserved and no such match authorizes a merge.
Missing or malformed IDs do not prevent hashing available bytes; they appear in
`issues`. Missing or unreadable blobs have no verified hash and cannot qualify
for a byte match. Only the headers are parsed; this is not a full MIME validation
or attachment integrity audit.

`deletions` includes every existing tombstone. `linked_rows` can identify a
surviving source occurrence, an unambiguous moved Mailkeep occurrence with the
same source key, or the OE origin of a deleted adopted copy. Empty `linked_rows`
means the report cannot reconstruct content identity from that tombstone.
`deletion_linked_rows` also includes singleton messages with deletion evidence;
they do not have to be in a duplicate group to matter.

`folders` gives row counts, verified coverage, proposed superseded OE counts and
deletion-linked counts per source/account/folder. `outgoing_counts` is separate:
draft raw blobs can be attachment-only snapshots and must not be mistaken for
complete message identities.

The report is deliberately conservative about incomplete deletion history and
conflicting Mailkeep copies. It cannot prove there was never a deletion when the
old record contains no recoverable identity. It reads blob files after releasing
the database snapshot, so active ingestion may continue while it runs. Re-run
before any future migration, and establish sync protection before changing data.

Tests exercise exact byte matches, reused Message-IDs, independent copies,
successor precedence, moved/deleted copies, deleted adoptions, incomplete history,
missing blobs, untrusted filenames, invalid headers, cancellation, repeatability,
and unchanged database/blob bytes on an isolated fixture.
