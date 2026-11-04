-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 330_CoordinationOnTheSignalBus.sql
-- Date: 2026-11-04
-- Description: The coordination channels move onto the signal bus (#2046, part 4 of the coordination standard, #2027).
--
--              * One SQL function publishes: wh_publish_signal(channel, payload, durable wire name). It notifies the
--                channel at commit, and for a decision it also records the signal, with its payload, in the durable
--                signal log (wh_signals), which every instance's tail replays to a subscriber that missed the
--                notification. A doorbell is never recorded. Nothing else in the corpus notifies but this function
--                and the after-commit doorbell ringer (ring_doorbells), both the bus's.
--              * wh_signals gains payload (NULL for the doorbell-only durable signals of older releases), so a decision
--                is replayed with what changed, not only that something did.
--              * The four functions that notified a raw channel publish through it instead: a released role and a
--                published partition assignment are decisions (durable); a recorded multi-stream run and the reap's
--                orphan wake are doorbells (best-effort, a poll behind each).
--
--              Rolling deploy: the channel and payload of every notification are unchanged (wh_role_released with the
--              role, wh_partition_assignment with epoch:revision, wh_multi_stream_work with the run id,
--              wh_work_i_<instance> with 'orphan'), so an older instance's listeners hear this release's SQL and this
--              release's bus hears an older release's SQL. An older instance's tail skips the new wire names (it does not
--              know them) and advances past them; it never reads payload. No deprecation window is needed.
--
--              The column, the publish function and wh_release_role are in the bootstrap region: wh_release_role is
--              Preflight's (184), so its redefinition is too, and what it calls must exist when the bootstrap closure
--              applies. On a database whose wh_signals does not exist yet (the bootstrap runs before 065 on a fresh
--              database), the column waits for the main pass and the publish notifies without recording.
--
--              Number 330: the next free number after the ones taken on develop and in open pull requests.
--
-- Dependencies: 065 (wh_signals), 184 (wh_release_role, copied apart from the publish), 203 (wh_publish_partition_assignment,
--               copied apart from the publish), 208 (wh_record_multi_stream_run, copied apart from the publish), 316
--               (cleanup_stale_instances, copied apart from the publish)
-- Objects: wh_signals, wh_publish_signal, wh_release_role, wh_publish_partition_assignment, wh_record_multi_stream_run,
--          cleanup_stale_instances
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README
--            rule 12).

-- @whizbang:bootstrap-begin
-- Bootstrap: the durable signal payload, the bus's SQL publish, and the role release that calls it (Preflight's, 184).

-- ---------------------------------------------------------------------------------------------
-- wh_signals.payload, added only when the table exists and the column is missing.
-- ---------------------------------------------------------------------------------------------
DO $$
BEGIN
  IF to_regclass('__SCHEMA__.wh_signals') IS NOT NULL
     AND NOT EXISTS (SELECT 1 FROM pg_attribute
                     WHERE attrelid = to_regclass('__SCHEMA__.wh_signals')
                       AND attname = 'payload'
                       AND NOT attisdropped) THEN
    ALTER TABLE __SCHEMA__.wh_signals ADD COLUMN IF NOT EXISTS payload TEXT;

    COMMENT ON COLUMN __SCHEMA__.wh_signals.payload IS
      '330 (#2046): what a decision carries (the released role, the published assignment version), replayed by the '
      'tail with the signal. NULL for a doorbell-only durable signal.';
  END IF;
END $$;

-- ---------------------------------------------------------------------------------------------
-- wh_publish_signal: the signal bus's one SQL publish.
-- ---------------------------------------------------------------------------------------------
-- <docs>fundamentals/signal-bus/signal-bus#coordination-signals</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CoordinationSignalsSqlTests.cs:PublishSignal_ADecision_IsRecordedWithItsPayload_AndNotifiedAtCommitAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CoordinationSignalsSqlTests.cs:PublishSignal_ADoorbell_IsNotifiedButNeverRecordedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CoordinationSignalsSqlTests.cs:PublishSignal_InsideATransactionThatRollsBack_LeavesNoTraceAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_publish_signal(
  p_channel TEXT,
  p_payload TEXT,
  p_durable_wire_name TEXT DEFAULT NULL
) RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
  -- A decision is recorded with the notification, in the caller's transaction, so it is announced only if it commits.
  -- Before 065 has created the log (the bootstrap on a fresh database) there is nothing to record into yet.
  IF p_durable_wire_name IS NOT NULL AND to_regclass('__SCHEMA__.wh_signals') IS NOT NULL THEN
    INSERT INTO __SCHEMA__.wh_signals (wire_name, payload) VALUES (p_durable_wire_name, p_payload);
  END IF;
  PERFORM pg_notify(p_channel, COALESCE(p_payload, ''));
