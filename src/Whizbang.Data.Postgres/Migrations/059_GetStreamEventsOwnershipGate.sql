-- Migration: 059_GetStreamEventsOwnershipGate.sql
-- Date: 2026-06-21
-- Description: Enforce single-writer-per-stream in the live drain claim.
--              get_stream_events claimed perspective_events rows purely on the ROW lease
--              (instance_id IS NULL OR lease_expiry < now) and never consulted STREAM ownership.
--              So when a stream's owning instance was merely slow/throttled and a row's lease
--              lapsed (e.g. under stamper/ASB backpressure during a 350-item bulk import), a
--              DIFFERENT live instance re-claimed that row and applied it concurrently — two
--              writers on one stream. Combined with the unconditional perspective upsert, the
--              staler write reverted a per-item row from Completed to Running and stranded
--              production saga 019ee73d (2026-06-20).
--
--              This gates the claim CTE with the same live-owner check claim_orphaned uses
--              (027): a row whose stream is owned by a different instance that is alive (its
--              wh_service_instances row exists — cleanup_stale_instances removes dead ones — or
--              it has a live Whizbang LISTEN connection in pg_stat_activity) is NOT claimable.
--              Streams owned by the caller, unowned streams, and streams whose owner is dead
--              remain claimable, so a real owner death still fails over cleanly.
--
--              Preserves the mig-058 grace-windowed unstamped-row gate.
-- Dependencies: 058 (get_stream_events grace gate), 007 (wh_active_streams), 011 (wh_service_instances)

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
  out_attempts INTEGER
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
    es.event_data::TEXT,
    es.metadata::TEXT,
    es.scope::TEXT,
    pe.event_work_id,
    pe.perspective_name,
    es.commit_sequence,
    pe.attempts
  FROM __SCHEMA__.wh_perspective_events pe
  INNER JOIN __SCHEMA__.wh_event_store es
    ON pe.stream_id = es.stream_id
    AND pe.event_id = es.event_id
  WHERE pe.instance_id = p_instance_id
    AND pe.lease_expiry > p_now
    AND pe.processed_at IS NULL
    AND pe.stream_id = ANY(p_stream_ids)
    AND (es.commit_sequence IS NOT NULL OR pe.created_at <= v_stamp_cutoff)
  ORDER BY pe.stream_id, es.commit_sequence ASC NULLS LAST, es.event_id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.get_stream_events IS
'Mig 059 — atomic per-stream claim+fetch with the grace-windowed unstamped gate (058) AND a single-writer ownership gate: a row whose stream is owned by a different live instance is not claimable, so two pods never apply one stream concurrently (the cross-pod lost-update that stranded production saga 019ee73d). Caller-owned / unowned / dead-owner streams stay claimable for clean failover. Supersedes mig 058.';
