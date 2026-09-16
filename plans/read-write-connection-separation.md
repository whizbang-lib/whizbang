# Read/write connection separation: does Whizbang ever read from a read-only endpoint?

Status: **investigation complete, no code changed.** Every stage below is `Not started`.

## The question

A consumer deployment can define four `ConnectionStrings` keys per service:

| Key | Host role |
|---|---|
| `<service>-db` | pooled, through a transaction pooler |
| `<service>-db-direct` | direct to PostgreSQL, one per pod by convention |
| `<service>-db-init` | schema initialization, usually higher privilege |
| `<service>-db-readonly` | a reader endpoint or a replica |

Three of those are framework conventions. The fourth is not. The question this document answers is
whether Whizbang consults `<service>-db-readonly` for any of its reads, and if not, what it would
take to do so without breaking anything.

## The method

Static reading only; nothing was built and no test was run. Three passes:

1. A repo-wide search for any read-only connection concept: the key suffix, a reader endpoint, an
   Npgsql `Target Session Attributes`, a SQL Server `ApplicationIntent`, a second data source, a
   read/write connection factory.
2. The connection-resolution seams, read end to end: what builds a data source, what keys it reads,
   and which components receive it.
3. An inventory of the read paths, each classified by the connection it uses today, whether it runs
   inside a transaction that also writes, whether it needs read-your-writes, and whether a lagging
   answer would be loud or silent.

---

## The answer

**No. Whizbang has no read-only connection concept at all, and nothing in the framework reads a
`-readonly` key.** A consumer that defines one has created a configuration entry the framework never
looks at.

Three pieces of evidence, in increasing strength.

### 1. The key does not appear anywhere

A repo-wide search for `-readonly` returns three classes of hit and no fourth: the C# `readonly`
keyword described in prose, the repo's own `public const`/`static readonly` style pragma, and the
CI workflow's handling of GitHub merge-queue branch names. There is no `ReadOnlyConnection`, no
`ReadReplica`, no `Target Session Attributes`, no `ApplicationIntent`, no reader-endpoint type.

The documented surface agrees: the configuration reference lists exactly three connection-string
conventions, and `-readonly` is not among them
(`whizbang-lib.github.io/src/assets/docs/v1.0.0/operations/configuration/configuration-reference.md:132-135`).

### 2. The only key-suffix logic in the framework covers `-direct` and `-init`

| Seam | Keys it reads | Evidence |
|---|---|---|
| Notification / stamper listener | `{key}-direct`, then `{key}` | `src/Whizbang.Core/Notifications/NotificationConnectionStringResolver.cs:80-88` |
| Pinned worker pool | `{service}-db-direct` by convention, explicitly configured | `src/Whizbang.Core/Workers/WhizbangPinnedPoolOptions.cs:45-62`; `src/Whizbang.Data.Postgres/PostgresPinnedPoolServiceCollectionExtensions.cs:80` |
| Schema initialization | `{dbContextKey}-init` | `src/Whizbang.Data.EFCore.Postgres.Generators/EFCoreServiceRegistrationGenerator.cs:2063-2075` |
| DbContext / everything else | `{dbContextKey}` only | `src/Whizbang.Data.EFCore.Postgres.Generators/EFCoreServiceRegistrationGenerator.cs:1988-1989` |

`WhizbangNamingConvention.DeriveConnectionStringName` produces the base key and nothing else
(`src/Whizbang.Core/Naming/WhizbangNamingConvention.cs:48-58`): strip a trailing `DbContext`,
lowercase, append `-db`. There is no sibling-suffix helper.

### 3. There is exactly one runtime data source, and it is built from the pooled key

This is the decisive fact. The generated turnkey registration removes any other `NpgsqlDataSource`
and registers a single one from a single key:

```csharp
services.RemoveAll<Npgsql.NpgsqlDataSource>();
services.AddSingleton<Npgsql.NpgsqlDataSource>(sp => {
  var connectionString = config.GetConnectionString(connectionStringKey)
      ?? throw new InvalidOperationException(...);
  ...
});
```

(emitted by `src/Whizbang.Data.EFCore.Postgres.Generators/EFCoreServiceRegistrationGenerator.cs:1980-1989`
for the turnkey path, and `:1837-1848` for the public `Add{DbContext}` method, which is the same
decision twice)

Everything downstream shares it:

- the `DbContext`, via `UseNpgsql(dataSource, ...)`
  (`src/Whizbang.Data.EFCore.Postgres.Generators/EFCoreServiceRegistrationGenerator.cs:398-411`);
- `IDbContextFactory<T>`, which creates a scope and resolves that same context
  (`src/Whizbang.Data.EFCore.Postgres/ScopedDbContextFactory.cs:52-54`);
- every component that takes a data source directly rather than a context: the perspective snapshot
  store, the table-statistics provider, the message-type registry populator, the notify-debounce
  stats provider, and the event-store health probe
  (`src/Whizbang.Data.EFCore.Postgres/PostgresDriverExtensions.cs:188,205,218,226,254`).

The `-init` key never escapes initialization. It is read inside the initialization callback, used to
build a data source declared `await using`, and disposed when the callback returns
(`src/Whizbang.Data.EFCore.Postgres.Generators/EFCoreServiceRegistrationGenerator.cs:2069,2076,2083,2092`). Its
only other reader is `SchemaBoundaryConnections.Resolve`
(`src/Whizbang.Data.EFCore.Postgres/SchemaBoundaryConnections.cs:60-63`), whose two call sites are
both inside the schema pass: the commit-boundary segment applier and the maintenance `VACUUM`
(`src/Whizbang.Data.EFCore.Postgres.Generators/Templates/DbContextSchemaExtensionTemplate.cs:79,1923`).
**No runtime read path opens the `-init` connection.** That closes the one defect shape worth
checking for.

`IDbConnectionFactory` exposes a single `CreateConnectionAsync` and no read/write split
(`src/Whizbang.Core/Data/IDbConnectionFactory.cs:12-21`), despite the lens documentation citing it as
a code reference.

So the framework has three connection roles, not four: **pooled** (everything), **direct** (the
single LISTEN connection and, when enabled, the pinned worker pool), and **init** (schema only).

---

## A decision already exists, and it says primary

This was decided and written down before this audit. It should be cited, not re-litigated.

> **Important**: point **Whizbang's own connection string at the primary** - the outbox/inbox
> pipeline and perspective materialization are write-heavy and depend on read-your-writes
> consistency. Use replicas only for application-level read paths that tolerate replication lag.

(`whizbang-lib.github.io/src/assets/docs/v1.0.0/operations/deployment/scaling.md:256`)

The same page's read-replica example is explicitly marked as an application-level pattern and not a
framework API: its own `unverified` attribute says it is an application-level read/write connection
factory and not a Whizbang API (line 192). A second decision record declines a related idea for the
same reason: a materialized view for "is there work?" was rejected because it "adds replication lag,
breaks 'claim and lock' atomicity"
(`whizbang-lib.github.io/src/assets/docs/drafts/decisions/work-pump-decomposition/sql-function-decomposition.md:59`).

Whizbang's own answer to read scaling is therefore already a different mechanism: perspectives are
the read models, and the lens path over them is the read side. The documented position is that a
consumer may put *its own* queries on a replica, and that the framework's connection stays on the
primary.

So the `-readonly` key in a consumer's configuration is not a gap the framework failed to close. It
is a key that belongs to the consumer's own read paths, and the framework correctly ignores it. What
follows is sized against that, not against a presumption that the framework should adopt it.

---

## Inventory of read paths

`Connection today` values: **pooled** = the single `NpgsqlDataSource` from `ConnectionStrings:{key}`;
**direct** = the shared LISTEN connection or the pinned pool; **init** = the schema-initialization
connection; **context** = the `DbContext`'s own connection, which is the pooled data source.

`Txn` = the read runs inside a transaction that also writes. `RYW` = needs read-your-writes.
`Verdict`: **P** must be the primary, **P!** must be the primary *and* a replica would be silently
wrong, **R** replica-safe, **R?** replica-safe but the payoff is small or the product must agree.

### Lens and query read side

