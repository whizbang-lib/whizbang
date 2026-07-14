-- Migration: 066_TemporalSchedules.sql
-- Date: 2026-07-13 (F2 Temporal Engine — durable schedules + run log + due-notify)
-- Description: First-class scheduled + recurring events (and saga deadlines). Adds:
--                wh_schedules      — durable schedule definitions (one-shot / interval / cron)
--                wh_schedule_runs  — per-fire run/failure history (status, duration, stacktrace)
--                notify_schedules_due() — emits 'schedule' wake NOTIFYs to owning instances for
--                                         Active schedules whose next_fire_at has elapsed
--                                         (mirrors notify_scheduled_retry_due).
-- Dependencies: requires wh_active_streams + notify_instance_owners (migs 007/045).

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_schedules (
  schedule_id          UUID PRIMARY KEY,
  schedule_key         TEXT,                         -- optional developer key (idempotent create-or-update)
  stream_id            UUID,                         -- ownership / partition routing (NULL = poll-only, no NOTIFY)
  partition_number     INTEGER NOT NULL DEFAULT 0,
  recurrence_kind      SMALLINT NOT NULL DEFAULT 0,  -- 0=OneShot | 1=Interval | 2=Cron
  interval_ms          BIGINT,                       -- Interval
  cron                 TEXT,                         -- Cron expression
  timezone             TEXT,                         -- Cron timezone
  next_fire_at         TIMESTAMPTZ NOT NULL,
  last_fire_at         TIMESTAMPTZ,
  until_at             TIMESTAMPTZ,                  -- bound (optional)
  max_occurrences      BIGINT,                       -- bound (optional)
  occurrence_count     BIGINT NOT NULL DEFAULT 0,
  misfire_policy       SMALLINT NOT NULL DEFAULT 0,  -- 0=Coalesce | 1=CatchUp | 2=Skip
  catch_up_shape       SMALLINT NOT NULL DEFAULT 0,  -- reserved (misfire_policy is the live dial)
  catch_up_lookback_ms BIGINT,                       -- CatchUp burst bound: don't replay older than this (NULL = unbounded)
  delivery_guarantee   SMALLINT NOT NULL DEFAULT 0,  -- 0=AtLeastOnce | 1=AtMostOnce
  status               SMALLINT NOT NULL DEFAULT 0,  -- 0=Active | 1=Paused
  event_type           TEXT NOT NULL,                -- occurrence event to spawn
  event_data           JSONB,
  scope                JSONB,                        -- PerspectiveScope (tenant/user)
  version              BIGINT NOT NULL DEFAULT 0,    -- optimistic concurrency for updates
  created_at           TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Due-poll hot path: only Active schedules, ordered by next_fire_at. Partial index keeps it small.
CREATE INDEX IF NOT EXISTS idx_wh_schedules_due
  ON __SCHEMA__.wh_schedules (next_fire_at)
  WHERE status = 0;

-- Developer-key lookup (idempotent create-or-update / management by key).
CREATE INDEX IF NOT EXISTS idx_wh_schedules_key
  ON __SCHEMA__.wh_schedules (schedule_key)
  WHERE schedule_key IS NOT NULL;

COMMENT ON TABLE __SCHEMA__.wh_schedules IS
  'Durable schedule definitions for the temporal engine (one-shot / interval / cron, + saga deadlines). Each due schedule spawns an occurrence event; next_fire_at is advanced atomically with the spawn. status: 0=Active, 1=Paused.';

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_schedule_runs (
  run_id           BIGSERIAL PRIMARY KEY,
  schedule_id      UUID NOT NULL,
  occurrence_id    UUID NOT NULL,
  fired_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  status           SMALLINT NOT NULL,                -- 0=Success | 1=Failed | 2=Skipped(misfire) | 3=TriggeredEarly
  duration_ms      INTEGER,
  error_message    TEXT,
  error_stacktrace TEXT,
  instance_id      UUID
);

CREATE INDEX IF NOT EXISTS idx_wh_schedule_runs_by_schedule
  ON __SCHEMA__.wh_schedule_runs (schedule_id, fired_at DESC);

COMMENT ON TABLE __SCHEMA__.wh_schedule_runs IS
  'Per-fire run/failure history for the temporal engine: outcome, duration, occurrence-id, firing instance, and error message + stack trace on failure. Makes at-most-once delivery safe (non-retried failures land here) and feeds OTel/ops.';

-- ----------------------------------------------------------------------------
-- notify_schedules_due(): mirror of notify_scheduled_retry_due for wh_schedules.
-- Emits one pg_notify('wh_work_i_<owner>', 'schedule') per owning live instance for
-- Active schedules whose next_fire_at has elapsed. Stream-less schedules (stream_id
-- NULL) have no NOTIFY target and are discovered by the schedule pull-source backstop.
-- All table refs are __SCHEMA__-qualified (multi-schema hosts resolve via search_path
-- to public otherwise; see mig 049's note).
-- ----------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('notify_schedules_due');

CREATE OR REPLACE FUNCTION __SCHEMA__.notify_schedules_due()
RETURNS INTEGER AS $$
DECLARE
  v_streams UUID[];
  v_now TIMESTAMPTZ := NOW();
BEGIN
  SELECT ARRAY_AGG(DISTINCT stream_id) INTO v_streams
  FROM __SCHEMA__.wh_schedules
  WHERE status = 0
    AND next_fire_at <= v_now
    AND stream_id IS NOT NULL;

  IF v_streams IS NOT NULL AND array_length(v_streams, 1) > 0 THEN
    PERFORM __SCHEMA__.notify_instance_owners('schedule', v_streams);
    RETURN array_length(v_streams, 1);
  END IF;

  RETURN 0;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.notify_schedules_due IS
'Emits schedule-wake NOTIFYs (payload ''schedule'') for streams owning Active schedules whose next_fire_at has elapsed, one per owning instance. Called periodically by the temporal backup tick; stream-less schedules ride the pull-source backstop. Returns the count of distinct streams woken.';
