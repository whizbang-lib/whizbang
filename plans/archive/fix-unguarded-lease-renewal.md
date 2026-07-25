# Plan: Fix unguarded lease renewal in `process_work_batch`

## Context

Observed in a busy multi-instance consumer deployment on 2026-04-21.
`wh_active_streams` had accumulated **1.82 billion lifetime UPDATEs on 5,790
rows** (~315K writes per row) over ~3.6 days of uptime on a single service
alone; other services showed the same pattern (~290K updates/row). Autovacuum
could not keep up — `process_work_batch` calls were averaging 4-5 s with heavy
`LWLock:WALWrite` + `Lock:transactionid` waits, and end-user-visible processing
was severely delayed.

Root cause: `src/Whizbang.Data.Postgres/Migrations/029_ProcessWorkBatch.sql`
lines 1036-1041 unconditionally refreshes `lease_expiry` on every row owned by
the instance on **every** tick:

```sql
-- Renew active stream ownership for all streams owned by this instance.
-- Keeps stream stickiness alive as long as the instance is heartbeating (~1s ticks).
-- Without this, streams with no new messages would lose ownership after lease expiry.
UPDATE wh_active_streams
SET lease_expiry = v_lease_expiry
WHERE assigned_instance_id = p_instance_id;
```

With 3 pods × N streams × ~1 s ticks, this generates N × 3 dead tuples per
second cluster-wide. For Job's 5,790 streams: ~5,800 dead tuples / sec.

## Proof from a live deployment

A live patch was applied to all service DBs in one environment on 2026-04-21 via
`CREATE OR REPLACE FUNCTION`. Methodology and raw data were captured separately.
Key numbers:

| DB | `wh_active_streams` UPDATE rate (pre) | UPDATE rate (post) | Reduction |
|----|---------------------------------------|--------------------|-----------|
| Service A | 1,187 / sec (81,893 / 69 s) | ~0–10 / sec (steady state) | **≥100×** |
| Service B | similar scale | 0 / sec over 120 s | **≥200×** |
| Service C | similar scale | ~15 / sec | **~80×** |

`wh_active_streams` bloat on Service C dropped from 46 % dead (post-vacuum, pre-patch)
to steady at 46 % (no new churn). Dead-tuple creation on wh_active_streams
effectively stopped for all services.

## The fix (one line of SQL, pre-v1 in-place migration edit)

Per `feedback_local_version_file.md` / `project_pre_v1_migrations.md` — edit
migration 029 in place, no new migration file needed.

`src/Whizbang.Data.Postgres/Migrations/029_ProcessWorkBatch.sql` — replace
the `UPDATE wh_active_streams` block at lines 1036-1041:

```sql
-- Renew active stream ownership for streams whose lease is nearing expiry.
-- Refreshing every tick generated ~N_streams × N_instances dead tuples per
-- second, which autovacuum could not keep up with and which dominated
-- process_work_batch runtime via WAL contention. The `/ 3` threshold means
-- a freshly-renewed stream (lease at T+lease_duration) is not refreshed again
-- until T + 2/3 * lease_duration — typical reduction is 100-200× write volume.
-- Orphan-claim SLA is unchanged: streams still expire at lease_expiry.
UPDATE __SCHEMA__.wh_active_streams
SET lease_expiry = v_lease_expiry
WHERE assigned_instance_id = p_instance_id
  AND lease_expiry < p_now + (p_lease_duration_seconds / 3) * interval '1 second';
```

## TDD plan

### RED #1 — contract test: write-volume budget

**File**: `tests/Whizbang.Data.Dapper.Postgres.Tests/ActiveStreamsLeaseRenewalTests.cs`
(new; sibling to the existing `PartitionConsistencyTests.cs`).

Scenario: seed N streams owned by a single instance. Invoke
`process_work_batch` K times in a loop without any new messages. Assert
`pg_stat_user_tables.n_tup_upd` delta on `wh_active_streams` is less than
`N * (K * tick_interval) / (lease_duration / 3)` — i.e., each stream gets
refreshed at most once per `lease_duration / 3` period, not once per tick.

Numeric expectation at defaults (lease=300s, tick=1s, 100 streams, 300 ticks):

- **Old behavior**: 100 × 300 = 30,000 UPDATEs.
- **New behavior**: 100 × (300s × 1s / (300/3)s) = 100 × 3 = 300 UPDATEs. **100× reduction.**

