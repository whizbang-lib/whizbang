# Message Envelope Journey — Animation Spec

**Animation file:** `docs/diagrams/animations/06-message-envelope-journey.html`
**Steps:** 12
**Last verified against source:** 2026-04-05

---

## 1. Overview

**What this teaches:** How a `MessageEnvelope` accumulates processing hops as it travels through the system. Shows the complete `MessageHop` structure (v2 envelope with `DispatchContext`), how `ScopeDelta` records security context changes minimally across hops, causation/correlation chains, and the semantic difference between `HopType.Current` and `HopType.Causation`.

**Why it matters:** The envelope's hop list is the distributed tracing substrate for Whizbang. When debugging why a message went to the wrong service, why security context was wrong, or how events causally relate, the envelope hops are the evidence. Understanding this structure is prerequisite to using time-travel debugging effectively.

**Intended audience:** All developers; operations engineers reading production trace logs; anyone using `IMessageEnvelope` to inspect processing history.

**Conceptual prerequisite:** Understanding that every message in Whizbang is wrapped in a `MessageEnvelope` that travels alongside the payload through all processing stages.

---

## 2. Visual Layout

Two-panel layout (`grid-template-rows: 45% 55%`):

| Panel | DOM IDs | Represents |
|-------|---------|------------|
| Top: hop pipeline (4 columns + boundary) | `hop-col-1`–`hop-col-4`, `svc-bound`, `pip-pkt` | 4-hop sequence through Order Service → Inventory Service |
| Bottom: envelope inspector | `inspector-panel`, tabs, hop entries, causation nodes, scope sections | Live view of envelope state |

**Hop pipeline columns**: `hop-col-1` (Dispatcher), `hop-col-2` (Outbox Worker), boundary `svc-bound`, `hop-col-3` (Transport Consumer), `hop-col-4` (Perspective Worker). Background colors match service: cols 1-2 = orange (outbox phase), cols 3-4 = green (perspective phase).

**Pipeline packet** (`pip-pkt`): animated circle that moves between node positions. `.visible` shows it.

**Service boundary** (`svc-bound`): thin divider. `.glow` class adds pink color + shadow when packet crosses.

**Inspector tabs** (`.inspector-tab`): "Hops", "Causation Chain", "Security Scope". Click calls `switchTab(name)`.

**Hop entries** (`he-1`–`he-4`): hidden until `.visible`. `.current-hop`: cyan left border. `.causation-hop`: muted, italic.

**Causation chain nodes** (`cc-1`–`cc-3`): hidden until `.visible`.

**Scope sections** (`ss-initial`, `ss-hop2`, `ss-hop3`, `ss-merged`): hidden until `.visible`.

**Envelope message ID** (`env-msgid`): text updated per step.

**Reset:** `resetAll()` — hides packet, all hop entries, chain nodes, scope sections; removes glow from nodes and boundary; resets `env-msgid`; switches to Hops tab.

---

## 3. Source References

| Symbol | Source File | What to Check |
|--------|-------------|---------------|
| `IMessageEnvelope` | `src/Whizbang.Core/Observability/IMessageEnvelope.cs` | Fields: `MessageId`, `Payload`, `Hops`, `Version` (v2), `DispatchContext` (v2) — step 1 |
| `MessageEnvelope<TMessage>` | `src/Whizbang.Core/Observability/MessageEnvelope.cs` | Implementation; `Version = 1` default; `DispatchContext` required in v2 |
| `MessageDispatchContext` | `src/Whizbang.Core/Observability/MessageDispatchContext.cs` | Properties: `Mode` (DispatchModes), `Source` (MessageSource), `IsDefaultDispatch` (bool) — step 1 |
| `MessageHop` | `src/Whizbang.Core/Observability/MessageHop.cs` | All fields: `Type`, `ServiceInstance`, `Topic`, `StreamId`, `PartitionIndex`, `SequenceNumber`, `Duration`, `ExecutionStrategy`, `Scope`, `Metadata`, `Trail`, `CallerMemberName`, `CallerFilePath`, `CallerLineNumber`, `TraceParent` — steps 2, 5, 8, 10 |
| `HopType` enum | `src/Whizbang.Core/Observability/MessageHop.cs` | Values: `Current` = 0, `Causation` = 1 — step 12 |
| `ServiceInstanceInfo` | `src/Whizbang.Core/Observability/MessageHop.cs` | Record in hop: `ServiceName`, `InstanceId`, `HostName`, `ProcessId` — steps 2 and 8 |
| `ScopeDelta` | `src/Whizbang.Core/Security/ScopeDelta.cs` | Delta compression structure; `CollectionChanges` with Set/Add/Remove ops — steps 6 and 9 |
| `ScopeContext` | `src/Whizbang.Core/Security/IScopeContext.cs` | Full merged scope: `TenantId`, `UserId`, `Roles`, `Permissions` — step 3 |
| `IEnvelopeRegistry` | `src/Whizbang.Core/Observability/IEnvelopeRegistry.cs` | Registry for reference-identity lookup — step 1 |
| JSON short names | `src/Whizbang.Core/Observability/` | `"v"` = Version, `"dc"` = DispatchContext, `"h"` = Hops (from MessageEnvelope) |
| `MessageId` (UUIDv7) | `src/Whizbang.Core/` | Time-ordered UUID type — step 1 |

