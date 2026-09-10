# Priority on the wire: every seam carries the number, audit and system work is background

Branch `fix/priority-on-the-wire` off develop (ea22fbd6e, the priority PR merged). One pull request.

## What the live test showed

A bulk import ran with a chat message alongside on a deployment carrying the priority PR. The producer
stamped the import's fan-out 250 on its outbox and its own perspective rows; every other service received the
same events at 150, the undeclared default. The chat command still won (50 against 150), but the design's
margin was missing. In the same window one outbox held thousands of audit events at 150 ahead of everything
else, and the outbox publishes in arrival order.

Two structural causes, then a long tail:

1. The generated JSON metadata for typed envelopes (`MessageJsonContextGenerator._generateMessageEnvelopeFactories`)
   emits five properties (`id`, `p`, `h`, `tgt`, `sto`) and no `pri`; the polymorphic registry
   (`JsonContextRegistry._createPolymorphicEnvelopeTypeInfo`) emits three. A typed envelope serialized through
   either loses the number before it reaches any row or wire.
2. `OutboxDrainWorker._toOutboxWork` rebuilds the wire envelope field by field without the number, and
   `OutboxWork` has no property to carry it; `fetch_outbox_batch` does not return the row's column.

## Rule

Any place that constructs or copies an envelope or a work row either copies the number from its source or
declares a band with a reason, and ships with a test asserting the number on the far side of the seam. Audit,
system, integrity, backfill, repair, redelivery and replay work declares BACKGROUND. A minted composite carries
a number folded from its members (most urgent by default, the same fold the claim applies to a stream), so an
import's composite is 250 and the audit composite is 250.

## Decisions taken while building

- **Row over envelope.** `WorkPriority.FirstDeclared(row, envelope)`: the row's number is authoritative once it
  exists; a row fetched before the column existed falls back to the envelope. Both drains apply it. On the
  consumer side the row is the consumer's classification, which is what the handler's children must inherit.
- **The composite fold is a binding option.** `CoalescePolicyOptions.PriorityFold`: `MostUrgent` (default),
  `LeastUrgent`, `Manual` with `PriorityFor(batch)`. All-undeclared members, or Manual without a callback,
  leave the composite undeclared; the worker never invents a band. The ship worker consults no producer hook:
  the members were declared through the hooks when produced, and a fold is not a new declaration (running the
  chain would turn an all-undeclared composite interactive, because the worker has no handler on its context).
- **One fold helper for everything.** `WorkPriority.MostUrgent/LeastUrgent/Average` over numbers or
  `IPrioritized` items, `FirstDeclared` for the row-over-envelope rule. `ClaimedInboxStreamFolder` now folds
  with `MostUrgent` instead of its own `Math.Min` loop; the composite fold uses the same call.
- **A plain API.** `DispatchOptions.WithPriority(n)` (kept by the default producer hook), `envelope.WithPriority(n)`,
  `IPrioritized` on `IMessageEnvelope`, `OutboxMessage`, `InboxMessage`, `OutboxWork`, `InboxWork`,
  `OutboxBatchRow`, `InboxBatchRow`.
- **Migration 151.** `fetch_outbox_batch` re-created verbatim from 096 plus `priority`; `recover_dead_letter`
  re-created verbatim from 125 with every re-created row (outbox, inbox, perspective, broker) declared
  `__PRIORITY_BACKGROUND__` (new constant, rule 12). The 096 body's raw empty uuid became `__EMPTY_UUID__`.
- **Typed metadata omits zero.** The generated property 5 (`pri`) and the polymorphic property (`Priority`)
  both set `ShouldSerialize` to omit an undeclared number, like the attribute-honoring shape.

## The seams

