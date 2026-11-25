-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 590_OriginPositionFillRewindsEachPerspectiveFromWhereItsOrderChanged.sql (#2571): the readers and the
-- per-perspective rewind go, and 561's plan, stream-wide rewind and fill come back verbatim.

DROP FUNCTION IF EXISTS __SCHEMA__.wh_fill_origin_positions(UUID, UUID[], BIGINT[], UUID[], UUID[], TEXT[], UUID[], TIMESTAMPTZ);
DROP FUNCTION IF EXISTS __SCHEMA__._wh_rewind_perspectives_from(UUID[], TEXT[], UUID[], TIMESTAMPTZ);
DROP FUNCTION IF EXISTS __SCHEMA__.wh_origin_position_fill_readers(UUID[]);

SELECT __SCHEMA__.drop_all_overloads('wh_origin_position_fill_plan');
-- <docs>resilience/stream-integrity#filling-missing-origin-positions</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:FillPlan_ReturnsTheOriginsEventsOfTheStreamsTheFillTouches_OnlyAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_origin_position_fill_plan(p_origin UUID, p_event_ids UUID[])
RETURNS TABLE(stream_id UUID, event_id UUID, version BIGINT, origin_commit_sequence BIGINT) AS $$
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
  SELECT es.stream_id, es.event_id, es.version::bigint, es.origin_commit_sequence
  FROM __SCHEMA__.wh_event_store es
  WHERE es.stream_id = ANY(v_streams)
    AND es.origin_service_id = p_origin
  ORDER BY es.stream_id, es.version;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_origin_position_fill_plan(UUID, UUID[]) IS
'561 (#2544): locks the streams holding a named event received from p_origin without a position (the emit chain''s per-stream lock, in stream order) and returns p_origin''s events of those streams by version, for the caller to decide which streams the fill reorders.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- Rewinds every perspective of the streams from their start, as a re-sequence does (555): the snapshots go (each holds
-- a fold in the old order), every cursor but the collective sink's is flagged RewindRequired at the stream's first event
-- with its replay frontier cleared, and that trigger is queued for each. Returns the cursors flagged.
SELECT __SCHEMA__.drop_all_overloads('_wh_rewind_streams_from_start');
-- <docs>resilience/stream-integrity#filling-missing-origin-positions</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Fill_ThatChangesAStreamsOriginOrder_RewindsItsPerspectivesFromTheStartAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_rewind_streams_from_start(p_stream_ids UUID[], p_now TIMESTAMPTZ)
RETURNS INTEGER AS $$
DECLARE
  v_rewound INTEGER;
BEGIN
  IF COALESCE(cardinality(p_stream_ids), 0) = 0 THEN
    RETURN 0;
  END IF;

  DELETE FROM __SCHEMA__.wh_perspective_snapshots sn WHERE sn.stream_id = ANY(p_stream_ids);

  WITH firsts AS (
    SELECT DISTINCT ON (es.stream_id) es.stream_id, es.event_id AS trigger_id
    FROM __SCHEMA__.wh_event_store es
    WHERE es.stream_id = ANY(p_stream_ids)
    ORDER BY es.stream_id, es.version ASC
  ),
  flagged AS (
    UPDATE __SCHEMA__.wh_perspective_cursors c
    SET status = c.status | 32,  -- RewindRequired flag (1 << 5)
        rewind_trigger_event_id = f.trigger_id,
        rewind_flagged_at = p_now,
        rewind_first_flagged_at = COALESCE(c.rewind_first_flagged_at, p_now),
        replayed_through_commit_sequence = NULL
    FROM firsts f
    WHERE c.stream_id = f.stream_id
      AND c.perspective_name <> '__collective__'
    RETURNING c.stream_id, c.perspective_name, f.trigger_id
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
  SELECT count(*) INTO v_rewound FROM flagged;
  RETURN v_rewound;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_rewind_streams_from_start(UUID[], TIMESTAMPTZ) IS
'561 (#2544): removes the streams'' snapshots and flags every cursor but the collective sink''s RewindRequired at the stream''s first event, queuing that trigger, so its perspectives replay the stream from the start in its replay order, as a re-sequence (555) does. Returns the cursors flagged.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- Applies one origin's answer: fills the positions it gave (only NULL ones, through the redelivery reconcile's batch
-- call, which refolds each sealed epoch that now holds a filled event once), records the events it does not know, and
-- rewinds the streams the caller found reordered. Returns what it filled, recorded and rewound.
SELECT __SCHEMA__.drop_all_overloads('wh_fill_origin_positions');
-- <docs>resilience/stream-integrity#filling-missing-origin-positions</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Fill_FillsOnlyNullPositions_AndNeverOverwritesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Fill_RefoldsTheSealedEpochAFilledEventFallsIn_AndTheHeldFoldLetsItGoAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Fill_RecordsUnknownAnswers_AndTheAskNeverReturnsThemAgainAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Fill_ThatChangesAStreamsOriginOrder_RewindsItsPerspectivesFromTheStartAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_fill_origin_positions(
  p_origin UUID,
  p_event_ids UUID[],
  p_sequences BIGINT[],
  p_unknown_ids UUID[],
  p_rewind_streams UUID[],
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

  v_rewound := __SCHEMA__._wh_rewind_streams_from_start(p_rewind_streams, p_now);

  RETURN QUERY SELECT v_filled, v_unknown, v_rewound;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_fill_origin_positions(UUID, UUID[], BIGINT[], UUID[], UUID[], TIMESTAMPTZ) IS
'561 (#2544): applies an origin''s position answer: fills only NULL positions of events held from p_origin (through wh_reconcile_redelivered_bundles_counting_satisfied, which refolds each touched sealed epoch once), records the events it answered unknown so they are not asked again, and rewinds the streams the caller found reordered from their start.';
