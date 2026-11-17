-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 476_UnusedCoordinationTablesAreDropped.sql: each function as its last word before 476 defined it,
-- claim_orphaned_receptor_work as 026 defined it, and wh_partition_assignments re-created empty, as an earlier
-- release's bootstrap creates it. The table held nothing anything read, so the reverse loses nothing.
-- Constants: the double-underscore tokens in this file are substituted from Migrations/constants.txt at apply time.

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_partition_assignments (
  partition_number INTEGER NOT NULL PRIMARY KEY,
  instance_id UUID NOT NULL,
  assigned_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
  last_heartbeat TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE INDEX IF NOT EXISTS idx_partition_assignments_instance
  ON __SCHEMA__.wh_partition_assignments (instance_id, last_heartbeat);

SELECT __SCHEMA__.drop_all_overloads('wh_refresh_stale_queue_statistics');

-- <docs>fundamentals/work-coordinator/claim-loop</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkStaleStatisticsTests.cs:ClaimWork_StatisticsFromBeforeTheLoad_AcquiringIsStillPricedByTheBatchAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkStaleStatisticsTests.cs:RefreshStaleQueueStatistics_FreshStatistics_AnalyzesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkStaleStatisticsTests.cs:RefreshStaleQueueStatistics_WhileAPeerRefreshesTheTable_WaitsAndDoesNotAnalyzeAgainAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkStaleStatisticsTests.cs:RefreshStaleQueueStatistics_WhileAnotherSessionHoldsTheTable_GivesUpWithinTheLockTimeoutAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkStaleStatisticsTests.cs:RefreshStaleQueueStatistics_AsADataOnlyRole_StillAnalyzesTheStaleTableAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_refresh_stale_queue_statistics()
RETURNS INTEGER
LANGUAGE plpgsql
-- The rights of the role that migrated the schema, which owns the tables (see the header). Pinned search_path: every
-- object below is schema-qualified, and nothing a caller puts on its path can be resolved in its place.
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
  -- The tables claim_work acquires from or joins, each of which is empty between loads.
  v_tables CONSTANT REGCLASS[] := ARRAY[
    '__SCHEMA__.wh_outbox'::REGCLASS,
    '__SCHEMA__.wh_inbox'::REGCLASS,
    '__SCHEMA__.wh_inbox_state'::REGCLASS,
    '__SCHEMA__.wh_perspective_events'::REGCLASS,
    '__SCHEMA__.wh_receptor_processing'::REGCLASS,
    '__SCHEMA__.wh_active_streams'::REGCLASS
  ];
  -- The advisory lock's first key: this function. The second is the table's oid, unique per schema.
  c_lock_class CONSTANT INTEGER := hashtext('wh_refresh_stale_queue_statistics');
  v_table REGCLASS;
  v_refreshed INTEGER := 0;
  -- The caller's lock timeout, put back once the refresh is done.
  v_caller_lock_timeout TEXT := current_setting('lock_timeout');
BEGIN
  -- The healthy path: one catalog read per table, nothing stale, nothing locked.
  IF NOT EXISTS (SELECT 1 FROM unnest(v_tables) t WHERE __SCHEMA__._wh_queue_statistics_stale(t)) THEN
    RETURN 0;
  END IF;

  BEGIN
    -- Local to this block: an exception rolls the setting back with the block, and the success path restores it.
    PERFORM set_config('lock_timeout', '5s', TRUE);
    FOREACH v_table IN ARRAY v_tables LOOP
      IF __SCHEMA__._wh_queue_statistics_stale(v_table) THEN
        -- One claimant analyzes a table at a time; the rest wait for it, then find it fresh.
        PERFORM pg_advisory_xact_lock(c_lock_class, v_table::OID::INTEGER);
        -- The statistics reads above are cached for the transaction; a peer may have analyzed while this waited.
        PERFORM pg_stat_clear_snapshot();
        IF __SCHEMA__._wh_queue_statistics_stale(v_table) THEN
          IF NOT pg_has_role((SELECT c.relowner FROM pg_class c WHERE c.oid = v_table), 'MEMBER') THEN
            -- ANALYZE would skip it with a warning of its own; this one says what to do about it.
            RAISE WARNING 'whizbang: the planner statistics of % are stale and % cannot analyze it; the tables must be owned by the role that migrates the schema (#1349)',
              v_table, current_user;
            CONTINUE;
          END IF;
          EXECUTE format('ANALYZE %s', v_table);
          v_refreshed := v_refreshed + 1;
          RAISE LOG 'whizbang: analyzed % because its statistics were stale (#1349)', v_table;
        END IF;
      END IF;
    END LOOP;
    PERFORM set_config('lock_timeout', v_caller_lock_timeout, TRUE);
  EXCEPTION WHEN lock_not_available THEN
    RAISE WARNING 'whizbang: could not refresh the planner statistics of % within 5 s (%); claiming on the statistics it has',
      v_table, SQLERRM;
  END;
  RETURN v_refreshed;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_refresh_stale_queue_statistics() IS
  'Analyzes the queue tables whose statistics are stale (207, #1349), as the role that migrated the schema (SECURITY DEFINER), one claimant per table at a time under a '
  'transaction-scoped advisory lock and a five second lock timeout. Returns how many tables it analyzed. claim_work '
  'calls it before acquiring, so a backlog that arrived after the last analyze is planned as the backlog it is.';

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


-- <docs>messaging/work-coordinator</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorDeepPathTests.cs:DeregisterInstanceAsync_RegisteredInstanceWithLease_ReleasesLeaseAndRemovesInstanceRowAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperWorkCoordinatorBroadTests.cs:DeregisterInstanceAsync_RemovesRowAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.deregister_instance(
  p_instance_id UUID,
  p_service_name TEXT DEFAULT NULL,
  p_host_name TEXT DEFAULT NULL
) RETURNS VOID AS $$
BEGIN
  -- Release all outbox leases held by this instance
  UPDATE __SCHEMA__.wh_outbox SET instance_id = NULL, lease_expiry = NULL
  WHERE instance_id = p_instance_id AND processed_at IS NULL;

  -- Release all inbox leases held by this instance. 162: claim state only, so the lease table.
  UPDATE __SCHEMA__.wh_inbox_state SET instance_id = NULL, lease_expiry = NULL
  WHERE instance_id = p_instance_id AND processed_at IS NULL;

  -- Release all perspective event leases held by this instance
  UPDATE __SCHEMA__.wh_perspective_events SET instance_id = NULL, lease_expiry = NULL
  WHERE instance_id = p_instance_id AND processed_at IS NULL;

  -- Release all receptor processing leases held by this instance
  UPDATE __SCHEMA__.wh_receptor_processing SET instance_id = NULL, lease_expiry = NULL
  WHERE instance_id = p_instance_id AND completed_at IS NULL;

  -- Release all active stream assignments held by this instance
  UPDATE __SCHEMA__.wh_active_streams SET assigned_instance_id = NULL, lease_expiry = NULL
  WHERE assigned_instance_id = p_instance_id;

  -- Log shutdown to wh_log for audit trail (guaranteed persistence)
  INSERT INTO __SCHEMA__.wh_log (log_level, source, message_id, error_message, metadata)
  VALUES (
    1,  -- Information
    'shutdown',
    p_instance_id,
    'Instance deregistered during graceful shutdown',
    jsonb_build_object(
      'service_name', COALESCE(p_service_name, 'unknown'),
      'host_name', COALESCE(p_host_name, 'unknown')
    )
  );

  -- Remove the instance registration
  DELETE FROM __SCHEMA__.wh_service_instances WHERE instance_id = p_instance_id;
END;
$$ LANGUAGE plpgsql;


-- <docs>operations/infrastructure/purging-streams</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Operations/StreamPurgeTests.cs:Purge_RemovesEveryRowOfTheStreams_AndLeavesOthersAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Operations/StreamPurgeTests.cs:DryRun_CountsWhatWouldGo_AndChangesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Purge_AnArchivedStream_TakesItsArchiveParkedCursorsAndTombstoneAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_purge_streams(
  p_stream_ids UUID[],
  p_dry_run BOOLEAN,
  p_purge_id UUID,
  p_batch_index INTEGER,
  p_requested_by TEXT,
  p_reason TEXT
) RETURNS TABLE(purged_table TEXT, purged_rows BIGINT) AS $$
DECLARE
  v_ids UUID[];
  v_event_ids UUID[];
  v_message_ids UUID[];
  v_schema TEXT;
  v_table TEXT;
  v_n BIGINT;
  v_counts JSONB := '{}'::jsonb;
  v_lanes UUID[];
  v_mins BIGINT[];
  v_maxs BIGINT[];
  v_i INTEGER;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
BEGIN
  -- The empty stream id is "no stream": purging it would reach every streamless row.
  IF __EMPTY_UUID__::uuid = ANY(p_stream_ids) THEN
    RAISE EXCEPTION 'wh_purge_streams: the empty stream id cannot be purged';
  END IF;

  SELECT COALESCE(array_agg(DISTINCT s), ARRAY[]::uuid[]) INTO v_ids FROM unnest(p_stream_ids) s;

  -- Ids the indirect tables are keyed by, read before anything is deleted.
  SELECT COALESCE(array_agg(es.event_id), ARRAY[]::uuid[]) INTO v_event_ids
  FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = ANY(v_ids);
  SELECT v_event_ids || COALESCE(array_agg(i.message_id), ARRAY[]::uuid[]) INTO v_message_ids
  FROM __SCHEMA__.wh_inbox i WHERE i.stream_id = ANY(v_ids);

  -- Work tables, in the canonical lock order.
  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_outbox o WHERE o.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_outbox o WHERE o.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_outbox'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- Counted before the inbox delete that removes them by cascade.
  SELECT count(*) INTO v_n
  FROM __SCHEMA__.wh_inbox_state st
  JOIN __SCHEMA__.wh_inbox i ON i.message_id = st.message_id
  WHERE i.stream_id = ANY(v_ids);
  purged_table := 'wh_inbox_state'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_inbox i WHERE i.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_inbox i WHERE i.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_inbox'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_perspective_events pe WHERE pe.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_perspective_events pe WHERE pe.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_perspective_events'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_active_streams a WHERE a.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_active_streams a WHERE a.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_active_streams'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- Deduplication entries of the stream's messages: its events and its inbox messages.
  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_message_deduplication d WHERE d.message_id = ANY(v_message_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_message_deduplication d WHERE d.message_id = ANY(v_message_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_message_deduplication'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_receptor_processing rp WHERE rp.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_receptor_processing rp WHERE rp.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_receptor_processing'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- Before the events: fk_perspective_cursors_event is ON DELETE RESTRICT.
  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_perspective_cursors c WHERE c.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_perspective_cursors c WHERE c.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_perspective_cursors'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_perspective_snapshots sn WHERE sn.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_perspective_snapshots sn WHERE sn.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_perspective_snapshots'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_perspective_applied pa WHERE pa.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_perspective_applied pa WHERE pa.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_perspective_applied'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- Every perspective's row for the streams. The registry names the tables; one it names that does not
  -- exist yet is skipped. Going through regclass resolves this function's own schema whichever way the
  -- runner substituted it (see 105).
  SELECT n.nspname INTO v_schema
  FROM pg_catalog.pg_class c
  JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
  WHERE c.oid = '__SCHEMA__.wh_event_store'::regclass;

  FOR v_table IN
    SELECT DISTINCT r.table_name FROM __SCHEMA__.wh_perspective_registry r ORDER BY r.table_name
  LOOP
    CONTINUE WHEN to_regclass(format('%I.%I', v_schema, v_table)) IS NULL;
    IF p_dry_run THEN
      EXECUTE format('SELECT count(*) FROM %I.%I WHERE id = ANY($1)', v_schema, v_table) INTO v_n USING v_ids;
    ELSE
      EXECUTE format('DELETE FROM %I.%I WHERE id = ANY($1)', v_schema, v_table) USING v_ids;
      GET DIAGNOSTICS v_n = ROW_COUNT;
    END IF;
    purged_table := v_table; purged_rows := v_n; RETURN NEXT;
    v_counts := v_counts || jsonb_build_object(purged_table, v_n);
  END LOOP;

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_perspective_row_hold h WHERE h.row_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_perspective_row_hold h WHERE h.row_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_perspective_row_hold'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_row_eviction_journal j WHERE j.row_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_row_eviction_journal j WHERE j.row_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_row_eviction_journal'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- The sealed epochs covering the fold-relevant events refold AFTER the delete, and their ranges are
  -- unknowable once the rows are gone (the same capture close_stream makes).
  IF NOT p_dry_run THEN
    SELECT array_agg(t.lane), array_agg(t.mn), array_agg(t.mx)
    INTO v_lanes, v_mins, v_maxs
    FROM (
      SELECT COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid) AS lane,
             MIN(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mn,
             MAX(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mx
      FROM __SCHEMA__.wh_event_store es
      WHERE es.stream_id = ANY(v_ids)
        AND __DIGEST_ELIGIBLE__(es)
      GROUP BY 1
    ) t
    WHERE t.mn IS NOT NULL;
  END IF;

  -- Every event of the streams goes, so every digest bucket and ledger row of the streams goes with them.
  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_stream_digests sd WHERE sd.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_stream_digests sd WHERE sd.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_stream_digests'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_integrity_ledger il WHERE il.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_integrity_ledger il WHERE il.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_integrity_ledger'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_event_body eb WHERE eb.event_id = ANY(v_event_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_event_body eb WHERE eb.event_id = ANY(v_event_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_event_body'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_event_destruction_hold dh WHERE dh.event_id = ANY(v_event_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_event_destruction_hold dh WHERE dh.event_id = ANY(v_event_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_event_destruction_hold'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_lifecycle_completions lc WHERE lc.event_id = ANY(v_event_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_lifecycle_completions lc WHERE lc.event_id = ANY(v_event_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_lifecycle_completions'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_event_archive ea WHERE ea.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_event_archive ea WHERE ea.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_event_archive'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- 262 (#1736): the cursors a whole-stream archive parked for its restore; a purged stream is never restored.
  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_event_archive_cursors ac WHERE ac.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_event_archive_cursors ac WHERE ac.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_event_archive_cursors'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_apply_fold_watermarks fw WHERE fw.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_apply_fold_watermarks fw WHERE fw.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_apply_fold_watermarks'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_event_store'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- 260 (#1732): the stream's lifecycle row goes with its events, after them, in the order the emit chain writes the
  -- two tables. A purged stream is gone, not archived: no row is left for an end-of-life sweep to act on.
  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_stream_lifecycle sl WHERE sl.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_stream_lifecycle sl WHERE sl.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_stream_lifecycle'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- Refold what the delete touched, then bump the generation ONCE so consumers reset their seals and
  -- re-verify instead of alarming on a deliberate change.
  IF v_lanes IS NOT NULL THEN
    FOR v_i IN 1 .. array_length(v_lanes, 1) LOOP
      PERFORM __SCHEMA__._wh_refold_epochs_covering(v_lanes[v_i], v_mins[v_i], v_maxs[v_i]);
    END LOOP;
    PERFORM __SCHEMA__._wh_bump_origin_generation();
  END IF;

  -- A purged stream stays purged: mark it for every perspective, so a later event is skipped, not applied.
  v_n := COALESCE(array_length(v_ids, 1), 0);
  IF NOT p_dry_run THEN
    INSERT INTO __SCHEMA__.wh_stream_purge_markers (stream_id, perspective_name, purged_at, purge_event_id, purge_id)
    SELECT s, '*', NOW(), NULL, p_purge_id FROM unnest(v_ids) s
    ON CONFLICT (stream_id, perspective_name) DO UPDATE
      SET purged_at = EXCLUDED.purged_at, purge_event_id = NULL, purge_id = EXCLUDED.purge_id;

    INSERT INTO __SCHEMA__.wh_stream_purge_audit (purge_id, batch_index, requested_by, reason, stream_ids, row_counts)
    VALUES (p_purge_id, p_batch_index, p_requested_by, p_reason, v_ids, v_counts || jsonb_build_object('wh_stream_purge_markers', v_n))
    ON CONFLICT (purge_id, batch_index) DO NOTHING;
  END IF;
  purged_table := 'wh_stream_purge_markers'; purged_rows := v_n; RETURN NEXT;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_purge_streams IS
'Operator stream purge (#1030): removes every row of the listed streams from this service''s store in the caller''s transaction, marks them purged for every perspective, and audits the batch. p_dry_run counts the same rows and changes nothing. Returns one row per table. 214: the refolded epoch range leaves out at-most-once events (flags & 32), which no epoch holds. 260: and the streams'' wh_stream_lifecycle rows (#1732). 262: and the cursors a whole-stream archive parked (#1736).';


-- <docs>fundamentals/perspectives/rewind#system-events</docs>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/RemoveRewindLifecycleEventsMigrationTests.cs:Migration210_RemovesRewindEventsFromApplicationStreams_AndLeavesTheGapsAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/RemoveRewindLifecycleEventsMigrationTests.cs:Migration210_KeepsAnEventSomethingStillReferencesAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/RemoveRewindLifecycleEventsMigrationTests.cs:Migration210_RunsOnce_AndAnAppendAfterItContinuesFromTheTopAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_remove_rewind_lifecycle_events_batch(p_limit INT)
RETURNS BIGINT AS $$
DECLARE
  v_from BIGINT;
  v_to   BIGINT;
BEGIN
  IF COALESCE((SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings
               WHERE setting_key = 'rewind_lifecycle_event_cleanup_version'), 0) >= 1 THEN
    RETURN 0;
  END IF;
  IF to_regclass('__SCHEMA__.wh_rewind_cleanup_todo') IS NULL THEN
    RETURN 0;
  END IF;

  SELECT last_seq INTO v_from FROM __SCHEMA__.wh_rewind_cleanup_cursor WHERE only_row;
  v_from := COALESCE(v_from, 0);
  SELECT max(seq) INTO v_to
  FROM (SELECT seq FROM __SCHEMA__.wh_rewind_cleanup_todo WHERE seq > v_from ORDER BY seq LIMIT p_limit) s;
  -- An exhausted worklist is the only way the pass ends, so a slice that removes nothing cannot stop it.
  IF v_to IS NULL THEN
    RETURN 0;
  END IF;

  CREATE TEMP TABLE IF NOT EXISTS _rewind_cleanup_slice (event_id UUID PRIMARY KEY) ON COMMIT DROP;
  DELETE FROM _rewind_cleanup_slice;
  INSERT INTO _rewind_cleanup_slice (event_id)
  SELECT t.event_id
  FROM __SCHEMA__.wh_rewind_cleanup_todo t
  WHERE t.seq > v_from AND t.seq <= v_to
    AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_perspective_cursors c
                    WHERE c.last_event_id = t.event_id OR c.rewind_trigger_event_id = t.event_id)
    AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_perspective_snapshots s WHERE s.snapshot_event_id = t.event_id)
    AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_archive a WHERE a.event_id = t.event_id)
    AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_destruction_hold h WHERE h.event_id = t.event_id)
    AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_stream_purge_markers m WHERE m.purge_event_id = t.event_id)
    AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_unique_emission_claims u WHERE u.claimed_by_event_id = t.event_id)
    AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_collective_ordering_heads o WHERE o.last_event_id = t.event_id)
  ON CONFLICT DO NOTHING;

  DELETE FROM __SCHEMA__.wh_perspective_events p USING _rewind_cleanup_slice r WHERE p.event_id = r.event_id;
  DELETE FROM __SCHEMA__.wh_perspective_applied p USING _rewind_cleanup_slice r WHERE p.event_id = r.event_id;
  DELETE FROM __SCHEMA__.wh_lifecycle_completions l USING _rewind_cleanup_slice r WHERE l.event_id = r.event_id;
  DELETE FROM __SCHEMA__.wh_receptor_processing p USING _rewind_cleanup_slice r WHERE p.event_id = r.event_id;
  DELETE FROM __SCHEMA__.wh_event_body b USING _rewind_cleanup_slice r WHERE b.event_id = r.event_id;
  DELETE FROM __SCHEMA__.wh_event_store e USING _rewind_cleanup_slice r WHERE e.event_id = r.event_id;

  UPDATE __SCHEMA__.wh_rewind_cleanup_cursor SET last_seq = v_to WHERE only_row;

  -- Non-zero while the worklist has entries, however many the guard kept, so the runner calls again.
  RETURN GREATEST((SELECT count(*) FROM _rewind_cleanup_slice), 1);
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_remove_rewind_lifecycle_events_batch(INT) IS
'#1360: removes up to p_limit worklisted rewind lifecycle events from application streams, with their bodies and side rows, keeping any event still referenced. Returns 0 once the worklist is exhausted or the cleanup is already recorded as done. The runner calls it until it returns 0.';


-- <docs>fundamentals/events/superseding-events#what-a-rebuild-keeps</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamStartedAtSqlTests.cs:Reap_TheStreamsFirstEvent_RecordsItsBusinessTimeOnTheLedgerAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamStartedAtSqlTests.cs:Reap_ALaterEvent_RecordsNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/RebuildCreatedAtIntegrationTests.cs:RebuildBlueGreen_AfterTheFirstEventIsReaped_DatesTheRowAtTheStreamsStartAsync</tests>
-- <docs>fundamentals/events/superseding-events#the-reaper</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_SupersededAndConsumed_IsDeletedWithItsBodyAndItsDigestContribution_TheNewestIsKeptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_UnprocessedPerspectiveWork_KeepsIt_UntilItIsProcessedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_ACursorOnIt_KeepsIt_UntilTheCursorMovesOnAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_ASnapshotOnIt_KeepsIt_UntilTheSnapshotIsGoneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_AnActiveDestructionHold_KeepsIt_ALapsedOneDoesNotAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_AnEventTheStamperHasNotReached_IsKeptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_ADifferentTypeOrKeyInTheSameStream_IsUntouchedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_TheNewestOfItsKey_IsKept_EvenWhenQueuedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_DebugMode_KeepsEverythingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_AQueuedEventAlreadyGone_IsForgottenAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_RemovingFoldedHistory_RefoldsAndBumpsTheGenerationOnce_AnAtMostOnceEventDoesNotAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_TakesAtMostTheBatchSize_OldestQueuedFirstAsync</tests>
-- <docs>fundamentals/events/superseding-events#superseding-groups</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingGroupSqlTests.cs:Reap_AnEventSupersededByAnotherTypeOfItsGroup_IsDeleted_TheNewestIsKeptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingGroupSqlTests.cs:Reap_AQueuedEventWhoseOnlyNewerEventIsOutsideItsGroup_IsKeptAsync</tests>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_reap_superseded_events(p_batch_size INTEGER DEFAULT 5000)
RETURNS TABLE(task_name TEXT, rows_affected BIGINT, duration_ms DOUBLE PRECISION, status TEXT) AS $$
DECLARE
  v_start TIMESTAMPTZ := clock_timestamp();
  v_debug BOOLEAN;
  v_candidates UUID[];
  v_streams UUID[];
  v_victims UUID[];
  v_lanes UUID[];
  v_mins BIGINT[];
  v_maxs BIGINT[];
  v_i INTEGER;
  v_deleted BIGINT := 0;
  c_task CONSTANT TEXT := 'reap_superseded_events';
  c_flag_superseding CONSTANT INTEGER := 128;  -- EventFlags.Superseding (1 << 7)
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  c_flags_state_based CONSTANT INTEGER := 24;  -- EventFlags.Ephemeral (1 << 3) | EventFlags.Compacted (1 << 4)
BEGIN
  -- debug_mode keeps every event for forensics, as every other reaper does.
  SELECT COALESCE(
    (SELECT s.setting_value::BOOLEAN FROM __SCHEMA__.wh_settings s WHERE s.setting_key = 'debug_mode'),
    FALSE
  ) INTO v_debug;
  IF v_debug THEN
    RETURN QUERY SELECT c_task, 0::BIGINT,
      EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION, 'skipped (debug_mode=true)'::TEXT;
    RETURN;
  END IF;

  -- A queued event something else already removed (a prefix truncate, an archive, a purge) is forgotten.
  DELETE FROM __SCHEMA__.wh_superseded_events q
  WHERE NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = q.event_id);

  -- The batch, oldest queued first, and its streams in lock order.
  SELECT array_agg(b.event_id), array_agg(DISTINCT b.stream_id)
  INTO v_candidates, v_streams
  FROM (
    SELECT q.event_id, q.stream_id
    FROM __SCHEMA__.wh_superseded_events q
    ORDER BY q.queued_at, q.event_id
    LIMIT p_batch_size
  ) b;
  IF v_candidates IS NULL THEN
    RETURN QUERY SELECT c_task, 0::BIGINT,
      EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION, 'ok'::TEXT;
    RETURN;
  END IF;

  -- The emit chains' per-stream locks, in stream id order (array_agg DISTINCT sorts), so nothing is appended to,
  -- truncated from or archived out of these streams while their events are judged and removed.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || u.sid::text))
  FROM unnest(v_streams) AS u(sid)
  ORDER BY u.sid;

  -- The gates, read under the locks.
  SELECT array_agg(es.event_id) INTO v_victims
  FROM __SCHEMA__.wh_event_store es
  WHERE es.event_id = ANY(v_candidates)
    AND es.supersede_key IS NOT NULL
    AND COALESCE(es.flags, 0) & c_flag_superseding <> 0
    AND COALESCE(es.flags, 0) & c_flags_state_based = 0
    -- Never the newest of its key: a newer event of the same stream, group and key is stored. 352 (#2124): the group is
    -- the stored supersede_group, or the event type for an ungrouped row, so an ungrouped type is its own group.
    AND EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_store n
      WHERE n.stream_id = es.stream_id
        AND COALESCE(n.supersede_group, n.event_type) = COALESCE(es.supersede_group, es.event_type)
        AND n.supersede_key = es.supersede_key
        -- 454 (#2350): newer in the origin's order where both copies carry it, so a restored older copy stored above
        -- the newest is the one reaped, and the newest never is
        AND __SCHEMA__._wh_supersedes(n.origin_service_id, n.origin_commit_sequence, n.version,
                                  es.origin_service_id, es.origin_commit_sequence, es.version))
    -- The commit-sequence stamper has reached it, so nothing that walks the stamped order still owes it a place.
    AND es.commit_sequence IS NOT NULL
    -- Every perspective that consumes it has processed it.
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
      WHERE pe.event_id = es.event_id AND pe.processed_at IS NULL)
    -- No cursor rests on it (fk_perspective_cursors_event is RESTRICT on the EF Core schema).
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_cursors c
      WHERE c.stream_id = es.stream_id AND c.last_event_id = es.event_id)
    -- No snapshot rests on it: a rewind restores a snapshot and moves the cursor onto its event.
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_snapshots s
      WHERE s.stream_id = es.stream_id AND s.snapshot_event_id = es.event_id)
    -- A PreDestruction hook's Cancel or Defer is honored, as by every other path that removes an event.
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_destruction_hold h
      WHERE h.event_id = es.event_id AND h.hold_until > NOW());

  IF v_victims IS NOT NULL THEN
    -- The sequence range of the fold-relevant victims, per lane, for the epoch refold after the delete (as 263).
    SELECT array_agg(t.lane), array_agg(t.mn), array_agg(t.mx)
    INTO v_lanes, v_mins, v_maxs
    FROM (
      SELECT COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid) AS lane,
             MIN(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mn,
             MAX(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mx
      FROM __SCHEMA__.wh_event_store es
      WHERE es.event_id = ANY(v_victims)
        AND __DIGEST_ELIGIBLE__(es)
      GROUP BY 1
    ) t
    WHERE t.mn IS NOT NULL;

    -- Subtract the victims from wh_stream_digests before they go, as the prefix truncate does (263): XOR is
    -- self-inverse, so folding the same hashes again removes them. Only rows a fold ever added are subtracted.
    PERFORM __SCHEMA__._wh_fold_stream_digests(
      array_agg(COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid)), array_agg(COALESCE(es.scope ->> 't', '')),
      array_agg(es.event_type), array_agg(es.stream_id), array_agg(es.event_id), 'unfold', NOW())
    FROM __SCHEMA__.wh_event_store es
    WHERE es.event_id = ANY(v_victims)
      AND __DIGEST_ELIGIBLE__(es);

    DELETE FROM __SCHEMA__.wh_stream_digests d
    WHERE d.stream_id = ANY(v_streams) AND d.event_count <= 0;

    -- 358 (#2152): a victim that is its stream's first event (version 0 or 1) leaves its business time on the stream's
    -- ledger row, so a rebuild that writes the stream's row afresh still dates it at the stream's start. Written once:
    -- the lowest such victim of each stream, and never over a value already recorded.
    UPDATE __SCHEMA__.wh_stream_lifecycle l
    SET first_event_at = f.business_time
    FROM (
      SELECT DISTINCT ON (es.stream_id) es.stream_id,
             COALESCE(__SCHEMA__._wh_event_business_time(eb.metadata), es.created_at) AS business_time
      FROM __SCHEMA__.wh_event_store es
      LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = es.event_id
      WHERE es.event_id = ANY(v_victims) AND es.version <= 1
      ORDER BY es.stream_id, es.version
    ) f
    WHERE l.stream_id = f.stream_id AND l.first_event_at IS NULL;

    -- Lapsed holds of the victims go with them; the gates proved no active one remains. Their processed perspective
    -- work rows are left to Task 3's purge: this task runs after the work tables were visited in their canonical order,
    -- and touching one again here would break that order.
    DELETE FROM __SCHEMA__.wh_event_destruction_hold h WHERE h.event_id = ANY(v_victims);

    -- Bodies first, then pointers (wh_receptor_processing rows cascade on the EF Core schema).
    DELETE FROM __SCHEMA__.wh_event_body eb WHERE eb.event_id = ANY(v_victims);
    DELETE FROM __SCHEMA__.wh_event_store es WHERE es.event_id = ANY(v_victims);
    GET DIAGNOSTICS v_deleted = ROW_COUNT;

    DELETE FROM __SCHEMA__.wh_superseded_events q WHERE q.event_id = ANY(v_victims);

    -- Refold the sealed epochs that covered the victims, then bump the generation once, so consumers reset their
    -- seals and re-verify instead of alarming on a deliberate change (093).
    IF v_lanes IS NOT NULL THEN
      FOR v_i IN 1 .. array_length(v_lanes, 1) LOOP
        PERFORM __SCHEMA__._wh_refold_epochs_covering(v_lanes[v_i], v_mins[v_i], v_maxs[v_i]);
      END LOOP;
      PERFORM __SCHEMA__._wh_bump_origin_generation();
    END IF;
  END IF;

  RETURN QUERY SELECT c_task, v_deleted,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION, 'ok'::TEXT;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_reap_superseded_events(INTEGER) IS
  'Task 16 of perform_maintenance (295, #1729): deletes up to p_batch_size queued superseded events, with their bodies '
  'and their digest contribution, once a newer event of the same stream, group and key is stored and every consumer has '
  'processed them (no unprocessed perspective work, no cursor or snapshot on them, no active destruction hold, commit '
  'sequence stamped). Skipped under debug_mode. Reduces the digests as wh_truncate_stream_prefix does, refolding the '
  'covering sealed epochs and bumping the origin generation once when it removed folded history. 352: a group is the stored supersede_group, or the event type for an ungrouped row (#2124). 358: records the business time of a victim that is its stream''s first event on the stream''s ledger row (first_event_at, #2152).';

SELECT __SCHEMA__.drop_all_overloads('claim_orphaned_receptor_work');

-- <docs>operations/infrastructure/migrations#unused-tables-are-dropped</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/UnusedTablesAreDroppedTests.cs:TheDropReverses_AndAppliesAgainAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.claim_orphaned_receptor_work(
  p_instance_id UUID,
  p_instance_rank INTEGER,
  p_active_instance_count INTEGER,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ
) RETURNS TABLE(
  processing_id UUID,
  stream_id UUID
) AS $$
BEGIN
  RETURN QUERY
  UPDATE __SCHEMA__.wh_receptor_processing rp
  SET instance_id = p_instance_id,
      lease_expiry = p_lease_expiry,
      claimed_at = p_now
  WHERE (rp.instance_id IS NULL OR rp.lease_expiry < p_now)
    AND rp.completed_at IS NULL
    -- Partition-based load balancing using stream_id
    AND (
      rp.partition_number IS NULL
      OR (rp.partition_number % p_active_instance_count) = p_instance_rank
    )
    -- Respect stream ownership
    AND EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_active_streams ast
      WHERE ast.stream_id = rp.stream_id
        AND ast.assigned_instance_id = p_instance_id
        AND ast.lease_expiry > p_now
    )
  RETURNING rp.id AS processing_id, rp.stream_id AS stream_id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.claim_orphaned_receptor_work IS
'Claims orphaned receptor processing work with expired or null leases. Uses partition-based load balancing. Respects stream ownership. Returns claimed processing IDs for Orphaned flag in orchestrator response.';
