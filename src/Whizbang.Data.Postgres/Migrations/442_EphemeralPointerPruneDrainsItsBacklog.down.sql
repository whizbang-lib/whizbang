-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 442_EphemeralPointerPruneDrainsItsBacklog.sql (#2327): the prune goes back to its 113 definition (one
-- unbounded run per interval) and wh_pending_reclamation to its 441 definition. Reversible: neither holds state.

-- <docs>fundamentals/events/ephemeral-events#pruning-ancient-pointers</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralPointerPruneSqlTests.cs:Prune_KeepsNewestPointerPerStream_PrunesOlderReapedEphemeralPointersAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.prune_ancient_ephemeral_pointers()
RETURNS TABLE(rows_pruned BIGINT, status TEXT) AS $$
DECLARE
  v_enabled BOOLEAN;
  v_debug BOOLEAN;
  v_retention_days INTEGER;
  v_dedup_days INTEGER;
  v_interval_days INTEGER;
  v_horizon_days INTEGER;
  v_claimed BIGINT;
  v_rows BIGINT;
  v_fold_targets UUID[];
BEGIN
  -- Opt-in gate. Pruning append-only pointers is a deliberate storage-economy choice; off by default.
  SELECT COALESCE(
    (SELECT setting_value::BOOLEAN FROM __SCHEMA__.wh_settings WHERE setting_key = 'ephemeral_deep_maintenance_enabled'),
    FALSE) INTO v_enabled;
  IF NOT v_enabled THEN
    RETURN QUERY SELECT 0::BIGINT, 'disabled'::TEXT;
    RETURN;
  END IF;

  -- debug_mode retains forensic rows — the reaper (073) and completed-message purges honor it, so must this.
  SELECT COALESCE(
    (SELECT setting_value::BOOLEAN FROM __SCHEMA__.wh_settings WHERE setting_key = 'debug_mode'),
    FALSE) INTO v_debug;
  IF v_debug THEN
    RETURN QUERY SELECT 0::BIGINT, 'skipped (debug_mode=true)'::TEXT;
    RETURN;
  END IF;

  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'ephemeral_pointer_prune_interval_days'),
    30) INTO v_interval_days;

  -- Self-gate (multi-pod safe): atomically claim this tick by advancing the watermark ONLY if the interval
  -- has elapsed. If no row is updated, another pod already claimed it this interval (or it is not yet due) —
  -- return without pruning. The conditional UPDATE is the atomic CAS; no advisory lock needed.
  UPDATE __SCHEMA__.wh_settings
    SET setting_value = NOW()::TEXT
    WHERE setting_key = 'ephemeral_pointer_prune_last_run'
      AND setting_value::TIMESTAMPTZ < NOW() - (v_interval_days * INTERVAL '1 day');
  GET DIAGNOSTICS v_claimed = ROW_COUNT;
  IF v_claimed = 0 THEN
    RETURN QUERY SELECT 0::BIGINT, 'not due'::TEXT;
    RETURN;
  END IF;

  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'ephemeral_pointer_retention_days'),
    90) INTO v_retention_days;
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'dedup_retention_days'),
    30) INTO v_dedup_days;
  -- Safety floor: the horizon can NEVER be shorter than the dedup window — a still-deduped redelivery must
  -- never find its pointer already gone. Operators widen ephemeral_pointer_retention_days for longer
  -- cross-service replay windows; they can never accidentally shorten it below dedup.
  v_horizon_days := GREATEST(v_retention_days, v_dedup_days);

  -- Fold-before-discard: the streams about to lose pointers fold their CURRENT full path first.
  -- Watermark-aware, so a stream folded earlier (a close, a settled fold) costs one anti-join probe.
  SELECT COALESCE(array_agg(DISTINCT es.stream_id), '{}')
    INTO v_fold_targets
    FROM __SCHEMA__.wh_event_store es
   WHERE (es.flags & 8) = 8
     AND es.created_at < NOW() - (v_horizon_days * INTERVAL '1 day')
     AND NOT EXISTS (
       SELECT 1 FROM __SCHEMA__.wh_event_body eb WHERE eb.event_id = es.event_id)
     AND NOT EXISTS (
       SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
       WHERE pe.event_id = es.event_id AND pe.processed_at IS NULL)
     AND es.version < (
       SELECT MAX(es2.version) FROM __SCHEMA__.wh_event_store es2
       WHERE es2.stream_id = es.stream_id);
  IF array_length(v_fold_targets, 1) IS NOT NULL THEN
    PERFORM __SCHEMA__.fold_stream_apply_paths(v_fold_targets);
  END IF;

  DELETE FROM __SCHEMA__.wh_event_store es
  WHERE (es.flags & 8) = 8                                            -- ephemeral pointers only
    AND es.created_at < NOW() - (v_horizon_days * INTERVAL '1 day')   -- past the horizon
    AND NOT EXISTS (                                                  -- body already reaped by tier-1 (073)
      SELECT 1 FROM __SCHEMA__.wh_event_body eb WHERE eb.event_id = es.event_id)
    AND NOT EXISTS (                                                  -- no pending perspective work item
      SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
      WHERE pe.event_id = es.event_id AND pe.processed_at IS NULL)
    AND es.version < (                                                -- KEEP the newest pointer per stream:
      SELECT MAX(es2.version) FROM __SCHEMA__.wh_event_store es2       -- the surviving "tombstone" keeps the
      WHERE es2.stream_id = es.stream_id);                            -- stream flagged ephemeral (guard) and
                                                                      -- is the cursor's last_event_id target.
  GET DIAGNOSTICS v_rows = ROW_COUNT;

  RETURN QUERY SELECT v_rows, 'ok'::TEXT;
END;
$$ LANGUAGE plpgsql;

