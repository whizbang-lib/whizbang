# Perspective Sync (AppendAndWait) — Animation Spec

**Animation file:** `docs/diagrams/animations/11-perspective-sync-append-wait.html`
**Steps:** 10
**Last verified against source:** 2026-04-05

---

## 1. Overview

**What this teaches:** The request-response-over-event-sourcing pattern. Shows how a command receptor decorated with `[AwaitPerspectiveSync]` emits an event, then waits for a specific perspective to process it before returning — achieving read-your-writes consistency in an eventually-consistent system.

**Why it matters:** Without sync, a command handler that creates an order and then reads the order summary might see stale data (the event hasn't been projected yet). `[AwaitPerspectiveSync]` provides a controlled wait without polling in application code. This is the Whizbang mechanism for synchronous-looking behavior over an asynchronous substrate.

**Intended audience:** Developers building command handlers that need to return read model data; architects designing saga patterns; anyone debugging why a query after a command returns stale results.

**Conceptual prerequisite:** Understanding that perspectives are eventually consistent projections, that events flow through outbox/inbox/perspective pipeline, and that perspective cursors track the last processed event per stream.

---

## 2. Visual Layout

Two-column split layout (`grid-template-columns: 1fr 1fr`):

| Column | DOM IDs | Represents |
|--------|---------|------------|
| Left — Command side | `n-handler`, `n-emit`, `sc-attr`, `wi-await`, `n-result`, `n-continue` | Command receptor waiting for perspective sync |
| Right — Perspective side | `n-pe-event`, `n-pe-runner`, `cp-bar`/`cp-fill`/`cp-label`, `n-pe-apply`, `n-pe-save`, `n-pe-signal` | Perspective worker processing the emitted event |

**Wait indicator** (`wi-await`):
- Hidden until `showCard()` applies `.visible`
- `.waiting`: dashed border, `pulse` animation — actively polling
- `.synced`: solid green border, no animation — sync achieved

**Checkpoint progress bar** (`cp-bar`, `cp-fill`): hidden until `.visible`; `cp-fill` width animated from 0% to 100% across perspective processing steps.

**Sync card** (`sc-attr`): shows `AwaitPerspectiveSyncAttribute` fields (PerspectiveType, TimeoutMs, FireBehavior).

**Reset:** `resetAll()` — hides `sc-attr`, `wi-await`, removes `.waiting`/`.synced`; hides checkpoint bar; resets text fields; clears node glows.

---

## 3. Source References

| Symbol | Source File | What to Check |
|--------|-------------|---------------|
| `AwaitPerspectiveSyncAttribute` | `src/Whizbang.Core/Perspectives/Sync/AwaitPerspectiveSyncAttribute.cs` | Properties: `PerspectiveType`, `TimeoutMs` (default 5000), `FireBehavior` (`SyncFireBehavior` enum) — step 1 |
| `SyncFireBehavior` enum | `src/Whizbang.Core/Perspectives/Sync/AwaitPerspectiveSyncAttribute.cs` | Values: `FireOnSuccess`, `FireAlways`, `FireOnEachEvent` — step 1 shows `FireOnSuccess` |
| `IPerspectiveSyncAwaiter.WaitAsync()` | `src/Whizbang.Core/Perspectives/Sync/IPerspectiveSyncAwaiter.cs` | Signature: `WaitAsync(Type perspectiveType, PerspectiveSyncOptions options, ct)` — step 3 |
| `SyncResult` | `src/Whizbang.Core/Perspectives/Sync/` | Values: `Synced`, `TimedOut` — step 9 shows `SyncResult.Synced` |
| `ILifecycleCoordinator.SignalPerspectiveComplete()` | `src/Whizbang.Core/Lifecycle/ILifecycleCoordinator.cs` | Called after perspective processes event — step 8 |
| `IPerspectiveRunner.RunAsync()` | `src/Whizbang.Core/Perspectives/IPerspectiveRunner.cs` | Runs in right-side lane — steps 5–7 |
| `IPerspectiveStore.UpsertAsync()` | `src/Whizbang.Core/Perspectives/IPerspectiveStore.cs` | Saves updated model — step 7 |
| `wh_perspective_events` | `src/Whizbang.Data.Postgres/Migrations/` | Auto-created in Phase 4.6 of `process_work_batch` — step 4 |
| `wh_message_associations` | `src/Whizbang.Data.Postgres/Migrations/` | Links event types to perspective names — step 4 |
| `wh_perspective_cursors` | `src/Whizbang.Data.Postgres/Migrations/` | Checkpoint table updated in step 7 |
| `AwaitPerspectiveSyncAttribute.DefaultTimeoutMs` | `src/Whizbang.Core/Perspectives/Sync/AwaitPerspectiveSyncAttribute.cs` | Currently 5000ms — step 3 narration |

---

## 4. Steps Specification

### `handler` — Command Handler Entry (2500ms)

**Narration:** A command receptor is decorated with `[AwaitPerspectiveSync(typeof(OrderSummary))]`. This tells the framework to wait for the OrderSummary perspective to process the emitted event before the handler returns.

**DOM on enter:** `n-handler` `.glow`; `sc-attr` `.visible`
**DOM on exit:** `resetAll()`

**Source symbols:** `AwaitPerspectiveSyncAttribute` — `PerspectiveType`, `TimeoutMs`, `FireBehavior`

---

### `emit` — Emit Event (2500ms)

**Narration:** Handler emits `OrderPlacedEvent`. The event is written to the EventStore and OutboxRecord. The event's `MessageId` (UUIDv7) is tracked for sync.

**DOM on enter:** `n-emit` `.glow`
**DOM on exit:** `resetAll()`

**Source symbols:** `IEventStore`, `OutboxRecord` — event written; `MessageId` tracked by sync awaiter

---

### `awaiter-start` — Awaiter Begins Polling (3000ms)

**Narration:** `PerspectiveSyncAwaiter.WaitAsync(typeof(OrderSummary), options)` starts. It queries: "Has the OrderSummary perspective processed the event with this MessageId?" Answer: not yet. Polling begins with 5-second timeout.

**DOM on enter:** `wi-await` `.visible`+`.waiting`; title = "PerspectiveSyncAwaiter.WaitAsync()"; detail = "Polling perspective checkpoint..."
**DOM on exit:** `resetAll()`

**Source symbols:** `IPerspectiveSyncAwaiter.WaitAsync()`, `AwaitPerspectiveSyncAttribute.DefaultTimeoutMs` = 5000ms

---

### `pe-create` — Perspective Event Auto-Created (2500ms)

**Narration:** Phase 4.6 of `process_work_batch` auto-creates a `wh_perspective_events` row. The event is matched to the OrderSummary perspective via `wh_message_associations`.

**DOM on enter:** `n-pe-event` `.glow`; `wi-await` still `.waiting`
**DOM on exit:** `resetAll()`

**Source symbols:** `wh_perspective_events`, `wh_message_associations`, Phase 4.6 of `029_ProcessWorkBatch.sql`

---

### `pe-run` — Perspective Runner Processes (2500ms)

**Narration:** `PerspectiveRunner.RunAsync(streamId, "OrderSummary", lastProcessedEventId)` loads the event from the event store and begins applying it to the read model.

**DOM on enter:** `n-pe-runner` `.glow`; `wi-await` `.waiting` with elapsed time; `cp-bar` `.visible` at 30%
**DOM on exit:** `resetAll()`

**Source symbols:** `IPerspectiveRunner.RunAsync()`

---

### `pe-apply` — Apply Event to Model (2500ms)

**Narration:** Pure function `OrderSummary.Apply(OrderPlacedEvent)` updates the read model. The model state is deterministic — same events always produce the same result.

**DOM on enter:** `n-pe-apply` `.glow`; `wi-await` `.waiting` with "3.4s elapsed"; `cp-bar` at 60%
**DOM on exit:** `resetAll()`

**Source symbols:** `Apply(event)` pure function on perspective class

---

### `pe-save` — Save & Update Checkpoint (2500ms)

**Narration:** `PerspectiveStore.UpsertAsync()` saves the updated model. Perspective cursor checkpoint updated to include this event. `wh_perspective_events` row marked processed.

**DOM on enter:** `n-pe-save` `.glow`; `wi-await` `.waiting` with "4.1s elapsed"; `cp-bar` at 90%
**DOM on exit:** `resetAll()`

**Source symbols:** `IPerspectiveStore.UpsertAsync()`, `wh_perspective_cursors`, `wh_perspective_events.processed_at`

---

### `pe-signal` — Signal Perspective Complete (3000ms)

**Narration:** `SignalPerspectiveComplete(eventId, "OrderSummary")` notifies the sync awaiter. The awaiter's next poll sees the checkpoint has advanced past the tracked event. Sync achieved!

**DOM on enter:** `n-pe-signal` `.glow`; `cp-bar` at 100%/Complete; `wi-await` `.synced`, title = "Synced!", detail = "4.2s"
**DOM on exit:** `resetAll()`

**Source symbols:** `ILifecycleCoordinator.SignalPerspectiveComplete()` — notifies the awaiter

---

### `result` — SyncResult Returned (2500ms)

**Narration:** `SyncResult.Synced` returned to the handler. The OrderSummary read model now reflects the OrderPlacedEvent. Handler can safely query the read model for a consistent response.

**DOM on enter:** `n-result` `.glow`; subtitle = "SyncResult.Synced (4.2s)"; `wi-await` still `.synced`
**DOM on exit:** `resetAll()`

**Source symbols:** `SyncResult` — `Synced` value

---

### `continue` — Handler Continues (2500ms)

**Narration:** The command handler continues execution. It can now read from the OrderSummary perspective and return a consistent response to the API caller. Read-your-writes consistency achieved over event sourcing.

**DOM on enter:** `n-continue` `.glow`+`.highlight-success`
**DOM on exit:** `resetAll()`

**Source symbols:** none — summary step

**Intent:** The conceptual punchline: synchronous-looking behavior from asynchronous event sourcing.

---

## 5. Maintenance Guide

**`AwaitPerspectiveSyncAttribute` changes** (`src/Whizbang.Core/Perspectives/Sync/AwaitPerspectiveSyncAttribute.cs`):
- `DefaultTimeoutMs` changes from 5000ms → update step 3 narration
- `FireBehavior` enum values change → update step 1 attribute display
- New properties added to attribute → update step 1 sync card

**`IPerspectiveSyncAwaiter.WaitAsync()` signature changes** (`src/Whizbang.Core/Perspectives/Sync/IPerspectiveSyncAwaiter.cs`):
- If `PerspectiveSyncOptions` structure changes → update step 3
- If method renamed → update step 3

**`SyncResult` values change**:
- If `Synced`/`TimedOut` renamed or new values added → update step 9

**`ILifecycleCoordinator.SignalPerspectiveComplete()` changes** (`src/Whizbang.Core/Lifecycle/ILifecycleCoordinator.cs`):
- If signature changes → update step 8

**Phase 4.6 auto-creation logic changes** (`src/Whizbang.Data.Postgres/Migrations/029_ProcessWorkBatch.sql`):
- If `wh_perspective_events` is no longer auto-created in Phase 4.6 → step 4 needs full rewrite
- If `wh_message_associations` table renamed → step 4 narration

**What does NOT require an update:**
- Changes to `OutboxRecord`, `InboxRecord` fields
- Changes to `PolicyContext`, `IMessageTagHook`, source generators, consistent hashing
- Changes to `PostLifecycle` or `WhenAll` coordination
