-- Migration: 042_FetchPendingPerspectiveEvents.sql
-- Date: 2026-05-02
-- Description: Cheap ID-only prefetch for the perspective drainer (Phase H step 7 slice 1).
--              Returns (event_work_id, event_id) tuples for unprocessed rows leased to the
--              caller, ordered by event_id ASC. The drainer uses this BEFORE pulling event
--              bodies so it can filter against RecentlyProcessedEventCache (cooldown) and
--              the cached cursor (already-applied / inversion-anchor) without paying the
--              body-fetch + JSON-deserialize cost when no actual apply work is needed.
--              This closes the cursor-flush race window where claim_work re-issues a stream
--              before PerspectiveCompletionFlushWorker has DELETEd the wh_perspective_events
--              row (the ~25 ms coalesce window).
-- Dependencies: 009 (wh_perspective_events table)

SELECT __SCHEMA__.drop_all_overloads('fetch_pending_perspective_events');

CREATE OR REPLACE FUNCTION __SCHEMA__.fetch_pending_perspective_events(
  p_stream_id UUID,
  p_perspective_name TEXT,
  p_instance_id UUID
) RETURNS TABLE(
  out_event_work_id UUID,
  out_event_id UUID
) AS $$
BEGIN
  RETURN QUERY
  SELECT
    pe.event_work_id,
    pe.event_id
  FROM wh_perspective_events pe
  WHERE pe.stream_id = p_stream_id
    AND pe.perspective_name = p_perspective_name
    AND pe.instance_id = p_instance_id
    AND pe.processed_at IS NULL
  ORDER BY pe.event_id ASC;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.fetch_pending_perspective_events IS
'Returns (event_work_id, event_id) tuples for unprocessed wh_perspective_events rows leased to the caller, scoped to a single (stream_id, perspective_name) and ordered by event_id ASC. Cheap ID-only prefetch used by the perspective drainer to filter against the in-memory cooldown cache and the cached cursor before deciding whether to pull event bodies. Replaces the in-memory cursor-inversion check from Phase H step 6 slice 4 with a SQL-side filter; closes the cursor-flush race window between drain finish and PerspectiveCompletionFlushWorker DELETE landing.';
