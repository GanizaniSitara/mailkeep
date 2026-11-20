# Attachment content search design

Status: accepted v1

Date: 2026-08-12

Accepted: 2026-08-20 — the owner explicitly delegated autonomous completion of this work item through its
checkpoints. This accepts the v1 feature cut, the implementation-time PDF package evaluation, the
25 MiB / 10 second / 1,000,000-character defaults, and the 5 GiB / 50% sample budget gate. Coding and
the live-corpus run remain separate work, as scoped below.

## Post-v1 OCR addendum (2026-08-20)

The owner subsequently requested that the deferred OCR capability be added. The implementation now
registers a versioned `ocr` extractor in the isolated attachment worker. It uses the already-installed
Tesseract 5.5 runtime for raster images and ImageMagick for scanned-PDF rendering, with an eight-second
per-tool timeout, the existing ten-second worker boundary, and retryable `needs_ocr` reasons when a tool
is unavailable or recovers no text. Worker requests carry bounded base64 attachment bytes over NDJSON;
Tesseract and ImageMagick consume stdin/stdout, so no untrusted attachment payload is staged on disk and
no Defender exclusion is required. Scanned-PDF render output is bounded to 100 MiB in memory. Bumping the
coordinator extractor version requeues the old `image_requires_ocr` rows without changing source mail or
raw blobs.

This is still text OCR, not semantic image understanding. Automatic image tagging/captioning remains a
separate follow-on because it needs an agreed vision model, label policy, and privacy/retention decision;
no model or external service is invoked by Mailkeep.

## Decision summary

Mailkeep should discover attachments once per raw RFC822 blob, hash each decoded payload, and extract
text once per distinct attachment payload. Attachment text belongs in its own FTS5 index and is joined
back to catalogue documents through attachment occurrences. It must not be appended to `documents_fts`:
that would duplicate a PDF for every folder location, lose the matching filename, and prevent useful
per-attachment status.

The recommended v1 is deliberately broad enough to be useful but bounded enough to run safely:

- searchable filenames plus decoded content for plain text, CSV/TSV, HTML, XML, JSON, nested EML,
  DOCX, XLSX, PPTX, and text-layer PDF;
- a managed, replaceable extractor interface, with the potentially hostile parsing work isolated in a
  long-lived Mailkeep worker process so timeout/cancellation can terminate and restart it;
- resumable discovery and extraction, keyed by raw-message SHA-256 and attachment-payload SHA-256;
- explicit `indexed`, `empty`, `skipped`, `failed`, and `needs_ocr` outcomes with versions and reasons;
- one million extracted characters and 25 MiB decoded input per attachment, 10 seconds per extractor,
  OOXML ZIP limits, no archive descent, and no OCR;
- search defaults to message fields, attachment filename, and attachment content together, while CLI
  and Find Message can restrict the match scope;
- an attachment hit names the file, shows an attachment-derived snippet, and carries a stable occurrence
  id that can be opened from the original raw message without writing to a mail store.

No OE Classic file, imported source, raw Mailkeep blob, or message is modified. The feature writes only
to Mailkeep's derived search catalogue and a bounded extraction-temp directory.

## What the live code establishes

This design is based on the repository as of `ee8be94`:

- `RawBlobStore.Put` stores complete RFC822 messages at `data/blobs/<prefix>/<sha256>.eml` and reuses an
  existing blob with the same hash.
- `MailDatabase.messages.raw_path` points at that blob. `UpsertMimeMessageAsync` uses MimeKit and records
  only `attachment_count`; `ReadAttachments` reparses the MIME when a message is opened.
- `SearchCatalog` copies message rows into `mailkeep-search.db`, indexes subject/sender/recipients/body in
  `documents_fts`, and merges duplicate locations using its message fingerprint.
- `SearchCatalog.UpdateArchive` already demonstrates the desired incremental shape: compare identities,
  process only additions/removals/changed state, report progress, and commit in a transaction.
- `CatalogDocument` and the catalogue schema currently omit `raw_path`, so attachment discovery cannot be
  driven from the catalogue until raw blob identity is added.
- Search results are currently message-only (`MessageSummary` / `SearchEnvelope`), and the WinForms
  attachment display exposes a filename but no stable MIME-part identity or open action.
- The infrastructure project already depends on MimeKit and SQLite. OOXML can use the framework's
  `System.IO.Compression` and `XmlReader`; PDF text extraction is the only proposed new package decision.

## Format tiers

