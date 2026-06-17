# Design: First-Class Event Upcasting

## Status: PROPOSED — not yet implemented

> RFC authored against Whizbang 0.688. First customer driving the requirement:
> **a consumer** (per-item saga streams migration). The pattern Whizbang ships here is the
> pattern every customer inherits — so the bar is *correctness of the seam*, not
> speed of the first implementation.

## Problem

Events are immutable, but the **shapes** we project from them are not. Over a
system's life, a stream's events change form:

- A field is added, renamed, split, or given a default.
- An event type is superseded by a newer one (`FooHappened` → `FooHappenedV2`).
- The **routing key changes** — events that were written onto stream A should, under
  the new model, materialize onto stream B (the case that motivated this RFC; see
  *Worked example* below).

Today Whizbang has **no concept of an upcaster** — no hook to transform a stored
event after deserialization and before it reaches routing / `Apply`. The only tools
are:

1. **Freeze the old `Apply` forever.** Every projection keeps a parallel set of
   replay-only `Apply` methods that reconstruct the historical shape. They are dead
   weight on the live path, must never be edited, and accrete with every schema
   change. (a consumer currently carries 8 such frozen sections behind `REPLAY-ONLY /
   FROZEN` banners.)
2. **Re-emit migrated copies.** Read the old events, append faithful new-shape twins.
   This permanently doubles event volume, perturbs commit order, and bloats every
   index and backup — for a transform that is conceptually a pure function on read.

Both are workarounds for a missing framework capability. This RFC specifies that
capability: **`IEventUpcaster`**, invoked at a **single unified materialization
seam** so the transform applies identically on *every* read path.

## The seam is the whole point

The subtle, load-bearing requirement is **not** "add a transform function." It is
"add it at a chokepoint every read path provably flows through." An upcaster wired
into one read path but not another produces **different projected state depending on
how the events were read** — the worst class of event-sourcing bug, because the
divergence is silent and only surfaces on rebuild.

### Where events are materialized today

All event materialization in Whizbang flows through `IEventStore` (good — it is
already a decorator chain: `AuditingEventStoreDecorator`,
`SecurityContextEventStoreDecorator`, `SyncTrackingEventStoreDecorator`,
`AppendAndWaitEventStoreDecorator` all wrap an inner store). But it flows through
**three distinct deserialization entrypoints**, and they are *not* unified today:

| Entrypoint | Used by | Returns |
|---|---|---|
| `ReadAsync<TMessage>(streamId, …)` | typed C# replay / streaming reads | `MessageEnvelope<TMessage>` |
| `ReadPolymorphicAsync(streamId, fromEventId, eventTypes, …)` | perspective runner replay/snapshot path (`PerspectiveRunnerTemplate` → `_eventStore.ReadPolymorphicAsync`); lifecycle reads | `MessageEnvelope<IEvent>` |
| `DeserializeStreamEvents(rawRows, eventTypes)` | perspective runner **drain mode** (`PerspectiveWorker.cs:1062`, converting `get_stream_events` rows) | `List<MessageEnvelope<IEvent>>` |

(`GetEventsBetween[Polymorphic]Async` is a fourth, used by lifecycle receptors.)

The earlier a consumer seam analysis assumed perspective rebuild bypassed `IEventStore`
entirely. **It does not** — drain mode calls `IEventStore.DeserializeStreamEvents`
and the snapshot/replay path calls `IEventStore.ReadPolymorphicAsync`. So the seam
*is* `IEventStore`. The real hazard is narrower but still fatal: these are **separate
methods**, and a transform added to one (say `ReadPolymorphicAsync`, the easy one to
reach from a decorator) but not `DeserializeStreamEvents` yields exactly the
read-path-dependent divergence above. Drain mode is the hot path in production;
`ReadPolymorphicAsync` is the cold/rewind path. They **must** apply the identical
upcaster pipeline.

> This same seam gap silently defeats **audit, PII-redaction, and
> encryption-at-rest on replay** — any cross-cutting transform that must be "true on
> every read" has the same all-entrypoints-or-bust requirement. Upcasting is the
> forcing function to get the seam right once, for all of them.

## Proposed model

### Contract

```csharp
namespace Whizbang.Core.Messaging;

/// <summary>
/// Transforms a stored event after deserialization and BEFORE routing / Apply.
/// Pure: no I/O, no time, no random (Rule 10 — same constraints as a perspective Apply).
/// Invoked identically on every IEventStore materialization path.
/// </summary>
public interface IEventUpcaster {
  /// Cheap predicate — return false fast for events this upcaster ignores.
  bool CanUpcast(IEvent @event);

  /// Transform the event into its current shape. MAY return a different concrete
  /// type, a re-keyed StreamId, or the same instance mutated. MUST be deterministic.
  IEvent Upcast(IEvent @event);
}
```

