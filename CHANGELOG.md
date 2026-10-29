# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

> The `[Unreleased]` section below was reconstructed from the merged pull-request history
> (the early development history was squashed into the foundation commit), so it captures the
> ~6 months of pre-1.0 work between the alpha and today.

## [Unreleased]

### Added

#### Storage
- **The event store's `created_at` gets a BRIN index; the other BRIN candidates were measured and declined (#1745).**
  `pg_stats.correlation` was measured with each candidate seeded in its production write order
  (`BrinIndexCandidatesTests`). `wh_event_store.created_at` stays near 1 (0.98) and the startup orphaned-lifecycle
  reconciliation filters it by a thirty-minute range that no index reached, so the query read the whole store;
  migration 258 adds `idx_event_store_created_at_brin` (`pages_per_range = 128`) and the query now reads only the
  ranges holding the window. `commit_sequence` also correlates near 1, but its readers need its order, which only its
  existing B-tree gives. `wh_log.logged_at`, `wh_lifecycle_completions.completed_at` and
  `wh_message_deduplication.first_seen_at` measure 1.0 while they only grow and 0.60, 0.27 and 0.81 after one retention
  pass at their default retention (freed oldest pages take the newest rows), so they keep their B-trees. No index is
  dropped.
- **Bloated indexes on the queue tables that churn to empty are rebuilt with `REINDEX INDEX CONCURRENTLY` (#1746).**
  A B-tree never returns its pages to the file, so after a burst an index on a drained queue keeps its peak size. The
  new `queue-index-reindex` maintenance step surveys the B-tree indexes of `wh_outbox`, `wh_inbox`, `wh_inbox_state`,
  `wh_perspective_events`, `wh_active_streams` and `wh_doorbell_queue` from the catalog (size against the size a fresh
  index over the same rows needs; method `estimate`), drops every invalid `_ccnew`/`_ccold` leftover of an interrupted
  rebuild with `DROP INDEX CONCURRENTLY`, and then rebuilds at most one index per cycle: of the valid indexes more than
  `Whizbang:Workers:Maintenance:ReindexBloatThreshold` times their expected size (default `3.0`, `0` turns it off, any
  other value must be finite and above 1) and at least 1 MiB, the one with the most bytes to give back. It runs once per
  fleet through a claim keyed by a window of half the maintenance interval, does nothing while an index build is running
  on a queue table, and runs on the schema-initialization connection when configured (otherwise only for a table the
  application role owns). EF Core driver. New API: `MaintenanceWorkerOptions.ReindexBloatThreshold`,
  `QueueIndexReindexStep`, `QueueIndexBloat`, `QueueIndexNames`, and `IWorkCoordinator.GetQueueIndexBloatAsync`,
  `DropInvalidQueueIndexAsync` and `ReindexQueueIndexAsync`.
- **A Grafana dashboard for storage, kept in step with the meters by a drift test (#1748).**
  `dashboards/grafana/whizbang-storage.json` charts table size and its heap, TOAST and index split, growth per day
  (`deriv()` and `delta()` over the gauges), bytes per row, the bloat ratio by `method` with the rewrite threshold
  line, the tables measured for a rewrite, maintenance rows and duration by task, unused index bytes, bytes per event
  type by part with the metadata share, Postgres capability state and queue depths. The data source, service (`job`),
  database and rewrite threshold are variables; no environment is named. `StorageDashboardTests` reads every
  instrument the library's meters declare from the source, derives the names Prometheus exposes them by, and fails
  when a panel or variable query names any other `whizbang_` metric, names a service or database literally, or sums
  a per-instance table gauge without first taking the max across instances.

- **The event-type bytes sample reports the event-store pointer rows and the inline outbox metadata (#1749).** The
  sample now reads three tables at the same share in one statement: `wh_event_body` for the payload and metadata,
  `wh_event_store` for the pointer row every event keeps (its whole stored width), and `wh_outbox` for the metadata
  outbox rows holding an event carry inline (commands are left out). Both `whizbang.event_store.sampled_bytes_per_event`
  and `whizbang.event_store.estimated_type_bytes` carry a `part` tag (`data`, `metadata`, `pointer`,
  `outbox_metadata`), so a sum by `event_type` is a type's whole footprint and a sum by `part` is where it sits; a part
  the sample read no rows of is not reported. Types are ranked and folded under `other` by every part together.
  Breaking (pre-1.0): `EventTypeBytes` is now one `EventPartBytes` (sampled rows, mean bytes, extrapolated total) per
  part (`Data`, `Metadata`, `PointerRow`, `OutboxMetadata`), and `EventStoreBytesParts` names the tag values.
- **Perspective tables follow their model's fillfactor and autovacuum settings at every start; a table can be managed by hand (#1747).**
  The seven parameters `[PerspectiveTableStorage]` governs (`fillfactor`, the four `autovacuum_vacuum_*` and the two
  `toast.autovacuum_vacuum_*`) are no longer set by the hashed perspective schema SQL, which runs only when a schema
  changes. The generated schema declares them in a `-- @whizbang:table-storage` marker line, and every start of every
  instance, on both drivers, reads every perspective table's `reloptions` in one catalog query and alters only a table
  that differs: a declared value is set, and a governed parameter the model does not declare (removed, negative, or no
  attribute) is reset. One `ALTER TABLE … SET (…), RESET (…)` per table, under a 5-second lock timeout (a busy table is
  skipped with a warning and retried at the next start); a set or reset fillfactor on a table with rows queues a
  rewrite. Migration 259 adds the `perspective_storage_manual_tables` setting in `wh_settings`: a comma-separated list
  of bare table names whose governed parameters are neither set nor reset; each start logs the listed tables once
  (Information) and warns of each listed table whose model declares values it ignores. Parameters the attribute cannot
  set are never touched; `toast_tuple_target` and compression are applied as before and not reset. **Upgrade note:**
  a hand-set value of one of those seven parameters on a perspective table is reset at the first start on this release,
  unless the table is listed in `perspective_storage_manual_tables` first. New API: `PerspectiveStorageParameters`,
  `TableStorageDeclaration`, `TableStorageChange`, `PerspectiveStorageReport` (Whizbang.Data.Postgres.Schema),
  `PerspectiveStorageReconcile` (EF Core), `ColumnStorageSql.StorageMarker` and `STORAGE_MARKER`. Removed (pre-1.0):
  `ColumnStorageSql.SetFillFactor`.
- **Bloat is measured with pgstattuple_approx before a rewrite is queued, and every ratio records its method (#1807).**
  The catalog estimate still runs every statistics pass for every table and feeds `whizbang.table.bloat_ratio`. While
  the `pgstattuple` capability is Ready on every probed database, the maintenance cycle measures each table the estimate
  puts over the rewrite threshold with `pgstattuple_approx` before it enters the rewrite queue: at or over the threshold
  it is queued with the measured ratio, under it it is not queued and the log gives both figures, and a measurement
  more than 25% from its estimate is logged with both. The queue is taken largest measured ratio first, and the
  re-measure after a rewrite (online rewrite and the boot `Rewrite` step) uses `pgstattuple_approx` too. The exact
  `pgstattuple` scan never runs automatically, and without pgstattuple nothing changes. Migration 254 adds
  `wh_table_rewrite_measurements` (the queued table's ratio, its `method` of `estimate`, `approx` or `exact`, and the
  estimate it confirmed; a queued table without a row reads as `estimate`), `wh_measure_table_bloat_approx` and
  `wh_request_measured_table_rewrite`, and `wh_clear_table_rewrite` removes the measurement with the request. The bloat
  gauge gains a `method` tag (`estimate`, and a second `approx` series per measured table). New API:
  `BloatMeasurementMethod`, `BloatMeasurement`, `IWorkCoordinator.MeasureTableBloatAsync` and
  `RequestMeasuredTableRewriteAsync`, `TableRewriteCandidate.Method` and `RewriteThreshold`, and
  `TableStatisticsMetrics.UpdateMeasuredTableBloat`.

- **Online table rewrite through pg_squeeze, driven by the maintenance cycle (#1743).** With
  `Whizbang:Workers:Maintenance:TableRewriteStrategy = PgSqueeze`, the `online-table-rewrite` maintenance step rewrites
  the tables queued in `pending_table_rewrites` through `squeeze.squeeze_table`, largest bloat ratio first, within
  `Whizbang:Workers:Maintenance:OnlineRewriteMaxBytesPerCycle` heap bytes per cycle (default 1 GiB; a table that does not
  fit waits for a later cycle, `0` rewrites nothing). It runs only when pg_squeeze is Ready on every probed database, and
  before each call it checks the extension and the right to call it, logical WAL below Postgres 19, a free replication
  slot, a primary key or replica identity index, a role with `REPLICATION` (pg_squeeze creates a replication slot as the
  caller) and table ownership, logging the first unmet one instead of calling. A request is cleared only when the
  re-measure shows the rewrite improved the table. The boot-time `Rewrite` step no longer runs `PgSqueeze`. Each
  rewrite candidate now carries its heap size (`TableRewriteCandidate.SizeBytes`), and `IWorkCoordinator` gains
  `SqueezeTableAsync`.

- **pg_squeeze's logical WAL prerequisite is probed (#1751).** The Postgres capability probe reads `wal_level` and the
  server version, and an extension that decodes the WAL (pg_squeeze) reads the new state `NeedsLogicalWalLevel`
  (capability state `NeedsServerChange`) below Postgres 19 when `wal_level` is not `logical`, instead of `Ready`. An
  explicit `TableRewriteStrategy = PgSqueeze` on such a server fails start with the fix
  `set wal_level = logical (and max_replication_slots to at least one more than the slots in use), then restart the server`.
  A capability descriptor can now give a fix per provider detail (`CapabilityDescriptor.DetailRemedies`), which the
  verdict and the report use before the state's own fix.

- **A built-in partition manager for time-ordered framework tables (#1611).** With
  `Whizbang:Workers:Maintenance:Partitioning:Enabled` (default `false`), the maintenance cycle converts `wh_log` once
  to weekly partitions by `logged_at` (primary key `(log_id, logged_at)`, every writer unchanged, gated on the
  `wh_log_partition_version` marker), creates partitions `:CreateAhead` (default 3) intervals ahead with a default
  partition, and drops partitions wholly past `log_retention_days` instead of deleting rows. Only the maintainer-duty
  holder changes partitions, and no lock is waited for: an expired partition is emptied while writers are on the
  table and detached later. Migration 226.
  table and detached later. Migration 226.

- **pg_partman can create the partitions (#1752).** `Whizbang:Workers:Maintenance:Partitioning:Strategy` (default
  `Auto`) picks who creates them: `Auto` uses pg_partman where it is already Ready on every probed database and
  otherwise keeps the built-in manager, saying why once (it never installs pg_partman); `BuiltIn` never uses it;
  `PgPartman` requires it (`[RequiresPostgresExtension("pg_partman")]`), so it is provisioned where allowed and start
  fails with the fix where the server cannot provide it. Under pg_partman each table is registered once with
  `create_parent` (premake = `CreateAhead`, from the first interval no partition covers, no default table of its own,
  automatic maintenance off, the version-7 encoder for a uuid key) and gets `run_maintenance` each cycle. A converted
  table's oldest partition is first re-bounded from `MINVALUE` to the smallest key, which pg_partman can read, in three
  committed steps that never scan under a lock writers wait on. pg_partman's retention stays off: the built-in manager
  still drops expired partitions, so unconsumed signals are carried first (#1758). pg_partman's WARNINGs are logged,
  and a partition the built-in pass had to create after pg_partman maintained the table is logged as a miss. Under
  `Auto` a failed pass falls back to the built-in manager for the cycle; under `PgPartman` it fails the cycle.
  Migration 246.

- **`wh_signals` and `wh_lifecycle_completions` are partitioned by the partition manager (#1646).** While
  partitioning is enabled, `wh_signals` converts once to daily partitions by `created_at` (primary key
  `(id, created_at)`; publishing and the tail are unchanged) and `wh_lifecycle_completions` to daily `event_id` ranges
  (event ids are UUIDv7, so a day is the range from its first id to the next day's; the key stays `event_id`). Their
  partitions are dropped past `SignalRetention` and `LifecycleCompletionRetentionDays` (`0` keeps every partition).
  The manager now accepts a time-ordered `uuid` key as well as a `timestamptz` one, and the conversion moves a row
  whose key is past the oldest partition's bound (a version-4 id, a clock running ahead) into the default partition
  instead of failing. Once a table is partitioned and maintained, its row sweep clears only the default partition;
  switched off, it sweeps the whole table again. Migration 233.

- **The applied-event ledger vacuums after a fixed number of changes (#1666).** Migration 234 sets scale factor 0
  with a threshold of 10000 for both dead rows and inserts on `wh_perspective_applied`, which takes a row for every
  completed apply and prunes it an hour later. A scale factor made its vacuum wait on a share of a window that grows
  with the traffic.

- **A perspective table can declare when autovacuum runs (#1584).** `[PerspectiveTableStorage]` gains
  `AutovacuumVacuumThreshold`, `AutovacuumVacuumScaleFactor`, `AutovacuumInsertThreshold`,
  `AutovacuumInsertScaleFactor`, `ToastAutovacuumVacuumThreshold` and `ToastAutovacuumVacuumScaleFactor`. The schema
  pass of both drivers sets each as a storage parameter where the catalog differs, so a restart takes no lock; a
  negative value (the default) keeps the server's setting.

- **A slim stored form for event metadata (#1586).** `EnvelopeMetadata.ToSlim()` keeps what readers take from a stored
  event (message id, ephemeral TTL, message timestamp, correlation, causation, stream id, and the scope folded over
  every hop) and drops the delivery-time detail. A hop without a service instance now reads as
  `ServiceInstanceInfo.Unknown` instead of failing, and the writer leaves an unknown instance out. Nothing stores the
  slim form yet; this lets readers accept it before any writer produces it.

- **Stored bytes by event type (#1590).** The `Whizbang.TableStatistics` meter gains
  `whizbang.event_store.sampled_bytes_per_event` (mean stored payload and metadata bytes per event, tag `part`) and
  `whizbang.event_store.estimated_type_bytes` (extrapolated total per type). One instance per window samples
  `EventTypeSamplePercent` of the event body table's pages every `EventTypeSampleInterval`, claiming the window in
  `wh_unique_emission_claims`; other instances clear their reading. The largest `MaxEventTypes` types are named, the
  rest reported as `other`. The `Whizbang:TableStatistics` settings are now validated at host start.

- **A perspective table can reserve page space for in-place updates (#1592).** `[PerspectiveTableStorage(FillFactor =
  90)]` sets the table's `fillfactor` (10 to 100), so an update that changes no indexed column can stay on its page
  (a HOT update) and write no index entries. A changed fillfactor applies only to new pages, so the schema pass also
  queues a rewrite request for a table that already holds rows; a restart that finds the value in place queues
  nothing. `wh_perspective_cursors` gets fillfactor 90 in migration 223, queued for a rewrite the same way.
- **Out-of-range table storage values are reported at build time (#1592).** The new error WHIZ804 reports a
  `[PerspectiveTableStorage]` value PostgreSQL would refuse: a `FillFactor` outside 10 to 100, a `ToastTupleTarget`
  outside 128 to 8160, or an autovacuum scale factor above 100. The schema generators leave such a value out, where
  it used to fail the schema pass at startup.
- **Retention for the framework ledgers that had none (#1572).** The maintenance sweep now bounds `wh_log`
  (`log_retention_days`, default 14), `wh_schedule_runs` (`schedule_run_retention_days`, default 30, always keeping each
  schedule's newest run), the stream-purge audit (`stream_purge_audit_retention_days`, default 0 = keep forever) and the
  integrity ledger (`integrity_ledger_max_entries`, default 100000, through the trim that had no caller). Deletes run
  in batches and are skipped under `debug_mode`. Each window is settable under
  `Whizbang:Workers:Maintenance:Retention` (`LogDays`, `ScheduleRunDays`, `StreamPurgeAuditDays`,
  `IntegrityLedgerMaxEntries`) and published to `wh_settings` by the retention settings bridge.
- **The durable-signal retention window is configurable (#1574).** `WhizbangNotificationOptions.SignalRetention`
  (`Whizbang:Database:SignalRetention`, env `Whizbang__Database__SignalRetention`, default `7.00:00:00`) replaces the
  fixed seven days, and a window of zero or less is rejected at startup. The sweep now deletes in batches; it still
  never removes a signal some pod has not delivered.

#### Audit
- **Security system events are stored, each kind behind its own switch (#1599).** `SystemEventOptions` gains
  `AccessDeniedEnabled` and `PermissionChangedEnabled` (default `true`) and `AccessGrantedEnabled` and
  `ScopeContextEstablishedEnabled` (default `false`), bound from `Whizbang:SystemEvents:*` and honored by
  `IsEnabled<T>()`. `SystemEventEmitter` therefore stores denials and permission changes on `$wb-system` by default;
  before, every security event was dropped before the append. Grants and scope establishments happen on every read or
  request and stay opt-in.

- **Command audit records the length of a command's id lists, not the lists (#1525).** A command implementing the new
  `IBoundedCommandAudit` names its id-list properties in `ListsAuditedAsLengths()`, and `CommandAudited.CommandBody`
  keeps each one's length (a JSON number) in place of the list; null lists stay null and every other property is kept.
  `RebuildPerspectiveCommand` (`IncludeStreamIds`, `ExcludeStreamIds`) and `RequestRedeliveryCommand` (`StreamIds`)
  implement it, so a rebuild naming 100,000 streams no longer writes about 4 MB of audit per receiving service.
- **Audit records point at their event; the body is opt-in (#1588). Behavior change.** `EventAudited.OriginalBody` is
  now `JsonElement?` and is null (and left out of the record) unless the type is marked
  `[AuditEvent(IncludeBody = true)]` or `Whizbang:SystemEvents:AuditBodyDefault` (`Whizbang__SystemEvents__AuditBodyDefault`)
  is `Include`; the default is `Omit`. The record still carries the original event's id, type, stream, position and
  scope, which is all the built-in audit view stores. A consumer that reads `OriginalBody` sets `Include` (or marks
  its types) to keep receiving it.
- **Audit decisions read `[AuditEvent]` from a generated registry, not reflection (#1580).** A generator registers
  every type that carries the attribute, directly or through a base class, with its values into `AuditEventRegistry`
  at module load, and `AuditEligibility` reads from it, so Whizbang.Core no longer reads the attribute by reflection.
  A type generated code cannot name (a private nested type) is reported with the new warning WHIZ180 instead of
  silently losing its audit settings; make it internal or public, or register it with `AuditEventRegistry.Register`.

- **The large framework tables vacuum after a fixed number of changes (#1582).** Migration 222 sets autovacuum to a
  threshold of 10000 changes with scale factor 0 on `wh_perspective_cursors`, `wh_lifecycle_completions`,
  `wh_stream_digests` and `wh_message_deduplication`; an insert threshold of 10000 on `wh_event_store`, which keeps
  its visibility map current for index-only scans; and the same on `wh_event_body`'s TOAST table. A scale factor
  grows with the table, so a large table used to hold hundreds of thousands of dead rows before a vacuum.

#### Startup
- **Every instance records and logs its startup stage (part of #1636).** One owner, `IStartupStageReporter`, holds the
  stage this instance is in (`StartupStage`: Preflight 10, Elect 20, Migrate 30, Converge 40, Coordinate 50, Accept
  commands 60, Running 70, Draining 80, Stopped 90; fixed numbers with gaps, never list positions). Entering a stage
  logs `Stage X of 7: <name> - <message>` at Information (`Shutdown stage: <name> - <message>` for Draining and
  Stopped) and writes `startup_stage`, `startup_stage_message` and `startup_stage_entered_at` on the instance's row
  (migration 231), with `lifecycle_phase` written from the stage. Stages only move forward; progress within a stage
  repeats its prefix and keeps its entry time. A stage entered before the row exists is held and written when the
  instance registers, and a failed write is held until the next accepted heartbeat. The host's lifecycle marks
  Preflight, Draining (before any worker stops) and Stopped; schema initialization reports Elect, Migrate (skipped
  when the schema is already current), Converge and Coordinate; the lifecycle's AcceptingCommands and Running
  transitions enter stages 60 and 70. `wh_instance_is_eligible_for_work` reads the stage: a recorded stage must be 60
  or 70 (a row with none is judged by its phase, as before), so a draining instance stops being eligible the moment
  Draining is written. `wh_instance_startup_stages()` lists every instance with its stage, `X of 7`, message and time
  in stage. The `startup` health component's detail leads with the stage's line, `AggregatedHealth.Stage` carries
  it, and the ASP.NET health checks add `startup_stage`, `startup_stage_name` and `startup_stage_message` to their
  data. `IStartupStageObserver` watches every change.
- **Workers declare the startup stage they need (part of #1636).** `IStartupStageGate` (default `StartupStageGate`,
  registered by the worker pipeline as an observer of the stage reporter) answers whether this instance has reached a
  startup stage and waits for it: a stage is reached once it, or a later one, is entered or skipped, and every stage up
  to Coordinate once the schema-ready gate opens; a shutdown stage reaches nothing.
  `services.AddHostedServiceAtStage(stage, sp => ...)` registers a hosted service, wrapped in
  `StartupStageGatedService<TService>`, that the host starts with everything else and that starts the service only
  once its stage is reached (a fault in the service's own loop is the wrapper's, so the host handles it as before).
  `PgSharedNotifyConnection.OnAliveLockClaimed` is raised on each alive-lock claim, the work-available poll sources
  take a `SchemaReadyGate`, and `RoleAssignmentHealthSource` has a constructor taking the stage gate.
- **`ISchemaInitializationObserver`** — watch schema initialization: `OnSchemaLockContendedAsync` (another
  instance holds the schema lock; this start waits) and `OnAttemptFailedAsync` (a background attempt failed and
  will be retried), on both Postgres drivers.
- **`ISchemaInitializationRunner` and `AddWhizbangSchemaInitialization()`** — one schema initializer runs every
  driver's registered runner in order, then opens the schema-ready gate.
- **Capabilities: one pattern for external dependencies** (#1511, #1513, #1498): every external dependency the
  library can use is a capability, identified by provider and name (`postgres/pg_squeeze`), described once
  (purpose, features, whether the library may provision it, prerequisites, the fix per state) and probed per
  scope with one generic state: Ready, Provisionable, NotPermitted, NeedsServerChange, NoPrivilege, Missing or
  Unknown, with the provider's own detail kept beside it. `ICapabilityProvider` is the extension point for
  any package; `ICapabilityRegistry` aggregates every provider. After the migration step, and without
  blocking readiness, the library probes every provider and writes one verdict per provider and scope:
  the usable capabilities with their purpose at Information, the rest at Debug with the fix. A provider
  that cannot probe a scope reads Unknown with a warning and never stops startup. Gauge
  `whizbang.capability.state` (tags provider, capability, scope, state) on the new `Whizbang.Capabilities`
  meter, in `WhizbangMeters.All`. `Whizbang:Capabilities:ProbeOnStartup` (default `true`) turns the probe off.
  Postgres extensions are the first provider, registered by both Postgres drivers with the database as the
  scope: `vector`, `pg_trgm`, `pg_squeeze`, `pg_repack`, `pg_partman`, `pgstattuple`, `pg_stat_statements`,
  `pg_cron`.
- **`Whizbang:Workers:Maintenance:TableRewriteStrategy`** (#1742): `Auto` (default), `PgSqueeze`, `VacuumFull` or
  `None`, resolved against `AllowTableRewrite` by the post-ready `Rewrite` step. `Auto` rewrites with `VACUUM FULL`
  when `AllowTableRewrite` permits it (the behavior earlier releases had) and otherwise only detects and reports; it
  never starts an online rewrite, and names the opt-in when pg_squeeze is usable. `PgSqueeze` requires the pg_squeeze
  extension, so an explicit choice on a server that does not allow it stops start with the fix; it never falls back
  to `VACUUM FULL`, and this version has no pg_squeeze rewriter yet, which the step reports. `None` rewrites nothing.
- **Required capabilities are provisioned before the verdict** (#1750): between the probe and the verdict, the
  `Capabilities` step creates every capability a requirement in force names that is Provisionable, and upgrades
  every one that is Ready with a newer version available, so an explicit requirement on a capability the library
  may create passes. The verdict line says `created`, `already present` or `upgraded` before the version. A
  capability nothing requires is only detected. `Whizbang:Capabilities:AutoProvision` (default `true`), overridden
  per provider (`Whizbang:Capabilities:<provider>:AutoProvision`) and per capability
  (`Whizbang:Capabilities:<provider>:<name>:AutoProvision`), turns it off; detection is unchanged, so an
  operator-created extension is still used. A refusal is reported by the verdict line, never thrown, and a refused
  upgrade leaves the installed version working. The Postgres provider runs `CREATE EXTENSION` and
  `ALTER EXTENSION ... UPDATE` each in one transaction holding the schema lock. `ICapabilityProvisioner` is the
  extension point for any provider.
- **Unknown configuration keys are reported at startup** (#1490): .NET binding ignores a key nothing reads,
  so a removed or misspelled `Whizbang:*` key used to leave its setting at the default without a word. When
  the host starts, every configured `Whizbang:*` key is checked against the keys the library reads (the
  recorded configuration key manifest, embedded in `Whizbang.Core`; consumer-named entries such as
  `Whizbang:PerspectiveRowRetention:Overrides:<Model>` count as known). Each key nothing reads is logged once
  at Warning, naming the key; a removed key also names its replacement, for example
  `Whizbang:WorkCoordinator:MaintenanceInterval` points to `Whizbang:Workers:Maintenance:IntervalMinutes`,
  `Whizbang:Housekeeping:SettledCooldown` and `Whizbang:Housekeeping:MaxDeferral`.
  `Whizbang:Configuration:UnknownKeys` (default `Warn`) selects `Warn`, `Throw` (fail start, naming every
  unknown key) or `Ignore`.
- **pg_repack table rewrites, in process or by an external job** (#1753): `TableRewriteStrategy = PgRepack` rewrites
  requested tables online with pg_repack and requires the pg_repack extension.
  `Whizbang:Workers:Maintenance:TableRewrite:PgRepack:Mode` picks who runs the client: `InProcess` (default; the
  maintainer-duty holder runs it per table in the `Rewrite` step) or `External` (Whizbang never starts a process; the
  tables stay queued for an operator's job). In process, the client (`:ClientPath`, default `pg_repack` from `PATH`)
  is a probed prerequisite, `program/pg_repack`: missing, it fails start when `Mode` was set explicitly and degrades
  with a warning when left at its default. The connection reaches the client only as libpq environment variables
  (`PGPASSWORD` among them), never as process arguments, and the client runs with `--no-superuser-check` and
  `--no-kill-backend`. `IExternalProcessRunner` is the seam every process start goes through, and
  `IWorkCoordinator.RewriteTableWithAsync` hands an external rewrite its table and connection and re-measures after.
- **Removed configuration keys are declared with `[RemovedConfigurationKey]`** (#1755): the hand-kept table is gone.
  The configuration manifest generator records each declaration (on the assembly, or on the replacement's options
  type) in the assembly's manifest, `ConfigurationManifestRegistry.FindRemovedKey` answers the startup check, and a
  package built on Whizbang declares its own removed keys the same way. All eight former entries moved to the
  attribute. A key removed with nothing in its place logs event 3, naming no replacement.

#### Messaging & delivery
- **A derived type can override an inherited `[StreamId]` on purpose (#1833).** `[StreamId(Override = true)]` on a
  property or a record primary-constructor parameter makes it the stream ID ahead of any inherited `[StreamId]`,
  including where an inherited property used to beat a derived record's `[StreamId]` parameter, and an override on an
  intermediate base type is inherited below it. The extractor, the perspective runner and the analyzer make the same
  choice: hiding an inherited stream ID with an override is not reported as WHIZ006, and a perspective event whose
  stream ID is an override is not WHIZ031. WHIZ006 is still reported for two `[StreamId]` declared on one type,
  override or not, and for a hide without the option. A new warning, **WHIZ016**, reports an override where no base
  type declares a `[StreamId]`. A plain `[StreamId]` is unchanged (`Override` defaults to `false`).
- **`[ReceptorPriority]` orders the receptors a service runs for one message (#1412).** Lower runs first;
  `ReceptorPriority.FIRST`, `EARLY`, `DEFAULT`, `LATE` and `LAST` name the usual points, and a receptor that
  declares nothing runs at `DEFAULT`, the middle of the range.
- **Composite events** — `ICompositeEvent`: durable dispatch-time fan-out that lives inside the
  inbox/dispatch/retry/DLQ envelope (not at the transport edge), with lineage, composite
  `[StreamId]` generation, echo-gate exemption for owned composites, and end-to-end inner-event
  persistence.
- **Collective events** — `ICollectiveEvent` cross-perspective cohorts: per-perspective `Where`
  projection, `ICollectiveQuery` on both EF Core and Dapper, pluggable marker-gated apply hooks
  (collective + per-event), and a collective post-apply lifecycle with OpenTelemetry spans.
- **Transports** — FIFO ordering (Azure Service Bus sessions / RabbitMQ single-active-consumer),
  ordering on auto-created ASB topics, transport batch receive (sliding-window → bulk inbox insert
  → ACK) with `IInboxChannelWriter` routing, resilient receive + orphan-inbox janitor, and full ASB
  auto-provisioning (idempotent topic/subscription/rule creation) plus an Aspire dev-config generator.
- **Unified endpoints** — `[CommandEndpoint<TCommand, TResult>]` generates REST + GraphQL endpoints
  from a command, with before/after/error mutation hooks; `SendManyAsync` / `PublishManyAsync` /
  `LocalSendManyAsync` batch APIs.
- **Large-message offloading (claim-check)** — producers detect oversized envelopes pre-flight, upload
  to an `IMessageBodyStore`, and swap the wire payload for a small claim (`whizbang.is-claim` header);
  receivers download, SHA-256 verify, and rehydrate. `Whizbang.Offloads.AzureBlob` with delete-on-consume
  active cleanup (time-based TTL delegated to Azure Blob lifecycle rules).
- **Config-driven offload registration** — `AddWhizbangAzureBlobOffloadsFromConfiguration(IConfiguration)`
  scans every provider under `Whizbang:Offloads:AzureBlob:<name>`, registers a blob store for each,
  enables the claim-check hook, and binds the selector from `Whizbang:BodyOffload`. A no-op when no
  providers are configured, so offload is opt-in by config presence with zero per-service code.

#### Work coordination, perspectives & sagas
- **A saga can end in one commit or rollback signal (#1613).** A `[Saga]` marked `[SignalsOutcome]` publishes exactly
  one `SagaCommittedEvent` or `SagaRolledBackEvent` per run on its own stream, whichever path ends it: `Completed`
  commits; `Failed`, `Abandoned` and (by default) `CompletedWithFailures` roll back, the last overridable with
  `OnCompletedWithFailures = SagaOutcome.Commit`. `BaseSagaService.RollbackSagaAsync(ctx, reason, ct)` rolls a run back
  explicitly (a cancel), and a completion already in flight then cannot commit it. A failed signal is retried by the
  next watchdog tick. `BaseSagaModel.Outcome` and `SagaApplyHelper.TrackOutcome` record it on the projection. The
  framework signals and does not compensate; see the stage-then-apply guidance in the docs.
- **A large rebuild selection splits into bounded commands (#1525).** `RebuildPerspectiveCommand.SplitByStreams(max)`
  returns commands naming at most `max` streams each (default `DEFAULT_STREAMS_PER_COMMAND`, 500, the stream
  redelivery bound), in order and without duplicates, each with `RequestId = ChunkRequestId(requestId, chunkIndex)`, a
  name-based id of the chunk index within the caller's request id. `IncludeStreamIds` documents the size guidance: the
  list travels whole through the outbox, the transport and each inbox, and more than about 6,500 ids exceeds the Azure
  Service Bus Standard message limit.
- **A period close runs the destruction-stage receptors around its truncate (#1504).** `IStreamCloser.CloseAsync`
  now does what the ephemeral reap does: the events at or below the close version whose type has a receptor at a
  destruction stage are loaded first, `PreDestructionDetached`/`PreDestructionInline` run before the truncate (a
  throwing `PreDestructionInline` receptor aborts the close, as a throwing `IDestructionHook` does) and
  `PostDestructionDetached`/`PostDestructionInline` run with the same envelopes after a close that truncated. With no
  destruction-stage receptor registered nothing extra is read. `IWorkCoordinator.GetStreamCloseTargetsAsync` lists a
  close's range (EF Core Postgres; the default is empty), and `StreamCloser` takes an `IServiceScopeFactory`.
- **Lifecycle coordinator** — `LifecycleCoordinator` with `PostLifecycle` / `PostAllPerspectives` /
  `ImmediateAsync` / `FireAt` stages that fire once after all perspectives complete.
- **NOTIFY-first work coordination** — zero-idle-polling claim loop with NOTIFY reconnect + startup
  catch-up, orphan-redistribution NOTIFY, a pinned worker connection pool, and turnkey
  `AddWhizbangNotificationDataSource` LISTEN data-source auto-discovery (SCRAM-SHA-256).
- **Dead-letter pipeline & recovery** — `wh_dead_letters` + `move_to_dead_letters()` + `IDeadLetterStore`
  with forensic preservation, plus two recovery flows: a policy-driven `DeadLetterRecoveryWorker` and
  transport auto-aggressive drainers; `ScheduledRetryWorker` for delayed retries.
- **Sagas** — the `Whizbang.Sagas` application block; `IDispatcher.PublishOnceAsync` exactly-once
  emission (`wh_unique_emission_claims` + `IClaimedEmissionStore`); framework-managed saga completion
  lifecycle with an adaptive watchdog scheduler and intra-pod stream affinity.
- **Perspective rewind** — rewind detection for auto-created perspective events, startup rewind scan +
  configuration + observability, and a catch-up loop that re-reads event-store HEAD so events appended
  mid-rewind are not skipped.
- **Throughput** — drain mode (per-perspective event filtering with full lifecycle), a bulk-import
  throughput path, and throughput instrumentation (gate hold-duration histograms, WorkCoordinator metrics).
- **Retention windows from configuration** (#1487): every retention window the maintenance sweep reads from
  `wh_settings` is now an option, set like any other: `Whizbang:Workers:Maintenance:Retention` (deduplication,
  stuck inbox, recovered dead letters, abandoned streams, instance-eviction tombstones, orphaned perspective
  events, collective-ordering heads) and `Whizbang:Ephemeral` (`RewindGraceSeconds`, `PointerPrune`). Each
  maintenance cycle publishes the options you set before the sweep runs; an option left unset leaves the stored
  value alone, a negative value fails startup naming the key, and a failed write is logged without failing the
  cycle. The Dapper engine now also publishes `debug_mode`, which it never did.
- **Maintenance deferral ceiling is a time** (#1491): `Whizbang:Housekeeping:MaxDeferral` (default one hour)
  bounds how long the idle gate can hold back maintenance or dead-letter recovery, measured from the first
  deferral, so changing a tick interval no longer moves the ceiling. The forced pass still reports as
  `ProceedDeferralLimit`. `MaxConsecutiveDeferrals` remains as a legacy fallback, used only when it is set and
  `MaxDeferral` is not; with neither set the one-hour time applies. A zero or negative `MaxDeferral` fails
  startup naming the key. The turnkey coordinator now measures its dwell and ceiling on the registered
  `TimeProvider`.

#### Context, identity & serialization
- **Scope & security propagation** — `[InheritScope]` + `ScopeFields`, `IStreamScopeEvent` +
  `UpdateStreamScopeCommand`, `[RequirePermission]` + claim aggregation, and scope JSONB column
  population with read-path hydration.
- **Cascade context & correlation** — `MessageContextAccessor` (AsyncLocal, child-scope isolation),
  `AutoPopulate` for class messages, turnkey W3C `X-Correlation-ID` end-to-end propagation, centralized
  cascade-identity resolution across worker/detached boundaries, and `ICallerInfo`.
- **Stable type identity** — `[PinnedId]` + type registry + Roslyn analyzer/code-fix; a committed
  pinned-type ledger (`.whizbang/pinned-type-ledger.json`) with governed rename detection/acknowledgement/
  aliasing and ledger-aware registry reconcile; type-definition fingerprint migrations.
- **Strongly-typed id providers** — generated `IWhizbangIdProvider<TId>` providers + registry.
- **Event upcasting** — `IEventUpcaster` (re-key / type-change / field-backfill, pure & AOT-safe) +
  `EventUpcasterPipeline` (ordered composition) + `AddEventUpcaster<T>()` + `UpcastingEventStoreDecorator`
  wired innermost in the `IEventStore` stack, applying on every polymorphic read path. Zero-cost
  passthrough when no upcasters are registered.
- **Re-key upcasters re-route on rebuild** — `IPerspectiveRunner.RunRebuildAsync` reads a physical
  stream's events, partitions them by post-upcast target stream id, and projects each partition onto its
  own row; the live drain hot path is unchanged. Enables a per-stream history migration via a one-time
  projection rebuild.
- **Framework-wide serialization versioning** — `SerializationVersion.CURRENT`, `VersionedJsonEnvelope`
  (stamp/read a version on any persisted JSON blob), `IVersionedJsonSerializer` / `<T>` and
  `VersionedJsonSerializerRegistry`.
- **Snapshot serialization versioning** — `SnapshotEnvelope` + `SnapshotUpgradePolicy`
  (`RebuildFromEvents` default / `None` / `LazyUpcast` / `UpgradeOnStartup`); snapshots are stamped on
  write and rebuilt from events on a version mismatch or legacy blob.
- **Size-aware serialization** — `SerializationResult` (bytes + `SizeBytes` + content type + version)
  and `SerializationOptions`, produced by `WireEnvelopeSerializer` at a single serialize-once point so the
  body-path (inline vs offload) decision reads the size off the result.
- Canonical `EventTypeMatchingHelper.BuildTypeLookup` / `TryResolveType` — one normalized
  stored-`EventType` → `Type` resolver shared by every event-store read path.

#### Observability & tooling
- **The perspective worker's cooldown cache reports when it goes over its cap (#1828).** `RecentlyProcessedEventCache.OnCapacityExceeded` (`Action<int>`, the resident count) is raised when an insert takes
  the cache past `MaxEntries`, before it evicts. Each raise is followed by evicting entries still inside their TTL, so
  a steady stream of raises means `MaxEntries` is too small for the drain. It costs nothing below the cap.
- **Subscription status changes are an event (#1830).** `TransportConsumerWorker.SubscriptionStatusChanged` is
  raised each time a destination's subscription status changes value (to Healthy, Recovering, Failed, or back to
  Pending on a retry), with the previous and new status and the destination's live state, after the fields that
  accompany the status (last error, attempt count, subscription) are recorded. Use it for alerting, metrics or a
  per-destination readiness gate, or to wait in a test on the exact transition asserted. A throwing handler is logged
  as a warning and affects neither the subscription nor the other handlers; with no handler attached a change costs
  one null check. The worker tests that read a subscription state after waiting on a subscribe call now wait on this
  event, which removes a race that failed a CI run.
- **Every runtime parse of a message can be counted (#1691, part of #1526).** `MessageParseDiagnostics` is a
  public, AOT-safe seam through which each parse of a stored or received message on the outbox, inbox, transport
  receive, offload, event store, lifecycle, multi-stream and redelivery paths reports its message id, source, kind
  and call site; with no observer subscribed a parse site costs one volatile read. `MessageParseRecorder` counts and
  summarizes the parses of chosen messages. Regression tests pin today's counts per flow (a locally published event
  feeding two perspectives is parsed 17 times), and `plans/message-cache.md` proposes the cache that lowers them.
- **Unused-index report (#1669).** Each table-statistics cycle reports the indexes on `wh_` tables that nothing has
  scanned since the statistics were last reset, with their size, on the new gauge
  `whizbang.table.unused_index_bytes` (tags `table_name`, `index_name`), and logs the set at Information when it
  changes. Unique, primary-key and exclusion indexes are left out; an index on a partitioned table is one series with
  its partitions summed; past `MaxTrackedTables` the smallest are summed under `other`. Nothing is ever dropped.
  `ITableStatisticsProvider.GetUnusedIndexesAsync` defaults to empty.
- **The table-statistics collection interval is a setting (#1668).** `Whizbang:TableStatistics:CollectionInterval`
  (default `00:00:30`, the interval the collector always used) sets how often each instance reads the tables into the
  `Whizbang.TableStatistics` gauges. Zero or negative fails host start naming the key.
- **Table gauges carry a `database` tag (#1717).** Every `Whizbang.TableStatistics` gauge (table sizes, components,
  live rows, bloat, unused indexes, queue depths and the event-type bytes) is tagged with the database it was read
  from (`current_database()`), so services on different databases report the same table as separate series. The
  collector asks once and keeps the name. `ITableStatisticsProvider.GetDatabaseNameAsync` defaults to null, which
  reports the gauges without the tag. `Whizbang:TableStatistics:MaxTrackedTables` (default 500) is the cap per
  database.
- **Two more ephemeral build rules (#1544, #1545).** WHIZ131 (Error) reports a call site that asks to rebuild or
  rewind a perspective whose every applied event is ephemeral: constructing `RebuildPerspectiveCommand` for it, an
  `IPerspectiveRebuilder` rebuild, or `IPerspectiveRunner.RewindAndRunAsync`. The perspective is resolved when it is
  named by a constant string (its CLR type name), `typeof(T).FullName` or `TypeNameFormatter.FormatClrTypeName(typeof(T))`;
  a mixed-mode perspective is left to WHIZ130. WHIZ132 (Warning) reports a receptor whose input message is ephemeral
  and whose response returns or cascades a Sourced event, which would keep what the ephemeral message carried as a
  durable fact. The new `[DangerouslyAllowSourcedEventsFromEphemeralInput]` attribute on the receptor silences it for
  a deliberate case, the way `[DangerouslyAllowMixedEphemeralAndSourcedEvents]` silences WHIZ130.
- **A stream never mixes ephemeral and Sourced events (WHIZ133, #1546).** The step that copies outbox and inbox events
  into the event store, and the direct append (`IEventStore.AppendAsync` on both drivers), refuse an event whose kind
  differs from the kind its stream already holds, in either order and with no opt-in; a stream with nothing stored
  takes the kind of its first event. A compaction origin is neither kind, so a compacted stream keeps receiving the kind
  it held. A direct append now stores the event's kind (the Ephemeral or Compacted flag, `[Ephemeral]` on a type
  included, through the event stores' new optional `IEphemeralModeResolver`) instead of storing every event as Sourced,
  and a refusal there throws `MixedStreamEventException` with nothing stored. A write its caller awaits (the immediate and scoped
  strategies' flush) fails with the new `MixedStreamEventException`, which names the stream, the refused event's type
  and the kind the stream holds, and keeps nothing of the write. Every other write (the batch flushes, handler
  commits, the inbox claim) dead-letters only the refused row, with the new reason
  `MessageFailureReason.MixedStreamRefused` (held for review), and stores the rest. `OutboxMessage.RaiseOnMixedStream`
  carries the choice to the store (migration 228).
- **Framework-shaped handlers no longer fail a consumer's unused-parameter rules (#1378).** A `[CollectiveApplyFor]`
  handler is bound by its shape, not an interface, so IDE0060, CA1801, Roslynator RCS1163 and Sonar S1172 flagged
  its event parameter whenever the handler did not read it. The analyzer package now ships a `DiagnosticSuppressor`
  (WHIZS001-004) that suppresses those four rules on exactly that parameter, in the IDE and in the build. The
  `ICollectiveQuery` parameter was already optional; `[CollectiveApplyFor]` now documents both accepted shapes, so a
  handler that does not read the query leaves it out. The type registry and the collective generator now share one
  definition of a handler, so a method the generator rejects no longer has its first parameter listed as an event.
- **OpenTelemetry metrics** — instrumentation across the dispatch, workers, coordinator, perspectives,
  transport, and lifecycle meters.
- **`whizbang migrate` CLI** — Marten/Wolverine migration analyzers + transformers (JSONB LINQ,
  global-using rewrites, package-ref upgrades); PgBouncer-compatible schema initialization; the
  `ExtractMessageRegistry` MSBuild target shipped via NuGet.
- **Table statistics cover every framework and perspective table, on both Postgres drivers (#1488).** The
  `Whizbang.TableStatistics` meter reported sizes and bloat for a fixed list of seven tables, so the event body
  store, deduplication, dead letters and every perspective table were invisible, and a Dapper host reported nothing
  at all. Every `wh_` table in the service's schema is now found in the catalog on each refresh (partitioned tables
  once, with their partitions summed), and two gauges are added: `whizbang.table.component_bytes` (tags
  `table_name`, `component` = `heap`, `toast`, `indexes`, adding up to `whizbang.table.estimated_bytes`) and
  `whizbang.table.live_rows`. All of it comes from one catalog statement per refresh. Cardinality is capped by
  `Whizbang:TableStatistics:MaxTrackedTables` (default 500); past it the smallest tables are summed under
  `table_name="other"`. A table missing from a refresh stops reporting instead of keeping its last value, and
  `TableStatisticsCollector.CycleCompleted` is public. **Upgrade:** `PostgresTableStatisticsProvider` moved from
  `Whizbang.Data.EFCore.Postgres` to `Whizbang.Data.Postgres.Observability`; a host that constructs it directly
  changes the namespace. Dashboards that sum `estimated_bytes` across tables will see the new tables' bytes.
- **API hygiene** — a `SyncMode` enum + `LocalInvokeAndSyncAsync(msg, SyncMode, ct)` overload; the old
  timeout-based overloads are now `[Obsolete]`.

### Changed

- **Per-message debug logging and by-name type lookups no longer allocate (#1756).** With debug logging off, the
  dispatcher's tag-processing step and the perspective worker's per-batch description cost nothing (tag processing
  30-41 ns and 32-88 B down to 3-5 ns and 0 B; a 16-group batch description 414 ns and 1,752 B down to 1.2 ns and 0 B).
  A by-name type lookup of the stored "Type, Assembly" form no longer copies the name, and the JSON wire-name map and
  control-plane name set read a frozen snapshot (one lookup 93-98 ns and 384 B down to 11-12 ns and 0 B). Shared
  counters were measured under contention and left as they are. Numbers in `benchmarks/results/1756.md`.

- **pg_repack in process is budgeted per maintenance cycle, as pg_squeeze is (#1808).** With
  `Whizbang:Workers:Maintenance:TableRewriteStrategy = PgRepack` and `TableRewrite:PgRepack:Mode = InProcess`, the
  boot-time `Rewrite` step no longer runs pg_repack over every queued table at once; it skips and says the maintenance
  cycle performs it. The `online-table-rewrite` maintenance step now serves both online strategies: it takes the queued
  tables largest bloat ratio first within `Whizbang:Workers:Maintenance:OnlineRewriteMaxBytesPerCycle`, runs only when
  the `pg_repack` extension is Ready on every probed database and the `program/pg_repack` client is Ready on the host
  (otherwise it logs why and keeps the queue), passes the connection to the client only through its environment, and
  clears a request only when the re-measure shows the rewrite improved the table. `External` mode is unchanged:
  Whizbang never starts the client.

- **Statement statistics are read only while `pg_stat_statements` is Ready (#1754).** `GetExpensivePredicatesAsync`
  on the Postgres table statistics consults the capability registry for its database before reading
  `pg_stat_statements`, instead of reading it and catching every `PostgresException`. Any other state, or no
  registry, answers empty without opening a connection. Both drivers pass the registry and the name their database is
  probed under; a provider constructed directly takes them as the optional `capabilities` and `capabilityScope`
  arguments, and without them reads no statement statistics. A read that fails while the capability reads Ready (the
  extension dropped after the probe) is no longer swallowed.
- **Breaking (binary): `PostgresTableStatisticsProvider`'s two constructors gain the optional `capabilities` and
  `capabilityScope` parameters (#1754).** Source that constructs the provider compiles unchanged; an assembly compiled
  against 0.2625.0 that calls either constructor directly is recompiled. Recorded in the package's
  `CompatibilitySuppressions.xml` (#1761).
- **The gated prefix truncate is named for what it does: `close_stream` is now `wh_truncate_stream_prefix` (#1731).
  Breaking.** "Close" is left to closing the books, a different and higher-level operation. Migration 240 drops every
  `close_stream` overload and creates `wh_truncate_stream_prefix` with the same arguments and behavior; its result column
  is `truncate_status` (was `close_status`) and its success status `truncated` (was `closed`). The C# API follows:
  `IWorkCoordinator.CloseStreamAsync` is `TruncateStreamPrefixAsync`, `GetStreamCloseTargetsAsync` is
  `GetStreamTruncateTargetsAsync`, `StreamCloseResult` is `StreamTruncateResult`, and `IStreamCloser` /
  `StreamCloser` with `CloseAsync` are `IStreamPrefixTruncator` / `StreamPrefixTruncator` with `TruncateAsync`
  (registered by `AddWhizbangWorkers()` as before). The refusal statuses are unchanged. No forwarding alias is kept.
  The scheduled close types (`ScheduledStreamClose`, `StreamCloseSchedule`) keep their names, so stored schedules
  still dispatch. **Upgrade:** rename the calls and types above; compare a result's `Status` with `"truncated"`
  instead of `"closed"`; and change SQL that calls `close_stream(...)` or reads `close_status` to
  `wh_truncate_stream_prefix(...)` and `truncate_status`. A custom `IWorkCoordinator` that overrode `CloseStreamAsync`
  overrides `TruncateStreamPrefixAsync` instead, or the default (`unsupported`) answers.
- **Transport test warmup waits on the subscriber's signal only (#1657).** `SubscriptionWarmup.WarmupAsync` in the
  testing library publishes one warmup message and completes when the subscriber signals that it arrived. The 5-second
  initial delay and the 2-second resend loop are gone, and so are the `retryInterval` and `initialDelay` parameters
  and `DefaultRetryInterval` / `DefaultInitialDelay`: a transport's subscription is durable once its subscribe call
  returns, so the one warmup message waits on the broker until the subscriber takes it.
- **The Dapper driver names a perspective's table from its model, as the EF Core driver does, and a model can name its
  table with `[PerspectiveTable]` (#1719). Behavior change.** The schema generator took the table name from the
  perspective class (`OrderSummaryPerspective` became `wh_per_order_summary_perspective`); it now uses the model type
  and the same suffix-stripping rule as the EF Core generator (`OrderSummaryModel` becomes `wh_per_order_summary`),
  through one shared helper, so one model has one table on either driver. Two perspectives of one model now share that
  table on the Dapper driver too. **Existing tables are renamed in place at the next start, keeping their rows:**
  migration 243 adds `wh_follow_perspective_table_renames`, which both drivers call before they create the perspective
  tables. It renames the table the registry recorded for the model, or on the Dapper driver the table still under the
  name a released build gave it (that build never wrote the registry, so the generated registry payload now carries
  each perspective's former name), to the new name, and with it every index on it whose name carries the old table
  name or the part of it past `wh_per_` (when the new index name is free and fits). When a table already exists under
  both names, nothing is dropped or merged, and a warning names the model and both tables. This also fixes a rename
  the registry reconcile could never complete on either driver: it ran after the new name had been created as an
  empty table, and left the rows under the old name. A model folded by two perspectives had a table per perspective on
  the Dapper driver: the first is renamed and the second is left in place, unread, with that warning; move its rows by
  hand if they differ, then drop it. The new `[PerspectiveTable("wh_per_...")]` on a model
  sets the name exactly as written (the suffix-stripping properties do not alter it). The schema generator now
  reports a name past the identifier limit as an error (WHIZ823, the EF Core generator's WHIZ820), a manual name that
  is not `wh_per_` followed by lowercase letters, digits and underscores (WHIZ824), and two models on one table
  (WHIZ825), on both generators. WHIZ820's message names `[PerspectiveTable]` as a remedy.

- **The generators package writes no generated source to disk and embeds no message registry by default (#1609).
  Behavior change.** The package's props no longer turn `EmitCompilerGeneratedFiles` on, and
  `WhizbangEmitMessageRegistry` now defaults to `false`: the generator embeds no registry JSON in the assembly and the
  build writes no `.whizbang/cache/message-registry.json`. `WhizbangIncludePinnedTypeLedger` stays `true`, no longer
  depends on the registry switch, and `.whizbang/pinned-type-ledger.json` keeps updating with generated files off,
  because both files are now read from the built assembly instead of from generated `.cs` files on disk. Both
  extraction targets are skipped on a build that did not recompile. **Upgrade:** for the VS Code extension's message
  registry, set `<WhizbangEmitMessageRegistry>true</WhizbangEmitMessageRegistry>` in the project; to keep generated
  files on disk, set `<EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>` (they still go to
  `.whizbang/cache/` unless `CompilerGeneratedFilesOutputPath` says otherwise).
- **A migration edit that changes no executable SQL is no longer a change (#1648).** Both drivers and the bootstrap
  closure hash a migration's normalized SQL: comments (`--` and nested `/* */`), whitespace at line edges and runs of it
  between tokens, blank lines and the line ending convention are left out, while string literals, `E'...'` strings,
  quoted identifiers and dollar-quoted bodies are hashed exactly as written (so a comment inside a function body is
  still a change). A comment, license header or whitespace edit to a shipped migration is skipped by every database,
  and the review checks' `migrations.edited` rule no longer flags it; an executable edit is still applied and flagged.
  **Upgrade:** the first start after upgrading rewrites, under the migration lock and before deciding what to run,
  every `wh_schema_migrations` row whose hash is the raw-text hash of its file as shipped to the normalized hash, and
  records the normalized bootstrap closure hash beside the old one, so nothing is applied again and the bootstrap does
  not re-run; a row matching neither form belongs to a file that really changed and is applied as before. Later starts
  find nothing to rewrite.
- **The unread `table_rewrite_policy` setting is retired (#1667).** Migration 089 seeded it in `wh_settings` and
  nothing ever read it: table rewrites are governed by `AllowTableRewrite` alone. Migration 235 deletes the row, so
  behavior is the same whatever it held. `table_rewrite_bloat_threshold`, `table_rewrite_min_rows` and
  `pending_table_rewrites` stay.
- **System events are stored by default wherever an event store is registered (#1621). Behavior change.**
  `AddWhizbang` (and `AddSystemEvents`) now register the real `SystemEventEmitter` with TryAdd in place of
  `NullSystemEventEmitter`, so the `SystemEventOptions` defaults decide what is stored: denials and permission changes
  on `$wb-system`, and audit records when audit is enabled. Before, a service that never registered the emitter itself
  stored no system events, silently. The default is a singleton that takes each record's event store and audit
  decision hook from a scope of its own, so a scoped (EF Core) store or a scoped hook is never captured, a record commits on its own rather
  than in the caller's unit of work, and a fire-and-forget record no longer shares the caller's `DbContext`. With no
  event store registered the null emitter stays. A host's own `ISystemEventEmitter` registered before `AddWhizbang`
  is the only one registered; one registered after it is the one resolved. **Upgrade:** a service that wants no
  system events turns the kinds off in `SystemEventOptions` or registers `NullSystemEventEmitter` itself.
- **No trace store is registered by default, and the in-memory store is bounded (#1601). Behavior change.**
  `AddWhizbang` no longer registers `InMemoryTraceStore` as `ITraceStore`; it stored every dispatched envelope for
  the life of the process and nothing in the library read it. With no store registered the dispatcher does no trace
  work. A host opts in with `services.AddWhizbangInMemoryTraceStore()` (or `(capacity)`), which leaves exactly one
  `ITraceStore` registration. `InMemoryTraceStore` now keeps the most recent `Capacity` envelopes (default
  `DEFAULT_CAPACITY`, 10,000), evicting the oldest first, and no query returns an evicted envelope. **Upgrade:** a
  host that resolves `ITraceStore` registers one explicitly.
- **Audit records from the system-event emitter are written on the tenant's audit ledger (#1555). Behavior change.**
  `SystemEventEmitter.EmitEventAuditedAsync`, `EmitCommandAuditedAsync` (used by the command-audit pipeline behavior)
  and an `EventAudited` or `CommandAudited` given to `EmitAsync` now append to `AuditLedger.StreamFor(tenant)`, the
  stream the outbox path has written audit records on since #1485, instead of the global `$wb-system` stream.
  `$wb-system` now holds only the global kinds: the security records and the perspective advisories.
  `SystemEventStreams.StreamFor(systemEvent)` names the stream for any system event. **Upgrade:** a reader that
  looked for audit records on `$wb-system` (`SystemEventStreams.StreamId`) reads the tenant's ledger instead;
  perspectives that consume `EventAudited` by type are unaffected.
- **Breaking (observability): the work-batch instruments are retired, and flush and claim timing is recorded
  (#1535).** `whizbang.work_coordinator.process_batch.*`, `batch.*` and `returned.*` described a single SQL call
  that no longer exists, and nothing recorded them. They are removed with their `WorkCoordinatorMetrics`
  properties. `flush.duration` is now recorded by every strategy for each flush that had work, tagged `strategy`;
  the Immediate strategy now counts `flush.empty_calls` like the others; and `whizbang.perspective.claim.duration`
  times each claim round trip. **Upgrade:** a dashboard or alert on a retired instrument moves to
  `flush.duration` (flush time), `perspective.claim.duration` (claim time) or `pg_stat_statements` (per-function
  SQL time); code that read a removed property drops the reference.
- **Breaking (configuration): `MessageProcessingOptions.InboxBatchSize`, `InboxBatchSlideMs` and
  `InboxBatchMaxWaitMs` are removed (#1536).** Nothing read them: inbox batching is configured on
  `SlidingWindowInboxOptions`, so tuning them changed nothing. **Upgrade:** set
  `Whizbang:Workers:InboxBatch:MaxSize`, `SlidingWindow` and `MaxWait` instead. The startup configuration check
  names those keys for a deployment that still sets the old ones under `MessageProcessing`.
- **Breaking (ephemeral events): `TransientStorage.InMemory` is removed (#1538).** It was never acted on: the
  read model was a persisted row either way, and a model held in one instance's memory would be lost whenever its
  stream moved. Whether an ephemeral event can stay local and in memory is open as #1549. **Upgrade:** remove
  `Storage = TransientStorage.InMemory`; the default, `PersistedRow`, is what those types already got.
- **Breaking (packaging): no shipped package depends on a dependency under a non-open-source license (#1552).**
  `Whizbang.Transports.HotChocolate` depends on `HotChocolate` instead of `HotChocolate.AspNetCore`, which also
  brought the Nitro IDE under the ChilliCream License. `Whizbang.Data.Dapper.Custom` no longer references
  `Microsoft.Data.SqlClient`, which nothing used and which brought the SNI runtime under Microsoft's proprietary
  terms. `Whizbang.Migrate` and `Whizbang.CLI` no longer bundle LibGit2Sharp, which nothing used and whose native
  libgit2 is GPL-2.0. **Upgrade:** an application that serves GraphQL over HTTP (`AddGraphQLServer`, `MapGraphQL`)
  adds a `HotChocolate.AspNetCore` package reference itself. An application that used SQL Server through the
  transitive `Microsoft.Data.SqlClient` references it directly.
- **Breaking (dispatch): every local receptor for a message runs, on every call (#1412).** A send or a local
  invoke ran only the first receptor for a command while a publish ran them all. Now every entry runs them all,
  in `[ReceptorPriority]` order. When a caller asks for a result and several receptors return one, the earliest
  answers and the others' results cascade. **Upgrade:** a command with two receptors now runs both.
- **Breaking (lifecycle): the default local stage is `LocalImmediateInline`, and `LocalImmediateDetached` is a
  background hook (#1392).** A receptor with no `[FireAt]` was registered at `LocalImmediateDetached` but awaited by
  the caller, which is Inline behavior; it is now registered at `LocalImmediateInline`, with no change in how it runs.
  A receptor declared `[FireAt(LocalImmediateDetached)]` now runs in its own scope after the handler, whenever this
  service handles the message in-process, and nobody waits for it; its exception is logged. **Upgrade:** a receptor
  that used `[FireAt(LocalImmediateDetached)]` as its message's only handler no longer handles it; remove the
  attribute or declare `LocalImmediateInline`. A runtime registration at `LocalImmediateDetached` is likewise a hook.
- **Breaking (lifecycle): `ImmediateDetached` is never waited for (#1393).** Local dispatch awaited it and a receptor
  exception reached the caller, and an inbox message with no `PostInboxDetached` receptor ran it inline, so its
  exception failed the message. It now runs in its own scope on every entry point, and a failure is logged. Tests that
  count its invocations wait for a signal rather than reading the count when the dispatch returns.
- **Breaking (dispatch): a returned value no receptor handles fails the call (#1418).** A value a receptor
  returns that is neither a command nor an event, and has no receptor of its own type, now goes to the
  receptors of an interface or base type it implements, as a mediator would, in this assembly, other
  assemblies, or registered at runtime. With none the call throws `ReceptorNotFoundException`; it used to be
  skipped with a warning. **Upgrade:** set `WhizbangCoreOptions.RequireReceptorForReturnedResult = false` to
  keep skipping.
- **Breaking (security): a local dispatch runs without a caller context by default (#1419).** With
  `AllowAnonymous = false`, whether a context-less in-process dispatch was refused depended on the handler's
  shape, and the refusal came from a lifecycle stage after the handler had already run. An in-process
  dispatch now runs, like a mediator call. `MessageSecurityOptions.RequireContextForLocalDispatch = true`
  refuses it up front, before any handler runs or anything is written. Exempt, control-plane and system
  messages are never refused. The dispatcher also now reads the application's configured
  `ExemptMessageTypes`; it read an unconfigured copy before.
- **Breaking (dispatch): the default batch-options overloads refuse options they would drop (#1415).** An
  `IDispatcher` implementation that does not override `SendManyAsync`/`PublishManyAsync` with options used to
  ignore them silently; the default now throws `NotSupportedException` naming the setting it cannot honor.
- **Breaking (build diagnostics): ten never-reported diagnostic IDs are retired, five are now reported
  (#1427).** `WHIZ006`, `WHIZ025`, `WHIZ801`, `WHIZ802` and `WHIZ805` are reported by analyzers. `WHIZ003`,
  `WHIZ005`, `WHIZ020`, `WHIZ023`, `WHIZ042`, `WHIZ050`, `WHIZ051`, `WHIZ052`, `WHIZ057` and `WHIZ803` are
  retired and will never be reused; any property type may be a stream id, so `WHIZ005` described no rule.
  `WHIZ014` and `WHIZ022` now point at the offending declaration. **Upgrade:** remove
  suppressions of the retired IDs.
- **Breaking (scope inheritance): `[InheritScope]` is applied the way it is documented, and checked where it takes
  effect.** Creation records ownership: a new perspective row copies every scope field from the message that created
  it, including the user, and `[InheritScope]` written without `OnCreate` now means the same (it used to mean tenant
  only). `Always` is implemented: the named fields follow the latest writer on every apply, while the rest of the
  row's scope stays as created; the apply read returns the row's stored scope so only those fields change.
  `ScopeInheritance.Resolve` and `PerspectiveScope.WithFieldsFrom` hold the rules. WHIZ170 now reports the attribute
  on a perspective class over another model, where the runner ignores it, instead of on models, where it works; WHIZ171
  says all-None gives rows no scope. **Upgrade:** a model with `[InheritScope(Always = ...)]` and no `OnCreate` now
  records every field at creation; add `OnCreate = ScopeFields.Tenant` to keep tenant only. Move any `[InheritScope]`
  from a perspective class to its model.
- **Breaking (build diagnostics): four diagnostic IDs that meant two things each now mean one.** The
  diagnostics without docs pages moved: `WHIZ071` "Polymorphic Base Type Discovered" is now `WHIZ072`,
  `WHIZ400` "[InheritScope] on a non-perspective type" is now `WHIZ170`, `WHIZ401` "[InheritScope] with
  all-None flags" is now `WHIZ171`, and `WHIZ811` "declared JSON converter never consulted" is now
  `WHIZ812`. `WHIZ071`, `WHIZ400`, `WHIZ401` and `WHIZ811` keep their other meanings (missing Pgvector
  package, the two multi-model `ILensQuery` checks, polymorphic property). **Upgrade:** a `#pragma` or
  `.editorconfig` entry that suppressed one of the moved diagnostics names the new ID. A build guard now
  fails when two diagnostics share an ID.
- **Breaking (Dapper driver): the schema is initialized at host start, not inside `AddWhizbangPostgres`.**
  Registration no longer connects to the database. With `initializeSchema: true` the shared schema
  initializer (`WhizbangDatabaseInitializerService`, now in `Whizbang.Data.Postgres` and used by both
  drivers) waits for the database with the `PostgresOptions` retry settings, migrates under the schema
  initialization lock the EF Core driver takes, registers the instance, runs the managed-object cleanup
  and opens `ISchemaReadyGate`, in the background by default (`SchemaInitializationOptions`). **Upgrade:**
  code that used the schema between registration and host start must wait on `ISchemaReadyGate`. A host
  with `initializeSchema: false` gets an open gate at start.
- **Both drivers register the instance at start and check the cleanup settings first.** An EF Core
  instance joins `wh_service_instances` at start whether or not a duty elector is registered, and reads
  `Whizbang:Schema:Reconcile` before it touches the database, so a bad value fails the start before the
  instance registers, as on the Dapper driver.
- **The once-per-fleet maintenance claim falls back to the claim table.** The EF Core managed-object and
  physical-column-fill steps used to stand down when no `IClaimedEmissionStore` was registered; they now
  claim through `wh_unique_emission_claims` directly (`FleetClaim`), as the Dapper step already did.

- **Role assignment: the mixed-version bridge is off by default.**
  `Whizbang:Database:RoleAssignment:HoldLegacySessionLock` now defaults to `false`. A holder no longer
  also takes the duty's legacy session advisory lock, which frees one pinned connection per held role.
  **A rolling deploy from a release older than role assignment must set it to `true` for that one
  deploy**, so an instance still running the session-lock elector cannot act alongside a new holder;
  turn it back off afterwards. With it off, a vote still refuses while an old holder is visible and a
  holder steps aside within one renewal of seeing one, but for that renewal interval both may act.
- **Audit records are now written on the idle band by default, not the background band.** Audit is
  durable rather than real-time, it is most of what a bulk load generates, and it is read days
  later if at all -- so it no longer competes with the work a person is waiting on. The idle band
  is withheld while the service is busy, and withholding is bounded by TIME rather than by the
  service happening to go quiet: a busy service still takes a bounded slice of the band once the
  oldest record passes 30 minutes, and clears the band whole once it passes 4 hours, however busy
  it is. An idle record is therefore delayed, never dropped.
  - *One switch, one place:* `SystemEventOptions.AuditPriority`. Every writer of an audit record
    reads it, so the record, the envelope it travels in and the queue row it lands on cannot
    disagree. Set it to `WorkPriority.BACKGROUND` to restore the previous behavior -- do that if
    something downstream reads the trail on a deadline, such as a live compliance feed.
  - *Security events did NOT move.* A denial, a permission change or a scope establishment stays on
    the background band, because a band that exists to be withheld under load is the wrong place
    for the record of a load that looks like an attack. A system event nobody has classified also
    stays on background, so a type added later is never silently withheld.
  - *Where the band means what:* the inbox event lanes and the perspective lanes WITHHOLD idle work
    as described above. The outbox does not withhold; it orders by priority, so an audit record
    publishes behind everything else rather than being held back. Inbox commands are not withheld
    either, their lane being unbounded above the interactive band.
- **Per-namespace command inboxes are now the DEFAULT transport topology, and the legacy catch-all
  `inbox` topic is retired out of the box.** A service configured with `AddWhizbang().WithRouting(…)`
  and no inbox/outbox call subscribes to one `inbox.<contract-namespace>` entity per command
  namespace its receptors handle, plus the system broadcast inbox `inbox.whizbang`, and to no
  catch-all; commands publish to those same entities and events keep publishing to domain topics
  unchanged. Three broker operations per command (send, deliver, settle) instead of a fan-out across
  every service bound to the shared inbox.
  - *Existing services keep working with no code change.* Explicitly selecting a legacy strategy
    (`Inbox.UseSharedTopic` / `Outbox.UseSharedTopic` / `UseDomainTopics` / `UseCustom`) also restores
    the pre-migration flip and retirement state, so the shared inbox is still named by the topology
    manifest and still provisioned by both transports.
  - *To adopt, one namespace at a time:* drop the inbox call on the handling service and add
    `KeepSharedInbox()` (it then subscribes to its per-namespace inboxes AND the catch-all — a strict
    superset); flip each publisher namespace with `RouteCommandNamespaceToInbox(ns)`; once every
    namespace is flipped, drop `KeepSharedInbox()` and delete the catch-all at the broker.
  - New switches, all configuration-bindable: `RouteNoCommandNamespacesToInbox()` /
    `Whizbang:Routing:RouteAllCommandNamespacesToInbox` (full publisher rollback) and
    `KeepSharedInbox()` / `Whizbang:Routing:RetireSharedInbox=false` (keep the catch-all while
    publishers stay flipped). `Whizbang:Routing:RetireSharedInbox` and the
    `CommandNamespacesToInbox` list now bind both directions, so a migration step or a rollback needs
    no redeploy.
- Work-pump decomposed: the claim poller returns stream-ids only and a per-stream drainer fetches bodies,
  with single-writer-per-stream ownership; every former polling worker was converted to
  NOTIFY/channel/transport-driven.
- CLR type-name encoding normalized (nested types encoded with `+`, not `.`), consolidated across the
  message-type registry via migrations.
- EFCore and Dapper event stores route all polymorphic type resolution through the shared
  `EventTypeMatchingHelper` resolver (removes duplicated per-store type maps).
- Publish path (`TransportPublishStrategy`) and both RabbitMQ + Azure Service Bus transports route wire
  serialization through the shared `WireEnvelopeSerializer`; ASB receive resolves types via the shared
  `BodyClaimWireHelper` (its type-binder / raw-receptor fallbacks preserved).
- Perspective snapshot blobs are now versioned envelopes; pre-existing unversioned snapshots are
  transparently rebuilt from events on first read.
- Default coordinator tuning: `MaxInboxAttempts = 10`, `NotifyHealthyPollingIntervalMilliseconds = 30000`.
- **One stored unit for every date, time and duration in a perspective document:** microseconds
  (an instant since the epoch, a date at its midnight UTC, a time of day since midnight, a duration
  plain), on both storage paths. The serializer's converters are global to the persistence profile and
  an EF convention converts every temporal EF maps inside a document, so nothing is generated per
  property; both paths read through one reader per kind that tolerates a rendering (counted on
  `whizbang.perspective.temporal_form_fallbacks`) and refuses anything else in the same words. Rows
  written by an earlier release are converted at startup by a rewrite derived from the readers, gated by
  the `wh_perspective_forms` ledger (migration 153), on the migrator after the election; a date's index
  now casts through `bigint` and is renamed for it. Deploy a unit-changing release without a mixed
  fleet; the migrator warns about other releases still alive.
- **Contributing: a pipeline change needs a build admin's approval of its latest commit, checked by CI
  (#1479).** A pull request that changes the pipeline, the gates or the build inputs (the files
  `.github/CODEOWNERS` assigns to build admins) now passes the required checks only when a build admin
  opened it, or a build admin approved its latest commit; a commit pushed after the approval needs
  approving again. The new `Gate · Pipeline owner` check enforces it, on the pull request and in the merge
  queue, and runs again when a review is submitted or dismissed. It replaces the code owner review the
  branch rules required, which a build admin's own pull request could never satisfy, so a build admin's
  pipeline change now merges through the merge queue like any other change.
- **Contributing: a pull request cannot remove the pipeline-owner check that applies to it (#1566).**
  The `Gate · Pipeline owner` job runs from the pull request's own `ci.yml`, so a pull request could
  delete it. PR intake now runs the same check from the base branch when a pull request enters the merge
  queue, and takes a pull request that fails it out of the queue with a comment saying why.
- **Contributing: CI can run on team machines or Blacksmith, and falls back to GitHub-hosted runners
  (#1762).** A new `Plan · Pick runners` job chooses, per job class, an idle machine in the organization's
  self-hosted pool (trusted runs only), then Blacksmith when the `CI_BLACKSMITH_RUNNER` variable names a
  label, then `ubuntu-latest`. The build, format, unit, component, integration and analysis jobs follow
  it; pack, publish, release and gate jobs stay on GitHub-hosted runners, as does the compile of a push
  run. Format, the no-op rebuild check, the unit, component and pipeline script tests run on any OS
  and architecture (pwsh steps, the framework-dependent build run with `dotnet test --test-modules`,
  each project's coverage source roots pointed at the job's own checkout); compile stays Linux X64 and
  the container suites stay on Linux with Docker. Any missing secret, unset variable, API error or timeout falls back, and the picker can never
  fail the gate; with nothing configured every job runs where it did before. Setup, the label
  convention and the variables are in `.github/WORKFLOWS.md`, "Runner selection".

### Fixed
- **A failed pending-events statistics query is reported instead of swallowed.** The perspective worker refreshes
  `whizbang.perspective.pending_events` about once a minute and discarded every failure of the query behind it
  (`IWorkCoordinator.GatherStatisticsAsync`: a timeout, a missing permission, a function a partial migration never
  created), so the gauge kept its last value with nothing to explain it, and a frozen number read as real lag or real
  health. The worker still keeps running, and now logs the first failure of a streak at Warning with the exception,
  naming the query and the stale gauge, logs further failures in the streak at Debug, logs at Information when a refresh
  succeeds again and how many it missed, and counts every failed refresh on the new counter
  `whizbang.perspective.pending_events_refresh_failures` (`PerspectiveMetrics.PendingEventsRefreshFailures`)
  (Fixes #1865)
- **A rewind's write is checked against the row version it read, and a refused rewind is reported.** A rewind replays a
  stream in memory and writes the row once at the end, without a row version, so the store's ordering guard was the
  only check on it: when another writer folded a newer event into the row while the rewind replayed (after a lease
  expiry or an orphan sweep, for example), the rewind's frontier sorted below the row's stamp, the guard refused the
  write, and the rewind reported itself complete with its correction lost and nothing logged. The generated runner now
  reads the row version before the rewind reads any event and writes against it, as a live apply does (#928). A row
  that moved refuses the write with `PerspectiveRowConflictException`, and the replay runs again from the same restore
  point, now including the newer event, so the row ends with both; a row that moves under every one of five replays
  fails the rewind, which the worker counts on `whizbang.perspective.rewinds_failed` and retries. A rewind write the
  guard refuses on a row that did not move is logged at Warning as `[diag.rewind.write.refused]` with the frontier event,
  instead of passing as applied. Stores that track no row versions write exactly as before (Fixes #1863)
- **Each host derives saga per-item stream ids from its own namespace.** `SagaItemStreams.AppDefaultNamespace` is one
  process-wide value that every `AddWhizbangSagas` call overwrites, and the framework's per-item readers derived from
  it at call time, so in a process with several hosts (integration tests, a test server per service) the last
  registration decided the namespace for all of them. A host registered without the setting reset it to the default,
  and another host's stranded-item watchdog then derived ids for streams it never writes, read items whose terminal
  events were stored as not terminal, and failed them, with nothing logged. `AddWhizbangSagas` now registers an
  `ISagaItemStreamIdDeriver` (`SagaItemStreamIdDeriver`) built from that container's own `SagaOptions`, and a saga
  service given the options its host registered (as the container injects them) derives under that host's namespace in
  its watchdog, stranded-item resolution and reconciliation. `SagaItemCompletionReconciler.ResolveCompletionCountsAsync`
  and `ReconcileTerminalCountsAsync` gain overloads that take the deriver. `SagaItemStreams.Of(sagaId, item)`, the
  existing reconciler overloads, and a service given options it built itself or none keep the process-wide namespace,
  now documented as such; code running in a host should inject the deriver (Fixes #1888)
- **The transport batch collector and the batch flusher run on an injected clock.** `TransportBatchCollector<T>` armed
  its sliding window and hard max on raw `System.Threading.Timer`s, and `BatchFlusher<T>` measured its coalesce
  window with `Environment.TickCount` and waited out its retry backoff and its shutdown drain bound on the real clock,
  so neither could be driven by a test clock and their tests slept. Both now take an optional `TimeProvider` (null
  keeps the system clock). The RabbitMQ and Azure Service Bus transports hand their own clock to the collectors they
  build, `InProcessTransport` gains a constructor that takes one, and the five flush workers (outbox and perspective
  completions, failures, lease renewals, inbox handler commits) take an optional `TimeProvider` that the container
  fills with the host's registered clock. The flusher's coalesce window now waits on the channel instead of spinning
  with `Task.Yield` until its deadline, so an idle window no longer burns a thread-pool thread; a batch already at
  `ImmediateFlushThreshold`, the first item included, flushes without opening a window; and shutdown flushes a batch
  whose window is still open at once instead of sitting it out. The collector and flusher tests run on a fake clock
  and wait on signals (Fixes #1868).
- **Stream integrity repairs an event inbox deduplication remembers but the event store lost.** A redelivered event
  whose message id was already in `wh_message_deduplication` but which the consumer's event store no longer held (a
  failed fan-out in an older version, manual cleanup, a store restored from an older backup) was dropped as a duplicate
  on every redelivery, so its bucket never healed and repair asked for it on the terminal cadence forever. The children
  of a redelivery bundle now carry `InboxMessage.RestoreIfMissing`, and for them `store_inbox_messages` (migration 252)
  checks deduplication against the event store: a child neither the event store nor the inbox holds is stored again,
  as state only, so it is projected without running trigger receptors a second time. A duplicate of a stored event is
  still dropped, ordinary traffic keeps trusting deduplication, and ephemeral and at-most-once events are never
  restored. A child on a stream whose earlier history was truncated here is refused, because it cannot be told from
  an event the truncation removed on purpose: it is stored settled with the reason, the reconciliation reads it
  (`wh_reconcile_redelivered_bundle_by_first_hop` from migration 253, `RedeliveredBucketPresence.RefusedTruncated`,
  `RedeliveryReconciliation.RefusedTruncated`), and the bucket is marked as one repair cannot change and reported once
  (Fixes #1843)
- **A redelivery no longer stamps its origin on events this service authored.** Origin adoption on redelivery
  (#1790) could not tell an event received before origins were recorded from one this service authored, since both
  have empty origin columns; when an origin's own store had filed events received from this service as its own, its
  redelivery would stamp itself as their origin, and the origin would then alarm on a surplus it could never repair.
  Before an empty origin is filled, the receiver now reads the stored event's hops and adopts only when the first hop
  names the bundle's origin service (the service on the bundle's publish hop, compared without case). A first hop
  with no service identity (the slim stored form), no hops, or a bundle naming no service adopt as before, so that
  history still converges; an event that already carries an origin is unchanged. Refused events keep their empty
  origin and local digest, are counted per bucket (`RedeliveredBucketPresence.RefusedForeignOrigin`,
  `RedeliveryReconciliation.RefusedForeignOrigin`) and on the new counter `whizbang.stream_integrity.origins_refused`,
  and are reported in one Warning per bundle. Migration 253 adds `wh_reconcile_redelivered_bundle_by_first_hop`, the
  one function the coordinator calls per bundle (it also returns the truncation refusals and replaces 252's
  `wh_reconcile_redelivered_bundle`);
  `IWorkCoordinator.AdoptRedeliveredOriginAsync` takes the origin's service name, `RedeliveryReconciler.ReconcileAsync`
  accepts it, and `RedeliveryReconciler.OriginServiceNameOf` reads it from a bundle's envelope. The 249 function is
  kept for versions that still call it (Fixes #1844)
- **The perspective worker's idle poll and its other waits run on the injected clock (Fixes #1861).** `PerspectiveWorker`
  takes a `TimeProvider` but waited out its idle poll, the blocking startup rewind scan's re-poll and the stream lock
  keepalive with `Task.Delay` on the machine's clock, and stamped affinity-gate activity and sent completions with
  `DateTimeOffset.UtcNow`. Each now uses the worker's `TimeProvider`, so a host or test that supplies one decides when
  the worker polls again, renews a lock, sweeps an idle gate or ages a sent completion, and the blocking scan's reported
  duration is measured on that clock. With no provider supplied (`TimeProvider.System`) nothing changes.
- **A host stopped twice at once no longer fails its stop-time batcher flush on a disposed service provider (Fixes
  #1875).** A test host built from the application's entry point (`WebApplicationFactory<Program>`) is stopped by the
  factory's dispose and, once `ApplicationStopping` fires, by the application's own `Run`, which disposes the host as
  soon as its own stop returns. When that stop finished first, the factory's stop reached `BatchStrategyShutdownFlush`
  with the container already disposed, and resolving the batchers threw `ObjectDisposedException`. Every stop now
  shares the one flush the first stop started, so the buffered batches are still stored while the provider is alive,
  and a stop that arrives after the container disposed the flush service resolves nothing (the container already
  disposed, and so flushed, the batchers it owned).
- **A stored event read back without hops keeps its whole scope on every driver path.** The Dapper typed read (through
  the event envelope adapter) rebuilt a hopless event's scope from the tenant and user alone, the Dapper polymorphic and
  checkpoint-range reads from the four id fields, and EF Core reconciliation from the tenant and user, so allowed
  principals, scope extensions and the system and declared-unscoped markers were dropped and a receptor or perspective
  reading them after a replay, rebuild, redelivery or reconciliation saw less than the original dispatch did. Every
  read-back path now restores the stored scope through one reader (`StoredScope`), field for field as the EF Core event
  store does; a first hop that recorded its own scope still keeps it, and a row stored without a scope still reads back
  unscoped. The hop-scope source guard now allows no tenant-and-user-only hop scope anywhere (Fixes #1855)
- **An outbox message the producer-side batcher accepted is never dropped when its store fails (Fixes #1871).** An event
  a receptor returned through `LocalInvokeAsync` (or one it published) is buffered per stream and stored by that
  stream's worker after the caller has returned. A store that failed was logged ("Dispatcher re-emission will
  recover") and its batch dropped, though nothing re-emits it, so a burst over many streams against a remote database
  lost a random subset of events: the per-stream flushes overflowed the connection pool, and a pool or server
  connection failure is not retried by the store. Now a transient database failure (lost connection, exhausted pool,
  command timeout, server out of connection slots, deadlock) is retried on the stream's own worker with backoff on the
  strategy's `TimeProvider`, so the stream's order is kept (`Whizbang:Workers:OutboxBatch:MaxFlushAttempts`, default 5;
  `FlushRetryBackoff`, default 100 ms; `FlushRetryMaxBackoff`, default 5 s). A batch that still cannot be stored, fails
  with an error a retry cannot fix, or is abandoned by a stop past its deadline is written to `wh_dead_letters` as
  `wh_outbox` dead letters with the new reason `OutboxStoreFailed` (23), through `wh_store_outbox_dead_letters`
  (migration 256), which also logs each message in `wh_log`; dead-letter recovery then stores it through
  `store_outbox_messages` (default policy: five recoveries a minute apart, then held for review). It is counted
  (`whizbang.work_coordinator.outbox_flush.failed_messages`, tagged `cause` and `outcome`; retries in
  `whizbang.work_coordinator.outbox_flush.retries`), reported to every registered `IOutboxFlushFailureObserver`, and
  logged at Error with every message id and how to recover it; when the dead-letter write fails too, the log says the
  messages are lost unless re-sent. A stop past its deadline now waits for that report instead of discarding what was
  buffered. The outbox and inbox batch flushes take a slot of the `WorkCoordinatorGate`, so concurrent per-stream
  flushes cannot open more connections than the gate allows. `LocalInvokeAsync` still does not wait for the write.
  `IWorkCoordinator` gains `StoreOutboxDeadLettersAsync`, implemented by both Postgres drivers.
- **Stored keys that differ from a declared JSON name only in case are rewritten at startup and read back, not
  defaulted (Fixes #1874).** Releases before the message JSON context honored `[JsonPropertyName]` (#920) stored a
  property under its C# name. Where that differs from the declared name only in case (`"X"` for
  `[JsonPropertyName("x")] X`), Entity Framework's mapped reader matched the declared name exactly, found no key, and
  read the member's default with no error: a row loaded "successfully" with every such value zeroed. The stored-form
  rewrite phase (EF Core) and the schema pass (Dapper) now find stored keys that match a declared name only
  case-insensitively, at any depth and inside collections of records, and move them to the declared name
  (`DeclaredKeySpellingPass`). The declared names come from what reads each document: Entity Framework's model for a
  mapped document, the serializer's metadata otherwise; the Dapper driver learns each table's model type from a
  `typeof` the generated schema registers at load (`PerspectiveDocumentModels`), with no reflection. The scan walks
  each table in primary-key order in bounded chunks, returns only the rows its predicate finds, and conditions each
  update on the row version it read. Each table rewritten is logged at Warning with the member paths and row counts
  (`Rewrote stored keys to their declared spelling in public.wh_per_picture: 56 row(s) updated; data
  $.Objects[*].Points[*]: X -> x in 56 row(s); ...`). Both spellings with the same value merge; with different values,
  or a key matching two declared names, the row is left as it was and startup stops naming the table, column, row and
  path (`StoredFormConversionBlockedException`). Each table checked is recorded in `wh_stored_form_migrations` as
  `declared-names:<table>:<hash of its declarations>`, settled by the first pass that changes nothing and finds nothing
  missing, after which it is skipped with one lookup until its declarations change. A `required` member (or
  `[JsonRequired]`) is enforced on read-model rows only: the generated message JSON context marks it required on a
  perspective model and on the types only perspective models carry, so a stored row missing it fails the read with a
  `JsonException` instead of defaulting. Events, commands and every other message keep reading a missing member as
  its default: events are history, read tolerantly and upcast, so a replay or rebuild never fails because a later
  change added `required`, and new messages are still protected by the compiler's `required` at construction. A type
  a perspective model shares with an event is read tolerantly too, since one type has one reader, and its required
  members are declared to the startup check (`DeclaredRequiredMembers`). A required member whose type admits null is
  never marked, because the framework omits a null when writing. Entity Framework's mapped reader cannot refuse a
  missing key, so the startup pass reports stored rows missing a required member at Error, naming the path, the row
  count and sample rows, on every path and for shared types as well. Events are history and are not rewritten: the
  combined serializer options now read property names case-insensitively, so an event stored under the C# name
  replays with its values; writing still uses the declared name. **Upgrade:** the first start rewrites affected rows;
  deploy without a mixed fleet, or let a second start catch rows an older instance wrote meanwhile (a pass that
  changed rows is not settled). A row holding both spellings with different values stops startup until one key is
  removed. A `required` non-nullable member missing from a stored perspective row of a type only perspective models
  carry now fails that row's read; fill old documents with `[StoredForm(DefaultWhenMissing = ...)]` or drop the
  `required` modifier. Stored events and other messages are unaffected. A type with two
  members whose JSON names differ only in case now fails at first use with System.Text.Json's name-conflict error,
  because names are matched case-insensitively; rename one with `[JsonPropertyName]`. A stored key that differs from
  its declared name by more than case (a C# name `Points` declared `pts`) is not moved; declare it with
  `[StoredForm(PreviousName = "Points")]`.
- **A batch drain worker no longer runs with the ambient context of the caller that started it (Fixes #1883).** The
  outbox, inbox and apply sliding-window batchers and the per-stream serializer start a stream's drain worker from
  inside the first append for that stream, and `Task.Run` copied that caller's `ExecutionContext` into the worker. Every
  later flush of the stream then saw the first appender's `AsyncLocal` values: its pinned connection
  (`PinnedConnectionContext.Current`) after the caller released it, or while the caller was still using it, and its
  scope, tenant and activity, carried into flushes of other callers' messages. Drain workers now start with the flow
  suppressed, so a flush sees no ambient state from whoever appended first.
- **An inbox message the inbox batcher accepted is never dropped when its store fails (Fixes #1885).** The default
  `IInboxBatchStrategy`, `SlidingWindowInboxBatchStrategy`, logged a failed per-stream store and dropped the batch,
  counting on transport redelivery. But `AppendAsync` returns once the message is buffered, so the code that appended
  it may already have settled it with its broker, and the batcher holds no acknowledgement it could withhold. (The
  transports' own receive paths do not use this batcher: they store each delivery before settling it, so a failed
  store there already leaves the delivery for the broker to re-deliver.) The inbox batcher now does what the outbox
  batcher does since #1871: a transient database failure is retried on the stream's own worker with backoff, keeping
  the stream's order (`Whizbang:Workers:InboxBatch:MaxFlushAttempts`, default 5; `FlushRetryBackoff`, default 100 ms;
  `FlushRetryMaxBackoff`, default 5 s). A batch that still cannot be stored, fails with an error a retry cannot fix, or
  is abandoned by a stop past its deadline is written to `wh_dead_letters` as `wh_inbox` dead letters with the new
  reason `InboxStoreFailed` (24), through `wh_store_inbox_dead_letters` (migration 257), which also logs each message
  in `wh_log`; a message whose store had committed is skipped. Dead-letter recovery re-enters it through the inbox
  store like any inbox dead letter (default policy: five recoveries a minute apart, then held for review). It is
  counted (`whizbang.work_coordinator.inbox_flush.failed_messages`, tagged `cause` and `outcome`; retries in
  `whizbang.work_coordinator.inbox_flush.retries`), reported to every registered `IInboxFlushFailureObserver`, and
  logged at Error with every message id. `IWorkCoordinator` gains `StoreInboxDeadLettersAsync`, implemented by both
  Postgres drivers.
- **Stream integrity converges on history received before origins were recorded, and stops re-requesting a bucket
  repair cannot change.** Events a service stored before migration 087 keep empty origin columns, so origin-keyed
  verification (checkpoint counts, the origin's digest lane) counted every one as missing; repair asked the origin to
  redeliver them, the store skipped each redelivered event as a duplicate, and the next check asked again, a standing
  loop bounded only by the repair drain rate. A committed redelivery bundle is now reconciled with the events the store
  already holds (`RedeliveryReconciler`, migration 249 `wh_adopt_redelivered_origin`): an empty origin takes the
  bundle's origin and an empty origin commit sequence the event's sequence (a recorded origin is never overwritten or
  re-attributed, and the local service adopts nothing), the event's digest moves from the local lane to the origin's,
  and sealed epochs over it are refolded. The origin is the only source of those sequences, so this history is
  backfilled by the one redelivery verification asks for, then verifies like any other. A bundle whose events were all
  already stored under their origin marks its buckets in the repair ledger (`wh_integrity_ledger.unhealable_at`,
  `IIntegrityRepairLedger.MarkUnhealableBatchAsync`): neither the burst grant nor the paced drain asks for them again,
  the cooldown stops re-reporting them, and each is reported once (a warning naming the bucket); a changed signature
  or a heal lifts the mark, and a bucket whose origin never answered keeps the terminal cadence. New counters
  `whizbang.stream_integrity.origins_adopted` and `whizbang.stream_integrity.repairs_unhealable`; `IWorkCoordinator`
  gains `AdoptRedeliveredOriginAsync` and `IntegrityMarkUnhealableBatchAsync` (Fixes #1790)
- **Stream integrity no longer treats history stamped after an upgrade as live gaps.** Right after an upgrade the
  historical events are unstamped, and the commit-order stamper gives them sequences over the next minutes or hours.
  Three parts of stream integrity counted that history as new live traffic. The first checkpoint baselined at the
  highest stamped sequence, so the history stamped later fell into the following windows, where no consumer can hold it
  under those sequences, and every bucket became a gap that was confirmed and repaired. The baseline now also records a
  history floor (`integrity_checkpoint_history_floor` in `wh_settings`, the last insertion-order value handed out), and
  windows count only events written after it; a store an earlier version baselined takes the floor on its next advance.
  The local lane's type-level digest folded unstamped events into its open window; it now waits for the stamp, as the
  windowed answer and the settled maximum already did (migration 250), while a received event with no origin sequence
  still folds there. Coverage-gap detection reported unstamped events, which the stamp fence holds back from
  perspectives, as never folded and asked for rebuilds; it now skips them, and its query walks streams one at a time
  and stops at the report cap instead of grouping the whole store first. (Fixes #1791)
- **A lease renewal extends only the leases the renewing instance holds (Fixes #1852).** Renewal matched rows by id
  alone, in every category, so an instance whose lease had lapsed, and whose row another instance had then claimed,
  extended the new holder's row and stream leases on its next renewal; the row stayed away from the instance working
  it and both treated it as theirs. Renewal now goes through `wh_renew_held_leases` (migration 255), which renews a row
  only while the renewing instance holds it and returns the ids it did not renew (re-claimed, completed or gone).
  `IWorkCoordinator.RenewLeasesAsync` is replaced by `RenewHeldLeasesAsync(category, instanceId, ids, leaseSeconds)`,
  which returns those ids, on both Postgres drivers, and every caller stops holding them: the inbox dispatch keeper
  drops them from its held rows without dispatching their queued copies or extending a running handler's deadline,
  the outbox publisher's not-ready hold stops re-buffering them, the outbox drain's hold no longer reports them as
  held, and the lease renewal worker (which now takes the instance provider) logs them. Each is logged at Warning.
  `renew_leases` is left in place, unused, for instances still on the previous release during a rolling deployment.
- **An ephemeral perspective drops a late event once, instead of on every claim (Fixes #1862).** When a late event
  lands behind an ephemeral perspective's row and no readable snapshot floor precedes it, the rewind is skipped by
  design (a replay would read reaped bodies) and the event is dropped. The skip reported only "nothing replayed", so
  the event's work stayed pending: the drain loop and every later claim found it behind the cursor again, skipped the
  same rewind and logged the same drop, without end and across restarts. The skipped rewind now names the dropped
  event (`PerspectiveCursorCompletion.DroppedStragglers`), and the worker retires its work once (rows completed with
  nothing applied, cooldown and rewind memory marked, lifecycle signaled) without moving the cursor, then applies the
  rest of the batch; a second straggler in the batch is dropped the same way. On the work-item path the events after
  the cursor take the normal path instead of being skipped with the rewind. Work an older build left pending is
  retired at the first claim after upgrading, and the cursor's rewind flag clears once nothing is pending. Each drop
  logs one Warning (the misleading "performing full replay" warnings no longer precede it for an ephemeral
  perspective) and counts once on the new `whizbang.perspective.stragglers_dropped` counter (tag `perspective_name`).
  No migration.
- **Inbox commit failures are logged at Warning, and a dropped control-plane row keeps its last error (Fixes #1789).**
  A per-row failure returned by `commit_handler_batch` was counted only in a Debug line. It is now logged at Warning
  with the message id, type and error before it is routed to the failure channel; one flush logs the first five and
  summarizes the rest, and a failure result naming no request is logged too. The control-plane drop line now carries
  the row's last recorded error and names its cause: a lease that lapsed without a reported outcome, a recorded
  failure, or none recorded. `HandlerCommitRequest` gains an init-only `MessageType` for the log line.
- **Inbox rows this instance holds keep their leases and are queued once (Fixes #1788).** Nothing renewed inbox
  leases, so a row queued in memory past its lease lost it without being dispatched, was re-claimed with another
  attempt charged, and was queued again while its first copy waited (the drain re-queued every leased row each time
  the claim re-offered its stream). The inbox channel writer now tracks each held row as queued, dispatching or
  waiting for its commit. The dispatch worker's lease keeper renews held rows once half their lease is spent (up to
  `LeaseHandleOptions.MaxRenewalsPerWork` per state, extending a running row's dispatch deadline with it); a row whose
  renewals run out is logged with its type and state, and one never dispatched is handed back with its attempt
  refunded. Only the first copy of a row dispatches, the drain skips rows already held, a row stays held until its
  commit lands, and rows retired at the attempt bound no longer count as drain throughput. The lease renewal
  killswitch (`Whizbang:Workers:LeaseRenewal:Enabled`) turns the keeper off. No migration.
- **Inbox memory is bounded by payload bytes, not row counts (Fixes #1793).** The deserialize cache is bounded by
  `Whizbang:Workers:InboxDeserializeCache:MaxBytes` (default 64 MiB, measured as each payload's UTF-8 JSON) as well as
  its entry count, does not cache a payload larger than the budget, and drops a row's entry when the row completes.
  The rows held in memory for dispatch are bounded by `Whizbang:Workers:InboxDrain:MaxHeldPayloadBytes` (default
  128 MiB; zero disables): a fetched row that would exceed it is not queued, and it and the rest of its stream in that
  fetch are handed back with their attempts refunded, to be offered again once the working set drains. With nothing
  held one row is always admitted, however large.
- **A fresh or upgrading database no longer deadlocks its migration against the service's own workers (part of
  #1636).** The migrations that add columns to the instance and role tables took `ACCESS EXCLUSIVE` on them on every
  run while the same instance's role electors, partition assigner, notification connection and work-available probes
  used them with the schema gate closed, so the pass met `40P01` repeatedly and finished on a lucky retry. Four
  changes, on both Postgres drivers:
  - *Idempotent DDL takes no lock when there is nothing to do.* `ALTER TABLE ... ADD COLUMN IF NOT EXISTS` and
    `CREATE INDEX IF NOT EXISTS` take their table lock before they look; each such statement, in every migration, the
    core tables' ensure and the bootstrap closure, now runs inside a `DO` block that asks the catalog first
    (`CatalogCheckedDdl`), so a re-run, and a migration re-applying what the bootstrap created, takes no lock on a
    table its workers use. Applied as the SQL is executed: no migration file and no ledger hash changes.
  - *Each worker starts at its stage.* The duty holder (maintainer votes, and the vacuum on winning) and the partition
    assigner start at Coordinate, the notification connection and its alive-lock at the end of Preflight; the
    work-available probes send nothing while the schema-ready gate is closed, and the `roles` health component reads
    nothing before Elect. The migrator election still runs in Elect. The alive-lock claim no longer calls a function
    the migrations have not created yet: it waits, at Debug, and is made on a later keepalive (it used to fail with
    `42883` at Warning and never be made on that connection). The Dapper start reports Elect as skipped once the
    database answers.
  - *A deadlocked step is retried in place.* A step (a file on the Dapper driver) that loses a `40P01` runs again after
    100 ms doubling to 2 s, logged at Warning as `Migration X of Y - Deadlocked : <step> : 40P01 ...; retrying the
    step in <delay> (deadlock n of 5)`; nothing before it runs again, and after five it fails as any step does.
  - *A second initialization waits for the first.* `EnsureWhizbangInitializedAsync` called while the host's own
    initialization was still running returned at once ("already initialized"); it now waits for it, and runs one of
    its own if that one failed. `app.Services` and the root scope the hosted initializer is built from are now one
    host to it.
- **A message's hop scope carries the request's scope extensions and the claims the application lists (Fixes #1854).**
  The hop scope a dispatched message carried was rebuilt from the tenant and user id alone, so a scope extension (for
  example one `WhizbangScopeOptions.ExtensionClaimMappings` maps from a claim at the HTTP edge), the customer and
  organization ids and every claim were dropped at the first hop, and a receptor in another service, and every event it
  emitted, saw an empty `Extensions` list and no claims. Every dispatch path (send, publish, batches, scheduled and
  deferred messages, local runs, cascades, the transport bridges and the event-store decorator) now builds its hop scope
  through one helper, `HopScope`, which carries the whole ambient `PerspectiveScope`. Claims cross a hop only by type,
  when the application lists the type in `WhizbangScopeOptions.PropagatedClaimTypes`
  (`Whizbang:Scope:PropagatedClaimTypes`, empty by default), so no token or personal claim is copied into message
  metadata unless it is listed. A receiving service restores both into its scope context
  (`IScopeContext.Scope.Extensions`, `IScopeContext.Claims`) and passes the restored claims on to the messages it
  dispatches. The allow-list rides on the scope context as `SecurityExtraction.PropagatedClaimTypes` and
  `ImmutableScopeContext.PropagatedClaimTypes`. Extensions and claims travel only with the tenant and user they were
  established for, and a scope with no tenant and no user still yields no hop scope. The wire format is unchanged: the
  hop scope already had fields for extensions and claims, and older receivers read them.
- **An event in an absorbed namespace reaches the event store when nothing on the service consumes it (Fixes #1776).**
  Both consumer workers decided "no local consumer" from the receptor registries alone, so an unconsumed event in a
  namespace the service absorbs (`RoutingOptions.AbsorbNamespaces`) was stored settled as `no_local_consumer` and never
  copied into the event store, and a perspective added later had nothing to rebuild from. The transport consumer and
  the Service Bus consumer now make one shared decision that asks the message-discard policy when the registries find
  no consumer, so such an event is stored as a live inbox row and the claim copies it into the event store. A child of
  a composite in an absorbed namespace is likewise kept at expansion instead of being dropped. Nothing here takes the
  event, so the dispatch gate settles the row on its first claim without dispatching, retrying or dead-lettering it,
  and now reports that skip as `NoLocalConsumer` (Debug) rather than `RegistryChanged` (Information). An unconsumed
  event in a namespace that is not absorbed is stored settled as before.
- **On the Dapper driver, a perspective table that gains a column is migrated by column copy instead of an event
  replay (Fixes #1826).** Choosing how to migrate a changed perspective table compares each existing column's type,
  read from the database, with its type in the new DDL. The database reported `timestamp with time zone` and
  `character varying` where the DDL said `TIMESTAMPTZ` and `VARCHAR(n)`, and the DDL parser kept `UUID PRIMARY KEY`
  as the type of `id UUID PRIMARY KEY DEFAULT ...`, so every perspective table's change (adding one column included)
  was classified as destructive and rebuilt by a background event replay. Both sides are now compared in the spelling
  `format_type` reports (`PostgresColumnType.Canonicalize`): `TIMESTAMPTZ`, `VARCHAR(n)`, `INT`/`INT4`, `BOOL`,
  `DECIMAL`, `FLOAT8` and the other aliases match their long forms, and an array or schema-qualified type such as
  `vector(n)` matches too. A real type change (`TEXT` to `INTEGER`, or a different length or precision) is still
  destructive. The existing columns are now read from `pg_attribute`. No migration. The EF Core driver does not choose
  a migration strategy from column types, so it is unaffected.
- **The commit-order stamper drains a whole-store backlog in time proportional to its size (Fixes #1786).** After an
  upgrade that left every historical event unstamped, the stamper's batch fell to one row per call and stayed there.
  Two causes, both fixed. The stamp (migration 248) is now four statements that can each be planned only one way: the
  walk reads its batch through the insert-transaction index and stops, with sorting off for it; no step joins the
  batch's own intermediate sets (205's one statement was planned as nested loops on the small estimates an unknown
  backlog gives, so a 1,000-row call compared about a million pairs, twice); and the update reaches each row by key.
  The order, the visibility fence and the signature are unchanged. The batch sizing (`CommitOrderStampSizer`) tells a
  fixed per-call cost from one that follows the size: when a halved call cost at least three quarters of the call
  twice its size, the batch stops shrinking there, and any call well inside the budget grows the batch again, past
  any size a slow call or a timeout backed off to, instead of the cap holding until the backlog drained.

- **A receiver stores every event under the stream its producer stored it on (Fixes #1798).** A typed composite's
  children were filed under the composite's stream, a re-delivery bundle's children under the bundle envelope's id
  (a random stream), and a lone audit record or an event sent through `TransportManager.PublishToTargetsAsync` or
  `DispatcherTransportBridge` under its message id, because nothing on the hop named the stream. Live apply keyed the
  row by that stream, so the event folded onto a row no reader asks for and never reached the row it is about, and a
  later rebuild recorded the wrong stream's position on the right row, which the ordering guard then held against
  every later rebuild. Every hop writer and every inbox-row builder now goes through one helper, `StreamIdentity`; a
  re-delivery bundle records each child's stream (`RedeliveryComposite.InnerStreamIds`), with a fallback for bundles
  from older senders. A parity suite stores each delivery shape the producer's way and the receiver's way and checks
  they agree, and a source guard fails when a new path reads or writes the hop's stream outside the helper. Events
  already misfiled are moved by the operator repair `IFanoutChildRefiler` (registered by both PostgreSQL drivers),
  which calls the new `wh_refile_events` function (migration 247) to renumber both streams and move each event's
  digest, inbox and applied-ledger rows in one transaction, then names the streams to rebuild.
- **With the paced repair drain on, `MaxAutoRepairRequestsPerAudit` caps what one comparison adds to the drain, and
  the drain's backlog has a total bound (Fixes #1792).** The drain (`RepairDrainEnabled`, the default) dispatches from
  the repair ledger, and a manifest comparison wrote every deficit bucket of every chunk into it: the per-audit cap was
  applied only on the burst path, so with the drain on it capped nothing, and the ledger grew with the number of
  divergent streams and accumulated across audits. At `AutoRepairCapped` with the drain on, a comparison now admits at
  most `MaxAutoRepairRequestsPerAudit` new deficit buckets, and none once the ledger holds the new
  `Whizbang:StreamIntegrity:MaxRepairDrainBacklog` per-stream rows (default 10,000, at least 1, checked at host start).
  A bucket the ledger already tracks is refreshed without counting. A deficit over either cap is deferred, not
  dropped: nothing is written for it, it stays divergent, and a later audit re-detects it once the drain has made room.
  Each comparison that defers logs one warning with the counts and the backlog, and
  `whizbang.stream_integrity.repairs_deferred` counts them by origin and reason (`per_audit_cap`, `backlog_cap`).
  `IWorkCoordinator` gains `IntegrityProbeRepairBacklogAsync`, one bounded read that answers the backlog and which keys
  are already tracked; without it (or when it fails, logged) the per-comparison cap alone applies. The burst path
  (`RepairDrainEnabled = false`) and `ReportOnly` are unchanged. No migration.
- **The paced repair drain claims only deficits; identity mismatches and local surpluses are no longer re-requested
  (Fixes #1842).** A manifest comparison writes a ledger row for every divergence it reports, and the drain's claim
  selected rows by origin, stream and backoff alone, so with `RepairDrainEnabled` (the default) it asked the origin to
  redeliver mismatches and surpluses on every backoff step, a redelivery that cannot change them, and those rows counted
  toward `MaxRepairDrainBacklog` and could defer real deficits. The comparison now states each divergence's kind when it
  reports it (`IntegrityReportObservation.IsDeficit`, required), recorded in the new `wh_integrity_ledger.is_deficit`
  column by the new report batch `wh_integrity_report_divergences` (migration 251); the ledger row cannot derive the kind,
  because its digest halves are hashes, not counts. The claim (`wh_integrity_claim_repair_drain`) takes deficit rows only,
  and the backlog probe counts only the rows the claim can take (deficits, not marked unhealable) and treats a bucket as
  tracked only when the ledger holds it as a deficit; both read a new partial index over exactly those rows. A row of
  unknown kind is never claimed: rows recorded before this release (no backfill is possible; the next comparison that
  sees the bucket records its kind), and rows whose signature a report without the kind moved (the per-bucket fallback,
  or an older version during a rolling upgrade). Mismatches and surpluses stay in the ledger for reporting.
- **A rebuild folds a row that events were re-keyed onto from its own stream and every re-keyed source (Fixes #1820).**
  A row that received events re-keyed from another stream was rebuilt from an empty model with only the re-keyed
  events, so its own history was lost. The rebuild now collects every re-key in the run and folds each target row
  once, from its own stream and all its sources, merged by commit sequence with each stream's own order kept.
  `IPerspectiveRunner.RunRekeyedRowAsync` is a new default member; routing lives in `RebuildRowRouting`. A rebuild of
  selected streams that names a target but not its sources cannot see the events re-keyed onto it; rebuild the sources
  with it.
- **An Azure Service Bus subscription whose processor stops for good now reports a disconnect (Fixes #1838).**
  Nothing raised `OnDisconnected` for Azure Service Bus, so a processor that could no longer receive left its
  destination Healthy while the service silently stopped consuming from it. The transport's processor error handler
  now raises it, and records `Disconnection`, for a receive or session-accept failure the processor will not get past
  by retrying: a non-transient `MessagingEntityNotFound` or `MessagingEntityDisabled`, an `UnauthorizedAccessException`,
  or an `ObjectDisposedException` (the processor or its client disposed underneath the subscription). The consumer
  then replaces the subscription, which provisions a deleted entity and its filter rules again when provisioning is
  on and otherwise retries with backoff, so the destination reads Recovering. Transient failures the SDK retries
  itself, per-message failures (lock lost, message not found, session cannot be locked), quota exceeded and errors
  from the message handler, a settlement or a lock renewal are not disconnects. A subscription reports at most one,
  and none while the application has paused or disposed it or disposed the transport.
- **Repeated disconnects of one destination back off across reconnects (Fixes #1840).** When a destination kept
  failing right after each successful subscribe (an Azure Service Bus entity that stays disabled or an identity that
  stays unauthorized, or a RabbitMQ channel that closes again as soon as it opens), every reconnect started a fresh
  backoff, so the consumer reconnected every `InitialRetryDelay` forever, flipping the destination between Healthy and
  Recovering each cycle and flooding the broker, `SubscriptionStatusChanged` handlers and the logs. Each reconnect now
  waits the previous reconnect's delay times `BackoffMultiplier`, up to `MaxRetryDelay`, while the subscription keeps
  dropping before it has stayed healthy for the new `SubscriptionResilienceOptions.ReconnectBackoffResetAfter` (30
  seconds by default). A subscription that stays healthy that long has recovered, and its next disconnect waits
  `InitialRetryDelay` again, so an ordinary one-off drop reconnects as quickly as before; `TimeSpan.Zero` keeps the old
  behavior. The disconnect warning now says how long the reconnect waits. `SubscriptionRetryHelper.SubscribeWithRetryAsync`
  has an overload that takes a `TimeProvider`, and `TransportConsumerWorker` takes an optional one (the system clock
  when omitted) for its retry, reconnect and health-monitor waits.
- **A released debugger-aware clock pause subscription is not called again.** Disposing the subscription returned by
  `IDebuggerAwareClock.OnPauseStateChanged` stopped the wait for new pause changes, but a change already queued when it
  was disposed was still passed to the handler afterwards, so a handler could run after its owner had released it and
  torn down what it uses. The subscription now checks for release before taking each queued change, and leaves the
  changes it did not take in the queue. A call already running when `Dispose` is called may still finish; `Dispose`
  does not wait for it, so it can be called from the handler itself.
- **Every debugger-aware clock pause subscriber receives every pause change (Fixes #1847).** The subscriptions
  returned by `IDebuggerAwareClock.OnPauseStateChanged` shared one queue and competed for its changes, so with two or
  more subscribers each change reached only one of them. Each subscription now has its own buffer of the ten most
  recent changes and its own delivery, so every live subscriber receives every change made after it subscribed, in
  order. A slow or blocked handler delays only itself (one more than ten changes behind loses the oldest, so its last
  change is still the current state), and a handler that throws is logged at Error and stays subscribed. A released
  subscription still receives nothing more. `DebuggerAwareClock` gains a constructor taking an
  `ILogger<DebuggerAwareClock>`, which the default registration supplies.
- **A reconnect after a disconnect now disposes the subscription it replaces (Fixes #1839).** When a destination's
  subscription reported an unexpected disconnect, the subscription retry helper subscribed again and overwrote the
  state's subscription without disposing the old one, so every drop left a RabbitMQ channel and consumer, an Azure
  Service Bus processor or a composite's child subscriptions behind, with the helper's disconnect handler still
  attached. The reconnect now detaches the handler and disposes the old subscription, off the transport's callback,
  before it subscribes again. A disposal that throws is logged as a warning naming the destination, and the reconnect
  goes on. Connection recovery, shutdown and the reconnect take the subscription out of the state atomically, so
  whichever path takes it disposes it and it is never disposed twice.
- **A transport disconnect while a subscription is being established is no longer missed (Fixes #1831).** The
  subscription retry helper recorded a destination as Healthy and only then attached its disconnect handler, so a
  disconnect in between had no listener: the status stayed Healthy and no reconnect was attempted, and the consumer
  stopped receiving from that destination until a restart. The handler is now attached before Healthy is recorded, a
  disconnect it hears before then is acted on right after the change to Healthy (so Healthy cannot overwrite the
  Recovering it causes), and a disconnect raised before the handler was attached is found through the new
  `ISubscription.Disconnection` property, which the RabbitMQ, Azure Service Bus and namespace-mirroring subscriptions
  set before they raise `OnDisconnected` (the interface's default is `null`, so other implementations still compile).
  Only the first disconnect of a subscription starts a reconnect, and a disconnect from a subscription that a later
  subscribe has replaced no longer knocks the current one into Recovering or starts a second consumer.
- **A rebuild of a versioned perspective no longer refuses a stream whose events were appended out of event-id
  order (Fixes #1819).** A selected-streams rebuild folds a model marked `IVersionedApplyTarget` from an empty model
  in stream-version order, so its write carries the event id of the last event by version. When that event was minted
  before the one the stored row records, its id sorted below the stored one, the store's event-id ordering guard
  refused the write, and the rebuild failed with "folded but left the row unchanged". A rebuild's write is no longer
  ordered on the event id: it takes the commit-sequence guard every other model's rebuild takes, on the highest commit
  sequence among the events it folded. The row-version check still refuses a stale read in every mode, and the live
  apply and the rewind keep ordering on the event id. Both PostgreSQL drivers decide this through the shared
  `PerspectiveRowVersionCommands.OrdersOnEventId`. No migration: the guard is part of each write's statement.
- **The `wh_signals` row sweep is no longer held back by the cursor of an instance that is no longer registered
  (#1771).** The sweep deleted up to the slowest cursor in `wh_signal_cursors`, dead or alive, so an instance that
  stopped without being reaped left its cursor behind and the sweep removed nothing until the stale-instance reap
  removed its row, while the table grew. The sweep now counts only the cursors of instances registered in
  `wh_service_instances`, the rule the partition carry uses (#1758), so both paths agree on which instances are live.
  With no registered instance's cursor the floor is 0 and nothing is deleted, as with an empty cursor table. No
  migration: the sweep's statement is the worker's own.
- **A health source that blocks before it returns no longer stops the health probe answering (#1811).** The
  aggregator called each source's `ReportAsync` on the probe's own thread and bounded the task it returned. A source
  that blocked inside the call, before returning a task (a synchronous library call, or a wait on an asynchronous
  one), held the probe there: its deadline was never armed, no later source was asked, and the probe never answered,
  which is how a liveness probe gets a healthy pod restarted. Each source is now asked on a thread-pool thread, so such
  a source is reported `Faulted` when its `SourceTimeout` elapses, like any other unresponsive source.
- **The health probe's per-source deadline runs on the host's registered `TimeProvider` (#1811).** Each health source
  is bounded by `WhizbangHealthOptions.SourceTimeout`, and that deadline always ran on the system clock, so a host or
  test on a fake clock could not decide when it elapsed. `WhizbangHealthAggregator` gains a constructor that takes the
  `TimeProvider`, and `AddWhizbangManagedHealth` passes the container's registered one, or `TimeProvider.System` when
  none is registered. Behavior on the system clock is unchanged.
- **`flush_completions` no longer deadlocks against another instance completing the same outbox rows (#1785).** The
  composite flusher behind `IWorkCoordinator.FlushCompletionsAsync` completed outbox rows and later reported outbox
  failures in the same transaction: two passes over `wh_outbox`, each in the canonical (stream_id, created_at,
  message_id) order, which together are not one order. A flush that completed a later row and failed an earlier one
  held the later row while it waited on the earlier, and a transaction completing both in order waited on it in turn.
  Migration 245 redefines `flush_completions` to lock every outbox row it will write first, in one ordered pass: the
  completed rows, the failed rows, and the rest of each failed row's run that the failure releases. It also runs its
  steps in the canonical table order (outbox, inbox, perspective work). The signature is unchanged, so both drivers
  call it as before. The flush workers, which send completions and failures in transactions of their own, were not
  affected.
- **A heartbeat no longer rolls an instance's recorded lifecycle phase back behind its startup stage (#1781).** A beat
  reads the phase when it is built and writes it when it lands, so a beat built before a startup stage or transition
  was recorded wrote the earlier phase back over it: an instance that had entered Accept commands read Migrating again
  in `wh_service_instances`, and was left out of partition assignment and claim rankings until its next beat or claim.
  Migration 244 redefines `record_heartbeat` so that a beat from the process the row records never moves the phase down
  the startup and shutdown ladder (Starting, Connecting, Migrating, AcceptingCommands, Running, Stopping, Stopped), which
  a process only ever climbs. Such a beat keeps the recorded phase and still refreshes liveness. Phases off the ladder
  (a pause, a stand-by and the revival from it, a fault) are taken from the beat as before, a beat ahead of the row
  still repairs a lost transition, and a new process registering under an instance id an old process left behind
  records its own, earlier phase. The function's signature is unchanged, so both drivers call it as before.
- **A Windows build no longer rewrites a committed pinned-type ledger whose content did not change (#1769).** The
  extraction step compared `.whizbang/pinned-type-ledger.json` with the extracted JSON byte for byte, and the ledger
  was serialized with `Environment.NewLine`, so a Windows build rewrote a ledger checked out with LF as CRLF: version
  control reported it modified with no content changed, and the next build recompiled the project, because the ledger
  is a compiler input. The extraction now leaves a file that differs only in line endings alone, for the ledger and
  the message registry alike, and the ledger is written with LF on every operating system. In a repository with no
  line-ending normalization whose ledger was committed with CRLF, the first content change rewrites it with LF.
- **The migration tool's commented decision file keeps every decision it holds (Fixes #1766).** Saving a decision file
  with comments wrote the tenant and stream-id detection, the routing domains and topics, the per-class and
  per-interface strategy maps, `preserve_packages` and `connection_string_name` as fixed template values, so loading
  it back silently reverted them to their defaults, and an unset pre-migration commit or current category loaded as an
  empty string instead of unset. Every field is now written from the recorded decisions and JSON-escaped, so a
  commented file loads back with exactly the decisions it was saved with.
- **The migration tool reads back the decision file it generates for a Windows project path, and a blank docs
  path no longer throws on Windows (#1764).** `whizbang-migrate apply --generate-decision-file` wrote the commented
  decision file by interpolating values into a JSON template, so the backslashes of a path such as `C:\Users\me\App`
  (or a quote or control character on any platform) made the file invalid and the next run failed to load it. Every
  string value in the commented file is now JSON-escaped, and the usage comment that repeats the path blanks any
  character that would end the comment early. Values with no special characters are written exactly as before. The
  source generators' documentation path resolver now treats a whitespace-only `WhizbangDocsPath` or project directory
  as unset, as it already did an empty one, instead of passing it to the file system, where Windows rejected it.
- **A stopping host waits for all of the framework's background work before it disposes the service provider
  (#1721).** Only the outbox batcher's detached stages were waited for. The work coordinator strategies' flush stages,
  the dispatcher's ImmediateDetached stages, the receptor invoker's tag handlers, the destruction stages, and the
  inbox dispatch, outbox drain, perspective and Service Bus consumer workers' detached stages all ran on after the
  container was disposed, failing with `ObjectDisposedException` or losing their work or failure record. The shared
  `DetachedLifecycleWork` now records them, and every entry point registers it with the stop-time flush
  (`TryAddWhizbangDefaults`), so a host without the worker pipeline waits too. The hosted workers wait for their own
  detached stages in their stop, before the failure flush registered ahead of them stops. The interval and batch
  strategies register with the host and are stopped and flushed while it stops: before, the timer, batch-size and
  debounce flushes were not awaited, and disposal returned at once on the in-progress guard when one was running,
  dropping the work queued after that flush's snapshot. The idle sweeps of the sliding-window batchers and the
  per-stream serializer are waited for by their stop (an evicted stream's last flush was not), the transport batch
  collector's disposal waits for its flushes (other than one it is called from, as an in-process reply disposing
  its own subscription is), the transport consumer's active body cleanup is waited for by its stop,
  and disposing the Service Bus transport ends a throttle pause instead of letting it restart a processor later.
  `DetachedLifecycleWork` gains `Waiting`, `RegisterProducer` and `StopProducersAsync`, and the four work coordinator
  strategies take it as a required constructor argument.
- **A stopping host no longer logs "Error invoking PostDistributeDetached lifecycle receptors ... Cannot access a
  disposed object".** The outbox batcher's flush runs its detached lifecycle stages (`PreDistributeDetached`,
  `DistributeDetached`, `PostDistributeDetached`) in the background, and nothing waited for them: one still running
  when the host disposed its service provider failed for every message it had not reached yet, so those receptors
  did not run and an error was logged at every shutdown that caught a flush in progress. The shutdown flush now
  waits for them after flushing the batchers and before the provider is disposed, logging at Information how many
  it is waiting for, within the host's shutdown timeout. `DistributeLifecycleContext` gains an optional
  `DetachedWork` (`DetachedLifecycleWork`) that records the background invocations.
- **A received message is stored before it is settled with the broker, and a skip is recorded with the row (#1720).**
  Several receive paths completed, acked or nacked a message before anything stored it, so it was gone with no row.
  Azure Service Bus acked and dropped a message whose envelope type no context here describes, one the typed read
  rejected and one no local consumer takes, handled a raw receptor match with no inbox row, and dead-lettered a valid
  envelope that lacked the `EnvelopeType` property. RabbitMQ nacked an unresolvable type without requeue (dropped
  unless a dead-letter exchange was declared) and acked a no-consumer message on the single-message path, and a body
  its deserializer threw on left `FlushBatchAsync` with the `JsonException`: the batch collector re-queues a batch whose
  flush throws, so that message and its neighbors were flushed again on every sliding window and never acked or
  nacked. The transport consumer skipped no-consumer, foreign-target, owned-echo, self-echo and unknown-event messages
  before `store_inbox_messages`, counting them as `messages_deduplicated`, and the Service Bus consumer dropped a
  no-consumer message the same way. Now every body that is a valid envelope reaches the handler and is stored first: as
  its typed envelope, or as received (`ReceivedEnvelopeReader`, an envelope of `JsonElement` with the type the producer
  named) when this service cannot read its type, and a raw receptor runs once it is stored. A message the consumer
  decides not to process is stored settled: `InboxMessage.DiscardedOnReceive` (one of `InboxReceiveDiscard`) makes
  `store_inbox_messages` write the row processed, Stored and Published, with `discarded on receive: <reason>` in
  `wh_inbox_state.error`, never claimed, copied into the event store or dispatched (migration 242). Each skip is
  counted by `whizbang.transport.inbox.messages_discarded` with a `reason` tag. Only a body that is not a readable
  envelope is refused before storage, and it is dead-lettered at the broker (where the dead-letter drain gives it
  custody), never dropped; a valid envelope with no envelope type is given dead-letter custody with its body by the
  consumer. Operators will see more inbox rows (settled ones, purged by maintenance with other completed rows) and
  fewer Service Bus ack-drop warnings; the `receptorRegistry`, `perspectiveRegistry`, `discardPolicy` and
  `absorbedNamespaces` constructor parameters of `AzureServiceBusTransport` and `discardPolicy` of `RabbitMQTransport`
  are no longer consulted on receive. The control class's non-durable receive is unchanged.
- **A recovered outbox or perspective dead letter is published or applied as the work it was, and reads Recovered
  only once that has happened (#1722).** `recover_dead_letter` re-inserted both with column lists of its own and
  marked the dead letter Recovered in the same call, before anything ran. The outbox copy wrote the envelope type
  `recovered`, which the drain sends as the wire's envelope type header, so a receiving service could not resolve the
  message and dropped it (Azure Service Bus completed it as `MISSING_JSON_TYPE_INFO`; RabbitMQ dead-lettered it). It
  was also stored as not an event, with no scope or flags, partition 0 and background priority, and skipped every
  step of `store_outbox_messages`. The perspective copy wrote partition 0, background priority and no rewind check.
  The dead-letter capture now keeps every field the store needs (`move_to_dead_letters`: envelope type, event flag,
  flags, scope, priority, schedule and coalesce group for the outbox; priority for perspective work), and recovery
  re-enters through `store_outbox_messages` or `store_perspective_events` (redefined with the event-store chain's
  rules, including the rewind of an event behind its cursor and the doorbell), differing only by naming the dead
  letter (`recovered_from_dead_letter_id`). The dead letter stays Recovering until `complete_outbox_published` or
  `process_perspective_event_completions` retires the row, then reads Recovered; a recovery dead-lettered again is
  held for review naming the new dead letter, now for every source table; a perspective dead letter whose event is no
  longer in the event store is held for review with the reason. A dead letter captured before this change is
  re-entered under the envelope type its message type names, as an event with its event's flags and scope when the
  event store holds it. Recovery stores the row with the partition count arrivals are stored with: the
  new `wh_recover_dead_letter(id, partition_count)` takes it, `recover_dead_letter(id)` keeps its signature and uses the
  claim's default, and `EFCoreDeadLetterRecoveryService` gains an optional `partitionCount` that the Postgres driver
  fills from `ClaimWorkerOptions.PartitionCount`.
  Recovered outbox and perspective work now takes the priority it was stored with rather than background. Migration
  241.
- **A recovered broker or inbox dead letter reaches the event store, and reads Recovered only once processed
  (#1664).** Recovery re-emitted an inbox or broker dead letter with inbox writes of its own that had drifted from a
  delivery: the row was not an event and, for a broker import without a session, had no stream. The inbox copy into
  the event store never took it, so the row was dispatched, completed and deleted with nothing appended, its id
  recorded as seen, and its dead letter already marked Recovered. Recovery now builds the row with the transport
  receive path's own builder and classification and stores it with `store_inbox_messages` (migration 236), so
  `is_event`, the stream, flags, scope, priority and the producer's identity are a delivery's. A recovered row differs
  only in that it claims its dead letter, is not dropped as a duplicate (its observation is still counted), and names
  the dead letter on its work-state row (`InboxMessage.RecoveredFromDeadLetterId`). The dead letter stays Recovering
  until the message completes: Recovered when processed, HoldForReview with the outcome in `operator_notes` when an
  inbox gate completes it unprocessed, and HoldForReview under an operator hold, naming the new dead letter, when the
  inbox dead-letters it again. `recover_dead_letter` now declines inbox and broker rows, and
  `IDeadLetterRecoveryService` gains `ReadRedeliveryAsync` (a default implementation returns `null`, which falls back to
  `RecoverAsync`). Recovered inbox and broker messages now take the priority a delivery takes rather than background.
  Events already lost this way are not restored by this change; see the issue for the repair.
- **A perspective that declines every event of a stream no longer leaves a blank row for it (#1677).** When every
  `Apply` returned `ApplyResult<TModel>.None()` for a stream's events, a rewind of that stream (a late event behind
  the cursor, events of one stream split across concurrent claims, a startup rewind or a resurrection) wrote the
  empty default model the runner builds as `Apply` input, so the table gained a row of defaults (empty strings, zero
  counts, `DateTime.MinValue`) that read as a real one, and a rewind with snapshots on could snapshot it too. A
  rewind now writes, and snapshots, only a model an `Apply` produced or a snapshot it restored, as the live drain
  already did. A soft delete (`ApplyResult.Delete()`) that returns no model on a stream with no row no longer
  creates one either. Every generated row write now asks `PerspectiveRowWriteGuard`, which refuses a model no
  `Apply` produced and logs a `[diag.write.unproduced]` warning. Blank rows already written are left in place;
  this change does not remove them.
- **A received collective event no longer fires its post-perspective lifecycle stages twice (#1676).** In a service
  that runs the collective sink, a collective event received from another service ran through `PostAllPerspectives`
  and `PostLifecycle` as soon as it was received, because no per-stream perspective lists a collective event type,
  and again when the sink's set-based apply completed, so a hook at either stage (for example a
  `PostAllPerspectivesDetached` notification hook) ran twice for every such event. The inbox and Service Bus
  receive paths now leave those stages to the sink, which fires each of them once per event however many
  collective handlers apply it and however many batches the run spans. A service without the collective sink
  registered still fires them on receipt, as before.
- **A schema pass continued part way through no longer records the files it already applied as skipped (#1687).**
  When a retry, or a second instance that took the schema lock between two of the first one's steps, continued a pass
  of the same build, its plan recorded `status 3`, `Skipped (hash unchanged)`, over every migration file and
  perspective table the pass had already applied, so `wh_schema_migrations` said they were never applied. The schema
  itself was complete and every hash was right, so nothing re-ran; the ledger was wrong. Only the beginning of a pass
  records skips now.
- **Parsed JSON values no longer strand pooled buffers (#1679).** Twenty-three places in the libraries built a
  `JsonElement` with `JsonDocument.Parse(json).RootElement` (outbox and transport metadata, body offload claims,
  control-message lifetimes, audit payloads, the Dapper envelope adapter and the EF Core orphaned-event fallback)
  and never disposed the document, so the buffers it rented from the shared pool were never returned, on paths that
  run per message. They now use `JsonElement.Parse`, whose element owns its memory; every returned element keeps the
  same content and stays valid after the call. Generated message-tag registries with `ExtraJson` now dispose the
  document they merge from. A repository analyzer (WHZ0005) fails the build on the pattern, so it cannot return while
  the SDK's own rule, CA2026, ships only with SDK 11; `.editorconfig` raises CA2026 to an error for that SDK.
- **A perspective no longer loses an earlier event when a later one of its stream was applied first (#1608).** Under
  burst load two apply workers can carry one stream's events with overlapping transactions, and the later event can
  reach the row first. The framework answers that by folding the earlier event onto the row the other worker
  committed, rewinding the stream when the event sits below the row's position. On a model opted into strict
  ordering (`IVersionedApplyTarget`, which per-item saga rows use) the store refused that corrective write, because
  the rewind records the row's own newest event id and the guard admitted only a strictly newer one, and a live
  fold of the earlier event alone was refused the same way; both refusals were reported as applied, so the work
  rows were retired and the row kept only the later event. The guard now admits a write at the stored event id,
  and a live write the ordering guard refuses re-folds the stream instead of being reported applied. Rows already
  missing an event are repaired by rebuilding the perspective (any mode); on earlier releases only a blue-green
  rebuild repairs them, because an in-place rebuild of a strictly ordered row was refused the same way.
- **The rollback outbox publisher's not-ready hold now keeps its leases (#1606).** `OutboxPublishWorker` (off by
  default; `OutboxDrainWorker` is the default publisher) held rows while the transport reported not ready by queueing
  their ids on the lease renewal channel, which drops any id with no registered lease handle, and a held row has
  none. The leases lapsed, another claim took the rows, and each re-claim charged an attempt. The hold now renews the
  held rows' leases directly through the work coordinator, as the drain worker does (#1558): when a row is first held
  and again once half of `LeaseRenewalWorkerOptions.LeaseSeconds` has passed, so it does not make a round trip on
  every retry. A renewal that fails is logged and retried on the next pass; it is not reported as a failed publish.
  The worker's constructor no longer takes an `ILeaseRenewalChannel`.
- **A failed backlog head peek on Azure Service Bus is logged (#1643).** The backlog sample reports an entity whose
  oldest-message peek throws (a missing permission, a deleted entity, a broker error) with its depth and no age, as it
  does an empty entity, and nothing said why. The failure is now logged at Warning with the entity, the exception type
  and its message, at most once every five minutes per entity on the transport's `TimeProvider`; the depth is still
  reported.
- **An event's flags keep every bit when the JSON options write enums as names (#1612).** The outbox and inbox
  store functions read an event's flags from the stored message's JSON and, for anything but a number, kept only
  `Collective`. JSON options carrying `JsonStringEnumConverter` (added by a host to its options, or registered with
  `JsonContextRegistry`) write the flags as names, so on such a host an ephemeral event was stored as Sourced (its body
  never reaped, its stream invisible to the WHIZ133 and rebuild guards), and the `Composite`, `NoRebroadcast`,
  `Compacted` and `AtMostOnce` bits were stored as 0, with nothing reported. The framework's default options write the
  number and were not affected. `OutboxMessage.Flags` and `InboxMessage.Flags` are now always written as the number
  (`EventFlagsNumberJsonConverter`, a property-level converter that outranks the options), and the store functions
  (migration 232) read a name list bit by bit for any writer that still sends one: every `EventFlags` name, under any
  naming policy, and a number among the names. A name `EventFlags` does not define raises SQLSTATE `22P02` naming it,
  so an event whose kind cannot be read is refused rather than stored as Sourced. Events already stored with the lost
  bits are not repaired by this change.
- **The managed-object reconcile can no longer leave the schema lock held behind a transaction-pooling pooler
  (#1639).** It took the schema initialization lock with `pg_try_advisory_lock` and released it in a separate
  statement, so behind a pooler the release could run on another backend: PostgreSQL warned `you don't own a lock of
  type ExclusiveLock`, the release returned false unchecked, and the backend that took the lock kept it, so every
  later schema initialization on every instance found the key held. The plan and its ledger rows now run in one
  transaction under `pg_try_advisory_xact_lock`, and each drop other than a concurrent index drop runs in a
  transaction of its own under the same lock; the commit or rollback releases it where it was taken. A concurrent
  index drop cannot run in a transaction, so it holds the session lock for its own statements only on a connection
  that reaches one backend (the server's process id matches the one the connection was given); behind a pooler it
  stays pending with a Warning naming the indexes until a direct initialization connection is configured. A drop
  that finds another session holding the lock stays pending for the next run. Every release of a session-level
  advisory lock (the reconcile, the duty elector, the role elector's bridge and the commit-order stamper's leader
  lock) now checks the result and logs a Warning naming the lock and its key when this session did not hold it, and
  the role elector warns, as the duty elector and the stamper already did, when its bridge lock resolves through a
  pooled connection-string key.

- **An event audit record's envelope names the audited event as its cause.** The emitter built the audit envelope's
  hop with no causation, so the record could be traced to the event it audits only through the payload's
  `OriginalEventId`; the hop's `CausationId` is now that event's message id. The test meant to hold this had passed
  without checking anything: its `IsNotNull()` on a nullable value passes on null under the pinned TUnit (#1642).
- **A failed migration step no longer discards the steps that succeeded before it (#1637).** The EF Core driver ran
  its whole schema pass (core tables, every migration file and its ledger row, perspective tables, constraints,
  associations, registries) in one transaction, so a failure in any step, such as a `40P01` on a fresh database
  (#1636), rolled back every file that had already applied and each retry started from nothing while holding every
  lock until the final commit. Each step is now a transaction of its own, taking the schema advisory lock (still
  transaction-scoped) at its start and re-checking its own ledger row under it: a failure rolls back only that step,
  the retry resumes at it, and a second instance that starts part way through finishes the pass without applying
  any step twice. The steps with no ledger row of their own are recorded as `step:<context>:<step>` rows, and a file
  re-run by the redefinition closure or a sweep is marked `pending-rerun` before anything runs, so the plan survives
  a failure. The failed step's record (status -1, `Failed: <SQLSTATE> <message>`) is written after its rollback and
  never carries the file's hash; the Dapper driver wrote the hash, so a file that failed for a reason outside its
  text was skipped on every later start, and now runs again. Every step logs `Migration X of Y - Started : <name>`
  and then `Succeeded`, `Skipped` or `Failed : <name> : <SQLSTATE> <message>` (at Error, saying when a retry comes),
  and the pass ends with `Migration complete: Y of Y succeeded (N applied, M skipped) in <duration>` or
  `Migration stopped at X of Y: <name> : <error>`. `ISchemaInitializationObserver.OnSchemaStepCommittedAsync` reports
  each committed step.
- **Perspective tables keep their names when `WhizbangStripTableNameSuffixes` is not set (#1660).** Since the
  table-naming properties became visible to the generators (#1426), a project that never set the property handed
  the generators an empty value, which was read as "do not strip". Every perspective table then gained its model's
  suffix, so `ActiveTenantModel` mapped to `wh_per_active_tenant_model` instead of `wh_per_active_tenant`, and an
  upgraded service looked for tables that did not exist. An empty or blank value now counts as unset, so suffixes
  are stripped by default and tables keep the names they had on 0.2623 again. A project that sets the property to
  `false` still keeps the suffix, and a set value is trimmed before it is read. The suffix list and identifier
  length properties already ignored a blank value and behave as before.
- **A backlog of aged messages is no longer dead-lettered on arrival (#1663).** The receive gate quarantined any
  message first enqueued longer ago than the poison age threshold (by default 50 minutes, derived from lock renewal
  times delivery attempts), even on its first delivery. A backlog that took longer than that to drain, such as
  ordinary events queued behind a large backfill, was dead-lettered by every subscriber without ever being
  attempted. Age measures queueing, not failure, so the receive gate now accepts every message until it is stored
  and never quarantines on age: an aged message is reported on the `whizbang.message.aged_on_receive` counter
  (tagged by gate, with the transport and entity) and a warning, and proceeds. A redelivery loop, including a
  session lock-loss storm whose broker delivery count never moves, is bounded after storage: every redelivery of a
  stored message raises its durable observation count before any handler runs, and the inbox gate quarantines it
  once the count reaches `MaxDurableObservations` and processing has started. `PoisonQuarantineReason.AgedOnReceive`
  and `IPoisonMessageDetector.RecordAgedOnReceive` are new (the latter has a no-op default, so custom detectors
  are unaffected); `MessageAgeExceeded` is no longer produced by the default detector. The age threshold option is
  unchanged and now only decides what is reported.
- **A blank configuration value no longer changes runtime behavior (#1660 sweep).** A key that is present with an
  empty or whitespace value (an appsettings placeholder, or an environment variable set to nothing) now behaves as
  if it were absent in four places that used to act on it. A blank `Whizbang:ServiceName` no longer becomes the
  service name. A blank `ConnectionStrings:db` (or its `-direct` and `-init` variants) no longer counts as
  configured, so a service still on its legacy connection string name keeps using it. A blank `-init` connection
  string no longer stops startup: schema initialization runs on the main connection, as it does when the key is
  absent, and a blank main connection string is reported as not found. A blank `Whizbang:Scope` key no longer
  replaces a GraphQL scope claim-type list with a name that never matches, which silently dropped the tenant, user,
  roles or permissions from every request. It now leaves the default, or the value set in code.
- **A received stream is stored in the producer's order when two claims carry it (#1607).** Two concurrent claims
  of a receiving service could lease one source stream's events at once: the later claim skipped the head the
  earlier one had locked, stored the rest first, and the head was then appended after them, so perspectives saw a
  stream's "created" event after the events it should precede, and a rebuild replayed the same order. Fan-out
  children carry no origin commit sequence, so nothing could sort the stream back. The inbox emit chain (migration
  230) now leaves an event unstored while an earlier event of its stream is pending and not held by the same pass,
  and stores it after that event, in message-id order (producer order for fan-out children). `fetch_inbox_batch`
  hands out an event only once it is stored, so a waiting event is never dispatched first. Not covered: an earlier
  event that has not reached the receiver's inbox yet (separate deliveries reordered in transport). Streams already
  stored out of order are not repaired by this change.
- **A starting or draining instance is no longer given work (#1638).** "Alive" and "eligible for work" were one test,
  so an instance registered during the migrator election and kept alive by its heartbeat or alive-lock was a
  partition-assignment candidate and was counted in `claim_work`'s peer ranking while it was still migrating, and its
  share stalled until it was ready; a draining instance kept its share until its heartbeat lapsed. Now only an
  instance whose recorded lifecycle phase is `AcceptingCommands` or `Running` is eligible (one definition,
  `wh_instance_is_eligible_for_work`, migration 229): the candidates, `claim_work`'s and the multi-stream claim's
  rankings and the elected assigner's own membership follow it, and a starting or draining instance's share goes to
  its peers at once. Liveness, the reap and stream ownership are unchanged. An instance that records no phase (an
  older build) is eligible whenever it is live, as before. The phase is recorded reliably: the bootstrap registration
  records `Migrating`, the heartbeat and the claim loop's registrations carry the phase being entered
  (`IWhizbangLifecycleState.AnnouncedPhase`), and a claim whose row records another phase than the caller's
  (`ClaimWorkRequest.LifecyclePhase`) is reported stale so the claim loop writes it.
- **Embedded migrations no longer push consumer builds toward CS8103 (#1622).** The EF Core generator embedded the
  text of every migration in each assembly with a generated DbContext as ordinary string literals, which share one
  user-string heap capped at 16,777,215 bytes, so every migration added moved every consumer closer to failing to
  build with CS8103 and nothing warned first. The migration and bootstrap lists are now emitted as UTF-8 literals,
  which the compiler places in the data section, and decoded once at run time to exactly the text they carried
  before, so applied migrations keep their content hashes. Consumers need no change and no compiler flag.
- **A direct event-store append is folded into the stream digest (#1600).** Events stored with
  `IEventStore.AppendAsync` (both PostgreSQL drivers, through `wh_append_event`) were never folded into
  `wh_stream_digests`, while the emit chain folds every event it stores and the full recompute the integrity sweep
  trusts counts both. A stream written by both paths, such as `$wb-system`, therefore had a table digest that missed
  some of its events, and closing it subtracted events that had never been added. `wh_append_event` (migration 225)
  now folds the event in the same call and under the same stream lock, with the emit chain's bucket and rules:
  ephemeral, at-most-once and digest-exempt types (the audit records) are left out. The integrity audit's
  verification sweep (`VerifyDigestTableAsync`) reported every such stream as drift and healed it; it heals rows
  appended before the migration once more and then finds nothing.
- **Concurrent appends to one stream no longer fail or retry (#1555).** `IEventStore.AppendAsync` on both PostgreSQL
  drivers read the stream's last version and inserted the next in separate round trips, so two writers on one
  stream collided on its unique version: the EF Core store threw `Concurrent modification detected` and the Dapper
  store retried up to ten times. Both now call `wh_append_event` (migration 224), which takes the stream's advisory
  lock, the one the outbox and inbox emit paths take, and assigns the version in the database, so writers wait their
  turn and get consecutive versions; a direct append and an emit on one stream serialize too. The EF Core append runs
  on the context's connection and in its current transaction, and still saves the context's other pending changes
  first. A duplicate event id now fails at once as "an event with this id is already stored" instead of being
  reported or retried as a race.
- **A fire-and-forget security record that fails to store is logged and counted (#1555).** The `AccessDenied`
  record of a denied lens read was emitted with its task discarded, so a store failure lost it silently. Such emits
  now go through `SystemEventFireAndForget.Emit`, which never blocks or faults the caller and, on failure, logs a
  warning naming the event type, tenant and stream and increments `whizbang.system_events.emit_failures`
  (tag `event_type`) on the `Whizbang.EventCategories` meter.
- **The default outbox publisher ignored `ITransportReadinessCheck` (#1558).** `OutboxDrainWorker` sent to a transport
  that reported not ready, and each failure then counted as an attempt. It now asks the publish strategy before each
  pass. While the transport is not ready it publishes nothing, renews the leases of every row it holds on the offered
  streams (by `LeaseRenewalWorkerOptions.LeaseSeconds`) and reports no failure, so no attempt is charged; the claim
  offers the streams again and they publish once the transport is ready. The new `OnOutboxPublishHeld` event fires
  after each held pass with the streams and the rows kept.

- **`[PopulateTimestamp(TimestampKind.QueuedAt)]` was never stamped (#1557).** Nothing called the QueuedAt populator
  after the publisher that once did was removed, so a property marked this way stayed null. The dispatcher now stamps
  it as it writes a message's outbox copy (send, publish, their batch forms, a cascade and a deferred publish), and the
  coalesce worker stamps the composites it writes, both from the registered `TimeProvider`. The stored copy, and so
  the delivered one, carries the time. The message a local handler runs before the copy is written is not stamped.
- **Row retention on the Dapper driver enrolled nothing, because that driver never wrote the perspective registry
  (#1548).** The driver-neutral schema generator now emits each assembly's perspective registry payload and registers
  it at load, and the Dapper driver reconciles it into `wh_perspective_registry` at startup after building the
  perspective tables, as the EF Core driver does. Declared retention windows are now adopted and swept, and the
  row-eviction cleanup finds each model's perspectives. The payload is built by one shared builder for both drivers.
  That builder now describes a vector field's index with the method the field names (`hnsw` or `ivfflat`), and no
  index for `None`, as the table DDL does; an EF Core table with such a field reports schema drift once after upgrading,
  as its recorded description is corrected.

- **Turning on `ReconcileHistoricalOnStartup` after a type became ephemeral reclassified nothing (#1578).** The
  reconciler acted only on the boot that first detected the drift, and only if the flag was on then; the warning told
  operators to enable the flag, which then had no effect. A boot with the flag on now reclassifies the history of
  every ephemeral event type, whenever its drift was detected (the reclassification is idempotent). With the flag
  off, nothing changes.

- **The Dapper driver could not install into a named schema (#1547).** `AddWhizbangPostgres` rendered the migrations for
  `public` while the tables went wherever the session's search path pointed, and the startup readiness check looked
  only in `public`. The initializer now takes the schema the way the Dapper work coordinator does: the first schema the
  connection string's Search Path names (created if it does not exist), otherwise `current_schema()`. It renders the
  migrations for that schema, and its catalog queries and the readiness check look there, so tables, functions and the
  coordinator agree.
- **A subscriber's integrity checkpoint counts included the audit records it received (#1554).** The
  origin's checkpoint window and the subscriber's received count now leave `EventAudited` and `CommandAudited` out,
  as every integrity digest does, so audit traffic no longer reads as a count the origin never declared.
- **A subscriber's integrity digests reported drift for every audit record it received (#1492).** A received audit
  composite is fanned out into its records, which are stored as events under the origin's id, so the inbox emit chain
  folded them into the origin's digest lane. The origin never stores the audit records it publishes, so reconciliation
  saw rows the origin could not account for; and records the origin did store (the system event emitter appends them)
  were answered for although no subscriber was sent them. Audit records (`EventAudited`, `CommandAudited`) are a
  function of the event they audit and are now left out of every digest fold: both emit chains, the epoch seals, the
  type-level answers and the work coordinator's recomputes and verification sweep. `DigestExemptTypes.TypeNames` names
  them. Migration 216 removes the buckets and sealed epochs an earlier release folded for them, so stored digests agree
  at once.
- **Audit records were missing the event they audit, its stream position and its causation, and ignored the
  audit decision hook on the main path (#1485).** Every `EventAudited` writer (the outbox audit path, the event
  store decorator and `SystemEventEmitter`) now sets `OriginalEventId`, `CausationId`, `AuditReason` and
  `AuditLevel`; the outbox path's `OriginalStreamPosition` is the audited event's stored version (it was always
  0), filled in when the record is stored with or after its event (a record written before its event, which the
  per-stream outbox batcher can do, keeps 0). The decorator's message overload now stores the event under the id
  the record names. A registered `IAuditDecisionHook` now vetoes and names records on every path, and
  `SystemEventEmitter` honors `AuditMode.OptIn` and takes an optional hook.
  **Behavior changes:** audit records sent through the outbox now travel on one stream per tenant (the audit
  ledger, `AuditLedger.StreamFor(tenant)` = UUIDv5 of the tenant under UUIDv5("sys-audit")) instead of one
  stream per record, and a coalesced bundle of them rides the same stream; this pins a tenant's audit publishing
  to one stream (one owner, ordered, and one session per tenant on a session-enabled transport). A receiving
  service still stores each record on its own stream (the record's id), so perspectives over `EventAudited`
  keep one row per record. The event store decorator's records are now transport-only, like the outbox path's:
  the origin no longer writes them to its own event store, so a perspective over `EventAudited` in the
  producing service itself sees no records from that path. Applies migration 216.
- **The Dapper work coordinator ignored the configured schema (#1508).** Its statements used bare names resolved
  through the session's `search_path`, and maintenance, the orphan-inbox purge, the pending-message discard and the
  pending-stream check named `public` outright. A pooled connection (PgBouncer in transaction mode, for one) does
  not carry a configured `search_path` to the server connection a statement lands on, so a host whose framework
  tables live in another schema read and wrote `public` or failed there. Every coordinator statement is now
  qualified with the configured schema: the new optional `schema` argument of the `DapperWorkCoordinator`
  constructor, else the first schema the connection string's `Search Path` names, else the server's
  `current_schema()`, asked once on first use and kept (which honors a default set on the role or database).
- **Perspective row retention did nothing on the Dapper driver (#1505).** The Dapper work coordinator implemented
  none of the row-retention members and inherited the interface's no-op defaults, so a `[RowTtl]` or `[RowCap]`
  declaration removed no row: nothing was enrolled or adopted, the expiry and cap sweeps reported `unsupported`,
  a registered `IPerspectiveRowDestructionGuard` was offered nothing, and the stream-group cascade never drained.
  The Dapper coordinator now runs the same statements as the EF Core coordinator, from one shared copy, qualified
  with its configured schema: enrollment from the declarations, the adoption gate and its backlog count, the
  expiry sweep, the cap sweep behind its once-per-window claim, the pre-destruction seam's holds and retry
  ladder, the cascade drain and delete, and the staged rebuild's presence reconcile. Known limit: the Dapper
  driver does not write `wh_perspective_registry` (only the EF Core driver's schema reconcile does), and
  enrollment matches declarations against registry rows, so on a Dapper host a perspective is enrolled only once
  its registry row exists; until then each declaration is logged at Warning as matching no registry row.
- **An evicted row of an ephemeral perspective stayed missing when its kept snapshot was in an older snapshot
  format (#1506).** With the earlier history already reaped, the next event treated itself as a late straggler and
  was not applied, so the row stayed missing until a later event arrived. A snapshot that cannot be read is now
  handled the same way as no snapshot: the row starts fresh from the incoming events and a warning names the
  unreadable snapshot. The incoming events are always applied.
- **At-most-once schedule occurrences were folded into integrity digests, counted in checkpoints and selected for
  re-delivery (#1484).** Every exclusion looked for the delivery guarantee in the stored event metadata, which never
  carried it, so none matched: a consumer comparing digests saw each occurrence as missing, and re-delivery could send
  an occurrence its schedule declared at most once. The guarantee is now a flag on the stored event
  (`EventFlags.AtMostOnce`, value 32), set when the occurrence is spawned, and the stream and epoch digests, the
  verification sweep, the checkpoint counts and the re-delivery selection leave flagged events out. Migration 214 flags
  occurrences already stored (read from the outbox row when it remains, otherwise from the schedule), removes them
  from the stored digests and sealed epochs, and advances the origin generation once, so consumers reset their seals
  and re-verify instead of reporting drift. The digest and checkpoint queries no longer read the event body to apply
  the filter.
- **A collective sink run read its stream's whole queue (#1524).** Each run read every unprocessed collective
  on the stream, joined to the event store and sorted, then applied only the rows it held: draining a busy ordering
  key cost the backlog squared over the batch. A run now reads the head of the queue on an index, through its own
  rows and one more, and asks separately which of its held rows and predecessors are still queued (migration 215).
  An unstamped collective now takes its place in the queue by stream version, the order a replay gives it, rather
  than by event id.
- **WHIZ006 missed a `[StreamId]` inherited from a base type (#1512).** A derived property silently hid an
  inherited one, and an inherited property silently won over a derived record's constructor parameter. Both are now
  reported.
- **Diagnostics linked pages that did not exist (#1430).** Every WHIZ descriptor now links its own diagnostics page
  and a test that asserts it, and a test keeps it that way. WHIZ900's summary named WHIZ200, and the pinned-identity
  comments named WHIZ100-102 for WHIZ110-112.
- **Documentation, comments, logs and database comments named `process_work_batch`, which no longer exists
  (#1463).** They now name the functions that do that work. The `process_batch` metric instruments keep their names
  for dashboard compatibility; nothing records them (#1535). Migration 220 corrects two function comments.
- **A multi-stream run over many streams ran its host out of memory.** A run stored the whole command, its stream ids
  included, and every claimed stream read and parsed it again, so a run's cost grew with the square of its stream
  count: an in-place rebuild over 127,288 streams parsed about 630 GB and OOMKilled its pods. A run's payload now
  carries no stream ids (they are rows of the run, and each stream's handler is given its id), the claim sends each
  run's payload once (migration 213), and a run's payload is read back once and shared by its streams. A run recorded
  by an earlier release still works each of its streams.
- **Each stream of a multi-stream rebuild did a whole rebuild's reporting.** Every stream published its own rebuild
  Started, Progress and Completed events, computed two row digests and logged at Information, about 254,000 system
  events for a 127,000-stream rebuild. A stream of a run now reports nothing of its own; the run announces its
  completion once (`MultiStreamCommandCompleted`). `IPerspectiveRebuilder.RebuildStreamOfRunAsync` is the new entry.
- **A rebuild of named streams reconciled the whole follower table.** The stream-group presence reconcile deleted
  across every row of a follower perspective after each rebuild, so a multi-stream rebuild did that once per stream.
  It is now scoped to the streams the rebuild replayed (`IWorkCoordinator.ReconcileFollowerPresenceAsync` overload).
- **A rebuild that excludes streams listed every stream in the store.** It took every distinct stream in the event
  store, whatever events it held, and recorded that list as a run for every perspective. It now takes each
  perspective's own streams less the excluded ones, listing the whole store only for a rebuilder that cannot list a
  perspective's streams, and then once per command.
- **A coalesced audit composite had no size bound.** A batch of up to 500 audit singles, each carrying the original
  message's body, became one message no broker accepts. `CoalescePolicyOptions.MaxBatchBytes` (default 192,000)
  splits a batch by size as `MaxBatchCount` does by count.
- **A published composite never reached another service (0.2623.0 regression).** Its outbox copy was routed to
  its owner's command destination, because a composite is not an `IEvent`, so only the publishing service got it
  and discarded it as its own echo. Nothing failed or logged. A composite now goes to the topic its inner events
  share, wherever the composite type is declared.
- **A composite written any way other than a single publish was not expanded here.** `PublishManyAsync` and a
  composite returned by a receptor now expand an owned composite locally, as a publish does; a returned composite
  is no longer refused as an owned command nothing handles.
- **A scheduled composite expanded at publish.** Its inner events were stored and handled before its time. It now
  expands when it is delivered.
- **A scheduled owned event was discarded when it came back.** The transport consumer dropped the publisher's own
  deferred copy as an echo, so the publisher never handled its scheduled event. A copy marked as deferred to its
  delivery now passes the echo check.
- **A message that is not an event, routed to the event store only, vanished.** Its row had no destination and was
  not stored. It is now refused with `DispatchRejectedException` (`NotAnEventCannotBeStoreOnly`); one that also runs
  here keeps its local run.
- **The transport re-routed an event to a command inbox by its type name.** An event in a `Commands` namespace or
  named `...Command` went to a command inbox no subscriber of the event reads. An event row now keeps the topic the
  dispatcher chose.
- **PreOutbox receptors never fired for an owned composite.** It was filtered as an owned command; it is filtered
  as an event.
- **A receptor's top-level `Route.X(...)` result lost its route (#1405).** `return Route.Local(evt);` cascaded
  the event with the default route; only a route inside a tuple was kept. Fixed with the dispatch policy work, and now
  covered on every entry by a test against the generated dispatcher.
- **A send or publish handled locally never fired PostLifecycle (#1413).** PostLifecycle now fires once per
  handling service on every local run. When the event is stored and a perspective on this service applies
  it, the perspective worker fires it after the apply instead, so it still fires once.
- **`PublishOnceAsync` kept its claim when the publish failed outside a transaction (#1414),** so a retry
  was refused as a duplicate. The claim is now released when the publish fails; when the release fails too,
  both errors surface together.
- **The periodic maintenance sweep ran on every instance (#1377).** It now runs only on the instance holding
  the maintainer duty, as the vacuum already did.
- **The destruction lifecycle stages never fired (#1381).** The receptors of `PreDestruction*` stages now
  run before the ephemeral reap removes a message, and `PostDestruction*` after, with the envelopes loaded
  before the reap. A throwing inline pre-destruction receptor holds the batch for a retry, as a throwing destruction hook
  does.
- **Startup failed with `ITransport (required by TransportConsumerWorker)` when no transport was composed
  (#1529).** Registration validation checked every requirement in the generated manifest, including types the
  composition never registers, so `AddWhizbang()` with a storage driver and no `AddTransportConsumer()` could not
  start. A requirement is now checked only when its implementation is registered in the service collection being
  validated: as a service type, an implementation type, an instance (keyed or not), or through a factory
  registration. For factory registrations the generator records the service type each descriptor carries in the
  new `ServiceRequirement.FactoryServiceTypes` (`IHostedService` for `AddHostedService<T>`); the two-argument
  `ServiceRequirement` constructor is unchanged. A registered type with a missing dependency still fails startup.
- **Closing a stream failed whenever a perspective cursor pointed into the truncated range, and left snapshots a
  rewind could no longer replay from (#1486).** `CloseStreamAsync` now moves such a cursor onto the first event
  that survives the close, removes every snapshot that needs a truncated event (keeping a moved perspective's
  current-state snapshot), and removes processed work rows and lapsed destruction holds for the truncated
  events. Deduplication entries are kept so a redelivered copy is still turned away. Two new statuses refuse a
  close instead of losing data: `held` (an active destruction hold covers an event in the range) and
  `cursor_behind` (a perspective in the range has not yet applied the first surviving event). Same behavior on
  the EF Core and Dapper schemas.
- **Evicting a perspective row left its cursor and every snapshot behind, and a row evicted by a cap or a
  stream-group cascade came back holding only the newest event (#1483).** An eviction now reclaims storage
  only: for each perspective of the evicted model, the row's cursor and every snapshot but the latest are
  removed with it, on every eviction path (row TTL, row cap, stream-group cascade, stamped expiry) and both
  drivers (migration 211). The next event on the stream rebuilds the whole row from that snapshot plus the
  later history, for any perspective whose rows can be evicted (`PerspectiveRowEviction.CanEvict`), not only
  row-TTL ones. An ephemeral perspective rebuilds from a surviving snapshot; with none it starts the row
  fresh and logs a warning. Perspectives on the same stream that fold another model are untouched. The EF
  Core registry payload now names each model's perspectives (`wh_perspective_registry.perspective_names`),
  recorded at the next startup; until then an eviction removes the row alone.
- **A scope event's scope could be overwritten or narrowed before the row was written (#1432).** A later message in
  the same batch replaced it, the model's `[InheritScope(OnCreate)]` filtered it (dropping the principals the event
  shared the row with), and `IPerspectiveScopeFor.ApplyScope` was given the last message's scope as the current one.
  The runner now writes the scope the event set, unfiltered, and gives `ApplyScope` the row's stored scope.
  `ScopeInheritance.Resolve` takes that scope in place of its `scopeEventChanged` flag.
- **A lens or perspective that inherits its interface from an abstract base was never registered (#1482).**
- **A generic message type broke the consumer's build (#1481).** The generators emitted the open type into generated
  code. They now skip it, and WHIZ015 warns at the type that generic message types are not supported.
- **The message registry was never enriched with docs URLs or tests (#1428).** The docs and tests maps were read
  case-sensitively against their camelCase keys.
- **Five build properties had no effect when set in a project (#1426):** `WhizbangMaxIdentifierLength`,
  `WhizbangStripTableNameSuffixes`, `WhizbangTableNameSuffixesToStrip`, `WhizbangGuidInterceptionEnabled` and
  `WhizbangQueryComposingAttributes`. The generator packages now declare them compiler-visible.
- **Build diagnostics can be suppressed where they appear (#1429).** Twenty were reported with no location (or an
  external-file one); they are now reported at the declaration they are about.
- **Build diagnostic messages said things the analyzers do not do (#1428).** Among them: WHIZ104 now finds
  `System.DateTime.UtcNow`, WHIZ102 no longer doubles WHIZ103 on an `HttpClient` call, WHIZ811 is silenced by the
  `[PolymorphicDiscriminator]` it suggests, and WHIZ900 covers every Inline stage and `WaitForAppliedAsync`.
- **Mutation testing never completed (#1444).**
- **Perspective sync waited on an event id the Postgres event stores never wrote (#1465).** Appending a
  message, the sync tracker recorded one id and the store wrote another, so `AppendAndWaitAsync` and any
  sync wait on that append could only time out. The tracker now sits inside the security-context decorator
  and records the id of the envelope the store writes.
- **The envelope a local handler looks up carried a fresh message id, not the stored copy's (#1464).** A
  receptor that keyed anything on its envelope's message id pointed at an id no stored row carried. It now
  carries the id of the copy the dispatch stores, on every entry that runs a message here and writes its
  copy.
- **A publish's or cascade's own handlers saw a different sent time and correlation than the copy it
  stored and sent (#1455).** Registering the envelope for the local run stamped the message a second time
  after its copy was serialized, and a batch stamped its copies only after its local runs, so a handler that
  recognizes redelivery by the sent time handled a genuine redelivery twice. The message is now stamped
  once, by its copy when there is one and by the local run when there is not, and a batch send runs under
  its copy's correlation.
- **An upgrade failed on an EF Core context with a schema of its own (#1530).** The ledger re-runs a migration
  whose text changed, and on such a context, with the default `search_path`, a re-run could not see what the
  earlier run had done: the guards asked `current_schema()`, which on the driver's connection is `public`, and
  the driver's qualification of bare framework table names reached inside string literals, turning
  `table_name = 'wh_inbox'` into a name no row has. Upgrading from 0.1024.0 failed at startup (046 raised 42701
  on `source_service_id`). The EF Core driver now runs each migration with `search_path` set to the migrated
  schema (the Dapper driver's schema already is `current_schema()`), the qualification leaves literals, quoted identifiers and comments alone, and migration 213 redefines the seven
  run-time functions that found their tables through `current_schema()` (the row reaps, `perform_maintenance`,
  the retention preview, the reap-target collection and the table-rewrite scan), which on such a context looked
  in `public` and did nothing. The literal rewrite had also changed the dead-letter `source_table` comparisons
  (`move_to_dead_letters` rejected `wh_outbox`), the stream-purge labels and the advisory-lock keys of a
  named-schema EF Core context. No shipped migration is edited; on a named-schema EF Core context, the migrations
  with a framework name in a literal or comment have a new hash and run once more on the first start.
- **A multi-instance Dapper fleet never dropped a retired schema object (#1253).** The Dapper driver ran
  its managed-object cleanup at registration with no instance id, so every running peer looked
  unreported and the fleet gate held every drop; it also ignored `Whizbang:Schema:Reconcile` and logged
  nothing. It now runs at host start with the instance id, the settings and the host's logger, and a
  maintenance step re-runs it between starts.
- **The schema-ready gate never opened on a Dapper host,** so the background workers of a Dapper-only
  host waited forever. The shared initializer opens it on both drivers.
- **Instances starting together on the Dapper driver migrated concurrently.** They now take the schema
  initialization lock, and a start that finds it held tells every `ISchemaInitializationObserver` before it
  waits.
- **Running the generated registration twice initialized each DbContext twice per start.** A second
  `DbContextInitializationRegistry.Register` for the same context now replaces the first.
- **A DbContext registered by the application missed the periodic cleanup re-run,** because only the
  generated turnkey registration keyed its managed-object manifest. The generated model registration now
  keys it for every context.
- **The bootstrap closure was never recognized as recorded:** the infrastructure schema script
  carried a stamp of the current time in a comment line, and that script is the first thing the
  closure hash covers, so every instance of one release computed a different hash, the record never
  matched, and every start still applied the bootstrap DDL under the lock. The schema builders no
  longer stamp the clock, and the hash covers statements only: comment-only lines and line endings
  take no part, so a header or a note cannot turn one release into two closures.
- **The perspective-table pass re-applied its DDL on every start of a schema whose models share a
  type name:** the per-perspective hash rows were keyed by the model's simple name, so models nested
  under feature holders (`Order.Model`, `Invoice.Model`, or several `SagaModel`s) shared one row;
  whichever wrote last owned it, the one compared first read as changed on every start, the slow
  path ran, and `CREATE TABLE` and `CREATE INDEX ... IF NOT EXISTS` took relation locks on hot
  tables for nothing, which is what deadlocked an instance starting under load. The rows are keyed
  by table name now, which is unique within a schema. The first start on this release records the
  new keys (one ordinary perspective pass); the rows under the old keys stay behind, inert.
- **A refused `pg_trgm` extension failed the whole perspective pass:** every substring index emitted
  its own `CREATE EXTENSION IF NOT EXISTS pg_trgm;`, and on a server that refuses the extension
  (not allow-listed, no privilege, not installed) that statement failed the pass, so a service with
  one substring index paid a failed startup attempt on every start and never got the index either
  way. The extension is now created once per table script inside a marked block, and the schema
  pass applies the block under a savepoint: a refusal (`0A000`, `42501`, `58P01`) skips the trigram
  indexes with one warning naming them and lets the pass complete; substring queries scan until an
  operator provides the extension. Both of the scripts the generator writes carry the block: the
  hash-tracked one per table, and the single script the pass falls back to when it cannot read the
  tracking tables. A trigram index left outside the block that creates the extension is the worse
  half of the same defect, because it reaches a server with no `gin_trgm_ops` operator class as an
  ordinary statement and fails the pass with nothing to skip.
- **One claim poll cost what the instance held, not what the poll returned:** with acquisition
  bounded, a busy instance -- thousands of leased rows, polling several times a second to re-offer the
  streams it holds so its drains keep a current work list -- still touched thousands of blocks per
  poll, because every part of the poll priced itself by the holdings: the three orphan guards read
  `instance_id IS NULL OR lease_expiry < now`, a disjunction with no index order, so proving nothing
  was orphaned examined every pending row; the outbox re-offer ranked every held row with a window
  function it never read; the inbox re-offer ranked every held row with three window functions and
  fetched every held row's heap page to do it; the perspective re-offer aggregated every held event;
  and the inbox event-store chain re-checked every held event against the event store on every poll,
  twice. Migration 158 makes each part cost what the batch costs: the guards probe two index heads
  (unowned rows, and the oldest lease through a new lease-expiry index per queue table); the outbox
  re-offer walks an arrival index and stops at the batch; the inbox and perspective re-offers walk
  lane indexes -- holder, priority bucket, and for the inbox kind and fresh-or-retried class -- one
  index-only probe per stream from a stream id drawn per poll, wrapping once and stopping at the
  batch; and the chain reads only rows without `wh_inbox.chain_emitted_at` and stamps the ones whose
  event it finds in the event store, leaving a row whose insert conflicted unstamped so the next poll
  re-attempts it. Priority order, per-stream order, the fresh-work share, partitions, leases and the
  outstanding budget are unchanged. Summed over the four tables and their indexes, one steady-state
  poll returning 300 rows costs 700 blocks and 305 tuples at 5,000 held rows per table and 925 blocks
  and 304 tuples at 40,000, against 5,425/35,012 and 42,388/280,012 before: eight times the holdings
  cost eight times as much before and 1.3 times as much now.
- **A held inbox stream is re-offered as one row per lane instead of every row it holds there**
  (behavior note, same change): the drain has consumed stream ids and pulled a stream's rows on
  demand since Phase H, and the batch hooks fold a stream's returned rows to its most urgent number
  and oldest arrival, so a stream's further rows inside one lane carried nothing a caller read while
  ranking them was the poll's whole cost. A stream whose rows span priority buckets or the
  command/event lanes is still returned once per lane, and the fold over those rows is unchanged, so
  `PriorityBatchEntry.PendingRows` now counts the lanes a stream appears in (usually one) rather than
  its rows in the batch. `p_max_streams` now bounds returned streams rather than returned rows for
  the inbox, which is what the parameter is named for; acquisition keeps its own row bound (145). The
  perspective re-offer's small-streams-first tier is gone with the per-stream drain that made it
  moot. `ClaimWorkPlanShapeTests` asserts both ceilings -- blocks touched and tuples examined, per
  table -- at a full budget of holdings and again at double it, because blocks alone would not catch
  an index-only pass over the whole holdings.
- **A transient database failure inside one perspective batch stopped the whole host:** the channel
  consumer loop rethrew after logging, the exception left `ExecuteAsync`, and the host's default
  `BackgroundServiceExceptionBehavior` (`StopHost`) shut the process down over a deadlock that would
  have passed on the next attempt — which also made every perspective apply failure a potential host
  stop, since the per-group catch reports, parks the row, and rethrows into that same loop. Two new
  types carry the fix: `TransientDatabaseFailure` classifies what a loop caught (deadlock,
  serialization failure, statement canceled, lock timeout, connection lost, insufficient resources, a
  wrapped command timeout, or the provider's own transient flag) from `DbException.SqlState` and
  `DbException.IsTransient` alone, wrappers and aggregates included, so Core still references no
  provider; `WorkerLoopRecovery` is the single place a loop decides what to do about it, picking
  between the worker's own two report lines and waiting a bounded backoff on the worker's
  `TimeProvider` (250 ms doubling to 30 s, reset by the next good iteration). The perspective consumer
  loop and its drain pass now report each failed batch once at Error with the reason, the SQLSTATE and
  the batch's stream ids, release those streams' unstarted rows so a sibling can take them instead of
  waiting out the lease, back off and continue; a failure that is not the database's is reported as a
  defect under its own event id and the loop still continues. The audit behind it also found the
  outbox drain worker's batch body guarded by a `finally` with no `catch`, so anything from the
  identity lookup, the security-context establishment or the publish flush ended the worker: it now
  has the same per-batch guard its inbox mirror always had. The claim poll and the inbox drain name
  the classification on the lines they already wrote.
- **The stored-form rewrite skipped when it lost the schema lock, and nothing ran it later:** the
  phase took the schema-init key with a single `pg_try_advisory_lock` and skipped at Debug on a lost
  attempt, on the assumption that the holder was another rewriter. The holder is often a sibling's
  bootstrap or DDL transaction, which converts nothing, and a sibling staged to wait for the migrator
  never rewrites, so two instances starting together left every table of the schema unconverted with
  nothing above debug level to say so. The phase now waits for the key (poll with backoff, up to the
  schema command timeout), holds it at transaction scope in a transaction of its own with a savepoint
  per table, logs once at Information that it is waiting, and gives up with a warning naming the key.
  A migrator killed mid-rewrite left the remaining tables unconverted for the same reason: every
  replacement instance was a waiter, and the rewrite sat before the wait behind a "not a waiter"
  guard. A waiter whose deferral ends with the migrator gone now runs the rewrite before it contends
  for the DDL lock, through the same body the migrator runs. The wait covers a race, not a queue: an
  instance that finds the key already held when it is about to rewrite watches the key through the
  same deferral a waiter uses, rather than sitting inside the rewrite for the whole ten-minute budget
  behind a migration, and rewrites when the wait ends.
- **The rewrite said nothing on success:** each table's DO block now raises a notice on every exit
  (converted with its update count, settled and skipped, or table absent) and the phase relays it at
  Information, followed by a one-line summary of the pass; a table that fails is still a warning
  naming it. `SchemaCommandBoundary.ApplyOnAsync`, whose only caller was the phase, is removed.
- **Maintenance ran at the peak of a bulk load:** the housekeeping gate measured settledness from
  unprocessed inbox rows and live leases alone, so a producer whose load sat in its outbox and a
  consumer whose load sat in its perspective events both read as idle, and the purges and the
  digest-epoch closure occupied two to three database backends for the length of the load.
  `ServiceBacklog` now carries bounded counts of pending outbox rows and pending perspective events,
  `IsSettled` requires all four measures to be zero, and a deferred sweep is logged at Information
  with every count rather than at Debug.
- **Store-backed pull sources polled an idle store at full cadence:** with every queue empty the
  poll loops alone committed over a hundred transactions a second per busy database. A
  `BasePollSignalSource` given a `PollIdleBackoff` stretches its interval after a run of empty ticks
  (doubling per tick up to a ceiling) and returns to the base interval on the first hit or on any
  reschedule; the work-available and due-schedule sources use three empty ticks and a one-minute
  ceiling.
- **A drain-path apply failure of any other kind parked nothing:** the cursor failure the drain
  path reported named no event, so the coordinator recorded nothing against the rows; the lease
  lapsed, the rows were re-claimed, and the same failure repeated every cycle with no backoff and no
  dead-letter. Every failure now parks each leased row of the group through the failure channel
  (reason Unknown, the exception's message as the error), the way the stored-form failure already
  did, so the rows back off and dead-letter at the configured threshold.
- **Tag payload-size thresholds could not be raised for one tag, or from configuration:** the
  warning threshold defaulted to 8 KiB, nothing bound either threshold from configuration, and a tag
  whose payloads are legitimately wide produced a warning per hook per message. `TagOptions` now
  takes per-tag thresholds (`UsePayloadSizeThresholds`), the processor resolves the tag's value before
  the global one, and `TagPayloadSizeConfigurationBinder` reads both, globally and per tag, from
  `Whizbang:Tags` without reflection; an empty value disables a threshold and a non-numeric value
  fails startup naming the key.
- **The claim poll priced itself by the backlog, not the batch:** measured under a bulk load, one
  `claim_work` call read tens of thousands of blocks and the queue tables were scanned whole several
  times per poll on every instance, so the poll alone took most of the database's cores and the
  backlog grew because of it. Two causes. The outbox acquisition sorted every pending row to keep a
  batch and the perspective acquisition aggregated every claimable event to choose its streams;
  migration 157 adds an arrival-order covering index for the outbox and an urgency-order one for
  perspective events, and `claim_orphaned_perspective_events` (150) now chooses streams from a bounded
  window of the most urgent events while still capturing a selected stream in full. And a session
  that polled while the tables were empty kept generic plans made for empty tables, which scanned
  them whole once they filled; `claim_work` now runs under `plan_cache_mode = force_custom_plan`, so
  the poll and everything it calls plan for the tables as they are. `ClaimWorkPlanShapeTests`
  reproduces both shapes and asserts the tuples one poll reads stay within a few batches.
- **The digest-epoch lane probes scanned the event store whole:** the closure and verification
  probes for a foreign lane filter on `origin_service_id` and `origin_commit_sequence`, and no index
  covered those columns, so each probe was a parallel sequential scan of the event store, about
  twenty per maintenance tick. Migration 155 adds `idx_event_store_origin_lane`, partial on the rows
  that have a lane.
- **The commit-order stamper sorted every unstamped row on every wake:** the eligibility query
  ordered the unstamped set by transaction id before taking a batch and ran whether or not anything
  was unstamped, about half a core per busy database on the backstop tick. The leader now asks the
  partial index whether any row is unstamped and runs the stamp only when the answer is yes; a wake
  that finds nothing raises `OnStampSkipped`.
- **Every instance start applied the bootstrap closure, and idempotent DDL still locks:**
  `CREATE INDEX IF NOT EXISTS` on an existing index takes a share lock on the table before it finds
  nothing to do, and an instance an autoscaler started under load deadlocked against the maintenance
  sweep and the poll sources. The transaction that applies the closure now records a hash of its
  scripts in `wh_bootstrap_closure` (created by the closure itself, in migration 000), and an
  instance whose closure is recorded applies nothing: no statement, no lock, no wait. A changed
  closure runs in full once; the migration ledger is untouched.
- **Outbox and inbox failure reasons were always Unknown:** `process_outbox_failures` and
  `process_inbox_failures` read the reason from a `FailureReason` element that nothing writes, so
  the dead-letter decision could not tell a lease that lapsed from a handler that threw. Migration
  156 reads `Reason` and `FailureReason` alike, as 154 did for perspective events.
- **A perspective document one path wrote and the other could not read:** an opaque document was
  written as canonical numbers under the persistence profile and read through the data source's
  default-profile options, so every read failed. Opaque columns are now bound to the persistence
  profile explicitly (`PerspectiveDocumentSerialization`). A row that still cannot be read is
  classified (`StoredFormUnreadable`), logged at Error once per perspective and stream with the path
  and the refusal, counted on `whizbang.perspective.read_failures`, reported on the
  `perspective-stored-forms` health component, and its leased rows are parked with backoff through the
  failure channel instead of retried every cycle.
- **The generated message context handed one serializer profile metadata built for another:** its
  metadata cache was keyed by type alone, so a date the wire profile asked for first was written by
  the persistence profile as a rendering, intermittently, into a document whose index casts the key to
  `bigint`. The cache is now keyed by the options the metadata was created for.
- **Perspective failures reported through the failure channel never matched a row:**
  `process_perspective_event_failures` read `EventWorkId`/`FailureReason` while the runtime serializes
  `MessageId`/`Reason`; migration 154 reads both spellings, so failures are recorded, backed off and
  dead-lettered at the configured threshold.
- **jsonb polymorphic `$type` round-trip** — jsonb reorders object keys so `$type` is no longer first;
  `AllowOutOfOrderMetadataProperties` is now set in the combined serializer options, so drained
  polymorphic events are no longer silently dropped.
- **Intra-pod stream-affinity gate** — a per-`(StreamId, PerspectiveName)` gate prevents concurrent
  perspective loops from applying same-stream events out of order (cross-pod stale-read / lost-update).
- **Custom-schema startup** — `process_work_batch` and related calls are schema-qualified at the call
  site (`HasDefaultSchema` + qualified names), so services on a non-default schema no longer fail to start.
- **Nested message send** — the generator emits CLR `+` for nested type names, so `SendAsync` resolves
  nested-type messages (temporary `.`→`+` workaround removed).
- **Lease/heartbeat renewal** — freshness-guarded renewal (and later removal of per-tick renewal) ends
  dead-tuple bloat and multi-second work-batch calls.
- **Work-coordination hardening** — WorkCoordinatorGate acquire deadlines + guaranteed-deadline timeouts
  with an `UnobservedTask` hook, an empty-`StreamId` structural sentinel, and an EF Core 10
  null-materialization workaround.
- **Connection-pool exhaustion** under bulk import (semaphore + batched inbox dedup); reduced `claim_work`
  contention.
- **Duplicate saga completions** under concurrent terminal handlers (via `PublishOnceAsync`).
- Perspective snapshot serialization uses the source-generated JSON registry options instead of
  reflection-based `JsonSerializer` (AOT-correct; WhizbangId-bearing model fields no longer collapse to `{}`).
- Perspective runner stream-id extraction walks the event's inheritance chain, so a `[StreamId]` declared
  on a base event type is detected (previously only directly-declared keys were found).

## [0.1.0-alpha] - 2026-01-19

### Added
- Initial alpha release
- Core messaging infrastructure (Dispatcher, Receptors, Message Envelopes)
- Event-driven architecture support
- CQRS patterns and implementations
- Event sourcing foundations
- Zero-reflection, AOT-compatible design
- PostgreSQL support with UUIDv7 and JsonB
- EF Core 10 integration with compiled models
- Dapper support for PostgreSQL and SQLite
- Azure Service Bus transport integration
- Whizbang CLI tool for code generation and management
- Comprehensive test suite with TUnit and Rocks (100% coverage)
- Source generators for zero-reflection functionality
- Observability and logging abstractions
- Partitioning and sequencing support
- Work coordination and batch processing

### Documentation
- Comprehensive API documentation at https://whizbang-lib.github.io
- Getting started guides and tutorials
- Code examples with verified tests
- Architecture documentation
- AI-enhanced documentation with MCP server integration

### Performance
- Baseline benchmarks established
- Optimized for .NET 10 and Native AOT

### Infrastructure
- GitHub Actions CI/CD pipelines
- SonarCloud integration for code quality
- Codecov integration for test coverage
- Dependabot for dependency management
- GitVersion for semantic versioning
