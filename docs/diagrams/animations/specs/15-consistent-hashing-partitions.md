# Consistent Hashing & Partition Assignment — Animation Spec

**Animation file:** `docs/diagrams/animations/15-consistent-hashing-partitions.html`
**Steps:** 8
**Last verified against source:** 2026-04-05

---

## 1. Overview

**What this teaches:** How Whizbang distributes work across multiple service instances using virtual partitions and consistent hashing. Shows heartbeat registration, rank calculation, work claiming, automatic rebalancing when an instance dies, and self-healing when it rejoins — all without a coordination protocol.

**Why it matters:** Platform engineers operating multi-instance deployments need to understand why work distributes the way it does, how dead instance detection works, and what happens during instance restarts or scaling events.

**Intended audience:** Platform engineers; DevOps; developers troubleshooting work distribution or claiming behavior; anyone asking "why is Instance A processing messages from this stream but not Instance B?"

**Conceptual prerequisite:** Understanding that `process_work_batch` is called on a timer by each service instance, and that messages are assigned to partition numbers based on their stream ID.

---

## 2. Visual Layout

Vertical flex layout (`flex-direction: column`):

| Region | DOM IDs | Represents |
|--------|---------|------------|
| Instance row | `inst-a`, `inst-b`, `inst-c` | Three service instances with rank/partition display |
| Partition strip | `p0`–`p11` | 12 of 10,000 virtual partitions (representative subset) |
| Formula card | `fc-main` (title `fc-title`, code `fc-code`, note `fc-note`) | Current formula / explanation overlay |

**Instance box states** (`inst-a`, `inst-b`, `inst-c`):
- Default: neutral border
- `.active`: cyan border
- `.dead`: error border, `opacity: 0.4`, dashed
- `.claiming`: gold border, `var(--phase-cascade-bg)` background

**Partition cell states** (`p0`–`p11`):
- Default: `opacity: 0.4`, neutral
- `.inst-a`: blue background — owned by Instance A
- `.inst-b`: green background — owned by Instance B
- `.inst-c`: purple background — owned by Instance C
- `.orphaned`: red background, `pulse` animation — no owner
- `.reclaimed`: gold background — recently reclaimed

**Formula card**: hidden until `showFormula()` applies `.visible`.

**Reset:** `resetAll()` — removes all state classes from instance boxes and partition cells; hides formula card; resets rank/parts text; restores strategy badge.

---

## 3. Source References

| Symbol | Source File | What to Check |
|--------|-------------|---------------|
| `compute_partition()` SQL | `src/Whizbang.Data.Postgres/Migrations/001_CreateComputePartitionFunction.sql` | Formula: `abs(hashtext(p_stream_id::TEXT)) % p_partition_count`; IMMUTABLE function — step 1 |
| `register_instance_heartbeat()` | `src/Whizbang.Data.Postgres/Migrations/010_RegisterInstanceHeartbeat.sql` | Updates heartbeat timestamp each `process_work_batch` call; step 2 |
| `cleanup_stale_instances()` | `src/Whizbang.Data.Postgres/Migrations/011_CleanupStaleInstances.sql` | Removes instances beyond stale threshold; step 6 |
| `calculate_instance_rank()` | `src/Whizbang.Data.Postgres/Migrations/012_CalculateInstanceRank.sql` | Returns deterministic rank from active instance list; steps 2, 7, 8 |
| `claim_orphaned_outbox()` | `src/Whizbang.Data.Postgres/Migrations/024_ClaimOrphanedOutbox.sql` | Claims outbox work by partition ownership; step 4 |
| `claim_orphaned_inbox()` | `src/Whizbang.Data.Postgres/Migrations/025_ClaimOrphanedInbox.sql` | Claims inbox work by partition ownership; step 4 |
| `StaleThresholdSeconds` default | `src/Whizbang.Core/Messaging/IWorkCoordinator.cs` | Default is 30s (configurable via `wh_settings`) — step 2 formula card note and step 6 narration |
| `wh_settings` table | `src/Whizbang.Data.Postgres/Migrations/028_EventStorageErrorTracking.sql` | `stale_threshold_seconds` configurable via settings; step 6 |
| Stream ordering NOT EXISTS clause | `src/Whizbang.Data.Postgres/Migrations/029_ProcessWorkBatch.sql` Phase 7 | Step 5 narration references the NOT EXISTS guard |

