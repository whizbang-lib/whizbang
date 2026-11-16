-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 397_TypesDeclaredEphemeralTogetherConvertTheirStreamsWhole.sql
-- Date: 2026-11-12
-- Description: The ephemeral history walk converts stream by stream (#2278).
--
--              Until now each walk converted one type and refused any stream holding a Sourced event of a type outside
--              that type's names, released and allowed types. When several types that share streams were all declared
--              ephemeral after their history was written durable, each type's walk was refused by the other types'
--              events, which were still Sourced because their own walks were refused the same way: none could go first,
--              and the blocker report named only types that were themselves declared ephemeral.
--
--              Now the walk is given the other declared-ephemeral types, with their own released and allowed types. A
--              candidate stream is judged by every Sourced type it holds:
--
--                walked     a name of the walked type.
--                declared   a name of another declared-ephemeral type.
--                released   released for a declared type the stream holds (the walked type or another).
--                allowed    allowed to share a stream with EVERY declared type the stream holds: it stays Sourced.
--                blocking   anything else.
--
--              A stream with no blocking type is converted whole, in one statement: every Sourced event of a walked,
--              declared or released type in it is stamped ephemeral, so the stream goes from durable to ephemeral as a
--              unit and is never left partly converted. A stream with a blocking type is refused whole, and only the
--              blocking types are recorded in wh_ephemeral_blockers, so the report stays the list a developer acts on
--              (mark ephemeral, release, allow the pair, or keep). The extra events go through the same digest
--              subtraction, bucket clean-up, epoch refold and origin generation bump as the walked type's own.
--
--                _wh_ephemeral_stream_verdicts    new: the one judgment of a stream, read by the body and by the report.
--                _wh_reclassify_ephemeral_events  393's body, judging through the verdicts; also returns events_declared,
--                                                 the other declared types' events converted with the stream.
--                _wh_record_ephemeral_blockers    392's report, recording the verdicts' blocking types.
--                wh_ephemeral_history_apply       393's batch, taking the other declared types and their rules as bound
--                                                 parameters and returning events_declared.
--
--              With no other declared type every verdict is 393's, so a single declared type walks exactly as before.
--
--              Number 397: 394 to 396 are taken by other open work.
--
-- Reversal: lossy (393's body and batch and 392's report are restored; events converted with their stream stay
--           ephemeral)
-- Dependencies: 393 (_wh_reclassify_ephemeral_events, wh_ephemeral_history_apply), 392 (_wh_record_ephemeral_blockers,
--               wh_ephemeral_blockers), 380 (wh_type_history_walks, _wh_type_history_advance), 336
--               (_wh_fold_stream_digests), 093 (_wh_refold_epochs_covering, _wh_bump_origin_generation), 214
--               (EventFlags.AtMostOnce), 006 (normalize_event_type)
-- Objects: _wh_ephemeral_stream_verdicts, _wh_reclassify_ephemeral_events, _wh_record_ephemeral_blockers,
--          wh_ephemeral_history_apply
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests): each takes new parameters.
SELECT __SCHEMA__.drop_all_overloads('_wh_ephemeral_stream_verdicts');
SELECT __SCHEMA__.drop_all_overloads('_wh_reclassify_ephemeral_events');
SELECT __SCHEMA__.drop_all_overloads('_wh_record_ephemeral_blockers');
SELECT __SCHEMA__.drop_all_overloads('wh_ephemeral_history_apply');

-- ===========================================================================================
-- The judgment of a stream
-- ===========================================================================================

