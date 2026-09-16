-- Migration: 154_PerspectiveFailureElementNames.sql
-- Date: 2026-09-15
-- Description: process_perspective_event_failures reads the failure element the runtime writes.
--
--              The runtime reports a failed perspective event through the failure channel as the
--              same record every category shares, serialized with the field names MessageId and
--              Reason. This function read EventWorkId and FailureReason, so every element the
--              runtime ever sent matched no row: the failure was never recorded, the counter the
--              dead-letter decision reads never moved, the retry was never scheduled with backoff,
--              and the lease simply lapsed. The row was re-claimed on the next cycle and failed
--              again, forever, which is how an unreadable stored document produced an identical
--              error every cycle with nothing parked and nothing dead-lettered.
--
--              The element's work id is now taken from MessageId, or from EventWorkId when a
--              caller writes that name, and the reason from Reason or FailureReason likewise.
--              Everything else is reproduced verbatim from 139.
--
-- Dependencies: 139 (process_perspective_event_failures)
-- Objects: process_perspective_event_failures

CREATE OR REPLACE FUNCTION __SCHEMA__.process_perspective_event_failures(
  p_failures JSONB,
  p_now TIMESTAMPTZ
) RETURNS VOID AS $$
DECLARE
  v_failure RECORD;
BEGIN
  IF jsonb_array_length(p_failures) = 0 THEN RETURN; END IF;

  FOR v_failure IN
    SELECT
      COALESCE(elem->>__ENVELOPE_FIELD_MESSAGE_ID__, elem->>'EventWorkId')::UUID as work_id,
      (elem->>'CompletedStatus')::INTEGER as status_flags,
      elem->>'Error' as error_message,
      COALESCE(elem->>'Reason', elem->>'FailureReason')::INTEGER as failure_reason
    FROM jsonb_array_elements(p_failures) as elem
  LOOP
    UPDATE __SCHEMA__.wh_perspective_events pe
    SET status = pe.status | v_failure.status_flags | 32768,  -- Set Failed bit (32768)
        error = v_failure.error_message,
        failure_reason = COALESCE(v_failure.failure_reason, 0),  -- Default to Unknown (0)
        failures = pe.failures + 1,
        -- Exponential backoff: 30s * 2^failures, capped at 5 minutes
        scheduled_for = p_now + (INTERVAL '30 seconds' * LEAST(POWER(2, LEAST(pe.failures + 1, 10)), 10)),
        instance_id = NULL,
        lease_expiry = NULL
    WHERE pe.event_work_id = v_failure.work_id;
  END LOOP;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.process_perspective_event_failures(JSONB, TIMESTAMPTZ) IS
  'Records apply failures: sets the Failed bit and error, bumps failures (never attempts), schedules '
  'the retry with a backoff that escalates on failures, and releases the lease. Reads the element the '
  'runtime writes (MessageId, Reason) as well as the older names (EventWorkId, FailureReason).';
