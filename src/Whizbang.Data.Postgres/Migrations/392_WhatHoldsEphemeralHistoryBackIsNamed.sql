-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 392_WhatHoldsEphemeralHistoryBackIsNamed.sql
-- Date: 2026-11-10
-- Description: The ephemeral history walk names the durable event types that keep each ephemeral type's history from
--              being reclassified, and can release them for that type's streams only (#2250).
--
--              381's walk leaves an event of a type made [Ephemeral] Sourced when its stream holds a Sourced event of
--              another type, and only counts it. An operator who expected a large reclaim saw a large blocked count and
--              nothing to act on. Each batch now records, for its blocked events, the durable types present in their
--              streams, aggregated per (ephemeral type, blocking type): blocked events, blocked streams (each stream
--              once per walk: in the batch holding its first blocked event), the approximate stored size of the
--              blocked events and one sample stream. It reads only the batch's blocked streams, under the per-stream
--              locks the batch already holds, so the cost stays inside the walk's per-cycle budget; there is no extra
--              pass over the store.
--
--                wh_ephemeral_blockers            one row per (ephemeral type, blocking type) for the type's CURRENT walk:
--                                                 the walk's started_at is kept on the row, and a restarted walk (a newer
--                                                 definition, or a changed variant) deletes the rows of the walk before.
--                _wh_record_ephemeral_blockers    one batch's blocked events, aggregated into the table.
--                _wh_reclassify_ephemeral_events  381's body, plus the release: the types released for this ephemeral
--                                                 type do not block it, and their Sourced events in the streams it
--                                                 reclassifies are reclassified with it, through the same digest fold,
--                                                 emptied buckets and epoch refold. Only streams holding the ephemeral
--                                                 type are touched: never a released type's other streams.
--                wh_ephemeral_history_apply       381's batch, taking the release list and the report switch as
--                                                 parameters (never interpolated), with both defaulting to off.
--                wh_ephemeral_blockers_report     the stored rows of the current walks, each joined to the newest route of
--                                                 its blocking type (wh_routes, 286): service, topic, caller class and
--                                                 member of the hop that wrote it.
--                wh_ephemeral_mixed_inputs        for a read model opted into mixing ephemeral and Sourced events, its
--                                                 durable inputs' stored Sourced events and streams, and the streams that
--                                                 also hold one of its ephemeral inputs; read once per process.
--
--              With the report off and nothing released the batch does exactly what 381's did.
--
--              Number 392: 391 is the highest on develop.
--
-- Reversal: lossy (the report's table is dropped with its rows and 381's body and batch are restored; events a release
--           reclassified stay ephemeral, like every reclassified event)
-- Dependencies: 381 (_wh_reclassify_ephemeral_events, wh_ephemeral_history_apply, reclassify_events_ephemeral), 380
--               (wh_type_history_walks, _wh_type_history_window_is_mine, _wh_type_history_advance), 336
--               (_wh_fold_stream_digests), 286 (wh_routes), 093 (_wh_refold_epochs_covering, _wh_bump_origin_generation),
--               214 (EventFlags.AtMostOnce), 077 (wh_event_body), 006 (normalize_event_type)
-- Objects: wh_ephemeral_blockers, _wh_record_ephemeral_blockers, _wh_reclassify_ephemeral_events,
--          wh_ephemeral_history_apply, wh_ephemeral_blockers_report, wh_ephemeral_mixed_inputs
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests): the body and the batch take
-- new parameters, and their results gain the released count.
SELECT __SCHEMA__.drop_all_overloads('_wh_reclassify_ephemeral_events');
SELECT __SCHEMA__.drop_all_overloads('wh_ephemeral_history_apply');
SELECT __SCHEMA__.drop_all_overloads('_wh_record_ephemeral_blockers');
SELECT __SCHEMA__.drop_all_overloads('wh_ephemeral_blockers_report');
SELECT __SCHEMA__.drop_all_overloads('wh_ephemeral_mixed_inputs');