-- <docs>fundamentals/events/ephemeral-events#a-stream-becomes-ephemeral-whole</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralStreamByStreamSqlTests.cs:OneWalk_ConvertsEveryDeclaredEphemeralTypeInTheStream_Together_AndTheDigestsEqualARecountAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralStreamByStreamSqlTests.cs:AStreamHoldingAGenuinelyDurableType_IsRefusedWhole_AndTheReportNamesOnlyThatTypeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralStreamByStreamSqlTests.cs:AReleaseOfTheOtherDeclaredType_AppliesInTheStreamsItIsIn_ToTheFirstWalkAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralStreamByStreamSqlTests.cs:AnAllowedDurableType_ThatOnlyOneDeclaredTypeIsPairedWith_RefusesTheStream_AndIsReportedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralStreamByStreamSqlTests.cs:AnAllowedDurableType_PairedWithEveryDeclaredTypeInTheStream_StaysDurable_AndTheRestConvertAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_ephemeral_stream_verdicts(
  p_stream_ids UUID[],
  p_names TEXT[],
  p_released TEXT[] DEFAULT NULL,
  p_allowed TEXT[] DEFAULT NULL,
  p_declared TEXT[] DEFAULT NULL,
  p_rule_owner TEXT[] DEFAULT NULL,
  p_rule_kind TEXT[] DEFAULT NULL,
  p_rule_type TEXT[] DEFAULT NULL
)
RETURNS TABLE(stream_id UUID, event_type TEXT, verdict TEXT)
LANGUAGE plpgsql
SET plan_cache_mode = force_custom_plan
AS $$
DECLARE
  c_flag_ephemeral CONSTANT INTEGER := 8;
  v_names TEXT[] := COALESCE(p_names, ARRAY[]::TEXT[]);
  v_declared TEXT[];
BEGIN
  IF cardinality(COALESCE(p_stream_ids, ARRAY[]::UUID[])) = 0 OR cardinality(v_names) = 0 THEN
    RETURN;
  END IF;

  -- The other declared-ephemeral types' names; a name of the walked type is the walked type.
  SELECT COALESCE(array_agg(DISTINCT t), ARRAY[]::TEXT[]) INTO v_declared
  FROM unnest(COALESCE(p_declared, ARRAY[]::TEXT[])) AS t
  WHERE t IS NOT NULL AND NOT (t = ANY(v_names));

  RETURN QUERY
  WITH present AS (
    -- Each Sourced type each stream holds: one read of the streams' events through idx_event_store_stream.
    SELECT DISTINCT es.stream_id, es.event_type::TEXT AS event_type
    FROM __SCHEMA__.wh_event_store es
    WHERE es.stream_id = ANY(p_stream_ids)
      AND (es.flags & c_flag_ephemeral) = 0
  ),
  -- Every declared type's rules, by each of its stored names: the walked type's released and allowed types for each of
  -- its names, and the other declared types' rules as given.
  rule AS (
    SELECT n AS owner, 'released'::TEXT AS kind, r AS rule_type
    FROM unnest(v_names) AS n CROSS JOIN unnest(COALESCE(p_released, ARRAY[]::TEXT[])) AS r
    WHERE r IS NOT NULL
    UNION
    SELECT n, 'allowed', a
    FROM unnest(v_names) AS n CROSS JOIN unnest(COALESCE(p_allowed, ARRAY[]::TEXT[])) AS a
    WHERE a IS NOT NULL
    UNION
    SELECT x.o, x.k, x.t
    FROM unnest(COALESCE(p_rule_owner, ARRAY[]::TEXT[]), COALESCE(p_rule_kind, ARRAY[]::TEXT[]), COALESCE(p_rule_type, ARRAY[]::TEXT[])) AS x(o, k, t)
    WHERE x.o = ANY(v_declared) AND x.k IN ('released', 'allowed') AND x.t IS NOT NULL
  ),
  -- The declared types each stream holds: the ones whose rules judge its other types.
  holder AS (
    SELECT p.stream_id, p.event_type
    FROM present p
    WHERE p.event_type = ANY(v_names) OR p.event_type = ANY(v_declared)
  )
  SELECT p.stream_id,
         p.event_type,
         CASE
           WHEN p.event_type = ANY(v_names) THEN 'walked'
           WHEN p.event_type = ANY(v_declared) THEN 'declared'
           -- Released for any declared type the stream holds: the release applies in that type's streams.
           WHEN EXISTS (
             SELECT 1 FROM holder h JOIN rule r ON r.owner = h.event_type
             WHERE h.stream_id = p.stream_id AND r.kind = 'released' AND r.rule_type = p.event_type
           ) THEN 'released'
           -- Allowed with every declared type the stream holds: kept, and no declared type is left beside a Sourced type
           -- it is not paired with.
           WHEN EXISTS (SELECT 1 FROM holder h WHERE h.stream_id = p.stream_id)
            AND NOT EXISTS (
             SELECT 1 FROM holder h
             WHERE h.stream_id = p.stream_id
               AND NOT EXISTS (
                 SELECT 1 FROM rule r
                 WHERE r.owner = h.event_type AND r.kind = 'allowed' AND r.rule_type = p.event_type)
           ) THEN 'allowed'
           ELSE 'blocking'
         END::TEXT
  FROM present p;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_ephemeral_stream_verdicts(UUID[], TEXT[], TEXT[], TEXT[], TEXT[], TEXT[], TEXT[], TEXT[]) IS
  'Issue #2278: judges each Sourced type each given stream holds, for the ephemeral history walk of the type stored under '
  'p_names: walked (a name of it), declared (a name of another declared-ephemeral type, p_declared), released (released '
  'for a declared type the stream holds), allowed (allowed with every declared type the stream holds; it stays Sourced) '
  'or blocking. p_released and p_allowed are the walked type''s rules; the other declared types'' are the parallel '
  'arrays p_rule_owner (a stored name), p_rule_kind (released or allowed) and p_rule_type. A stream with no blocking '
  'type converts whole. Read by _wh_reclassify_ephemeral_events and _wh_record_ephemeral_blockers.';

