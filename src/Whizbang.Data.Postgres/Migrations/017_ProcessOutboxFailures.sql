-- Migration: 017_ProcessOutboxFailures.sql
-- Date: 2025-12-25
-- Description: Creates process_outbox_failures function for handling failed outbox messages.
--              Implements exponential backoff retry with scheduled_for timestamp.
-- Dependencies: 001-016 (requires wh_outbox table)

SELECT __SCHEMA__.drop_all_overloads('process_outbox_failures');

CREATE OR REPLACE FUNCTION __SCHEMA__.process_outbox_failures(
  p_failures JSONB,
  p_now TIMESTAMPTZ
) RETURNS VOID AS $$
DECLARE
  v_failure RECORD;
  v_schedule_id TEXT;
  v_delivery INTEGER;
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
    -- F2 temporal: is this a schedule occurrence? _wh_spawn_occurrence stamps the occurrence's metadata
    -- with scheduleId + deliveryGuarantee, so the failure path can honour the per-schedule guarantee.
    -- Plain messages have no scheduleId => v_schedule_id is NULL and they take the unchanged path below.
    SELECT o.metadata->>'scheduleId', COALESCE((o.metadata->>'deliveryGuarantee')::INTEGER, 0)
    INTO v_schedule_id, v_delivery
    FROM __SCHEMA__.wh_outbox o
    WHERE o.message_id = v_failure.msg_id;

    IF v_schedule_id IS NOT NULL THEN
      -- Durable run/failure log for the occurrence (status 1 = Failed), with the error text.
      INSERT INTO __SCHEMA__.wh_schedule_runs
        (schedule_id, occurrence_id, fired_at, status, error_message)
      VALUES (v_schedule_id::UUID, v_failure.msg_id, p_now, 1, v_failure.error_message);
    END IF;

    IF v_schedule_id IS NOT NULL AND v_delivery = 1 THEN
      -- AT-MOST-ONCE (delivery_guarantee = 1): never redeliver — redelivery is dangerous for
      -- non-idempotent ops. Park the message terminally: the claim requires
      -- (scheduled_for IS NULL OR scheduled_for <= NOW()), so 'infinity' is never claimable again.
      -- The failure is durably recorded in wh_schedule_runs above, which is what makes this safe.
      UPDATE __SCHEMA__.wh_outbox o
      SET status = o.status | v_failure.status_flags | 32768,  -- Set Failed bit (32768)
          error = v_failure.error_message,
          failure_reason = COALESCE(v_failure.failure_reason, 0),
          scheduled_for = 'infinity'::TIMESTAMPTZ,             -- terminal: no retry, ever
          instance_id = NULL,
          lease_expiry = NULL
      WHERE o.message_id = v_failure.msg_id;
    ELSE
      -- Default (at-least-once, and every non-schedule message): unchanged retry behaviour.
      -- Phase H step 8 slice D: claim_orphaned_outbox is the SOLE source of attempt counting.
      -- See mig 018 for the rationale — symmetric across all three work tables.
      UPDATE __SCHEMA__.wh_outbox o
      SET status = o.status | v_failure.status_flags | 32768,  -- Set Failed bit (32768)
          error = v_failure.error_message,
          failure_reason = COALESCE(v_failure.failure_reason, 0),  -- Default to Unknown (0)
          -- Exponential backoff: 30s * 2^attempts, capped at 5 minutes
          scheduled_for = p_now + (INTERVAL '30 seconds' * LEAST(POWER(2, LEAST(o.attempts, 10)), 10)),
          instance_id = NULL,
          lease_expiry = NULL
      WHERE o.message_id = v_failure.msg_id;
    END IF;
  END LOOP;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.process_outbox_failures IS
'Processes outbox message failures. Sets Failed flag, records error details, and schedules retry with exponential backoff (capped at 5 minutes), releasing the lease for reclaim. F2 temporal: a failing schedule occurrence also lands a wh_schedule_runs Failed row (the durable run/failure log); and when its schedule is AT-MOST-ONCE (delivery_guarantee=1) the message is parked terminally (scheduled_for=infinity, never claimable) instead of retried — redelivery is unsafe for non-idempotent scheduled work.';
