-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 367_TheClaimRunsOnlyWhatCanFindWork.sql
-- Date: 2026-11-08
-- Description: The claim runs only the statements that can find something, cutting the planning it pays on every call.
--
--              claim_work runs under plan_cache_mode = force_custom_plan (157), and the functions it calls inherit it.
--              On a pooled connection reset between calls (DISCARD ALL drops every cached plan, plpgsql's included) a
--              statement is planned each time it executes, so the claim's fixed cost is the number of statements it runs.
--              Planning was measured at about half of the claim, and three groups of statements ran with nothing to do:
--
--                Re-offer walks   the inbox walk ran once per lane (four buckets, two kinds) and the perspective walk
--                                 once per bucket, each planned afresh, though most lanes held nothing. One statement
--                                 now asks every lane of both whether the instance holds a pending row there (the
--                                 first entry of its held-lanes index), and a walk runs only for a lane that does.
--                                 That question is wider than the walk's own predicate, so a lane it calls empty
--                                 returns nothing.
--                Empty store pass _emit_event_store_chain_for_inbox stops after its lock pass when the lock pass finds
--                                 no candidate, running only the stamp for rows whose event is already stored.
--                Collective route the inbox pass's collective routing insert runs only when an event it stored carries
--                                 the collective flag, read from the stored rows.
--
--              Nothing either function returns, stores, stamps or refuses changes. plan_cache_mode is not changed: under
--              a reset pooled connection a generic plan would be rebuilt on every call anyway, so relaxing it saves
--              nothing there and risks the empty-table plans 157 exists to prevent.
--
--              Number 367: 359 to 366 are taken by changes in flight.
--
-- Dependencies: 349 (claim_work's last word), 352 (_emit_event_store_chain_for_inbox's last word), each copied whole
--               with the changes above; idx_inbox_state_held_lanes, idx_perspective_held_lanes
-- Objects: claim_work, _emit_event_store_chain_for_inbox
-- Constants: the double-underscore tokens in this file (for example __CATEGORY_OUTBOX__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

-- ---------------------------------------------------------------------------------------------
-- claim_work: copied from its last word (349), a re-offer walk run only for a lane the instance holds a row in.
-- ---------------------------------------------------------------------------------------------
-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('claim_work');

-- <docs>fundamentals/work-coordinator/claim-loop</docs>
-- <docs>fundamentals/work-coordinator/claim-loop#the-claim-runs-only-what-can-find-work</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimFixedCostSqlTests.cs:InboxReOffer_AnInstanceHoldingOneLane_WalksOnlyThatLane_AndOffersItsStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimFixedCostSqlTests.cs:InboxReOffer_ALaneHoldingOnlyUnfetchableRows_IsStillWalked_AndOffersNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimFixedCostSqlTests.cs:Claim_AnInstanceHoldingNothing_RunsNoReOfferWalkAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimFixedCostSqlTests.cs:PerspectiveReOffer_AnInstanceHoldingOneBucket_WalksOnlyThatBucket_AndOffersItsStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimFixedCostSqlTests.cs:PerspectiveReOffer_TheIdleBucketWhileWithheld_IsNotOffered_AndNotWalkedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeldBackEventsDoNotCrowdTheReOfferTests.cs:OneStreamReOffer_PassesOverAHeldBackEvent_ToAFetchableCheckpointAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeldBackEventsDoNotCrowdTheReOfferTests.cs:HeldBackStream_OnceItsPredecessorIsHeldToo_IsStoredAndReOfferedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimReofferPlanShapeTests.cs:TheReOfferWalk_ReadsTheWorkTableOnlyThroughItsHeldLanesIndexAsync</tests>
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
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkEligibilitySqlTests.cs:Rank_APeerInMigrating_IsNotCounted_SoTheCallerCoversEveryPartitionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkEligibilitySqlTests.cs:Rank_APeerThatReachesAcceptingCommands_IsCountedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkEligibilitySqlTests.cs:Rank_ADrainingPeer_ItsShareMovesToTheCallerAtOnceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkEligibilitySqlTests.cs:Rank_ADirectPeerHoldingItsLockInMigrating_IsNotCountedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkEligibilitySqlTests.cs:Rank_AnOlderPeerThatRecordsNoPhase_IsCountedAsBeforeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkEligibilitySqlTests.cs:Rank_ACallerThatIsNotYetEligible_StillCountsItselfAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkEligibilitySqlTests.cs:Claim_ADrainingCaller_StillClaimsItsShareAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkEligibilitySqlTests.cs:Claim_ACallerThatRecordsNoPhase_StillClaimsItsShareAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkEligibilitySqlTests.cs:Claim_ARefusedCallerWhoseRowIsStale_IsStillAskedToRegisterAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkEligibilitySqlTests.cs:Notice_ACallerWhoseRowRecordsAnotherPhase_IsAskedToRegisterAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkEligibilitySqlTests.cs:Notice_ACallerWhoseRowRecordsItsPhase_IsNotAskedToRegisterAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkEligibilitySqlTests.cs:Notice_ADrainingCallerWhoseRowSaysSo_IsNotAskedToRegisterAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkEligibilitySqlTests.cs:Notice_ACallerThatSaysNothingOfItsPhase_IsJudgedAsBeforeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LivenessRuleReadersSqlTests.cs:Rank_ADirectPeerThatLostItsLock_IsNotCountedPastOneBeatAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LivenessRuleReadersSqlTests.cs:Rank_ADirectPeerWithoutItsLock_IsCountedInsideOneBeatAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LivenessRuleReadersSqlTests.cs:Rank_APeerInsideItsOwnWindow_IsCountedPastTheOldThirtySecondCutoffAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LivenessRuleReadersSqlTests.cs:Caller_LiveByTheRule_IsNotAskedToReRegisterAsync</tests>
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
  p_alive_lock_held BOOLEAN DEFAULT FALSE,
  -- 229 (#1638): the lifecycle phase the caller announces. When its row records another phase (a transition write that
  -- was lost, or overwritten by a beat built before it), the registration is reported stale and the caller's
  -- registration writes the phase, so a ready instance cannot stay ineligible for work while it claims. NULL (an older
  -- caller): the row is judged by liveness alone, as before.
  p_lifecycle_phase TEXT DEFAULT NULL
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

  -- 207 (#1349): plan the acquisitions below on statistics that describe the tables as they are. A queue table
  -- analyzed while empty and filled since, with its autovacuum worker stuck behind the claim scans, made every
  -- candidate index look free, and one claim read the backlog once per stream it captured. On a healthy database
  -- this is a catalog read per table and analyzes nothing; autovacuum always gets there first.
  PERFORM __SCHEMA__.wh_refresh_stale_queue_statistics();

  -- Non-empty path: claim outbox work and return it.
  -- Inbox / receptor / perspective claim + return land in subsequent TDD cycles.
  DECLARE
    v_now TIMESTAMPTZ := NOW();
    v_lease_expiry TIMESTAMPTZ := v_now + (p_lease_seconds || ' seconds')::INTERVAL;
    v_stale_cutoff TIMESTAMPTZ := v_now - INTERVAL '30 seconds';
    v_rank INTEGER;
    -- 203: set when the cached assignment passed the fence and supplied the rank.
    v_assigned BOOLEAN := FALSE;
    -- 300 (#1636): the caller's own row, read once by the registration probe.
    v_caller __SCHEMA__.wh_service_instances%ROWTYPE;
    v_caller_found BOOLEAN;
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
    -- 367: whether this instance holds any pending row in each re-offer lane, read once before the walks. Inbox lanes
    -- are indexed bucket * 2 + 1 for commands and bucket * 2 + 2 for events; perspective buckets are bucket + 1.
    v_inbox_lanes_held BOOLEAN[];
    v_perspective_buckets_held BOOLEAN[];
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
    -- 229 (#1638): and a row that records another phase than the one the caller announces is stale as well; the same
    -- primary-key probe reads it, so the check costs nothing.
    -- 300 (#1636): the same primary-key probe reads the caller's row once, for the registration check and for whether
    -- the caller may claim at all. No second lookup: the probe already fetched this row.
    SELECT * INTO v_caller FROM __SCHEMA__.wh_service_instances WHERE instance_id = p_instance_id;
    v_caller_found := FOUND;
    IF NOT v_caller_found
       OR NOT __SCHEMA__.wh_instance_liveness(v_caller, p_alive_lock_held)
       OR NOT (p_lifecycle_phase IS NULL OR v_caller.lifecycle_phase = p_lifecycle_phase) THEN
      RAISE NOTICE 'whizbang.instance_registration_stale=true';
    END IF;

    -- 300 (#1636): a caller whose own row says it may not claim (not eligible for work, and not draining) gets nothing.
    -- Its share is already its peers': they rank it out (229) and the assigner leaves it out (229), so refusing it
    -- leaves nothing unclaimed. A caller with no row is served as before: a running instance whose row was reaped, or
    -- one that has not registered yet, re-registers on the notice above and is ranked as live meanwhile.
    IF v_caller_found AND NOT __SCHEMA__.wh_instance_may_claim(v_caller) THEN
      RETURN;
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
        -- 229 (#1638): and only a peer eligible for work is counted (wh_instance_is_eligible_for_work): alive is not
        -- ready, and a starting or draining peer's share must go to the others at once. Read from the rows this scan
        -- already reads; the caller is counted below whatever its own row says, as it always was.
        SELECT si.instance_id
        FROM __SCHEMA__.wh_service_instances si
        WHERE __SCHEMA__.wh_instance_liveness(si, si.instance_id IN (SELECT h.instance_id FROM __SCHEMA__.wh_direct_alive_lock_holders() h))
          AND __SCHEMA__.wh_instance_is_eligible_for_work(si)
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
    -- 367: a walk runs only for a lane the instance holds a row in. Under plan_cache_mode = force_custom_plan, and on a
    -- pooled connection that is reset between calls, every execution of a statement is planned afresh, so the eight
    -- inbox walks below and the perspective walks after them were a plan each per claim, most of them for lanes holding
    -- nothing (on a busy service the walks returned a row on about one execution in five). One statement asks every
    -- lane of both loops instead: one correlated probe per lane, planned once, each the first entry of its held-lanes
    -- index under (instance, bucket[, kind]), so it costs the same whatever the instance holds. It asks only whether a
    -- pending row is held there, which every row a walk can land on satisfies, so a lane it reports empty is one whose
    -- walk would return nothing; a lane holding only rows a walk passes over (a lapsed lease, an unstored event) is
    -- walked exactly as before. Read after the store pass, which is what makes rows fetchable, and after every
    -- acquisition, in this transaction; nothing between here and the perspective walks writes a perspective event.
    SELECT
      (SELECT array_agg(EXISTS (
                SELECT 1 FROM __SCHEMA__.wh_inbox_state i
                WHERE i.instance_id = p_instance_id
                  AND i.processed_at IS NULL
                  AND (CASE WHEN i.priority <= 99 THEN 0 WHEN i.priority <= 199 THEN 1 WHEN i.priority <= 399 THEN 2 ELSE 3 END) = l.bucket
                  AND i.is_event = l.is_event) ORDER BY l.lane)
       FROM (VALUES (1, 0, false), (2, 0, true), (3, 1, false), (4, 1, true),
                    (5, 2, false), (6, 2, true), (7, 3, false), (8, 3, true)) AS l(lane, bucket, is_event)),
      (SELECT array_agg(EXISTS (
                SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
                WHERE pe.instance_id = p_instance_id
                  AND pe.processed_at IS NULL
                  AND (CASE WHEN pe.priority <= 99 THEN 0 WHEN pe.priority <= 199 THEN 1 WHEN pe.priority <= 399 THEN 2 ELSE 3 END) = l.bucket)
                ORDER BY l.bucket)
       FROM (VALUES (0), (1), (2), (3)) AS l(bucket))
    INTO v_inbox_lanes_held, v_perspective_buckets_held;
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
        -- 367: an empty lane is not walked; see the lane probe above.
        CONTINUE WHEN NOT v_inbox_lanes_held[v_bucket * 2 + CASE WHEN v_is_event THEN 2 ELSE 1 END];
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
               -- 349 (#2117): fetch_inbox_batch never returns an event the store pass left unstored, so the
               -- walk does not land on one; a stream whose held rows are all such events is not offered.
               AND (NOT i.is_event OR i.chain_emitted_at IS NOT NULL)
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
               -- 349 (#2117): fetch_inbox_batch never returns an event the store pass left unstored, so the
               -- walk does not land on one; a stream whose held rows are all such events is not offered.
               AND (NOT i.is_event OR i.chain_emitted_at IS NOT NULL)
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
      -- 367: an empty bucket is not walked (the lane probe before the inbox walks). Skipped where the walk would run, after the trickle bound above, so the
      -- remaining count, and the perspective row count derived from it below, are what they were.
      CONTINUE WHEN NOT v_perspective_buckets_held[v_bucket + 1];
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
COMMENT ON FUNCTION __SCHEMA__.claim_work(UUID, TEXT, TEXT, INTEGER, INTEGER, INTEGER, INTEGER, DOUBLE PRECISION, INTEGER, BOOLEAN, INTEGER, BOOLEAN, INTERVAL, INTEGER, INTERVAL, INTEGER, INTEGER, BIGINT, BIGINT, BOOLEAN, TEXT) IS
  'The claim poll: acquires orphaned and unowned work for an instance (outbox, inbox, perspective events, receptor '
  'work) and returns what it holds. 196: never writes the caller''s registration. 202: the acquisitions defer the '
  'stream ledger, and it is written once for every stream they leased (wh_lease_claimed_streams), in one stream_id '
  'pass after the last queue row is locked. 203: a cached partition assignment (epoch, revision) that is still '
  'published and unexpired supplies the rank and member count; otherwise the claim ranks itself and raises '
  'whizbang.partition_assignment_stale. 204: a peer is ranked live by a fresh heartbeat or, when it is direct, by its '
  'held alive-lock (wh_direct_alive_lock_holders); p_alive_lock_held keeps a direct caller holding its lock from being '
  'asked to re-register between the slow beats of its heartbeat. 229: only a peer eligible for work '
  '(wh_instance_is_eligible_for_work) is ranked, and p_lifecycle_phase reports a registration that records another '
  'phase than the caller''s stale. 300: a caller whose own row may not claim (wh_instance_may_claim: not eligible '
  'for work and not draining) gets nothing; a caller with no row is served as before. 349: the inbox re-offer skips '
  'a held event the store pass left unstored, which the drain cannot fetch. 367: a re-offer walk runs only for a lane '
  'the instance holds a pending row in, read for every lane of both walks in one statement before them.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- <docs>fundamentals/events/stream-end-of-life#the-stream-lifecycle-ledger</docs>
-- <docs>fundamentals/events/stream-end-of-life#a-sealed-stream-refuses-appends</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:InboxAppend_CreatesAndAdvancesTheRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceivedStreamOrderSqlTests.cs:TwoClaimsOfOneStream_TheHeadsClaimCommitsLast_VersionsFollowTheProducerOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AuditDigestLaneSqlTests.cs:InboxStoredAuditRecord_LeavesTheOriginsDigestLaneUntouched_WhileADomainEventFoldsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:TerminalEvent_OnTheInbox_SealsTheStream_AndLaterRowsAreRefusedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:InboxAppend_ToASealedStream_DeadLettersTheRow_AndStoresTheRestAsync</tests>
-- <docs>fundamentals/events/archive-store#appending-to-an-archived-stream</docs>
-- <docs>fundamentals/events/superseding-events#across-services</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Emit_Inbox_StampsTheKey_AndQueuesTheOlderOneAsync</tests>
-- <docs>fundamentals/events/superseding-events#superseding-groups</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingGroupSqlTests.cs:Emit_Inbox_StampsTheGroup_AndQueuesAcrossTypesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveAppendPolicySqlTests.cs:InboxAppend_Refuse_DeadLettersTheRow_AndStoresTheRestAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveAppendPolicySqlTests.cs:InboxAppend_AutoRestore_DeadLettersAwaitingRestore_AndQueuesTheRequestAsync</tests>
-- <docs>data/event-store#stored-metadata</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataWriteSqlTests.cs:InboxChain_DefaultForm_StoresTheSlimRow_AndStagesTheFullFormAsync</tests>
-- <docs>fundamentals/events/archive-store#new-generation</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveNewGenerationSqlTests.cs:InboxAppend_StoresTheReceivedEventAtTheNextVersionAsync</tests>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestWritersTests.cs:InboxEmitChain_FoldsEveryLaneIntoTheOriginsBucketAsync</tests>
-- <docs>fundamentals/work-coordinator/claim-loop#the-claim-runs-only-what-can-find-work</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimFixedCostSqlTests.cs:StorePass_ARowWhoseEventIsAlreadyStored_IsStamped_WithoutRunningTheCopyAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimFixedCostSqlTests.cs:StorePass_AfterAPassWithNoCandidate_AReadyRowIsStoredByTheNextClaimAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimFixedCostSqlTests.cs:StorePass_AnOrdinaryEvent_RunsNoCollectiveRoutingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimFixedCostSqlTests.cs:StorePass_ACollectiveEvent_IsStillRoutedToTheCollectiveSinkAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._emit_event_store_chain_for_inbox(
  p_instance_id UUID,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_partition_count INTEGER DEFAULT 10000
) RETURNS INTEGER AS $$
DECLARE
  v_stored_event_ids UUID[];
  v_count INTEGER;
  v_local_service_id UUID;
  c_field_message_id CONSTANT TEXT := __ENVELOPE_FIELD_MESSAGE_ID__;
  c_field_hops CONSTANT TEXT := 'Hops';
  c_source_perspective CONSTANT TEXT := __CATEGORY_PERSPECTIVE__;
  -- Migration 061: collective routing sink + flag bit (EventFlags.Collective = 1 << 0).
  c_collective_sink CONSTANT TEXT := '__collective__';
  c_flag_collective CONSTANT INTEGER := 1;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  -- 260: EventFlags.Ephemeral (1 << 3) | EventFlags.Compacted (1 << 4), the events the lifecycle ledger does not count.
  c_flags_off_the_ledger CONSTANT INTEGER := 24;
  -- 228 (#1546): the rows this pass would copy, for the refusal before the copy.
  v_candidate_ids UUID[];
  v_candidate_streams UUID[];
  v_candidate_flags INTEGER[];
  v_candidate_types TEXT[];
  c_flag_terminal CONSTANT INTEGER := 64;  -- 266: EventFlags.Terminal (1 << 6)
  c_flag_superseding CONSTANT INTEGER := 128;  -- 295: EventFlags.Superseding (1 << 7)
  v_superseding_ids UUID[];                -- 295: the stored events that carry a superseding key
  v_sealed_refused UUID[];                 -- 266: the rows a sealed stream refused
  v_any_sealed BOOLEAN;                    -- 271: a candidate's stream is sealed
  v_any_archived BOOLEAN;                  -- 271: a candidate's stream is archived (and not sealed)
  v_archived_refused UUID[];               -- 271: the rows an archived stream refused
  v_write_form TEXT;                       -- 285: Full, Slim or Linked
  v_full_for TEXT[];                       -- 285: the type names and namespaces kept full
  v_keep_all BOOLEAN;                      -- 285: debug mode or the Full form keeps every row full
  v_any_collective BOOLEAN;                -- 367: an event this pass stored carries the collective flag
BEGIN
  -- Migration 087: resolve the LOCAL service id once. store_inbox_messages (062) COALESCEs a
  -- missing envelope SourceServiceId to the local id, so a wh_inbox row attributed to SELF (or
  -- zero) is a locally-originated event (loopback), its origin_service_id must stay NULL,
  -- matching the 046 contract ("NULL for locally-originated events").
  SELECT service_id INTO v_local_service_id FROM __SCHEMA__.wh_service_config LIMIT 1;

  -- Phase H step 10 slice 2: per-stream advisory locks. Mirrors _emit_event_store_chain (lines
  -- 311-329). Without these, two concurrent claim_work calls (e.g., NOTIFY-driven wake racing
  -- a heartbeat-driven poll) can both read MAX(version)=N from wh_event_store for the same
  -- stream and both attempt INSERT at version=N+1, violating idx_event_store_stream
  -- UNIQUE(stream_id, version) (PG error 23505). Reproduced in production on a consumer's
  -- service during job creation. Lock order is hashtext(stream_id::text), sorted ASC,
  -- ensures deadlock-free nesting between any pair of transactions touching overlapping stream
  -- sets. pg_advisory_xact_lock auto-releases at commit/rollback.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || sid::text))
  FROM (
    -- 162: every column this pass reads is on wh_inbox_state, so the lock pass never touches the
    -- wide message row at all. chain_emitted_at moved here precisely so this predicate stays on one
    -- table and one partial index can cover it; see idx_inbox_state_chain_pending.
    SELECT DISTINCT ist.stream_id AS sid
    FROM __SCHEMA__.wh_inbox_state ist
    WHERE ist.instance_id = p_instance_id
      AND ist.lease_expiry > p_now
      AND ist.processed_at IS NULL
      AND ist.is_event = true
      AND ist.stream_id IS NOT NULL
      -- 158: only rows this chain has never read (idx_inbox_state_chain_pending); every held row was
      -- read against the event store on every poll before, twice.
      AND ist.chain_emitted_at IS NULL
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = ist.message_id
      )
    ORDER BY ist.stream_id
  ) AS streams_to_lock;

  -- 367: no candidate, nothing to copy. The lock pass selects by the same predicate as every later read of this pass
  -- (the refusals' candidates, the copy's inbox_events), so when it locks nothing they find nothing, and only the stamp
  -- below has work: rows whose event is already stored. Each statement skipped is one not planned, and this function
  -- inherits claim_work's force_custom_plan. A row a concurrent claim commits after this read is not lost: it is still
  -- unstamped, so the next claim's guard runs this pass for it, as it would have after any pass that missed it.
  IF NOT FOUND THEN
    UPDATE __SCHEMA__.wh_inbox_state ist
    SET chain_emitted_at = p_now
    WHERE ist.instance_id = p_instance_id
      AND ist.lease_expiry > p_now
      AND ist.processed_at IS NULL
      AND ist.is_event = true
      AND ist.stream_id IS NOT NULL
      AND ist.chain_emitted_at IS NULL
      AND EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = ist.message_id
      );
    RETURN 0;
  END IF;

  -- 228 (#1546): a received event that would make its stream hold both ephemeral and Sourced events is refused
  -- before the copy. Nobody awaits a claim, so every refusal here is dead-lettered: recorded in wh_dead_letters and
  -- its lease row completed in place, so the copy below no longer sees it and the claim never offers it again. The
  -- message row is not touched here: this runs inside claim_work after the stream ledger, and wh_inbox comes first
  -- in the canonical lock order. The rows are the ones the copy reads, by the same predicate, under the locks taken
  -- above.
  SELECT array_agg(ist.message_id), array_agg(ist.stream_id), array_agg(i.flags), array_agg(i.message_type)
    INTO v_candidate_ids, v_candidate_streams, v_candidate_flags, v_candidate_types
  FROM __SCHEMA__.wh_inbox_state ist
  JOIN __SCHEMA__.wh_inbox i ON i.message_id = ist.message_id
  WHERE ist.instance_id = p_instance_id
    AND ist.lease_expiry > p_now
    AND ist.processed_at IS NULL
    AND ist.is_event = true
    AND ist.stream_id IS NOT NULL
    AND ist.chain_emitted_at IS NULL
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = ist.message_id
    );
  -- 266 (#1733): a sealed stream refuses every append, so its rows are dead-lettered and completed in place first, as
  -- the mixed-stream refusal does, and leave the candidates.
  -- 271 (#1738): so does an archived stream, by its type's AppendToArchived policy, after the seal. One probe per stream
  -- of idx_stream_lifecycle_closed, which holds only the sealed and the archived streams, answers both, so the common
  -- case (none of either, no terminal event) costs what 266's probe cost.
  IF v_candidate_ids IS NOT NULL THEN
    SELECT COALESCE(bool_or(l.sealed_at IS NOT NULL), false),
           COALESCE(bool_or(l.sealed_at IS NULL AND l.state = 'Archived'), false)
      INTO v_any_sealed, v_any_archived
    FROM __SCHEMA__.wh_stream_lifecycle l
    WHERE l.stream_id = ANY(v_candidate_streams)
      AND (l.sealed_at IS NOT NULL OR l.state = 'Archived');
    IF v_any_sealed OR EXISTS (
         SELECT 1 FROM unnest(v_candidate_flags) AS f(bits)
         WHERE COALESCE(f.bits, 0) & c_flag_terminal <> 0
           AND COALESCE(f.bits, 0) & c_flags_off_the_ledger = 0) THEN
      v_sealed_refused := __SCHEMA__._wh_refuse_sealed_stream_inbox_events(
        v_candidate_ids, v_candidate_streams, v_candidate_flags, v_candidate_types, p_instance_id, p_now);
      IF cardinality(v_sealed_refused) > 0 THEN
        SELECT array_agg(k.id ORDER BY k.ord), array_agg(k.sid ORDER BY k.ord), array_agg(k.bits ORDER BY k.ord),
               array_agg(k.type_name ORDER BY k.ord)
          INTO v_candidate_ids, v_candidate_streams, v_candidate_flags, v_candidate_types
        FROM unnest(v_candidate_ids, v_candidate_streams, v_candidate_flags, v_candidate_types)
               WITH ORDINALITY AS k(id, sid, bits, type_name, ord)
        WHERE k.id <> ALL(v_sealed_refused);
      END IF;
    END IF;
    IF v_any_archived AND v_candidate_ids IS NOT NULL THEN
      v_archived_refused := __SCHEMA__._wh_refuse_archived_stream_inbox_events(
        v_candidate_ids, v_candidate_streams, p_instance_id, p_now);
      IF cardinality(v_archived_refused) > 0 THEN
        SELECT array_agg(k.id ORDER BY k.ord), array_agg(k.sid ORDER BY k.ord), array_agg(k.bits ORDER BY k.ord),
               array_agg(k.type_name ORDER BY k.ord)
          INTO v_candidate_ids, v_candidate_streams, v_candidate_flags, v_candidate_types
        FROM unnest(v_candidate_ids, v_candidate_streams, v_candidate_flags, v_candidate_types)
               WITH ORDINALITY AS k(id, sid, bits, type_name, ord)
        WHERE k.id <> ALL(v_archived_refused);
      END IF;
      -- 296 (#1734): the archived streams the refusal left alone start their new generation.
      IF v_candidate_ids IS NOT NULL THEN
        PERFORM __SCHEMA__._wh_start_new_generations(v_candidate_streams);
      END IF;
    END IF;
  END IF;
  IF v_candidate_ids IS NOT NULL THEN
    PERFORM __SCHEMA__._wh_refuse_mixed_stream_inbox_events(
      v_candidate_ids, v_candidate_streams, v_candidate_flags, v_candidate_types, p_instance_id, p_now);
  END IF;

  -- 285 (#1725): the stored-metadata settings, read once for the whole batch.
  SELECT s.write_form, s.full_for, s.keep_all INTO v_write_form, v_full_for, v_keep_all
  FROM __SCHEMA__._wh_metadata_write_settings() s;

  -- Auto-create event_store rows for inbox rows owned by this instance that:
  --   - are events (is_event = true)
  --   - have a stream_id
  --   - aren't yet in wh_event_store (idempotent, ON CONFLICT swallows duplicates)
  -- Bounded by lease ownership so we don't scan the whole inbox every tick.
  -- Phase H step 10 slice 1: ORDER BY i.message_id (UUIDv7 = chronological at the source) so
  -- version assignment matches canonical event_id order. See _emit_event_store_chain above
  -- for the rationale, same fix applies to the inbox backfill path.
  -- Migration 061: i.flags carried into wh_event_store.flags (was dropped here previously).
  WITH inbox_events AS (
    SELECT
      i.message_id,
      ist.stream_id,
      i.message_type,
      i.event_data,
      i.metadata,
      i.scope,
      i.flags,
      i.source_service_id,
      i.source_commit_sequence,
      ist.received_at
    -- 162: this pass needs the message body, so both tables. The LEASE table drives the join, so
    -- selection happens on the narrow table and the wide row is fetched only for the rows that
    -- survive it, which is the opposite of scanning the wide row to apply the same predicate.
    FROM __SCHEMA__.wh_inbox_state ist
    JOIN __SCHEMA__.wh_inbox i ON i.message_id = ist.message_id
    WHERE ist.instance_id = p_instance_id
      AND ist.lease_expiry > p_now
      AND ist.processed_at IS NULL
      AND ist.is_event = true
      AND ist.stream_id IS NOT NULL
      AND ist.chain_emitted_at IS NULL  -- 158: see the lock pass above
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = ist.message_id
      )
  ),
  -- 230 (#1607): a received stream is stored in the producer's order, however many claims carry it. A claim
  -- numbers the rows it holds after the stream's tail, so a claim that holds a stream's later events and reaches
  -- this pass first would number them ahead of the head another claim leased earlier and has not stored yet.
  -- The head is visible here as a pending event of the stream that this pass does not hold: not yet in the event
  -- store, and not leased to this instance under a live lease (a peer's lease, an expired one, none, or a lease a
  -- concurrent claim has not committed). Every row of this batch that sorts after the stream's first such event
  -- waits: it is not stored and not stamped, so the next claim re-reads it, and fetch_inbox_batch does not hand
  -- it to the dispatcher before it is stored. Rows before it are stored now, so the head of a stream never waits.
  -- The order is the message id, the order this pass has always numbered by (slice 1): a fan-out child's id is
  -- derived from its composite and ordinal, so it sorts in producer order whether or not it carries an origin
  -- commit sequence.
  --
  -- One probe per stream, over idx_inbox_state_stream_order (stream_id, received_at, message_id), bounded by the
  -- batch: only rows received no later than the batch's newest row and sorting before its last one can hold it
  -- back. In a stream's FIFO backlog that range is the batch and the rows before it, never the rows queued behind
  -- it, and the lease test is answered from the index, so only a row this pass does not hold reads the heap. No
  -- index is added: wh_inbox_state's index count is a gated design property (InboxStateWriteCostScenarioTests).
  -- An event received after the rows it precedes is not waited for; that is the transport reordering two
  -- deliveries, which the receiver cannot see coming.
  batch_streams AS (
    SELECT ie.stream_id,
           MAX(ie.received_at) AS newest_received_at,
           (array_agg(ie.message_id ORDER BY ie.message_id DESC))[1] AS last_message_id
    FROM inbox_events ie
    GROUP BY ie.stream_id
  ),
  held_back_from AS (
    SELECT s.stream_id,
           (SELECT b.message_id
            FROM __SCHEMA__.wh_inbox_state b
            WHERE b.stream_id = s.stream_id
              AND b.processed_at IS NULL
              AND b.received_at <= s.newest_received_at
              AND b.message_id < s.last_message_id
              AND NOT COALESCE(b.instance_id = p_instance_id AND b.lease_expiry > p_now, false)
              AND b.is_event = true
              AND b.chain_emitted_at IS NULL
              AND NOT EXISTS (
                SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = b.message_id
              )
            ORDER BY b.message_id
            LIMIT 1) AS first_unheld
    FROM batch_streams s
  ),
  storable_events AS (
    SELECT ie.*,
           ROW_NUMBER() OVER (PARTITION BY ie.stream_id ORDER BY ie.message_id) AS row_num
    FROM inbox_events ie
    JOIN held_back_from h ON h.stream_id = ie.stream_id
    WHERE h.first_unheld IS NULL OR ie.message_id < h.first_unheld
  ),
  -- Phase H step 10 slice 3: version computed via correlated subquery rather than a
  -- pre-materialized CTE. Inside the per-stream advisory lock the values are equivalent, but
  -- the per-row form is defensive, see _emit_event_store_chain above for the rationale.
  -- Migration 072: materialize the extracted payload + built metadata ONCE (see outbox fn above).
  computed_full AS (
    SELECT
      ie.message_id,
      ie.stream_id,
      SPLIT_PART(__SCHEMA__.normalize_event_type(ie.message_type), ',', 1) AS aggregate_type,
      __SCHEMA__.normalize_event_type(ie.message_type) AS event_type,
      COALESCE(ie.event_data::jsonb -> 'p', ie.event_data::jsonb -> 'Payload', ie.event_data::jsonb -> 'payload') AS body_data,
      COALESCE(ie.event_data::jsonb -> 'h', ie.event_data::jsonb -> c_field_hops, ie.event_data::jsonb -> 'hops', '[]'::jsonb) AS hops,
      jsonb_build_object(
        c_field_message_id, COALESCE(ie.event_data::jsonb -> 'id', ie.event_data::jsonb -> c_field_message_id, ie.event_data::jsonb -> 'messageId'),
        c_field_hops, COALESCE(ie.event_data::jsonb -> 'h', ie.event_data::jsonb -> c_field_hops, ie.event_data::jsonb -> 'hops', '[]'::jsonb)
      ) || CASE
        WHEN ie.metadata IS NOT NULL
             AND jsonb_typeof(ie.metadata::jsonb -> 'ett') = 'number'
        THEN jsonb_build_object('ephemeral_expires_at',
               p_now + ((ie.metadata::jsonb ->> 'ett')::int * INTERVAL '1 second'))
        ELSE '{}'::jsonb
      END AS full_meta,
      ie.scope,
      ie.row_num,
      ie.flags,
      -- 295 (#1729): a superseding event's key, from the metadata the dispatch or receive path stamped it in. Only
      -- a row carrying the flag gets one, so a stray 'sk' on another event is never read.
      CASE WHEN COALESCE(ie.flags, 0) & c_flag_superseding <> 0 THEN ie.metadata::jsonb ->> 'sk' END AS supersede_key,
      -- 352 (#2124): and the superseding group the receive path stamped beside the key, on a flagged row only.
      CASE WHEN COALESCE(ie.flags, 0) & c_flag_superseding <> 0 THEN ie.metadata::jsonb ->> 'sg' END AS supersede_group,
      -- Migration 087: normalize the received origin, self/zero means locally-originated (NULL).
      CASE
        WHEN ie.source_service_id IS NULL
             OR ie.source_service_id = __EMPTY_UUID__::uuid
             OR ie.source_service_id = v_local_service_id
        THEN NULL
        ELSE ie.source_service_id
      END AS origin_service_id,
      NULLIF(ie.source_commit_sequence, 0) AS origin_commit_sequence
    FROM storable_events ie
  ),
  -- 285 (#1725): each event's route id, and the metadata its row keeps: the full form (plus the route id) under debug
  -- mode, under the Full form or for a listed type, otherwise the slim form. Pure CPU on values already in hand.
  -- Materialized, so each event's route id is computed once however many times the next step reads it.
  computed_routes AS MATERIALIZED (
    SELECT cf.*, __SCHEMA__._wh_route_id(split_part(cf.event_type, ',', 1), cf.hops) AS route_id
    FROM computed_full cf
  ),
  computed AS (
    SELECT cr.*,
      CASE
        WHEN v_keep_all OR __SCHEMA__._wh_metadata_kept_full(split_part(cr.event_type, ',', 1), v_full_for)
        THEN cr.full_meta || jsonb_build_object('rid', cr.route_id)
        ELSE __SCHEMA__._wh_slim_event_metadata(cr.full_meta, cr.route_id)
      END AS body_meta
    FROM computed_routes cr
  ),
  stored_events AS (
    INSERT INTO __SCHEMA__.wh_event_store (
      event_id, stream_id, aggregate_id, aggregate_type, event_type,
      scope, version, created_at, flags, origin_service_id, origin_commit_sequence, supersede_key, supersede_group
    )
    SELECT
      c.message_id,
      c.stream_id,
      c.stream_id,
      c.aggregate_type,
      c.event_type,
      c.scope,
      -- 296 (#1734): see _emit_event_store_chain.
      COALESCE((SELECT MAX(es.version) FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = c.stream_id),
               CASE WHEN v_any_archived THEN
                 (SELECT l.last_version FROM __SCHEMA__.wh_stream_lifecycle l WHERE l.stream_id = c.stream_id)
               END,
               0) + c.row_num,
      p_now,
      c.flags,
      -- Migration 087: stamp the origin identity the transport delivered (046 columns were never
      -- populated by the emit chain before this, consumer-side origin-keyed verification needs them).
      c.origin_service_id,
      CASE WHEN c.origin_service_id IS NULL THEN NULL ELSE c.origin_commit_sequence END,
      c.supersede_key,
      c.supersede_group
    FROM computed c
    -- Phase H step 10 slice 4: DO NOTHING with NO constraint specifier so PG handles BOTH the
    -- event_id PK conflict (idempotent re-store) AND the idx_event_store_stream (stream_id, version)
    -- UNIQUE conflict gracefully. Conflicting rows are silently skipped; the next claim_work cycle
    -- re-attempts them with a fresh MAX(version) snapshot.
    ON CONFLICT DO NOTHING
    RETURNING event_id, stream_id, version, supersede_key, flags
  ),
  -- Migration 077 (full split): offload EVERY body (see outbox fn above).
  stored_bodies AS (
    INSERT INTO __SCHEMA__.wh_event_body (event_id, event_data, metadata)
    SELECT c.message_id, c.body_data, c.body_meta
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    -- Constraint-LESS form (event_id PK is wh_event_body's only constraint, so semantics are
    -- identical), keeps the emit-chain source free of constraint-specific ON CONFLICT forms,
    -- which the version-ordering regression lock forbids (a specific-constraint form on
    -- wh_event_store once let idx_event_store_stream conflicts bubble up as PG 23505).
    ON CONFLICT DO NOTHING
    RETURNING event_id
  ),
  -- 285 (#1725): under the Linked form, the full form is appended to the staging table for maintenance to distill into
  -- routes and traces. Append-only: the key is new for every stored event, so there is nothing to check.
  staged_metadata AS (
    INSERT INTO __SCHEMA__.wh_event_metadata_staging (event_id, recorded_at, route_id, event_type, metadata)
    SELECT c.message_id, p_now, c.route_id, split_part(c.event_type, ',', 1), c.full_meta
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    WHERE v_write_form = 'Linked'
  ),
  -- Migration 087 (A1c): incrementally fold the just-stored events into wh_stream_digests.
  -- Bucket + predicates mirror ComputeStreamDigestsAsync (the full-sweep recompute) exactly:
  -- ephemeral (flags & 8) and at-most-once occurrences are excluded; XOR is self-inverse, so
  -- ON CONFLICT folds new hashes in by XOR. Joined to stored_events so an idempotent re-store
  -- (ON CONFLICT DO NOTHING above) never double-folds. Bucket conflicts across concurrent
  -- transactions are impossible here: the bucket key contains stream_id and the per-stream
  -- advisory locks serialize same-stream emits; ORDER BY is belt-and-suspenders lock ordering.
  -- The zero-uuid origin bucket = locally-originated events; a non-zero origin = events
  -- received FROM that origin (inbox flavor only).
  digest_folds AS (
    -- 336 (#2106): every lane, through the one fold helper. Referenced by the statement's final SELECT, so it
    -- always runs (a CTE that writes nothing runs only when read).
    SELECT __SCHEMA__._wh_fold_stream_digests(
      array_agg(COALESCE(c.origin_service_id, __EMPTY_UUID__::uuid)), array_agg(COALESCE(c.scope::jsonb ->> 't', '')), array_agg(c.event_type),
      array_agg(c.stream_id), array_agg(c.message_id), 'fold', p_now) AS folded
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    WHERE COALESCE(c.flags, 0) & 8 = 0
      -- 214 (#1484): the guarantee is a flag bit on the row. The stored metadata never carried it, so the
      -- body_meta test this replaces never matched and every at-most-once occurrence was folded.
      AND COALESCE(c.flags, 0) & c_flag_at_most_once = 0
      -- 216 (#1492): audit records are never digested; a received one no longer lands in its origin's lane.
      AND split_part(c.event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__)
  ),
  -- 260 (#1732): the stream lifecycle ledger. One row per stream this call stored a Sourced event on, in the same
  -- statement as the copy: one set-based upsert per batch, grouped by stream (never a write per event), in stream id
  -- order under the per-stream advisory locks taken above, so it can wait only on a batch that holds the same stream.
  -- An event that is ephemeral or a compaction origin (flags & 24) does not count, and neither does a stream-less event:
  -- an ephemeral stream is reaped by its time to live and never reaches end of life. The update touches no indexed
  -- column (last_version, approx_bytes, and a bucket that changes only when the UTC hour does), so it stays heap-only.
  -- 266 (#1733): a terminal event among them seals the stream in this statement (see _emit_event_store_chain).
  lifecycle_upsert AS (
    INSERT INTO __SCHEMA__.wh_stream_lifecycle AS l
      (stream_id, stream_type, tenant, first_appended_at, last_appended_bucket, last_version, approx_bytes,
       state, sealed_at, sealed_reason)
    SELECT
      se.stream_id,
      (array_agg(c.aggregate_type ORDER BY se.version))[1],
      (array_agg(c.scope::jsonb ->> 't' ORDER BY se.version))[1],
      p_now,
      date_trunc('hour', p_now, 'UTC'),
      MAX(se.version),
      SUM(COALESCE(pg_column_size(c.body_data), 0) + COALESCE(pg_column_size(c.body_meta), 0)),
      CASE WHEN bool_or(COALESCE(c.flags, 0) & c_flag_terminal <> 0) THEN 'Sealed' ELSE 'Active' END,
      CASE WHEN bool_or(COALESCE(c.flags, 0) & c_flag_terminal <> 0) THEN p_now END,
      (array_agg(__SCHEMA__._wh_terminal_seal_reason(c.aggregate_type, c.message_id) ORDER BY se.version)
         FILTER (WHERE COALESCE(c.flags, 0) & c_flag_terminal <> 0))[1]
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    WHERE COALESCE(c.flags, 0) & c_flags_off_the_ledger = 0
      AND se.stream_id <> __EMPTY_UUID__::uuid
    GROUP BY se.stream_id
    ORDER BY se.stream_id
    ON CONFLICT (stream_id) DO UPDATE SET
      last_version = GREATEST(l.last_version, EXCLUDED.last_version),
      approx_bytes = l.approx_bytes + EXCLUDED.approx_bytes,
      last_appended_bucket = GREATEST(l.last_appended_bucket, EXCLUDED.last_appended_bucket),
      state = CASE WHEN EXCLUDED.sealed_at IS NOT NULL AND l.sealed_at IS NULL AND l.state = 'Active'
                   THEN 'Sealed' ELSE l.state END,
      sealed_reason = CASE WHEN l.sealed_at IS NULL THEN EXCLUDED.sealed_reason ELSE l.sealed_reason END,
      sealed_at = COALESCE(l.sealed_at, EXCLUDED.sealed_at)
  )
  SELECT array_agg(event_id), array_agg(event_id) FILTER (WHERE supersede_key IS NOT NULL),
         -- 367: whether the collective routing below has anything to route, from the rows this statement stored.
         COALESCE(bool_or((COALESCE(flags, 0) & c_flag_collective) = c_flag_collective), false)
    INTO v_stored_event_ids, v_superseding_ids, v_any_collective
  FROM stored_events CROSS JOIN digest_folds;
  v_stored_event_ids := COALESCE(v_stored_event_ids, '{}');
  v_count := cardinality(v_stored_event_ids);

  -- 295 (#1729): a stored superseding event queues the older events of its stream, type and key for the reaper. A
  -- separate statement, so it sees every event this batch stored as well as the ones before it. Skipped outright when
  -- the batch stored none, so an ordinary batch pays nothing; otherwise one probe of idx_event_store_superseding per
  -- superseding event.
  IF v_superseding_ids IS NOT NULL THEN
    PERFORM __SCHEMA__._wh_queue_superseded_events(v_superseding_ids, p_now);
  END IF;

  -- 158: stamp the held rows the chain is finished with, so the next poll reads none of them.
  -- "Finished" is the row's event being in the event store: either this pass inserted it, or it was
  -- already there and the pass above excluded the row for exactly that reason. A row whose insert hit
  -- ON CONFLICT DO NOTHING has no event-store row, so it stays unstamped and the next poll re-attempts
  -- it with a fresh version snapshot, which is the contract that conflict clause states. The stamp runs
  -- before the nothing-was-stored return below, because that return is the steady state of a busy
  -- instance (every held event already chained) and the case the stamp exists for. The stamp and the
  -- inserts share this transaction, so a failure leaves both undone.
  -- 162: the stamp is the write this migration helps most. It used to rewrite a row carrying the
  -- whole message body across twenty-four indexes to set one timestamp, measured at about 41 blocks
  -- per row stamped with only 30 of those spent finding the rows. It now writes the narrow table,
  -- and the predicate that finds them is local to it.
  UPDATE __SCHEMA__.wh_inbox_state ist
  SET chain_emitted_at = p_now
  WHERE ist.instance_id = p_instance_id
    AND ist.lease_expiry > p_now
    AND ist.processed_at IS NULL
    AND ist.is_event = true
    AND ist.stream_id IS NOT NULL
    AND ist.chain_emitted_at IS NULL
    AND EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = ist.message_id
    );

  IF v_count = 0 THEN
    RETURN 0;
  END IF;

  -- Auto-create perspective_events for the newly-stored events.
  -- Phase H step 6 slice 2: populate partition_number for symmetric load balancing.
  -- Slice 26.14: route the lease through wh_active_streams (live owner wins; fall back to
  -- commit instance when no live owner). Mirror of the outbox-side change in
  -- _emit_event_store_chain.
  INSERT INTO __SCHEMA__.wh_perspective_events (
    event_work_id, stream_id, perspective_name, event_id,
    partition_number, status, attempts, created_at, instance_id, lease_expiry, priority
  )
  SELECT DISTINCT
    gen_random_uuid(),
    es.stream_id,
    ma.target_name,
    es.event_id,
    __SCHEMA__.compute_partition(es.stream_id, p_partition_count),
    1,                  -- Stored flag
    0,
    p_now,
    -- Slice 26.14: when caller is actively leasing (p_lease_expiry IS NOT NULL),
    -- route through wh_active_streams to the stream's pinned owner. When caller passed
    -- NULL p_lease_expiry / NULL p_instance_id (strategy-flush path, "leave unleased so
    -- claim_orphaned picks it up"), preserve that contract; otherwise we'd land in
    -- instance_id-set-but-lease-NULL purgatory that claim_orphaned's filter excludes.
    CASE WHEN p_lease_expiry IS NOT NULL THEN COALESCE(owner.assigned_instance_id, p_instance_id) ELSE NULL END,
    p_lease_expiry,
    COALESCE(src.priority, 150)   -- 149: the projection is scheduled with the source row's number
  FROM __SCHEMA__.wh_event_store es
  LEFT JOIN __SCHEMA__.wh_inbox src ON src.message_id = es.event_id
  INNER JOIN __SCHEMA__.wh_message_associations ma
    ON es.event_type = ma.normalized_message_type
    AND ma.association_type = c_source_perspective
  LEFT JOIN LATERAL (
    SELECT ast.assigned_instance_id
    FROM __SCHEMA__.wh_active_streams ast
    WHERE ast.stream_id = es.stream_id
      AND ast.assigned_instance_id IS NOT NULL
      AND EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_service_instances si
        WHERE si.instance_id = ast.assigned_instance_id
      )
  ) owner ON TRUE
  WHERE es.event_id = ANY(v_stored_event_ids)
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events pe_check
      WHERE pe_check.stream_id = es.stream_id
        AND pe_check.perspective_name = ma.target_name
        AND pe_check.event_id = es.event_id
    )
  ON CONFLICT ON CONSTRAINT uq_perspective_event DO NOTHING;

  -- 100: reconcile self-declares the rewind. Any of THIS invocation's fresh work items whose
  -- event id slots BELOW its cursor's last-applied event is a straggler by construction, a
  -- backfilled event keeps its ORIGINAL id, while its fresh local commit_sequence sits above
  -- the cursor and hides it from the runtime inversion detector forever. Flag the cursor
  -- RewindRequired with the earliest straggler as trigger (min-merge with any existing
  -- trigger, mirroring complete_perspective_checkpoint's straggler path); the worker's
  -- existing rewind routing replays the stream through the corrected order.
  UPDATE __SCHEMA__.wh_perspective_cursors pc
  SET status = pc.status | 32,  -- RewindRequired flag (1 << 5)
      rewind_trigger_event_id = CASE
        WHEN pc.rewind_trigger_event_id IS NULL THEN s.straggler_event_id
        WHEN s.straggler_event_id < pc.rewind_trigger_event_id THEN s.straggler_event_id
        ELSE pc.rewind_trigger_event_id
      END,
      rewind_flagged_at = p_now,
      rewind_first_flagged_at = COALESCE(pc.rewind_first_flagged_at, p_now)
  FROM (
    SELECT pe.stream_id, pe.perspective_name,
           (array_agg(pe.event_id ORDER BY pe.event_id))[1] AS straggler_event_id
    FROM __SCHEMA__.wh_perspective_events pe
    JOIN __SCHEMA__.wh_perspective_cursors c
      ON c.stream_id = pe.stream_id AND c.perspective_name = pe.perspective_name
    WHERE pe.event_id = ANY(v_stored_event_ids)
      AND pe.processed_at IS NULL
      AND c.last_event_id IS NOT NULL
      AND pe.event_id < c.last_event_id
    GROUP BY pe.stream_id, pe.perspective_name
  ) s
  WHERE pc.stream_id = s.stream_id AND pc.perspective_name = s.perspective_name;

  -- 367: only when an event this pass stored is collective; v_any_collective is read from the stored rows themselves.
  IF v_any_collective THEN
    -- Migration 061: collective-event routing (inbox path). See _emit_event_store_chain above.
    INSERT INTO __SCHEMA__.wh_perspective_events (
      event_work_id, stream_id, perspective_name, event_id,
      partition_number, status, attempts, created_at, instance_id, lease_expiry, priority
    )
    SELECT DISTINCT
      gen_random_uuid(),
      es.stream_id,
      c_collective_sink,
      es.event_id,
      __SCHEMA__.compute_partition(es.stream_id, p_partition_count),
      1,                  -- Stored flag
      0,
      p_now,
      CASE WHEN p_lease_expiry IS NOT NULL THEN COALESCE(owner.assigned_instance_id, p_instance_id) ELSE NULL END,
      p_lease_expiry,
      COALESCE(src.priority, 150)   -- 149: the projection is scheduled with the source row's number
    FROM __SCHEMA__.wh_event_store es
    LEFT JOIN __SCHEMA__.wh_inbox src ON src.message_id = es.event_id
    LEFT JOIN LATERAL (
      SELECT ast.assigned_instance_id
      FROM __SCHEMA__.wh_active_streams ast
      WHERE ast.stream_id = es.stream_id
        AND ast.assigned_instance_id IS NOT NULL
        AND EXISTS (
          SELECT 1 FROM __SCHEMA__.wh_service_instances si
          WHERE si.instance_id = ast.assigned_instance_id
        )
    ) owner ON TRUE
    WHERE es.event_id = ANY(v_stored_event_ids)
      AND (es.flags & c_flag_collective) = c_flag_collective
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_perspective_events pe_check
        WHERE pe_check.stream_id = es.stream_id
          AND pe_check.perspective_name = c_collective_sink
          AND pe_check.event_id = es.event_id
      )
    ON CONFLICT ON CONSTRAINT uq_perspective_event DO NOTHING;
  END IF;

  -- Slice 26.4: wake the commit-order stamper. See _emit_event_store_chain above for
  -- the rationale and dedup semantics. Inbox backfill is generally a smaller fan-in
  -- than outbox-emit, but the NOTIFY is just as cheap and keeps the stamper hot path
  -- responsive whether events arrive locally or via transport.
  PERFORM __SCHEMA__._queue_doorbell('wh_committed', '');

  RETURN v_count;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._emit_event_store_chain_for_inbox(UUID, TIMESTAMPTZ, TIMESTAMPTZ, INTEGER) IS
  'Copies leased inbox events into the event store, stamps their origin and creates their perspective work (162). 214: the digest fold leaves out at-most-once events (flags & 32). 216: and audit records (#1492). 228: an event that would make its stream hold both ephemeral and Sourced events is dead-lettered instead (WHIZ133, #1546). 230: a row waits behind an earlier pending row of its stream that this pass does not hold, so a received stream is stored in the producer''s order (#1607). 260: keeps each stored stream''s wh_stream_lifecycle row, one upsert per batch (#1732). 266: dead-letters each received event a sealed stream refuses and stores the rest; a terminal event seals its stream in the same statement (#1733). 271: and each received event an archived stream refuses, by its type''s AppendToArchived policy, queuing the stream''s restore under AutoRestore (#1738). 285: each row keeps the slim form unless kept full, carries its route id, and under the Linked form stages its full form (#1725). 295: stamps a superseding event''s key (wh_event_store.supersede_key) and queues the older events of its key for the reaper (#1729). 296: starts the new generation of an archived stream whose type declares NewGeneration, numbered on from its last version (#1734). 352: and its superseding group (wh_event_store.supersede_group, #2124), so the older events of its group, not only its type, are queued. 367: a pass whose lock pass finds no candidate only stamps, and the collective routing runs only when a stored event is collective.';
