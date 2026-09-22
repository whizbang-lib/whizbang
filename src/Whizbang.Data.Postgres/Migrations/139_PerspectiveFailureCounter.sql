-- Migration: 139_PerspectiveFailureCounter
-- Date: 2026-09-07
-- Description: wh_perspective_events counts failures separately from leases (issue #700).
--
--   attempts counts dispatch starts: claim_work, claim_orphaned_perspective_events and the drain's
--   own get_stream_events each bump it when they lease the row. That is the right diagnostic for
--   "how many times has this row been handed to a worker" and the wrong input for the dead-letter
--   decision. A row whose lease lapses without an apply (the worker skipped it, died mid-batch, or
--   classified it as recently processed) is re-claimed and bumped again, so under a sustained
--   backlog a perfectly good event crosses MaxPerspectiveEventAttempts without one apply ever
--   failing and is dead-lettered as a thrash casualty. Observed at scale: hundreds of good events
--   across twenty projections in under two minutes once the lease churn started.
--
--   Fix: a `failures` column that ONLY process_perspective_event_failures moves. The retry backoff
--   escalates on failures (a lease lapse is a scheduling event, not evidence of poison), and
--   get_stream_events surfaces the counter so the drain's pre-apply dead-letter check reads it.
--   attempts is left exactly as it was (see 025/ClaimOrphanedAttemptsIncrementSqlTests): the
--   claim paths keep counting dispatch starts, and the reactive orphan disposal (#679) keeps
--   keying on it, because an orphan never reaches an apply and lease count is the only signal.
--
-- Dependencies: 019 (process_perspective_event_failures), 078 (get_stream_events current text)
-- Objects: wh_perspective_events.failures, process_perspective_event_failures, get_stream_events

ALTER TABLE __SCHEMA__.wh_perspective_events
  ADD COLUMN IF NOT EXISTS failures INTEGER NOT NULL DEFAULT 0;

COMMENT ON COLUMN __SCHEMA__.wh_perspective_events.failures IS
  'Apply failures recorded by process_perspective_event_failures. The dead-letter decision reads '
  'this, never attempts: attempts counts leases (dispatch starts) and a lease can lapse without '
  'an apply, which is scheduling, not poison.';

-- ============================================================================
-- process_perspective_event_failures — reproduced verbatim from 019 with two changes: the failure
-- counter is bumped, and the backoff escalates on failures instead of attempts.
-- ============================================================================
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
      (elem->>'EventWorkId')::UUID as work_id,
      (elem->>'CompletedStatus')::INTEGER as status_flags,
      elem->>'Error' as error_message,
      (elem->>'FailureReason')::INTEGER as failure_reason
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
  'the retry with a backoff that escalates on failures, and releases the lease.';

-- ============================================================================
-- get_stream_events — reproduced verbatim from 078 with one change: out_failures is surfaced.
-- The RETURNS TABLE shape changed, so the prior overloads are dropped first (rule 5).
-- ============================================================================
SELECT __SCHEMA__.drop_all_overloads('get_stream_events');

CREATE OR REPLACE FUNCTION __SCHEMA__.get_stream_events(
  p_instance_id UUID,
  p_stream_ids UUID[],
  p_now TIMESTAMPTZ DEFAULT NOW(),
  p_lease_seconds INTEGER DEFAULT 300
) RETURNS TABLE(
  out_stream_id UUID,
  out_event_id UUID,
  out_event_type TEXT,
  out_event_data TEXT,
  out_metadata TEXT,
  out_scope TEXT,
  out_event_work_id UUID,
  out_perspective_name VARCHAR(200),
  out_commit_sequence BIGINT,
  out_attempts INTEGER,
  out_failures INTEGER
) AS $$
DECLARE
  v_lease_expiry TIMESTAMPTZ;
  v_stamp_grace CONSTANT INTERVAL := INTERVAL '5 seconds';
  v_stamp_cutoff TIMESTAMPTZ;
BEGIN
  v_lease_expiry := p_now + (p_lease_seconds || ' seconds')::INTERVAL;
  v_stamp_cutoff := p_now - v_stamp_grace;

  -- Atomic claim+fetch (slice 25) + grace-windowed unstamped gate (mig 058) + live-owner gate
  -- (mig 059). A row is claimable only if its stream is NOT owned by a DIFFERENT live instance —
  -- enforcing single-writer-per-stream so two pods never apply one stream concurrently.
  WITH eligible AS (
    SELECT pe.event_work_id, pe.instance_id, pe.attempts
    FROM __SCHEMA__.wh_perspective_events pe
    INNER JOIN __SCHEMA__.wh_event_store es
      ON es.stream_id = pe.stream_id
      AND es.event_id = pe.event_id
    WHERE pe.stream_id = ANY(p_stream_ids)
      AND pe.processed_at IS NULL
      AND (pe.scheduled_for IS NULL OR pe.scheduled_for <= p_now)
      AND (
        pe.instance_id IS NULL
        OR pe.lease_expiry < p_now
      )
      AND (es.commit_sequence IS NOT NULL OR pe.created_at <= v_stamp_cutoff)
      -- mig 059: single-writer gate. Do not claim a row whose stream is owned by a different
      -- LIVE instance (mirrors claim_orphaned's liveness: a wh_service_instances row exists, or a
      -- live LISTEN connection in pg_stat_activity). Caller-owned / unowned / dead-owner streams
      -- stay claimable.
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_active_streams ast
        WHERE ast.stream_id = pe.stream_id
          AND ast.assigned_instance_id IS NOT NULL
          AND ast.assigned_instance_id <> p_instance_id
          AND (
            EXISTS (
              SELECT 1 FROM __SCHEMA__.wh_service_instances si
              WHERE si.instance_id = ast.assigned_instance_id
            )
            OR EXISTS (
              SELECT 1 FROM pg_stat_activity sa
              WHERE sa.application_name = 'whizbang-' || ast.assigned_instance_id::text
            )
          )
      )
    ORDER BY pe.event_work_id
    FOR UPDATE OF pe SKIP LOCKED
  )
  UPDATE __SCHEMA__.wh_perspective_events pe
  SET instance_id = p_instance_id,
      lease_expiry = v_lease_expiry,
      attempts = pe.attempts + 1
  FROM eligible e
  WHERE pe.event_work_id = e.event_work_id;

  RETURN QUERY
  SELECT
    pe.stream_id,
    es.event_id,
    es.event_type::TEXT,
    eb.event_data::TEXT,
    eb.metadata::TEXT,
    es.scope::TEXT,
    pe.event_work_id,
    pe.perspective_name,
    es.commit_sequence,
    pe.attempts,
    pe.failures
  FROM __SCHEMA__.wh_perspective_events pe
  INNER JOIN __SCHEMA__.wh_event_store es
    ON pe.stream_id = es.stream_id
    AND pe.event_id = es.event_id
  -- Migration 072: COALESCE the offloaded ephemeral body back in. Sourced events have a non-NULL
  -- inline body so the join contributes nothing; ephemeral events read their body from wh_event_body.
  LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = es.event_id
  WHERE pe.instance_id = p_instance_id
    AND pe.lease_expiry > p_now
    AND pe.processed_at IS NULL
    AND pe.stream_id = ANY(p_stream_ids)
    AND (es.commit_sequence IS NOT NULL OR pe.created_at <= v_stamp_cutoff)
  ORDER BY pe.stream_id, es.commit_sequence ASC NULLS LAST, es.event_id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.get_stream_events(UUID, UUID[], TIMESTAMPTZ, INTEGER) IS
  'Atomic claim+fetch of pending perspective work for the given streams (slice 25): claims unleased '
  'or lapsed rows for the instance (bumping attempts), gates unstamped rows behind a grace window '
  '(058) and streams owned by another live instance (059), then returns the rows leased to the '
  'instance with their event bodies, commit sequence, attempts and failures (139).';
