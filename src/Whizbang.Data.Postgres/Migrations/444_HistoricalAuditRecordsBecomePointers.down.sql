-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 444_HistoricalAuditRecordsBecomePointers.sql (#2326): wh_pending_reclamation goes back to its 442
-- definition and the batch function goes. Lossy: bodies already removed are not restored (the audited events hold them),
-- and the walk's rows (operation audit-body) are removed.

-- Counted (each predicate copied from the task, each served by the index named):
--   purge_completed_perspective_events  processed_at IS NOT NULL (idx_perspective_events_processed, 031)
--   purge_old_deduplication             first_seen_at past dedup_retention_days (idx_message_dedup_first_seen)
--   purge_instance_evictions            evicted_at past instance_eviction_retention_hours (idx_instance_evictions_evicted_at)
--   prune_collective_ordering_heads     updated_at past collective_ordering_head_retention_days
--                                       (idx_collective_ordering_heads_updated_at)
--   purge_idle_routes                   last_seen past route_idle_days (idx_routes_last_seen)
--   prune_ancient_ephemeral_pointers    442 (#2327): the pointers the opt-in prune would remove once due, under its own
--                                       rule (idx_event_store_ephemeral_reap, created_at past the horizon); nothing
--                                       while the prune is disabled or under debug_mode
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
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralPointerPruneSqlTests.cs:Pending_CountsWhatThePruneWouldRemove_OnlyWhileItIsEnabledAsync</tests>
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
  v_prune_enabled BOOLEAN;   -- 442
  v_prune_horizon INTEGER;   -- 442
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

  -- 442 (#2327): what the ephemeral pointer prune would remove once due, read as the prune reads its settings.
  SELECT COALESCE(
    (SELECT setting_value::BOOLEAN FROM __SCHEMA__.wh_settings WHERE setting_key = 'ephemeral_deep_maintenance_enabled'), FALSE)
    INTO v_prune_enabled;
  SELECT GREATEST(
    COALESCE((SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'ephemeral_pointer_retention_days'), 90),
    COALESCE((SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'dedup_retention_days'), 30))
    INTO v_prune_horizon;
  IF v_prune_enabled AND NOT v_debug_mode THEN
    SELECT count(*) INTO v_rows FROM (
      SELECT 1 FROM __SCHEMA__.wh_event_store es
      WHERE (es.flags & 8) = 8
        AND es.created_at < NOW() - (v_prune_horizon * INTERVAL '1 day')
        AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_body eb WHERE eb.event_id = es.event_id)
        AND NOT EXISTS (
          SELECT 1 FROM __SCHEMA__.wh_perspective_events pe WHERE pe.event_id = es.event_id AND pe.processed_at IS NULL)
        AND es.version < (SELECT MAX(es2.version) FROM __SCHEMA__.wh_event_store es2 WHERE es2.stream_id = es.stream_id)
      LIMIT v_limit) x;
    RETURN QUERY SELECT 'prune_ancient_ephemeral_pointers'::TEXT, LEAST(v_rows, v_limit - 1), v_rows = v_limit;
  END IF;

  -- 441 (#2322): what the historical metadata conversion has left to read.
  RETURN QUERY SELECT 'convert_metadata_history'::TEXT, r.remaining, r.capped
    FROM __SCHEMA__.wh_metadata_history_remaining(p_cap) r;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_pending_reclamation(INTEGER) IS
  'The rows each index-served maintenance task would remove now, one bounded count each (at most p_cap + 1 rows '
  'read); capped means at least p_cap (#2289). Read by the pending-reclamation sampler, never by the maintenance cycle.';

SELECT __SCHEMA__.drop_all_overloads('wh_audit_body_apply');
DELETE FROM __SCHEMA__.wh_type_history_walks WHERE operation = 'audit-body';