`Upcast` may:
- **Re-key** — change `StreamId` (the a consumer case: move a per-item event off the saga
  stream onto its per-item stream). The framework keys the projection row by the
  returned envelope's `StreamId`, so re-keying is how an upcaster redistributes
  history across aggregate boundaries.
- **Change type** — return a new concrete `IEvent` (`FooV1` → `FooV2`), provided the
  target type is registered in the AOT `JsonSerializerContext` / `IEventTypeProvider`.
- **Backfill fields** — set defaults for fields added since the event was written.

### Registration & composition

Upcasters form an ordered pipeline. Each event is run through the chain; multiple
upcasters may apply in sequence (V1→V2 then V2→V3). Registration is explicit and
AOT-friendly (no assembly scanning):

```csharp
services.AddWhizbang(/* … */)
  .AddEventUpcaster<SagaItemStreamUpcaster>()        // re-key legacy per-item events
  .AddEventUpcaster<LegacyDomainItemEventUpcaster>(); // map Single*/FieldPopulation* → SagaItem*
```

Ordering is **registration order**; the pipeline applies each upcaster whose
`CanUpcast` returns true, feeding the output of one into the `CanUpcast` of the next.
A pure-function chain on a single event — bounded, deterministic, snapshot-safe.

### Invocation seam (the invariant)

> **Invariant.** Every `IEventStore` materialization entrypoint — `ReadAsync<T>`,
> `ReadPolymorphicAsync`, `DeserializeStreamEvents`, `GetEventsBetween[Polymorphic]Async`
> — applies the **same** upcaster pipeline, at the **same** point (immediately after
> deserialization, before the envelope is returned). No entrypoint may skip it.

Two implementation shapes satisfy the invariant; either is acceptable, but the seam
must be enforced structurally, not by convention:

- **(a) A dedicated `UpcastingEventStoreDecorator : IEventStore`** that wraps the
  inner store and runs the pipeline in every method before returning. Matches the
  existing decorator precedent exactly. Risk: a future `IEventStore` method added
  without an upcast call silently reopens the gap.
- **(b) A shared internal `_materialize(envelope)` step** inside the deserialization
  helper that *all* concrete stores (`DapperEventStoreBase`, `EFCoreEventStore`,
  `InMemoryEventStore`) already funnel through, so a new read method physically
  cannot deserialize without passing through it.

**Recommendation: (b), enforced by a contract test** (below) that fails if any
entrypoint returns un-upcasted events. The decorator (a) is the easier first cut but
relies on every future method remembering to call the pipeline; (b) makes the
invariant a property of the type, not of reviewer vigilance.

Either way the pipeline runs **once per event per read**, on already-deserialized
objects — the marginal cost is `CanUpcast` (a type check + a couple of field
compares) for the overwhelmingly common no-op case.

## Worked example — a consumer per-item saga streams

a consumer migrated 10 sagas so per-item events route to **per-item streams**
(`StreamId = SagaItemStreams.Of(sagaId, itemIdentifier)`). Sagas created *before* the
migration wrote their per-item events onto the **saga's own stream**, in two
historical shapes. On rebuild through current code those do not reconstruct per-item
state (the projection rejects saga-stream-routed events). Two upcasters close it:

```csharp
// 1. Re-key: generic per-item event still sitting on the saga stream → per-item stream.
public sealed class SagaItemStreamUpcaster : IEventUpcaster {
  public bool CanUpcast(IEvent e) =>
    e is BaseSagaItemEvent i
    && SagaItemStreams.IsPerItemRouted(i.SagaName)
    && i.SagaId != Guid.Empty
    && e.StreamId == i.SagaId;                 // still on the saga stream → needs re-keying
  public IEvent Upcast(IEvent e) {
    var i = (BaseSagaItemEvent)e;
    e.StreamId = SagaItemStreams.Of(i.SagaId, i.ItemIdentifier);
    return e;
  }
}

// 2. Type-change + re-key: oldest legacy domain events → generic SagaItem* on per-item stream.
//    SingleJobStatusUpdateProcessed/Failed, SingleEntityEmbeddingProcessed/Failed,
//    SingleJobMappingProcessed/Failed, FieldPopulationStarted/Completed/Failed
//    → SagaItemContracts.SagaItem{Started,Completed,Failed}Event, StreamId = Of(sagaId, itemId).
```

Result: old events replay into the per-item `SagaItemModel` correctly and **durably**
(every future rebuild re-applies the transform), the byte-for-byte event log stays
immutable, and a consumer deletes its 8 frozen replay-only `Apply` sections. No re-emit, no
2× volume, no SQL backfill (a backfill is non-durable — wiped on the next rebuild —
and is explicitly rejected as anything but a throwaway dashboard patch).

## Replay-equivalence guarantee

The framework MUST guarantee: **for any stored event `e`, the state projected from
`e` is identical whether `e` is read via drain mode, replay, snapshot rehydration, or
ad-hoc query.** Upcasting only preserves this if the invariant above holds. This is
the one guarantee the contract test enforces and the one that, if broken, produces
silent state divergence.