-- ===========================================================================================
-- The report's rows
-- ===========================================================================================

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_ephemeral_blockers (
  ephemeral_type TEXT NOT NULL,
  blocking_type TEXT NOT NULL,
  walk_started_at TIMESTAMPTZ NOT NULL,
  events_blocked BIGINT NOT NULL DEFAULT 0,
  streams_blocked BIGINT NOT NULL DEFAULT 0,
  bytes_blocked BIGINT NOT NULL DEFAULT 0,
  sample_stream_id UUID NULL,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  CONSTRAINT pk_ephemeral_blockers PRIMARY KEY (ephemeral_type, blocking_type)
);

COMMENT ON TABLE __SCHEMA__.wh_ephemeral_blockers IS
  'The ephemeral blocker report: what keeps an ephemeral type''s stored history from being reclassified, per (ephemeral type, blocking '
  'type) for the type''s current ephemeral history walk (walk_started_at): the events left Sourced because their stream '
  'holds a Sourced event of the blocking type, those streams (each once per walk), the approximate stored size of the '
  'blocked events and one sample stream. Written by the walk''s batches when Whizbang:Ephemeral:BlockerReport:Enabled is '
  'on; the rows of an earlier walk of the type are deleted when it restarts.';

-- ===========================================================================================
-- Recording one batch's blockers
-- ===========================================================================================

-- <docs>fundamentals/events/ephemeral-events#what-holds-ephemeral-history-back</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:Report_AMixedStream_NamesTheBlockingType_ItsCounts_ASampleStream_AndItsRouteAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:Streams_AreCountedOnce_WhenTheirEventsSpanSeveralBatchesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:Release_AStreamStillHeldByAnUnreleasedType_IsLeftWhole_AndReportedForThatTypeOnlyAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_record_ephemeral_blockers(
  p_ephemeral_type TEXT,
  p_names TEXT[],
  p_kept TEXT[],
  p_event_ids UUID[],
  p_after BIGINT,
  p_walk_started_at TIMESTAMPTZ
)
RETURNS VOID
LANGUAGE plpgsql
SET plan_cache_mode = force_custom_plan
AS $$
DECLARE
  c_flag_ephemeral CONSTANT INTEGER := 8;
BEGIN
  IF cardinality(COALESCE(p_event_ids, ARRAY[]::UUID[])) = 0 THEN
    RETURN;
  END IF;

  WITH blocked AS (
    -- The batch's Sourced events of the type whose stream holds a Sourced event of a type that blocks it: not one of
    -- the type's names, and not released for it.
    SELECT es.event_id, es.stream_id
    FROM __SCHEMA__.wh_event_store es
    WHERE es.event_id = ANY(p_event_ids)
      AND es.event_type = ANY(p_names)
      AND (es.flags & c_flag_ephemeral) = 0
      AND EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store b
        WHERE b.stream_id = es.stream_id
          AND NOT (b.event_type = ANY(p_kept))
          AND (b.flags & c_flag_ephemeral) = 0
      )
  ),
  per_stream AS (
    -- A stream is counted once per walk: by the batch holding its first blocked event, the batch after which no earlier
    -- Sourced event of the walked type is in it (an earlier one would have been blocked by an earlier batch).
    SELECT bl.stream_id,
           COUNT(*) AS events,
           SUM(COALESCE(pg_column_size(body.event_data), 0) + COALESCE(pg_column_size(body.metadata), 0)) AS bytes,
           NOT EXISTS (
             SELECT 1 FROM __SCHEMA__.wh_event_store x
             WHERE x.stream_id = bl.stream_id
               AND x.event_type = p_ephemeral_type
               AND (x.flags & c_flag_ephemeral) = 0
               AND x.commit_sequence <= p_after
           ) AS first_seen
    FROM blocked bl
    LEFT JOIN __SCHEMA__.wh_event_body body ON body.event_id = bl.event_id
    GROUP BY bl.stream_id
  ),
  pairs AS (
    SELECT DISTINCT ps.stream_id, b.event_type AS blocking_type
    FROM per_stream ps
    JOIN __SCHEMA__.wh_event_store b ON b.stream_id = ps.stream_id
    WHERE NOT (b.event_type = ANY(p_kept))
      AND (b.flags & c_flag_ephemeral) = 0
  )
  INSERT INTO __SCHEMA__.wh_ephemeral_blockers AS t
    (ephemeral_type, blocking_type, walk_started_at, events_blocked, streams_blocked, bytes_blocked, sample_stream_id,
     updated_at)
  SELECT p_ephemeral_type, p.blocking_type, p_walk_started_at, SUM(ps.events), COUNT(*) FILTER (WHERE ps.first_seen),
         SUM(ps.bytes), (array_agg(ps.stream_id ORDER BY ps.stream_id))[1], NOW()
  FROM pairs p
  JOIN per_stream ps ON ps.stream_id = p.stream_id
  GROUP BY p.blocking_type
  ON CONFLICT (ephemeral_type, blocking_type) DO UPDATE SET
    events_blocked = t.events_blocked + EXCLUDED.events_blocked,
    streams_blocked = t.streams_blocked + EXCLUDED.streams_blocked,
    bytes_blocked = t.bytes_blocked + EXCLUDED.bytes_blocked,
    sample_stream_id = COALESCE(t.sample_stream_id, EXCLUDED.sample_stream_id),
    updated_at = NOW();
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_record_ephemeral_blockers(TEXT, TEXT[], TEXT[], UUID[], BIGINT, TIMESTAMPTZ) IS
  'The ephemeral blocker report: adds one batch of the ephemeral history walk to wh_ephemeral_blockers: for the given Sourced events of '
  'the type whose streams hold a Sourced event of a type not in p_kept (the type''s names and the types released for '
  'it), the blocking types present, with the blocked events, the streams seen first in this batch, their approximate '
  'stored size and one sample stream.';

