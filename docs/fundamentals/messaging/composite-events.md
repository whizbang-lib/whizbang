# Composite Events

**Status**: Implemented — dispatch-time fan-out, pre-fanout hook, fan-out control, no-rebroadcast guard, publish-time local fan-out for owned composites (v0.769+)
**Namespace**: `Whizbang.Core.Messaging`
**Design / plan**: [`plans/composite-events-turnkey.md`](../../../plans/composite-events-turnkey.md)

## Overview

A **composite event** bundles many inner events into one transport hop. A bulk operation that
produces N domain events (e.g. "350 jobs imported" emitting hundreds of field events) sends **one**
wire message instead of N — one outbox row, one publish, one receive — then **fans out** into the N
inner events at the receiver. The composite itself is **never written to the event store**; only the
expanded inner events are persisted, so replay reads the inner events back as if no composite ever
existed.

A composite travels over transport **exactly like an ordinary `IEvent`** (one outbox row, one
publish) — the single difference is that it is **not** event-stored. It **fans out at every destination
service**, and the children, wherever they fan out, are **local-published** (event store + receptors +
perspectives) and **never rebroadcast**. Two places fan out:

- **The publishing service, at publish** ([step 1.1](#publish-time-local-fan-out)) — when a service
  publishes a composite in its own domain, it expands it and local-publishes each inner event right
  there, materializing its own children immediately. The composite *also* goes over transport
  (step 1.2); its own transported copy loops back and is **echo-discarded** (it already fanned out), so
  there is no double fan-out.
- **Every other subscribing service, on receive** ([step 2.1](#dispatch-lifecycle)) — the composite
  arrives over transport and fans out at the receive-side dispatch seam (`InboxDispatchWorker`), the
  same way.

Both paths reuse the ordinary local-publish / receive machinery — there is no bespoke fan-out
transport.

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
the composite was already delivered to every subscriber of its topic. Defended in depth:

1. **Hop-based echo suppression (primary).** Children inherit the composite's `Hops` by reference, so
   the owned-echo suppressor treats them as received-from-upstream and won't re-publish them. This
   covers the persisted-and-re-claimed child too.
2. **`EventFlags.NoRebroadcast` guard (explicit).** Fan-out stamps every child — both its persisted
   `InboxMessage.Flags` and its in-memory envelope `Flags` — with `NoRebroadcast`. The outbox-enqueue
   boundary (`Dispatcher.PublishToOutboxAsync` / `PublishToOutboxDynamicAsync`, via
   `NoRebroadcastGuard.ShouldSuppress`) hard-drops any publish whose source envelope carries the flag.
   Even a receptor that explicitly re-publishes a fan-out child it is processing is stopped at the gate.

## Publish-time local fan-out

A composite fans out at **every** destination that receives it — and the publishing service is itself a
destination. Rather than make the publishing service wait to receive its own transported copy back, it
fans the composite out **locally, at publish** (`Dispatcher._fanOutCompositeLocallyAtPublishAsync`):

```
JobService: PublishAsync(OrderBulkImportComposite)   // owned domain
  ├─ 1.1  expand → local-publish each inner event
  │         (DispatchModes.Local = local receptors + event store, NO transport)
  │         → children land in the event store + fire receptors/perspectives, stamped NoRebroadcast
  └─ 1.2  PublishToOutboxAsync(composite) → transport (ONE wire row; composite is NOT event-stored)
            → other subscribing services receive it and fan out the same way (step 2.1)
            → JobService's own transported copy loops back → echo-discarded (already fanned out at 1.1)
```

- **1.1** is gated on `_isOwnedNamespace` — a service only fans out locally for composites in a domain it
  owns. Inner events are local-published through the ordinary `CascadeMessageAsync(DispatchModes.Local)`
  path, so they reuse the exact event-store + receptor + perspective machinery a normal `PublishAsync`
  uses, minus the per-event outbox row. `FanoutAtomicity` governs child failure (Atomic propagates,
  Independent logs and continues).
- **1.2** is the ordinary outbox publish — the composite travels like any owned event.
- **No double fan-out**: because the publishing service already fanned out at 1.1, its own loopback copy
  is **echo-discarded** by the normal owned-echo suppression (no special case needed — a composite in
  its own namespace is a self-echo just like an owned event).
- **Other services (2.1)**: the composite is *not* an echo for them, so it survives to the dispatch seam
  and the existing `InboxDispatchWorker` fan-out local-publishes the children there.

Net effect: the inner events land in the event store and trigger their receptor cascades **exactly as
if they had been published individually**, at every service that consumes the domain — the composite is
purely a transport/packaging optimization, not a change in downstream semantics.

## Treatment flags (extending `EventFlags`)

The no-rebroadcast guard generalizes into a small convention. `EventFlags` (the per-event bitmask on the
envelope / inbox / outbox / event-store rows) holds two kinds of bit:

- **Category** — what the event *is*: `Composite`, `Collective`.
- **Treatment** — tell the framework to *bypass or alter* some functionality for this event:
  `NoRebroadcast` today; future candidates like `SuppressNotifications` or `SkipPerspectives`.

Treatment flags are **not composite-specific** — *any* event can carry one. An event opts in by a
**marker interface** (the builders already derive `Composite`/`Collective` via `payload is IXxxEvent`,
so a new treatment slots into the same `|`-chain) for a type-level treatment, or by setting the
**per-instance** `IMessageEnvelope.Flags` carrier at publish. Composites are just one producer: fan-out
**propagates** the composite's treatment flags to every child (today the child stamp carries
`NoRebroadcast`).

The rule that keeps this honest: **a treatment flag ships with the gate that reads it** — a bit nothing
branches on is dead code. The mechanism (the bitmask, the marker-interface derivation, the per-instance
carrier, composite→child propagation) is in place; each new bit is a small, localized addition shipped
with its consumer.

## Code ↔ tests

| Concern | Code | Tests |
|---|---|---|
| Composite marker / authoring | `ICompositeEvent`, `CompositeEventBase` | `Messaging/CompositeEventBaseTests.cs` |
| Wire serialization (polymorphic, AOT) | `MessageJsonContextGenerator`, `JsonContextRegistry` | `JsonContextRegistryTests.cs` |
| Dispatch recognition (drop-gate) | `ReceptorRegistryQueryGenerator` | `ReceptorRegistryQueryGeneratorTests.cs` (`Generator_WithCompositeEvent_*`) |
| Publish-time local fan-out (1.1) | `Dispatcher._fanOutCompositeLocallyAtPublishAsync`, `Dispatcher.PublishAsync` | `Dispatcher/DispatcherCompositePublishFanoutTests.cs` |
| Dispatch-time fan-out | `CompositeInboxFanout`, `InboxDispatchWorker` | `Messaging/CompositeInboxFanoutTests.cs`, `Workers/InboxDispatchWorkerTests.cs` (`CompositeMessage_FansOut*`, `CompositeOverCap_DeadLetters*`) |
| Pre-fanout hook (atomic emit) | `DispatchOutboxCollector`, `InboxDispatchWorker._invokePreFanoutHookAsync`, `Dispatcher` outbox seam | `Messaging/DispatchOutboxCollectorTests.cs`, `Workers/InboxDispatchWorkerTests.cs` (`CompositeWithPreFanoutReceptor_*`) |
| Fan-out control | `FanoutMode`/`FanoutAtomicity`/`FanoutDirective`, `DispatchFanoutControl`, `CompositeInboxFanout`, `InboxDispatchWorker` | `Messaging/DispatchFanoutControlTests.cs`, `Messaging/CompositeInboxFanoutTests.cs` (atomicity + replacement), `Workers/InboxDispatchWorkerTests.cs` (`CompositeDirective_*`, `CompositeFanoutMode_Manual_*`) |
| No-rebroadcast guard | `EventFlags.NoRebroadcast`, `IMessageEnvelope.Flags`, `NoRebroadcastGuard`, `CompositeInboxFanout` stamp, `Dispatcher` outbox seam | `Messaging/NoRebroadcastGuardTests.cs`, `Messaging/CompositeInboxFanoutTests.cs` (`TryExpand_ChildrenCarryNoRebroadcastFlag`), `Dispatcher/DispatcherNoRebroadcastGuardTests.cs` |
| Treatment-flag convention (`EventFlags`) | `EventFlags` (category vs treatment), `IMessageEnvelope.Flags` carrier | `Messaging/EventFlagsTests.cs` (bit-position locks incl. `NoRebroadcast`), `Messaging/EventFlagsTransportTests.cs` (outbox/inbox + envelope-carrier shape) |
| No transport-edge expansion | `TransportConsumerWorker` | `Workers/TransportConsumerWorkerCompositeNoExpandTests.cs` |
