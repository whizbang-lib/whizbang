# Inbox / Outbox Pattern — Animation Spec

**Animation file:** `docs/diagrams/animations/04-inbox-outbox-pattern.html`
**Steps:** 14
**Last verified against source:** 2026-04-05

---

## 1. Overview

**What this teaches:** The transactional outbox and inbox patterns as implemented in Whizbang. Shows how at-least-once delivery (outbox) and exactly-once processing (inbox) are achieved through database records, lease-based worker claiming, transport publishing, and deduplication.

**Why it matters:** These patterns are the foundation of reliable cross-service messaging. Developers need to understand why messages can't be lost (outbox), why they won't be processed twice (inbox dedup), and what the failure modes look like (lease expiry, retry counting).

**Intended audience:** All developers building multi-service Whizbang systems; operations engineers investigating message delivery; anyone debugging missing or duplicate messages.

**Conceptual prerequisite:** Understanding that Whizbang uses a database as the durable backbone for message delivery before transport handoff.

---

## 2. Visual Layout

Three-column split-screen (`grid-template-columns: 1fr 120px 1fr`):

| Column | DOM IDs | Represents |
|--------|---------|------------|
| Left — Service A (sender) | `n-app`, `n-disp`, `rc-outbox`, `n-worker`, `tx-boundary` | Application → Dispatcher → OutboxRecord → OutboxWorker |
| Center — Transport | `n-xport` (transport icon) | Transport (Kafka/RabbitMQ/Service Bus) |
| Right — Service B (receiver) | `n-tc`, `rc-inbox`, `dedup-new`, `n-receptor` | TransportConsumer → InboxRecord → Receptor |

**OutboxRecord card** (`rc-outbox`): fields `of-msgid`, `of-type`, `of-dest`, `of-status`, `of-instance`, `of-lease`, `of-pub`. Hidden until `activateCard()`.
- `.filling`: gold border — being written
- `.draining`: green border — being read/claimed
- `.active`: cyan border — selected/active

**InboxRecord card** (`rc-inbox`): fields `if-msgid`, `if-handler`, `if-type`, `if-recv`, `if-proc`, `if-status`. Same card states.

**Transaction boundary** (`tx-bound`): absolutely positioned dashed border over dispatcher + outbox record area. `.visible` shows it.

**Dedup indicator** (`dedup-new`): shows "New message — processing". `.visible` when dedup check passes.

**Transport icon** (`n-xport`): `.active` class adds cyan border and glow.

**Individual fields**: hidden until `showField()` or `showFieldValue()` called. `.visible` makes them appear. `.updated` triggers flash animation.

**Reset:** `resetAll()` — hides all packets, clears node states, resets both record cards, hides dedup indicator and transaction boundary, resets node sublabels.

---

## 3. Source References

| Symbol | Source File | What to Check |
|--------|-------------|---------------|
| `OutboxRecord` | `src/Whizbang.Core/Messaging/OutboxRecord.cs` | All fields shown in the card: `MessageId`, `MessageType`, `Destination`, `Attempts`, `StatusFlags`, `InstanceId`, `LeaseExpiry`, `PublishedAt`, `ProcessedAt`, `StreamId`, `PartitionNumber`, `FailureReason`, `ScheduledFor` |
| `InboxRecord` | `src/Whizbang.Core/Messaging/InboxRecord.cs` | All fields shown: `MessageId`, `HandlerName`, `MessageType`, `ReceivedAt`, `ProcessedAt`, `StatusFlags` |
| `MessageProcessingStatus` flags enum | `src/Whizbang.Core/Messaging/` | `StatusFlags` values: `Stored`, `Published`, `Processed` etc. — steps 3, 8, 11, 13 |
| `IDispatcher.SendAsync()` | `src/Whizbang.Core/IDispatcher.cs` | Entry point — step 1 |
| `IDeliveryReceipt` | `src/Whizbang.Core/` | Return type of `SendAsync()` — step 4 |
| `LifecycleStage.PreOutboxDetached/Inline` | `src/Whizbang.Core/Messaging/LifecycleStage.cs` | Pre-publish lifecycle stages — step 6 |
| `LifecycleStage.PostOutboxDetached/Inline` | `src/Whizbang.Core/Messaging/LifecycleStage.cs` | Post-publish lifecycle stages — step 8 |
| `LifecycleStage.PreInboxDetached/Inline` | `src/Whizbang.Core/Messaging/LifecycleStage.cs` | Pre-processing lifecycle stages — step 11 |
| `LifecycleStage.PostInboxDetached/Inline` | `src/Whizbang.Core/Messaging/LifecycleStage.cs` | Post-processing lifecycle stages — step 13 |
| `WorkCoordinatorPublisherWorker` | `src/Whizbang.Core/Workers/WorkCoordinatorPublisherWorker.cs` | Publisher worker implementation; skips PreOutbox/PostOutbox for null-destination (event-store-only) messages |
| `TransportConsumer` / self-echo | `src/Whizbang.Core/Workers/TransportConsumerWorker.cs` | Self-echo discard: owned-namespace events dropped at inbox — step 9 |
| `MessageDispatchContext` | `src/Whizbang.Core/Observability/MessageDispatchContext.cs` | `Mode` and `Source` on envelope — step 2 |

