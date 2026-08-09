# Perspective Runner: Snapshot Restore & Replay — Animation Spec

**Animation file:** `docs/diagrams/animations/08-perspective-runner-replay.html`
**Steps:** 12
**Last verified against source:** 2026-04-05

---

## 1. Overview

**What this teaches:** The three `IPerspectiveRunner` operation modes: `RunAsync` (normal incremental replay), `RewindAndRunAsync` (late event correction via snapshot restore + full replay), and `BootstrapSnapshotAsync` (initial snapshot creation). Shows how snapshots enable efficient late-event handling without replaying from event zero.

**Why it matters:** Perspectives are the read model layer. When they get out of sync (late events, corruption, schema changes), these three methods are the recovery toolkit. Understanding when each is called and what it does is essential for operating a Whizbang system and for developers implementing custom perspectives.

**Intended audience:** All developers working with perspectives; operations engineers investigating read model inconsistencies; anyone implementing `IPerspectiveBase<TModel, TEvents...>`.

**Conceptual prerequisite:** Understanding that perspectives apply events as pure functions to produce read models, that checkpoints track the last processed event, and that `process_work_batch` creates perspective event work items in Phase 4.6.

---

## 2. Visual Layout

Two-row layout (`grid-template-rows: 32px 1fr`):

| Region | DOM IDs | Represents |
|--------|---------|------------|
| Mode tabs row | `mt-run`, `mt-rewind`, `mt-bootstrap` | Active operation mode indicator |
| Flow area (3-column grid) | Left: event stream; Center: process flow; Right: model state | Full animation canvas |

**Event slots** (`ev1`–`ev7`): show event stream with status coloring.
- `.highlight`: cyan border — being loaded
- `.applied`: green border, success bg — processed
- `.late`: pink border — late-arriving
- `.snapshot-point`: gold border — snapshot anchor
- `.replaying`: cyan border — being replayed in rewind

**Process flow nodes** (`n-runner`, `n-result`) and step cards (`sc-checkpoint`, `sc-query`, `sc-apply`, `sc-save`, `sc-snapshot`): cards hidden until `showCard()` applies `.visible`. Detail text updated per step.

**Model state** (`ms-current`, `ms-snapshot`): `ms-snapshot` hidden by default (shown in rewind steps). `.active` adds cyan border. `.snapshot` adds gold border.

**Model fields** (`mf-id`, `mf-status`, `mf-items`, `mf-total`, `mf-disc`): `.changed` class triggers `field-flash` animation.

**Mode tabs**: `.active` class applies cyan background.

**Reset:** `resetAll()` — clears event slot states, hides step cards, removes model state classes, resets model fields to `—`, hides snapshot model.

---

## 3. Source References

| Symbol | Source File | What to Check |
|--------|-------------|---------------|
| `IPerspectiveRunner.RunAsync()` | `src/Whizbang.Core/Perspectives/IPerspectiveRunner.cs` | Signature: `RunAsync(streamId, perspectiveName, lastProcessedEventId, ct)` returns `PerspectiveCursorCompletion` — steps 1–5 |
| `IPerspectiveRunner.RewindAndRunAsync()` | `src/Whizbang.Core/Perspectives/IPerspectiveRunner.cs` | Signature: `RewindAndRunAsync(streamId, perspectiveName, triggeringEventId, ct)` returns `PerspectiveCursorCompletion` — steps 6–10 |
| `IPerspectiveRunner.BootstrapSnapshotAsync()` | `src/Whizbang.Core/Perspectives/IPerspectiveRunner.cs` | Signature: `BootstrapSnapshotAsync(streamId, perspectiveName, lastProcessedEventId, ct)` — steps 11–12 |
| `PerspectiveCursorCompletion` | `src/Whizbang.Core/Perspectives/` | Return type of RunAsync/RewindAndRunAsync; contains `Status` and `LastEventId` — steps 5 and 10 |
| `IEventStore.ReadAsync<T>()` | `src/Whizbang.Core/Messaging/IEventStore.cs` | Loads events from checkpoint onward — step 2 |
| `IPerspectiveSnapshotStore.GetLatestSnapshotBeforeAsync()` | `src/Whizbang.Core/Perspectives/IPerspectiveSnapshotStore.cs` | Used in RewindAndRunAsync — step 7 |
| `IPerspectiveSnapshotStore.HasAnySnapshotAsync()` | `src/Whizbang.Core/Perspectives/IPerspectiveSnapshotStore.cs` | Bootstrap detection — step 11 |
| `IPerspectiveSnapshotStore.CreateSnapshotAsync()` | `src/Whizbang.Core/Perspectives/IPerspectiveSnapshotStore.cs` | Bootstrap creates initial snapshot — step 12 |
| `IPerspectiveStore.UpsertAsync()` | `src/Whizbang.Core/Perspectives/IPerspectiveStore.cs` | Saves model after replay — steps 5 and 10 |