-- ===========================================================================================
-- The report, by the verdicts
-- ===========================================================================================

-- <docs>fundamentals/events/ephemeral-events#what-holds-ephemeral-history-back</docs>
-- <docs>fundamentals/events/ephemeral-events#a-stream-becomes-ephemeral-whole</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:Report_AMixedStream_NamesTheBlockingType_ItsCounts_ASampleStream_AndItsRouteAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:Streams_AreCountedOnce_WhenTheirEventsSpanSeveralBatchesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:Release_AStreamStillHeldByAnUnreleasedType_IsLeftWhole_AndReportedForThatTypeOnlyAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MixedStreamAllowanceSqlTests.cs:Report_AnAllowedPair_IsNoBlocker_AndAnUnallowedOneOffersAllowMixingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralStreamByStreamSqlTests.cs:AStreamHoldingAGenuinelyDurableType_IsRefusedWhole_AndTheReportNamesOnlyThatTypeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralStreamByStreamSqlTests.cs:AnAllowedDurableType_ThatOnlyOneDeclaredTypeIsPairedWith_RefusesTheStream_AndIsReportedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_record_ephemeral_blockers(
  p_ephemeral_type TEXT,
  p_names TEXT[],
  p_event_ids UUID[],
  p_after BIGINT,
  p_walk_started_at TIMESTAMPTZ,
  p_released TEXT[] DEFAULT NULL,
  p_allowed TEXT[] DEFAULT NULL,
  p_declared TEXT[] DEFAULT NULL,
  p_rule_owner TEXT[] DEFAULT NULL,
  p_rule_kind TEXT[] DEFAULT NULL,
  p_rule_type TEXT[] DEFAULT NULL
)
RETURNS VOID
LANGUAGE plpgsql
SET plan_cache_mode = force_custom_plan
AS $$
DECLARE
  c_flag_ephemeral CONSTANT INTEGER := 8;
  v_streams UUID[];