---

## 4. Steps Specification

### `app-send` — Send Command (2200ms)

**Narration:** Application calls `SendAsync(PlaceOrderCommand)` on the Dispatcher.

**DOM on enter:** `n-app` `.glow`; packet `pkt-cmd` shown at `n-app`; if `isTarget`: animates to `n-disp`
**DOM on exit:** `resetAll()`

**Source symbols:** `IDispatcher.SendAsync()`

---

### `envelope` — Create Envelope (2200ms)

**Narration:** Dispatcher creates `MessageEnvelope` with UUIDv7 `MessageId` and resolves `DispatchMode`.

**DOM on enter:** `n-disp` `.glow`; sublabel = "creating envelope..."
**DOM on exit:** `resetAll()`

**Source symbols:** `MessageEnvelope` (v2), `MessageDispatchContext`, UUID7 `MessageId`

---

### `tx-write` — Transactional Write (3500ms)

**Narration:** Within a single database transaction: `OutboxRecord` written alongside `EventStore` append. Atomicity ensures both succeed or both fail.

**DOM on enter:** transaction boundary `tx-bound` `.visible` (positioned over dispatcher + record area); `rc-outbox` `.active`; `of-msgid`, `of-type`, `of-dest`, `of-status` fields appear with delays
**DOM on exit:** `resetAll()`

**Source symbols:** `OutboxRecord` — `MessageId`, `MessageType`, `Destination`, `StatusFlags`; atomic transaction with event store

---

### `receipt` — Delivery Receipt (2000ms)

**Narration:** `IDeliveryReceipt` returned to the application. Confirms dispatch, not downstream processing.

**DOM on enter:** packet `pkt-receipt` shown, animates from `n-disp` to `n-app`; `n-app` `.glow`
**DOM on exit:** `resetAll()`

**Source symbols:** `IDeliveryReceipt`

---

### `worker-poll` — Worker Claims Record (2800ms)

**Narration:** `OutboxWorker` polls for unpublished records. Claims via lease: sets `InstanceId` and `LeaseExpiry` on the `OutboxRecord`.

**DOM on enter:** `n-worker` `.glow`; `rc-outbox` `.active` with existing fields; `of-instance` and `of-lease` fields appear
**DOM on exit:** `resetAll()`

**Source symbols:** `OutboxRecord.InstanceId`, `OutboxRecord.LeaseExpiry`; `claim_orphaned_outbox()` SQL

---

### `pre-outbox` — PreOutbox Stages (1800ms)

**Narration:** `PreOutboxDetached` and `PreOutboxInline` lifecycle stages fire before transport publish. Opportunity for enrichment or validation before publish.

**DOM on enter:** `n-worker` `.glow`; sublabel = "PreOutbox [Async/Inline]"
**DOM on exit:** `resetAll()`

**Source symbols:** `LifecycleStage.PreOutboxDetached`, `LifecycleStage.PreOutboxInline`

**Note:** These stages are SKIPPED for null-destination (event-store-only) messages — `WorkCoordinatorPublisherWorker` checks `string.IsNullOrEmpty(work.Destination)` before invoking pre/post-outbox lifecycle.

---

### `publish` — Publish to Transport (2500ms)

**Narration:** Message published to transport. The transport handles partitioning, ordering guarantees, and durable delivery.

**DOM on enter:** packet `pkt-xport` shown; if `isTarget`: animates from `n-worker` to `n-xport`; `n-xport` `.active`
**DOM on exit:** `resetAll()`

**Source symbols:** `IMessagePublishStrategy` — transport publish

---

### `post-outbox` — PostOutbox + Mark Published (2200ms)

**Narration:** `PostOutboxDetached/Inline` stages fire. `OutboxRecord.PublishedAt` set. `StatusFlags` transitions to Published.

**DOM on enter:** `rc-outbox` `.active`; all fields visible; `of-status` updated to "Published"; `of-pub` = "now"; `n-worker` sublabel = "PostOutbox [Async/Inline]"
**DOM on exit:** `resetAll()`

**Source symbols:** `LifecycleStage.PostOutboxDetached`, `LifecycleStage.PostOutboxInline`, `OutboxRecord.PublishedAt`, `MessageProcessingStatus`

**Note:** Also skipped for null-destination messages (same as PreOutbox).

---

### `deliver` — Cross-Service Delivery (2500ms)

**Narration:** Message delivered from transport to `TransportConsumer` in Service B. Self-echo check: if the message originated from this service (owned namespace), it is discarded to prevent double-firing.

