# Bulk migration performance and verification

BulkFolderMigration and FolderBundle.Bulk implement offline format conversion.
Live mailbox inventories and timing diaries belong outside Git.

A root lease and persistent pending marker gate other clients. Create and check
one working coordinator copy before publication. Preserve original IDs,
source/account/folder identity, metadata, marks, search entries and ancillary
tables. Conversion is not adoption or copy/delete.

Use prepared inserts and durable bounded transactions without per-message undo
events during initial conversion. Validate source MIME against its existing
content address; independently reread destination MIME against that address.
Compare exact metadata/state and an ordered occurrence digest. Compare original
coordinator tables once across the whole run, including unrelated rows. Check
schema and required triggers before publication.

Publish verified folder files before replacing the coordinator. Durable phases
support recovery after either publication step. Failed staging preserves the
source. Subsequent writes use the durable journal; missing acknowledged storage
fails instead of accepting a stale projection. Rebuild preserves IDs and hierarchy.

CLI: folder storage bulk plan|run|status|publish|recover --root ROOT.
Consult current help for execution/root/plan confirmations and live opt-in.
This document authorizes no live operation.

Independent verification covers pristine MIME, metadata/state, hierarchy,
occurrence digests, triggers, journal completion and unrelated data. Counts or
sampling alone do not prove byte preservation. A production hash check differs
from an additional literal source/destination comparison.

Measure checked-copy, writing, selected verification, global comparison and
publication separately. Do not extrapolate small synthetic timings to a full
mailbox or omit deliberately throttled I/O from downtime estimates. Repeated
whole-store checks per folder can dominate useful conversion work.

Benchmark disposable stores with invented mail. Retain failures; test corrupt
bytes, schema/metadata changes, missing storage, interruption and recovery.
