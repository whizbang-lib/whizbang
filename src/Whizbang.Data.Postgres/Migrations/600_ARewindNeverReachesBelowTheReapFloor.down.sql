-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 600_ARewindNeverReachesBelowTheReapFloor.sql (#2591): 590's rewind comes back verbatim, and the floor
-- and repair functions and the repair's setting go.
--
-- What the repair changed is not restored. It cleared rewinds that could never complete (nothing they would replay has a
-- body), moved others up to the first event that can still be read, and deleted queued replays of events without a body.
-- Putting any of that back would bring back the stall the repair ended: a batch failing on an unreadable row, and a
-- startup in Blocking mode waiting on a rewind that cannot finish.

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


DROP FUNCTION IF EXISTS __SCHEMA__.wh_rewind_reap_floor(UUID[], TEXT[], UUID[]);
DROP FUNCTION IF EXISTS __SCHEMA__._wh_repair_unreplayable_rewinds();
DELETE FROM __SCHEMA__.wh_settings WHERE setting_key = 'rewind_reap_floor_repaired';