-- ===========================================================================================
-- The reclassification body, with the release
-- ===========================================================================================

-- <docs>fundamentals/events/ephemeral-events#making-an-existing-type-ephemeral</docs>
-- <docs>fundamentals/events/ephemeral-events#what-holds-ephemeral-history-back</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralHistoryReclassifySqlTests.cs:Reclassify_AMixedStream_IsReportedAndLeftUntouched_AndTheDigestsEqualARecountAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralReclassifySqlTests.cs:Reclassify_MixedStream_SkipsAndReportsBlocked_ButReclassifiesCleanStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:Release_StampsTheBlockingEvents_OnlyInStreamsHoldingTheEphemeralType_AndTheDigestsEqualARecountAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:NoRelease_NoDurableEventChangesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:Release_AStreamStillHeldByAnUnreleasedType_IsLeftWhole_AndReportedForThatTypeOnlyAsync</tests>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestWritersTests.cs:ReclassifyEphemeral_SubtractsEveryLaneOfTheReclassifiedEventsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_reclassify_ephemeral_events(
  p_names TEXT[],
  p_event_ids UUID[],
  p_released TEXT[] DEFAULT NULL
)
RETURNS TABLE(
  events_reclassified BIGINT,
  streams_reclassified BIGINT,
  events_blocked BIGINT,
  streams_blocked BIGINT,
  events_released BIGINT
) AS $$
DECLARE
  c_flag_ephemeral CONSTANT INTEGER := 8;
  c_zero CONSTANT UUID := __EMPTY_UUID__;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  v_released TEXT[];
  v_kept TEXT[];
  v_clean_ids UUID[];
  v_clean_streams UUID[];
  v_released_ids UUID[];
  v_ids UUID[];
  v_events BIGINT := 0;
  v_streams BIGINT := 0;
  v_released_events BIGINT := 0;
  v_blocked_events BIGINT := 0;
  v_blocked_streams BIGINT := 0;
  v_touched UUID[];
  v_lanes UUID[];
  v_mins BIGINT[];
  v_maxs BIGINT[];
  v_i INTEGER;
