-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 278_OneWriterOfTheInstancePhase.sql
-- Date: 2026-11-01
-- Description: One write per transition: the stage record is the only writer of an instance's recorded lifecycle
--              phase (#1636).
--
--              Three writers set wh_service_instances.lifecycle_phase on every transition: the startup stage recorder
--              (wh_record_startup_stage, 231), the transition recorder (record_instance_state, 109, with the library
--              version) and every beat (record_heartbeat, last word 244). They raced: 244 stopped a stale beat rolling
--              the phase back down the startup ladder, but a pause and a stand-by are off the ladder, and each
--              transition still cost two updates and a third on the next beat.
--
--              wh_record_instance_transition is the one write: the stage, what it is doing, the phase and the version,
--              in one update. A stage writes the phase it maps to; a pause or a stand-by writes its phase with a NULL
--              stage and keeps the stage, its message and when it was entered. A NULL version keeps the recorded one.
--              It is in the bootstrap closure because an instance's registration, the last step of Preflight, goes
--              through it twice on a fresh database as on any other: the registration records Preflight's phase where
--              the row records none (a row without a phase reads as an older build, eligible for work during its
--              migration), and the stage held until the row existed is then written as that transition's one update.
--
--              The heartbeat (record_heartbeat) records the phase and the version only as a registration, never over a
--              recorded value:
--                * on the row it inserts (the Dapper driver's first beat, a claim re-registering a missing row): a row
--                  without a phase reads as an older build, eligible for work whenever it is live;
--                * on a row that records none (a registration by an older build, which could not say);
--                * for a new process under the same instance id (another host or process id: a restart before the
--                  old row was reaped), which starts at the bottom of the ladder and runs its own version.
--              Every other beat leaves both alone. A beat no longer repairs a lost transition: the stage record holds a
--              write it could not make and writes it again on the next accepted beat, and with one writer there is no
--              other write for a beat to repair. 244's ladder (wh_lifecycle_phase_rank) is no longer consulted here;
--              the function stays defined.
--
--              Kept as they were, for the rolling upgrade: wh_record_startup_stage and record_instance_state are still
--              called by older builds; claim_work's own-registration probe still compares a phase the caller passes
--              (this release passes none). record_heartbeat keeps its signature.
--
--              Number 278: reserved for this change.
--
-- Dependencies: 277 (the stage columns, in the closure), 244 (record_heartbeat, copied verbatim apart from the phase and
--               the version)
-- Objects: wh_record_instance_transition, record_heartbeat
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README
--            rule 12).

-- @whizbang:bootstrap-begin
-- Bootstrap: registration writes the instance's held transition through wh_record_instance_transition.

-- ---------------------------------------------------------------------------------------------
-- wh_record_instance_transition: stage, phase and version, one update.
-- ---------------------------------------------------------------------------------------------
-- <docs>operations/startup/startup-status#the-instance-row</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceTransitionSqlTests.cs:AStage_IsWrittenWithItsPhaseAndTheVersion_AndIsNotAHeartbeatAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceTransitionSqlTests.cs:ATransition_IsOneUpdateOfTheRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceTransitionSqlTests.cs:APhaseOnlyWrite_KeepsTheStage_ItsMessage_AndWhenItWasEnteredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceTransitionSqlTests.cs:TheSameStageAgain_KeepsWhenItWasEntered_AndANewStageStartsItsOwnAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceTransitionSqlTests.cs:ANullVersion_KeepsTheRecordedOneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceTransitionSqlTests.cs:AnInstanceWithNoRow_ReportsNothingWrittenAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceTransitionSqlTests.cs:Registration_RecordsThePhaseItRegistersIn_AndNeverOverwritesARecordedOneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/SchemaBootstrapPhaseTests.cs:AnEmptyDatabaseRecordsATransitionAtRegistrationAfterTheBootstrapAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperHeartbeatPhaseOrderTests.cs:ATransition_IsRecorded_StagePhaseAndVersionTogether_AndAPauseKeepsTheStageAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_record_instance_transition(
  p_instance_id UUID,
  p_stage SMALLINT,
  p_message TEXT,
  p_lifecycle_phase TEXT,
  p_library_version TEXT
) RETURNS BOOLEAN
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
BEGIN
  UPDATE __SCHEMA__.wh_service_instances
  SET startup_stage = COALESCE(p_stage, startup_stage),
      -- A phase-only write (NULL stage) keeps what the stage is doing.
      startup_stage_message = CASE WHEN p_stage IS NULL THEN startup_stage_message ELSE p_message END,
      -- A phase-only write, and progress within a stage, keep the time the stage was entered; a new stage starts its own.
      startup_stage_entered_at = CASE
        WHEN p_stage IS NULL
          OR (startup_stage IS NOT DISTINCT FROM p_stage AND startup_stage_entered_at IS NOT NULL)
          THEN startup_stage_entered_at
        ELSE clock_timestamp()
      END,
      lifecycle_phase = COALESCE(p_lifecycle_phase, lifecycle_phase),
      library_version = COALESCE(p_library_version, library_version)
  WHERE instance_id = p_instance_id;

  RETURN FOUND;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_record_instance_transition(UUID, SMALLINT, TEXT, TEXT, TEXT) IS
  '278 (#1636): records one transition on an instance''s own row in one update: the stage and what it is doing, the '
  'lifecycle phase and the library version. The only write of a recorded phase. A NULL stage is a phase-only write (a '
  'pause, a stand-by) that keeps the stage, its message and when it was entered; a NULL version keeps the recorded one. '
  'Returns FALSE when the instance has no row yet (expected before it registers). Not a heartbeat: last_heartbeat_at '
  'is never touched.';

