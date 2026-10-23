-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 016_UpdatePerspectiveCheckpoints.sql
-- Date: 2025-12-25
-- Description: Creates update_perspective_cursors, which persists each stream's perspective cursor at the position the
--              run reports (#1363). The cursor follows each completion's own LastEventId: it is created when missing,
--              advanced when the reported event sorts later in its stream, and never moved back by an earlier one. It
--              never passes the first event still unprocessed for the pair (the gap invariant).
--
--              It used to derive the position from the processed wh_perspective_events rows. complete_perspective
--              deletes those rows (production mode) before calling this, so in production nothing was found: a missing
--              cursor was never created and an existing one never advanced; only status changed. The rows' pre-creation
--              that once covered the first case went away with process_work_batch.
-- Dependencies: 001-015 (requires wh_perspective_events and wh_perspective_cursors tables)

SELECT __SCHEMA__.drop_all_overloads('update_perspective_cursors');

-- <docs>fundamentals/work-coordinator/batched-flushers#perspective-cursors</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorAdvanceSqlTests.cs:CompletePerspective_ProductionMode_CreatesTheMissingCursorAtTheCompletionsEventAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorAdvanceSqlTests.cs:CompletePerspective_ProductionMode_AdvancesAnExistingCursorAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorAdvanceSqlTests.cs:CompletePerspective_AnEarlierEventThanTheCursor_LeavesTheCursorWhereItIsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorAdvanceSqlTests.cs:CompletePerspective_TwoCompletionsForOneStreamInABatch_TheLaterEventWinsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorAdvanceSqlTests.cs:CompletePerspective_ACompletionWithNoEvent_CreatesNoCursorAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorAdvanceSqlTests.cs:CompletePerspective_AnEventTheStoreDoesNotHold_IsSkippedAndTheRestOfTheBatchRecordedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.update_perspective_cursors(
  p_completed_events JSONB,  -- [{StreamId, PerspectiveName, LastEventId, ...}]: PerspectiveCursorCompletion
  p_debug_mode BOOLEAN DEFAULT FALSE
) RETURNS VOID AS $$
BEGIN
  -- Lock the existing cursors of every reported pair in one order, so two batches touching the same pairs queue
  -- instead of deadlocking.
  PERFORM 1
  FROM __SCHEMA__.wh_perspective_cursors pc
  JOIN (
    SELECT DISTINCT (elem->>'StreamId')::UUID AS stream_id, elem->>'PerspectiveName' AS perspective_name
    FROM jsonb_array_elements(p_completed_events) AS elem
  ) p ON p.stream_id = pc.stream_id AND p.perspective_name = pc.perspective_name
  ORDER BY pc.stream_id, pc.perspective_name
  FOR UPDATE OF pc;

  -- The position of each (stream, perspective) is the latest of: the event its completions report (LastEventId), and
  -- any processed rows still present (debug mode keeps them; production deletes them before this runs, which is why
  -- the reported event is needed at all). It never passes the first event still unprocessed for the pair (the gap
  -- invariant: an event behind the cursor would never be applied), and never moves the cursor back. An event the store
  -- does not hold cannot be a cursor (fk_perspective_cursors_event) and is not a candidate. Every reported pair's
  -- status becomes 2 when nothing is left unprocessed, whether or not its position moved.
  WITH pairs AS (
    SELECT DISTINCT (elem->>'StreamId')::UUID AS stream_id, elem->>'PerspectiveName' AS perspective_name
    FROM jsonb_array_elements(p_completed_events) AS elem
  ),
  reported AS (
    SELECT (elem->>'StreamId')::UUID AS stream_id, elem->>'PerspectiveName' AS perspective_name,
      (elem->>'LastEventId')::UUID AS last_event_id
    FROM jsonb_array_elements(p_completed_events) AS elem
    WHERE elem->>'LastEventId' IS NOT NULL
      AND (elem->>'LastEventId')::UUID <> __EMPTY_UUID__::UUID
  ),
  gate AS (
    SELECT p.stream_id, p.perspective_name,
      (SELECT min(e.version)
         FROM __SCHEMA__.wh_perspective_events pe
         JOIN __SCHEMA__.wh_event_store e ON e.event_id = pe.event_id
        WHERE pe.stream_id = p.stream_id AND pe.perspective_name = p.perspective_name
          AND pe.processed_at IS NULL) AS first_unprocessed_version,
      NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
        WHERE pe.stream_id = p.stream_id AND pe.perspective_name = p.perspective_name
          AND pe.processed_at IS NULL) AS is_complete
    FROM pairs p
  ),
  candidates AS (
    SELECT r.stream_id, r.perspective_name, e.event_id, e.version
    FROM reported r
    JOIN __SCHEMA__.wh_event_store e ON e.event_id = r.last_event_id
    UNION ALL
    SELECT pe.stream_id, pe.perspective_name, e.event_id, e.version
    FROM pairs p
    JOIN __SCHEMA__.wh_perspective_events pe
      ON pe.stream_id = p.stream_id AND pe.perspective_name = p.perspective_name AND pe.processed_at IS NOT NULL
    JOIN __SCHEMA__.wh_event_store e ON e.event_id = pe.event_id
  ),
  best AS (
    SELECT DISTINCT ON (c.stream_id, c.perspective_name) c.stream_id, c.perspective_name, c.event_id, c.version
    FROM candidates c
    JOIN gate g ON g.stream_id = c.stream_id AND g.perspective_name = c.perspective_name
    WHERE g.first_unprocessed_version IS NULL OR c.version < g.first_unprocessed_version
    ORDER BY c.stream_id, c.perspective_name, c.version DESC
  ),
  positioned AS (
    SELECT g.stream_id, g.perspective_name, g.is_complete, b.event_id, b.version
    FROM gate g
    LEFT JOIN best b ON b.stream_id = g.stream_id AND b.perspective_name = g.perspective_name
  ),
  updated AS (
    UPDATE __SCHEMA__.wh_perspective_cursors pc
    SET last_event_id = CASE
          -- Never backwards. A cursor whose event the store no longer holds is behind any event it does.
          WHEN p.event_id IS NOT NULL
               AND p.version > COALESCE(
                 (SELECT e.version FROM __SCHEMA__.wh_event_store e WHERE e.event_id = pc.last_event_id), -1)
            THEN p.event_id
          ELSE pc.last_event_id
        END,
        status = CASE WHEN p.is_complete THEN 2 ELSE pc.status END
    FROM positioned p
    WHERE pc.stream_id = p.stream_id AND pc.perspective_name = p.perspective_name
    RETURNING pc.stream_id, pc.perspective_name
  )
  INSERT INTO __SCHEMA__.wh_perspective_cursors (stream_id, perspective_name, last_event_id, status)
  SELECT p.stream_id, p.perspective_name, p.event_id, CASE WHEN p.is_complete THEN 2 ELSE 0 END
  FROM positioned p
  WHERE p.event_id IS NOT NULL
    AND NOT EXISTS (SELECT 1 FROM updated u WHERE u.stream_id = p.stream_id AND u.perspective_name = p.perspective_name)
  ORDER BY p.stream_id, p.perspective_name
  ON CONFLICT (stream_id, perspective_name) DO NOTHING;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.update_perspective_cursors(JSONB, BOOLEAN) IS
'#1363: persists each stream perspective cursor at the latest of the reported event and any processed rows still '
'present, never past the first unprocessed event and never moved back: created when missing, advanced otherwise. '
'Status is 2 for every reported pair with no unprocessed work left.';
