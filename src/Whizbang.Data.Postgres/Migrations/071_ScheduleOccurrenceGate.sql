-- Migration: 071_ScheduleOccurrenceGate.sql
-- Date: 2026-07-14 (F2 temporal engine — increment 6b: pre-fire hook / occurrence gate)
-- Description: The occurrence-level operations the pre-fire gate needs. The gate runs in C# immediately
--              before a scheduled occurrence is published (execution), because occurrence CREATION is an
--              atomic SQL claim+advance and C# cannot run inside it.
--                wh_defer_occurrence          — retry this SAME occurrence later: reschedule the pending
--                                               outbox message and release its lease. Not dropped, not
--                                               re-created — so exactly-once creation still holds.
--                wh_log_schedule_run          — append a run-log row (gate outcomes: Skipped/etc).
--                wh_refresh_schedule_authority — write back a re-resolved authority snapshot so subsequent
--                                               fires start from fresh claims instead of stale ones.
-- Dependencies: 066 (wh_schedules / wh_schedule_runs), wh_outbox.

SELECT __SCHEMA__.drop_all_overloads('wh_defer_occurrence');

CREATE OR REPLACE FUNCTION __SCHEMA__.wh_defer_occurrence(
  p_occurrence_id UUID,
  p_until TIMESTAMPTZ
) RETURNS BOOLEAN
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
DECLARE
  v_found BOOLEAN;
BEGIN
  -- Only a still-pending (unpublished) occurrence can be deferred. Releasing the lease lets any instance
  -- pick it up once p_until arrives; the claim requires (scheduled_for IS NULL OR scheduled_for <= NOW()).
  UPDATE wh_outbox
  SET scheduled_for = p_until,
      instance_id = NULL,
      lease_expiry = NULL
  WHERE message_id = p_occurrence_id;

  GET DIAGNOSTICS v_found = ROW_COUNT;
  RETURN v_found;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_defer_occurrence IS
  'Pre-fire hook Defer: retries the SAME occurrence at a later time by rescheduling its pending outbox '
  'message and releasing the lease. The occurrence is neither dropped nor re-created.';

SELECT __SCHEMA__.drop_all_overloads('wh_log_schedule_run');

CREATE OR REPLACE FUNCTION __SCHEMA__.wh_log_schedule_run(
  p_schedule_id UUID,
  p_occurrence_id UUID,
  p_status SMALLINT,
  p_note TEXT
) RETURNS VOID
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
BEGIN
  INSERT INTO __SCHEMA__.wh_schedule_runs
    (schedule_id, occurrence_id, fired_at, status, error_message)
  VALUES (p_schedule_id, p_occurrence_id, NOW(), p_status, p_note);
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_log_schedule_run IS
  'Appends a run-log row (0=Success 1=Failed 2=Skipped 3=TriggeredEarly). Used by the pre-fire gate to '
  'keep Skip/Cancel outcomes auditable.';

SELECT __SCHEMA__.drop_all_overloads('wh_refresh_schedule_authority');

CREATE OR REPLACE FUNCTION __SCHEMA__.wh_refresh_schedule_authority(
  p_schedule_id UUID,
  p_authority_claims JSONB
) RETURNS BOOLEAN
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
DECLARE
  v_found BOOLEAN;
BEGIN
  -- The create-time claims snapshot can go stale; the pre-fire hook re-resolves it (only the application
  -- knows how) and writes it back here so subsequent fires start fresh.
  UPDATE __SCHEMA__.wh_schedules
  SET authority_claims = p_authority_claims,
      version = version + 1
  WHERE schedule_id = p_schedule_id;

  GET DIAGNOSTICS v_found = ROW_COUNT;
  RETURN v_found;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_refresh_schedule_authority IS
  'Writes back a re-resolved authority claims snapshot for a schedule (the pre-fire hook Proceed path).';
