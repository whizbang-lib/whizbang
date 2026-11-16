-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 401_CleanupReportsWhatItHasLeft.sql
-- Date: 2026-11-12
-- Description: The storage meters reported what cleanup removed (whizbang.maintenance.rows_affected) and how large the
--              tables are, but nothing reported how much is eligible for removal and not yet removed, so a dashboard
--              could not tell "nothing left to clean" from "cleanup is falling behind" (#2289). Two readings, taken by
--              the pending-reclamation sampler on its own cadence, never inside the maintenance cycle:
--
--                wh_pending_reclamation              per maintenance task whose predicate an index serves, the rows
--                                                    that task would remove now: one bounded count each, reading at
--                                                    most p_cap + 1 rows through the task's index, so a reading costs
--                                                    the same however large the backlog is. capped says the count
--                                                    stopped at p_cap (at least that many).
--                wh_ephemeral_history_walk_remaining per incomplete ephemeral history walk (380), the events of its
--                                                    type it has yet to visit, from the walk's stored progress.
--
--              The tasks counted, and why the others are not, are listed on wh_pending_reclamation below. A task whose
--              predicate no index serves reports nothing: a guess would read as a measurement, and an unbounded count
--              on a sampling cadence would make the meter the heaviest statement on the database.
--
--              Number 401: 399 is the highest on this branch.
--
-- Reversal: reversible (both functions dropped; they hold no state)
-- Dependencies: 366 (the maintenance predicates mirrored here), 286 (route_idle_days), 380 (wh_type_history_walks),
--               163 (idx_event_store_ephemeral_reap), 031 (idx_perspective_events_processed), 106
--               (idx_instance_evictions_evicted_at), 190 (idx_collective_ordering_heads_updated_at), 286
--               (idx_routes_last_seen), the dedup schema (idx_message_dedup_first_seen)
-- Objects: wh_pending_reclamation, wh_ephemeral_history_walk_remaining

SELECT __SCHEMA__.drop_all_overloads('wh_pending_reclamation');
SELECT __SCHEMA__.drop_all_overloads('wh_ephemeral_history_walk_remaining');

-- ---------------------------------------------------------------------------------------------
-- wh_pending_reclamation: the rows each index-served maintenance task would remove now, bounded.
-- ---------------------------------------------------------------------------------------------
-- Counted (each predicate copied from the task, each served by the index named):
--   purge_completed_perspective_events  processed_at IS NOT NULL (idx_perspective_events_processed, 031)
--   purge_old_deduplication             first_seen_at past dedup_retention_days (idx_message_dedup_first_seen)
--   purge_instance_evictions            evicted_at past instance_eviction_retention_hours (idx_instance_evictions_evicted_at)
--   prune_collective_ordering_heads     updated_at past collective_ordering_head_retention_days
--                                       (idx_collective_ordering_heads_updated_at)
--   purge_idle_routes                   last_seen past route_idle_days (idx_routes_last_seen)
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
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_pending_reclamation(INTEGER) IS
  'The rows each index-served maintenance task would remove now, one bounded count each (at most p_cap + 1 rows '
  'read); capped means at least p_cap (#2289). Read by the pending-reclamation sampler, never by the maintenance cycle.';

-- ---------------------------------------------------------------------------------------------
-- wh_ephemeral_history_walk_remaining: the events each incomplete ephemeral history walk has yet to visit.
-- ---------------------------------------------------------------------------------------------
-- events_total is the type's event count when the walk started or restarted, and events_matched the events of the type
-- the cursor has passed, so their difference is what is left. A completed walk (completed_at set) reports nothing. Reads
-- only wh_type_history_walks, one row per walked type.
-- <docs>operations/observability/metrics#pending-reclamation</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PendingReclamationSqlTests.cs:AnIncompleteEphemeralWalk_ReportsWhatItHasLeft_AndACompleteOneNothingAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_ephemeral_history_walk_remaining()
RETURNS TABLE(event_type TEXT, remaining BIGINT)
LANGUAGE sql
STABLE
AS $$
  SELECT w.event_type, GREATEST(w.events_total - w.events_matched, 0)::BIGINT
  FROM __SCHEMA__.wh_type_history_walks w
  WHERE w.operation = 'ephemeral'
    AND w.completed_at IS NULL;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_ephemeral_history_walk_remaining() IS
  'The events each incomplete ephemeral history walk has yet to visit, from its stored progress (#2289).';
