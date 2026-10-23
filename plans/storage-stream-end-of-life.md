# Stream end-of-life: finalize, archive, close the books

Workstream WS-3 of the storage-reclamation program. Companion plans: `storage-ledger-retention.md`
(WS-5) and `storage-extension-capabilities.md` (WS-8). Release: 1.0.

## Context

The evidence comes from read-only measurement of a production deployment's development environment
(October 2026). The event store (`wh_event_body` plus `wh_event_store`) is 50 to 57% of every service
database, and nothing ever leaves it. A stream that is finished, idle for months, or long past any
reader's interest costs the same bytes, index entries and vacuum work as a stream written today.

The framework already has most of the parts, but they are disconnected:

| Part | Where | State |
|---|---|---|
| Gated prefix truncate | `close_stream`, latest in `src/Whizbang.Data.Postgres/Migrations/093_OriginGeneration.sql:116` | works; consumption gate, carry-forward guard, digest subtraction, origin-generation bump |
| Cold copy table | `wh_event_archive`, `085_EventArchive.sql:14` | written only by `close_stream(p_archive => TRUE)` |
| Archive read-back | `IWorkCoordinator.GetArchivedEventsAsync`, `src/Whizbang.Core/Messaging/IWorkCoordinator.cs:1917` | explicit call only; no load, rewind or rebuild reads it |
| Hooked closer | `IStreamCloser` / `StreamCloser`, `src/Whizbang.Core/Lifecycle/StreamCloser.cs:16,35` | fires `PreDestruction`/`PostDestruction` around the truncate |
| Disposition vocabulary | `Disposition` enum, `src/Whizbang.Core/Lifecycle/Destruction.cs:57` | declared, never acted on |
| Whole-stream purge | `wh_purge_streams`, `180_StreamPurge.sql:76`; `IStreamPurger`, `src/Whizbang.Core/Messaging/IStreamPurger.cs:30` | works; deletes every companion row |
| Destruction holds | `wh_event_destruction_hold`, `079_EventDestructionHold.sql:12` | honored by the ephemeral reaper only |

Three facts shape this plan:

1. **Every destruction is a delete today.** The ephemeral reaper reads the hook's decision for
   `Cancel` and `DeferUntil` and ignores `DestructionResult.Disposition`
   (`src/Whizbang.Core/Workers/MaintenanceWorker.cs:805-818`). `StreamCloser` ignores it too and
   archives only when the caller passes `archive: true`. Nothing in `src/` reads
   `Disposition.Archive`, `Compact` or `CryptoShred`. The `Destruction` attribute enum says "the
   analyzer rejects a value whose phase has not shipped" (`src/Whizbang.Core/Attributes/Destruction.cs:10`);
   no such analyzer exists, so `[Ephemeral(Destruction = Destruction.Archived)]` compiles and deletes.
2. **"Close" means the wrong thing.** `close_stream` truncates a prefix. "Closing the books" (B6) is
   a different, higher-level operation that should own the word.
3. **Defect 4: `close_stream` fails or leaves debris.**
   - `fk_perspective_cursors_event` is `ON DELETE RESTRICT`
     (`src/Whizbang.Data.EFCore.Postgres.Generators/Templates/DbContextSchemaExtensionTemplate.cs:1446`).
     A perspective that does not subscribe to the carry-forward event keeps its cursor on the last
     detail event it applied, inside the truncated range, so the close raises a foreign-key error.
     The function still says "no FK" (`084_CloseStream.sql:15`, `093_OriginGeneration.sql:232`);
     `wh_purge_streams` already deletes cursors first for this reason (`180_StreamPurge.sql:174`).
   - The constraint exists only on the EF Core schema path; the Dapper schema defers it
     (`src/Whizbang.Data.Schema/Schemas/PerspectiveCursorsSchema.cs:21`). The defect is invisible on
     Dapper, so tests must cover both engines.
   - Snapshots at or below the close point (`wh_perspective_snapshots.snapshot_version`, added by
     `209_SnapshotsCarryTheirStreamVersion.sql:58`) survive. A rewind can select one and try to replay
     from events that no longer exist.

## Goal

A developer or an operator can say how a stream ends, and the framework carries it out safely:
seal it, snapshot it, wait out a grace period, then archive, compact, purge or close the books, with
the archive readable again on demand. Hot storage holds only streams someone still reads.

**Decisions (maintainer, final):** the append-to-archived-stream policy is per stream type; archived
detail is read through transparently on load, rewind and rebuild; the archive is pluggable, with a
Postgres-table store built in and an Azure Blob store as the production default; each perspective
chooses what happens to its rows for archived streams; all of it ships in 1.0.

## Slices

Each slice is one TDD increment (RED tests first, docs in the same PR). Docs paths are under
`whizbang-lib.github.io/src/assets/docs/`; "new" means absent from `develop` today.

