-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 590_OriginPositionFillRewindsEachPerspectiveFromWhereItsOrderChanged.sql
-- Date: 2026-11-24
-- Description: Filling missing origin positions rewinds each perspective of a stream from the first version its own
--              replay order changed at, and only when it changed (#2571).
--
--              561 decided once per stream whether the fill changed the replay order, then rewound every perspective
--              of the stream from its first event and deleted all of its snapshots. A perspective replays only the
--              event types it handles, and the id merge that places an unpositioned event among its origin's
--              positioned ones is not stable under taking a subset, so the stream-wide answer was wrong both ways: it
--              rewound perspectives whose own order the fill left alone, and it missed a perspective whose own order
--              changed while the stream's did not (its fold then stayed wrong with nothing pending to repair it).
--
--                the plan        wh_origin_position_fill_plan also returns each event's type.
--                the readers     wh_origin_position_fill_readers: every cursor of the locked streams but the collective
--                                sink's, with the types its perspective's association rows name, or NULL when it has
--                                none (the caller then compares every event of the origin, the conservative answer).
--                                The caller compares each cursor's own events before and after the fill by the replay's
--                                rule and names, per cursor that changed, the event stored at the first changed slot.
--                the rewind      _wh_rewind_perspectives_from flags each named cursor RewindRequired at that event with
--                                its replay frontier cleared, deletes only that perspective's snapshots at the event's
--                                version or above (and any that records no version), and queues the trigger. A cursor
--                                already flagged keeps its trigger when that one is lower. Only cursors not flagged
--                                before are counted.
--                the fill        wh_fill_origin_positions takes the per-cursor rewinds in place of the stream list.
--
--              Cost: the readers read one cursor index range per locked stream and the association rows by target; the
--              rewind touches only the named cursors and their snapshots by key. Fewer cursors are flagged than before,
--              and each replays from its first changed version instead of the stream's start.
--
--              Number 590: reserved for this change; 562 to 589 are not used.
--
-- Reversal: reversible (561's plan, rewind and fill come back)
-- Dependencies: 561 (the fill and its plan), 454 (wh_reconcile_redelivered_bundles_counting_satisfied), 455 (the
--               cursor's rewind flag and replay frontier), 209 (snapshot_version)
-- Objects: wh_origin_position_fill_plan, wh_origin_position_fill_readers, _wh_rewind_perspectives_from,
--          _wh_rewind_streams_from_start, wh_fill_origin_positions
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- Copied from 561 with #2571's delta: each event's type is returned, so the caller can take each perspective's events.
SELECT __SCHEMA__.drop_all_overloads('wh_origin_position_fill_plan');
-- <docs>resilience/stream-integrity#filling-missing-origin-positions</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:FillPlan_ReturnsTheOriginsEventsOfTheStreamsTheFillTouches_OnlyAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_origin_position_fill_plan(p_origin UUID, p_event_ids UUID[])
RETURNS TABLE(stream_id UUID, event_id UUID, version BIGINT, origin_commit_sequence BIGINT, event_type TEXT) AS $$
DECLARE
  v_streams UUID[];
BEGIN
  SELECT array_agg(DISTINCT es.stream_id ORDER BY es.stream_id) INTO v_streams
  FROM __SCHEMA__.wh_event_store es
  WHERE es.event_id = ANY(p_event_ids)
    AND es.origin_service_id = p_origin
    AND es.origin_commit_sequence IS NULL;
  IF v_streams IS NULL THEN
    RETURN;
  END IF;

  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || s.sid::text))
  FROM unnest(v_streams) AS s(sid);

  RETURN QUERY
  SELECT es.stream_id, es.event_id, es.version::bigint, es.origin_commit_sequence, es.event_type::text
  FROM __SCHEMA__.wh_event_store es
  WHERE es.stream_id = ANY(v_streams)
    AND es.origin_service_id = p_origin
  ORDER BY es.stream_id, es.version;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_origin_position_fill_plan(UUID, UUID[]) IS
'590 (#2571, from 561): locks the streams holding a named event received from p_origin without a position (the emit chain''s per-stream lock, in stream order) and returns p_origin''s events of those streams by version, with their types, for the caller to decide which perspectives the fill reorders.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- Who reads the streams: every cursor but the collective sink's, with the event types its perspective handles.
SELECT __SCHEMA__.drop_all_overloads('wh_origin_position_fill_readers');
-- <docs>resilience/stream-integrity#filling-missing-origin-positions</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:FillReaders_NameEachCursorsAssociatedTypes_AndNoneForAPerspectiveWithoutAssociationsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_origin_position_fill_readers(p_stream_ids UUID[])
RETURNS TABLE(stream_id UUID, perspective_name TEXT, event_types TEXT[]) AS $$
  SELECT c.stream_id, c.perspective_name::text,
         (SELECT array_agg(DISTINCT ma.normalized_message_type::text ORDER BY ma.normalized_message_type::text)
          FROM __SCHEMA__.wh_message_associations ma
          WHERE ma.target_name = c.perspective_name
            AND ma.association_type = __CATEGORY_PERSPECTIVE__
            AND ma.normalized_message_type IS NOT NULL)
  FROM __SCHEMA__.wh_perspective_cursors c
  WHERE c.stream_id = ANY(p_stream_ids)
    AND c.perspective_name <> '__collective__'
  ORDER BY c.stream_id, c.perspective_name;
$$ LANGUAGE sql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_origin_position_fill_readers(UUID[]) IS
'590 (#2571): every perspective cursor of the streams but the collective sink''s, with the normalized event types its perspective''s association rows name; NULL types when it has none, so the caller compares all of the origin''s events for it.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- Rewinds each named cursor from its trigger: only that perspective's snapshots at the trigger's version or above go
-- (the ones below hold a fold the replay still folds), the cursor is flagged RewindRequired at the lower of its trigger
-- and any it already had, its replay frontier is cleared, and that trigger is queued. Returns the cursors newly flagged.
SELECT __SCHEMA__.drop_all_overloads('_wh_rewind_perspectives_from');
-- <docs>resilience/stream-integrity#filling-missing-origin-positions</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Fill_RewindsFromTheFirstChangedVersion_KeepingTheSnapshotsBelowItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Fill_RewindsALaggingCursorFromTheFirstChangedVersion_KeepingItsEarlierSnapshotAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Fill_MergingWithAFlagAlreadySet_KeepsTheLowerTrigger_AndCountsOnlyNewlyFlaggedCursorsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Fill_ThatKeepsTheStreamsOrder_ButReordersAPerspectivesOwnEvents_RewindsThatPerspectiveAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_rewind_perspectives_from(
  p_stream_ids UUID[],
  p_perspective_names TEXT[],
  p_trigger_event_ids UUID[],
  p_now TIMESTAMPTZ
) RETURNS INTEGER AS $$
DECLARE
  v_rewound INTEGER;
