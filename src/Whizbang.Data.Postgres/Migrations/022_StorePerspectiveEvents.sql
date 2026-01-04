-- Migration: 022_StorePerspectiveEvents.sql
-- Date: 2025-12-25
-- Description: Creates store_perspective_events function for inserting new perspective events with immediate lease.
--              Returns event work IDs for marking as "NewlyStored" in orchestrator response.
-- Dependencies: 001-021 (requires wh_perspective_events table from migration 009)

CREATE OR REPLACE FUNCTION __SCHEMA__.store_perspective_events(
  p_events JSONB,
  p_instance_id UUID,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ
) RETURNS TABLE(
  event_work_id UUID,
  stream_id UUID,
  perspective_name VARCHAR(200),
  was_newly_created BOOLEAN
) AS $$
DECLARE
  v_event RECORD;
  v_was_new BOOLEAN;
  v_work_id UUID;
BEGIN
  FOR v_event IN
    SELECT
      (elem->>'StreamId')::UUID as v_stream_id,
      (elem->>'PerspectiveName')::VARCHAR(200) as v_perspective_name,
      (elem->>'EventId')::UUID as v_event_id,
      (elem->>'SequenceNumber')::BIGINT as v_sequence_number
    FROM jsonb_array_elements(p_events) as elem
  LOOP
    -- Generate work item ID
    v_work_id := gen_random_uuid();

    -- Insert perspective event with immediate lease (ON CONFLICT for idempotency)
    INSERT INTO wh_perspective_events (
      event_work_id,
      stream_id,
      perspective_name,
      event_id,
      sequence_number,
      status,
      attempts,
      created_at,
      instance_id,
      lease_expiry
    ) VALUES (
      v_work_id,
      v_event.v_stream_id,
      v_event.v_perspective_name,
      v_event.v_event_id,
      v_event.v_sequence_number,
      1,  -- Stored flag
      0,  -- Initial attempts
      p_now,
      p_instance_id,  -- Immediate lease
      p_lease_expiry
    )
    ON CONFLICT ON CONSTRAINT uq_perspective_event DO NOTHING;

    -- Check if insert succeeded (ROW_COUNT = 1 means new row)
    GET DIAGNOSTICS v_was_new = ROW_COUNT;

    -- If conflict occurred, get existing work_id
    IF NOT v_was_new THEN
      SELECT pe.event_work_id INTO v_work_id
      FROM wh_perspective_events pe
      WHERE pe.stream_id = v_event.v_stream_id
        AND pe.perspective_name = v_event.v_perspective_name
        AND pe.event_id = v_event.v_event_id;
    END IF;

    RETURN QUERY SELECT v_work_id, v_event.v_stream_id, v_event.v_perspective_name, v_was_new;
  END LOOP;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.store_perspective_events IS
'Stores new perspective events with immediate lease to current instance. Unique constraint on (stream_id, perspective_name, event_id) prevents duplicates. Returns event work IDs for NewlyStored flag in orchestrator response.';
