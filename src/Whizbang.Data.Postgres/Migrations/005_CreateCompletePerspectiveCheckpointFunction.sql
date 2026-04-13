-- Migration: 005_CreateCompletePerspectiveCheckpointFunction.sql
-- Date: 2025-12-21 (merged from 005 and 005a)
-- Description: Creates complete_perspective_cursor_work function for updating perspective checkpoints
--              when perspective runners report completion/failure results.
--              Supports CatchingUp status for time-travel/replay scenarios.
--              Uses explicit event ID array to prevent concurrent late-arriving events
--              from being incorrectly marked as processed via range-based cursor advancement.
-- Dependencies: 001-004 (requires wh_perspective_cursors table)
-- Used by: 006 (process_work_batch calls this function)

DROP FUNCTION IF EXISTS __SCHEMA__.complete_perspective_cursor_work(UUID, TEXT, UUID, SMALLINT, TEXT);
DROP FUNCTION IF EXISTS __SCHEMA__.complete_perspective_cursor_work(UUID, VARCHAR(200), UUID, SMALLINT, TEXT);
DROP FUNCTION IF EXISTS __SCHEMA__.complete_perspective_cursor_work(UUID, TEXT, UUID, UUID[], SMALLINT, TEXT);
DROP FUNCTION IF EXISTS __SCHEMA__.complete_perspective_cursor_work(UUID, TEXT, UUID, JSONB, SMALLINT, TEXT);

CREATE OR REPLACE FUNCTION __SCHEMA__.complete_perspective_cursor_work(
  p_stream_id UUID,
  p_perspective_name TEXT,
  p_last_event_id UUID,
  p_processed_event_ids JSONB,
  p_status SMALLINT,
  p_error_message TEXT DEFAULT NULL
) RETURNS VOID AS $$
DECLARE
  v_is_catching_up BOOLEAN;
  v_straggler_event_id UUID;
  v_event_ids UUID[];
BEGIN
  -- Parse JSONB array of event IDs to UUID[] for use in queries
  IF p_processed_event_ids IS NOT NULL
     AND jsonb_typeof(p_processed_event_ids) = 'array'
     AND jsonb_array_length(p_processed_event_ids) > 0 THEN
    SELECT array_agg(e::UUID) INTO v_event_ids
    FROM jsonb_array_elements_text(p_processed_event_ids) AS e;
  ELSE
    v_event_ids := '{}'::UUID[];
  END IF;

  -- Check if this perspective is in CatchingUp mode (status & 8 = 8)
  SELECT (status & 8) = 8
  INTO v_is_catching_up
  FROM __SCHEMA__.wh_perspective_cursors
  WHERE stream_id = p_stream_id
    AND perspective_name = p_perspective_name;

  -- Update checkpoint with results from perspective runner
  -- Includes error message for failed runs, clears error for successful runs
  -- Clears rewind columns after successful completion to prevent rewind loops
  UPDATE __SCHEMA__.wh_perspective_cursors
  SET last_event_id = p_last_event_id,
      status = p_status,
      processed_at = NOW(),
      error = p_error_message,
      rewind_trigger_event_id = NULL,
      rewind_flagged_at = NULL,
      rewind_first_flagged_at = NULL
  WHERE stream_id = p_stream_id
    AND perspective_name = p_perspective_name;

  -- CRITICAL: Mark ONLY actually-processed perspective events
  -- Uses explicit event ID array instead of range-based marking (event_id <= cursor)
  -- to prevent concurrent late-arriving events from being incorrectly marked as processed.
  -- Event IDs are the specific events the runner read from wh_event_store and applied.
  UPDATE __SCHEMA__.wh_perspective_events pe
  SET processed_at = NOW()
  WHERE stream_id = p_stream_id
    AND perspective_name = p_perspective_name
    AND processed_at IS NULL
    AND event_id = ANY(v_event_ids);

  -- Belt-and-suspenders: detect unprocessed events below the cursor.
  -- If any perspective_events exist with event_id < cursor and processed_at IS NULL,
  -- they arrived during processing and the runner never saw them. Flag rewind so the
  -- next run replays from snapshot and picks them up.
  SELECT event_id INTO v_straggler_event_id
  FROM __SCHEMA__.wh_perspective_events
  WHERE stream_id = p_stream_id
    AND perspective_name = p_perspective_name
    AND processed_at IS NULL
    AND event_id < p_last_event_id
  ORDER BY event_id
  LIMIT 1;

  IF v_straggler_event_id IS NOT NULL THEN
    UPDATE __SCHEMA__.wh_perspective_cursors
    SET status = status | 32,  -- RewindRequired flag (1 << 5)
        rewind_trigger_event_id = CASE
          WHEN rewind_trigger_event_id IS NULL THEN v_straggler_event_id
          WHEN v_straggler_event_id < rewind_trigger_event_id THEN v_straggler_event_id
          ELSE rewind_trigger_event_id
        END,
        rewind_flagged_at = NOW(),
        rewind_first_flagged_at = COALESCE(rewind_first_flagged_at, NOW())
    WHERE stream_id = p_stream_id
      AND perspective_name = p_perspective_name;
  END IF;

  -- If we were catching up and successfully completed, clear the CatchingUp flag
  IF v_is_catching_up AND (p_status & 2) = 2 THEN  -- Completed flag
    UPDATE __SCHEMA__.wh_perspective_cursors
    SET status = status & ~8  -- Clear CatchingUp flag (bitwise AND NOT)
    WHERE stream_id = p_stream_id
      AND perspective_name = p_perspective_name;
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.complete_perspective_cursor_work IS 'Updates perspective checkpoint with completion/failure results from perspective runner. Uses explicit event ID array to prevent concurrent late-arriving events from being incorrectly marked as processed. Supports CatchingUp status for time-travel/replay scenarios.';
