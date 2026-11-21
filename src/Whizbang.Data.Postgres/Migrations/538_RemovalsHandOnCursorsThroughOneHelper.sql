-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 538_RemovalsHandOnCursorsThroughOneHelper.sql
-- Date: 2026-11-20
-- Description: The cursor hand-on and the stream-start record the superseding reaper does before it deletes are moved
--              into two helpers, so every removal of events a perspective applied shares them (#2329).
--
--                _wh_hand_on_cursors     a cursor resting on an event that is leaving moves to the stream's last event
--                                        at or before its bound that is not leaving, once its perspective has no work
--                                        pending at or before that bound; a cursor flagged for a rewind is left. The
--                                        superseding reaper (p_newest_copy) bounds it by the later of the copy and its
--                                        key's newest copy, and moves it only when the perspective consumes the newest
--                                        copy's type, exactly as 532 did. A removal of a whole type (the retirement of
--                                        a deleted event type, 539) bounds it by the leaving event itself: the cursor
--                                        steps back to the last event before it, which the perspective applied too, and
--                                        the leaving events' own work rows do not hold it, since they leave with them.
--                _wh_keep_stream_start   a leaving event that is its stream's first event (version 0 or 1) leaves its
--                                        business time on the stream's ledger row, once, as 358 did inside the reaper.
--
--              wh_reap_superseded_events is copied whole from its last word (532) and calls the two helpers in place of
--              the statements they hold. Nothing it does changes.
--
-- Reversal: reversible (532's wh_reap_superseded_events is restored and the helpers are dropped)
-- Dependencies: 532 (last word of wh_reap_superseded_events), 454 (_wh_supersedes), 358 (_wh_event_business_time)
-- Objects: _wh_hand_on_cursors, _wh_keep_stream_start, wh_reap_superseded_events
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- ---------------------------------------------------------------------------------------------
-- _wh_hand_on_cursors: the cursor hand-on of 532, shared.
-- ---------------------------------------------------------------------------------------------

-- <docs>fundamentals/events/superseding-events#the-reaper</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_ACursorOnTheOlderCopy_WhoseNewerCopyItsPerspectiveApplied_MovesOnToIt_AndTheOlderIsReapedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_ACursorOnTheOlderCopy_WhoseNewerCopyIsStillPending_StaysAndKeepsItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_ACursorFlaggedForARewind_IsLeftToTheRewindAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_ACursorOnAnOlderCopyStoredAboveTheNewest_MovesBackToTheNewest_AndTheOlderIsReapedAsync</tests>
-- <docs>fundamentals/events/retired-event-types#cursors-and-snapshots</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RetiredEventTypePurgeSqlTests.cs:Retire_ACursorOnARetiredEvent_StepsBackToTheEventBeforeIt_AndTheStreamIsPurgedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RetiredEventTypePurgeSqlTests.cs:Retire_ACursorWithNoEarlierEventToStepBackTo_KeepsTheStreamWaitingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RetiredEventTypePurgeSqlTests.cs:Retire_ACursorWithOtherWorkPendingBeforeIt_KeepsTheStreamWaitingAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_hand_on_cursors(p_leaving UUID[], p_streams UUID[], p_newest_copy BOOLEAN)
RETURNS INTEGER AS $$
DECLARE
  v_rows INTEGER := 0;
BEGIN
  IF COALESCE(cardinality(p_leaving), 0) = 0 THEN
    RETURN 0;
  END IF;
  UPDATE __SCHEMA__.wh_perspective_cursors c
  SET last_event_id = h.target_event_id
  FROM (
    SELECT cc.stream_id, cc.perspective_name, t.event_id AS target_event_id
    FROM __SCHEMA__.wh_perspective_cursors cc
    JOIN __SCHEMA__.wh_event_store o ON o.event_id = cc.last_event_id
    -- The key's newest copy, for the superseding reaper only.
    LEFT JOIN LATERAL (
      SELECT n.event_id, n.event_type, n.version
      FROM __SCHEMA__.wh_event_store n
      WHERE n.stream_id = o.stream_id
        AND COALESCE(n.supersede_group, n.event_type) = COALESCE(o.supersede_group, o.event_type)
        AND n.supersede_key = o.supersede_key
        AND __SCHEMA__._wh_supersedes(n.origin_service_id, n.origin_commit_sequence, n.version,
                                  o.origin_service_id, o.origin_commit_sequence, o.version)
        AND NOT EXISTS (
          SELECT 1 FROM __SCHEMA__.wh_event_store m
          WHERE m.stream_id = n.stream_id
            AND COALESCE(m.supersede_group, m.event_type) = COALESCE(n.supersede_group, n.event_type)
            AND m.supersede_key = n.supersede_key
            AND __SCHEMA__._wh_supersedes(m.origin_service_id, m.origin_commit_sequence, m.version,
                                      n.origin_service_id, n.origin_commit_sequence, n.version))
      LIMIT 1
    ) n ON p_newest_copy
    -- How far the perspective has applied: the later of the two copies, or the leaving event itself.
    CROSS JOIN LATERAL (
      SELECT CASE WHEN p_newest_copy THEN GREATEST(o.version, n.version) ELSE o.version END AS bound
    ) b
    -- The stream's last event at or before the bound that stays (the newest copy may be queued, and stays).
    CROSS JOIN LATERAL (
      SELECT e.event_id
      FROM __SCHEMA__.wh_event_store e
      WHERE e.stream_id = o.stream_id
        AND e.version <= b.bound
        AND ((p_newest_copy AND e.event_id = n.event_id) OR e.event_id <> ALL(p_leaving))
      ORDER BY e.version DESC
      LIMIT 1
    ) t
    WHERE cc.stream_id = ANY(p_streams)
      AND cc.last_event_id = ANY(p_leaving)
      AND (cc.status & 32) = 0
      AND (NOT p_newest_copy OR (
        o.supersede_key IS NOT NULL
        AND n.event_id IS NOT NULL
        AND EXISTS (
          SELECT 1 FROM __SCHEMA__.wh_message_associations ma
          WHERE ma.normalized_message_type = n.event_type
            AND ma.association_type = __CATEGORY_PERSPECTIVE__
            AND ma.target_name = cc.perspective_name)))
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
        JOIN __SCHEMA__.wh_event_store pv ON pv.event_id = pe.event_id
        WHERE pe.stream_id = cc.stream_id
          AND pe.perspective_name = cc.perspective_name
          AND pe.processed_at IS NULL
          AND pv.version <= b.bound
          -- A removal of a whole type takes the leaving events' own work with them.
          AND (p_newest_copy OR pe.event_id <> ALL(p_leaving)))
  ) h
  WHERE c.stream_id = h.stream_id
    AND c.perspective_name = h.perspective_name;
  GET DIAGNOSTICS v_rows = ROW_COUNT;
  RETURN v_rows;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_hand_on_cursors(UUID[], UUID[], BOOLEAN) IS
  '538 (#2329): before events leave, a cursor resting on one moves to the stream''s last event at or before its bound '
  'that stays, once its perspective has no work pending at or before that bound; a cursor flagged for a rewind is left. '
  'p_newest_copy (the superseding reaper, 532): the bound is the later of the copy and its key''s newest copy, and the '
  'perspective must consume the newest copy''s type. Otherwise (a whole type removed): the bound is the leaving event, '
  'and the leaving events'' own work rows do not count as pending. Returns the cursors moved.';

