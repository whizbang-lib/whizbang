-- =============================================
-- Create acquire_perspective_checkpoint_work function
-- Creates or updates perspective checkpoints when processing stream events
-- Uses UPSERT pattern: insert new checkpoint or update existing one
-- =============================================

CREATE OR REPLACE FUNCTION acquire_perspective_checkpoint_work(
  p_stream_id UUID,
  p_perspective_names TEXT[],
  p_event_id UUID
)
RETURNS TABLE (
  stream_id UUID,
  perspective_name TEXT,
  status SMALLINT,
  last_event_id UUID
)
LANGUAGE plpgsql
AS $$
DECLARE
  v_perspective_name TEXT;
BEGIN
  -- For each perspective, create or update its checkpoint
  FOREACH v_perspective_name IN ARRAY p_perspective_names
  LOOP
    -- UPSERT: Insert new checkpoint or update if already exists
    INSERT INTO wh_perspective_checkpoints (
      stream_id,
      perspective_name,
      last_event_id,
      status,
      processed_at
    )
    VALUES (
      p_stream_id,
      v_perspective_name,
      p_event_id,
      0,  -- Processing status
      NOW()
    )
    ON CONFLICT (stream_id, perspective_name) DO UPDATE
    SET
      last_event_id = EXCLUDED.last_event_id,
      status = 0,  -- Reset to Processing when new event arrives
      processed_at = NOW(),
      error = NULL;  -- Clear any previous error

    -- Return the checkpoint record
    RETURN QUERY
    SELECT
      pc.stream_id,
      pc.perspective_name,
      pc.status,
      pc.last_event_id
    FROM wh_perspective_checkpoints pc
    WHERE pc.stream_id = p_stream_id
      AND pc.perspective_name = v_perspective_name;
  END LOOP;

  RETURN;
END;
$$;
