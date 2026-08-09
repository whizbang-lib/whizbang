# ProcessWorkBatch Lifecycle — Animation Spec

**Animation file:** `docs/diagrams/animations/process-work-batch-lifecycle.html`
**Steps:** 20
**Last verified against source:** 2026-04-05

---

## 1. Overview

**What this teaches:** The C# orchestration around PostgreSQL's `process_work_batch()` function. Shows the two-loop architecture (coordinator loop + publisher loop), how completions/failures/new messages accumulate between batch calls, how strategy selection controls flush timing, and how work results are distributed to bounded channels after the SQL call.

**Why it matters:** `process_work_batch` is called thousands of times per second in a production system. Understanding the C# layer — what accumulates between calls, how leases are managed, how the feedback loop works — is essential for performance tuning and debugging work distribution issues.

**Intended audience:** Platform engineers and framework contributors; operations engineers debugging why outbox messages aren't being published; developers investigating high database load.

**Conceptual prerequisite:** Understanding that `process_work_batch` is a PostgreSQL function that runs all 7 phases atomically, and that each service instance calls it on a tight loop.

---

## 2. Visual Layout

Three-column grid (`grid-template-columns: 240px 1fr 240px`):

| Column | DOM IDs | Represents |
|--------|---------|------------|
| Left — Accumulation | Strategy badges; `q-completions`, `q-failures`, `q-new-work`, `q-leases`; `n-request` | Queue cards filling between batch calls, strategy selection, request building |
| Center — PostgreSQL | `n-postgres`; phase indicators `pi-1`–`pi-7`; `n-workbatch` | The 7-phase SQL function |
| Right — Distribution | `n-coordinator`; `ch-outbox`, `ch-inbox`, `ch-perspective`; `n-publisher`; `n-feedback` | Channel distribution, publisher worker, feedback loop |

**Queue card states**: `.filling` (gold border — accumulating), `.draining` (green border — being flushed), `.active` (cyan border).

**Queue item visibility**: hidden until `showItem()` applies `.visible`.

**Phase indicators** (`pi-1`–`pi-7`): hidden until `showPhase()` applies `.visible`. Each has a colored background class (`ph-foundation`, `ph-completions`, etc.).

**Phase indicator labels** (in HTML): `1 Foundation — Heartbeat, Rank` | `2 Completions — Mark Done` | `3 Failures — Record Errors` | `4 Storage — New Work Items` | `4.5 Events → EventStore` | `4.6-4.7 Auto-Perspective Events` | `5 Claiming — Orphaned Work` | `6 Lease Renewals` | `7 Return — Batch-Limited, Stream-Ranked`

**Channel card states**: `.active` (cyan border); items hidden until `showItem()`.

**Strategy badges** (`sb-immediate`, `sb-scoped`, `sb-interval`): only `sb-interval` starts `.active`. Others toggled per step.

**Reset:** `resetAll()` — hides phases, queue items, channel items; removes node states, queue card states, packet states; resets all strategy badges with `sb-interval` active.

---

## 3. Source References

