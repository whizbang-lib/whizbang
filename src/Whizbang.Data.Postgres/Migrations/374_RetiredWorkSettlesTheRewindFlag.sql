-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 374_RetiredWorkSettlesTheRewindFlag.sql
-- Date: 2026-11-09
-- Description: Retiring a perspective pair's last pending work row settles a cursor still flagged RewindRequired.
--
--              The worker reports a cursor completion and retires the group's work rows through two queues that every
--              consumer loop flushes, onto one channel the flusher cuts by size and time. The cursor can therefore reach
--              complete_perspective in an earlier flush than its rows. update_perspective_cursors then still sees the
--              rows pending, so it keeps the cursor's status, RewindRequired included, and the flush that later retires
--              the rows carries no cursor for the pair. Nothing came back to the cursor: it stayed flagged with no work,
--              every later event on the stream took a full rewind, and a blocking startup rewind scan found it on every
--              restart.
--
--              complete_perspective now settles, for the pairs whose rows it retired, a flagged cursor with nothing left
--              pending: status 2, the same rule update_perspective_cursors applies when both arrive in one flush. Only
--              flagged cursors are written, so the common flush adds one primary key probe per retired pair and no
--              write. wh_settle_stale_rewind_flags applies the same rule to every cursor, and this migration runs it once
--              for the cursors an older build left flagged; it is idempotent (a flag with pending work is never touched).
--
--              Number 374: 360 to 373 are taken on develop or by changes in flight.
--
-- Dependencies: 016 (update_perspective_cursors), 029 (complete_perspective, copied whole with the delta below),
--               241 (process_perspective_event_completions, returns the retired pairs)
-- Constants: the double-underscore tokens in this file are substituted from Migrations/constants.txt at apply time.
-- Objects: _wh_settle_rewind_flags, wh_settle_stale_rewind_flags, complete_perspective

-- The flagged cursors among the given pairs that have no pending work left are settled: status 2, as
-- update_perspective_cursors leaves a pair whose work is complete. Returns how many were settled.
-- <docs>fundamentals/work-coordinator/batched-flushers#perspective-cursors</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveRewindFlagSettleSqlTests.cs:CompletePerspective_TheCursorFlushedBeforeItsWork_ClearsTheRewindFlagWhenTheWorkIsRetiredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveRewindFlagSettleSqlTests.cs:CompletePerspective_RetiringOneOfTwoPendingRows_KeepsTheRewindFlagAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveRewindFlagSettleSqlTests.cs:CompletePerspective_AFlaggedCursorOfAnotherPerspective_IsLeftAloneAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_settle_rewind_flags(
  p_stream_ids UUID[],
  p_perspective_names TEXT[]
) RETURNS INTEGER AS $$
DECLARE
  v_settled INTEGER;
BEGIN
  IF p_stream_ids IS NULL OR cardinality(p_stream_ids) = 0 THEN
    RETURN 0;
  END IF;

  UPDATE __SCHEMA__.wh_perspective_cursors pc
  SET status = 2
  FROM (SELECT DISTINCT u.sid, u.pname FROM unnest(p_stream_ids, p_perspective_names) AS u(sid, pname)) p
  WHERE pc.stream_id = p.sid
    AND pc.perspective_name = p.pname
    AND (pc.status & 32) = 32
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
      WHERE pe.stream_id = pc.stream_id
        AND pe.perspective_name = pc.perspective_name
        AND pe.processed_at IS NULL);
  GET DIAGNOSTICS v_settled = ROW_COUNT;
  RETURN v_settled;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_settle_rewind_flags(UUID[], TEXT[]) IS
'374: settles the RewindRequired cursors among the given (stream, perspective) pairs that have no pending work left '
'(status 2, as update_perspective_cursors leaves a completed pair). Returns how many were settled.';

-- Every cursor flagged RewindRequired with no pending work is settled. Run once by migration 374 for the cursors an
-- older build left flagged; safe to run again. Returns how many were settled.
-- <docs>fundamentals/work-coordinator/batched-flushers#perspective-cursors</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveRewindFlagSettleSqlTests.cs:SettleStaleRewindFlags_ClearsFlagsWithNoPendingWork_AndKeepsFlagsThatStillHaveSomeAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_settle_stale_rewind_flags() RETURNS INTEGER AS $$
DECLARE
  v_settled INTEGER;
BEGIN
  UPDATE __SCHEMA__.wh_perspective_cursors pc
  SET status = 2
  WHERE (pc.status & 32) = 32
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
      WHERE pe.stream_id = pc.stream_id
        AND pe.perspective_name = pc.perspective_name
        AND pe.processed_at IS NULL);
  GET DIAGNOSTICS v_settled = ROW_COUNT;
  RETURN v_settled;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_settle_stale_rewind_flags() IS
