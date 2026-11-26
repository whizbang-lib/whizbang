-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 596_APrunedPointerIsNeverOneACursorRestsOn.sql (#2574): the prune goes back to its 442 definition. Reversible:
-- it holds no state.

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