### S1. Fix defect 4 in the truncate primitive (done, #1486)

- **Change (landed).** Migration `212_CloseStreamReanchorsCursorsAndSnapshots.sql` redefines `close_stream`
  whole (editing `093` in place is no longer allowed; CI refuses edits to shipped migrations). Decisions:
  - A cursor inside the range moves onto the boundary event (the first survivor above the close point).
    When its perspective still has an unprocessed work row for that boundary event, the close is refused
    with `cursor_behind`: the cursor is a read position, so moving it onto an unapplied event would skip it.
  - Snapshots: kept when at or above the close point, or, for a perspective whose cursor is inside the range,
    at or after that cursor's event (its exact current state). Everything else at or below the close point,
    and a versionless snapshot on a truncated event, is removed.
  - An active `wh_event_destruction_hold` on an event in the range refuses the close with `held`; lapsed holds
    go with the events. Processed `wh_perspective_events` rows of the truncated events are removed.
    `wh_message_deduplication` rows are kept (they turn a redelivered copy of a truncated event away).
- **Tests.** `StreamCloseSqlTests` (EF Core, with the RESTRICT key) and `DapperStreamCloseCompanionTests`.
- **Docs (pending, docs repo).** `v1.0.0/fundamentals/events/event-streams.md` (truncation section).

### S2. Rename the prefix truncate to free "close"

- **Change.** `close_stream` becomes `truncate_stream_prefix` (`drop_all_overloads('close_stream')`
  first, per README rule 5). `IWorkCoordinator.CloseStreamAsync` (`IWorkCoordinator.cs:1903`)
  becomes `TruncateStreamPrefixAsync`; `StreamCloseResult` (`IWorkCoordinator.cs:2629`) becomes
  `StreamTruncateResult`; `IStreamCloser`/`StreamCloser` become `IStreamPrefixTruncator`/
  `StreamPrefixTruncator`; the DI line at `src/Whizbang.Core/Workers/WorkerPipelineExtensions.cs:378`
  follows. `EFCoreWorkCoordinator.CloseStreamAsync` (`src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:1756`)
  is renamed with it. Pre-1.0, so no shim.
- **RED tests.** A schema test that no function named `close_stream` exists after migration; the
  existing `StreamCloserTests`, `StreamCloserBranchCoverageTests` and `StreamCloserFoldOrderTests`
  move to the new names and fail until the rename lands.
- **Docs.** `contributors/data-engines/sql-function-contracts.md`, `v1.0.0/fundamentals/events/event-streams.md`.

### S3. Stream lifecycle ledger (candidate discovery without scanning the event store)

- **Change.** New migration with `wh_stream_lifecycle` (stream_id PK, stream_type, tenant,
  first_appended_at, last_appended_bucket, last_version, approx_bytes, state, sealed_at,
  disposition_due_at, archive_locator, origin_generation). The emit chain
  (`_emit_event_store_chain`, latest in `162_InboxWorkStateSideTable.sql`) upserts one row per stream
  per batch. To keep this off the hot path's HOT-update budget, `last_appended_bucket` advances only
  when the hour changes, and the only indexed columns are `state` and `disposition_due_at`
  (partial index on due rows). A one-time, batched backfill gated on a `wh_settings` version marker
  (README rule 6) seeds existing streams.
- **RED tests.** Appends create and advance the row; a second append within the hour does not
  rewrite an indexed column; the backfill is resumable and idempotent; `EXPLAIN` of the due-stream
  query uses the partial index and never touches `wh_event_store`.
- **Docs.** New `v1.0.0/fundamentals/events/stream-end-of-life.md`.

### S4. P1: developer-driven finalization and sealing

- **Change.** `IStreamFinalizer.FinalizeAsync(streamId, reason)` and a declarative
  `[TerminalEvent]` attribute on an event type (generator-registered, no reflection). Finalizing
  sets `state = Sealed`. `IEventStore.AppendAsync` (`src/Whizbang.Core/Messaging/IEventStore.cs:47,75`)
  refuses a sealed stream with a typed `StreamSealedException` (stream id, sealed_at, reason); the
  emit chain refuses as a backstop so outbox-path writers cannot bypass it.
- **RED tests.** Appending the terminal event seals the stream in the same transaction; the next
  append throws `StreamSealedException` on both engines; an outbox-path append to a sealed stream
  fails into the dead-letter path with the same reason; finalize is idempotent.
- **Docs.** `stream-end-of-life.md`; `v1.0.0/fundamentals/events/event-streams.md`.

### S5. P1: final snapshot and scheduled disposition

