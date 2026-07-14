-- Migration: 068_ClaimDueSchedules.sql
-- Date: 2026-07-13 (F2 temporal engine — increment 4a: authoritative claim+advance)
-- Description: The temporal engine's authoritative fire. For each due Active schedule owned by the
--              calling instance, in ONE transaction:
--                1. lease the row (FOR UPDATE SKIP LOCKED) so no two instances double-fire;
--                2. spawn the occurrence event into the outbox via store_outbox_messages (the exact
--                   same emit contract as any other event — deterministic occurrence-id so a
--                   re-attempt of the same fire dedups at the outbox);
--                3. compute the next fire via wh_schedule_next_fire and apply until_at / max_occurrences
--                   bounds (a null/exceeded next completes the schedule: status 2);
--                4. advance the row (last_fire_at, next_fire_at, occurrence_count, version);
--                5. write a wh_schedule_runs row (status 0 = spawned/Success).
--              Occurrence creation is therefore exactly-once even under concurrent claimers: the whole
--              unit is atomic, and SKIP LOCKED serialises which instance owns each due row.
-- Dependencies: 066 (wh_schedules/wh_schedule_runs), 067 (wh_schedule_next_fire), 062 (store_outbox_messages).
-- Schedule status: 0=Active, 1=Paused, 2=Completed (bounds reached / one-shot fired).

-- ---------------------------------------------------------------------------
-- _wh_spawn_occurrence — the ONE place an occurrence is emitted. Shared by the scheduled fire
-- (wh_claim_due_schedules, run status 0) and the manual fire (wh_trigger_schedule_now, run status 3)
-- so both use the identical outbox/event-store emit contract and both land a wh_schedule_runs row.
-- ---------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('_wh_spawn_occurrence');

CREATE OR REPLACE FUNCTION __SCHEMA__._wh_spawn_occurrence(
  p_schedule_id UUID,
  p_occurrence_id UUID,
  p_occurrence_number BIGINT,
  p_event_type TEXT,
  p_event_data JSONB,
  p_scope JSONB,
  p_stream_id UUID,
  p_delivery_guarantee SMALLINT,
  p_instance_id UUID,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_partition_count INTEGER,
  p_run_status SMALLINT
) RETURNS VOID
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
DECLARE
  v_message JSONB;
BEGIN
  v_message := jsonb_build_array(jsonb_build_object(
    'MessageId', p_occurrence_id,
    'MessageType', p_event_type,
    -- Envelope carries the event under 'Payload' (what _emit_event_store_chain extracts into
    -- wh_event_store.event_data), plus the message id + empty hop list.
    'Envelope', jsonb_build_object(
      'Payload', COALESCE(p_event_data, '{}'::jsonb),
      'id', p_occurrence_id,
      'hops', '[]'::jsonb),
    'Metadata', jsonb_build_object(
      'scheduleId', p_schedule_id,
      'occurrence', p_occurrence_number,
      'deliveryGuarantee', p_delivery_guarantee),
    'Scope', COALESCE(p_scope, 'null'::jsonb),
    'StreamId', p_stream_id,
    'IsEvent', true,
    'Flags', 0
  ));
  PERFORM __SCHEMA__.store_outbox_messages(
    v_message, p_instance_id, p_lease_expiry, p_now, p_partition_count);

  INSERT INTO __SCHEMA__.wh_schedule_runs
    (schedule_id, occurrence_id, fired_at, status, instance_id)
  VALUES (p_schedule_id, p_occurrence_id, p_now, p_run_status, p_instance_id);
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_spawn_occurrence IS
  'Emits one schedule occurrence through the normal outbox/event-store chain and logs a wh_schedule_runs '
  'row. Shared by the scheduled fire (status 0) and trigger-now (status 3 TriggeredEarly).';

