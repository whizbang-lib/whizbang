# Set-based perspective writes (#2246)

Status: **in progress**, started 2026-11-10. Phases 1 and 2 of #2246. Later, separate: write-behind (#2247),
staging-table rebuilds (#2248).

## Why

A batch of perspective work is processed one (stream, perspective) pair at a time. Each pair runs in parallel with its own
scope and DbContext and spends five to seven round trips on reads and writes. A fan-out that puts one event on each of
thousands of streams therefore costs thousands of single-row statements, and a composite does not help: at the
publishing service an owned composite is stored one inner event at a time.

## What is already batched (keep it)

- Checkpoints and work completion: `BatchedCompletionStrategy` buffers, and `PerspectiveCompletionFlushWorker` writes
  them with one `complete_perspective` call per flush, after the row write. Phase 1 does not move them into the
  row-write transaction. The runner already treats them as a later step, and moving them is not needed for the cost.
- Cursor prefetch on the drain path (`GetPerspectiveCursorsBatchAsync`).

## Today's per-pair path (origin/develop ded9dbaa3)

Worker groups claimed work by (stream, perspective) and runs `Parallel.ForEachAsync` at the governor width
(`PerspectiveWorker.cs` around 1395, 2029). The generated runner, per pair:

1. `ReadForApplyAsync`, then `GetByStreamIdAsync` unless absent, then eviction and purge-marker probes when absent.
2. Idempotency filter or rewind anchor (#1347), then fold (`ApplyEvent`, `IsFirstInLocalStream`).
3. `GetCommitSequenceAsync`, then `TryUpsertAsync` or `TryUpsertWithPhysicalFieldsAsync`. This is a single-row
   `INSERT ... ON CONFLICT ... WHERE <ordering guard>` or `UPDATE ... WHERE xmin = @expected AND <guard>`.
4. `!rowChanged` leads to a rewind (#1608). A row conflict retries up to 5 times.
5. `FlushAsync`, snapshot, PostPerspectiveDetached, and a completion returned to the worker (inline lifecycle and the
   sync tracker run in the worker).

## Design: split each pair into prepare and commit, and write the batch in between

Decided 2026-11-10 over a group-commit sink inside the store (runners unchanged, a counting barrier across parallel
runners): the split is deterministic, has no barrier, and lets the reads be batched as well as the writes.

The runner gains two entry points next to `RunWithEventsAsync`, which stays as the per-pair path:

- **Prepare**: load state from the batch's state source, then the idempotency filter and fold. It returns a
  `PendingPerspectiveWrite` (stream, perspective, table, model, physical values, scope, metadata, expected version,
  ordering guard values, and an operation of insert, update, upsert or purge), or a decision that the pair needs the
  per-pair path (rewind anchor, bootstrap, purge marker present, conflict).
- **Complete**: given the write's outcome, do what step 4 and 5 do today. That is snapshot, detached lifecycle and the
  completion. A refused row goes to the existing rewind path, and a conflict reruns the pair through the per-pair path.

The worker, per batch:

1. **State source** (seam): one query per perspective table for all streams in the batch
   (`SELECT ... WHERE id = ANY(@ids)`, returning model, metadata and xmin). Streams whose cursor is absent and whose
   first claimed event is first in its local stream are known new, so they are not read. Purge markers and eviction
   probes are answered in one query each.
2. Prepare every pair in parallel (CPU only, no I/O).
3. **Flush policy** (seam): default is every batch.
4. **Write sink** (seam): group pending writes by table and operation, and write each group with one statement per
   group. Inserts use `INSERT ... SELECT FROM jsonb_to_recordset(@rows) ON CONFLICT DO NOTHING RETURNING id`, updates
   use `UPDATE ... FROM jsonb_to_recordset(@rows) WHERE id = r.id AND xmin = r.xmin AND <guard> RETURNING id`, and
   purges use `DELETE ... WHERE id = ANY(@ids)`. Ids not returned are refused or conflicted, which is the same signal as
   today's `rowChanged=false` and conflict. One transaction per table group. A statement that fails (constraint, bad
   row) is split in halves until the failing pair is found, and that pair alone takes the per-pair path.
5. Complete every pair, then the inline lifecycle and the sync tracker as today.

Locks: the per-(stream, perspective) apply coordinator and affinity gate are taken for every pair in the batch before
prepare and released after complete, in a fixed order (stream id, then perspective) so two batches cannot deadlock.

## Selection and registration

- `PerspectiveWriteMode.SetBased` is the default; `PerspectiveWriteMode.PerStream` keeps today's path. The service
  default is set in options, and a per-perspective override is an attribute.
- The three seams' defaults are registered by W! with `TryAdd` in the same place the worker is registered, and a test
  resolves each from a plain registration.
- `IApplyBatchStrategy` / `SlidingWindowApplyBatchStrategy` (no caller) is removed. The drain path's `DrainBatcher` is
  the live coalescer, and the flush-policy seam replaces the idea.

## Phase 2: conformance suite

One integration suite on Postgres, parameterized by write mode, runs real generated runners through the worker:
ordering guard and rewind, late events, idempotency, row version conflicts, model actions (purge), physical fields
(split and inline), snapshots and rewind from snapshot, collectives on the same rows, lifecycle stages and sync,
rebuild, and failure isolation (a throwing apply, a failing row). The existing integration classes listed in the issue
are the starting inventory.

## Steps (each RED then GREEN, committed separately)

1. Docs-site page (proposal): perspective write modes, with the reasons above.
2. Write sink in `Whizbang.Data.Postgres`: grouped statements and the per-row outcome, tested on Postgres
   (insert, update with guard, xmin conflict, purge, physical fields, a failing row is isolated).
   **Done** (local commits on this branch): `IPerspectiveWriteSink`, `PendingPerspectiveWrite` and
   `PerspectiveWriteOutcome` in Core; `PerspectiveWriteSql` and `PostgresPerspectiveWriter` in `Whizbang.Data.Postgres`
   (multi-row `VALUES` per table and shape, `RETURNING id`, one version read to tell refused from conflict, savepoint
   bisection inside a transaction); `EFCorePostgresPerspectiveWriteSink`, registered with `TryAddScoped` by the
   Postgres driver. The per-stream atomic upsert and the sink share one preparation step
   (`BaseUpsertStrategy.PreparePendingWrite`), so both write the same bytes. Open for step 4/5: the public entry the
   runner prepares through, and which DbContext the sink writes when a service has more than one.
3. State source: batched load and known-new detection.
   **Done** (local commits): `IPerspectiveStateSource` / `IPerspectiveBatchState` and the default
   `BatchedPerspectiveStateSource` (registered with `TryAddSingleton` by the Postgres driver). A batch reads no row for a
   stream it knows is new (no cursor, first event first in its stream), and for the rest of a perspective reads the rows
   once through the store's `ReadForApplyManyAsync`: two statements per table (versions, metadata and scopes with one
   `id = ANY` read, then the models with one EF query, so every model is materialized as `GetByStreamIdAsync` does). The
   purge-marker and history probes of the rows it found missing are one query each (`PurgedAmongAsync`,
   `StreamsWithEventsBeforeAsync`, with per-stream defaults). A failed batched read is logged and each pair reads per
   stream. Only prepare consults it; a rerun reads afresh. The snapshot count is now per stream (it was per runner).
4. Runner template: prepare and complete, generated for every perspective, with generator tests.
   **Done** (local commits): `IPerspectiveRunner.PrepareAsync` / `CompleteAsync` (defaults hand every pair to the
   per-pair path), `PreparedPerspectiveApply` + `PerspectivePerPairReason`, `IPerspectiveStore.PrepareWrite` /
   `PreparePurge` (EF Core store through `BaseUpsertStrategy.PreparePendingWrite`), and the lock seam
   `PerspectiveApplyLockSet` (stream id, then perspective name, ordinal; released in reverse). The per-pair attempt is
   built from the same pieces as the split: `_readBatchAsync` (row load through `_loadRowForApplyAsync`, the seam step
   3 replaces), `_foldAsync`, `_beginRowWriteAsync`, then `_writeRowAsync` (per-pair) or the store's prepare, and
   `_finishApplyAsync`. Complete reruns a hand-back, a conflict or a rejected row through the lock-free per-pair core.
5. Worker: the set-based batch path behind the mode, with locks taken as a set.
   **Done** (local commits): `PerspectiveApplyBatch` (lock set, prepare at the governor width, one flush per DbContext
   through that context's keyed sink and the flush policy, complete with each outcome; a pair's failure stays on the
   pair). Claimed path: every group's affinity gate in `PerspectiveApplyLockSet.Order`, then begin (resolve,
   pre-lifecycle), rewind/bootstrap/no-read runners per pair, the rest batched, then the same finish (report, sync,
   post-lifecycle). Drain path: the first lap of every stream batched (collective sink and per-stream pairs first, per
   pair), later laps per pair. The per-pair code is the same begin/finish/fail split, so the two cannot drift.
