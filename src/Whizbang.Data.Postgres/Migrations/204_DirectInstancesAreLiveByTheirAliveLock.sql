-- Migration: 204_DirectInstancesAreLiveByTheirAliveLock.sql
-- Date: 2026-10-10
-- Description: A direct instance is live while its alive-lock is held, so its heartbeat can run on the slow cadence (#1286).
--
--              The alive-lock (055) was never registered as the heartbeat's lock source, so every instance beat on the
--              fast cadence. Registering it puts a direct instance that holds its lock on the slow cadence, and then its
--              heartbeat row is older than claim_work's 30 second rank cutoff for half of every beat: its peers ranking
--              themselves would count it out and their shares would overlap. The owner's decisions (#1254, #1286):
--                * the library tracks each instance's connection mode, with a strategy per mode;
--                * direct mode: the alive-lock is authoritative. Held, the instance is live whatever its heartbeat's
--                  age; not held, it is judged by its heartbeat like any other instance, at once;
--                * pooled mode: the heartbeat alone decides. A pooled instance's lock is never consulted (its session
--                  cannot be seen through a pooler), so a pooled instance is judged exactly as before.
--
--              One definition of "this direct instance holds its alive-lock": wh_direct_alive_lock_holders. Every
--              reader that judges a peer by its heartbeat applies the same rule with it: live when the heartbeat is
--              within the reader's own window, or when the instance is direct and its alive-lock is held.
--                * claim_work ranks its peers by that rule (the 30 second cutoff is unchanged for a heartbeat). The
--                  lock is read only for a direct row older than the cutoff, and then once per claim, as a hashed
--                  subplan: a claim whose peers all have fresh rows, or are pooled, never reads pg_locks. A claim that
--                  presents the elected assigner's current assignment does not rank at all (203).
--                * claim_work's own-registration notice: the caller says whether it holds its alive-lock
--                  (p_alive_lock_held), so a direct caller holding it is not asked to re-register between slow beats.
--                * wh_partition_assignment_candidates reads the lock through the same function.
--              Readers left as they were, because they already honor the lock: the stale-peer reap
--              (cleanup_stale_instances spares any row whose alive-lock is held, 055 and v0.687), is_instance_alive,
--              and claim_orphaned_* (a stream's owner is live with a fresh heartbeat or its direct connection's
--              application_name in pg_stat_activity, the same session that holds the lock). Role votes and bridge
--              expiry read the registration, which only the reap removes.
--
-- Dependencies: 055 (the alive-lock key), 203 (wh_service_instances.connection_mode; wh_partition_assignment_candidates
--               and claim_work, copied verbatim apart from the delta)
-- Objects: wh_direct_alive_lock_holders, wh_partition_assignment_candidates, claim_work
-- Constants: the double-underscore tokens in this file (for example __CATEGORY_OUTBOX__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

-- ---------------------------------------------------------------------------------------------
-- wh_direct_alive_lock_holders: the direct instances whose alive-lock is held right now.
-- ---------------------------------------------------------------------------------------------
-- The one definition of the lock half of the liveness rule. 055's key (hashtext of 'wh_instance_alive:' and the id,
-- taken as one bigint, so classid is its high half and objid its low half, objsubid 1). pg_locks is read once per
-- call. Not filtered by database, as 055 and the reap are not: the key carries the instance's unique id, and a filter
-- would cost a catalog scan of pg_database on every claim that reads the locks (AliveLockRankCostScenarioTests).
-- Only rows recorded as direct are considered: a pooled instance is judged by its heartbeat alone.
SELECT __SCHEMA__.drop_all_overloads('wh_direct_alive_lock_holders');

-- <docs>fundamentals/workers/instance-liveness</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AliveLockLivenessSqlTests.cs:Holders_ADirectInstanceHoldingItsLock_IsListedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AliveLockLivenessSqlTests.cs:Holders_APooledInstanceHoldingALock_IsNotListedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AliveLockLivenessSqlTests.cs:Holders_ADirectInstanceWhoseLockIsReleased_IsNotListedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Performance/AliveLockRankCostScenarioTests.cs:SelfRankedClaim_WithHalfTheFleetOnTheSlowCadence_ReadsTheLocksOnce_ReportAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_direct_alive_lock_holders()
RETURNS TABLE(instance_id UUID)
LANGUAGE sql
STABLE
AS $$
  SELECT si.instance_id
  FROM __SCHEMA__.wh_service_instances si
  WHERE si.connection_mode = 'direct'
    AND ((((hashtext('wh_instance_alive:' || si.instance_id::text)::BIGINT) >> 32) & x'FFFFFFFF'::BIGINT)::OID,
         ((hashtext('wh_instance_alive:' || si.instance_id::text)::BIGINT) & x'FFFFFFFF'::BIGINT)::OID)
        IN (SELECT l.classid, l.objid
            FROM pg_locks l
            WHERE l.locktype = 'advisory'
              AND l.granted
              AND l.objsubid = 1)
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_direct_alive_lock_holders() IS
  '204 (#1286): the instances registered as direct whose session alive-lock (055) is held right now. '
  'A direct instance holding its lock is live whatever the age of its heartbeat; every reader that judges peers by '
  'their heartbeat applies this as the other half of the rule. Pooled instances are never listed.';

-- ---------------------------------------------------------------------------------------------
-- wh_partition_assignment_candidates: last word 203, reading the lock through wh_direct_alive_lock_holders.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_partition_assignment_candidates');

-- <docs>fundamentals/work-coordinator/partition-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Candidates_ADirectInstanceHoldingItsAliveLock_ReportsTheLockAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Candidates_APooledInstance_IsNeverJudgedByALockAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Candidates_AnEvictedInstance_IsLeftOutAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AliveLockLivenessSqlTests.cs:Candidates_ADirectInstanceWhoseLockIsReleased_ReportsNoLockAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_partition_assignment_candidates()
RETURNS TABLE(
  instance_id UUID,
  connection_mode TEXT,
  -- Only ever true for a direct-mode instance: a pooled instance's lock is not consulted (its session cannot be
  -- seen), so it is judged by its heartbeat alone.
  alive_lock_held BOOLEAN,
  -- How long ago the instance last beat, by the database's clock.
  heartbeat_age INTERVAL
)
LANGUAGE sql
STABLE
SET timezone = 'UTC'
AS $$
  SELECT si.instance_id,
         COALESCE(si.connection_mode, 'pooled'),
         -- 204: the one definition of a held alive-lock. Only ever true for a direct-mode instance.
         si.instance_id IN (SELECT h.instance_id FROM __SCHEMA__.wh_direct_alive_lock_holders() h),
         clock_timestamp() - si.last_heartbeat_at
  FROM __SCHEMA__.wh_service_instances si
  WHERE NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_instance_evictions e WHERE e.instance_id = si.instance_id)
  ORDER BY si.instance_id;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_partition_assignment_candidates() IS
  '203 (#1254): every registered, not evicted instance with its connection mode, whether its alive-lock is held '
  '(direct mode only) and the age of its heartbeat. The assigner applies the per-mode strategy to these. 204: the lock '
  'is read through wh_direct_alive_lock_holders.';

-- ---------------------------------------------------------------------------------------------
-- claim_work: last word 203, ranking its peers by the liveness rule and told whether the caller holds its alive-lock.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('claim_work');

