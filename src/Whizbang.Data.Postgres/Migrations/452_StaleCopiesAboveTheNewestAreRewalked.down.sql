-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 452_StaleCopiesAboveTheNewestAreRewalked.sql (#2366): complete_perspective_cursor_work returns to 005's
-- definition and update_perspective_cursors to 016's, both copied whole; the repair functions and the
-- replayed_through_commit_sequence column go. A walk 452 restarted completes again and changes nothing more.

SELECT __SCHEMA__.drop_all_overloads('wh_restart_superseding_history_walks');
SELECT __SCHEMA__.drop_all_overloads('wh_settle_id_ordered_rewind_flags');

SELECT __SCHEMA__.drop_all_overloads('complete_perspective_cursor_work');

-- 005's definition, restored.
-- <docs>fundamentals/perspectives/rewind#one-order-for-a-stream</docs>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperWorkCoordinatorBroadTests.cs:ReportPerspectiveCompletionAsync_AdvancesTheCursorAndClearsTheErrorAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.complete_perspective_cursor_work(
  p_stream_id UUID,
  p_perspective_name TEXT,
  p_last_event_id UUID,
  p_processed_event_ids JSONB,
  p_status SMALLINT,
  p_error_message TEXT DEFAULT NULL
) RETURNS VOID AS $$
DECLARE
  v_is_catching_up BOOLEAN;
  v_straggler_event_id UUID;
  v_event_ids UUID[];
BEGIN
  -- Parse JSONB array of event IDs to UUID[] for use in queries
  IF p_processed_event_ids IS NOT NULL
     AND jsonb_typeof(p_processed_event_ids) = 'array'
     AND jsonb_array_length(p_processed_event_ids) > 0 THEN
    SELECT array_agg(e::UUID) INTO v_event_ids
    FROM jsonb_array_elements_text(p_processed_event_ids) AS e;
  ELSE
    v_event_ids := '{}'::UUID[];
  END IF;

  -- Check if this perspective is in CatchingUp mode (status & 8 = 8)
  SELECT (status & 8) = 8
  INTO v_is_catching_up
  FROM __SCHEMA__.wh_perspective_cursors
  WHERE stream_id = p_stream_id
    AND perspective_name = p_perspective_name;

  -- Update checkpoint with results from perspective runner
  -- Includes error message for failed runs, clears error for successful runs
  -- Clears rewind columns after successful completion to prevent rewind loops
  UPDATE __SCHEMA__.wh_perspective_cursors
  SET last_event_id = p_last_event_id,
      status = p_status,
      processed_at = NOW(),
      error = p_error_message,
      rewind_trigger_event_id = NULL,
      rewind_flagged_at = NULL,
      rewind_first_flagged_at = NULL
  WHERE stream_id = p_stream_id
    AND perspective_name = p_perspective_name;

  -- CRITICAL: Mark ONLY actually-processed perspective events
  -- Uses explicit event ID array instead of range-based marking (event_id <= cursor)
  -- to prevent concurrent late-arriving events from being incorrectly marked as processed.
  -- Event IDs are the specific events the runner read from wh_event_store and applied.
  UPDATE __SCHEMA__.wh_perspective_events pe
  SET processed_at = NOW()
  WHERE stream_id = p_stream_id
    AND perspective_name = p_perspective_name
    AND processed_at IS NULL
    AND event_id = ANY(v_event_ids);

  -- Belt-and-suspenders: detect unprocessed events below the cursor.
  -- If any perspective_events exist with event_id < cursor and processed_at IS NULL,
  -- they arrived during processing and the runner never saw them. Flag rewind so the
  -- next run replays from snapshot and picks them up.
  SELECT event_id INTO v_straggler_event_id
  FROM __SCHEMA__.wh_perspective_events
  WHERE stream_id = p_stream_id
    AND perspective_name = p_perspective_name
    AND processed_at IS NULL
    AND event_id < p_last_event_id
  ORDER BY event_id
  LIMIT 1;

  IF v_straggler_event_id IS NOT NULL THEN
    UPDATE __SCHEMA__.wh_perspective_cursors
    SET status = status | 32,  -- RewindRequired flag (1 << 5)
        rewind_trigger_event_id = CASE
          WHEN rewind_trigger_event_id IS NULL THEN v_straggler_event_id
          WHEN v_straggler_event_id < rewind_trigger_event_id THEN v_straggler_event_id
          ELSE rewind_trigger_event_id
        END,
        rewind_flagged_at = NOW(),
        rewind_first_flagged_at = COALESCE(rewind_first_flagged_at, NOW())
    WHERE stream_id = p_stream_id
      AND perspective_name = p_perspective_name;
  END IF;

  -- If we were catching up and successfully completed, clear the CatchingUp flag
  IF v_is_catching_up AND (p_status & 2) = 2 THEN  -- Completed flag
    UPDATE __SCHEMA__.wh_perspective_cursors
    SET status = status & ~8  -- Clear CatchingUp flag (bitwise AND NOT)
    WHERE stream_id = p_stream_id
      AND perspective_name = p_perspective_name;
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.complete_perspective_cursor_work IS 'Updates perspective checkpoint with completion/failure results from perspective runner. Uses explicit event ID array to prevent concurrent late-arriving events from being incorrectly marked as processed. Supports CatchingUp status for time-travel/replay scenarios.';

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
    SELECT DISTINCT (elem->>__ENVELOPE_FIELD_STREAM_ID__)::UUID AS stream_id, elem->>'PerspectiveName' AS perspective_name
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
    SELECT DISTINCT (elem->>__ENVELOPE_FIELD_STREAM_ID__)::UUID AS stream_id, elem->>'PerspectiveName' AS perspective_name
    FROM jsonb_array_elements(p_completed_events) AS elem
  ),
  reported AS (
    SELECT (elem->>__ENVELOPE_FIELD_STREAM_ID__)::UUID AS stream_id, elem->>'PerspectiveName' AS perspective_name,
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

-- RECLAIM: not required: the column is a nullable BIGINT that only a replay's completion sets, so a cursor row keeps at
--          most eight dropped bytes, and every later update of a cursor (one per completion) writes the row anew with the
--          dropped attribute null, so the bytes leave as cursors move.
ALTER TABLE __SCHEMA__.wh_perspective_cursors DROP COLUMN IF EXISTS replayed_through_commit_sequence;
