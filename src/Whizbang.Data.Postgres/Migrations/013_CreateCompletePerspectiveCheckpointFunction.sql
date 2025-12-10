-- =============================================
-- Create complete_perspective_checkpoint_work function
-- Updates perspective checkpoint status when processing completes (or fails)
-- Supports CatchingUp status for time-travel/replay scenarios
-- =============================================

CREATE OR REPLACE FUNCTION complete_perspective_checkpoint_work(
  p_stream_id UUID,
  p_perspective_name TEXT,
  p_last_event_id UUID,
  p_status SMALLINT,
  p_error TEXT DEFAULT NULL
)
RETURNS BOOLEAN
LANGUAGE plpgsql
AS $$
DECLARE
  v_rows_updated INT;
  v_is_catching_up BOOLEAN;
BEGIN
  -- Check if this perspective is in CatchingUp mode (status & 8 = 8)
  SELECT (status & 8) = 8
  INTO v_is_catching_up
  FROM wh_perspective_checkpoints
  WHERE stream_id = p_stream_id
    AND perspective_name = p_perspective_name;

  -- Update the checkpoint
  UPDATE wh_perspective_checkpoints
  SET
    last_event_id = p_last_event_id,
    status = p_status,
    processed_at = NOW(),
    error = p_error
  WHERE stream_id = p_stream_id
    AND perspective_name = p_perspective_name;

  GET DIAGNOSTICS v_rows_updated = ROW_COUNT;

  -- If we were catching up and successfully completed, clear the CatchingUp flag
  IF v_is_catching_up AND (p_status & 2) = 2 THEN  -- Completed flag
    UPDATE wh_perspective_checkpoints
    SET status = status & ~8  -- Clear CatchingUp flag (bitwise AND NOT)
    WHERE stream_id = p_stream_id
      AND perspective_name = p_perspective_name;
  END IF;

  -- Return true if a row was updated, false otherwise
  RETURN v_rows_updated > 0;
END;
$$;
