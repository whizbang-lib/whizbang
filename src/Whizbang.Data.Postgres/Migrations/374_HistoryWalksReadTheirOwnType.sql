-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 374_HistoryWalksReadTheirOwnType.sql
-- Date: 2026-11-09
-- Description: A change to the stored history of one event type reads that type's events, not the whole store (#2205).
--
--              345 brought the history a superseding type stored before its declaration under the superseding rule by
--              walking the WHOLE event store by event id, 5,000 events a batch, once per type. A walk cost the store's
--              size times the number of types: on a 4.27M-event store a type with 622 events had read 3.5M events after
--              17 hours, and every newly superseding type, and every group change, paid a full pass again.
--
--              345 walked the primary key to avoid an index every append would maintain. The type's events can be read
--              without a new one: idx_event_store_type_commit_sequence on (event_type, commit_sequence), built
--              concurrently after the migrations for the paced backfill (#2092), holds exactly one type's stamped
--              events in commit order. Every type-scoped history change now goes through one walker the core owns:
--
--                The cursor  wh_type_history_walks, one row per (operation, event type): the last commit sequence
--                            passed, the newest type definition and the operation's variant (the superseding group) the
--                            walk started under, its counts, and when it completed.
--                ensure      wh_type_history_ensure creates the walk the first time a type is seen, in a statement of
--                            its own, and restarts it from the first event when the variant changes (SKIP LOCKED).
--                begin       wh_type_history_begin takes the walk (SKIP LOCKED: one instance per type), restarts it when
--                            the type-definition registry holds a newer definition than it started under, and chooses
--                            the window: the type's next p_scan_size stamped events past the cursor, in commit order.
--                apply       each operation's own apply (wh_superseding_history_apply here) changes the window's rows
--                            and hands the cursor to _wh_type_history_advance, which moves it, counts what was read and
--                            decides completion.
--
--              Completion: a walk completes only once a window came back short (it read past the type's newest commit
--              sequence) AND none of the type's events are still unstamped. Walking in commit order is what the
--              superseded-event reaper's gate already requires (commit_sequence IS NOT NULL): an event is stamped by the
--              one stamper (a duty with one holder, every stamp fenced by its epoch), so it is numbered after every
--              sequence a walk has passed and is read by a later batch. A walk that reached the end while events of the
--              type are still unstamped reports 'waiting' and completes on the cycle after they are stamped.
--
--              The cost: a batch reads its window through the type index (an index-only range), the window's rows by
--              key, and counts what it read through the same index. A type with N events completes in ceil(N / scan)
--              batches whatever the size of the store. Until the concurrent build has made the index valid, the same
--              walk answers through idx_event_store_type_tenant (195), reading the type's events to sort them: still the
--              type's own cost, never the store's. begin plans each call against its own type and cursor
--              (force_custom_plan), as do the statements that end a batch, so a generic plan cannot settle on a walk
--              of idx_event_store_commit_sequence that reads every type to find one.
--
--              The superseding reconcile moves onto the walker: wh_superseding_history_rows and _apply take commit
--              sequences instead of event ids, keep every rule 345 and 352 established (the emit chains' per-stream
--              locks, SKIP LOCKED rows, stamping in either order, groups), and wh_superseding_history_begin and _ensure
--              are retired for the generic pair. The cursor rows 345 kept (wh_superseding_history) are cleared: every
--              superseding type's walk starts again from its first event under this migration, which re-reads the
--              type once and changes nothing already stamped. The table itself stays, empty, because 352 alters it and a
--              re-run of 352 must still find it.
--
--              Nothing on the append path changes: no index is added, and no write path is redefined.
--
--              Number 374: 372 is the highest on develop, 373 is taken by a change in flight, and 359 to 364 by an open
--              pull request.
--
-- Dependencies: 345 and 352 (the superseding reconcile this replaces: wh_superseding_history, its functions, the group),
--               295 (wh_superseded_events), 046 (commit_sequence), 195 (idx_event_store_type_tenant), 075
--               (wh_type_definitions), 077 (wh_event_body), 006 (normalize_event_type); idx_event_store_type_commit_sequence
--               is built after the migrations (ConcurrentIndexBuild, #2092)
-- Objects: wh_type_history_walks, wh_type_history_ensure, wh_type_history_begin, _wh_type_history_window_is_mine,
--          _wh_type_history_advance, wh_superseding_history_rows, wh_superseding_history_apply
-- Constants: the double-underscore tokens in this file are substituted from Migrations/constants.txt at apply time
--            (README rule 12).

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests): the superseding functions
-- take commit sequences now, and begin and ensure are the generic walker's.
SELECT __SCHEMA__.drop_all_overloads('wh_superseding_history_ensure');
SELECT __SCHEMA__.drop_all_overloads('wh_superseding_history_begin');
SELECT __SCHEMA__.drop_all_overloads('wh_superseding_history_rows');
SELECT __SCHEMA__.drop_all_overloads('wh_superseding_history_apply');
SELECT __SCHEMA__.drop_all_overloads('wh_type_history_ensure');
SELECT __SCHEMA__.drop_all_overloads('wh_type_history_begin');
SELECT __SCHEMA__.drop_all_overloads('_wh_type_history_window_is_mine');
SELECT __SCHEMA__.drop_all_overloads('_wh_type_history_advance');

-- ===========================================================================================
-- The cursor, one row per operation and type
-- ===========================================================================================

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_type_history_walks (
  operation TEXT NOT NULL,
  event_type TEXT NOT NULL,
  definition_watermark INTEGER NOT NULL DEFAULT 0,
  variant TEXT NULL,
  last_commit_sequence BIGINT NULL,
  events_total BIGINT NOT NULL DEFAULT 0,
  events_matched BIGINT NOT NULL DEFAULT 0,
  events_changed BIGINT NOT NULL DEFAULT 0,
  events_queued BIGINT NOT NULL DEFAULT 0,
  events_blocked BIGINT NOT NULL DEFAULT 0,
  started_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  completed_at TIMESTAMPTZ NULL,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  CONSTRAINT pk_type_history_walks PRIMARY KEY (operation, event_type)
);

COMMENT ON TABLE __SCHEMA__.wh_type_history_walks IS
  'One walk of one event type''s stored history by one operation (374, #2205): superseding (the history stored before '
  'a type was declared [Superseding]) or ephemeral (the history stored before a type was declared [Ephemeral]). The '
  'last commit sequence passed, the newest type definition and the variant (the superseding group) the walk started '
  'under, the type''s events when it started and how many it has read, changed, queued and left blocked, and when it '
  'completed. Delete a row to walk the type again.';

-- 345's cursor rows walked by event id; they mean nothing to the walker. Every type starts again from its first event.
-- The table stays, empty, because 352 alters it and a re-run of 352 must still find it.
DELETE FROM __SCHEMA__.wh_superseding_history;

COMMENT ON TABLE __SCHEMA__.wh_superseding_history IS
  'Retired by 374 (#2205): the superseding history walk keeps its cursor in wh_type_history_walks. Kept, empty, so a '
  're-run of 345 and 352 finds the table they create and alter.';

-- ===========================================================================================
-- The walker: ensure, begin, and the two helpers every operation's apply calls
-- ===========================================================================================

-- <docs>fundamentals/events/superseding-events#history-stored-before-the-declaration</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingGroupSqlTests.cs:Ensure_AChangedGroup_RestartsTheWalk_TheSameGroupDoesNotAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Reconcile_WhileAnotherInstanceHoldsTheType_SkipsWithoutWaiting_AndDoesNoWorkAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingGroupSqlTests.cs:Reconcile_TypesThatJoinAGroupLater_AreReStamped_AndTheOlderEventOfTheGroupIsReapedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_type_history_ensure(
  p_operation TEXT,
  p_event_type TEXT,
  p_definition_name TEXT,
  p_variant TEXT DEFAULT NULL
)
RETURNS VOID AS $$
DECLARE
  v_type TEXT := __SCHEMA__.normalize_event_type(p_event_type);
