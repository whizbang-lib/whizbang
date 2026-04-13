-- Migration: 037_CompletePerspectiveEvents.sql
-- Date: 2026-04-13
-- Description: Creates complete_perspective_events function for immediate per-stream completion.
--              Decoupled from process_work_batch tick so completions happen right after processing.
-- Dependencies: 009 (wh_perspective_events table)

SELECT __SCHEMA__.drop_all_overloads('complete_perspective_events');

CREATE OR REPLACE FUNCTION __SCHEMA__.complete_perspective_events(
  p_event_work_ids UUID[]
) RETURNS INTEGER AS $$
DECLARE
  v_deleted INTEGER;
BEGIN
  DELETE FROM wh_perspective_events
  WHERE event_work_id = ANY(p_event_work_ids);

  GET DIAGNOSTICS v_deleted = ROW_COUNT;
  RETURN v_deleted;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.complete_perspective_events IS
'Deletes completed wh_perspective_events rows by work_item_id. Called per-stream after PerspectiveWorker finishes processing, decoupled from the process_work_batch tick. Returns count of deleted rows.';
