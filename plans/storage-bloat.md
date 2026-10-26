# Storage bloat: rewrite, vacuum, fillfactor, indexes (WS-4)

Stop the framework's tables from holding space they cannot use, and reclaim it online where the
server allows. The real fix is to stop creating bloat (C2, C3); the rewriter (C1) cleans up what is
already there. Depends on `storage-extension-capabilities.md` (WS-8) for the pg_squeeze strategy.

## Context

Evidence from a production deployment's development environment:

- The framework has already flagged 53 tables in `pending_table_rewrites`, and nothing acts on
  them: rewrites need `AllowTableRewrite`, which defaults to false, and when enabled run as
  `VACUUM FULL` at boot under an ACCESS EXCLUSIVE lock.
- `wh_event_store`, meant to be append-only, shows about **22M updates against 1.8M inserts**
  (about 12 per event) and only **4% HOT**. Its indexes are twice the size of a fresh clone's.
- An update-heavy JSON perspective shows 4M updates on 116k rows at 0% HOT. Other update-heavy
  perspectives are near 0%; `wh_perspective_cursors` is at 52%.
- The server autovacuum default waits for 5% of a table to be dead (about 266k rows on a 5M-row table).

What exists today:

| Piece | Where |
|---|---|
| Rewrite queue and bloat detector (`wh_request_table_rewrite`, `wh_tables_needing_rewrite`, `wh_clear_table_rewrite`, settings `table_rewrite_policy`, `table_rewrite_bloat_threshold`, `table_rewrite_min_rows`, `pending_table_rewrites`) | `src/Whizbang.Data.Postgres/Migrations/089_TableRewriteRequests.sql` |
| Detection records requests each maintenance cycle | `src/Whizbang.Core/Workers/MaintenanceWorker.cs:325-329`, `:736-758` |
| Post-ready `Rewrite` step, maintainer duty, gated on `AllowTableRewrite` | `src/Whizbang.Core/Startup/TableRewriteStartupStep.cs:56-97`; registered at `src/Whizbang.Core/Workers/WorkerPipelineExtensions.cs:213`; option at `MaintenanceWorker.cs:1079` |
| `VACUUM FULL` plus re-measure | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:1132-1180`, `:1281` |
| Queue-table autovacuum (scale factor 0.02); event store deliberately not tuned | `Migrations/088_ChurnTableAutovacuum.sql` |
| `wh_event_body` autovacuum | `Migrations/073_ReapConsumedEphemeralBodies.sql:23-27` |
| Side-table precedent for a non-HOT mutable column set | `Migrations/162_InboxWorkStateSideTable.sql:1-40` |
| Perspective table storage attribute (`DataCompression`, `ToastTupleTarget`) | `src/Whizbang.Core/Perspectives/PerspectiveTableStorageAttribute.cs:33-45`; SQL in `src/Whizbang.Generators.Shared/Models/ColumnStorageSql.cs:24-45,131-176` |
| Bounded, fleet-once maintenance step precedent | `src/Whizbang.Data.EFCore.Postgres/IndexStatisticsMaintenanceStep.cs:42` |
| Housekeeping admission (defect 9) | `src/Whizbang.Core/Workers/HousekeepingCoordinator.cs:112-131`, `:304-311`, `:331-351` |

**Found while verifying:** the `table_rewrite_policy` setting (089, line 26) is read by nothing in
`src/` or `tests/`. Rewrites are governed only by the option.

### How HOT works, briefly

Postgres stores rows in 8 KB pages, and an UPDATE writes a new row version. If the new version fits
on the same page **and no indexed column changed** (including columns in a partial index's
predicate), it is a heap-only (HOT) update: no index entries are written and the old version is
pruned inside the page without vacuum. Otherwise every index gets a new entry, which means index
bloat, more WAL and more vacuum work. `fillfactor` (default 100) leaves free space in each page for
HOT; 70 to 90 suits rows that are updated. It cannot help when an indexed column changes, and a
rewritten TOASTed JSONB value writes new TOAST chunks regardless.

## Goal

1. Bloated framework tables are rewritten online when pg_squeeze is usable, and only in an operator
   window otherwise.
2. Framework and perspective tables carry autovacuum and fillfactor settings that match their
   write pattern.
3. `wh_event_store` stops taking avoidable updates.
4. Queue-table indexes that churn to empty are rebuilt without blocking.
5. The housekeeping starvation ceiling is a time, not a count.

## Configuration keys

| Key | Environment variable | Default |
|---|---|---|
| `Whizbang:Workers:Maintenance:TableRewriteStrategy` | `Whizbang__Workers__Maintenance__TableRewriteStrategy` | `Auto` |
| `Whizbang:Workers:Maintenance:AllowTableRewrite` (existing) | `Whizbang__Workers__Maintenance__AllowTableRewrite` | `false` |
| `Whizbang:Workers:Maintenance:OnlineRewriteMaxBytesPerCycle` | `Whizbang__Workers__Maintenance__OnlineRewriteMaxBytesPerCycle` | `10737418240` (10 GB) |
| `Whizbang:Workers:Maintenance:ReindexBloatThreshold` | `Whizbang__Workers__Maintenance__ReindexBloatThreshold` | `3.0` (0 disables) |
| `Whizbang:Housekeeping:MaxDeferral` | `Whizbang__Housekeeping__MaxDeferral` | `01:00:00` |

`TableRewriteStrategy` values: `Auto` (PgSqueeze if usable; otherwise VacuumFull at boot when
`AllowTableRewrite` is true; otherwise detect only), `PgSqueeze`, `PgRepack`, `VacuumFull`, `None`. `PgSqueeze` and `PgRepack` carry
`[RequiresPostgresExtension(...)]`, so an explicit choice on a server without the extension stops
startup with the capability verdict (WS-8).

## Slices

Each slice is one red-then-green increment and its own commit.

### C1: rewriter seam

1. **Seam, behavior unchanged.** Introduce `ITableRewriter` (strategy name, `CanRun`,
   `RewriteAsync` returning the re-measured ratio). Move today's `VACUUM FULL` path behind a
   `VacuumFullTableRewriter`. RED first: characterization tests pinning current outcomes
   (`tests/Whizbang.Core.Tests/Startup/TableRewriteStartupStepTests.cs`,
   `tests/Whizbang.Data.EFCore.Postgres.Tests/TableRewriteJourneyE2ETests.cs` stay green), plus a
   new test that the step resolves the rewriter by strategy. Docs:
   `operations/infrastructure/migrations.md` ("Table rewrites run post-ready").
   **Status (2026-10-23):** done in #1702. `ITableRewriter` (`Strategy`, `CanRun` with
   `CannotRunReason`, `RewriteAsync(coordinator, table)`) and `VacuumFullTableRewriter` live in
   `Whizbang.Core.Workers`; the rewrite itself stays `IWorkCoordinator.RewriteTableAsync`. The step takes
   the registered rewriters and a strategy name, fixed to `TableRewriteStrategies.VACUUM_FULL` in
   `AddWhizbangWorkers` until slice 2 resolves it from `TableRewriteStrategy`. A strategy with no
   registered rewriter skips and names it. The constructor change is recorded in
   `CompatibilitySuppressions.xml`.
2. **`TableRewriteStrategy` with `Auto`.** RED: the resolution matrix (four values by pg_squeeze
   usable or not by `AllowTableRewrite`), one test per cell, including "explicit PgSqueeze plus
   NotAllowlisted fails startup" (WS-8 slice 3). Docs:
   `operations/configuration/configuration-reference.md` (Maintenance section).
   **Status (2026-10-25):** done on branch `feat/1742-1750-rewrite-provisioning` (#1742). `TableRewriteStrategy`
   (`Auto`, `PgSqueeze`, `VacuumFull`, `None`; default `Auto`) on `MaintenanceWorkerOptions`, resolved each run by
   `TableRewriteStrategyResolver` against `AllowTableRewrite` and whether pg_squeeze is usable (Ready or Provisionable
   on every probed database), one test per cell. Per decision 1, `Auto` resolves to `VacuumFull` when permitted and
   otherwise detects and reports; it never picks pg_squeeze, and names the opt-in when pg_squeeze is usable.
   `PgSqueeze` is not gated by `AllowTableRewrite` (choosing it is the opt-in) and never falls back to `VacuumFull`.
   It carries `[RequiresPostgresExtension("pg_squeeze", Feature = "Online table rewrite")]`, the library's first
   setting-driven requirement: explicit `PgSqueeze` on a server that does not allowlist or preload it fails start with
   the fix. No pg_squeeze rewriter exists until slice 3 (#1743): the step skips with
   `...; no table rewriter is registered for it, so requested tables stay queued`. With a capability registry the
   `Rewrite` step depends on the `Capabilities` step. The `PgRepack` value is slice 5.
3. **PgSqueeze strategy, driven by maintenance.** The maintenance cycle (idle-gated by
   `HousekeepingCoordinator`) takes requested tables from `wh_tables_needing_rewrite()` largest
   ratio first and calls `squeeze.squeeze_table(schema, table)` on its own connection (see
   `ai-docs/schema-initialization-connections.md`), up to `OnlineRewriteMaxBytesPerCycle`. A request
   is cleared only after the re-measure improves, as today. Prerequisites checked before each call:
   logical `wal_level`, a free replication slot, a primary key or replica identity, and the role's
   rights. RED: unit tests for ordering, budget and "not cleared when not improved"; integration
   test on an image with pg_squeeze, which needs a new mirrored CI image
   (`ai-docs/ci-image-mirror.md`). Docs: new `operations/infrastructure/storage-maintenance.md`.
   Once this lands, revisit forcing one rewrite for a fillfactor change: today the request waits for
   bloat, by decision in #1594 (option 1), because the only strategy is a blocking VACUUM FULL.
4. **Retire the unread `table_rewrite_policy` key.** A new migration (CI blocks edits to existing
   ones) deletes the row; 089 keeps inserting it, so the new migration runs after it on fresh
   databases too. RED: a migration test that the key is absent after apply.
   **Status (2026-10-23):** done in #1667, migration 235. Nothing in `src/` read the key, so behavior is
   unchanged for any value it held.
5. **PgRepack strategy.** A fifth `TableRewriteStrategy` value, `PgRepack`, carrying
   `[RequiresPostgresExtension("pg_repack")]`. The extension is provisioned and the client program
   probed by WS-8 slice 9, which also owns its two run modes (in process from the maintainer duty, or
   an external job reading the same queue). RED: the resolution matrix gains the PgRepack column.
   Docs: the storage-maintenance page (when to prefer it over pg_squeeze).

### C2: autovacuum

6. **Framework tables, by migration.** New migration (next free number) setting reloptions:
   scale factor 0 with a fixed threshold (about 10k) for large update or delete tables
   (`wh_perspective_cursors`, `wh_lifecycle_completions`, `wh_stream_digests`,
   `wh_message_deduplication`); `autovacuum_vacuum_insert_threshold` and
   `autovacuum_vacuum_insert_scale_factor` on append-heavy `wh_event_store` (keeps the visibility map
   current for index-only scans); matching `toast.autovacuum_*` on TOAST-heavy `wh_event_body`.
   Update 088's comment about the event store, whose reason (dead tuples) does not cover the insert
   threshold. RED: a migration test reading `pg_class.reloptions` and the TOAST relation's
   reloptions per table; idempotence on re-run. Docs: storage-maintenance page.
   **Status (2026-10-23):** shipped in 222 (#1582). `wh_perspective_applied`, which stays unpartitioned
   (#1577), gets the same shape for deletes and inserts in migration 234 (#1666).
7. **Perspective tables, by attribute.** Extend `[PerspectiveTableStorage]` with
   `AutovacuumVacuumThreshold`, `AutovacuumVacuumScaleFactor`, `AutovacuumInsertThreshold` and
   matching TOAST properties. `TableStorageInfo.From` reads them; `ColumnStorageSql.ForTable` emits
   them through a generalized form of `SetToastTupleTarget` (`ColumnStorageSql.cs:168`, already
   "set only if not already set"). RED: `tests/Whizbang.Generators.Tests/ColumnStorageSqlTests.cs`
   for each property and for omission; an integration test that the reloptions land. Docs:
   `fundamentals/perspectives/physical-fields.md` (`#jsonb-storage`, where the attribute is
   documented).
   **Status (2026-10-21):** ships in #1584. A value out of PostgreSQL's range (a scale factor above
   100) fails the schema pass with the server's error; slice 8's range diagnostic covers these
   properties too.

