-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 449_RedeliveredCopiesKeepTheOriginsOrder.sql
-- Date: 2026-11-15
-- Description: A redelivered copy of a superseding event keeps its key and its place in the origin's order, so a stale
--              copy never becomes the newest (#2350).
--
--              A consumer that reaps the older copy of a key before its origin does reads as short, and a redelivery
--              restores the copy. The redelivery bundle's children carried no superseding key (fixed in
--              CompositeInboxFanout, which now stamps the key and group the live receive path stamps), and the restored
--              copy is stored at the end of the consumer's stream: above the newest copy by local version. Every
--              function that decided which copy of a key is newer compared local versions, so the restored stale copy
--              read as the newest: arriving, it queued the real newest for the reaper.
--
--              _wh_supersedes is now the one answer to "is this copy newer than that one": for two copies received from
--              the same origin, both carrying the origin's commit sequence, the origin's order; otherwise the stored
--              version, as before. It is a SQL function the planner inlines, so the probes keep their index. The queue,
--              the history walk and the reaper's newest check use it, and the queue now queues in both directions, as
--              the history walk already did: a stored copy that an already-stored copy supersedes is queued itself, so
--              a restored older copy is recognized as superseded on arrival. SupersedingOrderGuardTests fails on a
--              function that orders the copies of a key by comparing versions itself.
--
--              A copy that an already held copy of its key supersedes is never stored (store_inbox_messages), whether it
--              arrives for the first time (a live receive out of the origin's order, an integrity or an operator
--              redelivery) or is a repair child deduplication remembers: it is settled as satisfied
--              (__REPAIR_SATISFIED_SUPERSEDED__), so no perspective is offered it after the newest, and the bundle
--              reconciles count it present and apart (superseded_satisfied_count), so it is never read as a deficit.
--
--              The count is a new result column, and PostgreSQL refuses to change a function's result under CREATE OR
--              REPLACE (42P13). The earlier definers of the two reconciles (253 through 448) create them without a
--              drop, so a replay of any of them over a changed result would fail. The counting reconciles are new
--              functions, wh_reconcile_redelivered_bundle_counting_satisfied and
--              wh_reconcile_redelivered_bundles_counting_satisfied; the two reconciles keep their result and their
--              behavior (448), so a service on the previous release keeps calling them during a rolling deploy.
--              MigrationResultTypeReRunGuardTests fails on a definer that changes a result without dropping first.
--
--              Number 449: reserved for this change.
--
-- Reversal: reversible (the four functions go back to their last words in 352, 380, 448 and 368; _wh_supersedes and the
--           two counting reconciles go)
-- Dependencies: 252 and 368 (store_inbox_messages), 352 (_wh_queue_superseded_events, supersede_group), 380 (wh_superseding_history_apply),
--               448 (wh_reap_superseded_events)
-- Objects: _wh_supersedes, _wh_queue_superseded_events, wh_superseding_history_apply, wh_reap_superseded_events,
--          store_inbox_messages, wh_reconcile_redelivered_bundle_counting_satisfied,
--          wh_reconcile_redelivered_bundles_counting_satisfied (new; they read 448's reconciles, unchanged)
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- Whether one copy of a superseding key is newer than another: the origin's commit sequence when both were received from
-- the same origin with it, the stored version otherwise. One SELECT, IMMUTABLE and without SET, so the planner inlines it.
-- <docs>fundamentals/events/superseding-events#redelivered-copies</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredSupersedingCopySqlTests.cs:ARedeliveredOlderCopy_IsNeverStored_IsRecordedSatisfied_AndNoPerspectiveSeesItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredSupersedingCopySqlTests.cs:CopiesWithoutTheOriginsSequence_AreStillOrderedByVersion_AsBeforeAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_supersedes(
  p_origin UUID, p_origin_sequence BIGINT, p_version BIGINT,
  p_other_origin UUID, p_other_origin_sequence BIGINT, p_other_version BIGINT
) RETURNS BOOLEAN AS $$
  SELECT CASE
    WHEN p_origin IS NOT NULL AND p_origin = p_other_origin
         AND p_origin_sequence IS NOT NULL AND p_other_origin_sequence IS NOT NULL
      THEN p_origin_sequence > p_other_origin_sequence
    ELSE p_version > p_other_version
  END
$$ LANGUAGE sql IMMUTABLE;

COMMENT ON FUNCTION __SCHEMA__._wh_supersedes(UUID, BIGINT, BIGINT, UUID, BIGINT, BIGINT) IS
'Whether a copy of a superseding key is newer than another (#2350): by the origin''s commit sequence when both were received from the same origin with it, by stored version otherwise.';

