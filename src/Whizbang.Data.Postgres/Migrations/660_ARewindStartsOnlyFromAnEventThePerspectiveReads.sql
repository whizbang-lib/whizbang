-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 660_ARewindStartsOnlyFromAnEventThePerspectiveReads.sql
-- Date: 2026-11-25
-- Description: A rewind starts only from an event of a type the perspective reads (#2601).
--
--              600's floor (wh_rewind_reap_floor) counted the trigger itself whatever its type, and counted every type
--              for a perspective with no association rows. The origin-position fill once rewound every cursor of a
--              stream from the stream's first event, so a cursor could be flagged from an event its perspective does
--              not read, and that event queued for it. The drain applies a perspective only the events it reads, so no
--              pair ever formed for the row: it was leased on every pass with nothing recorded against it, and the
--              cursor stayed flagged, holding a Blocking startup to its cap on every start.
--
--                the floor     wh_rewind_reap_floor: the first event of the stream at or after the trigger's version
--                              that still has a body and whose type the perspective's association rows name. The trigger
--                              counts only when the perspective reads it. A perspective with no association rows reads
--                              nothing: the store queues a perspective only the event types its association rows name
--                              (the fan-out joins them), and the startup registration deletes the rows of a perspective
--                              its service no longer runs, so a cursor with none belongs to a perspective nothing here
--                              runs, and a rewind of it could never run. NULL when there is none.
--                the rewind    _wh_rewind_perspectives_from is unchanged: it floors each trigger through the function
--                              above, so it now starts from the first read event and skips a perspective that reads none.
--                the repair    _wh_repair_rewinds_from_unread_events, run once per database by this migration (gated on
--                              the rewind_read_floor_repaired setting, which records what it did): every flagged cursor
--                              whose trigger has no floor has its flag cleared; one whose floor is above its trigger is
--                              moved to the floor and the floor queued. Those cursors' queued rows whose event the
--                              perspective does not read, or whose body is gone, are deleted. It raises a NOTICE with the
--                              counts.
--                the cleanup   wh_remove_unregistered_perspective_cursors, called by the running service's maintenance
--                              step (only it knows which perspectives it registers): up to a batch of cursors of the
--                              perspectives no service runs, a name the caller does not register and no association row
--                              names, removed with their queued rows and snapshots.
--
--              Cost: the floor's walk is 600's, with the type probe an index lookup on the association rows by target.
--              The repair reads the cursor table once (the flag has no index; the worker's startup scan makes the same
--              pass on every start), then walks each flagged cursor's stream from its trigger, and touches only the
--              flagged cursors' rows by key.
--
--              Number 660: develop ends at 626, and 630 to 659 belong to a change in flight.
--
-- Reversal: reversible (600's floor comes back; the repair function and its setting are dropped; what the repair cleared
--           or moved is not restored, since each was a rewind that could never run)
-- Dependencies: 600 (wh_rewind_reap_floor, _wh_rewind_perspectives_from), 008 (wh_message_associations)
-- Objects: wh_rewind_reap_floor, _wh_repair_rewinds_from_unread_events, wh_remove_unregistered_perspective_cursors
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- Where each rewind can start: the first readable event of a type the perspective reads, at or after its trigger.
SELECT __SCHEMA__.drop_all_overloads('wh_rewind_reap_floor');
-- <docs>resilience/stream-integrity#a-rewind-starts-only-from-an-event-the-perspective-reads</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:ReapFloor_IsTheFirstEventWithABodyThePerspectiveReads_AtOrAfterTheTriggerAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.ReadFloor.cs:Rewind_FromAnEventThePerspectiveDoesNotRead_StartsAtTheFirstEventItReads_AndQueuesThatAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.ReadFloor.cs:Rewind_ForAPerspectiveWithNoAssociationRows_FlagsDeletesAndQueuesNothingAsync</tests>
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
      AND EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_message_associations ma
        WHERE ma.target_name = r.pname
          AND ma.association_type = __CATEGORY_PERSPECTIVE__
          AND ma.normalized_message_type = es.event_type)
    ORDER BY es.version
    LIMIT 1
  ) fl ON TRUE
  ORDER BY r.ord;
$$ LANGUAGE sql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_rewind_reap_floor(UUID[], TEXT[], UUID[]) IS
'660 (#2601, from 600): for each (stream, perspective, trigger) triple whose trigger the store holds, the first event of the stream at or after the trigger''s version that still has a body and whose type the perspective''s association rows name (the trigger counts only when the perspective reads it; a perspective with no association rows reads nothing); NULL when there is none, and then the replay cannot happen.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- Once per database: the read floor applied to the rewinds already flagged. Cleared where the perspective reads nothing at
-- or after the trigger, moved to the floor where it is above the trigger, and the queued rows nothing would apply deleted.
SELECT __SCHEMA__.drop_all_overloads('_wh_repair_rewinds_from_unread_events');
-- <docs>resilience/stream-integrity#a-rewind-starts-only-from-an-event-the-perspective-reads</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.ReadFloor.cs:Repair_MovesAnUnreadTriggerToTheFirstReadEvent_ClearsOneWithNone_LeavesTheRest_AndRunsOnceAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_repair_rewinds_from_unread_events()
RETURNS TABLE(cursors_cleared INTEGER, cursors_moved INTEGER, rows_deleted INTEGER) AS $$
DECLARE
  v_cleared INTEGER := 0;
  v_moved INTEGER := 0;
  v_deleted INTEGER := 0;
  v_streams UUID[];
  v_names TEXT[];
  v_triggers UUID[];
  v_fix_streams UUID[];
  v_fix_names TEXT[];
  v_fix_floors UUID[];
