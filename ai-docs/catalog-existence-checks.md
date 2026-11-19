# Catalog existence checks: name lookups and stale answers

**Read when** writing or reviewing code (C#, generated SQL, or a migration) that asks whether a table, index or
function exists: `to_regclass`, `to_regprocedure`, a `'x'::regclass` cast, or a `pg_class` / `pg_proc` read.

Found while fixing the schema bootstrap race (#2009) and audited across the library in #2038. The docs site carries
the rule at `contributors/data-engines/writing-migrations#catalog-existence-checks`.

## The mechanism

A name lookup is answered from the session's **catalog cache**, positive and negative entries alike. A session
processes other sessions' catalog invalidations only at these points:

1. when a transaction **starts**;
2. when a statement **opens a relation**. Measured on PostgreSQL 17: reading any table refreshes the cache, including a
   table already locked earlier in the transaction and catalog relations such as `pg_locks` and `pg_database`; a
   statement that opens no relation (`SELECT to_regclass(...)`, `SELECT 1`) does not;
3. when the parser **fails** to resolve a name (it accepts invalidations and retries). A `to_regprocedure` whose
   argument **type** does not resolve refreshes the same way, so a test that creates the table and the function
   together never sees the stale answer: create the argument's table first, then only the function mid-transaction.

Measured on PostgreSQL 17 with two sessions: inside one transaction, `to_regclass` kept answering "missing" for a table
another session created and "present" for one it dropped, and `to_regprocedure` kept answering "missing" for a function
created while its argument type already existed.

So a lookup returns a stale answer only when all of these hold:

- the **same name** was looked up earlier **in the same transaction** (that is what put the entry in the cache);
- **another session** committed a create, drop or rename of it after that first lookup;
- no statement between that commit and the second lookup opened a relation (an advisory lock, a sleep, or a client
  round trip does not count).

A read of `pg_class` / `pg_proc` / `pg_namespace` uses the statement's MVCC snapshot instead. Under read committed a
statement takes it fresh, so it sees the commit. Under repeatable read the snapshot is the transaction's and is "stale"
too, but consistently with every other read in that transaction.

`SchemaBootstrapPhaseTests.InsideATransaction_ToRegclassMissesATableAnotherSessionCommitted_WhereAPgClassReadSeesItAsync`
is the control: it fails if a server release ever stops showing the stale answer.

## The rule

- A method that takes **its caller's connection** (which may carry an open transaction) and turns an existence answer
  into a **decision** reads the catalog table: `CatalogPresence.RelationExistsAsync` for a relation, a `pg_proc` join
  for a function (see `SchemaBootstrapPhase.CanElectAsync`), unless a statement of the same call that opens a relation
  always runs first (R7 below), and a test locks that ordering.
- A lookup that is the **first of its name in a transaction of its own** is fresh and may stay a name lookup. That covers
  an autocommit statement, a `DO` block sent as one command, a function called by one statement, and a migration step.
- A lookup whose stale answer can only fail **toward the safe side** (run the idempotent statement, refuse and retry
  next cycle, raise an error) may stay a name lookup.
- Every new site states which of these it is, in a comment, when it is not obvious.

## Why the migration paths are safe

- **EF Core runner** (`SchemaStepRunner`): each step begins its own transaction and takes the schema lock with
  `pg_try_advisory_xact_lock`, which never waits. A lost lock rolls the transaction back and the wait happens outside
  any transaction, so no step's lookups straddle a wait. The objects a step asks about are created and dropped only by
  DDL under the same lock.
- **Dapper runner**: the schema lock is held by `SchemaInitializationLock` on a connection of its own; each migration
  runs in a transaction begun after the lock is held.
- **Bootstrap**: fixed in #2009 (the waiter's recheck under a blocking lock is the one place a lookup did straddle a wait).

## The audit (#2038), per site

Verdicts: **Switched** (now a catalog read, locked by a test) or **Kept**, with the reason code:

- **R1 fresh transaction**: the lookup is the first of its name in a transaction of its own (autocommit statement,
  single-command `DO` block, connection the method opens itself).
- **R2 migration step**: runs inside a schema-lock step as described above.
- **R3 safe direction**: a stale answer runs the idempotent statement as written, refuses and retries next cycle, or
  raises; it never skips work silently.
- **R4 never dropped**: a `::regclass` cast of a framework table the framework never drops or renames, so a stale
  positive cannot happen and a stale negative raises.
- **R5 report only**: the answer feeds a log line, a statistic or an operator report, not a decision.
- **R6 no new race**: a table created by another session mid-transaction can only hold rows that session commits
  concurrently, which the operation would miss under read committed anyway; the cache adds no new class of miss.
- **R7 refreshed first**: an earlier statement of the same call opens a relation, which refreshes the cache before the
  lookup.

### C# (runtime)

| Site | Verdict | Reason |
|---|---|---|
| `MigratorWatch.IsMigratingElsewhereAsync` | **Switched** to a `pg_proc` read (#2054) | Kept as a name lookup in #2038 because its session-lock probe read `pg_locks` first; #2044 removed that probe (no duty is a lock), which left the lookup stale inside a caller's transaction. It now reads `pg_proc` for both role functions, and opening `pg_proc` also refreshes the cache for the call that follows. Test: `MigratorWatchTests.InsideATransactionThatBeganBeforeTheRoleObjectsWereCommitted_SeesTheMigratorAsync` (fails on a `to_regprocedure` check) |
| `ManagedSchemaReconciler.TablesMissingDeclaredObjectsAsync` (ledger) | **Switched** to `CatalogPresence` | Public, caller's connection, gates the schema pass fast path. Test: `ManagedSchemaReconcilerTests.InsideATransactionThatBeganBeforeTheLedgerWasCommitted_NamesTheTableForRebuildingAsync` |
| `SchemaBootstrapPhase._recordedClosureAsync`, `CanElectAsync` | Switched in #2009 | Now through `CatalogPresence` / `pg_proc` |
| `ManagedSchemaReconciler` plan transaction (ledger) | Kept | R1: first lookup after a non-blocking try-lock in a fresh transaction |
| `ManagedSchemaCatalog.ReadEquivalentsAsync` | Kept | R1 in the plan; R3 on the missing-objects path (a stale miss names a table for an extra pass) and R4 (never dropped) |
| `ManagedSchemaFleet.ReportAndReadAsync` | Kept | R1 (inside the plan transaction, first lookup of the registry) |
| `ManagedSchemaLedger.StatusAsync` | Kept | R5 (CLI status and plan report) |
| `PostgresStartupAssessment._probeAsync` | Kept | R1 (connection the assessor opens, autocommit) |
| `FleetVersions.OtherLiveVersionsAsync` | Kept | R1 (fresh connection) and R5 (a mixed-fleet warning) |
| `IndexStatistics.AnalyzeAsync` | Kept | R1 (documented "not in a transaction") and R5 (statistics) |
| `PgSqueezePrerequisites.ReadAsync`, `EFCoreWorkCoordinator` squeeze / size / ownership reads | Kept | R3 (a stale miss refuses the rewrite, retried next cycle), R4, R5 |
| `EFCoreWorkCoordinator` bloat survey (`pg_relation_size(to_regclass)`) | Kept | R5 |
| `QueueIndexSql.SURVEY`, `LOOKUP` | Kept | R1 (single statement) and R3 (a stale miss refuses a rebuild) |
| `PgRoleElector._markAsync` (`to_regprocedure`) | Kept | The mark only works committed, so it is made on an autocommit connection by contract (R1); a stale miss returns no mark and says so, for that statement only |
| `PgSharedNotifyConnection.ClaimAliveLockAsync` | Kept | R1 (LISTEN connection, autocommit) and R3 (retried on the next keepalive) |
| `DeclaredKeySpellingPass` (table and journal) | Kept | R1 (connection of its own, no transaction) |
| `StoredFormMigrationJournal.ReadAsync` | Kept | R1 and R5 |
| `StoredFormIndexRebuild` state check | Kept | R1 (runs outside any transaction by design) |
| `StoredFormSettlement.IsSettledAsync` | Kept | R1 (fast-path check) and R6 (an absent table has nothing in the old form) |
| `CatalogCheckedDdl` guards (#1636) | Kept | R2 and R3: any stale answer runs the idempotent statement as written |

### Generated SQL (C# that emits SQL)

| Site | Verdict | Reason |
|---|---|---|
| `StoredFormMigrationSql` declare and gate blocks | Kept | R1/R2: single-command `DO` blocks; a missing table waits for the table |
| `StoredFormStep` column, index and work-table guards | Kept | R1/R2 inside the stored-form `DO` block; R4 for the `::regclass` casts |
| `CanonicalTemporalRewrite` table and ledger guard | Kept | R1/R2 (one `DO` block per table) |
| `EFCoreServiceRegistrationGenerator` perspective forms guard, constraint guards | Kept | R2 (perspective DDL step); R4 for `conrelid = 'x'::regclass` |
| `PerspectiveSchemaGenerator` constraint guard | Kept | R2, R4 |
| `ColumnStorageSql`, `PhysicalColumnSql` attribute and constraint guards | Kept | R2, R4 |
| `DbContextSchemaExtensionTemplate` tracking `owner` column check | Kept | Same command created the table (own-session change, always visible) |

### SQL functions (latest definition of each)

No function needed a new definition, so no migration changed. Only one function takes a blocking lock mid-body
(`wh_refresh_stale_queue_statistics`), and its casts are of queue tables that are never dropped (R4).

| Function (file of latest definition) | Verdict | Reason |
|---|---|---|
| `drop_all_overloads` (000) | Kept | `::regprocedure` of an oid just read; R4 |
| `wh_backfill_normalized_message_type_batch` (008), `wh_backfill_inbox_source_columns_batch` (046), `wh_normalize_aggregate_type_batch` (063), `wh_backfill_event_bodies_batch` (077), `wh_seed_stream_digests_batch` (087), `wh_seed_inbox_state_batch` (162), `wh_normalize_snapshot_type_names_batch` (191), `wh_remove_rewind_lifecycle_events_batch` (210) | Kept | R2: batched backfill regions run by the migration runner, one command per batch; the todo table is created and dropped by the same function |
| `wh_tables_needing_index_statistics` (178), `wh_measure_table_bloat_approx` (254) | Kept | R1, R5 |
| `wh_drop_document_index` (179), `wh_fill_physical_columns`, `_wh_drop_physical_move_sync`, `wh_sync_physical_moves`, `wh_arm_physical_column`, `wh_demote_physical_columns`, `wh_record_registered_physical_columns`, `wh_settle_physical_moves` (182) | Kept | R1/R2: one call per statement; each resolves its table once and then locks it by the DDL or DML it runs |
| `_role_vote_lock_key` (184), `ring_doorbells` (206) | Kept | R4 (an advisory key from a framework table's oid) |
| `wh_find_qualified_type_names` (191) | Kept | R4 |
| `wh_ensure_index` (201) | Kept | R2 (schema pass) |
| `wh_refresh_stale_queue_statistics` (207) | Kept | R4; the lock it waits on is per table, and nothing is looked up again by name after it |
| `wh_partition_convert`, `wh_partition_sweep_relation` (233), `wh_partition_maintain` (239), `_wh_partition_rebound`, `wh_partition_partman` (246) | Kept | R1: called by one statement and takes only try-locks and `NOWAIT` locks, so no lookup straddles a wait; R4 for `wh_settings` |
| `wh_follow_perspective_table_renames` (243) | Kept | R2 (schema step under the lock) |
| `wh_purge_streams` (262), `_wh_dispose_archived_stream_rows`, `_wh_restore_archived_stream_rows` (299) | Kept | R6; each table found is then locked by its `DELETE` or `INSERT` |
| `reap_unbounded_ledgers` (286) | Kept | R1, R4 |

### SQL at the top level of migrations

All Kept, R2 (run once by the migration runner under the schema lock, at the start of the step's transaction), and R4
for the `::regclass` casts: 031, 054, 057, 072, 077, 105, 123, 138, 145, 150, 158, 159, 162, 201, 223, 231, 271, 275,
277, 290. Migration 105's `to_regclass('public.wh_settings')` and `('public.wh_dead_letters')` are R2 as well.

## Writing a new check

1. Does the method take a connection it did not open? If yes and the answer drives behavior, read `pg_class` / `pg_proc`.
2. Otherwise, is this lookup the first of its name in its transaction? If yes, a name lookup is fine.
3. Otherwise, does a stale answer only ever do the safe thing? Say so in a comment.
4. A test of a switched site opens a transaction, looks once, commits the change on a second connection, and looks
   again: it must fail with the name lookup and pass with the catalog read.
