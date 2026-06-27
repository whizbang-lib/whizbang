# Composite Events

**Status**: Phase A implemented — dispatch-time fan-out (v0.758+)
**Namespace**: `Whizbang.Core.Messaging`
**Design / plan**: [`plans/composite-events-turnkey.md`](../../../plans/composite-events-turnkey.md)

## Overview

A **composite event** bundles many inner events into one transport hop. A bulk operation that
produces N domain events (e.g. "350 jobs imported" emitting hundreds of field events) sends **one**
wire message instead of N — one outbox row, one publish, one receive — then **fans out** into the N
inner events at the receiver. The composite itself is **never written to the event store**; only the
expanded inner events are persisted, so replay reads the inner events back as if no composite ever
existed.

The load-bearing principle:

> A composite is an **ordinary message** everywhere except **one seam — the fan-out** — and that seam
> sits **inside the durable inbox / dispatch / retry / DLQ envelope**, not outside it at the transport
> edge.

This is what makes a composite recoverable: if fan-out fails, the composite is just *an inbox row that
failed* → normal retry → dead-letter via the existing `IDeadLetterStore.MoveAsync(wh_inbox, …)`. There
is no separate composite-failure path.

## The three message roles

1. **Composite** — dispatchable but **never event-stored**. Lives transiently as an inbox row:
   received → (pre-fanout receptors) → fan out → deleted.
2. **Children (inner events)** — ordinary received events, produced by fan-out as inbox rows, processed
   normally (event store + perspectives + receptors). They **never outbox** (see the invariant below).
3. Everything downstream of fan-out sees only children — no composite awareness.

## Dispatch lifecycle

```
claim composite inbox row (InboxDispatchWorker.ProcessOneInnerAsync)
  → deserialize payload → typed ICompositeEvent
  → PRE-FANOUT: fire inline IReceptor<TComposite> under an outbox collector (Phase B)
  → FANOUT:     CompositeInboxFanout.TryExpand → N child inbox messages
  → COMMIT:     one HandlerCommitRequest { NewInboxMessages = children,
                                           NewOutboxMessages = pre-fanout emissions,
                                           InboxCompletion.Status = EventStored }
                → process_inbox_completions stores children + pre-fanout events
                  + DELETEs the composite (one tx)
  → children dispatch normally
```

