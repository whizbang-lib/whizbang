# End-to-End Event Processing Flow — Animation Spec

**Animation file:** `docs/diagrams/animations/07-end-to-end-flow.html`
**Steps:** 18
**Last verified against source:** 2026-04-05

---

## 1. Overview

**What this teaches:** The complete journey of a command through the Whizbang system — from API call to updated read model. Shows all 6 phases: command dispatch, event cascading, outbox processing, cross-service delivery, perspective projection, and query response.

**Why it matters:** This is the broadest possible view of the system. New developers need this mental model before diving into any subsystem. It establishes the vocabulary (command, event, receptor, perspective, outbox, inbox) in context.

**Intended audience:** All developers and architects; anyone new to Whizbang; technical demos. Also useful as a debugging reference to identify which phase an issue occurs in.

**Conceptual prerequisite:** None — this is the entry-point animation.

---

## 2. Visual Layout

Six-phase horizontal grid (`grid-template-columns: repeat(6, 1fr)`):

| Phase | Header ID | Column ID | Nodes |
|-------|-----------|-----------|-------|
| 1. Command Dispatch | `ph1` | `col1` | `n-api`, `n-dispatcher`, `n-registry`, `n-cmd-receptor` |
| 2. Event Cascading | `ph2` | `col2` | `n-cascader`, `n-eventstore`, `n-outbox-table` |
| 3. Outbox Processing | `ph3` | `col3` | `n-outbox-worker`, `n-transport` |
| 4. Cross-Service Delivery | `ph4` | `col4` | `n-consumer`, `n-inbox-table`, `n-evt-receptors` |
| 5. Perspective Projection | `ph5` | `col5` | `n-persp-runner`, `n-persp-store` |
| 6. Query | `ph6` | `col6` | `n-query-receptor`, `n-api2` |

**Phase visibility**: `setPhase(num)` sets active phase (header + column `.active`), all lower phases `.completed` (dimmed 35%), others remain at base opacity.

**Packets**: `pkt-cmd`, `pkt-evt`, `pkt-evt2`, `pkt-receipt`, `pkt-transport`, `pkt-inbox`, `pkt-query`, `pkt-result`. Shown with `showPacketAt()`, animated with `movePacket()`.

**Node state classes**: `.glow` (cyan border/shadow), `.active` (active processing), `.highlight-success` (green).

**Reset:** wraps individual step `onExit` functions; each step's `onExit` also calls `hideAllPackets()`, `clearPhases()`, removes node state classes.

**Note:** The file has a post-processing loop after the `steps` array definition that wraps all `onExit` callbacks to always call `hideAllPackets()`, `clearPhases()`, and reset all node classes and sublabels.

---

## 3. Source References

