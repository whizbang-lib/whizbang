# Lifecycle Coordinator: WhenAll Pattern — Animation Spec

**Animation file:** `docs/diagrams/animations/09-lifecycle-coordinator-whenall.html`
**Steps:** 10
**Last verified against source:** 2026-04-05

---

## 1. Overview

**What this teaches:** How the `LifecycleCoordinator` tracks an event through parallel processing paths and guarantees that `PostLifecycle` fires exactly once, only after ALL expected paths complete. Shows the `ExpectCompletionsFrom` registration, parallel local and distributed execution, `SignalSegmentComplete` checks, and the `PostAllPerspectives` variant.

**Why it matters:** `PostLifecycle` is where final cleanup, audit completion, and notification hooks run. Developers need to understand why `PostLifecycle` sometimes fires "immediately" (single-path dispatch) vs. "after a delay" (WhenAll waiting for distributed path). Incorrect expectations can cause lifecycle receptors to fire before remote processing is done.

**Intended audience:** Advanced developers writing `PostLifecycle` or `PostAllPerspectives` receptors; anyone debugging why a final-stage receptor fires too early or not at all; framework contributors.

**Conceptual prerequisite:** Understanding `DispatchModes.Both` (local dispatch + outbox), that `PostLifecycle` is the terminal stage, and that perspectives are read model projections.

---

## 2. Visual Layout

Three-region vertical layout:

| Region | DOM IDs | Represents |
|--------|---------|------------|
| Parallel paths row | Left: `path-local` + nodes; Center: coord badges; Right: `path-dist` + nodes | Local path (left), coordinator state (center), distributed path (right) |
| Expectation tracker | `trk`, `ts-local`, `ts-dist` | WhenAll registration state (pending/done) |
| Final stage | `n-postlifecycle` | PostLifecycle node |

**Local path nodes** (`n-local-entry`, `n-local-receptor`, `n-local-perspectives`, `n-local-signal`): standard node glow states.

**Distributed path nodes** (`n-dist-entry`, `n-dist-outbox`, `n-dist-inbox`, `n-dist-signal`): standard node glow states.

**Coordinator badges** (`cb-tracking`, `cb-expect`, `cb-status`):
- Default: neutral background
- `.waiting`: `var(--phase-cascade-bg)` yellow background — active/incomplete
- `.ready`: `var(--phase-perspective-bg)` green background — all conditions met

**Tracker status spans** (`ts-local`, `ts-dist`):
- `.pending`: yellow background — not yet complete
- `.done`: green background — path completed

**Reset:** `resetAll()` — clears node states, resets coordinator badges to neutral with default text, resets tracker statuses to `.pending`.

---

## 3. Source References

| Symbol | Source File | What to Check |
|--------|-------------|---------------|
| `ILifecycleCoordinator.BeginTracking()` | `src/Whizbang.Core/Lifecycle/ILifecycleCoordinator.cs` | Signature: `BeginTracking(eventId, envelope, entryStage, source, streamId?, perspectiveType?)` — step 1 |
| `ILifecycleCoordinator.ExpectCompletionsFrom()` | `src/Whizbang.Core/Lifecycle/ILifecycleCoordinator.cs` | Signature: `ExpectCompletionsFrom(eventId, params PostLifecycleCompletionSource[])` — step 2 |
| `ILifecycleCoordinator.SignalSegmentCompleteAsync()` | `src/Whizbang.Core/Lifecycle/ILifecycleCoordinator.cs` | Signature: `SignalSegmentCompleteAsync(eventId, source, scopedProvider, ct)` — steps 5 and 8 |
| `ILifecycleCoordinator.AbandonTracking()` | `src/Whizbang.Core/Lifecycle/ILifecycleCoordinator.cs` | Called after `PostLifecycle` fires — step 9 |
| `ILifecycleCoordinator.ExpectPerspectiveCompletions()` | `src/Whizbang.Core/Lifecycle/ILifecycleCoordinator.cs` | WhenAll for perspectives — step 10 |
| `ILifecycleCoordinator.SignalPerspectiveComplete()` | `src/Whizbang.Core/Lifecycle/ILifecycleCoordinator.cs` | Per-perspective signal — step 10 |
| `PostLifecycleCompletionSource` enum | `src/Whizbang.Core/Lifecycle/ILifecycleCoordinator.cs` | Values: `Local`, `Distributed`, `Outbox` — steps 2, 5, 8 |
| `DispatchModes.Both` | `src/Whizbang.Core/Dispatch/DispatchMode.cs` | `Both = LocalDispatch | Outbox` — triggers two-path WhenAll in step 2 |
| `LifecycleStage.PostLifecycleDetached/Inline` | `src/Whizbang.Core/Messaging/LifecycleStage.cs` | Terminal stages — step 9 |
| `LifecycleStage.PostAllPerspectivesDetached/Inline` | `src/Whizbang.Core/Messaging/LifecycleStage.cs` | Perspective WhenAll terminal stage — step 10 |