6. Registration and mode selection, and the resolution test.
   **Done** (local commits): `PerspectiveWriteMode { SetBased, PerStream }`, `PerspectiveWorkerOptions.WriteMode`
   (SetBased default), `[PerspectiveWriteMode]` read by the generator, and `PerspectiveWriteModes` (every generated runner
   registers its declared mode; an unregistered perspective is per stream). `IApplyBatchStrategy` is the flush-policy
   seam (`FlushEveryBatchStrategy`), registered with the worker; the old unwired strategy is deleted.
   **Batched reads (after review, 2026-11-11):** the commit sequences a batch records are read with one statement
   (`IEventStore.GetCommitSequencesAsync`). A claimed batch reads its set-based pairs' cursors with one statement and
   their streams' events with one read (`IEventStore.ReadPolymorphicManyAsync`, at most two statements in the EF Core
   store); each runner selects its events (`IPerspectiveRunner.SelectEvents`), and the processed events after the write
   come from the same read. A new claimed stream costs one cursor read, one event read, one commit-sequence read, no
   row read and one insert per table for the whole batch (`NewStreams_OnTheClaimedPath_...`). The drain skips the cursor
   read of a pair its batched cursor read found none for. Both trust the batched cursor read only when the coordinator
   says it reads cursors (`IWorkCoordinator.ReadsCursorsInBatches`, true in the EF Core and Dapper drivers), since the
   interface default returns nothing; the upcasting decorator declines a type change and the archive decorator a
   stream whose first events are archived, so those pairs read their own.
