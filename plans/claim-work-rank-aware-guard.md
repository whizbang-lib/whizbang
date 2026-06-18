# Plan: Rank-aware per-queue guards in `claim_work` (v0.688)

> **Status (2026-06-13)**: First hot-swap attempt on slot-3 was rolled back
> after finding a saga-completion regression. The DB-load reduction was
> confirmed real (`claim_orphaned_inbox` calls -36%, ms -40%) but on 2 of 3
> matched runs the BulkJobImport saga finished with 348-349/350 instead of
> 350/350. v0.687 hit 350/350 on all 3 matched runs. The miss is NOT noise
> at that ratio. See "Slot-3 findings" section at the bottom for the full
> data and the proposed pre-requisite fix.



## Observation (slot-3, 2026-06-13, 3-BFF, post-v0.687 import)

After `pg_stat_statements_reset()` and a 350-job bulk import on slot-3 with
3 BFF replicas + 2 Job replicas, the BFF DB query mix:

| Bucket | % | ms_total | calls | ms_mean |
|---|---|---|---|---|
| `claim_work` (lease claim) | 39.1% | 362s | 5,933 | 61ms |
| `_emit_event_store_chain_for_inbox` (nested in claim_work) | 4.4% | 140s | 4,790 | 29ms |
| `store_inbox_messages` | 12.7% | 118s | 17,716 | 6.7ms |
| **`claim_orphaned_inbox` (nested in claim_work)** | **10.6%** | **98s** | **5,290** | **9.6ms** |
| `get_stream_events` (nested) | 9.2% | 85s | 8,769 | 9.7ms |
| `claim_orphaned_outbox` (nested) | — | — | similar | similar |

The `claim_orphaned_*` rows are double-counted with `claim_work` because
`pg_stat_statements track=all` records nested SQL. The exclusive cost of
the orphan-claim sub-functions is the meaningful number: **~98s on
`claim_orphaned_inbox` alone, spread across 5,290 invocations** during a
~9-minute window. At ~10 calls/sec cluster-wide, the orphan walkers are
firing as often as the active claim path.

### Why so many orphan-claim calls?

`claim_work` (mig 029) has a v0.683 per-queue guard:

```sql
-- Claim orphaned / unowned inbox work — same predicate shape.
IF EXISTS (
  SELECT 1 FROM __SCHEMA__.wh_inbox
  WHERE processed_at IS NULL
    AND (instance_id IS NULL OR lease_expiry < v_now)
  LIMIT 1
) THEN
  PERFORM __SCHEMA__.claim_orphaned_inbox(
    p_instance_id, v_rank, v_count, v_lease_expiry, v_now, p_partition_count, v_stale_cutoff
  );
END IF;
```

The guard fires when *any* unowned-or-expired-lease inbox row exists
anywhere in the table. But `claim_orphaned_inbox` (mig 025) only actually
claims rows assignable to THIS instance — either via the owner-path
(`wh_active_streams.assigned_instance_id = p_instance_id`) or the
unowned-path (`partition_number % p_active_instance_count = p_instance_rank`).

In a multi-instance cluster, the guard's "any unowned row?" predicate
returns true *very* often, but a large fraction of those rows belong to
other ranks. `claim_orphaned_inbox` walks them and returns empty.
**Measured: 5,290 calls × 9.6ms mean ≈ 51s of no-op walks on BFF.**

Confirmed not contention — the mean is steady (consistent with one instance
walking rows). The waste is the call rate, not per-call cost.

## Root cause

The v0.683 guards were correct for ensuring "don't scan when queue is
truly empty," but they don't reflect the rank-modulo + ownership filter
that the inner function itself applies. The guard's predicate is a strict
subset of the function's predicate, so the function frequently returns
empty.

A predicate-aligned guard — one that mirrors the function's actual
eligibility check — eliminates the no-op invocations entirely.

## Design — rank/ownership-aware guards

