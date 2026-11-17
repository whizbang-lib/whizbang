-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 393_AllowedPairsMayShareAStream.sql
-- Date: 2026-11-11
-- Description: An (ephemeral type, Sourced type) pair that every read model applying both allows to share a stream
--              ([DangerouslyAllowMixedEphemeralAndSourcedEvents(Streams = MixedStreams.Allow)]) may do so (#2255).
--
--              Until now the attribute changed nothing at run time: a stream never held both kinds (WHIZ133, 228), the
--              ephemeral history walk left an ephemeral event Sourced when its stream held a Sourced event of another
--              type (381, 392), and so the ephemeral bodies of such streams were never reaped. For an allowed pair:
--
--                wh_mixed_stream_allowances       the allowed pairs, by every stored name of each type. Written only by
--                                                 wh_sync_mixed_stream_allowances, from the generated registry, at
--                                                 startup (the type-definition reconciler); empty by default.
--                wh_sync_mixed_stream_allowances  replaces the table's rows with the given pairs (bound arrays).
--                _wh_mixed_stream_refusals        228's finder, plus the allowance: while the table is empty it is 228's
--                                                 query exactly (the extra reads sit behind a one-time filter). With
--                                                 pairs, a stream whose write or newest event names a type of a pair is
--                                                 judged by every kind it holds: an event is refused when the stream
--                                                 holds, or this write brings, an event of the other kind whose type is
--                                                 not paired with it. Every other stream is judged as in 228.
--                _wh_reclassify_ephemeral_events  392's body, plus p_allowed: the Sourced types allowed with these names
--                                                 do not block them, and are not reclassified (unlike a release).
--                wh_ephemeral_history_apply       392's batch, taking the allowed types as a bound parameter, passed to the
--                                                 body and kept out of the blocker report.
--
--              The reaper needs no change: it reaps by the ephemeral flag, so the ephemeral events of an allowed stream
--              are reaped on their own rules and its Sourced events are kept.
--
--              Number 393: 392 is the highest on this branch.
--
-- Reversal: lossy (the allowance table is dropped with its rows and 392's and 228's definitions are restored; events
--           already stored on mixed streams, and events reclassified under an allowance, stay as they are)
-- Dependencies: 392 (_wh_reclassify_ephemeral_events, wh_ephemeral_history_apply, _wh_record_ephemeral_blockers), 228
--               (_wh_mixed_stream_refusals), 381 (reclassify_events_ephemeral), 380 (wh_type_history_walks), 336
--               (_wh_fold_stream_digests), 093 (_wh_refold_epochs_covering, _wh_bump_origin_generation), 214
--               (EventFlags.AtMostOnce), 006 (normalize_event_type)
-- Objects: wh_mixed_stream_allowances, wh_sync_mixed_stream_allowances, _wh_mixed_stream_refusals,
--          _wh_reclassify_ephemeral_events, wh_ephemeral_history_apply
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests): the body and the batch take
-- a new parameter.
SELECT __SCHEMA__.drop_all_overloads('_wh_reclassify_ephemeral_events');
SELECT __SCHEMA__.drop_all_overloads('wh_ephemeral_history_apply');
SELECT __SCHEMA__.drop_all_overloads('wh_sync_mixed_stream_allowances');

-- ===========================================================================================
-- The allowed pairs
-- ===========================================================================================

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_mixed_stream_allowances (
  ephemeral_type TEXT NOT NULL,
  durable_type TEXT NOT NULL,
  CONSTRAINT pk_mixed_stream_allowances PRIMARY KEY (ephemeral_type, durable_type)
);

COMMENT ON TABLE __SCHEMA__.wh_mixed_stream_allowances IS
  'The (ephemeral type, Sourced type) pairs that may share a stream (#2255): applied by a read model marked '
  '[DangerouslyAllowMixedEphemeralAndSourcedEvents(Streams = MixedStreams.Allow)] and by no read model without it, '
  'under every name each type is stored by. Replaced at startup by wh_sync_mixed_stream_allowances; empty, the store '
  'refuses every mixed stream as 228 did.';