BEGIN
  -- A type seen for the first time starts a walk, under the newest definition the registry holds for it. Its events are
  -- counted once, through the type's index, so the step can report how many are left.
  IF NOT EXISTS (
    SELECT 1 FROM __SCHEMA__.wh_type_history_walks h WHERE h.operation = p_operation AND h.event_type = v_type
  ) THEN
    INSERT INTO __SCHEMA__.wh_type_history_walks (operation, event_type, definition_watermark, events_total, variant)
    VALUES (
      p_operation,
      v_type,
      (SELECT COALESCE(MAX(d.definition_id), 0) FROM __SCHEMA__.wh_type_definitions d
       WHERE d.event_type = __SCHEMA__.normalize_event_type(p_definition_name)),
      (SELECT count(*) FROM __SCHEMA__.wh_event_store es WHERE es.event_type = v_type),
      p_variant)
    ON CONFLICT DO NOTHING;
    RETURN;
  END IF;

  -- A walk whose variant changed (a superseding type that joined, left or moved between groups) starts again from the
  -- first event under the new one. A row another instance holds is left for the next cycle (SKIP LOCKED): never waits.
  PERFORM 1 FROM __SCHEMA__.wh_type_history_walks h
  WHERE h.operation = p_operation AND h.event_type = v_type AND h.variant IS DISTINCT FROM p_variant
  FOR UPDATE SKIP LOCKED;
  IF FOUND THEN
    UPDATE __SCHEMA__.wh_type_history_walks h SET
      variant = p_variant,
      last_commit_sequence = NULL,
      events_total = (SELECT count(*) FROM __SCHEMA__.wh_event_store es WHERE es.event_type = v_type),
      events_matched = 0,
      events_changed = 0,
      events_queued = 0,
      events_blocked = 0,
      started_at = NOW(),
      completed_at = NULL,
      updated_at = NOW()
    WHERE h.operation = p_operation AND h.event_type = v_type;
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_type_history_ensure(TEXT, TEXT, TEXT, TEXT) IS
  'Issue #2205: creates an operation''s history walk of a type the first time the type is seen, in a statement of its '
  'own, so wh_type_history_begin never waits on another instance''s uncommitted row; restarts the walk when its variant '
  '(the superseding group) changes, SKIP LOCKED.';

