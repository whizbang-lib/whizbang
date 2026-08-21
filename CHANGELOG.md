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

### Fixed
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
