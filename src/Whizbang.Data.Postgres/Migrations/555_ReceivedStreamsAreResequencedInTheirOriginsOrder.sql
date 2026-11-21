-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 555_ReceivedStreamsAreResequencedInTheirOriginsOrder.sql
-- Date: 2026-11-21
-- Description: A received stream stored out of its origin's order is re-sequenced in place, and its perspectives are
--              rewound from its start (#1629).
--
--              Before #1607 a receiver could store one producer stream's events out of the producer's order (a
--              stream's initializer after its field events). A replay deals each origin's events into that origin's
--              slots in origin order (ReplayEventOrder, #2257), but everything that reads a stream by version (a read
--              from a version, a rewind floor, a superseding comparison without origin sequences, a driver whose
--              reads carry no origin) still sees the stored order. The operator re-sequence (IReceivedStreamResequencer)
--              stores the order a replay folds: each origin's events in its own order, in the versions those events
--              already hold. Local events, and the interleaving of different origins, keep their versions. The order
--              itself is decided in C# by the replay's own rule (ReceivedStreamOrder); this file finds the streams
--              and applies a decided order.
--
--                the window    wh_received_stream_order_window: the next p_window streams after a stream id, each
--                              marked out of order when, for one origin, a later version holds a lower id, or a later
--                              positioned event a lower origin sequence. A stream in neither shape is in its origins'
--                              order (shown in ReceivedStreamResequenceSqlTests over every arrangement), so the planner
--                              reads only the marked streams. A stream that holds ephemeral or collective events, or
--                              has archived history, is named with why it is left alone.
--                the locks     wh_try_lock_streams: the emit chains' per-stream lock, tried in stream id order and never
--                              waited for. A stream being written is reported busy and left for a re-run, so the tool
--                              never stalls a writer behind another stream's wait.
--                the apply     wh_resequence_stream: under the stream's lock, checks that every named event is still
--                              at the version the plan read (otherwise 'changed', nothing written), renumbers through
--                              negatives (idx_event_store_stream is unique and not deferrable), and rewinds the stream's
--                              perspectives: its snapshots go (each held a fold in the old order), every cursor but the
--                              collective sink's is flagged RewindRequired with the stream's first event as trigger and
--                              its replay frontier cleared, and that trigger is queued for each, a processed row made
--                              pending again. The worker's rewind then finds no snapshot below the first event and
--                              replays the whole stream. A completion racing the flag cannot settle it: the queued
--                              trigger sits below the cursor, uncovered, so complete_perspective_cursor_work flags it
--                              again as a straggler.
--
--              The versions are only permuted, so the stream's version set, its first and last version, the stream
--              digests (folded by event id) and commit sequences are unchanged; an append waiting on the lock lands
--              where it would have. Audit records keep the position they recorded (see the docs page).
--
--              Number 555: assigned to this change.
--
-- Reversal: reversible (the three functions are dropped; streams already re-sequenced stay in their origins' order)
-- Dependencies: 009 (wh_perspective_events, uq_perspective_event), 033 (rewind columns), 085 (wh_event_archive),
--               268 (wh_archive_manifest), 455 (replayed_through_commit_sequence, complete_perspective_cursor_work),
--               537 (the emit chains' per-stream lock key)
-- Objects: wh_received_stream_order_window, wh_try_lock_streams, wh_resequence_stream
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- ---------------------------------------------------------------------------------------------
-- wh_received_stream_order_window: the next window of streams, and which of them are out of their origins' order.
-- ---------------------------------------------------------------------------------------------

-- <docs>operations/infrastructure/resequencing-received-streams#finding-the-streams</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceivedStreamResequenceSqlTests.cs:Window_MarksEveryStreamThePlannerWouldMove_AndNoStreamInOrder_OverEveryArrangementAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceivedStreamResequenceSqlTests.cs:Resequence_EphemeralCollectiveAndArchivedStreams_AreLeftAloneAndReportedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_received_stream_order_window(p_after_stream_id UUID, p_window INTEGER)
RETURNS TABLE(stream_id UUID, out_of_order BOOLEAN, skip_reason TEXT) AS $$
  WITH w AS (
    SELECT DISTINCT es.stream_id
    FROM __SCHEMA__.wh_event_store es
    WHERE es.stream_id > p_after_stream_id
    ORDER BY es.stream_id
    LIMIT p_window
  ),
  received AS (
    SELECT es.stream_id, es.event_id, es.version, es.origin_service_id,
           COALESCE(es.origin_commit_sequence, 0) AS seq,
           lag(es.event_id) OVER (PARTITION BY es.stream_id, es.origin_service_id ORDER BY es.version) AS prev_id
    FROM __SCHEMA__.wh_event_store es
    JOIN w ON w.stream_id = es.stream_id
    WHERE es.origin_service_id IS NOT NULL
      AND es.origin_service_id <> __EMPTY_UUID__::uuid
  ),
  positioned AS (
    SELECT r.stream_id, r.seq,
           lag(r.seq) OVER (PARTITION BY r.stream_id, r.origin_service_id ORDER BY r.version) AS prev_seq
    FROM received r
    WHERE r.seq > 0
  ),
  marked AS (
    SELECT w.stream_id,
           EXISTS (SELECT 1 FROM received r WHERE r.stream_id = w.stream_id AND r.prev_id > r.event_id)
           OR EXISTS (SELECT 1 FROM positioned p WHERE p.stream_id = w.stream_id AND p.prev_seq > p.seq) AS out_of_order
    FROM w
  )
  SELECT m.stream_id, m.out_of_order,
         CASE
           WHEN NOT m.out_of_order THEN NULL
           WHEN EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_store es
                        WHERE es.stream_id = m.stream_id AND COALESCE(es.flags, 0) & 8 <> 0) THEN 'Ephemeral'
           WHEN EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_store es
                        WHERE es.stream_id = m.stream_id AND COALESCE(es.flags, 0) & 1 <> 0) THEN 'Collective'
           WHEN EXISTS (SELECT 1 FROM __SCHEMA__.wh_archive_manifest am WHERE am.stream_id = m.stream_id)
             OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_archive ea WHERE ea.stream_id = m.stream_id) THEN 'Archived'
         END
  FROM marked m
  ORDER BY m.stream_id;