| group | seam | action | test |
|---|---|---|---|
| A wire | generator envelope metadata | emit `pri` (omitted when zero) | MessageJsonContextGeneratorTests |
| A wire | `JsonContextRegistry._createPolymorphicEnvelopeTypeInfo` | emit `Priority` | JsonContextRegistryTests |
| A wire | `EnvelopeSerializer.SerializeEnvelope` | copy `envelope.Priority` | EnvelopeSerializerTests |
| A wire | `MessageEnvelopeExtensions.ReconstructWithPayload` (2) | copy | MessageEnvelopeExtensionsTests |
| A wire | `BodyOffloadPostSerializeHook._buildClaimEnvelope` | copy `original.Priority` | BodyOffloadPostSerializeHookTests |
| A wire | `OutboxDrainWorker._toOutboxWork` + `OutboxWork.Priority` | row, fall back to stored envelope | OutboxDrainWorkerGapTests |
| A wire | `InboxDrainWorker._toInboxWork` | stamp the row's number on the envelope | InboxDrainWorkerTests |
| A wire | `fetch_outbox_batch` (migration 151) + EF and Dapper readers | return and read `priority` | MessagePrioritySqlTests, DapperWorkCoordinatorWithDataTests |
| A wire | Dapper `FetchInboxBatchAsync` DTO | read `priority` | DapperWorkCoordinatorWithDataTests |
| B fan-out | `CompositeInboxFanout` child envelopes and rows (4 sites) | copy the composite's number | CompositeInboxFanoutTests |
| B fan-out | `CoalesceShipWorker` mint + `FetchPendingCoalesceAsync` | fold per binding | CoalesceShipWorkerTests, PriorityOnTheWireSqlTests |
| F api | `WorkPriority` folds, `IPrioritized`, `DispatchOptions.WithPriority`, `WithPriority` | new | WorkPriorityTests, DispatcherPriorityStampingTests, MessageEnvelopeExtensionsTests |
| C background | `AuditOutboxMessageBuilder` | BACKGROUND | AuditOutboxMessageBuilderCoverageTests |
| C background | `recover_dead_letter` (migration 151), all four sources | BACKGROUND | PriorityOnTheWireSqlTests |
| C background | `AuditingEventStoreDecorator`, `SystemEventEmitter` | BACKGROUND | new tests |
| C background | `IntegrityAuditWorker`, `IntegrityCheckpointWorker`, `IntegrityCheckpointReceptor`, `IntegrityManifestReceptors` (direct transport publishes) | BACKGROUND | new tests |
| C background | `RepairDrainWorker`, `SubscriptionExpansionWorker`, `RedeliveryPump` | BACKGROUND | new tests |
| C background | event store replay reconstructions (EF, Dapper Postgres, Dapper Sqlite, InMemory) | BACKGROUND where a queue is reached | new tests |
| D producer | `DispatcherTransportBridge._createEnvelope`, `TransportManager._createEnvelope` | ambient parent | new tests |
| D producer | append-time wraps (`SecurityContextEventStoreDecorator`, `InMemoryEventStore`, Dapper and EF stores, `EventEnvelopeJsonbAdapter`) | ambient parent (copy `pri` when the jsonb has it) | new tests |
| E end to end | in-memory transport: dispatch, outbox, drain, transport, consumer, inbox, dispatch, perspective | the number at every stored point, interactive and background | new integration tests |

Not seams: the dispatcher's sentinel envelopes, the test harnesses, read-only reconstructions that never reach
a queue. HotChocolate, Mutations, FastEndpoints and SignalR do not touch envelopes; they dispatch.

## Consumer side (a host)

The audit tag joins a host's background declarations. Nothing else changes: the namespace rules already push
the import down at the consumer; with the wire carrying the number, cross-service inheritance makes the
consumer rules a backstop rather than the mechanism.

## Progress

- [x] Red observed for the drain, the outbox fetch and the audit builder (cycle L).
- [x] Red observed for groups A, B, F: serializer, reconstruct (2), offload claim, fan-out children, generator
      metadata, polymorphic metadata, folds (4), coalesce fold options (3), `WithPriority` on the options,
      Dapper outbox and inbox fetch (cycle M).
- [x] Red observed for the inbox drain stamp and the SQL seams (coalesce fetch, recovery x4) (cycle M3). The
      coalesce fold store test was green on first run: `store_outbox_messages` already reads `Priority`; it stays
      as the guard on the row.
- [x] Green for A, B, F and the audit builder; migration 151; `ClaimedInboxStreamFolder` refactored onto the
      shared fold (cycle M4).
- [x] Red observed for group C (27 tests over 8 classes; the "no handling left behind" guards pass by design)
      (cycle M5b). Green applied: every emitter declares BACKGROUND; the integrity workers enter a background
      handling so what they dispatch inherits it.
- [x] Group C green (cycle M6). Three manifest receptor tests (repair, drill-down, bulk backfill) moved onto the
      existing `IntegrityManifestReceptorTests` harness, which drives those branches; their production lines were
      reverted, the assertion-level red observed (cycle M6b), and the lines restored.
- [x] Red observed for group D (cycle M6): transport bridge and manager (2 of 3 each; the no-parent case is a
      guard), the two in-process append wraps, the SQLite store's ambient case. The jsonb adapter and the Postgres
      store tests did not compile in that cycle (a missing using); their red rides cycle M7 only if the build
      order lets it, otherwise it is recorded as not separately observed.
- [x] Group E was green on first run: it composes seams that were already green (A and F); it is the guard that
      they compose, not a seam of its own.
- [x] Group D green (cycle M7); the jsonb adapter and the Postgres store edits were reverted, their red observed
      (cycle M7b), and restored (M7c). Full suite: 22,678 passed, 0 failed; Release build clean.
- [x] Open issues folded in: #743 (red: the flipped characterization test and an event-type sibling, cycle N1;
      green N2) and #742 (the unit-of-work suites inherit the contract, the subscription contract runs against the
      in-process transport; the docs repository's tests map assesses link health and flagged both files on develop
      and neither on the branch).
- [x] docs: message-priority page gained "On the wire", "Composites", "The C# API", "Background work",
      "Producer boundaries outside the dispatcher", "End to end", four decisions, references
- [x] host pattern doc: "How the number travels", "Composites", the API note, the audit tag row
- [ ] PR, pr-health clean, merge, publish, host bump, live retest: import events at 250 at every consumer,
      composites at 250, audit at 250 (the plan file is archived with the PR once the retest confirms)