-- ---------------------------------------------------------------------------------------------
-- _wh_keep_stream_start: the stream-start record of 358, shared.
-- ---------------------------------------------------------------------------------------------

-- <docs>fundamentals/events/superseding-events#what-a-rebuild-keeps</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamStartedAtSqlTests.cs:Reap_TheStreamsFirstEvent_RecordsItsBusinessTimeOnTheLedgerAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamStartedAtSqlTests.cs:Reap_ALaterEvent_RecordsNothingAsync</tests>
-- <docs>fundamentals/events/retired-event-types#what-the-purge-does</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RetiredEventTypePurgeSqlTests.cs:Retire_TheStreamsFirstEvent_LeavesItsBusinessTimeOnTheLedgerAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_keep_stream_start(p_leaving UUID[])
RETURNS INTEGER AS $$
DECLARE
  v_rows INTEGER := 0;
BEGIN
  IF COALESCE(cardinality(p_leaving), 0) = 0 THEN
    RETURN 0;
  END IF;
  -- Written once: the lowest such event of each stream, and never over a value already recorded.
  UPDATE __SCHEMA__.wh_stream_lifecycle l
  SET first_event_at = f.business_time
  FROM (
    SELECT DISTINCT ON (es.stream_id) es.stream_id,
           COALESCE(__SCHEMA__._wh_event_business_time(eb.metadata), es.created_at) AS business_time
    FROM __SCHEMA__.wh_event_store es
    LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = es.event_id
    WHERE es.event_id = ANY(p_leaving) AND es.version <= 1
    ORDER BY es.stream_id, es.version
  ) f
  WHERE l.stream_id = f.stream_id AND l.first_event_at IS NULL;
  GET DIAGNOSTICS v_rows = ROW_COUNT;
  RETURN v_rows;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_keep_stream_start(UUID[]) IS
  '538 (#2329): before events leave, one that is its stream''s first event (version 0 or 1) leaves its business time on '
  'the stream''s ledger row (first_event_at, 358), so a rebuild that writes the stream''s row afresh still dates it at '
  'the stream''s start. Written once per stream, never over a recorded value. Returns the ledger rows written.';