-- Whether the type has events the stamper has not numbered yet. A walk cannot complete while it has: they are read
-- once stamped. The unstamped set is small in steady state, and this reads it through whichever of the unstamped index
-- (046) and the type's index (195) the planner finds cheaper.
-- <docs>fundamentals/events/superseding-events#history-stored-before-the-declaration</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TypeHistoryWalkSqlTests.cs:Reconcile_EventsUnstampedAtTheStart_AreReconciledOnceStamped_AndHoldTheWalkOpenAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TypeHistoryWalkSqlTests.cs:Begin_AWalkPastTheEnd_WaitsForUnstampedEvents_ThenFinishesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Step_ReportsProgress_AndOneLineWhenATypeFinishesAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_type_history_advance(
  p_operation TEXT,
  p_event_type TEXT,
  p_after BIGINT,
  p_cursor BIGINT,
  p_scan_size INTEGER,
  p_rows_scanned BIGINT,
  p_held BIGINT,
  p_changed BIGINT,
  p_queued BIGINT,
  p_blocked BIGINT
)
RETURNS TABLE(status TEXT, events_total BIGINT, events_matched BIGINT) AS $$
DECLARE
  v_type TEXT := __SCHEMA__.normalize_event_type(p_event_type);
  v_matched BIGINT := 0;
  v_past_the_end BOOLEAN := p_held = 0 AND p_rows_scanned < GREATEST(COALESCE(p_scan_size, 1), 1);
  v_waiting BOOLEAN := FALSE;
  v_row __SCHEMA__.wh_type_history_walks%ROWTYPE;
