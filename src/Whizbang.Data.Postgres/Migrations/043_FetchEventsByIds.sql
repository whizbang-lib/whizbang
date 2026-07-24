-- Migration: 043_FetchEventsByIds.sql
-- Date: 2026-05-02
-- Description: Scoped event-body fetch from wh_event_store by event_id list (Phase H step 7
--              slice 4). Replaces get_stream_events' per-stream JOIN with a precise lookup so
--              the perspective drainer fetches bodies only for events that survived the
--              cooldown + cursor + inversion filters. Drainer pairs the result back to its
--              prefetched (event_work_id, event_id) tuples in C#.
-- Dependencies: wh_event_store table (created in mig 029 hot path, schema established earlier)

SELECT __SCHEMA__.drop_all_overloads('fetch_events_by_ids');

CREATE OR REPLACE FUNCTION __SCHEMA__.fetch_events_by_ids(
  p_event_ids UUID[]
) RETURNS TABLE(
  out_stream_id UUID,
  out_event_id UUID,
  out_event_type TEXT,
  out_event_data TEXT,
  out_metadata TEXT,
  out_scope TEXT
) AS $$
BEGIN
  RETURN QUERY
  SELECT
    es.stream_id,
    es.event_id,
    es.event_type::TEXT,
    es.event_data::TEXT,
    es.metadata::TEXT,
    es.scope::TEXT
  FROM __SCHEMA__.wh_event_store es
  WHERE es.event_id = ANY(p_event_ids)
  ORDER BY es.event_id ASC;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.fetch_events_by_ids IS
'Returns event bodies from wh_event_store for the given event_id list, ordered by event_id ASC. The perspective drainer calls this AFTER the cooldown + cursor + inversion filters reduce the prefetched (event_work_id, event_id) tuples to only those needing apply. event_work_id is intentionally NOT returned — drainer pairs results back to its prefetch tuples by event_id in C#. Replaces the per-stream JOIN in get_stream_events for the precise-lookup case.';