7. Conformance suite across both modes.
   **Done** (`PerspectiveWriteModeConformanceTests`, 7 cases x 2 modes): new and existing streams with Extracted and
   Split physical fields, a first event that finds a row, a row changed after the read (row version conflict), a row
   moved past the batch (ordering guard, rewind, late event, idempotency), purge, a throwing apply next to a row the
   database rejects, and sync. Concurrent writers are injected through the flush policy. Not yet in the suite:
   snapshots and rewind from a snapshot, collectives on the same rows, lifecycle receptors, rebuild (covered for the
   set-based path by focused prepare/complete tests).
8. Measure: a 100k-stream fan-out, round trips and rows per second, before and after.
   **Done** (`Measure_FanOutOfNewStreamsAsync`, explicit; 2026-11-12): 100,000 new streams, four consumers, claim batch
   1,000. Per-stream: 574,928 statements on the table (100,000 inserts, 474,928 row reads), 1,255,840 in total, 683.6 s,
   146 rows/s. Set-based: 2,915 on the table (709 inserts, 2,206 row reads), 662,087 in total, 764.0 s, 131 rows/s.
   Rows per second do not move: both modes are bound by per-pair completion (`complete_perspective_cursor_work` and its
   read, about three per stream with the harness's per-pair completion reporting), then claim and drain round trips.
   Follow-ups: batch completion reports for a fan-out; find why the set-based run still reads rows (2,206 reads for
   new streams, likely re-laps or re-claims) and why both modes report more cursor work than streams (re-claims under
   load).

## Decisions after step 2 (2026-11-10)

- **DbContext:** each perspective writes through its own store's DbContext, never one global sink, so a service with
  several contexts works. A test with two contexts locks it.
- **Transactions:** one transaction per table and statement-shape group. Nothing spans tables: each row's outcome
  stands alone, and completion is already a later step.
- **Prepare entry:** a public method on `IPerspectiveStore` returning a `PendingPerspectiveWrite` or null. A null
  (non-Npgsql provider, unresolvable type, non-plain identifier) sends that pair to the existing per-pair path, as do a
  rewind anchor, a bootstrap, a purge marker, an eviction hit and a row conflict.
- **No unwired types:** the sink ships in the same PR as the worker path that calls it.
