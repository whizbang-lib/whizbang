# Time-Travel Debugging: Late Event Rewind — Animation Spec

**Animation file:** `docs/diagrams/animations/12-time-travel-debugging-rewind.html`
**Steps:** 8
**Last verified against source:** 2026-04-05

---

## 1. Overview

**What this teaches:** What happens when an event arrives late (delayed by transport) and its UUID7 timestamp places it chronologically before events the perspective has already processed. Shows the automatic detection, snapshot lookup, model restoration, and replay sequence that corrects the read model without data loss or manual intervention.

**Why it matters:** Late-arriving events are a real scenario in distributed systems (network partitions, transport delays, cross-region replication). Whizbang handles them transparently via `RewindAndRunAsync`. This animation demonstrates the "time-travel" capability that makes event sourcing with perspectives resilient.

**Intended audience:** All developers using perspectives; operations engineers investigating read model inconsistencies; anyone trying to understand UUID7 ordering in the event store.

**Conceptual prerequisite:** Understanding that events are stored with UUID7 IDs (which embed timestamps) and that perspectives maintain a checkpoint cursor pointing to the last processed event.

---

## 2. Visual Layout

Vertical flex layout (`flex-direction: column`):

| Region | DOM IDs | Represents |
|--------|---------|------------|
| Timeline strip | `tstrip`, `te1`–`te6`, `tegap` | 7-slot event stream (6 normal + 1 gap/late slot) |
| Phase label | `phase-label` | Current phase description (updated per step) |
| Process cards row | `pc-detect`, `pc-snapshot`, `pc-replay` | Three-column status cards for detect / snapshot / replay phases |
| Model comparison | `mb-before`, `cmp-arrow`, `mb-after` | Before/after model state (only shown in step 7) |

**Timeline slot states** (`te1`–`te6`, `tegap`):
- Default: `opacity: 0.5`, no border
- `.normal`: green border, `var(--phase-perspective-bg)` — processed successfully
- `.late`: pink border, `#fce4ec` background — late-arriving event
- `.checkpoint`: gold border, `border-width: 3px` — last processed event
- `.snapshot-point`: orange border, `var(--phase-outbox-bg)` — snapshot anchor
- `.replaying`: cyan border, `var(--phase-dispatch-bg)` — being replayed

**Process card states** (`pc-detect`, `pc-snapshot`, `pc-replay`):
- Hidden by default (`opacity: 0`, `translateY(4px)`)
- `.visible`: fades in
- `.active`: cyan border — currently executing
- `.success`: green border — completed successfully
- `.problem`: pink border — error/unexpected state detected

**Model comparison** (`mb-before`, `mb-after`): hidden until step 7, shown as side-by-side model state comparison.

**Reset:** `resetAll()` — clears all timeline slot state classes, hides all process cards, hides model comparison, resets `phase-label` to `—`.

---

## 3. Source References

| Symbol | Source File | What to Check |
|--------|-------------|---------------|
| `IPerspectiveRunner.RewindAndRunAsync()` | `src/Whizbang.Core/Perspectives/IPerspectiveRunner.cs` | Signature: `RewindAndRunAsync(streamId, perspectiveName, triggeringEventId, ct)` — step 3 narration |
| `IPerspectiveRunner.RunAsync()` | `src/Whizbang.Core/Perspectives/IPerspectiveRunner.cs` | Normal replay path referenced in step 1 context |
| `IPerspectiveSnapshotStore.GetLatestSnapshotBeforeAsync()` | `src/Whizbang.Core/Perspectives/IPerspectiveSnapshotStore.cs` | Signature: `GetLatestSnapshotBeforeAsync(streamId, perspectiveName, beforeEventId, ct)` — step 4 narration |
| `IPerspectiveSnapshotStore.CreateSnapshotAsync()` | `src/Whizbang.Core/Perspectives/IPerspectiveSnapshotStore.cs` | Step 8: new snapshot created after correction |
| `IEventStore.ReadAsync<T>()` | `src/Whizbang.Core/Messaging/IEventStore.cs` | Events replayed in UUID7 order after snapshot restore |
| UUID7 ordering semantics | `src/Whizbang.Core/` (MessageId type) | UUID7 embeds timestamp — ordering by UUID7 = chronological ordering; this is the core of why late events can be detected and correctly positioned |
| `wh_perspective_cursors` | `src/Whizbang.Data.Postgres/Migrations/029_ProcessWorkBatch.sql` | The checkpoint table; step 1 refers to "checkpoint at event 6" |
| `wh_event_store` | `src/Whizbang.Data.Postgres/Migrations/` | Append-only event store; late events are appended with their original UUID7 |