END;
$$;
COMMENT ON FUNCTION __SCHEMA__.wh_publish_signal(TEXT, TEXT, TEXT) IS
  '330 (#2046): the signal bus''s SQL publish. Notifies p_channel with p_payload at commit; with a durable wire name, '
  'also records the signal and its payload in wh_signals for every instance''s tail to replay. The only SQL besides '
  'the after-commit doorbell ringer that notifies.';

-- ---------------------------------------------------------------------------------------------
-- wh_release_role: 184's definition, publishing the release on the bus.
-- ---------------------------------------------------------------------------------------------
-- vacate the role on a graceful stop, only when (instance, epoch) is the current assignment, so a stale grant cannot
-- vacate its successor.
--
-- <docs>proposals/duty-role-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentSqlTests.cs:Release_VacatesAtOnce_WithNoCooldownForTheReleaserAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentSqlTests.cs:Release_NotifiesWaiters_SoTheyReVoteAtOnceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentSqlTests.cs:Release_IsRecordedDurably_SoAnInstanceThatMissedTheNotificationIsStillToldAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_release_role(
  p_role TEXT,
  p_instance_id UUID,
  p_epoch BIGINT
) RETURNS BOOLEAN
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
BEGIN
  UPDATE __SCHEMA__.wh_role_assignments r
     SET holder_instance_id = NULL,
         lease_expires_at = NULL,
         drain_requested_at = NULL,
         drain_requested_by = NULL,
         duty_backend_pid = NULL,
         duty_backend_started_at = NULL,
         bridge_backend_pid = NULL,
         bridge_backend_started_at = NULL,
         last_holder_instance_id = p_instance_id,
         last_vacated_at = now(),
         last_vacated_reason = 'released',
         last_vacated_fleet_wide = FALSE
   WHERE r.role = p_role
     AND r.holder_instance_id = p_instance_id
     AND r.epoch = p_epoch;
  IF NOT FOUND THEN
    RETURN FALSE;
  END IF;
  PERFORM __SCHEMA__.release_capability(p_instance_id, p_role);
  -- 330 (#2046): published on the signal bus. A release is a decision: recorded in the durable signal log, with the
  -- role as its payload, and announced on wh_role_released at commit, the channel and payload an older release
  -- listens for.
  PERFORM __SCHEMA__.wh_publish_signal('wh_role_released', p_role, 'role-released');
  RETURN TRUE;
END;
$$;

-- @whizbang:bootstrap-end

-- ---------------------------------------------------------------------------------------------
-- wh_publish_partition_assignment: 203's definition, publishing the assignment on the bus.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_publish_partition_assignment');

-- <docs>fundamentals/work-coordinator/partition-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Publish_ByTheRoleHolder_WritesTheAssignmentAndAnnouncesItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Publish_ByADeposedAssigner_IsRefusedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Publish_AgainInTheSameTenure_AdvancesTheRevisionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Publish_ByANewTenure_RestartsTheRevisionAtTheNewEpochAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_publish_partition_assignment(
  p_role TEXT,
  p_instance_id UUID,
  p_epoch BIGINT,
  p_members UUID[],
  p_lease INTERVAL
) RETURNS BIGINT
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
DECLARE
  v_revision BIGINT;
