# Plan: the partition assignment is the only owner of a stream

Status: APPROVED by the owner (2026-11-23); open questions answered (section 6). Issues: #2540 (umbrella), #2545 (first slice, wave 11), phase issues listed in section 5.
Evidence: survey of develop @ b274d8843 (file:line references below; SQL paths are under
`src/Whizbang.Data.Postgres/Migrations/`, C# under `src/Whizbang.Core/Workers/` unless named).

## 1. Context

The owner's intent: the published partition assignment decides which instance owns a stream. The code
does something else. The assignment only decides who may take **unowned** work. Once a stream has an
owner in the stream ledger (`wh_active_streams.assigned_instance_id`), that stored owner wins, for as long
as it is live. The first instance to store work for a stream becomes its owner. A rebalance therefore
moves only idle streams. The busiest streams, the ones scaling out is meant to spread, never move.

The docs describe the code, not the intent: "Streams it has already claimed stay with it"
(`partition-assignment.md:172-176`), "Sticky: once claimed, `wh_active_streams` pins the stream to its
owner" (`multi-instance-coordination.md:442`), "Streams pin to the instance that first stores work for
them" (`work-coordination.md:63`).

Why stickiness was added (it solved real problems, and the new design must keep solving them):
- **Rank-churn wedge** (7379ff573, PR #194): claims needed both the partition rank and "no other owner".
  After the instance count changed, the rank holder and the stored owner each blocked the other, and inbox
  claims wedged for good in a consumer deployment.
- **Per-stream order and one writer**: committing instances raced on one saga stream (0d66fbba8, PR #212),
  and two pods lost an update (3f2bdccb7, PR #274).

Both came from instances disagreeing about who owns what. Stickiness papered over the disagreement. This
design removes the disagreement instead.

## 2. Where behavior contradicts the intent today

| # | Contradiction | Where |
|---|---|---|
| C1 | Every acquisition has an owner branch that wins over the partition | perspective `557:127`, outbox `317:1299`, inbox `411:301`, multi-stream `317:1075` |
| C2 | A live owner blocks the partition holder (perspectives: no lease test at all) | `557:132-136`, fetch gate `536:135-158`, outbox `317:1246-1254`, inbox `411:138-146` |
| C3 | Store paths pin the first writer as owner | `271:916-924`, `460:1950-1956` |
| C4 | New perspective rows are leased to the pinned owner or the committer, never by partition | `271:1241-1262`, `551:645-668`, `241:701-710` |
| C5 | Takeover happens only when the owner's instance row is gone, not when the assignment moves | `536:805-821`, `557:285-301` |
| C6 | `release_unstarted_leases` keeps the owner, so a released perspective stream returns to its releaser | `162:995-998` (#2545) |
| C7 | A claimer ranks itself when its cached assignment is missing, expired or excludes it, so two claimers can use two different maps at once | `PartitionAssignmentCache.cs:104`, `545:354-377` |
| C8 | The wake-up notification routes by the live list, not the assignment | `536:251-254, 296-312` |
| C9 | Nothing fences stream ownership: no epoch on the ledger, completions do not check the holder, the row-version check is not tied to ownership | `241` completions, `PerspectiveRowVersion.cs:33-38` |
| C10 | Instance step is plain modulo over sorted members: one join or leave moves about N/(N+1) of all partitions; no damping | `PartitionAssignment.cs:74-80`, `PartitionAssigner.cs:129-131` |
| C11 | `OnAssignmentChanged` has no subscriber, so nothing reacts to a rebalance | `src/` |
| C12 | Write-behind expiry is this process's clock since first hold, not ownership | `PerspectiveWriteBehindCache.cs:207-209` |

## 3. Target design

### 3.1 Principles

- **P1. One authority.** The published assignment is the only answer to "who owns stream S": the owner of
  `partition(S)` in the current map. No claim, store, lease, wake-up or release path consults anything else.
- **P2. One map at a time.** Every instance claims against the same published map version. Nobody computes
  a private ranking. Disagreement, the root cause of the wedge and the lost update, cannot happen.
- **P3. Ownership moves by handover, never by race.** A moved partition passes from the old owner to the
  new one through an explicit, fenced protocol. A stream is never claimable by two instances at once, so
  per-stream order and one writer hold by construction.
- **P4. Fence every write.** Ownership carries an epoch. A write from an instance whose epoch is stale is
  rejected in the same statement. Correctness never depends on a slow instance noticing in time.
- **P5. Save on a clean handover, drop only on lost ownership.** Work in memory is correct up to the
  events applied. Saving it is what the owner would do anyway. Dropping it forces a replay.
- **P6. Move as little as possible, as rarely as possible.** A join or leave moves about 1/N of the
  partitions, and membership churn during a rolling deploy settles before anything moves.
- **P7. Gravity.** The correct path is the only path: one SQL predicate for "I own this", used by every
  claim function, and a guard test that fails the build when a function bypasses it.

### 3.2 The ownership table (replaces stream-level ownership)

New table `wh_partition_ownership(partition_number PK, owner_instance_id, epoch bigint, state, next_owner_instance_id, handover_deadline)`,
one row per partition (default 10,000 rows).

- `state` is `active` or `handing_over`.
- `epoch` increases by one every time the owner changes. It is the fencing token.
- The assigner writes it, under its role epoch (`wh_assert_role_epoch`, `330:164,179-183`), so a deposed
  assigner cannot write a map.
- Ownership is per partition, not per stream. `wh_active_streams` loses `assigned_instance_id` as an
  authority (C3, C4, C5 go away). It keeps only what cleanup and activity tracking need, or is folded away
  if nothing does (decided in phase 3 by inventory).
- Per-row leases (`instance_id`, `lease_expiry` on work rows) stay. They recover work from a crash inside
  one owner's tenure. They no longer decide ownership.

One predicate, `wh_i_own(partition_number, instance_id) -> epoch or NULL`, answers "may I claim this", and
every claim function uses it (P7). It is a primary-key lookup on a 10,000-row table, cheaper than today's
join to `wh_active_streams` plus `wh_live_instance_ids()`.

### 3.3 Assignment: minimal moves, damped

- The assigner keeps the current map and changes it incrementally:
  - **Join**: the new member takes about 1/(N+1) of the partitions, taken evenly from existing members.
  - **Leave or death**: the leaver's partitions are spread evenly across the rest.
  - Nothing else moves. This replaces modulo over sorted members (C10).
- **Settle window**: membership changes are collected for a short window (default 5 seconds, bound from
  configuration) before one new map is published. A rolling deploy of K instances causes K small moves, not
  a reshuffle per heartbeat.
- **Ready before assigned**: a joining instance receives partitions only once its startup stages report
  ready, so it never owns work it cannot yet process.
- **Graceful leave hands over first**: an instance that is shutting down asks the assigner to move its
  partitions and hands them over (3.4) before it deregisters. Shutdown becomes a clean handover instead of
  a death.
- **No assigner, no stall**: a published map stays valid while its members are live. If the assigner role
  lapses, claims continue on the current map, and the next assigner (role failover, #966) resumes changes.
  A claimer never falls back to ranking itself (C7).
- The wake-up notification routes by the map (C8).

### 3.4 Handover protocol, per moved partition

1. The assigner sets the partition to `handing_over`, with `next_owner` and a `handover_deadline`
   (default 30 seconds, bound from configuration). Ownership and epoch stay with the old owner.
2. The old owner stops claiming new work in that partition. That is the effect of the predicate: claims
   need `active`.
3. The old owner finishes the batch it has in flight.
4. The old owner saves its write-behind models for streams in that partition (flush reason `handover`).
5. The old owner releases the partition's unstarted rows, then acknowledges.
6. The acknowledgement flips the row to `active`, owner = `next_owner`, `epoch + 1`, in one statement
   fenced on the old epoch.
7. The new owner's next claim sees the partition and replays from the stored state.

If the deadline passes without an acknowledgement (the old owner is dead, stuck or partitioned away), the
assigner completes steps 6 and 7 itself. The old owner's late writes then carry a stale epoch and are
rejected (3.5). Its held models are dropped, not saved (P5).

A partition in `handing_over` is owned by nobody new until the flip. That is the window in which order
and single-writer are protected. Its length is one batch plus one flush in the normal case, and the
deadline in the worst case.

### 3.5 Fencing

- Every claim returns the epoch of each claimed row's partition. The worker carries it with the batch.
- Every write that commits a result checks the epoch in the same statement, by joining
  `wh_partition_ownership` on partition and epoch:
  - completions and failures on all three queues;
  - perspective upserts and write-behind flushes;
  - outbox publish marks.
- A stale write affects zero rows and is reported as `fenced` (a metric and a debug log, never silent).
  The work stays for the current owner.
- The row-version check (`PerspectiveRowVersion`) stays. It protects concurrent edits; the epoch protects
  ownership. They answer different questions.

### 3.6 Write-behind (#2540)

- The cache subscribes to ownership changes (C11):
  - **clean handover**: save, then acknowledge (3.4 step 4);
  - **epoch loss without a handover**: drop.
- A held model records the epoch it was built under. A flush carries that epoch, so a stale flush is
  fenced (3.5).
- Expiry by the local clock (C12) becomes a memory bound only (maximum hold age), never an ownership
  decision.

### 3.7 Release paths

Ownership no longer lives on the stream, so a release never changes ownership:
- `release_unstarted_leases` returns rows to the partition owner's queue. If the releaser still owns the
  partition, it claims them again, which is correct under P1. #2545 is the first step: the release clears
  the stored owner, so the assignment decides.
- **#724 stuck-work release** existed so a sibling could try. Under P1 a sibling may not. The new rule: an
  instance that keeps failing the same rows reports itself unhealthy through the existing eligibility rule,
  and the assigner hands its partitions over (3.4). Rows that fail everywhere go to the dead-letter path as
  today.
- **Failed-batch release**: rows return to the owner and retry under the normal attempt limits.

### 3.8 Multi-stream commands

A run's streams are claimed by whoever owns each stream's partition, as today (`plans/multi-stream-commands.md`),
with the stored-owner branch removed. Each stream's outcome is fenced by its partition's epoch. A partition
that moves mid-run hands its streams over like any other work.

### 3.9 Mixed-version rollout

During a rolling deploy, old instances still use the owner path. The two rules must not run at once.
- Each instance registers the ownership mode it supports.
- Ownership by map turns on **per service** (one row in that service's `wh_settings`, flipped by its assigner) only
  when every live instance of that service supports it. A service's instances share one schema and one map, so the
  switch is scoped to them; other services switch on their own schedule. Until then everything runs as today.
- After the flip, an old instance that joins is ineligible for work and logs why.
- This connects to #2053 (the minimum release a rolling deploy can upgrade from). That decision should name
  the release that ships this mode.

## 4. What each choice protects, and its locking test

| Choice | Protects | Test that fails if it flips |
|---|---|---|
| P1 one authority | Rebalance actually moves busy streams | Real-database test: a stream with pending work, the map moves its partition, the new owner claims and the old owner claims nothing |
| P2 one map | No wedge, no split ownership | Guard test: no claim function ranks instances itself (scans migration SQL for `ROW_NUMBER() OVER (ORDER BY instance_id)` in claim paths); test with a stale cache claims nothing rather than self-ranking |
| P3 handover | Per-stream order, one writer | Test that a `handing_over` partition is claimable by neither side until the flip; ordering test across a handover |
| P4 fencing | A slow old owner cannot overwrite | Test: an old-epoch completion, upsert and flush each affect zero rows and raise `fenced` |
| P5 save vs drop | No needless replay; no stale write | Component tests: clean handover saves and nothing replays; deadline takeover drops and the new owner replays |
| P6 minimal moves | Small, rare moves | Property test: join or leave moves at most ceil(P/N) partitions; settle-window test with K joins inside the window publishes once |
| P7 gravity | No path bypasses the predicate | Guard test: every claim function calls `wh_i_own` |
| 3.9 mode gate | No mixed rules | Test: with one live instance on the old mode the map mode stays off |

## 5. Phases

1. **#2545** (in progress): a release clears the stored owner, so a released stream goes where its
   partition is assigned. Narrow, ships in wave 11.
2. **One map** (#2554): remove the self-ranking fallback (C7), route the wake-up by the map (C8). Claimers with no
   valid map wait. Small; safe under today's rules.
3. **Ownership table and predicate** (#2555): `wh_partition_ownership`, `wh_i_own`, and the mode gate (3.9), with
   claim functions switched to the predicate behind the gate. Removes C1 to C5. Inventory decides what is left
   of `wh_active_streams`.
4. **Fencing** (#2556): epochs through claims, completions, upserts, flushes and publish marks (C9).
5. **Handover** (#2557, the write-behind half of #2540): the protocol (3.4), graceful leave, the stuck-work rule (3.7), and the write-behind
   save/drop (#2540, C11, C12).
6. **Minimal moves and damping** (#2558): incremental map, settle window, ready-before-assigned (C10).
7. **Docs**: rewrite the ownership sections of `partition-assignment`, `multi-instance-coordination`,
   `work-coordination` and `claim-loop` (including the "consistent hashing" wording and the stale "no
   assignment table" line), plus a design page with the reasoning for each principle, linked both ways to
   the locking tests.

Each phase is its own issue, ships test-first, and leaves the system correct on its own. Phases 3 to 5 sit
behind the mode gate until phase 5 lands, then the gate defaults on.

Rough size: 2 to 3 weeks of work in total, most of it phases 3 to 5. Hot-path SQL in each phase gets an
EXPLAIN before and after.

## 6. Decisions (owner, 2026-11-23)

1. **Settle window and handover deadline defaults**: 5 seconds and 30 seconds, bound from configuration; measured on
   a deployed environment during phase 6.
2. **Mixed-version gate (3.9)**: the gate, scoped to one service (its instances share a schema and a map), not a
   minimum release.
3. **Partition count**: fixed at 10,000. Changing it later re-partitions every stream, which is a migration on its
   own.

## 7. Progress (2026-11-24)

Phases 4 (#2556) and 5 (#2557) are built in the wave 11 bundle, and the switch defaults on.

- Fencing: migrations 570 and 572. Claims project each row's epoch in the claim's own statement; one ledger per process
  (`PartitionTenures`) records claimed and fetched work, and the coordinator names the epoch on every completion,
  failure, cursor, publish mark and handler commit. Perspective rows are fenced in the set-based sink (`Fenced`) and the
  per-pair upsert (`PartitionOwnershipLostException`).
- Handover: migration 571 (publish sets `handing_over`, acknowledgement, deadline completion, renewals scoped).
  `PartitionHandoverWorker` hands a partition over once no participant works its streams, after the write-behind models
  there are saved and the queued writes land; a fenced write-behind flush drops the model.
- Graceful leave: the handover worker is a held-work releaser, so a stopping instance hands its partitions over before it
  releases its roles and deregisters.
- The stuck-work rule (3.7, #724) is not needed for ownership safety: in map mode a release returns rows to their owner,
  and rows that keep failing still reach the dead-letter path through the attempt limits. Left for a later liveness change.
- `PartitionAssigner.OwnershipByMap` defaults on, bound from `Whizbang:Database:PartitionAssigner:OwnershipByMap`.
