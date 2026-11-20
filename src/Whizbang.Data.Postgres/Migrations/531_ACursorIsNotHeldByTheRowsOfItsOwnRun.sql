-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 531_ACursorIsNotHeldByTheRowsOfItsOwnRun.sql
-- Date: 2026-11-20
-- Description: A cursor flushed before the retirement of its own run's work rows still moves to the event the run
--              reports (#2432).
--
--              The worker reports a run's cursor and retires the run's work rows through one channel that the flusher
--              cuts by size and time, so the cursor can reach complete_perspective in an earlier flush than the rows
--              (374 described the split and settled the rewind flag it left). update_perspective_cursors holds a cursor
--              before the first event still pending for its pair (the gap invariant), and in that split the pending
--              rows were those of the very events the run applied. The cursor stayed before them, and the later flush
--              that retired the rows carried no cursor. A stream that received no further event kept, for good, a
--              cursor naming an event its perspective had passed; the superseding reaper's cursor gate then held that
--              event forever.
--
--              update_perspective_cursors now leaves out of the gate, and out of the "no work left" status, the pending
--              rows of the events each completion reports applied: its LastEventId and its ProcessedEventIds, which the
--              batched path already serializes. A pending event the run did not report still holds the cursor.
--
-- Reversal: reversible (455's update_perspective_cursors is restored; cursors it moved stay where they are, at events
--           their perspectives applied)
-- Dependencies: 455 (last word of update_perspective_cursors), 374 (complete_perspective, which calls it)
-- Objects: update_perspective_cursors
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

SELECT __SCHEMA__.drop_all_overloads('update_perspective_cursors');

-- Copied whole from 455 with #2432's delta: a pending row of an event the completion reports its run applied
-- (LastEventId and ProcessedEventIds) is not a gap. The worker queues a cursor and the retirement of its rows on one
-- channel that the flusher cuts by size and time, so the cursor can be flushed before its rows; the gate then saw the
-- run's own event pending and held the cursor before it, and the later flush that retired the row carried no cursor.
-- <docs>fundamentals/work-coordinator/batched-flushers#perspective-cursors</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorAdvanceSqlTests.cs:CompletePerspective_ProductionMode_CreatesTheMissingCursorAtTheCompletionsEventAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorAdvanceSqlTests.cs:CompletePerspective_ProductionMode_AdvancesAnExistingCursorAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorAdvanceSqlTests.cs:CompletePerspective_AnEarlierEventThanTheCursor_LeavesTheCursorWhereItIsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorAdvanceSqlTests.cs:CompletePerspective_TwoCompletionsForOneStreamInABatch_TheLaterEventWinsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorAdvanceSqlTests.cs:CompletePerspective_ACompletionWithNoEvent_CreatesNoCursorAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorAdvanceSqlTests.cs:CompletePerspective_AnEventTheStoreDoesNotHold_IsSkippedAndTheRestOfTheBatchRecordedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorOneOrderSqlTests.cs:CompletePerspective_AReplayFrontier_IsStoredOnTheCursor_AndNeverLoweredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorAdvanceSqlTests.cs:CompletePerspective_AnEarlierEventStillPending_HoldsTheCursorBeforeItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorAdvanceSqlTests.cs:CompletePerspective_TheCursorFlushedBeforeItsWork_MovesToTheReportedEventAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorAdvanceSqlTests.cs:CompletePerspective_APendingEventTheRunDidNotReport_StillHoldsTheCursorBeforeItAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.update_perspective_cursors(
  p_completed_events JSONB,  -- [{StreamId, PerspectiveName, LastEventId, ReplayedThroughCommitSequence, ...}]
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

  -- 531 (#2432): a pending row of an event the pair's run reports applied is no gap: the run folded it, and the row
  -- waits only for the flush that retires it.
  -- The position of each (stream, perspective) is the latest of: the event its completions report (LastEventId), and
  -- any processed rows still present (debug mode keeps them; production deletes them before this runs, which is why
  -- the reported event is needed at all). It never passes the first event still unprocessed for the pair (the gap
  -- invariant: an event behind the cursor would never be applied), and never moves the cursor back. An event the store
  -- does not hold cannot be a cursor (fk_perspective_cursors_event) and is not a candidate. Every reported pair's
  -- status becomes 2 when nothing is left unprocessed, whether or not its position moved. The highest replay frontier
  -- reported for the pair is stored (#2366).
  WITH pairs AS (
    SELECT (elem->>__ENVELOPE_FIELD_STREAM_ID__)::UUID AS stream_id, elem->>'PerspectiveName' AS perspective_name,
      max((elem->>'ReplayedThroughCommitSequence')::BIGINT) AS replayed_through
    FROM jsonb_array_elements(p_completed_events) AS elem
    GROUP BY 1, 2
  ),
  reported AS (
    SELECT (elem->>__ENVELOPE_FIELD_STREAM_ID__)::UUID AS stream_id, elem->>'PerspectiveName' AS perspective_name,
      (elem->>'LastEventId')::UUID AS last_event_id
    FROM jsonb_array_elements(p_completed_events) AS elem
    WHERE elem->>'LastEventId' IS NOT NULL
      AND (elem->>'LastEventId')::UUID <> __EMPTY_UUID__::UUID
  ),
  -- 531 (#2432): the events each pair's runs applied. Their rows may still be pending, waiting on a later flush.
  ran AS (
    SELECT (elem->>__ENVELOPE_FIELD_STREAM_ID__)::UUID AS stream_id, elem->>'PerspectiveName' AS perspective_name,
      applied.event_id::UUID AS event_id
    FROM jsonb_array_elements(p_completed_events) AS elem
    CROSS JOIN LATERAL jsonb_array_elements_text(
      CASE WHEN jsonb_typeof(elem->'ProcessedEventIds') = 'array' THEN elem->'ProcessedEventIds' ELSE '[]'::JSONB END
    ) AS applied(event_id)
    UNION
    SELECT r.stream_id, r.perspective_name, r.last_event_id
    FROM reported r
  ),
  gate AS (
    SELECT p.stream_id, p.perspective_name, p.replayed_through,
      (SELECT min(e.version)
         FROM __SCHEMA__.wh_perspective_events pe
         JOIN __SCHEMA__.wh_event_store e ON e.event_id = pe.event_id
        WHERE pe.stream_id = p.stream_id AND pe.perspective_name = p.perspective_name
          AND pe.processed_at IS NULL
          AND NOT EXISTS (
            SELECT 1 FROM ran x
            WHERE x.stream_id = p.stream_id AND x.perspective_name = p.perspective_name AND x.event_id = pe.event_id)
      ) AS first_unprocessed_version,
      NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
        WHERE pe.stream_id = p.stream_id AND pe.perspective_name = p.perspective_name
          AND pe.processed_at IS NULL
          AND NOT EXISTS (
            SELECT 1 FROM ran x
            WHERE x.stream_id = p.stream_id AND x.perspective_name = p.perspective_name AND x.event_id = pe.event_id)
      ) AS is_complete
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
    SELECT g.stream_id, g.perspective_name, g.is_complete, g.replayed_through, b.event_id, b.version
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
        status = CASE WHEN p.is_complete THEN 2 ELSE pc.status END,
        replayed_through_commit_sequence = GREATEST(pc.replayed_through_commit_sequence, p.replayed_through)
    FROM positioned p
    WHERE pc.stream_id = p.stream_id AND pc.perspective_name = p.perspective_name
    RETURNING pc.stream_id, pc.perspective_name
  )
  INSERT INTO __SCHEMA__.wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, replayed_through_commit_sequence)
  SELECT p.stream_id, p.perspective_name, p.event_id, CASE WHEN p.is_complete THEN 2 ELSE 0 END, p.replayed_through
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
'Status is 2 for every reported pair with no unprocessed work left. #2366: stores the highest replay frontier reported. '
'531 (#2432): a pending row of an event the completion reports applied (LastEventId, ProcessedEventIds) is no gap and '
'no unfinished work, so a cursor flushed before its rows still moves.';

