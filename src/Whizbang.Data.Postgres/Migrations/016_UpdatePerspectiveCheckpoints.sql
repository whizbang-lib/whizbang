-- Migration: 016_UpdatePerspectiveCheckpoints.sql
-- Date: 2025-12-25
-- Description: Creates update_perspective_checkpoints function for updating persistent checkpoint state.
--              Finds highest completed sequence with no gaps, updates checkpoint atomically.
-- Dependencies: 001-015 (requires wh_perspective_events and wh_perspective_checkpoints tables)

CREATE OR REPLACE FUNCTION __SCHEMA__.update_perspective_checkpoints(
  p_completed_events JSONB,  -- [{StreamId, PerspectiveName}]
  p_debug_mode BOOLEAN DEFAULT FALSE
) RETURNS VOID AS $$
DECLARE
  v_checkpoint RECORD;
  v_last_sequence BIGINT;
  v_last_event_id UUID;
  v_is_complete BOOLEAN;
BEGIN
  FOR v_checkpoint IN
    SELECT DISTINCT
      (elem->>'StreamId')::UUID as stream_id,
      elem->>'PerspectiveName' as perspective_name
    FROM jsonb_array_elements(p_completed_events) as elem
  LOOP
    -- Reset variables for each checkpoint (prevent stale values from previous iteration)
    v_last_sequence := NULL;
    v_last_event_id := NULL;
    v_is_complete := FALSE;

    -- Find highest sequence with no gaps before it
    -- This ensures we only advance the checkpoint to a safely completed position
    SELECT MAX(pe.sequence_number) INTO v_last_sequence
    FROM wh_perspective_events pe
    WHERE pe.stream_id = v_checkpoint.stream_id
      AND pe.perspective_name = v_checkpoint.perspective_name
      AND pe.processed_at IS NOT NULL
      -- Critical: Ensure no earlier uncompleted events
      AND NOT EXISTS (
        SELECT 1 FROM wh_perspective_events earlier
        WHERE earlier.stream_id = pe.stream_id
          AND earlier.perspective_name = pe.perspective_name
          AND earlier.sequence_number < pe.sequence_number
          AND earlier.processed_at IS NULL
      );

    -- Get event_id for that sequence from event store
    IF v_last_sequence IS NOT NULL THEN
      SELECT es.event_id INTO v_last_event_id
      FROM wh_event_store es
      WHERE es.stream_id = v_checkpoint.stream_id
        AND es.sequence_number = v_last_sequence;
    END IF;

    -- Check if all events for this stream/perspective are complete
    v_is_complete := NOT EXISTS (
      SELECT 1 FROM wh_perspective_events pe2
      WHERE pe2.stream_id = v_checkpoint.stream_id
        AND pe2.perspective_name = v_checkpoint.perspective_name
        AND pe2.processed_at IS NULL
    );

    -- Update checkpoint atomically
    -- COALESCE ensures we don't overwrite with NULL if no new progress
    UPDATE wh_perspective_checkpoints pc
    SET last_event_id = COALESCE(v_last_event_id, pc.last_event_id),
        status = CASE WHEN v_is_complete THEN 2 ELSE pc.status END
    WHERE pc.stream_id = v_checkpoint.stream_id
      AND pc.perspective_name = v_checkpoint.perspective_name;

    -- If checkpoint doesn't exist, create it
    IF NOT FOUND THEN
      INSERT INTO wh_perspective_checkpoints (
        stream_id,
        perspective_name,
        last_event_id,
        status
      ) VALUES (
        v_checkpoint.stream_id,
        v_checkpoint.perspective_name,
        v_last_event_id,
        CASE WHEN v_is_complete THEN 2 ELSE 0 END
      );
    END IF;
  END LOOP;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.update_perspective_checkpoints IS
'Updates persistent perspective checkpoints based on completed events. Finds highest completed sequence with no gaps, ensuring sequential consistency. Updates or creates checkpoint records atomically.';