-- Counted (each predicate copied from the task, each served by the index named):
--   purge_completed_perspective_events  processed_at IS NOT NULL (idx_perspective_events_processed, 031)
--   purge_old_deduplication             first_seen_at past dedup_retention_days (idx_message_dedup_first_seen)
--   purge_instance_evictions            evicted_at past instance_eviction_retention_hours (idx_instance_evictions_evicted_at)
--   prune_collective_ordering_heads     updated_at past collective_ordering_head_retention_days
--                                       (idx_collective_ordering_heads_updated_at)
--   purge_idle_routes                   last_seen past route_idle_days (idx_routes_last_seen)
--   convert_metadata_history            441 (#2322): the event rows the historical metadata conversion has yet to read,
--                                       past its cursor (the wh_event_body key); 0 once the conversion is complete,
--                                       nothing while it is skipped (debug mode, switched off, or the Full form)
-- Not counted, because no index serves the predicate: purge_completed_outbox and purge_completed_inbox (every
-- processed_at index is partial on IS NULL), purge_recovered_dead_letters, reap_consumed_ephemeral_bodies (the
-- per-type grace and the TTL in body metadata leave the ephemeral index unbounded: pointers whose bodies were already
-- reaped stay in it), reap_expired_perspective_rows and the perspective row caps (expires_at is not indexed), and the
-- ledger trims of reap_unbounded_ledgers other than the routes.
-- Under debug_mode the tasks that skip under it report nothing, since they would remove nothing.
-- <docs>operations/observability/metrics#pending-reclamation</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PendingReclamationSqlTests.cs:EachTask_CountsWhatItWouldRemove_AndNothingElseAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PendingReclamationSqlTests.cs:ACountPastTheCap_StopsAtTheCap_AndSaysSoAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PendingReclamationSqlTests.cs:UnderDebugMode_TheTasksThatSkip_ReportNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Remaining_CountsTheRowsPastTheCursor_AndReachesZeroAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_pending_reclamation(p_cap INTEGER)
RETURNS TABLE(task_name TEXT, pending BIGINT, capped BOOLEAN)
LANGUAGE plpgsql
STABLE
AS $$
DECLARE
  v_limit BIGINT := GREATEST(p_cap, 1)::BIGINT + 1;
  v_debug_mode BOOLEAN;
  v_dedup_retention_days INTEGER;
  v_instance_eviction_retention_hours INTEGER;
  v_ordering_head_retention_days INTEGER;
  v_route_days INTEGER;
  v_rows BIGINT;
BEGIN
  -- Every setting read as the maintenance cycle reads it, with the same default, so a count is what the next cycle
  -- would remove.
  SELECT COALESCE(
    (SELECT setting_value::BOOLEAN FROM __SCHEMA__.wh_settings WHERE setting_key = 'debug_mode'), FALSE)
    INTO v_debug_mode;
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'dedup_retention_days'), 30)
    INTO v_dedup_retention_days;
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'instance_eviction_retention_hours'), 24)
    INTO v_instance_eviction_retention_hours;
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'collective_ordering_head_retention_days'), 7)
    INTO v_ordering_head_retention_days;
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'route_idle_days'), 365)
    INTO v_route_days;

  IF NOT v_debug_mode THEN
    SELECT count(*) INTO v_rows FROM (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events WHERE processed_at IS NOT NULL LIMIT v_limit) x;
    RETURN QUERY SELECT 'purge_completed_perspective_events'::TEXT, LEAST(v_rows, v_limit - 1), v_rows = v_limit;
  END IF;

  SELECT count(*) INTO v_rows FROM (
    SELECT 1 FROM __SCHEMA__.wh_message_deduplication
    WHERE first_seen_at < NOW() - (v_dedup_retention_days || ' days')::INTERVAL LIMIT v_limit) x;
  RETURN QUERY SELECT 'purge_old_deduplication'::TEXT, LEAST(v_rows, v_limit - 1), v_rows = v_limit;

  SELECT count(*) INTO v_rows FROM (
    SELECT 1 FROM __SCHEMA__.wh_instance_evictions
    WHERE evicted_at < NOW() - (v_instance_eviction_retention_hours * INTERVAL '1 hour') LIMIT v_limit) x;
  RETURN QUERY SELECT 'purge_instance_evictions'::TEXT, LEAST(v_rows, v_limit - 1), v_rows = v_limit;

  SELECT count(*) INTO v_rows FROM (
    SELECT 1 FROM __SCHEMA__.wh_collective_ordering_heads
    WHERE updated_at < NOW() - (v_ordering_head_retention_days * INTERVAL '1 day') LIMIT v_limit) x;
  RETURN QUERY SELECT 'prune_collective_ordering_heads'::TEXT, LEAST(v_rows, v_limit - 1), v_rows = v_limit;

  IF NOT v_debug_mode THEN
    SELECT count(*) INTO v_rows FROM (
      SELECT 1 FROM __SCHEMA__.wh_routes
      WHERE last_seen < NOW() - (v_route_days * INTERVAL '1 day') LIMIT v_limit) x;
    RETURN QUERY SELECT 'purge_idle_routes'::TEXT, LEAST(v_rows, v_limit - 1), v_rows = v_limit;
  END IF;

  -- 441 (#2322): what the historical metadata conversion has left to read.
  RETURN QUERY SELECT 'convert_metadata_history'::TEXT, r.remaining, r.capped
    FROM __SCHEMA__.wh_metadata_history_remaining(p_cap) r;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_pending_reclamation(INTEGER) IS
  'The rows each index-served maintenance task would remove now, one bounded count each (at most p_cap + 1 rows '
  'read); capped means at least p_cap (#2289). Read by the pending-reclamation sampler, never by the maintenance cycle.';