BEGIN
  -- The events of the type the cursor passed, counted in the type's index alone.
  IF p_cursor IS NOT NULL THEN
    SELECT count(*) INTO v_matched
    FROM __SCHEMA__.wh_event_store es
    WHERE es.event_type = v_type
      AND es.commit_sequence IS NOT NULL
      AND es.commit_sequence > COALESCE(p_after, -1)
      AND es.commit_sequence <= p_cursor;
  END IF;

  IF v_past_the_end THEN
    v_waiting := EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_type = v_type AND es.commit_sequence IS NULL);
  END IF;

  UPDATE __SCHEMA__.wh_type_history_walks h SET
    last_commit_sequence = p_cursor,
    events_matched = h.events_matched + v_matched,
    events_changed = h.events_changed + COALESCE(p_changed, 0),
    events_queued = h.events_queued + COALESCE(p_queued, 0),
    events_blocked = h.events_blocked + COALESCE(p_blocked, 0),
    completed_at = CASE WHEN v_past_the_end AND NOT v_waiting THEN NOW() END,
    updated_at = NOW()
  WHERE h.operation = p_operation AND h.event_type = v_type
  RETURNING * INTO v_row;

  RETURN QUERY SELECT
    CASE WHEN p_held > 0 THEN 'held (' || p_held || ' locked event(s) left for the next batch)'
         WHEN v_waiting THEN 'waiting (events of the type are not stamped yet)'
         WHEN v_past_the_end THEN 'finished'
         ELSE 'ok' END::TEXT,
    v_row.events_total, v_row.events_matched;
END;
$$ LANGUAGE plpgsql SET plan_cache_mode = force_custom_plan;

COMMENT ON FUNCTION __SCHEMA__._wh_type_history_advance(TEXT, TEXT, BIGINT, BIGINT, INTEGER, BIGINT, BIGINT, BIGINT, BIGINT, BIGINT) IS
  'Issue #2205: ends one batch of a type history walk. Moves the cursor, adds what the batch read and did to the walk, '
  'and completes the walk when the window came back short, nothing was held, and none of the type''s events is still '
  'unstamped. Status ok, held, waiting (unstamped events remain) or finished.';

-- Whether the window after p_after is still this transaction's to apply: the walk's row is held by this transaction
-- (begin took it) or by nobody, its cursor has not moved, and it is not complete.
-- <docs>fundamentals/events/superseding-events#history-stored-before-the-declaration</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Apply_MismatchedArrays_OrAWindowThatIsNotItsOwn_ChangeNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Reconcile_RunsInBoundedBatches_AndResumesFromItsCursor_AfterACrashAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_type_history_window_is_mine(p_operation TEXT, p_event_type TEXT, p_after BIGINT)
RETURNS BOOLEAN AS $$
DECLARE
  v_row __SCHEMA__.wh_type_history_walks%ROWTYPE;
BEGIN
  SELECT * INTO v_row
  FROM __SCHEMA__.wh_type_history_walks h
  WHERE h.operation = p_operation AND h.event_type = __SCHEMA__.normalize_event_type(p_event_type)
  FOR UPDATE SKIP LOCKED;
  RETURN FOUND AND v_row.last_commit_sequence IS NOT DISTINCT FROM p_after AND v_row.completed_at IS NULL;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_type_history_window_is_mine(TEXT, TEXT, BIGINT) IS
  'Issue #2205: whether a type history walk''s window after p_after is still the calling transaction''s to apply: the '
  'walk is held by it or by nobody (SKIP LOCKED), its cursor has not moved, and it is not complete.';