### C3: fillfactor and event-store churn

8. **Fillfactor.** `FillFactor` on `[PerspectiveTableStorage]` (10 to 100, validated by the
   generator with a diagnostic), plus fillfactor 90 on `wh_perspective_cursors` in slice 6's
   migration. A changed fillfactor applies only to new pages, so the schema pass also records a
   rewrite request (`wh_request_table_rewrite`) for the table. RED: generator tests for the
   statement and the out-of-range diagnostic; integration test reading `reloptions` and the queued
   request. Docs: physical-fields page, with the HOT explanation above.
   **Status (2026-10-21):** ships in #1592. The fillfactor went into a new migration (223) rather than
   slice 6's 222, which had already shipped. The range diagnostic is WHIZ804 (analyzer
   `PerspectiveTableStorageAnalyzer`), covering `FillFactor`, `ToastTupleTarget` and the three scale
   factors; the generators leave an out-of-range value out. The rewrite is queued only when the value
   changes on a table that has pages. `wh_tables_needing_rewrite` still offers a requested table only
   once it crosses the bloat threshold, so a queued request is a hint, not a forced rewrite.
9. **Defect 7 investigation: what updates `wh_event_store`.** Code search found these writers:

   | Writer | Columns | When |
   |---|---|---|
   | `stamp_pending_commit_sequences` (`Migrations/205_StampWalksOnlyEligibleRows.sql:189-195`, called by `src/Whizbang.Data.Postgres/Notifications/PgCommitOrderStamperWorker.cs:527`) | `commit_sequence` | once per event, the only steady-state writer |
   | `reclassify_events_ephemeral` (`Migrations/093_OriginGeneration.sql:263,329`, called from `EFCoreWorkCoordinator.cs:475`) | `flags` | at startup, only rows not yet flagged |
   | One-time backfills in 047, 063, 074, 077, 078, 197 (line 67), 205 (line 51) | various | at migration time, each guarded by a `WHERE` that skips done rows |
   | `src/Whizbang.Data.Dapper.Postgres/DapperEventTypeRenameTool.cs:119,126` | `event_type`, `aggregate_type` | operator tool only |

   The stamp can never be HOT: `commit_sequence` appears in five partial indexes
   (`idx_event_store_commit_sequence`, `_unstamped`, `_unstamped_insert_order`,
   `_unstamped_insert_xid`, `_unstamped_stream_version`) and in the `INCLUDE` list of
   `idx_event_store_ephemeral_reap`. That explains the 4% HOT. It does **not** explain 12 updates per
   event; code alone gives one. Hypotheses to test, in order: stamp batches that roll back
   (statement timeout, cancel) and retry, whose rows still count in `n_tup_upd`; earlier stamper
   generations still running on older nodes during rolling upgrades; migration backfills replaying
   on a long-lived database. RED: an integration test that inserts N events, runs the stamper to
   completion and asserts `n_tup_upd` grows by exactly N (and `n_tup_hot_upd` by 0), then a
   variant that forces a stamp timeout and asserts the retry count. The test that fails names the
   cause.
