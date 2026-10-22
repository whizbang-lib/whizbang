# Ledger retention: companion cleanup, bounded ledgers, partitioned work tables

Workstream WS-5 of the storage-reclamation program. Companion plans: `storage-stream-end-of-life.md`
(WS-3) and `storage-extension-capabilities.md` (WS-8, which gates the optional pg_partman strategy).
Release: 1.0.

## Context

The evidence comes from read-only measurement of a production deployment's development environment
(October 2026). Two framework ledgers dominated what was not the event store: the deduplication
table (about 2.4M rows, 263 MB in one service) and lifecycle completions (about 583k rows, 278 MB).
Both have retention, but it is a row-by-row `DELETE` that leaves dead tuples for vacuum and index
bloat behind it. Other ledgers have no retention at all, and most retention settings can only be
changed with a hand-written `UPDATE wh_settings` in every service database.

### Defect 1: row sweeps orphan cursors and snapshots

The perspective row TTL sweep and the row-cap sweep delete the perspective row and journal it, and
nothing else:

| Sweep | Latest definition |
|---|---|
| `reap_enrolled_perspective_rows` (TTL and max age) | `src/Whizbang.Data.Postgres/Migrations/112_StreamGroupCascadeAndApplyPathFold.sql:41` |
| `reap_perspective_row_caps` (row cap) | `113_CapSweepSafetyAndFoldWatermarks.sql:238` |
| `cascade_delete_perspective_rows` (stream-group cascade) | `112_StreamGroupCascadeAndApplyPathFold.sql:197` |

Victims go to `wh_row_eviction_journal` (`112_StreamGroupCascadeAndApplyPathFold.sql:20`), drained by
`IWorkCoordinator.DrainRowEvictionJournalAsync` (`src/Whizbang.Core/Messaging/IWorkCoordinator.cs:1319`,
called at `src/Whizbang.Core/Workers/MaintenanceWorker.cs:618`). No path deletes the matching
`wh_perspective_cursors` or `wh_perspective_snapshots` rows; the only deletes of those tables outside
snapshot pruning are in `wh_purge_streams` (`180_StreamPurge.sql:178,187`). Two consequences:
orphaned rows accumulate forever, and a later rewind can restore a stale snapshot and bring evicted
state back.

### Which ledgers have a delete path today

| Table | Delete path | Bounded? |
|---|---|---|
| `wh_message_deduplication` | `perform_maintenance`, `209_SnapshotsCarryTheirStreamVersion.sql:293`, by `dedup_retention_days` | yes, row-by-row |
| `wh_lifecycle_completions` | `MaintenanceWorker.cs:285`, `LifecycleCompletionRetentionDays` (default 7) | yes, row-by-row |
| `wh_perspective_applied` | inline prune on completion, older than 1 hour, 1000 per call (`177_PerspectiveAppliedLedger.sql:120-128`) | yes |
| `wh_signals` | `PgDurableSignalRetentionWorker` (`src/Whizbang.Data.Postgres/Notifications/PgDurableSignalRetentionWorker.cs:102`), fixed 7 days (`:55`), unbatched, unqualified table names | yes, not configurable |
| `wh_offload_claims` | passive expiry sweep, `RemoveOffloadClaimsAsync` (`src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:2756`) | yes |
| `wh_integrity_ledger` | heal delete (`090_IntegrityLedger.sql:207`); row-cap `wh_integrity_ledger_trim` (`090_IntegrityLedger.sql:218`) | **the trim has no caller anywhere** |
| `wh_stream_digests` | close and purge only (`093_OriginGeneration.sql`, `180_StreamPurge.sql`) | grows with stream count; WS-3 end-of-life bounds it |
| `wh_event_archive` | `wh_purge_streams` only (`180_StreamPurge.sql:310`) | intentional; governed by WS-3 archive policy |
| `wh_advisory_ledger` | none, by design (`168_AdvisoryLedger.sql:30-33`: bounded by the number of distinct findings) | yes; no change |
| `wh_log` | **none** (`028_EventStorageErrorTracking.sql:22`) | no |
| `wh_schedule_runs` | **none** (`066_TemporalSchedules.sql:61`) | no |
| `wh_stream_purge_audit` | **none** (`180_StreamPurge.sql:56`) | no; an audit, so opt-in only |

### Defect 5: retention settings have no configuration path