| Tier | Formats | v1 behavior | Engine |
|---|---|---|---|
| 0 | attachment filenames | Always searchable, even when content is skipped | FTS5 over occurrence names |
| 1 | TXT, CSV, TSV, Markdown, JSON, XML, HTML | Extract in v1; decode BOM/declared charset, fall back safely; HTML excludes script/style | Built-in .NET |
| 1 | EML (`message/rfc822`) | Extract headers and human-visible body in v1; do not promote it to a first-class message yet | Existing MimeKit |
| 2 | DOCX, XLSX, PPTX | Extract in v1 from known OOXML parts; preserve useful separators and sheet/slide order | `ZipArchive` + `XmlReader` |
| 3 | PDF with a text layer | Extract in v1; record `empty` or `needs_ocr` when no useful text is present | Managed `IPdfTextExtractor` implementation |
| 4 | scanned PDF, PNG, JPEG, TIFF, GIF, WebP | Detect and record `needs_ocr`; no OCR in v1 | Deferred, opt-in policy required |
| 5 | ZIP, 7z, RAR, TAR and other containers | Record `archive_deferred`; do not descend in v1 | Deferred |
| 5 | legacy DOC/XLS/PPT, MSG, RTF and unknown binary | Record `unsupported_format` | Deferred extractor plug-ins |

MIME type, filename extension, and conservative magic-byte sniffing all inform selection. A mismatch is
observable; it is never silently interpreted as executable content. Inline MIME resources are discovered
but only text-like inline parts are indexed in v1. Ordinary HTML-body resources such as logos are marked
`inline_nontext` so they do not inflate work or results.

Nested EML is attachment text in v1, not a catalogue `document`: promoting it creates unresolved questions
about folders, identity, deletion, and opening. That can be added later without changing the extraction
contract.

## Extraction engine

Use a small contract in `Mailkeep.Infrastructure`, independent of search and UI:

```csharp
public interface IAttachmentTextExtractor
{
    string Id { get; }
    int Version { get; }
    bool CanExtract(AttachmentProbe probe);
    ValueTask<ExtractionResult> ExtractAsync(
        Stream decodedContent, AttachmentProbe probe, ExtractionLimits limits,
        CancellationToken cancellationToken);
}
```

`ExtractionResult` contains status, normalized text, detected media type, extracted character count,
and warnings. Selection belongs to a registry, so an extractor can be replaced and only rows stamped with
its old `(extractor_id, extractor_version)` are queued again.

Run registered extractors in a long-lived `Mailkeep.Extractor.Worker` child process using a narrow NDJSON
request/response protocol. The coordinator owns catalogue writes; the worker receives one decoded temp
file at a time and never receives database credentials or mail-store write access. If an extractor exceeds
10 seconds, crashes, or ignores cancellation, kill and restart the worker and record the attachment outcome.
This gives a real time boundary that an in-process cancellation token cannot guarantee, without depending
on user-installed command-line programs. Built-in extractors can share the same contract and test suite.

For PDF, select a pure managed library behind `IPdfTextExtractor` during implementation after a small
quality/licence comparison. The current recommendation is PdfPig if its then-current package licence and
.NET 10 compatibility pass that check. Adding that package is an explicit slow-wheel implementation action,
not part of this design checkpoint.

## Discovery and identity

There are two distinct deduplication keys:

1. **Raw message blob SHA-256**: discover the MIME tree once even if the same message is filed in many
   folders.
2. **Decoded attachment payload SHA-256**: extract identical bytes once even if they occur in different
   messages or under different filenames.

The catalogue's `documents` row gains `raw_blob_hash` and `raw_path`. Both come from
`MailDatabase.messages.raw_path` during operational/archive catalogue sync. The hash is validated from the
content-addressed filename; legacy/null/invalid paths are retained as `no_raw_blob` rather than guessed.

Discovery parses each current-version raw blob with MimeKit, assigns a deterministic `part_path` from the
MIME tree (for example `1.2.3`), streams each decoded payload through SHA-256, and enforces the byte cap.
The decoded payload is written to an extraction temp file only when its hash needs extraction, then removed
after the worker response. Startup removes abandoned temp files older than 24 hours.

## Catalogue schema

All new tables live in derived `mailkeep-search.db`, not `mailkeep-v2.db` or any imported store. The SQL
below is structural; migrations must be idempotent and covered by an old-catalogue upgrade test.