BEGIN
  IF cardinality(COALESCE(p_names, ARRAY[]::TEXT[])) = 0 OR cardinality(COALESCE(p_event_ids, ARRAY[]::UUID[])) = 0 THEN
    RETURN QUERY SELECT 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT;
    RETURN;
  END IF;

  -- The types released for these names (#2250): they do not block them, and in the streams the names are reclassified
  -- in they are reclassified too. A name of the type itself is never a released type.
  SELECT COALESCE(array_agg(DISTINCT t), ARRAY[]::TEXT[]) INTO v_released
  FROM unnest(COALESCE(p_released, ARRAY[]::TEXT[])) AS t
  WHERE t IS NOT NULL AND NOT (t = ANY(p_names));
  v_kept := p_names || v_released;

  -- The given Sourced events whose stream would become MIXED: it holds a Sourced (flags & 8 = 0) event of a type that
  -- is neither one of the names nor released for them. Reclassifying them would violate the homogeneous-stream
  -- invariant, so they are left Sourced and reported here.
  SELECT COUNT(*), COUNT(DISTINCT es.stream_id) INTO v_blocked_events, v_blocked_streams
  FROM __SCHEMA__.wh_event_store es
  WHERE es.event_id = ANY(p_event_ids)
    AND es.event_type = ANY(p_names)
    AND (es.flags & c_flag_ephemeral) = 0
    AND EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_store b
      WHERE b.stream_id = es.stream_id
        AND NOT (b.event_type = ANY(v_kept))
        AND (b.flags & c_flag_ephemeral) = 0
    );

  -- The given Sourced events whose stream is homogeneous once the released types are set aside, and their streams.
  SELECT array_agg(es.event_id), array_agg(DISTINCT es.stream_id) INTO v_clean_ids, v_clean_streams
  FROM __SCHEMA__.wh_event_store es
  WHERE es.event_id = ANY(p_event_ids)
    AND es.event_type = ANY(p_names)
    AND (es.flags & c_flag_ephemeral) = 0
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_store b
      WHERE b.stream_id = es.stream_id
        AND NOT (b.event_type = ANY(v_kept))
        AND (b.flags & c_flag_ephemeral) = 0
    );

  -- The released types' Sourced events in exactly those streams: never in a stream that does not hold the names.
  IF cardinality(v_released) > 0 AND v_clean_streams IS NOT NULL THEN
    SELECT array_agg(b.event_id) INTO v_released_ids
    FROM __SCHEMA__.wh_event_store b
    WHERE b.stream_id = ANY(v_clean_streams)
      AND b.event_type = ANY(v_released)
      AND (b.flags & c_flag_ephemeral) = 0;
  END IF;
  v_ids := COALESCE(v_clean_ids, ARRAY[]::UUID[]) || COALESCE(v_released_ids, ARRAY[]::UUID[]);

  -- Migration 093 (#80-F): capture, per lane, the sequence range of the fold-relevant rows the stamp below will remove
  -- from the fold, evaluated BEFORE it.
  SELECT array_agg(t.lane), array_agg(t.mn), array_agg(t.mx)
  INTO v_lanes, v_mins, v_maxs
  FROM (
    SELECT COALESCE(es.origin_service_id, c_zero) AS lane,
           MIN(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mn,
           MAX(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mx
    FROM __SCHEMA__.wh_event_store es
    WHERE es.event_id = ANY(v_ids)
      AND (es.flags & c_flag_ephemeral) = 0
      AND (es.flags & c_flag_at_most_once) = 0  -- 214 (#1484)
    GROUP BY 1
  ) t
  WHERE t.mn IS NOT NULL;

  -- Full split (078): bodies live in wh_event_body from birth for both classes, so reclassification is a flags stamp.
  WITH reclassified AS (
    UPDATE __SCHEMA__.wh_event_store es
    SET flags = es.flags | c_flag_ephemeral
    WHERE es.event_id = ANY(v_ids)
      AND (es.flags & c_flag_ephemeral) = 0
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
  SELECT COUNT(*) FILTER (WHERE r.event_type = ANY(p_names)),
         COUNT(DISTINCT r.stream_id) FILTER (WHERE r.event_type = ANY(p_names)),
         COUNT(*) FILTER (WHERE NOT (r.event_type = ANY(p_names))),
         array_agg(DISTINCT r.stream_id)
  INTO v_events, v_streams, v_released_events, v_touched
  FROM reclassified r CROSS JOIN digest_subtract;

  -- Migration 087: drop the buckets the subtraction emptied, in the streams it touched (a separate statement, so it sees
  -- the subtraction).
  IF v_touched IS NOT NULL THEN
    DELETE FROM __SCHEMA__.wh_stream_digests d
    WHERE d.stream_id = ANY(v_touched) AND d.event_type = ANY(v_kept) AND d.event_count <= 0;
  END IF;

  -- Migration 093 (#80-F): refold the sealed epochs the reclassification touched, then bump the generation once.
  IF v_lanes IS NOT NULL AND v_events + v_released_events > 0 THEN
    FOR v_i IN 1 .. array_length(v_lanes, 1) LOOP
      PERFORM __SCHEMA__._wh_refold_epochs_covering(v_lanes[v_i], v_mins[v_i], v_maxs[v_i]);
    END LOOP;
    PERFORM __SCHEMA__._wh_bump_origin_generation();
  END IF;

  RETURN QUERY SELECT v_events, v_streams, v_blocked_events, v_blocked_streams, v_released_events;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_reclassify_ephemeral_events(TEXT[], UUID[], TEXT[]) IS
  'Issue #2206: stamps EventFlags.Ephemeral on the given Sourced events of the named types whose streams hold no '
  'Sourced event of another type, subtracts them from the stream digests through _wh_fold_stream_digests, drops the '
  'buckets that emptied, refolds the sealed epochs they were in and bumps the origin generation (093). Reports the '
  'given events left Sourced because their stream is mixed. 392: the types in p_released do not block the '
  'names, and their Sourced events in the streams the names are reclassified in are reclassified with them '
  '(events_released); no other stream of theirs is touched. The one body behind reclassify_events_ephemeral and the '
  'batched history walk.';

-- ===========================================================================================
-- One batch of the walk
-- ===========================================================================================

-- <docs>fundamentals/events/ephemeral-events#making-an-existing-type-ephemeral</docs>
-- <docs>fundamentals/events/ephemeral-events#what-holds-ephemeral-history-back</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralHistoryReclassifySqlTests.cs:Reclassify_RunsInBoundedBatches_AndResumesFromItsCursor_AfterACrashAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralHistoryReclassifySqlTests.cs:Apply_AWindowThatIsNotItsOwn_ChangesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:Report_Off_RecordsNothing_LogsNothing_AndTheReclassifyIsUnchangedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:Counts_DescribeTheCurrentWalk_AndResetWhenItRestartsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:Release_ANameIsBoundAsData_NeverSqlTextAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_ephemeral_history_apply(
  p_event_type TEXT,
  p_names TEXT[],
  p_after BIGINT,
  p_through BIGINT,
  p_scan_size INTEGER,
  p_rows_scanned BIGINT,
  p_released TEXT[] DEFAULT NULL,
  p_report BOOLEAN DEFAULT FALSE
)
RETURNS TABLE(
  status TEXT,
  events_reclassified BIGINT,
  events_blocked BIGINT,
  streams_blocked BIGINT,
  events_total BIGINT,
  events_matched BIGINT,
  events_released BIGINT
)
LANGUAGE plpgsql
SET plan_cache_mode = force_custom_plan
AS $$
DECLARE
  c_operation CONSTANT TEXT := 'ephemeral';
  c_flag_ephemeral CONSTANT INTEGER := 8;
  v_type TEXT := __SCHEMA__.normalize_event_type(p_event_type);
  v_names TEXT[];
  v_released TEXT[];
  v_started TIMESTAMPTZ;
  v_ids UUID[];
  v_streams UUID[];
  v_done RECORD;
  v_end RECORD;
BEGIN
  IF NOT __SCHEMA__._wh_type_history_window_is_mine(c_operation, v_type, p_after) THEN
    RETURN QUERY SELECT 'skipped (the window is no longer this call''s)'::TEXT, 0::BIGINT, 0::BIGINT, 0::BIGINT,
      COALESCE((SELECT h.events_total FROM __SCHEMA__.wh_type_history_walks h WHERE h.operation = c_operation AND h.event_type = v_type), 0),
      COALESCE((SELECT h.events_matched FROM __SCHEMA__.wh_type_history_walks h WHERE h.operation = c_operation AND h.event_type = v_type), 0),
      0::BIGINT;
    RETURN;
  END IF;

  -- Every name the type is stored under (current and former) is one type to the mixed-stream guard, and the walked
  -- name is always among them.
  SELECT array_agg(DISTINCT __SCHEMA__.normalize_event_type(t)) INTO v_names
  FROM unnest(array_append(COALESCE(p_names, ARRAY[]::TEXT[]), p_event_type)) AS t;

  -- The types released for this type (#2250), normalized like every stored name.
  SELECT COALESCE(array_agg(DISTINCT __SCHEMA__.normalize_event_type(t)), ARRAY[]::TEXT[]) INTO v_released
  FROM unnest(COALESCE(p_released, ARRAY[]::TEXT[])) AS t
  WHERE t IS NOT NULL;

  -- The report describes the current walk only: rows an earlier walk of the type wrote go when it restarts.
  SELECT h.started_at INTO v_started
  FROM __SCHEMA__.wh_type_history_walks h WHERE h.operation = c_operation AND h.event_type = v_type;
  DELETE FROM __SCHEMA__.wh_ephemeral_blockers b
  WHERE b.ephemeral_type = v_type AND b.walk_started_at IS DISTINCT FROM v_started;

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

  IF p_report THEN
    PERFORM __SCHEMA__._wh_record_ephemeral_blockers(v_type, v_names, v_names || v_released, v_ids, p_after, v_started);
  END IF;

  SELECT * INTO v_done FROM __SCHEMA__._wh_reclassify_ephemeral_events(v_names, v_ids, v_released);

  SELECT * INTO v_end
  FROM __SCHEMA__._wh_type_history_advance(
    c_operation, v_type, p_after, p_through, p_scan_size, p_rows_scanned, 0, v_done.events_reclassified, 0,
    v_done.events_blocked);

  RETURN QUERY SELECT v_end.status, v_done.events_reclassified, v_done.events_blocked, v_done.streams_blocked,
    v_end.events_total, v_end.events_matched, v_done.events_released;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_ephemeral_history_apply(TEXT, TEXT[], BIGINT, BIGINT, INTEGER, BIGINT, TEXT[], BOOLEAN) IS
  'Issue #2206: one batch of the ephemeral history walk. Reclassifies the Sourced events of the type in the window '
  '(p_after, p_through] of its commit sequences, under the emit chains'' per-stream locks, through '
  '_wh_reclassify_ephemeral_events (p_names are every name the type is stored under, for the mixed-stream guard), and '
  'moves the walk past the window. 392: p_released are the types released for this type (reclassified with it '
  'in its own streams only); p_report records the batch''s blockers in wh_ephemeral_blockers. Both default to off, '
  'which is 381''s batch.';

-- ===========================================================================================
-- Reading the report
-- ===========================================================================================

-- <docs>fundamentals/events/ephemeral-events#what-holds-ephemeral-history-back</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:Report_AMixedStream_NamesTheBlockingType_ItsCounts_ASampleStream_AndItsRouteAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:Counts_DescribeTheCurrentWalk_AndResetWhenItRestartsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:StartupSummary_IsLoggedOnce_AndTheGaugeReadsTheStoredReportAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_ephemeral_blockers_report()
RETURNS TABLE(
  ephemeral_type TEXT,
  blocking_type TEXT,
  events_blocked BIGINT,
  streams_blocked BIGINT,
  bytes_blocked BIGINT,
  sample_stream_id UUID,
  walk_complete BOOLEAN,
  route_service TEXT,
  route_topic TEXT,
  route_caller_class TEXT,
  route_caller_member TEXT
)
LANGUAGE sql STABLE
AS $$
  SELECT b.ephemeral_type, b.blocking_type, b.events_blocked, b.streams_blocked, b.bytes_blocked, b.sample_stream_id,
         h.completed_at IS NOT NULL,
         r.service, r.topic, r.caller_class, r.caller_member
  FROM __SCHEMA__.wh_ephemeral_blockers b
  JOIN __SCHEMA__.wh_type_history_walks h
    ON h.operation = 'ephemeral' AND h.event_type = b.ephemeral_type AND h.started_at = b.walk_started_at
  -- The newest route of the blocking type, and on it the hop that wrote the event (the first Current hop).
  LEFT JOIN LATERAL (
    SELECT NULLIF(hop.value ->> 'sn', '') AS service,
           NULLIF(hop.value ->> 'to', '') AS topic,
           NULLIF(hop.value ->> 'cl', '') AS caller_class,
           NULLIF(hop.value ->> 'cm', '') AS caller_member
    FROM __SCHEMA__.wh_routes rt
    CROSS JOIN LATERAL (
      SELECT h2.value
      FROM jsonb_array_elements(CASE WHEN jsonb_typeof(rt.hops) = 'array' THEN rt.hops ELSE '[]'::jsonb END)
        WITH ORDINALITY AS h2(value, ord)
      ORDER BY CASE WHEN COALESCE(h2.value ->> 'ty', '0') IN ('0', 'Current') THEN 0 ELSE 1 END, h2.ord
      LIMIT 1
    ) hop
    WHERE rt.event_type = b.blocking_type
    ORDER BY rt.last_seen DESC
    LIMIT 1
  ) r ON TRUE
  WHERE b.events_blocked > 0
  ORDER BY b.ephemeral_type, b.events_blocked DESC, b.blocking_type;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_ephemeral_blockers_report() IS
  'The ephemeral blocker report: the blocker report of every ephemeral type''s current history walk, one row per (ephemeral type, '
  'blocking type), with whether the walk is complete and the newest route of the blocking type: the service, topic, '
  'caller class and caller member of the hop that wrote it (NULL when no route is recorded).';

-- ===========================================================================================
-- Read models opted into mixing
-- ===========================================================================================

-- <docs>fundamentals/events/ephemeral-events#read-models-opted-into-mixing</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:MixedReadModel_WithNoDurableInputLeft_IsReportedReadyToDropTheAttributeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:MixedReadModel_WithDurableInputsLeft_ReportsTheirCounts_InTheirOwnStreamsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:MixedReadModel_ADurableInputSharingStreams_IsLinkedToItsBlockerEntryAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_ephemeral_mixed_inputs(p_durable TEXT[], p_ephemeral TEXT[])
RETURNS TABLE(
  event_type TEXT,
  events_stored BIGINT,
  streams_stored BIGINT,
  streams_mixed BIGINT
)
LANGUAGE plpgsql STABLE
SET plan_cache_mode = force_custom_plan
AS $$
DECLARE
  c_flag_ephemeral CONSTANT INTEGER := 8;
BEGIN
  -- Each durable input's Sourced events, read through the type's index, and of their streams those that also hold an
  -- event of one of the read model's ephemeral inputs (history only: the store refuses new mixing).
  RETURN QUERY
  SELECT t.name,
         COUNT(es.event_id),
         COUNT(DISTINCT es.stream_id),
         COUNT(DISTINCT es.stream_id) FILTER (WHERE EXISTS (
           SELECT 1 FROM __SCHEMA__.wh_event_store e
           WHERE e.stream_id = es.stream_id AND e.event_type = ANY(COALESCE(p_ephemeral, ARRAY[]::TEXT[]))))
  FROM (SELECT DISTINCT __SCHEMA__.normalize_event_type(d) AS name FROM unnest(COALESCE(p_durable, ARRAY[]::TEXT[])) AS d) t
  LEFT JOIN __SCHEMA__.wh_event_store es ON es.event_type = t.name AND (es.flags & c_flag_ephemeral) = 0
  GROUP BY t.name
  ORDER BY t.name;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_ephemeral_mixed_inputs(TEXT[], TEXT[]) IS
  'The ephemeral blocker report, for a read model opted into mixing ephemeral and Sourced events: per durable input '
  'type, its stored Sourced events and their streams, and the streams that also hold an event of one of the given '
  'ephemeral input types. Zero events for every durable input means the read model can drop the opt-in.';
