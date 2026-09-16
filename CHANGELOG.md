# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

> The `[Unreleased]` section below was reconstructed from the merged pull-request history
> (the early development history was squashed into the foundation commit), so it captures the
> ~6 months of pre-1.0 work between the alpha and today.

## [Unreleased]

### Added

#### Messaging & delivery
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
- **OpenTelemetry metrics** — instrumentation across the dispatch, workers, coordinator, perspectives,
  transport, and lifecycle meters.
- **`whizbang migrate` CLI** — Marten/Wolverine migration analyzers + transformers (JSONB LINQ,
  global-using rewrites, package-ref upgrades); PgBouncer-compatible schema initialization; the
  `ExtractMessageRegistry` MSBuild target shipped via NuGet.
- **API hygiene** — a `SyncMode` enum + `LocalInvokeAndSyncAsync(msg, SyncMode, ct)` overload; the old
  timeout-based overloads are now `[Obsolete]`.

### Changed
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

### Fixed
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
