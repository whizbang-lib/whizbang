-- Migration: 069_ScheduleManagement.sql
-- Date: 2026-07-13 (F2 temporal engine — increment 5a: schedule management API, DB core)
-- Description: Authoritative schedule lifecycle operations (DB = source of truth; a mutation later
--              also rings the arm-on-mutation doorbell — increment 5b):
--                wh_create_schedule     — create (or idempotent create-or-update by schedule_key);
--                                         computes the initial next_fire_at from the recurrence config.
--                wh_transition_schedule — pause / resume / cancel via a target status, with optional
--                                         optimistic-concurrency version check.
--              Adds a partial UNIQUE index on schedule_key so create-or-update-by-key is race-safe.
-- Dependencies: 066 (wh_schedules), 067 (wh_cron_next). Status: 0=Active 1=Paused 2=Completed 3=Cancelled.

-- Idempotent-by-key needs a unique key (partial: null keys are never deduplicated).
CREATE UNIQUE INDEX IF NOT EXISTS uq_wh_schedules_key
  ON __SCHEMA__.wh_schedules (schedule_key)
  WHERE schedule_key IS NOT NULL;

-- ---------------------------------------------------------------------------
-- wh_create_schedule — create or (by key) update; computes initial next_fire_at.
-- ---------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_create_schedule');

CREATE OR REPLACE FUNCTION __SCHEMA__.wh_create_schedule(
  p_schedule_id UUID,
  p_schedule_key TEXT,
  p_stream_id UUID,
  p_partition_number INTEGER,
  p_recurrence_kind SMALLINT,
  p_interval_ms BIGINT,
  p_cron TEXT,
  p_timezone TEXT,
  p_start_at TIMESTAMPTZ,
  p_until_at TIMESTAMPTZ,
  p_max_occurrences BIGINT,
  p_misfire_policy SMALLINT,
  p_delivery_guarantee SMALLINT,
  p_event_type TEXT,
  p_event_data JSONB,
  p_scope JSONB
) RETURNS TABLE(o_schedule_id UUID, o_next_fire_at TIMESTAMPTZ, o_was_created BOOLEAN)
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
DECLARE
  v_next TIMESTAMPTZ;
BEGIN
  -- Initial next fire: one-shot fires at start (or now); interval fires one interval out (or at start);
  -- cron fires at the next matching time after start/now.
  v_next := CASE p_recurrence_kind
    WHEN 0 THEN COALESCE(p_start_at, NOW())
    WHEN 1 THEN COALESCE(p_start_at, NOW() + make_interval(secs => COALESCE(p_interval_ms, 0) / 1000.0))
    WHEN 2 THEN __SCHEMA__.wh_cron_next(p_cron, COALESCE(p_start_at, NOW()), COALESCE(p_timezone, 'UTC'))
    ELSE NULL
  END;
  IF v_next IS NULL THEN
    RAISE EXCEPTION 'Schedule has no valid next fire (kind=%, cron=%)', p_recurrence_kind, p_cron;
  END IF;

  RETURN QUERY
  WITH upserted AS (
    INSERT INTO __SCHEMA__.wh_schedules AS sch (
      schedule_id, schedule_key, stream_id, partition_number, recurrence_kind, interval_ms, cron, timezone,
      next_fire_at, until_at, max_occurrences, misfire_policy, delivery_guarantee, status,
      event_type, event_data, scope
    ) VALUES (
      p_schedule_id, p_schedule_key, p_stream_id, COALESCE(p_partition_number, 0), p_recurrence_kind,
      p_interval_ms, p_cron, p_timezone, v_next, p_until_at, p_max_occurrences,
      COALESCE(p_misfire_policy, 0), COALESCE(p_delivery_guarantee, 0), 0, p_event_type, p_event_data, p_scope
    )
    ON CONFLICT (schedule_key) WHERE schedule_key IS NOT NULL
    DO UPDATE SET
      stream_id = EXCLUDED.stream_id,
      partition_number = EXCLUDED.partition_number,
      recurrence_kind = EXCLUDED.recurrence_kind,
      interval_ms = EXCLUDED.interval_ms,
      cron = EXCLUDED.cron,
      timezone = EXCLUDED.timezone,
      next_fire_at = EXCLUDED.next_fire_at,
      until_at = EXCLUDED.until_at,
      max_occurrences = EXCLUDED.max_occurrences,
      misfire_policy = EXCLUDED.misfire_policy,
      delivery_guarantee = EXCLUDED.delivery_guarantee,
      status = 0,                                 -- re-activate on create-or-update by key
      event_type = EXCLUDED.event_type,
      event_data = EXCLUDED.event_data,
      scope = EXCLUDED.scope,
      version = sch.version + 1
    RETURNING sch.schedule_id, sch.next_fire_at, (sch.xmax = 0) AS was_created
  )
  SELECT upserted.schedule_id, upserted.next_fire_at, upserted.was_created FROM upserted;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_create_schedule IS
  'Creates a schedule (or idempotently updates it by schedule_key), computing the initial next_fire_at '
  'from the recurrence config. Returns (schedule_id, next_fire_at, was_created).';

-- ---------------------------------------------------------------------------
-- wh_transition_schedule — pause (1) / resume (0) / cancel (3), optimistic-concurrency aware.
-- ---------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_transition_schedule');

CREATE OR REPLACE FUNCTION __SCHEMA__.wh_transition_schedule(
  p_schedule_id UUID,
  p_target_status SMALLINT,
  p_expected_version BIGINT DEFAULT NULL
) RETURNS TABLE(o_updated BOOLEAN, o_version BIGINT)
LANGUAGE plpgsql
AS $$
DECLARE
  v_version BIGINT;
BEGIN
  -- Only Active(0) / Paused(1) are transitionable; Completed(2) / Cancelled(3) are terminal.
  UPDATE __SCHEMA__.wh_schedules
  SET status = p_target_status,
      version = version + 1
  WHERE schedule_id = p_schedule_id
    AND status IN (0, 1)
    AND (p_expected_version IS NULL OR version = p_expected_version)
  RETURNING version INTO v_version;

  IF FOUND THEN
    RETURN QUERY SELECT TRUE, v_version;
  ELSE
    RETURN QUERY SELECT FALSE, NULL::BIGINT;
  END IF;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_transition_schedule IS
  'Transitions a schedule to a target status (0=resume/Active, 1=Pause, 3=Cancel) from a non-terminal '
  'state, honoring an optional optimistic-concurrency version check. Returns (updated, new_version).';
