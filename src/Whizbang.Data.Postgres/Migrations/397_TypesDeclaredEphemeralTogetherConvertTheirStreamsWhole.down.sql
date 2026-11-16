-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 397_TypesDeclaredEphemeralTogetherConvertTheirStreamsWhole.sql (#2278): the reclassification body and
-- the walk's batch as 393 defined them, the blocker report as 392 defined it, and the stream judgment dropped (lossy:
-- events converted with their stream stay ephemeral, and each type is again walked on its own).

SELECT __SCHEMA__.drop_all_overloads('_wh_reclassify_ephemeral_events');
SELECT __SCHEMA__.drop_all_overloads('_wh_record_ephemeral_blockers');
SELECT __SCHEMA__.drop_all_overloads('wh_ephemeral_history_apply');
SELECT __SCHEMA__.drop_all_overloads('_wh_ephemeral_stream_verdicts');

-- ---------------------------------------------------------------------------------------------
-- _wh_record_ephemeral_blockers: 392's definition, whole.
-- ---------------------------------------------------------------------------------------------
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

-- ---------------------------------------------------------------------------------------------
-- _wh_reclassify_ephemeral_events and wh_ephemeral_history_apply: 393's definitions, whole.
-- ---------------------------------------------------------------------------------------------

-- ===========================================================================================
-- The reclassification body, with the allowance
-- ===========================================================================================

-- <docs>fundamentals/events/ephemeral-events#making-an-existing-type-ephemeral</docs>
-- <docs>fundamentals/events/ephemeral-events#what-holds-ephemeral-history-back</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralHistoryReclassifySqlTests.cs:Reclassify_AMixedStream_IsReportedAndLeftUntouched_AndTheDigestsEqualARecountAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralReclassifySqlTests.cs:Reclassify_MixedStream_SkipsAndReportsBlocked_ButReclassifiesCleanStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:Release_StampsTheBlockingEvents_OnlyInStreamsHoldingTheEphemeralType_AndTheDigestsEqualARecountAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:NoRelease_NoDurableEventChangesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:Release_AStreamStillHeldByAnUnreleasedType_IsLeftWhole_AndReportedForThatTypeOnlyAsync</tests>
-- <docs>fundamentals/events/ephemeral-events#allowing-mixed-streams</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MixedStreamAllowanceSqlTests.cs:Reclassify_AnAllowedPair_ReclassifiesTheEphemeralEvents_KeepsTheDurable_AndTheDigestsEqualARecountAsync</tests>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestWritersTests.cs:ReclassifyEphemeral_SubtractsEveryLaneOfTheReclassifiedEventsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_reclassify_ephemeral_events(
  p_names TEXT[],
  p_event_ids UUID[],
  p_released TEXT[] DEFAULT NULL,
  p_allowed TEXT[] DEFAULT NULL
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
  v_allowed TEXT[];
  v_stamped TEXT[];
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
  -- 393 (#2255): the Sourced types allowed to share a stream with these names. They do not block them either, but they
  -- stay Sourced: the names' events in such streams are reclassified around them. A released type is released.
  SELECT COALESCE(array_agg(DISTINCT t), ARRAY[]::TEXT[]) INTO v_allowed
  FROM unnest(COALESCE(p_allowed, ARRAY[]::TEXT[])) AS t
  WHERE t IS NOT NULL AND NOT (t = ANY(p_names)) AND NOT (t = ANY(v_released));
  v_stamped := p_names || v_released;
  v_kept := v_stamped || v_allowed;

  -- The given Sourced events whose stream would become MIXED: it holds a Sourced (flags & 8 = 0) event of a type that
  -- is neither one of the names nor released or allowed for them. Reclassifying them would violate the homogeneous-stream
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

  -- The given Sourced events whose stream is homogeneous once the released and allowed types are set aside, and their
  -- streams.
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
    WHERE d.stream_id = ANY(v_touched) AND d.event_type = ANY(v_stamped) AND d.event_count <= 0;
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

COMMENT ON FUNCTION __SCHEMA__._wh_reclassify_ephemeral_events(TEXT[], UUID[], TEXT[], TEXT[]) IS
  'Issue #2206: stamps EventFlags.Ephemeral on the given Sourced events of the named types whose streams hold no '
  'Sourced event of another type, subtracts them from the stream digests through _wh_fold_stream_digests, drops the '
  'buckets that emptied, refolds the sealed epochs they were in and bumps the origin generation (093). Reports the '
  'given events left Sourced because their stream is mixed. 392: the types in p_released do not block the '
  'names, and their Sourced events in the streams the names are reclassified in are reclassified with them '
  '(events_released); no other stream of theirs is touched. 393 (#2255): the types in p_allowed do not block the names '
  'either, and stay Sourced. The one body behind reclassify_events_ephemeral and the batched history walk.';

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
-- <docs>fundamentals/events/ephemeral-events#allowing-mixed-streams</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MixedStreamAllowanceSqlTests.cs:Reclassify_AnAllowedPair_ReclassifiesTheEphemeralEvents_KeepsTheDurable_AndTheDigestsEqualARecountAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MixedStreamAllowanceSqlTests.cs:Report_AnAllowedPair_IsNoBlocker_AndAnUnallowedOneOffersAllowMixingAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_ephemeral_history_apply(
  p_event_type TEXT,
  p_names TEXT[],
  p_after BIGINT,
  p_through BIGINT,
  p_scan_size INTEGER,
  p_rows_scanned BIGINT,
  p_released TEXT[] DEFAULT NULL,
  p_report BOOLEAN DEFAULT FALSE,
  p_allowed TEXT[] DEFAULT NULL
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
  v_allowed TEXT[];
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

  -- 393 (#2255): the Sourced types allowed to share a stream with this type, normalized likewise. They are not blockers.
  SELECT COALESCE(array_agg(DISTINCT __SCHEMA__.normalize_event_type(t)), ARRAY[]::TEXT[]) INTO v_allowed
  FROM unnest(COALESCE(p_allowed, ARRAY[]::TEXT[])) AS t
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
    PERFORM __SCHEMA__._wh_record_ephemeral_blockers(v_type, v_names, v_names || v_released || v_allowed, v_ids, p_after, v_started);
  END IF;

  SELECT * INTO v_done FROM __SCHEMA__._wh_reclassify_ephemeral_events(v_names, v_ids, v_released, v_allowed);

  SELECT * INTO v_end
  FROM __SCHEMA__._wh_type_history_advance(
    c_operation, v_type, p_after, p_through, p_scan_size, p_rows_scanned, 0, v_done.events_reclassified, 0,
    v_done.events_blocked);

  RETURN QUERY SELECT v_end.status, v_done.events_reclassified, v_done.events_blocked, v_done.streams_blocked,
    v_end.events_total, v_end.events_matched, v_done.events_released;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_ephemeral_history_apply(TEXT, TEXT[], BIGINT, BIGINT, INTEGER, BIGINT, TEXT[], BOOLEAN, TEXT[]) IS
  'Issue #2206: one batch of the ephemeral history walk. Reclassifies the Sourced events of the type in the window '
  '(p_after, p_through] of its commit sequences, under the emit chains'' per-stream locks, through '
  '_wh_reclassify_ephemeral_events (p_names are every name the type is stored under, for the mixed-stream guard), and '
  'moves the walk past the window. 392: p_released are the types released for this type (reclassified with it '
  'in its own streams only); p_report records the batch''s blockers in wh_ephemeral_blockers. 393 (#2255): p_allowed '
  'are the Sourced types allowed to share a stream with it: no blockers, never reclassified. All default to off, '
  'which is 381''s batch.';
