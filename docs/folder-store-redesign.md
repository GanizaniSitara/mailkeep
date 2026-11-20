# Folder store redesign: folders as portable files on disk

Status: design specification (revised after the storage-format discussion). Nothing here authorises a live write, migration or install.

## Current read-side cutover — 10 November 2026

Bulk conversion, pilot consolidation and prototype-writer retirement are complete.
The read-side cutover was deployed and independently verified on 10 November.
The older checkpoints below describe historical gates and counts.

Converted-message MIME now resolves through the indexed projection to the exact
occurrence in `bound-folders/<uuid>/folder.mkdb`. The reader verifies folder
identity and immutable MIME hash. A pending move can resolve its former occurrence
through the durable journal; missing acknowledged content fails rather than
silently using a stale raw cache.

Message display, attachment extraction/indexing, picture export and restore
verification consume these bytes directly. Rebuilding the projection records
content-addressed raw paths without recreating the full `data/blobs` tree.
Callers that explicitly need a filename can materialize just that message.
Reading a converted message also works when the cache directory is not writable.

This does not authorize deleting all `data/blobs`: current drafts, queued mail,
connected or unconverted messages can still require original MIME there.
Any later cache cleanup must classify those references and protect them.
Startup creates one raw-path lookup index if absent; this is a one-time projection
schema change, not another mail conversion or full safety-copy operation.

## Storage design

## 1. Goal

A Mailkeep folder is a self-describing file on disk. It can be copied,
renamed, detached (archived to another drive) and attached again, and Mailkeep
then rebuilds whatever it needs from the file alone. Search and the folder
tree are derived views that can be thrown away and recomputed. Archiving never
means hard-deleting content.

Non-goals: changing the UI model (a folder is still a folder), replacing the
IMAP/POP sync, or writing to OE Classic's live store.

## 2. Where we are (verified 2026-10-22)

- Raw MIME is **already** stored as pristine files: `data/blobs/<2 hex>/<sha256>.eml`,
  content-addressed, written once (`RawBlobStore.Put`). The bytes are fine; only
  their *organisation* lives in the database.
- Folder membership, read state, deletions and provenance live only in
  `mailkeep-v2.db` (`messages`, `local_folders`, `local_deletions`,
  `adoption_cleanup`, `message_marks`). Three worlds: `source='live'` (writable),
  `source='archive'` (imported archives, read-only) and `outgoing_messages`
  (drafts and sent mail; its own text ids and status). Storing `body_text`/`body_html`
  inline per row bloats the monolithic database file.
- Search (`mailkeep-search.db`) and the attachment catalog are already derived
  stores; the attachment catalog is keyed by `raw_blob_hash` = the same SHA-256.
- The legacy importer lives in a separate migration tool (`OeMbxImporter`), not in
  this repo. It preserves repeated copies using raw hash + per-folder occurrence
  number.
- Draft and adoption migrations (`migration oe-drafts apply` and
  `migration adoptions apply`) are explicit opt-in steps.
- The adversarial review (`unified-message-model-adversarial-review.md`) stands:
  identity is not permission to delete; intentional copies stay; folder
  membership never authorises sending; rollback needs a consistent backup, not
  an old binary.

## 3. What OE Classic does, and what to keep or avoid

Inspected `%LOCALAPPDATA%\OEClassic\User\<account>\<Folder>.mbx|.db`.

| OE Classic design | Keep | Avoid |
|---|---|---|
| One folder = `.mbx` (messages, append-only, custom `[mbx] vers=1.00` header) + `.db` (SQLite: headers, snippet, flags, byte offset) | Folder = one unit on disk; append-only writes; per-folder cache so opening is instant | Private file format nothing else reads |
| Read/flag/watch/priority exist only in the `.db` | | State that cannot be rebuilt from the folder: lose the `.db` and it is gone |
| Message identity = per-folder integer + byte offset; Message-ID stored but unused as key | | Move = copy + delete; compaction changes offsets; index and store must stay in lockstep |
| Folder tree and account links live in the profile and in `c_acc_id` columns | | A folder's files do not describe themselves, so rename / attach / detach break |
| Delete = flag until compaction; `.writelock` files for single-writer | Tombstone-then-compact; single writer per folder | |

The reasoning behind their split (inferred, not documented by them): the `.mbx`
is a flat append-only log, cheap to write and recoverable by scanning, the
lesson of Outlook Express's corrupting `.dbx` files; the `.db` keeps the small,
frequently changing data so listing is instant and a flag change does not touch
a gigabyte of mail. Their mistake is that the "rebuildable" half is not
rebuildable: flags live only in the index.

## 4. Format decision

**One SQLite file per folder holding pristine message bytes and all state.**
(User decision 2026-10-22.) There is no mbox storage and no mbox export.

