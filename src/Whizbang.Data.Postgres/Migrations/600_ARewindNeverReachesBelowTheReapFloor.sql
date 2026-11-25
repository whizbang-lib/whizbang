-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 600_ARewindNeverReachesBelowTheReapFloor.sql
-- Date: 2026-11-25
-- Description: A rewind never queues a replay of an event whose body the reap has deleted (#2591).
--
--              The origin-position fill (590) names, per perspective cursor, the first version whose replay order the
--              fill changed, and _wh_rewind_perspectives_from flags the cursor at that event, deletes the perspective's
--              snapshots at or above it and queues it. It decides from the event store's pointer rows only, so on an
--              ephemeral stream whose bodies the reap had already deleted it queued an event nothing can read: the
--              drain's read of that row failed, and with it every batch the row was leased into.
--
--                the floor     wh_rewind_reap_floor: for each (stream, perspective, trigger) the first event of the
--                              stream at or after the trigger's version that still has a body and that the perspective
--                              reads (the types its association rows name, or every type when it has none; the trigger
--                              itself always counts). NULL when there is none: the replay the rewind asks for cannot
--                              happen.
--                the rewind    _wh_rewind_perspectives_from rewinds from the floored trigger. A rewind with no floor is
--                              skipped: the cursor is not flagged, no snapshot is deleted (the reap kept them as the
--                              floor of the fold) and nothing is queued. Everything else is 590's, verbatim.
--
--              The fill's caller (OriginPositionStore) reads the floor itself before the fill, so it can report and
--              count the rewinds that were skipped; any other caller of the rewind gets the same floor.
--
--              Cost: one walk per named rewind down the stream's version index from the trigger, probing the body by its
--              key, ending at the first event with a body (the trigger itself on a stream nothing was reaped from).
--
--              Number 600: 598 and 599 are taken by changes in flight.
--
-- Reversal: reversible (590's rewind comes back; the floor function is dropped)
-- Dependencies: 590 (_wh_rewind_perspectives_from), 072 (wh_event_body), 455 (the cursor's rewind flag and replay frontier)
-- Objects: wh_rewind_reap_floor, _wh_rewind_perspectives_from
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- Where each rewind can start: the first readable event the perspective reads at or after its trigger.
SELECT __SCHEMA__.drop_all_overloads('wh_rewind_reap_floor');
-- <docs>resilience/stream-integrity#a-rewind-never-reaches-below-the-reap-floor</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:ReapFloor_IsTheFirstEventWithABodyThePerspectiveReads_AtOrAfterTheTriggerAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_rewind_reap_floor(
  p_stream_ids UUID[],
  p_perspective_names TEXT[],
  p_trigger_event_ids UUID[]
) RETURNS TABLE(stream_id UUID, perspective_name TEXT, asked_trigger_event_id UUID, trigger_event_id UUID) AS $$
  SELECT r.sid, r.pname, r.tid, fl.event_id
  FROM unnest(p_stream_ids, p_perspective_names, p_trigger_event_ids) WITH ORDINALITY AS r(sid, pname, tid, ord)
  JOIN __SCHEMA__.wh_event_store t ON t.event_id = r.tid AND t.stream_id = r.sid
  LEFT JOIN LATERAL (
    SELECT es.event_id
    FROM __SCHEMA__.wh_event_store es
    WHERE es.stream_id = r.sid
      AND es.version >= t.version
      AND EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_body b WHERE b.event_id = es.event_id)
      AND (es.event_id = r.tid
           OR NOT EXISTS (
             SELECT 1 FROM __SCHEMA__.wh_message_associations ma
             WHERE ma.target_name = r.pname
               AND ma.association_type = __CATEGORY_PERSPECTIVE__
               AND ma.normalized_message_type IS NOT NULL)
           OR EXISTS (
             SELECT 1 FROM __SCHEMA__.wh_message_associations ma
             WHERE ma.target_name = r.pname
               AND ma.association_type = __CATEGORY_PERSPECTIVE__
               AND ma.normalized_message_type = es.event_type))
    ORDER BY es.version
    LIMIT 1
  ) fl ON TRUE
  ORDER BY r.ord;
$$ LANGUAGE sql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_rewind_reap_floor(UUID[], TEXT[], UUID[]) IS
'600 (#2591): for each (stream, perspective, trigger) triple whose trigger the store holds, the first event of the stream at or after the trigger''s version that still has a body and that the perspective reads (the types its association rows name, every type when it has none, and always the trigger itself); NULL when there is none, and then the replay cannot happen.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- Copied from 590 with #2591's delta: each trigger is floored first, and a rewind with no floor is skipped.
SELECT __SCHEMA__.drop_all_overloads('_wh_rewind_perspectives_from');
-- <docs>resilience/stream-integrity#filling-missing-origin-positions</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Fill_RewindsFromTheFirstChangedVersion_KeepingTheSnapshotsBelowItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Fill_RewindsALaggingCursorFromTheFirstChangedVersion_KeepingItsEarlierSnapshotAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Fill_MergingWithAFlagAlreadySet_KeepsTheLowerTrigger_AndCountsOnlyNewlyFlaggedCursorsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Fill_ThatKeepsTheStreamsOrder_ButReordersAPerspectivesOwnEvents_RewindsThatPerspectiveAsync</tests>
-- <docs>resilience/stream-integrity#a-rewind-never-reaches-below-the-reap-floor</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Rewind_FromAReapedTrigger_WithNothingReadableAfterIt_FlagsDeletesAndQueuesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Fill_ThatReordersAStreamWhoseEarliestBodiesWereReaped_RewindsFromTheFirstEventWithABodyAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_rewind_perspectives_from(
  p_stream_ids UUID[],
  p_perspective_names TEXT[],
  p_trigger_event_ids UUID[],
  p_now TIMESTAMPTZ
) RETURNS INTEGER AS $$
DECLARE
  v_rewound INTEGER;
  -- 600 (#2591): the rewinds that can replay, each from its floored trigger.
  v_streams UUID[];
  v_names TEXT[];
  v_triggers UUID[];
BEGIN
  IF COALESCE(cardinality(p_stream_ids), 0) = 0 THEN
    RETURN 0;
  END IF;
  IF cardinality(p_stream_ids) IS DISTINCT FROM cardinality(p_perspective_names)
     OR cardinality(p_stream_ids) IS DISTINCT FROM cardinality(p_trigger_event_ids) THEN
    RAISE EXCEPTION '_wh_rewind_perspectives_from: % streams, % perspectives and % triggers must pair by position',
      cardinality(p_stream_ids), cardinality(p_perspective_names), cardinality(p_trigger_event_ids);
  END IF;

  -- 600 (#2591): a rewind starts no earlier than the first event at or after its trigger that still has a body and that
  -- the perspective reads. One with none cannot replay anything and is skipped: its cursor is not flagged, its snapshots
  -- (the floor the reap kept) stay, and nothing is queued.
  SELECT array_agg(f.stream_id ORDER BY f.ord), array_agg(f.perspective_name ORDER BY f.ord), array_agg(f.trigger_event_id ORDER BY f.ord)
  INTO v_streams, v_names, v_triggers
  FROM (
    SELECT fl.stream_id, fl.perspective_name, fl.trigger_event_id, row_number() OVER () AS ord
    FROM __SCHEMA__.wh_rewind_reap_floor(p_stream_ids, p_perspective_names, p_trigger_event_ids) fl
  ) f
  WHERE f.trigger_event_id IS NOT NULL;
  IF v_streams IS NULL THEN
    RETURN 0;
  END IF;

  -- A snapshot at the trigger's version or above holds a fold in the old order; one that records no version cannot
  -- say, so it goes too. A snapshot below the trigger folds what the replay still folds there, and is kept.
  WITH rewinds AS (
    SELECT r.sid, r.pname, es.version AS trigger_version
    FROM unnest(v_streams, v_names, v_triggers) AS r(sid, pname, tid)
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
    FROM unnest(v_streams, v_names, v_triggers) AS r(sid, pname, tid)
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
'600 (#2591, from 590): each trigger floored first (wh_rewind_reap_floor), a triple with no floor skipped; then for each (stream, perspective, trigger) triple, deletes that perspective''s snapshots at the trigger''s version or above (or with no version), flags its cursor RewindRequired at the lower of the trigger and any it already had, clears its replay frontier and queues the trigger. Returns the cursors that were not flagged before.';