-- <docs>fundamentals/events/ephemeral-events#allowing-mixed-streams</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MixedStreamAllowanceSqlTests.cs:Sync_ReplacesThePairs_AndAnEmptyListClearsThemAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_sync_mixed_stream_allowances(p_ephemeral TEXT[], p_durable TEXT[])
RETURNS INTEGER
LANGUAGE plpgsql
AS $$
DECLARE
  v_count INTEGER;
BEGIN
  -- The pairs as given, normalized like every stored name; a pair with a missing side is no pair. The rows no longer
  -- given are deleted (a data-modifying CTE always runs) and the new ones inserted in one statement: the sets are disjoint.
  WITH wanted AS (
    SELECT DISTINCT __SCHEMA__.normalize_event_type(x.e) AS ephemeral_type, __SCHEMA__.normalize_event_type(x.d) AS durable_type
    FROM unnest(COALESCE(p_ephemeral, ARRAY[]::TEXT[]), COALESCE(p_durable, ARRAY[]::TEXT[])) AS x(e, d)
    WHERE x.e IS NOT NULL AND x.d IS NOT NULL
  ),
  removed AS (
    DELETE FROM __SCHEMA__.wh_mixed_stream_allowances a
    WHERE NOT EXISTS (SELECT 1 FROM wanted w WHERE w.ephemeral_type = a.ephemeral_type AND w.durable_type = a.durable_type)
    RETURNING 1
  )
  INSERT INTO __SCHEMA__.wh_mixed_stream_allowances (ephemeral_type, durable_type)
  SELECT w.ephemeral_type, w.durable_type FROM wanted w
  ON CONFLICT (ephemeral_type, durable_type) DO NOTHING;

  SELECT COUNT(*) INTO v_count FROM __SCHEMA__.wh_mixed_stream_allowances;
  RETURN v_count;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_sync_mixed_stream_allowances(TEXT[], TEXT[]) IS
  'Replaces wh_mixed_stream_allowances with the given (ephemeral type, Sourced type) pairs (#2255), parallel arrays, '
  'normalized; empty arrays clear it. Returns the pairs now held. Called once at startup by the type-definition '
  'reconciler.';

-- ===========================================================================================
-- The mixed-stream refusal, with the allowance
-- ===========================================================================================