-- @whizbang:bootstrap-end

-- ---------------------------------------------------------------------------------------------
-- record_heartbeat: last word 244, and a beat records the phase and version only as a registration.
-- ---------------------------------------------------------------------------------------------
-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('record_heartbeat');

-- <docs>operations/startup/startup-status#registration-writes</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkRegistrationSqlTests.cs:ClaimWork_HeldOpen_AHeartbeatThatFindsAStalePeerDoesNotWaitAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkRegistrationSqlTests.cs:ClaimWork_HeldOpen_AHeartbeatForTheSameInstanceDoesNotWaitAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecordHeartbeatBackfillTests.cs:PeerPastTheDerivedThreshold_IsStillReapedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecordHeartbeatBackfillTests.cs:BeatWithANewPhase_KeepsTheRecordedOneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Heartbeat_RecordsTheConnectionMode_AndANullKeepsItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Heartbeat_OfTheAssignerHoldingTheRole_RenewsTheAssignmentLeaseAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Heartbeat_OfADeposedAssigner_LeavesTheLeaseAloneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeartbeatPhaseOrderSqlTests.cs:AStaleBeat_AfterAcceptCommandsIsRecorded_KeepsThePhase_AndTheInstanceEligibleAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeartbeatPhaseOrderSqlTests.cs:ABeatAheadOfTheRow_DoesNotMoveThePhase_TheStageRecordIsItsOnlyWriterAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeartbeatPhaseOrderSqlTests.cs:APhaseOffTheLadder_OnABeat_IsNotWritten_EitherWayAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeartbeatPhaseOrderSqlTests.cs:ABeatOnARowThatRecordsNoPhaseOrVersion_FillsThemAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeartbeatPhaseOrderSqlTests.cs:ABeat_NeverOverwritesARecordedVersionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeartbeatPhaseOrderSqlTests.cs:ANewProcessUnderTheSameInstanceId_RecordsItsVersionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeartbeatPhaseOrderSqlTests.cs:ARestartUnderTheSameInstanceId_StartsBehindTheOldProcess_AndIsNotEligibleAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperHeartbeatPhaseOrderTests.cs:ABeat_NeverMovesTheRecordedPhase_EitherWayAsync</tests>
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
    -- 278 (#1636): the stage record is the only writer of a recorded phase. A beat records one only as a
    -- registration: where the row records none, or for a new process under the same instance id (another host or
    -- process id), which starts at the bottom of the ladder. A NULL on the beat keeps what is recorded.
    lifecycle_phase = CASE
      WHEN EXCLUDED.lifecycle_phase IS NOT NULL
           AND (__SCHEMA__.wh_service_instances.lifecycle_phase IS NULL
             OR __SCHEMA__.wh_service_instances.host_name IS DISTINCT FROM EXCLUDED.host_name
             OR __SCHEMA__.wh_service_instances.process_id IS DISTINCT FROM EXCLUDED.process_id)
        THEN EXCLUDED.lifecycle_phase
      ELSE __SCHEMA__.wh_service_instances.lifecycle_phase
    END,
    -- 278 (#1636): the version is recorded at registration, the same way: one process runs one version.
    library_version = CASE
      WHEN EXCLUDED.library_version IS NOT NULL
           AND (__SCHEMA__.wh_service_instances.library_version IS NULL
             OR __SCHEMA__.wh_service_instances.host_name IS DISTINCT FROM EXCLUDED.host_name
             OR __SCHEMA__.wh_service_instances.process_id IS DISTINCT FROM EXCLUDED.process_id)
        THEN EXCLUDED.library_version
      ELSE __SCHEMA__.wh_service_instances.library_version
    END,
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
'Decoupled heartbeat UPSERT. Inserts a new wh_service_instances row on first call, updates last_heartbeat_at on subsequent calls. Called by the C# HeartbeatWorker on its own timer, independent of polling cadence, and by ClaimWorker to register when a claim reports its row missing or stale (196). Opportunistically cleans up stale peers when detected (cheap pre-check guard); since 199 that cleanup steps aside under a 100 ms lock timeout instead of waiting for rows a claim holds. Migration 106: returns FALSE and does nothing when the calling instance_id has been tombstoned in wh_instance_evictions (this instance was reaped and must not rejoin); returns TRUE otherwise. Migration 147: p_stale_threshold_seconds lets the caller derive the peer-reap threshold from its own cadence instead of the 30 s constant. Migration 203: p_connection_mode records how the instance reaches the database '
'(direct or pooled; null keeps the recorded value), and the beat of the partition assigner renews the lease of the '
'assignment it published while it still holds the role. Migration 278 (#1636): the stage record '
'(wh_record_instance_transition) is the only writer of a recorded phase; a beat records p_lifecycle_phase and '
'p_library_version only as a registration: on the row it inserts, where the row records none, and for a new process '
'under the same instance id. Every other beat leaves them as recorded.';