| Symbol | Source File | What to Check |
|--------|-------------|---------------|
| `ProcessWorkBatchRequest` | `src/Whizbang.Core/Messaging/IWorkCoordinator.cs` | All input parameters: instance identity, completions, failures, new messages, renewals, config |
| `WorkBatch` | `src/Whizbang.Core/Messaging/IWorkCoordinator.cs` | Return type: `List<OutboxWork>`, `List<InboxWork>`, `List<PerspectiveWork>`, `List<SyncInquiryResult>` |
| `OutboxWork` | `src/Whizbang.Core/Messaging/IWorkCoordinator.cs` | Fields: `MessageId`, `Destination`, `Envelope`, `MessageType`, `StreamId`, `PartitionNumber`, `Attempts`, `Status`, `Metadata` |
| `InboxWork` | `src/Whizbang.Core/Messaging/IWorkCoordinator.cs` | Similar to OutboxWork; `HandlerName` instead of `Destination` |
| `StaleThresholdSeconds` default | `src/Whizbang.Core/Messaging/IWorkCoordinator.cs` | Default = 30s (changed from 600s) — step 8 narration says "30s (configurable via wh_settings)" ✓ |
| `IntervalWorkCoordinatorStrategy` | `src/Whizbang.Core/Messaging/IntervalWorkCoordinatorStrategy.cs` | Timer-based flushing, 100ms default — step 5 |
| `ImmediateUnitOfWorkStrategy` | `src/Whizbang.Core/Messaging/ImmediateUnitOfWorkStrategy.cs` | Flush per message — step 5 |
| `ScopedUnitOfWorkStrategy` | `src/Whizbang.Core/Messaging/ScopedUnitOfWorkStrategy.cs` | Flush on dispose — step 5 |
| `IWorkChannelWriter.ShouldRenewLease()` | `src/Whizbang.Core/Messaging/IWorkChannelWriter.cs` | Only renews when nearing expiry — step 4 |
| `WorkCoordinatorPublisherWorker` | `src/Whizbang.Core/Workers/WorkCoordinatorPublisherWorker.cs` | Main background loop; in-flight tracking; `_trackPublishResult()` — steps 18, 19 |
| `WorkBatchCoordinator` | `src/Whizbang.Core/Messaging/WorkBatchCoordinator.cs` | Distributes WorkBatch to channels — step 17 |
| `DapperWorkCoordinator` | `src/Whizbang.Data.Dapper.Postgres/DapperWorkCoordinator.cs` | Executes SQL call — step 7 |
| `EFCoreWorkCoordinator` | `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs` | EF Core variant — step 7 |
| `process_work_batch()` SQL | `src/Whizbang.Data.Postgres/Migrations/029_ProcessWorkBatch.sql` | All 7 phases; batch limits from `wh_settings` — step 15 |
| `register_instance_heartbeat()` | `src/Whizbang.Data.Postgres/Migrations/010_RegisterInstanceHeartbeat.sql` | Phase 1 — step 8 |
| `cleanup_stale_instances()` | `src/Whizbang.Data.Postgres/Migrations/011_CleanupStaleInstances.sql` | Phase 1 — step 8; stale threshold 30s |
| `calculate_instance_rank()` | `src/Whizbang.Data.Postgres/Migrations/012_CalculateInstanceRank.sql` | Phase 1 — step 8 |
| `process_outbox_completions()` | `src/Whizbang.Data.Postgres/Migrations/013_ProcessOutboxCompletions.sql` | Phase 2 — step 9 |
| `process_inbox_completions()` | `src/Whizbang.Data.Postgres/Migrations/014_ProcessInboxCompletions.sql` | Phase 2 — step 9 |
| `update_perspective_cursors()` | `src/Whizbang.Data.Postgres/Migrations/016_UpdatePerspectiveCursors.sql` | Phase 2 — step 9 |
| `store_outbox_messages()` | `src/Whizbang.Data.Postgres/Migrations/020_StoreOutboxMessages.sql` | Phase 4 — step 11 |
| `store_inbox_messages()` | `src/Whizbang.Data.Postgres/Migrations/021_StoreInboxMessages.sql` | Phase 4 — step 11 |
| `wh_message_associations` | Phase 4.6 in `029_ProcessWorkBatch.sql` | Step 12 |
| `claim_orphaned_outbox()` | `src/Whizbang.Data.Postgres/Migrations/024_ClaimOrphanedOutbox.sql` | Phase 5 — step 13 |
| `max_work_items` / `max_work_items_per_stream` | `wh_settings` table, Phase 7 in `029_ProcessWorkBatch.sql` | Defaults 100/25; step 15 |
| `PartitionCount` default | `src/Whizbang.Core/Messaging/IWorkCoordinator.cs` | 10,000 — step 6 |
| `LeaseSeconds` default | `src/Whizbang.Core/Messaging/IWorkCoordinator.cs` | 300 — step 6 |

---

## 4. Steps Specification

### `accumulate-comp` — Accumulate Completions (3000ms)

**Narration:** Between batch calls, the `CompletionTracker` accumulates results from the previous cycle. Outbox completions (published), inbox completions (processed), perspective completions (projected).

**DOM on enter:** `q-completions` `.filling`; count = 4; `qi-ob-comp`, `qi-ib-comp`, `qi-pe-comp`, `qi-pc-comp` visible with delays
**DOM on exit:** `resetAll()`

**Source symbols:** `CompletionTracker<T>` in `WorkCoordinatorPublisherWorker`; `MessageCompletion`