-- <docs>fundamentals/work-coordinator/claim-loop</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkSqlTests.cs:ClaimWork_OutboxHasUnprocessedWork_ReturnsThatWorkAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimOrphanedAcquisitionBoundSqlTests.cs:ClaimWork_DoesNotAcquireMoreThanItsCallerAskedForAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/BucketAwareClaimSqlTests.cs:ClaimOrphanedInbox_BackgroundStreams_KeepAFloorOfTheBatchAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IdleBandHeldRowReOfferTests.cs:WhileBusy_AnIdleBandCommandTheClaimLeases_IsReOfferedToTheDrainAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IdleBandHeldRowReOfferTests.cs:AnIdleBandCommandPastMaxAttempts_ReclaimedAfterItsLeaseLapses_ReachesTheDrainEveryCycleAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunSqlTests.cs:ClaimWork_OneLongStream_DrainsInRunsNotRowsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunSqlTests.cs:ClaimWork_ReOffersOneRowPerHeldStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunSqlTests.cs:ClaimWork_OutboxAcquisitionFillsItsBound_RaisesTheFullNoticeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkRegistrationSqlTests.cs:ClaimWork_HeldOpen_AHeartbeatForTheSameInstanceDoesNotWaitAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkRegistrationSqlTests.cs:ClaimWork_HeldOpen_TheRegistrationCallDoesNotWaitAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkRegistrationSqlTests.cs:ClaimWork_StaleRegistration_LeavesTheRowAlone_AndAsksForRegistrationAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkRegistrationSqlTests.cs:ClaimWork_MissingRegistration_DoesNotInsertIt_AndAsksForRegistrationAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkRegistrationSqlTests.cs:ClaimWork_StaleOwnRegistration_StillRanksItselfAmongTheLiveAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkRegistrationSqlTests.cs:ClaimWork_MissingOwnRegistration_StillRanksItselfAmongTheLiveAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimLedgerLockOrderSqlTests.cs:ClaimWork_LedgerRowsOfEveryAcquisition_AreTakenInOneStreamOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimLedgerLockOrderSqlTests.cs:ClaimWork_AStreamLeasedByTwoAcquisitions_IsWrittenOnceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimLedgerLockOrderSqlTests.cs:ConcurrentInboxClaims_WithNoInstanceEverRegistered_NeverDeadlockAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimLedgerLockOrderSqlTests.cs:ConcurrentOutboxAndInboxClaims_WithNoInstanceEverRegistered_NeverDeadlockAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:ClaimWork_WithTheCurrentAssignment_RanksByItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:ClaimWork_WithASupersededAssignment_RanksItselfAndAsksForARefreshAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:ClaimWork_WithAnExpiredAssignment_RanksItselfAndAsksForARefreshAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:ClaimWork_WhenNotAMemberOfTheCurrentAssignment_RanksItselfWithoutANoticeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AliveLockLivenessSqlTests.cs:Rank_ADirectPeerOnTheSlowCadenceHoldingItsLock_IsCountedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AliveLockLivenessSqlTests.cs:Rank_ADirectPeerWhoseLockIsReleased_IsCountedOutAtOnceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AliveLockLivenessSqlTests.cs:Rank_APooledPeerPastTheCutoff_IsCountedOutWhateverLockItsSessionHoldsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AliveLockLivenessSqlTests.cs:Notice_ADirectCallerHoldingItsLock_IsNotAskedToRegisterBetweenSlowBeatsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AliveLockLivenessSqlTests.cs:Notice_APooledCallerPastTheCutoff_IsStillAskedToRegisterAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.claim_work(
  p_instance_id UUID,
  p_service_name TEXT,
  p_host_name TEXT,
  p_process_id INTEGER,
  p_max_streams INTEGER DEFAULT 1000,
  p_partition_count INTEGER DEFAULT 10000,
  p_lease_seconds INTEGER DEFAULT 300,
  p_fresh_share DOUBLE PRECISION DEFAULT 0.5,
  p_max_rows INTEGER DEFAULT NULL,
  p_allow_steal BOOLEAN DEFAULT FALSE,
  p_max_perspective_streams INTEGER DEFAULT NULL,
  -- 167: the idle band. The CALLER owns settledness -- it already tracks the dwell for housekeeping
  -- -- and passes it in. Computing it here would mean counting the backlog on every poll, which is
  -- the cost the claim is measured against.
  p_idle_settled BOOLEAN DEFAULT FALSE,
  p_idle_trickle_after INTERVAL DEFAULT INTERVAL '30 minutes',
  p_idle_trickle_slice INTEGER DEFAULT 10,
  p_idle_force_after INTERVAL DEFAULT INTERVAL '4 hours',
  -- 171: the outbox's own row bound, independent of the stream window. NULL is the previous
  -- behavior: acquisition bounded by p_max_streams rows.
  p_max_outbox_rows INTEGER DEFAULT NULL,
  -- 171: how many consecutive rows one outbox stream may lease per claim. NULL (or 1) is the
  -- previous behavior: one row per chosen head.
  p_outbox_run_length INTEGER DEFAULT NULL,
  -- 203 (#1254): the partition assignment the caller has cached, by version. When it is still the published one and
  -- its lease has not run out, this instance's rank and the member count come from it instead of from ranking the
  -- heartbeat rows; otherwise the claim ranks itself as before and raises
  -- NOTICE 'whizbang.partition_assignment_stale=true' so the caller refreshes. NULL: no assignment, rank as before.
  p_assignment_epoch BIGINT DEFAULT NULL,
  p_assignment_revision BIGINT DEFAULT NULL,
  -- 204 (#1286): whether the caller holds its session alive-lock right now. A direct caller holding it is live by
  -- the lock, so its registration is not reported stale between the slow beats of its heartbeat.
  p_alive_lock_held BOOLEAN DEFAULT FALSE
) RETURNS TABLE(
  source VARCHAR(20),           -- __CATEGORY_OUTBOX__ | __CATEGORY_INBOX__ | 'receptor' | __CATEGORY_PERSPECTIVE__
  work_id UUID,
  work_stream_id UUID,
  partition_number INTEGER,
  destination VARCHAR(200),
  message_type VARCHAR(500),
  envelope_type VARCHAR(500),
  message_data TEXT,
  metadata JSONB,
  status INTEGER,
  attempts INTEGER,
  is_newly_stored BOOLEAN,
  is_orphaned BOOLEAN,
  perspective_name VARCHAR(200),
  priority INTEGER,             -- 150: the inbox row's effective priority (NULL for other sources)
  received_at TIMESTAMPTZ       -- 150: the inbox row's arrival (NULL for other sources)
) AS $$
DECLARE
  c_source_outbox CONSTANT VARCHAR(20) := __CATEGORY_OUTBOX__;
  c_source_inbox CONSTANT VARCHAR(20) := __CATEGORY_INBOX__;
  v_has_any_work BOOLEAN;
  -- 167: the idle band's admission, decided once per poll in the outer block. 170: it bounds inbox
  -- ACQUISITION and the perspective lane loop; the inbox re-offer no longer reads it.
  v_idle_admitted BOOLEAN := FALSE;  -- may the idle band be claimed at all on this poll
  v_idle_capped BOOLEAN := FALSE;    -- admitted by trickle, so bounded to a slice
  -- What to hand acquisition: 0 withheld, NULL full width, N a trickle slice.
  v_idle_acquire_rows INTEGER := 0;
  v_idle_oldest INTERVAL;            -- age of the oldest idle row this instance can see
BEGIN
  -- 157: this function runs under plan_cache_mode = force_custom_plan (see its closing clause), so
  -- the plans below and in the acquisition functions it calls are made for the tables as they are
  -- at each poll, never kept from a poll that found them empty.
  -- Empty-call short-circuit: cheap indexed EXISTS lookups on partial indexes.
  -- Each LIMIT 1 against an existing partial index is sub-millisecond when buffer-cached.
  -- Note: wh_receptor_processing uses completed_at (not processed_at) for the "is done" semantic.
  -- 115: coalesce-pending rows are not claim-pump work, they wait for the coalesce worker.


  v_has_any_work := EXISTS (SELECT 1 FROM __SCHEMA__.wh_outbox WHERE processed_at IS NULL AND coalesce_group IS NULL LIMIT 1)
                 OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_inbox_state WHERE processed_at IS NULL LIMIT 1)
                 OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_perspective_events WHERE processed_at IS NULL LIMIT 1)
                 OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_receptor_processing WHERE completed_at IS NULL LIMIT 1);

  IF NOT v_has_any_work THEN
    RETURN;  -- empty result set; orphan-claim sub-functions never invoked
  END IF;

  -- Non-empty path: claim outbox work and return it.
  -- Inbox / receptor / perspective claim + return land in subsequent TDD cycles.
  DECLARE
    v_now TIMESTAMPTZ := NOW();
    v_lease_expiry TIMESTAMPTZ := v_now + (p_lease_seconds || ' seconds')::INTERVAL;
    v_stale_cutoff TIMESTAMPTZ := v_now - INTERVAL '30 seconds';
    v_rank INTEGER;
    -- 203: set when the cached assignment passed the fence and supplied the rank.
    v_assigned BOOLEAN := FALSE;
    v_count INTEGER;
    -- v0.661: track per-category RETURN QUERY rowcount so the drain-mode hint
    -- can be derived from ROW_COUNT instead of four fresh COUNT(*) queries.
    v_outbox_rows INTEGER := 0;
    v_inbox_rows INTEGER := 0;
    v_receptor_rows INTEGER := 0;
    v_perspective_rows INTEGER := 0;
    -- 171: the outbox acquisition's row bound and what it actually leased, for the full notice.
    v_outbox_bound INTEGER := COALESCE(p_max_outbox_rows, p_max_streams);
    v_outbox_acquired INTEGER := 0;
    -- 158: the lane walks that re-offer held inbox and perspective streams.
    v_bucket INTEGER;
    v_is_event BOOLEAN;
    v_start UUID;
    v_remaining INTEGER;
    v_rows INTEGER;
    -- 202 (#1256): every stream the acquisitions below lease work on, with its partition number, in step. The
    -- acquisitions no longer write the stream ledger themselves; it is written once, after the last of them.
    v_ledger_streams UUID[] := '{}';
    v_ledger_partitions INTEGER[] := '{}';
  BEGIN
    -- 167: whether the idle band may be claimed at all on this poll.
    --
    -- Idle work is work nobody waits for, so it is withheld while the service is busy. A bucket that
    -- is withheld can starve, and its first occupant is auditing, so withholding is bounded by TIME
    -- rather than by the service happening to go quiet:
    --
    --   settled            -> drain at full width, which is the point of the band
    --   older than force   -> drain at full width anyway, however busy. The floor.
    --   older than trickle -> take a slice, so progress is never zero
    --   otherwise          -> withheld
    --
    -- One probe against the held-lane index answers all three, and only when the instance holds idle
    -- work at all: no idle rows, no cost.
    -- CLAIMABLE idle work, not merely work this instance already holds. An earlier draft asked only
    -- about held rows, which cannot admit anything: idle rows arrive unowned, so the band could
    -- never be entered, nothing was ever leased, and the probe went on reporting nothing to do. The
    -- band must be admitted on what this instance COULD take.
    --
    -- Both tables, because the band's first occupant is auditing and audit lands in perspective
    -- events; a gate derived from the inbox alone would withhold a perspective backlog for ever.
    --
    -- MIN of the timestamp rather than MAX of the age: the aggregate has to be over an indexed
    -- COLUMN for the planner to answer it by walking to the first qualifying entry and stopping.
    -- MAX(v_now - received_at) is an expression, and costs a pass over the whole band instead.
    -- LEAST ignores NULLs in Postgres, so a band empty on one side still reports the other, and
    -- empty on both leaves this NULL, which is the "nothing to admit" case below.
    SELECT v_now - LEAST(
      (SELECT MIN(i.received_at)
       FROM __SCHEMA__.wh_inbox_state i
       WHERE i.processed_at IS NULL
         AND i.is_event = TRUE
         AND i.priority > 399
         AND (i.instance_id = p_instance_id OR i.instance_id IS NULL OR i.lease_expiry < v_now)
         AND (i.scheduled_for IS NULL OR i.scheduled_for <= v_now)),
      (SELECT MIN(pe.created_at)
       FROM __SCHEMA__.wh_perspective_events pe
       WHERE pe.processed_at IS NULL
         AND pe.priority > 399
         AND (pe.instance_id = p_instance_id OR pe.instance_id IS NULL OR pe.lease_expiry < v_now)
         AND (pe.scheduled_for IS NULL OR pe.scheduled_for <= v_now))
    ) INTO v_idle_oldest;

    IF v_idle_oldest IS NULL THEN
      v_idle_admitted := FALSE;
    ELSIF p_idle_settled OR v_idle_oldest >= p_idle_force_after THEN
      v_idle_admitted := TRUE;
    ELSIF v_idle_oldest >= p_idle_trickle_after THEN
      v_idle_admitted := TRUE;
      v_idle_capped := TRUE;
    END IF;

    -- Inbox acquisition and the perspective re-offer are both derived from the one decision above.
    IF NOT v_idle_admitted THEN
      v_idle_acquire_rows := 0;
    ELSIF v_idle_capped THEN
      v_idle_acquire_rows := GREATEST(p_idle_trickle_slice, 0);
    ELSE
      v_idle_acquire_rows := NULL;
    END IF;
    -- 196 (#1226): the claim reads this instance's registration and never writes it. It used to
    -- repair a missing or stale row here, inside the claim's transaction, and the row stayed locked
    -- until the claim committed: record_heartbeat for this instance then waited for the whole claim,
    -- so a slow claim made the heartbeat late and a late heartbeat made the next claim hold the row.
    --
    -- A missing or stale row is reported instead, and the caller registers in a statement of its
    -- own once this claim has returned. On the healthy path that costs one primary-key probe, the
    -- one the repair's guard already paid.
    -- 204 (#1286): a direct caller that holds its alive-lock is live by the lock, which is how its peers rank it, so a
    -- row older than the cutoff is not stale for it: its heartbeat runs on the slow cadence while it holds the lock.
    IF NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_service_instances
      WHERE instance_id = p_instance_id
        AND (last_heartbeat_at >= v_stale_cutoff
             OR (p_alive_lock_held AND connection_mode = 'direct'))
    ) THEN
      RAISE NOTICE 'whizbang.instance_registration_stale=true';
    END IF;

    -- The rank counts this instance as live whatever its row says: it is running this claim. Ranked
    -- as absent it would fall to a solo rank and widen its claim over every peer's partitions until
    -- its registration lands. Peers are ranked as before, on a fresh heartbeat row. Their view of
    -- this instance catches up when the caller registers, which is no later than it did when the
    -- repair ran here: a write inside this transaction was invisible to them until the claim
    -- committed anyway.
    -- 203 (#1254): the fence where the assignment is read. The caller's cached version must still be the published
    -- one (epoch and revision) and its lease unexpired, and this instance a member of it. One read of a one-row table,
    -- which replaces the ranking below rather than adding to it. Deliberately not keyed on the singleton primary key:
    -- the table is one row by its constraint, so a scan of its one page costs one buffer where the key would cost an
    -- index page as well (PartitionAssignmentClaimCostScenarioTests).
    IF p_assignment_epoch IS NOT NULL THEN
      SELECT array_position(pa.members, p_instance_id) - 1, cardinality(pa.members)
      INTO v_rank, v_count
      FROM __SCHEMA__.wh_published_partition_assignment pa
      WHERE pa.epoch = p_assignment_epoch
        AND pa.revision = p_assignment_revision
        AND pa.lease_expires_at > v_now;
      v_assigned := v_rank IS NOT NULL;
      IF NOT FOUND THEN
        -- Superseded or expired: the caller refreshes, and this claim ranks itself. Never stop claiming.
        RAISE NOTICE 'whizbang.partition_assignment_stale=true';
      END IF;
    END IF;

    IF NOT v_assigned THEN
    SELECT ranked.instance_rank, ranked.active_instance_count INTO v_rank, v_count
    FROM (
      SELECT live.instance_id,
             (ROW_NUMBER() OVER (ORDER BY live.instance_id) - 1)::INTEGER AS instance_rank,
             COUNT(*) OVER ()::INTEGER AS active_instance_count
      FROM (
        -- 204 (#1286): the liveness rule. A fresh heartbeat, or a direct instance whose alive-lock is held: the slow
        -- cadence of a lock holder never counts it out, and a direct instance that lost its lock is judged by its
        -- heartbeat at once, like a pooled one. The lock is consulted only for a direct row past the cutoff (the OR
        -- short-circuits), as a hashed subplan built once per claim.
        SELECT si.instance_id
        FROM __SCHEMA__.wh_service_instances si
        WHERE si.last_heartbeat_at >= v_stale_cutoff
           OR (si.connection_mode = 'direct'
               AND si.instance_id IN (SELECT h.instance_id FROM __SCHEMA__.wh_direct_alive_lock_holders() h))
        UNION
        SELECT p_instance_id
      ) live
    ) ranked
    WHERE ranked.instance_id = p_instance_id;
    END IF;

    -- v0.683, per-inner-function guards. The existing v_has_any_work short-circuit
    -- (top of the function) only fires when ALL four queues are empty. Under steady
    -- import load, that's rare, but the typical pattern is "one queue has work,
    -- the others don't." Without per-function guards, claim_work paid the full
    -- claim_orphaned_*/emit_chain scan cost on every call regardless. Each guard
    -- uses an existing partial index (idx_{outbox,inbox}_unprocessed_claiming WHERE
    -- processed_at IS NULL, etc.) so the EXISTS probe is sub-millisecond. Behavior
    -- is preserved: if a guard returns false, the corresponding inner function had
    -- no rows to claim anyway, so skipping its scan is a pure win.

    -- Claim orphaned / unowned outbox work, only if any outbox row is unprocessed
    -- AND either unowned or has an expired lease (the orphan predicate matched by
    -- claim_orphaned_outbox's WHERE clause).
    -- 158: two index probes, not one scan. A single predicate over "unowned or expired" has to examine
    -- every pending row to prove there is none, which on a busy instance is its whole holdings on
    -- every poll. Unowned rows are found under instance_id IS NULL through the outstanding-by-instance
    -- index (123); expired leases are the head of the lease-expiry index (158), so an instance whose
    -- leases are all live proves it at the first entry.
    IF EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_outbox
      WHERE processed_at IS NULL
        AND coalesce_group IS NULL  -- 115: pending singles are never orphan-claimable
        AND instance_id IS NULL
      LIMIT 1
    ) OR EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_outbox
      WHERE lease_expiry < v_now
        AND processed_at IS NULL
        AND coalesce_group IS NULL
        AND instance_id IS NOT NULL
      LIMIT 1
    ) THEN
      -- Bounded for the same reason as the inbox call below: without a limit this acquires the whole
      -- eligible backlog in one statement, and the caller's claim limit never reaches the flood.
      -- 171: bounded by the OUTBOX row bound, not the stream window. The window is a stream count
      -- that starts at its floor and grows only on inbox evidence; as a row cap it leased one row on
      -- each of the oldest streams per claim, so a backlog on a few long streams drained one row per
      -- stream per cycle. The window still chooses how many streams move (the heads); each of them
      -- then leases a run of its next rows. Counted, so a full acquisition can say so below.
      -- 202: the ledger is deferred (the last argument) and collected for the one pass below.
      SELECT count(*)::INTEGER,
             v_ledger_streams || COALESCE(array_agg(a.stream_id), '{}'),
             v_ledger_partitions || COALESCE(array_agg(a.partition_number), '{}')
      INTO v_outbox_acquired, v_ledger_streams, v_ledger_partitions
      FROM __SCHEMA__.claim_orphaned_outbox(
        p_instance_id, v_rank, v_count, v_lease_expiry, v_now, p_partition_count, v_stale_cutoff,
        v_outbox_bound, GREATEST(COALESCE(p_outbox_run_length, 1), 1), p_max_streams,
        TRUE
      ) a;
    END IF;

    -- Claim orphaned / unowned inbox work, same predicate shape.
    -- 158: two index probes; see the outbox guard above.
    IF EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_inbox_state
      WHERE processed_at IS NULL
        AND instance_id IS NULL
      LIMIT 1
    ) OR EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_inbox_state
      WHERE lease_expiry < v_now
        AND processed_at IS NULL
        AND instance_id IS NOT NULL
      LIMIT 1
    ) THEN
      -- p_max_streams bounds ACQUISITION here, not just the re-emission below. Omitting it let this
      -- call lease the entire eligible backlog in one statement, charging an attempt to every row ,
      -- while the caller's claim window and outstanding budget bounded only what came back out of
      -- eligible_inbox. Both throttles sat downstream of the flood, so neither could ever have held.
      -- 145: acquisition has its own ROW bound. p_max_streams is a stream count; handing it to the
      -- acquisition as a row cap turned fat streams into one row per cycle (#714). The caller passes
      -- the rows it can afford; a caller that does not is bounded by the stream count as before.
      -- 202: the ledger is deferred (the last argument) and collected for the one pass below.
      SELECT v_ledger_streams || COALESCE(array_agg(a.stream_id), '{}'),
             v_ledger_partitions || COALESCE(array_agg(a.partition_number), '{}')
      INTO v_ledger_streams, v_ledger_partitions
      FROM __SCHEMA__.claim_orphaned_inbox(
        p_instance_id, v_rank, v_count, v_lease_expiry, v_now, p_partition_count, v_stale_cutoff,
        COALESCE(p_max_rows, p_max_streams), p_allow_steal,
        -- 167: the admission decided once per poll, carrying its size. 170: this is where the band
        -- is withheld; whatever is acquired is re-offered below in full, so a leased idle row can
        -- never be held unoffered.
        v_idle_acquire_rows,
        TRUE
      ) a;
    END IF;

    -- Claim orphaned perspective events, same predicate shape on
    -- wh_perspective_events.
    -- 158: two index probes; see the outbox guard above.
    IF EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events
      WHERE processed_at IS NULL
        AND instance_id IS NULL
      LIMIT 1
    ) OR EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events
      WHERE lease_expiry < v_now
        AND processed_at IS NULL
        AND instance_id IS NOT NULL
      LIMIT 1
    ) THEN
      -- 145 (#719): perspective ACQUISITION has its own bound. The caller passes 0 while its drain channel is
      -- above its cap, so a perspective backlog is drained before more is leased; re-emission of held work
      -- below stays on p_max_streams so the drain keeps moving.
      -- 160: the perspective acquisition gets a ROW bound as well as its stream bound. It used to
      -- take a batch of streams and lease every pending event of each, so one poll leased whatever
      -- a consumer's streams happened to hold and cost accordingly. The caller's own budget is used
      -- when it passes one; otherwise a multiple of the batch, because handing a stream count
      -- straight to an acquisition as a row cap is 145's #714 mistake in reverse.
      -- 202: the ledger is deferred (the last argument) and collected for the one pass below.
      SELECT v_ledger_streams || COALESCE(array_agg(a.stream_id), '{}'),
             v_ledger_partitions || COALESCE(array_agg(a.partition_number), '{}')
      INTO v_ledger_streams, v_ledger_partitions
      FROM __SCHEMA__.claim_orphaned_perspective_events(
        p_instance_id, v_lease_expiry, v_now, COALESCE(p_max_perspective_streams, p_max_streams), v_rank, v_count,
        COALESCE(p_max_rows, GREATEST(COALESCE(p_max_perspective_streams, p_max_streams), 1) * 8),
        TRUE
      ) a;
    END IF;

    -- Claim orphaned receptor work, wh_receptor_processing uses completed_at
    -- (not processed_at) for the "is done" semantic.
    IF EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_receptor_processing
      WHERE completed_at IS NULL
        AND (instance_id IS NULL OR lease_expiry < v_now)
      LIMIT 1
    ) THEN
      PERFORM __SCHEMA__.claim_orphaned_receptor_work(
        p_instance_id, v_rank, v_count, v_lease_expiry, v_now
      );
    END IF;

    -- 202 (#1256): the stream ledger for every stream the acquisitions above leased work on, in ONE stream_id
    -- pass. Each acquisition used to write its own streams' rows before the next one ran, so a claim took its
    -- ledger rows in up to three ascending runs, and two claims could each hold a row the other's later
    -- acquisition needed (rule 5 of the work-table lock order, ai-docs/load-under-bulk-import.md). Written
    -- here, after every queue row this claim locks, it also keeps the work-table order (162): the ledger last.
    --
    -- What a later acquisition saw changes in one case only: a stream an earlier acquisition in this same
    -- call took for the first time is not yet this instance's in the ledger when the later one ranks its
    -- rows, so they are claimed by partition as any unowned stream's are, and by ownership from the next
    -- call on. The emit chain below still reads the ledger after this pass, as it did.
    IF cardinality(v_ledger_streams) > 0 THEN
      PERFORM __SCHEMA__.wh_lease_claimed_streams(p_instance_id, v_now, v_lease_expiry, v_ledger_streams, v_ledger_partitions);
    END IF;

    -- Back-fill wh_event_store + wh_perspective_events for inbox events claimed above.
    -- Replaces legacy process_work_batch Phase 4.5B + 4.6 self-healing, ensures that by the
    -- time an inbox event row reaches InboxDispatchWorker, its event_store row exists and
    -- perspective_events have been created so PerspectiveWorker can pick them up.
    --
    -- v0.683 guard: only call when this instance owns at least one unprocessed
    -- inbox event row with a stream_id. emit_chain's own internal NOT EXISTS
    -- check against wh_event_store filters out already-emitted rows; we don't
    -- repeat that check here because a production measurement
    -- showed the wrapping NOT EXISTS predicate at 42 ms mean (~4-5% of total
    -- DB time) under heavy inbox load, overwhelming the savings from skipping
    -- emit_chain. The simpler EXISTS uses idx_inbox_instance_lease and is
    -- sub-millisecond. The handler-delay backlog scenario where every event_id
    -- is already in wh_event_store is rare and is more appropriately addressed
    -- on the handler side (composite events) than in the work-pump.
    -- 158: only rows the chain has never read (idx_inbox_chain_pending). A holder in steady state
    -- has none, and the poll skips the chain without touching a held row.
    IF EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_inbox_state i
      WHERE i.instance_id = p_instance_id
        AND i.processed_at IS NULL
        AND i.is_event = true
        AND i.stream_id IS NOT NULL
        AND i.chain_emitted_at IS NULL
      LIMIT 1
    ) THEN
      PERFORM __SCHEMA__._emit_event_store_chain_for_inbox(p_instance_id, v_lease_expiry, v_now, p_partition_count);
    END IF;

    -- Return outbox work owned by this instance, oldest first, a batch of it.
    -- 158: a walk of idx_outbox_held_arrival that stops at the batch. The previous shape ranked every
    -- held row per stream with a window function it never read and sorted them all before taking the
    -- batch, so the poll priced itself by the holdings: a busy instance holds thousands of leased rows
    -- and polls several times a second. Same rows in the same order, ties broken by message id.
    RETURN QUERY
    -- Per-stream-drain projection (Phase H step 5b): claim_work returns stream_ids only for
    -- outbox. The OutboxDrainWorker consumes WorkBatch.OutboxStreamIds and pulls full payloads
    -- on demand via fetch_outbox_batch. Body columns are NULL, keeps the bytes-on-the-wire
    -- proportional to the active stream set, not the leased-row count × payload size.
    --
    -- 171: one row per held STREAM, a batch of streams. The drain reads stream ids, and a claim now
    -- leases runs, so returning held rows let one stream's run fill the batch and hide every other
    -- stream this instance holds from the drain. The held rows are walked exactly as before (the
    -- same predicate and order, a Sort + Limit over this instance's leased rows, 166), bounded by
    -- the larger of the stream window and the outbox row bound, and folded to each stream's most
    -- urgent row. A row with no routable stream id is its own stream, as the drain keys it.
    SELECT
      c_source_outbox               AS source,
      h.message_id                  AS work_id,
      h.stream_id                   AS work_stream_id,
      h.partition_number,
      NULL::VARCHAR(200)            AS destination,
      NULL::VARCHAR(500)            AS message_type,
      NULL::VARCHAR(500)            AS envelope_type,
      NULL::TEXT                    AS message_data,
      NULL::JSONB                   AS metadata,
      h.status,
      h.attempts,
      false                         AS is_newly_stored,
      false                         AS is_orphaned,
      NULL::VARCHAR(200)            AS perspective_name,
      h.priority                    AS priority,   -- 166: what this row was scheduled on
      NULL::TIMESTAMPTZ             AS received_at
    FROM (
      SELECT DISTINCT ON (held.stream_key) held.*
      FROM (
        SELECT o.message_id, o.stream_id, o.partition_number, o.status, o.attempts, o.priority, o.created_at,
               CASE WHEN o.stream_id IS NULL OR o.stream_id = __EMPTY_UUID__::uuid
                    THEN o.message_id ELSE o.stream_id END AS stream_key
        FROM __SCHEMA__.wh_outbox o
        WHERE o.instance_id = p_instance_id
          AND o.processed_at IS NULL
          AND o.coalesce_group IS NULL  -- 115: pending singles; the index predicate
          AND o.lease_expiry > v_now
          AND o.published_at IS NULL  -- skip debug-mode forensic rows (production never sets this, row is deleted)
          AND (o.scheduled_for IS NULL OR o.scheduled_for <= v_now)
        -- 166: priority first, arrival as the tiebreak. Every other queue in the path honors the
        -- number the producer declared -- the inbox through its band lanes, the read-model claim by
        -- ordering on it -- and this one selected by arrival alone, so a reply someone was waiting for
        -- published behind whatever bulk work happened to be queued first.
        --
        -- On the NUMBER, not the band, because that is what the priority contract says: scheduling
        -- works on the bucket, and inside a bucket the number orders work.
        --
        -- This costs no index HERE. The branch is already narrowed to one instance's leased rows by
        -- idx_outbox_outstanding_by_instance, and it already sorts, because that index carries
        -- lease_expiry rather than created_at. Measured on a deployed database the plan is identical
        -- either way (Sort, cost 1.12..1.13), so priority joins an existing sort key. The ACQUISITION
        -- side is the one that needed idx_outbox_pending_priority_arrival (166); that restraint
        -- matters because the outbox is the hottest write path during a bulk load.
        ORDER BY o.priority, o.created_at, o.message_id
        LIMIT GREATEST(p_max_streams, v_outbox_bound, 0)
      ) held
      ORDER BY held.stream_key, held.priority, held.created_at, held.message_id
    ) h
    ORDER BY h.priority, h.created_at, h.message_id
    LIMIT p_max_streams;

    -- v0.661: track this category's RETURN QUERY rowcount so the drain-mode
    -- hint at function end can be derived from ROW_COUNT instead of a fresh
    -- COUNT(*) scan. See drain-mode hint block below.
    GET DIAGNOSTICS v_outbox_rows = ROW_COUNT;

    -- Return inbox work owned by this instance: one row per lane a held stream appears in, a batch
    -- of them.
    -- 158: the poll is priced by the batch, never by the holdings. The streams an instance holds are
    -- enumerated through idx_inbox_held_lanes one index-only probe per stream, lane by lane in the
    -- order the batch is ordered: most urgent bucket first (150), commands before events inside a
    -- bucket (145). Inside a lane the fresh and retried classes are walked side by side (126), each
    -- able to take the whole batch, because the share must not hold a slot empty when the other
    -- class is: what apportions them is the interleave below, not the walks. A lane's walk starts at
    -- a stream id drawn per poll and wraps once, so a lane larger than the batch does not enumerate
    -- the same streams on every poll; a lane no larger than the batch is enumerated whole. One step
    -- lands on one stream, so a lane costs exactly the streams it enumerates.
    --
    -- One row stands for a held stream in a lane: the oldest row the instance holds of it there,
    -- carrying that row's number, arrival and attempts. A stream whose rows span lanes is returned
    -- once per lane, exactly as before, and the batch hooks fold those rows to the stream's most
    -- urgent number and oldest arrival (ClaimedInboxStreamFolder), unchanged. What is no longer
    -- returned is a stream's further rows inside one lane: the drain consumes stream ids and pulls a
    -- stream's rows on demand (fetch_inbox_batch), so they carried nothing a caller read, and ranking
    -- them is what cost the poll. A stream met in both classes of a lane stands as its retried row,
    -- which is its head -- an attempt is charged at the head and everything older is processed, so a
    -- stream with a retried row has a retried head, and stream FIFO means the fresh rows behind it
    -- cannot dispatch anyway (126). The previous shape ranked every held row with three window
    -- functions on every poll and fetched every held row's heap page to do it, and a busy instance
    -- holds thousands of rows and polls several times a second.
    --
    -- GREATEST drops a NULL, so a NULL batch leaves nothing to fill and the re-offer returns nothing.
    -- Nothing passes NULL (the parameter defaults to 1000 and the coordinators pass an integer), and
    -- returning nothing is the safe reading of "no batch size": the previous LIMIT NULL meant no
    -- bound at all, which is the failure this migration exists to stop.
    v_remaining := GREATEST(p_max_streams, 0);
    <<inbox_lanes>>
    FOR v_bucket IN 0..3 LOOP
      -- 170: the idle band (bucket 3) is re-offered like every other bucket. These walks visit only
      -- rows this instance already holds under a live lease, so there is nothing left to withhold:
      -- the lease is taken and the attempt charged. 167 skipped the bucket unless the band was
      -- admitted, and acquisition takes idle commands and lapsed idle rows regardless, so a held row
      -- could sit here unoffered, lapse, be re-acquired and charged again, for ever -- never reaching
      -- the dispatcher that enforces the attempt ceiling. Admission bounds ACQUISITION
      -- (claim_orphaned_inbox's p_idle_max_rows); it never hides what is already held.
      FOREACH v_is_event IN ARRAY ARRAY[false, true] LOOP
        EXIT inbox_lanes WHEN v_remaining <= 0;
        v_start := gen_random_uuid();
        RETURN QUERY
        WITH RECURSIVE lanes AS (
          -- Two walks side by side, one per class, seeded at the drawn point. A seed is a bound and
          -- not a candidate.
          SELECT c.fresh_lane, v_start AS stream_id, false AS wrapped, true AS is_seed, 0 AS steps,
                 NULL::INTEGER AS priority, NULL::UUID AS message_id, NULL::TIMESTAMPTZ AS received_at,
                 NULL::INTEGER AS attempts, NULL::INTEGER AS partition_number, NULL::INTEGER AS status
          FROM (VALUES (true), (false)) AS c(fresh_lane)
          UNION ALL
          SELECT h.fresh_lane, n.stream_id, n.wrapped, false, h.steps + 1,
                 n.priority, n.message_id, n.received_at, n.attempts, n.partition_number, n.status
          FROM lanes h
          CROSS JOIN LATERAL (
            -- The lane's next stream after the current one, up to the drawn point once wrapped. The
            -- index is keyed (holder, bucket, kind, class, stream, arrival, id) and covers every
            -- column below, so this is one index-only probe: it lands on the stream's oldest row in
            -- the lane and the next stream is one probe away. The upper bound is written as a CASE
            -- rather than an OR so it stays an index condition; as a filter the walk would read past
            -- the drawn point instead of stopping at it.
            (SELECT i.stream_id, i.priority, i.message_id, i.received_at, i.attempts,
                    i.partition_number, i.status, h.wrapped AS wrapped
             FROM __SCHEMA__.wh_inbox_state i
             WHERE i.instance_id = p_instance_id
               AND i.processed_at IS NULL
               AND (CASE WHEN i.priority <= 99 THEN 0 WHEN i.priority <= 199 THEN 1 WHEN i.priority <= 399 THEN 2 ELSE 3 END) = v_bucket
               AND i.is_event = v_is_event
               AND (i.attempts = 0) = h.fresh_lane
               AND i.lease_expiry > v_now
               AND i.stream_id > h.stream_id
               AND i.stream_id <= CASE WHEN h.wrapped THEN v_start ELSE __MAX_UUID__::UUID END
             ORDER BY i.stream_id, i.received_at, i.message_id
             LIMIT 1)
            UNION ALL
            -- ...or, past the lane's last stream, its first one: the walk wraps, once.
            (SELECT i.stream_id, i.priority, i.message_id, i.received_at, i.attempts,
                    i.partition_number, i.status, true
             FROM __SCHEMA__.wh_inbox_state i
             WHERE i.instance_id = p_instance_id
               AND i.processed_at IS NULL
               AND (CASE WHEN i.priority <= 99 THEN 0 WHEN i.priority <= 199 THEN 1 WHEN i.priority <= 399 THEN 2 ELSE 3 END) = v_bucket
               AND i.is_event = v_is_event
               AND (i.attempts = 0) = h.fresh_lane
               AND i.lease_expiry > v_now
               AND NOT h.wrapped
               AND i.stream_id <= v_start
             ORDER BY i.stream_id, i.received_at, i.message_id
             LIMIT 1)
            LIMIT 1
          ) n
          -- One step, one stream: each class may take the whole batch, and the interleave apportions.
          WHERE h.steps < v_remaining
        ),
        inbox_candidates AS (
          -- A stream met in both classes stands as its retried row: that row is its head (126).
          SELECT DISTINCT ON (l.stream_id)
                 l.stream_id, l.priority, l.message_id, l.received_at, l.attempts,
                 l.partition_number, l.status
          FROM lanes l
          WHERE NOT l.is_seed
          ORDER BY l.stream_id, l.fresh_lane
        ),
        ranked_inbox AS (
          SELECT c.stream_id, c.priority, c.message_id, c.received_at, c.attempts,
                 c.partition_number, c.status,
                 (c.attempts = 0) AS is_fresh,
                 ROW_NUMBER() OVER (PARTITION BY (c.attempts = 0) ORDER BY c.received_at, c.message_id) AS class_rank
          FROM inbox_candidates c
        ),
        ordered_inbox AS (
          SELECT ri.stream_id, ri.priority, ri.message_id, ri.received_at, ri.attempts,
                 ri.partition_number, ri.status
          FROM ranked_inbox ri
          ORDER BY
            -- 126: fresh-head streams receive p_fresh_share of the batch, retried-head streams the
            -- remainder, each class in arrival order; an empty class hands its share to the other,
            -- because the key only competes candidates that exist.
            CASE WHEN ri.is_fresh
                 THEN (ri.class_rank - 1)::DOUBLE PRECISION / GREATEST(LEAST(p_fresh_share, 1.0), 0.000001)
                 ELSE (ri.class_rank - 1)::DOUBLE PRECISION / GREATEST(1.0 - LEAST(p_fresh_share, 1.0), 0.000001)
            END,
            ri.received_at,
            ri.message_id
          LIMIT v_remaining
        )
        -- Per-stream-drain projection (Phase H step 5d): inbox follows outbox into stream-ids-only.
        -- InboxDrainWorker reads stream_ids off IInboxDrainChannel and pulls payloads on demand
        -- via fetch_inbox_batch. Body columns are NULL -- keeps claim_work's bytes-on-the-wire
        -- proportional to active stream count.
        SELECT
          c_source_inbox                AS source,
          oi.message_id                 AS work_id,
          oi.stream_id                  AS work_stream_id,
          oi.partition_number,
          NULL::VARCHAR(200)            AS destination,
          NULL::VARCHAR(500)            AS message_type,
          NULL::VARCHAR(500)            AS envelope_type,
          NULL::TEXT                    AS message_data,
          NULL::JSONB                   AS metadata,
          oi.status,
          oi.attempts,
          false                         AS is_newly_stored,
          false                         AS is_orphaned,
          NULL::VARCHAR(200)            AS perspective_name,
          oi.priority                   AS priority,
          oi.received_at                AS received_at
        FROM ordered_inbox oi;

        GET DIAGNOSTICS v_rows = ROW_COUNT;
        v_remaining := v_remaining - v_rows;
      END LOOP;
    END LOOP;

    -- v0.661: the category's row count feeds the drain-mode hint at the end.
    v_inbox_rows := GREATEST(p_max_streams, 0) - v_remaining;

    -- Return receptor work owned by this instance.
    -- Receptor work uses `id` as the work_id (not message_id) and `completed_at` as the "done" marker.
    -- Most fields are NULL, receptors carry their state in dedicated columns the worker reads
    -- directly via the work_id; the row here just signals "this receptor needs attention".
    RETURN QUERY
    SELECT
      'receptor'::VARCHAR(20)       AS source,
      rp.id                         AS work_id,
      rp.stream_id                  AS work_stream_id,
      rp.partition_number,
      NULL::VARCHAR(200)            AS destination,
      NULL::VARCHAR(500)            AS message_type,
      NULL::VARCHAR(500)            AS envelope_type,
      NULL::TEXT                    AS message_data,
      NULL::JSONB                   AS metadata,
      rp.status::INTEGER,
      rp.attempts,
      false                         AS is_newly_stored,
      false                         AS is_orphaned,
      NULL::VARCHAR(200)            AS perspective_name,
      NULL::INTEGER                 AS priority,
      NULL::TIMESTAMPTZ             AS received_at
    FROM __SCHEMA__.wh_receptor_processing rp
    WHERE rp.instance_id = p_instance_id
      AND rp.lease_expiry > v_now
      AND rp.completed_at IS NULL
    LIMIT p_max_streams;

    -- v0.661: see outbox block above.
    GET DIAGNOSTICS v_receptor_rows = ROW_COUNT;

    -- Return perspective work as one row per distinct stream owned by this instance, most urgent
    -- bucket first, oldest event first within a bucket, a batch of streams.
    -- 158: the poll is priced by the batch, never by the holdings. The streams an instance holds are
    -- enumerated through idx_perspective_held_lanes one index-only probe per stream, bucket by bucket,
    -- most urgent first; the first entry of a stream in its bucket is its oldest held event there,
    -- which is what orders the streams within the bucket. The walk starts at a stream id drawn per
    -- poll and wraps once, so a bucket larger than the batch does not enumerate the same streams on
    -- every poll, and one no larger than the batch is enumerated whole. One step lands on one stream,
    -- so a bucket costs exactly the streams it enumerates. The previous shape aggregated every held
    -- event per poll to put streams with a hundred or fewer pending events ahead of larger ones; that
    -- tier guarded a batch of rows against one large stream, and the drain has been per stream with an
    -- unbounded channel since Phase H, so a large stream no longer displaces small ones.
    v_remaining := GREATEST(p_max_streams, 0);
    FOR v_bucket IN 0..3 LOOP
      -- 167: the idle band is bucket 3 and is skipped unless time or quiet admitted it.
      CONTINUE WHEN v_bucket = 3 AND NOT v_idle_admitted;
      IF v_bucket = 3 AND v_idle_capped THEN
        -- admitted by trickle, not by quiet: a slice, never a burst.
        v_remaining := LEAST(v_remaining, GREATEST(p_idle_trickle_slice, 0));
      END IF;
      EXIT WHEN v_remaining <= 0;
      v_start := gen_random_uuid();
      RETURN QUERY
      WITH RECURSIVE lane AS (
        -- Seeded at the drawn point; the seed is not a candidate.
        SELECT v_start AS stream_id, NULL::UUID AS event_id, false AS wrapped, true AS is_seed, 0 AS steps
        UNION ALL
        SELECT n.stream_id, n.event_id, n.wrapped, false, h.steps + 1
        FROM lane h
        CROSS JOIN LATERAL (
          -- The bucket's next stream after the current one, up to the drawn point once wrapped...
          (SELECT pe.stream_id, pe.event_id, h.wrapped AS wrapped
           FROM __SCHEMA__.wh_perspective_events pe
           WHERE pe.instance_id = p_instance_id
             AND pe.processed_at IS NULL
             AND (CASE WHEN pe.priority <= 99 THEN 0 WHEN pe.priority <= 199 THEN 1 WHEN pe.priority <= 399 THEN 2 ELSE 3 END) = v_bucket
             AND pe.lease_expiry > v_now
             AND pe.stream_id > h.stream_id
             AND pe.stream_id <= CASE WHEN h.wrapped THEN v_start ELSE __MAX_UUID__::UUID END
           ORDER BY pe.stream_id, pe.event_id
           LIMIT 1)
          UNION ALL
          -- ...or, past the bucket's last stream, its first one: the walk wraps.
          (SELECT pe.stream_id, pe.event_id, true
           FROM __SCHEMA__.wh_perspective_events pe
           WHERE pe.instance_id = p_instance_id
             AND pe.processed_at IS NULL
             AND (CASE WHEN pe.priority <= 99 THEN 0 WHEN pe.priority <= 199 THEN 1 WHEN pe.priority <= 399 THEN 2 ELSE 3 END) = v_bucket
             AND pe.lease_expiry > v_now
             AND NOT h.wrapped
             AND pe.stream_id <= v_start
           ORDER BY pe.stream_id, pe.event_id
           LIMIT 1)
          LIMIT 1
        ) n
        WHERE h.steps < v_remaining
      )
      SELECT
        'perspective_stream'::VARCHAR(20) AS source,
        NULL::UUID                        AS work_id,
        l.stream_id                       AS work_stream_id,
        NULL::INTEGER                     AS partition_number,
        NULL::VARCHAR(200)                AS destination,
        NULL::VARCHAR(500)                AS message_type,
        NULL::VARCHAR(500)                AS envelope_type,
        NULL::TEXT                        AS message_data,
        NULL::JSONB                       AS metadata,
        0::INTEGER                        AS status,
        0::INTEGER                        AS attempts,
        false                             AS is_newly_stored,
        false                             AS is_orphaned,
        NULL::VARCHAR(200)                AS perspective_name,
        NULL::INTEGER                     AS priority,
        NULL::TIMESTAMPTZ                 AS received_at
      FROM lane l
      WHERE NOT l.is_seed
      ORDER BY l.event_id, l.stream_id
      LIMIT v_remaining;

      GET DIAGNOSTICS v_rows = ROW_COUNT;
      v_remaining := v_remaining - v_rows;
    END LOOP;

    -- v0.661: the category's row count feeds the drain-mode hint at the end.
    v_perspective_rows := GREATEST(p_max_streams, 0) - v_remaining;

    -- 130 doorbell debounce: finding work stamps this instance's watermark, the signal
    -- producers use to suppress redundant notifies while this drainer is awake. Rides
    -- inside the claim (zero extra round trips) and skips the empty case so the
    -- empty-call short-circuit's ~1 ms idle floor is untouched.
    -- 140: the stamp never waits (issue #699). The store's debounce touches the same rows inside
    -- its own transaction; under a burst the two met in opposite orders with the inbox row locks
    -- and deadlocked. A watermark row another session holds is skipped this tick: the stamp is a
    -- freshness hint and the next tick re-stamps it. Rows that do not exist yet are created; rows
    -- that exist and are free are stamped in place.
    WITH kinds AS (
      SELECT k.kind
      FROM (VALUES (c_source_outbox), (c_source_inbox), (__CATEGORY_PERSPECTIVE__)) AS k(kind)
    WHERE (k.kind = c_source_outbox AND v_outbox_rows > 0)
       OR (k.kind = c_source_inbox AND (v_inbox_rows > 0 OR v_receptor_rows > 0))
       -- 133: the perspective watermark must reflect DRAINABLE progress, not merely a
       -- claimed Stored-but-fence-held row. claim_work returns a perspective_stream row for
       -- any leased unprocessed perspective_event, but a row whose underlying event is still
       -- unstamped (commit_sequence IS NULL, held by the per-database ordering fence) cannot
       -- be surfaced by the fetch gate, the drainer spins with no progress. Arming the
       -- watermark for it lets the doorbell debounce (130/131) suppress the fence-clearing
       -- stamp's make-up ring, stranding visibility on the adaptive poll cap (issue #677).
       -- The EXISTS runs at most once: the k.kind guard short-circuits it away for the
       -- outbox/inbox VALUES rows.
       -- 158: the question is asked of a batch of the rows leased longest, not of every held row.
       -- Over all of them it was priced by the holdings whenever the fence held (#677 is exactly
       -- that state), joining every leased row to the event store per poll. A held row whose event is
       -- stamped is found among the oldest leases if it is found at all; when none of them is, the
       -- watermark stays unarmed and the make-up ring goes through, which is the safe direction.
       OR (k.kind = __CATEGORY_PERSPECTIVE__ AND v_perspective_rows > 0 AND EXISTS (
             SELECT 1
             FROM (
               SELECT pe.event_id
               FROM __SCHEMA__.wh_perspective_events pe
               WHERE pe.instance_id = p_instance_id
                 AND pe.lease_expiry > v_now
                 AND pe.processed_at IS NULL
               ORDER BY pe.lease_expiry
               LIMIT GREATEST(p_max_streams, 1)
             ) held
             JOIN __SCHEMA__.wh_event_store es ON es.event_id = held.event_id
             WHERE es.commit_sequence IS NOT NULL))
    ),
    lockable AS (
      SELECT ns.instance_id, ns.payload_kind
      FROM __SCHEMA__.wh_notify_state ns
      JOIN kinds ON kinds.kind = ns.payload_kind
      WHERE ns.instance_id = p_instance_id
      FOR UPDATE OF ns SKIP LOCKED
    ),
    stamped AS (
      UPDATE __SCHEMA__.wh_notify_state ns
      SET last_work_at = NOW()
      FROM lockable
      WHERE ns.instance_id = lockable.instance_id AND ns.payload_kind = lockable.payload_kind
      RETURNING ns.payload_kind
    )
    INSERT INTO __SCHEMA__.wh_notify_state (instance_id, payload_kind, last_work_at)
    SELECT p_instance_id, kinds.kind, NOW()
    FROM kinds
    WHERE NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_notify_state ns2
      WHERE ns2.instance_id = p_instance_id AND ns2.payload_kind = kinds.kind)
    ON CONFLICT (instance_id, payload_kind) DO NOTHING;

    -- Drain-mode hint: if any of the four return categories filled its LIMIT
    -- (rows == p_max_streams), there's likely more eligible work for this
    -- instance, RAISE NOTICE so the C# claim worker skips its wait and
    -- re-polls immediately. Survives pgbouncer (protocol message, not a
    -- session-state thing).
    --
    -- v0.661 forensic (gate.hold_duration_ms histogram during a consumer's
    -- draft-job import): the prior implementation ran four separate
    -- COUNT(*) queries here (one per category, plus a COUNT(DISTINCT
    -- stream_id) on wh_perspective_events). Under import load with millions
    -- of leased rows per instance, those counts dominated claim_work hold
    -- time, ClaimWorkAsync at p99 5031 ms / avg 128 ms. We don't need
    -- exact counts; we only need to know whether ANY category filled its
    -- LIMIT. ROW_COUNT after each RETURN QUERY gives us that for free.
    IF v_outbox_rows = p_max_streams
       OR v_inbox_rows = p_max_streams
       OR v_receptor_rows = p_max_streams
       OR v_perspective_rows = p_max_streams THEN
      RAISE NOTICE 'whizbang.has_more=true';
    END IF;

    -- 171: the outbox acquisition leased its whole row bound, so the backlog it was taken from is
    -- at least that large again. Distinct from has_more above, which also fires when this instance
    -- merely HOLDS a full batch (a re-offer), and a claim loop that re-claimed on a re-offer would
    -- spin. This one is only ever raised by NEW work, so the claim loop may claim again at once;
    -- what bounds that loop is the row bound itself, which the caller sizes from what it holds.
    IF v_outbox_bound > 0 AND v_outbox_acquired >= v_outbox_bound THEN
      RAISE NOTICE 'whizbang.outbox_acquisition_full=true';
    END IF;
  END;

  RETURN;
END;
-- 157: custom plans for the poll and everything it calls. The queue tables are empty between loads,
-- and a session that polled while they were empty kept generic plans made for empty tables; once
-- the tables filled those plans scanned them whole, nested, on every poll, until the next analyze
-- invalidated them. Measured: a poll that takes well under a second with fresh plans did not finish
-- inside the command timeout with the empty-table plans. Planning per call costs milliseconds.
$$ LANGUAGE plpgsql SET plan_cache_mode = force_custom_plan;

COMMENT ON FUNCTION __SCHEMA__.claim_work(UUID, TEXT, TEXT, INTEGER, INTEGER, INTEGER, INTEGER, DOUBLE PRECISION, INTEGER, BOOLEAN, INTEGER, BOOLEAN, INTERVAL, INTEGER, INTERVAL, INTEGER, INTEGER, BIGINT, BIGINT, BOOLEAN) IS
  'The claim poll: acquires orphaned and unowned work for an instance (outbox, inbox, perspective events, receptor '
  'work) and returns what it holds. 196: never writes the caller''s registration. 202: the acquisitions defer the '
  'stream ledger, and it is written once for every stream they leased (wh_lease_claimed_streams), in one stream_id '
  'pass after the last queue row is locked. 203: a cached partition assignment (epoch, revision) that is still '
  'published and unexpired supplies the rank and member count; otherwise the claim ranks itself and raises '
  'whizbang.partition_assignment_stale. 204: a peer is ranked live by a fresh heartbeat or, when it is direct, by its '
  'held alive-lock (wh_direct_alive_lock_holders); p_alive_lock_held keeps a direct caller holding its lock from being '
  'asked to re-register between the slow beats of its heartbeat.';
