-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 316_OneDurableInstanceRecord.sql
-- Date: 2026-11-03
-- Description: The instance row is the durable record instances coordinate through, judged by ONE liveness rule (#2027).
--
--              Before this, "alive" had several definitions: claim_work ranked peers by a fresh heartbeat inside a fixed
--              30 second cutoff or a held alive-lock (204); the reap took the caller's cadence-derived threshold and its
--              own definitive-dead cutoff (147, v0.687); is_instance_alive took whatever threshold its caller passed; the
--              orphan claims counted a stream owner live by its LISTEN connection in pg_stat_activity; and the C# side
--              kept windows of its own (the partition assigner, the standby handshake, the fleet readers). Each reader
--              could call the same instance dead and alive at the same moment.
--
--              The rule, defined once:
--                * wh_instance_liveness(row, alive_lock_held): a direct instance is live while it holds its
--                  alive-lock and its heartbeat is newer than the definitive-dead cutoff (twice its window, at least
--                  five minutes), or while its heartbeat is newer than one beat (30 seconds), so a direct instance
--                  whose lock is gone is taken over after one beat (owner decision on #2022). Any other instance
--                  cannot hold the lock and is live while its heartbeat is newer than the window it declares for
--                  itself (liveness_window_seconds, recorded from its own cadence: HeartbeatLivenessThreshold), so a
--                  single late beat never reads it as dead. A row that records no window (an older build) is judged
--                  by 30 seconds, the cutoff the claim always used.
--                * wh_live_instance_ids(): the instances the rule calls live, reading the alive-locks through 204's
--                  wh_direct_alive_lock_holders only for a direct row past its window.
--                * wh_instance_is_live(id): the same rule for one instance; no row is not live.
--              is_instance_alive keeps its signature for older callers and delegates to the rule; the threshold it is
--              passed is no longer read. The reap (cleanup_stale_instances) removes exactly the instances the rule calls
--              not live and wakes exactly the ones it calls live; record_heartbeat records the window and asks the rule.
--
--              The record also carries both versions: the application version beside the library version, on every
--              instance row (application_version, NULL when unknown), recorded at registration by record_heartbeat, the
--              same way as the library version.
--
--              The columns and the rule are Preflight's (bootstrap region): the migrator reads the rule before the main
--              pass (FleetVersions), and the standby handshake reads it before this release's pass has run. 204's
--              wh_direct_alive_lock_holders is copied verbatim into the region because the rule's set form calls it,
--              and a SQL function's body is checked when it is created.
--
--              052's diagnostic view wh_live_instances (listen_alive) is left as it is: no Whizbang reader uses it, and
--              adding a column to it would make a re-run of 052 fail (a view cannot lose columns).
--
--              Number 316: the next free number after the ones taken on develop and in open pull requests.
--
-- Dependencies: 277 (the instance columns, in the closure), 278 (record_heartbeat, copied verbatim apart from the delta), 204 (wh_direct_alive_lock_holders, copied verbatim), 162
--               (cleanup_stale_instances, copied apart from the rule), 055 (is_instance_alive's signature)
-- Objects: wh_service_instances, wh_direct_alive_lock_holders, wh_instance_liveness, wh_live_instance_ids, wh_instance_is_live,
--          is_instance_alive, record_heartbeat, cleanup_stale_instances
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README
--            rule 12).

-- @whizbang:bootstrap-begin
-- Bootstrap: the instance record's columns and the one liveness rule (read by the migrator and the standby handshake
-- before the main pass).

-- ---------------------------------------------------------------------------------------------
-- The instance row's new columns, added only when missing.
-- ---------------------------------------------------------------------------------------------
DO $$
BEGIN
  IF (SELECT count(*) FROM pg_attribute
      WHERE attrelid = to_regclass('__SCHEMA__.wh_service_instances')
        AND attname IN ('application_version', 'liveness_window_seconds')
        AND NOT attisdropped) < 2 THEN
    ALTER TABLE __SCHEMA__.wh_service_instances
      ADD COLUMN IF NOT EXISTS application_version TEXT,
      ADD COLUMN IF NOT EXISTS liveness_window_seconds INTEGER;

    COMMENT ON COLUMN __SCHEMA__.wh_service_instances.application_version IS
      '316 (#2027): the version of the application the instance runs, beside library_version: SemVer text, '
      '''deploy:<n>'' for a host-supplied deploy number, NULL when unknown. Recorded at registration.';
    COMMENT ON COLUMN __SCHEMA__.wh_service_instances.liveness_window_seconds IS
      '316 (#2027): how old the instance''s heartbeat may get before the one liveness rule (wh_instance_liveness) calls '
      'it not live, declared by the instance from its own cadence. NULL (an older build) is judged by 30 seconds.';
  END IF;
END $$;

-- ---------------------------------------------------------------------------------------------
-- wh_direct_alive_lock_holders: 204's definition, word for word, so the rule below can call it.
-- ---------------------------------------------------------------------------------------------
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
-- wh_instance_liveness: THE liveness rule. Every reader judges an instance through it.
-- ---------------------------------------------------------------------------------------------
-- A plain SQL expression with no subquery of its own, so the planner inlines it: the alive-lock answer is the caller's
-- argument, evaluated last and only for a direct row past one beat and inside the definitive-dead cutoff.
-- <docs>fundamentals/workers/instance-liveness</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceLivenessRuleSqlTests.cs:Liveness_AFreshHeartbeatWithinTheInstancesOwnWindow_IsLiveAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceLivenessRuleSqlTests.cs:Liveness_AHeartbeatPastTheInstancesOwnWindow_IsNotLiveAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceLivenessRuleSqlTests.cs:Liveness_ARowThatRecordsNoWindow_IsJudgedByThirtySecondsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceLivenessRuleSqlTests.cs:Liveness_ADirectInstanceHoldingItsAliveLock_IsLivePastItsWindowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceLivenessRuleSqlTests.cs:Liveness_ADirectInstanceHoldingItsAliveLock_PastTheDefinitiveCutoff_IsNotLiveAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceLivenessRuleSqlTests.cs:Liveness_APooledInstanceHoldingALock_IsJudgedByItsHeartbeatAloneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceLivenessRuleSqlTests.cs:Liveness_ADirectInstanceWithoutItsLock_IsLiveForOneBeatAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceLivenessRuleSqlTests.cs:Liveness_ADirectInstanceWithoutItsLock_PastOneBeat_IsNotLiveInsideItsWindowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceLivenessRuleSqlTests.cs:Liveness_APooledInstance_IsLiveInsideItsWindowPastOneBeatAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_instance_liveness(
  p_instance __SCHEMA__.wh_service_instances,
  p_alive_lock_held BOOLEAN
) RETURNS BOOLEAN
LANGUAGE sql
STABLE
AS $$
  SELECT CASE
    WHEN p_instance.connection_mode = 'direct' THEN
      p_instance.last_heartbeat_at >= now() - INTERVAL '30 seconds'
      OR (p_instance.last_heartbeat_at >= now() - GREATEST(INTERVAL '5 minutes',
            make_interval(secs => 2 * COALESCE(p_instance.liveness_window_seconds, 30)))
          AND p_alive_lock_held)
    ELSE p_instance.last_heartbeat_at >= now() - make_interval(secs => COALESCE(p_instance.liveness_window_seconds, 30))
  END
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_instance_liveness(__SCHEMA__.wh_service_instances, BOOLEAN) IS
  '316 (#2027): the one liveness rule. A direct instance is live while it holds its alive-lock and its heartbeat is '
  'newer than the definitive-dead cutoff (twice its window, at least five minutes), or while its heartbeat is newer '
  'than one beat (30 seconds): a direct instance whose lock is gone is taken over after one beat. Any other instance '
  '(behind a transaction pooler, unable to hold the lock) is live while its heartbeat is newer than its own window '
  '(liveness_window_seconds, 30 seconds when unrecorded). The caller passes whether the alive-lock is held; '
  'wh_live_instance_ids reads it from wh_direct_alive_lock_holders.';

-- ---------------------------------------------------------------------------------------------
-- wh_live_instance_ids: the instances the rule calls live.
-- ---------------------------------------------------------------------------------------------
-- <docs>fundamentals/workers/instance-liveness</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceLivenessRuleSqlTests.cs:Liveness_ADirectInstanceHoldingItsAliveLock_IsLivePastItsWindowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceLivenessRuleSqlTests.cs:Liveness_ADirectInstanceWhoseLockIsReleased_IsJudgedByItsWindowAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_live_instance_ids()
RETURNS TABLE(instance_id UUID)
LANGUAGE sql
STABLE
AS $$
  SELECT si.instance_id
  FROM __SCHEMA__.wh_service_instances si
  WHERE __SCHEMA__.wh_instance_liveness(
    si, si.instance_id IN (SELECT h.instance_id FROM __SCHEMA__.wh_direct_alive_lock_holders() h))
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_live_instance_ids() IS
  '316 (#2027): every instance the one liveness rule (wh_instance_liveness) calls live, the alive-locks read through '
  'wh_direct_alive_lock_holders.';

-- ---------------------------------------------------------------------------------------------
-- wh_instance_is_live: the rule for one instance.
-- ---------------------------------------------------------------------------------------------
-- <docs>fundamentals/workers/instance-liveness</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceLivenessRuleSqlTests.cs:IsLive_AnInstanceWithNoRow_IsNotLiveAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceLivenessRuleSqlTests.cs:Liveness_AFreshHeartbeatWithinTheInstancesOwnWindow_IsLiveAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_instance_is_live(p_instance_id UUID)
RETURNS BOOLEAN
LANGUAGE sql
STABLE
AS $$
  SELECT EXISTS (SELECT 1 FROM __SCHEMA__.wh_live_instance_ids() l WHERE l.instance_id = p_instance_id)
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_instance_is_live(UUID) IS
  '316 (#2027): whether the one liveness rule calls this instance live. An instance with no row is not live.';

-- @whizbang:bootstrap-end

-- ---------------------------------------------------------------------------------------------
-- is_instance_alive: 055's signature, delegating to the rule. The threshold is no longer read.
-- ---------------------------------------------------------------------------------------------
-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('is_instance_alive');

-- <docs>fundamentals/workers/instance-liveness</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceLivenessRuleSqlTests.cs:IsInstanceAlive_DelegatesToTheRule_WhateverThresholdTheCallerPassesAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.is_instance_alive(
  p_instance_id UUID,
  p_heartbeat_threshold_seconds INT DEFAULT 90
) RETURNS BOOLEAN AS $$
BEGIN
  -- 316 (#2027): kept for the callers of older releases; the rule decides, and the threshold is not read.
  RETURN __SCHEMA__.wh_instance_is_live(p_instance_id);
END;
$$ LANGUAGE plpgsql STABLE;

COMMENT ON FUNCTION __SCHEMA__.is_instance_alive(UUID, INT) IS
  '055: whether an instance is alive. 316 (#2027): delegates to the one liveness rule (wh_instance_is_live); the '
  'threshold argument is kept for older callers and is no longer read.';

-- ---------------------------------------------------------------------------------------------
-- record_heartbeat: last word 278, recording the window and the application version, and asking the rule.
-- ---------------------------------------------------------------------------------------------
-- The arity changes, so the old overload goes first (exactly one overload per framework function).
SELECT __SCHEMA__.drop_all_overloads('record_heartbeat');

-- <docs>operations/startup/startup-status#registration-writes</docs>
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
'p_application_version is recorded like p_library_version.';

-- ---------------------------------------------------------------------------------------------
-- cleanup_stale_instances: last word 162; the rule decides who is reaped and who is woken.
-- ---------------------------------------------------------------------------------------------
-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('cleanup_stale_instances');

-- <docs>fundamentals/workers/instance-liveness</docs>
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
    RETURNING si.instance_id
  )
  SELECT ARRAY_AGG(instance_id) INTO v_deleted_ids
  FROM deleted;

  -- Release all work from deleted instances
  IF v_deleted_ids IS NOT NULL THEN
    -- Tombstone every reaped instance so a paused process that resumes and calls
    -- record_heartbeat again is refused rather than silently rejoining. See migration 106.
    -- ON CONFLICT is defensive only: an instance_id cannot be re-deleted once gone, so a
    -- collision here would mean a caller reused an id, which this must not paper over by
    -- discarding the earlier eviction's timestamp.
    INSERT INTO __SCHEMA__.wh_instance_evictions (instance_id, evicted_at, reason)
    SELECT unnest(v_deleted_ids), NOW(), 'not live (wh_instance_liveness)'
    ON CONFLICT (instance_id) DO NOTHING;

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
    -- Per-instance channel naming matches existing PgWorkNotificationListener.ChannelName:
    --   wh_work_i_{instance_id}
    -- Payload "orphan" signals "go run claim_orphaned_*" to ClaimWorker._onSignal.
    PERFORM pg_notify('wh_work_i_' || l.instance_id::text, 'orphan')
    FROM __SCHEMA__.wh_live_instance_ids() l;  -- 316: live instances only, by the one rule
  END IF;

  -- Return deleted IDs for orchestrator logging
  RETURN QUERY
  SELECT UNNEST(COALESCE(v_deleted_ids, ARRAY[]::UUID[]));
END;
$$ LANGUAGE plpgsql;