Retention knobs live in `wh_settings`, one row per database. Only `debug_mode` is published from an
option: `DebugRetentionBridge` (`src/Whizbang.Core/Workers/DebugRetentionBridge.cs:31`) and
`IWorkCoordinator.SyncDebugRetentionSettingAsync` (`IWorkCoordinator.cs:244`, implemented at
`EFCoreWorkCoordinator.cs:252`), called each cycle at `MaintenanceWorker.cs:206`. The Dapper
coordinator (`src/Whizbang.Data.Dapper.Postgres/DapperWorkCoordinator.cs`) does not implement it and
inherits the no-op default, so on Dapper even `debug_mode` is not synced. Every other key changes
only through SQL.

## Goal

Every framework ledger has a bounded, configurable lifetime; deleting a perspective row removes
everything that describes it; the highest-churn time-ordered ledgers drop whole partitions instead of
deleting rows; and every retention knob is settable from configuration like any other option.

## Slices

Each slice is one TDD increment (RED tests first, docs in the same PR). Docs paths are under
`whizbang-lib.github.io/src/assets/docs/`; "new" means absent from `develop` today.

### S1. Companion cleanup for evicted perspective rows (defect 1)

- **Change.** The journal drain at `MaintenanceWorker.cs:618` already hands back each evicted
  `(table, row_id)`. For each, resolve the perspective through `wh_perspective_registry` and delete
  `wh_perspective_cursors` and `wh_perspective_snapshots` rows for `(stream_id = row_id,
  perspective_name)` in the same transaction as the cascade. Do it in a SQL helper so both engines
  share it, and give the Dapper coordinator the drain if it lacks one.
- **RED tests** (`tests/Whizbang.Data.EFCore.Postgres.Tests/EnrolledRowReaperSqlTests.cs`,
  `PerspectiveRowCapSqlTests.cs`, plus Dapper twins): after a TTL reap, a cap reap and a group
  cascade, no cursor or snapshot remains for the evicted row; a rewind after eviction does not
  resurrect the row; rows of other perspectives on the same stream are untouched.
- **Docs.** `proposals/perspective-row-retention.md` is still a proposal; publish it as
  `v1.0.0/fundamentals/perspectives/row-retention.md` (new) with a "what an eviction removes" section.
  Update `v1.0.0/fundamentals/perspectives/snapshots.md`.

### S2. Retention settings get a configuration path (defect 5)

- **Change.** Generalize the debug bridge: a `RetentionSettingsBridge` with a static table of
  `(setting key, value type, option accessor)` and one `IWorkCoordinator.SyncRetentionSettingsAsync`
  that upserts all of them in one statement (`unnest`), called where the debug sync is called today.
  A property left unset (`null`) is not written, so a value an operator set in SQL stands; a set
  property is pushed every cycle. Implement it on both coordinators, which also fixes the Dapper
  `debug_mode` gap.
- **Proposed options.** A nested `Retention` object on `MaintenanceWorkerOptions`
  (`MaintenanceWorker.cs:1063`), so the existing `Whizbang:Workers:Maintenance` binding
  (`src/Whizbang.Core/Workers/WorkerPipelineExtensions.cs:783-792`) reaches it with no new binder
  call; ephemeral knobs go on `EphemeralOptions` (`src/Whizbang.Core/Configuration/EphemeralOptions.cs`,
  bound from `Whizbang:Ephemeral` at `WorkerPipelineExtensions.cs:635`).

| `wh_settings` key | SQL default | Option property | Env-var form |
|---|---|---|---|
| `dedup_retention_days` | 30 | `Retention.DeduplicationDays` | `Whizbang__Workers__Maintenance__Retention__DeduplicationDays` |
| `stuck_inbox_retention_days` | 7 | `Retention.StuckInboxDays` | `..._Retention__StuckInboxDays` |
| `dead_letter_retention_days` | 7 | `Retention.RecoveredDeadLetterDays` | `..._Retention__RecoveredDeadLetterDays` |
| `abandoned_stream_hours` | 1 | `Retention.AbandonedStreamHours` | `..._Retention__AbandonedStreamHours` |
| `instance_eviction_retention_hours` | 24 | `Retention.InstanceEvictionTombstoneHours` | `..._Retention__InstanceEvictionTombstoneHours` |
| `orphan_perspective_grace_hours` | 1 | `Retention.OrphanPerspectiveEventGraceHours` | `..._Retention__OrphanPerspectiveEventGraceHours` |
| `collective_ordering_head_retention_days` | 7 | `Retention.CollectiveOrderingHeadDays` | `..._Retention__CollectiveOrderingHeadDays` |
| `ephemeral_rewind_grace_seconds` | 300 | `EphemeralOptions.RewindGraceSeconds` | `Whizbang__Ephemeral__RewindGraceSeconds` |
| `ephemeral_deep_maintenance_enabled` | false | `EphemeralOptions.PointerPrune.Enabled` | `Whizbang__Ephemeral__PointerPrune__Enabled` |
| `ephemeral_pointer_retention_days` | 90 | `EphemeralOptions.PointerPrune.RetentionDays` | `Whizbang__Ephemeral__PointerPrune__RetentionDays` |
| `ephemeral_pointer_prune_interval_days` | 30 | `EphemeralOptions.PointerPrune.IntervalDays` | `Whizbang__Ephemeral__PointerPrune__IntervalDays` |

  `..._` abbreviates `Whizbang__Workers__Maintenance_`. The three `table_rewrite_*` keys
  (`089_TableRewriteRequests.sql:26-31`) belong to WS-4, which owns the rewriter seam. Keys added by
  S3 and S4 join this table.
