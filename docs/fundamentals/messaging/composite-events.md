# Composite Events

**Status**: Implemented — dispatch-time fan-out, pre-fanout hook, fan-out control, no-rebroadcast guard, owned-composite echo-gate exemption (v0.768+)
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
publish) — the single difference is that it is **not** event-stored. It then **fans out at every
destination service that receives it, including the publishing service itself** (an owned event loops
back to its own service). Fan-out runs on the local inbox/dispatch path, so the children **are**
event-stored locally but **never rebroadcast**. There is **no separate producer-side code path**: a
service publishing a composite in its own domain consumes its own loopback copy and fans it out
through the exact same receive-side seam any other subscriber uses. The one accommodation this
requires is the [echo-gate exemption](#owned-composites-fan-out-at-the-publishing-service) below.

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
  `ICompositeEvent` type and lists it in **both** `AnyConsumerTypes` (so the receive-boundary drop-gate
  keeps the composite alive long enough to reach the dispatch seam) **and** a dedicated `CompositeTypes`
  set surfaced as `WhizbangReceptorRegistryQuery.IsComposite(typeName)` /
  `IReceptorRegistryQuery.IsComposite` (so the echo-gate can recognize an owned composite — see
  [below](#owned-composites-fan-out-at-the-publishing-service)). The abstract
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

## Owned composites: fan-out at the publishing service

A composite fans out at **every** destination that receives it — and for an owned-domain composite,
the publishing service is itself a destination (the message loops back to its own inbox). This is what
makes "publish a composite in your own domain" work with **no** producer-side special case: the
service consumes its own loopback copy and fans it out through the ordinary receive-side seam.

The one obstacle is **echo suppression**. The receive boundary
(`TransportConsumerWorker._shouldDiscardOwnedEcho`) normally **discards** an owned event arriving from
transport — it's a redundant echo, because an owned event is already event-stored at publish time. But
a composite is **not** an event and is **not** event-stored at publish: discarding its loopback would
drop it before it ever fanned out, so the publishing service would persist **none** of the inner
events.

So composites are **exempt** from echo-discard. The gate consults
`IReceptorRegistryQuery.IsComposite(innerType)` (backed by the generated `CompositeTypes` set) and lets
any composite through to the dispatch seam, where the existing `InboxDispatchWorker` fan-out takes
over. The children it produces are stamped [`NoRebroadcast`](#the-no-rebroadcast-invariant), so the
loopback expands locally without any child going back onto the wire.

```
JobService: PublishAsync(OrderBulkImportComposite)   // owned domain
  → outbox → transport (ONE wire row; composite is NOT event-stored)
  → loops back to JobService's own inbox
  → echo-gate: IsComposite == true ⟹ NOT discarded (an ordinary owned event WOULD be)
  → InboxDispatchWorker fan-out (the same seam every subscriber uses)
  → children: event-stored + perspectives + receptors, stamped NoRebroadcast (never re-transported)
```

Net effect: the inner events land in the event store and trigger their receptor cascades **exactly as
if they had been published individually** — the composite is purely a transport/packaging optimization,
not a change in downstream semantics.

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
| Composite recognition (`IsComposite`) | `ReceptorRegistryQueryGenerator` (`CompositeTypes` emission), `WhizbangReceptorRegistryQuery.IsComposite`, `IReceptorRegistryQuery.IsComposite`, `WhizbangReceptorRegistryQueryAdapter` | `ReceptorRegistryQueryGeneratorTests.cs` (`Generator_WithCompositeEvent_RegistersInCompositeTypes`), `Generated/WhizbangReceptorRegistryQueryAggregationTests.cs` (`*_IsComposite_*`), `Messaging/WhizbangReceptorRegistryQueryAdapterTests.cs` (`IsComposite_*`) |
| Owned-composite echo-gate exemption | `TransportConsumerWorker._shouldDiscardOwnedEcho` | `Workers/TransportConsumerWorkerOwnedCompositeEchoTests.cs` |
| Dispatch-time fan-out | `CompositeInboxFanout`, `InboxDispatchWorker` | `Messaging/CompositeInboxFanoutTests.cs`, `Workers/InboxDispatchWorkerTests.cs` (`CompositeMessage_FansOut*`, `CompositeOverCap_DeadLetters*`) |
| Pre-fanout hook (atomic emit) | `DispatchOutboxCollector`, `InboxDispatchWorker._invokePreFanoutHookAsync`, `Dispatcher` outbox seam | `Messaging/DispatchOutboxCollectorTests.cs`, `Workers/InboxDispatchWorkerTests.cs` (`CompositeWithPreFanoutReceptor_*`) |
| Fan-out control | `FanoutMode`/`FanoutAtomicity`/`FanoutDirective`, `DispatchFanoutControl`, `CompositeInboxFanout`, `InboxDispatchWorker` | `Messaging/DispatchFanoutControlTests.cs`, `Messaging/CompositeInboxFanoutTests.cs` (atomicity + replacement), `Workers/InboxDispatchWorkerTests.cs` (`CompositeDirective_*`, `CompositeFanoutMode_Manual_*`) |
| No-rebroadcast guard | `EventFlags.NoRebroadcast`, `IMessageEnvelope.Flags`, `NoRebroadcastGuard`, `CompositeInboxFanout` stamp, `Dispatcher` outbox seam | `Messaging/NoRebroadcastGuardTests.cs`, `Messaging/CompositeInboxFanoutTests.cs` (`TryExpand_ChildrenCarryNoRebroadcastFlag`), `Dispatcher/DispatcherNoRebroadcastGuardTests.cs` |
| Treatment-flag convention (`EventFlags`) | `EventFlags` (category vs treatment), `IMessageEnvelope.Flags` carrier | `Messaging/EventFlagsTests.cs` (bit-position locks incl. `NoRebroadcast`), `Messaging/EventFlagsTransportTests.cs` (outbox/inbox + envelope-carrier shape) |
| No transport-edge expansion | `TransportConsumerWorker` | `Workers/TransportConsumerWorkerCompositeNoExpandTests.cs` |
