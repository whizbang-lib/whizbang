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
| Gated prefix truncate | `wh_truncate_stream_prefix` (`close_stream` until S2), latest in `src/Whizbang.Data.Postgres/Migrations/240_TruncateStreamPrefix.sql` | works; consumption gate, carry-forward guard, digest subtraction, origin-generation bump |
| Cold copy table | `wh_event_archive`, `085_EventArchive.sql:14` | written only by `wh_truncate_stream_prefix(p_archive => TRUE)` |
| Archive read-back | `IWorkCoordinator.GetArchivedEventsAsync`, `src/Whizbang.Core/Messaging/IWorkCoordinator.cs:1917` | explicit call only; no load, rewind or rebuild reads it |
| Hooked truncator | `IStreamPrefixTruncator` / `StreamPrefixTruncator` (`IStreamCloser` until S2), `src/Whizbang.Core/Lifecycle/StreamPrefixTruncator.cs` | fires `PreDestruction`/`PostDestruction` around the truncate |
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

**Status (2026-10-24):** done. #1486, closed by PR #1522 (migration 212). The truncation section of
`v1.0.0/fundamentals/events/event-streams.md` is published (docs repo).

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
- **Docs (published, docs repo).** `v1.0.0/fundamentals/events/event-streams.md` (truncation section).

### S2. Rename the prefix truncate to free "close" (done, #1731)

- **Landed.** Migration `240_TruncateStreamPrefix.sql` drops every `close_stream` overload and creates
  `wh_truncate_stream_prefix` from the 214 definition; its result column is `truncate_status` and its success
  status `truncated` (was `closed`), so no "close" is left in the primitive's contract. The C# renames below
  landed as planned, with `IStreamPrefixTruncator.TruncateAsync` (was `IStreamCloser.CloseAsync`) and
  `IWorkCoordinator.GetStreamTruncateTargetsAsync` (was `GetStreamCloseTargetsAsync`). Kept: the SQL test
  classes `StreamCloseSqlTests` and `DapperStreamCloseCompanionTests` and their `CloseStream_` methods, because
  migrations 212 and 214 link to them by name and the migration lint validates those links; and the scheduled
  close types (`ScheduledStreamClose`, `StreamCloseSchedule`, `ScheduledStreamCloseReceptor`), whose name is the
  schedule's stored event type and which belong to closing the books (S12).