$$ LANGUAGE sql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_received_stream_order_window(UUID, INTEGER) IS
'#1629: the next p_window streams after p_after_stream_id, each marked out of its origins'' order (for one origin, a later version holding a lower id or a later positioned event a lower origin sequence), with why an out-of-order stream is left alone (Ephemeral, Collective or Archived, the names of ResequenceSkipReason). Read-only.';

-- ---------------------------------------------------------------------------------------------
-- wh_try_lock_streams: the emit chains' per-stream locks, tried in stream id order, never waited for.
-- ---------------------------------------------------------------------------------------------

-- <docs>operations/infrastructure/resequencing-received-streams#on-a-live-system</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceivedStreamResequenceSqlTests.cs:Resequence_AStreamBeingWritten_IsReportedBusy_AndARerunFixesItAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_try_lock_streams(p_stream_ids UUID[])
RETURNS SETOF UUID AS $$
DECLARE
  v_stream UUID;
BEGIN
  FOR v_stream IN SELECT DISTINCT s FROM unnest(p_stream_ids) AS s ORDER BY s LOOP
    IF pg_try_advisory_xact_lock(hashtext('wh_event_store:' || v_stream::text)) THEN
      RETURN NEXT v_stream;
    END IF;
  END LOOP;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_try_lock_streams(UUID[]) IS
'#1629: tries the emit chains'' per-stream advisory lock (hashtext(''wh_event_store:'' || stream_id)) of each stream, in stream id order, for the calling transaction, and returns the streams it took. Never waits.';

-- ---------------------------------------------------------------------------------------------
-- wh_resequence_stream: applies a decided order to one stream and rewinds its perspectives from its start.
-- ---------------------------------------------------------------------------------------------

-- <docs>operations/infrastructure/resequencing-received-streams#what-a-re-sequence-changes</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceivedStreamResequenceSqlTests.cs:Resequence_ASingleOriginStoredOutOfOrder_IsStoredInTheOriginsOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceivedStreamResequenceSqlTests.cs:Resequence_RewindsEveryPerspectiveOfTheStreamFromItsStart_AndLeavesTheCollectiveSinkAloneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceivedStreamResequenceSqlTests.cs:Apply_AnAppendDuringTheRun_WaitsForIt_AndLandsAfterTheStreamsLastVersionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceivedStreamResequenceSqlTests.cs:Apply_APlanTheStreamNoLongerMatches_ChangesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceivedStreamResequenceSqlTests.cs:Apply_APlanThatIsNotAPermutationOfItsVersions_IsRefusedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceivedStreamResequenceSqlTests.cs:Apply_ACompletionThatRacesTheFlag_LeavesTheRewindAskedForAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_resequence_stream(
  p_stream_id UUID,
  p_event_ids UUID[],
  p_from_versions INTEGER[],
  p_to_versions INTEGER[],
  p_now TIMESTAMPTZ
) RETURNS TABLE(outcome TEXT, perspectives_rewound INTEGER, snapshots_removed INTEGER) AS $$
DECLARE
  v_count INTEGER := COALESCE(array_length(p_event_ids, 1), 0);
  v_matched INTEGER;
  v_snapshots INTEGER;
  v_rewound INTEGER;
  v_trigger UUID;