---

### `accumulate-fail` — Accumulate Failures (2500ms)

**Narration:** Failures from the previous cycle: transport errors (outbox), handler exceptions (inbox). Each carries `MessageId`, `Status`, `Error`, and `FailureReason`.

**DOM on enter:** completions filling; `q-failures` `.filling`; count = 2; failure items visible
**DOM on exit:** `resetAll()`

**Source symbols:** `MessageFailure` — `MessageId`, `CompletedStatus`, `Error`, `Reason` (`MessageFailureReason` enum)

---

### `accumulate-new` — Accumulate New Messages (2500ms)

**Narration:** New work from the `Dispatcher`: outbox messages (to publish), inbox messages (received from transport), perspective events (projection work). Queued via `IUnitOfWorkStrategy`.

**DOM on enter:** completions + failures filling; `q-new-work` `.filling`; count = 3; new message items visible
**DOM on exit:** `resetAll()`

**Source symbols:** `IUnitOfWorkStrategy.QueueMessageAsync()`; `OutboxMessage`, `InboxMessage`

---

### `accumulate-lease` — Accumulate Lease Renewals (2200ms)

**Narration:** Messages still being processed or buffered need lease extensions to prevent orphaning. Only renewed when nearing expiry (>half lease duration) via `ShouldRenewLease` — not every tick. Tracked as `RenewOutboxLeaseIds` / `RenewInboxLeaseIds`.

**DOM on enter:** all queue cards filling; `q-leases` `.filling`; count = 2; lease items visible
**DOM on exit:** `resetAll()`

**Source symbols:** `IWorkChannelWriter.ShouldRenewLease()`; `ProcessWorkBatchRequest.RenewOutboxLeaseIds`, `RenewInboxLeaseIds`

---

### `strategy-trigger` — Strategy Triggers Flush (2800ms)

**Narration:** `IntervalWorkCoordinatorStrategy` timer fires (default 100ms). Snapshots all queues under lock, marks items as "Sent", clears queues. Other strategies: `Immediate` (every message), `Scoped` (on Dispose).

**DOM on enter:** all queue cards draining; `n-strategy` `.glow`; `sb-interval` `.active`
**DOM on exit:** `resetAll()`

**Source symbols:** `IntervalWorkCoordinatorStrategy`, `ImmediateUnitOfWorkStrategy`, `ScopedUnitOfWorkStrategy`

---

### `build-request` — Build Request (2500ms)

**Narration:** `ProcessWorkBatchRequest` built from snapshots: instance identity (InstanceId, ServiceName, HostName, ProcessId), all completions/failures/new messages/renewals, config (PartitionCount=10000, LeaseSeconds=300).

**DOM on enter:** `n-request` `.glow`; sublabel = "25 parameters"
**DOM on exit:** `resetAll()` (sublabel restored)

**Source symbols:** `ProcessWorkBatchRequest` — all fields; `PartitionCount` = 10,000; `LeaseSeconds` = 300

---

### `sql-call` — Single SQL Call (2500ms)

**Narration:** One atomic call to PostgreSQL: `SELECT * FROM process_work_batch(...)`. All 7 phases execute within a single transaction. Uses Dapper or EF Core depending on configuration.

**DOM on enter:** `n-postgres` `.glow` + `.pulse`
**DOM on exit:** `resetAll()` (pulse removed)

**Source symbols:** `DapperWorkCoordinator`, `EFCoreWorkCoordinator`

---

### `phase1` — Phase 1: Foundation (3000ms)

**Narration:** **Heartbeat**: register_instance_heartbeat() — updates last_seen. **Cleanup**: cleanup_stale_instances() — removes instances silent > 30s (configurable via `wh_settings`). **Rank**: calculate_instance_rank() — deterministic position for partition ownership.

**DOM on enter:** `n-postgres` `.glow`; `pi-1` visible
**DOM on exit:** `resetAll()`

**Source symbols:** `register_instance_heartbeat()`, `cleanup_stale_instances()`, `calculate_instance_rank()`, `StaleThresholdSeconds` = 30s default

---

### `phase2` — Phase 2: Completions (3000ms)

