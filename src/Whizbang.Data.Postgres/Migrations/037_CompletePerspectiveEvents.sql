-- Migration: 037_CompletePerspectiveEvents.sql
-- Date: 2026-04-13
-- Description: Creates complete_perspective_events function for immediate per-stream completion.
--              Decoupled from process_work_batch tick so completions happen right after processing.
-- Dependencies: 009 (wh_perspective_events table)

SELECT __SCHEMA__.drop_all_overloads('complete_perspective_events');

CREATE OR REPLACE FUNCTION __SCHEMA__.complete_perspective_events(
  p_event_work_ids UUID[],
  p_debug_mode BOOLEAN DEFAULT FALSE
) RETURNS INTEGER AS $$
DECLARE
  v_affected INTEGER;
BEGIN
  IF p_event_work_ids IS NULL OR array_length(p_event_work_ids, 1) IS NULL THEN
    RETURN 0;
  END IF;

  IF p_debug_mode THEN
    -- Debug mode: retain the row for forensics; stamp processed_at so eligible_*
    -- filters skip it on subsequent claims.
    UPDATE wh_perspective_events
    SET processed_at = NOW(),
        instance_id = NULL,
        lease_expiry = NULL
    WHERE event_work_id = ANY(p_event_work_ids)
      AND processed_at IS NULL;
  ELSE
    -- Production: row exits the table on completion — claim_work can never re-issue it.
    DELETE FROM wh_perspective_events
    WHERE event_work_id = ANY(p_event_work_ids);
  END IF;

  GET DIAGNOSTICS v_affected = ROW_COUNT;
  RETURN v_affected;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.complete_perspective_events IS
'Completes wh_perspective_events rows by event_work_id. Production (p_debug_mode=FALSE, default): DELETEs the rows. Debug (p_debug_mode=TRUE): retains rows with processed_at stamped — eligible perspective_events filters processed_at IS NULL so claim_work skips them. Returns rows-affected.';
