-- Migration: 038_GetStreamEvents.sql
-- Date: 2026-04-13 (updated 2026-04-14 to include metadata + scope for envelope reconstruction)
-- Description: Creates get_stream_events function for batch-fetching events for multiple streams.
--              Called by PerspectiveWorker AFTER process_work_batch returns stream IDs.
--              Returns denormalized rows: one row per (stream, event) with event_work_id for completion.
--              Includes metadata (JSONB) and scope (JSONB) for full envelope reconstruction.
--              C# determines which perspectives apply from event_type using its registry.
-- Dependencies: 001-037 (requires wh_perspective_events, wh_event_store tables)

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
  out_perspective_name VARCHAR(200)
) AS $$
DECLARE
  v_lease_expiry TIMESTAMPTZ;
BEGIN
  v_lease_expiry := p_now + (p_lease_seconds || ' seconds')::INTERVAL;

  -- Slice 25: atomic claim+fetch. Before reading, claim/re-lease every eligible row
  -- for the requested streams to p_instance_id. This closes the race where a row
  -- existed in wh_perspective_events but was orphaned (instance_id NULL) or had an
  -- expired lease at the moment of the SELECT — invisible to the original
  -- instance_id-filtered query, so the perspective worker advanced its cursor past
  -- it, only to find it later (now behind cursor) and trigger a rewind. Investigated
  -- a consumer run 6: BulkImportOrchestration / EmbeddingSaga streams produced
  -- 38-second-delta inversions because batch N's fetch missed rows that
  -- claim_orphaned_perspective_events only claimed between batch N and batch N+1.
  --
  -- Eligibility for claim:
  --   * unprocessed
  --   * scheduled_for is past (or null)
  --   * AND (orphaned, OR expired lease, OR already leased to us)
  --
  -- attempts bumps only on lease takeover (instance_id changes) to match
  -- claim_orphaned_perspective_events accounting; same-instance re-lease does
  -- not inflate the counter.
  UPDATE wh_perspective_events pe
  SET instance_id = p_instance_id,
      lease_expiry = v_lease_expiry,
      attempts = CASE
        WHEN pe.instance_id IS DISTINCT FROM p_instance_id THEN pe.attempts + 1
        ELSE pe.attempts
      END
  WHERE pe.stream_id = ANY(p_stream_ids)
    AND pe.processed_at IS NULL
    AND (pe.scheduled_for IS NULL OR pe.scheduled_for <= p_now)
    AND (
      pe.instance_id IS NULL
      OR pe.lease_expiry < p_now
      OR pe.instance_id = p_instance_id
    );

  -- Now read all rows leased to us. Same MVCC snapshot as the UPDATE above —
  -- PL/pgSQL functions execute in one transaction, so our writes are visible
  -- to the subsequent SELECT.
  RETURN QUERY
  SELECT
    pe.stream_id,
    es.event_id,
    es.event_type::TEXT,
    es.event_data::TEXT,
    es.metadata::TEXT,
    es.scope::TEXT,
    pe.event_work_id,
    pe.perspective_name
  FROM wh_perspective_events pe
  INNER JOIN wh_event_store es
    ON pe.stream_id = es.stream_id
    AND pe.event_id = es.event_id
  WHERE pe.instance_id = p_instance_id
    AND pe.lease_expiry > p_now
    AND pe.processed_at IS NULL
    AND pe.stream_id = ANY(p_stream_ids)
  ORDER BY pe.stream_id, es.event_id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.get_stream_events IS
'Slice 25 — atomic per-stream claim+fetch. Before returning, claims (or re-leases) every eligible wh_perspective_events row for the requested streams to p_instance_id (orphans, expired leases, and already-ours rows all consolidated under our lease). Then returns denormalized rows joining wh_perspective_events with wh_event_store. Caller invariant: after this returns, no unprocessed rows for the requested streams exist unleased to anyone else. Eliminates the cursor-advances-past-orphaned-rows race that produced residual inversions after slices 23 + 24c.';