BEGIN
  IF COALESCE(cardinality(p_stream_ids), 0) = 0 THEN
    RETURN 0;
  END IF;
  IF cardinality(p_stream_ids) IS DISTINCT FROM cardinality(p_perspective_names)
     OR cardinality(p_stream_ids) IS DISTINCT FROM cardinality(p_trigger_event_ids) THEN
    RAISE EXCEPTION '_wh_rewind_perspectives_from: % streams, % perspectives and % triggers must pair by position',
      cardinality(p_stream_ids), cardinality(p_perspective_names), cardinality(p_trigger_event_ids);
  END IF;

  -- A snapshot at the trigger's version or above holds a fold in the old order; one that records no version cannot
  -- say, so it goes too. A snapshot below the trigger folds what the replay still folds there, and is kept.
  WITH rewinds AS (
    SELECT r.sid, r.pname, es.version AS trigger_version
    FROM unnest(p_stream_ids, p_perspective_names, p_trigger_event_ids) AS r(sid, pname, tid)
    JOIN __SCHEMA__.wh_event_store es ON es.event_id = r.tid AND es.stream_id = r.sid
    WHERE r.pname <> '__collective__'
  )
  DELETE FROM __SCHEMA__.wh_perspective_snapshots sn
  USING rewinds r
  WHERE sn.stream_id = r.sid
    AND sn.perspective_name = r.pname
    AND (sn.snapshot_version IS NULL OR sn.snapshot_version >= r.trigger_version);

  WITH rewinds AS (
    SELECT r.sid, r.pname, r.tid, es.version AS trigger_version
    FROM unnest(p_stream_ids, p_perspective_names, p_trigger_event_ids) AS r(sid, pname, tid)
    JOIN __SCHEMA__.wh_event_store es ON es.event_id = r.tid AND es.stream_id = r.sid
    WHERE r.pname <> '__collective__'
  ),
  targets AS (
    SELECT c.stream_id, c.perspective_name, (c.status & 32) <> 0 AS was_flagged, r.tid AS trigger_id, r.trigger_version,
           (SELECT es.version FROM __SCHEMA__.wh_event_store es
            WHERE es.event_id = c.rewind_trigger_event_id AND es.stream_id = c.stream_id) AS held_version,
           c.rewind_trigger_event_id AS held_trigger
    FROM __SCHEMA__.wh_perspective_cursors c
    JOIN rewinds r ON r.sid = c.stream_id AND r.pname = c.perspective_name
  ),
  flagged AS (
    UPDATE __SCHEMA__.wh_perspective_cursors c
    SET status = c.status | 32,  -- RewindRequired flag (1 << 5)
        -- A flag already set from a lower version replays this one too, so its trigger is kept.
        rewind_trigger_event_id = CASE
          WHEN cur.was_flagged AND cur.held_version IS NOT NULL AND cur.held_version < cur.trigger_version THEN cur.held_trigger
          ELSE cur.trigger_id
        END,
        rewind_flagged_at = p_now,
        rewind_first_flagged_at = COALESCE(c.rewind_first_flagged_at, p_now),
        replayed_through_commit_sequence = NULL
    FROM targets cur
    WHERE c.stream_id = cur.stream_id
      AND c.perspective_name = cur.perspective_name
    RETURNING c.stream_id, c.perspective_name, c.rewind_trigger_event_id AS trigger_id, cur.was_flagged
  ),
  queued AS (
    INSERT INTO __SCHEMA__.wh_perspective_events (
      event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at, priority)
    SELECT gen_random_uuid(), f.stream_id, f.perspective_name, f.trigger_id,
      __SCHEMA__.compute_partition(f.stream_id, 10000), 1, 0, p_now, __PRIORITY_BACKGROUND__
    FROM flagged f
    -- A processed row of the trigger (debug mode keeps it) is made pending again; a pending one is left as it is.
    ON CONFLICT ON CONSTRAINT uq_perspective_event DO UPDATE
      SET processed_at = NULL, status = EXCLUDED.status, attempts = 0, error = NULL, failure_reason = NULL,
          instance_id = NULL, lease_expiry = NULL, claimed_at = NULL, scheduled_for = NULL
      WHERE __SCHEMA__.wh_perspective_events.processed_at IS NOT NULL
    RETURNING 1
  )
  SELECT count(*) FILTER (WHERE NOT f.was_flagged) INTO v_rewound FROM flagged f;
  RETURN v_rewound;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_rewind_perspectives_from(UUID[], TEXT[], UUID[], TIMESTAMPTZ) IS