---

## 4. Steps Specification

### RunAsync Mode

### `run-start` — RunAsync Begins (2500ms)

**Narration:** `PerspectiveRunner.RunAsync(streamId, "OrderSummary", lastProcessedEventId: null)` — starts from the beginning since no checkpoint exists.

**DOM on enter:** `setMode('run')`; `n-runner` `.glow`; `sc-checkpoint` `.visible`, detail = "lastProcessedEventId: null (start from beginning)"
**DOM on exit:** `resetAll()`

**Source symbols:** `IPerspectiveRunner.RunAsync()` — `lastProcessedEventId` null = start from beginning

---

### `run-query` — Query Events (2500ms)

**Narration:** `ReadAsync(streamId, fromEventId: null)` — loads all events for stream order-123 from the event store in UUID7 order.

**DOM on enter:** `setMode('run')`; `n-runner` `.glow`; `sc-checkpoint` + `sc-query` `.visible`; `ev1`–`ev6` get `.highlight`
**DOM on exit:** `resetAll()`

**Source symbols:** `IEventStore.ReadAsync<T>()` — `fromEventId: null` = load from start

---

### `run-apply1` — Apply Events 1-3 (3000ms)

**Narration:** Pure function `Apply(event)` called for each event in order. OrderCreated sets initial state, two ItemAdded events increment items and total.

**DOM on enter:** `setMode('run')`; `sc-apply` `.visible`; `ev1`–`ev3` `.applied`; `ms-current` `.active`; model fields set with `.changed`
**DOM on exit:** `resetAll()`

**Source symbols:** `Apply(event)` pure function on perspective class

---

### `run-apply2` — Apply Events 4-6 (3000ms)

**Narration:** PaymentReceived updates status to Paid. OrderShipped sets Shipped. DeliveryConfirmed sets Delivered. Model state evolves with each event.

**DOM on enter:** `setMode('run')`; `sc-apply` `.visible`; `ev1`–`ev6` `.applied`; `ms-current` `.active`; status field `.changed` = "Delivered"
**DOM on exit:** `resetAll()`

**Source symbols:** `Apply(event)` pure function

---

### `run-save` — Save & Checkpoint (2500ms)

**Narration:** `PerspectiveStore.UpsertAsync(streamId, model)` saves the read model. Checkpoint updated to event 6. `PerspectiveCursorCompletion` returned with status and lastEventId.

**DOM on enter:** `setMode('run')`; `sc-save` `.visible`; `ev1`–`ev6` `.applied`; `n-result` `.glow`; `ms-current` `.active`
**DOM on exit:** `resetAll()`

**Source symbols:** `IPerspectiveStore.UpsertAsync()`, `PerspectiveCursorCompletion`

---

### RewindAndRunAsync Mode

### `rewind-detect` — Late Event Detected (3000ms)

**Narration:** Event 7 (`DiscountApplied`) arrives late — its UUID7 timestamp places it between events 3 and 4 in the stream. The perspective checkpoint is at event 6, so this event was missed. `RewindAndRunAsync` triggered.

**DOM on enter:** `setMode('rewind')`; `ev1`–`ev6` `.applied`; `ev7` `.late`; `n-runner` `.glow`
**DOM on exit:** `resetAll()`

**Source symbols:** UUID7 ordering, `IPerspectiveRunner.RewindAndRunAsync()`

---

### `rewind-snapshot` — Find Nearest Snapshot (3000ms)

**Narration:** `GetLatestSnapshotBeforeAsync(streamId, "OrderSummary", triggeringEventId)` — finds snapshot at event 3. This is the safe restore point before the late event's position.

**DOM on enter:** `setMode('rewind')`; `ev7` `.late`; `ev3` `.snapshot-point`; `sc-snapshot` `.visible`; `ms-snapshot` shown
**DOM on exit:** `resetAll()`

**Source symbols:** `IPerspectiveSnapshotStore.GetLatestSnapshotBeforeAsync()` — `beforeEventId` = triggering late event's UUID7

---

### `rewind-restore` — Restore from Snapshot (2500ms)

**Narration:** Model state restored from snapshot JSON. Now at event 3: OrderId=order-123, Status=Created, Items=2, Total=$75.00. Replay will continue from here.

