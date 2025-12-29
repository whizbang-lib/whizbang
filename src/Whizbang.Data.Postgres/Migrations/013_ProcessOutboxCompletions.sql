-- Migration: 013_ProcessOutboxCompletions.sql
-- Date: 2025-12-25
-- Description: Creates process_outbox_completions function for marking outbox messages as complete.
--              Returns stream IDs for downstream stream cleanup. Supports debug mode retention.
-- Dependencies: 001-012 (requires wh_outbox table)

CREATE OR REPLACE FUNCTION process_outbox_completions(
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
    SELECT o.status, o.stream_id
    INTO v_current_status, v_stream_id
    FROM wh_outbox o
    WHERE o.message_id = v_completion.msg_id;

    -- Skip if message not found (already deleted or never existed)
    IF NOT FOUND THEN
      CONTINUE;
    END IF;

    v_new_status := v_current_status | v_completion.status_flags;

    -- Special case: status_flags = 0 means "release lease without completion"
    -- Don't set processed_at so message remains claimable by other instances
    IF v_completion.status_flags = 0 THEN
      UPDATE wh_outbox o
      SET instance_id = NULL,
          lease_expiry = NULL
      WHERE o.message_id = v_completion.msg_id;
      RETURN QUERY SELECT v_completion.msg_id AS message_id, v_stream_id AS stream_id, FALSE AS was_deleted;
      CONTINUE;
    END IF;

    IF p_debug_mode THEN
      -- Debug mode: Retain message for troubleshooting
      UPDATE wh_outbox o
      SET status = v_new_status,
          processed_at = p_now,
          published_at = CASE WHEN (v_completion.status_flags & 4) = 4
                              THEN p_now ELSE o.published_at END,
          instance_id = NULL,
          lease_expiry = NULL
      WHERE o.message_id = v_completion.msg_id;

      RETURN QUERY SELECT v_completion.msg_id AS message_id, v_stream_id AS stream_id, FALSE AS was_deleted;

    ELSE
      -- Production: Delete if Published flag set (ephemeral pattern)
      IF (v_new_status & 4) = 4 THEN
        DELETE FROM wh_outbox o WHERE o.message_id = v_completion.msg_id;
        RETURN QUERY SELECT v_completion.msg_id AS message_id, v_stream_id AS stream_id, TRUE AS was_deleted;
      ELSE
        -- Not yet published, retain with updated status
        UPDATE wh_outbox o
        SET status = v_new_status,
            processed_at = p_now,
            instance_id = NULL,
            lease_expiry = NULL
        WHERE o.message_id = v_completion.msg_id;
        RETURN QUERY SELECT v_completion.msg_id AS message_id, v_stream_id AS stream_id, FALSE AS was_deleted;
      END IF;
    END IF;
  END LOOP;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION process_outbox_completions IS
'Processes outbox message completions. In production mode, deletes messages with Published flag (ephemeral). In debug mode, retains all messages for troubleshooting. Returns stream IDs for cleanup orchestration.';