'590 (#2571): for each (stream, perspective, trigger) triple, deletes that perspective''s snapshots at the trigger''s version or above (or with no version), flags its cursor RewindRequired at the lower of the trigger and any it already had, clears its replay frontier and queues the trigger. Returns the cursors that were not flagged before.';

-- The stream-wide rewind 561 used is replaced by the per-perspective one above.
DROP FUNCTION IF EXISTS __SCHEMA__._wh_rewind_streams_from_start(UUID[], TIMESTAMPTZ);

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- Copied from 561 with #2571's delta: the rewinds are per cursor (stream, perspective, trigger), not per stream.
SELECT __SCHEMA__.drop_all_overloads('wh_fill_origin_positions');
-- <docs>resilience/stream-integrity#filling-missing-origin-positions</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Fill_FillsOnlyNullPositions_AndNeverOverwritesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Fill_RefoldsTheSealedEpochAFilledEventFallsIn_AndTheHeldFoldLetsItGoAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Fill_RecordsUnknownAnswers_AndTheAskNeverReturnsThemAgainAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Fill_ThatChangesAStreamsOriginOrder_RewindsItsPerspectivesFromTheStartAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Fill_ThatReordersTheStream_ButNotAPerspectivesOwnEvents_LeavesThatPerspectiveAndItsSnapshotAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_fill_origin_positions(
  p_origin UUID,
  p_event_ids UUID[],
  p_sequences BIGINT[],
  p_unknown_ids UUID[],
  p_rewind_streams UUID[],
  p_rewind_perspectives TEXT[],
  p_rewind_triggers UUID[],
  p_now TIMESTAMPTZ
) RETURNS TABLE(filled INTEGER, unknown_recorded INTEGER, perspectives_rewound INTEGER) AS $$
DECLARE
  v_ids UUID[];
  v_seqs BIGINT[];
  v_filled INTEGER := 0;
  v_unknown INTEGER := 0;
  v_rewound INTEGER;