-- <docs>operations/diagnostics/whiz133</docs>
-- <docs>fundamentals/events/ephemeral-events#allowing-mixed-streams</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MixedStreamGuardSqlTests.cs:Store_SourcedEventOnAnEphemeralStream_DeadLettersTheSourcedEventAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MixedStreamGuardSqlTests.cs:Store_EphemeralEventOnASourcedStream_DeadLettersTheEphemeralEventAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MixedStreamGuardSqlTests.cs:Store_FirstWriteMixesBothKinds_KeepsTheFirstKindAndDeadLettersTheOtherAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MixedStreamGuardSqlTests.cs:Store_SourcedEventOnACompactedStream_IsStoredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MixedStreamGuardSqlTests.cs:InboxChain_FirstClaimMixesBothKinds_KeepsTheFirstKindAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DirectAppendMixedStreamGuardTests.cs:DirectAppend_CompactionOriginOnAnEphemeralStream_IsStored_AndTheStreamStaysEphemeralAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MixedStreamAllowanceSqlTests.cs:Append_AnAllowedPair_StoresEitherKindOnAStreamHoldingTheOtherAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MixedStreamAllowanceSqlTests.cs:Append_AnUnpairedType_IsStillRefused_OnAStreamTheAllowanceMixedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MixedStreamAllowanceSqlTests.cs:Append_WithNoAllowance_IsRefusedAsBeforeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MixedStreamAllowanceSqlTests.cs:Store_AnAllowedPairInOneOutboxWrite_StoresBothKindsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_mixed_stream_refusals(
  p_event_ids UUID[],
  p_stream_ids UUID[],
  p_flags INTEGER[],
  p_event_types TEXT[]
) RETURNS TABLE(
  refused_event_id UUID,
  refused_stream_id UUID,
  refusal_text TEXT,
  refusal_detail JSONB
) AS $$
DECLARE
  c_flag_ephemeral CONSTANT INTEGER := 8;     -- EventFlags.Ephemeral (1 << 3)
  -- EventFlags.Compacted (1 << 4): a compaction origin is neither kind. It is never refused, and it never decides
  -- a stream's kind, so the stream it heads keeps the kind of the events around it (StreamCompactor appends one at
  -- the head of an ephemeral stream, which goes on taking ephemeral events).
  c_flag_compacted CONSTANT INTEGER := 16;
  -- 393 (#2255): whether any pair may share a stream. False, the default, makes this 228's finder: the reads below that
  -- only the allowance needs are behind it as a one-time filter, so they never run.
  v_allow BOOLEAN;
BEGIN
  IF p_event_ids IS NULL OR cardinality(p_event_ids) = 0 THEN
    RETURN;
  END IF;

  -- The copy's own per-stream lock, taken in the copy's order (stream id ascending). Held to commit, so the kind
  -- read below cannot change before the copy stores against it; the copy's later request for the same lock is a
  -- no-op in this transaction.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || s.sid::text))
  FROM (
    SELECT DISTINCT u.sid
    FROM unnest(p_stream_ids) AS u(sid)
    WHERE u.sid IS NOT NULL
    ORDER BY u.sid
  ) AS s;

  v_allow := EXISTS (SELECT 1 FROM __SCHEMA__.wh_mixed_stream_allowances);

  RETURN QUERY
  WITH candidate AS (
    SELECT c.event_id,
           c.stream_id,
           (COALESCE(c.flags, 0) & c_flag_ephemeral) <> 0 AS is_ephemeral,
           __SCHEMA__.normalize_event_type(c.event_type) AS event_type
    FROM unnest(p_event_ids, p_stream_ids, p_flags, p_event_types) AS c(event_id, stream_id, flags, event_type)
    WHERE c.stream_id IS NOT NULL
      AND (COALESCE(c.flags, 0) & c_flag_compacted) = 0
  ),
  -- Each stream's first event in this write (version order is message id order), one sort over the write.
  opener AS (
    SELECT DISTINCT ON (candidate.stream_id) candidate.stream_id, candidate.is_ephemeral, candidate.event_type
    FROM candidate
    ORDER BY candidate.stream_id, candidate.event_id
  ),
  stream_kind AS (
    SELECT o.stream_id,
           COALESCE(stored.is_ephemeral, o.is_ephemeral) AS is_ephemeral,
           COALESCE(stored.event_type, o.event_type) AS event_type
    FROM opener o
    -- The newest stored event that is not a compaction origin, read backward through idx_event_store_stream
    -- (stream_id, version): one index lookup per stream, and one more row for a stream whose head is an origin.
    -- A stream with nothing of a kind stored takes the kind of its first event in this write.
    LEFT JOIN LATERAL (
      SELECT (es.flags & c_flag_ephemeral) <> 0 AS is_ephemeral, es.event_type
      FROM __SCHEMA__.wh_event_store es
      WHERE es.stream_id = o.stream_id
        AND (es.flags & c_flag_compacted) = 0
      ORDER BY es.version DESC
      LIMIT 1
    ) AS stored ON TRUE
  ),
  -- 393: every type named by an allowed pair, under the one-time filter.
  paired_type AS (
    SELECT a.ephemeral_type AS event_type FROM __SCHEMA__.wh_mixed_stream_allowances a WHERE v_allow
    UNION
    SELECT a.durable_type FROM __SCHEMA__.wh_mixed_stream_allowances a WHERE v_allow
  ),
  -- 393: the streams judged by every kind they hold: those whose newest event, or an event of this write, is of a
  -- paired type. A stream mixed under an allowance holds only paired types (anything else is refused here), so its
  -- newest event is always one; a stream with none can only hold one kind, and is judged as 228 judged it.
  checked AS (
    SELECT k.stream_id
    FROM stream_kind k
    WHERE v_allow
      AND (EXISTS (SELECT 1 FROM paired_type p WHERE p.event_type = k.event_type)
           OR EXISTS (SELECT 1 FROM candidate c JOIN paired_type p ON p.event_type = c.event_type
                      WHERE c.stream_id = k.stream_id))
  ),
  -- 393: the (type, kind)s each checked stream holds (written_before NULL), and those this write brings to it, each with
  -- its event id (version order is message id order). A compaction origin is neither kind.
  present AS (
    SELECT DISTINCT es.stream_id, es.event_type, (es.flags & c_flag_ephemeral) <> 0 AS is_ephemeral, NULL::UUID AS written_before
    FROM checked ch
    JOIN __SCHEMA__.wh_event_store es ON es.stream_id = ch.stream_id
    WHERE (es.flags & c_flag_compacted) = 0
    UNION ALL
    SELECT c.stream_id, c.event_type, c.is_ephemeral, c.event_id
    FROM candidate c
    JOIN checked ch ON ch.stream_id = c.stream_id
  ),
  refused AS (
    -- 228: an event whose kind differs from its stream's, in every stream the allowance does not check.
    SELECT c.event_id,
           c.stream_id,
           c.event_type,
           CASE WHEN c.is_ephemeral THEN 'ephemeral' ELSE 'Sourced' END AS event_kind,
           k.event_type AS stream_event_type,
           CASE WHEN k.is_ephemeral THEN 'ephemeral' ELSE 'Sourced' END AS stream_kind
    FROM candidate c
    JOIN stream_kind k ON k.stream_id = c.stream_id
    WHERE c.is_ephemeral <> k.is_ephemeral
      AND NOT EXISTS (SELECT 1 FROM checked ch WHERE ch.stream_id = c.stream_id)
    UNION ALL
    -- 393: in a checked stream, an event the stream holds an event of the other kind for, stored or earlier in this write,
    -- of a type not paired with it; named by the first such type. The write is judged in its own order, as if each event
    -- were stored after the ones before it. An earlier event of the write that is itself refused still counts, which can
    -- refuse more than needed in one write but never less.
    SELECT c.event_id,
           c.stream_id,
           c.event_type,
           CASE WHEN c.is_ephemeral THEN 'ephemeral' ELSE 'Sourced' END,
           other.event_type,
           CASE WHEN other.is_ephemeral THEN 'ephemeral' ELSE 'Sourced' END
    FROM candidate c
    JOIN checked ch ON ch.stream_id = c.stream_id
    CROSS JOIN LATERAL (
      SELECT p.event_type, p.is_ephemeral
      FROM present p
      WHERE p.stream_id = c.stream_id
        AND (p.written_before IS NULL OR p.written_before < c.event_id)
        AND p.is_ephemeral <> c.is_ephemeral
        AND NOT EXISTS (
          SELECT 1 FROM __SCHEMA__.wh_mixed_stream_allowances a
          WHERE a.ephemeral_type = CASE WHEN c.is_ephemeral THEN c.event_type ELSE p.event_type END
            AND a.durable_type = CASE WHEN c.is_ephemeral THEN p.event_type ELSE c.event_type END
        )
      ORDER BY p.event_type
      LIMIT 1
    ) AS other
  )
  SELECT r.event_id,
         r.stream_id,
         format(
           'WHIZ133: stream %s holds %s events (%s), so the %s event %s (event %s) was refused. '
             || 'A stream never mixes ephemeral and Sourced events: publish the ephemeral events on their own stream.',
           r.stream_id, r.stream_kind, r.stream_event_type, r.event_kind, r.event_type, r.event_id),
         jsonb_build_object(
           'stream_id', r.stream_id,
           'event_id', r.event_id,
           'event_type', r.event_type,
           'event_kind', r.event_kind,
           'stream_event_type', r.stream_event_type,
           'stream_kind', r.stream_kind)
  FROM refused r
  ORDER BY r.stream_id ASC, r.event_id ASC;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_mixed_stream_refusals(UUID[], UUID[], INTEGER[], TEXT[]) IS
  'Finds the events that would make a stream hold both ephemeral and Sourced events (WHIZ133, #1546), under the '
  'event store copy''s per-stream advisory locks, and words each refusal. A compaction origin (flags & 16) is '
  'neither kind. Locks no work table. 393 (#2255): a pair in wh_mixed_stream_allowances may share a stream; a stream '
  'naming a paired type is judged by every kind it holds, and an event is refused only for an other-kind type it is '
  'not paired with. With no pair the finder is 228''s.';

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