Push the function's own eligibility shape into the guard. The guard
becomes a *strict superset of zero* of the function's claim set — when
the guard returns true, the function is guaranteed to find at least one
row to claim.

For `claim_orphaned_inbox` (mirrors mig 025's WHERE clause):

```sql
IF EXISTS (
  SELECT 1 FROM __SCHEMA__.wh_inbox i
  WHERE i.processed_at IS NULL
    AND (i.instance_id IS NULL OR i.lease_expiry < v_now)
    AND (i.scheduled_for IS NULL OR i.scheduled_for <= v_now)
    AND (
      -- OWNER PATH: this instance already owns the stream
      EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_active_streams ast
        WHERE ast.stream_id = i.stream_id
          AND ast.assigned_instance_id = p_instance_id
          AND ast.lease_expiry > v_now
      )
      -- UNOWNED PATH: partition modulo matches OR no partition
      OR (
        i.partition_number IS NULL
        OR (i.partition_number % v_count) = v_rank
      )
    )
  LIMIT 1
) THEN
  PERFORM __SCHEMA__.claim_orphaned_inbox(
    p_instance_id, v_rank, v_count, v_lease_expiry, v_now, p_partition_count, v_stale_cutoff
  );
END IF;
```

The guard does NOT replicate the **NOT EXISTS live-peer-owner** check
from the function body — that requires a `pg_stat_activity` scan which
is too expensive for a guard. Net effect: guard may still fire for rows
that have a live peer owner (the function then returns empty for those
specific rows). Acceptable false-positive rate; the dominant case
(modulo-mismatch unowned rows) is eliminated.

Symmetric guards for the other three orphan claimers:

| Guard | Rank predicate | Ownership predicate |
|---|---|---|
| outbox | `partition_number IS NULL OR (% = rank)` | `wh_active_streams.assigned = p_instance_id` |
| inbox | same | same |
| perspective_events | same | same |
| receptor_work | `partition_number IS NULL OR (% = rank)` | (mig 028 — confirm) |

Each guard uses the existing partial index
`idx_{outbox,inbox,perspective_events}_unprocessed_claiming WHERE
processed_at IS NULL`. The additional predicates add a modulo + occasional
join probe; expected sub-millisecond.

## Single-PR scope

### Slice 1 — RED: predicate-aligned guard test

**Test file**: `tests/Whizbang.Data.EFCore.Postgres.Tests/Workers/ClaimWorkRankAwareGuardTests.cs`

Single integration test against a real Postgres container, parameterized
across the four queue tables. Setup:

- 3 simulated instances registered in `wh_service_instances` (all heartbeats fresh).
- Compute rank/count via `calculate_instance_rank`.
- Insert 30 unowned `wh_inbox` rows with `partition_number = 0..29`. Only
  rows where `% 3 = my_rank` are claimable by this instance.
- Insert 10 owned-by-peer rows with fresh leases — should be filtered
  out by the ownership branch.
- Insert 5 owned-by-me rows already in `wh_active_streams` — must be
  matched by the owner path regardless of modulo.

Assertions:
1. With the **current guard** (predicate-loose), calling `claim_work`
   from rank-0 invokes `claim_orphaned_inbox` and the function returns
   ≤10 rows (only the modulo-matched + owned-by-me ones).
2. With the **new guard** (predicate-aligned), calling `claim_work` from
   a rank with zero eligible rows (e.g. insert only `partition_number=0`
   rows, claim from rank=2) MUST NOT invoke `claim_orphaned_inbox` at all.

Verification mechanism: a `wh_orphan_claim_log` audit table created in
the test fixture, populated by a temporary `CREATE OR REPLACE FUNCTION
claim_orphaned_inbox` wrapper that records every invocation. Stash the
production function, swap in the audit wrapper for the test, restore on
teardown. (Pattern matches the migration-replay test fixture in
`tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveCursorAdvanceTests.cs`.)

Test cases — one per queue:
- `ClaimWork_NoEligibleInboxRowsForThisRank_DoesNotInvokeClaimOrphanedInboxAsync`
- `ClaimWork_NoEligibleOutboxRowsForThisRank_DoesNotInvokeClaimOrphanedOutboxAsync`
- `ClaimWork_NoEligiblePerspectiveEventsForThisRank_DoesNotInvokeClaimOrphanedPerspectiveEventsAsync`
- `ClaimWork_NoEligibleReceptorWorkForThisRank_DoesNotInvokeClaimOrphanedReceptorWorkAsync`
- `ClaimWork_OwnedByMeStreamWithModuloMismatch_StillInvokesClaimOrphanedInboxAsync`
  (regression lock — owner path must still let stuck-stream owners reclaim
  rows whose partition routes to a different rank.)

Run on the current code: all five RED tests must FAIL because the loose
guard invokes the function regardless. Commit as a RED commit per
`feedback_red_before_green.md`.

### Slice 2 — GREEN: rank-aware guards in mig 029

Edit `src/Whizbang.Data.Postgres/Migrations/029_ProcessWorkBatch.sql`
in place (pre-v1, per `project_pre_v1_migrations.md`). Replace the four
v0.683 guards with the predicate-aligned shape shown above.

**Schema-qualify every table reference inside the function body** per
`feedback_schema_qualify_in_function_bodies.md` — including the new
`wh_active_streams` references. Mig 029 already uses `__SCHEMA__.` prefix
on every internal reference; preserve that.

Commit. RED tests now PASS.

### Slice 3 — Coverage: edge cases

Same test file, additional cases — these can PASS from the start
(behavior-locking, not RED-first since they exercise the GREEN
implementation directly):

- `ClaimWork_AllRowsHaveNullPartitionNumber_InvokesAllOrphanClaimersAsync`
  (null partition is claimable by any rank).
- `ClaimWork_ScheduledForFuture_DoesNotInvokeClaimerAsync`
  (`scheduled_for > v_now` filters out future rows from the guard, matching
  the function body's `scheduled_for <= v_now` check).
- `ClaimWork_PartitionNumberMatchesMyRankButLivePeerOwns_StillInvokesClaimerAsync`
  (documented false-positive: live-peer ownership is not checked in the
  guard — function returns empty for that row. Asserts the false-positive
  is bounded to "live peer holds the stream lease," not "any other instance
  exists.")
- `CalculateInstanceRank_StaleInstance_ReducesCountInGuardEvaluationAsync`
  (lock the guard's reaction to cluster topology change — stale instance
  shouldn't keep its rank claimed by the modulo.)

### Slice 4 — Live experiment on slot-3 (hot-swap, no release)

**The function body is purely SQL — the C# coordinator calls
`claim_work()` and is opaque to the inner predicate shape.** A v0.688
release isn't required to measure the impact; hot-swap is safe.

Sequence (matches `feedback_sql_experiment_in_isolation.md` — the
"live-traffic deploy is confirmation" caveat applies):

1. **Baseline** (already captured 2026-06-13 ~08:11–08:18, see
   `plans/notes-track-3-execution.md` if archived). 5,290
   `claim_orphaned_inbox` calls / 98s on BFF DB.

2. **Apply** the new `claim_work` via `psql` against the slot-3 BFF DB
   (`jdx_bff_service_db_slot3`) and the slot-3 Job DB
   (`jdx_job_service_db_slot3`) using `CREATE OR REPLACE FUNCTION` —
   atomic swap, no restart needed. Save the pre-swap function definition
   to `/tmp/claim_work_v0.687_backup.sql` first for rollback.

3. **Reset stats**: `SELECT pg_stat_statements_reset()` on both DBs.

4. **Re-run** the 350-job baseline import (same payload as prior run —
   `healthcare-350.compcode-patched.json`).

5. **Capture** the same pg_stat_statements query used today. Compare:
   - `claim_orphaned_inbox` call count: expect <500 (10× reduction).
   - `claim_orphaned_inbox` ms_total: expect <10s (10× reduction).
   - `claim_work` ms_mean: expect drop from 61ms toward ~40ms (the inner
     orphan-claim walk was contributing ~20ms exclusive of guard work).
   - End-to-end saga time: small improvement expected (~5-15s), not
     dramatic — BFF was no longer the bottleneck after v0.687, so this
     is a tax on a non-bottleneck path. The win is **less DB load, not
     faster saga**.

6. **If results match expectations**: keep the hot-swap in place,
   commit the migration change to the Whizbang repo, cut v0.688 release
   that ships the same migration as the in-place fix. Slot-3 doesn't
   need re-deploy.

7. **If results don't match** (or unexpected regression): rollback via
   `psql -f /tmp/claim_work_v0.687_backup.sql`. Diagnose. Iterate.

Risk in hot-swap: very low — function returns the same shape, same
semantics, just fires the inner PERFORM less often. No schema changes.
No index changes. Any pending claim_work invocation completes the old
body and the next one picks up the new body atomically. PgBouncer
session-level state is unaffected (no GUCs touched).

### Slice 5 — Docs

- Update `whizbang-lib.github.io/src/assets/docs/v1.0.0/fundamentals/workers/claim-work-internals.md`
  (or create if absent) — document the v0.688 guard shape, the modulo +
  ownership predicate, and the documented false-positive on live-peer
  ownership.
- Update the comment block above each guard in mig 029 to point at the
  v0.688 rationale (in-place comment, not a new comment block — keep
  the v0.683 historical context intact).
- Add `<docs>` and `<tests>` XML tags to any newly-public type (none
  expected for a SQL-only change).
- Regenerate `code-docs-map.json` if any C# was touched (no for this PR).

### Slice 6 — PR

- Branch: `feat/claim-work-rank-aware-guard` from `develop`.
- Format: `dotnet format --verify-no-changes` clean (no C# touched but
  run anyway).
- Local tests: `pwsh scripts/Run-Tests.ps1 -Mode AiIntegrations -ProjectFilter
  "Whizbang.Data.EFCore.Postgres.Tests"` clean.
- PR to `develop` per `feedback_pr_workflow_for_team_repos.md`. CI
  watch per `feedback_always_monitor_pr_ci.md`.
- Tag commits "RED: …" and "GREEN: …" per slice for revertability.

## Out of scope (with reasons)

- **Per-queue micro-tuning of the modulo evaluation** (e.g. caching the
  rank/count for a few seconds). Reason: rank/count is already computed
  once per `claim_work` call from `calculate_instance_rank`; caching
  beyond the function scope would introduce staleness risk during
  cluster rebalancing, for a sub-ms savings.
- **Touching `claim_orphaned_inbox` itself**. Reason: the function body
  is correct — the dual OWNER/UNOWNED path is necessary for rank-churn
  recovery. The fix is at the call-site, not the callee. Editing the
  function would risk regressing the wedge-recovery behavior covered by
  existing tests.
- **Adding a separate `is_claimable_by_rank` SQL function**. Reason:
  adds a function call to every claim_work invocation for code factoring
  with no measurable benefit. Inline guard is plenty.
- **Bulk-import event amplification fix (Option A)**. Tracked separately
  in `JDNext/plans/bulk-import-event-granularity.md`. Different repo,
  different release cycle, different scope.

## Risk

- **Index sufficiency**: The new guard predicate uses `wh_inbox.partition_number`
  and joins `wh_active_streams.assigned_instance_id`. Both columns are
  indexed today (partition_number is part of the partial unprocessed-
  claiming index; assigned_instance_id is the wh_active_streams PK/index
  per mig 022). No new index needed.
- **False positives from live-peer ownership**: documented and bounded —
  guard fires for rows where modulo matches but a live peer owns. Function
  returns empty for those rows; remaining rows in the same call may still
  be claimed. No correctness risk; small efficiency tax.
- **Concurrent rank changes**: `v_rank`/`v_count` are computed inside the
  same call as the guard, so the guard always sees a consistent snapshot
  per call. Cross-call drift is acceptable (next call recomputes).
- **Pre-v1 migration mutation**: Mig 029 is touched in place per
  `project_pre_v1_migrations.md`. No downstream consumer should re-run
  old migrations; the test fixture re-runs migrations from scratch so
  the test fully exercises the new version.

## Verification checklist

- [ ] Slice 1: 5 RED tests committed, all FAILING on develop.
- [ ] Slice 2: mig 029 updated, 5 tests now PASSING.
- [ ] Slice 3: 4 additional edge-case tests, all PASSING.
- [ ] Slice 4: slot-3 hot-swap applied; pg_stat_statements snapshot
      confirms ≥10× reduction in `claim_orphaned_inbox` calls + ms_total.
- [ ] Slice 5: docs page added/updated, regenerated maps clean.
- [ ] Slice 6: PR opened to `develop`, CI green, merged.

## Acceptance criteria

After deploy-or-hot-swap on slot-3, a repeated 350-job baseline import
produces:

- `claim_orphaned_inbox` calls: **<500** (was 5,290).
- `claim_orphaned_inbox` ms_total: **<10s** (was 98s).
- `claim_orphaned_outbox` similarly reduced.
- `claim_work` ms_mean: **<50ms** (was 61ms).
- No regression in DLQ count (still 0 net for healthy imports).
- Saga completion at 350/350 (already locked by v0.687, must remain).
- No new flakiness in EFCore.Postgres integration tests.

## Slot-3 findings — 2026-06-13 hot-swap attempt (ROLLED BACK)

### What we learned

**The DB-load reduction is real and measurable.** On a matched cluster
(3 BFFs + 3 Job pods, HPA pinned to minReplicas=3, slot-3 dev), three
v0.687 baseline runs and three v0.688 hot-swap runs of the 350-job
BulkJobImport file produced:

| Run | Version | Cluster | Saga result | Duration |
|---|---|---|---|---|
| #1 | v0.687 | 3 BFFs (fresh) | 350/350 | 2m44s |
| #2 | v0.687 | HPA-thrashing 3↔2 | 350/350 | 3m13s |
| #3 | v0.687 | sterile 3/3 | 350/350 | 3m05s |
| #4 | **v0.688** | 3 BFFs (fresh) | **349/350** (line 254 missing) | 3m03s |
| #5 | **v0.688** | sterile 3/3 | 350/350 | 3m21s |
| #6 | **v0.688** | sterile 3/3 | **348/350** (lines 225, 309 missing) | 2m56s |

v0.687 = 3 wins. v0.688 = 1 win, 2 misses. At 2-of-3 miss rate on a
matched cluster, this is **not run-to-run variance**. The guard change
introduced a real saga-completion regression.

Stat comparison (sterile v0.687 vs sterile v0.688, BFF DB):

| Bucket | v0.687 | v0.688 | Δ |
|---|---|---|---|
| `claim_orphaned_inbox` calls | 5,213 | **3,337** | **−36%** ✓ |
| `claim_orphaned_inbox` ms_total | 125s | **75s** | **−40%** ✓ |
| `claim_orphaned_outbox` calls | 5,213 | **3,337** | **−36%** ✓ |
| `claim_orphaned_outbox` ms_total | 35s | **17s** | **−51%** ✓ |
| `claim_work` ms_total | 398s | 399s | 0% |
| `_emit_event_store_chain_for_inbox` ms | 142s | 150s | +6% |
| `get_stream_events` ms | 75s | **106s** | **+41%** ⚠ |
| Saga `version` (state-update count) | 107 | 90 / 113 / 98 | mixed |

The 40-51% reduction in orphan-claim load matched the design prediction.
**But** `get_stream_events` rose 41% and saga occasionally lost events.

### Why the regression — hypothesis

Less-frequent orphan-claim calls means each call claims **larger
batches** of rows that have accumulated since the last claim. Larger
batches dispatched to perspective handlers create **larger concurrent
write fan-outs to the saga perspective**. The saga perspective uses
optimistic concurrency (EF Core's row-version column). Under higher
concurrent-write pressure, more updates lose the optimistic check
and are retried. **The v0.687 rewind catch-up loop (see
`perspective-rewind-completion-gap.md`) closes gaps from one specific
race window**; v0.688's reduced claim cadence opens a *different*
race window that the existing catch-up doesn't cover.

Supporting evidence:
- Saga `version` of 90 on the worst-miss run is the **lowest** saga
  update count across all six runs — fewer accepted updates is
  consistent with more losses to optimistic concurrency.
- `get_stream_events` +41% is consistent with retry storms reading the
  same stream repeatedly during conflict resolution.
- Both regressions appear in matched sterile clusters, so it's not a
  cluster-stability artifact.

### Decision

**Rolled back to v0.687 on slot-3 at 2026-06-13 10:01 EDT.** Backup files
in `/tmp/slot3-claim-work-v0.688/claim_work_v0.687_{bff,job}.sql`. HPA
minReplicas patches left at 3 for both BFF and Job services.

### Pre-requisite before re-attempting v0.688

The v0.688 guard fix is **correct in shape** but **unsafe to ship
until the saga perspective's concurrency story is robust to larger
claim batches**. Sequence of work before retry:

1. **Diagnose the saga-perspective race** under increased concurrent
   apply pressure. Specifically: what happens when N concurrent
   `SagaItemCompletedEvent` apply calls hit `wh_per_bulk_job_import_orchestration_saga`
   simultaneously? Trace the optimistic-concurrency retry path. Look
   for retries that re-read but don't re-apply *all* events that
   landed during their first attempt.
2. **Fix the saga race**. Likely options:
   - Serialize saga-perspective writes per stream (per-stream
     SemaphoreSlim or DB-level advisory lock on saga_stream_id).
   - Switch the saga's `ProcessedLineNumbers` accumulator from
     `state.ProcessedLineNumbers.Add(N)` to an atomic
     `INSERT INTO saga_processed_lines(saga_id, line_number)
     ON CONFLICT DO NOTHING` against a separate side table — then
     the saga's "complete?" check becomes a COUNT(*) against that
     table. No optimistic-concurrency conflicts; missing events
     remain missing if they never arrive but races don't cause
     missing events.
   - Extend the rewind catch-up to detect "ProcessedLineNumbers.Count
     < observed SagaItemCompletedEvent count" and force a replay
     from the gap point.
3. **Lock the fix with an integration test** that reproduces the slot-3
   condition — many concurrent SagaItemCompletedEvents in a single
   claim batch, assert all lines land in ProcessedLineNumbers.
4. **Re-attempt v0.688 hot-swap** on slot-3 once steps 1-3 ship.

### What stays good from this attempt

- `/tmp/slot3-claim-work-v0.688/claim_work_v0.688.sql` — patched function
  body, ready to redeploy when prerequisites are met.
- The four guard predicate shapes (outbox / inbox / perspective_events /
  receptor_work) — predicate-aligned with their respective callee
  functions. Mig 029 edit is still the right structural change.
- The slice plan above (RED tests + GREEN edit + docs) is unchanged;
  the only blocker is the saga-race prerequisite.
- HPA pinning (BFF + Job at minReplicas=3) — keep on slot-3 to
  preserve test reproducibility; review before merging back to other
  slots.

### Open question worth checking before the saga fix

Is the same race already latent on v0.687? On v0.687 the saga hit
350/350 across all three runs in this batch, but earlier in the project
we saw 347/350 once. If v0.687's 347/350 was the same race fired by a
different timing window (e.g. a cold start), the fix needed here would
also protect existing v0.687 production. Worth a literature review of
prior 350-job sagas across all slots before assuming the race is
v0.688-exclusive.
