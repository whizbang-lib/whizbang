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
- **A built-in partition manager for time-ordered framework tables (#1611).** With
  `Whizbang:Workers:Maintenance:Partitioning:Enabled` (default `false`), the maintenance cycle converts `wh_log` once
  to weekly partitions by `logged_at` (primary key `(log_id, logged_at)`, every writer unchanged, gated on the
  `wh_log_partition_version` marker), creates partitions `:CreateAhead` (default 3) intervals ahead with a default
  partition, and drops partitions wholly past `log_retention_days` instead of deleting rows. Only the maintainer-duty
  holder changes partitions, and no lock is waited for: an expired partition is emptied while writers are on the
  table and detached later. Migration 226.

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

#### Messaging & delivery
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
- **Every runtime parse of a message can be counted (#1691, part of #1526).** `MessageParseDiagnostics` is a
  public, AOT-safe seam through which each parse of a stored or received message on the outbox, inbox, transport
  receive, offload, event store, lifecycle, multi-stream and redelivery paths reports its message id, source, kind
  and call site; with no observer subscribed a parse site costs one volatile read. `MessageParseRecorder` counts and
  summarizes the parses of chosen messages. Regression tests pin today's counts per flow (a locally published event
  feeding two perspectives is parsed 17 times), and `plans/message-cache.md` proposes the cache that lowers them.
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

### Fixed
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
