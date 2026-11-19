-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 345_SupersedingHistoryReconcile.sql
-- Date: 2026-11-06
-- Description: The events a superseding type stored before it was declared [Superseding] are brought under the
--              superseding rule (#2110).
--
--              295 stamps the flag and the key when an event is stored, so an event stored before its type was declared
--              superseding carries neither and is never queued for the reaper. The superseding-history-reconcile
--              maintenance step walks such a type's stored events and, batch by batch, in one transaction each:
--
--                0. wh_superseding_history_ensure creates the type's cursor row the first time the type is walked, in a
--                   statement of its own (committed at once), so no instance ever waits on another's uncommitted row.
--                1. wh_superseding_history_begin takes the type's cursor row (SKIP LOCKED, so a second instance skips
--                   instead of doing the same work) and chooses the window: the next p_scan_size events of the whole
--                   store by event id, read through its primary key. The walk restarts from the first event when the
--                   type-definition registry holds a definition of the type newer than the one the walk started under,
--                   which is how a type that becomes superseding again after being something else is walked again.
--                2. wh_superseding_history_rows returns the window's events of the type that carry no superseding flag
--                   and are Sourced, with their payloads. The step reads each key from the payload with the generated
--                   accessor the write paths use (SupersedingEvents.KeyOf), so the key is the one a write would store.
--                3. wh_superseding_history_apply takes the per-stream locks the emit chains take, in stream id order,
--                   locks the rows it was given that are still unflagged (SKIP LOCKED), stamps EventFlags.Superseding
--                   and supersede_key on them (a null key stays null: flagged, never superseded), and queues in
--                   wh_superseded_events every event a newer event of its stream, type and key supersedes, whichever of
--                   the two was stamped first. It then moves the cursor past the window, or to just before the first row
--                   another transaction held, and marks the walk complete when the window was the end of the store.
--
--              Nothing is deleted here. The reaper (wh_reap_superseded_events, 295) removes a queued event only past
--              every one of its gates, and adjusts the digests as it always has.
--
--              Why the primary key: no index serves "the events of type T without the flag" without visiting every
--              flagged one again on each batch, and an index for that would be written by every append for the life of
--              the store. A walk by event id reads each event once per walk, in bounded windows, and costs an idle
--              database nothing once complete (one cursor row per type).
--
--              Number 345: 336 to 339 are taken by a change in flight; 340 to 344 are left for others in flight.
--
-- Dependencies: 295 (supersede_key, idx_event_store_superseding, wh_superseded_events), 075 (wh_type_definitions),
--               077 (wh_event_body), 006 (normalize_event_type)
-- Objects: wh_superseding_history, wh_superseding_history_ensure, wh_superseding_history_begin, wh_superseding_history_rows, wh_superseding_history_apply
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_superseding_history_ensure');
SELECT __SCHEMA__.drop_all_overloads('wh_superseding_history_begin');
SELECT __SCHEMA__.drop_all_overloads('wh_superseding_history_rows');
SELECT __SCHEMA__.drop_all_overloads('wh_superseding_history_apply');

-- ===========================================================================================
-- The cursor, one row per superseding type
-- ===========================================================================================

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_superseding_history (
  event_type TEXT NOT NULL,
  definition_watermark INTEGER NOT NULL DEFAULT 0,
  last_event_id UUID NULL,
  events_total BIGINT NOT NULL DEFAULT 0,
  events_matched BIGINT NOT NULL DEFAULT 0,
  events_stamped BIGINT NOT NULL DEFAULT 0,
  events_queued BIGINT NOT NULL DEFAULT 0,
  started_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  completed_at TIMESTAMPTZ NULL,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  CONSTRAINT pk_superseding_history PRIMARY KEY (event_type)
);

COMMENT ON TABLE __SCHEMA__.wh_superseding_history IS
  'The walk that brings a superseding type''s events stored before its declaration under the superseding rule (345, '
  '#2110): the last event id passed, the newest type definition the walk started under, the type''s events when it '
  'started and how many it has read, stamped and queued, and when it read to the end. Delete a row to walk the type again.';

-- ===========================================================================================
-- One batch: begin, rows, apply (one transaction, driven by the maintenance step)
-- ===========================================================================================

