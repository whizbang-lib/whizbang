-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 442_EphemeralPointerPruneDrainsItsBacklog.sql
-- Date: 2026-11-14
-- Description: The opt-in ephemeral pointer prune drains a backlog in bounded batches, and reports what it would remove
--              (#2327).
--
--              The prune ran once per ephemeral_pointer_prune_interval_days (7 or 30) as one unbounded statement over
--              every qualifying pointer, so pointers that came to qualify after a run (their body reaped later, or a
--              type reclassified later) waited a whole interval, and a large backlog was deleted in one transaction,
--              which a timeout would roll back whole, watermark included, every cycle. Measured on a long-running
--              database: about 822,000 pointers qualified under the prune's own rule five days after its last run.
--
--                one call      at most ephemeral_pointer_prune_batch_size pointers (default 50000), oldest first through
--                              idx_event_store_ephemeral_reap, folding only the streams of that batch first
--                a full batch  the watermark goes back to what it was, so the next cycle is due again: the backlog
--                              drains a batch a cycle, then the interval resumes ('ok (more due, next cycle)')
--                pending       wh_pending_reclamation reports prune_ancient_ephemeral_pointers (capped) while the
--                              prune is enabled, so whizbang.maintenance.pending_rows shows it
--
--              The rule itself is unchanged (ephemeral, past the horizon, body reaped, no pending perspective work, never
--              the newest pointer of its stream), and so is the self-gate.
--
--              Number 442: reserved for this change.
--
-- Reversal: reversible (both functions go back to their earlier definitions: the prune to 113, the reading to 441)
-- Dependencies: 113 (prune_ancient_ephemeral_pointers), 163 (idx_event_store_ephemeral_reap), 441 (wh_pending_reclamation)
-- Objects: prune_ancient_ephemeral_pointers, wh_pending_reclamation
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- <docs>fundamentals/events/ephemeral-events#pruning-ancient-pointers</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralPointerPruneSqlTests.cs:Prune_KeepsNewestPointerPerStream_PrunesOlderReapedEphemeralPointersAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralPointerPruneSqlTests.cs:Prune_FoldsTheStreamShape_BeforeDiscardingPointersAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralPointerPruneSqlTests.cs:Prune_SelfGate_SecondImmediateCallIsNotDueAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralPointerPruneSqlTests.cs:Prune_ABacklogLargerThanABatch_DrainsOverSuccessiveCalls_ThenWaitsForTheIntervalAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralPointerPruneSqlTests.cs:Prune_PointersThatDoNotQualify_StayWhileTheBacklogDrainsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.prune_ancient_ephemeral_pointers()
RETURNS TABLE(rows_pruned BIGINT, status TEXT) AS $$
DECLARE
  v_enabled BOOLEAN;
  v_debug BOOLEAN;
  v_retention_days INTEGER;
  v_dedup_days INTEGER;
  v_interval_days INTEGER;
  v_horizon_days INTEGER;
  v_batch_size INTEGER;   -- 442
  v_previous TEXT;        -- 442: the watermark before this call claimed it
  v_claimed BIGINT;
  v_rows BIGINT;
  v_ids UUID[];           -- 442: this call's batch
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

  -- debug_mode retains forensic rows: the reaper (073) and completed-message purges honor it, so must this.
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
  -- 442 (#2327): the pointers one call prunes at most.
  SELECT GREATEST(COALESCE(
    (SELECT CASE WHEN btrim(setting_value) ~ '^[0-9]{1,9}$' THEN btrim(setting_value)::INTEGER END
     FROM __SCHEMA__.wh_settings WHERE setting_key = 'ephemeral_pointer_prune_batch_size'), 50000), 1)
    INTO v_batch_size;

  -- Self-gate (multi-pod safe): atomically claim this tick by advancing the watermark ONLY if the interval has elapsed.
  -- If no row is updated, another pod already claimed it this interval (or it is not yet due); return without pruning.
  -- The conditional UPDATE is the atomic CAS; the row lock it takes serializes a second pod until this call commits.
  SELECT s.setting_value INTO v_previous FROM __SCHEMA__.wh_settings s WHERE s.setting_key = 'ephemeral_pointer_prune_last_run';
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
  -- Safety floor: the horizon can NEVER be shorter than the dedup window; a still-deduped redelivery must never find
  -- its pointer already gone.
  v_horizon_days := GREATEST(v_retention_days, v_dedup_days);

  -- 442 (#2327): this call's batch, oldest first through idx_event_store_ephemeral_reap (created_at leads it), under
  -- the prune's own rule: ephemeral, past the horizon, body already reaped, no pending perspective work, and not the
  -- newest pointer of its stream (the surviving tombstone keeps the stream flagged ephemeral for the rebuild guard and
  -- is the cursor's last_event_id target).
  SELECT COALESCE(array_agg(c.event_id), '{}'), COALESCE(array_agg(DISTINCT c.stream_id), '{}')
    INTO v_ids, v_fold_targets
  FROM (
    SELECT es.event_id, es.stream_id
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
        WHERE es2.stream_id = es.stream_id)
    ORDER BY es.created_at
    LIMIT v_batch_size
  ) c;

  -- Fold-before-discard: the streams about to lose pointers fold their CURRENT full path first. Watermark-aware, so a
  -- stream folded earlier (a close, a settled fold) costs one anti-join probe.
  IF array_length(v_fold_targets, 1) IS NOT NULL THEN
    PERFORM __SCHEMA__.fold_stream_apply_paths(v_fold_targets);
  END IF;

  DELETE FROM __SCHEMA__.wh_event_store es
  WHERE es.event_id = ANY(v_ids)
    AND (es.flags & 8) = 8
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_body eb WHERE eb.event_id = es.event_id)
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
      WHERE pe.event_id = es.event_id AND pe.processed_at IS NULL);
  GET DIAGNOSTICS v_rows = ROW_COUNT;

  -- 442 (#2327): a full batch may have left more behind. The watermark goes back to what it was, so the next cycle is due
  -- again and the backlog drains a batch a cycle; only a call that took less than a batch leaves the interval running.
  IF cardinality(v_ids) >= v_batch_size THEN
    UPDATE __SCHEMA__.wh_settings SET setting_value = v_previous WHERE setting_key = 'ephemeral_pointer_prune_last_run';
    RETURN QUERY SELECT v_rows, 'ok (more due, next cycle)'::TEXT;
    RETURN;
  END IF;

  RETURN QUERY SELECT v_rows, 'ok'::TEXT;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.prune_ancient_ephemeral_pointers() IS
  'Opt-in (ephemeral_deep_maintenance_enabled) prune of ancient ephemeral pointers: body reaped, past the horizon, no '
  'pending perspective work, never the newest pointer of a stream; folds each stream first. Self-gated to '
  'ephemeral_pointer_prune_interval_days. Issue #2327: at most ephemeral_pointer_prune_batch_size (default 50000) per '
  'call, oldest first; a full batch leaves the watermark where it was, so the backlog drains a batch a cycle.';

-- ---------------------------------------------------------------------------------------------
-- wh_pending_reclamation, last word 441_MetadataHistoryConversionReportsWhatIsLeft.sql, copied whole, with one more
-- task: prune_ancient_ephemeral_pointers.
-- ---------------------------------------------------------------------------------------------
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