- **Change.** On seal, request a final snapshot for each consuming snapshotting perspective
  (`IPerspectiveSnapshotStore`, `src/Whizbang.Core/Perspectives/IPerspectiveSnapshotStore.cs:16`) and
  set `disposition_due_at = sealed_at + grace`. A new `IMaintenanceStep`
  (`src/Whizbang.Core/Workers/IMaintenanceStep.cs:24`) claims due rows in batches and runs the
  disposition only when the consumption gate passes and no `wh_event_destruction_hold` row covers the
  stream's events; otherwise it pushes `disposition_due_at` forward.
- **RED tests.** Nothing happens before the grace ends; a pending work item or an active hold defers
  and is retried; the final snapshot exists before disposition; a `PreDestruction` veto defers.
- **Config.** `Whizbang:StreamLifecycle:FinalizeGrace` (`Whizbang__StreamLifecycle__FinalizeGrace`,
  default `7.00:00:00`); `Whizbang:StreamLifecycle:SweepBatchSize` (default `500`).

### S6. Wire `Disposition`

- **Change.** The reaper (`MaintenanceWorker.cs:805`) and the stream disposition step route on
  `DestructionResult.Disposition`: `Delete` as today, `Archive` through `IArchiveStore` (S7) before
  the delete, `Compact` to a registered compaction handler (refused with a logged reason when none
  is registered), `CryptoShred` refused until its phase ships. `StreamPrefixTruncator` drops its
  `archive` boolean in favor of the declared or hook-returned disposition. Add the missing analyzer:
  a `Destruction` value whose handler has not shipped is a compile error, not a silent delete.
- **RED tests.** A hook returning `Proceed(Disposition.Archive)` leaves the detail readable from the
  archive; `Compact` without a handler keeps the data and logs; the analyzer flags
  `Destruction.OnCompaction` until a handler exists.
- **Docs.** New `v1.0.0/fundamentals/events/stream-end-of-life.md#dispositions`; the ephemeral
  events page is still a proposal (`proposals/ephemeral-events.md`) although code `<docs>` tags
  point at `fundamentals/events/ephemeral-events`; promote it in this slice.

### S7. B3: `IArchiveStore` and the Postgres-table store

- **Change.** `IArchiveStore` in Core: `WriteAsync(ArchiveBatch)`, `ReadAsync(streamId, fromVersion)`,
  `RestoreAsync(streamId)`, `DeleteAsync(streamId)`. `PostgresTableArchiveStore` wraps
  `wh_event_archive` and keeps today's single-transaction copy-then-truncate. Every archive bumps the
  origin generation (`_wh_bump_origin_generation`, `093_OriginGeneration.sql:32`) so integrity
  re-verifies instead of alarming. The archived stream keeps a tombstone: its `wh_stream_lifecycle`
  row stays with `state = Archived`, `last_version` and `archive_locator`.
- **RED tests.** Archive then read returns byte-identical events in version order; a failure between
  copy and truncate leaves the stream fully hot; the tombstone survives and blocks version reuse.
- **Docs.** New `v1.0.0/fundamentals/events/archive-store.md`.

### S8. B3: Azure Blob archive store (production default)

- **Change.** New package `Whizbang.Archive.AzureBlob` (reusing `Azure.Storage.Blobs`, already pinned
  in `Directory.Packages.props`). One blob per archive chunk: JSONL, zstd-compressed, with a manifest
  row (`wh_archive_manifest`: stream, version range, count, sha256, generation). Two-phase: upload,
  verify hash, then truncate; a crash between phases re-uploads idempotently by content hash.
  Registering the package replaces the Postgres store.
- **RED tests.** Round-trip against Azurite; a corrupted blob is detected by hash on read; a crash
  after upload and before truncate leaves no duplicate and no loss.
- **Config.** `Whizbang:Archive:AzureBlob:ServiceUri`, `:ContainerName` (default `whizbang-archive`),
  `:CompressionLevel` (default `3`), `:MaxEventsPerChunk` (default `10000`); env form
  `Whizbang__Archive__AzureBlob__ContainerName`.
- **Docs.** `archive-store.md#azure-blob`; `v1.0.0/fundamentals/offloads/providers/azure-blob.md`
  (cross-link).

### S9. Read-through, restore, and the append-to-archived policy

- **Change.** `IEventStore.ReadAsync` (`IEventStore.cs:177,189`), rewind and
  `PerspectiveRebuilder` consult the tombstone and stream from the archive when the requested range
  is archived. `RestoreAsync` re-inserts detail, bumps the generation and returns the stream to
  `Active`. Per stream type, an append to an archived stream is one of: `NewGeneration` (start a new
  generation seeded from the final snapshot), `AutoRestore`, or `Refuse` (`StreamArchivedException`).
- **RED tests.** Load, rewind and rebuild return the same model for an archived and an unarchived
  copy of the same stream; each append policy behaves as declared; a restore is idempotent.