-- <docs>fundamentals/events/superseding-events#history-stored-before-the-declaration</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Reconcile_HistoryStoredBeforeTheDeclaration_IsReapedDownToTheNewestOfEachKey_OnceConsumedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Reconcile_WhileAnotherInstanceHoldsTheType_SkipsWithoutWaiting_AndDoesNoWorkAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_superseding_history_ensure(p_event_type TEXT, p_definition_name TEXT)
RETURNS VOID AS $$
DECLARE
  v_type TEXT := __SCHEMA__.normalize_event_type(p_event_type);
BEGIN
  -- A type seen for the first time starts a walk, under the newest definition the registry holds for it. Its events are
  -- counted once, through idx_event_store_type_tenant, so the step can report how many are left.
  IF NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_superseding_history h WHERE h.event_type = v_type) THEN
    INSERT INTO __SCHEMA__.wh_superseding_history (event_type, definition_watermark, events_total)
    VALUES (
      v_type,
      (SELECT COALESCE(MAX(d.definition_id), 0) FROM __SCHEMA__.wh_type_definitions d
       WHERE d.event_type = __SCHEMA__.normalize_event_type(p_definition_name)),
      (SELECT count(*) FROM __SCHEMA__.wh_event_store es WHERE es.event_type = v_type))
    ON CONFLICT DO NOTHING;
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_superseding_history_ensure(TEXT, TEXT) IS
  'Issue #2110: creates a superseding type''s history walk the first time the type is seen, in a statement of its own, '
  'so wh_superseding_history_begin never waits on another instance''s uncommitted row.';

-- <docs>fundamentals/events/superseding-events#history-stored-before-the-declaration</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Reconcile_HistoryStoredBeforeTheDeclaration_IsReapedDownToTheNewestOfEachKey_OnceConsumedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Reconcile_WhileAnotherInstanceHoldsTheType_SkipsWithoutWaiting_AndDoesNoWorkAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Reconcile_ANewerDefinitionOfTheType_WalksItAgainFromTheStartAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Reconcile_RunsInBoundedBatches_AndResumesFromItsCursor_AfterACrashAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_superseding_history_begin(
  p_event_type TEXT,
  p_definition_name TEXT,
  p_scan_size INTEGER DEFAULT 5000
)
RETURNS TABLE(
  status TEXT,
  after_id UUID,
  through_id UUID,
  rows_scanned BIGINT,
  events_total BIGINT,
  events_matched BIGINT,
  events_stamped BIGINT,
  events_queued BIGINT
) AS $$
DECLARE
  v_scan INTEGER := GREATEST(COALESCE(p_scan_size, 1), 1);
  v_type TEXT := __SCHEMA__.normalize_event_type(p_event_type);
  v_definition INTEGER;
  v_row __SCHEMA__.wh_superseding_history%ROWTYPE;
  v_through UUID;
  v_scanned BIGINT;