BEGIN
  -- The fence where the assignment is written: only the current holder of the role, at the epoch it was granted,
  -- may publish. A deposed assigner gets WHF01 and writes nothing. The assert takes the role row FOR SHARE, so a
  -- vote cannot hand the role on between the check and the write below.
  PERFORM __SCHEMA__.wh_assert_role_epoch(p_role, p_instance_id, p_epoch);

  INSERT INTO __SCHEMA__.wh_published_partition_assignment AS pa
    (singleton, role, epoch, revision, assigner_instance_id, members, published_at, lease, lease_expires_at)
  VALUES (TRUE, p_role, p_epoch, 1, p_instance_id, p_members, clock_timestamp(), p_lease, clock_timestamp() + p_lease)
  ON CONFLICT (singleton) DO UPDATE SET
    role = EXCLUDED.role,
    epoch = EXCLUDED.epoch,
    revision = CASE WHEN pa.epoch = EXCLUDED.epoch THEN pa.revision + 1 ELSE 1 END,
    assigner_instance_id = EXCLUDED.assigner_instance_id,
    members = EXCLUDED.members,
    published_at = EXCLUDED.published_at,
    lease = EXCLUDED.lease,
    lease_expires_at = EXCLUDED.lease_expires_at
  -- Epochs only rise, so an assignment never goes back to an earlier tenure's.
  WHERE pa.epoch <= EXCLUDED.epoch
  RETURNING pa.revision INTO v_revision;

  IF v_revision IS NULL THEN
    RAISE EXCEPTION 'partition assignment at epoch % is older than the published one', p_epoch
      USING ERRCODE = 'WHF01';
  END IF;

  -- Claimers cache the assignment and refresh it on this; the payload says which version to expect.
  -- 330 (#2046): published on the signal bus. A publish is a decision: recorded in the durable signal log and
  -- announced on wh_partition_assignment at commit, the channel and payload an older release listens for.
  PERFORM __SCHEMA__.wh_publish_signal(
    'wh_partition_assignment', p_epoch::TEXT || ':' || v_revision::TEXT, 'partition-assignment-published');
  RETURN v_revision;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_publish_partition_assignment(TEXT, UUID, BIGINT, UUID[], INTERVAL) IS
  '203 (#1254): publishes the partition assignment. Fenced by wh_assert_role_epoch (WHF01 for a deposed assigner); '
  'the revision advances within an epoch and restarts at 1 for a new one; an older epoch is refused. Returns the '
  'revision. 330 (#2046): announces epoch:revision on the signal bus (wh_partition_assignment), recorded durably.';

-- ---------------------------------------------------------------------------------------------
-- wh_record_multi_stream_run: 208's definition, ringing its doorbell on the bus.
-- ---------------------------------------------------------------------------------------------
-- <docs>fundamentals/dispatcher/multi-stream-commands#intake</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandStoreSqlTests.cs:RecordRunAsync_ANewRun_IsPendingForEveryStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandStoreSqlTests.cs:RecordRunAsync_TheSameRunTwice_RecordsItOnceAndSaysSoAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandStoreSqlTests.cs:RecordRunAsync_ANewRun_WakesEveryInstanceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CoordinationSignalsSqlTests.cs:RecordingAMultiStreamRun_RingsTheOlderChannelWithTheRunId_AndRecordsNothingAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_record_multi_stream_run(
  p_run_id UUID,
  p_command_type TEXT,
  p_payload TEXT,
  p_scope TEXT,
  p_stream_ids UUID[],
  p_partition_count INTEGER
) RETURNS BOOLEAN
LANGUAGE plpgsql
AS $$
DECLARE
  v_streams UUID[];
BEGIN
  SELECT array_agg(DISTINCT s) INTO v_streams FROM unnest(p_stream_ids) AS s;

  INSERT INTO __SCHEMA__.wh_multi_stream_runs (run_id, command_type, payload, scope, stream_count)
  VALUES (p_run_id, p_command_type, p_payload, p_scope, cardinality(v_streams))
  ON CONFLICT (run_id) DO NOTHING;
  IF NOT FOUND THEN
    -- A repeat delivery: the first recording stands.
    RETURN FALSE;
  END IF;

  INSERT INTO __SCHEMA__.wh_multi_stream_run_streams (run_id, stream_id, partition_number)
  SELECT p_run_id, s, __SCHEMA__.compute_partition(s, p_partition_count)
  FROM unnest(v_streams) AS s;

  -- Delivered at commit, to every instance listening; a poll is the backstop.
  -- 330 (#2046): a doorbell on the signal bus, never recorded, on the channel and payload an older release listens for.
  PERFORM __SCHEMA__.wh_publish_signal('wh_multi_stream_work', p_run_id::TEXT);
  RETURN TRUE;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_record_multi_stream_run(UUID, TEXT, TEXT, TEXT, UUID[], INTEGER) IS
  '208 (#1358): records a multi-stream run and one pending item per distinct stream, idempotent on the run id; '
  'returns false for a run already recorded. 330 (#2046): rings wh_multi_stream_work on the signal bus.';

-- ---------------------------------------------------------------------------------------------
-- cleanup_stale_instances: 316's definition, waking the live instances on the bus.
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
