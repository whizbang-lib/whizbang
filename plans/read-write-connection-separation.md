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

### The concrete read-after-write a user would actually see

Everything above is a framework-internal read. This one is the user-visible case, and it is the
strongest argument for the read-your-writes half of mechanism 3.

A common refresh pattern, which the framework supplies the pieces for
(`src/Whizbang.Core/Notifications/AppSignals/IAppSignalChannel.cs:11`), runs: a mutation, then the
event, then the perspective apply, then a tagged notification pushed to the client, then the client
re-queries. That final re-query is an **API-origin lens read arriving immediately after a write**,
which is exactly the read the design would route to the reader by default. And the notification is
emitted *because* the perspective was applied on the primary, so the client is being told, with
authority, that new data exists.

If that re-query lands on a reader that has not caught up, the client is told to refresh and is then
shown the old data. **That is worse than not refreshing at all**: the user asked for the new state,
was told it was ready, and got the previous one, with no error anywhere. A UI that had simply not
refreshed would at least be consistently stale rather than confidently wrong, and a user who has just
acted is the least willing audience for a stale answer.

Note the shape: the notification is what makes this case both likely and severe. Likely, because the
notification deliberately triggers the read at the worst possible moment, microseconds after the
write. Severe, because the notification is a promise the stale read breaks.

### Making the notification-triggered read correct

Six options, ordered, with the first as the recommendation.

**1. Mark the post-notification read fresh-required and route that single read to the primary.**
Recommended. It is deterministic rather than probabilistic, it needs no lag measurement at all, and
the reads that pay the primary's cost are exactly the ones that need it: a client that was just told
to refresh. It is also the smallest possible change, because it is mechanism 1's call-site override
(stage 3) used in the tightening direction, which the design already requires to exist. Nothing new
is needed beyond the override itself.

**2. Carry the commit position in the notification and let the follow-up read use it**: wait briefly
for the reader to reach that position, fall back to the primary, or answer honestly that it is not at
that version yet. Stronger than option 1 in that it can still serve the read from the reader, and
correspondingly more machinery. This is not invention; it is the standard shape of causal
consistency, and the precedents are worth naming so it is not argued from first principles:
MongoDB's `afterClusterTime` token, Vitess tracking replication position per shard, Aurora's session
consistency mode, and explicit read timestamps in Spanner and CockroachDB.

**Does the framework already have the value that would be carried? Yes, from `commit_sequence`
stamping, and the comparison pair already exists as a designed pair.** Specifically:

- `PerspectiveMetadata.CommitSequence` (`src/Whizbang.Core/Lenses/PerspectiveMetadata.cs:70`) sits on
  the row the lens reads, and is in the `Lenses` namespace, so it is already part of the lens-facing
  surface. A read can therefore check the position on the row it is about to return, with no separate
  lag query.
- `MessageEnvelope.LocalCommitSequence` (`src/Whizbang.Core/Observability/MessageEnvelope.cs:127-134`)
  is documented as the value "the perspective runner's idempotency filter compares against
  `Lenses.PerspectiveMetadata.CommitSequence`". The comparison this option needs is already
  implemented, for a different purpose, on the same two fields.
- It is the right token rather than a convenient one: `commit_sequence` is used in preference to the
  event id precisely because "UUIDv7 event_ids can invert under concurrent emission while
  commit_sequence cannot" (`src/Whizbang.Core/Lenses/PerspectiveMetadata.cs:65-66`).
- Availability is not a problem on this path. `LocalCommitSequence` is null for rows the stamper has
  not reached, but `get_stream_events` refuses unstamped rows
  (`src/Whizbang.Data.Postgres/Migrations/058_GetStreamEventsUnstampedGate.sql:79,113`), so an apply
  can only have run on a stamped row, and the value is therefore non-null exactly when a
  post-apply notification would be emitted.

**The existing sync awaiter does not give the value.** `SyncResult` carries an outcome, counts and an
elapsed time, and no position at all
(`src/Whizbang.Core/Perspectives/Sync/SyncResult.cs:14-20`), and `IsCaughtUpAsync` returns a bare
`bool` (`src/Whizbang.Core/Perspectives/Sync/IPerspectiveSyncAwaiter.cs:48`). It answers "has the
apply completed", which is the timing, not "to what position", which is the token. So the awaiter is
the wrong source and the stamp is the right one.

The one genuinely missing piece is transport: `LocalCommitSequence` is `[JsonIgnore]` and documented
as "NOT serialized, derived from the local row at read time"
(`src/Whizbang.Core/Observability/MessageEnvelope.cs:133-134`). Carrying it to a client means adding
it to the notification payload deliberately, not flipping a serialization flag on an envelope field
that is intentionally local.