10. **Stop the churn.** Depends on slice 9. If the excess is retries, bound them (smaller batches,
    no full-batch rollback). The one remaining update per event then moves out of the wide row,
    following the 162 precedent: a narrow `wh_event_commit_order (event_id, commit_sequence)` table
    plus an unstamped-work table holding `insert_xid`, `insert_order`, `stream_id`, `version`, so
    `wh_event_store` becomes insert-only and drops five partial indexes. RED: the slice 9 test asserts
    0 updates per event; read-path tests for every `commit_sequence` reader. Docs:
    `operations/infrastructure/migrations.md`. This is a large change and needs a design decision
    first (see open questions).

### C4: indexes

11. **BRIN candidates.** Measure `pg_stats.correlation` for `created_at` and `commit_sequence` on
    `wh_event_store`, `wh_log`, `wh_lifecycle_completions` and `wh_message_deduplication`, and add a
    BRIN index only where correlation is near 1 and a range filter exists. The existing B-tree
    `idx_event_store_ephemeral_reap` stays: the reap needs its order. RED: a plan-shape test per added
    index (`EXPLAIN` uses it for the range query).
12. **Index review, report only.** A diagnostic listing framework indexes with zero scans since the
    last stats reset, as a log and a gauge. Nothing is dropped automatically.
    **Status (2026-10-23):** done in #1669: `whizbang.table.unused_index_bytes` on the table-statistics meter
    and an Information log line when the set changes. Constraint indexes are left out; partition indexes roll
    up to their parent; capped by `MaxTrackedTables`.