-- ---------------------------------------------------------------------------------------------
-- wh_reap_superseded_events: copied from its last word (532), calling the two helpers.
-- ---------------------------------------------------------------------------------------------

-- <docs>fundamentals/events/superseding-events#what-a-rebuild-keeps</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamStartedAtSqlTests.cs:Reap_TheStreamsFirstEvent_RecordsItsBusinessTimeOnTheLedgerAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamStartedAtSqlTests.cs:Reap_ALaterEvent_RecordsNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/RebuildCreatedAtIntegrationTests.cs:RebuildBlueGreen_AfterTheFirstEventIsReaped_DatesTheRowAtTheStreamsStartAsync</tests>
-- <docs>fundamentals/events/superseding-events#the-reaper</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_SupersededAndConsumed_IsDeletedWithItsBodyAndItsDigestContribution_TheNewestIsKeptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_UnprocessedPerspectiveWork_KeepsIt_UntilItIsProcessedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_ACursorOnIt_KeepsIt_UntilTheCursorMovesOnAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_ACursorOnTheOlderCopy_WhoseNewerCopyItsPerspectiveApplied_MovesOnToIt_AndTheOlderIsReapedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_ACursorOnTheOlderCopy_WhoseNewerCopyIsStillPending_StaysAndKeepsItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_ACursorFlaggedForARewind_IsLeftToTheRewindAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_ACursorOnAnOlderCopyStoredAboveTheNewest_MovesBackToTheNewest_AndTheOlderIsReapedAsync</tests>
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
  v_deleted BIGINT := 0;
  c_task CONSTANT TEXT := 'reap_superseded_events';
  c_flag_superseding CONSTANT INTEGER := 128;  -- EventFlags.Superseding (1 << 7)
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

  -- 532 (#2433): a cursor resting on a queued copy whose key's newest copy its perspective has applied moves on, as the
  -- key's slot is handed on, so a lagging pointer no longer holds the copy forever. 538 (#2329): through the one hand-on
  -- every removal of a perspective's applied events shares.
  PERFORM __SCHEMA__._wh_hand_on_cursors(v_candidates, v_streams, TRUE);

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
        -- 454 (#2350): newer in the origin's order where both copies carry it, so a restored older copy stored above
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
    -- 461 (#2364): the digest counts a key's slot, not its copies, and a newer copy of every victim's key stays, so
    -- nothing is subtracted, refolded or bumped: physical deletion is timing, and the digest does not follow it. The
    -- copies left behind inherit a victim anchor's place first, so the slot is still counted where its first copy sat.
    PERFORM __SCHEMA__._wh_hand_on_slots(v_victims);

    -- 358 (#2152): a victim that is its stream's first event leaves its business time on the stream's ledger row. 538
    -- (#2329): through the helper every removal of a stream's first event shares.
    PERFORM __SCHEMA__._wh_keep_stream_start(v_victims);

    -- Lapsed holds of the victims go with them; the gates proved no active one remains. Their processed perspective
    -- work rows are left to Task 3's purge: this task runs after the work tables were visited in their canonical order,
    -- and touching one again here would break that order.
    DELETE FROM __SCHEMA__.wh_event_destruction_hold h WHERE h.event_id = ANY(v_victims);

    -- Bodies first, then pointers (rows that reference an event cascade from it on the EF Core schema).
    DELETE FROM __SCHEMA__.wh_event_body eb WHERE eb.event_id = ANY(v_victims);
    DELETE FROM __SCHEMA__.wh_event_store es WHERE es.event_id = ANY(v_victims);
    GET DIAGNOSTICS v_deleted = ROW_COUNT;

    DELETE FROM __SCHEMA__.wh_superseded_events q WHERE q.event_id = ANY(v_victims);
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
  'covering sealed epochs and bumping the origin generation once when it removed folded history. 352: a group is the stored supersede_group, or the event type for an ungrouped row (#2124). 358: records the business time of a victim that is its stream''s first event on the stream''s ledger row (first_event_at, #2152). 461 (#2364): deletes physically and touches no digest, seal or generation: the digest counts the key''s slot, and the copies left inherit an anchor''s place (_wh_hand_on_slots). 463: copied whole from 461; its comment no longer names wh_receptor_processing. '
  '532 (#2433): a cursor resting on a queued copy whose key''s newest copy its perspective applied moves on first, as '
  'the slot is handed on, to the stream''s last event at or before the later of the two copies. 538 (#2329): the cursor '
  'hand-on and the stream-start record are the shared _wh_hand_on_cursors and _wh_keep_stream_start.';