```sql
ALTER TABLE documents ADD COLUMN raw_blob_hash TEXT;
ALTER TABLE documents ADD COLUMN raw_path TEXT;
CREATE INDEX idx_documents_raw_blob ON documents(raw_blob_hash);

CREATE TABLE raw_blob_scans (
    raw_blob_hash       TEXT PRIMARY KEY,
    raw_path            TEXT NOT NULL,
    mime_scan_version   INTEGER NOT NULL,
    status              TEXT NOT NULL,
    reason              TEXT,
    attachment_count    INTEGER NOT NULL DEFAULT 0,
    attempted_at        TEXT,
    completed_at        TEXT,
    duration_ms         INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE attachments (
    id                  INTEGER PRIMARY KEY,
    sha256              TEXT NOT NULL UNIQUE,
    detected_media_type TEXT NOT NULL DEFAULT '',
    byte_size           INTEGER NOT NULL,
    extractor_id        TEXT,
    extractor_version   INTEGER,
    status              TEXT NOT NULL,
    reason              TEXT,
    extracted_chars     INTEGER NOT NULL DEFAULT 0,
    attempted_at        TEXT,
    completed_at        TEXT,
    duration_ms         INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE attachment_text (
    attachment_id       INTEGER PRIMARY KEY REFERENCES attachments(id) ON DELETE CASCADE,
    content_text        TEXT NOT NULL
);

CREATE VIRTUAL TABLE attachment_text_fts USING fts5(
    content_text,
    content='attachment_text', content_rowid='attachment_id',
    tokenize='unicode61 remove_diacritics 2'
);

CREATE TABLE attachment_occurrences (
    id                  INTEGER PRIMARY KEY,
    raw_blob_hash       TEXT NOT NULL,
    part_path           TEXT NOT NULL,
    attachment_id       INTEGER REFERENCES attachments(id),
    filename            TEXT NOT NULL DEFAULT 'attachment',
    declared_media_type TEXT NOT NULL DEFAULT '',
    is_inline           INTEGER NOT NULL DEFAULT 0,
    nesting_depth       INTEGER NOT NULL DEFAULT 0,
    UNIQUE(raw_blob_hash, part_path)
);
CREATE INDEX idx_occurrence_blob ON attachment_occurrences(raw_blob_hash);
CREATE INDEX idx_occurrence_attachment ON attachment_occurrences(attachment_id);

CREATE VIRTUAL TABLE attachment_names_fts USING fts5(
    filename,
    content='attachment_occurrences', content_rowid='id',
    tokenize='unicode61 remove_diacritics 2'
);

CREATE TABLE attachment_runs (
    id                  INTEGER PRIMARY KEY,
    started_at          TEXT NOT NULL,
    finished_at         TEXT,
    status              TEXT NOT NULL,
    discovered          INTEGER NOT NULL DEFAULT 0,
    extracted           INTEGER NOT NULL DEFAULT 0,
    skipped             INTEGER NOT NULL DEFAULT 0,
    failed              INTEGER NOT NULL DEFAULT 0,
    bytes_read          INTEGER NOT NULL DEFAULT 0
);
```

FTS external-content triggers (or equivalent transaction code) must keep both FTS tables synchronized.
Never update the content table without updating its FTS row in the same transaction.

`attachment_text` intentionally retains normalized text because FTS snippets are part of the UX. This costs
the retained UTF-8/SQLite text plus the term index, but avoids keeping a second filesystem cache and makes
status/backup behavior understandable. Text is capped, and a pre-rollout sample measures the real multiplier.

Rows no longer referenced by any current `documents.raw_blob_hash` are garbage-collection candidates, not
immediate deletions. A later bounded GC pass may remove unreferenced scans/occurrences and then attachment
payload rows with no occurrences. Catalogue rebuild may safely recreate all of this because it is derived.

## Run model

### Incremental sync

1. Sync message catalogue rows as today, including raw blob hash/path.
2. Select distinct current raw blob hashes with no successful scan at `mime_scan_version`.
3. Discover one MIME blob at a time; upsert its scan and occurrences transactionally.
4. Hash decoded payloads while enforcing limits. Reuse an existing attachment row when possible.
5. Queue only attachments with no terminal current-version extraction result.
6. Extract with bounded concurrency (default 2 workers/tasks; one worker process initially is acceptable),
   committing each result independently so cancellation loses at most the current file.
7. Report discovery, extraction, commit, and cleanup progress separately.

No generation change means no MIME scan. A parser-version change queues only hashes produced by that parser;
a MIME discovery-version change rediscovers blobs but can reuse unchanged payload extraction.