| Symbol | Source File | What to Check |
|--------|-------------|---------------|
| `IDispatcher.SendAsync()` | `src/Whizbang.Core/IDispatcher.cs` | Step 1 |
| `MessageEnvelope<T>` (v2) | `src/Whizbang.Core/Observability/MessageEnvelope.cs` | Step 2 — `MessageId` (UUIDv7), `DispatchContext` (Mode, Source), `CallerInfo`, `ScopeContext` |
| `MessageDispatchContext` | `src/Whizbang.Core/Observability/MessageDispatchContext.cs` | Step 2 — `Mode=Local`, `Source=Local` for direct dispatch |
| `IReceptorRegistry.GetReceptorsFor()` | `src/Whizbang.Core/Messaging/IReceptorRegistry.cs` | Step 3 — `GetReceptorsFor(type, stage)` |
| `LifecycleStage.ImmediateDetached` | `src/Whizbang.Core/Messaging/LifecycleStage.cs` | Steps 3 and 15 |
| `IReceptor<TMessage, TResponse>.HandleAsync()` | `src/Whizbang.Core/IReceptor.cs` | Step 4 |
| `IEventCascader.CascadeFromResultAsync()` | `src/Whizbang.Core/Messaging/IEventCascader.cs` | Step 5 |
| `DispatchModes` | `src/Whizbang.Core/Dispatch/DispatchMode.cs` | Step 6 — Routed wrapper, `[DefaultRouting]` attribute, default Outbox |
| `DefaultRoutingAttribute` | `src/Whizbang.Core/Dispatch/DefaultRoutingAttribute.cs` | Step 6 — attribute for overriding default dispatch mode |
| `Routed<T>` / `Route.*` | `src/Whizbang.Core/Dispatch/Routed.cs`, `Route.cs` | Step 6 — explicit routing wrapper |
| `IEventStore.AppendAsync()` | `src/Whizbang.Core/Messaging/IEventStore.cs` | Step 7 |
| `OutboxRecord` | `src/Whizbang.Core/Messaging/OutboxRecord.cs` | Step 8 — `MessageId`, `MessageType`, `Destination`, `StatusFlags` |
| `IDeliveryReceipt` | `src/Whizbang.Core/` | Step 9 |
| `OutboxRecord.InstanceId`, `OutboxRecord.LeaseExpiry` | `src/Whizbang.Core/Messaging/OutboxRecord.cs` | Step 10 |
| `LifecycleStage.PreOutboxDetached/Inline` | `src/Whizbang.Core/Messaging/LifecycleStage.cs` | Step 11 |
| `LifecycleStage.PostOutboxDetached/Inline` | `src/Whizbang.Core/Messaging/LifecycleStage.cs` | Step 13 |
| `TransportConsumerWorker` self-echo | `src/Whizbang.Core/Workers/TransportConsumerWorker.cs` | Step 14 — owned-namespace message discard |
| `InboxRecord` | `src/Whizbang.Core/Messaging/InboxRecord.cs` | Step 15 |
| `IPerspectiveRunner.RunAsync()` | `src/Whizbang.Core/Perspectives/IPerspectiveRunner.cs` | Step 16 |
| `IEventStore.ReadAsync()` | `src/Whizbang.Core/Messaging/IEventStore.cs` | Step 16 |
| `IPerspectiveStore.UpsertAsync()` | `src/Whizbang.Core/Perspectives/IPerspectiveStore.cs` | Step 16 |
| `LifecycleStage.PostPerspectiveDetached/Inline` | `src/Whizbang.Core/Messaging/LifecycleStage.cs` | Step 17 |
| `LifecycleStage.PostAllPerspectivesDetached/Inline` | `src/Whizbang.Core/Messaging/LifecycleStage.cs` | Step 17 |
| `LifecycleStage.PostLifecycleDetached/Inline` | `src/Whizbang.Core/Messaging/LifecycleStage.cs` | Step 17 |
| `IDispatcher.LocalInvokeAsync()` | `src/Whizbang.Core/IDispatcher.cs` | Step 18 |
| `IPerspectiveStore.GetByStreamIdAsync()` | `src/Whizbang.Core/Perspectives/IPerspectiveStore.cs` | Step 18 |

---

## 4. Steps Specification

### `cmd-send` — Send Command (2500ms)

**Narration:** API calls `SendAsync(PlaceOrderCommand)`. The command enters the Dispatcher.

**DOM on enter:** `setPhase(1)`; `n-api` `.glow`; `pkt-cmd` shown at `n-api`; if `isTarget`: animates to `n-dispatcher`
**DOM on exit:** reset

**Source symbols:** `IDispatcher.SendAsync()`

---

### `envelope` — Create Envelope (2500ms)

**Narration:** Dispatcher creates `MessageEnvelope<PlaceOrderCommand>` (v2) with a UUIDv7 `MessageId`, `DispatchContext` (Mode=Local, Source=Local), `CallerInfo`, and `ScopeContext`.

**DOM on enter:** `setPhase(1)`; `n-dispatcher` `.glow`; sublabel = "creating envelope..."
**DOM on exit:** reset (sublabel restored)

**Source symbols:** `MessageEnvelope<T>`, `MessageDispatchContext`, `MessageId` (UUIDv7)

---

### `registry-lookup` — Registry Lookup (2000ms)

**Narration:** Dispatcher calls `GetReceptorsFor(PlaceOrderCommand, ImmediateDetached)` on the source-generated `ReceptorRegistry`.

**DOM on enter:** `setPhase(1)`; `n-registry` `.glow`; `n-dispatcher` `.active`
**DOM on exit:** reset

**Source symbols:** `IReceptorRegistry.GetReceptorsFor()`, `LifecycleStage.ImmediateDetached`

---

### `cmd-handle` — Handle Command (2500ms)

**Narration:** `OrderCommandReceptor.HandleAsync(command)` — validates business rules and applies domain logic.

**DOM on enter:** `setPhase(1)`; `n-cmd-receptor` `.glow`; `pkt-cmd` shown at receptor
**DOM on exit:** reset

**Source symbols:** `IReceptor<TMessage, TResponse>.HandleAsync()`

---

### `event-emit` — Emit Event (2500ms)

