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
  p_now TIMESTAMPTZ DEFAULT NOW()
) RETURNS TABLE(
  out_stream_id UUID,
  out_event_id UUID,
  out_event_type TEXT,
  out_event_data TEXT,
  out_metadata TEXT,
  out_scope TEXT,
  out_event_work_id UUID
) AS $$
BEGIN
  RETURN QUERY
  SELECT
    pe.stream_id,
    es.event_id,
    es.event_type::TEXT,
    es.event_data::TEXT,
    es.metadata::TEXT,
    es.scope::TEXT,
    pe.event_work_id
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
'Batch-fetches events for multiple streams in a single call. Returns denormalized rows joining wh_perspective_events (work queue) with wh_event_store (actual event data). Includes metadata and scope columns for full envelope reconstruction with tenant context and tracing. Only returns events leased to the requesting instance. C# determines which perspectives apply from event_type. event_work_id is used for completion reporting via complete_perspective_events.';