**3. Push the changed projection with the notification** so no re-read happens at all. Removes the
race by removing the read. Two costs to name: the payload grows with the projection rather than
staying a tag, and the push has to be authorized per subscriber, because a notification fan-out that
carries data has to answer the same scope and tenant questions the lens query answers with its scope
filters (`src/Whizbang.Data.EFCore.Postgres/EFCorePostgresLensQuery.cs:113-144`). A tag needs no such
check; a payload does.

**4. Optimistic client-side update with reconciliation.** Workable, and a client concern rather than a
framework one. Recorded so the list is complete, not as something the framework would supply.

**5. A per-session write window**: for a few seconds after a session writes, route that session's
reads to the primary. Worth having as a net for the reads that *no* notification triggered, which
options 1 and 2 do not cover. This is a well-trodden pattern rather than a new one: Rails ships it as
`ActiveRecord::Middleware::DatabaseSelector` with a configurable delay. Note that it is the rejected
notification delay applied to the read side instead, and that is exactly why it is acceptable: it
costs no latency in the common case, and when the window is wrong it degrades to a correct read on
the primary rather than a stale one on the reader.

**6. Hold each tagged notification until the reader has passed the commit that wrote the perspective
row.** The most complete of the options, and the most machinery. Stamp the held notification with the
position at commit, maintain the reader's replayed position, and release held notifications in
position order once the reader has passed them. It replaces guessing a delay with observing a
position, so it is deterministic where the rejected fixed delay is probabilistic, and the client's
follow-up read needs no token and no per-query routing decision at all, because **the notification
itself is the barrier**. Six things decide whether it is buildable as described.

**(a) The barrier is the perspective-row commit, not the event append.** This is a correctness point,
not a preference. The read a tagged notification triggers is a read of a *perspective*, and the
append and the apply commit in **different transactions**, with the apply the later one. A reader
that has replayed past the append has therefore not necessarily replayed the perspective row the
client is about to read, so stamping the held notification with an event-store position would release
it too early. The barrier must be the position of the commit that wrote the perspective row.

Capturing it there is both correct and free, because the hook already fires after that commit.
`_applyDrainModePerspectiveCompletionAsync` runs "immediately after a successful
`RunWithEventsAsync`" (`src/Whizbang.Core/Workers/PerspectiveWorker.cs:2737-2743`), and the lifecycle
receptor fans and the completion signal are inside it
(`src/Whizbang.Core/Workers/PerspectiveWorker.cs:2788-2798`), with tag hooks registered at the
`PostAllPerspectives` stage (`src/Whizbang.Core/Tags/MessageTagRegistry.cs:27`), which fires once
after **all** perspectives for an event complete
(`src/Whizbang.Core/Workers/PerspectiveWorker.cs:316-319`). So the position can be read at the moment
the notification is raised, with no extra round trip and no new ordering to establish.

**(b) No watermark per perspective is needed, and this is the question worth answering explicitly.**
The physical replay position is a **single global barrier**: if the reader has replayed past that
commit, it has replayed that perspective row and every other table, because physical replay is
ordered and total. One value per reader therefore covers every tag. Keep the two axes separate in
any implementation, because conflating them is exactly what makes a per-perspective watermark look
necessary:

- the **barrier** is one physical replay position per reader;
- the **routing policy** is per lens, which is stage 3's declared default.

**(c) A purely logical watermark stalls, so it is the fallback and not the mechanism.** Reading the
per-stream, per-perspective cursor rows from the reader would work in an environment where the
engine replay position is not readable, and it carries two costs the physical position does not: it
**only advances when events are written**, so in a quiet period the reader is fully current while the
watermark never reaches the target and held notifications would never release at all; and it needs
per-perspective bookkeeping that the single physical value gives for free. If it must be used,
release on **either** condition, position reached or replay timestamp later than the commit time, so
an idle system cannot deadlock the queue.

**(d) A standby cannot report its own position over the notification channel, so this must poll.**
PostgreSQL refuses `NOTIFY` during recovery and the notification queue is not replicated, so a
replica can neither originate a notice about its own progress nor relay one. The mechanism therefore
has to **poll the reader connection for one value**. Frame that cost accurately, because it sounds
worse than it is: one scalar, on one connection, per service, debounced, which is the same shape as
the two statistics collectors the framework already ships
(`src/Whizbang.Core/Observability/TableStatisticsCollector.cs`,
`src/Whizbang.Core/Observability/NotifyDebounceStatsCollector.cs`). It is **not** the thing the
standing preference against polling is about: that concerns a *client* polling for *data*, repeatedly
and per user. This is one server-side gauge read on a cadence, and stage 6 needs it anyway.

