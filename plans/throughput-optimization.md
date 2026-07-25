# Throughput optimization — inbox dispatch bottleneck

## Context

A consumer import test on 2026-06-08 16:00 UTC, post-v0.658 deploy, drove the service's inbox queue to 12,204 pending rows. Pipeline drained cleanly (zero stuck rows, zero DLQ entries — v0.657 / v0.658 held). But the **inbox dispatch stage drained at only ~27 rows/sec during the slow phase**, then burst to ~234 rows/sec near the end. Service CPU peaked at 20% of one core (203m on a 4-core pod). The system was throttling at ~10× below its capacity.

Telemetry evidence at the throttle:

- Service connections (4 to the service DB) all idle on `ClientRead`
- Zero active queries on the service DB during the slow phase
- `wh_inbox.error` NULL across all 12k rows
- Continuous `WorkCoordinatorGate.AcquireAsync entered — currentCount=49/50` / `GRANTED — remaining=48/50` logs

Implication: gate slots were held during application-side work (lifecycle hooks, perspective sync wait) that doesn't need the DB. Per-slot hold time ≈ `50 / 27 = 1.85s`, where DB-only ops should be 50-100 ms. The gate was originally meant to bound DB connection draw; holding it through non-DB work means it acts as a global parallelism cap that scales nothing with available CPU.

## Connection budget — non-negotiable framing

Before any of the "bump the cap" tuning, the per-environment connection math has to hold. The current defaults are tuned for a 1-pod-per-service dev topology and start to break when prod runs 6-10 pods per service.

**Production topology** (per `project_whizbang_pgbouncer_topology`):
- pgbouncer sits between pods and Postgres
- Each pod opens ONE direct Postgres connection (for `LISTEN` only); everything else routes through pgbouncer
- pgbouncer pools server-side connections; client-side `MaxPoolSize` is independent of Postgres `max_connections`
- Net: bumping `MaxPoolSize` per pod is **safe in prod** — pgbouncer absorbs the server-side cost

**Dev (direct-Postgres) topology**:
- Pods connect direct to a shared Postgres flexible server
- `max_connections = 429` (verified)
- ~13 services × ~3 environments × ~5 conns at rest ≈ 195 used (verified 85-127 in 7-day window)
- 1 pod per service in dev → bumping per-pod MaxPoolSize multiplies modestly

**Safe-to-bump ceiling (dev)**: with 1 pod per service, `MaxPoolSize = 30` per pod, 13 services = 390 max. Stays under 429. Safe.

**Safe-to-bump ceiling (prod)**: pgbouncer-mediated, so per-pod budget is determined by pgbouncer's pool size and prod can choose independently. Recommendation: per-pod `MaxPoolSize = 50` with pgbouncer pool sized to handle peak concurrent server-side connections (typically a fraction of total client connections).

**This is why the per-env config story matters more than the absolute number.** Every slice below either gates on env-specific defaults or applies uniformly because the limit is application-side, not connection-side.

## Approach

Eight slices. The first three are config-tuning (cheap, immediate effect). Slices 4-6 are the real structural fixes in Whizbang. Slices 7-8 are diagnostics that should land **before** the tuning so we measure right. Slice 9 is the consumer-side composite-event refactor, which is the largest end-to-end win but needs producer-side architecture work and is tracked separately.

### Slice 1 — Connection-budget-aware defaults for `WorkCoordinatorGate.MaxConcurrent`

**Current state**: default = 50, applied uniformly across all deployments.

**Concern user raised**: bumping uniformly to 200 in environments with 6-10 pods per service would risk maxing out the Postgres server connection pool.

**Resolution**: the gate's job is to bound concurrent `IWorkCoordinator` calls, NOT to bound the absolute number of DB connections. Each gate slot holds **at most one** connection from the per-pod Npgsql pool. So the gate's effective ceiling is `min(MaxConcurrent, MaxPoolSize)`. Bumping the gate above the pool just makes it advisory.

The right shape:
- **Default `MaxConcurrent = MaxPoolSize` at construction time.** Resolves to the configured pool ceiling automatically — no separate tuning knob to maintain.
- Keep the explicit override for callers who want to set a different cap.
- Update the gate constructor to accept `IOptions<NpgsqlConfig>` (or the equivalent existing options class) and default `MaxConcurrent` to the pool's `MaxPoolSize` minus a small reserve for non-gated DB work (e.g., 5).

**Why this is safer than "bump to 200"**: pool sizes are already env-specific and tuned per-pod. The gate inherits that scaling automatically.