BEGIN
  IF cardinality(COALESCE(p_event_ids, ARRAY[]::UUID[])) = 0 THEN
    RETURN;
  END IF;

  SELECT array_agg(DISTINCT es.stream_id) INTO v_streams
  FROM __SCHEMA__.wh_event_store es
  WHERE es.event_id = ANY(p_event_ids)
    AND es.event_type = ANY(p_names)
    AND (es.flags & c_flag_ephemeral) = 0;

  WITH verdict AS (
    SELECT v.stream_id, v.event_type
    FROM __SCHEMA__._wh_ephemeral_stream_verdicts(
      v_streams, p_names, p_released, p_allowed, p_declared, p_rule_owner, p_rule_kind, p_rule_type) v
    WHERE v.verdict = 'blocking'
  ),
  blocked AS (
    -- The batch's Sourced events of the type whose stream holds a blocking type.
    SELECT es.event_id, es.stream_id
    FROM __SCHEMA__.wh_event_store es
    WHERE es.event_id = ANY(p_event_ids)
      AND es.event_type = ANY(p_names)
      AND (es.flags & c_flag_ephemeral) = 0
      AND EXISTS (SELECT 1 FROM verdict v WHERE v.stream_id = es.stream_id)
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
    SELECT DISTINCT ps.stream_id, v.event_type AS blocking_type
    FROM per_stream ps
    JOIN verdict v ON v.stream_id = ps.stream_id
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

COMMENT ON FUNCTION __SCHEMA__._wh_record_ephemeral_blockers(TEXT, TEXT[], UUID[], BIGINT, TIMESTAMPTZ, TEXT[], TEXT[], TEXT[], TEXT[], TEXT[], TEXT[]) IS
  'The ephemeral blocker report: adds one batch of the ephemeral history walk to wh_ephemeral_blockers: for the given '
  'Sourced events of the type whose streams hold a blocking type (_wh_ephemeral_stream_verdicts), the blocking types '
  'present, with the blocked events, the streams seen first in this batch, their approximate stored size and one sample '
  'stream. 397 (#2278): another declared-ephemeral type is never a blocking type.';

-- ===========================================================================================
-- The reclassification body, stream by stream
-- ===========================================================================================

-- <docs>fundamentals/events/ephemeral-events#making-an-existing-type-ephemeral</docs>
-- <docs>fundamentals/events/ephemeral-events#what-holds-ephemeral-history-back</docs>
-- <docs>fundamentals/events/ephemeral-events#allowing-mixed-streams</docs>
-- <docs>fundamentals/events/ephemeral-events#a-stream-becomes-ephemeral-whole</docs>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralHistoryReclassifySqlTests.cs:Reclassify_AMixedStream_IsReportedAndLeftUntouched_AndTheDigestsEqualARecountAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralReclassifySqlTests.cs:Reclassify_MixedStream_SkipsAndReportsBlocked_ButReclassifiesCleanStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:Release_StampsTheBlockingEvents_OnlyInStreamsHoldingTheEphemeralType_AndTheDigestsEqualARecountAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:NoRelease_NoDurableEventChangesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:Release_AStreamStillHeldByAnUnreleasedType_IsLeftWhole_AndReportedForThatTypeOnlyAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MixedStreamAllowanceSqlTests.cs:Reclassify_AnAllowedPair_ReclassifiesTheEphemeralEvents_KeepsTheDurable_AndTheDigestsEqualARecountAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestWritersTests.cs:ReclassifyEphemeral_SubtractsEveryLaneOfTheReclassifiedEventsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralStreamByStreamSqlTests.cs:OneWalk_ConvertsEveryDeclaredEphemeralTypeInTheStream_Together_AndTheDigestsEqualARecountAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralStreamByStreamSqlTests.cs:AnAllowedDurableType_PairedWithEveryDeclaredTypeInTheStream_StaysDurable_AndTheRestConvertAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_reclassify_ephemeral_events(
  p_names TEXT[],
  p_event_ids UUID[],
  p_released TEXT[] DEFAULT NULL,
  p_allowed TEXT[] DEFAULT NULL,
  p_declared TEXT[] DEFAULT NULL,
  p_rule_owner TEXT[] DEFAULT NULL,
  p_rule_kind TEXT[] DEFAULT NULL,
  p_rule_type TEXT[] DEFAULT NULL
)
RETURNS TABLE(
  events_reclassified BIGINT,
  streams_reclassified BIGINT,
  events_blocked BIGINT,
  streams_blocked BIGINT,
  events_released BIGINT,
  events_declared BIGINT
) AS $$
DECLARE
  c_flag_ephemeral CONSTANT INTEGER := 8;
  c_zero CONSTANT UUID := __EMPTY_UUID__;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  v_streams UUID[];
  v_refused UUID[];
  v_stream_ids UUID[];
  v_types TEXT[];
  v_verdicts TEXT[];
  v_ids UUID[];
  v_events BIGINT := 0;
  v_streams_done BIGINT := 0;
  v_released_events BIGINT := 0;
  v_declared_events BIGINT := 0;
  v_blocked_events BIGINT := 0;
  v_blocked_streams BIGINT := 0;
  v_touched UUID[];
  v_touched_types TEXT[];
  v_lanes UUID[];
  v_mins BIGINT[];
  v_maxs BIGINT[];
  v_i INTEGER;