**Permissions confirmed**, since the whole mechanism depends on an ordinary role being able to read
the position. On PostgreSQL 17.7, `pg_is_in_recovery`, `pg_last_wal_replay_lsn`,
`pg_last_xact_replay_timestamp`, `pg_current_wal_lsn` and `pg_wal_lsn_diff` all carry the default
catalog ACL, meaning `EXECUTE` to `PUBLIC`, and a role created `NOSUPERUSER` calls all of them
successfully with no grant. Two notes from that check: on a primary the two replay functions return
`NULL`, which doubles as the primary-detection stage 8 needs; and the `NOTIFY`-during-recovery
refusal above is documented engine behavior that this audit could not exercise locally, because it
needs a real standby, so it is the one item to confirm against the deployed topology rather than
taken on trust.

**(e) Bounds it must carry.** Without all three it is a new failure mode rather than a fix:

- a **hold deadline**, after which the notification is released anyway and the read it triggers is
  routed to the primary instead. A held notification that never releases is a worse outcome than a
  stale read, because the client is never told anything at all;
- a **cap on held notifications**, with a defined overflow behavior: collapse to a single coarse
  "something changed, refresh" rather than dropping silently. Silent dropping is the failure class
  this whole document is about;
- **release in position order**, so client-side ordering assumptions continue to hold.

**(f) Several replicas behind one reader endpoint break it.** This is the structural limit. A reader
endpoint that load-balances across replicas gives no way to know which one will serve the client's
follow-up query, so releasing correctly would have to wait for the **slowest** replica, which imposes
the worst replica's lag on every notification. That is the point at which option 2 is simply the
better mechanism: carrying the position to the query and letting the query wait **binds the wait to
the connection that actually serves the read**, which is the only binding that is correct under a
fan-out endpoint.

**The comparison the decision should turn on.** Option 1 achieves the same user-visible correctness
with none of this machinery: one read, on the primary, deliberately. Option 6's only advantage over
it is that every read stays on the reader. So option 6 is justified if the notification-triggered
share of lens reads is large, and is over-engineering if that share is small. **Measure the share
before building it.**

Nothing present yields that measurement. There is no lens-query instrument at all: the nearest names
are `whizbang.event_store.query.duration`, which is the event store rather than the lens, and
`whizbang.perspective.read_failures`, which counts failures. The numerator has a usable proxy in
`whizbang.lifecycle_coordinator.perspective_completions_signaled`, but there is no denominator to
divide it by. Stage 7's per-operation naming is what would produce both sides, which is a reason to
sequence stage 7 before any decision on option 6 rather than after it.

**On polling after the notification.** Re-querying on a timer until the data appears does work, and it
should still be rejected. It conflicts with the standing engineering preference against polling, and
more to the point it spends repeated round trips to paper over a read that could simply have been
correct the first time. Option 1 is the answer to that impulse: one read, on the primary, deliberately.

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
| 3 | Mechanism 1: declared intent, as a per-lens default plus a call-site override in both directions | medium | `Not started` |
| 4 | Mechanism 2: the ambient write-scope guard, plus the deny-by-default allow-list as a registry test and a connection-role assertion at the coordinator's connection acquisition | medium | `Not started` |
| 5 | Mechanism 3: bounded staleness, as a lag budget with a primary fallback, plus the read-your-writes option | medium | `Not started` |
| 6 | Replication-lag measurement (time and byte lag) as a provider, collector, meter gauges, and a health source. **Stage 5 has no input without this** | medium | `Not started` |
| 7 | Connection observability: role-tagged pool gauges and an operation name on every database call | medium | `Not started` |
| 7a | Prerequisite tidying: migrate the twelve remaining `application_name` literals onto the constant that already exists | small | `Not started` |
| 8 | Startup detection and a one-line Information log when the reader key points at the primary | small | `Not started` |

Stages 1 to 5 and 8 are gated on the first item in "Not decided" below. Stages 6, 7 and 7a stand on
their own merits: the framework cannot measure replication lag or report connection saturation today
regardless of whether a reader is ever adopted, so they are worth doing even if the answer to the
gating question is no.

### How much is actually on the table, and what the unit of the decision is

**The unit of the decision is the call origin of a lens query, not the role of the service that
hosts it.** That is the reframing that makes the rest of this tractable, and it follows from what the
lens actually is: a read-only design. Its documentation says so, its interface exposes no write, and
this audit found that the implementation matches the claim.