BEGIN
  -- The newest definition the registry holds for the type: a walk started under an older one walks again.
  SELECT COALESCE(MAX(d.definition_id), 0) INTO v_definition
  FROM __SCHEMA__.wh_type_definitions d
  WHERE d.event_type = __SCHEMA__.normalize_event_type(p_definition_name);

  -- One instance per type: a row another instance holds means it is walking the type, and this call leaves it to it.
  -- A type with no row has not been ensured (wh_superseding_history_ensure), and nothing is walked.
  SELECT * INTO v_row
  FROM __SCHEMA__.wh_superseding_history h
  WHERE h.event_type = v_type
  FOR UPDATE SKIP LOCKED;
  IF NOT FOUND THEN
    RETURN QUERY SELECT
      CASE WHEN EXISTS (SELECT 1 FROM __SCHEMA__.wh_superseding_history h WHERE h.event_type = v_type)
           THEN 'skipped (another instance is reconciling)'
           ELSE 'skipped (no walk; wh_superseding_history_ensure starts one)' END::TEXT,
      NULL::UUID, NULL::UUID, 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT;
    RETURN;
  END IF;

  IF v_definition > v_row.definition_watermark THEN
    UPDATE __SCHEMA__.wh_superseding_history h SET
      definition_watermark = v_definition,
      last_event_id = NULL,
      events_total = (SELECT count(*) FROM __SCHEMA__.wh_event_store es WHERE es.event_type = v_type),
      events_matched = 0,
      events_stamped = 0,
      events_queued = 0,
      started_at = NOW(),
      completed_at = NULL,
      updated_at = NOW()
    WHERE h.event_type = v_type
    RETURNING * INTO v_row;
  END IF;

  IF v_row.completed_at IS NOT NULL THEN
    RETURN QUERY SELECT 'complete'::TEXT, v_row.last_event_id, v_row.last_event_id, 0::BIGINT,
      v_row.events_total, v_row.events_matched, v_row.events_stamped, v_row.events_queued;
    RETURN;
  END IF;

  -- The window: the next events of the whole store by event id, through its primary key.
  SELECT count(*), (array_agg(w.event_id ORDER BY w.event_id DESC))[1]
  INTO v_scanned, v_through
  FROM (
    SELECT es.event_id
    FROM __SCHEMA__.wh_event_store es
    WHERE es.event_id > COALESCE(v_row.last_event_id, __EMPTY_UUID__::uuid)
    ORDER BY es.event_id
    LIMIT v_scan
  ) w;

  IF v_scanned = 0 THEN
    UPDATE __SCHEMA__.wh_superseding_history h SET completed_at = NOW(), updated_at = NOW()
    WHERE h.event_type = v_type
    RETURNING * INTO v_row;
    RETURN QUERY SELECT 'finished'::TEXT, v_row.last_event_id, v_row.last_event_id, 0::BIGINT,
      v_row.events_total, v_row.events_matched, v_row.events_stamped, v_row.events_queued;
    RETURN;
  END IF;

  RETURN QUERY SELECT 'ok'::TEXT, v_row.last_event_id, v_through, v_scanned,
    v_row.events_total, v_row.events_matched, v_row.events_stamped, v_row.events_queued;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_superseding_history_begin(TEXT, TEXT, INTEGER) IS
  'Issue #2110: takes a superseding type''s history walk (SKIP LOCKED), restarts it when the type-definition registry '
  'holds a newer definition than the walk started under, and returns the next window of up to p_scan_size events of the '
  'store by event id. Status ok, complete, finished (read to the end now) or skipped (another instance is reconciling).';

-- <docs>fundamentals/events/superseding-events#history-stored-before-the-declaration</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Reconcile_HistoryStoredBeforeTheDeclaration_IsReapedDownToTheNewestOfEachKey_OnceConsumedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Reconcile_ATypeThatIsNotSuperseding_IsNotTouchedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Reconcile_AnEphemeralEventOfTheType_IsLeftToTheEphemeralReaperAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_superseding_history_rows(p_event_type TEXT, p_after_id UUID, p_through_id UUID)
RETURNS TABLE(event_id UUID, event_data JSONB) AS $$
  -- 152 = EventFlags.Superseding (128) | Ephemeral (8) | Compacted (16): only an unflagged Sourced event is a candidate.
  SELECT es.event_id, b.event_data
  FROM __SCHEMA__.wh_event_store es
  JOIN __SCHEMA__.wh_event_body b ON b.event_id = es.event_id
  WHERE es.event_id > COALESCE(p_after_id, __EMPTY_UUID__::uuid)
    AND es.event_id <= p_through_id
    AND es.event_type = __SCHEMA__.normalize_event_type(p_event_type)
    AND COALESCE(es.flags, 0) & 152 = 0
  ORDER BY es.event_id;
$$ LANGUAGE sql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_superseding_history_rows(TEXT, UUID, UUID) IS
  'Issue #2110: the events of the type in the window (p_after_id, p_through_id] that carry no superseding flag and are '
  'Sourced, with their payloads, for the maintenance step to read each key from.';

