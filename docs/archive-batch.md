# Archive batch safety

Live folder names, inventories, receipts and deployment diaries belong in
private operational evidence outside Git.

Before a live batch, generate an exact read-only plan, check free space and
obtain approval for its scope. Politely close Mailkeep, refusing an open compose
window or dialog. Make and verify the approved backup before moving any folder.

After each folder, independently check selected original MIME bytes, state,
receipts and required triggers. Compare unrelated data once at the end of the
batch rather than rereading the entire mailbox after every folder. Record counts,
timings and outcomes privately. Reopen after verification and stop at the agreed
review milestone. Retain rollback material under the current approved policy.

## Restore and copy

Restore moves selected folders or a subtree back to Local Mail. Copy retains
the archive source. Preflight the complete selection, verify MIME and hierarchy,
and refuse conflicting occupied destinations. Receipts identify prior restored
occurrences: preserve subsequent filing/read state and do not resurrect removed
mail. Verify committed Local Mail before retiring active archive files.
Interrupted finalization must retain recovery material and resume without
duplicating mail.

Archive browsing reconstructs hierarchy and uses an independent window. Normal
search excludes archives unless requested. Re-archiving a restored folder makes
a new verified snapshot. Refuse unsupported state rather than discard it.

## Batch implementation contract

The batch controller defaults to a read-only plan, walks children before parents, checks original blobs and destination collisions, and records evidence outside Git. Execution requires an exact root confirmation. A checked backup precedes the first move; subsequent moves may reuse that backup only under the reviewed batch policy. The CLI no-backup option is not permission to omit the initial approved backup.

Each move preserves original bytes/read state, commits receipts and reimport protection, and undergoes independent post-move verification. Retain original MIME; archive movement does not authorize compaction or blob cleanup. Save the committed result before running postchecks. Stop on the first failure, hidden duplicate, unsupported message state or mismatching destination. Never automatically retry, overwrite or undo an already committed move.

Independent batch verification checks new receipts while preserving old ones, opens one folder bundle at a time and compares unrelated messages once across the full batch. Recovery records must distinguish committed moves from incomplete postchecks.