BEGIN
  -- Claimed first, in the migration's transaction: a failure rolls the claim back with the work.
  INSERT INTO __SCHEMA__.wh_settings (setting_key, setting_value, value_type, description)
  VALUES ('rewind_read_floor_repaired', 'true', 'boolean',
          '660 (#2601): the rewinds already flagged from events their perspective does not read have been repaired.')
  ON CONFLICT (setting_key) DO NOTHING;
  IF NOT FOUND THEN
    RETURN QUERY SELECT 0, 0, 0;
    RETURN;
  END IF;

  SELECT array_agg(c.stream_id), array_agg(c.perspective_name::text), array_agg(c.rewind_trigger_event_id)
  INTO v_streams, v_names, v_triggers
  FROM __SCHEMA__.wh_perspective_cursors c
  WHERE (c.status & 32) <> 0
    AND c.rewind_trigger_event_id IS NOT NULL
    AND c.perspective_name <> '__collective__';

  -- The cursors whose floor is not their trigger: NULL where the perspective reads nothing at or after it. A trigger the
  -- store no longer holds has no floor row, and is left to the rewind as before.
  SELECT array_agg(f.stream_id), array_agg(f.perspective_name), array_agg(f.trigger_event_id)
  INTO v_fix_streams, v_fix_names, v_fix_floors
  FROM __SCHEMA__.wh_rewind_reap_floor(
    COALESCE(v_streams, ARRAY[]::UUID[]), COALESCE(v_names, ARRAY[]::TEXT[]), COALESCE(v_triggers, ARRAY[]::UUID[])) f
  WHERE f.trigger_event_id IS DISTINCT FROM f.asked_trigger_event_id;

  IF v_fix_streams IS NOT NULL THEN
    -- The queued rows nothing would apply: an event the perspective does not read, or one whose body is gone.
    DELETE FROM __SCHEMA__.wh_perspective_events pe
    USING unnest(v_fix_streams, v_fix_names) AS r(sid, pname)
    WHERE pe.stream_id = r.sid
      AND pe.perspective_name = r.pname
      AND pe.processed_at IS NULL
      AND NOT EXISTS (
        SELECT 1
        FROM __SCHEMA__.wh_event_store es
        JOIN __SCHEMA__.wh_message_associations ma
          ON ma.target_name = r.pname
         AND ma.association_type = __CATEGORY_PERSPECTIVE__
         AND ma.normalized_message_type = es.event_type
        WHERE es.event_id = pe.event_id
          AND es.stream_id = pe.stream_id
          AND EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_body b WHERE b.event_id = pe.event_id));
    GET DIAGNOSTICS v_deleted = ROW_COUNT;

    UPDATE __SCHEMA__.wh_perspective_cursors c
    SET status = c.status & ~32,  -- RewindRequired flag (1 << 5) cleared
        rewind_trigger_event_id = NULL,
        rewind_flagged_at = NULL,
        rewind_first_flagged_at = NULL
    FROM unnest(v_fix_streams, v_fix_names, v_fix_floors) AS r(sid, pname, floor_id)
    WHERE c.stream_id = r.sid AND c.perspective_name = r.pname AND r.floor_id IS NULL;
    GET DIAGNOSTICS v_cleared = ROW_COUNT;

    UPDATE __SCHEMA__.wh_perspective_cursors c
    SET rewind_trigger_event_id = r.floor_id
    FROM unnest(v_fix_streams, v_fix_names, v_fix_floors) AS r(sid, pname, floor_id)
    WHERE c.stream_id = r.sid AND c.perspective_name = r.pname AND r.floor_id IS NOT NULL;
    GET DIAGNOSTICS v_moved = ROW_COUNT;

    -- The floor is queued, as the rewind queues its trigger; a processed row of it (debug mode) is made pending again.
    INSERT INTO __SCHEMA__.wh_perspective_events (
      event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at, priority)
    SELECT gen_random_uuid(), r.sid, r.pname, r.floor_id, __SCHEMA__.compute_partition(r.sid, 10000), 1, 0, NOW(),
           __PRIORITY_BACKGROUND__
    FROM unnest(v_fix_streams, v_fix_names, v_fix_floors) AS r(sid, pname, floor_id)
    WHERE r.floor_id IS NOT NULL
    ON CONFLICT ON CONSTRAINT uq_perspective_event DO UPDATE
      SET processed_at = NULL, status = EXCLUDED.status, attempts = 0, error = NULL, failure_reason = NULL,
          instance_id = NULL, lease_expiry = NULL, claimed_at = NULL, scheduled_for = NULL
      WHERE __SCHEMA__.wh_perspective_events.processed_at IS NOT NULL;
  END IF;

  UPDATE __SCHEMA__.wh_settings
  SET description = format('660 (#2601): the rewinds already flagged from events their perspective does not read have been repaired: %s cleared, %s moved to the first event read, %s queued rows nothing would apply deleted.',
                           v_cleared, v_moved, v_deleted)
  WHERE setting_key = 'rewind_read_floor_repaired';
  RAISE NOTICE '660 (#2601): % rewind(s) from events nothing reads cleared, % moved to the first event their perspective reads, % queued row(s) nothing would apply deleted',
    v_cleared, v_moved, v_deleted;
  RETURN QUERY SELECT v_cleared, v_moved, v_deleted;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_repair_rewinds_from_unread_events() IS
