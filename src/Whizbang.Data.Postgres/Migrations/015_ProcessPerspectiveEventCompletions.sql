-- Migration: 015_ProcessPerspectiveEventCompletions.sql
-- Date: 2025-12-25
-- Description: Creates process_perspective_event_completions function for marking perspective events as complete.
--              Returns stream/perspective pairs for checkpoint updates. Supports debug mode retention.
-- Dependencies: 001-014 (requires wh_perspective_events table from migration 009)

CREATE OR REPLACE FUNCTION __SCHEMA__.process_perspective_event_completions(
  p_completions JSONB,
  p_now TIMESTAMPTZ,
  p_debug_mode BOOLEAN DEFAULT FALSE
) RETURNS TABLE(
  event_work_id UUID,
  stream_id UUID,
  perspective_name VARCHAR(200),
  was_deleted BOOLEAN
) AS $$
DECLARE
  v_completion RECORD;
  v_stream_id UUID;
  v_perspective_name VARCHAR(200);
BEGIN
  FOR v_completion IN
    SELECT
      (elem->>'EventWorkId')::UUID as work_id,
      (elem->>'StatusFlags')::INTEGER as status_flags
    FROM jsonb_array_elements(p_completions) as elem
  LOOP
    -- Get stream and perspective info
    SELECT pe.stream_id, pe.perspective_name
    INTO v_stream_id, v_perspective_name
    FROM wh_perspective_events pe
    WHERE pe.event_work_id = v_completion.work_id;

    -- Skip if event not found (already deleted or never existed)
    IF NOT FOUND THEN
      CONTINUE;
    END IF;

    IF p_debug_mode THEN
      -- Debug mode: Retain event for troubleshooting
      UPDATE wh_perspective_events pe
      SET status = pe.status | v_completion.status_flags,
          processed_at = p_now,
          instance_id = NULL,
          lease_expiry = NULL
      WHERE pe.event_work_id = v_completion.work_id;

      RETURN QUERY SELECT v_completion.work_id AS event_work_id, v_stream_id AS stream_id, v_perspective_name AS perspective_name, FALSE AS was_deleted;

    ELSE
      -- Production: Delete event (ephemeral pattern)
      DELETE FROM wh_perspective_events pe
      WHERE pe.event_work_id = v_completion.work_id;

      RETURN QUERY SELECT v_completion.work_id AS event_work_id, v_stream_id AS stream_id, v_perspective_name AS perspective_name, TRUE AS was_deleted;
    END IF;
  END LOOP;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.process_perspective_event_completions IS
'Processes perspective event completions. In production mode, deletes events (ephemeral). In debug mode, retains events for troubleshooting. Returns stream/perspective pairs for checkpoint update orchestration.';