SQLite is a standard container: the schema is readable in-file by any SQLite
tool. It gives byte-exact blobs, transactions, WAL crash safety,
one-writer-many-readers locking, FTS and per-folder VACUUM for free.

Rejected: *mbox* (bodies must be escaped so stored bytes are not the message
bytes, and locking, crash recovery and compaction would be ours to write);
*Maildir* (255k files for the archive, flag suffix illegal on NTFS); *EML
directory* (user); *single global SQLite* (not detachable, and it is what we
have now).

Compared with today (one 13.8 GB SQLite plus blobs): per-folder SQLite keeps
every property we rely on (indexes, atomic writes, locking) and adds the
missing ones (per-folder blast radius, per-folder backup, detach/attach).

**State is never written into the message bytes.** Read/flag/delete live in
the `state` table, so the bytes, and therefore the SHA-256 identity and the
attachment catalog linkage, never change.

## 5. Folder bundle layout

```
Projects/                      # the folder; directory name = display name
  folder.mkdb                  # SQLite: the complete folder (truth + caches)
  Subfolder/                   # children nest as directories, each a bundle
    folder.mkdb
```

Inside `folder.mkdb` (`PRAGMA application_id` stamped with a Mailkeep magic
number; `PRAGMA user_version` = format version):

```
folder    (format, uuid, name, kind, created_at, next_seq)       -- self-description
messages  (seq PK, sha256, raw BLOB, size, received_at)          -- TRUTH, append-only
state     (seq PK, is_read, flagged, deleted_at, origin, added_at, copied_from)  -- TRUTH
events    (id, seq, ev, at, detail)                               -- TRUTH: undo/audit log
headers   (seq PK, subject, sender, sender_name, recipients, date_utc, message_id,
           attachment_count, snippet)                             -- CACHE, rebuildable from raw
fts       (fts5 over subject/sender/recipients/body_text)         -- CACHE, rebuildable
```

Rebuild rule: `folder`, `messages`, `state` and `events` are the truth;
`headers`, `fts` and the global search catalog are derivable from them.
Losing a cache never loses state (the OE Classic failure).

Identity (three concepts, kept separate, per the review):

- **Occurrence id** = `(folder uuid, seq)`. `seq` is a monotonic counter from
  `folder.next_seq`, never reused.
- **Content fingerprint** = SHA-256 of the pristine message bytes, stored next
  to the blob and re-checked by `verify`. Equal fingerprints in two folders are
  two occurrences, not one message.
- **RFC Message-ID** = lookup aid only, never a merge key.

Provenance travels with the message in `state.origin`:
`oe-import:<account>/<folder>/<n>`, `imap:<account>:<uidvalidity>/<uid>`,
`pop:<account>:<uidl>`, `composed:<draft id>`, `copied-from:<uuid>/<seq>`.

## 6. Operations

| Operation | What happens |
|---|---|
| add | one transaction: insert `messages` + `state` + `headers` row, `events(added)`, bump `next_seq` |
| read / unread / flag | update `state`, append `events` |
| delete | set `state.deleted_at`; bytes stay until compact; Ctrl+Z clears it (`events(restored)`) |
| move A -> B | add to B with `copied_from`, then delete in A. Same sha, new occurrence id |
| rename folder | rename directory, update `folder.name`; uuid unchanged, nothing else moves |
| detach | close the file, forget the path in the registry. The file is complete on its own |
| attach PATH | open read-only, check `application_id`/`user_version`/`folder` row, rebuild `headers`/`fts` if missing or stale, register uuid -> path, index into global search |
| compact | explicit, user-run: delete rows with `deleted_at` older than the retention window, then `VACUUM INTO` a new file and swap; old file kept until verified |
| archive | convert (if still DB-backed) + verify + detach + move directory to the archive location; re-attach read-only later |
| verify | `PRAGMA integrity_check`; re-hash every blob against `sha256`; replay `events`; report, never fix silently |

## 7. Global layer

- `registry.json` (or a small table): attached bundles, `uuid -> path`, order
  in the tree. This and accounts/identities are the only global truth.
- `mailkeep-search.db`: derived from bundles; `index rebuild` recreates it.
  Message refs become `mk:<uuid>/<seq>` (CLI contract change, versioned).
- Send queue: side table keyed by occurrence id with status, attempts,
  frozen-MIME sha, next attempt, last error. Being in the Outbox bundle is
  presentation; the queue row is the authorisation (review finding 4).
- Attachment catalog: unchanged, keyed by the same SHA-256.
- Cross-folder queries (search, counts) use `ATTACH DATABASE` or the global
  catalog; never a single giant file again.

## 8. Durability and concurrency