**Narration:** Receptor returns `OrderPlacedEvent`. The event cascades to the `IEventCascader`.

**DOM on enter:** `setPhase(2)`; `pkt-evt` shown; if `isTarget`: animates to `n-cascader`
**DOM on exit:** reset

**Source symbols:** `IEventCascader.CascadeFromResultAsync()`

---

### `resolve-mode` — Resolve DispatchMode (2500ms)

**Narration:** `EventCascader` resolves routing: checks `Routed<T>` wrapper, then `[DefaultRouting]` attribute, then defaults to `DispatchModes.Outbox`.

**DOM on enter:** `setPhase(2)`; `n-cascader` `.glow`
**DOM on exit:** reset

**Source symbols:** `DispatchModes`, `DefaultRoutingAttribute`, `Routed<T>`

---

### `es-append` — Event Store Append (2500ms)

**Narration:** `AppendAsync(streamId, envelope)` — append-only write to event stream order-123, sequence N+1.

**DOM on enter:** `setPhase(2)`; `n-eventstore` `.glow`; `pkt-evt` shown; sublabel = "stream: order-123, seq: N+1"
**DOM on exit:** reset (sublabel restored)

**Source symbols:** `IEventStore.AppendAsync()`

---

### `outbox-write` — Outbox Write (2500ms)

**Narration:** `OutboxRecord` written transactionally alongside event store append. Fields: `MessageId`, `MessageType`, `Destination`, `StatusFlags`.

**DOM on enter:** `setPhase(2)`; `n-outbox-table` `.glow`; sublabel = "StatusFlags: Stored"
**DOM on exit:** reset

**Source symbols:** `OutboxRecord` — `MessageId`, `MessageType`, `Destination`, `StatusFlags`

---

### `receipt` — Delivery Receipt (2000ms)

**Narration:** `IDeliveryReceipt` returned to the API caller. Confirms dispatch, not processing.

**DOM on enter:** `setPhase(1)`; `pkt-receipt` shown; if `isTarget`: animates to `n-api`; `n-api` `.glow`
**DOM on exit:** reset

**Source symbols:** `IDeliveryReceipt`

---

### `worker-claim` — Worker Claims (2500ms)

**Narration:** `OutboxWorker` claims unpublished records with lease-based, partition-aware polling. Sets `InstanceId` and `LeaseExpiry`.

**DOM on enter:** `setPhase(3)`; `n-outbox-worker` `.glow`; `n-outbox-table` `.active`
**DOM on exit:** reset

**Source symbols:** `OutboxRecord.InstanceId`, `OutboxRecord.LeaseExpiry`; `claim_orphaned_outbox()`

---

### `pre-outbox` — PreOutbox Stages (1800ms)

**Narration:** `PreOutboxDetached` and `PreOutboxInline` lifecycle stages fire before transport publish.

**DOM on enter:** `setPhase(3)`; `n-outbox-worker` `.glow`; sublabel = "PreOutbox [Detached/Inline]"
**DOM on exit:** reset

**Source symbols:** `LifecycleStage.PreOutboxDetached`, `LifecycleStage.PreOutboxInline`

---

### `transport-pub` — Publish to Transport (2500ms)

**Narration:** Message published to transport (Kafka / RabbitMQ / Service Bus / EventStore).

**DOM on enter:** `setPhase(3)`; `n-transport` `.glow`; `pkt-transport` shown; if `isTarget`: animates from worker to transport
**DOM on exit:** reset

**Source symbols:** `IMessagePublishStrategy`

---

### `post-outbox` — PostOutbox + Mark (2000ms)

**Narration:** `PostOutboxDetached/Inline` stages fire. `OutboxRecord.PublishedAt` set, `StatusFlags` updated.

**DOM on enter:** `setPhase(3)`; `n-outbox-table` sublabel = "Published ✓"; `.highlight-success`
**DOM on exit:** reset

**Source symbols:** `LifecycleStage.PostOutboxDetached`, `LifecycleStage.PostOutboxInline`, `OutboxRecord.PublishedAt`

---

### `consumer-recv` — Consumer Receives (2500ms)

**Narration:** `TransportConsumer` receives message. Checks `InboxRecord` deduplication by `MessageId + HandlerName`. Self-echo check: owned-namespace events are discarded to prevent double-firing.

**DOM on enter:** `setPhase(4)`; `n-consumer` `.glow`; `pkt-inbox` shown; if `isTarget`: animates from transport; `n-inbox-table` `.active`
**DOM on exit:** reset