---

## 4. Steps Specification

### `begin` — BeginTracking (2800ms)

**Narration:** Event enters via Dispatcher. `LifecycleCoordinator.BeginTracking(eventId, envelope, ImmediateDetached, Local)` creates a tracking handle. The coordinator tracks this event through all lifecycle stages.

**DOM on enter:** `n-local-entry`, `n-dist-entry` get `.glow`; `cb-tracking` gets `.waiting`, text = "BeginTracking"
**DOM on exit:** `resetAll()`

**Source symbols:** `ILifecycleCoordinator.BeginTracking()`, `LifecycleStage.ImmediateDetached`, `MessageSource.Local`

---

### `expect` — ExpectCompletionsFrom (3000ms)

**Narration:** `ExpectCompletionsFrom(eventId, Local, Distributed)` — because `DispatchModes.Both` was used, the coordinator registers TWO required completion sources. PostLifecycle will only fire when both signal complete.

**DOM on enter:** `cb-expect` `.waiting`, text = "Expect: Local + Distributed"; tracker `trk` gets `.active`
**DOM on exit:** `resetAll()`

**Source symbols:** `ILifecycleCoordinator.ExpectCompletionsFrom()`, `PostLifecycleCompletionSource.Local`, `PostLifecycleCompletionSource.Distributed`, `DispatchModes.Both`

---

### `local-receptors` — Local Path: Receptors (2500ms)

**Narration:** Local path begins: `ImmediateDetached` stage fires local receptors. `HandleAsync(event)` processes the event in-process. Meanwhile, the distributed path runs in parallel.

**DOM on enter:** `n-local-receptor` gets `.glow`; tracker active; `cb-expect` `.waiting`
**DOM on exit:** `resetAll()`

**Source symbols:** `LifecycleStage.ImmediateDetached`, `IReceptor<TMessage>.HandleAsync()`

---

### `local-perspectives` — Local Path: Perspectives (2500ms)

**Narration:** Local perspective workers process the event: `PerspectiveRunner.RunAsync()` applies the event to read models. Checkpoint updated.

**DOM on enter:** `n-local-perspectives` `.glow`; `n-local-receptor` `.highlight-success`; tracker active; `cb-expect` `.waiting`
**DOM on exit:** `resetAll()`

**Source symbols:** `IPerspectiveRunner.RunAsync()`

---

### `local-signal` — Local Path Signals Complete (3000ms)

**Narration:** `SignalSegmentCompleteAsync(eventId, PostLifecycleCompletionSource.Local)` — the local path is done. Coordinator checks: are all expected sources complete? Distributed is still pending — NOT YET. Wait.

**DOM on enter:** `n-local-signal` `.glow`; `n-local-receptor`, `n-local-perspectives` `.highlight-success`; `ts-local` → `.done`; `cb-status` `.waiting`, text = "1/2 — waiting"
**DOM on exit:** `resetAll()`

**Source symbols:** `ILifecycleCoordinator.SignalSegmentCompleteAsync()`, `PostLifecycleCompletionSource.Local`

---

### `dist-outbox` — Distributed Path: Outbox (2500ms)

**Narration:** Meanwhile, the distributed path: `OutboxWorker` claims the message, publishes to transport (Kafka/RabbitMQ/Service Bus).

**DOM on enter:** `n-dist-outbox` `.glow`; tracker/status badges show 1/2 waiting
**DOM on exit:** `resetAll()`

**Source symbols:** `OutboxRecord`, outbox worker

---

### `dist-inbox` — Distributed Path: Inbox (2500ms)

**Narration:** `TransportConsumer` receives the message in another service. `InboxRecord` deduplication check passes. Receptors invoked.