- **RED tests** (`tests/Whizbang.Core.Tests/Workers/DebugRetentionBridgeTests.cs` grows into
  `RetentionSettingsBridgeTests.cs`): each property maps to its key and literal; unset properties are
  not written; a configured value reaches `wh_settings` within one cycle on both engines; a failed
  sync never fails the cycle; options validation rejects negative values.
- **Docs.** `v1.0.0/operations/configuration/configuration-reference.md` (Maintenance section, plus a
  "Database-side settings" table naming the key each option publishes);
  `v1.0.0/fundamentals/workers/maintenance-steps.md`.

### S3. Retention for unbounded ledgers

**Status (2026-10-21):** the durable-signal window (`WhizbangNotificationOptions.SignalRetention`) and batched
signal sweep ship in #1574; the four SQL ledgers ship in #1572.


- **Change.** Batched deletes in `perform_maintenance` (edit the latest definition in place;
  `debug_mode`-guarded) for `wh_log` (`log_retention_days`, default 14) and `wh_schedule_runs`
  (`schedule_run_retention_days`, default 30, always keeping the newest run per schedule).
  `wh_stream_purge_audit` gets `stream_purge_audit_retention_days`, default 0 meaning keep forever.
  Call `wh_integrity_ledger_trim` each cycle with `integrity_ledger_max_entries` (default 100000).
  `PgDurableSignalRetentionWorker` takes its window from options, deletes in batches and
  schema-qualifies `wh_signals` and `wh_signal_cursors`.
- **RED tests.** Rows older than the window go and younger rows stay, per table; the newest run per
  schedule survives any window; 0 keeps the audit; the trim keeps exactly the configured count,
  newest first; the signal sweep honors a configured window and a non-default schema.
- **Config.** `Retention.LogDays`, `Retention.ScheduleRunDays`, `Retention.StreamPurgeAuditDays`,
  `Retention.IntegrityLedgerMaxEntries` under `Whizbang:Workers:Maintenance`;
  `WhizbangNotificationOptions.SignalRetention`, bound from `Whizbang:Database`
  (`PostgresNotificationsServiceCollectionExtensions.cs:58`), env `Whizbang__Database__SignalRetention`,
  default `7.00:00:00`.
- **Docs.** `v1.0.0/fundamentals/workers/maintenance-steps.md`;
  `v1.0.0/operations/infrastructure/purging-streams.md` (audit retention).

### S4. B4 part 1: time-partitioned work ledgers, managed by the maintenance worker