| Path | Connection today | Txn | RYW | Verdict | Evidence |
|---|---|---|---|---|---|
| `ILensQuery<T>` / `IScopedLensAccess<T>.Query`, `GetByIdAsync` | context, via a fresh scope from `ScopedDbContextFactory` | no | no | **R?** | `src/Whizbang.Core/Lenses/IScopedLensAccess.cs:18,27`; `src/Whizbang.Data.EFCore.Postgres/EFCorePostgresLensQuery.cs:161,185`; `src/Whizbang.Data.EFCore.Postgres/ScopedDbContextFactory.cs:52-57` |
| Multi-model lens `Query<T>()` | context, fresh scope | no | no | **R?** | `src/Whizbang.Data.EFCore.Postgres/MultiModelScopedAccess.cs:23` |
| Filterable lens (`IScopedLensFactory` path) | context, fresh scope | no | no | **R?** | `src/Whizbang.Data.EFCore.Postgres/EFCoreFilterableLensQuery.cs:82,174,198` |
| Scope filtering (tenant, org, customer, user, principals) | same query | no | no | **R?** | `src/Whizbang.Data.EFCore.Postgres/EFCorePostgresLensQuery.cs:113-144` |
| TTL expiry predicate on every lens read | same query | no | no | **R?** | `src/Whizbang.Data.EFCore.Postgres/LensExpiryFilter.cs:24-50` |
| REST list endpoint: `CountAsync` then `Skip`/`Take` | same query | no | no | **R?** | `src/Whizbang.Transports.FastEndpoints.Generators/RestLensEndpointGenerator.cs:217,223,226` |
| `ISyncAwareLensQuery<T>.GetByIdAsync` | context, after awaiting the sync fence | no | **yes** | **P!** | `src/Whizbang.Core/Lenses/ISyncAwareLensQuery.cs:39-51`; `src/Whizbang.Core/Perspectives/Sync/IPerspectiveSyncAwaiter.cs:36,48` |
| Legacy `RegisterPerspectiveModel` lens shape | the **request** `DbContext`, shared with writers | possible | possible | **P** | `src/Whizbang.Data.EFCore.Postgres/EFCoreInfrastructureRegistration.cs:53-54` |

### Perspective apply

| Path | Connection today | Txn | RYW | Verdict | Evidence |
|---|---|---|---|---|---|
| Atomic upsert (preferred path): invariant is the `ON CONFLICT ... WHERE`, no row read | context | own statement | n/a | **P** | `src/Whizbang.Data.EFCore.Postgres/BaseUpsertStrategy.cs:225,347-374` |
| Fallback upsert: current-row read feeding the cross-pod lost-update guard, `Version + 1`, `CreatedAt` and `Scope` carry-forward | context | **no** (separate from `SaveChangesAsync`) | **yes** | **P!** | `src/Whizbang.Data.EFCore.Postgres/BaseUpsertStrategy.cs:507-510,529-536,545-549,597` |
| Runner: current perspective row (`GetByStreamIdAsync`) | context | no | **yes** | **P!** | `src/Whizbang.Generators/Templates/PerspectiveRunnerTemplate.cs:248-251` |
| Runner: persisted metadata for the idempotency filter | context | no | **yes** | **P!** | `src/Whizbang.Generators/Templates/PerspectiveRunnerTemplate.cs:317-335` |
| Runner: `commit_sequence` of the last applied event, to stamp the row | context | no | **yes** | **P!** | `src/Whizbang.Data.EFCore.Postgres/EFCoreEventStore.cs:283-289`, called at `PerspectiveRunnerTemplate.cs:654` |
| Runner: TTL resurrection probe `HasStreamEventsBeforeAsync` | context | no | **yes** | **P!** | `src/Whizbang.Generators/Templates/PerspectiveRunnerTemplate.cs:266-276`; `src/Whizbang.Data.EFCore.Postgres/EFCoreEventStore.cs:300,316` |
| Cursor read, cold cache and batch prefetch | context / pooled | no | **yes** | **P!** | `src/Whizbang.Core/Workers/PerspectiveWorker.cs:1800,2114,2982`; `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:4246-4250,4294-4303` |
| Collective apply: keyset `SELECT` then `UPDATE` under `pg_advisory_xact_lock` | context | **yes** | yes | **P** | `src/Whizbang.Data.EFCore.Postgres/Collective/EFCoreCollectiveAdapter.cs:225-268`; `src/Whizbang.Data.Dapper.Postgres/Collective/DapperCollectiveEventApplier.cs:187-238` |
| Collective sink: cursor read then event read | context | no | **yes** | **P!** | `src/Whizbang.Core/Workers/PerspectiveWorker.cs:3098-3135` |
| Rewind catch-up loop (terminates on "read returned zero") | context | no | **yes** | **P!** | `src/Whizbang.Generators/Templates/PerspectiveRunnerTemplate.cs:1183-1208,1336-1361` |
| Rebuild: `DISTINCT stream_id` scoping scan | context | no | no | **R** | `src/Whizbang.Core/Perspectives/PerspectiveRebuilder.cs:182-194`; `src/Whizbang.Data.EFCore.Postgres/RebuildPerspectiveCommandReceptor.cs:183-192` |
| Rebuild: per-stream full replay | context | no | see note | **P** | `src/Whizbang.Core/Perspectives/PerspectiveRebuilder.cs:261`; `PerspectiveRunnerTemplate.cs:184` |
| Replay reader for the `IsNew` lifecycle flag (pending-id set plus events) | context | no | **yes** | **P!** | `src/Whizbang.Data.EFCore.Postgres/Perspectives/EFCorePerspectiveReplayReader.cs:70,88-115` |
| Snapshot reads (latest, before-event, before-commit-sequence) | its own connection from the single data source | no | **yes** | **P!** | `src/Whizbang.Data.EFCore.Postgres/EFCorePerspectiveSnapshotStore.cs:71,100,132,161`; decision at `PerspectiveRunnerTemplate.cs:1023-1051,1069-1094` |
| Snapshot-existence probe before bootstrap | same | no | no | **R?** | `src/Whizbang.Core/Workers/PerspectiveWorker.cs:3566-3580`; `EFCorePerspectiveSnapshotStore.cs:188-190` |
| Perspective statistics gauge | context, `SqlQueryRaw` | no | no | **R** | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:3547-3566`; consumer `src/Whizbang.Core/Workers/PerspectiveWorker.cs:795-807` |

### Event store

| Path | Connection today | Txn | RYW | Verdict | Evidence |
|---|---|---|---|---|---|
| `get_stream_events` (**claims a lease and bumps `attempts`**; not a read) | context or pinned direct | **yes** | yes | **P** | `src/Whizbang.Data.Postgres/Migrations/139_PerspectiveFailureCounter.sql:106-148`; `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:4726,4740-4745` |
| SQL-side append: per-row `MAX(version)` under `pg_advisory_xact_lock` | the writing connection | **yes** | yes | **P** | `src/Whizbang.Data.Postgres/Migrations/149_MessagePriority.sql:500-508,573,581` |
| C# append: `GetLastSequenceAsync` before the insert (EF, Dapper Postgres, Dapper Sqlite) | context / own connection | **no** | **yes** | **P!** | `src/Whizbang.Data.EFCore.Postgres/EFCoreEventStore.cs:124-126,556-563`; `src/Whizbang.Data.Dapper.Postgres/DapperPostgresEventStore.cs:325-333`; `src/Whizbang.Data.Dapper.Sqlite/DapperSqliteEventStore.cs:47-60` |
| `HasStreamEventsBeforeAsync` resurrection probe | context | no | **yes** | **P!** | `src/Whizbang.Data.EFCore.Postgres/EFCoreEventStore.cs:300,316`; rationale `PerspectiveRunnerTemplate.cs:266-275` |
| `ReadAsync` / `ReadPolymorphicAsync` / `GetEventsBetweenPolymorphicAsync` on the live drain | context | no | **yes** | **P!** | `src/Whizbang.Data.EFCore.Postgres/EFCoreEventStore.cs:188,234,344,481-492`; callers `PerspectiveWorker.cs:1199,3135,3312,4124` |
| `IEventStoreQuery.Query` / `GetEventsByType` (ad hoc read surface) | context | no | no | **R?** | `src/Whizbang.Core/Messaging/IEventStoreQuery.cs:28,46`; `src/Whizbang.Data.EFCore.Postgres/EFCoreFilterableEventStoreQuery.cs:49,80` |
| Audit decorator: `GetLastSequenceAsync` to stamp `streamPosition` | context | no | **yes** | **P!** | `src/Whizbang.Core/SystemEvents/AuditingEventStoreDecorator.cs:122-125` |
| `commit_sequence` stamping and the visibility fence | **direct** (shared LISTEN connection) | **yes** | yes | **P** | `src/Whizbang.Data.Postgres/Notifications/PgCommitOrderStamperWorker.cs:325-364`; `src/Whizbang.Data.Postgres/Migrations/132_StampAlwaysRingsDebounced.sql:42-73` |
| `get_stream_events` unstamped visibility gate | inside the claim | yes | yes | **P** | `src/Whizbang.Data.Postgres/Migrations/058_GetStreamEventsUnstampedGate.sql:79,113` |
| `SelectRedeliveryEventsAsync` (keyset-paginated, cadence-driven) | pooled or pinned | no | no | **R** | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:2401,2418-2434` |
| `GetEphemeralPairsNeedingSnapshotAsync` | pooled or pinned | no | no | **R** | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:653,671-687` |
| `GetEphemeralBodiesAboutToReapAsync` (the `SELECT` form of a `DELETE` predicate) | pooled or pinned | no | **yes** | **P!** | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:1722,1739-1759` |
| `GetStateBasedStreamIdsAsync` (event flags never change once written) | pooled or pinned | no | no | **R** | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:539,558` |
| `CountSourcedEventsForTypesAsync` (type reconciliation count) | pooled or pinned | no | no | **R** | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:434,455-458` |

