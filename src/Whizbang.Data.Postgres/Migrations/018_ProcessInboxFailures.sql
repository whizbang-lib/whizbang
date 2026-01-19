-- Migration: 018_ProcessInboxFailures.sql
-- Date: 2025-12-25
-- Description: Creates process_inbox_failures function for handling failed inbox messages.
--              Implements exponential backoff retry with scheduled_for timestamp.
-- Dependencies: 001-017 (requires wh_inbox table)

CREATE OR REPLACE FUNCTION __SCHEMA__.process_inbox_failures(
  p_failures JSONB,
  p_now TIMESTAMPTZ
) RETURNS VOID AS $$
DECLARE
  v_failure RECORD;
BEGIN
  FOR v_failure IN
    SELECT
      (elem->>'MessageId')::UUID as msg_id,
      (elem->>'CompletedStatus')::INTEGER as status_flags,
      elem->>'Error' as error_message,
      (elem->>'FailureReason')::INTEGER as failure_reason
    FROM jsonb_array_elements(p_failures) as elem
  LOOP
    -- Update message with failure information and exponential backoff
    UPDATE wh_inbox i
    SET status = i.status | v_failure.status_flags | 32768,  -- Set Failed bit (32768)
        error = v_failure.error_message,
        failure_reason = COALESCE(v_failure.failure_reason, 0),  -- Default to Unknown (0)
        attempts = i.attempts + 1,
        -- Exponential backoff: 30s * 2^(attempts+1)
        scheduled_for = p_now + (INTERVAL '30 seconds' * POWER(2, i.attempts + 1)),
        instance_id = NULL,
        lease_expiry = NULL
    WHERE i.message_id = v_failure.msg_id;
  END LOOP;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.process_inbox_failures IS
'Processes inbox message failures. Sets Failed flag, records error details, increments attempts, and schedules retry with exponential backoff. Releases lease for reclaiming by other instances.';