BEGIN
  IF v_count = 0
     OR COALESCE(array_length(p_from_versions, 1), 0) <> v_count
     OR COALESCE(array_length(p_to_versions, 1), 0) <> v_count
     OR (SELECT count(DISTINCT e) FROM unnest(p_event_ids) AS e) <> v_count
     OR (SELECT array_agg(v ORDER BY v) FROM unnest(p_from_versions) AS v)
        IS DISTINCT FROM (SELECT array_agg(v ORDER BY v) FROM unnest(p_to_versions) AS v) THEN
    RAISE EXCEPTION 'wh_resequence_stream: the plan for stream % is not a permutation of the versions its events hold', p_stream_id
      USING ERRCODE = '22023';
  END IF;

  -- The emit chains' lock (re-entrant when the caller already took it), so no append numbers the stream meanwhile.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || p_stream_id::text));

  -- Every named event must still be where the plan read it; the rows are locked for the renumber.
  SELECT count(*) INTO v_matched
  FROM (
    SELECT es.event_id
    FROM __SCHEMA__.wh_event_store es
    JOIN unnest(p_event_ids, p_from_versions) AS p(event_id, from_version)
      ON p.event_id = es.event_id AND es.version = p.from_version
    WHERE es.stream_id = p_stream_id
    FOR UPDATE OF es
  ) AS held;
  IF v_matched <> v_count THEN
    RETURN QUERY SELECT 'changed'::TEXT, 0, 0;
    RETURN;
  END IF;

  -- Through negatives below zero (a stream may start at version 0), so no intermediate row meets another's version.
  UPDATE __SCHEMA__.wh_event_store es
  SET version = -(es.version + 1)
  WHERE es.stream_id = p_stream_id AND es.event_id = ANY(p_event_ids);

  UPDATE __SCHEMA__.wh_event_store es
  SET version = p.to_version
  FROM unnest(p_event_ids, p_to_versions) AS p(event_id, to_version)
  WHERE es.stream_id = p_stream_id AND es.event_id = p.event_id;

  -- Every snapshot of the stream holds a fold in the old order.
  DELETE FROM __SCHEMA__.wh_perspective_snapshots sn WHERE sn.stream_id = p_stream_id;
  GET DIAGNOSTICS v_snapshots = ROW_COUNT;

  -- The rewind replays from the stream's first event: no snapshot sits below it.
  SELECT es.event_id INTO v_trigger
  FROM __SCHEMA__.wh_event_store es
  WHERE es.stream_id = p_stream_id
  ORDER BY es.version
  LIMIT 1;

  WITH flagged AS (
    UPDATE __SCHEMA__.wh_perspective_cursors c
    SET status = c.status | 32,  -- RewindRequired flag (1 << 5)
        rewind_trigger_event_id = v_trigger,
        rewind_flagged_at = p_now,
        rewind_first_flagged_at = COALESCE(c.rewind_first_flagged_at, p_now),
        replayed_through_commit_sequence = NULL
    WHERE c.stream_id = p_stream_id
      AND c.perspective_name <> '__collective__'
    RETURNING c.perspective_name
  ),
  queued AS (
    INSERT INTO __SCHEMA__.wh_perspective_events (
      event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at, priority)
    SELECT gen_random_uuid(), p_stream_id, f.perspective_name, v_trigger,
      __SCHEMA__.compute_partition(p_stream_id, 10000), 1, 0, p_now, __PRIORITY_BACKGROUND__
    FROM flagged f
    -- A processed row of the trigger (debug mode keeps it) is made pending again; a pending one is left as it is.
    ON CONFLICT ON CONSTRAINT uq_perspective_event DO UPDATE
      SET processed_at = NULL, status = EXCLUDED.status, attempts = 0, error = NULL, failure_reason = NULL,
          instance_id = NULL, lease_expiry = NULL, claimed_at = NULL, scheduled_for = NULL
      WHERE __SCHEMA__.wh_perspective_events.processed_at IS NOT NULL
    RETURNING 1
  )
  SELECT count(*) INTO v_rewound FROM flagged;

  RETURN QUERY SELECT 'resequenced'::TEXT, v_rewound, v_snapshots;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_resequence_stream(UUID, UUID[], INTEGER[], INTEGER[], TIMESTAMPTZ) IS
'#1629: stores each named event of p_stream_id at its new version (a permutation of the versions they hold, else 22023), under the stream''s lock, when every one is still at its planned old version (else ''changed'' and nothing written); removes the stream''s snapshots and flags every cursor but the collective sink''s RewindRequired at the stream''s first event, queuing that trigger, so its perspectives replay it from the start.';
