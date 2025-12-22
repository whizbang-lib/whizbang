-- Migration: 005a_CreateCompletePerspectiveCheckpointWorkFunction.sql
-- Date: 2025-12-21
-- Description: Creates complete_perspective_checkpoint_work function for updating perspective checkpoints
--              when perspective runners report completion/failure results.
-- Dependencies: 001-005 (requires wh_perspective_checkpoints table from 005)
-- Used by: 006 (process_work_batch calls this function)

DROP FUNCTION IF EXISTS complete_perspective_checkpoint_work;

CREATE OR REPLACE FUNCTION complete_perspective_checkpoint_work(
  p_stream_id UUID,
  p_perspective_name VARCHAR(200),
  p_last_event_id UUID,
  p_status SMALLINT,
  p_error_message TEXT DEFAULT NULL
) RETURNS VOID AS $$
BEGIN
  -- Update checkpoint with results from perspective runner
  -- Includes error message for failed runs, clears error for successful runs
  UPDATE wh_perspective_checkpoints
  SET last_event_id = p_last_event_id,
      status = p_status,
      error = p_error_message
  WHERE stream_id = p_stream_id
    AND perspective_name = p_perspective_name;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION complete_perspective_checkpoint_work IS 'Updates perspective checkpoint with completion/failure results from perspective runner';