BEGIN
  IF cardinality(p_event_ids) IS DISTINCT FROM cardinality(p_sequences) THEN
    RAISE EXCEPTION 'wh_fill_origin_positions: % event ids and % sequences must pair by position',
      cardinality(p_event_ids), cardinality(p_sequences);
  END IF;

  -- Only events still held from this origin without a position, and only real positions: never an overwrite.
  SELECT array_agg(m.eid ORDER BY m.eid), array_agg(m.seq ORDER BY m.eid) INTO v_ids, v_seqs
  FROM unnest(p_event_ids, p_sequences) AS m(eid, seq)
  JOIN __SCHEMA__.wh_event_store es ON es.event_id = m.eid
  WHERE es.origin_service_id = p_origin AND es.origin_commit_sequence IS NULL AND m.seq > 0;

  IF v_ids IS NOT NULL THEN
    SELECT COALESCE(SUM(r.adopted_count), 0)::int INTO v_filled
    FROM __SCHEMA__.wh_reconcile_redelivered_bundles_counting_satisfied(
      ARRAY[p_origin], ARRAY[NULL::text], array_fill(1, ARRAY[cardinality(v_ids)]), v_ids, v_seqs) r;
  END IF;

  WITH recorded AS (
    INSERT INTO __SCHEMA__.wh_origin_position_unknown (event_id, origin_service_id, recorded_at)
    SELECT es.event_id, p_origin, p_now
    FROM __SCHEMA__.wh_event_store es
    WHERE es.event_id = ANY(COALESCE(p_unknown_ids, ARRAY[]::UUID[]))
      AND es.origin_service_id = p_origin AND es.origin_commit_sequence IS NULL
    ON CONFLICT (event_id) DO NOTHING
    RETURNING 1
  )
  SELECT count(*)::int INTO v_unknown FROM recorded;

  v_rewound := __SCHEMA__._wh_rewind_perspectives_from(p_rewind_streams, p_rewind_perspectives, p_rewind_triggers, p_now);

  RETURN QUERY SELECT v_filled, v_unknown, v_rewound;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_fill_origin_positions(UUID, UUID[], BIGINT[], UUID[], UUID[], TEXT[], UUID[], TIMESTAMPTZ) IS
'590 (#2571, from 561): applies an origin''s position answer: fills only NULL positions of events held from p_origin (through wh_reconcile_redelivered_bundles_counting_satisfied, which refolds each touched sealed epoch once), records the events it answered unknown so they are not asked again, and rewinds each named perspective cursor from the trigger the caller found, the first version its own replay order changed at.';