**DOM on enter:** `n-dist-inbox` `.glow`; `n-dist-outbox` `.highlight-success`; tracker/status at 1/2 waiting
**DOM on exit:** `resetAll()`

**Source symbols:** `InboxRecord`, `TransportConsumer`

---

### `dist-signal` — Distributed Path Signals Complete (3000ms)

**Narration:** `SignalSegmentCompleteAsync(eventId, PostLifecycleCompletionSource.Distributed)` — the distributed path is done. Coordinator checks: Local done, Distributed done. ALL COMPLETE. Fire PostLifecycle!

**DOM on enter:** `n-dist-signal` `.glow`; `n-dist-outbox`, `n-dist-inbox` `.highlight-success`; `ts-dist` → `.done`; `cb-status` `.ready`, text = "2/2 — ALL COMPLETE"
**DOM on exit:** `resetAll()`

**Source symbols:** `ILifecycleCoordinator.SignalSegmentCompleteAsync()`, `PostLifecycleCompletionSource.Distributed`

---

### `postlifecycle` — PostLifecycle Fires (3000ms)

**Narration:** `PostLifecycleDetached` and `PostLifecycleInline` stages fire EXACTLY ONCE for this event. Receptors registered at these stages execute (cleanup, audit, notifications). `AbandonTracking(eventId)` removes the tracking entry.

**DOM on enter:** tracker statuses both `.done`; `cb-status` `.ready`; `n-postlifecycle` `.glow` + `.highlight-success`
**DOM on exit:** `resetAll()`

**Source symbols:** `LifecycleStage.PostLifecycleDetached`, `LifecycleStage.PostLifecycleInline`, `ILifecycleCoordinator.AbandonTracking()`

---

### `perspective-whenall` — Perspective WhenAll Variant (3500ms)

**Narration:** Bonus pattern: `ExpectPerspectiveCompletions(eventId, ["OrderSummary", "InventoryView"])` — PostAllPerspectives only fires when EVERY perspective signals via `SignalPerspectiveComplete(eventId, perspectiveName)`. Same WhenAll pattern, applied to read model projections.

**DOM on enter:** tracker shows "OrderSummary" (done) and "InventoryView" (done); `cb-status` `.ready`, text = "PostAllPerspectives ✓"
**DOM on exit:** `resetAll()`

**Source symbols:** `ILifecycleCoordinator.ExpectPerspectiveCompletions()`, `ILifecycleCoordinator.SignalPerspectiveComplete()`, `LifecycleStage.PostAllPerspectivesDetached/Inline`

---

## 5. Maintenance Guide

**`ILifecycleCoordinator` method signature changes** (`src/Whizbang.Core/Lifecycle/ILifecycleCoordinator.cs`):
- `BeginTracking()` parameters change → update step 1
- `ExpectCompletionsFrom()` API change → update step 2
- `SignalSegmentCompleteAsync()` signature change → update steps 5 and 8
- `AbandonTracking()` removed or renamed → update step 9
- `ExpectPerspectiveCompletions()` / `SignalPerspectiveComplete()` change → update step 10

**`PostLifecycleCompletionSource` enum values change** (`src/Whizbang.Core/Lifecycle/ILifecycleCoordinator.cs`):
- If `Local`, `Distributed`, or `Outbox` renamed → update steps 2, 5, 8

**New completion source added** (e.g., `EventStore`):
- If `ExpectCompletionsFrom` needs a third source for new dispatch modes → steps 2, 5, 8 need updating; the animation's 2-path layout would also need a third parallel column

**`DispatchModes.Both` semantics change** (`src/Whizbang.Core/Dispatch/DispatchMode.cs`):
- If `Both` no longer triggers a two-path WhenAll → step 2 narration breaks

**PostLifecycle stage names change** (`src/Whizbang.Core/Messaging/LifecycleStage.cs`):
- If `PostLifecycleDetached/Inline` or `PostAllPerspectivesDetached/Inline` renamed → steps 9 and 10

**What does NOT require an update:**
- Changes to `OutboxRecord`, `InboxRecord` fields
- Changes to `PolicyContext`, tag hooks, source generators
- Changes to `compute_partition()` or heartbeat SQL
- Example perspective names (`OrderSummary`, `InventoryView`) — illustrative
