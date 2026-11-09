-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 375_EphemeralHistoryReclassifiesInBatches.sql
-- Date: 2026-11-09
-- Description: The history of a type made [Ephemeral] is reclassified in bounded batches by the maintenance cycle,
--              through the per-type history walker (#2206).
--
--              With Whizbang:Ephemeral:ReconcileHistoricalOnStartup on, the startup type-definition reconciler called
--              reclassify_events_ephemeral(names), which stamped EventFlags.Ephemeral on every Sourced event of the type
--              in one statement: one transaction over every row of the type (hundreds of thousands on a long-lived
--              store), one burst of WAL, a lock on every touched row for its whole length, and a statement that could
--              run into the command timeout, after which nothing was reclassified and the next startup tried again.
--
--              The reclassification is now an operation on the walker 374 introduced ('ephemeral'): the type's own
--              stamped events in commit order, a window per batch, each batch its own short transaction, one instance per
--              type, resumable from the last committed cursor, and complete only once nothing of the type is unstamped.
--              The maintenance step that drives it (EphemeralHistoryReclassifyStep) runs in the maintenance cycle, so
--              startup no longer waits on it; the setting keeps its meaning as the opt-in.
--
--                _wh_reclassify_ephemeral_events  the one body of the reclassification, over a given set of events:
--                                                 the mixed-stream guard (an event whose stream holds a Sourced event of
--                                                 another type is left Sourced and reported), the stamp, the digest
--                                                 subtraction through _wh_fold_stream_digests (336), the emptied buckets
--                                                 of the touched streams, the sealed epochs the events were in refolded
--                                                 and the origin generation bumped once (093). Copied from 336's
--                                                 reclassify_events_ephemeral, narrowed to the given events.
--                wh_ephemeral_history_apply       one window of the walk: the window's Sourced events of the type, under
--                                                 the emit chains' per-stream locks (stream id order), handed to the body,
--                                                 then the walk's cursor moved by _wh_type_history_advance (374).
--                reclassify_events_ephemeral      kept for an explicit, one-statement reclassification; now the body
--                                                 over every Sourced event of the names, so the two cannot drift. Its
--                                                 streams_blocked counts the streams of the events it left Sourced.
--
--              Number 375: 374 is #2205's.
--
-- Dependencies: 374 (wh_type_history_walks, _wh_type_history_window_is_mine, _wh_type_history_advance), 336
--               (reclassify_events_ephemeral, _wh_fold_stream_digests), 093 (_wh_refold_epochs_covering,
--               _wh_bump_origin_generation), 214 (EventFlags.AtMostOnce), 006 (normalize_event_type)
-- Objects: _wh_reclassify_ephemeral_events, wh_ephemeral_history_apply, reclassify_events_ephemeral
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('_wh_reclassify_ephemeral_events');
SELECT __SCHEMA__.drop_all_overloads('wh_ephemeral_history_apply');

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- <docs>fundamentals/events/ephemeral-events#making-an-existing-type-ephemeral</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralHistoryReclassifySqlTests.cs:Reclassify_RunsInBoundedBatches_AndResumesFromItsCursor_AfterACrashAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralHistoryReclassifySqlTests.cs:Reclassify_AMixedStream_IsReportedAndLeftUntouched_AndTheDigestsEqualARecountAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralReclassifySqlTests.cs:Reclassify_MixedStream_SkipsAndReportsBlocked_ButReclassifiesCleanStreamAsync</tests>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestWritersTests.cs:ReclassifyEphemeral_SubtractsEveryLaneOfTheReclassifiedEventsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_reclassify_ephemeral_events(p_names TEXT[], p_event_ids UUID[])
RETURNS TABLE(
  events_reclassified BIGINT,
  streams_reclassified BIGINT,
  events_blocked BIGINT,
  streams_blocked BIGINT
) AS $$
DECLARE
  c_flag_ephemeral CONSTANT INTEGER := 8;
  c_zero CONSTANT UUID := __EMPTY_UUID__;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  v_events BIGINT := 0;
  v_streams BIGINT := 0;
  v_blocked_events BIGINT := 0;
  v_blocked_streams BIGINT := 0;
  v_touched UUID[];
  v_lanes UUID[];
  v_mins BIGINT[];
  v_maxs BIGINT[];
  v_i INTEGER;
BEGIN
  IF cardinality(COALESCE(p_names, ARRAY[]::TEXT[])) = 0 OR cardinality(COALESCE(p_event_ids, ARRAY[]::UUID[])) = 0 THEN
    RETURN QUERY SELECT 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT;
    RETURN;
  END IF;

  -- The given Sourced events whose stream would become MIXED: it holds a Sourced (flags & 8 = 0) event whose type is
  -- NOT one of the names. Reclassifying them would violate the homogeneous-stream invariant, so they are left Sourced
  -- by the stamp below and reported here.
  SELECT COUNT(*), COUNT(DISTINCT es.stream_id) INTO v_blocked_events, v_blocked_streams
  FROM __SCHEMA__.wh_event_store es
  WHERE es.event_id = ANY(p_event_ids)
    AND es.event_type = ANY(p_names)
    AND (es.flags & c_flag_ephemeral) = 0
    AND EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_store b
      WHERE b.stream_id = es.stream_id
        AND NOT (b.event_type = ANY(p_names))
        AND (b.flags & c_flag_ephemeral) = 0
    );

  -- Migration 093 (#80-F): capture, per lane, the sequence range of the fold-relevant rows the stamp below will remove
  -- from the fold, same predicates as the UPDATE, evaluated BEFORE it.
  SELECT array_agg(t.lane), array_agg(t.mn), array_agg(t.mx)
  INTO v_lanes, v_mins, v_maxs
  FROM (
    SELECT COALESCE(es.origin_service_id, c_zero) AS lane,
           MIN(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mn,
           MAX(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mx
    FROM __SCHEMA__.wh_event_store es
    WHERE es.event_id = ANY(p_event_ids)
      AND es.event_type = ANY(p_names)
      AND (es.flags & c_flag_ephemeral) = 0
      AND (es.flags & c_flag_at_most_once) = 0  -- 214 (#1484)
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store b
        WHERE b.stream_id = es.stream_id
          AND NOT (b.event_type = ANY(p_names))
          AND (b.flags & c_flag_ephemeral) = 0
      )
    GROUP BY 1
  ) t
  WHERE t.mn IS NOT NULL;

  -- Full split (078): bodies live in wh_event_body from birth for both classes, so reclassification is a flags stamp.
  WITH reclassified AS (
    UPDATE __SCHEMA__.wh_event_store es
    SET flags = es.flags | c_flag_ephemeral
    WHERE es.event_id = ANY(p_event_ids)
      AND es.event_type = ANY(p_names)
      AND (es.flags & c_flag_ephemeral) = 0
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store b
        WHERE b.stream_id = es.stream_id
          AND NOT (b.event_type = ANY(p_names))
          AND (b.flags & c_flag_ephemeral) = 0
      )
    RETURNING es.event_id, es.stream_id, es.event_type, es.scope, es.origin_service_id, es.flags
  ),
  -- Migration 087 (A1c): the reclassified rows leave the audited digest set (flags & 8 excluded), so they are XORed
  -- back out of wh_stream_digests. 336 (#2106): every lane leaves through the one fold helper; read by the final
  -- SELECT, so it always runs.
  digest_subtract AS (
    SELECT __SCHEMA__._wh_fold_stream_digests(
      array_agg(COALESCE(r.origin_service_id, __EMPTY_UUID__::uuid)), array_agg(COALESCE(r.scope ->> 't', '')),
      array_agg(r.event_type), array_agg(r.stream_id), array_agg(r.event_id), 'unfold', NOW()) AS unfolded
    FROM reclassified r
    WHERE (r.flags & c_flag_at_most_once) = 0  -- 214 (#1484): never folded, so never subtracted
  )
  SELECT COUNT(*), COUNT(DISTINCT stream_id), array_agg(DISTINCT stream_id)
  INTO v_events, v_streams, v_touched
  FROM reclassified CROSS JOIN digest_subtract;

  -- Migration 087: drop the buckets the subtraction emptied, in the streams it touched (a separate statement, so it sees
  -- the subtraction).
  IF v_touched IS NOT NULL THEN
    DELETE FROM __SCHEMA__.wh_stream_digests d
    WHERE d.stream_id = ANY(v_touched) AND d.event_type = ANY(p_names) AND d.event_count <= 0;
  END IF;

  -- Migration 093 (#80-F): refold the sealed epochs the reclassification touched, then bump the generation once.
  IF v_lanes IS NOT NULL AND v_events > 0 THEN
    FOR v_i IN 1 .. array_length(v_lanes, 1) LOOP
      PERFORM __SCHEMA__._wh_refold_epochs_covering(v_lanes[v_i], v_mins[v_i], v_maxs[v_i]);
    END LOOP;
    PERFORM __SCHEMA__._wh_bump_origin_generation();
  END IF;

  RETURN QUERY SELECT v_events, v_streams, v_blocked_events, v_blocked_streams;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_reclassify_ephemeral_events(TEXT[], UUID[]) IS
  'Issue #2206: stamps EventFlags.Ephemeral on the given Sourced events of the named types whose streams hold no '
  'Sourced event of another type, subtracts them from the stream digests through _wh_fold_stream_digests, drops the '
  'buckets that emptied, refolds the sealed epochs they were in and bumps the origin generation (093). Reports the '
  'given events left Sourced because their stream is mixed. The one body behind reclassify_events_ephemeral and the '
  'batched history walk.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- <docs>fundamentals/events/ephemeral-events#making-an-existing-type-ephemeral</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralHistoryReclassifySqlTests.cs:Reclassify_RunsInBoundedBatches_AndResumesFromItsCursor_AfterACrashAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralHistoryReclassifySqlTests.cs:Reclassify_AMixedStream_IsReportedAndLeftUntouched_AndTheDigestsEqualARecountAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralHistoryReclassifySqlTests.cs:TheWalk_ReadsTheTypeThroughItsIndex_NeverTheStoreAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralHistoryReclassifySqlTests.cs:Apply_AWindowThatIsNotItsOwn_ChangesNothingAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_ephemeral_history_apply(
  p_event_type TEXT,
  p_names TEXT[],
  p_after BIGINT,
  p_through BIGINT,
  p_scan_size INTEGER,
  p_rows_scanned BIGINT
)
RETURNS TABLE(
  status TEXT,
  events_reclassified BIGINT,
  events_blocked BIGINT,
  streams_blocked BIGINT,
  events_total BIGINT,
  events_matched BIGINT
)
LANGUAGE plpgsql
SET plan_cache_mode = force_custom_plan
AS $$
DECLARE
  c_operation CONSTANT TEXT := 'ephemeral';
  c_flag_ephemeral CONSTANT INTEGER := 8;
  v_type TEXT := __SCHEMA__.normalize_event_type(p_event_type);
  v_names TEXT[];
  v_ids UUID[];
  v_streams UUID[];
  v_done RECORD;
  v_end RECORD;
BEGIN
  IF NOT __SCHEMA__._wh_type_history_window_is_mine(c_operation, v_type, p_after) THEN
    RETURN QUERY SELECT 'skipped (the window is no longer this call''s)'::TEXT, 0::BIGINT, 0::BIGINT, 0::BIGINT,
      COALESCE((SELECT h.events_total FROM __SCHEMA__.wh_type_history_walks h WHERE h.operation = c_operation AND h.event_type = v_type), 0),
      COALESCE((SELECT h.events_matched FROM __SCHEMA__.wh_type_history_walks h WHERE h.operation = c_operation AND h.event_type = v_type), 0);
    RETURN;
  END IF;

  -- Every name the type is stored under (current and former) is one type to the mixed-stream guard, and the walked
  -- name is always among them.
  SELECT array_agg(DISTINCT __SCHEMA__.normalize_event_type(t)) INTO v_names
  FROM unnest(array_append(COALESCE(p_names, ARRAY[]::TEXT[]), p_event_type)) AS t;

  -- The window's Sourced events of the type, read in the type's index, and their streams in lock order.
  SELECT array_agg(es.event_id), array_agg(DISTINCT es.stream_id)
  INTO v_ids, v_streams
  FROM __SCHEMA__.wh_event_store es
  WHERE es.event_type = v_type
    AND es.commit_sequence IS NOT NULL
    AND es.commit_sequence > COALESCE(p_after, -1)
    AND es.commit_sequence <= p_through
    AND (es.flags & c_flag_ephemeral) = 0;

  -- The emit chains' per-stream locks, in stream id order (array_agg DISTINCT sorts): no event is appended to these
  -- streams between the mixed-stream guard and the stamp, so a stream judged homogeneous stays so until it is stamped.
  IF v_streams IS NOT NULL THEN
    PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || u.sid::text))
    FROM unnest(v_streams) AS u(sid)
    ORDER BY u.sid;
  END IF;

  SELECT * INTO v_done FROM __SCHEMA__._wh_reclassify_ephemeral_events(v_names, v_ids);

  SELECT * INTO v_end
  FROM __SCHEMA__._wh_type_history_advance(
    c_operation, v_type, p_after, p_through, p_scan_size, p_rows_scanned, 0, v_done.events_reclassified, 0,
    v_done.events_blocked);

  RETURN QUERY SELECT v_end.status, v_done.events_reclassified, v_done.events_blocked, v_done.streams_blocked,
    v_end.events_total, v_end.events_matched;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_ephemeral_history_apply(TEXT, TEXT[], BIGINT, BIGINT, INTEGER, BIGINT) IS
  'Issue #2206: one batch of the ephemeral history walk. Reclassifies the Sourced events of the type in the window '
  '(p_after, p_through] of its commit sequences, under the emit chains'' per-stream locks, through '
  '_wh_reclassify_ephemeral_events (p_names are every name the type is stored under, for the mixed-stream guard), and '
  'moves the walk past the window.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- <docs>fundamentals/events/ephemeral-events#making-an-existing-type-ephemeral</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralReclassifySqlTests.cs:Reclassify_HistoricalSourcedEvent_BecomesEphemeral_AndReapableAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralReclassifySqlTests.cs:Reclassify_MixedStream_SkipsAndReportsBlocked_ButReclassifiesCleanStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralReclassifySqlTests.cs:Reclassify_Idempotent_SecondRunReclassifiesZeroAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestTableSqlTests.cs:Reclassify_SubtractsReclassifiedRows_EmptiedBucketDroppedAsync</tests>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestWritersTests.cs:ReclassifyEphemeral_SubtractsEveryLaneOfTheReclassifiedEventsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.reclassify_events_ephemeral(p_event_types TEXT[])
RETURNS TABLE(
  events_reclassified BIGINT,
  streams_reclassified BIGINT,
  streams_blocked BIGINT
) AS $$
DECLARE
  c_flag_ephemeral CONSTANT INTEGER := 8;
  v_names TEXT[];
  v_ids UUID[];
  v_done RECORD;
BEGIN
  -- Normalize every name the logical type was ever stored under (current + former).
  SELECT array_agg(__SCHEMA__.normalize_event_type(t)) INTO v_names
  FROM unnest(p_event_types) AS t;

  SELECT array_agg(es.event_id) INTO v_ids
  FROM __SCHEMA__.wh_event_store es
  WHERE es.event_type = ANY(v_names)
    AND (es.flags & c_flag_ephemeral) = 0;

  SELECT * INTO v_done FROM __SCHEMA__._wh_reclassify_ephemeral_events(v_names, v_ids);

  RETURN QUERY SELECT v_done.events_reclassified, v_done.streams_reclassified, v_done.streams_blocked;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.reclassify_events_ephemeral(TEXT[]) IS
  'Stamps EventFlags.Ephemeral on the historical rows of the named types in homogeneous streams, subtracts them from the '
  'stream digests, refolds the sealed epochs they were in and bumps the origin generation (093). 214: at-most-once '
  'events (flags & 32), never folded, are never subtracted. 375 (#2206): one statement over every event of the names, '
  'through _wh_reclassify_ephemeral_events, the body the batched maintenance walk (wh_ephemeral_history_apply) runs '
  'per window; startup no longer calls it.';