A lens query issued from the API layer, where the application is explicitly reading, has no write in
scope. That is the natural default for the reader connection. A lens query reached from inside a
receptor, a perspective apply, or an open transaction is the exception, and it is exactly what the
ambient write-scope guard keys on. So the design is: **default the API-origin lens read surface to
the reader, and let the guard rather than the developer catch the in-process exception.**

Framing it by call origin rather than by service role matters because the same lens type is reached
both ways inside one host, so no service-level switch can be correct. It also puts the burden in the
right place: a developer declaring a read model's default does not have to reason about every call
site that might ever reach it, because the guard is what makes an aggressive default safe.

#### Why that surface is cheap to move

Three findings from this audit, and together they say the API-origin lens surface needs a different
data source and nothing else:

- **It already tracks nothing.** Every lens read is `AsNoTracking()`
  (`src/Whizbang.Data.EFCore.Postgres/EFCorePostgresLensQuery.cs:161,185`;
  `src/Whizbang.Data.EFCore.Postgres/EFCoreFilterableLensQuery.cs:82,174,198`;
  `src/Whizbang.Data.EFCore.Postgres/MultiModelScopedAccess.cs:23`), and no context anywhere sets
  `QueryTrackingBehavior`. There is no change-tracking work to do.
- **It already resolves through a factory that opens a fresh scope.** Every generated lens
  registration takes `IDbContextFactory<TDbContext>` and calls `CreateDbContext()`
  (`src/Whizbang.Data.EFCore.Postgres.Generators/Templates/Snippets/EFCoreSnippets.cs:329-337`;
  `src/Whizbang.Data.EFCore.Postgres/ScopedDbContextFactory.cs:52-57`), so it is already decoupled
  from the request context the writers use. That one call is the whole seam.
- **Its raw-SQL escape hatch is dead code**, so the read surface is genuinely closed to `IQueryable`
  and `GetByIdAsync`. All three escape-hatch methods require the internal `IDbContextAccessor` and no
  production lens implements it (`src/Whizbang.Data.EFCore.Postgres/LensQueryConnectionExtensions.cs:77,110,140,171`).
  Nothing can reach around the seam for a connection.

The one exception to guard against is the legacy `RegisterPerspectiveModel` shape, which registers
`ILensQuery<TModel>` scoped over the *request* `DbContext`
(`src/Whizbang.Data.EFCore.Postgres/EFCoreInfrastructureRegistration.cs:53-54`). No generator emits
it, but a host wiring lenses that way has put its lens reads on the write context, and must not be
moved.

#### The framework-general list

Independently of call origin, four paths carry essentially all of the value the framework itself can
move, and three of them are cheap:

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
buys nothing measurable. So the honest summary is: **one path is worth real money, three are worth
a measurable amount, and a long tail is worth nothing.** The one worth real money is the API-origin
lens surface, which is why the whole design above is built around its call origin rather than around
a connection-string key. Note also that the framework already spent
optimization effort on this problem in a different direction, by decomposing the poll and bounding
it by the batch it returns rather than the backlog it scans
(`ai-docs/load-under-bulk-import.md:40-62`), and by pinning the hot worker connections off the pooler
(`src/Whizbang.Core/Workers/WhizbangPinnedPoolOptions.cs:10-16`); a reader is additive to those, not
a substitute for them.

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

### The three mechanisms, and the one that is deliberately absent

Routing is decided by exactly three layered mechanisms, in this order. Each is a stage below.

1. **Declared intent** (stage 3): a per-lens default, plus a call-site override in both directions.
2. **The ambient write-scope guard** (stage 4): the primary is forced whenever the query runs inside
   a write scope, so a lens whose default is the reader cannot be silently wrong when it is called
   from inside a transaction, a receptor, or a perspective apply.
3. **Bounded staleness** (stage 5): measured replication lag against a budget, falling back to the
   primary while the budget is exceeded, plus a read-your-writes option for callers that need one.

#### Rejected, so nobody proposes it again: a recency cache of written streams

A tempting fourth mechanism is to keep a set of recently written stream ids and send reads for those
to the primary while everything else goes to the reader. **Do not build this.** Two independent
holes, either of which is disqualifying:

- **It would be fed by a best-effort signal.** The doorbell and signal-bus path degrades to a polling
  fallback, and it does so under exactly the conditions that make replica lag worst. This is not a
  hypothetical: the framework models it as a first-class state and reports it as a health component.
  A transport that cannot deliver its own loopback probe within the probe timeout marks the wire
  route failed (`src/Whizbang.Core/Signals/SignalBusProbeSignal.cs:4-6`;
  `src/Whizbang.Core/Signals/SignalBusOptions.cs:4-10`), and the health source then reports that the
  bus still serves on polling fallback while every hop pays the poll interval
  (`src/Whizbang.Core/Health/SignalBusHealthSource.cs:10-12`). Instances do log exactly that under
  heavy load. A routing decision built on a signal that can silently stop arriving would send reads
  to a stale reader precisely when it is most stale, and the failure would be invisible at the call
  site, which is the one property this whole document argues against.
- **The cache is per process.** One instance does not see another instance's writes, so a read that
  needs the primary because a *peer* just wrote is routed to the reader anyway. That is not a tuning
  gap; it is the common case in a multi-instance fleet.

Staleness is bounded by measuring it (mechanism 3), never by guessing which rows are fresh.

#### Also rejected: delaying a notification by the measured lag

The other tempting shortcut, and it will be proposed again because it sounds cheap: when a
perspective apply triggers a notification that makes a client re-read, delay that notification by
the measured replication lag so the follow-up read is likely to find the reader caught up. **Do not
build this either.** Three reasons, and the third is the one that decides it:

- **Lag is a distribution, not a value.** Delaying by the mean is wrong about half the time, which
  is a coin flip on correctness. Delaying by the tail imposes the tail on every notification, so the
  common case pays for the rare one.
- **The measured figure is about a different transaction.** A replay position describes where the
  reader has got to on some other commit; it says nothing about when *this* commit will land there.
  Timing a delay off it is using a number that does not answer the question.
- **It degrades worst at the worst moment.** The delay grows exactly when lag spikes, which is under
  bulk load, so the user-visible refresh latency is worst precisely when the system is already
  struggling. It trades latency in the common case for a partial reduction of a failure in the rare
  one, and it never fully removes the failure.

The same idea applied to the **read** side instead is acceptable, and appears below as option 5,
because it costs no latency in the common case and its failure mode is a correct read rather than a
stale one. The asymmetry is the whole point: delay the notification and everyone waits; route the
read and only the affected reads pay.

### Stage 3: declared intent, per lens and per call site

Status: `Not started`.

Routing is a declaration, not an inference. Two surfaces, and both directions must exist:

- **A per-lens default.** The natural home is `WhizbangPerspectiveAttribute`
  (`src/Whizbang.Core/Perspectives/WhizbangPerspectiveAttribute.cs:84`), which already declares the
  read model a lens reads over, so the default travels with the model rather than with the call and
  the generator can carry it into the emitted registration. A model backing a user-facing grid can
  declare the reader; one read inside a decision declares the primary. The framework default must be
  the primary, because the safe direction is the one that costs latency rather than correctness.
  Note that a consumer's lens is an ordinary type taking `ILensQuery<TModel>` in its constructor
  (for example `samples/ECommerce/ECommerce.BFF.API/Lenses/OrderLens.cs:10`), so there is no lens
  type for the framework to attribute; the model is the only declaration site it owns.
- **A call-site override in both directions.** Both, not one. A reader-default lens needs a way to
  demand the primary for the one query that follows a write the user just made, and a
  primary-default lens needs a way to opt a heavy reporting query onto the reader without changing
  the lens for every other caller. An override that only relaxes would make the safe default
  unusable; an override that only tightens would make the fast default unreachable.

The override belongs on the scoped-access seam the lens API already funnels every read through
(`src/Whizbang.Core/Lenses/IScopedLensAccess.cs:18,27`), so it composes with the scope selection
that is already expressed there rather than becoming a second, parallel fluent chain.

**The tightening direction is load-bearing, not symmetry for its own sake.** It is what option 1 of
"Making the notification-triggered read correct" is built from: a client that was just told to
refresh marks its follow-up read fresh-required, and that single read goes to the primary. That is the
recommended answer to the one user-visible read-after-write in this document, and it needs nothing
beyond this override, which is the strongest reason to ship both directions in the same stage rather
than deferring one.

### Stage 4: the ambient write-scope guard, and the allow-list

Status: `Not started`.

Declared intent is not enough on its own, because the same lens is called from both a controller and
a receptor. The guard closes that: **whenever a read runs inside a write scope, the primary is
forced, whatever the declaration says.** Three conditions qualify, and all three are observable in
process without asking the database:

- an open transaction on the ambient `DbContext`;
- executing inside a receptor;
- executing inside a perspective apply.