**DOM on enter:** packet `pkt-inbox` shown; if `isTarget`: animates from `n-xport` to `n-tc`; `n-tc` `.glow`
**DOM on exit:** `resetAll()`

**Source symbols:** `TransportConsumerWorker` — self-echo discard for owned-namespace events

---

### `dedup` — Deduplication Check (2500ms)

**Narration:** `TransportConsumer` checks `InboxRecord` table for existing entry with same `MessageId + HandlerName`. This is a new message — proceed to processing.

**DOM on enter:** `n-tc` `.glow`; `dedup-new` `.visible`
**DOM on exit:** `resetAll()`

**Source symbols:** `InboxRecord` — composite key `(MessageId, HandlerName)` for deduplication

---

### `inbox-write` — PreInbox + Write InboxRecord (2800ms)

**Narration:** `PreInboxDetached/Inline` stages fire. `InboxRecord` written with `MessageId`, `HandlerName`, `ReceivedAt`.

**DOM on enter:** `rc-inbox` `.active`; `if-msgid`, `if-handler`, `if-type`, `if-recv` fields appear; `if-status` = "Processing"
**DOM on exit:** `resetAll()`

**Source symbols:** `LifecycleStage.PreInboxDetached`, `LifecycleStage.PreInboxInline`, `InboxRecord` — `MessageId`, `HandlerName`, `ReceivedAt`

---

### `invoke` — Receptor Invocation (2500ms)

**Narration:** `InventoryReceptor.HandleAsync(OrderPlacedEvent)` invoked. Receptor processes the event and returns result/cascaded events.

**DOM on enter:** `n-receptor` `.glow`; sublabel = "HandleAsync(event)"
**DOM on exit:** `resetAll()`

**Source symbols:** `IReceptor<TMessage>.HandleAsync()`

---

### `post-inbox` — PostInbox + Mark Processed (2200ms)

**Narration:** `PostInboxDetached/Inline` stages fire. `InboxRecord.ProcessedAt` set. `StatusFlags` transitions to Processed.

**DOM on enter:** `rc-inbox` `.active`; all fields visible; `if-proc` = "now"; `if-status` = "Processed"; `n-tc` `.highlight-success`
**DOM on exit:** `resetAll()`

**Source symbols:** `LifecycleStage.PostInboxDetached`, `LifecycleStage.PostInboxInline`, `InboxRecord.ProcessedAt`, `MessageProcessingStatus`

---

### `ack` — Acknowledge (2500ms)

**Narration:** Acknowledgment sent back to transport. Message delivery cycle complete. At-least-once delivery guaranteed via outbox; exactly-once processing guaranteed via inbox deduplication.

**DOM on enter:** packet `pkt-ack` shown; if `isTarget`: animates from `n-tc` to `n-xport`; `n-xport` `.active`
**DOM on exit:** `resetAll()`

**Source symbols:** transport acknowledgment mechanism

---

## 5. Maintenance Guide

**`OutboxRecord` field changes** (`src/Whizbang.Core/Messaging/OutboxRecord.cs`):
- Fields shown in the record card: `MessageId`, `MessageType`, `Destination`, `StatusFlags`, `InstanceId`, `LeaseExpiry`, `PublishedAt`
- If any are renamed, removed, or new fields added that should be shown → update steps 3, 5, 8

**`InboxRecord` field changes** (`src/Whizbang.Core/Messaging/InboxRecord.cs`):
- Fields shown: `MessageId`, `HandlerName`, `MessageType`, `ReceivedAt`, `ProcessedAt`, `StatusFlags`
- If renamed/removed → update steps 11, 13

**`MessageProcessingStatus` values change**:
- If `Stored`, `Published`, or `Processed` values change → update steps 3, 8, 11, 13

**Lifecycle stage names change** (`src/Whizbang.Core/Messaging/LifecycleStage.cs`):
- `PreOutboxDetached/Inline` → steps 6, note in step 8
- `PostOutboxDetached/Inline` → step 8
- `PreInboxDetached/Inline` → step 11
- `PostInboxDetached/Inline` → step 13

**Null-destination PreOutbox/PostOutbox skip behavior changes** (`src/Whizbang.Core/Workers/WorkCoordinatorPublisherWorker.cs`):
- If `string.IsNullOrEmpty(work.Destination)` check changes → update notes in steps 6 and 8

**Self-echo discard behavior changes** (`src/Whizbang.Core/Workers/TransportConsumerWorker.cs`):
- If owned-namespace check changes or is removed → update step 9 narration

**`IDeliveryReceipt` changes**:
- If renamed → update step 4 narration

**What does NOT require an update:**
- Changes to `MessageHop`, `ScopeDelta`, `DispatchModes` (beyond what affects envelope creation in step 2)
- Changes to `PolicyContext`, tag hooks, source generators
- Changes to perspective or snapshot logic
- Example message/receptor names — illustrative
