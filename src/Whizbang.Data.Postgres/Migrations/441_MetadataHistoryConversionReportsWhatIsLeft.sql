-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 441_MetadataHistoryConversionReportsWhatIsLeft.sql
-- Date: 2026-11-14
-- Description: The historical metadata conversion (298, #1727) says how much it has left (#2322).
--
--              It walks wh_event_body in event id order from its cursor, a bounded batch at a time, and nothing reported
--              how far it had to go: a database with millions of full-form rows converting a fixed slice per cycle looked
--              the same as one that had stopped. wh_metadata_history_remaining counts the rows past the cursor, at most
--              p_cap + 1 of them through the table's key, so a reading costs the same however large the remainder is:
--
--                skipped (debug mode, metadata_convert_history off, the Full form)   no row
--                complete                                                             0
--                otherwise                                                            the rows past the cursor (capped
--                                                                                     says at least p_cap)
--
--              The rows past the cursor are what the walk still reads, rows written since slim metadata included, so this
--              is the work left in reads, which is what each cycle spends. The maintenance step logs it with every cycle,
--              and wh_pending_reclamation reports it as the convert_metadata_history task, so it reaches
--              whizbang.maintenance.pending_rows.
--
--              Number 441: reserved for this change.
--
-- Reversal: reversible (wh_pending_reclamation goes back to its 401 definition; the new function is dropped)
-- Dependencies: 298 (wh_metadata_history_conversion), 285 (_wh_metadata_write_settings), 401 (wh_pending_reclamation)
-- Objects: wh_metadata_history_remaining, wh_pending_reclamation
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

SELECT __SCHEMA__.drop_all_overloads('wh_metadata_history_remaining');

-- The rows the historical metadata conversion has yet to read, past its cursor, bounded by p_cap; no row while the
-- conversion is skipped. The settings are read as wh_convert_event_metadata_history reads them.
-- <docs>data/event-store#history-conversion</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Remaining_CountsTheRowsPastTheCursor_AndReachesZeroAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Remaining_WhileSkipped_ReportsNothing_AndAPastTheCapCountSaysSoAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_metadata_history_remaining(p_cap INTEGER)
RETURNS TABLE(remaining BIGINT, capped BOOLEAN)
LANGUAGE plpgsql
STABLE
AS $$
DECLARE
  v_limit BIGINT := GREATEST(COALESCE(p_cap, 1), 1)::BIGINT + 1;
  v_form TEXT;
  v_debug BOOLEAN;
  v_convert BOOLEAN;
  v_from UUID;
  v_completed TIMESTAMPTZ;
  v_rows BIGINT;
BEGIN
  SELECT s.write_form INTO v_form FROM __SCHEMA__._wh_metadata_write_settings() s;
  SELECT
    COALESCE(bool_or(CASE WHEN st.setting_key = 'debug_mode'
                          THEN lower(btrim(st.setting_value)) IN ('true', 't', '1', 'yes', 'y', 'on') END), FALSE),
    NOT COALESCE(bool_or(CASE WHEN st.setting_key = 'metadata_convert_history'
                              THEN lower(btrim(st.setting_value)) IN ('false', 'f', '0', 'no', 'n', 'off') END), FALSE)
  INTO v_debug, v_convert
  FROM __SCHEMA__.wh_settings st
  WHERE st.setting_key IN ('debug_mode', 'metadata_convert_history');
  IF v_debug OR NOT v_convert OR v_form = 'Full' THEN
    RETURN;
  END IF;

  SELECT c.last_event_id, c.completed_at INTO v_from, v_completed
  FROM __SCHEMA__.wh_metadata_history_conversion c WHERE c.only_row;
  IF v_completed IS NOT NULL THEN
    RETURN QUERY SELECT 0::BIGINT, FALSE;
    RETURN;
  END IF;

  SELECT count(*) INTO v_rows FROM (
    SELECT 1 FROM __SCHEMA__.wh_event_body eb
    WHERE eb.event_id > COALESCE(v_from, __EMPTY_UUID__::uuid)
    LIMIT v_limit) x;
  RETURN QUERY SELECT LEAST(v_rows, v_limit - 1), v_rows = v_limit;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_metadata_history_remaining(INTEGER) IS
  'Issue #2322: the event rows the historical metadata conversion has yet to read past its cursor (at most p_cap + 1 '
  'read; capped means at least p_cap); 0 once complete; no row while the conversion is skipped.';

-- ---------------------------------------------------------------------------------------------
-- wh_pending_reclamation, last word 401_CleanupReportsWhatItHasLeft.sql, copied whole, with one more task:
-- convert_metadata_history.
-- ---------------------------------------------------------------------------------------------
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