**Narration:** Mark completed work: `process_outbox_completions()`, `process_inbox_completions()`, `process_perspective_event_completions()`. Update checkpoints via `update_perspective_cursors()`. Delete ephemeral perspective events.

**DOM on enter:** `n-postgres` `.glow`; `pi-1`, `pi-2` visible
**DOM on exit:** `resetAll()`

**Source symbols:** `process_outbox_completions()`, `process_inbox_completions()`, `process_perspective_event_completions()`, `update_perspective_cursors()`

---

### `phase3` — Phase 3: Failures (2500ms)

**Narration:** Record failures: increment `Attempts`, set `Error` and `FailureReason`, update `StatusFlags`. Items schedule retry via `scheduled_for` with exponential backoff.

**DOM on enter:** `n-postgres` `.glow`; `pi-1`–`pi-3` visible
**DOM on exit:** `resetAll()`

**Source symbols:** `process_outbox_failures()`, `process_inbox_failures()`, `OutboxRecord.Attempts`, `OutboxRecord.ScheduledFor`

---

### `phase4` — Phase 4: Storage (2500ms)

**Narration:** Store new work: `store_outbox_messages()`, `store_inbox_messages()`, `store_perspective_events()`. Each returns was_newly_created flag. Idempotent via ON CONFLICT DO NOTHING.

**DOM on enter:** `n-postgres` `.glow`; `pi-1`–`pi-4` visible
**DOM on exit:** `resetAll()`

**Source symbols:** `store_outbox_messages()`, `store_inbox_messages()`, `store_perspective_events()`

---

### `phase45` — Phase 4.5-4.7: Event Storage + Auto-Perspective (3500ms)

**Narration:** **4.5**: Store events from outbox/inbox to `wh_event_store` with sequential versioning. **4.6**: Auto-create perspective event work items via `wh_message_associations`. **4.7**: Auto-create perspective checkpoint cursors for new streams.

**DOM on enter:** `n-postgres` `.glow`; `pi-1`–`pi-4`, `pi-45`, `pi-46` visible
**DOM on exit:** `resetAll()`

**Source symbols:** `wh_event_store`, `wh_message_associations`, `wh_perspective_events`, `wh_perspective_cursors`

---

### `phase5` — Phase 5: Claiming (3500ms)

**Narration:** Claim orphaned work via consistent hashing: `abs(hashtext(stream_id)) % partition_count`. Each instance claims partitions matching its rank. `claim_orphaned_outbox()`, `claim_orphaned_inbox()`, `claim_orphaned_receptor_work()`, `claim_orphaned_perspective_events()`.

**DOM on enter:** `n-postgres` `.glow`; `pi-1`–`pi-5` visible
**DOM on exit:** `resetAll()`

**Source symbols:** `compute_partition()` SQL, `claim_orphaned_outbox()`, `claim_orphaned_inbox()`, `claim_orphaned_receptor_work()`, `claim_orphaned_perspective_events()`

---

### `phase6` — Phase 6: Lease Renewals (2200ms)

**Narration:** Extend `lease_expiry` for outbox/inbox/perspective items still being processed. Prevents premature orphaning of items held in publisher buffer or awaiting transport.

**DOM on enter:** `n-postgres` `.glow`; `pi-1`–`pi-6` visible
**DOM on exit:** `resetAll()`

**Source symbols:** `OutboxRecord.LeaseExpiry`, `InboxRecord.LeaseExpiry`

---

### `phase7` — Phase 7: Return Results (3500ms)

**Narration:** Return ALL owned unprocessed work — not just new/orphaned. Per-stream ranking limits each stream to `max_work_items_per_stream` (default 25), then global `LIMIT max_work_items` (default 100) prevents hot loops. Stream-ordering enforced via NOT EXISTS. Ack counts on first row metadata.

**DOM on enter:** `n-postgres` `.glow`; all phases `pi-1`–`pi-7` visible
**DOM on exit:** `resetAll()`

**Source symbols:** `max_work_items` and `max_work_items_per_stream` from `wh_settings` (Phase 7 in `029_ProcessWorkBatch.sql`); NOT EXISTS stream ordering guard

---

### `workbatch` — WorkBatch Returned (2500ms)

