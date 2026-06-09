-- Migration: 016_UpdatePerspectiveCheckpoints.sql
-- Date: 2025-12-25
-- Description: Creates update_perspective_cursors function for updating persistent checkpoint state.
--              Finds highest completed sequence with no gaps, updates checkpoint atomically.
-- Dependencies: 001-015 (requires wh_perspective_events and wh_perspective_cursors tables)

SELECT __SCHEMA__.drop_all_overloads('update_perspective_cursors');

CREATE OR REPLACE FUNCTION __SCHEMA__.update_perspective_cursors(
  p_completed_events JSONB,  -- [{StreamId, PerspectiveName}]
  p_debug_mode BOOLEAN DEFAULT FALSE
) RETURNS VOID AS $$
BEGIN
  -- v0.671: collapse the prior FOR-loop (four statements per (StreamId,
  -- PerspectiveName) pair — gap-free SELECT, NOT EXISTS is_complete check,
  -- UPDATE wh_perspective_cursors, conditional INSERT if NOT FOUND) into a
  -- two-statement bulk pattern. For M distinct pairs, the loop pattern
  -- issued 4M round trips; the bulk pattern is 2 (one UPDATE + one INSERT
  -- for any new pairs not yet in wh_perspective_cursors).
  --
  -- production during-import data (PR #252 cycle) showed CompletePerspectiveAsync
  -- as the second-largest gate-hold contributor after ClaimWorkAsync. Slice A
  -- (PR #253) collapsed process_perspective_event_completions's loop;
  -- this slice B addresses the other major loop inside complete_perspective.
  --
  -- Behavioral parity with the loop implementation preserved:
  --   - Gap-free SELECT runs as a correlated subquery per pair in the CTE.
  --   - COALESCE(new_last_event_id, pc.last_event_id) preserves cursor when
  --     no gap-free event was found (e.g., production-mode rows already
  --     deleted by process_perspective_event_completions before this runs).
  --   - is_complete = NOT EXISTS unprocessed rows for the pair → sets
  --     status to 2 (Completed) when fully drained; preserves existing
  --     status otherwise.
  --   - INSERT path fires only for pairs with no existing cursor AND a
  --     non-NULL gap-free event_id (the NOT NULL constraint demands a real
  --     last_event_id — same constraint the loop implementation honored).
  IF p_completed_events IS NULL OR jsonb_array_length(p_completed_events) = 0 THEN
    RETURN;
  END IF;

  -- Statement 1: bulk UPDATE for pairs that already have a cursor row.
  WITH pairs AS (
    SELECT DISTINCT
      (elem->>'StreamId')::UUID AS stream_id,
      elem->>'PerspectiveName' AS perspective_name
    FROM jsonb_array_elements(p_completed_events) AS elem
  ),
  computed AS (
    SELECT
      p.stream_id,
      p.perspective_name,
      (SELECT pe.event_id
       FROM __SCHEMA__.wh_perspective_events pe
       WHERE pe.stream_id = p.stream_id
         AND pe.perspective_name = p.perspective_name
         AND pe.processed_at IS NOT NULL
         AND NOT EXISTS (
           SELECT 1 FROM __SCHEMA__.wh_perspective_events earlier
           WHERE earlier.stream_id = pe.stream_id
             AND earlier.perspective_name = pe.perspective_name
             AND earlier.event_id < pe.event_id
             AND earlier.processed_at IS NULL
         )
       ORDER BY pe.event_id DESC
       LIMIT 1) AS new_last_event_id,
      NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_perspective_events pe2
        WHERE pe2.stream_id = p.stream_id
          AND pe2.perspective_name = p.perspective_name
          AND pe2.processed_at IS NULL
      ) AS is_complete
    FROM pairs p
  )
  UPDATE __SCHEMA__.wh_perspective_cursors pc
  SET last_event_id = COALESCE(c.new_last_event_id, pc.last_event_id),
      status = CASE WHEN c.is_complete THEN 2 ELSE pc.status END
  FROM computed c
  WHERE pc.stream_id = c.stream_id
    AND pc.perspective_name = c.perspective_name;

  -- Statement 2: bulk INSERT for pairs that did NOT have a cursor and DO have
  -- gap-free progress to record. Matches the loop's IF NOT FOUND branch. The
  -- NOT NULL constraint on last_event_id forces us to skip new pairs without
  -- progress — same constraint the loop honored (it would have errored if it
  -- tried to INSERT with NULL last_event_id; the loop's flow filters that
  -- case implicitly because real production cursors are pre-created when
  -- their first event is stored via store_perspective_events).
  WITH pairs AS (
    SELECT DISTINCT
      (elem->>'StreamId')::UUID AS stream_id,
      elem->>'PerspectiveName' AS perspective_name
    FROM jsonb_array_elements(p_completed_events) AS elem
  ),
  needed_inserts AS (
    SELECT
      p.stream_id,
      p.perspective_name,
      (SELECT pe.event_id
       FROM __SCHEMA__.wh_perspective_events pe
       WHERE pe.stream_id = p.stream_id
         AND pe.perspective_name = p.perspective_name
         AND pe.processed_at IS NOT NULL
         AND NOT EXISTS (
           SELECT 1 FROM __SCHEMA__.wh_perspective_events earlier
           WHERE earlier.stream_id = pe.stream_id
             AND earlier.perspective_name = pe.perspective_name
             AND earlier.event_id < pe.event_id
             AND earlier.processed_at IS NULL
         )
       ORDER BY pe.event_id DESC
       LIMIT 1) AS new_last_event_id,
      NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_perspective_events pe2
        WHERE pe2.stream_id = p.stream_id
          AND pe2.perspective_name = p.perspective_name
          AND pe2.processed_at IS NULL
      ) AS is_complete
    FROM pairs p
    WHERE NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_cursors pc
      WHERE pc.stream_id = p.stream_id
        AND pc.perspective_name = p.perspective_name
    )
  )
  INSERT INTO __SCHEMA__.wh_perspective_cursors (stream_id, perspective_name, last_event_id, status)
  SELECT stream_id, perspective_name, new_last_event_id,
         CASE WHEN is_complete THEN 2 ELSE 0 END
  FROM needed_inserts
  WHERE new_last_event_id IS NOT NULL;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.update_perspective_cursors IS
'Updates persistent perspective checkpoints based on completed events. Finds highest completed sequence with no gaps, ensuring sequential consistency. Updates or creates checkpoint records atomically.';