### Work coordination

| Path | Connection today | Txn | RYW | Verdict | Evidence |
|---|---|---|---|---|---|
| `claim_work` and every `claim_orphaned_*`: `EXISTS` short-circuits, rank, re-offer CTEs, stream-FIFO head guards, notify watermark | context or pinned direct | **yes** | yes | **P** | `src/Whizbang.Data.Postgres/Migrations/150_BucketAwareClaim.sql:687-690,726-762,850-903,1046-1130`; `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:2054-2061` |
| `claim_orphaned_inbox` owner-liveness probe over `pg_stat_activity` | inside the claim | yes | yes | **P** | `src/Whizbang.Data.Postgres/Migrations/150_BucketAwareClaim.sql:114-128` |
| `count_outstanding_work`, riding the claim's own round trip and snapshot | same connection as the claim | **yes** when bundled | **yes** | **P!** | `src/Whizbang.Data.Postgres/Migrations/123_CountOutstandingWork.sql:53-82`; `EFCoreWorkCoordinator.cs:170,2062-2067`; consumer `src/Whizbang.Core/Workers/ClaimWorker.cs:862,919-920` |
| `CountServiceBacklogAsync` (bounded counts on all four work tables plus an age probe) | pooled or pinned | no | **yes** | **P!** | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:234-296`; consumers `src/Whizbang.Core/Workers/MaintenanceWorker.cs:87`, `src/Whizbang.Data.EFCore.Postgres/IntegrityCheckpointReceptor.cs:93-101,128-129` |
| `fetch_inbox_batch` / `fetch_outbox_batch` (selects the rows `claim_work` just leased; hydrates `Attempts`) | pooled or pinned | no | **yes** | **P!** | `src/Whizbang.Data.Postgres/Migrations/091_DrainFetchByteBudget.sql:75-76`; `096_OutboxDrainFetchByteBudget.sql:99-100`; `EFCoreWorkCoordinator.cs:4884,4934,4998,5027` |
| Lease renewal, completion, attempt refund, unstarted-lease release | pooled or pinned | **yes** | yes | **P** | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:2313,389,2349,2387-2396` |
| `ClaimAndFetchPendingPerspectiveEventsAsync` (claim and read in one call, deliberately) | pooled or pinned | **yes** | yes | **P** | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:5074`; rationale `src/Whizbang.Core/Messaging/IWorkCoordinator.cs:2120-2125` |
| `resolve_sync_inquiries` (backs the perspective sync fence) | pooled or pinned | no | **yes** | **P!** | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:1986` |
| Work-available and due-schedule poll sources over `wh_active_streams` | its **own** resolved connection (`-direct` preferred) | no | **yes** | **P!** | `src/Whizbang.Data.Postgres/Notifications/PgWorkAvailablePollSources.cs:33-101,113-119`; `PgWorkAvailablePollSourceBase.cs:88-102`; `src/Whizbang.Core/Signals/PollIdleBackoff.cs:38-45` |
| `GetPendingCoalesceGroupStatsAsync` (prices the coalesce fire decision) | pooled or pinned | no | yes | **P** | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:3813-3817` |
| `FetchPendingCoalesceAsync` (`FOR UPDATE SKIP LOCKED`) | pooled or pinned | **yes** | yes | **P** | `src/Whizbang.Core/Messaging/IWorkCoordinator.cs:2279-2290` |
| Doorbell ring (write, after commit) | same connection as the write | yes | n/a | **P** | `src/Whizbang.Data.Postgres/DoorbellRinger.cs:33-60`; `EFCoreWorkCoordinator.cs:364,2117,2167` |
| `record_heartbeat`, `cleanup_stale_instances`, `calculate_instance_rank` | pooled or pinned | **yes** | yes | **P** | `src/Whizbang.Data.Postgres/Migrations/147_HeartbeatRegistryBackfill.sql:54-81`; `106_InstanceEvictionFencing.sql:48`; `012_CalculateInstanceRank.sql:9-48` |
| `is_instance_alive` over `pg_locks`, `wh_live_instances` over `pg_stat_activity` | its own resolved connection | no | n/a | **P!** | `src/Whizbang.Data.Postgres/Migrations/055_InstanceAliveAdvisoryLock.sql:61-64`; `052_LiveInstancesView.sql:48-50`; consumer `src/Whizbang.Data.Postgres/Notifications/PgInstanceLifecycleMonitor.cs:173-178,196-208` |
| Duty election: session `pg_try_advisory_lock` plus `record_capability` | **direct**, its own dedicated connection | yes | yes | **P!** | `src/Whizbang.Data.Postgres/Notifications/PgDutyElector.cs:114-117,123,138`; `src/Whizbang.Data.Postgres/Migrations/108_InstanceCapabilities.sql:58-78` |
| `GetStandbyRequestAsync` | pooled or pinned | no | no | **R?** | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:1216-1245` |
| `FindStuckOutboxRowsAsync` / `FindStuckInboxRowsAsync` (warning sentinels) | pooled or pinned | no | no | **R** | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:3981-4026`; `src/Whizbang.Core/Messaging/IWorkCoordinator.cs:2242-2244` |
| `GetOrphanedLifecycleEventsAsync` | context, `SqlQueryRaw` | no | no | **R** | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:4425` |

### Schema, startup, and LISTEN/NOTIFY

| Path | Connection today | Txn | RYW | Verdict | Evidence |
|---|---|---|---|---|---|
| `LISTEN` on the shared notification connection | **direct**, required | no | n/a | **P!** | `src/Whizbang.Data.Postgres/Notifications/PgSharedNotifyConnection.cs:681-704`; see the note below |
| The instance alive-lock, held on the LISTEN connection so `pg_locks` sees the pod | **direct** | no | n/a | **P!** | `src/Whizbang.Data.Postgres/Notifications/PgSharedNotifyConnection.cs:434` |
| `AdvisoryLockProbe.IsHeldElsewhereAsync` over `pg_locks` (is the migrator alive or dead) | init, else the context's own data source | no | n/a | **P!** | `src/Whizbang.Data.Postgres/AdvisoryLockProbe.cs:46-74`; callers `src/Whizbang.Data.EFCore.Postgres.Generators/Templates/DbContextSchemaExtensionTemplate.cs:242,245,402` |
| `SchemaMigrationDeferral`: "is the schema current yet" before "is the lock held" | same | no | **yes** | **P!** | `ai-docs/schema-initialization-connections.md:143-156` |
| Bootstrap closure hash read (`wh_bootstrap_closure`) | init or the context's own data source | **yes** | **yes** | **P!** | `ai-docs/schema-initialization-connections.md:270-286` |
| Schema-version ledger (`wh_schema_migrations`) | init or the context's own data source | **yes** | **yes** | **P** | `ai-docs/schema-initialization-connections.md:344-347` |
| Commit-boundary segment applier and the maintenance `VACUUM` | resolved by `SchemaBoundaryConnections` | writes | yes | **P** | `src/Whizbang.Data.EFCore.Postgres/SchemaBoundaryConnections.cs:54-94`; `DbContextSchemaExtensionTemplate.cs:79,1923` |
| Event-store health probe (`SELECT 1`) | pooled | no | n/a | **P!** | `src/Whizbang.Data.EFCore.Postgres/PostgresDriverExtensions.cs:215-222,359-366` |
| Table-statistics metrics: `pg_stat_user_tables`, `pg_stats`, `pg_relation_size` | pooled | no | n/a | **P!** | `src/Whizbang.Data.EFCore.Postgres/PostgresTableStatisticsProvider.cs:42-71` |
| Table-statistics metrics: queue-depth counts over the work tables | pooled | no | no | **R** | `src/Whizbang.Data.EFCore.Postgres/PostgresTableStatisticsProvider.cs:105-113` |
| Notify-debounce metrics: aggregate over `wh_notify_state` | pooled | no | no | **R** | `src/Whizbang.Data.EFCore.Postgres/PostgresNotifyDebounceStatsProvider.cs:33-38` |
| Message-type registry reconcile at startup | pooled | writes | yes | **P** | `src/Whizbang.Data.EFCore.Postgres/EFCoreMessageTypeRegistryPopulator.cs:25-32`; `PostgresDriverExtensions.cs:186-192` |