**Status (2026-10-21):** S4a ships in #1611: the built-in partition manager (`wh_partition_maintain`,
`wh_partition_convert`, migration 226), the maintenance step on both drivers under the maintainer duty, the internal
`PartitionedTable` registration, `Partitioning:Enabled` / `:CreateAhead`, and `wh_log` converted weekly. Remaining:
`wh_signals` and `wh_lifecycle_completions` (follow-ups, registered the same way), and the pg_partman strategy
(`Partitioning:Strategy`, WS-8 slice 8). Deduplication and `wh_perspective_applied` stay unpartitioned (#1577).

**Also needed by WS-2 S9/S9b** (`plans/storage-event-slimming.md`): the staged full-metadata table and the trace
table are daily partitions created ahead and dropped behind by this partition manager, so S4 lands first.

- **Change.** A built-in partition manager registered as an `IMaintenanceStep`
  (`src/Whizbang.Core/Workers/IMaintenanceStep.cs:24`) and backed by one SQL function
  (`wh_partition_maintain(table, interval, ahead, keep)`): create partitions ahead, detach and drop
  partitions wholly past the retention window. Each table is converted once by a data migration gated
  on a `wh_settings` version marker (README rule 6): create the partitioned parent, attach the legacy
  table as the oldest partition when its constraints allow, otherwise copy in batches. Candidates and
  partition keys, honoring "every unique key includes the partition key":

| Table | Today's key | Partition key | Interval |
|---|---|---|---|
| `wh_message_deduplication` | `message_id` | `message_id` range (time-ordered ids) | daily |
| `wh_lifecycle_completions` | `event_id` | `event_id` range | daily |
| `wh_signals` | `id` (bigserial) | `created_at`, key becomes `(id, created_at)` | daily |
| `wh_log` | `log_id` (bigserial) | `logged_at`, key becomes `(log_id, logged_at)` | weekly |
| `wh_perspective_applied` | `(event_id, perspective_name)` | `event_id` range | hourly or daily |

  The per-row retention deletes from S2 and S3 stay as the fallback for an unconverted table. DDL
  runs on the connection rules in `ai-docs/schema-initialization-connections.md` (detach runs outside
  the runner transaction, never while the table is mid-conversion). The per-tenant audit ledger from
  WS-2 adopts the same manager when it ships.
- **Optional pg_partman strategy.** `PartitionManagementStrategy { Auto, BuiltIn, PgPartman }`, with
  `PgPartman` carrying `[RequiresPostgresExtension("pg_partman", Feature = "Partition management")]`
  from WS-8. Explicitly choosing it with the extension missing fails at startup; `Auto` uses
  pg_partman when the capability probe reports it usable, otherwise the built-in manager.
  WS-8 slice 8 provisions the extension and runs `partman.create_parent` on conversion and
  `partman.run_maintenance` from the maintenance worker each cycle; its verdict line names this
  setting.
- **RED tests.** Partitions exist `ahead` intervals forward after a cycle; a partition entirely
  past the window is dropped and one straddling it is kept; a duplicate message id within the window
  is still rejected after conversion; conversion is resumable; the drop takes no lock that blocks
  inserts into the current partition; `PgPartman` with the extension missing throws naming the
  setting.
- **Config.** `Whizbang:Workers:Maintenance:Partitioning:Strategy` (default `Auto`),
  `:Enabled` (default `false` until the conversion has soaked), `:CreateAhead` (default `3`);
  env form `Whizbang__Workers__Maintenance__Partitioning__Strategy`.
- **Docs.** New `v1.0.0/operations/infrastructure/partitioned-ledgers.md`;
  `v1.0.0/operations/infrastructure/migrations.md` (conversion markers).

## Engineering standards

Strict red-before-green TDD (`ai-docs/tdd-strict.md`); 100% line and branch coverage on hand-written
code plus the per-PR new-code gate; AOT-safe, no reflection, options bound through the binder source
generator (`ai-docs/aot-requirements.md`); every public member carries `<docs>` and `<tests>` tags,
and so does every new SQL function (`ai-docs/code-standards.md`); SQL follows
`src/Whizbang.Data.Postgres/Migrations/README.md` (idempotent, `__SCHEMA__` everywhere including
function bodies, whole-function `CREATE OR REPLACE`, data migrations gated on a version marker,
`debug_mode` guard on destructive work, header block and `COMMENT ON`, constants in `constants.txt`).
Every SQL test runs on both engines.

## Verification

- Before and after each slice, re-run the read-only table-size, dead-tuple and HOT queries used for
  the original evidence against a test environment; partitioned ledgers should show near-zero dead
  tuples and flat index size across a retention boundary.
- `whizbang.table.estimated_bytes` (meter `Whizbang.TableStatistics`) for each ledger stays flat
  under steady load once retention is on (the meter's table coverage is widened by WS-6).
- A perspective with `[RowTtl]` run for several windows leaves cursor and snapshot counts equal to
  its live row count.

## Open questions

1. Partitioning deduplication by `message_id` range assumes time-ordered ids. Messages that arrive
   with foreign, random ids would land in one partition. Is a default partition plus row deletes
   acceptable for those, or does deduplication stay unpartitioned?
2. `wh_perspective_applied` is pruned after one hour; is hourly partitioning worth its DDL churn, or
   is the inline prune already enough?
3. Should partition DDL and the retention sync run only under the maintainer duty? Open issue
   #1377 asks the same of the maintenance sweep; this plan assumes partition DDL is duty-gated
   either way, since two instances creating the same partition must not race.
4. Do unset retention options really leave the database value alone, or should a configured
   deployment be authoritative and write defaults too (as `debug_mode` does in both directions)?
