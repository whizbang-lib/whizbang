-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 452_StaleCopiesAboveTheNewestAreRewalked.sql
-- Date: 2026-11-15 (one order for a stream's cursor: 2026-11-16)
-- Description: Stale copies stored above a stream's newest event are walked again, and a perspective cursor follows one
--              order for its stream, so a stream holding such copies converges after one rewind (#2366).
--
--              Before 449, a redelivered older copy of a superseding key was stored at the end of a consumer's stream,
--              above the newest copy by local version, and nothing that ordered copies knew better. 449 orders them by
--              the origin's commit sequence and stops new stale copies at the store, but the copies already stored stay:
--              each superseding type's history walk had completed under the old rule. wh_restart_superseding_history_walks()
--              restarts every completed superseding walk from its first event, and the superseding-history-reconcile
--              maintenance step walks each type again under 449's rule. Called once here.
--
--              Such a stream's ids and versions disagree: the newest event has the highest id and the lowest version.
--              Four orders met on it. A rewind reported the largest id it applied with the largest commit sequence it
--              applied, a position no event has; complete_perspective_cursor_work set the cursor to it unconditionally,
--              moving it back to the highest-id event, and flagged a pending row as a straggler by event id; the worker
--              then judged a copy queued again against the mixed position and rewound again, forever. Now one order
--              holds: version, the order a replay folds in, with the commit sequence as the replay's frontier.
--
--              - wh_perspective_cursors.replayed_through_commit_sequence: the frontier a completed replay read through.
--                Every event stamped at or below it is applied, so a work row for one, queued again, is covered.
--              - complete_perspective_cursor_work: never moves the cursor back (by version), stores the frontier, clears
--                the rewind flag only once the cursor is at or past its trigger or the trigger is covered, and detects a
--                straggler by version, never naming a covered event. It creates the cursor of a pair that has none at
--                the reported event, as the batched path does (before, a stream completed only here never had one).
--              - update_perspective_cursors: the batched path already moves by version and never back; it now stores
--                the frontier too.
--              - wh_settle_id_ordered_rewind_flags(): places the cursors already in the stuck shape and keeps their flags,
--                so the fixed code resolves each with one rewind; called once here. It queues one row per stuck pair with
--                no pending work (making the trigger's unpurged processed row pending again) and nothing on a re-run.
--
--              Number 452: reserved for this change.
--
-- Reversal: reversible (the functions return to 005 and 016, the column and the repair function go; a walk restarted
--           completes again and changes nothing)
-- Dependencies: 005 (complete_perspective_cursor_work), 016 (update_perspective_cursors), 380 (wh_type_history_walks),
--               449 (_wh_supersedes in the superseding walk)
-- Objects: wh_restart_superseding_history_walks, wh_perspective_cursors, complete_perspective_cursor_work,
--          update_perspective_cursors, wh_settle_id_ordered_rewind_flags
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- Restarts every completed superseding history walk from its first event. Returns the walks restarted.
-- <docs>fundamentals/events/superseding-events#stale-copies-already-stored</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Repair_AStreamHoldingOlderCopiesAboveItsNewest_QueuesTheCopies_OnceTheWalkRestartsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_restart_superseding_history_walks() RETURNS INTEGER AS $$
DECLARE
  v_restarted INTEGER;
BEGIN
  UPDATE __SCHEMA__.wh_type_history_walks h SET
    last_commit_sequence = NULL,
    events_total = (SELECT count(*) FROM __SCHEMA__.wh_event_store es WHERE es.event_type = h.event_type),
    events_matched = 0,
    events_changed = 0,
    events_queued = 0,
    events_blocked = 0,
    started_at = NOW(),
    completed_at = NULL,
    updated_at = NOW()
  WHERE h.operation = 'superseding'
    AND h.completed_at IS NOT NULL;
  GET DIAGNOSTICS v_restarted = ROW_COUNT;
  RETURN v_restarted;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_restart_superseding_history_walks() IS
'Restarts every completed superseding history walk from its first event, so the walk applies the origin-ordered rule of 449 to the copies already stored (#2366). Returns the walks restarted.';

SELECT __SCHEMA__.wh_restart_superseding_history_walks();

-- ===========================================================================================
-- One order for a stream's cursor
-- ===========================================================================================

ALTER TABLE __SCHEMA__.wh_perspective_cursors ADD COLUMN IF NOT EXISTS replayed_through_commit_sequence BIGINT NULL;

COMMENT ON COLUMN __SCHEMA__.wh_perspective_cursors.replayed_through_commit_sequence IS
'The commit-sequence frontier the last completed replay (rewind or rebuild) of this pair read through, never lowered: every event of the stream stamped at or below it is folded into the row, so a work row for one is already applied (#2366).';

SELECT __SCHEMA__.drop_all_overloads('complete_perspective_cursor_work');

-- Records a perspective run's completion or failure on the stream's cursor (the immediate path; the batched path is
-- update_perspective_cursors). Copied whole from 005 with #2366's delta: one order (version), never backward.
-- <docs>fundamentals/perspectives/rewind#one-order-for-a-stream</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorOneOrderSqlTests.cs:CompleteCursorWork_AnEarlierVersionThanTheCursor_LeavesTheCursorWhereItIsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorOneOrderSqlTests.cs:CompleteCursorWork_AReplayAtTheLastVersion_MovesTheCursorStoresItsFrontierAndClearsTheFlagAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorOneOrderSqlTests.cs:CompleteCursorWork_APositionBelowTheTrigger_KeepsTheFlagAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorOneOrderSqlTests.cs:CompleteCursorWork_APendingRowBelowTheCursorByVersion_IsFlagged_UnlessAReplayReadItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorOneOrderSqlTests.cs:CompleteCursorWork_AStreamWithNoCursor_GetsOneAtTheReportedEventAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorOneOrderSqlTests.cs:CompleteCursorWork_AnEventTheStoreDoesNotHold_CreatesNoCursorAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveRewindLoopTests.cs:TwoServices_SupersededReap_Redelivery_AndRewind_TogetherConverge_WithNoLoopAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveRewindLoopTests.cs:AStuckCursorOnTheHighestIdAtTheLowestVersion_ConvergesAfterOneRewind_AndARequeuedCopyIsNotRewoundAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.complete_perspective_cursor_work(
  p_stream_id UUID,
  p_perspective_name TEXT,
  p_last_event_id UUID,
  p_processed_event_ids JSONB,
  p_status SMALLINT,
  p_error_message TEXT DEFAULT NULL,
  p_replayed_through_commit_sequence BIGINT DEFAULT NULL
) RETURNS VOID AS $$
DECLARE
  v_found BOOLEAN;
  v_is_catching_up BOOLEAN;
  v_event_ids UUID[];
  v_cursor_version BIGINT;
  v_reported_version BIGINT;
  v_position_event_id UUID;
  v_position_version BIGINT;
  v_replayed_through BIGINT;
  v_trigger UUID;
  v_trigger_version BIGINT;
  v_trigger_sequence BIGINT;
  v_keep_flag BOOLEAN := FALSE;
  v_straggler_event_id UUID;
  v_straggler_version BIGINT;
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

  -- The cursor as it stands, locked: CatchingUp, its position's version, its frontier and the trigger it is flagged for.
  SELECT TRUE, (c.status & 8) = 8, ce.version, c.replayed_through_commit_sequence,
         CASE WHEN (c.status & 32) = 32 THEN c.rewind_trigger_event_id END
  INTO v_found, v_is_catching_up, v_cursor_version, v_replayed_through, v_trigger
  FROM __SCHEMA__.wh_perspective_cursors c
  LEFT JOIN __SCHEMA__.wh_event_store ce ON ce.event_id = c.last_event_id
  WHERE c.stream_id = p_stream_id
    AND c.perspective_name = p_perspective_name
  FOR UPDATE OF c;

  IF v_found THEN
    -- One order (#2366): the cursor moves only to an event later in the stream by version, never back. A cursor whose
    -- event the store no longer holds is behind any event it does; a reported event the store does not hold cannot be
    -- placed, so it moves only a cursor that cannot be placed either (the report is then all there is to go by).
    SELECT e.version INTO v_reported_version FROM __SCHEMA__.wh_event_store e WHERE e.event_id = p_last_event_id;
    IF (v_reported_version IS NOT NULL AND (v_cursor_version IS NULL OR v_reported_version > v_cursor_version))
       OR (v_reported_version IS NULL AND v_cursor_version IS NULL) THEN
      v_position_event_id := p_last_event_id;
      v_position_version := v_reported_version;
    ELSE
      v_position_version := v_cursor_version;
    END IF;
    v_replayed_through := GREATEST(v_replayed_through, p_replayed_through_commit_sequence);

    -- The flag stays until the cursor is at or past its trigger, or a completed replay read the trigger.
    IF v_trigger IS NOT NULL THEN
      SELECT e.version, e.commit_sequence INTO v_trigger_version, v_trigger_sequence
      FROM __SCHEMA__.wh_event_store e WHERE e.event_id = v_trigger;
      v_keep_flag := v_trigger_version IS NOT NULL
        AND (v_position_version IS NULL OR v_trigger_version > v_position_version)
        AND NOT (v_trigger_sequence IS NOT NULL AND v_trigger_sequence <= COALESCE(v_replayed_through, -1));
    END IF;

    UPDATE __SCHEMA__.wh_perspective_cursors
    SET last_event_id = COALESCE(v_position_event_id, last_event_id),
        status = p_status | CASE WHEN v_keep_flag THEN 32 ELSE 0 END,
        processed_at = NOW(),
        error = p_error_message,
        replayed_through_commit_sequence = v_replayed_through,
        rewind_trigger_event_id = CASE WHEN v_keep_flag THEN rewind_trigger_event_id END,
        rewind_flagged_at = CASE WHEN v_keep_flag THEN rewind_flagged_at END,
        rewind_first_flagged_at = CASE WHEN v_keep_flag THEN rewind_first_flagged_at END
    WHERE stream_id = p_stream_id
      AND perspective_name = p_perspective_name;
  ELSE
    -- A pair with no cursor gets one at the reported event, as update_perspective_cursors creates a missing one (#2366).
    -- Before, this path only updated, so a new stream completed here never had a cursor. A reported event the store
    -- does not hold cannot be placed, so it creates none.
    SELECT e.version INTO v_position_version
    FROM __SCHEMA__.wh_event_store e WHERE e.event_id = p_last_event_id AND e.stream_id = p_stream_id;
    IF v_position_version IS NOT NULL THEN
      INSERT INTO __SCHEMA__.wh_perspective_cursors
        (stream_id, perspective_name, last_event_id, status, processed_at, error, replayed_through_commit_sequence)
      VALUES (p_stream_id, p_perspective_name, p_last_event_id, p_status & ~(32::SMALLINT), NOW(), p_error_message,
              p_replayed_through_commit_sequence)
      ON CONFLICT (stream_id, perspective_name) DO NOTHING;
      v_found := FOUND;
      v_is_catching_up := FALSE;
      v_replayed_through := p_replayed_through_commit_sequence;
    END IF;
  END IF;

  -- CRITICAL: Mark ONLY actually-processed perspective events
  -- Uses explicit event ID array instead of range-based marking (event_id <= cursor)
  -- to prevent concurrent late-arriving events from being incorrectly marked as processed.
  UPDATE __SCHEMA__.wh_perspective_events pe
  SET processed_at = NOW()
  WHERE stream_id = p_stream_id
    AND perspective_name = p_perspective_name
    AND processed_at IS NULL
    AND event_id = ANY(v_event_ids);

  IF NOT COALESCE(v_found, FALSE) THEN
    RETURN;
  END IF;

  -- A straggler is an unprocessed row below the cursor in the stream's order (by version), which the run never saw.
  -- A row for an event a completed replay read through is covered, never a straggler: it is a copy queued again.
  SELECT pe.event_id, e.version INTO v_straggler_event_id, v_straggler_version
  FROM __SCHEMA__.wh_perspective_events pe
  JOIN __SCHEMA__.wh_event_store e ON e.event_id = pe.event_id
  WHERE pe.stream_id = p_stream_id
    AND pe.perspective_name = p_perspective_name
    AND pe.processed_at IS NULL
    AND e.version < v_position_version
    AND NOT (e.commit_sequence IS NOT NULL AND e.commit_sequence <= COALESCE(v_replayed_through, -1))
  ORDER BY e.version, pe.event_id
  LIMIT 1;

  IF v_straggler_event_id IS NOT NULL THEN
    UPDATE __SCHEMA__.wh_perspective_cursors
    SET status = status | 32,  -- RewindRequired flag (1 << 5)
        rewind_trigger_event_id = CASE
          WHEN NOT v_keep_flag OR v_straggler_version < v_trigger_version THEN v_straggler_event_id
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

COMMENT ON FUNCTION __SCHEMA__.complete_perspective_cursor_work(UUID, TEXT, UUID, JSONB, SMALLINT, TEXT, BIGINT) IS
'#2366: records a perspective run on its cursor in the stream''s one order (version): never moved back, the replay frontier stored and never lowered, the rewind flag cleared only at or past its trigger or once a replay read it, and a straggler detected by version, never one a replay read. Marks only the reported events processed.';

SELECT __SCHEMA__.drop_all_overloads('update_perspective_cursors');

-- Copied whole from 016 with #2366's delta: each pair's replay frontier (ReplayedThroughCommitSequence) is stored on its
-- cursor, never lowered. The position already moves by version and never back.
-- <docs>fundamentals/work-coordinator/batched-flushers#perspective-cursors</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorAdvanceSqlTests.cs:CompletePerspective_ProductionMode_CreatesTheMissingCursorAtTheCompletionsEventAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorAdvanceSqlTests.cs:CompletePerspective_ProductionMode_AdvancesAnExistingCursorAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorAdvanceSqlTests.cs:CompletePerspective_AnEarlierEventThanTheCursor_LeavesTheCursorWhereItIsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorAdvanceSqlTests.cs:CompletePerspective_TwoCompletionsForOneStreamInABatch_TheLaterEventWinsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorAdvanceSqlTests.cs:CompletePerspective_ACompletionWithNoEvent_CreatesNoCursorAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorAdvanceSqlTests.cs:CompletePerspective_AnEventTheStoreDoesNotHold_IsSkippedAndTheRestOfTheBatchRecordedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorOneOrderSqlTests.cs:CompletePerspective_AReplayFrontier_IsStoredOnTheCursor_AndNeverLoweredAsync</tests>
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
  gate AS (
    SELECT p.stream_id, p.perspective_name, p.replayed_through,
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
'Status is 2 for every reported pair with no unprocessed work left. #2366: stores the highest replay frontier reported.';

-- Repairs every cursor left in the stuck shape of #2366: flagged RewindRequired for a trigger stored after the cursor's
-- event (a higher version) whose id sorts below it. The repair only places the cursor: it moves to the last event of
-- its stream by version that sits before the first event still pending for the pair (never back, never past pending
-- work). The flag is kept. A genuine late arrival the receive chain flagged has the same id and version shape, and an
-- older build may have retired its row by a live apply the idempotency filter dropped, so a cleared flag could be a
-- rewind still owed; the fixed code resolves each kept flag with exactly one rewind instead. A pair left with no pending
-- work gets its trigger queued (unleased, so the claim loop picks it up), or the flag would wait for the stream's next
-- event and hold a blocking startup scan on every start meanwhile. Returns the cursors repaired.
-- <docs>fundamentals/perspectives/rewind#one-order-for-a-stream</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorOneOrderSqlTests.cs:Repair_ACursorInTheStuckShape_MovesToTheLastVersionBeforePendingWork_AndKeepsItsFlagAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorOneOrderSqlTests.cs:Repair_AGenuineLateArrivalWhoseRowIsRetired_KeepsItsFlag_AndQueuesItsRewindAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorOneOrderSqlTests.cs:Repair_AFlagWhoseTriggerIsStillPending_OrIsBelowTheCursorByVersion_IsLeftAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorOneOrderSqlTests.cs:Repair_ATriggerWhoseProcessedRowIsNotYetPurged_IsQueuedAgainOnThatRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorOneOrderSqlTests.cs:Repair_QueuesOneRowPerStuckPairWithNoPendingWork_NothingElse_AndARerunQueuesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveRewindLoopTests.cs:AStuckCursorRepairedOnDeploy_IsResolvedByExactlyOneRewind_AndNeverASecondAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_settle_id_ordered_rewind_flags() RETURNS INTEGER AS $$
DECLARE
  v_repaired INTEGER;
BEGIN
  WITH stuck AS (
    SELECT c.stream_id, c.perspective_name, c.rewind_trigger_event_id AS trigger_event_id, ce.version AS cursor_version,
      (SELECT min(pv.version)
         FROM __SCHEMA__.wh_perspective_events pe
         JOIN __SCHEMA__.wh_event_store pv ON pv.event_id = pe.event_id
        WHERE pe.stream_id = c.stream_id AND pe.perspective_name = c.perspective_name
          AND pe.processed_at IS NULL) AS first_pending_version
    FROM __SCHEMA__.wh_perspective_cursors c
    JOIN __SCHEMA__.wh_event_store ce ON ce.event_id = c.last_event_id
    JOIN __SCHEMA__.wh_event_store te ON te.event_id = c.rewind_trigger_event_id AND te.stream_id = c.stream_id
    WHERE (c.status & 32) = 32
      AND te.version > ce.version
      AND te.event_id < c.last_event_id
  ),
  positioned AS (
    SELECT s.stream_id, s.perspective_name, s.trigger_event_id, s.first_pending_version,
      (SELECT e.event_id
         FROM __SCHEMA__.wh_event_store e
        WHERE e.stream_id = s.stream_id
          AND e.version > s.cursor_version
          AND e.version < COALESCE(s.first_pending_version, 9223372036854775807)
        ORDER BY e.version DESC
        LIMIT 1) AS position_event_id
    FROM stuck s
  ),
  moved AS (
    UPDATE __SCHEMA__.wh_perspective_cursors c
    SET last_event_id = p.position_event_id
    FROM positioned p
    WHERE c.stream_id = p.stream_id AND c.perspective_name = p.perspective_name
      AND p.position_event_id IS NOT NULL
    RETURNING c.stream_id, c.perspective_name
  ),
  queued AS (
    INSERT INTO __SCHEMA__.wh_perspective_events (
      event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at, priority)
    SELECT gen_random_uuid(), p.stream_id, p.perspective_name, p.trigger_event_id,
      __SCHEMA__.compute_partition(p.stream_id, 10000), 1, 0, NOW(), 150
    FROM positioned p
    WHERE p.first_pending_version IS NULL
    -- The trigger's processed row may not be purged yet (debug mode keeps it): it is made pending again, so the rewind
    -- runs, instead of failing the migration on the row's unique key.
    ON CONFLICT ON CONSTRAINT uq_perspective_event DO UPDATE
      SET processed_at = NULL, status = EXCLUDED.status, attempts = 0, error = NULL, failure_reason = NULL,
          instance_id = NULL, lease_expiry = NULL, claimed_at = NULL, scheduled_for = NULL
    RETURNING stream_id
  )
  SELECT count(*) INTO v_repaired FROM moved;
  RETURN v_repaired;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_settle_id_ordered_rewind_flags() IS
'#2366: places each cursor flagged RewindRequired for a trigger stored after it (higher version, lower id) at its stream''s last version before pending work, never back, and keeps the flag for one rewind; a pair with no pending work gets its trigger queued. Returns the cursors repaired.';

SELECT __SCHEMA__.wh_settle_id_ordered_rewind_flags();
