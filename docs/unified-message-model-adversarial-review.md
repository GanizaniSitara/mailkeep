# Unified message model: adversarial review

Reviewed 2026-10-04 against Mailkeep commit `64d6310`, the current OE importer,
and this work item through this work item. This is a design review, not approval to migrate
or merge mail. this work item is selected as the first work item.

## Verdict

User clarification after review (2026-10-04): for byte-identical OE Classic and
Mailkeep copies, Mailkeep is the successor and its folder/read/deleted state wins.
Consolidating the older OE representation under this rule is intentional, not
data loss. The original critique below overstated that case. Message-ID-only
matches remain insufficient; conflicting copies within Mailkeep and incomplete
deletion evidence still need review. The approved first deliverable is a read-only
report, with no schema backfill or mail migration.

Keep the product goal: ordinary folder operations should work consistently on
imported, received, and composed mail. Do not execute the eight tickets as written.
They conflate message content, mailbox occurrences, and transport state, defer a
prerequisite until step 7, and include behaviour already implemented.

## Findings

1. **Critical: matching identity is not permission to delete a row.**
   `MailDatabase.CopyMessagesToLocal` deliberately creates independent copies with
   fresh source keys (MailDatabase.cs:965). The OE importer also preserves repeated
   copies, even within one folder, using a raw hash plus occurrence number
   (OeMbxImporter.cs:411-428). Step 6's global collapse by Message-ID would undo
   these semantics. A repeated Message-ID with different bytes is ambiguous;
   identical bytes in separate accounts or folders can still be intentional copies.
   Retain a stable internal occurrence ID, RFC Message-ID, byte fingerprint, and
   source linkage as separate concepts. Group candidates for inspection; never
   interpret a group size as a count of safely removable messages.

2. **Critical: the sync protection is ordered after the destructive changes.**
   Steps 2 and 4 convert, move or remove imported rows; step 7 introduces the ledger
   intended to protect them. Today's importer looks up source keys by current
   folder, imports missing keys, and reapplies OE read state
   (OeMbxImporter.cs:189-244). Before changing imported occurrences, implement and
   test the source mapping/deletion history, or enforce suspension of incompatible
   import writers. A restart-only check does not test re-import safety.

3. **High: step 3 is based on an outdated description of the code.**
   `MailSendService.SendClaimedAsync` already records delivery and writes an
   ordinary Sent Items message keyed by `sent:<outgoing id>` (lines 195-216).
   Tests cover retained Sent copies and attachments. Recast this work item as an audit
   and repair of missing copies, visibility, and retryable local persistence.
   Do not introduce a competing Sent-copy writer. Source presence does not prove
   that the currently published binary or every historical send has this behaviour.

4. **High: folder membership is insufficient authorization to transmit.**
   Current queueing validates recipients and sending identity and freezes MIME;
   the sender atomically claims queued rows (MailSendService.cs:146-158;
   MailDatabase.cs:1840-1885). Step 5 must preserve explicit queue intent, frozen
   envelope/bytes, claim state and retries. Importing, copying or dragging a
   received message into a folder called Outbox must not authorize sending.
   The parent ticket's imported-Outbox-send acceptance conflicts with step 2's
   explicit conversion to unsent drafts. Adopt the latter. Also define recovery
   for SMTP acceptance followed by a local crash; a database transaction cannot
   make the remote SMTP delivery atomic with the local Sent move.

5. **High: the proposed permanent OE origin key is not established.**
   The implementation uses raw hashes with per-folder occurrence numbers, not
   an immutable OE message ID. A tuple containing a folder changes on a move;
   duplicate numbering alone cannot establish which identical copy survived a
   deletion. Prove source identity across moves, deletions and compaction before
   treating it as permanent. Keep ambiguous matches explicit.

6. **High: one folder per occurrence is reasonable; one folder per global
   content identity loses information.**
   The existing search catalog explicitly returns multiple locations for grouped
   hits (SearchCatalog.cs:471). Unifying the writable row model does not require
   deleting independent local/connected/account occurrences. Specify copy versus
   move and local versus provider authority before changing the schema.

7. **High: rollback and consistency are underspecified.**
   Keeping an old executable is not rollback for transformed data. The plan needs
   a consistent SQLite backup, retained blobs, writer coordination, verification
   of the restore, schema compatibility, and handling of mail received or sent
   after the backup. Preserve ID mappings for outgoing origins, message marks,
   CLI IDs, search references and attachment occurrences; reconcile derived
   indexes. The catalog's public reference currently includes folder and source
   key (SearchCatalog.cs:697), so an in-place move can affect lookup identity.

8. **Medium: step 1 is not currently specified as read-only.**
   Adding/backfilling an indexed column is a write. Moreover CLI startup calls
   Database.Initialize and Catalog.Initialize before dispatch (Program.cs:75-78).
   A genuinely read-only report must bypass both and open the DB read-only.
   Separate diagnostic work from any later explicit, backed-up schema/backfill
   operation. Missing or malformed MIME must be reported, not silently assigned
   invented identities. Raw hashes are exact-byte evidence, not proof that
   differently serialized copies are unrelated.

## First ticket: evidence before migration

this work item should first deliver a genuinely read-only identity/copy report. Record
coverage and failures, Message-ID candidate groups, exact-byte groups, existing
adoption links, account/folder boundaries, and conflicting states. Preserve raw
values; do not use the catalog's lowercased Message-ID grouping as an irreversible
merge key. Report outgoing rows separately: draft raw blobs may contain only
attachment snapshots (MailSendService.cs:93-109), not the full draft message.

Test intentional same-folder copies, copies across accounts, reused Message-IDs
with different content, headerless/malformed messages, missing blobs, adoption
links, cancelled scans and report repeatability. Verify the report leaves schema,
rows, settings, FTS and blob contents unchanged. Any subsequent persisted identity
column is a non-unique lookup aid, not a new canonical key or a merge instruction.

## Dependency corrections before steps 2-8

- Establish occurrence identity and source mappings/deletion history before any
  imported-row conversion. Specify edit conflicts and which side wins; a list of
  timestamped events alone does not resolve concurrent changes.
- Correct this work item to the verified missing behaviour.
- Preserve explicit queue authorization while unifying folder presentation.
- Replace unconditional collapse with reviewed, evidence-backed repair of
  redundant representations. Keep intentional copies.
- Validate round-trip sync on isolated stores, including repeated identical mail,
  deleted folders, changed flags, interruptions and retries. Never write to the
  live OE store as part of this review.

These corrections retain the user's single-model goal and staged delivery.
They reject the proposed identity shortcut and unsafe ordering, not the goal.