- **Recognition** — the source generator (`ReceptorRegistryQueryGenerator`) discovers every concrete
  `ICompositeEvent` type and lists it in `AnyConsumerTypes`, so the receive-boundary drop-gate keeps a
  composite alive long enough to reach the dispatch seam. The abstract
  [`CompositeEventBase`](#authoring) is skipped (never dispatched).
- **Fan-out** is AOT-clean: `CompositeInboxFanout` builds each child as a
  `MessageEnvelope<IMessage>` and serializes it through `IEnvelopeSerializer.SerializeEnvelope`, which
  derives the wire type from the inner event's **runtime** type — no runtime reflection.
- **Atomicity** — the children and the composite-row deletion commit in one transaction
  (`commit_handler_batch` → `process_inbox_completions`). The `EventStored` bit (value `2`) drives the
  DELETE.
- **Failure** — a cap breach (`MaxInnerEventsAllowed`) or child-serialization error returns no partial
  fan-out; the composite row is dead-lettered with `MessageFailureReason.CompositeInnerEventLimitExceeded`
  / `CompositeExpansionFailure`. When no `IDeadLetterStore` is wired, it falls back to the legacy
  mark-Published terminal completion.

## Authoring

Derive from `CompositeEventBase` — one line for the common case:

```csharp
public sealed class OrderBulkImportComposite : CompositeEventBase;

// producer:
var composite = new OrderBulkImportComposite {
  StreamId = jobStreamId,      // every inner event inherits this stream at the receiver
  Inner    = jobFieldEvents,   // List<IMessage>, in producer-yielded order
};
composite.EnsureWithinCap();   // producer-side fail-fast before publishing
await dispatcher.PublishAsync(composite, ct);
```

The base carries the shared `[StreamId]`, the `List<IMessage> Inner` (typed concretely so each element
serializes with its polymorphic `$type` discriminator), an overridable `MaxInnerEventsAllowed`
(default 10,000), and `EnsureWithinCap()`. Inner events share the composite's stream — a producer that
needs per-inner streams must emit separate envelopes (no composite).

## Pre-fanout hook

A receptor can listen for the composite itself — `IReceptor<TComposite>` fires **before** fan-out,
inside the dispatch step, so it can validate the batch, stamp per-batch metadata, or emit a durable
`BatchReceivedEvent`. Its **inline** emissions are captured by an ambient `DispatchOutboxCollector`
(which diverts the would-be outbox write into an in-memory buffer) and folded into the **same**
`HandlerCommitRequest` as the fan-out children — so pre-fanout side-effects and children commit
**all-or-nothing**. A pre-fanout receptor that throws fails the composite inbox row → normal retry →
DLQ, exactly like any other dispatch failure.

Only **inline** receptors participate in the atomic commit; detached (`[FireAt(...Detached)]`)
receptors run fire-and-forget after the dispatch step and cannot be part of its transaction. The
children dispatch normally post-fanout — there is no composite awareness downstream.

## Fan-out control

Fan-out is zero-config by default, with a declarative knob on the composite and an imperative override
from a pre-fanout receptor.

**Declarative** (on the composite type / `CompositeEventBase`):

- `FanoutMode` — `Auto` (default, fans out `InnerEvents`) | `Manual` (a pre-fanout receptor drives it;
  nothing fans out without an explicit directive).
- `Atomicity` — `Independent` (default; a child that fails to serialize is dropped and the rest fan out
  — "one bad child doesn't sink the batch") | `Atomic` (any child failure dead-letters the whole
  composite — use when the inner events are one logical unit). A cap breach (`MaxInnerEventsAllowed`)
  always dead-letters the whole composite regardless of atomicity — it signals a runaway producer.

**Imperative** — a pre-fanout receptor calls `DispatchFanoutControl.Set(...)` (ambient, like the
collector) to impose a `FanoutDirective`, which takes precedence over `FanoutMode`:

- `Proceed` (default) — fan out the composite's own `InnerEvents`.
- `Skip` — suppress fan-out; the receptor handled the composite. The composite row is still deleted and
  any emitted events still commit.
- `ReplaceWith(children)` — fan out a receptor-supplied set instead (filter / transform / re-key before
  the children are created).

```csharp
public sealed class OrderBulkImportComposite : CompositeEventBase {
  public OrderBulkImportComposite() {
    Atomicity = FanoutAtomicity.Atomic;   // a job's field events are one unit
  }
}
```

## The no-rebroadcast invariant

> One composite on the wire; children are received-events confined to the
> inbox → event-store → local-processing path. Children never outbox.

Correct by construction — fan-out writes children to the **inbox/received** path, never `PublishAsync`;
the composite was already delivered to every subscriber of its topic. In Phase A the active defense is
**hop-based echo suppression**: children inherit the composite's `Hops` by reference, so the owned-echo
suppressor treats them as received-from-upstream and won't re-publish them. (Phase D adds an explicit
`EventFlags` guard at the outbox-enqueue boundary as defense-in-depth.)

## Code ↔ tests

| Concern | Code | Tests |
|---|---|---|
| Composite marker / authoring | `ICompositeEvent`, `CompositeEventBase` | `Messaging/CompositeEventBaseTests.cs` |
| Wire serialization (polymorphic, AOT) | `MessageJsonContextGenerator`, `JsonContextRegistry` | `JsonContextRegistryTests.cs` |
| Dispatch recognition (drop-gate) | `ReceptorRegistryQueryGenerator` | `ReceptorRegistryQueryGeneratorTests.cs` (`Generator_WithCompositeEvent_*`) |
| Dispatch-time fan-out | `CompositeInboxFanout`, `InboxDispatchWorker` | `Messaging/CompositeInboxFanoutTests.cs`, `Workers/InboxDispatchWorkerTests.cs` (`CompositeMessage_FansOut*`, `CompositeOverCap_DeadLetters*`) |
| Pre-fanout hook (atomic emit) | `DispatchOutboxCollector`, `InboxDispatchWorker._invokePreFanoutHookAsync`, `Dispatcher` outbox seam | `Messaging/DispatchOutboxCollectorTests.cs`, `Workers/InboxDispatchWorkerTests.cs` (`CompositeWithPreFanoutReceptor_*`) |
| Fan-out control | `FanoutMode`/`FanoutAtomicity`/`FanoutDirective`, `DispatchFanoutControl`, `CompositeInboxFanout`, `InboxDispatchWorker` | `Messaging/DispatchFanoutControlTests.cs`, `Messaging/CompositeInboxFanoutTests.cs` (atomicity + replacement), `Workers/InboxDispatchWorkerTests.cs` (`CompositeDirective_*`, `CompositeFanoutMode_Manual_*`) |
| No transport-edge expansion | `TransportConsumerWorker` | `Workers/TransportConsumerWorkerCompositeNoExpandTests.cs` |

## Upcoming (see the plan)

- **Phase D** — explicit `EventFlags.NoRebroadcast` guard enforced at the outbox-enqueue boundary.