- **Change.** `close_stream` becomes `wh_truncate_stream_prefix` (`drop_all_overloads('close_stream')`
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

**Status (2026-10-29):** done in #1732 (migration 260), pending review. S4, S5, S7 and S11 build on it.

- **Landed.** `wh_stream_lifecycle` with the planned columns; `state` is `Active`, `Sealed` or `Archived`
  (`ck_stream_lifecycle_state`); `origin_generation` is nullable (null until a history mutation). All three write paths
  (outbox chain, inbox chain, `wh_append_event`) keep it under the stream lock they already hold; `wh_purge_streams`
  removes the row. Ephemeral events, compaction origins and the empty stream id get no row. Indexes: the key and
  `idx_stream_lifecycle_due (disposition_due_at, stream_id) INCLUDE (state) WHERE disposition_due_at IS NOT NULL`;
  fillfactor 45 (measured: 70 kept only 58% of whole-page rewrites heap-only). The backfill runs outside the
  migration from the `stream-lifecycle-backfill` maintenance step (`StreamLifecycleBackfill`, both drivers), gated on
  `stream_lifecycle_backfill_version` with a cursor in `stream_lifecycle_backfill_cursor`.
- **Left for later slices.** Prefix truncation does not reduce `approx_bytes` (S7/S11 adjust it when they truncate on
  purpose); nothing writes `sealed_at`, `disposition_due_at`, `archive_locator` or `origin_generation` yet.

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

**Status (2026-10-29):** implemented on `feat/1733-stream-sealing` (#1733), on top of storage bundle 9 (S3 and S7).
Migration `266_SealedStreamsRefuseAppends.sql` (267, also reserved, is unused). Decisions taken in the slice:

- **The reason** is a new column, `wh_stream_lifecycle.sealed_reason`, written with `sealed_at` and never changed after.
  A terminal event writes `Terminal event <type> (event <id>)`. The seal is `sealed_at`, not the state: an archived
  stream stays `Archived` when finalized, and `wh_restore_archived_stream` (copied from 262) now returns a sealed stream
  to `Sealed` rather than `Active`.
- **`[TerminalEvent]`** is registered by the message catalog generator (`TerminalEvents.Register` in its module
  initializer, plus `IsTerminal` on the catalog entry for the receive path) and stamped as `EventFlags.Terminal`
  (`1 << 6`); `_wh_event_flags_from_names` (copied from 232) reads the name. Inherited; ignored on a command.
- **The refusal** sits in the outbox and inbox emit chains and in `wh_append_event`, under the stream lock. A direct
  append raises `WHS01` (`StreamSealedException` on both drivers); the chains dead-letter the refused event alone
  (`MessageFailureReason.StreamSealed`, 25, held for review) and store the rest, unlike the tombstone's `WHA01` trigger,
  which fails the whole batch. An outbox write whose caller awaits the flush (the `RaiseOnMixedStream` element of the
  immediate and scoped strategies) raises `WHS01` from `store_outbox_messages` instead (owner decision on #1733,
  matching the mixed-stream refusal), and the strategy withdraws it. An event after a terminal event in the same write
  is refused.
- **Ephemeral streams** are never sealed: they have no ledger row, finalizing one returns `NothingToSeal`, and the
  terminal flag on an ephemeral event does nothing.
- **Cost:** one probe per batch of `idx_stream_lifecycle_sealed` (partial on `sealed_at`, holding only sealed streams);
  measured in `StreamSealingEmitCostScenarioTests` (numbers in the docs page).

Open for later slices: S5 (scheduling the disposition of a sealed stream), S12 (closing the books seals through
`IStreamFinalizer`). Docs: `stream-end-of-life.md#sealing-a-stream` and `event-streams.md` (docs branch `docs/1733`).

Original slice text:

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

**Status (2026-10-24):** open. No disposition step and no `Whizbang:StreamLifecycle` options. Depends on S3 and
S4.

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

**Status (2026-10-24):** open. No code routes on `DestructionResult.Disposition`; `Destruction.OnCompaction` and
`Archived` are still documented as planned and inert (`src/Whizbang.Core/Attributes/Destruction.cs:10`). The
ephemeral events page this slice was to promote is already published. Depends on S7 for `Archive`.

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

**Status (2026-10-29):** implemented on `feat/1736-archive-store` (#1736), to ship with S3 (#1732) in one bundle, S3
first. Migration `262_ArchiveStoreKeepsATombstone.sql`; `IArchiveStore`, `ArchiveBatch` (one stream, prefix or whole),
`ArchiveLocator` (`{store}://{address}?codec={codec}&format={format}`, codec recorded per archive), and
`PostgresTableArchiveStore` (codec `none`: the rows stay `jsonb`), registered by both drivers and used by the prefix
truncate's archive path. A whole-stream archive parks cursors in `wh_event_archive_cursors`, keeps snapshots and leaves
the tombstone (built from the events by 260's rules when the stream has no ledger row yet); a statement trigger on
`wh_event_store` refuses version reuse (`WHA01`). The archive keeps `approx_bytes` measuring the hot store (a prefix
subtracts what it moved, a whole stream counts 0, a restore adds back), and `wh_purge_streams` (copied from 260) also
removes the parked cursors. `RestoreAsync` (restores exact positions) and `DeleteAsync` are implemented here, so S9
only consumes them. Open for S9: the per-type append-to-archived policy on the write paths (today the trigger raises,
which fails the whole emit-chain batch). Docs: `v1.0.0/fundamentals/events/archive-store.md` (docs branch `docs/1736`).

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

**Status (2026-10-29):** implemented on `feat/1737-blob-archive` (#1737), on top of the S7 bundle. Migration
`268_ArchiveManifest.sql`; package `Whizbang.Archive.AzureBlob` (`AzureBlobArchiveStore`, `AzureBlobArchiveOptions`,
`AddWhizbangAzureBlobArchive`, `ArchiveObjectCorruptedException`); both drivers register `PostgresArchiveConnection`.
As built, differing from the plan below: Brotli (decision 3), quality 4 by default (measured; 3 was a zstd level); the
"two phases" are one database transaction (262's `wh_archive_stream` stages and truncates, the objects upload, the
manifest rows are written, then the commit), which makes a crash before the commit leave the stream fully hot and an
uploaded object unreferenced, and a retry rewrites the same content-addressed object; the manifest records each
object's locator, range, count, byte length, SHA-256 and generation (checked on every read), with `ON DELETE CASCADE`
from `wh_stream_lifecycle` so a purge removes it; restore and delete stage objects back through `wh_event_archive` and
reuse 262's SQL; archives the table store wrote stay readable and move into objects on the next write. The account is
`ServiceUri` with a host-registered `TokenCredential`, or `ConnectionString` (Azurite). Migration
`269_ArchiveObjectDeletionQueue.sql` (owner decision on #1737): a trigger on the manifest queues every object a row stops
naming (purge, restore, delete) in the removing transaction, and `ArchiveObjectDeletionStep` (maintenance, maintainer
duty) deletes them with confirm-then-clear and backoff retries. Open: no access-tier setting yet. Docs:
`archive-store.md#the-azure-blob-store` (docs branch `docs/1737`).

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

**Status (2026-10-30):** implemented on `feat/1738-archive-read-through` (#1738), migration
`271_ArchiveReadThroughAndAppendPolicy.sql` (272 and 273, first reserved for it, were later used by #1955 for the archived-collective index and the parked rebuild cursor; 274 by #1958 for the deleted-archive record). Owner decisions of 2026-10-30 on
#1738, as built:

- **Read-through by gap detection** (`ArchiveAwareEventStoreDecorator`, innermost in the event store's decorator chain
  on both drivers when an `IArchiveStore` is registered; `Whizbang:Archive:ReadThrough`, default `true`). A read from the
  start (a sequence read from 0 or 1, an id or between-read with no starting event, the rebuild) asks the archive only
  when the hot read shows a gap (no hot event, or a first hot version above 1); a resumed read never does. Every driver
  read now carries `LocalStreamVersion`; archived rows are decoded by the driver's `DeserializeStreamEvents` (now
  implemented on the Dapper store) and carry their commit positions (`ArchivedEvent.CommitSequence`,
  `OriginServiceId`, `OriginCommitSequence`). The readers that seemed to bypass `IEventStore` (the replay reader, the
  collective replay applier, the runner's rewind and rebuild) all resolve the decorated store, so they read through.
  Measured: a resumed read issues the same statements as before; a from-the-start read of an archived stream one more.
- **Archive rules** in `wh_archive_stream`: a prefix through a version below 1 is refused (`not_past_first_version`);
  one reaching any perspective's newest snapshot is refused (`snapshot_in_range`, refused rather than clamped so the
  caller gets exactly what it asked for or nothing).
- **Snapshots record archived data**: `wh_perspective_snapshots.archived_through_version`, set on the covered snapshots
  by every archive, cleared by every restore; the rewind reads through the archive from a marked snapshot
  (`IEventStore.ReadPolymorphicThroughArchiveAsync`, `IPerspectiveSnapshotStore.GetSnapshotFloorAsync`); a marked snapshot
  with no version is not used (full replay through the gap).
- **Append-to-archived policy** per stream type (`Whizbang:StreamLifecycle:Types:<StreamType>:AppendToArchived`, default
  `Refuse`; `NewGeneration` is not an enum value and fails startup naming #1734), stored in `wh_stream_type_policies`
  on every maintenance tick before the idle gate. 262's whole-batch `WHA01` trigger is dropped; every write path refuses
  per event after the seal's check, one probe of the new `idx_stream_lifecycle_closed` (sealed or archived streams)
  replacing the seal's probe. Refuse: `StreamArchivedException` (`WHA01`) / dead letter `StreamArchived` (26).
  AutoRestore: inline restore and one retry (`WHA02`) for direct and awaited writes; background writes dead-lettered
  `AwaitingRestore` (27), held by a `wh_archive_restore_requests` row (Pending, Restoring, Restored, Failed) queued in the
  same transaction; `ArchiveRestoreRequestStep` (maintenance, both drivers) restores through `IArchiveStore`; any
  restore completes the request and makes its dead letters due for the recovery worker (policy `AfterRestore`); a
  failure holds them for review with the error.

Left for later: NewGeneration (#1734, S5); the Dapper driver has no dead-letter recovery service, so on Dapper an
`AwaitingRestore` dead letter becomes due but nothing redrives it until that service exists. Docs:
`archive-store.md#read-through`, `#appending-to-an-archived-stream`, `#restore-requests`, `rewind.md`, `rebuild.md`
(docs branch `docs/1738`).

Original slice text:

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

**Status (2026-10-24):** open. No `[ArchivedStreamRows]`. Depends on S7; reuses the WS-5 S1 companion cleanup,
which is done.

- **Change.** `[ArchivedStreamRows(Keep | Drop | Archive)]` on a perspective, with a config override.
  `Drop` deletes the row and its cursor and snapshots (shares the companion cleanup from WS-5);
  `Archive` moves the row JSON into the archive store beside the events and restores it on restore.
- **RED tests.** Each choice, on both engines; a restore brings back an archived row.
- **Config.** `Whizbang:Perspectives:ArchivedStreamRows:<Perspective>` (default `Keep`).
- **Docs.** `archive-store.md#perspective-rows`; `v1.0.0/fundamentals/perspectives/perspectives.md`.

### S11. B1: policy-driven end-of-life

**Status (2026-10-24):** open. Nothing is built. Depends on S3 and S5.

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

**Status (2026-10-24):** open. `src/` has no `IBookCloser`. Depends on S4 and S11.

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
