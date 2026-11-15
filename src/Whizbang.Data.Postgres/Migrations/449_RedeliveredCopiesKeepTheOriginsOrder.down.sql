-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 449_RedeliveredCopiesKeepTheOriginsOrder.sql (#2350): the queue, the history walk and the reaper go back
-- to their last words in 352, 380 and 448, ordering the copies of a key by stored version, and _wh_supersedes goes.

-- ── _wh_queue_superseded_events: last word 352, copied whole ──
-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- Queues the older events of each given event's stream, group and key. Called by the emit chains and wh_append_event
-- after they store an event with a key; a separate statement there, so it sees the events stored just before it.
-- <docs>fundamentals/events/superseding-events#superseding-groups</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Emit_Outbox_StampsTheKeyOnlyOnASupersedingEvent_AndQueuesTheOlderOneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingGroupSqlTests.cs:Emit_ANewerEventOfAnotherTypeInTheGroup_QueuesTheOlder_InEitherOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingGroupSqlTests.cs:Emit_UngroupedTypes_StillSupersedeOnlyTheirOwnTypeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingGroupSqlTests.cs:Emit_AGroupedAndAnUngroupedType_NeverSupersedeEachOtherAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingGroupSqlTests.cs:TheGroupProbe_IsServedByTheGroupIndexAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_queue_superseded_events(p_event_ids UUID[], p_now TIMESTAMPTZ)
RETURNS VOID AS $$
BEGIN
  INSERT INTO __SCHEMA__.wh_superseded_events (event_id, stream_id, queued_at)
  SELECT DISTINCT older.event_id, older.stream_id, p_now
  FROM __SCHEMA__.wh_event_store newer
  JOIN __SCHEMA__.wh_event_store older
    ON older.stream_id = newer.stream_id
   AND COALESCE(older.supersede_group, older.event_type) = COALESCE(newer.supersede_group, newer.event_type)
   AND older.supersede_key = newer.supersede_key
   AND older.version < newer.version
  WHERE newer.event_id = ANY(p_event_ids)
    AND newer.supersede_key IS NOT NULL
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
  'the reaper''s gates remain the only path to deletion. 380 (#2205): the window is a range of the type''s commit '
  'sequences, and the walk ends through _wh_type_history_advance.';

-- ── wh_reap_superseded_events: last word 358, copied whole ──
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
        AND n.version > es.version)
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


SELECT __SCHEMA__.drop_all_overloads('_wh_supersedes');
