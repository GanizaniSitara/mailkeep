# Pilot writer consolidation

This document describes the offline upgrade contract, not a live-store inventory.

## Format and identity

Version 3 binds a Local Mail folder UUID/key independently of account. Every
occurrence retains its own account, source provenance, message ID, MIME and state.
An empty account on the version-3 binding means that the account lives on each
occurrence; it is not a new mailbox identity. Version-2 account bindings remain
readable. A projection rebuild refuses overlapping logical and account bindings.

Upgrading a pilot file uses SQLite backup into an unpublished copy. All original
MIME, tombstones, state, events, aliases and retirement receipts survive. Only
the format/binding and active projection metadata gain fields. Every field
already present in pilot metadata must match the current projection. The
original files remain in `local-folders`; the new writer uses `bound-folders`.

## Offline command

`mailkeep folder storage consolidate-pilots plan --root ROOT` is read-only.
The reviewed plan contains the exact keys, UUIDs, counts and staging-space bound.
The write command additionally requires `--confirm-root ROOT`,
`--confirm-plan DIGEST` and `--execute`. The default live root also requires
`--allow-live`. The CLI refuses an open Mailkeep GUI. Close it with the existing
polite-close helper, which refuses compose/dialog state, before any live run.

The operation takes a checked full coordinator backup in
`pilot-consolidation/original.db`, verifies each upgraded folder, publishes the
new files, and atomically replaces pilot registrations/triggers with the common
writer's bindings. Ordinary message, outgoing, folder and alias rows are unchanged.
The original pilot configuration is retained as `original-pilot-marker.json`;
the old enabled flag is explicitly cleared after the coordinator commit.
New local folders, including empty folders and later accounts in them, use the
same logical writer. Legacy enrollment and per-folder rollback are disabled on
a consolidated store; rollback requires a consistent full snapshot.

## Interrupted publication

The persistent `pilot-consolidation/pending` marker blocks normal startup during
an incomplete operation. The old files are never edited. Before the coordinator
commit, `consolidate-pilots recover` returns unpublished copies to staging and
keeps the pilot writer active. After that commit it checks every published file
and binding before completing the transition. Changed or missing committed files
keep recovery blocked. An aborted run can be retried, retaining its evidence.

The complete marker prevents the old pilot writer from reactivating. A fresh
projection rebuild restores the logical writer setting as well as message
provenance, retirement receipts and hierarchy. A stale database containing pilot
registrations alongside a complete marker is refused.

## Independent verification and remaining gates

`mailkeep-imported-folder-check.py pilots --root ROOT --out PROOF`
uses read-only SQLite and Python, without Mailkeep libraries. It compares each
upgraded file against the retained original, including MIME, state, tombstones,
events and receipts; checks active metadata against the checked backup; checks
that the pilot writer is retired; then compares the coordinator tables once.
It does not re-read all unrelated mail after each folder.

Use disposable fixtures to verify byte identity, state, provenance, aliases, interruption recovery and unrelated coordinator state. Keep live inventories and timing evidence outside Git.
