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

Not changed: `PinnedPool.Enabled` already defaults to false in the framework (the observed inversion came
from a consumer opt-in); `Perspective.MaxConcurrentDrainConsumers` stays 4 (the deadlock is a lock-order
fix, not a concurrency default); `MaxInFlightCommands` stays 50 pending the gate/pool self-check.

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
