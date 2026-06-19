# Event Upcasting

**Status**: Implemented (core) — v0.689+
**Namespace**: `Whizbang.Core.Messaging`
**Design / rationale**: [`docs/design/event-upcasting.md`](./design/event-upcasting.md)

## Overview

Events are immutable, but the shapes you project from them are not. Over a system's life a
stream's events change form — a field is added or renamed, an event type is superseded
(`FooV1` → `FooV2`), or the **routing key** changes (events written onto stream A should now
materialize onto stream B).

**Upcasting** transforms a stored event into its current shape **on read** — after
deserialization, before routing / perspective `Apply` — without ever rewriting the event log.
It is the durable alternative to freezing old `Apply` methods forever or re-emitting migrated
event copies (which permanently doubles event volume).

The load-bearing guarantee is the **seam**: upcasters run on *every* polymorphic read path
(drain, replay, snapshot rehydration, lifecycle), so projected state never depends on how an
event was read.

## Table of Contents

1. [When to use it](#when-to-use-it)
2. [The `IEventUpcaster` contract](#the-ieventupcaster-contract)
3. [Registration & ordering](#registration--ordering)
4. [What runs where (the seam)](#what-runs-where-the-seam)
5. [Three transform shapes](#three-transform-shapes)
6. [Rules & constraints (AOT, purity)](#rules--constraints-aot-purity)
7. [Limitations & current status](#limitations--current-status)
8. [Snapshots](#snapshots)
9. [Testing](#testing)

## When to use it

Reach for an upcaster when **stored events** need to project differently than they were
written, and you want that correction to survive every future projection rebuild:

- A new field must be backfilled with a default for old events.
- An old event type should project as a newer type.
- Old events must be re-keyed onto a different stream (the a consumer per-item-saga-streams case).

Do **not** use it for forward-only changes you can make in the producer, or for one-off data
fixes (those belong in a migration). Upcasting is for the *read model of history*.

## The `IEventUpcaster` contract

```csharp
public interface IEventUpcaster {
  bool CanUpcast(IEvent storedEvent);   // cheap predicate — true only for events you transform
  IEvent Upcast(IEvent storedEvent);    // transform; only called when CanUpcast returned true
}
```

`Upcast` may return a **new instance** (type change) or the **same instance mutated** (re-key /
backfill). Cast to the concrete type you own — that is how it stays reflection-free.

```csharp
// Backfill + type change: OrderPlacedV1 -> OrderPlacedV2 (adds Channel, default "web")
public sealed class OrderPlacedV1ToV2 : IEventUpcaster {
  public bool CanUpcast(IEvent e) => e is OrderPlacedV1;
  public IEvent Upcast(IEvent e) {
    var v1 = (OrderPlacedV1)e;
    return new OrderPlacedV2 { StreamId = v1.StreamId, Total = v1.Total, Channel = "web" };
  }
}
```

```csharp
// Re-key: move a per-item saga event off the saga stream onto its own per-item stream.
public sealed class SagaItemStreamUpcaster : IEventUpcaster {
  public bool CanUpcast(IEvent e) =>
    e is BaseSagaItemEvent i && SagaItemStreams.IsPerItemRouted(i.SagaName)
    && i.SagaId != Guid.Empty && e is { } && i.StreamId == i.SagaId;
  public IEvent Upcast(IEvent e) {
    var i = (BaseSagaItemEvent)e;
    i.StreamId = SagaItemStreams.Of(i.SagaId, i.ItemIdentifier);  // set the [StreamId] property
    return i;
  }
}
```

## Registration & ordering

Registration is explicit (no assembly scanning — AOT-safe). **Order matters**: register
oldest-shape → newest-shape so a stale event walks the whole chain in one pass.

```csharp
services
  .AddEventUpcaster<OrderPlacedV1ToV2>()   // applied first
  .AddEventUpcaster<OrderPlacedV2ToV3>();  // then this — V1 event reaches V3 in one pass
```

The pipeline runs each registered upcaster once, in order; the output of one feeds the next
upcaster's `CanUpcast`. A single forward pass is bounded and cannot loop. Register newest-first
and a `V1` input would stop at `V2` — so keep them oldest-first.

When no upcasters are registered the read path is an allocation-free passthrough — you pay
nothing.

## What runs where (the seam)

`UpcastingEventStoreDecorator` sits **innermost** in the `IEventStore` decorator stack (below
security/sync/append-and-wait), so every outer decorator and consumer observes upcasted events.
It applies the pipeline on all three **polymorphic** read paths:

| Path | Method | Used by |
|------|--------|---------|
| Drain (hot path) | `DeserializeStreamEvents` | perspective runner, live projection |
| Replay / snapshot | `ReadPolymorphicAsync` | rebuild, rewind, ad-hoc polymorphic reads |
| Lifecycle | `GetEventsBetweenPolymorphicAsync` | post-perspective lifecycle receptors |

This is the **unified-seam invariant**: the same pipeline on every path, enforced by a contract
test, so the same stored event always projects to the same state.

## Three transform shapes

- **Backfill** — set fields added since the event was written (return same or new instance).
- **Type change** — return a different concrete `IEvent`. The target type must be registered in
  the source-generated JSON context (it is, if it's a normal event).
- **Re-key** — set the `[StreamId]`-marked property to land the event on a different stream. On a
  perspective **rebuild** the runner re-routes the event onto the new stream's row (see below).

## Rules & constraints (AOT, purity)

- **Pure / deterministic (Rule 10)** — no I/O, no clock, no randomness. Same input ⇒ same
  output, on every read. Upcasters run constantly; keep `CanUpcast` allocation-light.
- **No reflection / AOT-safe** — operate on `IEvent` and cast to the concrete types you own.
  Registration carries the trim annotations needed for native AOT.
- **One event at a time** — an upcaster cannot fold multiple events into one (that would break
  per-row replay).

## Limitations & current status

- **Typed reads delegate unchanged.** `ReadAsync<TMessage>` / `GetEventsBetweenAsync<TMessage>`
  return a concrete `TMessage`, so a type-changing upcast can't be expressed there — and those
  are not projection-rebuild paths. Type-change/re-key apply on the polymorphic paths above.
- **Re-key re-routes on rebuild, not on live drain.** During a perspective rebuild the generated
  runner's `RunRebuildAsync` reads a physical stream's events (upcasted), partitions them by their
  **post-upcast** `[StreamId]`, and projects each partition onto its own row — so a re-keyed
  historical event lands on its new stream's row, not the stored one. The live drain path is
  deliberately untouched: new events are already written to their correct stream, so only rebuild of
  historically mis-keyed events needs re-routing. Validated end-to-end by
  `RekeyThroughRebuildTests` (Testcontainers) and the a consumer `SagaItemStreamUpcasterReplayTests`.
- **Rebuild does not purge first**, so re-key-on-rebuild is a **run-once** history migration: run it
  once per environment (a second rebuild is a no-op on already-materialised target rows). Backfill
  and type-change work on all paths today.

## Snapshots

Snapshots are a derived cache of the perspective model and carry their own shape. Snapshot
versioning + `SnapshotUpgradePolicy` (default `RebuildFromEvents`) is specified in the
[design doc](./design/event-upcasting.md#snapshot-versioning--upgrade-companion-to-event-upcasting)
and tracked separately — it is the snapshot analogue of event upcasting.

## Testing

- Pipeline composition/ordering/passthrough:
  `tests/Whizbang.Core.Tests/Messaging/EventUpcasterPipelineTests.cs`
- Registration & DI order:
  `tests/Whizbang.Core.Tests/Messaging/EventUpcasterRegistrationTests.cs`
- Seam contract (applies on every polymorphic entrypoint):
  `tests/Whizbang.Core.Tests/Messaging/UpcastingEventStoreDecoratorTests.cs`
