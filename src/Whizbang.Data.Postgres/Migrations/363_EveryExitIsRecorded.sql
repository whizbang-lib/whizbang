-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 363_EveryExitIsRecorded.sql
-- Date: 2026-11-07
-- Description: Every exit of an instance is recorded, and an eviction says when, by whom and why (#2126).
--
--              Every way a process exits goes through one exit path, which writes one log entry and records the same
--              facts here: one row per exit of any kind (a host shutdown, an operator's stop, an upgrade stand-down, a
--              crash, a startup failure, an incompatible schema, an eviction), not columns on the eviction tombstone,
--              since most exits are not evictions. The write is best effort: when the database cannot be reached the
--              log entry says the row could not be written.
--
--              An evicted instance exits (code 3) so its host restarts it under a new instance id. Its exit states the
--              eviction as the evicting peer recorded it, so the tombstone now records it in full:
--                * last_heartbeat_at and liveness_window_seconds: what the one liveness rule (316) judged the instance
--                  by, taken from the row the reap deleted;
--                * evicted_by for a reap too (110 recorded it only for the standby handshake's fence): record_heartbeat
--                  names itself as the reaper for the reap it runs, and cleanup_stale_instances writes that name.
--
--              The table, its function and the tombstone's new columns are in the bootstrap region: a starting instance
--              beats from its registration (362), so it can be evicted, and exit, before the main pass reaches this
--              file. wh_record_instance_exit reads the tombstone in the same call, so the exit's entry can state the
--              eviction from one round trip. Rows older than 30 days are removed by the next write.
--
--              record_heartbeat (316's) and cleanup_stale_instances (330's) are redefined whole, each with its delta.
--
--              Number 363: above every number on develop (through 358) and in open pull requests.
--
-- Dependencies: 106 (wh_instance_evictions), 110 and 277 (evicted_by), 316 (record_heartbeat, liveness_window_seconds),
--               330 (cleanup_stale_instances)
-- Objects: wh_instance_exits, wh_record_instance_exit, wh_instance_evictions, record_heartbeat, cleanup_stale_instances
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README
--            rule 12).

-- @whizbang:bootstrap-begin
-- Bootstrap: an instance can be evicted, and exit, from its registration on.

ALTER TABLE __SCHEMA__.wh_instance_evictions ADD COLUMN IF NOT EXISTS last_heartbeat_at TIMESTAMPTZ;
ALTER TABLE __SCHEMA__.wh_instance_evictions ADD COLUMN IF NOT EXISTS liveness_window_seconds INTEGER;

COMMENT ON COLUMN __SCHEMA__.wh_instance_evictions.last_heartbeat_at IS
  '363 (#2126): the evicted instance''s last heartbeat, as the reap that deleted its row saw it. NULL on a tombstone '
  'written before 363, or by the standby handshake''s fence.';
COMMENT ON COLUMN __SCHEMA__.wh_instance_evictions.liveness_window_seconds IS
  '363 (#2126): the liveness window the one rule judged the evicted instance by (NULL: its row recorded none, and the '
  'rule used 30 seconds).';

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_instance_exits (
  exit_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  instance_id UUID NOT NULL,
  service_name TEXT NOT NULL,
  host_name TEXT NOT NULL,
  process_id INTEGER NOT NULL,
  exit_cause TEXT NOT NULL,
  exit_code INTEGER NOT NULL,
  intended BOOLEAN NOT NULL,
  reason TEXT NOT NULL,
  exception_type TEXT,
  exception_message TEXT,
  startup_stage TEXT,
  lifecycle_phase TEXT,
  uptime_seconds DOUBLE PRECISION,
  seconds_since_last_heartbeat DOUBLE PRECISION,
  held_roles TEXT[] NOT NULL DEFAULT ARRAY[]::TEXT[],
  held_leases INTEGER NOT NULL DEFAULT 0,
  in_hand INTEGER NOT NULL DEFAULT 0,
  library_version TEXT,
  evicted_at TIMESTAMPTZ,
  evicted_by UUID,
  eviction_reason TEXT,
  evicted_last_heartbeat_at TIMESTAMPTZ,
  evicted_liveness_window_seconds INTEGER,
  exited_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

COMMENT ON TABLE __SCHEMA__.wh_instance_exits IS
  '363 (#2126): one row per exit of an instance, of any kind, written by the one exit path when the database is '
  'reachable: the cause (ExitCause) and exit code, whether it was intended, why, where the instance was (stage, phase, '
  'uptime, time since its last accepted heartbeat), what it held, and for an eviction the tombstone''s account of it. '
  'Rows older than 30 days are removed by the next write.';

CREATE INDEX IF NOT EXISTS idx_instance_exits_exited_at ON __SCHEMA__.wh_instance_exits (exited_at);
CREATE INDEX IF NOT EXISTS idx_instance_exits_instance_id ON __SCHEMA__.wh_instance_exits (instance_id);

-- ---------------------------------------------------------------------------------------------
-- wh_record_instance_exit: one exit's row, with the instance's tombstone read in the same call.
-- ---------------------------------------------------------------------------------------------
-- <docs>operations/startup/instance-exits#the-exit-record</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceExitRecordSqlTests.cs:AnExit_IsRecordedWithEveryFact_AndNoTombstoneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceExitRecordSqlTests.cs:AnEvictedInstancesExit_RecordsAndReturnsTheTombstoneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceExitRecordSqlTests.cs:AWrite_RemovesRowsOlderThanThirtyDaysAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperInstanceExitTests.cs:AnEvictedInstancesExit_RecordsAndReturnsTheTombstoneAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_record_instance_exit(
  p_instance_id UUID,
  p_service_name TEXT,
  p_host_name TEXT,
  p_process_id INTEGER,
  p_exit_cause TEXT,
  p_exit_code INTEGER,
  p_intended BOOLEAN,
  p_reason TEXT,
  p_exception_type TEXT,
  p_exception_message TEXT,
  p_startup_stage TEXT,
  p_lifecycle_phase TEXT,
  p_uptime_seconds DOUBLE PRECISION,
  p_seconds_since_last_heartbeat DOUBLE PRECISION,
  p_held_roles TEXT[],
  p_held_leases INTEGER,
  p_in_hand INTEGER,
  p_library_version TEXT
) RETURNS TABLE(
  recorded_exit_id BIGINT,
  tombstone_evicted_at TIMESTAMPTZ,
  tombstone_evicted_by UUID,
  tombstone_reason TEXT,
  tombstone_last_heartbeat_at TIMESTAMPTZ,
  tombstone_liveness_window_seconds INTEGER
)
LANGUAGE sql
SET timezone = 'UTC'
AS $$
  -- Exits are rare, so the retention runs with each write rather than as a maintenance step.
  WITH purged AS (
    DELETE FROM __SCHEMA__.wh_instance_exits x WHERE x.exited_at < NOW() - INTERVAL '30 days'
  ),
  tombstone AS (
    SELECT e.evicted_at, e.evicted_by, e.reason, e.last_heartbeat_at, e.liveness_window_seconds
    FROM __SCHEMA__.wh_instance_evictions e
    WHERE e.instance_id = p_instance_id
  ),
  written AS (
    INSERT INTO __SCHEMA__.wh_instance_exits
      (instance_id, service_name, host_name, process_id, exit_cause, exit_code, intended, reason, exception_type,
       exception_message, startup_stage, lifecycle_phase, uptime_seconds, seconds_since_last_heartbeat, held_roles,
       held_leases, in_hand, library_version, evicted_at, evicted_by, eviction_reason, evicted_last_heartbeat_at,
       evicted_liveness_window_seconds)
    SELECT p_instance_id, p_service_name, p_host_name, p_process_id, p_exit_cause, p_exit_code, p_intended, p_reason,
           p_exception_type, p_exception_message, p_startup_stage, p_lifecycle_phase, p_uptime_seconds,
           p_seconds_since_last_heartbeat, COALESCE(p_held_roles, ARRAY[]::TEXT[]), COALESCE(p_held_leases, 0),
           COALESCE(p_in_hand, 0), p_library_version, t.evicted_at, t.evicted_by, t.reason, t.last_heartbeat_at,
           t.liveness_window_seconds
    FROM (SELECT 1) AS one
    LEFT JOIN tombstone t ON TRUE
    RETURNING exit_id, evicted_at, evicted_by, eviction_reason, evicted_last_heartbeat_at, evicted_liveness_window_seconds
  )
  SELECT w.exit_id, w.evicted_at, w.evicted_by, w.eviction_reason, w.evicted_last_heartbeat_at,
         w.evicted_liveness_window_seconds
  FROM written w;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_record_instance_exit(UUID, TEXT, TEXT, INTEGER, TEXT, INTEGER, BOOLEAN, TEXT, TEXT, TEXT, TEXT, TEXT, DOUBLE PRECISION, DOUBLE PRECISION, TEXT[], INTEGER, INTEGER, TEXT) IS
  '363 (#2126): writes one row of wh_instance_exits for an exit of p_instance_id, with the instance''s eviction '
  'tombstone when it has one, and returns the row''s id and the tombstone''s facts (NULL when it has none), so the '
  'exit''s log entry can state them. Removes rows older than 30 days.';

-- @whizbang:bootstrap-end

-- ---------------------------------------------------------------------------------------------
-- record_heartbeat: 316's definition, naming itself as the reaper of the reap it runs.
-- ---------------------------------------------------------------------------------------------
-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('record_heartbeat');
-- <docs>operations/startup/startup-status#registration-writes</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceExitRecordSqlTests.cs:APeersReap_RecordsTheReaper_AndTheHeartbeatAndWindowItJudgedAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperInstanceExitTests.cs:APeersReap_RecordsTheReaper_AndTheHeartbeatAndWindowItJudgedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceLivenessRuleSqlTests.cs:Heartbeat_RecordsTheWindowAndTheApplicationVersionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceLivenessRuleSqlTests.cs:Heartbeat_ANullWindowKeepsTheRecordedOneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceLivenessRuleSqlTests.cs:Heartbeat_RecordsTheApplicationVersionOnlyAsARegistrationAsync</tests>
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
  p_connection_mode TEXT DEFAULT NULL,
  -- 316 (#2027): the application version, recorded at registration like the library version. NULL keeps it.
  p_application_version TEXT DEFAULT NULL
) RETURNS BOOLEAN AS $$
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
     connection_mode, liveness_window_seconds, application_version)
  VALUES
    (p_instance_id, p_service_name, p_host_name, p_process_id, NOW(), NOW(), p_metadata, p_lifecycle_phase, p_library_version,
     p_connection_mode, p_stale_threshold_seconds, p_application_version)
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
    -- 316 (#2027): the application version, recorded the same way as the library version.
    application_version = CASE
      WHEN EXCLUDED.application_version IS NOT NULL
           AND (__SCHEMA__.wh_service_instances.application_version IS NULL
             OR __SCHEMA__.wh_service_instances.host_name IS DISTINCT FROM EXCLUDED.host_name
             OR __SCHEMA__.wh_service_instances.process_id IS DISTINCT FROM EXCLUDED.process_id)
        THEN EXCLUDED.application_version
      ELSE __SCHEMA__.wh_service_instances.application_version
    END,
    -- 316 (#2027): the window this instance declares for the one liveness rule (wh_instance_liveness), derived from its
    -- own cadence; a NULL keeps what was recorded.
    liveness_window_seconds = COALESCE(EXCLUDED.liveness_window_seconds, __SCHEMA__.wh_service_instances.liveness_window_seconds),
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
  -- 316 (#2027): the pre-check asks the one rule whether any peer is past its own window by its heartbeat alone (no
  -- lock is read here); the reap then removes exactly the peers the rule says are not live.
  IF EXISTS (
    SELECT 1 FROM __SCHEMA__.wh_service_instances si
    WHERE NOT __SCHEMA__.wh_instance_liveness(si, FALSE)
      AND si.instance_id != p_instance_id
    LIMIT 1
  ) THEN
    -- 199: a heartbeat never waits for the reap. Its lease releases wait on rows a claim may hold, so
    -- the reap runs in a subtransaction under a short lock timeout and is rolled back when a lock is
    -- not available; the backstop and the next heartbeat try again. The timeout is set inside the
    -- subtransaction, so the rollback restores the caller's value with it.
    BEGIN
      PERFORM set_config('lock_timeout', '100ms', true);
      -- 363 (#2126): the reap records on each tombstone which instance evicted the peer. Local to the subtransaction, so
      -- nothing else this transaction runs reads it.
      PERFORM set_config('whizbang.reaper_instance_id', p_instance_id::text, true);
      PERFORM __SCHEMA__.cleanup_stale_instances(NULL, NULL);
    EXCEPTION WHEN lock_not_available OR deadlock_detected THEN
      NULL;
    END;
  END IF;

  RETURN TRUE;
END;
$$ LANGUAGE plpgsql;
COMMENT ON FUNCTION __SCHEMA__.record_heartbeat(UUID, TEXT, TEXT, INTEGER, JSONB, TEXT, TEXT, INTEGER, TEXT, TEXT) IS
'Decoupled heartbeat UPSERT. Inserts a new wh_service_instances row on first call, updates last_heartbeat_at on subsequent calls. Called by the C# HeartbeatWorker on its own timer, independent of polling cadence, and by ClaimWorker to register when a claim reports its row missing or stale (196). Opportunistically cleans up stale peers when detected (cheap pre-check guard); since 199 that cleanup steps aside under a 100 ms lock timeout instead of waiting for rows a claim holds. Migration 106: returns FALSE and does nothing when the calling instance_id has been tombstoned in wh_instance_evictions (this instance was reaped and must not rejoin); returns TRUE otherwise. Migration 147: p_stale_threshold_seconds lets the caller derive the peer-reap threshold from its own cadence instead of the 30 s constant. Migration 203: p_connection_mode records how the instance reaches the database '
'(direct or pooled; null keeps the recorded value), and the beat of the partition assigner renews the lease of the '
'assignment it published while it still holds the role. Migration 278 (#1636): the stage record '
'(wh_record_instance_transition) is the only writer of a recorded phase; a beat records p_lifecycle_phase and '
'p_library_version only as a registration: on the row it inserts, where the row records none, and for a new process '
'under the same instance id. Every other beat leaves them as recorded. Migration 316 (#2027): p_stale_threshold_seconds '
'is recorded as the instance''s liveness window (NULL keeps it), the one rule (wh_instance_liveness) judges every peer, and '
'p_application_version is recorded like p_library_version. Migration 363 (#2126): the reap it runs records this instance '
'as the evicting peer on each tombstone.';

-- ---------------------------------------------------------------------------------------------
-- cleanup_stale_instances: 330's definition, recording on each tombstone the reaper and what the rule judged.
-- ---------------------------------------------------------------------------------------------
-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('cleanup_stale_instances');

-- <docs>fundamentals/workers/instance-liveness</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceExitRecordSqlTests.cs:APeersReap_RecordsTheReaper_AndTheHeartbeatAndWindowItJudgedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceExitRecordSqlTests.cs:AReapByAnyOtherCaller_RecordsNoReaperAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CleanupStaleInstancesOrphanNotifySqlTests.cs:CleanupStaleInstances_OneStaleOneLive_EmitsOrphanOnLiveChannelAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceLivenessRuleSqlTests.cs:Reap_RemovesExactlyTheInstancesTheRuleSaysAreNotLive_WhateverCutoffTheCallerPassesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceLivenessRuleSqlTests.cs:Reap_ADirectInstanceHoldingItsLockPastTheDefinitiveCutoff_IsRemovedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceLivenessRuleSqlTests.cs:Reap_WakesOnlyLiveInstancesAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.cleanup_stale_instances(
  p_stale_cutoff TIMESTAMPTZ,
  p_definitive_dead_cutoff TIMESTAMPTZ DEFAULT NULL
) RETURNS TABLE(deleted_instance_id UUID) AS $$
DECLARE
  v_deleted_ids UUID[];
BEGIN

  -- 316 (#2027): the one liveness rule decides. A row is reaped when wh_instance_liveness says it is not live: past its
  -- own window by its heartbeat, and not a direct instance holding its alive-lock inside the definitive-dead cutoff.
  -- p_stale_cutoff and p_definitive_dead_cutoff are kept for the callers of older releases and no longer read: the
  -- window is each instance's own, recorded on its row.
  WITH deleted AS (
    DELETE FROM __SCHEMA__.wh_service_instances si
    WHERE si.instance_id NOT IN (SELECT l.instance_id FROM __SCHEMA__.wh_live_instance_ids() l)
    RETURNING si.instance_id, si.last_heartbeat_at, si.liveness_window_seconds
  ),
  -- Tombstone every reaped instance so a paused process that resumes and calls
  -- record_heartbeat again is refused rather than silently rejoining. See migration 106.
  -- ON CONFLICT is defensive only: an instance_id cannot be re-deleted once gone, so a
  -- collision here would mean a caller reused an id, which this must not paper over by
  -- discarding the earlier eviction's timestamp.
  -- 363 (#2126): the tombstone records what the rule judged the instance by (its last heartbeat and its window) and the
  -- peer whose heartbeat ran the reap (record_heartbeat sets whizbang.reaper_instance_id; NULL for any other caller), so
  -- the evicted instance's exit can say when, by whom and why.
  tombstoned AS (
    INSERT INTO __SCHEMA__.wh_instance_evictions
      (instance_id, evicted_at, reason, evicted_by, last_heartbeat_at, liveness_window_seconds)
    SELECT d.instance_id, NOW(), 'not live (wh_instance_liveness)',
           NULLIF(current_setting('whizbang.reaper_instance_id', true), '')::UUID,
           d.last_heartbeat_at, d.liveness_window_seconds
    FROM deleted d
    ON CONFLICT (instance_id) DO NOTHING
  )
  SELECT ARRAY_AGG(instance_id) INTO v_deleted_ids
  FROM deleted;

  -- Release all work from deleted instances
  IF v_deleted_ids IS NOT NULL THEN
    -- Release outbox messages
    UPDATE __SCHEMA__.wh_outbox
    SET instance_id = NULL,
        lease_expiry = NULL
    WHERE instance_id = ANY(v_deleted_ids);

    -- Release inbox messages
    -- 162: the inbox's claim state lives in wh_inbox_state, so releasing a dead instance's inbox
    -- leases no longer rewrites the message rows. This is the reclaim path that runs most often on
    -- a fleet losing pods, and it was the one paying the most per row released.
    UPDATE __SCHEMA__.wh_inbox_state
    SET instance_id = NULL,
        lease_expiry = NULL
    WHERE instance_id = ANY(v_deleted_ids);

    -- Release perspective events
    UPDATE __SCHEMA__.wh_perspective_events
    SET instance_id = NULL,
        lease_expiry = NULL
    WHERE instance_id = ANY(v_deleted_ids);

    -- Release active stream assignments from deleted instances
    UPDATE __SCHEMA__.wh_active_streams
    SET assigned_instance_id = NULL,
        lease_expiry = NULL
    WHERE assigned_instance_id = ANY(v_deleted_ids);

    -- Release receptor processing leases from deleted instances
    UPDATE __SCHEMA__.wh_receptor_processing
    SET instance_id = NULL,
        lease_expiry = NULL
    WHERE instance_id = ANY(v_deleted_ids);

    -- Log stale instance removal to wh_log for audit trail
    INSERT INTO __SCHEMA__.wh_log (log_level, source, message_id, error_message, metadata)
    SELECT
      2,  -- Warning
      'stale_cleanup',
      unnest(v_deleted_ids),
      'Stale instance removed, all leases released',
      jsonb_build_object(
        'deleted_instance_count', array_length(v_deleted_ids, 1),
        'rule', 'wh_instance_liveness'
      );

    -- v0.502 slice B.3: orphan-redistribution NOTIFY.
    -- After releasing leases owned by the dead instances, wake every LIVE instance so it
    -- runs a catch-up claim_orphaned_* over the newly-unowned rows. Without this, live
    -- instances only discover the released work on their next poll tick, which under the
    -- new v0.502 NotifyHealthyPollingIntervalMilliseconds=30000 default could be up to
    -- 30 seconds away. Emitting a NOTIFY here turns orphan recovery from polling-bound to
    -- NOTIFY-bound, the architectural goal of v0.502.
    --
    -- 330 (#2046): a doorbell on the signal bus, never recorded. The instance's channel, wh_work_i_{instance_id}, is the
    -- one the bus's transport (and an older release's listener) LISTENs on; the payload "orphan" is the bus's
    -- WorkOrphanedSignal, which sends the claim worker to claim_orphaned_*.
    PERFORM __SCHEMA__.wh_publish_signal('wh_work_i_' || l.instance_id::text, 'orphan')
    FROM __SCHEMA__.wh_live_instance_ids() l;  -- 316: live instances only, by the one rule
  END IF;

  -- Return deleted IDs for orchestrator logging
  RETURN QUERY
  SELECT UNNEST(COALESCE(v_deleted_ids, ARRAY[]::UUID[]));
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.cleanup_stale_instances(TIMESTAMPTZ, TIMESTAMPTZ) IS
  'Reaps every instance the one liveness rule (wh_instance_liveness) says is not live, tombstones it in wh_instance_evictions, releases its leases and wakes the live instances. 363 (#2126): each tombstone records the reaper (the instance whose record_heartbeat ran the reap; NULL for any other caller) and the last heartbeat and liveness window the rule judged the instance by.';