-- <docs>fundamentals/events/superseding-events#history-stored-before-the-declaration</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Reconcile_HistoryStoredBeforeTheDeclaration_IsReapedDownToTheNewestOfEachKey_OnceConsumedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Reconcile_ANullKey_IsStampedUnkeyed_AndNeverQueued_AKeylessTypeKeepsOnePerStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Reconcile_OldEventsTheGatesProtect_StayAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Reconcile_ASecondRun_ChangesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Reconcile_AnOldEventStampedAfterTheNewerOneOfItsKey_IsQueuedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Reconcile_ARowAnotherTransactionHolds_IsLeftForTheNextBatch_WithoutWaitingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Reconcile_ReceivedCopies_AreReconciledInTheReceivingStoreAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_superseding_history_apply(
  p_event_type TEXT,
  p_after_id UUID,
  p_through_id UUID,
  p_scan_size INTEGER,
  p_rows_scanned BIGINT,
  p_event_ids UUID[],
  p_keys TEXT[]
)
RETURNS TABLE(
  status TEXT,
  events_stamped BIGINT,
  events_queued BIGINT,
  events_held BIGINT,
  events_total BIGINT,
  events_matched BIGINT
) AS $$
DECLARE
  c_flag_superseding CONSTANT INTEGER := 128;    -- EventFlags.Superseding (1 << 7), 295
  c_flags_not_candidates CONSTANT INTEGER := 152; -- Superseding | Ephemeral (1 << 3) | Compacted (1 << 4)
  v_type TEXT := __SCHEMA__.normalize_event_type(p_event_type);
  v_row __SCHEMA__.wh_superseding_history%ROWTYPE;
  v_streams UUID[];
  v_stamped BIGINT := 0;
  v_queued BIGINT := 0;
  v_held BIGINT := 0;
  v_first_held UUID;
  v_cursor UUID;
  v_matched BIGINT;
  v_complete BOOLEAN;
