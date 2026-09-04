-- Migration: 129_StackHistory
-- Date: 2026-09-04
-- Description: Rolling stack-history log (P4 of plans/dlq-stack-intelligence.md). wh_stack_daily
--   is a bounded per-stack-per-day occurrence rollup, and wh_stacks gains last_seen, so
--   "which failure shapes trend over time" survives the purge/archival of the underlying dead
--   letters — decoupling long-term trend from DLQ retention. record_dead_letter_stack maintains
--   both; prune_stack_history rolls the window (non-positive retention = keep forever).
-- Dependencies: 128_DlqStackTables (wh_stacks, record_dead_letter_stack)
-- Objects: wh_stack_daily, record_dead_letter_stack, record_dead_letter_stacks, prune_stack_history

ALTER TABLE __SCHEMA__.wh_stacks ADD COLUMN IF NOT EXISTS last_seen TIMESTAMPTZ;
ALTER TABLE __SCHEMA__.wh_stacks ADD COLUMN IF NOT EXISTS total_occurrences BIGINT NOT NULL DEFAULT 0;

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_stack_daily (
  stack_id    VARCHAR(16) NOT NULL,
  day         DATE        NOT NULL,
  occurrences BIGINT      NOT NULL DEFAULT 0,
  PRIMARY KEY (stack_id, day)
);

-- Rolling-window prune walks by day; trend queries walk by (stack_id, day).
CREATE INDEX IF NOT EXISTS wh_stack_daily_day_idx ON __SCHEMA__.wh_stack_daily (day);

COMMENT ON TABLE __SCHEMA__.wh_stack_daily IS
'Rolling stack-history log (129): one row per stack per day with an occurrence count. Bounded growth (a storm is a handful of stacks, not a row per event), and it survives dead-letter purging — so failure-shape trends outlive DLQ retention. Pruned by prune_stack_history.';

-- ============================================================================
-- record_dead_letter_stack — VERBATIM from 128 plus last_seen + daily rollup
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__.record_dead_letter_stack(
  p_dead_letter_id UUID, p_stack_id VARCHAR(16), p_is_prose BOOLEAN, p_frames TEXT[])
RETURNS VOID
LANGUAGE plpgsql
AS $$
DECLARE
  v_pos INTEGER;
BEGIN
  INSERT INTO __SCHEMA__.wh_stacks (stack_id, frame_count, is_prose, last_seen, total_occurrences)
  VALUES (p_stack_id, COALESCE(array_length(p_frames, 1), 0), p_is_prose, NOW(), 1)
  ON CONFLICT (stack_id) DO UPDATE
    SET last_seen = NOW(), total_occurrences = __SCHEMA__.wh_stacks.total_occurrences + 1;

  IF p_frames IS NOT NULL THEN
    FOR v_pos IN 1..COALESCE(array_length(p_frames, 1), 0) LOOP
      INSERT INTO __SCHEMA__.wh_stack_frames (frame)
      VALUES (p_frames[v_pos])
      ON CONFLICT (frame) DO NOTHING;

      INSERT INTO __SCHEMA__.wh_stack_links (stack_id, position, frame_id)
      SELECT p_stack_id, v_pos, f.frame_id
      FROM __SCHEMA__.wh_stack_frames f
      WHERE f.frame = p_frames[v_pos]
      ON CONFLICT (stack_id, position) DO NOTHING;
    END LOOP;
  END IF;

  -- Rolling history: bump today's occurrence count. Deduped to one row per stack per day.
  INSERT INTO __SCHEMA__.wh_stack_daily (stack_id, day, occurrences)
  VALUES (p_stack_id, CURRENT_DATE, 1)
  ON CONFLICT (stack_id, day) DO UPDATE SET occurrences = __SCHEMA__.wh_stack_daily.occurrences + 1;

  UPDATE __SCHEMA__.wh_dead_letters
  SET stack_id = p_stack_id
  WHERE dead_letter_id = p_dead_letter_id;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.record_dead_letter_stack IS
'Persists a C#-normalized stack idempotently (128, extended 129): stack row (with last_seen), deduped frames, ordered links, today''s rolling-history count, and the dead letter''s stamp. A storm records the same stack thousands of times — every conflict is a designed no-op, and the daily count is the one growing number (bounded to one row per stack per day).';

-- ============================================================================
-- prune_stack_history — the rolling window
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__.prune_stack_history(p_retention_days INTEGER)
RETURNS INTEGER
LANGUAGE plpgsql
AS $$
DECLARE
  v_count INTEGER;
BEGIN
  -- Non-positive retention disables the rolling cleanup: the log is kept forever.
  IF p_retention_days <= 0 THEN
    RETURN 0;
  END IF;
  DELETE FROM __SCHEMA__.wh_stack_daily
  WHERE day < CURRENT_DATE - p_retention_days;
  GET DIAGNOSTICS v_count = ROW_COUNT;
  RETURN v_count;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.prune_stack_history IS
'Rolls the stack-history window (129): deletes wh_stack_daily rows older than p_retention_days. A non-positive retention disables the cleanup and returns 0 — the log is kept forever. Run on the recovery worker''s idle-gated scan.';

-- ============================================================================
-- record_dead_letter_stacks — batch wrapper: one round trip for a whole backfill
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__.record_dead_letter_stacks(p_entries JSONB)
RETURNS INTEGER
LANGUAGE plpgsql
AS $$
DECLARE
  v_entry  JSONB;
  v_count  INTEGER := 0;
  v_frames TEXT[];
BEGIN
  -- One server-side loop, one network round trip: the backfill can carry a storm-sized
  -- batch instead of N per-row calls. Each entry delegates to the single-row function so
  -- the stack/frame/link/daily/stamp logic stays in exactly one place.
  FOR v_entry IN SELECT * FROM jsonb_array_elements(p_entries) LOOP
    SELECT array_agg(x) INTO v_frames
    FROM jsonb_array_elements_text(v_entry -> 'frames') AS x;
    PERFORM __SCHEMA__.record_dead_letter_stack(
      (v_entry ->> 'dead_letter_id')::UUID,
      (v_entry ->> 'stack_id')::VARCHAR(16),
      (v_entry ->> 'is_prose')::BOOLEAN,
      COALESCE(v_frames, ARRAY[]::TEXT[]));
    v_count := v_count + 1;
  END LOOP;
  RETURN v_count;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.record_dead_letter_stacks IS
'Batch wrapper for record_dead_letter_stack (129): takes a JSON array of {dead_letter_id, stack_id, is_prose, frames} and records each in one round trip. The stack backfill uses this so a 500-row batch is one call, not 500.';