BEGIN
  IF cardinality(COALESCE(p_names, ARRAY[]::TEXT[])) = 0 OR cardinality(COALESCE(p_event_ids, ARRAY[]::UUID[])) = 0 THEN
    RETURN QUERY SELECT 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT;
    RETURN;
  END IF;

  -- The streams of the given Sourced events of the names: the candidates.
  SELECT array_agg(DISTINCT es.stream_id) INTO v_streams
  FROM __SCHEMA__.wh_event_store es
  WHERE es.event_id = ANY(p_event_ids)
    AND es.event_type = ANY(p_names)
    AND (es.flags & c_flag_ephemeral) = 0;

  -- 397 (#2278): each candidate stream judged once, by every Sourced type it holds. A stream holding a blocking type is
  -- refused whole; in every other stream the walked, declared and released types are stamped, and the allowed stay.
  SELECT COALESCE(array_agg(DISTINCT v.stream_id) FILTER (WHERE v.verdict = 'blocking'), ARRAY[]::UUID[]),
         array_agg(v.stream_id) FILTER (WHERE v.verdict IN ('walked', 'declared', 'released')),
         array_agg(v.event_type) FILTER (WHERE v.verdict IN ('walked', 'declared', 'released')),
         array_agg(v.verdict) FILTER (WHERE v.verdict IN ('walked', 'declared', 'released'))
  INTO v_refused, v_stream_ids, v_types, v_verdicts
  FROM __SCHEMA__._wh_ephemeral_stream_verdicts(
    v_streams, p_names, p_released, p_allowed, p_declared, p_rule_owner, p_rule_kind, p_rule_type) v;

  -- The given Sourced events whose stream would become MIXED, left Sourced and reported here.
  SELECT COUNT(*), COUNT(DISTINCT es.stream_id) INTO v_blocked_events, v_blocked_streams
  FROM __SCHEMA__.wh_event_store es
  WHERE es.event_id = ANY(p_event_ids)
    AND es.event_type = ANY(p_names)
    AND (es.flags & c_flag_ephemeral) = 0
    AND es.stream_id = ANY(v_refused);

  -- Every Sourced event of a stamped type in the streams that convert: the whole stream, never part of it.
  SELECT array_agg(es.event_id) INTO v_ids
  FROM unnest(v_stream_ids, v_types) AS c(sid, event_type)
  JOIN __SCHEMA__.wh_event_store es ON es.stream_id = c.sid AND es.event_type = c.event_type
  WHERE NOT (c.sid = ANY(v_refused))
    AND (es.flags & c_flag_ephemeral) = 0;
  v_ids := COALESCE(v_ids, ARRAY[]::UUID[]);

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
  ),
  judged AS (
    -- Each stamped row with its stream's verdict on its type (one verdict per stream and type, so one row each).
    SELECT r.stream_id, r.event_type, x.verdict
    FROM reclassified r
    JOIN unnest(v_stream_ids, v_types, v_verdicts) AS x(sid, event_type, verdict)
      ON x.sid = r.stream_id AND x.event_type = r.event_type
  )
  SELECT COUNT(*) FILTER (WHERE j.verdict = 'walked'),
         COUNT(DISTINCT j.stream_id) FILTER (WHERE j.verdict = 'walked'),
         COUNT(*) FILTER (WHERE j.verdict = 'released'),
         COUNT(*) FILTER (WHERE j.verdict = 'declared'),
         array_agg(DISTINCT j.stream_id),
         array_agg(DISTINCT j.event_type)
  INTO v_events, v_streams_done, v_released_events, v_declared_events, v_touched, v_touched_types
  FROM judged j CROSS JOIN digest_subtract;

  -- Migration 087: drop the buckets the subtraction emptied, in the streams it touched (a separate statement, so it sees
  -- the subtraction).
  IF v_touched IS NOT NULL THEN
    DELETE FROM __SCHEMA__.wh_stream_digests d
    WHERE d.stream_id = ANY(v_touched) AND d.event_type = ANY(v_touched_types) AND d.event_count <= 0;
  END IF;

  -- Migration 093 (#80-F): refold the sealed epochs the reclassification touched, then bump the generation once.
  IF v_lanes IS NOT NULL AND v_events + v_released_events + v_declared_events > 0 THEN
    FOR v_i IN 1 .. array_length(v_lanes, 1) LOOP
      PERFORM __SCHEMA__._wh_refold_epochs_covering(v_lanes[v_i], v_mins[v_i], v_maxs[v_i]);
    END LOOP;
    PERFORM __SCHEMA__._wh_bump_origin_generation();
  END IF;

  RETURN QUERY SELECT v_events, v_streams_done, v_blocked_events, v_blocked_streams, v_released_events, v_declared_events;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_reclassify_ephemeral_events(TEXT[], UUID[], TEXT[], TEXT[], TEXT[], TEXT[], TEXT[], TEXT[]) IS
  'Issue #2206: stamps EventFlags.Ephemeral on the Sourced history of the named types, subtracts it from the stream '
  'digests through _wh_fold_stream_digests, drops the buckets that emptied, refolds the sealed epochs it was in and '
  'bumps the origin generation (093). 397 (#2278): stream by stream. Each stream of the given events is judged by '
  '_wh_ephemeral_stream_verdicts; a stream holding a blocking type is left whole and its given events reported '
  '(events_blocked); every other stream is converted whole: every Sourced event in it of the names, of another '
  'declared-ephemeral type (p_declared, events_declared) or of a type released for one of them (events_released). The '
  'allowed types stay Sourced. The one body behind reclassify_events_ephemeral and the batched history walk.';

-- ===========================================================================================
-- One batch of the walk
-- ===========================================================================================

-- <docs>fundamentals/events/ephemeral-events#making-an-existing-type-ephemeral</docs>
-- <docs>fundamentals/events/ephemeral-events#what-holds-ephemeral-history-back</docs>
-- <docs>fundamentals/events/ephemeral-events#allowing-mixed-streams</docs>
-- <docs>fundamentals/events/ephemeral-events#a-stream-becomes-ephemeral-whole</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralHistoryReclassifySqlTests.cs:Reclassify_RunsInBoundedBatches_AndResumesFromItsCursor_AfterACrashAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralHistoryReclassifySqlTests.cs:Apply_AWindowThatIsNotItsOwn_ChangesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:Report_Off_RecordsNothing_LogsNothing_AndTheReclassifyIsUnchangedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:Counts_DescribeTheCurrentWalk_AndResetWhenItRestartsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MixedStreamAllowanceSqlTests.cs:Report_AnAllowedPair_IsNoBlocker_AndAnUnallowedOneOffersAllowMixingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralStreamByStreamSqlTests.cs:TheOtherTypesLaterWalk_FindsItsEventsAlreadyEphemeral_AndTheCountersStayTruthfulAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralStreamByStreamSqlTests.cs:ANameIsBoundAsData_NeverSqlTextAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperEphemeralHistoryReclassifyTests.cs:TwoDeclaredTypesSharingAStream_AreConvertedTogether_ByTheRegisteredStepAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_ephemeral_history_apply(
  p_event_type TEXT,
  p_names TEXT[],
  p_after BIGINT,
  p_through BIGINT,
  p_scan_size INTEGER,
  p_rows_scanned BIGINT,
  p_released TEXT[] DEFAULT NULL,
  p_report BOOLEAN DEFAULT FALSE,
  p_allowed TEXT[] DEFAULT NULL,
  p_declared TEXT[] DEFAULT NULL,
  p_rule_owner TEXT[] DEFAULT NULL,
  p_rule_kind TEXT[] DEFAULT NULL,
  p_rule_type TEXT[] DEFAULT NULL
)
RETURNS TABLE(
  status TEXT,
  events_reclassified BIGINT,
  events_blocked BIGINT,
  streams_blocked BIGINT,
  events_total BIGINT,
  events_matched BIGINT,
  events_released BIGINT,
  events_declared BIGINT
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
  v_declared TEXT[];
  v_rule_owner TEXT[];
  v_rule_kind TEXT[];
  v_rule_type TEXT[];
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
      0::BIGINT, 0::BIGINT;
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

  -- 397 (#2278): the other declared-ephemeral types' names, and their released and allowed types, normalized likewise.
  SELECT COALESCE(array_agg(DISTINCT __SCHEMA__.normalize_event_type(t)), ARRAY[]::TEXT[]) INTO v_declared
  FROM unnest(COALESCE(p_declared, ARRAY[]::TEXT[])) AS t
  WHERE t IS NOT NULL;
  SELECT COALESCE(array_agg(__SCHEMA__.normalize_event_type(x.o)), ARRAY[]::TEXT[]),
         COALESCE(array_agg(x.k), ARRAY[]::TEXT[]),
         COALESCE(array_agg(__SCHEMA__.normalize_event_type(x.t)), ARRAY[]::TEXT[])
  INTO v_rule_owner, v_rule_kind, v_rule_type
  FROM unnest(COALESCE(p_rule_owner, ARRAY[]::TEXT[]), COALESCE(p_rule_kind, ARRAY[]::TEXT[]), COALESCE(p_rule_type, ARRAY[]::TEXT[])) AS x(o, k, t)
  WHERE x.o IS NOT NULL AND x.k IS NOT NULL AND x.t IS NOT NULL;

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
  -- streams between the judgment and the stamp, so a stream judged convertible stays so until it is stamped.
  IF v_streams IS NOT NULL THEN
    PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || u.sid::text))
    FROM unnest(v_streams) AS u(sid)
    ORDER BY u.sid;
  END IF;

  IF p_report THEN
    PERFORM __SCHEMA__._wh_record_ephemeral_blockers(
      v_type, v_names, v_ids, p_after, v_started, v_released, v_allowed, v_declared, v_rule_owner, v_rule_kind, v_rule_type);
  END IF;

  SELECT * INTO v_done
  FROM __SCHEMA__._wh_reclassify_ephemeral_events(
    v_names, v_ids, v_released, v_allowed, v_declared, v_rule_owner, v_rule_kind, v_rule_type);

  SELECT * INTO v_end
  FROM __SCHEMA__._wh_type_history_advance(
    c_operation, v_type, p_after, p_through, p_scan_size, p_rows_scanned, 0, v_done.events_reclassified, 0,
    v_done.events_blocked);

  RETURN QUERY SELECT v_end.status, v_done.events_reclassified, v_done.events_blocked, v_done.streams_blocked,
    v_end.events_total, v_end.events_matched, v_done.events_released, v_done.events_declared;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_ephemeral_history_apply(TEXT, TEXT[], BIGINT, BIGINT, INTEGER, BIGINT, TEXT[], BOOLEAN, TEXT[], TEXT[], TEXT[], TEXT[], TEXT[]) IS
  'Issue #2206: one batch of the ephemeral history walk. Reclassifies the Sourced events of the type in the window '
  '(p_after, p_through] of its commit sequences, under the emit chains'' per-stream locks, through '
  '_wh_reclassify_ephemeral_events (p_names are every name the type is stored under), and moves the walk past the '
  'window. 392: p_released are the types released for this type; p_report records the batch''s blockers in '
  'wh_ephemeral_blockers. 393 (#2255): p_allowed are the Sourced types allowed to share a stream with it. 397 (#2278): '
  'p_declared are the other declared-ephemeral types'' names and p_rule_owner/p_rule_kind/p_rule_type their released '
  'and allowed types: a stream holding only declared, released and allowed types converts whole, and events_declared '
  'counts the other declared types'' events converted with it. All default to off, which is 381''s batch.';
