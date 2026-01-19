-- Migration: 019_ProcessPerspectiveEventFailures.sql
-- Date: 2025-12-25
-- Description: Creates process_perspective_event_failures function for handling failed perspective events.
--              Implements exponential backoff retry with scheduled_for timestamp.
-- Dependencies: 001-018 (requires wh_perspective_events table from migration 009)

CREATE OR REPLACE FUNCTION __SCHEMA__.process_perspective_event_failures(
  p_failures JSONB,
  p_now TIMESTAMPTZ
) RETURNS VOID AS $$
DECLARE
  v_failure RECORD;
BEGIN
  FOR v_failure IN
    SELECT
      (elem->>'EventWorkId')::UUID as work_id,
      (elem->>'CompletedStatus')::INTEGER as status_flags,
      elem->>'Error' as error_message,
      (elem->>'FailureReason')::INTEGER as failure_reason
    FROM jsonb_array_elements(p_failures) as elem
  LOOP
    -- Update event with failure information and exponential backoff
    UPDATE wh_perspective_events pe
    SET status = pe.status | v_failure.status_flags | 32768,  -- Set Failed bit (32768)
        error = v_failure.error_message,
        failure_reason = COALESCE(v_failure.failure_reason, 0),  -- Default to Unknown (0)
        attempts = pe.attempts + 1,
        -- Exponential backoff: 30s * 2^(attempts+1)
        scheduled_for = p_now + (INTERVAL '30 seconds' * POWER(2, pe.attempts + 1)),
        instance_id = NULL,
        lease_expiry = NULL
    WHERE pe.event_work_id = v_failure.work_id;
  END LOOP;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.process_perspective_event_failures IS
'Processes perspective event failures. Sets Failed flag, records error details, increments attempts, and schedules retry with exponential backoff. Releases lease for reclaiming by other instances.';
