-- Migration: 017_ProcessOutboxFailures.sql
-- Date: 2025-12-25
-- Description: Creates process_outbox_failures function for handling failed outbox messages.
--              Implements exponential backoff retry with scheduled_for timestamp.
-- Dependencies: 001-016 (requires wh_outbox table)

CREATE OR REPLACE FUNCTION __SCHEMA__.process_outbox_failures(
  p_failures JSONB,
  p_now TIMESTAMPTZ
) RETURNS VOID AS $$
DECLARE
  v_failure RECORD;
BEGIN
  IF jsonb_array_length(p_failures) = 0 THEN RETURN; END IF;

  FOR v_failure IN
    SELECT
      (elem->>'MessageId')::UUID as msg_id,
      (elem->>'CompletedStatus')::INTEGER as status_flags,
      elem->>'Error' as error_message,
      (elem->>'FailureReason')::INTEGER as failure_reason
    FROM jsonb_array_elements(p_failures) as elem
  LOOP
    -- Update message with failure information and exponential backoff
    UPDATE wh_outbox o
    SET status = o.status | v_failure.status_flags | 32768,  -- Set Failed bit (32768)
        error = v_failure.error_message,
        failure_reason = COALESCE(v_failure.failure_reason, 0),  -- Default to Unknown (0)
        attempts = o.attempts + 1,
        -- Exponential backoff: 30s * 2^(attempts+1), capped at 5 minutes
        scheduled_for = p_now + (INTERVAL '30 seconds' * LEAST(POWER(2, LEAST(o.attempts + 1, 10)), 10)),
        instance_id = NULL,
        lease_expiry = NULL
    WHERE o.message_id = v_failure.msg_id;
  END LOOP;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.process_outbox_failures IS
'Processes outbox message failures. Sets Failed flag, records error details, increments attempts, and schedules retry with exponential backoff (capped at 5 minutes). Releases lease for reclaiming by other instances.';