**Critical files**:
- `src/Whizbang.Core/Messaging/WorkCoordinatorGate.cs` (extend constructor; default sourced from options)
- `src/Whizbang.Core/Configuration/WhizbangCoreOptions.cs` (document the relationship)
- Wherever the gate is registered in DI (likely `Whizbang.Data.EFCore.Postgres/PostgresServiceCollectionExtensions.cs`)

**RED**: `WorkCoordinatorGate_DefaultMaxConcurrent_DerivedFromPoolSizeAsync` — construct with `MaxPoolSize=30`, assert `MaxConcurrent` defaults to 25 (or whatever reserve). Currently it defaults to 50.

**GREEN**: derive default from options.

### Slice 2 — Connection-pool sizing guidance per env

**Current state**: the service's `MaxPoolSize = 10` (verified in pod env). Same default across envs.

**Concern user raised**: scaling pool size linearly with pod count blows Postgres's `max_connections=429` ceiling in dev.

**Resolution**: this is a documentation + config-management problem, not a code problem. Whizbang has no opinion on pool size — it's set by the consumer's connection string.

This slice ships:
- A doc page `operations/configuration/connection-pool-sizing.md` with the per-env formula:
  - Dev with direct PG: `MaxPoolSize ≤ floor((max_connections - reserve) / (services × pods_per_service))`
  - Prod with pgbouncer: per-pod `MaxPoolSize` independent of server cap; pgbouncer pool sizes the server side
- A startup-time SANITY CHECK in `EFCoreServiceCollectionExtensions` that logs Warning if `MaxPoolSize × (configured services × pods) > 80%` of `max_connections` (queried from the server at startup). Opt-in via a `ValidateConnectionBudget` flag.

**Critical files**:
- `src/Whizbang.Data.EFCore.Postgres/EFCoreServiceCollectionExtensions.cs` (sanity check)
- `whizbang-lib.github.io/.../operations/configuration/connection-pool-sizing.md` (new doc)

**RED**: `ConnectionBudgetSanity_OverProvisioned_LogsWarningAsync` — register with `MaxPoolSize=200`, `services=10`, `pods=10`, ServerMaxConnections=200; assert Warning logged with the computed total. Currently silent.

**GREEN**: implement the sanity-check Hosted Service.

### Slice 3 — `InboxDispatchWorker.MaxConcurrentStreams` aligned with the gate

**Current state**: `InboxDispatchWorkerOptions.MaxConcurrentStreams` default 16 (mirrors `OutboxDrainWorkerOptions`).

**Concern user raised**: same scaling concern as slices 1-2.

**Resolution**: same answer as Slice 1 — derive from the gate's effective cap rather than carry an independent magic number. `MaxConcurrentStreams` cannot exceed `MaxConcurrent` of the gate (the gate is upstream of every coordinator call), so capping it to `min(configured, gate.MaxConcurrent)` is correct.

**Critical files**:
- `src/Whizbang.Core/Workers/InboxDispatchWorker.cs` (gate-derived cap at startup)
- `src/Whizbang.Core/Workers/InboxDispatchWorkerOptions.cs` (document the relationship)

**RED**: `InboxDispatchWorker_MaxConcurrentStreams_NeverExceedsGate_Async`.

**GREEN**: clamp at startup, log Warning if config is invalid.

### Slice 4 — Release gate before blocking on perspective completion

**STATUS (2026-06-08): BLOCKED on Slice 7 telemetry. The original premise was invalidated by reading the actual code; see the "Premise invalidated" subsection below before doing any further work on this slice.**

**Original framing (kept for posterity)**: Slices 1-3 raise the cap; this slice removes the in-app stall that makes the cap matter so much.

**Original-state claim**: `InboxDispatchWorker._publishOneAsync` (or its equivalent dispatch path) acquires the `WorkCoordinatorGate` slot via `IWorkCoordinator.<Some Op>`, then invokes the receptor. The receptor's invoker chain calls into `IReceptorInvoker.InvokeAsync` which may pass through `PerspectiveCompletionWaiter` to synchronously wait for the perspective worker to finish processing the event before returning. During that wait, **the gate slot is still held**.

#### Premise invalidated (2026-06-08 audit)

A direct audit of every `_gate.AcquireAsync(...)` call site in both drivers contradicts the original framing:

- **`EFCoreWorkCoordinator.cs`** — 13 gated methods, all the same shape: acquire gate → single `SELECT some_function(...)` SQL call → release gate. Examples: `RecordHeartbeatAsync:182`, `CompletePerspectiveAsync:293`, `ClaimWorkAsync:451`, `CommitHandlerBatchAsync:553`, `FlushCompletionsAsync:320`, `ReportFailuresAsync:627`. No application work inside the gate scope.
- **`DapperWorkCoordinator.cs`** — 13 gated methods, same shape (acquire → single SQL call → release).
- **`EFCoreDeadLetterStore`, `ScopedEFCoreDeadLetterStore`, `EFCoreDeadLetterRecoveryService`, `DapperDeadLetterStore`** — 5 additional gated call sites; all single-SQL-call scope.
- **`InboxDispatchWorker`** — does NOT call `IWorkCoordinator` at all on the dispatch path. The receptor is invoked via `receptorInvoker.InvokeAsync(...)` (line 570) with no gate held. Failures route via `_failureChannel`, results via `_handlerCommitChannel` — both channels, not coordinator calls.
- **`ReceptorInvoker.cs`** — `_invokeReceptorBodyAsync` (line 685) has no gate acquisition. The receptor body just invokes the user's pre-compiled handler delegate.
- **`PerspectiveCompletionWaiter`** — lives in `Whizbang.Testing/Lifecycle/PerspectiveCompletionWaiter.cs`, not production code. It's a test utility, not a production wait.

The perspective-wait path that DOES exist is `Dispatcher._waitForPerspectivesIfNeededAsync` (`Dispatcher.cs:367`), invoked when `DispatchOptions.WaitForPerspectives = true`. It calls `_eventCompletionAwaiter.WaitForEventsAsync(...)` and blocks until perspectives signal complete. But the call sequence is:

1. Receptor handler → `_dispatcher.SendAsync(...)`
2. Dispatcher → `_workCoordinator.StoreOutboxMessagesAsync(...)` (gate briefly held here)
3. Coordinator returns, **gate released**
4. Dispatcher → `_waitForPerspectivesIfNeededAsync(...)` ← the synchronous wait
5. Returns to receptor

The gate is released at step 3, before step 4 starts. **The perspective wait happens with no gate slot held.** There is no "release gate before perspective wait" lever to pull because the gate isn't held during the wait.

#### What the 49-50/50 saturation actually means

