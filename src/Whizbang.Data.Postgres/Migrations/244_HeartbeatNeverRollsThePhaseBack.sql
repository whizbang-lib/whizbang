-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 244_HeartbeatNeverRollsThePhaseBack.sql
-- Date: 2026-10-26
-- Description: A heartbeat never moves an instance's recorded lifecycle phase backwards along the startup and
--              shutdown ladder (#1781).
--
--              Three writers set wh_service_instances.lifecycle_phase: the startup stage recorder
--              (wh_record_startup_stage, 231), the transition recorder (record_instance_state, 109) and every beat
--              (record_heartbeat, 147, last word 203: the heartbeat worker's and the claim worker's registration).
--              A beat reads the phase when it is built and writes it when it lands. When a stage or a transition is
--              recorded in between, the beat carries the phase before it and wrote it back: a row that had recorded
--              Accept commands read Migrating again, and wh_instance_is_eligible_for_work (229, 231) left a ready
--              instance out until its next beat or claim.
--
--              The fix rests on one fact about a process: it never moves backwards along the ladder
--                Starting, Connecting, Migrating, AcceptingCommands, Running, Stopping, Stopped.
--              Every transition onto a rung comes from below it or from off the ladder (a revival from StandingBy and
--              a resume both go to Running), and the stage reporter never re-enters an earlier stage. So a beat whose
--              phase is a rung below the rung the row records is a stale beat from the same process, and it keeps the
--              recorded phase. That holds whoever recorded the row's phase, so it covers a row that records no stage
--              (an older build, or the Dapper driver, whose beats race each other) as well as one that does.
--
--              What it leaves alone, and why:
--                * a beat at or above the row's rung moves the phase as before: a transition whose own write was lost
--                  is still repaired by the next beat;
--                * a phase off the ladder (Pausing, Paused, StandingBy, Resuming, Faulted, Halted), on either side, is
--                  taken from the beat as before. These phases move both ways (Running to Paused and back), so a rank
--                  cannot tell a stale beat from a live one there; the beat stays their repair;
--                * a phase the database does not know (a newer build's) has no rung and is taken as before;
--                * a beat from another process: the row records the host and process id of the process that first
--                  registered it, and a beat that names a different host or process id is a new process under the same
--                  instance id (a restart after a crash, the row not yet reaped). A new process legitimately starts at
--                  the bottom of the ladder, so its phase is taken. By default each process has an instance id of its
--                  own and registers a new row; a graceful stop deletes the row and a reap deletes and tombstones it.
--
--              Kept as it was: the signature, so the C# callers on both drivers and claim_work's registration probe
--              (229) are unchanged. A stale registration that keeps the recorded phase leaves the probe reading the
--              newer phase the caller is about to announce, never an older one.
--
--              Number 244: 241 and 242 are taken by an open branch.
--
-- Dependencies: 203 (record_heartbeat, copied verbatim apart from the phase), 109 (lifecycle_phase), 231 (the stages)
-- Objects: wh_lifecycle_phase_rank, record_heartbeat
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- ---------------------------------------------------------------------------------------------
-- wh_lifecycle_phase_rank: a phase's rung on the startup and shutdown ladder.
-- ---------------------------------------------------------------------------------------------
-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_lifecycle_phase_rank');

-- <docs>operations/startup/startup-status#startup-stages</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeartbeatPhaseOrderSqlTests.cs:Rank_FollowsTheLadder_AndIsNullOffItAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_lifecycle_phase_rank(p_phase TEXT)
RETURNS INTEGER
LANGUAGE sql
IMMUTABLE
PARALLEL SAFE
AS $$
  -- The names are LifecyclePhase's, in the order a process moves through them; HeartbeatPhaseOrderSqlTests checks
  -- every phase of the enum against this list. NULL: off the ladder, unknown, or no phase at all.
  SELECT array_position(
    ARRAY['Starting', 'Connecting', 'Migrating', 'AcceptingCommands', 'Running', 'Stopping', 'Stopped']::TEXT[],
    p_phase) - 1
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_lifecycle_phase_rank(TEXT) IS
  '244 (#1781): a lifecycle phase''s rung on the startup and shutdown ladder (Starting 0, Connecting 1, Migrating 2, '
  'AcceptingCommands 3, Running 4, Stopping 5, Stopped 6). A process never moves down it. NULL for a phase off the '
  'ladder (Pausing, Paused, StandingBy, Resuming, Faulted, Halted) or one this database does not know.';

-- ---------------------------------------------------------------------------------------------
-- record_heartbeat: last word 203, and a stale beat no longer rolls the phase back.
-- ---------------------------------------------------------------------------------------------
-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('record_heartbeat');

-- <docs>fundamentals/work-coordinator/claim-loop</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkRegistrationSqlTests.cs:ClaimWork_HeldOpen_AHeartbeatThatFindsAStalePeerDoesNotWaitAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkRegistrationSqlTests.cs:ClaimWork_HeldOpen_AHeartbeatForTheSameInstanceDoesNotWaitAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecordHeartbeatBackfillTests.cs:PeerPastTheDerivedThreshold_IsStillReapedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Heartbeat_RecordsTheConnectionMode_AndANullKeepsItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Heartbeat_OfTheAssignerHoldingTheRole_RenewsTheAssignmentLeaseAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Heartbeat_OfADeposedAssigner_LeavesTheLeaseAloneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeartbeatPhaseOrderSqlTests.cs:AStaleBeat_AfterAcceptCommandsIsRecorded_KeepsThePhase_AndTheInstanceEligibleAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeartbeatPhaseOrderSqlTests.cs:AStaleBeat_BehindTheRecordedStage_KeepsThePhaseTheStageWroteAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeartbeatPhaseOrderSqlTests.cs:AStaleBeat_OnARowThatRecordsNoStage_DoesNotRollTheTransitionBackAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeartbeatPhaseOrderSqlTests.cs:ABeatAheadOfTheRow_MovesThePhaseForward_RepairingALostTransitionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeartbeatPhaseOrderSqlTests.cs:APhaseOffTheLadder_IsTakenFromTheBeat_EitherWayAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeartbeatPhaseOrderSqlTests.cs:ABeatThatNamesNoPhase_KeepsTheRecordedPhaseAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeartbeatPhaseOrderSqlTests.cs:APhaseTheDatabaseDoesNotKnow_IsTakenFromTheBeatAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeartbeatPhaseOrderSqlTests.cs:ARestartUnderTheSameInstanceId_StartsBehindTheOldProcess_AndIsNotEligibleAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperHeartbeatPhaseOrderTests.cs:AStaleBeat_AfterALaterBeat_KeepsTheLaterPhase_AndTheInstanceEligibleAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperHeartbeatPhaseOrderTests.cs:ARestartUnderTheSameInstanceId_FromAnotherHost_RecordsItsEarlierPhaseAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.record_heartbeat(
  p_instance_id UUID,
  p_service_name TEXT,
  p_host_name TEXT,
  p_process_id INTEGER,
  p_metadata JSONB DEFAULT '{}'::JSONB,
  p_lifecycle_phase TEXT DEFAULT NULL,
  p_library_version TEXT DEFAULT NULL,
  p_stale_threshold_seconds INTEGER DEFAULT NULL,
  -- 203 (#1254): 'direct' or 'pooled', how this instance reaches the database. NULL keeps what is recorded.
  p_connection_mode TEXT DEFAULT NULL
) RETURNS BOOLEAN AS $$
DECLARE
  -- 147: the caller's cadence-derived threshold; the pre-147 constant when nothing is passed.
  v_stale_cutoff TIMESTAMPTZ := NOW() - (COALESCE(p_stale_threshold_seconds, 30) * INTERVAL '1 second');
  -- v0.687: any heartbeat older than this is treated as definitively dead and
  -- bypasses the v0.681 alive-lock guard. Covers OOMKilled pods on half-open TCP
  -- where the session lock can linger until OS keepalive (~2 h default on Linux).
  -- 147: never below twice the stale threshold, so the two cutoffs cannot cross.
  v_definitive_dead_cutoff TIMESTAMPTZ := NOW() - GREATEST(INTERVAL '5 minutes', 2 * (COALESCE(p_stale_threshold_seconds, 30) * INTERVAL '1 second'));
BEGIN
  -- An evicted instance must never rejoin silently. Reaping already released its leases
  -- to other instances; a heartbeat that quietly re-inserted the row would let the
  -- returning process believe it was still an ordinary fleet member. Refuse instead, and
  -- tell the caller (this is the fence reaping was missing, migration 106).
  IF EXISTS (SELECT 1 FROM __SCHEMA__.wh_instance_evictions WHERE instance_id = p_instance_id) THEN
    RETURN FALSE;
  END IF;

  INSERT INTO __SCHEMA__.wh_service_instances
    (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata, lifecycle_phase, library_version,
     connection_mode)
  VALUES
    (p_instance_id, p_service_name, p_host_name, p_process_id, NOW(), NOW(), p_metadata, p_lifecycle_phase, p_library_version,
     p_connection_mode)
  ON CONFLICT (instance_id) DO UPDATE SET
    last_heartbeat_at = NOW(),
    metadata = EXCLUDED.metadata,
    -- 147: the beat's value is the live truth when present; a NULL keeps what record_instance_state wrote.
    -- 244 (#1781): except a stale beat. A beat from the process the row records whose phase is a rung below the
    -- row's was built before the stage or transition recorded since, and keeps the recorded phase. A rung that is
    -- NULL (a phase off the ladder or unknown, on either side) compares to nothing and the beat's value stands.
    lifecycle_phase = CASE
      WHEN EXCLUDED.lifecycle_phase IS NULL
        THEN __SCHEMA__.wh_service_instances.lifecycle_phase
      WHEN __SCHEMA__.wh_service_instances.host_name = EXCLUDED.host_name
           AND __SCHEMA__.wh_service_instances.process_id = EXCLUDED.process_id
           AND __SCHEMA__.wh_lifecycle_phase_rank(EXCLUDED.lifecycle_phase)
               < __SCHEMA__.wh_lifecycle_phase_rank(__SCHEMA__.wh_service_instances.lifecycle_phase)
        THEN __SCHEMA__.wh_service_instances.lifecycle_phase
      ELSE EXCLUDED.lifecycle_phase
    END,
    library_version = COALESCE(EXCLUDED.library_version, __SCHEMA__.wh_service_instances.library_version),
    -- 203: the registration records the connection mode; a NULL keeps what was recorded.
    connection_mode = COALESCE(EXCLUDED.connection_mode, __SCHEMA__.wh_service_instances.connection_mode);

  -- 203 (#1254): the heartbeat is the assigner's liveness where it has no alive-lock (and the fallback where it
  -- has one), so it renews the partition assignment this instance published while it still holds the role. One
  -- primary-key probe of a one-row table; nothing happens for any other instance.
  UPDATE __SCHEMA__.wh_published_partition_assignment pa
  SET lease_expires_at = clock_timestamp() + pa.lease
  WHERE pa.singleton
    AND pa.assigner_instance_id = p_instance_id
    AND EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_role_assignments r
      WHERE r.role = pa.role AND r.holder_instance_id = p_instance_id AND r.epoch = pa.epoch);

  -- Opportunistic stale-peer cleanup (Phase H step 6 slice 1). When a peer has gone
  -- silent past the stale cutoff, delete its row and release its leases so live
  -- instances can claim on the next claim_work tick. Cheap pre-check on the indexed
  -- last_heartbeat_at means the heavyweight DELETE+lease-null block only fires when
  -- there's actually a stale peer; most heartbeats no-op past the EXISTS check.
  -- Backstop: MaintenanceWorker also runs cleanup_stale_instances every IntervalMinutes.
  IF EXISTS (
    SELECT 1 FROM __SCHEMA__.wh_service_instances
    WHERE last_heartbeat_at < v_stale_cutoff
      AND instance_id != p_instance_id
    LIMIT 1
  ) THEN
    -- 199: a heartbeat never waits for the reap. Its lease releases wait on rows a claim may hold, so
    -- the reap runs in a subtransaction under a short lock timeout and is rolled back when a lock is
    -- not available; the backstop and the next heartbeat try again. The timeout is set inside the
    -- subtransaction, so the rollback restores the caller's value with it.
    BEGIN
      PERFORM set_config('lock_timeout', '100ms', true);
      PERFORM __SCHEMA__.cleanup_stale_instances(v_stale_cutoff, v_definitive_dead_cutoff);
    EXCEPTION WHEN lock_not_available OR deadlock_detected THEN
      NULL;
    END;
  END IF;

  RETURN TRUE;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.record_heartbeat(UUID, TEXT, TEXT, INTEGER, JSONB, TEXT, TEXT, INTEGER, TEXT) IS
'Decoupled heartbeat UPSERT. Inserts a new wh_service_instances row on first call, updates last_heartbeat_at on subsequent calls. Called by the C# HeartbeatWorker on its own timer, independent of polling cadence, and by ClaimWorker to register when a claim reports its row missing or stale (196). Opportunistically cleans up stale peers when detected (cheap pre-check guard); since 199 that cleanup steps aside under a 100 ms lock timeout instead of waiting for rows a claim holds. Migration 106: returns FALSE and does nothing when the calling instance_id has been tombstoned in wh_instance_evictions (this instance was reaped and must not rejoin); returns TRUE otherwise. Migration 147: p_lifecycle_phase and p_library_version backfill the registry row (non-null wins, null keeps the recorded value), and p_stale_threshold_seconds lets the caller derive the peer-reap threshold from its own cadence instead of the 30 s constant. Migration 203: p_connection_mode records how the instance reaches the database '
'(direct or pooled; null keeps the recorded value), and the beat of the partition assigner renews the lease of the '
'assignment it published while it still holds the role. Migration 244 (#1781): a beat from the process the row records '
'never moves the phase down the startup and shutdown ladder (wh_lifecycle_phase_rank); such a beat is stale and keeps '
'the recorded phase. Phases off the ladder, and a beat from another process under the same instance id, are taken as before.';