**Narration:** `WorkBatch` returned to C#: `List<OutboxWork>`, `List<InboxWork>`, `List<PerspectiveWork>`, `List<SyncInquiryResult>`. Each item has `is_newly_stored` and `is_orphaned` flags.

**DOM on enter:** `n-workbatch` `.glow`
**DOM on exit:** `resetAll()`

**Source symbols:** `WorkBatch`, `OutboxWork`, `InboxWork`, `PerspectiveWork`, `SyncInquiryResult`; `is_newly_stored`, `is_orphaned` flags

---

### `distribute` — Distribute to Channels (3000ms)

**Narration:** `WorkBatchCoordinator` distributes work to bounded channels: `OutboxWork` → publisher channel, `InboxWork` → inbox channel, `PerspectiveWork` → perspective channel. Ack counts extracted from first row metadata.

**DOM on enter:** `n-coordinator` `.glow`; outbox/inbox/perspective channels activate with items at staggered delays
**DOM on exit:** `resetAll()`

**Source symbols:** `WorkBatchCoordinator`; bounded channel architecture; ack counts in first row `Metadata`

---

### `publish` — Publisher Worker Processes (3000ms)

**Narration:** `WorkCoordinatorPublisherWorker` reads from outbox channel. Publishes via `IMessagePublishStrategy`. Skips PreOutbox/PostOutbox for null-destination (event-store-only) messages. Tracks results: success → completion (stays in-flight until DB confirms), TransportException → lease renewal, other failure → failure report.

**DOM on enter:** `n-publisher` `.glow`; outbox channel active; perspective channel active
**DOM on exit:** `resetAll()`

**Source symbols:** `WorkCoordinatorPublisherWorker._trackPublishResult()`; null-destination skip for PreOutbox/PostOutbox; in-flight tracking until DB confirmation; `MessageFailureReason.TransportException`

---

### `feedback` — Feedback Loop (3500ms)

**Narration:** Processing results feed back into the next cycle's completions, failures, and lease renewals. The `CompletionTracker` state machine: Pending → Sent → Acknowledged → cleared. This closes the loop — the next `ProcessWorkBatch` call carries these results.

**DOM on enter:** `n-feedback` `.glow`+`.highlight-success`; `q-completions`, `q-failures`, `q-leases` filling with delays
**DOM on exit:** `resetAll()`

**Source symbols:** `CompletionTracker<T>` state machine; `ProcessWorkBatchRequest` built from new cycle's accumulated data

---

## 5. Maintenance Guide

**`ProcessWorkBatchRequest` input changes** (`src/Whizbang.Core/Messaging/IWorkCoordinator.cs`):
- New input parameters → update step 6
- `PartitionCount` default changes from 10,000 → step 6
- `LeaseSeconds` default changes from 300 → step 6
- `StaleThresholdSeconds` default: already updated to 30s ✓ — step 8 reflects this

**`WorkBatch` / result type changes** (`src/Whizbang.Core/Messaging/IWorkCoordinator.cs`):
- `OutboxWork`, `InboxWork`, `PerspectiveWork` fields change → step 16
- New work type added → step 16 and possibly step 17

**Publisher worker behavior changes** (`src/Whizbang.Core/Workers/WorkCoordinatorPublisherWorker.cs`):
- In-flight tracking behavior changes → step 18 narration (currently says "stays in-flight until DB confirms")
- Null-destination skip behavior changes → step 18 narration
- `ShouldRenewLease` logic changes → step 4 narration

**`process_work_batch()` phase changes** (`src/Whizbang.Data.Postgres/Migrations/029_ProcessWorkBatch.sql`):
- Phase numbering changes → update all `pi-*` labels
- New phases added → add new phase indicator and step
- `max_work_items` / `max_work_items_per_stream` default values change → step 15
- Stale threshold source (currently `wh_settings` or parameter) → step 8

**SQL function renames** (migrations `010`–`027`):
- Any function called in phases 1–6 renamed → update corresponding phase step narration

**Strategy defaults change**:
- `IntervalWorkCoordinatorStrategy` interval changes from 100ms → step 5 narration

**What does NOT require an update:**
- Changes to `MessageHop`, `ScopeDelta`, `HopType`
- Changes to `PolicyContext`, `IMessageTagHook`, source generators
- Changes to `IPerspectiveRunner` or `IPerspectiveSnapshotStore`