'660 (#2601), once per database (the rewind_read_floor_repaired setting): every flagged perspective cursor whose perspective reads nothing at or after its trigger is unflagged, one whose first read event is above its trigger is moved to it and it queued, and those cursors'' queued rows nothing would apply (an unread type, or no body) are deleted. Returns the counts and raises them as a NOTICE.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- What is left of a perspective no service runs: its cursors, queued rows and snapshots, a bounded batch at a time.
SELECT __SCHEMA__.drop_all_overloads('wh_remove_unregistered_perspective_cursors');
-- <docs>fundamentals/perspectives/rewind#perspectives-no-longer-registered</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/UnregisteredPerspectiveCleanupStepTests.cs:Clean_RemovesTheCursorsRowsAndSnapshotsOfAPerspectiveNothingRuns_AndLeavesTheRestAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/UnregisteredPerspectiveCleanupStepTests.cs:Clean_TakesBatchesWithinTheCycleBudget_AndTheNextCycleTakesTheRestAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_remove_unregistered_perspective_cursors(
  p_registered TEXT[],
  p_batch INTEGER
) RETURNS TABLE(perspective_name TEXT, cursors_removed INTEGER, rows_removed INTEGER, snapshots_removed INTEGER) AS $$
DECLARE
  v_names TEXT[];
BEGIN
  -- The cursors' perspective names, one index probe each (a skip scan of the name index), less the ones the caller
  -- registers, the collective sink and any name a service's association rows still name.
  WITH RECURSIVE names AS (
    (SELECT c.perspective_name::text AS name
     FROM __SCHEMA__.wh_perspective_cursors c
     ORDER BY c.perspective_name
     LIMIT 1)
    UNION ALL
    SELECT (SELECT c.perspective_name::text
            FROM __SCHEMA__.wh_perspective_cursors c
            WHERE c.perspective_name > n.name
            ORDER BY c.perspective_name
            LIMIT 1)
    FROM names n
    WHERE n.name IS NOT NULL
  )
  SELECT array_agg(n.name ORDER BY n.name) INTO v_names
  FROM names n
  WHERE n.name IS NOT NULL
    AND n.name <> '__collective__'
    AND NOT (n.name = ANY(COALESCE(p_registered, ARRAY[]::TEXT[])))
    AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_message_associations ma WHERE ma.target_name = n.name);
  IF v_names IS NULL THEN
    RETURN;
  END IF;

  RETURN QUERY
  WITH batch AS MATERIALIZED (
    SELECT c.stream_id, c.perspective_name
    FROM __SCHEMA__.wh_perspective_cursors c
    WHERE c.perspective_name = ANY(v_names)
    LIMIT p_batch
  ),
  rows_gone AS (
    DELETE FROM __SCHEMA__.wh_perspective_events pe
    USING batch b
    WHERE pe.stream_id = b.stream_id AND pe.perspective_name = b.perspective_name AND pe.processed_at IS NULL
    RETURNING pe.perspective_name::text AS pname
  ),
  snapshots_gone AS (
    DELETE FROM __SCHEMA__.wh_perspective_snapshots sn
    USING batch b
    WHERE sn.stream_id = b.stream_id AND sn.perspective_name = b.perspective_name
    RETURNING sn.perspective_name::text AS pname
  ),
  cursors_gone AS (
    DELETE FROM __SCHEMA__.wh_perspective_cursors c
    USING batch b
    WHERE c.stream_id = b.stream_id AND c.perspective_name = b.perspective_name
    RETURNING c.perspective_name::text AS pname
  )
  SELECT g.pname,
         count(*)::INTEGER,
         (SELECT count(*) FROM rows_gone r WHERE r.pname = g.pname)::INTEGER,
         (SELECT count(*) FROM snapshots_gone s WHERE s.pname = g.pname)::INTEGER
  FROM cursors_gone g
  GROUP BY g.pname
  ORDER BY g.pname;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_remove_unregistered_perspective_cursors(TEXT[], INTEGER) IS
'660 (#2601): removes up to p_batch cursors of the perspectives no service runs (a cursor''s name not in p_registered, not the collective sink, and named by no association row), with their queued rows and snapshots. Returns the counts per perspective; no rows when nothing is left.';

SELECT * FROM __SCHEMA__._wh_repair_rewinds_from_unread_events();
