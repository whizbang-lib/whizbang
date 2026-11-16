-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 440_LedgerSweepsAreBoundedAndReturnDrainedDefaults.sql (#2321): reap_unbounded_ledgers goes back to its
-- 286 definition (each task sweeps until nothing is left, and drained default partitions keep their space), then the
-- two helpers are dropped. Reversible: neither holds state.

-- <docs>fundamentals/workers/maintenance-steps#ledger-retention</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LedgerRetentionSqlTests.cs:Log_OlderThanTheWindow_IsRemoved_YoungerIsKeptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LedgerRetentionSqlTests.cs:Log_ConfiguredWindow_IsHonoredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LedgerRetentionSqlTests.cs:ScheduleRuns_OlderThanTheWindow_AreRemoved_ButTheNewestRunOfEachScheduleIsKeptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LedgerRetentionSqlTests.cs:StreamPurgeAudit_IsKeptForeverByDefault_AndBoundedWhenConfiguredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LedgerRetentionSqlTests.cs:IntegrityLedger_IsTrimmedToTheConfiguredCount_NewestKeptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LedgerRetentionSqlTests.cs:DebugMode_KeepsEveryLedgerRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/LogPartitionConversionTests.cs:OnceConverted_RetentionDropsPartitions_InsteadOfDeletingRowsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/LogPartitionConversionTests.cs:AConvertedLog_WithPartitioningSwitchedOff_IsStillBoundedByTheRowDeleteAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/LogPartitionConversionTests.cs:OnceManaged_TheRowDeleteSweepsOnlyTheDefaultPartition_AndLeavesPartitionsToTheDropAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/LogPartitionConversionTests.cs:OnceManaged_WithoutADefaultPartition_TheRowDeleteSkipsAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperPartitionManagerSqlTests.cs:TheLog_OnceManaged_IsSweptInItsDefaultPartitionOnlyAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveApplyProgressSqlTests.cs:TheSweep_RemovesAFinishedRecordPastTheHorizon_AndKeepsAYoungerOneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveApplyProgressSqlTests.cs:TheSweep_HonorsTheConfiguredHorizonAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveApplyProgressSqlTests.cs:TheSweep_KeepsAFinishedRecord_WhileItsWorkRowIsPendingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveApplyProgressSqlTests.cs:TheSweep_KeepsUnfinishedAndAbandonedRecords_AndTheFinishedRecordsOfADeadLetteredCollectiveAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveApplyProgressSqlTests.cs:TheSweep_UnderDebugMode_KeepsEveryRecordAsync</tests>
-- <docs>data/event-store#retention</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataDistillationSqlTests.cs:Retention_EachWindow_RemovesOnlyWhatIsPastItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataDistillationSqlTests.cs:Retention_DebugMode_RemovesNothingAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.reap_unbounded_ledgers(p_batch_size INTEGER DEFAULT 5000)
RETURNS TABLE(
  task_name TEXT,
  rows_affected BIGINT,
  duration_ms DOUBLE PRECISION,
  status TEXT
) AS $$
DECLARE
  v_debug_mode BOOLEAN;
  v_start TIMESTAMPTZ;
  v_rows BIGINT;
  v_batch BIGINT;
  v_log_days INTEGER;
  v_run_days INTEGER;
  v_audit_days INTEGER;
  v_ledger_max INTEGER;
  v_log_relation TEXT;
  v_log_default BOOLEAN;
  v_progress_days INTEGER;
  v_staging_days INTEGER;   -- 286
  v_trace_days INTEGER;     -- 286
  v_route_days INTEGER;     -- 286
  v_relation TEXT;          -- 286: the relation a partitioned ledger's row sweep works on
BEGIN
  SELECT COALESCE(
    (SELECT setting_value::BOOLEAN FROM __SCHEMA__.wh_settings WHERE setting_key = 'debug_mode'), FALSE)
    INTO v_debug_mode;
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'log_retention_days'), 14)
    INTO v_log_days;
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'schedule_run_retention_days'), 30)
    INTO v_run_days;
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'stream_purge_audit_retention_days'), 0)
    INTO v_audit_days;
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'integrity_ledger_max_entries'), 100000)
    INTO v_ledger_max;
  -- 286 (#1726): the staged metadata, trace and route windows.
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'metadata_staging_days'), 2)
    INTO v_staging_days;
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'trace_retention_days'), 30)
    INTO v_trace_days;
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'route_idle_days'), 365)
    INTO v_route_days;
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'collective_apply_progress_retention_days'), 7)
    INTO v_progress_days;
  -- 238: the relation the wh_log row delete works on (233): wh_log itself while it is plain or partition maintenance
  -- is off; once it is partitioned and maintained, only its default partition, which holds the rows no partition
  -- covers and which no partition drop ever reaches; NULL when a maintained wh_log has no default partition.
  v_log_relation := __SCHEMA__.wh_partition_sweep_relation('wh_log');
  SELECT COALESCE((SELECT c.relispartition FROM pg_class c WHERE c.oid = to_regclass(v_log_relation)), FALSE)
    INTO v_log_default;

  -- wh_log, by logged_at (indexed, 028; a partition inherits the parent's index).
  v_start := clock_timestamp();
  v_rows := 0;
  IF NOT v_debug_mode AND v_log_relation IS NOT NULL THEN
    LOOP
      EXECUTE format(
        'DELETE FROM %s WHERE log_id IN (SELECT l.log_id FROM %s l WHERE l.logged_at < $1 LIMIT $2)',
        v_log_relation, v_log_relation)
        USING NOW() - (v_log_days * INTERVAL '1 day'), p_batch_size;
      GET DIAGNOSTICS v_batch = ROW_COUNT;
      v_rows := v_rows + v_batch;
      EXIT WHEN v_batch < p_batch_size;
    END LOOP;
  END IF;
  RETURN QUERY SELECT
    'purge_old_log_entries'::TEXT, v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE
      WHEN v_debug_mode THEN 'skipped (debug_mode=true)'
      WHEN v_log_relation IS NULL THEN 'skipped (partitioned: no default partition)'
      WHEN v_log_default THEN 'ok (partitioned: the default partition only; expired partitions are dropped)'
      ELSE 'ok'
    END::TEXT;

  -- wh_schedule_runs, by fired_at, never a schedule's newest run (the (schedule_id, fired_at DESC) index, 066,
  -- answers "is there a newer run" for each candidate).
  v_start := clock_timestamp();
  v_rows := 0;
  IF NOT v_debug_mode THEN
    LOOP
      DELETE FROM __SCHEMA__.wh_schedule_runs
      WHERE run_id IN (
        SELECT r.run_id FROM __SCHEMA__.wh_schedule_runs r
        WHERE r.fired_at < NOW() - (v_run_days * INTERVAL '1 day')
          AND EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_schedule_runs newer
            WHERE newer.schedule_id = r.schedule_id AND newer.fired_at > r.fired_at)
        LIMIT p_batch_size);
      GET DIAGNOSTICS v_batch = ROW_COUNT;
      v_rows := v_rows + v_batch;
      EXIT WHEN v_batch < p_batch_size;
    END LOOP;
  END IF;
  RETURN QUERY SELECT
    'purge_old_schedule_runs'::TEXT, v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_debug_mode THEN 'skipped (debug_mode=true)' ELSE 'ok' END::TEXT;

  -- wh_stream_purge_audit, by purged_at, only when an operator set a window.
  v_start := clock_timestamp();
  v_rows := 0;
  IF NOT v_debug_mode AND v_audit_days > 0 THEN
    DELETE FROM __SCHEMA__.wh_stream_purge_audit a
    WHERE a.purged_at < NOW() - (v_audit_days * INTERVAL '1 day');
    GET DIAGNOSTICS v_rows = ROW_COUNT;
  END IF;
  RETURN QUERY SELECT
    'purge_old_stream_purge_audit'::TEXT, v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE
      WHEN v_debug_mode THEN 'skipped (debug_mode=true)'
      WHEN v_audit_days = 0 THEN 'kept (stream_purge_audit_retention_days=0)'
      ELSE 'ok'
    END::TEXT;

  -- wh_integrity_ledger, to its row cap, by the trim 090 shipped.
  v_start := clock_timestamp();
  v_rows := 0;
  IF NOT v_debug_mode THEN
    v_rows := __SCHEMA__.wh_integrity_ledger_trim(v_ledger_max);
  END IF;
  RETURN QUERY SELECT
    'trim_integrity_ledger'::TEXT, v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_debug_mode THEN 'skipped (debug_mode=true)' ELSE 'ok' END::TEXT;

  -- 279: wh_collective_apply_progress, a finished record once it is past its horizon (by applied_at) and the work row
  -- of its collective is no longer pending, so a re-delivery of a pending collective still finds its finished apply.
  -- Never an abandoned record, nor a finished record of a collective that has one: those go with the dead letter.
  v_start := clock_timestamp();
  v_rows := 0;
  IF NOT v_debug_mode THEN
    LOOP
      DELETE FROM __SCHEMA__.wh_collective_apply_progress d
      WHERE (d.event_id, d.entry_key) IN (
        SELECT p.event_id, p.entry_key FROM __SCHEMA__.wh_collective_apply_progress p
        WHERE p.completed
          AND NOT p.abandoned
          AND p.applied_at < NOW() - (v_progress_days * INTERVAL '1 day')
          AND NOT EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_collective_apply_progress a
            WHERE a.event_id = p.event_id AND a.abandoned)
          AND NOT EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
            WHERE pe.event_id = p.event_id AND pe.processed_at IS NULL AND pe.perspective_name = '__collective__')
        LIMIT p_batch_size);
      GET DIAGNOSTICS v_batch = ROW_COUNT;
      v_rows := v_rows + v_batch;
      EXIT WHEN v_batch < p_batch_size;
    END LOOP;
  END IF;
  RETURN QUERY SELECT
    'purge_collective_apply_progress'::TEXT, v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_debug_mode THEN 'skipped (debug_mode=true)' ELSE 'ok' END::TEXT;

  -- 286 (#1726): staged metadata maintenance has not distilled, by recorded_at, on the relation the partition manager
  -- leaves to the row sweep (the table, or its default partition once partitioned and maintained).
  v_start := clock_timestamp();
  v_rows := 0;
  v_relation := __SCHEMA__.wh_partition_sweep_relation('wh_event_metadata_staging');
  IF NOT v_debug_mode AND v_relation IS NOT NULL THEN
    LOOP
      EXECUTE format(
        'DELETE FROM %s s WHERE (s.event_id, s.recorded_at) IN (SELECT x.event_id, x.recorded_at FROM %s x WHERE x.recorded_at < $1 LIMIT $2)',
        v_relation, v_relation)
        USING NOW() - (v_staging_days * INTERVAL '1 day'), p_batch_size;
      GET DIAGNOSTICS v_batch = ROW_COUNT;
      v_rows := v_rows + v_batch;
      EXIT WHEN v_batch < p_batch_size;
    END LOOP;
  END IF;
  RETURN QUERY SELECT
    'purge_old_staged_metadata'::TEXT, v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE
      WHEN v_debug_mode THEN 'skipped (debug_mode=true)'
      WHEN v_relation IS NULL THEN 'skipped (partitioned: no default partition)'
      ELSE 'ok'
    END::TEXT;

  -- 286 (#1726): traces past their window, the same way.
  v_start := clock_timestamp();
  v_rows := 0;
  v_relation := __SCHEMA__.wh_partition_sweep_relation('wh_traces');
  IF NOT v_debug_mode AND v_relation IS NOT NULL THEN
    LOOP
      EXECUTE format(
        'DELETE FROM %s t WHERE (t.event_id, t.recorded_at) IN (SELECT x.event_id, x.recorded_at FROM %s x WHERE x.recorded_at < $1 LIMIT $2)',
        v_relation, v_relation)
        USING NOW() - (v_trace_days * INTERVAL '1 day'), p_batch_size;
      GET DIAGNOSTICS v_batch = ROW_COUNT;
      v_rows := v_rows + v_batch;
      EXIT WHEN v_batch < p_batch_size;
    END LOOP;
  END IF;
  RETURN QUERY SELECT
    'purge_old_traces'::TEXT, v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE
      WHEN v_debug_mode THEN 'skipped (debug_mode=true)'
      WHEN v_relation IS NULL THEN 'skipped (partitioned: no default partition)'
      ELSE 'ok'
    END::TEXT;

  -- 286 (#1726): routes not taken for route_idle_days (idx_routes_last_seen). A route still taken is never removed.
  v_start := clock_timestamp();
  v_rows := 0;
  IF NOT v_debug_mode THEN
    LOOP
      DELETE FROM __SCHEMA__.wh_routes
      WHERE route_id IN (
        SELECT r.route_id FROM __SCHEMA__.wh_routes r
        WHERE r.last_seen < NOW() - (v_route_days * INTERVAL '1 day')
        LIMIT p_batch_size);
      GET DIAGNOSTICS v_batch = ROW_COUNT;
      v_rows := v_rows + v_batch;
      EXIT WHEN v_batch < p_batch_size;
    END LOOP;
  END IF;
  RETURN QUERY SELECT
    'purge_idle_routes'::TEXT, v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_debug_mode THEN 'skipped (debug_mode=true)' ELSE 'ok' END::TEXT;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.reap_unbounded_ledgers(INTEGER) IS
  'Issue #1572: bounds wh_log, wh_schedule_runs (keeping each schedule''s newest run), wh_stream_purge_audit '
  '(opt-in) and wh_integrity_ledger, in batches, from their wh_settings windows. Called by perform_maintenance. '
  'Issue #1611: skips the wh_log row delete while wh_log is partitioned and partition maintenance is enabled. '
  'Issue #1694: a partitioned and maintained wh_log is swept in its default partition only (wh_partition_sweep_relation). '
  'Issue #1969: finished collective apply records past collective_apply_progress_retention_days (default 7), once the '
  'collective''s work row is no longer pending. '
  'Issue #1726: and staged metadata past metadata_staging_days, traces past trace_retention_days (both the same '
  'way), and routes not taken for route_idle_days.';

SELECT __SCHEMA__.drop_all_overloads('_wh_sweep_status_note');
SELECT __SCHEMA__.drop_all_overloads('_wh_truncate_drained_default_partition');
