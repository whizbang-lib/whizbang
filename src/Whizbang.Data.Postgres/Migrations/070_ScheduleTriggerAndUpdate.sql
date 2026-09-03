-- Migration: 070_ScheduleTriggerAndUpdate.sql
-- Date: 2026-07-13 (F2 temporal engine — increment 5d: trigger-now + update)
-- Description: Completes the schedule management API.
--                wh_trigger_schedule_now — fire an EXTRA occurrence immediately, by id, WITHOUT
--                                          disturbing cadence: next_fire_at / last_fire_at /
--                                          occurrence_count are untouched (so it also does not consume a
--                                          max_occurrences slot). Emits through the shared
--                                          _wh_spawn_occurrence (068) and logs the run as status 3
--                                          (TriggeredEarly). Uses a fresh random occurrence-id so it can
--                                          never collide with the deterministic cadence occurrence-ids.
--                wh_update_schedule      — reconfigure a schedule (recurrence / bounds / payload) and
--                                          recompute next_fire_at, with optimistic-concurrency version
--                                          check; rings the arm-on-mutation doorbell.
-- Dependencies: 066 (wh_schedules/runs), 067 (wh_cron_next), 068 (_wh_spawn_occurrence), 069 (mgmt core).
-- Run status: 0=Success(scheduled) 1=Failed 2=Skipped(misfire) 3=TriggeredEarly.

-- ---------------------------------------------------------------------------
-- wh_trigger_schedule_now — manual out-of-band fire; cadence untouched.
-- ---------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_trigger_schedule_now');

CREATE OR REPLACE FUNCTION __SCHEMA__.wh_trigger_schedule_now(
  p_schedule_id UUID,
  p_instance_id UUID,
  p_lease_expiry TIMESTAMPTZ,
  p_partition_count INTEGER
) RETURNS TABLE(o_triggered BOOLEAN, o_occurrence_id UUID)
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
DECLARE
  v_sched RECORD;
  v_occurrence_id UUID;
  v_now TIMESTAMPTZ := NOW();
BEGIN
  -- Only a non-terminal schedule can be triggered (Active or Paused — a paused schedule may still be
  -- run manually by an operator). Completed/Canceled are terminal.
  SELECT * INTO v_sched
  FROM __SCHEMA__.wh_schedules
  WHERE schedule_id = p_schedule_id
    AND status IN (0, 1)
  FOR UPDATE;

  IF NOT FOUND THEN
    RETURN QUERY SELECT FALSE, NULL::UUID;
    RETURN;
  END IF;

  -- Fresh random id: the cadence ids are md5(schedule:occurrence#), so a random id can never collide
  -- with the next scheduled occurrence (and each manual trigger is a distinct occurrence).
  v_occurrence_id := gen_random_uuid();

  PERFORM __SCHEMA__._wh_spawn_occurrence(
    v_sched.schedule_id, v_occurrence_id, v_sched.occurrence_count,
    v_sched.event_type, v_sched.event_data, v_sched.scope, v_sched.stream_id,
    v_sched.delivery_guarantee, p_instance_id, p_lease_expiry, v_now, p_partition_count,
    3::SMALLINT,   -- 3 = TriggeredEarly
    v_sched.authority_principal_id, v_sched.authority_claims);

  -- Deliberately NOT advancing next_fire_at / last_fire_at / occurrence_count: the regular cadence
  -- continues exactly as it would have, and the manual fire does not consume a max_occurrences slot.
  RETURN QUERY SELECT TRUE, v_occurrence_id;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_trigger_schedule_now IS
  'Fires an extra occurrence for a non-terminal schedule immediately, without disturbing its cadence '
  '(next_fire_at / last_fire_at / occurrence_count untouched). Logged as run status 3 (TriggeredEarly).';

-- ---------------------------------------------------------------------------
-- wh_update_schedule — reconfigure + recompute next_fire_at, optimistic-concurrency aware.
-- ---------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_update_schedule');

CREATE OR REPLACE FUNCTION __SCHEMA__.wh_update_schedule(
  p_schedule_id UUID,
  p_expected_version BIGINT,
  p_recurrence_kind SMALLINT,
  p_interval_ms BIGINT,
  p_cron TEXT,
  p_timezone TEXT,
  p_start_at TIMESTAMPTZ,
  p_until_at TIMESTAMPTZ,
  p_max_occurrences BIGINT,
  p_misfire_policy SMALLINT,
  p_delivery_guarantee SMALLINT,
  p_event_data JSONB,
  p_scope JSONB,
  p_catch_up_lookback_ms BIGINT DEFAULT NULL
) RETURNS TABLE(o_updated BOOLEAN, o_next_fire_at TIMESTAMPTZ, o_version BIGINT)
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
DECLARE
  v_next TIMESTAMPTZ;
  v_version BIGINT;
  v_stream UUID;
BEGIN
  -- Recompute the next fire from the NEW recurrence config (same rules as create).
  v_next := CASE p_recurrence_kind
    WHEN 0 THEN COALESCE(p_start_at, NOW())
    WHEN 1 THEN COALESCE(p_start_at, NOW() + make_interval(secs => COALESCE(p_interval_ms, 0) / 1000.0))
    WHEN 2 THEN __SCHEMA__.wh_cron_next(p_cron, COALESCE(p_start_at, NOW()), COALESCE(p_timezone, 'UTC'))
    ELSE NULL
  END;
  IF v_next IS NULL THEN
    RAISE EXCEPTION 'Updated schedule has no valid next fire (kind=%, cron=%)', p_recurrence_kind, p_cron;
  END IF;

  UPDATE __SCHEMA__.wh_schedules
  SET recurrence_kind = p_recurrence_kind,
      interval_ms = p_interval_ms,
      cron = p_cron,
      timezone = p_timezone,
      next_fire_at = v_next,
      until_at = p_until_at,
      max_occurrences = p_max_occurrences,
      misfire_policy = COALESCE(p_misfire_policy, misfire_policy),
      delivery_guarantee = COALESCE(p_delivery_guarantee, delivery_guarantee),
      event_data = p_event_data,
      scope = p_scope,
      catch_up_lookback_ms = p_catch_up_lookback_ms,
      version = version + 1
  WHERE schedule_id = p_schedule_id
    AND status IN (0, 1)                                       -- non-terminal only
    AND (p_expected_version IS NULL OR version = p_expected_version)
  RETURNING version, stream_id INTO v_version, v_stream;

  IF FOUND THEN
    -- Arm-on-mutation: the recomputed next_fire_at may be nearer than the owner's armed timer.
    IF v_stream IS NOT NULL THEN
      PERFORM __SCHEMA__.notify_instance_owners('schedule', ARRAY[v_stream]);
    END IF;
    RETURN QUERY SELECT TRUE, v_next, v_version;
  ELSE
    RETURN QUERY SELECT FALSE, NULL::TIMESTAMPTZ, NULL::BIGINT;
  END IF;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_update_schedule IS
  'Reconfigures a non-terminal schedule (recurrence / bounds / payload) and recomputes next_fire_at, '
  'honoring an optional optimistic-concurrency version check. Rings the arm-on-mutation doorbell.';