---

## 4. Steps Specification

### `normal-state` — Normal Processing Complete (2800ms)

**Narration:** Events 1-6 have been processed normally. OrderSummary perspective checkpoint is at event 6 (Delivered). The read model shows $75.00 total, no discount. Everything looks correct... so far.

**DOM on enter:** `phase-label` = "Current State: Checkpoint at Event 6"; events `te1`–`te6` get `.normal`; `te6` gets `.checkpoint`
**DOM on exit:** `resetAll()`

**Source symbols:** `wh_perspective_cursors` (checkpoint table); `IPerspectiveRunner.RunAsync()`

**Intent:** Establishes the pre-condition. Shows 6 processed events with checkpoint at event 6.

---

### `late-arrives` — Late Event Arrives (3500ms)

**Narration:** A `DiscountApplied` event arrives 30 seconds late. Its UUID7 timestamp (10:00:04) places it between events 3 and 4. This event was created by another service but delayed in transport. The perspective missed it.

**DOM on enter:** `phase-label` = "PROBLEM: Late Event Detected!"; `te1`–`te6` get `.normal`; `te6` gets `.checkpoint`; `tegap` gets `.late` and its type text changes to "DiscountApplied", seq to "!"
**DOM on exit:** `resetAll()` + restores `tegap` text to "gap" and seq to "?"

**Source symbols:** UUID7 timestamp ordering; transport delivery delay scenario

**Intent:** Shows the problem. The visual gap slot represents the event that arrived after the checkpoint but belongs before it chronologically.

---

### `detect-rewind` — RewindAndRunAsync Triggered (3000ms)

**Narration:** The perspective worker detects the late event's UUID7 is BEFORE the checkpoint. `RewindAndRunAsync(streamId, "OrderSummary", triggeringEventId)` is called to correct the model.

**DOM on enter:** `phase-label` = "Phase 1: Detection — Triggering Rewind"; `pc-detect` `.visible` + `.problem`; `tegap` `.late` with corrected text
**DOM on exit:** `resetAll()` + restores `tegap`

**Source symbols:** `IPerspectiveRunner.RewindAndRunAsync()` — signature, `triggeringEventId` parameter

**Intent:** Shows the detection trigger. The process card appears in problem state to signal an unexpected condition was found.

---

### `find-snapshot` — Find Nearest Snapshot (3000ms)

**Narration:** `GetLatestSnapshotBeforeAsync(streamId, "OrderSummary", triggeringEventId)` — finds snapshot at event 3 (10:00:03). This is the safe restore point: the last known-good state BEFORE the late event's timestamp.

**DOM on enter:** `phase-label` = "Phase 2: Snapshot Search"; `pc-detect` `.visible`+`.problem`; `pc-snapshot` `.visible`+`.active`; `te3` gets `.snapshot-point`; `tegap` `.late`
**DOM on exit:** `resetAll()` + restores `tegap`

**Source symbols:** `IPerspectiveSnapshotStore.GetLatestSnapshotBeforeAsync()` — `beforeEventId` is the triggering late event's UUID7

**Intent:** Shows snapshot lookup. The `beforeEventId` parameter is the key — it finds a snapshot that predates the late event.

---

### `restore` — Restore Snapshot State (2500ms)

**Narration:** Model state restored from snapshot JSON. Now at event 3: OrderId=order-123, Status=Created, Items=2, Total=$75.00. All subsequent events (including the late one) will be replayed from here.

**DOM on enter:** `phase-label` = "Phase 3: Restore from Snapshot (Event 3)"; `te3` `.snapshot-point`; `pc-snapshot` `.visible`+`.success`; `mb-before` `.visible`
**DOM on exit:** `resetAll()`

**Source symbols:** `IPerspectiveSnapshotStore.GetLatestSnapshotBeforeAsync()` return value — `(SnapshotEventId, JsonDocument SnapshotData)` tuple

**Intent:** Shows model state after snapshot restore. The "before" model card establishes the pre-discount baseline.

