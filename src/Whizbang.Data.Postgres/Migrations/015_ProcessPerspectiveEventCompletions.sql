-- Migration: 015_ProcessPerspectiveEventCompletions.sql
-- Date: 2025-12-25
-- Description: Creates process_perspective_event_completions function for marking perspective events as complete.
--              Returns stream/perspective pairs for checkpoint updates. Supports debug mode retention.
-- Dependencies: 001-014 (requires wh_perspective_events table from migration 009)

SELECT __SCHEMA__.drop_all_overloads('process_perspective_event_completions');

CREATE OR REPLACE FUNCTION __SCHEMA__.process_perspective_event_completions(
  p_completions JSONB,
  p_now TIMESTAMPTZ,
  p_debug_mode BOOLEAN DEFAULT FALSE
) RETURNS TABLE(
  event_work_id UUID,
  stream_id UUID,
  perspective_name VARCHAR(200),
  was_deleted BOOLEAN
) AS $$
BEGIN
  IF jsonb_array_length(p_completions) = 0 THEN RETURN; END IF;

  -- v0.671: collapse the prior FOR-loop (two statements per completion — a
  -- SELECT to fetch stream_id/perspective_name then a DELETE/UPDATE) into a
  -- single set-based statement with RETURNING. For a batch of N completions,
  -- the loop pattern issued 2N round trips through the planner; the bulk
  -- pattern is one. Production during-import data (PR #252 cycle) showed this
  -- sub-function as one of the structural contributors to
  -- CompletePerspectiveAsync avg 122 ms.
  --
  -- Not-found completions are naturally filtered: the USING/jsonb_array_elements
  -- join only matches IDs that exist in wh_perspective_events, so silently-
  -- skipped IDs (already deleted or never existed) don't appear in RETURNING.
  IF p_debug_mode THEN
    -- Debug mode: retain rows, stamp status | status_flags + processed_at,
    -- clear lease columns. RETURNING gives the (work_id, stream_id, name)
    -- triple per matched row.
    RETURN QUERY
    WITH completions AS (
      SELECT
        (elem->>'EventWorkId')::UUID AS work_id,
        (elem->>'StatusFlags')::INTEGER AS status_flags
      FROM jsonb_array_elements(p_completions) AS elem
    )
    UPDATE __SCHEMA__.wh_perspective_events pe
    SET status = pe.status | c.status_flags,
        processed_at = p_now,
        instance_id = NULL,
        lease_expiry = NULL
    FROM completions c
    WHERE pe.event_work_id = c.work_id
    RETURNING pe.event_work_id, pe.stream_id, pe.perspective_name, FALSE AS was_deleted;
  ELSE
    -- Production: DELETE matched rows, returning identity for cursor advancement.
    RETURN QUERY
    WITH completions AS (
      SELECT (elem->>'EventWorkId')::UUID AS work_id
      FROM jsonb_array_elements(p_completions) AS elem
    )
    DELETE FROM __SCHEMA__.wh_perspective_events pe
    USING completions c
    WHERE pe.event_work_id = c.work_id
    RETURNING pe.event_work_id, pe.stream_id, pe.perspective_name, TRUE AS was_deleted;
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.process_perspective_event_completions IS
'Processes perspective event completions. In production mode, deletes events (ephemeral). In debug mode, retains events for troubleshooting. Returns stream/perspective pairs for checkpoint update orchestration.';