-- <docs>fundamentals/events/superseding-events#history-stored-before-the-declaration</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TypeHistoryWalkSqlTests.cs:Reconcile_CostTracksTheTypesOwnEvents_NotTheStoreAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TypeHistoryWalkSqlTests.cs:TheWalk_ReadsTheTypeThroughItsIndex_NeverTheStoreAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TypeHistoryWalkSqlTests.cs:Begin_AWalkPastTheEnd_WaitsForUnstampedEvents_ThenFinishesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Reconcile_WhileAnotherInstanceHoldsTheType_SkipsWithoutWaiting_AndDoesNoWorkAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Reconcile_ANewerDefinitionOfTheType_WalksItAgainFromTheStartAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Reconcile_RunsInBoundedBatches_AndResumesFromItsCursor_AfterACrashAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_type_history_begin(
  p_operation TEXT,
  p_event_type TEXT,
  p_definition_name TEXT,
  p_scan_size INTEGER DEFAULT 5000
)
RETURNS TABLE(
  status TEXT,
  after_sequence BIGINT,
  through_sequence BIGINT,
  rows_scanned BIGINT,
  events_total BIGINT,
  events_matched BIGINT,
  events_changed BIGINT,
  events_queued BIGINT
)
LANGUAGE plpgsql
SET plan_cache_mode = force_custom_plan
AS $$
DECLARE
  v_scan INTEGER := GREATEST(COALESCE(p_scan_size, 1), 1);
  v_type TEXT := __SCHEMA__.normalize_event_type(p_event_type);
  v_definition INTEGER;
  v_row __SCHEMA__.wh_type_history_walks%ROWTYPE;
  v_through BIGINT;
  v_scanned BIGINT;
  v_waiting BOOLEAN;
BEGIN
  -- The newest definition the registry holds for the type: a walk started under an older one walks again.
  SELECT COALESCE(MAX(d.definition_id), 0) INTO v_definition
  FROM __SCHEMA__.wh_type_definitions d
  WHERE d.event_type = __SCHEMA__.normalize_event_type(p_definition_name);

  -- One instance per type: a row another instance holds means it is walking the type, and this call leaves it to it.
  -- A type with no row has not been ensured (wh_type_history_ensure), and nothing is walked.
  SELECT * INTO v_row
  FROM __SCHEMA__.wh_type_history_walks h
  WHERE h.operation = p_operation AND h.event_type = v_type
  FOR UPDATE SKIP LOCKED;
  IF NOT FOUND THEN
    RETURN QUERY SELECT
      CASE WHEN EXISTS (
             SELECT 1 FROM __SCHEMA__.wh_type_history_walks h WHERE h.operation = p_operation AND h.event_type = v_type)
           THEN 'skipped (another instance is reconciling)'
           ELSE 'skipped (no walk; wh_type_history_ensure starts one)' END::TEXT,
      NULL::BIGINT, NULL::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT;
    RETURN;
  END IF;

  IF v_definition > v_row.definition_watermark THEN
    UPDATE __SCHEMA__.wh_type_history_walks h SET
      definition_watermark = v_definition,
      last_commit_sequence = NULL,
      events_total = (SELECT count(*) FROM __SCHEMA__.wh_event_store es WHERE es.event_type = v_type),
      events_matched = 0,
      events_changed = 0,
      events_queued = 0,
      events_blocked = 0,
      started_at = NOW(),
      completed_at = NULL,
      updated_at = NOW()
    WHERE h.operation = p_operation AND h.event_type = v_type
    RETURNING * INTO v_row;
  END IF;

  IF v_row.completed_at IS NOT NULL THEN
    RETURN QUERY SELECT 'complete'::TEXT, v_row.last_commit_sequence, v_row.last_commit_sequence, 0::BIGINT,
      v_row.events_total, v_row.events_matched, v_row.events_changed, v_row.events_queued;
    RETURN;
  END IF;

  -- The window: the type's next stamped events past the cursor, in commit order, read in the type's index
  -- (idx_event_store_type_commit_sequence, or idx_event_store_type_tenant until that one is valid).
  SELECT count(*), max(w.commit_sequence)
  INTO v_scanned, v_through
  FROM (
    SELECT es.commit_sequence
    FROM __SCHEMA__.wh_event_store es
    WHERE es.event_type = v_type
      AND es.commit_sequence IS NOT NULL
      AND es.commit_sequence > COALESCE(v_row.last_commit_sequence, -1)
    ORDER BY es.commit_sequence
    LIMIT v_scan
  ) w;

  IF v_scanned = 0 THEN
    -- Past the type's newest stamped event. Complete only when none of its events is still waiting for its number.
    v_waiting := EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_type = v_type AND es.commit_sequence IS NULL);
    IF v_waiting THEN
      RETURN QUERY SELECT 'waiting (events of the type are not stamped yet)'::TEXT, v_row.last_commit_sequence,
        v_row.last_commit_sequence, 0::BIGINT, v_row.events_total, v_row.events_matched, v_row.events_changed,
        v_row.events_queued;
      RETURN;
    END IF;
    UPDATE __SCHEMA__.wh_type_history_walks h SET completed_at = NOW(), updated_at = NOW()
    WHERE h.operation = p_operation AND h.event_type = v_type
    RETURNING * INTO v_row;
    RETURN QUERY SELECT 'finished'::TEXT, v_row.last_commit_sequence, v_row.last_commit_sequence, 0::BIGINT,
      v_row.events_total, v_row.events_matched, v_row.events_changed, v_row.events_queued;
    RETURN;
  END IF;

  RETURN QUERY SELECT 'ok'::TEXT, v_row.last_commit_sequence, v_through, v_scanned,
    v_row.events_total, v_row.events_matched, v_row.events_changed, v_row.events_queued;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_type_history_begin(TEXT, TEXT, TEXT, INTEGER) IS
  'Issue #2205: takes an operation''s history walk of a type (SKIP LOCKED), restarts it when the type-definition '
  'registry holds a newer definition than the walk started under, and returns the next window: up to p_scan_size of the '
  'type''s stamped events past the cursor, in commit order, read in the type''s index. Status ok, complete, finished '
  '(read to the end now), waiting (read to the end, but events of the type are not stamped yet) or skipped.';