Test must **fail** (RED) against current migration 029.

### RED #2 — orphan-claim SLA preserved

Seed a stream owned by instance A. Let lease expire naturally (advance clock
past `lease_duration`). Invoke `process_work_batch` from instance B and assert
the stream is claimable via the orphan path. Prevents regressions where the
guard is misconfigured and starves the orphan claimer.

### GREEN

Apply the one-line guard to migration 029. Both RED tests flip GREEN.

### REFACTOR + docs

1. Extract `p_lease_duration_seconds / 3` into a named local
   `v_refresh_threshold TIMESTAMPTZ := p_now + (p_lease_duration_seconds / 3) * interval '1 second';`
   computed alongside `v_lease_expiry` at the top of the function. Avoids
   re-computing per-row.
2. Update the comment block above the UPDATE to describe the conditional
   semantics.
3. Doc page: `whizbang-lib.github.io/src/assets/docs/v1.0.0/operations/workers/
   process-work-batch-lease-semantics.md` — explains the refresh cadence and
   how `p_lease_duration_seconds` tuning affects both failover SLA and write
   volume.
4. Cross-link from existing `stream-locking.md`.

### Coverage / sonar

- 100 % coverage on the new test file.
- `pwsh scripts/Run-Tests.ps1 -Mode AiIntegrations -ProjectFilter "Postgres"` clean.
- Sonar clean.

## Related patterns to investigate (out of scope for this PR, track separately)

These showed up during the same pg_stat_statements investigation:

1. **`register_instance_heartbeat` (migration 010)** — INSERT/ON-CONFLICT
   UPDATE on `wh_service_instances` every tick. One service accumulated
   4,686,253 lifetime calls, 8,300 % dead tuples on a 3-row table. Small
   table, so low runtime impact, but same pattern. **Candidate fix**: skip
   UPDATE if `last_heartbeat_at > p_now - lease_duration/3`.

2. **Concurrent `process_work_batch` call rate** — one service ran 1,042,862 pwb
   calls at ~510 ms mean. Tick cadence should be tunable per service; currently
   all services share the same polling interval and many tick faster than they
   need to. Not a migration fix — worker config.

3. **`pg_stat_statements` LWLock under high query rate** — not a Whizbang bug
   per se, but high query rate (mostly from items 1-2) triggers serialization
   in the stats extension itself. Reducing total query rate reduces this.

4. **`store_inbox_messages` / `store_outbox_messages` partition refresh
   interaction** — migration 029 has a deadlock fence (line 583-590) that
   does a sorted `SELECT … FOR UPDATE` across the same rows the UPDATE below
   would touch. With the new guard, this fence now sees fewer rows needing
   to be locked. Worth measuring — may simplify the fence.

## Fix delivery shape (TDD-strict)

1. **RED #1** — write-volume budget test (contract) → fails.
2. **RED #2** — orphan-claim SLA test → fails (if we broke it).
3. **GREEN** — apply guard to migration 029.
4. **REFACTOR** — extract `v_refresh_threshold`, update comments.
5. **Lock-in tests** — N-instance scenario with overlapping streams; large-N
   throughput test.
6. **Docs** — new doc page, cross-link.
7. **Publish local NuGet** — bump Whizbang local version, deploy to the test
   environment as the permanent fix (replaces the live CREATE OR REPLACE FUNCTION
   applied 2026-04-21). Verify via `pg_get_functiondef` that the guard is
   present after the migration runs on that environment's next startup.
8. **Rollout to other environments** — after the initial soak, roll the same
   Whizbang version to the remaining environments.

## Rollback

The live patch was captured per-service before it was applied — re-applying
any of those captured function definitions restores the unguarded behavior
byte-for-byte.

The migration 029 edit is additive: removing the guard restores the old
behavior. Integration tests would catch any regression.

## References

- Before/after data, analysis, and rollback artifacts were captured out-of-band
  in the deployment environment's diagnostics directory.
- Related recent commits on `release/v0.233.2-alpha.1`:
  - `760aa5ce` — partition count unification + recompute migration
    (caused the initial bloat spike by UPDATE-ing every stream in
    `recompute_partition_numbers`)
  - `f3ff00d8` — drain-mode Apply-exactly-once dedupe (separate bug)