The forensic that motivated this slice — "gate held at 49-50/50 with service CPU at 20%" — is real, but the root cause is NOT the pattern Slice 4 targeted. Working hypotheses, in priority order (to be validated by Slice 7's `whizbang.gate.hold_duration_ms` histogram with `caller` tagging once it ships):

1. **`ClaimWorkAsync`** (`EFCoreWorkCoordinator.cs:448`, `DapperWorkCoordinator.cs:343`) — the `claim_work()` RPC scans across categories, does the lease assignment in SQL. Under heavy load with many active streams and large unclaimed backlogs, this single call can run hundreds of ms. With multiple workers concurrently calling it, the gate fills.
2. **`FlushCompletionsAsync`** (`EFCoreWorkCoordinator.cs:317`) and **`CommitHandlerBatchAsync`** (`EFCoreWorkCoordinator.cs:546`) — batched-write coordinator methods whose payload size scales with traffic.
3. **Npgsql connection-pool exhaustion masquerading as gate exhaustion** — `await conn.OpenAsync(...)` happens *inside* the gate scope. If the pool is contended (e.g., pgbouncer in PROD or direct-PG max_connections in DEV), the gate slot is held during the pool wait. From `GateHoldDuration` alone we can't distinguish "long SQL execution" from "long connection-acquire" — but the `caller` tag will at least narrow which method is implicated.

#### What we actually need before reshaping anything

The test environment needs Slice 7's hold-duration histogram running in production for long enough to read the per-caller p50/p95 distribution. That observation tells us:

- Which gated method dominates the hold time (1 vs 2 vs 3 above)
- Whether the slow caller's slowness is from SQL execution or connection acquisition (compare gate hold time against `npgsql_pool_*` counters or `ExecuteScalarAsync` traces if available)
- Whether the structural fix is "split this specific slow call" (rare, surgical), "raise the pool size in this env" (config, not code), or "introduce work-stealing/batching to reduce call rate" (targeted)

Until that data exists, **Slice 4 has no concrete lever to pull**. Any refactor based on the original "gate-held-during-perspective-wait" premise would be a no-op at best, and at worst would destabilize a correctly-scoped concurrency primitive with no measurable benefit.

#### Unblock criteria

Slice 4 reopens when at least one of these is true:

1. The `whizbang.gate.hold_duration_ms` histogram (Slice 7) has been deployed and read in the test environment for a continuous import workload; the per-caller p95 identifies a specific gated method as the hot path AND that method's hold time is dominated by application work (not SQL execution or connection acquisition).
2. New evidence emerges that the current gate scoping is wrong somewhere I missed in this audit. (Possible — large codebase. The audit above covered the 31 known `_gate.AcquireAsync(...)` call sites; if a new one shows up in a refactor, re-evaluate.)
3. The work-coordinator chain grows a "long-running coordinator op" — e.g., a future call that batches across many SQL invocations within a single gated scope. That would be exactly the pattern Slice 4 was designed for.

#### Why this is left in the plan rather than deleted

Two reasons. First, the framing — "don't hold a concurrency primitive across application work" — IS a real architectural rule, even though the current code already follows it. Future refactors could violate it inadvertently; a documented rationale here makes the "why not" of any future regression visible. Second, the forensic raised a real question (why is the gate saturated?) and the answer needs to be telemetry-driven, not premise-driven. Keeping the slice as a placeholder with clear unblock criteria preserves the question.

#### Original design (for reference; do not implement)

**Functional implications** (your question):

What changes:
- Today: inbox-dispatch acquires gate → invokes receptor → receptor awaits perspective completion (1-2 seconds typical, dominated by perspective worker's own scheduling) → releases gate.
- After fix: inbox-dispatch acquires gate → does the DB-bound work (claim, fetch, deserialize) → **releases gate** → invokes receptor → receptor awaits perspective completion → records inbox completion via a fresh gate acquire.

What stays the same:
- **Ordering**: per-stream FIFO preserved (the unstuck row of a stream still processes before the next one in the same stream — that's enforced by the per-stream channel, not by gate-holding).
- **Failure recovery**: if the receptor throws after gate release, the row's lease still expires and `claim_orphaned_inbox` retries on the next tick. No correctness change.
- **Idempotency**: inbox dedup at `IReceptorDedupStore` already prevents double-fire on retry; this slice doesn't open a new race.
- **Lifecycle hooks**: PreInbox/PostInbox stages fire from the same call sites and observe the same envelope state.

What gets faster:
- A gate slot freed during the 1.85s perspective wait can be used by another stream's dispatch immediately. With 50 streams in flight and a 1.85s average wait, the slot is effectively returned ~27× faster.
- Compounds with Slice 3's higher concurrent-streams cap.

What carries new risk:
- **Memory pressure**: each in-flight receptor holds payload + envelope in memory until perspective completes. Today the gate caps in-flight to 50. With this fix, in-flight can grow until `InboxDispatchWorker.MaxConcurrentStreams × MaxPerStream` (16 × 100 = 1600 today; potentially more under Slice 3). Mitigate with an explicit `InboxDispatchWorker.MaxInFlightReceptors` cap (default to old gate cap to be conservative).
- **Perspective backpressure**: if perspective worker can't keep up, in-flight receptors grow until they OOM. Currently masked by the gate. Mitigate same way (in-flight cap).
- **Diagnostic regression**: the existing v0.654 saturation Warning on the gate no longer fires when perspective is the actual bottleneck. Replace with a separate `InboxDispatch.InFlightReceptors` gauge + saturation Warning.

**Design**: the receptor invoker chain needs to surface a "post-DB-work, pre-application-work" callback. Cleanest shape: wrap the receptor body in `using var __ = gate.AcquireAsync(...); /* DB ops */ /* release */ /* application work */`. The `_invokeReceptorBodyAsync` in `ReceptorInvoker.cs` is the right place — split into `_acquireAndDoDbBoundWorkAsync` (gate-held) and `_doApplicationBoundWorkAsync` (gate-released).

**Critical files**:
- `src/Whizbang.Core/Messaging/ReceptorInvoker.cs` — split the body
- `src/Whizbang.Core/Workers/InboxDispatchWorker.cs` — surface the new `MaxInFlightReceptors` knob
- `src/Whizbang.Core/Messaging/WorkCoordinatorGate.cs` — replace the saturation Warning with an inbox-dispatch-side one (or add a separate `InboxInFlight` gauge — see Slice 7)

**RED**: `ReceptorInvoker_PerspectiveSyncWait_GateReleasedBeforeWait_Async`. Register a gate with `MaxConcurrent=1`. Fire receptor A whose perspective handler takes 500ms. Concurrently fire receptor B. Without the fix, B waits 500ms+ for A. With the fix, B's gate acquire grants immediately while A is still in its perspective wait.

**GREEN**: refactor.

### Slice 5 — Parallelize within a fetched batch (preserving per-stream FIFO across batches)

**Current state**: `fetch_inbox_batch` returns up to 100 rows per stream. The dispatcher processes them **serially** (foreach + await).

Per-stream FIFO requires that row N completes before row N+1 starts WHEN THEY'RE IN THE SAME STREAM. But:
- Different streams in the same batch are independent → can fan out in parallel
- Within a stream, the existing serial processing is correct

The current code already serialises per-stream via the channel topology. But within the **per-stream drain task**, even rows that have no causal dependency are still serial. That's an over-conservative correctness guarantee for the common case where the receptor's effect is idempotent or commutative.

**Design** (least invasive):
- Add `InboxDispatchWorker.WithinStreamParallelism` (default 1 to preserve current behavior).
- When > 1, partition the per-stream batch into N parallel pipes; each pipe processes its slice in order. Cross-pipe ordering is undefined within the batch, FIFO preserved across batches.
- Opt-in per consumer because cross-row dependencies within a stream depend on the receptor's semantics; default-off keeps the existing guarantee.

**Critical files**:
- `src/Whizbang.Core/Workers/InboxDispatchWorker.cs`
- `src/Whizbang.Core/Workers/InboxDispatchWorkerOptions.cs`

**RED**: `InboxDispatchWorker_WithinStreamParallelism2_FanOutWithinBatchAsync` — load a batch of 4 rows on one stream, set the receptor to record observed start order, set parallelism=2, assert at least one out-of-order start (proves fan-out) but FIFO preserved across batch boundaries.

**GREEN**: pipe-partition the batch.

### Slice 6 — Coalesce perspective writes per batch (single transaction)

**Current state**: `PerspectiveWorker._upsertPerspectiveRowsAsync` (or equivalent) writes one perspective row per transaction. With 100 events per batch each producing 1-5 perspective rows, that's 100-500 commits per batch.

**Design**:
- Wrap the per-batch upsert loop in a single `using var tx = await context.Database.BeginTransactionAsync(...)`.
- All upserts in the batch commit together.
- On failure: existing retry semantics intact (the batch fails and re-claims; no row written).

**Functional implication**:
- Reduces commit overhead from `O(rows) × 5ms ≈ 500ms` to `O(1) × 5ms = 5ms` per batch.
- Per `feedback_efcore_user_tx_with_retry_strategy`: any `BeginTransactionAsync` in EF-on-Postgres code MUST be wrapped in `context.Database.CreateExecutionStrategy().ExecuteAsync(...)`. This is a hard rule for retry compatibility (`NpgsqlRetryingExecutionStrategy` is in use on the consumer). Slice 6 must honor it.
- Per `feedback_dont_wrap_perspective_upsert_in_explicit_tx`: **CAREFUL** — explicit BEGIN TX + advisory lock on `BaseUpsertStrategy` caused livelocks under slice-17 parallelism. Slice 21 (atomic UPSERT) or slice 19 retry is the safer pattern. This slice must use the atomic-UPSERT path, not a manually-held lock.

**Critical files**:
- `src/Whizbang.Core/Workers/PerspectiveWorker.cs` (or `Whizbang.Perspectives` if separated)
- `src/Whizbang.Data.EFCore.Postgres/<perspective upsert helpers>`

**RED**: `PerspectiveWorker_BatchedUpserts_OneCommitPerBatchAsync` — wire a fake `IDbContextTransaction` that counts `CommitAsync` calls; dispatch a batch of 10 events; assert exactly 1 commit (today: 10).

**GREEN**: wrap in `ExecuteAsync`-backed transaction; atomic UPSERT semantics preserved.

### Slice 7 — `WorkCoordinatorGate.HoldDuration` histogram

**Why this slice lands first**: with slices 1-3 raising caps and Slice 4 restructuring when the gate is held, we need to know WHERE time is spent or we'll be tuning blind.

**Design**:
- Add an OpenTelemetry `Histogram<double>` named `whizbang.gate.hold_duration_ms` on `WorkCoordinatorGate`.
- Bracket the gate-held window in `AcquireAsync` → `Releaser.Dispose`; record duration on dispose.
- Tag with the calling worker name (so operators can filter by `InboxDispatch` vs `OutboxDrain` vs `PerspectiveWorker`).

**Critical files**:
- `src/Whizbang.Core/Messaging/WorkCoordinatorGate.cs`
- `src/Whizbang.Core/Observability/WorkCoordinatorMetrics.cs` (add the histogram)

**RED**: `WorkCoordinatorGate_HoldDuration_HistogramRecordedOnDisposeAsync` — capture metric, acquire, await 100ms, dispose; assert one histogram observation ≥ 100ms.

**GREEN**: add the histogram + tagging.

### Slice 8 — `InboxDispatch.MessageType` histogram

**Why**: surfaces which event types dominate dispatch time. The import showed one child event type (`RecordLineItemAddedEvent`) was 2,704 rows — if those each take 2s vs 50ms, the operator wants to know.

**Design**:
- Histogram `whizbang.inbox.dispatch_duration_ms` tagged with `message_type` (short form — last segment of AQN to keep cardinality bounded).

**Critical files**:
- `src/Whizbang.Core/Workers/InboxDispatchWorker.cs`
- `src/Whizbang.Core/Observability/InboxMetrics.cs` (new or extend existing)

**RED**: `InboxDispatchWorker_DispatchDurationHistogram_TagsByMessageTypeAsync`.

**GREEN**: add the histogram.

## Verification

After all 8 slices land, on the test environment's service, re-run the consumer import test (~5000 rows per record):

```
-- Expected outcomes
-- 1. Gate hold duration histogram: p95 < 200 ms (today: ~1800 ms)
SELECT histogram_p95('whizbang.gate.hold_duration_ms') FROM otel WHERE service='<service>';

-- 2. Inbox drain rate ≥ 100 rows/sec sustained (today: 27 rows/sec)
-- 3. No stuck rows (already proven by v0.658, regression-lock)
-- 4. No DLQ entries from the import (already proven)
-- 5. Service CPU peak: 60-80% of one core (today: 20% — actually using the headroom)
```

Per `feedback_tdd_no_exceptions` — strict TDD on all eight slices. Per `feedback_tdd_coverage_docs` — 100% line + branch on new types (`HoldDuration` histogram emission, in-flight gauge, derived defaults). AOT compatible (LoggerMessage source generators; no reflection in hot path).

`pwsh scripts/Run-Tests.ps1 -Mode AiUnit` clean; `-Mode AiIntegrations` clean.

## Standards

Per `feedback_tdd_no_exceptions` + `feedback_red_before_green`: RED commit per behavioural slice, GREEN commit immediately after. No timing tests per `feedback_no_timing_tests` — use `TaskCompletionSource` + fake `TimeProvider` for any async signaling.

Per `feedback_lock_invariants_in_tests`: every new option (Gate's auto-derived default, InboxDispatch's in-flight cap, WithinStreamParallelism default) gets a regression test pinning the invariant; otherwise the next refactor silently regresses them.

Per `feedback_tdd_coverage_docs`: ALL slices — 100% coverage, AOT compatible, update docs repo, link docs/tests/code via `<docs>` and `<tests>` tags.

Per `feedback_use_trackedguid`: any new GUID generation in tests uses `TrackedGuid.NewMedo()`.

Per `feedback_no_parallel_builds`: agents (if used) MUST NOT run builds/format/tests; do that once after all agents complete.

Per `feedback_no_public_api_renames_for_sonar`: no renames of public types for SonarCloud — suppress if needed.

Per `feedback_show_full_namespace`: don't strip namespace segments when humanizing — full AQN in diagnostic surfaces.

## Out of scope (with rationale)

- **`perform_maintenance` not clearing dead `wh_active_streams` rows.** A baseline showed 56,363 rows older than 7 days (75% of 75,037). Separate from throughput; tracked as Task #254 for a follow-up PR.

- **Replacing the gate with per-resource semaphores.** Architecturally cleaner (separate semaphores for "DB connections" vs "perspective dispatch" vs "outbox publish") but a much bigger refactor that breaks the existing observability surface. Slice 4 buys 80% of the win by refactoring HOW the gate is held; full decomposition deferred until measured evidence shows the gate-with-segregated-hold isn't enough.

- **Dynamic / adaptive concurrency control (AIMD).** Tracked in memory `project_parallelism_auto_tuning` as future work. Slices 1-3 give operators well-tuned static defaults first; auto-tuning can layer on top once we have the histograms from slices 7-8 to drive it.

- **Connection pinning per worker.** Per `project_whizbang_pgbouncer_topology`: production uses pgbouncer with no worker pinning. Deliberately preserved.

## Sequencing

| Slice | Status | Effort | Depends on | Notes |
|---|---|---|---|---|
| 7 — Gate hold-duration histogram | DONE (v0.660) | ~2 h | — | Land first so subsequent tuning is measured |
| 8 — Inbox dispatch duration by message type | DONE (v0.660) | ~2 h | — | Same: instrument before tuning |
| 1 — Gate default derived from pool | DONE (v0.660) | ~1 h | 7 (for measurement) | Cheap config refactor — landed as `WorkCoordinatorGate.FromPoolSize` |
| 3 — InboxDispatch concurrency clamp | DONE (v0.660) | ~1 h | 1 | Trivial follow-on — `ClampPartitionCount` |
| 2 — Connection-budget sanity check | TODO | ~2 h | — | Independent; safety net |
| 4 — Release gate before perspective wait | **BLOCKED on Slice 7 telemetry** | ~half day | 7 (telemetry-derived evidence) | Original premise invalidated 2026-06-08; see slice body for audit findings + unblock criteria |
| 5 — Within-stream parallelism (opt-in) | TODO | ~3 h | Was 4 — now unblocked (Slice 4 stalled) | Layered on top of restructured holds |
| 6 — Batched perspective writes | **NEXT** | ~3 h | — | Independent; uses ExecuteAsync per the consumer's retry-strategy rule |

Remaining: Slice 6 first (independent, measurable throughput win), then Slice 2 (safety net), then Slice 5 (within-stream parallelism — note: was originally blocked on Slice 4 in this plan; with Slice 4 stalled on telemetry, Slice 5's gains stand on their own and are no longer transitively blocked). Slice 4 reopens when production hold-duration histograms surface a specific caller to target.

## Next plan: Composite events (consumer + Whizbang)

**This plan's logical successor.** Throughput optimization above raises the cap and removes the in-app stall on the *consumer*. The next plan reshapes the *workload itself* on the *producer* side. Both wins are multiplicative — Slice 6's batched perspective writes per inbox row only matter so much when each inbox row carries one event; coupled with composite events carrying ~5,000 inner events, the per-batch commit collapses 5,000 → 1.

### Why this is the next plan, not part of this one

The Whizbang-side contract change (introducing `ICompositeEvent`, expanding at dispatch, recording inner events in `wh_event_store`) is tractable inside Whizbang. But the win only materializes when producers adopt it. The consumer's record-import pipeline is the obvious pilot — and getting that adoption right needs:

- Producer-side architecture work (which event types become composites, which stay granular)
- Event-versioning planning (composites land in the event store with stable schema; future replay must still work)
- A consumer ↔ Whizbang interface design discussion (the contract is small but the consumer expectations around failure / ordering / replay are not)

This plan stays in the Whizbang perimeter. The next plan crosses into consumer coordination — so it gets its own design discussion, slicing, and review cycle.

### The end-to-end win

| Phase | Today (per record) | After this plan's Slice 6 | After next plan (composite) |
|---|---|---|---|
| Outbox rows | ~5,000 | ~5,000 | **1** |
| RabbitMQ messages | ~5,000 | ~5,000 | **1** |
| Inbox rows | ~5,000 | ~5,000 | **1** |
| Receptor invocations | ~5,000 | ~5,000 | ~5,000 (expanded at consumer — preserves per-event semantics) |
| Perspective_events rows | ~5,000 | ~5,000 | ~5,000 (expanded) |
| Perspective transactions | ~5,000 | ~50 (batched per fetch_inbox_batch) | **1** (one batch insert from one composite) |

The receptor invocation count stays at 5,000 by design — that's the irreducible state-change work. Everything upstream (outbox / transport / inbox claim/lease overhead) collapses to a single envelope.

### Recommended design — Option A: composite envelope, expand at consumer

The composite is a wire-level / storage optimization. The receptor model on the consumer side is unchanged.

- Receptors keep subscribing to inner event types (`RecordLineItemAddedEvent`, etc.) — they don't know they came from a composite.
- The event store still records inner events one row each — replay semantics identical to today.
- Lifecycle hooks (`PreInboxInline`, `PostInboxInline`, `PrePerspectiveInline`, etc.) fire once per inner event, just like today. Observability is unchanged.
- Only the outbox row, transport message, and inbox row collapse to one envelope each.

This isolates the optimization to producer ergonomics + a small dispatcher change. No receptor migration. No event-store backfill.

### Concrete `ICompositeEvent` contract sketch

```csharp
namespace Whizbang.Core.Messaging;

/// <summary>
/// Marker for events that carry inline child events. The receptor pipeline
/// expands a composite at inbox dispatch and at perspective_events projection,
/// invoking the registered receptors / perspectives for each inner event with
/// the SAME envelope hops and metadata as the outer composite. The composite
/// itself is recorded ONCE in wh_outbox / transport / wh_inbox; the inner
/// events appear individually downstream.
/// </summary>
public interface ICompositeEvent {
  /// <summary>Inner events, in causal order. Receptor invocation order matches.</summary>
  IReadOnlyList<IEvent> InnerEvents { get; }
}
```

Whizbang-side surgery (sketched for the next plan's slicing):

- **Storage** (`store_outbox_messages` migration): when payload implements `ICompositeEvent`, write ONE `wh_outbox` row carrying the composite as payload. Also emit N rows into `wh_event_store` (one per inner) so event-sourcing semantics are preserved for replay/audit.
- **Inbox dispatch** (`InboxDispatchWorker`): when a row's payload is a composite, iterate `InnerEvents`, invoke receptors per inner with the composite's envelope hops attached. Per-inner lifecycle hooks fire as today.
- **Perspective expansion** (`PerspectiveWorker` or storage path): when an inbox row is a composite, create N `wh_perspective_events` rows from inner events in a SINGLE INSERT — pairs naturally with this plan's Slice 6's batched-commit transaction.

### Producer-side ergonomics sketch (consumer)

```csharp
// Today (5,000 separate publishes — what we measured in the import test)
foreach (var i in lineItems) await dispatcher.PublishAsync(new RecordLineItemAddedEvent(i));
foreach (var n in notes)     await dispatcher.PublishAsync(new RecordNoteAddedEvent(n));
// ... etc

// After the next plan (one publish)
await dispatcher.PublishAsync(new RecordCreatedComposite {
  LineItems   = lineItems.ToImmutableArray(),
  Notes       = notes.ToImmutableArray(),
  Attachments = attachments.ToImmutableArray(),
  // ...
});
```

The composite class:

```csharp
public sealed record RecordCreatedComposite : IEvent, ICompositeEvent {
  public ImmutableArray<RecordLineItemAddedEvent> LineItems { get; init; }
  public ImmutableArray<RecordNoteAddedEvent> Notes { get; init; }
  public ImmutableArray<RecordAttachmentAddedEvent> Attachments { get; init; }
  // ...

  IReadOnlyList<IEvent> ICompositeEvent.InnerEvents => [
    ..LineItems,
    ..Notes,
    ..Attachments,
    // ...
  ];
}
```

### Open design questions to resolve before slicing the next plan

These need user input and a consumer architecture review before the next plan can lock its slices:

1. **Atomicity of failure.** If 4,999 of 5,000 inner events apply successfully and one throws, what should Whizbang do?
   - (a) Composite-level all-or-nothing — first failure aborts; whole composite re-claims (clean rollback; one bad row blocks 4,999 good ones)
   - (b) Per-inner retry — record success of the 4,999; retry only the failed one (preserves progress; composite has partial state)
   - (c) Configurable per composite type
   - Default depends on the consumer's current failure semantics (what does the consumer do today when one of 5,000 events fails?). Likely (a) for simplicity, (c) eventually.
2. **Inner-event ordering / stream_id.** Inner events inherit the composite's stream_id (serial within composite — current per-stream FIFO contract), or keep their producer-supplied stream_id (parallelism across inner events within one composite)?
   - Lean: inherit composite's stream_id by default. Trying to preserve per-inner stream_ids makes the composite-vs-single comparison harder to reason about.
3. **Composite size limit.** `MaxInnerEventsPerComposite` (default 1000?) enforced at storage time to catch accidental "load all 100k events into one composite" mistakes early.
4. **Event-store granularity on replay.** Replay always uses expanded inner events; composite is wire-only. (Recommended; calls out the alternative.)
5. **Producer migration sequence.**
   1. Whizbang library lands the `ICompositeEvent` contract + consumer support
   2. The consumer adopts composite for ONE producer (record import) as a pilot
   3. Measure the actual end-to-end improvement against the baseline captured in this plan's context
   4. Migrate other producers based on the data

When these are resolved, the next plan can be sliced with confidence.

## References

- Branch: `release/v0.660.0-alpha.1` off latest `develop`
- Import evidence: service CPU peaked 203m, inbox peaked 12,204, drain rate 27→234/sec, gate at 49-50/50 throughout
- Whizbang options reference: `Whizbang.Core.Configuration.WhizbangCoreOptions`, `Whizbang.Core.Workers.InboxDispatchWorkerOptions`, `Whizbang.Core.Workers.OutboxDrainWorkerOptions`
- v0.654 gate hardening: `Whizbang.Core.Messaging.WorkCoordinatorGate.AcquireAsync` (the deadline behavior we're preserving)
- Related memories: `project_whizbang_pgbouncer_topology`, `project_parallelism_auto_tuning`, `feedback_efcore_user_tx_with_retry_strategy`, `feedback_dont_wrap_perspective_upsert_in_explicit_tx`
