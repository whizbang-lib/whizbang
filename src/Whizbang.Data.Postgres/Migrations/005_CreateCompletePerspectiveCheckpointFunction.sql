-- Migration: 005_CreateCompletePerspectiveCheckpointFunction.sql
-- Date: 2025-12-21 (merged from 005 and 005a)
-- Description: Creates complete_perspective_checkpoint_work function for updating perspective checkpoints
--              when perspective runners report completion/failure results.
--              Supports CatchingUp status for time-travel/replay scenarios.
-- Dependencies: 001-004 (requires wh_perspective_checkpoints table)
-- Used by: 006 (process_work_batch calls this function)

DROP FUNCTION IF EXISTS complete_perspective_checkpoint_work(UUID, TEXT, UUID, SMALLINT, TEXT);
DROP FUNCTION IF EXISTS complete_perspective_checkpoint_work(UUID, VARCHAR(200), UUID, SMALLINT, TEXT);

CREATE OR REPLACE FUNCTION complete_perspective_checkpoint_work(
  p_stream_id UUID,
  p_perspective_name VARCHAR(200),
  p_last_event_id UUID,
  p_status SMALLINT,
  p_error_message TEXT DEFAULT NULL
) RETURNS VOID AS $$
DECLARE
  v_is_catching_up BOOLEAN;
BEGIN
  -- Check if this perspective is in CatchingUp mode (status & 8 = 8)
  SELECT (status & 8) = 8
  INTO v_is_catching_up
  FROM wh_perspective_checkpoints
  WHERE stream_id = p_stream_id
    AND perspective_name = p_perspective_name;

  -- Update checkpoint with results from perspective runner
  -- Includes error message for failed runs, clears error for successful runs
  UPDATE wh_perspective_checkpoints
  SET last_event_id = p_last_event_id,
      status = p_status,
      processed_at = NOW(),
      error = p_error_message
  WHERE stream_id = p_stream_id
    AND perspective_name = p_perspective_name;

  -- If we were catching up and successfully completed, clear the CatchingUp flag
  IF v_is_catching_up AND (p_status & 2) = 2 THEN  -- Completed flag
    UPDATE wh_perspective_checkpoints
    SET status = status & ~8  -- Clear CatchingUp flag (bitwise AND NOT)
    WHERE stream_id = p_stream_id
      AND perspective_name = p_perspective_name;
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION complete_perspective_checkpoint_work IS 'Updates perspective checkpoint with completion/failure results from perspective runner. Supports CatchingUp status for time-travel/replay scenarios.';