13. **`REINDEX CONCURRENTLY` task.** For queue tables that churn to empty (`wh_outbox`, `wh_inbox`,
    `wh_inbox_state`, `wh_perspective_events`): when an index's size over its expected size exceeds
    `ReindexBloatThreshold`, rebuild it concurrently on its own connection (it cannot run in a
    transaction), one index per cycle, fleet-once through the same claim as
    `IndexStatisticsMaintenanceStep`. A leftover invalid index from an interrupted rebuild is dropped
    first. RED: threshold decision unit tests; integration test that a bloated index shrinks and
    that an invalid leftover is cleaned. Docs: storage-maintenance page and
    `fundamentals/workers/maintenance-steps.md`.

### Defect 9: idle-gate ceiling as a time

14. **`MaxDeferral` TimeSpan.** Today the forced pass comes when `deferrals > MaxConsecutiveDeferrals`
    (`HousekeepingCoordinator.cs:348-350`), so the ceiling is the count times each activity's own
    tick: about an hour for maintenance at `IntervalMinutes = 10`, something else for dead-letter
    recovery, which keeps its own counter (`:304`). Changing the tick silently moves the ceiling.
    Add `MaxDeferral` (default one hour), measured from the first deferral with the coordinator's
    `TimeProvider`; when set, it governs, and the count remains as a legacy fallback. RED, on
    `tests/Whizbang.Core.Tests/Workers/HousekeepingCoordinatorTests.cs` with a fake time provider:
    forced at the time regardless of tick; not forced one tick early; reset on End; count still
    honored when only it is configured. Docs: `operations/workers/housekeeping-arbitration.md`
    (lines 74 and 96 describe the count today) and the configuration reference.