---

## 4. Steps Specification

### `create` — Envelope Created (2500ms)

**Narration:** Dispatcher creates `MessageEnvelope<PlaceOrderCommand>` (v2). `MessageId` is a UUIDv7 (time-ordered). `DispatchContext` records Mode (Local/Outbox/Both) and Source. Registered in `EnvelopeRegistry` for reference-identity lookup.

**DOM on enter:** `n-dispatcher` `.glow`; `env-msgid` = "MessageId: 019abc-ef12-7..."; switch to Hops tab
**DOM on exit:** `resetAll()`

**Source symbols:** `MessageEnvelope<TMessage>` (v2), `MessageDispatchContext`, `MessageId` (UUIDv7), `IEnvelopeRegistry`

**Intent:** Establishes the v2 envelope structure. `DispatchContext` is new in v2 (was not in v1 envelopes).

---

### `hop1` — Hop 1 — Dispatcher (3500ms)

**Narration:** First `MessageHop` recorded: `Type: Current`, `ServiceInstance` (Order Service), `Topic`, `StreamId`, `CallerInfo` (PlaceOrder @ OrderController.cs:42), `TraceParent`.

**DOM on enter:** `n-dispatcher` `.glow`; `env-msgid` set; show packet at dispatcher; `he-1` `.visible`
**DOM on exit:** `resetAll()`

**Source symbols:** `MessageHop` — `Type`, `ServiceInstance` (`ServiceInstanceInfo`), `Topic`, `StreamId`, `CallerMemberName`, `CallerFilePath`, `CallerLineNumber`, `TraceParent`

---

### `scope1` — Initial Scope (2500ms)

**Narration:** Full `ScopeContext` attached on first hop: `TenantId: acme`, `UserId: jane`, `Roles: [admin]`, `Permissions: [orders.write]`.

**DOM on enter:** `n-dispatcher` `.glow`; packet shown; `he-1` `.visible`; switch to Scope tab; `ss-initial` `.visible`
**DOM on exit:** `resetAll()`

**Source symbols:** `ScopeContext` — `TenantId`, `UserId`, `Roles`, `Permissions`

**Intent:** First hop carries the full scope (no delta yet — nothing to diff against).

---

### `move-h2` — Move to Outbox Worker (2500ms)

**Narration:** Envelope moves to `OutboxWorker` within the same service (Order Service). Still within the service boundary.

**DOM on enter:** `env-msgid` set; `he-1` `.visible`; switch to Hops tab; if `isTarget`: packet animates from dispatcher to outbox worker
**DOM on exit:** `resetAll()`

**Source symbols:** none — transition step

---

### `hop2` — Hop 2 — Outbox Worker (3000ms)

**Narration:** Second hop recorded: `PartitionIndex: 3`, `SequenceNumber: 1847`, `Duration: 12ms`. Same service, different processing context.

**DOM on enter:** `n-outbox-worker` `.glow`; packet shown at outbox worker; switch to Hops tab; `he-1`, `he-2` `.visible`
**DOM on exit:** `resetAll()`

**Source symbols:** `MessageHop` — `PartitionIndex`, `SequenceNumber`, `Duration`

---

### `scope2` — Scope Delta — Hop 2 (2800ms)

**Narration:** `ScopeDelta` on Hop 2: `+ Role: service-account`. The outbox worker runs under a service account, adding its role. Stored as minimal delta, not full context.

**DOM on enter:** `n-outbox-worker` `.glow`; packet shown; `he-1`, `he-2` `.visible`; switch to Scope tab; `ss-initial`, `ss-hop2` `.visible`
**DOM on exit:** `resetAll()`

**Source symbols:** `ScopeDelta` — `CollectionChanges` with Add operation

---

### `cross-boundary` — Cross Service Boundary (3000ms)

**Narration:** Message crosses the service boundary via transport. The envelope travels with all its accumulated hops and metadata intact.

**DOM on enter:** `env-msgid` set; `he-1`, `he-2` `.visible`; `svc-bound` `.glow`; if `isTarget`: packet animates from outbox worker to consumer
**DOM on exit:** `resetAll()`

**Source symbols:** transport delivery; envelope serialization preserves all hops across service boundaries

---