'374: settles every cursor flagged RewindRequired that has no pending work left (status 2). Run once by migration 374; '
'idempotent. Returns how many were settled.';

-- complete_perspective: last word 029, copied whole with the 374 delta (the retired pairs settle a flagged cursor).
SELECT __SCHEMA__.drop_all_overloads('complete_perspective');

-- <docs>fundamentals/work-coordinator/batched-flushers#perspective-cursors</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveRewindFlagSettleSqlTests.cs:CompletePerspective_TheCursorFlushedBeforeItsWork_ClearsTheRewindFlagWhenTheWorkIsRetiredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveRewindFlagSettleSqlTests.cs:CompletePerspective_DebugMode_ClearsTheRewindFlagWhenTheLastRowIsStampedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorAdvanceSqlTests.cs:CompletePerspective_ProductionMode_AdvancesAnExistingCursorAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.complete_perspective(
  p_cursors JSONB,                          -- [{StreamId, PerspectiveName}] for cursor advancement
  p_event_work_ids UUID[],                  -- event_work_id rows to mark complete
  p_debug_mode BOOLEAN DEFAULT FALSE        -- production: DELETE rows; debug: retain with processed_at stamped
) RETURNS VOID AS $$
DECLARE
  v_now TIMESTAMPTZ := NOW();
  v_completions JSONB;
  v_retired_streams UUID[];
  v_retired_perspectives TEXT[];
BEGIN
  -- Build the JSONB array of completions from the work-id list. StatusFlags = 1 = Completed.
  IF p_event_work_ids IS NOT NULL AND array_length(p_event_work_ids, 1) IS NOT NULL THEN
    SELECT jsonb_agg(jsonb_build_object('EventWorkId', wid, 'StatusFlags', 1))
    INTO v_completions
    FROM unnest(p_event_work_ids) AS wid;

    -- 374: the retired rows' pairs are kept, so a flagged cursor whose completion an earlier flush carried can settle.
    SELECT array_agg(r.stream_id), array_agg(r.perspective_name::TEXT)
    INTO v_retired_streams, v_retired_perspectives
    FROM __SCHEMA__.process_perspective_event_completions(
      COALESCE(v_completions, '[]'::JSONB),
      v_now,
      p_debug_mode  -- production: DELETE; debug: stamp processed_at and retain
    ) r;
  END IF;

  -- Advance cursors for the (StreamId, PerspectiveName) pairs in p_cursors.
  IF p_cursors IS NOT NULL AND jsonb_array_length(p_cursors) > 0 THEN
    PERFORM __SCHEMA__.update_perspective_cursors(p_cursors, p_debug_mode);
  END IF;

  -- 374: a pair whose last pending row this flush retired has nothing left for a rewind to replay. When its cursor's
  -- completion came in an earlier flush, update_perspective_cursors saw the row pending and kept RewindRequired; settle
  -- it now. A pair with work still pending keeps its flag.
  PERFORM __SCHEMA__._wh_settle_rewind_flags(v_retired_streams, v_retired_perspectives);

  -- Slice 27: routed NOTIFY for downstream wakeups (e.g., perspective-sync awaiters
  -- watching for cursor advancement). Resolve stream IDs from the cursor payload and
  -- route to each stream's owning instance only. Polling backstop catches streams
  -- whose ownership isn't currently known in wh_active_streams.
  IF p_cursors IS NOT NULL AND jsonb_array_length(p_cursors) > 0 THEN
    DECLARE
      v_cursor_stream_ids UUID[];
    BEGIN
      SELECT array_agg(DISTINCT (elem ->> __ENVELOPE_FIELD_STREAM_ID__)::UUID)
      INTO v_cursor_stream_ids
      FROM jsonb_array_elements(p_cursors) AS elem
      WHERE elem ->> __ENVELOPE_FIELD_STREAM_ID__ IS NOT NULL;

      IF v_cursor_stream_ids IS NOT NULL THEN
        PERFORM __SCHEMA__.notify_instance_owners(__CATEGORY_PERSPECTIVE__, v_cursor_stream_ids);
      END IF;
    END;
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.complete_perspective IS
'Batched perspective completion. Production (p_debug_mode=FALSE, default): DELETEs event-work rows and advances cursors. Debug (p_debug_mode=TRUE): retains rows with processed_at stamped (eligible_perspective_events filters processed_at IS NULL → row treated as deleted by claim_work). Single round-trip. Coalesced flush from C# PerspectiveCompletionFlushWorker. Rings the owners of the cursors'' streams so peers know cursors moved. 374: a retired pair with nothing left pending settles a cursor still flagged RewindRequired, whichever flush carried its completion.';

-- 374: the cursors an older build left flagged with no pending work are settled once.
SELECT __SCHEMA__.wh_settle_stale_rewind_flags();