---

## 4. Steps Specification

### `formula` — Partition Hash Formula (3000ms)

**Narration:** `compute_partition(stream_id, 10000)` — PostgreSQL function: `abs(hashtext(stream_id::TEXT)) % partition_count`. Deterministic: same stream always maps to same partition. IMMUTABLE for caching.

**DOM on enter:** formula card `.visible`; `fc-title` = "Partition Assignment Formula"; `fc-code` = `abs(hashtext(stream_id::TEXT)) % 10,000`; `fc-note` = "IMMUTABLE — same input always produces same output"
**DOM on exit:** `resetAll()`

**Source symbols:** `compute_partition()` SQL — `hashtext()` PostgreSQL function, IMMUTABLE flag

**Intent:** Establishes the mathematical foundation before showing instances.

---

### `heartbeat` — Instance Heartbeat (2800ms)

**Narration:** Each instance registers a heartbeat on every `process_work_batch` call via `register_instance_heartbeat()`. Rank calculated from deterministic ordering of active instances. 3 instances → ranks 0, 1, 2.

**DOM on enter:** `inst-a`, `inst-b`, `inst-c` all `.active`; formula card `.visible` with heartbeat context
**DOM on exit:** `resetAll()`

**Source symbols:** `register_instance_heartbeat()`, `calculate_instance_rank()`

Stale threshold is 30s by default (configurable via `wh_settings`). Formula card note and step 6 narration both reflect this.

---

### `assign-3` — 3-Instance Distribution (3000ms)

**Narration:** With 3 instances, partition ownership: `partition % 3 = rank`. Partitions 0,3,6,9 → Instance A (rank 0). Partitions 1,4,7,10 → Instance B (rank 1). Partitions 2,5,8,11 → Instance C (rank 2). Even distribution.

**DOM on enter:** `inst-a`–`inst-c` all `.active` with ownership text; `assignPartitions3()` colors partition cells (A=blue, B=green, C=purple)
**DOM on exit:** `resetAll()`

**Source symbols:** `calculate_instance_rank()` — modular arithmetic `partition % active_count = instance_rank`

**Intent:** Shows the clean 3-way distribution. Visual partition coloring makes ownership immediately obvious.

---

### `claim-work` — Claiming Orphaned Work (3000ms)

**Narration:** `claim_orphaned_outbox(instance_id, rank, count, lease_expiry)` — each instance claims work items whose partition matches its rank. Lease-based: sets `instance_id` and `lease_expiry` on claimed rows.

**DOM on enter:** `inst-a` gets `.claiming`; partition cells colored; formula card `.visible` with claiming SQL
**DOM on exit:** `resetAll()`

**Source symbols:** `claim_orphaned_outbox()` — WHERE clause: `partition_number % active_count = instance_rank AND (instance_id IS NULL OR lease_expiry < now)`

**Intent:** Shows the actual claiming SQL logic.

---

### `stream-order` — Stream Ordering Guarantee (3000ms)

**Narration:** Critical: within a stream, messages are processed in order. `NOT EXISTS (SELECT 1 FROM wh_outbox blocked WHERE blocked.stream_id = o.stream_id AND blocked.stream_id IS NOT NULL AND blocked.processed_at IS NULL AND blocked.created_at < o.created_at AND blocked.scheduled_for IS NOT NULL AND blocked.scheduled_for > p_now)`. Earlier retries block later messages.

**DOM on enter:** partition cells colored; formula card `.visible` with ordering SQL explanation
**DOM on exit:** `resetAll()`