**Source symbols:** `InboxRecord` dedup key `(MessageId, HandlerName)`; `TransportConsumerWorker` self-echo discard

---

### `inbox-invoke` — Inbox Write + Invoke (2500ms)

**Narration:** `InboxRecord` written. Receptors invoked: `InventoryReceptor`, `NotificationReceptor`.

**DOM on enter:** `setPhase(4)`; `n-inbox-table` `.glow`; `n-evt-receptors` `.glow`; `n-inbox-table` sublabel = "ReceivedAt: now"
**DOM on exit:** reset

**Source symbols:** `InboxRecord` — `ReceivedAt`; `IReceptor<TMessage>.HandleAsync()`

---

### `perspective` — Perspective Projection (3000ms)

**Narration:** `PerspectiveRunner.RunAsync()` reads event stream via `ReadAsync(streamId, lastCheckpoint)`. Applies each event via pure `Apply(event)` function. Upserts read model to `PerspectiveStore`.

**DOM on enter:** `setPhase(5)`; `n-persp-runner` `.glow`; `n-eventstore` `.active`; after 1200ms: `n-persp-store` `.glow`; sublabel = "OrderSummary updated"
**DOM on exit:** reset

**Source symbols:** `IPerspectiveRunner.RunAsync()`, `IEventStore.ReadAsync()`, `Apply(event)`, `IPerspectiveStore.UpsertAsync()`

---

### `lifecycle-final` — Final Lifecycle (2000ms)

**Narration:** `PostPerspectiveDetached`, `PostAllPerspectivesDetached` (WhenAll), and `PostLifecycleDetached` stages fire. Event processing complete.

**DOM on enter:** `setPhase(5)`; `n-persp-runner` sublabel = "PostLifecycle ✓"; `.highlight-success`
**DOM on exit:** reset

**Source symbols:** `LifecycleStage.PostPerspectiveDetached/Inline`, `LifecycleStage.PostAllPerspectivesDetached/Inline`, `LifecycleStage.PostLifecycleDetached/Inline`

---

### `query` — Query Path (3000ms)

**Narration:** API sends `GetOrderQuery` via `LocalInvokeAsync`. `QueryReceptor` reads from `PerspectiveStore` and returns `OrderSummaryResult`.

**DOM on enter:** `setPhase(6)`; `n-query-receptor` `.glow`; `n-persp-store` `.active`; `pkt-query` shown; after 1000ms: `pkt-result` shown; if `isTarget`: animates to `n-api2`; `n-api2` `.glow`
**DOM on exit:** reset

**Source symbols:** `IDispatcher.LocalInvokeAsync()`, `IPerspectiveStore.GetByStreamIdAsync()`

---

## 5. Maintenance Guide

**`IDispatcher` method changes** (`src/Whizbang.Core/IDispatcher.cs`):
- `SendAsync()` renamed → step 1
- `LocalInvokeAsync()` renamed → step 18
- New dispatch methods added → consider adding new animation steps or updating step 6

**`MessageEnvelope` / `MessageDispatchContext` changes** (`src/Whizbang.Core/Observability/`):
- New v2+ fields → update step 2 narration
- `DispatchContext.Mode` or `.Source` property names change → step 2

**`DispatchModes` / routing resolution changes** (`src/Whizbang.Core/Dispatch/`):
- Default mode changes from `Outbox` → step 6 narration
- `DefaultRoutingAttribute` removed or renamed → step 6
- `Routed<T>` API changes → step 6

**`LifecycleStage` values change** (`src/Whizbang.Core/Messaging/LifecycleStage.cs`):
- Any stage renamed → find affected step in steps 11, 13, 17

**`OutboxRecord` / `InboxRecord` field changes**:
- See `04-inbox-outbox-pattern.md` for detailed field references
- Steps 8 and 15 reference specific fields

**`IPerspectiveRunner.RunAsync()` signature change** → step 16

**`IPerspectiveStore.GetByStreamIdAsync()` change** → step 18

**TransportConsumer self-echo behavior changes**:
- If owned-namespace discard removed → step 14 narration

**What does NOT require an update:**
- Changes to `ScopeDelta`, `HopType`, `MessageHop` detailed fields (covered in `06-message-envelope-journey.md`)
- Changes to `PolicyContext` or tag hooks
- Changes to `compute_partition()` or instance heartbeat SQL
- Example message/receptor/perspective names — illustrative