-- ── _wh_queue_superseded_events: last word 352, copied whole ──
-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- Queues the older events of each given event's stream, group and key, and each given event an already-stored copy of its
-- key supersedes, ordered by _wh_supersedes (449, #2350). Called by the emit chains and wh_append_event after they store an
-- event with a key; a separate statement there, so it sees the events stored just before it.
-- <docs>fundamentals/events/superseding-events#redelivered-copies</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredSupersedingCopySqlTests.cs:ARedeliveredOlderCopy_IsNeverStored_IsRecordedSatisfied_AndNoPerspectiveSeesItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredSupersedingCopySqlTests.cs:CopiesWithoutTheOriginsSequence_AreStillOrderedByVersion_AsBeforeAsync</tests>
-- <docs>fundamentals/events/superseding-events#superseding-groups</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Emit_Outbox_StampsTheKeyOnlyOnASupersedingEvent_AndQueuesTheOlderOneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingGroupSqlTests.cs:Emit_ANewerEventOfAnotherTypeInTheGroup_QueuesTheOlder_InEitherOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingGroupSqlTests.cs:Emit_UngroupedTypes_StillSupersedeOnlyTheirOwnTypeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingGroupSqlTests.cs:Emit_AGroupedAndAnUngroupedType_NeverSupersedeEachOtherAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingGroupSqlTests.cs:TheGroupProbe_IsServedByTheGroupIndexAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_queue_superseded_events(p_event_ids UUID[], p_now TIMESTAMPTZ)
RETURNS VOID AS $$
BEGIN
  -- 449 (#2350): in both directions, as the history walk queues: the older copies of each stored event's key, and each
  -- stored event an already-stored copy supersedes. The second is a restored older copy: stored above the newest by
  -- version, it is older in its origin's order (_wh_supersedes), so it is queued itself and the newest is not.
  INSERT INTO __SCHEMA__.wh_superseded_events (event_id, stream_id, queued_at)
  SELECT DISTINCT q.event_id, q.stream_id, p_now
  FROM (
    SELECT older.event_id, older.stream_id
    FROM __SCHEMA__.wh_event_store newer
    JOIN __SCHEMA__.wh_event_store older
      ON older.stream_id = newer.stream_id
     AND COALESCE(older.supersede_group, older.event_type) = COALESCE(newer.supersede_group, newer.event_type)
     AND older.supersede_key = newer.supersede_key
     AND __SCHEMA__._wh_supersedes(newer.origin_service_id, newer.origin_commit_sequence, newer.version,
                               older.origin_service_id, older.origin_commit_sequence, older.version)
    WHERE newer.event_id = ANY(p_event_ids)
      AND newer.supersede_key IS NOT NULL
    UNION ALL
    SELECT stored.event_id, stored.stream_id
    FROM __SCHEMA__.wh_event_store stored
    WHERE stored.event_id = ANY(p_event_ids)
      AND stored.supersede_key IS NOT NULL
      AND EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store n
        WHERE n.stream_id = stored.stream_id
          AND COALESCE(n.supersede_group, n.event_type) = COALESCE(stored.supersede_group, stored.event_type)
          AND n.supersede_key = stored.supersede_key
          AND __SCHEMA__._wh_supersedes(n.origin_service_id, n.origin_commit_sequence, n.version,
                                 stored.origin_service_id, stored.origin_commit_sequence, stored.version))
  ) q
  ON CONFLICT DO NOTHING;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_queue_superseded_events(UUID[], TIMESTAMPTZ) IS
  'Queues, in wh_superseded_events, every older event of the stream, group and key of each given event (295, #1729); '
  'the group is supersede_group, or the event type for an ungrouped row (352, #2124).';

-- ── wh_superseding_history_apply: last word 380, copied whole ──
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
      SELECT es.event_id, es.stream_id, COALESCE(es.supersede_group, es.event_type) AS grp, es.supersede_key, es.version,
             es.origin_service_id, es.origin_commit_sequence
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
       -- 449 (#2350): which copy is newer, in the origin's order where both carry it
       AND __SCHEMA__._wh_supersedes(s.origin_service_id, s.origin_commit_sequence, s.version,
                                  o.origin_service_id, o.origin_commit_sequence, o.version)
      UNION
      SELECT s.event_id, s.stream_id
      FROM s
      WHERE EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store n
        WHERE n.stream_id = s.stream_id
          AND COALESCE(n.supersede_group, n.event_type) = s.grp
          AND n.supersede_key = s.supersede_key
          AND __SCHEMA__._wh_supersedes(n.origin_service_id, n.origin_commit_sequence, n.version,
                                    s.origin_service_id, s.origin_commit_sequence, s.version))
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
  'the reaper''s gates remain the only path to deletion. 380 (#2205): the window is a range of the type''s commit '
  'sequences, and the walk ends through _wh_type_history_advance.';

-- ── wh_reap_superseded_events: last word 448, copied whole ──
-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- <docs>fundamentals/events/superseding-events#what-a-rebuild-keeps</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamStartedAtSqlTests.cs:Reap_TheStreamsFirstEvent_RecordsItsBusinessTimeOnTheLedgerAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamStartedAtSqlTests.cs:Reap_ALaterEvent_RecordsNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/RebuildCreatedAtIntegrationTests.cs:RebuildBlueGreen_AfterTheFirstEventIsReaped_DatesTheRowAtTheStreamsStartAsync</tests>
-- <docs>fundamentals/events/superseding-events#the-reaper</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_SupersededAndConsumed_IsDeletedWithItsBodyAndItsDigestContribution_TheNewestIsKeptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_UnprocessedPerspectiveWork_KeepsIt_UntilItIsProcessedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_ACursorOnIt_KeepsIt_UntilTheCursorMovesOnAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_ASnapshotOnIt_KeepsIt_UntilTheSnapshotIsGoneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_AnActiveDestructionHold_KeepsIt_ALapsedOneDoesNotAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_AnEventTheStamperHasNotReached_IsKeptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_ADifferentTypeOrKeyInTheSameStream_IsUntouchedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_TheNewestOfItsKey_IsKept_EvenWhenQueuedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_DebugMode_KeepsEverythingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_AQueuedEventAlreadyGone_IsForgottenAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_RemovingFoldedHistory_RefoldsAndBumpsTheGenerationOnce_AnAtMostOnceEventDoesNotAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_TakesAtMostTheBatchSize_OldestQueuedFirstAsync</tests>
-- <docs>fundamentals/events/superseding-events#superseding-groups</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingGroupSqlTests.cs:Reap_AnEventSupersededByAnotherTypeOfItsGroup_IsDeleted_TheNewestIsKeptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingGroupSqlTests.cs:Reap_AQueuedEventWhoseOnlyNewerEventIsOutsideItsGroup_IsKeptAsync</tests>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_reap_superseded_events(p_batch_size INTEGER DEFAULT 5000)
RETURNS TABLE(task_name TEXT, rows_affected BIGINT, duration_ms DOUBLE PRECISION, status TEXT) AS $$
DECLARE
  v_start TIMESTAMPTZ := clock_timestamp();
  v_debug BOOLEAN;
  v_candidates UUID[];
  v_streams UUID[];
  v_victims UUID[];
  v_lanes UUID[];
  v_mins BIGINT[];
  v_maxs BIGINT[];
  v_i INTEGER;
  v_deleted BIGINT := 0;
  c_task CONSTANT TEXT := 'reap_superseded_events';
  c_flag_superseding CONSTANT INTEGER := 128;  -- EventFlags.Superseding (1 << 7)
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  c_flags_state_based CONSTANT INTEGER := 24;  -- EventFlags.Ephemeral (1 << 3) | EventFlags.Compacted (1 << 4)
BEGIN
  -- debug_mode keeps every event for forensics, as every other reaper does.
  SELECT COALESCE(
    (SELECT s.setting_value::BOOLEAN FROM __SCHEMA__.wh_settings s WHERE s.setting_key = 'debug_mode'),
    FALSE
  ) INTO v_debug;
  IF v_debug THEN
    RETURN QUERY SELECT c_task, 0::BIGINT,
      EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION, 'skipped (debug_mode=true)'::TEXT;
    RETURN;
  END IF;

  -- A queued event something else already removed (a prefix truncate, an archive, a purge) is forgotten.
  DELETE FROM __SCHEMA__.wh_superseded_events q
  WHERE NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = q.event_id);

  -- The batch, oldest queued first, and its streams in lock order.
  SELECT array_agg(b.event_id), array_agg(DISTINCT b.stream_id)
  INTO v_candidates, v_streams
  FROM (
    SELECT q.event_id, q.stream_id
    FROM __SCHEMA__.wh_superseded_events q
    ORDER BY q.queued_at, q.event_id
    LIMIT p_batch_size
  ) b;
  IF v_candidates IS NULL THEN
    RETURN QUERY SELECT c_task, 0::BIGINT,
      EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION, 'ok'::TEXT;
    RETURN;
  END IF;

  -- The emit chains' per-stream locks, in stream id order (array_agg DISTINCT sorts), so nothing is appended to,
  -- truncated from or archived out of these streams while their events are judged and removed.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || u.sid::text))
  FROM unnest(v_streams) AS u(sid)
  ORDER BY u.sid;

  -- The gates, read under the locks.
  SELECT array_agg(es.event_id) INTO v_victims
  FROM __SCHEMA__.wh_event_store es
  WHERE es.event_id = ANY(v_candidates)
    AND es.supersede_key IS NOT NULL
    AND COALESCE(es.flags, 0) & c_flag_superseding <> 0
    AND COALESCE(es.flags, 0) & c_flags_state_based = 0
    -- Never the newest of its key: a newer event of the same stream, group and key is stored. 352 (#2124): the group is
    -- the stored supersede_group, or the event type for an ungrouped row, so an ungrouped type is its own group.
    AND EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_store n
      WHERE n.stream_id = es.stream_id
        AND COALESCE(n.supersede_group, n.event_type) = COALESCE(es.supersede_group, es.event_type)
        AND n.supersede_key = es.supersede_key
        -- 449 (#2350): newer in the origin's order where both copies carry it, so a restored older copy stored above
        -- the newest is the one reaped, and the newest never is
        AND __SCHEMA__._wh_supersedes(n.origin_service_id, n.origin_commit_sequence, n.version,
                                  es.origin_service_id, es.origin_commit_sequence, es.version))
    -- The commit-sequence stamper has reached it, so nothing that walks the stamped order still owes it a place.
    AND es.commit_sequence IS NOT NULL
    -- Every perspective that consumes it has processed it.
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
      WHERE pe.event_id = es.event_id AND pe.processed_at IS NULL)
    -- No cursor rests on it (fk_perspective_cursors_event is RESTRICT on the EF Core schema).
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_cursors c
      WHERE c.stream_id = es.stream_id AND c.last_event_id = es.event_id)
    -- No snapshot rests on it: a rewind restores a snapshot and moves the cursor onto its event.
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_snapshots s
      WHERE s.stream_id = es.stream_id AND s.snapshot_event_id = es.event_id)
    -- A PreDestruction hook's Cancel or Defer is honored, as by every other path that removes an event.
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_destruction_hold h
      WHERE h.event_id = es.event_id AND h.hold_until > NOW());

  IF v_victims IS NOT NULL THEN
    -- The sequence range of the fold-relevant victims, per lane, for the epoch refold after the delete (as 263).
    SELECT array_agg(t.lane), array_agg(t.mn), array_agg(t.mx)
    INTO v_lanes, v_mins, v_maxs
    FROM (
      SELECT COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid) AS lane,
             MIN(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mn,
             MAX(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mx
      FROM __SCHEMA__.wh_event_store es
      WHERE es.event_id = ANY(v_victims)
        AND __DIGEST_ELIGIBLE__(es)
      GROUP BY 1
    ) t
    WHERE t.mn IS NOT NULL;

    -- Subtract the victims from wh_stream_digests before they go, as the prefix truncate does (263): XOR is
    -- self-inverse, so folding the same hashes again removes them. Only rows a fold ever added are subtracted.
    PERFORM __SCHEMA__._wh_fold_stream_digests(
      array_agg(COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid)), array_agg(COALESCE(es.scope ->> 't', '')),
      array_agg(es.event_type), array_agg(es.stream_id), array_agg(es.event_id), 'unfold', NOW())
    FROM __SCHEMA__.wh_event_store es
    WHERE es.event_id = ANY(v_victims)
      AND __DIGEST_ELIGIBLE__(es);

    DELETE FROM __SCHEMA__.wh_stream_digests d
    WHERE d.stream_id = ANY(v_streams) AND d.event_count <= 0;

    -- 358 (#2152): a victim that is its stream's first event (version 0 or 1) leaves its business time on the stream's
    -- ledger row, so a rebuild that writes the stream's row afresh still dates it at the stream's start. Written once:
    -- the lowest such victim of each stream, and never over a value already recorded.
    UPDATE __SCHEMA__.wh_stream_lifecycle l
    SET first_event_at = f.business_time
    FROM (
      SELECT DISTINCT ON (es.stream_id) es.stream_id,
             COALESCE(__SCHEMA__._wh_event_business_time(eb.metadata), es.created_at) AS business_time
      FROM __SCHEMA__.wh_event_store es
      LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = es.event_id
      WHERE es.event_id = ANY(v_victims) AND es.version <= 1
      ORDER BY es.stream_id, es.version
    ) f
    WHERE l.stream_id = f.stream_id AND l.first_event_at IS NULL;

    -- Lapsed holds of the victims go with them; the gates proved no active one remains. Their processed perspective
    -- work rows are left to Task 3's purge: this task runs after the work tables were visited in their canonical order,
    -- and touching one again here would break that order.
    DELETE FROM __SCHEMA__.wh_event_destruction_hold h WHERE h.event_id = ANY(v_victims);

    -- Bodies first, then pointers (wh_receptor_processing rows cascade on the EF Core schema).
    DELETE FROM __SCHEMA__.wh_event_body eb WHERE eb.event_id = ANY(v_victims);
    DELETE FROM __SCHEMA__.wh_event_store es WHERE es.event_id = ANY(v_victims);
    GET DIAGNOSTICS v_deleted = ROW_COUNT;

    DELETE FROM __SCHEMA__.wh_superseded_events q WHERE q.event_id = ANY(v_victims);

    -- Refold the sealed epochs that covered the victims, then bump the generation once, so consumers reset their
    -- seals and re-verify instead of alarming on a deliberate change (093).
    IF v_lanes IS NOT NULL THEN
      FOR v_i IN 1 .. array_length(v_lanes, 1) LOOP
        PERFORM __SCHEMA__._wh_refold_epochs_covering(v_lanes[v_i], v_mins[v_i], v_maxs[v_i]);
      END LOOP;
      PERFORM __SCHEMA__._wh_bump_origin_generation();
    END IF;
  END IF;

  RETURN QUERY SELECT c_task, v_deleted,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION, 'ok'::TEXT;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_reap_superseded_events(INTEGER) IS
  'Task 16 of perform_maintenance (295, #1729): deletes up to p_batch_size queued superseded events, with their bodies '
  'and their digest contribution, once a newer event of the same stream, group and key is stored and every consumer has '
  'processed them (no unprocessed perspective work, no cursor or snapshot on them, no active destruction hold, commit '
  'sequence stamped). Skipped under debug_mode. Reduces the digests as wh_truncate_stream_prefix does, refolding the '
  'covering sealed epochs and bumping the origin generation once when it removed folded history. 352: a group is the stored supersede_group, or the event type for an ungrouped row (#2124). 358: records the business time of a victim that is its stream''s first event on the stream''s ledger row (first_event_at, #2152).';

-- ── store_inbox_messages: last word 368, copied whole, with an older copy of a held superseding key settled as satisfied ──
-- <docs>fundamentals/events/superseding-events#redelivered-copies</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredSupersedingCopySqlTests.cs:ARedeliveredOlderCopy_IsNeverStored_IsRecordedSatisfied_AndNoPerspectiveSeesItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredSupersedingCopySqlTests.cs:ARedeliveredCopyNewerThanTheHeldOne_IsRestoredAsBeforeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredSupersedingCopySqlTests.cs:AnOlderCopyArrivingForTheFirstTime_AfterTheNewest_IsNeverStored_AndIsRecordedSatisfiedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TwoServiceIntegrityConvergenceTests.cs:AnOlderRedeliveredCopy_IsNeverStoredOrApplied_AndThePairConvergesWithNoFurtherResendAsync</tests>
-- store_inbox_messages: last word 252, with a message discarded on receive stored without its body. Signature unchanged.
-- <docs>resilience/stream-integrity#remembered-but-missing</docs>
-- <docs>messaging/transports/transport-consumer#batch-handler</docs>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStoreInboxMessagesTests.cs:SingleMessage_StoresInboxAndDedupAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStoreInboxMessagesTests.cs:DuplicateMessageId_SecondCallNoOpsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredDeadLetterReentrySqlTests.cs:ARecoveredMessage_IsStoredInTheSameWorkShapeAsADeliveryOfTheSameEnvelopeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InboxDiscardedOnReceiveSqlTests.cs:AMessageDiscardedOnReceive_IsStoredSettled_WithItsReason_AndNeverClaimedOrCopiedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredMissingEventRestoreSqlTests.cs:ARepairedEvent_DeduplicationRemembers_TheStoreLost_IsRestored_AndTheBucketHealsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredMissingEventRestoreSqlTests.cs:ARestoredEvent_IsStoredStateOnly_SoTriggerReceptorsThatRanOnItsFirstArrivalDoNotRunAgainAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredMissingEventRestoreSqlTests.cs:ADuplicateOfAStoredEvent_IsStillDropped_EvenAsRepairTrafficAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredMissingEventRestoreSqlTests.cs:OrdinaryTraffic_DeduplicationRemembers_TheStoreLost_IsDroppedAsBeforeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredMissingEventRestoreSqlTests.cs:ARepairedEvent_StillPendingInTheInbox_IsNotStoredTwiceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredMissingEventRestoreSqlTests.cs:ARepairedEphemeralEvent_IsNotRestoredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredMissingEventRestoreSqlTests.cs:ARepairedEvent_OnAStreamWhoseEarlierHistoryWasTruncated_IsRefused_AndReportedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InboxDiscardedOnReceiveKeepsNoBodySqlTests.cs:AMessageDiscardedOnReceive_IsStoredWithoutItsBody_AndIsStillADuplicateOnRedeliveryAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InboxDiscardedOnReceiveKeepsNoBodySqlTests.cs:AMessageDiscardedOnReceive_RedeliveredForProcessing_IsStillADuplicate_AndNotDispatchedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InboxDiscardedOnReceiveKeepsNoBodySqlTests.cs:AnOrdinaryArrival_KeepsItsBodyAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InboxDiscardedOnReceiveKeepsNoBodySqlTests.cs:ADiscardedRedelivery_OfAProcessedMessage_LeavesItsStoredBodyAloneAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.store_inbox_messages(
  p_messages JSONB,
  p_instance_id UUID,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_partition_count INTEGER
) RETURNS TABLE(
  message_id UUID,
  stream_id UUID,
  was_newly_created BOOLEAN
) AS $$
#variable_conflict use_column
DECLARE
  c_field_flags CONSTANT TEXT := __ENVELOPE_FIELD_FLAGS__;
  v_msg RECORD;
  v_partition INTEGER;
  v_observations INTEGER;  -- 1 on first sight; N on the Nth redelivery
  v_probed_streams UUID[] := ARRAY[]::UUID[];
  v_empty_inbox_streams UUID[] := ARRAY[]::UUID[];
  v_notify_inbox_streams UUID[] := ARRAY[]::UUID[];
  v_is_new BOOLEAN;           -- 236: a first arrival, or a recovery that claimed its dead letter
  v_claimed INTEGER;          -- 236: rows the recovery claim or the message insert touched
  v_settled BOOLEAN;          -- 242: the consumer skipped it on receive, so the row is stored already settled
  -- 252 (#1843): the flags of events integrity never counts, so their absence from the store is no gap.
  c_flag_superseding CONSTANT INTEGER := 128;  -- 295: EventFlags.Superseding (1 << 7)
  c_flags_never_repaired CONSTANT INTEGER := 8 | 32;  -- EventFlags.Ephemeral (1 << 3) | EventFlags.AtMostOnce (1 << 5)
  v_restore BOOLEAN;          -- 252: repair traffic deduplication remembers and the store lost, stored again as state only
  v_refusal TEXT;             -- 252: why such a child is refused instead, stored settled with the reason
BEGIN
  IF jsonb_array_length(p_messages) = 0 THEN RETURN; END IF;

  FOR v_msg IN
    SELECT
      (elem->>__ENVELOPE_FIELD_MESSAGE_ID__)::UUID as msg_id,
      elem->>'HandlerName' as handler_name,
      elem->>'EnvelopeType' as envelope_type,
      elem->>'MessageType' as message_type,
      -- 368: a message the consumer discarded on receive keeps no body. Nothing reads a settled row's envelope or
      -- metadata (it is never claimed, copied, dispatched or dead-lettered, and deduplication is answered by
      -- wh_message_deduplication), so both are left unread here and stored empty by the COALESCE in the insert.
      CASE WHEN NULLIF(elem->>'DiscardedOnReceive', '') IS NULL THEN elem->'Envelope' END as envelope_data,
      CASE WHEN NULLIF(elem->>'DiscardedOnReceive', '') IS NULL THEN elem->'Metadata' END as metadata,
      elem->'Scope' as scope,
      (elem->>__ENVELOPE_FIELD_STREAM_ID__)::UUID as stream_id,
      (elem->>'IsEvent')::BOOLEAN as is_event,
      -- 149: the effective priority the consumer classified; zero (undeclared) and an absent field read as standard.
      COALESCE(NULLIF((elem->>'Priority')::INTEGER, 0), 150) as priority,
      -- EventFlags (062): same read as store_outbox_messages so collective events delivered
      -- cross-service via the inbox also route to the __collective__ sink. 232 (#1612): every bit of a name list.
      CASE
        WHEN elem->>c_field_flags IS NULL OR elem->>c_field_flags = '' THEN 0
        WHEN elem->>c_field_flags ~ '^[0-9]+$' THEN (elem->>c_field_flags)::INTEGER
        ELSE __SCHEMA__._wh_event_flags_from_names(elem->>c_field_flags)
      END as flags,
      -- 146 (#727): a zero GUID is "unknown", not a producer; COALESCE below then falls back to this service.
      NULLIF((elem->>'SourceServiceId')::UUID, __EMPTY_UUID__::UUID) as source_service_id,
      (elem->>'SourceCommitSequence')::BIGINT as source_commit_sequence,
      -- 236 (#1664): set only by dead-letter recovery, naming the dead-letter row the message re-enters from.
      (elem->>'RecoveredFromDeadLetterId')::UUID as recovered_from,
      -- 242 (#1720): set by a consumer that skipped the message on receive, naming why.
      NULLIF(elem->>'DiscardedOnReceive', '') as discarded_on_receive,
      -- 252 (#1843): set on the children of a redelivery that repair or an operator asked for.
      COALESCE((elem->>'RestoreIfMissing')::BOOLEAN, false) as restore_if_missing
    FROM jsonb_array_elements(p_messages) as elem
    ORDER BY (elem->>__ENVELOPE_FIELD_STREAM_ID__)::UUID NULLS FIRST, (elem->>__ENVELOPE_FIELD_MESSAGE_ID__)::UUID
  LOOP
    -- 236 (#1664): dead-letter recovery re-enters a message HERE, as one more arrival, so every arrival rule
    -- applies to it unchanged. Three things differ, and only for a row that names its dead letter:
    --   1. it claims that dead letter first (Recovering), in this transaction, so two recoveries of one row
    --      cannot both re-enter it, and a dead letter whose message is still in the inbox is left alone;
    --   2. a recovered id is usually already in wh_message_deduplication, so the observation is still counted
    --      below (poison bounding keeps working) but the message is not dropped as a duplicate;
    --   3. the work-state row carries the dead-letter id, so the completion settles the dead letter
    --      (process_inbox_completions) instead of recovery declaring it Recovered before anything ran.
    -- The attempts budget is refunded because the row is new: attempts starts at 0 as for every arrival.
    IF v_msg.recovered_from IS NOT NULL THEN
      UPDATE __SCHEMA__.wh_dead_letters dl
      SET recovery_status = 1,                  -- Recovering, until the inbox completes the message
          recovery_attempts = dl.recovery_attempts + 1,
          last_recovery_at = p_now
      WHERE dl.dead_letter_id = v_msg.recovered_from
        AND dl.source_id = v_msg.msg_id
        AND dl.recovery_status NOT IN (1, 2, 3, 4)  -- not already Recovering, HoldForReview, Recovered, PermanentlyFailed
        AND dl.recovered_at IS NULL
        AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_inbox i WHERE i.message_id = v_msg.msg_id);
      GET DIAGNOSTICS v_claimed = ROW_COUNT;
      IF v_claimed = 0 THEN
        CONTINUE;  -- claimed by another recovery, already settled, or the message is still in the inbox
      END IF;
    END IF;

    -- 121: DO UPDATE (was DO NOTHING) so a redelivery is COUNTED rather than silently swallowed.
    -- RETURNING gives the post-write count on both arms, so newness is read from the value
    -- (= 1) instead of from ROW_COUNT, which DO UPDATE would report as 1 either way.
    INSERT INTO __SCHEMA__.wh_message_deduplication AS dedup
      (message_id, first_seen_at, observation_count)
    VALUES (v_msg.msg_id, p_now, 1)
    ON CONFLICT ON CONSTRAINT wh_message_deduplication_pkey DO UPDATE
      SET observation_count = dedup.observation_count + 1
    RETURNING dedup.observation_count INTO v_observations;

    -- 252 (#1843): for repair traffic deduplication is checked against the event store, not trusted alone. The
    -- deduplication row is written on receipt and the event-store row can be lost afterwards (a failed fan-out in an
    -- older version, manual cleanup, a store restored from an older backup while deduplication survived), and then
    -- every redelivery repair asked for was dropped here, so the bucket never healed and repair asked forever. A
    -- repair child whose id was seen, that is an event integrity counts, and that neither the event store nor the
    -- inbox holds, is stored again. Its envelope is marked state only: deduplication remembers the message, so the
    -- reactions to it had their chance, and repair restores state, never a second reaction. One case is refused: a
    -- stream whose lowest stored version is above 1 had its earlier history truncated here (wh_truncate_stream_prefix
    -- keeps a stream's deduplication rows precisely so a redelivered copy is not stored below the truncation point),
    -- and a lost event cannot be told from a truncated one. The refused child is stored settled with the reason,
    -- which the redelivery's reconciliation reads (wh_reconcile_redelivered_bundle) to report the bucket once as one
    -- repair cannot change. A child the store or the inbox still holds is a duplicate, as before.
    v_restore := FALSE;
    v_refusal := NULL;
    -- 449 (#2350): a copy of a superseding key that a newer copy already held here supersedes is never stored: stored,
    -- it would sit above the newest by version and be applied after it. It is settled as satisfied, the redelivery's
    -- reconciliation reads it as present, and nothing claims, copies or applies it. This holds for a copy arriving for
    -- the first time, by any path (a live receive out of the origin's order, an integrity or an operator redelivery),
    -- and for a repair child deduplication remembers and the store lost. Newer is the origin's order
    -- (_wh_supersedes); without the origin's sequence on both copies nothing is refused, as before.
    IF v_msg.recovered_from IS NULL
       AND v_msg.discarded_on_receive IS NULL
       AND COALESCE(v_msg.is_event, false)
       AND v_msg.stream_id IS NOT NULL
       AND COALESCE(v_msg.flags, 0) & c_flag_superseding <> 0
       AND v_msg.metadata ->> 'sk' IS NOT NULL
       AND (v_observations = 1
            OR (v_msg.restore_if_missing
                AND COALESCE(v_msg.flags, 0) & c_flags_never_repaired = 0
                AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = v_msg.msg_id)
                AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_inbox i WHERE i.message_id = v_msg.msg_id)))
       AND EXISTS (
         SELECT 1 FROM __SCHEMA__.wh_event_store n
         WHERE n.stream_id = v_msg.stream_id
           AND n.supersede_key = v_msg.metadata ->> 'sk'
           AND COALESCE(n.supersede_group, n.event_type)
               = COALESCE(v_msg.metadata ->> 'sg', __SCHEMA__.normalize_event_type(v_msg.message_type))
           AND __SCHEMA__._wh_supersedes(n.origin_service_id, n.origin_commit_sequence, n.version,
                                        v_msg.source_service_id, NULLIF(v_msg.source_commit_sequence, 0), 2147483647)) THEN
      v_refusal := __REPAIR_SATISFIED_SUPERSEDED__;
    ELSIF v_observations > 1
       AND v_msg.restore_if_missing
       AND v_msg.recovered_from IS NULL
       AND v_msg.discarded_on_receive IS NULL
       AND COALESCE(v_msg.is_event, false)
       AND v_msg.stream_id IS NOT NULL
       AND COALESCE(v_msg.flags, 0) & c_flags_never_repaired = 0
       AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = v_msg.msg_id)
       AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_inbox i WHERE i.message_id = v_msg.msg_id) THEN
      IF (SELECT MIN(es.version) FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = v_msg.stream_id) > 1 THEN
        v_refusal := __REPAIR_REFUSED_TRUNCATED__;
      ELSE
        v_restore := TRUE;
      END IF;
    END IF;

    -- 236: a recovery that claimed its dead letter stores the message whatever the count says.
    -- 252: and so does a repair child restored or refused above.
    v_is_new := v_observations = 1 OR v_msg.recovered_from IS NOT NULL OR v_restore OR v_refusal IS NOT NULL;
    -- 242 (#1720): a message the consumer skipped on receive (no local consumer, directed at another service, this
    -- service's own echo, an event of an unknown type) is stored like every arrival, so the skip is durable and
    -- inspectable, but already settled: processed, Stored and Published, the reason in its error column. Nothing
    -- claims, copies or dispatches a processed row, so it rings no doorbell, pins no stream and is not new work.
    -- 252: a refused repair child is stored settled the same way.
    v_settled := v_msg.discarded_on_receive IS NOT NULL OR v_refusal IS NOT NULL;
    IF v_is_new THEN
      IF v_msg.stream_id IS NOT NULL THEN
        v_partition := __SCHEMA__.compute_partition(v_msg.stream_id, p_partition_count);
      ELSE
        v_partition := NULL;
      END IF;

      -- 114: emptiness probe, see store_outbox_messages; inbox pending = not processed
      -- and schedule-eligible (the drain-fetch predicate minus the lease dimension).
      IF NOT v_settled AND v_msg.stream_id IS NOT NULL AND NOT (v_msg.stream_id = ANY(v_probed_streams)) THEN
        v_probed_streams := array_append(v_probed_streams, v_msg.stream_id);

        -- 162: all three columns are work state now, so the probe reads the narrow table.
        --
        -- The ORDER BY is the remedy from 160 and 161, applied here BEFORE it bites rather than
        -- after. This is the third instance of the same shape: an emptiness probe by stream id with
        -- processed_at IS NULL under a bare LIMIT 1. The planner prices a sequential scan as though
        -- the first row it reads will match, because that predicate is true of nearly every row in
        -- a table carrying a backlog, and on a stream whose work is all done there is no match at
        -- all, so it reads the whole table. Ordering by the index key removes that plan instead of
        -- out-costing it, and stream_id is pinned by the equality so it changes no result. received_at
        -- is in the clause because ordering by stream_id alone is dropped as redundant.
        PERFORM 1 FROM __SCHEMA__.wh_inbox_state ist
          WHERE ist.stream_id = v_msg.stream_id
            AND ist.processed_at IS NULL
            AND (ist.scheduled_for IS NULL OR ist.scheduled_for <= p_now)
          ORDER BY ist.stream_id, ist.received_at
          LIMIT 1
          FOR SHARE OF ist SKIP LOCKED;   -- 140: a locked pending row is being completed; treat as empty and ring
        IF NOT FOUND THEN
          v_empty_inbox_streams := array_append(v_empty_inbox_streams, v_msg.stream_id);
        END IF;
      END IF;

      -- 162: the message row and its claim state are two inserts now. Both carry a DO NOTHING
    -- conflict clause, so the pair is idempotent under the redelivery this function exists to
    -- absorb: a duplicate leaves the existing message AND its existing claim state untouched,
    -- rather than resetting a lease some instance is holding.
    INSERT INTO __SCHEMA__.wh_inbox (
      message_id,
      handler_name,
      message_type,
      event_data,
      metadata,
      scope,
      stream_id,
      is_event,
      flags,
      received_at,
      source_service_id,
      source_commit_sequence,
      priority
    ) VALUES (
      v_msg.msg_id,
      v_msg.handler_name,
      v_msg.message_type,
      -- 252: a restored repair child is state only ('sto', the envelope's StateOnly on the wire).
      CASE WHEN v_restore
        THEN jsonb_set(COALESCE(v_msg.envelope_data, '{}'::jsonb), ARRAY['sto'], 'true'::jsonb)
        ELSE COALESCE(v_msg.envelope_data, '{}'::jsonb)
      END,
      COALESCE(v_msg.metadata, '{}'::jsonb),
      COALESCE(v_msg.scope, 'null'::jsonb),
      v_msg.stream_id,
      COALESCE(v_msg.is_event, false),
      COALESCE(v_msg.flags, 0),
      p_now,
      COALESCE(v_msg.source_service_id, (SELECT service_id FROM __SCHEMA__.wh_service_config LIMIT 1)),
      COALESCE(v_msg.source_commit_sequence, 0),
      v_msg.priority
    )
    ON CONFLICT ON CONSTRAINT wh_inbox_pkey DO NOTHING;
    -- 236: a recovery that lost the race to a concurrent arrival of the same id hands its dead letter back,
    -- so the dead letter is never left Recovering behind a row that does not name it.
    GET DIAGNOSTICS v_claimed = ROW_COUNT;
    IF v_claimed = 0 AND v_msg.recovered_from IS NOT NULL THEN
      UPDATE __SCHEMA__.wh_dead_letters dl
      SET recovery_status = 0
      WHERE dl.dead_letter_id = v_msg.recovered_from AND dl.recovery_status = 1;
      CONTINUE;
    END IF;
    -- 252: a restore that lost the race to a concurrent arrival of the same id leaves that arrival as it is.
    IF v_claimed = 0 AND v_restore THEN
      CONTINUE;
    END IF;

    -- The lease row, unleased and unattempted. A message with no lease row can never be claimed and
    -- would fail silently, so this insert is not optional bookkeeping: it is what makes the message
    -- visible to the claim at all.
    INSERT INTO __SCHEMA__.wh_inbox_state (
      message_id, stream_id, received_at, partition_number, priority, is_event,
      status, processed_at, instance_id, lease_expiry, attempts, recovered_from_dead_letter_id, error
    ) VALUES (
      v_msg.msg_id,
      v_msg.stream_id,
      p_now,
      v_partition,
      v_msg.priority,
      COALESCE(v_msg.is_event, false),
      CASE WHEN v_settled THEN 1 | 4 ELSE 1 END,  -- Stored flag; 242: and Published, for a row settled on receive
      CASE WHEN v_settled THEN p_now END,          -- 242: a settled row is processed as it arrives
      NULL,  -- No lease, immediately claimable by WorkCoordinatorPublisherWorker
      NULL,
      0,     -- Initial attempts
      v_msg.recovered_from,  -- 236: NULL for every arrival but a recovery
      -- 242: the reason a settled row records, on the column a processing failure would use; NULL otherwise.
      -- 252: a refused repair child records why it was refused.
      COALESCE(v_refusal, 'discarded on receive: ' || v_msg.discarded_on_receive)
    )
    ON CONFLICT (message_id) DO NOTHING;

    IF v_settled THEN
      CONTINUE;  -- 242: no doorbell, no pin, not new work
    END IF;

      IF v_msg.stream_id IS NOT NULL
         AND v_msg.stream_id = ANY(v_empty_inbox_streams)
         AND NOT (v_msg.stream_id = ANY(v_notify_inbox_streams)) THEN
        v_notify_inbox_streams := array_append(v_notify_inbox_streams, v_msg.stream_id);
      END IF;

      -- Pinning is ownership/routing, unchanged by 114.
      IF v_msg.stream_id IS NOT NULL AND p_instance_id IS NOT NULL THEN
        INSERT INTO __SCHEMA__.wh_active_streams
          (stream_id, partition_number, assigned_instance_id, last_activity_at)
        VALUES
          (v_msg.stream_id, COALESCE(v_partition, 0), p_instance_id, p_now)
        ON CONFLICT (stream_id) DO NOTHING;
      END IF;
      RETURN QUERY SELECT v_msg.msg_id AS message_id, v_msg.stream_id AS stream_id, TRUE AS was_newly_created;
    END IF;  -- end of the single-observation branch (236: or a claimed recovery)
  END LOOP;

  IF cardinality(v_notify_inbox_streams) > 0 THEN
    PERFORM __SCHEMA__.notify_instance_owners(__CATEGORY_INBOX__, v_notify_inbox_streams);
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.store_inbox_messages(JSONB, UUID, TIMESTAMPTZ, TIMESTAMPTZ, INTEGER) IS
  'Stores received inbox messages with deduplication (121: every arrival counted), priority (149), the work-state row (162). 236 (#1664): a message recovered from a dead letter (RecoveredFromDeadLetterId) re-enters here as one more arrival: it claims its dead letter, is stored even though its id was seen, and its work-state row names the dead letter for the completion to settle. 242 (#1720): a message the consumer skipped on receive (DiscardedOnReceive) is stored settled: processed, Stored and Published, the reason in error, never claimed, copied or returned as new work. 368: and without its body (event_data and metadata stored empty), since nothing reads a settled row''s body and deduplication does not need it. 252 (#1843): a repair child (RestoreIfMissing) whose id was seen but which neither the event store nor the inbox holds is stored again as state only, or stored settled as refused when its stream had earlier history truncated.';

-- New in 449: wh_reconcile_redelivered_bundle_by_first_hop (448) copied whole, its result gaining
-- superseded_satisfied_count. A new name, so no earlier definer of the old one ever meets this result.
SELECT __SCHEMA__.drop_all_overloads('wh_reconcile_redelivered_bundle_counting_satisfied');

-- ── wh_reconcile_redelivered_bundle_by_first_hop: last word 420, copied whole ──
-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- <docs>resilience/stream-integrity#pre-stamp-history-first-hop</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredOriginAdoptionSqlTests.cs:Adopt_EmptyingALocalBucket_ReadsOnlyTheBucketsItChanges_NotTheWholeLocalLaneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredOriginAdoptionSqlTests.cs:Adopt_WithoutTheOriginsName_AdoptsAsBeforeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredOriginAdoptionSqlTests.cs:Adopt_ABlankOrUnknownOriginName_AdoptsAsBeforeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredOriginAdoptionSqlTests.cs:Adopt_ServiceNamesCompareWithoutCase_AndAnotherServiceInAnyCaseIsRefusedAsync</tests>
-- <docs>resilience/stream-integrity#pre-stamp-history</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredMissingEventRestoreSqlTests.cs:ARepairedEvent_OnAStreamWhoseEarlierHistoryWasTruncated_IsRefused_AndReportedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredMissingEventRestoreSqlTests.cs:ARepairedEvent_DeduplicationRemembers_TheStoreLost_IsRestored_AndTheBucketHealsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredOriginAdoptionSqlTests.cs:Adopt_AnEventWhoseFirstHopNamesTheBundlesOrigin_AdoptsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredOriginAdoptionSqlTests.cs:Adopt_AnEventWhoseFirstHopNamesThisService_IsRefusedAndStaysLocalAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredOriginAdoptionSqlTests.cs:Adopt_AFirstHopWithoutAServiceIdentity_AdoptsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredOriginAdoptionSqlTests.cs:Adopt_TheLegacyLongFormHop_IsReadAndComparedWithoutCaseAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredOriginAdoptionSqlTests.cs:Adopt_EventsStoredWithoutAnOrigin_TakeTheBundlesOriginAndSequencesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredOriginAdoptionSqlTests.cs:Adopt_IntoSealedEpochs_RefoldsBothLanes_AndAdvancesTheOriginGenerationAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryBundleCatalogChurnSqlTests.cs:ReconcileBundle_ASecondCallOnTheConnection_WritesNothingToTheSystemCatalogAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryBundleCatalogChurnSqlTests.cs:ReconcileBundle_TwiceInOneTransaction_StartsFromAnEmptyWorkingTableAsync</tests>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
-- <docs>resilience/stream-integrity#per-bundle-work-table</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryBundleCatalogChurnSqlTests.cs:ReconcileBundle_AfterTheSessionIsReset_WritesNothingToTheSystemCatalogAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryReconcileEquivalenceSqlTests.cs:ReconcileBundleCountingSatisfied_GivesWhatTheWorkTableVersionGave_WithNothingSatisfiedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/PerCallFunctionsCreateNoTemporaryTableTests.cs:NoFunctionCreatesATemporaryTable_OutsideTheExemptOnesAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_reconcile_redelivered_bundle_counting_satisfied(
  p_origin_service_id UUID,
  p_origin_service_name TEXT,
  p_event_ids UUID[],
  p_commit_sequences BIGINT[]
) RETURNS TABLE(stream_id UUID, tenant_scope TEXT, event_type TEXT, present_count INTEGER, adopted_count INTEGER,
                refused_truncated_count INTEGER, refused_foreign_origin_count INTEGER,
                superseded_satisfied_count INTEGER) AS $$
#variable_conflict use_column
DECLARE
  c_flag_ephemeral CONSTANT INTEGER := 8;      -- EventFlags.Ephemeral (1 << 3)
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  c_field_hops CONSTANT TEXT := 'Hops';        -- EnvelopeMetadata.Hops, as the emit chain stores it (230)
  c_unknown_service CONSTANT TEXT := 'unknown';  -- ServiceInstanceInfo.Unknown, lower-cased: no service identity
  v_local_service_id UUID;
  v_adoptable BOOLEAN;
  v_origin_name TEXT;
  v_min BIGINT;
  v_max BIGINT;
  v_width BIGINT;
  v_leaving_streams UUID[];
  -- 420: the bundle's stored events, one element per event, in parallel arrays (was a temporary table).
  v_w_ids UUID[];
  v_w_streams UUID[];
  v_w_tenants TEXT[];
  v_w_types TEXT[];
  v_w_left_local BOOLEAN[];
  v_w_local_seqs BIGINT[];
  v_w_origin_seqs BIGINT[];
  v_w_adopted BOOLEAN[];
  v_w_refused BOOLEAN[];
  v_w_digested BOOLEAN[];
BEGIN
  IF cardinality(p_event_ids) IS DISTINCT FROM cardinality(p_commit_sequences) THEN
    RAISE EXCEPTION 'wh_reconcile_redelivered_bundle_counting_satisfied: % event ids but % commit sequences',
      cardinality(p_event_ids), cardinality(p_commit_sequences);
  END IF;

  -- The emit chain's normalization (087): the local service and the empty id are "locally originated".
  SELECT sc.service_id INTO v_local_service_id FROM __SCHEMA__.wh_service_config sc LIMIT 1;
  v_adoptable := p_origin_service_id IS NOT NULL
    AND p_origin_service_id <> __EMPTY_UUID__::uuid
    AND p_origin_service_id IS DISTINCT FROM v_local_service_id;
  -- No name (or the Unknown sentinel) leaves the first-hop check nothing to compare.
  v_origin_name := NULLIF(NULLIF(lower(btrim(p_origin_service_name)), ''), c_unknown_service);

  -- The streams' append locks, in the emit chain's order, so a concurrent fold of the same bucket waits for this one.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || s.sid::text))
  FROM (
    SELECT DISTINCT es.stream_id AS sid
    FROM unnest(p_event_ids) AS u(eid)
    JOIN __SCHEMA__.wh_event_store es ON es.event_id = u.eid
    ORDER BY 1
  ) s;

  -- 420 (#2181): the rows are gathered into arrays by one statement. 321 kept a temporary table per connection
  -- instead, but every session reset dropped it, and a pooled connection is reset each time it is handed out, so it
  -- was created again on almost every call: catalog rows written and deleted, an invalidation for every other
  -- session, fresh plans, and new statement-statistics entries per call. The bundle answer had the same fix in 369
  -- (#2179).
  SELECT array_agg(w.event_id), array_agg(w.stream_id), array_agg(w.tenant_scope), array_agg(w.event_type),
         array_agg(w.left_local_lane), array_agg(w.local_sequence), array_agg(w.origin_sequence),
         array_agg(w.adopted), array_agg(w.refused), array_agg(w.digested)
  INTO v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_left_local, v_w_local_seqs, v_w_origin_seqs, v_w_adopted,
       v_w_refused, v_w_digested
  FROM (
  SELECT
    c.event_id,
    c.stream_id,
    c.tenant_scope,
    c.event_type,
    c.fills_origin AND NOT c.refused,
    c.local_sequence,
    c.origin_sequence,
    (c.fills_origin AND NOT c.refused) OR c.fills_sequence,
    c.refused,
    c.digested
  FROM (
    SELECT
      es.event_id,
      es.stream_id,
      COALESCE(es.scope ->> 't', '') AS tenant_scope,
      es.event_type,
      v_adoptable AND es.origin_service_id IS NULL AS fills_origin,
      COALESCE(v_adoptable AND es.origin_service_id = p_origin_service_id AND es.origin_commit_sequence IS NULL
        AND COALESCE(m.seq, 0) > 0, false) AS fills_sequence,
      COALESCE(fh.service IS NOT NULL AND fh.service <> c_unknown_service AND fh.service <> v_origin_name, false) AS refused,
      es.commit_sequence AS local_sequence,
      COALESCE(es.origin_commit_sequence, NULLIF(m.seq, 0)) AS origin_sequence,
      __DIGEST_ELIGIBLE__(es) AS digested
    FROM (SELECT DISTINCT ON (u.eid) u.eid, u.seq FROM unnest(p_event_ids, p_commit_sequences) AS u(eid, seq) ORDER BY u.eid) m
    JOIN __SCHEMA__.wh_event_store es ON es.event_id = m.eid
    -- The first hop's service, read only for an event the bundle would give an origin, and only when there is a
    -- name to compare it with: the one-time catch-up, never an event that already carries its origin.
    LEFT JOIN LATERAL (
      SELECT NULLIF(lower(btrim(COALESCE(
               h.hop -> 'si' ->> 'sn',
               h.hop -> 'ServiceInstance' ->> 'ServiceName'))), '') AS service
      FROM (SELECT (eb.metadata -> c_field_hops) -> 0 AS hop
            FROM __SCHEMA__.wh_event_body eb
            WHERE eb.event_id = es.event_id
              AND v_adoptable AND v_origin_name IS NOT NULL AND es.origin_service_id IS NULL) h
    ) fh ON true
  ) c
  ) AS w(event_id, stream_id, tenant_scope, event_type, left_local_lane, local_sequence, origin_sequence, adopted,
        refused, digested);

  UPDATE __SCHEMA__.wh_event_store es
  SET origin_service_id = p_origin_service_id, origin_commit_sequence = r.origin_sequence
  FROM unnest(v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_left_local, v_w_local_seqs, v_w_origin_seqs,
                v_w_adopted, v_w_refused, v_w_digested)
         AS r(event_id, stream_id, tenant_scope, event_type, left_local_lane, local_sequence, origin_sequence,
              adopted, refused, digested)
  WHERE es.event_id = r.event_id AND r.adopted;

  -- 301 (#2000): the local-lane statements below are driven by the streams the bundle moved, as an array the planner
  -- can see. Joined only against the temporary rows (which carry no statistics), the planner reached the digests
  -- through the primary key's leading column, which matches the whole local lane, and read every local bucket on
  -- every bundle. With the streams named, idx_stream_digests_stream finds the handful of buckets the bundle changes.
  SELECT array_agg(DISTINCT r.stream_id) INTO v_leaving_streams
  FROM unnest(v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_left_local, v_w_local_seqs, v_w_origin_seqs,
                v_w_adopted, v_w_refused, v_w_digested)
         AS r(event_id, stream_id, tenant_scope, event_type, left_local_lane, local_sequence, origin_sequence,
              adopted, refused, digested)
  WHERE r.adopted AND r.digested AND r.left_local_lane;

  -- Digests: XOR is self-inverse, so an adopted event's hash leaves the local bucket and joins the origin's.
  -- 336 (#2106): every lane leaves through the one fold helper.
  PERFORM __SCHEMA__._wh_fold_stream_digests(
    array_agg(__EMPTY_UUID__::uuid), array_agg(r.tenant_scope), array_agg(r.event_type), array_agg(r.stream_id),
    array_agg(r.event_id), 'unfold', now())
  FROM unnest(v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_left_local, v_w_local_seqs, v_w_origin_seqs,
                v_w_adopted, v_w_refused, v_w_digested)
         AS r(event_id, stream_id, tenant_scope, event_type, left_local_lane, local_sequence, origin_sequence,
              adopted, refused, digested)
  WHERE r.adopted AND r.digested AND r.left_local_lane;
  DELETE FROM __SCHEMA__.wh_stream_digests d
  USING (SELECT DISTINCT r.tenant_scope, r.event_type, r.stream_id
         FROM unnest(v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_left_local, v_w_local_seqs, v_w_origin_seqs,
                       v_w_adopted, v_w_refused, v_w_digested)
                AS r(event_id, stream_id, tenant_scope, event_type, left_local_lane, local_sequence, origin_sequence,
                     adopted, refused, digested)
         WHERE r.adopted AND r.digested AND r.left_local_lane) l
  WHERE d.origin_service_id = __EMPTY_UUID__::uuid
    AND d.scope_tenant = l.tenant_scope AND d.event_type = l.event_type AND d.stream_id = l.stream_id
    AND d.stream_id = ANY(v_leaving_streams)
    AND d.event_count <= 0;

  PERFORM __SCHEMA__._wh_fold_stream_digests(
    array_agg(p_origin_service_id), array_agg(r.tenant_scope), array_agg(r.event_type), array_agg(r.stream_id),
    array_agg(r.event_id), 'fold', now())
  FROM unnest(v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_left_local, v_w_local_seqs, v_w_origin_seqs,
                v_w_adopted, v_w_refused, v_w_digested)
         AS r(event_id, stream_id, tenant_scope, event_type, left_local_lane, local_sequence, origin_sequence,
              adopted, refused, digested)
  WHERE r.adopted AND r.digested AND r.left_local_lane;

  -- Sealed epochs over the moved events: the origin's lane by origin sequence, the local lane by local sequence.
  SELECT MIN(r.origin_sequence), MAX(r.origin_sequence) INTO v_min, v_max
  FROM unnest(v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_left_local, v_w_local_seqs, v_w_origin_seqs,
                v_w_adopted, v_w_refused, v_w_digested)
         AS r(event_id, stream_id, tenant_scope, event_type, left_local_lane, local_sequence, origin_sequence,
              adopted, refused, digested)
  WHERE r.adopted AND r.digested AND r.origin_sequence IS NOT NULL;
  IF v_min IS NOT NULL THEN
    PERFORM __SCHEMA__._wh_refold_epochs_covering(p_origin_service_id, v_min, v_max);
  END IF;

  SELECT MIN(r.local_sequence), MAX(r.local_sequence) INTO v_min, v_max
  FROM unnest(v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_left_local, v_w_local_seqs, v_w_origin_seqs,
                v_w_adopted, v_w_refused, v_w_digested)
         AS r(event_id, stream_id, tenant_scope, event_type, left_local_lane, local_sequence, origin_sequence,
              adopted, refused, digested)
  WHERE r.adopted AND r.digested AND r.left_local_lane AND r.local_sequence IS NOT NULL;
  SELECT f.epoch_width INTO v_width
  FROM __SCHEMA__.wh_digest_epoch_frontiers f
  WHERE f.origin_service_id = __EMPTY_UUID__::uuid;
  IF v_min IS NOT NULL AND v_width IS NOT NULL
     AND __SCHEMA__.refold_digest_epochs(__EMPTY_UUID__::uuid, v_min / v_width, v_max / v_width) > 0 THEN
    PERFORM __SCHEMA__._wh_bump_origin_generation();
  END IF;

  RETURN QUERY
  SELECT b.sid, b.tenant, b.etype, SUM(b.present)::int, SUM(b.adopted)::int, SUM(b.truncated)::int, SUM(b.foreign_origin)::int,
         SUM(b.superseded)::int
  FROM (
    SELECT r.stream_id AS sid, r.tenant_scope AS tenant, r.event_type AS etype, 1 AS present,
           r.adopted::int AS adopted, 0 AS truncated, r.refused::int AS foreign_origin, 0 AS superseded
    FROM unnest(v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_left_local, v_w_local_seqs, v_w_origin_seqs,
                  v_w_adopted, v_w_refused, v_w_digested)
           AS r(event_id, stream_id, tenant_scope, event_type, left_local_lane, local_sequence, origin_sequence,
                adopted, refused, digested)
    UNION ALL
    -- 252: a child store_inbox_messages refused to restore, keyed as every digest fold keys it (the stream, the
    -- scope's tenant, the emit chain's normalized type).
    SELECT i.stream_id, COALESCE(i.scope ->> 't', ''), __SCHEMA__.normalize_event_type(i.message_type), 0, 0, 1, 0, 0
    FROM __SCHEMA__.wh_inbox i
    JOIN __SCHEMA__.wh_inbox_state ist ON ist.message_id = i.message_id
    WHERE i.message_id = ANY(p_event_ids)
      AND ist.error = __REPAIR_REFUSED_TRUNCATED__
      AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = i.message_id)
    UNION ALL
    -- 449 (#2350): a child a newer held copy of its key supersedes is satisfied: present, and counted apart.
    SELECT i.stream_id, COALESCE(i.scope ->> 't', ''), __SCHEMA__.normalize_event_type(i.message_type), 1, 0, 0, 0, 1
    FROM __SCHEMA__.wh_inbox i
    JOIN __SCHEMA__.wh_inbox_state ist ON ist.message_id = i.message_id
    WHERE i.message_id = ANY(p_event_ids)
      AND ist.error = __REPAIR_SATISFIED_SUPERSEDED__
  ) b
  GROUP BY b.sid, b.tenant, b.etype
  ORDER BY b.sid, b.tenant, b.etype;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_reconcile_redelivered_bundle_counting_satisfied(UUID, TEXT, UUID[], BIGINT[]) IS
'449 (#2350): wh_reconcile_redelivered_bundle_by_first_hop with superseded_satisfied_count: a child settled as satisfied because a newer held copy of its key supersedes it is present and counted apart. 420 (#2181): its working rows are gathered into arrays; 321''s per-connection temporary table is gone. 301 (#2000): the local-lane digest update and delete are driven by the moved streams through idx_stream_digests_stream. 253 (#1843, #1844): reconciles a redelivery bundle with the store; the one entry point per bundle. wh_adopt_redelivered_origin (249) with a first-hop check, plus the children store_inbox_messages (252) refused to restore past deduplication because the stream had earlier history truncated here (refused_truncated_count). An empty origin takes the bundle''s origin only when the stored event''s first hop names the bundle''s origin service (p_origin_service_name, compared without case) or carries no service identity; a first hop naming another service refuses it (the event keeps its empty origin and its local digest) and is counted in refused_foreign_origin_count. Everything else is 249''s: an empty origin commit sequence takes the event''s sequence, nothing is overwritten or re-attributed, the local service adopts nothing, digests move lanes and sealed epochs are refolded. Returns per (stream, tenant, type) bucket the events present, adopted, refused as truncated and refused by the first-hop check.';

-- New in 449: wh_reconcile_redelivered_bundles (448) copied whole, its result gaining superseded_satisfied_count.
-- A new name, so no earlier definer of the old one ever meets this result.
SELECT __SCHEMA__.drop_all_overloads('wh_reconcile_redelivered_bundles_counting_satisfied');

-- ── wh_reconcile_redelivered_bundles: last word 420, copied whole ──
-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- <docs>resilience/stream-integrity#redelivery-reconciles-run-as-a-batch</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryBatchReconcileSqlTests.cs:Batch_RefoldsEachSealedEpochOnce_NotOncePerBundleAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryBatchReconcileSqlTests.cs:Batch_FillsEverySequence_AsTheSingleBundleCallsWouldAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryBatchReconcileSqlTests.cs:Batch_AnEventTwoBundlesName_AdoptsOnce_ForTheFirstBundleAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryBatchReconcileSqlTests.cs:Batch_EventsThatAlreadyCarryTheirOrigin_AdoptNothing_AndRefoldNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryBatchReconcileSqlTests.cs:Batch_ReportsOriginsFilled_ApartFromSequencesFilledAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryBatchReconcileSqlTests.cs:Batch_AFirstHopNamingAnotherService_IsRefused_AndALaterBundleOfThatServiceAdoptsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryBatchReconcileSqlTests.cs:Batch_MovesDigestsFromTheLocalLane_AndRefoldsTheLocalLaneOnceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryBatchReconcileSqlTests.cs:Batch_ReportsTheChildrenTheStoreRefusedToRestore_PerBundleAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryBatchReconcileSqlTests.cs:Batch_MismatchedArrays_AreRefusedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryBatchReconcileSqlTests.cs:Batch_UnderAGenericPlan_FindsStoredEventsByKey_NotAStoreScanAsync</tests>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
-- <docs>resilience/stream-integrity#per-bundle-work-table</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryBundleCatalogChurnSqlTests.cs:ReconcileBatch_AfterTheSessionIsReset_WritesNothingToTheSystemCatalogAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryReconcileEquivalenceSqlTests.cs:ReconcileBundlesCountingSatisfied_GivesWhatTheWorkTableVersionGave_WithNothingSatisfiedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/PerCallFunctionsCreateNoTemporaryTableTests.cs:NoFunctionCreatesATemporaryTable_OutsideTheExemptOnesAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_reconcile_redelivered_bundles_counting_satisfied(
  p_bundle_origins   UUID[],     -- per bundle, by position (1-based): the origin it was published by
  p_bundle_names     TEXT[],     -- per bundle: the origin's service name from its first hop, or NULL
  p_event_bundles    INTEGER[],  -- per event: the position of its bundle in p_bundle_origins
  p_event_ids        UUID[],     -- per event: its id
  p_commit_sequences BIGINT[]    -- per event: its origin sequence, or NULL / 0 when the origin did not stamp one
) RETURNS TABLE(bundle INTEGER, stream_id UUID, tenant_scope TEXT, event_type TEXT, present_count INTEGER,
                adopted_count INTEGER, origins_filled_count INTEGER, refused_truncated_count INTEGER,
                refused_foreign_origin_count INTEGER,
                superseded_satisfied_count INTEGER) AS $$
#variable_conflict use_column
DECLARE
  c_flag_ephemeral CONSTANT INTEGER := 8;        -- EventFlags.Ephemeral (1 << 3)
  c_flag_at_most_once CONSTANT INTEGER := 32;    -- EventFlags.AtMostOnce (1 << 5), 214
  c_field_hops CONSTANT TEXT := 'Hops';          -- EnvelopeMetadata.Hops, as the emit chain stores it (230)
  c_unknown_service CONSTANT TEXT := 'unknown';  -- ServiceInstanceInfo.Unknown, lower-cased: no service identity
  v_local_service_id UUID;
  v_ids UUID[];
  v_origins UUID[];
  v_sequences BIGINT[];
  v_leaving_streams UUID[];
  v_local_width BIGINT;
  v_local_refolded INTEGER := 0;
  r_epoch RECORD;
  -- 420: one element per (bundle, stored event), in parallel arrays (was a temporary table).
  v_w_bundles INTEGER[];
  v_w_ids UUID[];
  v_w_streams UUID[];
  v_w_tenants TEXT[];
  v_w_types TEXT[];
  v_w_origins UUID[];
  v_w_local_seqs BIGINT[];
  v_w_origin_seqs BIGINT[];
  v_w_adopted BOOLEAN[];
  v_w_left_local BOOLEAN[];
  v_w_refused BOOLEAN[];
  v_w_digested BOOLEAN[];
BEGIN
  IF cardinality(p_bundle_origins) IS DISTINCT FROM cardinality(p_bundle_names) THEN
    RAISE EXCEPTION 'wh_reconcile_redelivered_bundles_counting_satisfied: % bundle origins but % bundle names',
      cardinality(p_bundle_origins), cardinality(p_bundle_names);
  END IF;
  IF cardinality(p_event_ids) IS DISTINCT FROM cardinality(p_event_bundles)
     OR cardinality(p_event_ids) IS DISTINCT FROM cardinality(p_commit_sequences) THEN
    RAISE EXCEPTION 'wh_reconcile_redelivered_bundles_counting_satisfied: % event ids, % bundle positions and % commit sequences',
      cardinality(p_event_ids), cardinality(p_event_bundles), cardinality(p_commit_sequences);
  END IF;

  -- The emit chain's normalization (087): the local service and the empty id are "locally originated".
  SELECT sc.service_id INTO v_local_service_id FROM __SCHEMA__.wh_service_config sc LIMIT 1;

  -- Every bundle's streams' append locks, once, in the emit chain's order.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || s.sid::text))
  FROM (
    SELECT DISTINCT es.stream_id AS sid
    FROM unnest(p_event_ids) AS u(eid)
    JOIN __SCHEMA__.wh_event_store es ON es.event_id = u.eid
    ORDER BY 1
  ) s;

  -- One row per (bundle, stored event). Each event adopts for the first bundle that can fill it; the others count it
  -- present only, as a later single-bundle call would have found it already filled.
  -- 420 (#2181): the rows are gathered into arrays by one statement. The temporary table was dropped by every
  -- session reset, and a pooled connection is reset each time it is handed out, so it was created again on almost
  -- every call: catalog rows written and deleted, an invalidation for every other session, fresh plans, and new
  -- statement-statistics entries per call. The bundle answer had the same fix in 369 (#2179).
  SELECT array_agg(g.bundle), array_agg(g.event_id), array_agg(g.stream_id), array_agg(g.tenant_scope),
         array_agg(g.event_type), array_agg(g.origin_service_id), array_agg(g.local_sequence),
         array_agg(g.origin_sequence), array_agg(g.adopted), array_agg(g.left_local_lane), array_agg(g.refused),
         array_agg(g.digested)
  INTO v_w_bundles, v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_origins, v_w_local_seqs, v_w_origin_seqs,
       v_w_adopted, v_w_left_local, v_w_refused, v_w_digested
  FROM (
  WITH bundles AS (
    SELECT b.pos,
           b.origin,
           b.origin IS NOT NULL AND b.origin <> __EMPTY_UUID__::uuid
             AND b.origin IS DISTINCT FROM v_local_service_id AS adoptable,
           NULLIF(NULLIF(lower(btrim(b.name)), ''), c_unknown_service) AS origin_name
    FROM unnest(p_bundle_origins, p_bundle_names) WITH ORDINALITY AS b(origin, name, pos)
  ), events AS (
    SELECT DISTINCT ON (u.pos, u.eid) u.pos::int AS pos, u.eid, NULLIF(u.seq, 0) AS seq
    FROM unnest(p_event_bundles, p_event_ids, p_commit_sequences) AS u(pos, eid, seq)
    ORDER BY u.pos, u.eid, u.seq DESC NULLS LAST
  ), candidates AS (
    SELECT
      e.pos,
      es.event_id,
      es.stream_id,
      COALESCE(es.scope ->> 't', '') AS tenant_scope,
      es.event_type,
      b.origin,
      es.commit_sequence AS local_sequence,
      es.origin_commit_sequence AS stored_sequence,
      e.seq,
      b.adoptable AND es.origin_service_id IS NULL AS fills_origin,
      COALESCE(b.adoptable AND es.origin_service_id = b.origin AND es.origin_commit_sequence IS NULL
        AND e.seq IS NOT NULL, false) AS fills_sequence,
      COALESCE(fh.service IS NOT NULL AND fh.service <> c_unknown_service AND fh.service <> b.origin_name, false)
        AS refused,
      __DIGEST_ELIGIBLE__(es) AS digested
    FROM events e
    JOIN bundles b ON b.pos = e.pos
    JOIN __SCHEMA__.wh_event_store es ON es.event_id = e.eid
    -- The first hop's service, read only for an event the bundle would give an origin, and only when there is a
    -- name to compare it with: the one-time catch-up, never an event that already carries its origin.
    LEFT JOIN LATERAL (
      SELECT NULLIF(lower(btrim(COALESCE(
               h.hop -> 'si' ->> 'sn',
               h.hop -> 'ServiceInstance' ->> 'ServiceName'))), '') AS service
      FROM (SELECT (eb.metadata -> c_field_hops) -> 0 AS hop
            FROM __SCHEMA__.wh_event_body eb
            WHERE eb.event_id = es.event_id
              AND b.adoptable AND b.origin_name IS NOT NULL AND es.origin_service_id IS NULL) h
    ) fh ON true
  ), judged AS (
    SELECT c.*, (c.fills_origin AND NOT c.refused) OR c.fills_sequence AS can_adopt
    FROM candidates c
  ), winners AS (
    SELECT DISTINCT ON (j.event_id) j.event_id, j.pos, j.origin, j.can_adopt, j.fills_origin AND NOT j.refused AS fills_origin
    FROM judged j
    ORDER BY j.event_id, j.can_adopt DESC, j.pos
  )
  SELECT
    j.pos,
    j.event_id,
    j.stream_id,
    j.tenant_scope,
    j.event_type,
    CASE WHEN w.can_adopt THEN w.origin END,
    j.local_sequence,
    CASE WHEN w.can_adopt THEN COALESCE(j.stored_sequence, j.seq, (
      SELECT max(o.seq) FROM judged o WHERE o.event_id = j.event_id AND o.origin = w.origin)) END,
    w.can_adopt AND w.pos = j.pos,
    w.can_adopt AND w.pos = j.pos AND w.fills_origin,
    -- Refused as a single-bundle call would have refused it: before the bundle that filled it, or when none did.
    j.refused AND (NOT w.can_adopt OR j.pos < w.pos),
    j.digested
  FROM judged j
  JOIN winners w ON w.event_id = j.event_id
  ) AS g(bundle, event_id, stream_id, tenant_scope, event_type, origin_service_id, local_sequence, origin_sequence,
        adopted, left_local_lane, refused, digested);

  -- The fills, as arrays the planner can see, so each stored event is found by its key.
  SELECT array_agg(r.event_id ORDER BY r.event_id), array_agg(r.origin_service_id ORDER BY r.event_id),
         array_agg(r.origin_sequence ORDER BY r.event_id)
  INTO v_ids, v_origins, v_sequences
  FROM unnest(v_w_bundles, v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_origins, v_w_local_seqs,
                v_w_origin_seqs, v_w_adopted, v_w_left_local, v_w_refused, v_w_digested)
         AS r(bundle, event_id, stream_id, tenant_scope, event_type, origin_service_id, local_sequence,
              origin_sequence, adopted, left_local_lane, refused, digested)
  WHERE r.adopted;

  IF v_ids IS NOT NULL THEN
    UPDATE __SCHEMA__.wh_event_store es
    SET origin_service_id = m.origin, origin_commit_sequence = m.seq
    FROM unnest(v_ids, v_origins, v_sequences) AS m(eid, origin, seq)
    WHERE es.event_id = m.eid;
  END IF;

  -- Digests: XOR is self-inverse, so an adopted event's hash leaves the local bucket and joins its origin's.
  SELECT array_agg(DISTINCT r.stream_id) INTO v_leaving_streams
  FROM unnest(v_w_bundles, v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_origins, v_w_local_seqs,
                v_w_origin_seqs, v_w_adopted, v_w_left_local, v_w_refused, v_w_digested)
         AS r(bundle, event_id, stream_id, tenant_scope, event_type, origin_service_id, local_sequence,
              origin_sequence, adopted, left_local_lane, refused, digested)
  WHERE r.adopted AND r.digested AND r.left_local_lane;

  IF v_leaving_streams IS NOT NULL THEN
    -- 336 (#2106): every lane leaves through the one fold helper.
    PERFORM __SCHEMA__._wh_fold_stream_digests(
      array_agg(__EMPTY_UUID__::uuid), array_agg(r.tenant_scope), array_agg(r.event_type), array_agg(r.stream_id),
      array_agg(r.event_id), 'unfold', now())
    FROM unnest(v_w_bundles, v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_origins, v_w_local_seqs,
                  v_w_origin_seqs, v_w_adopted, v_w_left_local, v_w_refused, v_w_digested)
           AS r(bundle, event_id, stream_id, tenant_scope, event_type, origin_service_id, local_sequence,
                origin_sequence, adopted, left_local_lane, refused, digested)
    WHERE r.adopted AND r.digested AND r.left_local_lane;
    DELETE FROM __SCHEMA__.wh_stream_digests d
    USING (SELECT DISTINCT r.tenant_scope, r.event_type, r.stream_id
           FROM unnest(v_w_bundles, v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_origins, v_w_local_seqs,
                         v_w_origin_seqs, v_w_adopted, v_w_left_local, v_w_refused, v_w_digested)
                  AS r(bundle, event_id, stream_id, tenant_scope, event_type, origin_service_id, local_sequence,
                       origin_sequence, adopted, left_local_lane, refused, digested)
           WHERE r.adopted AND r.digested AND r.left_local_lane) l
    WHERE d.origin_service_id = __EMPTY_UUID__::uuid
      AND d.scope_tenant = l.tenant_scope AND d.event_type = l.event_type AND d.stream_id = l.stream_id
      AND d.stream_id = ANY(v_leaving_streams)
      AND d.event_count <= 0;

    PERFORM __SCHEMA__._wh_fold_stream_digests(
      array_agg(r.origin_service_id), array_agg(r.tenant_scope), array_agg(r.event_type), array_agg(r.stream_id),
      array_agg(r.event_id), 'fold', now())
    FROM unnest(v_w_bundles, v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_origins, v_w_local_seqs,
                  v_w_origin_seqs, v_w_adopted, v_w_left_local, v_w_refused, v_w_digested)
           AS r(bundle, event_id, stream_id, tenant_scope, event_type, origin_service_id, local_sequence,
                origin_sequence, adopted, left_local_lane, refused, digested)
    WHERE r.adopted AND r.digested AND r.left_local_lane;
  END IF;

  -- The sealed epochs that hold a moved event, each once: the origins' lanes by origin sequence.
  FOR r_epoch IN
    SELECT DISTINCT r.origin_service_id AS lane, r.origin_sequence / f.epoch_width AS epoch, f.epoch_width AS width
    FROM unnest(v_w_bundles, v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_origins, v_w_local_seqs,
                  v_w_origin_seqs, v_w_adopted, v_w_left_local, v_w_refused, v_w_digested)
           AS r(bundle, event_id, stream_id, tenant_scope, event_type, origin_service_id, local_sequence,
                origin_sequence, adopted, left_local_lane, refused, digested)
    JOIN __SCHEMA__.wh_digest_epoch_frontiers f ON f.origin_service_id = r.origin_service_id
    WHERE r.adopted AND r.digested AND r.origin_sequence IS NOT NULL
      AND r.origin_sequence >= 0 AND r.origin_sequence / f.epoch_width <= f.closed_through_epoch
    ORDER BY 1, 2
  LOOP
    PERFORM __SCHEMA__._wh_fold_digest_epoch(r_epoch.lane, r_epoch.epoch, r_epoch.width, NOW());
  END LOOP;

  -- The local lane by local sequence; a refold there moves the origin generation, once.
  SELECT f.epoch_width INTO v_local_width
  FROM __SCHEMA__.wh_digest_epoch_frontiers f
  WHERE f.origin_service_id = __EMPTY_UUID__::uuid;
  IF v_local_width IS NOT NULL THEN
    FOR r_epoch IN
      SELECT DISTINCT r.local_sequence / v_local_width AS epoch
      FROM unnest(v_w_bundles, v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_origins, v_w_local_seqs,
                    v_w_origin_seqs, v_w_adopted, v_w_left_local, v_w_refused, v_w_digested)
             AS r(bundle, event_id, stream_id, tenant_scope, event_type, origin_service_id, local_sequence,
                  origin_sequence, adopted, left_local_lane, refused, digested)
      JOIN __SCHEMA__.wh_digest_epoch_frontiers f ON f.origin_service_id = __EMPTY_UUID__::uuid
      WHERE r.adopted AND r.digested AND r.left_local_lane AND r.local_sequence IS NOT NULL
        AND r.local_sequence >= 0 AND r.local_sequence / v_local_width <= f.closed_through_epoch
      ORDER BY 1
    LOOP
      PERFORM __SCHEMA__._wh_fold_digest_epoch(__EMPTY_UUID__::uuid, r_epoch.epoch, v_local_width, NOW());
      v_local_refolded := v_local_refolded + 1;
    END LOOP;
    IF v_local_refolded > 0 THEN
      PERFORM __SCHEMA__._wh_bump_origin_generation();
    END IF;
  END IF;

  RETURN QUERY
  SELECT x.pos, x.sid, x.tenant, x.etype, SUM(x.present)::int, SUM(x.adopted)::int, SUM(x.filled)::int,
         SUM(x.truncated)::int, SUM(x.foreign_origin)::int, SUM(x.superseded)::int
  FROM (
    SELECT r.bundle AS pos, r.stream_id AS sid, r.tenant_scope AS tenant, r.event_type AS etype, 1 AS present,
           r.adopted::int AS adopted, r.left_local_lane::int AS filled, 0 AS truncated, r.refused::int AS foreign_origin,
           0 AS superseded
    FROM unnest(v_w_bundles, v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_origins, v_w_local_seqs,
                  v_w_origin_seqs, v_w_adopted, v_w_left_local, v_w_refused, v_w_digested)
           AS r(bundle, event_id, stream_id, tenant_scope, event_type, origin_service_id, local_sequence,
                origin_sequence, adopted, left_local_lane, refused, digested)
    UNION ALL
    -- 252: a child store_inbox_messages refused to restore, keyed as every digest fold keys it (the stream, the
    -- scope's tenant, the emit chain's normalized type), for each bundle that named it.
    SELECT u.pos::int, i.stream_id, COALESCE(i.scope ->> 't', ''), __SCHEMA__.normalize_event_type(i.message_type),
           0, 0, 0, 1, 0, 0
    FROM (SELECT DISTINCT p.pos, p.eid FROM unnest(p_event_bundles, p_event_ids) AS p(pos, eid)) u
    JOIN __SCHEMA__.wh_inbox i ON i.message_id = u.eid
    JOIN __SCHEMA__.wh_inbox_state ist ON ist.message_id = i.message_id
    WHERE ist.error = __REPAIR_REFUSED_TRUNCATED__
      AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = i.message_id)
    UNION ALL
    -- 449 (#2350): a child a newer held copy of its key supersedes is satisfied: present, and counted apart.
    SELECT u.pos::int, i.stream_id, COALESCE(i.scope ->> 't', ''), __SCHEMA__.normalize_event_type(i.message_type),
           1, 0, 0, 0, 0, 1
    FROM (SELECT DISTINCT p.pos, p.eid FROM unnest(p_event_bundles, p_event_ids) AS p(pos, eid)) u
    JOIN __SCHEMA__.wh_inbox i ON i.message_id = u.eid
    JOIN __SCHEMA__.wh_inbox_state ist ON ist.message_id = i.message_id
    WHERE ist.error = __REPAIR_SATISFIED_SUPERSEDED__
  ) x
  GROUP BY x.pos, x.sid, x.tenant, x.etype
  ORDER BY x.pos, x.sid, x.tenant, x.etype;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_reconcile_redelivered_bundles_counting_satisfied(UUID[], TEXT[], INTEGER[], UUID[], BIGINT[]) IS
'449 (#2350): wh_reconcile_redelivered_bundles with superseded_satisfied_count, as wh_reconcile_redelivered_bundle_counting_satisfied. 420 (#2181): its working rows are gathered into arrays, not a temporary table. 335 (#2099): reconciles many redelivery bundles at once, with the per-bundle semantics of wh_reconcile_redelivered_bundle_counting_satisfied (253, 321): an empty origin takes its bundle''s origin unless the stored first hop names another service, an empty origin sequence of an event that carries the bundle''s origin takes the bundle''s sequence, nothing is overwritten, the local service adopts nothing, digests move lanes. Each event adopts at most once per batch, for the first bundle that can fill it. Every bundle''s streams are locked once in order, the fills are one update, and each sealed epoch holding a moved event is refolded once per batch rather than every epoch of each bundle''s range. Returns per (bundle, stream, tenant, type) the events present, adopted, whose origin was filled (origins_filled_count; the rest of adopted took only the sequence), refused as truncated and refused by the first-hop check.';