**Source symbols:** Phase 7 NOT EXISTS guard in `src/Whizbang.Data.Postgres/Migrations/029_ProcessWorkBatch.sql`

**Intent:** Shows the ordering guarantee that prevents out-of-sequence processing within a stream, even during retries.

---

### `instance-dies` — Instance B Dies (3000ms)

**Narration:** Instance B stops sending heartbeats (crash, network partition, deployment). After stale threshold (30s by default, configurable via `wh_settings`), `cleanup_stale_instances()` removes it. Partitions 1, 4, 7, 10 are now orphaned — no owner.

**DOM on enter:** `inst-a` `.active`; `inst-b` `.dead`; `inst-c` `.active`; partition cells p1,p4,p7,p10 get `.orphaned`; rank-b shows "DEAD — no heartbeat"
**DOM on exit:** `resetAll()`

**Source symbols:** `cleanup_stale_instances()`, `StaleThresholdSeconds`

Both the narration and formula card now correctly say "30s (configurable via wh_settings)".

---

### `rebalance` — Automatic Rebalancing (3500ms)

**Narration:** With 2 instances remaining, ranks recalculated: A=0, C=1. New formula: `partition % 2`. Instance A claims even partitions (0,2,4,6,8,10). Instance C claims odd partitions (1,3,5,7,9,11). B's orphaned work automatically redistributed.

**DOM on enter:** `inst-a` `.active`; `inst-b` `.dead`+rank "REMOVED"; `inst-c` `.active`; `assignPartitions2()` recolors cells to 2-way; formula card with "Old: partition % 3 / New: partition % 2"
**DOM on exit:** `resetAll()`

**Source symbols:** `calculate_instance_rank()` — recalculates after stale cleanup

**Intent:** Shows automatic rebalancing without any coordination protocol. The partition ownership shifts purely based on rank recalculation.

---

### `rejoin` — Instance B Rejoins (3000ms)

**Narration:** Instance B comes back online, registers heartbeat. 3 active instances again, ranks recalculated. Partitions automatically redistribute back to 3-way split. Zero manual intervention — the system self-heals.

**DOM on enter:** `inst-a`–`inst-c` all `.active` with restored ranks; `assignPartitions3()` restores 3-way coloring; formula card "Back to partition % 3"
**DOM on exit:** `resetAll()`

**Source symbols:** `register_instance_heartbeat()`, `calculate_instance_rank()`

**Intent:** Demonstrates the self-healing closing the loop.

---

## 5. Maintenance Guide

**Stale threshold default** (`src/Whizbang.Core/Messaging/IWorkCoordinator.cs`):
- Default is 30s (configurable via `wh_settings`) — steps 2 and 6 reflect this ✓
- If default changes again → update step 2 formula card and step 6 narration

**`compute_partition()` formula changes** (`src/Whizbang.Data.Postgres/Migrations/001_CreateComputePartitionFunction.sql`):
- If `hashtext()` is replaced with a different hash function → update step 1 formula and narration
- If default partition count changes from 10,000 → update step 1 formula card

**`register_instance_heartbeat()` / `cleanup_stale_instances()` changes**:
- If heartbeat table schema changes → may affect how rank is described in step 2
- If stale threshold becomes non-configurable → update step 6

**`claim_orphaned_outbox()` claiming logic changes** (`src/Whizbang.Data.Postgres/Migrations/024_ClaimOrphanedOutbox.sql`):
- If WHERE clause changes from `partition_number % active_count = instance_rank` → step 4 narration and formula card

**Stream ordering NOT EXISTS clause changes** (`src/Whizbang.Data.Postgres/Migrations/029_ProcessWorkBatch.sql`):
- If the blocking condition changes → step 5 narration (quotes the SQL verbatim)
- If stream ordering is removed or changed to a different mechanism → rewrite step 5

**What does NOT require an update:**
- Changes to `MessageEnvelope`, `MessageHop`, `IDispatcher`, tag hooks, lifecycle stages
- Changes to perspective or snapshot logic
- Application-level changes (message types, receptors, perspectives)