### Commands and cancellation

Extend the existing index family rather than create an unrelated command family:

```text
mailkeep index status
mailkeep index sync --attachments
mailkeep index attachments status
mailkeep index attachments sync [--max-items N]
mailkeep index attachments rebuild --extractor ID [--execute]
```

Normal `index sync` may discover/extract new attachments after message indexing, but an initial 88k-item
backfill must be an explicit background action with visible confirmation/progress. `rebuild` remains a dry
run without `--execute`. Ctrl-C and WinForms Cancel request graceful cancellation, then terminate a stuck
worker after a short grace period. Restart simply selects unfinished/stale rows.

Stale `processing` state older than the worker lease (one minute beyond the time limit) is reset to pending.
Failures use capped retries: one automatic retry for worker crash/transient I/O, then terminal `failed` until
an extractor version changes or an explicit retry is requested.

### Limits (v1 defaults)

- decoded payload: 25 MiB; larger files are `too_large` but filenames remain searchable;
- retained normalized text: 1,000,000 characters per distinct payload, with `truncated` warning;
- extractor wall clock: 10 seconds per payload;
- MIME nesting: one attached EML level; deeper nested parts are `nesting_limit`;
- OOXML: at most 10,000 ZIP entries, 100 MiB total uncompressed selected parts, 20:1 aggregate expansion,
  and no external relationships or macros executed;
- archives: depth zero in v1;
- concurrency: 2 extraction tasks at most, lowered to 1 by the UI while foreground mail sync is busy;
- transactions: one discovered blob or up to 100 extraction results, never the entire corpus;
- temp storage: one payload per worker plus a small queue, removed after use and swept after 24 hours.

All limits are constants/configuration owned by the extraction coordinator and are printed by attachment
status. They are not hidden extractor behavior.

## Index-size budget

Do not guess from the 88k count. Before the full backfill, implementation must provide a read-only sampling
mode that discovers a deterministic 1,000-blob sample and reports, without changing the live catalogue:

- decoded bytes and extracted characters by format/status;
- p50/p95/max extraction time;
- projected retained-text bytes for the full corpus;
- projected FTS size using an isolated temporary catalogue;
- counts excluded by every limit.

The owner review gate for the full run is the sample report. Recommended budget: stop and revisit caps if
the projected attachment additions exceed 5 GiB or 50% of current Mailkeep data size, whichever is lower.
The run also stops automatically if the actual catalogue grows beyond the approved projection by 25%.

## Search and opening UX

### Query behavior

The default search scope becomes `all`: existing message fields plus attachment filenames and extracted
attachment text. Existing query translation and malformed-query fallback remain. Add a typed match scope:

- CLI: `--match all|message|attachment-name|attachment-content`;
- Find Message: checkboxes for Message, Attachment filenames, Attachment contents (all checked initially);
- the existing “Message has attachment(s)” switch remains a non-text filter and is not overloaded.

Counts remain distinct logical message fingerprints. A message with three matching attachments is one result.
Ranking takes the best component score with modest boosts for subject, then filename, then attachment/body
content; exact weights are fixture-tested rather than inferred from raw BM25 values across separate FTS tables.

### Result shape

Add optional attachment match metadata rather than smuggle it into `MessageSummary.Snippet`:

```csharp
public sealed record AttachmentMatch(
    long OccurrenceId, string FileName, string MediaType,
    string MatchKind, string Snippet, bool TextWasTruncated);
```

`SearchResult` carries the message summary plus its best attachment match and a matching-attachment count.
CLI JSON adds `attachment_match`; existing fields stay stable. WinForms shows a paperclip/file-name badge and
labels the snippet “From attachment: filename”. A filename match has a filename snippet; a content match uses
`snippet(attachment_text_fts, ...)`.

Opening uses `OccurrenceId` to resolve current `raw_blob_hash + part_path`, reparses the original MIME,
validates that the decoded SHA-256 still matches, and writes a disposable copy under Mailkeep's preview/temp
directory before invoking the normal OS open flow. It never exposes an arbitrary stored path and never writes
back to raw blobs or OE Classic. Missing/changed blobs produce a clear stale-index error and queue rediscovery.

## Observability

`mailkeep index attachments status` and the WinForms progress surface report:

- raw blobs pending/scanned/failed and current MIME scan version;
- distinct payloads pending/indexed/empty/needs-ocr/skipped/failed;
- counts and bytes by format and reason;
- current filename, elapsed time, run throughput, and estimated remaining work;
- extractor ids/versions, last completed run, last error, catalogue size, and retained text characters;
- how many results are filename-searchable but content-unavailable.