- **Config.** `Whizbang:Archive:ReadThrough` (default `true`);
  `Whizbang:StreamLifecycle:Types:<StreamType>:AppendToArchived` (default `Refuse`).
- **Docs.** `archive-store.md#read-through`; `v1.0.0/fundamentals/perspectives/rewind.md`;
  `v1.0.0/fundamentals/perspectives/rebuild.md`.

### S10. Per-perspective policy for rows of archived streams

- **Change.** `[ArchivedStreamRows(Keep | Drop | Archive)]` on a perspective, with a config override.
  `Drop` deletes the row and its cursor and snapshots (shares the companion cleanup from WS-5);
  `Archive` moves the row JSON into the archive store beside the events and restores it on restore.
- **RED tests.** Each choice, on both engines; a restore brings back an archived row.
- **Config.** `Whizbang:Perspectives:ArchivedStreamRows:<Perspective>` (default `Keep`).
- **Docs.** `archive-store.md#perspective-rows`; `v1.0.0/fundamentals/perspectives/perspectives.md`.

### S11. B1: policy-driven end-of-life

- **Change.** A stream type declares a lifecycle policy (attribute plus a registration builder,
  source-generated), overridable from config. Triggers: sliding idle TTL, absolute age,
  finalized plus grace (S5), version cap (archive the prefix, keep the tail N), per-tenant size cap
  (from `approx_bytes`), explicit. Actions: `Archive`, `Compact` (ephemeral only), `Purge` (through
  `IStreamPurger`), `CloseBooks` (S12). The S5 step evaluates triggers from `wh_stream_lifecycle`
  only.
- **RED tests.** One per trigger: a stream just inside the threshold is untouched, just outside is
  acted on; the version-cap trigger leaves exactly N events hot; the tenant cap picks the oldest
  streams first; config overrides the attribute.
- **Config.** `Whizbang:StreamLifecycle:Enabled` (default `false`);
  `Whizbang:StreamLifecycle:Types:<StreamType>:IdleTtl`, `:MaxAge`, `:KeepTailVersions`,
  `:TenantByteCap`, `:Action`; env form `Whizbang__StreamLifecycle__Types__<StreamType>__IdleTtl`.
- **Docs.** `stream-end-of-life.md#policies`; `v1.0.0/operations/configuration/configuration-reference.md`.

### S12. B6: close the books

- **Change.** `IBookCloser.CloseBooksAsync(streamId, closingSummary, openingBalance)`: (1) append the
  closing summary as a durable event, (2) seal (S4), (3) open a successor stream whose first event is
  the opening balance and carries the predecessor id and final version, (4) schedule the
  predecessor's declared disposition (S11). Steps 1 to 3 commit atomically. Promote
  `proposals/archival-compaction.md` into the published page.
- **RED tests.** The successor's opening event references the predecessor; the predecessor refuses
  appends; a failure at step 3 leaves the predecessor unsealed; the predecessor is archived after
  grace and still readable.
- **Docs.** New `v1.0.0/fundamentals/events/closing-the-books.md`.

## Engineering standards

Strict red-before-green TDD (`ai-docs/tdd-strict.md`); 100% line and branch coverage on hand-written
code plus the per-PR new-code gate; AOT-safe, no reflection, registration through generators
(`ai-docs/aot-requirements.md`); every public member carries `<docs>` and `<tests>` tags
(`ai-docs/code-standards.md`); SQL follows `src/Whizbang.Data.Postgres/Migrations/README.md`
(idempotent, `__SCHEMA__`-qualified everywhere including function bodies, `drop_all_overloads` before
an arity change, header block, `COMMENT ON`, `debug_mode` guard on destructive work, constants in
`constants.txt`). Both engines (EF Core and Dapper) get every SQL test.

## Verification

- Archive, read-through and restore round-trips on both engines and against Azurite.
- Re-run the storage-size queries used for the original evidence against a test environment before
  and after enabling an idle-TTL archive policy; `wh_event_body` and `wh_event_store` should shrink
  by the archived share, and `whizbang.table.estimated_bytes` should follow.
- A rewind and a full rebuild of a perspective over archived streams produce the same rows as before
  archiving.

## Decisions (formerly open questions)

Decided on 2026-10-22 in #1673 (the owner accepted the recommendations).

1. **A stream's type** is identified by the existing `wh_event_store.aggregate_type` column.
2. ~~Cursor re-anchoring in S1 versus dropping the cursor?~~ Re-anchor (S1, #1486). Dropping the
   cursor would make the next apply start from an empty position and the next rewind replay from an
   empty model; the kept exact-floor snapshot is what keeps a later rewind correct.
3. **Archive compression** ships Brotli first, because it is in the base class library. zstd comes
   later and needs a new dependency.
4. **`Compact`** is a developer-supplied compaction handler in 1.0.
5. **The lifecycle sweep** runs under the maintainer duty only, as the partition manager does.