---

### `replay-with-late` — Replay in UUID7 Order (3500ms)

**Narration:** Replay ALL events after snapshot in UUID7 timestamp order: Event 3 (snap) then DiscountApplied (10:00:04), PaymentReceived (10:00:05), OrderShipped (10:00:06), Delivered (10:00:07). The late event is now in its correct chronological position.

**DOM on enter:** `phase-label` = "Phase 4: Replay in UUID7 Order (late event in correct position)"; `te3` `.snapshot-point`; `tegap` `.replaying` with "DiscountApplied"/"!" text; `te4`–`te6` `.replaying`; `pc-replay` `.visible`+`.active`
**DOM on exit:** `resetAll()` + restores `tegap`

**Source symbols:** `IEventStore.ReadAsync<T>()` — UUID7 ordering; late event inserted in correct chronological position

**Intent:** The key step. Shows that by replaying from the snapshot in UUID7 order, the late event is naturally applied before the later events — as if it had never been late.

---

### `corrected-model` — Corrected Model State (3500ms)

**Narration:** Model now includes the discount. Total recalculated: $75.00 * 0.90 = $67.50. Because the discount was applied in the correct chronological order (before payment), the business logic is correct. No data loss, no manual intervention.

**DOM on enter:** `phase-label` = "Phase 5: Corrected Model — Late Event Fully Integrated"; `te1`–`te6` and `tegap` all `.normal`; `tegap` text = "DiscountApplied", seq = "3.5"; `mb-before` `.visible`; `cmp-arrow` `.visible`; `mb-after` `.visible`
**DOM on exit:** `resetAll()` + restores `tegap`

**Source symbols:** pure function semantics of `Apply(event)` — deterministic output when events are replayed in correct order

**Intent:** Shows the corrected model alongside the original. The $67.50 vs $75.00 difference demonstrates that business logic executed correctly because event ordering was corrected.

---

### `checkpoint-update` — Save & New Checkpoint (2500ms)

**Narration:** Corrected model saved. Checkpoint updated to include all 7 events. A new snapshot may be created for future efficiency. The perspective is now fully consistent — as if the event was never late.

**DOM on enter:** `phase-label` = "Complete: Checkpoint Updated, Model Consistent"; `pc-detect` `.visible`+`.success`; `pc-snapshot` `.visible`+`.success`; `pc-replay` `.visible`+`.success`
**DOM on exit:** `resetAll()`

**Source symbols:** `IPerspectiveRunner` — checkpoint update; `IPerspectiveSnapshotStore.CreateSnapshotAsync()` — optional new snapshot

**Intent:** Shows resolution. All three phase cards appear green — clean completion.

---

## 5. Maintenance Guide

**`IPerspectiveRunner.RewindAndRunAsync()` signature changes** (`src/Whizbang.Core/Perspectives/IPerspectiveRunner.cs`):
- If parameters renamed or added → update step 3 narration
- If behavior changes (e.g., no longer looks for snapshots) → steps 4 and 5 may need rewriting

**`IPerspectiveSnapshotStore.GetLatestSnapshotBeforeAsync()` changes** (`src/Whizbang.Core/Perspectives/IPerspectiveSnapshotStore.cs`):
- If parameter `beforeEventId` renamed → update step 4 narration
- If return type changes from tuple → update step 5 narration

**UUID7 ordering behavior changes:**
- If Whizbang moves away from UUID7 to a different time-ordered ID → steps 2 and 6 narrations need updates (both mention UUID7 timestamps)
- If UUID7 generation changes to not embed monotonic timestamps → the "chronological position" language needs rethinking

**Event store ordering changes** (`src/Whizbang.Core/Messaging/IEventStore.cs`):
- If `ReadAsync` changes ordering semantics away from UUID7 order → step 6 narration about "UUID7 timestamp order" breaks

**`wh_perspective_cursors` schema changes** (`src/Whizbang.Data.Postgres/Migrations/`):
- If checkpoint representation changes → steps 1 and 8 narrations may need updating

**What does NOT require an update:**
- Changes to `OutboxRecord`, `InboxRecord`, dispatch modes, or lifecycle stages
- Changes to `IMessageTagHook`, `IPolicyEngine`
- Example event names (`DiscountApplied`, `PaymentReceived`, etc.) — these are illustrative