## Testing

- **Seam contract test (load-bearing):** a single stored event of a type an upcaster
  transforms, materialized through *each* `IEventStore` entrypoint
  (`ReadAsync<T>`, `ReadPolymorphicAsync`, `DeserializeStreamEvents`,
  `GetEventsBetweenPolymorphicAsync`); assert all return the **upcasted** shape.
  This is the test that makes "every read path runs the same pipeline" a property,
  not a hope. Run against `InMemoryEventStore` and both Postgres stores.
- **Golden replay fixture:** seed a stream with old-shape events + a snapshot; rebuild
  the projection; assert the model matches historical truth; rebuild a second time and
  assert byte-identical state (durability).
- **Composition:** V1→V2→V3 chain applies in registration order; an event matching no
  upcaster passes through untouched and allocation-free on the no-op path.
- **AOT:** type-changing upcasters require the target type registered in the
  `JsonSerializerContext`; a missing registration fails at build/startup, not at
  replay.
- **Determinism (Rule 10):** an analyzer or test rejects `Upcast` bodies that touch
  `DateTime.Now`, `Guid.NewGuid()`, or I/O — same constraint as perspective `Apply`.

## Interaction with rewind / commit-sequence

Upcasting is read-time and does not alter stored `commit_sequence`, event ordering,
or cursor positions — the rewind/cursor machinery reasons about the unchanged stored
log, and upcasters transform only the in-memory projection of each row. Re-keying an
event's `StreamId` changes which projection row it lands on but not its position in
the commit sequence. (This is precisely why read-time upcasting is correct where
re-emit is not: re-emit perturbs the very sequence rewind depends on.)

## Non-goals

- **Mutating the event store.** Events stay immutable; upcasting transforms on read.
- **Re-running side effects** for already-materialized history (completion handlers,
  hooks). Upcasting feeds projections; it does not re-dispatch.
- **Cross-event aggregation.** An upcaster sees one event at a time. Folding multiple
  old events into one new event is out of scope (and would break per-row replay).

## Snapshot versioning & upgrade (companion to event upcasting)

Snapshots are a derived cache of the perspective model — but unlike events they carry no
shape version, so a model-shape change silently misparses or loses fields (the Tier-1
serialization fix exposed this: it changed the blob format with no way to *detect* a stale
blob). The fix mirrors event upcasting, applied to the model:

- **Stamp a `SchemaVersion`** on every snapshot blob at write time (the version of the model
  shape that produced it). Cheap to read back before deserializing.
- **Read through a configurable `SnapshotUpgradePolicy`:**
  - `RebuildFromEvents` *(default, safest)* — version mismatch ⇒ discard the snapshot and
    replay from events. Always correct: snapshots are a pure derived cache, so the stamp just
    turns a silent misparse into an explicit "stale → rebuild". No old deserializers needed.
  - `LazyUpcast` — keep old-version model deserializers in C# (snapshot upcasters keyed by
    `SchemaVersion`) and upgrade a stream's snapshot when it is next used.
  - `UpgradeOnStartup` — scan and rewrite all snapshots to the current version at boot.
  - `None` — treat a mismatch as an error.
- **Old-version deserializers** (for `LazyUpcast`/`UpgradeOnStartup`) are registered, versioned
  C# functions — the snapshot analogue of `IEventUpcaster`. AOT-safe (explicit registration, no
  reflection).

This supersedes the interim "invalidate old snapshots on deploy" note from the Tier-1
serialization change: `RebuildFromEvents` makes that automatic and detectable rather than a
manual deploy step.

## Open questions

1. **Decorator (a) vs shared-step (b).** Recommendation is (b) for structural
   enforcement; (a) is a faster first cut. Decide before implementation — it shapes
   the contract test.
2. **`[ReplayFrozen]` analyzer.** Once upcasting lands, the frozen replay-only `Apply`
   methods become deletable. Is a transitional analyzer/attribute (mark a method as
   historical, hash its body, warn on edit) still worth shipping, or does upcasting
   make it moot? (Original a consumer ask; upcasting subsumes most of its value.)
3. ~~**Snapshot invalidation.**~~ *Resolved* — see "Snapshot versioning & upgrade" above:
   stamp `SchemaVersion`, read through `SnapshotUpgradePolicy` (default `RebuildFromEvents`).

## Rollout / sequencing

1. Land `IEventUpcaster` + registration + the chosen seam (b recommended) + the seam
   contract test. Pure framework, no customer coupling.
2. Ship the golden-fixture + composition + AOT + determinism tests.
3. a consumer registers `SagaItemStreamUpcaster` + `LegacyDomainItemEventUpcaster`, deletes
   its 8 frozen `Apply` sections and removes `TryComplete`/`TryFailFast`/
   `SagaApplyHelper` (its Slice 3 cleanup, currently blocked on those frozen methods).
4. Document the `[ReplayFrozen]` decision (open question 2) and snapshot invalidation
   (open question 3).