- SQLite WAL mode per folder: one writer, many readers, enforced by SQLite.
  A detached or archived folder is opened `?mode=ro`.
- Every mutation is one transaction; a crash leaves the previous committed
  state. No torn-tail handling to write.
- Backups are file copies taken when no writer holds the folder, or
  `VACUUM INTO` for a consistent copy while live. Incremental backup = copy
  the folders whose file changed.
- Corruption: `integrity_check` on attach and in `verify`; recovery = copy
  every readable row to a fresh file, report the rest. Blast radius is one folder.
- Antivirus and sync tools scanning a large file on every write: measure on
  the biggest OE folders. One file per folder is the rule; a split by year is a
  permitted extension of the format, offered to the user as a choice, never
  applied automatically (decision 4).


## Format decisions

1. One SQLite file per folder as storage, chosen for robustness. No mbox in any
   form: not storage, not export (dropped by the user to avoid confusion).
   Maildir rejected.
2. Read/flag state lives in the folder file's own tables, never in the message
   bytes.
3. Archive location: default `%LOCALAPPDATA%\Mailkeep\archive\<Folder>\` with an
   optional path override. "Probably ok for now."
4. One file per folder regardless of size. Splitting by year is never automatic;
   if it is ever needed it is offered to the user as a choice.


Live migration inventories and deployment diaries are private operational evidence, not repository documentation. See the storage source and current CLI help for implemented behavior.

## Archive restore and recovery design reference

The following restores historical implementation contracts removed during the privacy cleanup. It is a design reference, not deployment status or permission for live operations; confirm current CLI behavior before use.

Implemented update, 26 October 2026: archive browsing now reconstructs nested
parents from folder paths. Right-click offers **Restore Folder/Tree to Local
Mail…** to move back, and **Copy Folder/Tree to Local Mail…** to retain both.
Original paths are the
default; choosing another parent prefixes those paths. Existing destination
folders containing the selected mail are refused rather than overwritten.
Repeated restores skip previously restored occurrences and preserve subsequent
filing/read state. A removed restored message is not silently resurrected.

`folder restore LOCATION [--path LABEL] [--tree] [--into PARENT_PATH] [--execute]`
preflights every selected bundle and restores its selection in one mailbox
transaction. It validates raw MIME hashes, sizes, occurrence state and a portable
tree inventory; missing/corrupt children refuse the whole selection. Header and
search caches are disposable. Flagged/deleted archive states currently refuse
restore pending support. Interrupted transactions may leave unreferenced CAS
blobs, but no visible partial folders/mail. `folder manifest LOCATION --execute`
upgrades older archives after checking retained mailbox receipts for missing
files. New archives record original logical paths and update this inventory.
Legacy archives without an inventory cannot prove a child was already missing;
keep their checked backup and generate the inventory before moving storage.

This supplies selected-folder/tree recovery first, as requested by the owner.
It does not implement writable normal-mail folder bundles (increment 4), nor
migrate the selected imported tree.

Owner UI feedback and decision, 26 October: Archive opens as an independent
taskbar window. **Restore Folder/Tree to Local Mail** now means move back:
commit Local Mail, independently verify every retained occurrence, then retire
the selected files from the active Archive tree/inventory. **Copy Folder/Tree to
Local Mail** remains a separate action retaining the archive original. Both show
explicit destination paths and omit zero skipped-message counters. Existing
copies identified by restore receipts are reused with their current mailbox
state. The same folder path can subsequently be archived again as a new verified
snapshot; unrelated occupied destinations are still refused.

`folder restore` now performs the move-back operation; `folder copy-from-archive`
has the previous additive behavior. Restore retains same-directory
`folder.mkdb.restored-<operation-id>.bak` recovery snapshots, which Archive does
not browse/search. They are not automatically purged. A flushed pending intent,
archive operation lock, retained-source locks and immediate mailbox transaction
protect final verification/file retirement. Interrupted finalization is resumable
through **Finish Interrupted Restore** on the archive root, without duplicating
mail; source/recovery files remain available if verification refuses. No physical
power-loss experiment has been performed. Old archive-window IDs bind to bundle
UUIDs and cannot accidentally address a replacement at the same path.

Re-archiving refreshes normal Local Mail search as well as imported-mail search.
Current archive storage cannot retain replied/forwarded marks: archiving such a
folder now refuses explicitly instead of discarding those marks. Unsupported
archive flagged/deleted states remain refused on restore.


## Detached archive and benchmark design targets

Detached storage keeps its own search index. Explicit detached-archive search should open storage read-only without attaching it. Measure folder opening, cache reconstruction, durable insertion, conversion and verification independently on disposable representative data. Historical targets were folder open below 200 ms, insert below 50 ms and large-folder index rebuilds in minutes rather than hours; these are targets, not measured acceptance claims.