Forcing rather than refusing is the right response. A refusal would turn a correct-but-slow
composition into an outage, and the composition is legitimate: reading a lens inside a receptor is a
documented pattern. Log the force at debug with the lens name so a consumer can see which of its
reader-default lenses never actually reach the reader, and fix the composition if it cares.

The guard also makes the per-lens default safe to set aggressively, which is what makes mechanism 1
worth having at all.

**Call origin is the reason this exists**, not an edge case for it. The same lens type is reached
from the API layer and from an apply inside one host, so the difference between a safe read and an
unsafe one is where the call came from, not which service it is in (see "How much is actually on the
table"). The receptor and apply conditions above are therefore load-bearing rather than defensive: a
guard covering only the open-transaction case would miss the two origins that most often reach a
lens without one.

**Beyond the guard, an allow-list decides which framework-internal components may hold a reader at
all.** "Must be the primary" is a property of the component, not of the query, and there are three
candidate shapes for enforcing it. The recommendation is the second, with the third as a cheap
backstop.

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

### Stage 5: bounded staleness, and why the existing fence does not cover it

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

So bounded staleness has to be measured, and it has two halves.

**A lag budget, with the primary as the fallback.** Each reader-eligible read carries a maximum
tolerable lag, defaulting from a framework-wide setting and overridable per lens. The router
compares it against the lag measurement from stage 6. While the measured lag is inside the budget,
the read goes to the reader. While it is outside, or while the measurement is missing or stale, the
read goes to the primary. Note the direction carefully: **an unavailable measurement must route to
the primary, not to the reader.** That is the same asymmetry the codebase already applies to its
settledness gates, where an unmeasured answer is never treated as the permissive one
(`src/Whizbang.Core/Messaging/IWorkCoordinator.cs:184-187`), and it is the one place a reader design
most easily gets it backwards, because "no lag reading" reads like "no lag".

The fallback must also be observable, or it becomes the silent-failure class this document is about.
A counter of reads that fell back, attributed by lens, is the minimum: a consumer whose reader is
permanently over budget should see that its replica is buying nothing, not merely enjoy correct
results at primary cost.

**A read-your-writes option for callers that need one.** For the caller that has just written and
must see its own write, a lag budget is the wrong instrument, because it bounds staleness in general
rather than relative to a specific write. The mechanism is to capture the write position at commit
and then either wait for the reader to reach that position or use the primary. Two notes on fitting
it to this codebase:

- The framework already has a write position with the right properties. `commit_sequence` is
  assigned in commit order and is the value the whole read side already gates on
  (`src/Whizbang.Data.Postgres/Migrations/058_GetStreamEventsUnstampedGate.sql:79,113`), so a caller
  that captured the `commit_sequence` of its write has a token a reader can be compared against.
  That reuses an existing fence rather than adding a second notion of progress.
- The waiting form must be bounded and must fall back to the primary on expiry, never return a stale
  answer. This is the same shape as the existing perspective sync awaiter, which already has a
  timeout and an explicit "proceed with eventual consistency" branch
  (`src/Whizbang.Core/Lenses/ISyncAwareLensQuery.cs:44-50`), so the precedent for the timeout
  semantics exists; what it lacks is any awareness of which endpoint the following query runs on.

### Stage 6: measuring replication lag

Status: `Not started`.

**The framework cannot measure this today.** Nothing queries `pg_is_in_recovery`,
`pg_last_xact_replay_timestamp`, `pg_last_wal_replay_lsn`, or `pg_current_wal_lsn` anywhere; the
repo-wide search for any of them returns nothing. Mechanism 3 has no input until this exists, which
is why it is a stage of its own rather than a detail of stage 5.

**What the measurement is for, and the one thing it must never be used for.** Two purposes only:

1. It feeds the **bounded-staleness circuit breaker** in stage 5. The reader is used while the lag is
   inside the budget and the primary is used while it is outside, so the measurement gates a binary
   routing decision rather than a duration.
2. It is an **observability signal**, so an operator can see that a configured reader is permanently
   over budget and is therefore buying nothing.

**It must not be used to time a delay.** Neither to delay a notification, which is rejected above,
nor to sleep before a read in the hope that the reader will have caught up. A lag figure supports the
question "is the reader currently fit to serve", which is a threshold comparison against a
distribution's current position. It does not support "how long until this particular commit arrives",
which is what a delay would need and what the figure cannot answer. Reviewers should treat any use of
this value as a sleep duration as a defect.

Both useful measurements are available from the reader endpoint itself, and both are worth having
because they answer different questions:

- **Time lag**, how far behind in wall-clock terms. This is what a product decision is expressed in
  ("the grid may be a few seconds behind"), so it is the one a lag budget should be configured
  against. Its known weakness is that on an idle primary it grows without anything being wrong,
  which the collector must not report as a fault.
- **Byte lag**, how far behind in WAL terms. This is the one that is meaningful when the primary is
  idle and the one that shows a replica falling behind under write pressure before the time lag
  becomes alarming.

The natural home is the pattern the framework already uses twice for exactly this shape of periodic
measurement: a provider that owns a data source, a collector on a cadence that refreshes cached
values, and a meter whose gauges read the cache rather than querying
(`src/Whizbang.Data.EFCore.Postgres/PostgresTableStatisticsProvider.cs` with
`src/Whizbang.Core/Observability/TableStatisticsCollector.cs`; and
`src/Whizbang.Data.EFCore.Postgres/PostgresNotifyDebounceStatsProvider.cs` with
`src/Whizbang.Core/Observability/NotifyDebounceStatsCollector.cs`). Following it means the gauges cost
nothing per scrape, which matters because the lag reading is also on the routing path.

Alongside the gauges, a health-source component beside the existing ones
(`src/Whizbang.Core/Health/`, registered the way the event-store connectivity source is at
`src/Whizbang.Data.EFCore.Postgres/PostgresDriverExtensions.cs:215-220`). One constraint on its
severity: a lagging reader must **not** make the service unhealthy, because the fallback means reads
are still correct. Degraded is the honest state, and the reason has to say so, or a lag spike becomes
an outage that the fallback had already handled.

### Stage 7: connection observability, and the `application_name` constraint

Status: `Not started`.

A reader is a fourth connection role, and the framework currently cannot see the three it already
has. Of roughly 295 distinct `whizbang.*` instrument names, four are connection-adjacent and all are
narrow: the notification connection's state
(`whizbang.postgres.notifications.connection_state`) and three pinned-pool instruments
(`whizbang.workers.pinned_pool.borrow.duration`, `.borrow.timeouts`, `.connection_recycles`). **There
is no pool-utilization metric for the main data source at all**, so nothing reports saturation on the
connection every read and write actually uses. Adding a role without adding this would mean a
consumer could not tell which of two pools was exhausted.

Two proposals:

- **Role-tagged pool gauges.** In-use, idle, and waiting counts, each carrying a role attribute over
  `writer`, `reader`, `direct`, and `init`. One instrument per measure with a role attribute, not one
  instrument per role, so a dashboard sums or splits by role without knowing the role set in advance,
  and a fifth role later costs no new instrument.
- **An operation name on every database call.** The gauges say a pool is saturated; the operation name
  says by what. This is the difference between "the reader pool is full" and "the reader pool is full
  of one reporting query", and it is also what makes the fallback counter in stage 5 actionable.

**The constraint to be careful about.** Seventeen predicates across fourteen shipped migrations match
`pg_stat_activity.application_name` with **equality** against `'whizbang-' || instance_id`, and they
are load-bearing: they are the TCP-fresh half of instance liveness, used by the orphan-claim paths,
the ownership gate, and the `wh_live_instances` view (`src/Whizbang.Data.Postgres/Migrations/052_LiveInstancesView.sql:49`;
`024_ClaimOrphanedOutbox.sql:69`; `025_ClaimOrphanedInbox.sql:87`;
`027_ClaimOrphanedPerspectiveEvents.sql:72`; `059_GetStreamEventsOwnershipGate.sql:83`;
`072_EphemeralBodyOffload.sql:620`; `078_DropInlineBodyColumns.sql:567`;
`115_TagBoundCoalescing.sql:685`; `138_BoundedInboxAcquisitionIndex.sql:124`;
`139_PerspectiveFailureCounter.sql:136`; `140_LockFreeDoorbellProbes.sql:889`;
`145_BoundedAcquisitionRewrite.sql:97`; `148_ActiveStreamLeases.sql:73,436,625`;
`150_BucketAwareClaim.sql:122,1230`).

**Recommendation: preserve the exact value on the notification connection, and do not move the
predicates to a prefix match.** Three reasons, and the first is the one that settles it:

1. **`ApplicationName` is set on the notification connections and nowhere else**
   (`src/Whizbang.Data.Postgres/Notifications/PgSharedNotifyConnection.cs:395`;
   `src/Whizbang.Data.Postgres/Notifications/PostgresNotificationsServiceCollectionExtensions.cs:243,362`,
   all through the one helper `PgSharedNotifyConnection.ComputeApplicationName` at `:110`). The
   pooled data source sets none. So naming the writer, reader and init connections cannot collide
   with those predicates at all, as long as the notification connection keeps emitting exactly
   `whizbang-{instanceId:D}`. The change is additive and needs no migration.
2. **A prefix match would break the signal's purpose.** The predicate exists because the LISTEN
   connection is TCP-fresh, unlike the heartbeat column, so its presence is a sub-second liveness
   signal (`052_LiveInstancesView.sql:52`). Widening it to a prefix would let a pod's *pooled*
   connections satisfy it, so an instance whose LISTEN connection had died would still read as alive
   on the strength of a pooled connection. That converts a precise liveness signal into a vague one,
   which is the opposite of the fix.
3. **Seventeen predicates is a large blast radius for no benefit**, and the prefix already has a
   single home to change if it ever must: `src/Whizbang.Data.Postgres/Migrations/constants.txt:16`
   defines `__INSTANCE_APPLICATION_NAME_PREFIX__` as `'whizbang-'`, and the two newest migrations use
   the placeholder while the twelve older ones still carry the literal.

So the one piece of tidying worth doing, independently of any of this, is to migrate those twelve
remaining literals onto the placeholder that already exists, so the prefix has one definition rather
than thirteen. That is a mechanical change with a clear invariant to test, and it is the prerequisite
for ever touching the prefix at all.

### Stage 8: migration path, and a reader that is really the primary

Status: `Not started`.

Migration is a no-op by construction: with the stage-1 fallback, a consumer that configures nothing
keeps today's behavior exactly, and a consumer that configures `-readonly` moves only the paths
stage 4's allow-list names.

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
2. **Whether the lens path may be stale.** Everything in stages 2 to 5 hinges on this and the framework
   cannot answer it: whether a user-facing grid may be a few seconds behind is the consumer's
   product decision, not the framework's. If the answer is no for any consumer, the default must be
   the primary and the reader must be opt-in per lens. Note that this is answered per read model
   rather than once, which is why stage 3 puts the default on the model and not on a global switch.
3. **Whether an API-origin lens read may default to the reader, or must opt in.** The design
   recommends defaulting it, on the grounds that the guard makes an aggressive default safe and that
   a default nobody sets is a feature nobody gets. The opposite choice is defensible and costs only
   adoption, so it is the owner's.
4. **Whether the sync-aware lens path may ever use a reader.** The recommendation is no by default:
   the fence it advertises would stop working silently (stage 5), so it should stay on the primary
   unless the caller opts into the read-your-writes form that compares the reader against a captured
   write position. Whether to offer that form on the sync-aware seam at all, or to keep the two
   mechanisms separate, is the decision.
5. **The lag budget's default value**, and whether it is a single framework-wide number or must be
   per lens from the start. The framework can supply a conservative default; what a product will
   tolerate is not something it can guess.
6. **Whether the reader is a framework concern at all.** The consumer already owns its own read
   paths, and the documented position hands them the replica. An alternative that costs the
   framework nothing is to document that `-readonly` is a consumer key, state plainly that Whizbang
   never reads it, and close the question. Note that stages 6, 7 and 7a survive this answer: lag
   measurement and connection observability are gaps regardless.
7. **What to do with the four incidental findings above.** Each is a real defect and none is in this
   audit's scope.

### Already decided, recorded so it is not reopened

- **A recency cache of written streams is rejected**, on the two grounds given in the design: it
  would be fed by a signal that degrades to polling under exactly the load that worsens lag, and a
  per-process cache cannot see a peer's writes. Bound staleness by measuring it, never by guessing
  which rows are fresh.
- **Delaying a notification by the measured lag is rejected**: lag is a distribution rather than a
  value, the figure describes another transaction's replay position, and the delay grows exactly
  when lag spikes. The same idea on the read side (a per-session write window) is acceptable,
  because its failure mode is a correct read rather than a stale one.
- **The lag measurement feeds a threshold and an observability signal, never a sleep duration.**
- **A held-notification barrier, if built, is keyed on the commit that wrote the perspective row**
  and not on the event append, because those commit in different transactions and the apply is the
  later one. One physical replay position per reader is the whole barrier; no per-perspective
  watermark is needed.
- **The unit of the routing decision is a lens query's call origin**, not the role of the service
  hosting it, so an API-origin read is the reader's natural default and the guard catches the
  in-process exception.
- **Routing is decided by three layered mechanisms and no others**: declared intent, the ambient
  write-scope guard, and bounded staleness.
- **`application_name` keeps its exact value on the notification connection**, and the seventeen
  equality predicates across fourteen migrations stay equality predicates (stage 7).
