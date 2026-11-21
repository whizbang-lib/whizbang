-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 539_RetiredEventTypesArePurgedByName.sql
-- Date: 2026-11-20
-- Description: The stored events of a deleted event type are purged by the type's name, from long-lived mixed streams,
--              a bounded batch of streams at a time (#2329).
--
--              A stream-level policy reaches a type only through the type that opens its streams, so a deleted type
--              on long-lived streams kept its events for good. A retired type is now a rule (RuleSemantics 3): no digest
--              counts it on either side of a service pair, the receive boundary discards it, and this purge removes its
--              stored events wherever they are:
--
--                the names     wh_retired_event_type_names resolves each retired name (without its assembly) to the
--                              names it is stored under, walking the distinct stored types through
--                              idx_event_store_type_tenant (one index probe per distinct type, never a scan of events);
--                              a name nothing is stored under comes back with a NULL stored name.
--                the batch     up to p_batch_streams streams that hold a retired event, none of which a gate holds: the
--                              stamper has not reached it, a destruction hold is active on it, or a snapshot rests on it.
--                              A stream leaves whole or waits whole, so a superseding key never loses some copies and
--                              keeps others, and its slot is unfolded exactly once.
--                the locks     the emit chains' per-stream locks, in stream id order, as every removal takes them.
--                cursors       a cursor resting on a retired event steps back to the stream's last event before it
--                              (_wh_hand_on_cursors, shared with the superseding reaper); a cursor flagged for a rewind,
--                              one with other work pending before it, or one with no earlier event keeps its stream
--                              waiting.
--                the digest    the leaving events are unfolded from their stream buckets through the one fold, the
--                              retired types' buckets left with no live part are dropped with what cuts carried into
--                              them, the retired types' removed-epoch rows go, the sealed epochs the events and those
--                              rows sat in are refolded, and the origin generation is bumped once, floored at the lowest
--                              position touched. A superseding key whose copies of another type stay keeps its slot,
--                              handed on to them (_wh_hand_on_slots).
--                the rows      each leaving event's body, perspective work, applied record, lifecycle completion,
--                              lapsed hold and superseded-queue row go with it; deduplication rows stay, so a resend of
--                              a retired event is recognized and not stored again.
--
--              debug_mode keeps everything, as every other reaper does.
--
-- Reversal: reversible (the two functions are dropped; events already purged stay purged)
-- Dependencies: 538 (_wh_hand_on_cursors, _wh_keep_stream_start), 461 (_wh_fold_stream_digests, _wh_hand_on_slots),
--               460 (wh_digest_epoch_removed, carried columns, _wh_epoch_width), 408 (_wh_refold_epochs_covering,
--               _wh_bump_origin_generation), 195 (idx_event_store_type_tenant), 079 (wh_event_destruction_hold)
-- Objects: wh_retired_event_type_names, wh_retire_event_types
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- ---------------------------------------------------------------------------------------------
-- wh_retired_event_type_names: the names each retired type is stored under.
-- ---------------------------------------------------------------------------------------------

-- <docs>fundamentals/events/retired-event-types#declaring-a-retired-type</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RetiredEventTypePurgeSqlTests.cs:Names_ResolveToEveryNameTheTypeIsStoredUnder_AndAnUnknownNameToNoneAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_retired_event_type_names(p_names TEXT[])
RETURNS TABLE(retired_name TEXT, stored_name TEXT) AS $$
  -- The distinct stored types, one index probe each (a loose scan of idx_event_store_type_tenant).
  WITH RECURSIVE stored(event_type) AS (
    (SELECT es.event_type FROM __SCHEMA__.wh_event_store es ORDER BY es.event_type LIMIT 1)
    UNION ALL
    SELECT (SELECT es.event_type FROM __SCHEMA__.wh_event_store es
            WHERE es.event_type > s.event_type ORDER BY es.event_type LIMIT 1)
    FROM stored s
    WHERE s.event_type IS NOT NULL
  )
  SELECT n.name, s.event_type
  FROM (SELECT DISTINCT btrim(u.name) AS name FROM unnest(p_names) AS u(name) WHERE btrim(u.name) <> '') n
  LEFT JOIN stored s ON s.event_type IS NOT NULL AND btrim(split_part(s.event_type, ',', 1)) = n.name
  ORDER BY 1, 2;
$$ LANGUAGE sql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_retired_event_type_names(TEXT[]) IS
  '539 (#2329): each retired type name (without its assembly) with every name its events are stored under, or one row '
  'with a NULL stored name when nothing is stored under it. Walks the distinct stored types through '
  'idx_event_store_type_tenant, one probe per type.';

-- ---------------------------------------------------------------------------------------------
-- wh_retire_event_types: one bounded batch of the purge.
-- ---------------------------------------------------------------------------------------------

-- <docs>fundamentals/events/retired-event-types#what-the-purge-does</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RetiredEventTypePurgeSqlTests.cs:Retire_RemovesEveryEventOfTheType_FromAMixedStream_AndTheOtherTypesStayAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RetiredEventTypePurgeSqlTests.cs:Retire_UnfoldsTheTypeFromTheDigests_SoTheyEqualARecount_AndRefoldsAndBumpsOnceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RetiredEventTypePurgeSqlTests.cs:Retire_DropsWhatACutCarriedForTheType_AndItsRemovedEpochRowsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RetiredEventTypePurgeSqlTests.cs:Retire_TakesTheEventsCompanionRows_AndKeepsTheirDeduplicationAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RetiredEventTypePurgeSqlTests.cs:Retire_AStreamAGateHolds_WaitsWhole_AndTheOthersAreRetiredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RetiredEventTypePurgeSqlTests.cs:Retire_TakesAtMostTheBatchOfStreams_AndTheNextBatchTakesTheRestAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RetiredEventTypePurgeSqlTests.cs:Retire_ASupersedingType_UnfoldsEachKeysSlotOnce_AndASlotSharedWithAStayingTypeIsHandedOnAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RetiredEventTypePurgeSqlTests.cs:Retire_DebugMode_NoNames_OrNothingStored_ChangeNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RetiredEventTypePurgeSqlTests.cs:Retire_ACursorOnARetiredEvent_StepsBackToTheEventBeforeIt_AndTheStreamIsPurgedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RetiredEventTypePurgeSqlTests.cs:Retire_ACursorWithNoEarlierEventToStepBackTo_KeepsTheStreamWaitingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RetiredEventTypePurgeSqlTests.cs:Retire_ACursorWithOtherWorkPendingBeforeIt_KeepsTheStreamWaitingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RetiredEventTypePurgeSqlTests.cs:Retire_TheStreamsFirstEvent_LeavesItsBusinessTimeOnTheLedgerAsync</tests>
-- <docs>resilience/stream-integrity#retired-event-types</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CleanupIntegrityMatrixTests.cs:CleanupAndIntegrity_Converge_AndAGenuineLossBesideTheCleanupIsStillRepairedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_retire_event_types(p_names TEXT[], p_batch_streams INTEGER DEFAULT 500)
RETURNS TABLE(events_retired BIGINT, streams_retired INTEGER, streams_waiting INTEGER, status TEXT) AS $$
DECLARE
  v_debug BOOLEAN;
  v_stored TEXT[];
  v_streams UUID[];
  v_leaving UUID[];
  v_waiting UUID[];
  v_victims UUID[];
  v_done UUID[];
  v_held INTEGER := 0;
  v_lanes UUID[];
  v_mins BIGINT[];
  v_maxs BIGINT[];
  v_i INTEGER;
  v_deleted BIGINT := 0;
BEGIN
  IF COALESCE(cardinality(p_names), 0) = 0 THEN
    RETURN QUERY SELECT 0::BIGINT, 0, 0, 'ok'::TEXT;
    RETURN;
  END IF;

  -- debug_mode keeps every event for forensics, as every other reaper does.
  SELECT COALESCE(
    (SELECT s.setting_value::BOOLEAN FROM __SCHEMA__.wh_settings s WHERE s.setting_key = 'debug_mode'),
    FALSE
  ) INTO v_debug;
  IF v_debug THEN
    RETURN QUERY SELECT 0::BIGINT, 0, 0, 'skipped (debug_mode=true)'::TEXT;
    RETURN;
  END IF;

  SELECT array_agg(DISTINCT n.stored_name) INTO v_stored
  FROM __SCHEMA__.wh_retired_event_type_names(p_names) n
  WHERE n.stored_name IS NOT NULL;
  IF v_stored IS NULL THEN
    RETURN QUERY SELECT 0::BIGINT, 0, 0, 'ok'::TEXT;
    RETURN;
  END IF;

  -- The streams a gate holds wait whole: the stamper has not reached one of their retired events, a destruction hold
  -- is active on one, or a snapshot rests on one (a rewind restores a snapshot and moves the cursor onto its event).
  SELECT COUNT(DISTINCT g.stream_id)::INTEGER INTO v_held
  FROM __SCHEMA__.wh_event_store g
  WHERE g.event_type = ANY(v_stored)
    AND (g.commit_sequence IS NULL
      OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_destruction_hold h WHERE h.event_id = g.event_id AND h.hold_until > NOW())
      OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_perspective_snapshots sn
                 WHERE sn.stream_id = g.stream_id AND sn.snapshot_event_id = g.event_id));

  -- The batch: streams holding a retired event, none of which a gate holds, in stream id order.
  SELECT array_agg(c.stream_id ORDER BY c.stream_id) INTO v_streams
  FROM (
    SELECT DISTINCT es.stream_id
    FROM __SCHEMA__.wh_event_store es
    WHERE es.event_type = ANY(v_stored)
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store g
        WHERE g.stream_id = es.stream_id
          AND g.event_type = ANY(v_stored)
          AND (g.commit_sequence IS NULL
            OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_destruction_hold h WHERE h.event_id = g.event_id AND h.hold_until > NOW())
            OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_perspective_snapshots sn
                       WHERE sn.stream_id = g.stream_id AND sn.snapshot_event_id = g.event_id)))
    ORDER BY es.stream_id
    LIMIT GREATEST(p_batch_streams, 1)
  ) c;
  IF v_streams IS NULL THEN
    RETURN QUERY SELECT 0::BIGINT, 0, v_held,
      CASE WHEN v_held > 0 THEN 'waiting (' || v_held || ' streams held)' ELSE 'ok' END;
    RETURN;
  END IF;

  -- The emit chains' per-stream locks, in stream id order, so nothing is appended to, truncated from or archived out of
  -- these streams while their events are judged and removed.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || u.sid::text))
  FROM unnest(v_streams) AS u(sid)
  ORDER BY u.sid;

  SELECT array_agg(es.event_id) INTO v_leaving
  FROM __SCHEMA__.wh_event_store es
  WHERE es.stream_id = ANY(v_streams) AND es.event_type = ANY(v_stored);

  -- A cursor resting on a retired event steps back to the stream's last event before it, which its perspective applied
  -- too: the one hand-on every removal shares (538).
  PERFORM __SCHEMA__._wh_hand_on_cursors(v_leaving, v_streams, FALSE);

  -- Read again under the locks: a stream whose retired events a gate now holds, or a cursor still rests on, waits whole.
  SELECT COALESCE(array_agg(DISTINCT es.stream_id), ARRAY[]::UUID[]) INTO v_waiting
  FROM __SCHEMA__.wh_event_store es
  WHERE es.event_id = ANY(v_leaving)
    AND (es.commit_sequence IS NULL
      OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_destruction_hold h WHERE h.event_id = es.event_id AND h.hold_until > NOW())
      OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_perspective_snapshots sn
                 WHERE sn.stream_id = es.stream_id AND sn.snapshot_event_id = es.event_id)
      OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_perspective_cursors cu
                 WHERE cu.stream_id = es.stream_id AND cu.last_event_id = es.event_id));

  SELECT array_agg(es.event_id), array_agg(DISTINCT es.stream_id) INTO v_victims, v_done
  FROM __SCHEMA__.wh_event_store es
  WHERE es.event_id = ANY(v_leaving) AND es.stream_id <> ALL(v_waiting);

  IF v_victims IS NOT NULL THEN
    -- The sequence range of the counted victims and of the retired types' removed-epoch rows, per lane, for the epoch
    -- refold after the delete (as 263). An epoch row's range is its epoch's.
    SELECT array_agg(t.lane), array_agg(t.mn), array_agg(t.mx)
    INTO v_lanes, v_mins, v_maxs
    FROM (
      SELECT r.lane, MIN(r.mn) AS mn, MAX(r.mx) AS mx
      FROM (
        SELECT COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid) AS lane,
               CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END AS mn,
               CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END AS mx
        FROM __SCHEMA__.wh_event_store es
        WHERE es.event_id = ANY(v_victims)
          AND __DIGEST_ELIGIBLE__(es)
        UNION ALL
        SELECT er.origin_service_id, er.epoch_id * __SCHEMA__._wh_epoch_width(er.origin_service_id),
               (er.epoch_id + 1) * __SCHEMA__._wh_epoch_width(er.origin_service_id) - 1
        FROM __SCHEMA__.wh_digest_epoch_removed er
        WHERE er.event_type = ANY(v_stored)
          AND er.epoch_id >= 0
      ) r
      WHERE r.mn IS NOT NULL
      GROUP BY r.lane
    ) t;

    -- Unfold the victims from their stream buckets through the one fold. A superseding copy counts as its key's slot
    -- through the key's anchor; the whole stream leaves, so each slot is unfolded once. A key whose copies of another
    -- type of its group stay keeps its slot: it is handed on to them below, not unfolded.
    PERFORM __SCHEMA__._wh_fold_stream_digests(
      array_agg(COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid)), array_agg(COALESCE(es.scope ->> 't', '')),
      array_agg(es.event_type), array_agg(es.stream_id), array_agg(es.event_id), 'unfold', NOW())
    FROM __SCHEMA__.wh_event_store es
    WHERE es.event_id = ANY(v_victims)
      AND __DIGEST_ELIGIBLE__(es)
      AND NOT (es.supersede_key IS NOT NULL AND EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store k
        WHERE k.stream_id = es.stream_id
          AND COALESCE(k.supersede_group, k.event_type) = COALESCE(es.supersede_group, es.event_type)
          AND k.supersede_key = es.supersede_key
          AND k.event_id <> ALL(v_victims)));
    PERFORM __SCHEMA__._wh_hand_on_slots(v_victims);

    -- A retired type is counted by no digest: its buckets in these streams with no live part left go, with whatever cuts
    -- carried into them (460), and so do its removed-epoch rows. Any other bucket the unfold emptied goes too.
    DELETE FROM __SCHEMA__.wh_stream_digests d
    WHERE d.stream_id = ANY(v_done)
      AND (d.event_count <= 0
        OR (d.event_type = ANY(v_stored) AND d.event_count - COALESCE(d.carried_count, 0) <= 0));
    DELETE FROM __SCHEMA__.wh_digest_epoch_removed er WHERE er.event_type = ANY(v_stored);

    PERFORM __SCHEMA__._wh_keep_stream_start(v_victims);

    -- Each victim's companion rows go with it. Deduplication rows stay: a resend of a retired event is recognized as
    -- one, and the receive boundary turns away the type anyway.
    DELETE FROM __SCHEMA__.wh_perspective_events pe WHERE pe.event_id = ANY(v_victims);
    DELETE FROM __SCHEMA__.wh_perspective_applied pa WHERE pa.event_id = ANY(v_victims);
    DELETE FROM __SCHEMA__.wh_lifecycle_completions lc WHERE lc.event_id = ANY(v_victims);
    DELETE FROM __SCHEMA__.wh_event_destruction_hold h WHERE h.event_id = ANY(v_victims);
    DELETE FROM __SCHEMA__.wh_superseded_events q WHERE q.event_id = ANY(v_victims);

    -- Bodies first, then pointers.
    DELETE FROM __SCHEMA__.wh_event_body eb WHERE eb.event_id = ANY(v_victims);
    DELETE FROM __SCHEMA__.wh_event_store es WHERE es.event_id = ANY(v_victims);
    GET DIAGNOSTICS v_deleted = ROW_COUNT;

    -- A retirement is a rule change: refold the sealed epochs the type sat in, then bump the generation once, floored at
    -- the lowest position touched, so consumers lower their seals that far and re-verify instead of alarming (093, 408).
    IF v_lanes IS NOT NULL THEN
      FOR v_i IN 1 .. array_length(v_lanes, 1) LOOP
        PERFORM __SCHEMA__._wh_refold_epochs_covering(v_lanes[v_i], v_mins[v_i], v_maxs[v_i]);
      END LOOP;
      PERFORM __SCHEMA__._wh_bump_origin_generation();
    END IF;
  END IF;

  v_held := v_held + COALESCE(cardinality(v_waiting), 0);
  RETURN QUERY SELECT v_deleted, COALESCE(cardinality(v_done), 0), v_held,
    CASE WHEN v_held > 0 THEN 'ok (' || v_held || ' streams waiting)' ELSE 'ok' END;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_retire_event_types(TEXT[], INTEGER) IS
  '539 (#2329): one bounded batch of the purge of retired event types (names without the assembly). Takes up to '
  'p_batch_streams streams holding a retired event that no gate holds (unstamped, an active destruction hold, a snapshot '
  'on it), under the per-stream locks; hands cursors on (_wh_hand_on_cursors); a stream still held waits whole. Unfolds '
  'the victims from the stream digests, drops the retired types'' emptied buckets with their carried part and their '
  'removed-epoch rows, refolds the covering sealed epochs and bumps the origin generation once. Removes each victim with '
  'its body, perspective work, applied record, lifecycle completion, hold and superseded-queue row; deduplication stays. '
  'Skipped under debug_mode. Returns the events removed, the streams retired, the streams waiting and a status.';