Logs must identify hashes only by a short prefix and may include sanitized filenames; they never log extracted
text. Status distinguishes “no match” from “not inspected”, “unsupported”, “too large”, “needs OCR”, and
“extractor failed”.

## Verification fixtures and acceptance tests

Fixtures are synthetic and stored under the test project; no live mail or copyrighted corpus attachment is
required. The implementation checkpoint is complete only when these automated cases pass:

1. A phrase present only in TXT, CSV, HTML, nested EML, DOCX, XLSX, PPTX, and text-layer PDF is found.
2. HTML scripts/styles, OOXML metadata/macros, and binary garbage do not become searchable text.
3. Filename-only search works for supported, unsupported, too-large, and failed attachments.
4. One raw blob referenced by several catalogue documents is discovered once; locations still merge.
5. Identical decoded payloads in different messages/filenames extract once but return each occurrence name.
6. An attachment with several matching terms returns one logical message and its best attachment/snippet.
7. `--match` and Find Message scopes separate message, filename, and content matches correctly.
8. A scanned PDF/image is `needs_ocr`; ZIP is `archive_deferred`; unknown binary is `unsupported_format`.
9. Oversize, OOXML expansion/entry limits, malformed MIME/ZIP/PDF, worker crash, and timeout record reasons
   and allow the next item to proceed.
10. Cancellation followed by restart resumes pending work without re-extracting completed hashes.
11. Incrementing one extractor version requeues only payloads owned by that extractor; changing MIME scan
    version rediscovers blobs while reusing payload results.
12. Catalogue migration from today's schema preserves existing message search and can roll forward twice.
13. Removing the last document reference makes attachment rows GC-eligible; shared rows remain searchable.
14. Opening resolves the exact MIME part, verifies its hash, and writes only to the test preview directory.
15. Before/after hashes and mtimes prove source `.eml`, OE Classic, and imported mail-store files are unchanged.
16. Existing `dotnet test Mailkeep.sln` and UI smoke tests remain green.

A small performance test builds 10,000 synthetic occurrences with deliberate duplicates and verifies that a
second no-change sync performs no extraction, search returns within the existing search budget, and memory is
bounded by batch size rather than corpus size. Performance thresholds should be recorded on the build machine,
not embedded from a guessed workstation speed.

## V1 feature cut

### Included

- schema/migration and raw blob identity propagation;
- MIME discovery, occurrence identity, decoded-payload hashing and deduplication;
- built-in text/CSV/HTML/XML/JSON/EML and OOXML extractors;
- one managed text-layer PDF extractor selected at implementation review;
- worker isolation, limits, cancellation, resume, retry, versions and status reasons;
- separate filename/content FTS and unified message-deduplicated search;
- CLI status/sync/search controls and JSON attachment match metadata;
- Find Message scope controls, attachment badge/snippet, and safe open-by-occurrence;
- synthetic correctness/security/resume/migration fixtures and an isolated sample estimator;
- read-only handling of all source mail and raw blobs.

### Deferred

- semantic image tagging/captioning (OCR is covered by the post-v1 addendum above);
- ZIP/7z/RAR/TAR descent;
- legacy Office, MSG, and RTF parsers;
- promoting nested EML to first-class messages;
- indexing encrypted/password-protected documents;
- semantic/vector search or generated summaries;
- attachment-text highlighting inside the original document;
- automatic full-corpus backfill before owner approval of the sample projection.

## Implementation checkpoints

1. **Owner design review (complete 2026-08-20):** v1 cut, implementation-time PDF package evaluation,
   25 MiB/10 s/1M-character defaults, and 5 GiB/50% sample budget gate accepted.
2. **Schema + discovery:** migrations, raw blob propagation, dedupe, statuses, limits, resume tests.
3. **Extractors + worker:** tier 1/2/PDF fixtures, isolation, cancellation, versioned re-extraction.
4. **Search surfaces:** unified ranking/deduplication, CLI JSON, WinForms scopes/snippets/open action.
5. **Isolated estimator:** run synthetic tests and a read-only 1,000-blob sample; present projection.
6. **Owner run approval:** only then perform the resumable live backfill and verify status/search examples.

this work item ends at the completed step 1 design checkpoint. Steps 2-6 are the separately tracked implementation
and run work; the full-corpus backfill still requires the step 6 owner approval after the estimator report.
