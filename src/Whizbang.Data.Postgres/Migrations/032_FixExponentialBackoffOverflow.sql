-- Migration: 032_FixExponentialBackoffOverflow.sql
-- Date: 2026-03-06
-- Description: Fixes PostgreSQL interval overflow in failure processing functions.
--              Caps exponential backoff at 5 minutes to prevent "22008: interval out of range" error.
--              When attempts is too high, POWER(2, attempts+1) becomes astronomically large.
--              The backoff grows exponentially until 5 minutes, then stays at 5 minutes.
-- Dependencies: 017-019 (failure processing functions)

-- Maximum backoff multiplier: 10 gives 5 minutes with 30s base (30 * 10 = 300s = 5 min)
-- Exponential growth: 60s, 120s, 240s, 300s (capped), 300s, 300s, ...
-- This prevents: INTERVAL '30 seconds' * POWER(2, 60+1) from overflowing

-- Fix process_outbox_failures
CREATE OR REPLACE FUNCTION __SCHEMA__.process_outbox_failures(
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
    UPDATE wh_outbox o
    SET status = o.status | v_failure.status_flags | 32768,  -- Set Failed bit (32768)
        error = v_failure.error_message,
        failure_reason = COALESCE(v_failure.failure_reason, 0),  -- Default to Unknown (0)
        attempts = o.attempts + 1,
        -- Exponential backoff: 30s * 2^(attempts+1), capped at 5 minutes
        scheduled_for = p_now + (INTERVAL '30 seconds' * LEAST(POWER(2, o.attempts + 1), 10)),
        instance_id = NULL,
        lease_expiry = NULL
    WHERE o.message_id = v_failure.msg_id;
  END LOOP;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.process_outbox_failures IS
'Processes outbox message failures. Sets Failed flag, records error details, increments attempts, and schedules retry with exponential backoff (capped at 5 minutes). Releases lease for reclaiming by other instances.';

-- Fix process_inbox_failures
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
        -- Exponential backoff: 30s * 2^(attempts+1), capped at 5 minutes
        scheduled_for = p_now + (INTERVAL '30 seconds' * LEAST(POWER(2, i.attempts + 1), 10)),
        instance_id = NULL,
        lease_expiry = NULL
    WHERE i.message_id = v_failure.msg_id;
  END LOOP;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.process_inbox_failures IS
'Processes inbox message failures. Sets Failed flag, records error details, increments attempts, and schedules retry with exponential backoff (capped at 5 minutes). Releases lease for reclaiming by other instances.';

-- Fix process_perspective_event_failures
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
        -- Exponential backoff: 30s * 2^(attempts+1), capped at 5 minutes
        scheduled_for = p_now + (INTERVAL '30 seconds' * LEAST(POWER(2, pe.attempts + 1), 10)),
        instance_id = NULL,
        lease_expiry = NULL
    WHERE pe.event_work_id = v_failure.work_id;
  END LOOP;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.process_perspective_event_failures IS
'Processes perspective event failures. Sets Failed flag, records error details, increments attempts, and schedules retry with exponential backoff (capped at 5 minutes). Releases lease for reclaiming by other instances.';