BEGIN
  IF cardinality(COALESCE(p_event_ids, ARRAY[]::UUID[])) <> cardinality(COALESCE(p_keys, ARRAY[]::TEXT[])) THEN
    RAISE EXCEPTION 'wh_superseding_history_apply: % event ids and % keys', cardinality(p_event_ids), cardinality(p_keys)
      USING ERRCODE = '22023';
  END IF;

  -- The cursor row, which the begin call of this transaction holds. Another instance holding it, or a cursor that moved
  -- since the window was chosen, means this window is not this call's to apply.
  SELECT * INTO v_row
  FROM __SCHEMA__.wh_superseding_history h
  WHERE h.event_type = v_type
  FOR UPDATE SKIP LOCKED;
  IF NOT FOUND OR v_row.last_event_id IS DISTINCT FROM p_after_id OR v_row.completed_at IS NOT NULL THEN
    RETURN QUERY SELECT 'skipped (the window is no longer this call''s)'::TEXT, 0::BIGINT, 0::BIGINT, 0::BIGINT,
      COALESCE(v_row.events_total, 0), COALESCE(v_row.events_matched, 0);
    RETURN;
  END IF;

  IF cardinality(p_event_ids) > 0 THEN
    -- The emit chains' per-stream locks, in stream id order (array_agg DISTINCT sorts), taken before any row lock. A
    -- newer event of a key stored concurrently is then either seen here or sees the stamp, so no old event is missed.
    SELECT array_agg(DISTINCT es.stream_id) INTO v_streams
    FROM __SCHEMA__.wh_event_store es
    WHERE es.event_id = ANY(p_event_ids);
    PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || u.sid::text))
    FROM unnest(v_streams) AS u(sid)
    ORDER BY u.sid;

    -- Stamp the rows still unflagged that no other transaction holds; a held row waits for the next batch.
    WITH given AS (
      SELECT g.event_id, g.supersede_key FROM unnest(p_event_ids, p_keys) AS g(event_id, supersede_key)
    ),
    locked AS MATERIALIZED (
      SELECT es.event_id
      FROM __SCHEMA__.wh_event_store es
      WHERE es.event_id = ANY(p_event_ids)
        AND es.event_type = v_type
        AND COALESCE(es.flags, 0) & c_flags_not_candidates = 0
      ORDER BY es.event_id
      FOR UPDATE SKIP LOCKED
    ),
    stamped AS (
      UPDATE __SCHEMA__.wh_event_store es
      SET flags = COALESCE(es.flags, 0) | c_flag_superseding,
          supersede_key = g.supersede_key
      FROM locked l
      JOIN given g ON g.event_id = l.event_id
      WHERE es.event_id = l.event_id
      RETURNING es.event_id
    )
    SELECT count(*) INTO v_stamped FROM stamped;

    -- Queue every event a newer event of its stream, type and key supersedes, in both directions: the older events of
    -- each event stamped here, and each event stamped here that a newer one already supersedes. So the order in which
    -- the history is stamped never leaves an old event unqueued. A separate statement, so it sees the stamps.
    WITH s AS (
      SELECT es.event_id, es.stream_id, es.event_type, es.supersede_key, es.version
      FROM __SCHEMA__.wh_event_store es
      WHERE es.event_id = ANY(p_event_ids)
        AND es.supersede_key IS NOT NULL
        AND COALESCE(es.flags, 0) & c_flag_superseding <> 0
    ),
    to_queue AS (
      SELECT o.event_id, o.stream_id
      FROM s
      JOIN __SCHEMA__.wh_event_store o
        ON o.stream_id = s.stream_id
       AND o.event_type = s.event_type
       AND o.supersede_key = s.supersede_key
       AND o.version < s.version
      UNION
      SELECT s.event_id, s.stream_id
      FROM s
      WHERE EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store n
        WHERE n.stream_id = s.stream_id
          AND n.event_type = s.event_type
          AND n.supersede_key = s.supersede_key
          AND n.version > s.version)
    ),
    queued AS (
      INSERT INTO __SCHEMA__.wh_superseded_events (event_id, stream_id, queued_at)
      SELECT q.event_id, q.stream_id, NOW() FROM to_queue q
      ON CONFLICT DO NOTHING
      RETURNING 1
    )
    SELECT count(*) INTO v_queued FROM queued;

    -- A given row of the type still unflagged was held by another transaction (a row deleted meanwhile is gone).
    SELECT count(*) INTO v_held
    FROM __SCHEMA__.wh_event_store es
    WHERE es.event_id = ANY(p_event_ids)
      AND es.event_type = v_type
      AND COALESCE(es.flags, 0) & c_flags_not_candidates = 0;
    IF v_held > 0 THEN
      SELECT es.event_id INTO v_first_held
      FROM __SCHEMA__.wh_event_store es
      WHERE es.event_id = ANY(p_event_ids)
        AND es.event_type = v_type
        AND COALESCE(es.flags, 0) & c_flags_not_candidates = 0
      ORDER BY es.event_id
      LIMIT 1;
    END IF;
  END IF;

  -- The cursor passes the window, or stops just before the first held row so the next batch reads it again.
  IF v_held = 0 THEN
    v_cursor := p_through_id;
  ELSE
    SELECT es.event_id INTO v_cursor
    FROM __SCHEMA__.wh_event_store es
    WHERE es.event_id > COALESCE(p_after_id, __EMPTY_UUID__::uuid)
      AND es.event_id < v_first_held
    ORDER BY es.event_id DESC
    LIMIT 1;
    v_cursor := COALESCE(v_cursor, p_after_id);
  END IF;

  SELECT count(*) INTO v_matched
  FROM __SCHEMA__.wh_event_store es
  WHERE es.event_id > COALESCE(p_after_id, __EMPTY_UUID__::uuid)
    AND es.event_id <= v_cursor
    AND es.event_type = v_type;

  v_complete := v_held = 0 AND p_rows_scanned < GREATEST(COALESCE(p_scan_size, 1), 1);

  UPDATE __SCHEMA__.wh_superseding_history h SET
    last_event_id = v_cursor,
    events_matched = h.events_matched + v_matched,
    events_stamped = h.events_stamped + v_stamped,
    events_queued = h.events_queued + v_queued,
    completed_at = CASE WHEN v_complete THEN NOW() END,
    updated_at = NOW()
  WHERE h.event_type = v_type
  RETURNING * INTO v_row;

  RETURN QUERY SELECT
    CASE WHEN v_held > 0 THEN 'held (' || v_held || ' locked event(s) left for the next batch)'
         WHEN v_complete THEN 'finished'
         ELSE 'ok' END::TEXT,
    v_stamped, v_queued, v_held, v_row.events_total, v_row.events_matched;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_superseding_history_apply(TEXT, UUID, UUID, INTEGER, BIGINT, UUID[], TEXT[]) IS
  'Issue #2110: under the emit chains'' per-stream locks, stamps EventFlags.Superseding and the given keys on the given '
  'unflagged events of the type (SKIP LOCKED), queues every event a newer one of its stream, type and key supersedes, and '
  'moves the type''s cursor past the window or to just before the first held row. Deletes nothing: the reaper''s gates '
  'remain the only path to deletion.';