**DOM on enter:** `setMode('rewind')`; `ev7` `.late`; `ev3` `.snapshot-point`; `ms-current` `.active` with fields set; `sc-snapshot` `.visible`
**DOM on exit:** `resetAll()`

**Source symbols:** `IPerspectiveSnapshotStore.GetLatestSnapshotBeforeAsync()` return — `(SnapshotEventId, JsonDocument SnapshotData)` tuple

---

### `rewind-replay` — Replay with Late Event (3500ms)

**Narration:** Replay ALL events after snapshot in UUID7 order. The late event (DiscountApplied) now appears in its correct position between events 3 and 4. Apply: DiscountApplied (sets 10% discount), PaymentReceived, OrderShipped, DeliveryConfirmed.

**DOM on enter:** `setMode('rewind')`; `ev1`–`ev3` `.applied`; `ev7` `.applied`; `ev4`–`ev6` `.applied`; `sc-apply` `.visible`; model fields updated with discount
**DOM on exit:** `resetAll()`

**Source symbols:** `Apply(event)` pure function; UUID7 ordering placing late event in correct position

---

### `rewind-save` — Save Corrected Model (2500ms)

**Narration:** Corrected model saved. Total now reflects 10% discount ($67.50 vs $75.00). Checkpoint updated to event 7. The late event is fully integrated without data loss.

**DOM on enter:** `setMode('rewind')`; `sc-save` `.visible`; `ev1`–`ev7` `.applied`; `n-result` `.glow`; model shows corrected total
**DOM on exit:** `resetAll()`

**Source symbols:** `IPerspectiveStore.UpsertAsync()`, `PerspectiveCursorCompletion`

---

### BootstrapSnapshotAsync Mode

### `bootstrap-detect` — Bootstrap Detection (2500ms)

**Narration:** `HasAnySnapshotAsync()` returns false — this stream has processed events but has no snapshots yet. `BootstrapSnapshotAsync` creates one for future rewind efficiency.

**DOM on enter:** `setMode('bootstrap')`; `n-runner` `.glow`; `sc-snapshot` `.visible`, detail = "HasAnySnapshotAsync() = false"
**DOM on exit:** `resetAll()`

**Source symbols:** `IPerspectiveSnapshotStore.HasAnySnapshotAsync()` — cheap existence check

---

### `bootstrap-create` — Create Snapshot (2500ms)

**Narration:** `CreateSnapshotAsync(streamId, "OrderSummary", lastProcessedEventId, modelJson)` — serializes current model state as JSON and persists it. Future rewinds can restore from here instead of replaying from event 0.

**DOM on enter:** `setMode('bootstrap')`; `sc-snapshot` `.visible`; `n-result` `.glow`; `ms-snapshot` shown
**DOM on exit:** `resetAll()`

**Source symbols:** `IPerspectiveSnapshotStore.CreateSnapshotAsync()` — signature: `(streamId, perspectiveName, snapshotEventId, JsonDocument snapshotData, ct)`

---

## 5. Maintenance Guide

**`IPerspectiveRunner` method signature changes** (`src/Whizbang.Core/Perspectives/IPerspectiveRunner.cs`):
- `RunAsync()` parameters change → update step 1 narration
- `RewindAndRunAsync()` parameters change → update step 6 narration
- `BootstrapSnapshotAsync()` parameters change → update step 12 narration
- `PerspectiveCursorCompletion` fields change → update steps 5 and 10

**`IPerspectiveSnapshotStore` method changes** (`src/Whizbang.Core/Perspectives/IPerspectiveSnapshotStore.cs`):
- `GetLatestSnapshotBeforeAsync()` signature change → update step 7
- `HasAnySnapshotAsync()` signature change → update step 11
- `CreateSnapshotAsync()` signature change → update step 12

**`IPerspectiveStore.UpsertAsync()` changes** (`src/Whizbang.Core/Perspectives/IPerspectiveStore.cs`):
- Method renamed → update steps 5 and 10

**`IEventStore.ReadAsync<T>()` changes** (`src/Whizbang.Core/Messaging/IEventStore.cs`):
- Signature change → update step 2

**What does NOT require an update:**
- Changes to `OutboxRecord`, `InboxRecord`, `IDispatcher`, lifecycle stages
- Changes to `PolicyContext`, tag hooks, source generators
- Changes to `process_work_batch` SQL (the auto-creation in Phase 4.6 triggers the runner but doesn't appear in these steps)
- Example event names (`DiscountApplied`, `PaymentReceived`, etc.) — illustrative