-- ===========================================================================================
-- The superseding reconcile on the walker (345, 352)
-- ===========================================================================================

-- <docs>fundamentals/events/superseding-events#history-stored-before-the-declaration</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Reconcile_HistoryStoredBeforeTheDeclaration_IsReapedDownToTheNewestOfEachKey_OnceConsumedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Reconcile_ATypeThatIsNotSuperseding_IsNotTouchedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Reconcile_AnEphemeralEventOfTheType_IsLeftToTheEphemeralReaperAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TypeHistoryWalkSqlTests.cs:TheWalk_ReadsTheTypeThroughItsIndex_NeverTheStoreAsync</tests>
-- <docs>fundamentals/events/superseding-events#superseding-groups</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingGroupSqlTests.cs:Reconcile_TypesThatJoinAGroupLater_AreReStamped_AndTheOlderEventOfTheGroupIsReapedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_superseding_history_rows(p_event_type TEXT, p_after BIGINT, p_through BIGINT)
RETURNS TABLE(event_id UUID, event_data JSONB)
LANGUAGE plpgsql STABLE
SET plan_cache_mode = force_custom_plan
AS $$
BEGIN
  -- PL/pgSQL, not SQL: a SQL function's body is planned with its arguments as parameters whatever plan_cache_mode says,
  -- and a generic plan of this window reads every event of the store between the two commit sequences through
  -- idx_event_store_commit_sequence. Planned with the real type and window, it reads the type's index.
  --
  -- A Sourced event (24 = EventFlags.Ephemeral (8) | Compacted (16) clear) of the window is a candidate when it is not
  -- flagged superseding (128), or (352, #2124) when it is flagged under another group than the one the walk stamps.
  RETURN QUERY
  SELECT es.event_id, b.event_data
  FROM __SCHEMA__.wh_event_store es
  JOIN __SCHEMA__.wh_event_body b ON b.event_id = es.event_id
  LEFT JOIN __SCHEMA__.wh_type_history_walks h ON h.operation = 'superseding' AND h.event_type = es.event_type
  WHERE es.event_type = __SCHEMA__.normalize_event_type(p_event_type)
    AND es.commit_sequence IS NOT NULL
    AND es.commit_sequence > COALESCE(p_after, -1)
    AND es.commit_sequence <= p_through
    AND COALESCE(es.flags, 0) & 24 = 0
    AND (COALESCE(es.flags, 0) & 128 = 0 OR es.supersede_group IS DISTINCT FROM h.variant)
  ORDER BY es.commit_sequence;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_superseding_history_rows(TEXT, BIGINT, BIGINT) IS
  'Issue #2110: the events of the type in the window (p_after, p_through] of commit sequences that carry no superseding '
  'flag and are Sourced, with their payloads, for the maintenance step to read each key from. 352 (#2124): and the '
  'flagged ones stamped under another group than the walk''s. 374 (#2205): the window is the type''s own, in commit order.';

-- <docs>fundamentals/events/superseding-events#history-stored-before-the-declaration</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Reconcile_HistoryStoredBeforeTheDeclaration_IsReapedDownToTheNewestOfEachKey_OnceConsumedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Reconcile_ANullKey_IsStampedUnkeyed_AndNeverQueued_AKeylessTypeKeepsOnePerStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Reconcile_OldEventsTheGatesProtect_StayAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Reconcile_ASecondRun_ChangesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Reconcile_AnOldEventStampedAfterTheNewerOneOfItsKey_IsQueuedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Reconcile_ARowAnotherTransactionHolds_IsLeftForTheNextBatch_WithoutWaitingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Reconcile_ReceivedCopies_AreReconciledInTheReceivingStoreAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Apply_MismatchedArrays_OrAWindowThatIsNotItsOwn_ChangeNothingAsync</tests>
-- <docs>fundamentals/events/superseding-events#superseding-groups</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingGroupSqlTests.cs:Reconcile_TypesThatJoinAGroupLater_AreReStamped_AndTheOlderEventOfTheGroupIsReapedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_superseding_history_apply(
  p_event_type TEXT,
  p_after BIGINT,
  p_through BIGINT,
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
  c_operation CONSTANT TEXT := 'superseding';
  c_flag_superseding CONSTANT INTEGER := 128;    -- EventFlags.Superseding (1 << 7), 295
  c_flags_state_based CONSTANT INTEGER := 24;    -- Ephemeral (1 << 3) | Compacted (1 << 4)
  v_type TEXT := __SCHEMA__.normalize_event_type(p_event_type);
  v_group TEXT;                                   -- 352 (#2124): the group the walk stamps
  v_streams UUID[];
  v_stamped BIGINT := 0;
  v_queued BIGINT := 0;
  v_held BIGINT := 0;
  v_first_held BIGINT;
  v_cursor BIGINT;
  v_end RECORD;
BEGIN
  IF cardinality(COALESCE(p_event_ids, ARRAY[]::UUID[])) <> cardinality(COALESCE(p_keys, ARRAY[]::TEXT[])) THEN
    RAISE EXCEPTION 'wh_superseding_history_apply: % event ids and % keys', cardinality(p_event_ids), cardinality(p_keys)
      USING ERRCODE = '22023';
  END IF;

  -- The walk's row, which the begin call of this transaction holds. Another instance holding it, or a cursor that moved
  -- since the window was chosen, means this window is not this call's to apply.
  IF NOT __SCHEMA__._wh_type_history_window_is_mine(c_operation, v_type, p_after) THEN
    RETURN QUERY SELECT 'skipped (the window is no longer this call''s)'::TEXT, 0::BIGINT, 0::BIGINT, 0::BIGINT,
      COALESCE((SELECT h.events_total FROM __SCHEMA__.wh_type_history_walks h WHERE h.operation = c_operation AND h.event_type = v_type), 0),
      COALESCE((SELECT h.events_matched FROM __SCHEMA__.wh_type_history_walks h WHERE h.operation = c_operation AND h.event_type = v_type), 0);
    RETURN;
  END IF;
  SELECT h.variant INTO v_group
  FROM __SCHEMA__.wh_type_history_walks h WHERE h.operation = c_operation AND h.event_type = v_type;

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
        AND COALESCE(es.flags, 0) & c_flags_state_based = 0
        AND (COALESCE(es.flags, 0) & c_flag_superseding = 0 OR es.supersede_group IS DISTINCT FROM v_group)
      ORDER BY es.event_id
      FOR UPDATE SKIP LOCKED
    ),
    stamped AS (
      UPDATE __SCHEMA__.wh_event_store es
      SET flags = COALESCE(es.flags, 0) | c_flag_superseding,
          supersede_key = g.supersede_key,
          supersede_group = v_group
      FROM locked l
      JOIN given g ON g.event_id = l.event_id
      WHERE es.event_id = l.event_id
      RETURNING es.event_id
    )
    SELECT count(*) INTO v_stamped FROM stamped;

    -- Queue every event a newer event of its stream, group (352: the event type when ungrouped) and key supersedes, in
    -- both directions: the older events of each event stamped here, and each event stamped here that a newer one already
    -- supersedes. So the order in which the history is stamped never leaves an old event unqueued. A separate statement,
    -- so it sees the stamps.
    WITH s AS (
      SELECT es.event_id, es.stream_id, COALESCE(es.supersede_group, es.event_type) AS grp, es.supersede_key, es.version
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
       AND COALESCE(o.supersede_group, o.event_type) = s.grp
       AND o.supersede_key = s.supersede_key
       AND o.version < s.version
      UNION
      SELECT s.event_id, s.stream_id
      FROM s
      WHERE EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store n
        WHERE n.stream_id = s.stream_id
          AND COALESCE(n.supersede_group, n.event_type) = s.grp
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
    SELECT count(*), min(es.commit_sequence) INTO v_held, v_first_held
    FROM __SCHEMA__.wh_event_store es
    WHERE es.event_id = ANY(p_event_ids)
      AND es.event_type = v_type
      AND COALESCE(es.flags, 0) & c_flags_state_based = 0
      AND (COALESCE(es.flags, 0) & c_flag_superseding = 0 OR es.supersede_group IS DISTINCT FROM v_group);
  END IF;

  -- The cursor passes the window, or stops just before the first held row so the next batch reads it again.
  IF v_held = 0 THEN
    v_cursor := p_through;
  ELSE
    SELECT max(es.commit_sequence) INTO v_cursor
    FROM __SCHEMA__.wh_event_store es
    WHERE es.event_type = v_type
      AND es.commit_sequence IS NOT NULL
      AND es.commit_sequence > COALESCE(p_after, -1)
      AND es.commit_sequence < v_first_held;
    v_cursor := COALESCE(v_cursor, p_after);
  END IF;

  SELECT * INTO v_end
  FROM __SCHEMA__._wh_type_history_advance(
    c_operation, v_type, p_after, v_cursor, p_scan_size, p_rows_scanned, v_held, v_stamped, v_queued, 0);

  RETURN QUERY SELECT v_end.status, v_stamped, v_queued, v_held, v_end.events_total, v_end.events_matched;
END;
$$ LANGUAGE plpgsql SET plan_cache_mode = force_custom_plan;

COMMENT ON FUNCTION __SCHEMA__.wh_superseding_history_apply(TEXT, BIGINT, BIGINT, INTEGER, BIGINT, UUID[], TEXT[]) IS
  'Issue #2110: under the emit chains'' per-stream locks, stamps EventFlags.Superseding, the given keys and the walk''s '
  'group on the given unflagged events of the type (SKIP LOCKED), queues every event a newer one of its stream, group '
  'and key supersedes, and moves the type''s walk past the window or to just before the first held row. Deletes nothing: '
  'the reaper''s gates remain the only path to deletion. 374 (#2205): the window is a range of the type''s commit '
  'sequences, and the walk ends through _wh_type_history_advance.';
