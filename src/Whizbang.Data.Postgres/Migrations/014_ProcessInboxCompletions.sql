-- Migration: 014_ProcessInboxCompletions.sql
-- Date: 2025-12-25
-- Description: Creates process_inbox_completions function for marking inbox messages as complete.
--              Returns stream IDs for downstream stream cleanup. Supports debug mode retention.
-- Dependencies: 001-013 (requires wh_inbox table)

CREATE OR REPLACE FUNCTION process_inbox_completions(
  p_completions JSONB,
  p_now TIMESTAMPTZ,
  p_debug_mode BOOLEAN DEFAULT FALSE
) RETURNS TABLE(
  message_id UUID,
  stream_id UUID,
  was_deleted BOOLEAN
) AS $$
DECLARE
  v_completion RECORD;
  v_current_status INTEGER;
  v_new_status INTEGER;
  v_stream_id UUID;
BEGIN
  FOR v_completion IN
    SELECT
      (elem->>'MessageId')::UUID as msg_id,
      (elem->>'Status')::INTEGER as status_flags
    FROM jsonb_array_elements(p_completions) as elem
  LOOP
    -- Get current status and stream_id
    SELECT i.status, i.stream_id
    INTO v_current_status, v_stream_id
    FROM wh_inbox i
    WHERE i.message_id = v_completion.msg_id;

    -- Skip if message not found (already deleted or never existed)
    IF NOT FOUND THEN
      CONTINUE;
    END IF;

    v_new_status := v_current_status | v_completion.status_flags;

    IF p_debug_mode THEN
      -- Debug mode: Retain message for troubleshooting
      UPDATE wh_inbox i
      SET status = v_new_status,
          processed_at = p_now,
          instance_id = NULL,
          lease_expiry = NULL
      WHERE i.message_id = v_completion.msg_id;

      RETURN QUERY SELECT v_completion.msg_id AS message_id, v_stream_id AS stream_id, FALSE AS was_deleted;

    ELSE
      -- Production: Delete if EventStored flag set (inbox completion = event stored)
      IF (v_new_status & 2) = 2 THEN
        DELETE FROM wh_inbox i WHERE i.message_id = v_completion.msg_id;
        RETURN QUERY SELECT v_completion.msg_id AS message_id, v_stream_id AS stream_id, TRUE AS was_deleted;
      ELSE
        -- Event not yet stored, retain with updated status
        UPDATE wh_inbox i
        SET status = v_new_status,
            processed_at = p_now,
            instance_id = NULL,
            lease_expiry = NULL
        WHERE i.message_id = v_completion.msg_id;
        RETURN QUERY SELECT v_completion.msg_id AS message_id, v_stream_id AS stream_id, FALSE AS was_deleted;
      END IF;
    END IF;
  END LOOP;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION process_inbox_completions IS
'Processes inbox message completions. In production mode, deletes messages with EventStored flag (ephemeral). In debug mode, retains all messages for troubleshooting. Returns stream IDs for cleanup orchestration.';