-- ---------------------------------------------------------------------------
-- _wh_advance_past — the first fire STRICTLY AFTER p_now, starting from p_from. O(1): interval jumps
-- by whole periods (never loops, so a schedule down for a year doesn't iterate a million times) and cron
-- simply asks for the next match after p_now. This is what makes Coalesce/Skip fast-forward cheap.
-- ---------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('_wh_advance_past');

CREATE OR REPLACE FUNCTION __SCHEMA__._wh_advance_past(
  p_kind SMALLINT,
  p_cron TEXT,
  p_interval_ms BIGINT,
  p_tz TEXT,
  p_from TIMESTAMPTZ,
  p_now TIMESTAMPTZ
) RETURNS TIMESTAMPTZ
LANGUAGE plpgsql
STABLE
SET timezone = 'UTC'
AS $$
DECLARE
  v_periods BIGINT;
BEGIN
  IF p_kind = 1 THEN
    IF p_interval_ms IS NULL OR p_interval_ms <= 0 THEN
      RETURN NULL;
    END IF;
    -- Whole periods elapsed since p_from, plus one => first period strictly after p_now.
    v_periods := floor(EXTRACT(EPOCH FROM (p_now - p_from)) * 1000.0 / p_interval_ms)::bigint + 1;
    IF v_periods < 1 THEN
      v_periods := 1;
    END IF;
    RETURN p_from + make_interval(secs => (v_periods * p_interval_ms) / 1000.0);
  ELSIF p_kind = 2 THEN
    RETURN __SCHEMA__.wh_cron_next(p_cron, p_now, COALESCE(p_tz, 'UTC'));
  END IF;
  RETURN NULL;   -- one-shot has no next fire
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_advance_past IS
  'First fire strictly after p_now (O(1) fast-forward). Used by the misfire policies to skip a missed '
  'window without iterating every missed occurrence.';

SELECT __SCHEMA__.drop_all_overloads('wh_claim_due_schedules');

CREATE OR REPLACE FUNCTION __SCHEMA__.wh_claim_due_schedules(
  p_instance_id UUID,
  p_lease_expiry TIMESTAMPTZ,
  p_partition_count INTEGER,
  p_limit INTEGER DEFAULT 100,
  p_now TIMESTAMPTZ DEFAULT NULL
) RETURNS TABLE(
  o_schedule_id UUID,
  o_occurrence_id UUID,
  o_fired_at TIMESTAMPTZ,
  o_next_fire_at TIMESTAMPTZ,
  o_completed BOOLEAN
)
SET timezone = 'UTC'   -- function-local: due comparison + spawn run in UTC regardless of caller session
AS $$
DECLARE
  v_sched RECORD;
  v_occurrence_id UUID;
  v_next TIMESTAMPTZ;
  v_completed BOOLEAN;
  v_cursor TIMESTAMPTZ;
  v_horizon TIMESTAMPTZ;
  v_skipped BOOLEAN;
  -- DB clock is authority for the due decision (avoids multi-instance skew). p_now is a test seam.
  v_now TIMESTAMPTZ := COALESCE(p_now, NOW());
BEGIN
  FOR v_sched IN
    SELECT sc.*
    FROM __SCHEMA__.wh_schedules sc
    JOIN __SCHEMA__.wh_active_streams s ON s.stream_id = sc.stream_id
    WHERE s.assigned_instance_id = p_instance_id
      AND sc.status = 0
      AND sc.next_fire_at <= v_now
    ORDER BY sc.next_fire_at
    LIMIT p_limit
    FOR UPDATE OF sc SKIP LOCKED
  LOOP
    -- Deterministic occurrence id (schedule + occurrence#) so a redelivered fire dedups at the outbox.
    v_occurrence_id := md5(v_sched.schedule_id::text || ':' || v_sched.occurrence_count::text)::uuid;
    v_cursor := v_sched.next_fire_at;
    v_skipped := FALSE;

    -- CatchUp burst bound: never replay occurrences older than the lookback horizon. Jump the cursor
    -- forward to the horizon first so a long outage can't unleash an unbounded replay storm.
    IF v_sched.misfire_policy = 1 AND v_sched.catch_up_lookback_ms IS NOT NULL THEN
      v_horizon := v_now - make_interval(secs => v_sched.catch_up_lookback_ms / 1000.0);
      IF v_cursor < v_horizon THEN
        v_cursor := COALESCE(__SCHEMA__._wh_advance_past(
          v_sched.recurrence_kind, v_sched.cron, v_sched.interval_ms, v_sched.timezone,
          v_cursor, v_horizon), v_cursor);
      END IF;
    END IF;

    -- One-step next fire from the cursor.
    v_next := __SCHEMA__.wh_schedule_next_fire(
      v_sched.recurrence_kind, v_sched.cron, v_sched.interval_ms, v_sched.timezone, v_cursor);

    -- MISFIRE: the very next fire is ALSO already due => more than one fire was missed (paused a long
    -- time / owner was down). Without this, an interval schedule would advance one step per claim and
    -- grind through every missed occurrence.
    IF v_next IS NOT NULL AND v_next <= v_now THEN
      IF v_sched.misfire_policy = 0 THEN
        -- Coalesce (default): fire ONCE now, then fast-forward past the whole missed window.
        v_next := __SCHEMA__._wh_advance_past(
          v_sched.recurrence_kind, v_sched.cron, v_sched.interval_ms, v_sched.timezone, v_cursor, v_now);
      ELSIF v_sched.misfire_policy = 2 THEN
        -- Skip: do NOT fire for the missed window at all; just fast-forward back onto cadence.
        v_skipped := TRUE;
        v_next := __SCHEMA__._wh_advance_past(
          v_sched.recurrence_kind, v_sched.cron, v_sched.interval_ms, v_sched.timezone, v_cursor, v_now);
      END IF;
      -- CatchUp (1): keep the one-step next => replay each missed occurrence, one per claim
      -- (bounded by catch_up_lookback_ms above).
    END IF;

    -- Bounds. A skipped fire doesn't consume a max_occurrences slot.
    IF v_next IS NOT NULL AND v_sched.until_at IS NOT NULL AND v_next > v_sched.until_at THEN
      v_next := NULL;
    END IF;
    IF NOT v_skipped AND v_next IS NOT NULL AND v_sched.max_occurrences IS NOT NULL
       AND (v_sched.occurrence_count + 1) >= v_sched.max_occurrences THEN
      v_next := NULL;
    END IF;
    v_completed := (v_next IS NULL);

    IF v_skipped THEN
      -- No occurrence spawned — record the misfire so the skip stays auditable.
      INSERT INTO __SCHEMA__.wh_schedule_runs
        (schedule_id, occurrence_id, fired_at, status, instance_id)
      VALUES (v_sched.schedule_id, v_occurrence_id, v_now, 2, p_instance_id);   -- 2 = Skipped (misfire)
    ELSE
      -- Spawn the occurrence transactionally through the shared emit (run status 0 = fired).
      PERFORM __SCHEMA__._wh_spawn_occurrence(
        v_sched.schedule_id, v_occurrence_id, v_sched.occurrence_count,
        v_sched.event_type, v_sched.event_data, v_sched.scope, v_sched.stream_id,
        v_sched.delivery_guarantee, p_instance_id, p_lease_expiry, v_now, p_partition_count,
        0::SMALLINT);
    END IF;

    -- Advance the schedule. Completed schedules keep their (past) next_fire_at — status 2 excludes
    -- them from the due-detect/notify queries so they never re-fire, satisfying the NOT NULL column.
    UPDATE __SCHEMA__.wh_schedules
    SET last_fire_at = CASE WHEN v_skipped THEN last_fire_at ELSE v_now END,
        next_fire_at = COALESCE(v_next, next_fire_at),
        occurrence_count = occurrence_count + CASE WHEN v_skipped THEN 0 ELSE 1 END,
        status = CASE WHEN v_completed THEN 2 ELSE status END,
        version = version + 1
    WHERE schedule_id = v_sched.schedule_id;

    o_schedule_id := v_sched.schedule_id;
    o_occurrence_id := v_occurrence_id;
    o_fired_at := v_now;
    o_next_fire_at := v_next;
    o_completed := v_completed;
    RETURN NEXT;
  END LOOP;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_claim_due_schedules IS
  'Temporal engine authoritative fire: leases due Active owned schedules (SKIP LOCKED), spawns each '
  'occurrence into the outbox, advances next_fire_at (wh_schedule_next_fire + until/max bounds; '
  'completion => status 2), and logs a wh_schedule_runs row — all atomically (exactly-once creation).';
