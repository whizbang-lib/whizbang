# Inbox acquisition: bounded, deterministic, cost-aware

Status: **138 merged (PR #694)** · **safe-by-default coordinator PR in flight** (cycle 1: defaults + poison casualties; cycle 2: coordinator-owned command timeout) · 139 designed + prototyped · 140 designed

## The problem (diagnosed live on a consumer bulk import)

A bulk import fans thousands of inbox rows per minute into a downstream consumer service. The claim cycle's `claim_orphaned_inbox` `pick` CTE
ranks **every** eligible pending row per stream (`ROW_NUMBER() OVER (PARTITION BY stream_id ORDER BY
received_at)`) on **every** poll, then keeps `p_max_rows`. No index matched the window order, so the
planner ran a Seq Scan of the whole inbox heap (~2.4 KB/row) plus a Sort: O(backlog) per poll.

Measured at a ~90k-row backlog: ~200 MB and ~700 ms per call, ~3 calls/s across variants → most of the
database CPU (`pg_stat_statements`: claim_work + claim_orphaned + pick ≈ 60% of an 8 h window). Everything
behind it starved: `commit_handler_batch` (normally <1 s) took 13–30 s, crossed the client command
timeout, and `BatchFlusher` **dropped the completions** ("items lost") → rows stayed leased →
`LeaseExpired` → re-claim → attempts climbed → `AdaptiveClaimWindow` collapsed to 1–2 rows + the poison
admission gate admitted 1 row/cycle → ~100 rows/min → real rows dead-lettered as `MaxAttemptsExceeded`.

Ruled out along the way: WorkCoordinator strategy (Batch is producer-side only - flipping it stalled
consumers), perspective save granularity (already once per batch), lock convoy as the primary cause
(waiters were 0 while commits ran 13–28 s executing), bloat (2% dead, autovacuum current), the
`AuditEventsComposite` (biggest commit → first to time out; a symptom).

## 138 - covering index + total order (this PR)

* `idx_inbox_pending_stream_order (stream_id, received_at, message_id) INCLUDE (instance_id,
  lease_expiry, scheduled_for, partition_number) WHERE processed_at IS NULL` - the pick becomes an
  Index Only Scan in window order, no Sort. Snapshot of that backlog: 971 vs 14,724 buffers, 272–326 vs 469–633 ms.
  **Live caveat:** on a hot inbox the visibility map is mostly cleared by lease/attempt updates, so the
  scan still fetches the heap for most rows - the time gain (no sort) holds (~1.8×), the I/O gain does
  not survive churn. 139 must not depend on index-only-ness.
* `(received_at, message_id)` total order in the window and the acquisition cut (bulk imports have
  many identical `received_at`; UUIDv7 ids are chronological at the source).
* Tests: `InboxAcquisitionIndexSqlTests` - plan shape (Index Only Scan via the index, no Seq Scan, no
  Sort Key; `enable_seqscan=off` makes it a capability assertion) and tie order (3 rows, same
  `received_at`, inserted largest-id first, bound 2 → ids 1,2). Both RED without 138.
* Retires two ad-hoc experiment indexes that exist only where this was diagnosed (`DROP INDEX IF EXISTS`).

## 139 - algorithmic bound (next)

Prototype `bounded-pick-v3.sql` (scratchpad) reached **1000/1000 parity** with the current function on a
copy of the real backlog, but was **not faster** (383–671 ms): inside a `LIMIT`ed ordered scan the
ownership `EXISTS`/`NOT EXISTS` become correlated index probes per scanned row (44k buffers), whereas the
baseline's planner hashes them. Design that removes the per-row cost:

1. Ownership is a per-**stream** property. Materialize `streams_live_owned_by_others` and
   `streams_owned_by_me` once per call from `wh_active_streams` ⋈ `wh_service_instances` (small,
   indexed) and test membership by hash, never by correlated probe.
2. Hybrid mode on `count(DISTINCT stream_id) LIMIT p_max+1` (index, early stop):
   * many streams (> p_max): scan `received_at` order through a narrow pending index, keep a row iff it
     is the first eligible row of its stream (one covering-index probe), stop at p_max - O(rows scanned
     until p_max heads).
   * few streams: `k = ceil(p_max / streams)` rows per stream via LATERAL - O(streams × k).
3. Keep `candidates … FOR UPDATE SKIP LOCKED` + the volatile re-check exactly as 025.
4. Regression: parity test against the 025 ranking on a seeded mixed backlog (fat streams + many
   singletons + expired leases interleaved) with the total order; plan test = no WindowAgg over the
   full set.

**Prototype v4 (hashed ownership + head probe, `bounded-pick-v4.sql`): 183 ms, 20,000 buffers, 1000/1000
parity on the ~90k-row snapshot** - vs baseline 469–633 ms / 14,724 buffers and v3 383–671 ms / 44,055. The
remaining per-row cost is the head-of-stream index probe (~5.8k probes for 1,000 heads), inherent to
breadth-first order; it is bounded by rows scanned, not by the backlog, and does not depend on the
visibility map. Realistic target: ≤ 200 ms at 100k pending - roughly a tenth of the CPU the current shape burns at the same cadence.
Measure on the snapshot method (`wb_scratch.inbox_snap`, `EXPLAIN (ANALYZE, BUFFERS)`) before touching
the migration.

## 140 - cost-aware AIMD (turnkey batch sizing)

Today `AdaptiveClaimWindow.Observe(claimed, reclaimed)` is AIMD on the **re-claim ratio** only (halve
when churn > 0.5, +25 when zero re-claims) and `AdaptiveOutstandingBudget.Observe(completed, elapsed)` is
throughput-based. Neither sees cost, so after a manual attempts reset (re-claims read 0) the window grew
to its ceiling, leased 7.8k rows, and produced 1.3 GB emit / 650 MB fan-out statements per cycle.

Two more mechanisms, both measured live, belong to 140:

* **Stage coupling through the outstanding budget.** The claim loop counts leased work of every kind as
  outstanding, so when the perspective stage falls behind (its leases reached ~10k, the budget ceiling
  is 10k) inbox acquisition collapses even though the inbox path itself is idle and fast. The two
  stages then oscillate: inbox drains at ~24k/min, perspectives pile up, inbox collapses to a trickle,
  perspectives drain at ~16k/min, inbox resumes. The budget must be per work category (inbox headroom
  from inbox leases only), or the perspective stage must apply back-pressure explicitly.
* **Rows vs streams units mismatch.** `ClaimWorker` converts row headroom to
  `streamsAffordable = ceil(headroom / rowsPerStream)` and passes that stream count as `p_max_streams`,
  which `claim_work` hands to `claim_orphaned_inbox` as its ROW cap. With fat streams (hundreds of rows
  each) that is `max(1, ...)` = one row per cycle, so throughput drops, the budget rate estimate drops,
  and the collapse is a fixed point; the 100-row floor never reaches the mechanism. Acquisition needs its
  own row bound (a `p_max_rows` parameter, dropping the old overload per the migration rules).

Add a latency observation with a target to both controllers and to the flusher batch sizes:

* claim: measure `claim_work` round-trip (new histogram `whizbang.claim.duration`); over target
  (default 100 ms) → multiplicative decrease of the window; under → additive increase (existing rule).
* commit: `whizbang.work_coordinator.flush.duration` already exists → drive `BatchFlusher.MaxBatchSize`
  / `ImmediateFlushThreshold` for the inbox-handler flusher; over target (default 1 s) → halve.
* startup self-check: `PostgresOptions.MaxInFlightCommands` (gate, 50) vs Npgsql `Maximum Pool Size`
  (a consumer ran 20) - warn like `AsbOpsRateSelfCheck`.
* red/green: controller unit tests with injected observations (no timing tests); E2E under a seeded
  backlog asserting the window shrinks when claim latency is injected above target.

### Lease expiry is the over-claim signal (second bulk import, observed)

With the outstanding budget off, the only bound on leased work was the churn-based claim window, and
it grows +25 streams per calm cycle up to `MaxStreamsPerBatch` (1000). Under a fan-out backlog every
service pulled hundreds of streams of rows with payloads per claim: pods reached 2.2-3.0 GiB and three
downstream services were OOM-killed (their restarts stranded leases, which became stamped casualties,
which the poison gate then throttled); the bff held ~15k leased rows while completing ~90 rows/s, so
leases expired in bulk (2.9k, then 9.7k stamped rows) and the drain collapsed to the gate's
one-row-per-cycle forced progress. Capping the window at 100 streams dropped pod memory to 350-900 MiB
and stopped the expiries.

For 140 the controller must treat an expired lease as the strongest over-claim signal: any expiry in a
cycle is a multiplicative decrease of both the claim window and the outstanding cap (target: zero
expiries), and the outstanding cap is derived from measured completion rate times lease length, per
work category, so a service never leases more than it can finish inside the lease. `MaxStreamsPerBatch`
should also bound memory (rows times payload), not just streams, and the framework default of 1000 is
too high for a fan-out backlog. Consumers that pin 1000 in their own configuration need the same change.

### Doorbell state hygiene (observed)

`wh_notify_state` keeps one row per (instance, payload kind) forever: on one service 134 of 140 rows
belonged to instances that no longer exist, and a dashboard reading `max(effective_window_ms)` reported
a 7 s regime that no live pod was in. The live rows were at the 50 ms floor. The maintenance sweep
should purge rows whose instance has been gone longer than the lease, and any regime reading must join
on live instances. Related: a chat start measured 19 s from creation to first turn with the model call
at 3 s; the doorbell was not the cause, and the remaining hop latency (claim poll intervals, perspective
lag, the projection re-fold on a missing row) still needs a per-hop measurement.

## Safe-by-default coordinator (in flight)

What a consumer gets with no configuration must be the safe thing. Changed, each red/green:

* `StreamIntegrityOptions.RepairMode` default `AutoRepairCapped` -> `ReportOnly` (detect and report; repair is
  the opt-in). A default that mutates data unasked is not one a consumer can trust out of the box.
* `ClaimWorkerOptions.AdaptiveOutstandingBudget` default `true` -> `false` until 140 makes it per category
  and row-bound; the churn-based claim window remains the bound.
* `PostgresOptions.CommandTimeoutSeconds` default 5 -> 120 (the Dapper path).
* Poison admission: rows whose `error` carries the acquisition SQL's abandonment stamp ("Attempt N ended
  without a reported outcome ...") are lease casualties, not poison: they neither raise the high-attempt
  share nor get deferred by it (`PoisonAdmissionPolicy.IsLeaseExpiryCasualty`).
* Coordinator-owned command timeout (cycle 2): every raw command the EF coordinator creates composes
  `WithCoordinatorTimeout()` (180 s, the same budget its EF context already had), so a consumer's
  connection-string timeout can no longer cancel a commit batch. 78 creation sites; 4 deliberate explicit
  timeouts (vacuum, maintenance) still override.
* Report-only is bilateral (cycle 3): a `ReportOnly` service takes no part in repair in either direction.
  `RepairTraffic` names the two repair message types; `RedeliveryRequestReceptor` declines requests as an
  origin; `InboxDispatchWorker` completes a `RedeliveryComposite` without fan-out as a consumer (a peer on
  the same topic may have asked for it); `MaintenanceWorker` sweeps parked, unleased repair rows every cycle
  through `IWorkCoordinator.DiscardPendingInboxMessagesAsync` (both drivers; containment match on the
  normalized type name because a stored `message_type` may carry version metadata or an envelope wrapper).
  Detection traffic is never touched. Metric `RepairTrafficDiscarded` (tag `role`). Consequence: healing
  needs the opt-in on both sides.
* A feature that is off leaves nothing behind (cycle 4): `IntegrityTraffic` maps every control-plane
  message to its feature, and the maintenance sweep discards pending inbox and outbox rows of features
  that are off (`IWorkCoordinator.DiscardPendingOutboxMessagesAsync` added, both drivers). Observed:
  a service with checkpoints, audit and report publishing all off held tens of thousands of unpublished
  `PerspectiveCoverageGapDetected` / `IntegrityDivergenceDetected` rows for weeks (unclaimable anyway
  because their partition numbers no longer matched the service's partition count: a separate stuck-row
  case for the sentinel). Never swept: peers' manifest requests, `RebuildPerspectiveCommand`.

Not changed: `PinnedPool.Enabled` already defaults to false in the framework (the observed inversion came
from a consumer opt-in); `Perspective.MaxConcurrentDrainConsumers` stays 4 (the deadlock is a lock-order
fix, not a concurrency default); `MaxInFlightCommands` stays 50 pending the gate/pool self-check.

### Audit singles under a bulk import (observed)

With audit logging on, every event also produces an `EventAudited` single that the tag-bound coalescer
folds into a `sys-audit` composite (`CoalescePolicyOptions`: slide 15 s, `MaxDelaySeconds` 120, batch
500). Under a bulk import the singles pile up (4,141 pending, 812 leased, oldest 13 min) and the folded
composites are the largest commits in the batch (the 13-30 s tail of the original root cause). Rows that
sit leased through a fold window that drifts past the lease churn as casualties. Two follow-ups: the
coalescer must renew the leases it holds (or hold rows claim-invisible without a lease), and the fold
size should be bounded by commit cost, not only by row count.

### Audit ledger streams (decided: one deterministic ledger stream per tenant)

Today every `EventAudited` single is minted on its own fresh stream (`AuditOutboxMessageBuilder`,
`StreamId = auditEvent.Id`) and every folded `sys-audit` composite gets another fresh stream
(`CoalesceShipWorker`, `TrackedGuid.NewMedo()`): a bulk import creates tens of thousands of singleton
streams with no ordering across audit records and full per-stream machinery spent on each. Audit records
are a ledger about the domain event, not part of the domain stream (they are `IsEvent = false`), so they
belong on neither the original stream nor a per-composite stream. Decision (owner, 2026-09-07): one
deterministic ledger stream per tenant, `UUIDv5("sys-audit", tenant)`, stamped on each single at mint and
inherited by the composite that folds it (the group is per tenant, so a fold never mixes ledgers);
`OriginalStreamId` stays a field. The collective sink (`__collective__`) then sees one orderable ledger
per tenant. No time bucket by default; a bucket or a small shard count is an optional policy knob for
bulk phases only. No migration: audit rows are never event-stored. Lands in its own PR after the
hold-and-wait fix (audit builder + coalesce fold + sink routing, red/green).

## Perspective drain hold-and-wait (next PR, red/green)

Mapped from source (file:line in the worktree at the time of writing):

* Lock graph edges (holder waits for): affinity semaphore S(stream, perspective) -> gate
  (`PerspectiveWorker.cs:1029` then `1049`, `1128`, `1160`, `2025`); S -> bounded completion/lease
  channel (`2842`, `BatchFlusher` FullMode.Wait); pinned connection P (Size 1) -> gate
  (`LeaseRenewalWorker.cs:63->94`, `PerspectiveCompletionFlushWorker.cs:67->71`, `ClaimWorker.cs:659->700`);
  completion-channel drain -> P -> gate; gate -> Npgsql pool.
* The cycle: S -> gate -> P -> completion/lease flush -> channel capacity -> S. Demand is
  `MaxConcurrentDrainConsumers` (4) x governor width (`MaxConcurrentPerspectives` 30) = 120 bodies holding
  S while queuing for 50 gate slots; the only worker that completes perspective rows and the lease
  renewer queue behind them on one pinned wire, renewals stop, leases lapse at `LeaseSeconds` 300, the
  claim loop re-offers the same set. Pinned on adds the Size-1 wire and `BatchFlusher.cs:91-100`
  discarding a batch on a borrow timeout (fatal); pinned off leaves S -> gate -> channel -> S (milder).
  One consumer keeps demand under the gate.
* `PostgresOptions.MaxInFlightCommands` has no consumer in `src/`; the gate is hard-coded at 50
  (`WorkerPipelineExtensions.cs:543`). The gate's acquire timeout (30 s) does not throw: it logs and
  returns a no-slot releaser; a timeout of 0 waits forever.
* Fix, in order: (1) no gated coordinator call and no bounded-channel write under S: resolve and load
  before `WaitAsync`, apply and mutate the cursor cache under S, report completion after `Release`
  (the cursor-inversion detector at `1955` already re-validates staleness); (2) pinned-pool borrows skip
  the gate (a borrow already caps concurrency at Size; gating it double-counts); (3) wire
  `MaxInFlightCommands` and clamp consumers x width against it at startup as `InboxDispatchWorker.cs:1059`
  does; (4) `BatchFlusher` re-enqueues a failed batch instead of discarding it (perspective completions
  and commit batches both ride it). RED test: `PerspectiveWorkerTestHarness` + a fake coordinator whose
  gate is a `SemaphoreSlim(1)` and a completion channel whose drain needs the same gate; two work items
  on one (stream, perspective) with two consumers; assert the completion capture stays empty while a
  gate wait is outstanding under S (hook `OnStreamAffinityGateContended`, `1263`). GREEN: the cursor
  completes with the gate at 1 because nothing gated runs under S.

### Load-sensitive test (to make deterministic)

`ClaimWorkerDoorbellLivenessTests.FreshWorkOnEmptyEdge_DoorbellPreceded_NoMissRecordedAsync` timed out at
its 30 s completion-signal cap once in a full Core run on a heavily loaded machine (11k tests in parallel
plus external probes) and passed in isolation immediately after. It waits on real signals (no polling),
so the cap is not the problem; the second claim it waits for depends on the worker's poll back-off
(`PollingMaxIntervalMilliseconds` 10 s) under starvation. Drive the second claim with an explicit
doorbell in the test instead of relying on the poll, so the outcome no longer depends on scheduling.

## Correctness follow-ups (separate PRs)

* A lost commit batch is a correctness bug: `BatchFlusher` must requeue idempotently on flush failure
  (never "items lost"). Test: inject a transient failure → completions still land.
* Raw commands (`CommitHandlerBatchAsync` uses `conn.CreateCommand()`) inherit the connection string's
  timeout, not EF's `SetCommandTimeout(3 min)`; set `cmd.CommandTimeout` from Whizbang options so a
  consumer's connection-string command timeout cannot kill a commit batch.
* Poison admission: distinguish `LeaseExpired`-from-timeout from real poison (maintenance/sentinel keep
  handling true bad rows).
* PerspectiveWorker deadlock (few fat streams): the drain consumer takes the per-(stream, perspective)
  `SemaphoreSlim` (line ~1029) and then makes gated coordinator calls while holding it; other paths hold a
  gate slot while waiting for a stream semaphore. With several consumers on a handful of streams that each
  carry ~1k rows across ~27 projections and a coordinator gate already near its cap, a hold-and-wait cycle
  forms: gate at cap, database idle, one instance holding every lease and RENEWING it, zero applies. The
  stage progressed only at lease-expiry cadence (attempts climbing 2-4) and fully recovered with a single
  consumer (4.2k rows in 31 s). Fix: one lock order (stream semaphore before any gate acquisition, never
  a gate slot held across a semaphore wait), a gate hold-duration watchdog with per-caller metrics exposed
  to operators, and lease renewal that stops for work that is not actually progressing.
* Pinned pool vs gate: flush and claim workers borrow the single pinned connection first and then call the
  coordinator (which takes the gate), while the coordinator takes the gate and then uses the ambient pinned
  connection. Same inversion class; verify and give it the same single order.
* Doorbell self-test under load: a pod whose signal-bus loopback probe misses its 5 s window at startup
  settles on polling fallback for its whole lifetime ("every hop pays the poll interval"). The self-test
  should retry with backoff and re-arm doorbells when the transport recovers.
* Benchmark tests: a `[Category("Benchmark")]` class (seed ~100k pending rows across mixed streams, time
  the acquisition call, assert a budget: 138 < 350 ms, 139 < 100 ms) excluded from the PR shard slices
  by construction (the shard guard accepts Benchmark as the alternative to a Shard category) and run by
  a scheduled/manual workflow job.

## Consumer-side follow-ups (until the framework owns them)

A consumer whose Npgsql connection string sets a command timeout below its worst commit batch will lose
completions (see correctness follow-ups); until `CommitHandlerBatchAsync` sets its own timeout, consumers
should set the connection-string command timeout well above the commit tail (minutes, not 30 s) and size
the Npgsql pool to at least `PostgresOptions.MaxInFlightCommands`. Operational recovery of a stalled
backlog: reset `attempts` on rows carrying the 025 abandonment stamp in `error` ("Attempt N ended without a
reported outcome: lease held by instance … expired") - a framework-written marker, not a handler error, so
they are lease casualties rather than poison - and re-inject dead letters with `recover_dead_letter`. The
poison admission gate counts these toward `HighAttemptThreshold` today; 140 should exclude them.
