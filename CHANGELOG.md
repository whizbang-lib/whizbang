# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- **Event upcasting** — first-class read-time event transformation. `IEventUpcaster`
  (re-key / type-change / field-backfill, pure & AOT-safe) + `EventUpcasterPipeline`
  (ordered, registration-order composition) + `AddEventUpcaster<T>()` registration +
  `UpcastingEventStoreDecorator` wired innermost in the `IEventStore` stack, applying the
  pipeline on every polymorphic read path (`ReadPolymorphicAsync`, `DeserializeStreamEvents`,
  `GetEventsBetweenPolymorphicAsync`) — one unified materialization seam. Zero-cost passthrough
  when no upcasters are registered. See `docs/design/event-upcasting.md` + `docs/event-upcasting.md`.
- **Re-key upcasters re-route on rebuild** — a perspective rebuild now honours an upcaster that
  changes an event's `[StreamId]`: `IPerspectiveRunner.RunRebuildAsync` (new, default-implemented)
  reads a physical stream's events, partitions them by their post-upcast target stream id, and
  projects each partition onto its own row. The generated runner gains a `ResolveTargetStreamId`
  switch; the live drain hot path (`RunAsync`/`RunWithEventsAsync`) is unchanged. Without a re-key
  upcaster the rebuild is a single partition — byte-for-byte the old behaviour. Validated by
  `RekeyThroughRebuildTests` (Testcontainers). Enables the JDX per-item-saga-streams history
  migration via a one-time projection rebuild.
- **Framework-wide serialization versioning** — `SerializationVersion.CURRENT` (version of the
  JSON serialization logic), `VersionedJsonEnvelope` (stamp/read a version on any persisted JSON
  blob), `IVersionedJsonSerializer` + `IVersionedJsonSerializer<T>` and
  `VersionedJsonSerializerRegistry` (recall the correct serializer by version).
- **Snapshot serialization versioning** — `SnapshotEnvelope` + `SnapshotUpgradePolicy`
  (`RebuildFromEvents` default / `None` / `LazyUpcast` / `UpgradeOnStartup`); perspective-runner
  snapshots are stamped on write and, on a version mismatch or legacy blob, rebuilt from events
  instead of misparsing. `PerspectiveSnapshotOptions.UpgradePolicy` added.
- **Size-aware serialization** — `SerializationResult` (bytes + `SizeBytes` + content type +
  version) and `SerializationOptions` (forward-extensible), produced by `WireEnvelopeSerializer`
  at a single serialize-once point so the message body-path (inline vs offload) decision reads the
  size off the result.
- Canonical `EventTypeMatchingHelper.BuildTypeLookup` / `TryResolveType` — one normalized
  stored-`EventType` → `Type` resolver shared by every event-store read path.

### Changed
- EFCore and Dapper event stores route all polymorphic type resolution through the shared
  `EventTypeMatchingHelper` resolver (removes duplicated per-store type maps).
- Publish path (`TransportPublishStrategy`) and both RabbitMQ + Azure Service Bus transports route
  wire serialization through the shared `WireEnvelopeSerializer`; ASB receive resolves types via
  the shared `BodyClaimWireHelper` (its type-binder / raw-receptor fallbacks preserved).
- Perspective snapshot blobs are now versioned envelopes; pre-existing unversioned snapshots are
  transparently rebuilt from events on first read.

### Fixed
- Perspective snapshot serialization now uses the source-generated JSON registry options instead
  of reflection-based `JsonSerializer` (AOT-correct; WhizbangId-bearing model fields no longer
  collapse to `{}`).
- Perspective runner stream-id extraction now walks the event's inheritance chain, so a `[StreamId]`
  declared on a base event type (e.g. a shared `BaseSagaItemEvent`) is detected. Previously only
  directly-declared `[StreamId]` properties were found, which left the re-key-on-rebuild
  `ResolveTargetStreamId` switch empty for inherited-key events (re-key silently fell back to the
  physical stream). Regression: `RekeyThroughRebuildTests` now exercises an inherited `[StreamId]`.

## [0.1.0-alpha] - 2026-01-XX

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