**Why `LISTEN`/`NOTIFY` must stay on a direct connection**, since the plan is asked to say so with
citations: `LISTEN` registration is session-scoped, and a transaction pooler returns the backend to
its pool after every transaction, so the registration is lost on the next hand-off. The framework
says this in three places and warns at runtime when it detects the pooled fallback: the log message
at `src/Whizbang.Data.Postgres/Notifications/PgSharedNotifyConnection.cs:703-704` ("LISTEN/NOTIFY
will not work through pgbouncer in transaction-pooling mode"), the resolver's `PooledKeyFallback`
source (`src/Whizbang.Core/Notifications/NotificationConnectionStringResolver.cs:87`), and the
documented topology, which budgets exactly one direct connection per pod for it. That is orthogonal
to the primary-versus-replica question, and it compounds it: the same connection also holds the
instance alive-lock that `pg_locks` reports to the rest of the fleet
(`PgSharedNotifyConnection.cs:434`).

### Dead-letter

| Path | Connection today | Txn | RYW | Verdict | Evidence |
|---|---|---|---|---|---|
| `FetchDueAsync` recovery-candidate selection, then per-row discard / exhaust / recover / reschedule | pooled or pinned | no | **yes** | **P!** | `src/Whizbang.Data.EFCore.Postgres/EFCoreDeadLetterRecoveryService.cs:36-63`; `src/Whizbang.Data.Postgres/Migrations/051_DeadLetterRecovery.sql:21-52`; mutations at `src/Whizbang.Core/Workers/DeadLetterRecoveryWorker.cs:528,587,617,630,647` |
| `EvaluateCampaignAsync` canary verdict (reads evidence, then writes the verdict) | pooled or pinned | **yes** | **yes** | **P!** | `src/Whizbang.Data.Postgres/Migrations/127_DlqCanaryCampaigns.sql:187-259`; consumer `src/Whizbang.Core/Workers/DeadLetterRecoveryWorker.cs:355-388` |
| `CountWaveRequarantinesAsync` (the only halt condition for a widening trickle wave) | pooled or pinned | no | **yes** | **P!** | `src/Whizbang.Data.Postgres/Migrations/127_DlqCanaryCampaigns.sql:334-359`; consumer `DeadLetterRecoveryWorker.cs:405-416` |
| `GetPassedCampaignFingerprintsAsync` (the exhaustion bypass) | pooled or pinned | no | **yes** | **P!** | `src/Whizbang.Data.Postgres/Migrations/134_PassedCampaignFingerprints.sql:17-27`; consumer `DeadLetterRecoveryWorker.cs:610` |
| `BeginCanaryProbesAsync` stratified pick (reads then writes in one function) | pooled or pinned | **yes** | yes | **P** | `src/Whizbang.Data.Postgres/Migrations/127_DlqCanaryCampaigns.sql:101-175` |
| `FetchUnstackedAsync` then `RecordStacksAsync` (stack-intelligence backfill, upsert) | pooled or pinned | no | no | **R** | `src/Whizbang.Data.Postgres/Migrations/128_DlqStackTables.sql:90-102`; `DeadLetterRecoveryWorker.cs:474-486` |
| `ListHeldCohortsAsync` as a display surface | pooled or pinned | no | no | **R?** | `src/Whizbang.Data.EFCore.Postgres/EFCoreDeadLetterRecoveryService.cs:78-92`; `src/Whizbang.Hosting.AspNet/DeadLetterOperatorEndpoints.cs:80` |
| `GET /due` operator listing (a stale id an operator then acts on) | pooled or pinned | no | no | **R?** | `src/Whizbang.Hosting.AspNet/DeadLetterOperatorEndpoints.cs:116` |
| Dead-letter population counts as a gauge | pooled | no | no | **R** | `src/Whizbang.Data.EFCore.Postgres/PostgresTableStatisticsProvider.cs:104-114` |

### Integrity

| Path | Connection today | Txn | RYW | Verdict | Evidence |
|---|---|---|---|---|---|
| `CountReceivedFromOriginAsync` (the recount that **confirms** a gap and unlocks auto-repair) | pooled or pinned | no | **yes** | **P!** | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:3109-3149`; decision and its recorded failure mode at `src/Whizbang.Data.EFCore.Postgres/IntegrityCheckpointReceptor.cs:86-90,119-121` |
| `GetPerspectiveCoverageGapsAsync` (issues a rebuild command) | pooled or pinned | no | **yes** | **P!** | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:3325-3361`; consumer `src/Whizbang.Core/Workers/IntegrityAuditWorker.cs:196,207` |
| `VerifyDigestTableAsync` / `VerifyDigestEpochsAsync` (data-modifying CTEs; the read *is* the write) | pooled or pinned | **yes** | yes | **P** | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:1368-1381,3457-3482` |
| `GetIntegrityOriginGenerationAsync` (guards seal coherence) | pooled or pinned | no | **yes** | **P!** | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:1345-1355` |
| `GetIntegritySealAsync`, `GetIntegritySettledMaxAsync` (window the manifest request) | pooled or pinned | no | **yes** | **P!** | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:1330-1342,1426` |
| `ComputeStreamDigests*` / `ComputeTypeDigests*` (the origin's answer to "what do I hold") | pooled or pinned | no | **yes** | **P!** | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:1384,1462,1511,3245,3289` |
| Integrity claim functions (report, repair, batch, drain) | pooled or pinned | **yes** | yes | **P** | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:698,712,778,809,855` |
| `GetIntegrityLedgerSummaryAsync` (convergence gauges only) | pooled or pinned | no | no | **R** | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:949-997`; `src/Whizbang.Data.Postgres/Migrations/090_IntegrityLedger.sql:251-270` |
| `GetOwnAuditedEventTypesAsync` (which topics the origin checkpoints) | pooled or pinned | no | no | **R?** | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:3365-3384` |

### Health, diagnostics, and metrics

| Path | Connection today | Txn | RYW | Verdict | Evidence |
|---|---|---|---|---|---|
| `PostgresHealthCheck` (`SELECT 1`, reported as "PostgreSQL database is accessible") | pooled | no | n/a | **P!** | `src/Whizbang.Data.Dapper.Postgres/PostgresHealthCheck.cs:19-35` |
| `PostgresConnectionRetry._isSchemaReadyAsync` over `information_schema` | its own retry connection | no | **yes** | **P!** | `src/Whizbang.Data.Postgres/PostgresConnectionRetry.cs:236-268` |
| Every non-DB health source and health check (schema gate, managed aggregate, subscription state) | none | no | n/a | n/a | `src/Whizbang.Hosting.AspNet/SchemaReadyHealthCheck.cs:18-27`; `src/Whizbang.Hosting.AspNet/WhizbangManagedHealthCheck.cs:26-48`; `src/Whizbang.Core/HealthChecks/SubscriptionHealthCheck.cs:28-93` |
| Every `ObservableGauge` callback (21 of them, all reading in-process caches, none querying) | none | no | n/a | n/a | `src/Whizbang.Core/Observability/TableStatisticsMetrics.cs:19-48`; `src/Whizbang.Core/Observability/StreamIntegrityMetrics.cs:190-221`; `src/Whizbang.Core/Observability/PerspectiveMetrics.cs:118` |
| `EFCorePostgresApplyStackQuery` (on-demand analytics endpoint, documented as off the hot path) | context, own scope | no | no | **R** | `src/Whizbang.Data.EFCore.Postgres/EFCorePostgresApplyStackQuery.cs:11-13,43-115`; `src/Whizbang.Hosting.AspNet/ApplyStackEndpoints.cs:72-73` |
| `EFCorePostgresStartupFleetStatusSource` (fleet display) | context, own scope | no | no | **R?** | `src/Whizbang.Data.EFCore.Postgres/EFCorePostgresStartupFleetStatusSource.cs:31-73` |
| `FleetVersions` mixed-fleet warning before a stored-form rewrite | its own connection | no | no | **R?** | `src/Whizbang.Data.Postgres/FleetVersions.cs:44-82` |

### Startup assess, schema ledger, and registries

| Path | Connection today | Txn | RYW | Verdict | Evidence |
|---|---|---|---|---|---|
| Startup assess: recorded library versions from the migration ledger, gating `Migrate` / `Serve` / `StandDown` | context | no | **yes** | **P!** | `src/Whizbang.Data.EFCore.Postgres/EFCorePostgresStartupAssessor.cs:60-129`; `src/Whizbang.Core/Startup/AssessStartupStep.cs:80-95`; re-assessed at runtime by `src/Whizbang.Core/Startup/StandbyWatcher.cs:163,207,216,230` |
| `_bulkGetHashesAsync` (the skip-initialization fast path) | init, else the context's own data source | no | **yes** | **P!** | `src/Whizbang.Data.EFCore.Postgres.Generators/Templates/DbContextSchemaExtensionTemplate.cs:283-285,298,330,892-910` |
| `_getExistingVersionAsync`, feeding `MigrationVersionGuard.MayApply` ("an older instance never overwrites a newer one") | inside the DDL transaction | **yes** | **yes** | **P!** | `DbContextSchemaExtensionTemplate.cs:1428-1437`; `src/Whizbang.Data.Postgres/MigrationVersionGuard.cs:48-58` |
| `pg_proc` duplicate-overload and stale-function-body probes; `_isSchemaCurrentAsync` | same | no | **yes** | **P!** | `DbContextSchemaExtensionTemplate.cs:696-700,766-789,861-866` |
| Dapper schema-initializer catalog reads (same DDL-skip decisions) | its own connection | no | **yes** | **P!** | `src/Whizbang.Data.Dapper.Postgres/PostgresSchemaInitializer.cs:122,297,665` |
| `SchemaBootstrapPhase` closure-hash probe (**fails safe**: unreadable reads as not-recorded, which applies) | init or the context's own data source | **yes** | yes | **P** | `src/Whizbang.Data.Postgres/SchemaBootstrapPhase.cs:291-307` |
| `CanElectAsync` object-existence probe (degrades to a lost optimization, and logs) | same | no | no | **R?** | `src/Whizbang.Data.Postgres/SchemaBootstrapPhase.cs:198-220` |
| `GetTypeDefinitionsAsync` snapshot, then re-register and record lineage | pooled or pinned | no | **yes** | **P!** | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:466-486`; `src/Whizbang.Core/Fingerprint/TypeDefinitionReconciler.cs:131,151,167` |
| `GetConsumedTypeRegistrationsAsync`, where an empty result is read as "first boot, baseline the whole catalog" and **permanently suppresses the backfill** | pooled or pinned | no | **yes** | **P!** | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:3176-3190`; `src/Whizbang.Core/Workers/SubscriptionExpansionWorker.cs:72-89,115` |
| `GetPerspectiveTableNamesAsync`, feeding cascade deletes | pooled or pinned | no | **yes** | **P!** | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:2817-2838`; `src/Whizbang.Core/Workers/MaintenanceWorker.cs:577,652,666` |
| `reconcile_message_type_registry` (the function writes) | pooled | **yes** | yes | **P** | `src/Whizbang.Data.EFCore.Postgres/EFCoreMessageTypeRegistryPopulator.cs:44-99` |
| Event-type rename drift detection, then updating both registries | its own connection | no | **yes** | **P!** | `src/Whizbang.Data.Dapper.Postgres/DapperEventTypeRenameTool.cs:63,158,165` |
| `PerspectiveMigrationWorker` pending-rebuild scan, which starts a blue/green rebuild | context | no | **yes** | **P** | `src/Whizbang.Core/Workers/PerspectiveMigrationWorker.cs:26,50,66` |

### Maintenance, housekeeping, and the reaper

| Path | Connection today | Txn | RYW | Verdict | Evidence |
|---|---|---|---|---|---|
| `GetExpiredOffloadClaimsAsync`, then deleting the offloaded bodies | pooled or pinned | no | **yes** | **P!** | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:2525`; `src/Whizbang.Core/Workers/MaintenanceWorker.cs:346-379` |
| `GetPerspectiveRowsAboutToReapAsync` (pre-destruction hooks) | pooled or pinned | no | **yes** | **P!** | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:2577`; `src/Whizbang.Core/Workers/MaintenanceWorker.cs:497-539` |
| `GetPerspectiveRowsByIdsAsync`, then cascade-deleting them | pooled or pinned | no | **yes** | **P!** | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:2840`; `src/Whizbang.Core/Workers/MaintenanceWorker.cs:622,652,666` |
| `GetCursorsRequiringRewindAsync` startup repair scan (a stale flag means a corrupt cursor is never repaired) | context | no | **yes** | **P!** | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:4617-4648` |
| `GetTablesNeedingRewriteAsync`, which can request a full-table rewrite | pooled or pinned | no | yes | **P** | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:1051-1070,1114-1131`; `src/Whizbang.Core/Workers/MaintenanceWorker.cs:694-712`; `src/Whizbang.Core/Startup/TableRewriteStartupStep.cs:73` |
| `perform_maintenance`, `close_digest_epochs`, the reap and fold passes, the sweep claims | pooled or pinned | **yes** | yes | **P** | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:5153-5176`; `src/Whizbang.Core/Workers/MaintenanceWorker.cs:145,224,237,261,276,431,452` |
| `DrainRowEvictionJournalAsync` (`DELETE ... RETURNING`) | pooled or pinned | **yes** | yes | **P** | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:2777` |
| Durable-signal retention delete (self-guarding, no separate decision read) | its own connection | **yes** | yes | **P** | `src/Whizbang.Data.Postgres/Notifications/PgDurableSignalRetentionWorker.cs:20-29,55-110` |

### Counts

112 classified rows above, plus two rows recording surfaces that touch no database at all. Some rows
group a family of closely related reads (the maintenance passes, for instance), so read these as
inventory rows rather than as individual SQL statements.

| Verdict | Count |
|---|---|
| **P!** must be the primary, and a replica would be **silently** wrong | 54 |
| **P** must be the primary, and a replica would fail loudly (it writes) or is otherwise unroutable | 29 |
| **R** replica-safe | 14 |
| **R?** replica-safe but the payoff is small, or the product must first agree it may be stale | 15 |

Read that the right way round: **83 of 112 must be the primary, and 54 of those would break
silently.** Only 14 are unreservedly replica-safe, and every one of them is a gauge, a log sentinel,
a one-shot startup reconciler, or an on-demand analytics endpoint.

Two structural observations that the counts understate:

- **The gauge layer is already off the database.** All 21 `ObservableGauge` callbacks read in-process
  caches; a separate collector does the query on a cadence
  (`src/Whizbang.Core/Observability/TableStatisticsMetrics.cs:19-48`;
  `src/Whizbang.Core/Observability/StreamIntegrityMetrics.cs:190-221`). So the metric surface that
  looks like the obvious replica win is already only a handful of periodic queries, not a per-scrape
  load.
- **Health is the worst candidate, not the best.** Every database-touching health path asserts the
  liveness of the node this service *writes to*
  (`src/Whizbang.Data.Dapper.Postgres/PostgresHealthCheck.cs:19-35`;
  `src/Whizbang.Data.EFCore.Postgres/PostgresDriverExtensions.cs:359-366`). Routing one to a reader
  inverts its meaning.

---

## Risks, with the silently-wrong cases called out

A hot standby refuses writes loudly (`25006`), so anything that writes is safe by construction: it
would fail on the first deployment and never reach production. The dangerous class is a pure `SELECT`
whose **answer is a decision**, because a lagging or server-local answer is well-formed, plausible,
and wrong.

There are three distinct ways a read-only endpoint is wrong here, and only the first is the one
people expect.

### Risk class 1: replication lag, where the answer is merely old

The familiar case. It is dangerous in this codebase because so many reads are not lookups but
**decisions the same code is about to act on**. The worst of them:

| Read | What a stale answer does | Evidence |
|---|---|---|
| The current-row read before a perspective upsert | The cross-pod lost-update guard compares the incoming `commit_sequence` against the stored one. A stale or absent stored row lets an older apply overwrite a newer one. The code names the observed consequence: a row reverted from a completed state to a running one, stranding a saga. The same read also carries `CreatedAt`, `Version` and `Scope` forward into the write, so a stale read regresses the version counter and writes back a stale scope, which is the tenant filter. | `src/Whizbang.Data.EFCore.Postgres/BaseUpsertStrategy.cs:507-510,526-536,545-549` |
| `count_outstanding_work` | Prices the claim's headroom. A stale-low count inflates headroom, the worker over-claims, every untouched row charges an attempt, and healthy messages dead-letter as max-attempts-exceeded with no failure recorded anywhere. | `src/Whizbang.Data.Postgres/Migrations/123_CountOutstandingWork.sql:53-82`; `src/Whizbang.Core/Workers/ClaimWorker.cs:862,919-920`; `src/Whizbang.Core/Messaging/IWorkCoordinator.cs:340-360` |
| `CountServiceBacklogAsync` | Two consumers, two different silent failures. The housekeeping gate reads a stale zero as settled and runs the heavy sweep at the peak of a load, which is a regression the repo has already measured and fixed once. The integrity checkpoint reads a stale-low backlog, concludes rows are genuinely missing rather than in flight, and triggers cross-service redelivery repair, which lengthens the queue that produced the false gap. | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:234-296`; `src/Whizbang.Core/Workers/MaintenanceWorker.cs:87`; `src/Whizbang.Data.EFCore.Postgres/IntegrityCheckpointReceptor.cs:86-101`; `ai-docs/load-under-bulk-import.md:61-77` |
| `fetch_inbox_batch` / `fetch_outbox_batch` | They select the rows `claim_work` leased in the immediately preceding statement. On a lagging reader the result is empty, the drain no-ops, the claim re-offers the same streams next cycle, and each re-offer charges another attempt. Converges on the same wrongful dead-lettering as the row above. | `src/Whizbang.Data.Postgres/Migrations/091_DrainFetchByteBudget.sql:75-76`; `096_OutboxDrainFetchByteBudget.sql:99-100` |
| `HasStreamEventsBeforeAsync` | The resurrection probe. A reader that has not yet received the earlier events answers "no history", and the runner folds one batch onto a fresh model. The code states the consequence: a sourced row is the fold of all its events, so this silently builds a corrupt partial row. | `src/Whizbang.Data.EFCore.Postgres/EFCoreEventStore.cs:300,316`; rationale at `src/Whizbang.Generators/Templates/PerspectiveRunnerTemplate.cs:266-275` |
| `GetLastSequenceAsync` before a C# append | A stale `MAX(version)` collides on every append. This one is loud, which is why it is in the second tier: the unique index raises, and the Dapper paths burn their retries and throw. | `src/Whizbang.Data.EFCore.Postgres/EFCoreEventStore.cs:124-126,167-171` |
| Perspective cursor reads | Drive the cursor-inversion detector and the rewind path. The code records that in-process cache staleness alone already produced either silent event loss or thousands of phantom rewinds; replica lag is strictly worse. | `src/Whizbang.Core/Workers/PerspectiveWorker.cs:2095-2134`; `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:4246-4250,4294-4303` |
| Snapshot reads | A snapshot written milliseconds earlier and not yet visible turns a rewind over the tail into a replay from event zero, and for an ephemeral perspective it drops the straggler and leaves the cursor unmoved. | `src/Whizbang.Data.EFCore.Postgres/EFCorePerspectiveSnapshotStore.cs:71,132`; decision at `PerspectiveRunnerTemplate.cs:1023-1051,1069-1094` |
| `GetEphemeralBodiesAboutToReapAsync` | It is the `DELETE` predicate of the reaper expressed as a `SELECT`, so hooks can hold bodies from destruction. A stale read hands the hooks a list that does not match what the delete will take, and misses holds written inside the lag window. Adjacent to irreversible deletion. | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:1722,1739-1759` |
| The work-available poll sources | The poll is the backstop for a debounced or suppressed notification, so when the store-side debounce has escalated it is the only prompt wake source. A stale "no work" both delays the wake and counts as an empty tick, and the empty tick walks the idle backoff toward its one-minute ceiling while work sits. The symptom is latency and there is no error. | `src/Whizbang.Data.Postgres/Notifications/PgWorkAvailablePollSources.cs:33-101,113-119`; `src/Whizbang.Core/Signals/PollIdleBackoff.cs:38-45` |

### The seven worst, ranked

If one read were routed by accident, these are the ones that would cost the most and announce it the
least. Each is a decision whose "no" and "nothing" branches are the destructive ones, and lag
produces exactly those.

1. **The integrity gap confirmation.** A consumer that is merely behind confirms a gap that does not
   exist and triggers cross-service redelivery repair, which lengthens the queue that produced the
   false gap. The file states this failure mode in its own words, as a hazard it already guards
   against; replication lag *is* that condition, permanently.
   (`src/Whizbang.Data.EFCore.Postgres/IntegrityCheckpointReceptor.cs:86-90,119-121`;
   `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:3109-3149`)
2. **The housekeeping and repair settledness gate.** A stale zero reads as settled, so the
   maintenance sweep, dead-letter recovery, and integrity auto-repair all fire at the peak of a
   load. Note the direction of the existing failure handling: an unreadable count is treated as
   unmeasured and the caller **proceeds**, so a reader that errors is more permissive, not less.
   (`src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:234-296`;
   `src/Whizbang.Core/Workers/MaintenanceWorker.cs:83-92`;
   `src/Whizbang.Core/Workers/HousekeepingCoordinator.cs:151-228`)
3. **The startup assess verdict.** The recorded-library-version read decides `Migrate`, `Serve`, or
   `StandDown`. A lagging ledger hides the newest recorded version, so an obsolete binary gets
   `Serve` and proceeds to serve, or contends for the migrator duty and re-applies older function
   bodies over newer ones. The assessor's own stance is that "every wrong answer at this point is
   worse than stopping", and this read is re-taken at runtime, so the exposure is continuous rather
   than only at boot.
   (`src/Whizbang.Data.EFCore.Postgres/EFCorePostgresStartupAssessor.cs:60-129`;
   `src/Whizbang.Core/Startup/StandbyWatcher.cs:163,207-230`)
4. **The migrator liveness probe.** `pg_locks` on a reader holds none of the primary's advisory
   locks, so a held lock reads as released, every waiter concludes the migrator died, and they all
   take the migration over concurrently while the real migrator is mid-DDL. This is the exact
   confusion the probe exists to resolve, collapsed to the wrong answer.
   (`src/Whizbang.Data.Postgres/AdvisoryLockProbe.cs:11-58`;
   `ai-docs/schema-initialization-connections.md:130-156`)
5. **The consumed-type baseline.** An empty result is interpreted as "first boot: nothing existed
   before this service to miss", which registers the whole catalog as a baseline and **permanently
   suppresses the backfill request**. Lag reproduces the empty result exactly, and the log line says
   "baselined", which is indistinguishable from a real first boot.
   (`src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:3176-3190`;
   `src/Whizbang.Core/Workers/SubscriptionExpansionWorker.cs:72-89`)
6. **The expired-offload-claim sweep.** A stale claim row means deleting an offloaded message body
   whose claim was just renewed. Irreversible, and silent.
   (`src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:2525`;
   `src/Whizbang.Core/Workers/MaintenanceWorker.cs:346-379`)
7. **The schema-initialization fast path.** A stale hash set makes the initializer conclude the
   schema is current and skip DDL it needed to apply, logging only that the schema is up to date.
   (`src/Whizbang.Data.EFCore.Postgres.Generators/Templates/DbContextSchemaExtensionTemplate.cs:283-298,330,892-910`)

Honorable mention, because it is the same shape and destructive: the canary campaign verdict.
`evaluate_canary_campaign` deliberately resolves an empty evidence set to pending rather than a
vacuous pass, and the symmetric skew condemns half-passed cohorts as failed. Lag reproduces both
skews, a failure verdict is durable and burns a generation budget, and the worst case releases a
genuinely poisonous cohort.
(`src/Whizbang.Data.Postgres/Migrations/127_DlqCanaryCampaigns.sql:228-255`)

### Risk class 2: server-local catalogs, where the answer is not stale but structurally wrong

This is the class most likely to be missed, because it has nothing to do with lag. Several reads are
not over tables at all; they are over views that describe **the node answering the query**. A replica
answers them correctly about itself, which is the wrong subject.

| Catalog | Read as | On a reader it means | Evidence |
|---|---|---|---|
| `pg_locks` | Is the instance alive (`is_instance_alive`); is the migrator still holding the schema lock (`AdvisoryLockProbe`) | A replica holds none of the primary's advisory locks, so every instance reads as dead and every migrator reads as dead. The first announces the whole fleet dead and triggers fleet-wide orphan takeover; the second makes every waiter conclude the migrator died and take the migration over. | `src/Whizbang.Data.Postgres/Migrations/055_InstanceAliveAdvisoryLock.sql:61-64`; `src/Whizbang.Data.Postgres/AdvisoryLockProbe.cs:46-58` |
| `pg_stat_activity` | Is there a live LISTEN connection for this owner (`wh_live_instances`, the orphan-claim liveness guard) | The pods' LISTEN connections are on the primary, so a reader sees none and reports every owner dead, letting orphan claim steal live work. | `src/Whizbang.Data.Postgres/Migrations/052_LiveInstancesView.sql:48-50`; `150_BucketAwareClaim.sql:121` |
| `pg_stat_activity.backend_xid`, `pg_prepared_xacts`, `pg_current_snapshot()` | The commit-order stamping fence | The fence is a statement about one node's transaction horizon. Computed on the wrong node it is meaningless, and stamping order is the ordering guarantee the whole read side rests on. | `src/Whizbang.Data.Postgres/Migrations/132_StampAlwaysRingsDebounced.sql:42-66` |
| Session advisory locks | Duty election, stamper leadership | A replica-local lock excludes nobody. Every instance wins its own and all of them believe they hold the duty. | `src/Whizbang.Data.Postgres/Notifications/PgDutyElector.cs:114-123`; `src/Whizbang.Data.Postgres/Notifications/PgCommitOrderStamperWorker.cs:325-332` |
| `pg_stat_user_tables` | Table size and bloat metrics | The activity counters on a standby are the standby's own, so the gauges would describe a node nobody writes to. | `src/Whizbang.Data.EFCore.Postgres/PostgresTableStatisticsProvider.cs:42-71` |
| `SELECT 1` on the configured data source | The event-store health probe | It answers "can I reach *a* database", not "can I reach the one I write to". A pod would report ready while the primary is unreachable. | `src/Whizbang.Data.EFCore.Postgres/PostgresDriverExtensions.cs:215-222,359-366` |

### Risk class 3: a fence that stops fencing

The framework has two consistency fences on the read side, and neither covers replication. Both would
keep passing and stop protecting:

- **The perspective sync fence.** `ISyncAwareLensQuery` waits for the apply to complete before
  querying, and learns that from the primary. Put the query on a reader and the fence passes while
  the row is not there yet. (`src/Whizbang.Core/Lenses/ISyncAwareLensQuery.cs:39-51`)
- **The unstamped visibility gate.** `get_stream_events` refuses rows not yet stamped with a
  `commit_sequence`, which is a statement about one node's transaction horizon and says nothing
  about replication. (`src/Whizbang.Data.Postgres/Migrations/058_GetStreamEventsUnstampedGate.sql:79,113`)

A third, weaker fence is worth naming because it is the pattern the codebase already relies on and a
replica defeats it. Several gates distinguish "nothing outstanding" from "nobody looked" by treating
a null as unmeasured, never as settled. The integrity checkpoint states it directly: "'nothing
outstanding' and 'nobody looked' are the same value and opposite facts"
(`src/Whizbang.Data.EFCore.Postgres/IntegrityCheckpointReceptor.cs:91-101`; also
`src/Whizbang.Core/Messaging/IWorkCoordinator.cs:184-187`). A reader introduces a third case with the
same value and yet another opposite fact: a plausible wrong number. The null guard cannot see it.

### A process note that makes class 1 harder to catch

Two of the read-only SQL functions are declared `LANGUAGE plpgsql` with no volatility marker and do
no writes, so they would **execute successfully on a hot standby** and simply return stale numbers:
`count_outstanding_work` (`src/Whizbang.Data.Postgres/Migrations/123_CountOutstandingWork.sql:82`)
and `resolve_sync_inquiries`. "Does it raise `25006`?" is therefore not a safe heuristic for what may
be routed. Any routing must work from an explicit allow-list, which is what stage 3 below proposes.

---

## Design, if a `-readonly` connection is adopted anyway

The decision above says the framework's connection stays on the primary, so none of this is proposed
as work to do now. It is written down so that the answer exists if the question is asked again, and
so that the cost is on the record rather than guessed at.

| Stage | What it delivers | Size | Status |
|---|---|---|---|
| 1 | Resolve a `-readonly` sibling key, falling back to the pooled key so a consumer without a replica is unchanged | small | `Not started` |
| 2 | A reader data source behind a marker interface plus a reader context factory, wired into the lens path only | medium | `Not started` |
| 3 | The deny-by-default allow-list, as a registry test, plus a connection-role assertion in the coordinator's connection acquisition | small | `Not started` |
| 4 | A replication-lag gauge, a documented staleness bound, and an explicit decision about the sync-aware lens path | medium | `Not started` |
| 5 | Startup detection and a one-line Information log when the reader key points at the primary | small | `Not started` |

Every stage is gated on the first item in "Not decided" below. Nothing here should be started until
that is answered.

### How much is actually on the table

Worth stating before the stages, because it is the part a reader will assume and get wrong. Four
paths carry essentially all of the value, and three of them are cheap:

1. **The lens read side.** The only genuinely high-volume read surface in the framework, and the one
   the read/write split exists for. It needs no change-tracking work (it is already
   `AsNoTracking()` throughout), it already resolves its context through a factory that opens a
   fresh scope, and its read surface is closed to `IQueryable` and `GetByIdAsync`. It is also the one
   path whose staleness is a **product** question rather than a framework one.
2. **The work-statistics gauge.** Four unbounded `COUNT(*)` over the queue tables and the active-stream
   table on a periodic cadence, feeding one gauge, inside a catch-everything block. These are the
   last unbounded counts in the hot set, so this is the largest single load item that is also
   unreservedly safe. (`src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:3547-3566`;
   consumer `src/Whizbang.Core/Workers/PerspectiveWorker.cs:792-807`)
3. **The rebuild scoping scan.** An unbounded `DISTINCT stream_id` over the whole event store, with
   the weakest consistency requirement in the inventory: a stream missing from a lagging reader is
   one whose events are all newer than the cursor, which the live drain applies anyway.
   (`src/Whizbang.Core/Perspectives/PerspectiveRebuilder.cs:182-194`)
4. **The two standalone statistics providers and the apply-stack analytics endpoint.** Already own
   their own data source or their own scope, so they are the cheapest to re-point, though the
   `pg_stat_user_tables` half of the table-statistics provider must stay (risk class 2 below).

Everything else on the `R` list is a one-shot startup reconciler or a log sentinel, and moving those
buys nothing measurable. So the honest summary is: **one path worth real money, three worth a
measurable amount, and a long tail worth nothing.** The framework already spent its optimization
effort on this problem in a different direction, by decomposing the poll and bounding it by the
batch it returns rather than the backlog it scans (`ai-docs/load-under-bulk-import.md:40-62`), and by
pinning the hot worker connections off the pooler
(`src/Whizbang.Core/Workers/WhizbangPinnedPoolOptions.cs:10-16`).

### Stage 1: resolve a reader key, falling back to the pooled key

Status: `Not started`.

Add one helper beside `DeriveConnectionStringName`
(`src/Whizbang.Core/Naming/WhizbangNamingConvention.cs:48`) and one resolver modeled on the
notification resolver's precedence
(`src/Whizbang.Core/Notifications/NotificationConnectionStringResolver.cs:61-97`):

1. an explicit option, when the host set one;
2. `ConnectionStrings:{derivedKey}-readonly`;
3. `ConnectionStrings:{derivedKey}` (the pooled key).

Step 3 is what makes the feature free for a consumer with no replica: the reader resolves to the
same endpoint it uses today, the code path is identical, and nothing changes. The resolver must
report its source the way the notification one does (`ResolutionSource`), because "fell back to the
pooled key" and "used the reader" are the two facts an operator needs and cannot otherwise observe.

Reuse, do not duplicate. The naming convention type exists precisely because two implementations of
one key convention silently diverged once and the notification workers ended up on the pooled
connection while EF used a different one
(`src/Whizbang.Core/Naming/WhizbangNamingConvention.cs:8-27`).

### Stage 2: how the read side would use it in EF Core 10

Status: `Not started`.

Three options, sized honestly.

**Rejected: a second `DbContext` type.** `DbContextOptions<T>` is keyed by `T`, so two endpoints for
one context type cannot both be registered. A derived reader type would need its own
`OnModelCreating`, its own generated perspective configuration, its own JSON wiring, and its own
`dotnet ef dbcontext optimize --nativeaot` run for any consumer using compiled models
(`ai-docs/efcore-aot-support.md:88,110-111`). That roughly doubles the generated surface for one
connection string.

**Rejected: swapping the connection per query.** Changing a context's connection requires it closed
and untransacted, and the turnkey registration configures the context with an `NpgsqlDataSource`
rather than a string, so there is nothing to swap to. It would also mutate a context other code in
the same DI scope is holding.

**The honest option: a reader data source behind a marker interface, plus a reader context factory.**
Two reasons this is the shape:

- A bare second `NpgsqlDataSource` registration cannot survive. The generated registration calls
  `services.RemoveAll<Npgsql.NpgsqlDataSource>()` before registering its own
  (`src/Whizbang.Data.EFCore.Postgres.Generators/EFCoreServiceRegistrationGenerator.cs:1980`), which
  is how an externally supplied one is dropped today. The framework already has the answer for
  exactly this collision: `INotificationDataSource` wraps a second data source behind a dedicated
  interface, and its remarks say why a bare resolution is wrong
  (`src/Whizbang.Data.Postgres/Notifications/INotificationDataSource.cs:7-39`). A reader data source
  should follow that precedent, not invent a fourth pattern.
- The read side needs no change-tracking work at all, because it already tracks nothing. Every lens
  read is `AsNoTracking()` (`src/Whizbang.Data.EFCore.Postgres/EFCorePostgresLensQuery.cs:161,185`;
  `src/Whizbang.Data.EFCore.Postgres/EFCoreFilterableLensQuery.cs:82,174,198`;
  `src/Whizbang.Data.EFCore.Postgres/MultiModelScopedAccess.cs:23`), and no context sets
  `QueryTrackingBehavior` anywhere. So a reader context needs a different data source and nothing
  else.

The seam is already there. Every generated lens registration takes
`IDbContextFactory<TDbContext>` and calls `CreateDbContext()`, which opens a fresh DI scope and
resolves a fresh context (`src/Whizbang.Data.EFCore.Postgres/ScopedDbContextFactory.cs:52-57`;
registration at
`src/Whizbang.Data.EFCore.Postgres.Generators/Templates/Snippets/EFCoreSnippets.cs:329-337`). A
reader factory registered alongside it, building `DbContextOptions<T>` over the reader data source
with the same `_emitNpgsqlConfiguration` body
(`src/Whizbang.Data.EFCore.Postgres.Generators/EFCoreServiceRegistrationGenerator.cs:398-411`), is a
drop-in for that one call. The compiled model, if a consumer generated one, is registered on the
options and can be reused as-is; no second model is needed.

Two caveats that must ship with it:

- `EnableRetryOnFailure` is on by default
  (`src/Whizbang.Data.EFCore.Postgres.Generators/EFCoreServiceRegistrationGenerator.cs:407`), so a
  reader context inherits a retrying execution strategy. That is correct for reads, but any caller
  that opens a transaction on a reader context must do so inside
  `CreateExecutionStrategy().ExecuteAsync(...)` (the rule recorded at
  `src/Whizbang.Data.EFCore.Postgres/Collective/EFCoreCollectiveAdapter.cs:171-176`).
- The legacy `RegisterPerspectiveModel` shape registers `ILensQuery<TModel>` scoped over the
  *request* `DbContext` (`src/Whizbang.Data.EFCore.Postgres/EFCoreInfrastructureRegistration.cs:53-54`),
  which is the same context the writers use. No generator emits it today, but a host that wires
  lenses that way would put reads on the write context, and must not be moved to a reader.

### Stage 3: what must never use it, and the guard

Status: `Not started`.

"Must be the primary" is not a property of a query; it is a property of the component that issues
it. The guard therefore belongs on components, and there are three candidate shapes. The
recommendation is the second, with the third as a cheap backstop.

**An analyzer.** The repo already ships thirteen analyzers
(`src/Whizbang.Generators/Analyzers/`), so the precedent exists. But an analyzer cannot see through
a DI resolution to know which data source a `DbContext` came from, so it would be limited to
flagging direct construction of a reader context inside a type marked as a writer. Low value for the
cost.

**A registry test, which is the recommendation.** The set of components allowed a reader connection
is small, enumerable, and stable. On this audit's evidence it is: the lens query implementations and
their factories; the two standalone statistics providers
(`src/Whizbang.Data.EFCore.Postgres/PostgresTableStatisticsProvider.cs`, minus its
`pg_stat_user_tables` half, and
`src/Whizbang.Data.EFCore.Postgres/PostgresNotifyDebounceStatsProvider.cs`); the apply-stack
analytics query; the two stuck-row log sentinels; the integrity ledger summary; the work-statistics
gauge; the rebuild scoping scan; and the handful of one-shot startup reconcilers listed in the
inventory. Everything else is denied by default. A test that resolves a host and asserts that
allow-list exactly, failing on any addition the list does not name, is the same shape as the existing
wiring audits
(for example `tests/Whizbang.Core.Tests/Startup/StartupWiringAuditTests.cs`,
`tests/Whizbang.Data.EFCore.Postgres.Tests/PostgresDriverExtensions_TurnkeyResolverWiringTests.cs`).
It fails on the change rather than on the deployment, which is the whole point.

**A runtime assertion as a backstop.** A reader data source should carry a connection-role tag, and
the coordinator's connection acquisition should refuse a reader. `CoordinatorConnectionScope` is
already the single place where the "pinned versus fresh" connection decision is made
(`src/Whizbang.Data.Postgres/CoordinatorConnectionScope.cs:40-108`), so a role check there covers
every coordinator read and write on both drivers in one place. This is cheap and it turns a silent
class of bug into a loud one.

### Stage 4: staleness handling, and why the existing fence does not cover it

Status: `Not started`.

This is the part most likely to be got wrong, because the framework already has a read-your-writes
fence on the lens path and it would appear to cover a replica while doing nothing of the kind.

`ISyncAwareLensQuery<TModel>.GetByIdAsync` waits on `IPerspectiveSyncAwaiter` before querying
(`src/Whizbang.Core/Lenses/ISyncAwareLensQuery.cs:39-51`;
`src/Whizbang.Core/Perspectives/Sync/IPerspectiveSyncAwaiter.cs:36,48`). The awaiter answers "has the
perspective apply completed", which it learns from the primary. It says nothing about whether the
endpoint the subsequent query runs against has received that row. Put the query on a reader and the
fence passes, the read is stale, and there is no error. **A fence that silently stops fencing is
worse than no fence**, so if the lens path ever moves, the sync-aware path must either stay on the
primary or gain a lag check, and that choice has to be explicit.

The event store's own visibility fence has the same shape and the same limit. `get_stream_events`
refuses rows whose `commit_sequence` is not yet stamped
(`src/Whizbang.Data.Postgres/Migrations/058_GetStreamEventsUnstampedGate.sql:79,113`), which is a
statement about one node's transaction horizon. It is not a statement about replication.

What a caller would need instead, if any path moves:

- a lag reading the caller can see, from `pg_last_xact_replay_timestamp()` on the reader, surfaced as
  a gauge beside the existing ones (the table-statistics and notify-debounce collectors are the
  model: `src/Whizbang.Data.EFCore.Postgres/PostgresDriverExtensions.cs:226-254`);
- a documented bound, so "the grid may be a few seconds behind" is a contract rather than a
  surprise;
- and for a caller that cannot tolerate it, a way to ask for the primary on that one query.

### Stage 5: migration path, and a reader that is really the primary

Status: `Not started`.

Migration is a no-op by construction: with the stage-1 fallback, a consumer that configures nothing
keeps today's behavior exactly, and a consumer that configures `-readonly` moves only the paths
stage 3's allow-list names.

The one thing the framework must do is refuse to be quietly misconfigured. A `-readonly` key
pointing at the primary is the common case in practice (it is what a consumer writes first, and what
a failover leaves behind), and it is indistinguishable from a working replica by every symptom. The
framework can tell the difference in one round trip:

- `SELECT pg_is_in_recovery()` is true on a standby and false on a primary. Neither it nor
  `transaction_read_only` is queried anywhere in the framework today, so this is new code, not a
  change.
- A reader that reports `false` is the primary. That is not an error, and must not be treated as one:
  it is a supported configuration (it is what the fallback produces), and refusing to start over it
  would be the "never migrating needs a human to clear" failure the schema work already warns about
  (`ai-docs/schema-initialization-connections.md:356-362`). Log it once at Information, naming the
  key and the consequence: reads are on the primary, the reader key bought nothing.
- A reader that reports `true` and is also the endpoint the writes go to cannot happen; writes would
  fail loudly on it.

Probe at startup, once, and re-probe on a reconnect. Do not probe per query.

---

## Incidental findings, recorded but not fixed here

These surfaced during the audit, are unrelated to connection separation, and each needs a design
decision plus tests rather than an obvious edit. Nothing in this branch changes them.

1. **The lens raw-SQL escape hatch cannot work.** `ExecuteSqlAsync`, `GetConnection` and
   `GetConnectionAsync` all require the lens to implement the internal `IDbContextAccessor`
   (`src/Whizbang.Data.EFCore.Postgres/LensQueryConnectionExtensions.cs:77,110,140,171`), and no
   production lens does; the only implementor is a test double. Every call against a real lens
   throws. For this audit that is good news, because it means the lens read surface is closed and
   there is no hidden raw-connection read path. As an API it is a public promise that is never kept.
2. **The Dapper perspective store has no lost-update fence.** Its `INSERT … ON CONFLICT DO UPDATE`
   sets every column unconditionally
   (`src/Whizbang.Data.Dapper.Postgres/DapperPostgresPerspectiveStore.cs:172-192`), where the EF
   atomic path gates the update on the incoming `commit_sequence` exceeding the stored one
   (`src/Whizbang.Data.EFCore.Postgres/BaseUpsertStrategy.cs:347-355`). The Dapper driver is
   last-writer-wins.
3. **The Dapper perspective store disables the idempotency filter.** It does not override
   `GetMetadataByStreamIdAsync`, so it inherits the interface default that returns `null`
   (`src/Whizbang.Core/Perspectives/IPerspectiveStore.cs:41-42`), and the runner's filter treats a
   null as "cannot order, let it through"
   (`src/Whizbang.Generators/Templates/PerspectiveRunnerTemplate.cs:317-335`).
4. **The emitted single-model lens registration skips the read-models gate.** The multi-model path
   calls `ReadModelsGuard.ThrowIfNotReady`
   (`src/Whizbang.Data.EFCore.Postgres.Generators/EFCoreServiceRegistrationGenerator.cs:1096`) and
   the legacy path calls it
   (`src/Whizbang.Data.EFCore.Postgres/EFCoreInfrastructureRegistration.cs:53`), but the emitted
   single-model snippet does not
   (`src/Whizbang.Data.EFCore.Postgres.Generators/Templates/Snippets/EFCoreSnippets.cs:329-338`).

---

## Not decided: the owner's call

1. **Whether to adopt a reader connection at all.** The documented decision says the framework's
   connection stays on the primary
   (`whizbang-lib.github.io/src/assets/docs/v1.0.0/operations/deployment/scaling.md:256`), and this
   audit found nothing that contradicts it. Reopening it is a product call, not an engineering one.
2. **Whether the lens path may be stale.** Everything in stage 2 hinges on this and the framework
   cannot answer it: whether a user-facing grid may be a few seconds behind is the consumer's
   product decision, not the framework's. If the answer is no for any consumer, the default must be
   the primary and the reader must be opt-in per lens.
3. **Whether the sync-aware lens path may ever use a reader.** The recommendation is no, because the
   fence it advertises would stop working silently (stage 4). Making it yes requires a lag check and
   a documented bound.
4. **Whether the reader is a framework concern at all.** The consumer already owns its own read
   paths, and the documented position hands them the replica. An alternative that costs the
   framework nothing is to document that `-readonly` is a consumer key, state plainly that Whizbang
   never reads it, and close the question.
5. **What to do with the four incidental findings above.** Each is a real defect and none is in this
   audit's scope.