## Engineering standards

- Strict red before green (`ai-docs/tdd-strict.md`); 100% of new lines and hand-written branches
  (`ai-docs/coverage-exclusions.md`).
- No reflection (`ai-docs/aot-requirements.md`); attribute values reach SQL through the generator.
- Migrations follow `src/Whizbang.Data.Postgres/Migrations/README.md`: idempotent, `__SCHEMA__`
  everywhere, header block, shared literals from `constants.txt`, `<docs>` and `<tests>` above every
  function, `pwsh scripts/Lint-MigrationSql.ps1`. Pre-1.0, fixes edit in place.
- Anything outside a transaction (`squeeze_table`, `REINDEX CONCURRENTLY`) follows
  `ai-docs/schema-initialization-connections.md`.
- Deterministic tests only: wait on signals, never on time (`ai-docs/flaky-tests.md`).
- `<docs>` and `<tests>` tags on public types (`ai-docs/documentation-maintenance.md`).

## Verification

- On the development environment, with pg_squeeze allowlisted and preloaded: the 53 queued tables
  drain across maintenance cycles with no ACCESS EXCLUSIVE wait longer than a second.
- Re-run the size and HOT queries before and after: `wh_event_store` updates per insert fall to 1
  (slice 9 fix) and then 0 (slice 10); the update-heavy perspective's HOT share rises after
  fillfactor plus rewrite.
- `whizbang.housekeeping.decisions` by verdict: forced passes arrive at `MaxDeferral` after the
  first deferral.

## Decisions (formerly open questions)

All decided on 2026-10-22 in #1671 (the owner accepted the recommendations).

1. **Auto and online rewrites.** `Auto` does not start online rewrites. Online rewrites need an
   explicit opt-in (`TableRewriteStrategy = PgSqueeze`); `Auto` detects which tables need a rewrite
   and reports them only.
2. **Event-store churn (slice 10).** `commit_sequence` stays on the event row, and one non-HOT update
   per event is accepted. #1519 measured one update per event, and a side table would add a join to
   every ordered read.
3. **Removed storage attributes.** The schema pass resets (`RESET`) an autovacuum or fillfactor
   setting when the attribute property that set it is removed, so the database matches the code.
4. **pg_squeeze privileges** on managed servers (superuser or REPLICATION) is an investigation, not a
   decision. It moves into C1.3 as a prerequisite to verify.