### `hop3` — Hop 3 — Transport Consumer (3000ms)

**Narration:** Third hop: new `ServiceInstance` — Inventory Service on different host/PID. New `TraceParent` span-id: `00-abc...-02`. Same `Topic` and `PartitionIndex`.

**DOM on enter:** `n-consumer` `.glow`; packet shown; switch to Hops tab; `he-1`, `he-2`, `he-3` `.visible`
**DOM on exit:** `resetAll()`

**Source symbols:** `MessageHop.ServiceInstance` (`ServiceInstanceInfo`) — different service/host/PID; `TraceParent` W3C Trace Context new span

---

### `scope3` — Scope Delta — Hop 3 (2800ms)

**Narration:** `ScopeDelta` on Hop 3: `+ Permission: inventory.reserve`. The consuming service adds its own authorization context. Merged view shows accumulated scope.

**DOM on enter:** `n-consumer` `.glow`; packet shown; `he-1`–`he-3` `.visible`; switch to Scope tab; `ss-initial`, `ss-hop2`, `ss-hop3`, `ss-merged` `.visible`
**DOM on exit:** `resetAll()`

**Source symbols:** `ScopeDelta` — Add permission; accumulated merged `ScopeContext`

---

### `hop4` — Hop 4 — Perspective Worker (3000ms)

**Narration:** Fourth hop: `StreamId: inventory-456`, `Duration: 3ms`. The envelope now has 4 complete hops — a full distributed trace.

**DOM on enter:** `n-persp-worker` `.glow`; packet shown; switch to Hops tab; `he-1`–`he-4` `.visible`
**DOM on exit:** `resetAll()`

**Source symbols:** `MessageHop` — `StreamId`, `Duration`

---

### `causation` — Causation Chain (3500ms)

**Narration:** Causation chain: `PlaceOrderCommand` (root) causes `OrderPlacedEvent` causes `InventoryReservedEvent`. All share `CorrelationId: corr-AAA`. Each `CausationId` points to its direct parent.

**DOM on enter:** `env-msgid` set; `he-1`–`he-4` `.visible`; switch to Causation tab; `cc-1`, `cc-2`, `cc-3` appear with staggered delays
**DOM on exit:** `resetAll()`

**Source symbols:** `MessageHop.CausationId`, `MessageHop.CorrelationId`, `MessageHop.CausationType` — distributed trace chain

---

### `hoptype` — HopType: Current vs Causation (4000ms)

**Narration:** When a child message is created, its parent's hops are carried forward as `HopType.Causation` (muted). The child's own processing adds a `HopType.Current` hop (bold). This enables full trace reconstruction across service boundaries.

**DOM on enter:** switch to Hops tab; `he-1`–`he-3` shown as `.causation-hop` with "(Causation)" titles; `he-4` shown as `.current-hop`; `n-persp-worker` `.glow`
**DOM on exit:** `resetAll()` + restore original hop titles/classes

**Source symbols:** `HopType.Current` = 0, `HopType.Causation` = 1 in `MessageHop.cs`

---

## 5. Maintenance Guide

**`IMessageEnvelope` / `MessageEnvelope` new fields** (`src/Whizbang.Core/Observability/IMessageEnvelope.cs`, `MessageEnvelope.cs`):
- `DispatchContext` was added in v2 — step 1 documents this; if v3 adds more fields, update step 1
- If `Version` field semantics change → update step 1

**`MessageDispatchContext` changes** (`src/Whizbang.Core/Observability/MessageDispatchContext.cs`):
- If `Mode`, `Source`, or `IsDefaultDispatch` change → update step 1

**`MessageHop` field changes** (`src/Whizbang.Core/Observability/MessageHop.cs`):
- Any field added, removed, or renamed in `MessageHop` → check which step references that field and update narration + hop entry HTML text
- `HopType` enum values change → update step 12

**`ServiceInstanceInfo` record changes** (`src/Whizbang.Core/Observability/MessageHop.cs`):
- If fields change (`ServiceName`, `InstanceId`, `HostName`, `ProcessId`) → steps 2 and 8

**`ScopeDelta` structure changes** (`src/Whizbang.Core/Security/ScopeDelta.cs`):
- If `CollectionChanges` or delta operations (Add/Remove/Set) change → steps 6 and 9

**`ScopeContext` property names change** (`src/Whizbang.Core/Security/IScopeContext.cs`):
- `TenantId`, `UserId`, `Roles`, `Permissions` → step 3

**What does NOT require an update:**
- Changes to `OutboxRecord`, `InboxRecord`, `LifecycleStage`
- Changes to `PolicyContext`, `IMessageTagHook`, source generators
- Changes to `process_work_batch` SQL phases
- Example service names and values (Order Service, Inventory Service, etc.) — illustrative
