-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 580_StreamsAreVersionedInEmissionOrder.sql
-- Date: 2026-11-23
-- Description: A stream's events in one stored batch are versioned in the order the producer emitted them, never by
--              id (#2551).
--
--              An id is time-ordered and never a stream position. Since #2520/#2543 a message dispatched inside a
--              handling takes an id derived from what caused it, sorting at its cause, so the effect a cause's local
--              receptor emits can sort before the cause itself. The emit chain numbered a batch's events per stream
--              with ROW_NUMBER() ORDER BY message_id, so written together the effect took version 1 and its cause
--              version 2; the sealing and mixed-stream refusals likewise judged "after a terminal event" and "the
--              stream's first event" by id.
--
--              How emission position reaches the SQL: it is the element's position in the batch the producer stores,
--              the p_messages array (jsonb_array_elements WITH ORDINALITY), taken once at the entry and carried as an
--              explicit ordinal. The producer keeps that array in emission order: each strategy flushes a stream's
--              messages in the order they were appended and no longer sorts them by id (C#, same change). No field is
--              added to the wire or to wh_outbox: the array already holds the order, an ordinal field beside it would
--              be a second source that could disagree with it, and the order only has to live for the one call that
--              stores the batch. Inside the call:
--                store_outbox_messages   inserts in its lock order (stream, then id) as before, records each
--                                        inserted event's position, and hands the refusals and the chain the
--                                        events in position order.
--                _emit_event_store_chain numbers each stream by the event's position in the id array it is handed
--                                        (unnest WITH ORDINALITY; an id listed twice keeps its first position).
--                the refusals            take their arrays in that order and judge by array position: the sealing
--                                        refusal's "after the first terminal event" (_wh_sealed_stream_refusals,
--                                        fed by _wh_refuse_sealed_stream_outbox_events, which now builds its arrays
--                                        in the chain's order), the mixed-stream refusal's "first event decides the
--                                        kind" and "earlier in the write" (_wh_mixed_stream_refusals), and the
--                                        archived refusal's report order (_wh_archived_stream_refusals).
--              commit_handler_result stores through store_outbox_messages, so a handler's commit is covered unchanged.
--
--              Cost: the same rows are read. The chain keeps the ANY probe of wh_outbox_pkey and hash-joins the
--              batch's positions to what it finds (EXPLAIN in the commit report: a bitmap scan of wh_outbox_pkey either
--              way). store_outbox_messages adds one in-memory sort of the arrays it already holds, only when it
--              inserted more than one event. Every other function is copied whole from its last word with only the
--              ordering delta.
--
--              Number 580: reserved for this change (580 to 584).
--
-- Reversal: reversible (the down file restores each function's previous last word, which versions by id)
-- Dependencies: 567 (_emit_event_store_chain, store_outbox_messages), 266 (_wh_sealed_stream_refusals,
--               _wh_refuse_sealed_stream_outbox_events), 393 (_wh_mixed_stream_refusals), 280 (_wh_archived_stream_refusals)
-- Objects: _wh_sealed_stream_refusals, _wh_refuse_sealed_stream_outbox_events, _wh_mixed_stream_refusals,
--          _wh_archived_stream_refusals, _emit_event_store_chain, store_outbox_messages
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- ---------------------------------------------------------------------------------------------
-- _wh_sealed_stream_refusals: copied from its last word (266), with the 580 delta: emission position, never id.
-- ---------------------------------------------------------------------------------------------
-- <docs>fundamentals/events/stream-end-of-life#a-sealed-stream-refuses-appends</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:OutboxAppend_ToASealedStream_DeadLettersThatEvent_AndTheRestOfTheBatchCommitsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:OutboxBatch_EventsAfterATerminalEvent_AreDeadLettered_AndTheOnesBeforeItAreStoredAsync</tests>
-- <docs>fundamentals/event-store/version-ordering#emission-order</docs>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_sealed_stream_refusals(
  p_event_ids UUID[],
  p_stream_ids UUID[],
  p_flags INTEGER[],
  p_event_types TEXT[],
  p_now TIMESTAMPTZ
) RETURNS TABLE(
  refused_event_id UUID,
  refused_stream_id UUID,
  refusal_text TEXT
) AS $$
DECLARE
  c_flag_terminal CONSTANT INTEGER := 64;          -- EventFlags.Terminal (1 << 6)
  c_flags_off_the_ledger CONSTANT INTEGER := 24;   -- ephemeral (8) or a compaction origin (16): no row to seal (260)
BEGIN
  RETURN QUERY
  -- 580 (#2551): an event's place in the write is its position in the arrays, which list the write in the order its
  -- events were emitted. "After a terminal event" is after it in that order, never by id: an id is time-ordered and
  -- says nothing about stream position.
  WITH candidate AS (
    SELECT c.event_id, c.stream_id, COALESCE(c.flags, 0) AS flags, c.event_type, c.position
    FROM unnest(p_event_ids, p_stream_ids, p_flags, p_event_types)
           WITH ORDINALITY AS c(event_id, stream_id, flags, event_type, position)
    WHERE c.stream_id IS NOT NULL
      AND c.stream_id <> __EMPTY_UUID__::uuid
  ),
  ending AS (
    SELECT DISTINCT ON (k.stream_id)
           k.stream_id,
           k.event_id,
           k.position,
           __SCHEMA__._wh_terminal_seal_reason(split_part(__SCHEMA__.normalize_event_type(k.event_type), ',', 1), k.event_id) AS reason
    FROM candidate k
    WHERE k.flags & c_flag_terminal <> 0
      AND k.flags & c_flags_off_the_ledger = 0
    ORDER BY k.stream_id, k.position
  ),
  sealed AS (
    SELECT l.stream_id, l.sealed_at, l.sealed_reason
    FROM __SCHEMA__.wh_stream_lifecycle l
    WHERE l.stream_id IN (SELECT k.stream_id FROM candidate k)
      AND l.sealed_at IS NOT NULL
  )
  SELECT k.event_id,
         k.stream_id,
         __SCHEMA__._wh_stream_sealed_refusal_text(k.stream_id, COALESCE(s.sealed_at, p_now), COALESCE(s.sealed_reason, e.reason))
  FROM candidate k
  LEFT JOIN sealed s ON s.stream_id = k.stream_id
  LEFT JOIN ending e ON e.stream_id = k.stream_id
  WHERE s.stream_id IS NOT NULL
     OR k.position > e.position
  ORDER BY k.position;
END;
$$ LANGUAGE plpgsql STABLE;

COMMENT ON FUNCTION __SCHEMA__._wh_sealed_stream_refusals(UUID[], UUID[], INTEGER[], TEXT[], TIMESTAMPTZ) IS
  'Finds the events of a write that a sealed stream refuses (#1733): those of a stream whose wh_stream_lifecycle row is '
  'sealed, and those after the first terminal event of their stream in the same write. Reads only; the caller holds the '
  'stream locks. 580 (#2551): "first" and "after" are by position in the arrays, the order the write was emitted in, '
  'never by id.';

-- ---------------------------------------------------------------------------------------------
-- _wh_refuse_sealed_stream_outbox_events: copied from its last word (266), with the 580 delta: emission position, never id.
-- ---------------------------------------------------------------------------------------------
-- <docs>fundamentals/events/stream-end-of-life#a-sealed-stream-refuses-appends</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:OutboxAppend_ToASealedStream_DeadLettersThatEvent_AndTheRestOfTheBatchCommitsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:StoreOutboxMessages_ToASealedStream_DeadLettersTheEvent_ThroughTheCoordinatorAsync</tests>
-- <docs>fundamentals/event-store/version-ordering#emission-order</docs>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_refuse_sealed_stream_outbox_events(
  p_outbox_message_ids UUID[],
  p_instance_id UUID,
  p_now TIMESTAMPTZ
) RETURNS INTEGER AS $$
DECLARE
  c_failure_reason CONSTANT INTEGER := 25;    -- MessageFailureReason.StreamSealed
  -- The dead-letter generation of a refusal made here, as 228 records it: the reason is held for review (a sealed stream
  -- refuses the same event every time), so generation replay never re-drives it.
  c_generation CONSTANT TEXT := 'whizbang/event-store-copy';
  v_ids UUID[];
  v_streams UUID[];
  v_flags INTEGER[];
  v_types TEXT[];
  v_refusal RECORD;
  v_count INTEGER := 0;
BEGIN
  -- 580 (#2551): the arrays list the batch in the order the chain was handed it, the order its events were emitted, so
  -- the refusal judges "after a terminal event" by emission position. An id listed twice keeps its first position.
  SELECT array_agg(o.message_id ORDER BY b.position), array_agg(o.stream_id ORDER BY b.position),
         array_agg(o.flags ORDER BY b.position), array_agg(o.message_type ORDER BY b.position)
    INTO v_ids, v_streams, v_flags, v_types
  FROM (
    SELECT listed.message_id, min(listed.position) AS position
    FROM unnest(p_outbox_message_ids) WITH ORDINALITY AS listed(message_id, position)
    GROUP BY listed.message_id
  ) AS b
  JOIN __SCHEMA__.wh_outbox o ON o.message_id = b.message_id
  WHERE o.message_id = ANY(p_outbox_message_ids)
    AND o.is_event = true
    AND o.stream_id IS NOT NULL;

  FOR v_refusal IN
    SELECT r.refused_event_id, r.refusal_text
    FROM __SCHEMA__._wh_sealed_stream_refusals(v_ids, v_streams, v_flags, v_types, p_now) r
  LOOP
    PERFORM __SCHEMA__.move_to_dead_letters(
      gen_random_uuid(), 'wh_outbox', v_refusal.refused_event_id, c_failure_reason, v_refusal.refusal_text,
      p_instance_id, c_generation);
    v_count := v_count + 1;
  END LOOP;

  RETURN v_count;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_refuse_sealed_stream_outbox_events(UUID[], UUID, TIMESTAMPTZ) IS
  'Moves each outbox event of the batch that a sealed stream refuses to wh_dead_letters (reason 25, the refusal as its '
  'error text), so the outbox emit chain stores the rest (#1733). Returns the events refused.';

-- ---------------------------------------------------------------------------------------------
-- _wh_mixed_stream_refusals: copied from its last word (393), with the 580 delta: emission position, never id.
-- ---------------------------------------------------------------------------------------------
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
-- <docs>fundamentals/event-store/version-ordering#emission-order</docs>
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
  -- 580 (#2551): an event's place in the write is its position in the arrays, which list the write in the order its
  -- events were emitted; the stream is versioned in that order, so "first" and "earlier" are by position, never by id.
  WITH candidate AS (
    SELECT c.event_id,
           c.stream_id,
           (COALESCE(c.flags, 0) & c_flag_ephemeral) <> 0 AS is_ephemeral,
           __SCHEMA__.normalize_event_type(c.event_type) AS event_type,
           c.position
    FROM unnest(p_event_ids, p_stream_ids, p_flags, p_event_types)
           WITH ORDINALITY AS c(event_id, stream_id, flags, event_type, position)
    WHERE c.stream_id IS NOT NULL
      AND (COALESCE(c.flags, 0) & c_flag_compacted) = 0
  ),
  -- Each stream's first event in this write (version order is emission order, 580), one sort over the write.
  opener AS (
    SELECT DISTINCT ON (candidate.stream_id) candidate.stream_id, candidate.is_ephemeral, candidate.event_type
    FROM candidate
    ORDER BY candidate.stream_id, candidate.position
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
  -- its position in the write (version order is emission order, 580). A compaction origin is neither kind.
  present AS (
    SELECT DISTINCT es.stream_id, es.event_type, (es.flags & c_flag_ephemeral) <> 0 AS is_ephemeral, NULL::BIGINT AS written_before
    FROM checked ch
    JOIN __SCHEMA__.wh_event_store es ON es.stream_id = ch.stream_id
    WHERE (es.flags & c_flag_compacted) = 0
    UNION ALL
    SELECT c.stream_id, c.event_type, c.is_ephemeral, c.position
    FROM candidate c
    JOIN checked ch ON ch.stream_id = c.stream_id
  ),
  refused AS (
    -- 228: an event whose kind differs from its stream's, in every stream the allowance does not check.
    SELECT c.event_id,
           c.stream_id,
           c.position,
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
           c.position,
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
        AND (p.written_before IS NULL OR p.written_before < c.position)
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
  ORDER BY r.position ASC;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_mixed_stream_refusals(UUID[], UUID[], INTEGER[], TEXT[]) IS
  'Finds the events that would make a stream hold both ephemeral and Sourced events (WHIZ133, #1546), under the '
  'event store copy''s per-stream advisory locks, and words each refusal. A compaction origin (flags & 16) is '
  'neither kind. Locks no work table. 393 (#2255): a pair in wh_mixed_stream_allowances may share a stream; a stream '
  'naming a paired type is judged by every kind it holds, and an event is refused only for an other-kind type it is '
  'not paired with. With no pair the finder is 228''s. 580 (#2551): a stream''s first event, and an event earlier in the '
  'write, are by position in the arrays, the order the write was emitted in, never by id.';

-- ---------------------------------------------------------------------------------------------
-- _wh_archived_stream_refusals: copied from its last word (280), with the 580 delta: emission position, never id.
-- ---------------------------------------------------------------------------------------------
-- <docs>fundamentals/events/archive-store#appending-to-an-archived-stream</docs>
-- <docs>fundamentals/events/archive-store#new-generation</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveAppendPolicySqlTests.cs:OutboxAppend_Refuse_DeadLettersThatEvent_AndTheRestOfTheBatchCommitsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveAppendPolicySqlTests.cs:OutboxAppend_AutoRestore_DeadLettersAwaitingRestore_AndQueuesOneRequestPerStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveNewGenerationSqlTests.cs:OutboxAppend_AParkedCursorWithoutASnapshotAtIt_IsDeadLetteredAwaitingRestoreAsync</tests>
-- <docs>fundamentals/event-store/version-ordering#emission-order</docs>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_archived_stream_refusals(p_event_ids UUID[], p_stream_ids UUID[])
RETURNS TABLE(refused_event_id UUID, refused_stream_id UUID, append_policy TEXT, refusal_text TEXT) AS $$
BEGIN
  RETURN QUERY
  WITH policy AS (
    SELECT c.event_id, c.stream_id, c.position, l.last_version,
           CASE WHEN p.append_to_archived = 'NewGeneration' THEN 'AutoRestore'
                ELSE COALESCE(p.append_to_archived, 'Refuse') END AS append_policy
    FROM unnest(p_event_ids, p_stream_ids) WITH ORDINALITY AS c(event_id, stream_id, position)
    JOIN __SCHEMA__.wh_stream_lifecycle l ON l.stream_id = c.stream_id
    LEFT JOIN __SCHEMA__.wh_stream_type_policies p ON p.stream_type = l.stream_type
    WHERE l.state = 'Archived'
      AND l.sealed_at IS NULL
      AND c.stream_id NOT IN (SELECT g.new_generation_stream_id FROM __SCHEMA__._wh_new_generation_streams(p_stream_ids) g)
  )
  SELECT pl.event_id,
         pl.stream_id,
         pl.append_policy,
         __SCHEMA__._wh_stream_archived_refusal_text(pl.stream_id, pl.last_version, pl.append_policy)
  FROM policy pl
  -- 580 (#2551): reported in the order the write lists them, never by id; a refusal here does not depend on order.
  ORDER BY pl.position;
END;
$$ LANGUAGE plpgsql STABLE;

COMMENT ON FUNCTION __SCHEMA__._wh_archived_stream_refusals(UUID[], UUID[]) IS
  'Finds the events of a write that an archived (and unsealed) stream refuses, with the AppendToArchived policy of the '
  'stream''s type (wh_stream_type_policies; Refuse when the type has no row) and the refusal''s wording (#1738). Reads only. '
  '280: a stream the write starts a new generation of is not refused, and NewGeneration without a snapshot at every parked '
  'cursor reads AutoRestore (#1734).';

-- ---------------------------------------------------------------------------------------------
-- _emit_event_store_chain: copied from its last word (567), with the 580 delta: emission position, never id.
-- ---------------------------------------------------------------------------------------------
-- <docs>fundamentals/events/stream-end-of-life#the-stream-lifecycle-ledger</docs>
-- <docs>fundamentals/messaging/collective-events#only-handled-types-are-queued</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CollectiveRowGateSqlTests.cs:Outbox_OnceTheMarkerExists_AHandledTypeGetsItsRow_AndAnUnhandledTypeGetsNoneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CollectiveRowGateSqlTests.cs:Outbox_WithoutAGateMarker_EveryCollectiveEventGetsItsRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EmitEventStoreChainCollectiveSqlTests.cs:EmitEventStoreChain_CollectiveEvent_CreatesSingleCollectiveSinkRowAsync</tests>
-- <docs>fundamentals/events/stream-end-of-life#a-sealed-stream-refuses-appends</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:OutboxAppend_CreatesTheRow_FromTheStreamsFirstEventAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:Append_WritesOneLedgerRowPerStreamPerBatch_NotOnePerEventAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:AppendWithinTheHour_IsAHeapOnlyUpdate_WhileAnIndexedColumnChangeIsNotAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:Bucket_AdvancesOnlyWhenTheHourChanges_AndAnEarlierClockNeverMovesItBackAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:ConcurrentBatchesOnDifferentStreams_NeitherWaitsForTheOtherAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AuditDigestLaneSqlTests.cs:Reconciliation_AuditTraffic_OriginAndSubscriberAnswerTheSameAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:TerminalEvent_OnTheOutbox_SealsTheStream_InTheTransactionThatStoresItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:OutboxAppend_ToASealedStream_DeadLettersThatEvent_AndTheRestOfTheBatchCommitsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:OutboxBatch_EventsAfterATerminalEvent_AreDeadLettered_AndTheOnesBeforeItAreStoredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:TerminalFlag_OnAnEphemeralEvent_SealsNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Performance/StreamSealingEmitCostScenarioTests.cs:EmitChain_WithAndWithoutTheSealCheck_ReportAsync</tests>
-- <docs>fundamentals/events/archive-store#appending-to-an-archived-stream</docs>
-- <docs>fundamentals/events/superseding-events#the-key</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Emit_Outbox_StampsTheKeyOnlyOnASupersedingEvent_AndQueuesTheOlderOneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Emit_TwoEventsOfOneKeyInOneBatch_QueueTheOlderAsync</tests>
-- <docs>fundamentals/events/superseding-events#superseding-groups</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingGroupSqlTests.cs:Emit_ANewerEventOfAnotherTypeInTheGroup_QueuesTheOlder_InEitherOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveAppendPolicySqlTests.cs:OutboxAppend_Refuse_DeadLettersThatEvent_AndTheRestOfTheBatchCommitsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveAppendPolicySqlTests.cs:OutboxAppend_AutoRestore_DeadLettersAwaitingRestore_AndQueuesOneRequestPerStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Performance/ArchivedStreamEmitCostScenarioTests.cs:EmitChain_With266sProbeAndWith271s_ReportAsync</tests>
-- <docs>data/event-store#stored-metadata</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataWriteSqlTests.cs:OutboxChain_DefaultForm_StoresTheSlimRow_AndStagesTheFullFormAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataWriteSqlTests.cs:FullListedType_KeepsTheFullRow_ByTypeAndByNamespaceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataWriteSqlTests.cs:DebugMode_KeepsEveryRowFullAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataWriteSqlTests.cs:WriteFormFull_KeepsFullRows_AndStagesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataWriteSqlTests.cs:WriteFormSlim_StoresSlimRows_AndStagesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Performance/StoredMetadataEmitCostScenarioTests.cs:EmitChain_FullAgainstLinked_ReportAsync</tests>
-- <docs>fundamentals/events/archive-store#new-generation</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveNewGenerationSqlTests.cs:OutboxAppend_StoresTheEventAtTheNextVersion_AndDeadLettersNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveNewGenerationSqlTests.cs:OutboxAppend_AParkedCursorWithoutASnapshotAtIt_IsDeadLetteredAwaitingRestoreAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveNewGenerationSqlTests.cs:StoreOutboxMessages_AnAwaitedWrite_IsStoredAtTheNextVersionAsync</tests>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestWritersTests.cs:OutboxEmitChain_FoldsEveryLaneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestWritersTests.cs:OutboxEmitChain_FoldingIntoARowWithoutIdLanes_KeepsThemNullAsync</tests>
-- <docs>fundamentals/events/superseding-events#the-digest-counts-the-key</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingCountsTheKeySqlTests.cs:Emit_CopiesOfAKey_AreCountedOnce_AsTheKeysSlotAsync</tests>
-- <docs>data/event-store#row-values</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRowValuesSqlTests.cs:OutboxChain_LeavesOutTheMessageIdAndTheHopScopeTheRowHolds_AndNothingElseAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRowValuesSqlTests.cs:OutboxChain_KeepsWhatTheRowDoesNotHoldAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRowValuesSqlTests.cs:FullRows_KeepEverythingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRowValuesReadTests.cs:EveryReader_ReadsATrimmedRow_AsItReadTheSameRowInTheSlimFormAsync</tests>
-- <docs>fundamentals/work-coordinator/partition-assignment#ownership-by-map</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipByMapStoreSqlTests.cs:EmitChain_InMapMode_LeasesByPartition_NeverToTheStoredOwnerAsync</tests>
-- <docs>fundamentals/event-store/version-ordering#emission-order</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EmissionOrderVersioningSqlTests.cs:EmitChain_EventsListedAgainstTheirIdOrder_AreVersionedInTheOrderListedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EmissionOrderVersioningSqlTests.cs:EmitChain_AnIdListedTwice_KeepsItsFirstPositionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EventStoreVersionOrderingSqlTests.cs:EmitChain_BatchedShuffledMessageIds_InsertsInTheBatchsOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EventStoreVersionOrderingSqlTests.cs:EmitEventStoreChain_AssignsVersions_InTheOrderTheBatchListsThem_NotByIdOrCreatedAtAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._emit_event_store_chain(
  p_outbox_message_ids UUID[],
  p_instance_id UUID,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_partition_count INTEGER DEFAULT 10000
) RETURNS INTEGER AS $$
DECLARE
  v_stored_event_ids UUID[];
  v_count INTEGER;
  c_field_message_id CONSTANT TEXT := __ENVELOPE_FIELD_MESSAGE_ID__;  -- NOSONAR S1192: the field name recurs per function; PL/pgSQL has no file-level constants
  c_field_hops CONSTANT TEXT := 'Hops';
  c_source_perspective CONSTANT TEXT := __CATEGORY_PERSPECTIVE__;
  -- Migration 061: collective routing sink + flag bit (EventFlags.Collective = 1 << 0).
  c_collective_sink CONSTANT TEXT := '__collective__';
  c_flag_collective CONSTANT INTEGER := 1;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  -- 260: EventFlags.Ephemeral (1 << 3) | EventFlags.Compacted (1 << 4), the events the lifecycle ledger does not count.
  c_flags_off_the_ledger CONSTANT INTEGER := 24;
  c_flag_terminal CONSTANT INTEGER := 64;  -- 266: EventFlags.Terminal (1 << 6)
  c_flag_superseding CONSTANT INTEGER := 128;  -- 295: EventFlags.Superseding (1 << 7)
  v_superseding_ids UUID[];                -- 295: the stored events that carry a superseding key
  v_streams UUID[];                        -- 266: the batch's streams, in lock order
  v_ends_a_stream BOOLEAN;                 -- 266: the batch carries a terminal event
  v_any_sealed BOOLEAN;                    -- 271: a stream of the batch is sealed
  v_any_archived BOOLEAN;                  -- 271: a stream of the batch is archived (and not sealed)
  v_write_form TEXT;                       -- 285: Full, Slim or Linked
  v_full_for TEXT[];                       -- 285: the type names and namespaces kept full
  v_trim BOOLEAN;                          -- 537: the trim gate is open (wh_settings metadata_trim_row_values)
  v_keep_all BOOLEAN;                      -- 285: debug mode or the Full form keeps every row full
  -- 566 (#2554, #2555): the service's switch, read once: on, the partition ownership table alone decides.
  v_map BOOLEAN := __SCHEMA__.wh_partition_ownership_by_map();
BEGIN
  IF p_outbox_message_ids IS NULL OR cardinality(p_outbox_message_ids) = 0 THEN
    RETURN 0;
  END IF;

  -- Per-stream advisory locks. Without these, two concurrent transactions can both
  -- read MAX(version)=N from wh_event_store and both attempt INSERT at version=N+1,
  -- violating idx_event_store_stream UNIQUE(stream_id, version) (PG error 23505). The
  -- legacy process_work_batch ran serially through one ProcessWorkBatchAsync call so
  -- the race didn't exist; the new path lets parallel handlers / strategy flushes
  -- target the same stream concurrently.
  --
  -- Lock order is the stream id, sorted ascending, which keeps the nesting deadlock-free between
  -- any pair of transactions touching overlapping stream sets.
  -- pg_advisory_xact_lock auto-releases at commit/rollback.
  -- 266 (#1733): the read that finds the batch's streams also notes whether the batch ends one, so the sealing check
  -- below costs no second read of the batch.
  SELECT array_agg(b.sid ORDER BY b.sid), COALESCE(bool_or(b.ends_stream), false)
    INTO v_streams, v_ends_a_stream
  FROM (
    SELECT o.stream_id AS sid,
           bool_or(COALESCE(o.flags, 0) & c_flag_terminal <> 0
                   AND COALESCE(o.flags, 0) & c_flags_off_the_ledger = 0) AS ends_stream
    FROM __SCHEMA__.wh_outbox o
    WHERE o.message_id = ANY(p_outbox_message_ids)
      AND o.is_event = true
      AND o.stream_id IS NOT NULL
    GROUP BY o.stream_id
  ) AS b;

  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || streams_to_lock.sid::text))
  FROM (
    SELECT u.sid FROM unnest(v_streams) AS u(sid) ORDER BY u.sid
  ) AS streams_to_lock;

  -- 266 (#1733): a sealed stream refuses every append. The refused event alone is dead-lettered, with the refusal as
  -- its error text, and the copy below stores the rest of the batch; a raise here would fail every stream in the batch
  -- for one sealed stream. Under the locks taken above, so a seal cannot land between this check and the copy.
  -- 271 (#1738): so does an archived stream, by its type's AppendToArchived policy, after the seal (a sealed stream's
  -- refusal is the seal's). One probe per stream of idx_stream_lifecycle_closed, which holds only the sealed and the
  -- archived streams, answers both, so the common case (none of either, no terminal event) costs what 266's probe cost.
  SELECT COALESCE(bool_or(l.sealed_at IS NOT NULL), false),
         COALESCE(bool_or(l.sealed_at IS NULL AND l.state = 'Archived'), false)
    INTO v_any_sealed, v_any_archived
  FROM __SCHEMA__.wh_stream_lifecycle l
  WHERE l.stream_id = ANY(v_streams)
    AND (l.sealed_at IS NOT NULL OR l.state = 'Archived');
  IF v_ends_a_stream OR v_any_sealed THEN
    PERFORM __SCHEMA__._wh_refuse_sealed_stream_outbox_events(p_outbox_message_ids, p_instance_id, p_now);
  END IF;
  IF v_any_archived THEN
    PERFORM __SCHEMA__._wh_refuse_archived_stream_outbox_events(p_outbox_message_ids, p_instance_id, p_now);
    -- 296 (#1734): the archived streams the refusal left alone start their new generation.
    PERFORM __SCHEMA__._wh_start_new_generations(v_streams);
  END IF;

  -- 285 (#1725): the stored-metadata settings, read once for the whole batch.
  SELECT s.write_form, s.full_for, s.keep_all INTO v_write_form, v_full_for, v_keep_all
  FROM __SCHEMA__._wh_metadata_write_settings() s;
  -- 537 (#2323): the trim gate, read once for the whole batch, beside the settings.
  v_trim := __SCHEMA__._wh_metadata_trim_on();

  -- Phase 4.5A-equivalent: store outbox events into wh_event_store with sequential versioning.
  -- 580 (#2551): a stream's events are numbered in the order the batch lists them, which is the order the producer
  -- emitted them (store_outbox_messages hands them over in that order), never by id. An id is time-ordered and says
  -- nothing about stream position: a derived id sorts at its cause, so an effect derived from a cause can sort before
  -- it. An id listed twice keeps its first position.
  -- Phase H step 10 slice 3: version is computed via a correlated subquery rather than a
  -- pre-materialized CTE. Inside the per-stream advisory lock the values are equivalent, but
  -- the per-row form is defensive, if a future refactor weakens or bypasses the lock, the
  -- per-row MAX read still picks up any concurrent commits.
  -- Migration 061: o.flags carried into wh_event_store.flags (was dropped here previously).
  WITH outbox_events AS (
    SELECT
      o.message_id,
      o.stream_id,
      o.message_type,
      o.event_data,
      o.metadata,
      o.scope,
      o.flags,
      o.created_at,
      ROW_NUMBER() OVER (PARTITION BY o.stream_id ORDER BY batch.position) AS row_num
    FROM (
      SELECT listed.message_id, min(listed.position) AS position
      FROM unnest(p_outbox_message_ids) WITH ORDINALITY AS listed(message_id, position)
      GROUP BY listed.message_id
    ) AS batch
    JOIN __SCHEMA__.wh_outbox o ON o.message_id = batch.message_id
    WHERE o.message_id = ANY(p_outbox_message_ids)
      AND o.is_event = true
      AND o.stream_id IS NOT NULL
  ),
  -- Migration 072: materialize the extracted payload + built metadata ONCE, so the pointer INSERT
  -- and the ephemeral body offload read identical values without recomputing the JSON extraction.
  computed_full AS (
    SELECT
      oe.message_id,
      oe.stream_id,
      SPLIT_PART(__SCHEMA__.normalize_event_type(oe.message_type), ',', 1) AS aggregate_type,
      __SCHEMA__.normalize_event_type(oe.message_type) AS event_type,
      COALESCE(oe.event_data::jsonb -> 'p', oe.event_data::jsonb -> 'Payload', oe.event_data::jsonb -> 'payload') AS body_data,
      COALESCE(oe.event_data::jsonb -> 'h', oe.event_data::jsonb -> c_field_hops, oe.event_data::jsonb -> 'hops', '[]'::jsonb) AS hops,
      jsonb_build_object(
        c_field_message_id, COALESCE(oe.event_data::jsonb -> 'id', oe.event_data::jsonb -> c_field_message_id, oe.event_data::jsonb -> 'messageId'),
        c_field_hops, COALESCE(oe.event_data::jsonb -> 'h', oe.event_data::jsonb -> c_field_hops, oe.event_data::jsonb -> 'hops', '[]'::jsonb)
      ) || CASE
        WHEN oe.metadata IS NOT NULL
             AND jsonb_typeof(oe.metadata::jsonb -> 'ett') = 'number'
        THEN jsonb_build_object('ephemeral_expires_at',
               p_now + ((oe.metadata::jsonb ->> 'ett')::int * INTERVAL '1 second'))
        ELSE '{}'::jsonb
      END AS full_meta,
      oe.scope,
      oe.row_num,
      oe.flags,
      -- 295 (#1729): a superseding event's key, from the metadata the dispatch or receive path stamped it in. Only
      -- a row carrying the flag gets one, so a stray 'sk' on another event is never read.
      CASE WHEN COALESCE(oe.flags, 0) & c_flag_superseding <> 0 THEN oe.metadata::jsonb ->> 'sk' END AS supersede_key,
      -- 352 (#2124): and its superseding group, beside the key, on a flagged row only.
      CASE WHEN COALESCE(oe.flags, 0) & c_flag_superseding <> 0 THEN oe.metadata::jsonb ->> 'sg' END AS supersede_group
    FROM outbox_events oe
  ),
  -- 285 (#1725): each event's route id, and the metadata its row keeps: the full form (plus the route id) under debug
  -- mode, under the Full form or for a listed type, otherwise the slim form. Pure CPU on values already in hand.
  -- Materialized, so each event's route id is computed once however many times the next step reads it.
  computed_routes AS MATERIALIZED (
    SELECT cf.*, __SCHEMA__._wh_route_id(split_part(cf.event_type, ',', 1), cf.hops) AS route_id
    FROM computed_full cf
  ),
  computed AS (
    SELECT cr.*,
      CASE
        WHEN v_keep_all OR __SCHEMA__._wh_metadata_kept_full(split_part(cr.event_type, ',', 1), v_full_for)
        THEN cr.full_meta || jsonb_build_object('rid', cr.route_id)
        -- 537 (#2323): once the trim gate is open, less what the row holds in its own columns, its event id and scope.
        WHEN v_trim THEN __SCHEMA__._wh_trim_row_values(
               __SCHEMA__._wh_slim_event_metadata(cr.full_meta, cr.route_id), cr.message_id, cr.scope::jsonb)
        ELSE __SCHEMA__._wh_slim_event_metadata(cr.full_meta, cr.route_id)
      END AS body_meta
    FROM computed_routes cr
  ),
  stored_events AS (
    INSERT INTO __SCHEMA__.wh_event_store (
      event_id, stream_id, aggregate_id, aggregate_type, event_type,
      scope, version, created_at, flags, supersede_key, supersede_group
    )
    SELECT
      c.message_id,
      c.stream_id,
      c.stream_id,
      c.aggregate_type,
      c.event_type,
      c.scope,
      -- 296 (#1734): a stream with nothing hot in a batch that held an archived stream may be a new generation, which
      -- continues from its ledger row's last version; any other batch reads nothing more.
      COALESCE((SELECT MAX(es.version) FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = c.stream_id),
               CASE WHEN v_any_archived THEN
                 (SELECT l.last_version FROM __SCHEMA__.wh_stream_lifecycle l WHERE l.stream_id = c.stream_id)
               END,
               0) + c.row_num,
      p_now,
      c.flags,
      c.supersede_key,
      c.supersede_group
    FROM computed c
    -- Phase H step 10 slice 4: DO NOTHING with NO constraint specifier so PG handles BOTH the
    -- event_id PK conflict (idempotent re-store) AND the idx_event_store_stream (stream_id, version)
    -- UNIQUE conflict gracefully. Conflicting rows are silently skipped; the next claim_work cycle
    -- re-attempts them with a fresh MAX(version) snapshot.
    ON CONFLICT DO NOTHING
    RETURNING event_id, stream_id, version, supersede_key
  ),
  -- Migration 077 (full split): offload EVERY body. Joined to stored_events so only events actually
  -- stored this call get a body row; ON CONFLICT keeps re-store idempotent.
  stored_bodies AS (
    INSERT INTO __SCHEMA__.wh_event_body (event_id, event_data, metadata)
    SELECT c.message_id, c.body_data, c.body_meta
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    -- Constraint-LESS form (event_id PK is wh_event_body's only constraint, so semantics are
    -- identical), keeps the emit-chain source free of constraint-specific ON CONFLICT forms,
    -- which the version-ordering regression lock forbids (a specific-constraint form on
    -- wh_event_store once let idx_event_store_stream conflicts bubble up as PG 23505).
    ON CONFLICT DO NOTHING
    RETURNING event_id
  ),
  -- 285 (#1725): under the Linked form, the full form is appended to the staging table for maintenance to distill into
  -- routes and traces. Append-only: the key is new for every stored event, so there is nothing to check.
  staged_metadata AS (
    INSERT INTO __SCHEMA__.wh_event_metadata_staging (event_id, recorded_at, route_id, event_type, metadata)
    SELECT c.message_id, p_now, c.route_id, split_part(c.event_type, ',', 1), c.full_meta
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    WHERE v_write_form = 'Linked'
  ),
  -- Migration 087 (A1c): incrementally fold the just-stored events into wh_stream_digests.
  -- Bucket + predicates mirror ComputeStreamDigestsAsync (the full-sweep recompute) exactly:
  -- ephemeral (flags & 8) and at-most-once occurrences are excluded; XOR is self-inverse, so
  -- ON CONFLICT folds new hashes in by XOR. Joined to stored_events so an idempotent re-store
  -- (ON CONFLICT DO NOTHING above) never double-folds. Bucket conflicts across concurrent
  -- transactions are impossible here: the bucket key contains stream_id and the per-stream
  -- advisory locks serialize same-stream emits; ORDER BY is belt-and-suspenders lock ordering.
  -- The zero-uuid origin bucket = locally-originated events; a non-zero origin = events
  -- received FROM that origin (inbox flavor only).
  digest_folds AS (
    -- 336 (#2106): every lane, through the one fold helper. Referenced by the statement's final SELECT, so it
    -- always runs (a CTE that writes nothing runs only when read).
    SELECT __SCHEMA__._wh_fold_stream_digests(
      array_agg(__EMPTY_UUID__::uuid), array_agg(COALESCE(c.scope::jsonb ->> 't', '')), array_agg(c.event_type),
      array_agg(c.stream_id),
      -- 461 (#2364): a superseding copy counts as its key's slot, once: only when the store holds no copy of its key yet,
      -- and only the first such copy of this batch. The slot is passed already: the rows this statement stores are not
      -- visible to it, so the helper could not read them back.
      array_agg(CASE WHEN c.supersede_key IS NULL THEN c.message_id
                     ELSE __SCHEMA__._wh_slot_id(c.stream_id, COALESCE(c.supersede_group, c.event_type), c.supersede_key) END),
      'fold', p_now, TRUE) AS folded
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    WHERE __DIGEST_ELIGIBLE__(c)
      AND (c.supersede_key IS NULL
           OR (NOT EXISTS (
                 SELECT 1 FROM __SCHEMA__.wh_event_store o
                 WHERE o.stream_id = c.stream_id
                   AND COALESCE(o.supersede_group, o.event_type) = COALESCE(c.supersede_group, c.event_type)
                   AND o.supersede_key = c.supersede_key)
               AND NOT EXISTS (
                 SELECT 1 FROM computed c2
                 JOIN stored_events se2 ON se2.event_id = c2.message_id
                 WHERE c2.stream_id = c.stream_id
                   AND COALESCE(c2.supersede_group, c2.event_type) = COALESCE(c.supersede_group, c.event_type)
                   AND c2.supersede_key = c.supersede_key
                   AND se2.version < se.version)))
  ),
  -- 260 (#1732): the stream lifecycle ledger. One row per stream this call stored a Sourced event on, in the same
  -- statement as the copy: one set-based upsert per batch, grouped by stream (never a write per event), in stream id
  -- order under the per-stream advisory locks taken above, so it can wait only on a batch that holds the same stream.
  -- An event that is ephemeral or a compaction origin (flags & 24) does not count, and neither does a stream-less event:
  -- an ephemeral stream is reaped by its time to live and never reaches end of life. The update touches no indexed
  -- column (last_version, approx_bytes, and a bucket that changes only when the UTC hour does), so it stays heap-only.
  -- 266 (#1733): a terminal event among them seals the stream in this statement, so the event and the seal commit
  -- together; every other append writes state, sealed_at and sealed_reason back unchanged, which keeps it heap-only.
  lifecycle_upsert AS (
    INSERT INTO __SCHEMA__.wh_stream_lifecycle AS l
      (stream_id, stream_type, tenant, first_appended_at, last_appended_bucket, last_version, approx_bytes,
       state, sealed_at, sealed_reason)
    SELECT
      se.stream_id,
      (array_agg(c.aggregate_type ORDER BY se.version))[1],
      (array_agg(c.scope::jsonb ->> 't' ORDER BY se.version))[1],
      p_now,
      date_trunc('hour', p_now, 'UTC'),
      MAX(se.version),
      SUM(COALESCE(pg_column_size(c.body_data), 0) + COALESCE(pg_column_size(c.body_meta), 0)),
      CASE WHEN bool_or(COALESCE(c.flags, 0) & c_flag_terminal <> 0) THEN 'Sealed' ELSE 'Active' END,
      CASE WHEN bool_or(COALESCE(c.flags, 0) & c_flag_terminal <> 0) THEN p_now END,
      (array_agg(__SCHEMA__._wh_terminal_seal_reason(c.aggregate_type, c.message_id) ORDER BY se.version)
         FILTER (WHERE COALESCE(c.flags, 0) & c_flag_terminal <> 0))[1]
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    WHERE COALESCE(c.flags, 0) & c_flags_off_the_ledger = 0
      AND se.stream_id <> __EMPTY_UUID__::uuid
    GROUP BY se.stream_id
    ORDER BY se.stream_id
    ON CONFLICT (stream_id) DO UPDATE SET
      last_version = GREATEST(l.last_version, EXCLUDED.last_version),
      approx_bytes = l.approx_bytes + EXCLUDED.approx_bytes,
      last_appended_bucket = GREATEST(l.last_appended_bucket, EXCLUDED.last_appended_bucket),
      state = CASE WHEN EXCLUDED.sealed_at IS NOT NULL AND l.sealed_at IS NULL AND l.state = 'Active'
                   THEN 'Sealed' ELSE l.state END,
      sealed_reason = CASE WHEN l.sealed_at IS NULL THEN EXCLUDED.sealed_reason ELSE l.sealed_reason END,
      sealed_at = COALESCE(l.sealed_at, EXCLUDED.sealed_at)
  )
  SELECT array_agg(event_id), array_agg(event_id) FILTER (WHERE supersede_key IS NOT NULL)
    INTO v_stored_event_ids, v_superseding_ids
  FROM stored_events CROSS JOIN digest_folds;
  v_stored_event_ids := COALESCE(v_stored_event_ids, '{}');
  v_count := cardinality(v_stored_event_ids);

  -- 295 (#1729): a stored superseding event queues the older events of its stream, type and key for the reaper. A
  -- separate statement, so it sees every event this batch stored as well as the ones before it. Skipped outright when
  -- the batch stored none, so an ordinary batch pays nothing; otherwise one probe of idx_event_store_superseding per
  -- superseding event.
  IF v_superseding_ids IS NOT NULL THEN
    PERFORM __SCHEMA__._wh_queue_superseded_events(v_superseding_ids, p_now);
  END IF;

  IF v_count = 0 THEN
    RETURN 0;
  END IF;

  -- Phase 4.6-equivalent: auto-create perspective events for matching event types.
  -- Phase H step 6 slice 2: populate partition_number via compute_partition(stream_id, p_partition_count)
  -- so claim_orphaned_perspective_events can apply partition-modulo load balancing symmetric
  -- with the outbox / inbox claim paths.
  -- Slice 26.14: route the lease through wh_active_streams. When a live owner is pinned for
  -- the stream, lease to that owner regardless of which instance ran the commit, eliminates
  -- the cross-instance saga race (different instances commit to the same stream, each
  -- selfishly leasing to themselves, drainer races) that produced the residual ~1000 cursor
  -- inversions in a production run. When no live owner is pinned yet (new stream OR stale owner),
  -- fall back to the commit instance so sync paths (UI waiting on a perspective checkpoint)
  -- don't pay claim_orphaned-polling-interval latency on first-event-per-stream.
  INSERT INTO __SCHEMA__.wh_perspective_events (
    event_work_id, stream_id, perspective_name, event_id,
    partition_number, status, attempts, created_at, instance_id, lease_expiry, priority
  )
  SELECT DISTINCT
    gen_random_uuid(),
    es.stream_id,
    ma.target_name,
    es.event_id,
    __SCHEMA__.compute_partition(es.stream_id, p_partition_count),
    1,                  -- Stored flag
    0,
    p_now,
    -- Slice 26.14: when caller is actively leasing (p_lease_expiry IS NOT NULL),
    -- route through wh_active_streams to the stream's pinned owner. When caller passed
    -- NULL p_lease_expiry / NULL p_instance_id (strategy-flush path, "leave unleased so
    -- claim_orphaned picks it up"), preserve that contract; otherwise we'd land in
    -- instance_id-set-but-lease-NULL purgatory that claim_orphaned's filter excludes.
    -- 567 (#2555): in map mode the row is leased to the committing instance only when it owns the stream's
    -- partition; otherwise it is left unleased for the partition's owner to claim. Never to a stored owner.
    CASE WHEN p_lease_expiry IS NULL THEN NULL
         WHEN v_map THEN CASE WHEN __SCHEMA__.wh_i_own(__SCHEMA__.compute_partition(es.stream_id, p_partition_count), p_instance_id) IS NOT NULL THEN p_instance_id END
         ELSE COALESCE(owner.assigned_instance_id, p_instance_id) END,
    CASE WHEN v_map AND NOT __SCHEMA__.wh_i_own(__SCHEMA__.compute_partition(es.stream_id, p_partition_count), p_instance_id) IS NOT NULL THEN NULL ELSE p_lease_expiry END,
    COALESCE(src.priority, 150)   -- 149: the projection is scheduled with the source row's number
  FROM __SCHEMA__.wh_event_store es
  LEFT JOIN __SCHEMA__.wh_outbox src ON src.message_id = es.event_id
  INNER JOIN __SCHEMA__.wh_message_associations ma
    ON es.event_type = ma.normalized_message_type
    AND ma.association_type = c_source_perspective
  LEFT JOIN LATERAL (
    SELECT ast.assigned_instance_id
    FROM __SCHEMA__.wh_active_streams ast
    WHERE ast.stream_id = es.stream_id
      AND ast.assigned_instance_id IS NOT NULL
      AND EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_service_instances si
        WHERE si.instance_id = ast.assigned_instance_id
      )
      -- 567: the stored owner decides nothing in map mode, so the ledger is not read.
      AND NOT v_map
  ) owner ON TRUE
  WHERE es.event_id = ANY(v_stored_event_ids)
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events pe_check
      WHERE pe_check.stream_id = es.stream_id
        AND pe_check.perspective_name = ma.target_name
        AND pe_check.event_id = es.event_id
    )
  ON CONFLICT ON CONSTRAINT uq_perspective_event DO NOTHING;

  -- Migration 061: collective-event routing. A collective event carries no perspective
  -- association, route it to the single fixed __collective__ sink so the perspective worker
  -- dispatches it via ICollectiveDispatcher exactly once (the dispatcher fans out to every
  -- matching model handler internally). One sink row per collective event, driven by the
  -- flag bit, independent of associations. Same partition / owner-lease / dedupe semantics as
  -- the association branch above.
  INSERT INTO __SCHEMA__.wh_perspective_events (
    event_work_id, stream_id, perspective_name, event_id,
    partition_number, status, attempts, created_at, instance_id, lease_expiry, priority
  )
  SELECT DISTINCT
    gen_random_uuid(),
    es.stream_id,
    c_collective_sink,
    es.event_id,
    __SCHEMA__.compute_partition(es.stream_id, p_partition_count),
    1,                  -- Stored flag
    0,
    p_now,
    -- 567 (#2555): in map mode the row is leased to the committing instance only when it owns the stream's
    -- partition; otherwise it is left unleased for the partition's owner to claim. Never to a stored owner.
    CASE WHEN p_lease_expiry IS NULL THEN NULL
         WHEN v_map THEN CASE WHEN __SCHEMA__.wh_i_own(__SCHEMA__.compute_partition(es.stream_id, p_partition_count), p_instance_id) IS NOT NULL THEN p_instance_id END
         ELSE COALESCE(owner.assigned_instance_id, p_instance_id) END,
    CASE WHEN v_map AND NOT __SCHEMA__.wh_i_own(__SCHEMA__.compute_partition(es.stream_id, p_partition_count), p_instance_id) IS NOT NULL THEN NULL ELSE p_lease_expiry END,
    COALESCE(src.priority, 150)   -- 149: the projection is scheduled with the source row's number
  FROM __SCHEMA__.wh_event_store es
  LEFT JOIN __SCHEMA__.wh_outbox src ON src.message_id = es.event_id
  LEFT JOIN LATERAL (
    SELECT ast.assigned_instance_id
    FROM __SCHEMA__.wh_active_streams ast
    WHERE ast.stream_id = es.stream_id
      AND ast.assigned_instance_id IS NOT NULL
      AND EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_service_instances si
        WHERE si.instance_id = ast.assigned_instance_id
      )
      -- 567: the stored owner decides nothing in map mode, so the ledger is not read.
      AND NOT v_map
  ) owner ON TRUE
  WHERE es.event_id = ANY(v_stored_event_ids)
    AND (es.flags & c_flag_collective) = c_flag_collective
    -- 391 (#2145): only a type a handler on this service applies, once every service registered here gates.
    AND __SCHEMA__._wh_collective_row_wanted(es.event_type)
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events pe_check
      WHERE pe_check.stream_id = es.stream_id
        AND pe_check.perspective_name = c_collective_sink
        AND pe_check.event_id = es.event_id
    )
  ON CONFLICT ON CONSTRAINT uq_perspective_event DO NOTHING;

  -- Slice 26.4: wake the commit-order stamper. PG buffers NOTIFY until COMMIT and
  -- dedups (channel, payload) within the transaction, so a tx storing 10k events
  -- delivers exactly one wh_committed notification to each LISTEN-er. The stamper
  -- is the only listener; on wake it runs stamp_pending_commit_sequences within ~1ms
  -- instead of waiting for its polling tick.
  PERFORM __SCHEMA__._queue_doorbell('wh_committed', '');

  RETURN v_count;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._emit_event_store_chain(UUID[], UUID, TIMESTAMPTZ, TIMESTAMPTZ, INTEGER) IS
  'Copies committed outbox events into the event store and creates their perspective work (146). 149: the perspective rows inherit the outbox row''s priority. 214: the digest fold leaves out at-most-once events (flags & 32). 216: and audit records (#1492). 260: keeps each stored stream''s wh_stream_lifecycle row, one upsert per batch (#1732). 266: dead-letters each event a sealed stream refuses and stores the rest; a terminal event seals its stream in the same statement (#1733). 271: and each event an archived stream refuses, by its type''s AppendToArchived policy, queuing the stream''s restore under AutoRestore (#1738). 285: each row keeps the slim form unless kept full, carries its route id, and under the Linked form stages its full form (#1725). 295: stamps a superseding event''s key (wh_event_store.supersede_key) and queues the older events of its key for the reaper (#1729). 296: starts the new generation of an archived stream whose type declares NewGeneration, numbered on from its last version (#1734). 352: and its superseding group (wh_event_store.supersede_group, #2124), so the older events of its group, not only its type, are queued. 391: a collective event of a type no handler on this service applies gets no __collective__ row once every service registered in the schema has written its collective_gate marker (_wh_collective_row_wanted, #2145). 537 (#2323): the slim row leaves out its message id when it is the event id, and its hop scope when it is the scope column alone. 567 (#2555): with the service''s switch on, no stored owner is pinned or leased to; leased by partition (wh_i_own). 580 (#2551): a stream''s events are numbered in the order the id array lists them, the order they were emitted, never by id.';

-- ---------------------------------------------------------------------------------------------
-- store_outbox_messages: copied from its last word (567), with the 580 delta: emission position, never id.
-- ---------------------------------------------------------------------------------------------
-- <docs>fundamentals/events/system-events#audit</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AuditRecordStreamPositionSqlTests.cs:Store_EventAndItsAuditTogether_RecordCarriesTheEventsVersionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/EFCoreCollectiveOrderingHeadTests.cs:StoreOutboxMessagesAsync_TwoCoordinators_LinkTheSecondToTheFirstAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MixedStreamGuardSqlTests.cs:Store_RefusedEventInABatch_LeavesTheOtherStreamsStoredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StringEventFlagsStoreTests.cs:StoreOutbox_FlagsWrittenAsNames_KeepsEveryBitAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredWorkReentrySqlTests.cs:ARecoveredOutboxMessage_IsStoredInTheShapeItWasStoredInAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredWorkReentrySqlTests.cs:ARecovery_OfAnOutboxMessageStillInTheOutbox_LeavesItsDeadLetterDueAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredOutboxDeadLetterPublishTests.cs:ARecoveredOutboxEvent_IsPublishedAsTheEventItWasStoredAs_AndReadsRecoveredOncePublishedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:StoreOutboxMessages_AnAwaitedWriteToASealedStream_ThrowsStreamSealedException_AndStoresNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:StoreOutboxMessages_TheSameWriteNotAwaited_IsDeadLettered_AndAnAwaitedWriteToAnOpenStreamIsStoredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveAppendPolicySqlTests.cs:StoreOutboxMessages_AnAwaitedWrite_Refuse_ThrowsStreamArchivedException_AndStoresNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveAppendPolicySqlTests.cs:StoreOutboxMessages_AnAwaitedWrite_AutoRestore_RaisesTheRestoreStateAsync</tests>
-- <docs>fundamentals/work-coordinator/partition-assignment#ownership-by-map</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipByMapStoreSqlTests.cs:StoreOutbox_InMapMode_RecordsTheStreamWithoutAnOwnerAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipByMapStoreSqlTests.cs:StoreOutbox_SwitchOff_PinsTheFirstWriter_AsBeforeAsync</tests>
-- <docs>fundamentals/messaging/sequence-integrity#how-the-hold-works</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SequenceIntegritySqlTests.cs:Store_WritesEachModesHoldOntoAnEventsRow_AndNeverOntoACommandsAsync</tests>
-- <docs>fundamentals/event-store/version-ordering#emission-order</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EmissionOrderVersioningSqlTests.cs:Store_ACauseAndItsDerivedEffectInOneBatch_TheCauseTakesTheLowerVersionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EmissionOrderVersioningSqlTests.cs:CommitHandlerResult_ACauseAndItsDerivedEffect_TheCauseTakesTheLowerVersionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MixedStreamGuardSqlTests.cs:Store_FirstWriteMixesBothKinds_KeepsTheFirstKindAndDeadLettersTheOtherAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.store_outbox_messages(
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
  v_msg RECORD;
  v_partition INTEGER;
  v_was_new BOOLEAN;
  v_inserted_event_ids UUID[] := ARRAY[]::UUID[];
  -- 216: streams this call pins, written after the audit position fill so the work tables stay in canonical lock order.
  v_pin_streams UUID[] := ARRAY[]::UUID[];
  v_pin_partitions INTEGER[] := ARRAY[]::INTEGER[];
  -- 114 edge-notify state: streams probed once per call (first encounter, BEFORE this
  -- call inserts for them), the probed-empty subsets per category, and the notify sets.
  v_probed_streams UUID[] := ARRAY[]::UUID[];
  v_empty_outbox_streams UUID[] := ARRAY[]::UUID[];
  v_empty_persp_streams UUID[] := ARRAY[]::UUID[];
  v_notify_outbox_streams UUID[] := ARRAY[]::UUID[];
  v_notify_persp_streams UUID[] := ARRAY[]::UUID[];
  -- 190: the outbox envelope as stored, with a keyed collective's predecessor link written into its payload.
  v_envelope JSONB;
  v_predecessor_id UUID;
  v_predecessor_type TEXT;
  -- 216 (#1485): the audit records this call stores, and the event each one audits, for the position fill below.
  v_audit_record_ids UUID[] := ARRAY[]::UUID[];
  v_audited_event_ids UUID[] := ARRAY[]::UUID[];
  v_audited_event_text TEXT;
  -- 228 (#1546): the stream, flags and type of each event in v_inserted_event_ids (same positions), the events whose
  -- refusal is raised to the caller, and the events the refusal dead-lettered.
  v_inserted_event_streams UUID[] := ARRAY[]::UUID[];
  v_inserted_event_flags INTEGER[] := ARRAY[]::INTEGER[];
  v_inserted_event_types TEXT[] := ARRAY[]::TEXT[];
  -- 580 (#2551): each inserted event's position in p_messages (same positions as v_inserted_event_ids), the order the
  -- producer emitted the batch in.
  v_inserted_event_positions BIGINT[] := ARRAY[]::BIGINT[];
  v_raise_event_ids UUID[] := ARRAY[]::UUID[];
  v_refused_event_ids UUID[];
  v_claimed INTEGER;          -- 241: rows the recovery claim touched
  -- 566 (#2554, #2555): the service's switch, read once: on, the partition ownership table alone decides.
  v_map BOOLEAN := __SCHEMA__.wh_partition_ownership_by_map();
BEGIN
  IF jsonb_array_length(p_messages) = 0 THEN RETURN; END IF;

  FOR v_msg IN
    SELECT
      (elem->>__ENVELOPE_FIELD_MESSAGE_ID__)::UUID as msg_id,
      elem->>'Destination' as destination,
      elem->>'MessageType' as message_type,
      elem->>'EnvelopeType' as envelope_type,
      elem->'Envelope' as envelope_data,
      elem->'Metadata' as metadata,
      elem->'Scope' as scope,
      (elem->>__ENVELOPE_FIELD_STREAM_ID__)::UUID as stream_id,
      (elem->>'IsEvent')::BOOLEAN as is_event,
      -- 149: the priority the producer declared; zero (undeclared) and an absent field read as standard.
      COALESCE(NULLIF((elem->>'Priority')::INTEGER, 0), 150) as priority,
      -- 559 (#2542): the hold the origin decided for the event, in milliseconds: 0 none, -1 until stamped.
      COALESCE((elem->>'SequenceHoldMilliseconds')::INTEGER, 0) as sequence_hold_ms,
      -- EventFlags (062): persisted so migration 061's collective routing can see (flags & 1). 232 (#1612): the
      -- number (what the framework writes) is read here; a name list is read bit by bit by _wh_event_flags_from_names,
      -- which raises on a name it does not know rather than store the event as Sourced.
      CASE
        WHEN elem->>__ENVELOPE_FIELD_FLAGS__ IS NULL OR elem->>__ENVELOPE_FIELD_FLAGS__ = '' THEN 0
        WHEN elem->>__ENVELOPE_FIELD_FLAGS__ ~ '^[0-9]+$' THEN (elem->>__ENVELOPE_FIELD_FLAGS__)::INTEGER
        ELSE __SCHEMA__._wh_event_flags_from_names(elem->>__ENVELOPE_FIELD_FLAGS__)
      END as flags,
      NULLIF(elem->>'ScheduledFor', '')::TIMESTAMPTZ as scheduled_for,
      -- 115 tag-bound coalescing: the group a pending single belongs to (NULL for normal rows).
      NULLIF(elem->>'CoalesceGroup', '') as coalesce_group,
      -- 190: set only on a collective with an ordering key; the type its successor names as its predecessor.
      NULLIF(elem->>'CollectiveLinkType', '') as collective_link_type,
      -- 228 (#1546): set by a write its caller awaits; a refusal of this event is raised instead of dead-lettered.
      COALESCE((elem->>'RaiseOnMixedStream')::BOOLEAN, false) as raise_on_mixed_stream,
      -- 241 (#1722): set only by dead-letter recovery, naming the dead-letter row the message re-enters from.
      (elem->>'RecoveredFromDeadLetterId')::UUID as recovered_from,
      -- 580 (#2551): the element's position in the batch, which is the order the producer emitted it in.
      listed.position as emission_position
    FROM jsonb_array_elements(p_messages) WITH ORDINALITY AS listed(elem, position)
    -- Inserted in stream and id order, the canonical lock order of the rows this loop writes; the order the events are
    -- versioned in is their emission position, collected below.
    ORDER BY (elem->>__ENVELOPE_FIELD_STREAM_ID__)::UUID NULLS FIRST, (elem->>__ENVELOPE_FIELD_MESSAGE_ID__)::UUID
  LOOP
    -- 241 (#1722): dead-letter recovery re-enters a message HERE, as one more arrival, so every rule below applies to
    -- it unchanged. A row that names its dead letter claims it first (Recovering), in this transaction, so two
    -- recoveries of one row cannot both re-enter it, and a dead letter whose message the outbox still holds is left
    -- alone. The row then carries the dead-letter id, and complete_outbox_published settles it once published.
    IF v_msg.recovered_from IS NOT NULL THEN
      UPDATE __SCHEMA__.wh_dead_letters dl
      SET recovery_status = 1,                  -- Recovering, until the message is published
          recovery_attempts = dl.recovery_attempts + 1,
          last_recovery_at = p_now
      WHERE dl.dead_letter_id = v_msg.recovered_from
        AND dl.source_id = v_msg.msg_id
        AND dl.recovery_status NOT IN (1, 2, 3, 4)  -- not already Recovering, HoldForReview, Recovered, PermanentlyFailed
        AND dl.recovered_at IS NULL
        AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_outbox o WHERE o.message_id = v_msg.msg_id);
      GET DIAGNOSTICS v_claimed = ROW_COUNT;
      IF v_claimed = 0 THEN
        CONTINUE;  -- claimed by another recovery, already settled, or the message is still in the outbox
      END IF;
    END IF;

    IF v_msg.stream_id IS NOT NULL THEN
      v_partition := __SCHEMA__.compute_partition(v_msg.stream_id, p_partition_count);
    ELSE
      v_partition := NULL;
    END IF;

    -- 114: emptiness probe, once per distinct stream per call, BEFORE this call's first
    -- insert for that stream (the loop is stream-ordered, so first encounter precedes all
    -- of the stream's inserts). FOR SHARE serializes against an in-flight completion of
    -- the last pending row; see the header's MVCC lost-wakeup guard.
    IF v_msg.stream_id IS NOT NULL AND NOT (v_msg.stream_id = ANY(v_probed_streams)) THEN
      v_probed_streams := array_append(v_probed_streams, v_msg.stream_id);

      PERFORM 1 FROM __SCHEMA__.wh_outbox o
        WHERE o.stream_id = v_msg.stream_id
          AND o.processed_at IS NULL
          AND o.published_at IS NULL
          AND (o.scheduled_for IS NULL OR o.scheduled_for <= p_now)
        LIMIT 1
        FOR SHARE OF o SKIP LOCKED;   -- 140: a locked pending row is being completed; treat as empty and ring
      IF NOT FOUND THEN
        v_empty_outbox_streams := array_append(v_empty_outbox_streams, v_msg.stream_id);
      END IF;

      PERFORM 1 FROM __SCHEMA__.wh_perspective_events pe
        WHERE pe.stream_id = v_msg.stream_id
          AND pe.processed_at IS NULL
        -- 161: the ORDER BY is what makes the index reachable, and it is the whole fix. Under a
        -- bare LIMIT 1 the planner prices a sequential scan as though the first row it reads will
        -- match, because processed_at IS NULL is true of nearly every row; on a stream whose work
        -- is all done there is no match at all, so it scans to exhaustion. An index alone does not
        -- change that choice and neither do extended statistics, both measured. Ordering by the
        -- index's own key removes the bad plan instead of out-costing it: a sequential scan would
        -- have to sort before it could answer. stream_id is pinned by the equality above, so the
        -- clause changes no result -- order by that column alone is dropped as redundant, which is
        -- why a second key of the index is here too. 580 (#2551): the second key is perspective_name, the next column
        -- of uq_perspective_event (stream_id, perspective_name, event_id), not the event id: the probe asks only whether
        -- a row exists, and nothing is ordered by id.
        ORDER BY pe.stream_id, pe.perspective_name
        LIMIT 1
        FOR SHARE OF pe SKIP LOCKED;  -- 140: same rule for the perspective doorbell
      IF NOT FOUND THEN
        v_empty_persp_streams := array_append(v_empty_persp_streams, v_msg.stream_id);
      END IF;
    END IF;

    -- 190: a keyed collective is linked to the one stored before it on its key (its stream), in this
    -- transaction, and the key's head moves to it. The head row is locked until commit, so publishers on one
    -- key, on any instance, take turns and form one chain. A message already stored (a retry's republish, or
    -- a scheduled row stored again when due) is not linked again: that would move the head backward.
    v_envelope := COALESCE(v_msg.envelope_data, '{}'::jsonb);
    IF v_msg.collective_link_type IS NOT NULL
       AND v_msg.stream_id IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_outbox o WHERE o.message_id = v_msg.msg_id)
       AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = v_msg.msg_id) THEN
      SELECT l.predecessor_id, l.predecessor_type
        INTO v_predecessor_id, v_predecessor_type
        FROM __SCHEMA__.wh_link_collective_predecessor(v_msg.stream_id, v_msg.msg_id, v_msg.collective_link_type, p_now) l;
      IF v_predecessor_id IS NOT NULL
         AND jsonb_typeof(v_envelope -> 'p') = 'object'
         AND v_envelope #>> __COLLECTIVE_PREDECESSOR_ID_PATH__ IS NULL THEN
        v_envelope := jsonb_set(
          jsonb_set(v_envelope, __COLLECTIVE_PREDECESSOR_ID_PATH__, to_jsonb(v_predecessor_id::TEXT)),
          __COLLECTIVE_PREDECESSOR_TYPE_PATH__, to_jsonb(v_predecessor_type));
      END IF;
    END IF;

    INSERT INTO __SCHEMA__.wh_outbox (
      message_id,
      destination,
      message_type,
      envelope_type,
      event_data,
      metadata,
      scope,
      stream_id,
      partition_number,
      is_event,
      flags,
      status,
      attempts,
      created_at,
      instance_id,
      lease_expiry,
      scheduled_for,
      coalesce_group,
      priority,
      recovered_from_dead_letter_id,
      sequence_hold_until
    ) VALUES (
      v_msg.msg_id,
      v_msg.destination,
      v_msg.message_type,
      v_msg.envelope_type,
      v_envelope,
      COALESCE(v_msg.metadata, '{}'::jsonb),
      COALESCE(v_msg.scope, 'null'::jsonb),
      v_msg.stream_id,
      v_partition,
      COALESCE(v_msg.is_event, false),
      COALESCE(v_msg.flags, 0),
      1,  -- Stored flag
      0,  -- Initial attempts
      p_now,
      p_instance_id,  -- Immediate lease
      p_lease_expiry,
      v_msg.scheduled_for,
      v_msg.coalesce_group,
      v_msg.priority,
      v_msg.recovered_from,  -- 241: NULL for every arrival but a recovery
      -- 559 (#2542): only an event that is published is stamped and held. The deadline is taken from the store's own
      -- clock, the one created_at is, so a bounded hold lasts what the origin asked for however late the row is fetched.
      CASE
        WHEN NOT COALESCE(v_msg.is_event, false) OR v_msg.destination IS NULL OR v_msg.sequence_hold_ms = 0 THEN NULL
        WHEN v_msg.sequence_hold_ms < 0 THEN 'infinity'::TIMESTAMPTZ
        ELSE p_now + v_msg.sequence_hold_ms * INTERVAL '1 millisecond'
      END
    )
    ON CONFLICT ON CONSTRAINT wh_outbox_pkey DO NOTHING;

    GET DIAGNOSTICS v_was_new = ROW_COUNT;

    -- 241: a recovery that lost the race to a concurrent store of the same id hands its dead letter back, so the dead
    -- letter is never left Recovering behind a row that does not name it.
    IF NOT v_was_new AND v_msg.recovered_from IS NOT NULL THEN
      UPDATE __SCHEMA__.wh_dead_letters dl
      SET recovery_status = 0
      WHERE dl.dead_letter_id = v_msg.recovered_from AND dl.recovery_status = 1;
      CONTINUE;
    END IF;

    -- 216 (#1485): an audit record built on the outbox path names the event it audits but carries position 0,
    -- because that event has no version until the emit chain stores it. Collected here and filled in below,
    -- after the chain has run. Matched by type, so no other message's payload is ever touched, and by a
    -- well-formed id, so a malformed payload is left alone rather than failing the batch.
    IF v_was_new
       AND NOT COALESCE(v_msg.is_event, false)
       AND split_part(v_msg.message_type, ',', 1) = __AUDIT_RECORD_TYPE__
       AND jsonb_typeof(v_envelope #> __AUDIT_ORIGINAL_STREAM_POSITION_PATH__) = 'number' THEN
      v_audited_event_text := v_envelope #>> __AUDIT_ORIGINAL_EVENT_ID_PATH__;
      IF v_audited_event_text ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' THEN
        v_audit_record_ids := array_append(v_audit_record_ids, v_msg.msg_id);
        v_audited_event_ids := array_append(v_audited_event_ids, v_audited_event_text::UUID);
      END IF;
    END IF;

    IF v_was_new AND COALESCE(v_msg.is_event, false) AND v_msg.stream_id IS NOT NULL THEN
      v_inserted_event_ids := array_append(v_inserted_event_ids, v_msg.msg_id);
      v_inserted_event_streams := array_append(v_inserted_event_streams, v_msg.stream_id);
      v_inserted_event_flags := array_append(v_inserted_event_flags, COALESCE(v_msg.flags, 0));
      v_inserted_event_types := array_append(v_inserted_event_types, v_msg.message_type);
      v_inserted_event_positions := array_append(v_inserted_event_positions, v_msg.emission_position);
      IF v_msg.raise_on_mixed_stream THEN
        v_raise_event_ids := array_append(v_raise_event_ids, v_msg.msg_id);
      END IF;
    END IF;

    -- 114: a genuinely-new, drainable-now row on a probed-empty stream is the
    -- empty→non-empty edge. Future-scheduled rows are not drainable now, so they
    -- do not ring (they surface via the scheduled-retry NOTIFY / poll when due).
    IF v_was_new AND v_msg.stream_id IS NOT NULL
       AND (v_msg.scheduled_for IS NULL OR v_msg.scheduled_for <= p_now)
       AND v_msg.stream_id = ANY(v_empty_outbox_streams)
       AND NOT (v_msg.stream_id = ANY(v_notify_outbox_streams)) THEN
      v_notify_outbox_streams := array_append(v_notify_outbox_streams, v_msg.stream_id);
    END IF;

    -- Pinning is ownership/routing, unchanged by 114 (the cold-notify tracking that
    -- used to ride this block is gone; the notify decision now keys on queue state).
    -- 216: the pin is collected here and written after the audit position fill below, so this function
    -- locks wh_outbox before wh_active_streams. The emit chain reads a stream's pinned owner and falls back
    -- to p_instance_id when none is pinned, which is the owner the pin would have named.
    IF v_was_new AND v_msg.stream_id IS NOT NULL AND p_instance_id IS NOT NULL THEN
      v_pin_streams := array_append(v_pin_streams, v_msg.stream_id);
      v_pin_partitions := array_append(v_pin_partitions, COALESCE(v_partition, 0));
    END IF;

    RETURN QUERY SELECT v_msg.msg_id AS message_id, v_msg.stream_id AS stream_id, v_was_new AS was_newly_created;
  END LOOP;

  -- 580 (#2551): the inserted events are handed on in the order the producer emitted them (their position in
  -- p_messages), not in the loop's lock order, which sorts them by stream and id. The refusals judge "first" and
  -- "after" by position in these arrays, and the chain numbers each stream in their order: an id is time-ordered,
  -- never stream position.
  IF cardinality(v_inserted_event_ids) > 1 THEN
    SELECT array_agg(k.id ORDER BY k.pos), array_agg(k.sid ORDER BY k.pos), array_agg(k.bits ORDER BY k.pos),
           array_agg(k.type_name ORDER BY k.pos)
      INTO v_inserted_event_ids, v_inserted_event_streams, v_inserted_event_flags, v_inserted_event_types
    FROM unnest(v_inserted_event_ids, v_inserted_event_streams, v_inserted_event_flags, v_inserted_event_types,
                v_inserted_event_positions) AS k(id, sid, bits, type_name, pos);
  END IF;

  -- 228 (#1546): an event that would make its stream hold both ephemeral and Sourced events is refused before the
  -- copy. A refusal the caller awaits raises (the whole call rolls back); any other is dead-lettered, which moves its
  -- outbox row out of the queue, and is left out of the copy so the rest of the write is stored.
  -- 266 (#1733): an event the caller awaits that a sealed stream refuses raises SQLSTATE WHS01 (StreamSealedException)
  -- and the whole call rolls back, as the mixed-stream refusal raises for it. Checked first, so an awaited event that is
  -- both is reported as sealed. Every other refused event is dead-lettered by the emit chain, which stores the rest.
  -- 271 (#1738): then one the caller awaits that an archived stream refuses raises SQLSTATE WHA01 (Refuse) or WHA02
  -- (AutoRestore: the caller restores the stream and retries once), and the whole call rolls back. After the seal, so a
  -- sealed archived stream is reported as sealed. Every other refused event is dead-lettered by the emit chain.
  IF cardinality(v_raise_event_ids) > 0 THEN
    PERFORM __SCHEMA__._wh_raise_sealed_stream_outbox_events(
      v_inserted_event_ids, v_inserted_event_streams, v_inserted_event_flags, v_inserted_event_types, v_raise_event_ids, p_now);
    PERFORM __SCHEMA__._wh_raise_archived_stream_outbox_events(
      v_inserted_event_ids, v_inserted_event_streams, v_raise_event_ids);
  END IF;

  IF cardinality(v_inserted_event_ids) > 0 THEN
    v_refused_event_ids := __SCHEMA__._wh_refuse_mixed_stream_outbox_events(
      v_inserted_event_ids, v_inserted_event_streams, v_inserted_event_flags, v_inserted_event_types,
      v_raise_event_ids, p_instance_id);
    IF cardinality(v_refused_event_ids) > 0 THEN
      v_inserted_event_ids := ARRAY(
        SELECT kept.id FROM unnest(v_inserted_event_ids) WITH ORDINALITY AS kept(id, ord)
        WHERE kept.id <> ALL(v_refused_event_ids) ORDER BY kept.ord);
    END IF;
  END IF;

  IF cardinality(v_inserted_event_ids) > 0 THEN
    PERFORM __SCHEMA__._emit_event_store_chain(
      v_inserted_event_ids,
      p_instance_id,
      p_lease_expiry,
      p_now,
      p_partition_count
    );

    -- 114: the perspective edge is decided AFTER the emit chain, so it rings only for
    -- streams whose perspective queue was empty before this call AND for which this
    -- call actually created work items (our own inserts are visible in-transaction).
    -- Association-less event types create no work and therefore never ring here.
    IF cardinality(v_empty_persp_streams) > 0 THEN
      SELECT COALESCE(array_agg(DISTINCT pe.stream_id), ARRAY[]::UUID[])
        INTO v_notify_persp_streams
        FROM __SCHEMA__.wh_perspective_events pe
        WHERE pe.stream_id = ANY(v_empty_persp_streams)
          AND pe.processed_at IS NULL;
    END IF;
  END IF;

  -- 216 (#1485): each audit record stored by this call takes its event's version, read by primary key. The
  -- chain above has stored the events this call carried; an event stored by an earlier call is found too. An
  -- event not yet stored (its write is still to come) leaves the record at 0.
  IF cardinality(v_audit_record_ids) > 0 THEN
    UPDATE __SCHEMA__.wh_outbox o
    SET event_data = jsonb_set(o.event_data::jsonb, __AUDIT_ORIGINAL_STREAM_POSITION_PATH__, to_jsonb(es.version::BIGINT))
    FROM unnest(v_audit_record_ids, v_audited_event_ids) AS a(record_id, audited_event_id)
    JOIN __SCHEMA__.wh_event_store es ON es.event_id = a.audited_event_id
    WHERE o.message_id = a.record_id;
  END IF;

  -- Pin the streams collected above: the first message of a stream names its partition, as the per-message
  -- insert did. Sorted by stream id, so two calls pinning the same streams take them in one order.
  IF cardinality(v_pin_streams) > 0 THEN
    INSERT INTO __SCHEMA__.wh_active_streams
      (stream_id, partition_number, assigned_instance_id, last_activity_at)
    -- 567 (#2555): in map mode the first writer is not the stream's owner; the row records the stream and its
    -- partition, and the partition's owner is named when it claims.
    SELECT DISTINCT ON (p.stream_id) p.stream_id, p.partition_number, CASE WHEN v_map THEN NULL ELSE p_instance_id END, p_now
    FROM unnest(v_pin_streams, v_pin_partitions) WITH ORDINALITY AS p(stream_id, partition_number, ord)
    ORDER BY p.stream_id, p.ord
    ON CONFLICT (stream_id) DO NOTHING;
  END IF;

  IF cardinality(v_notify_outbox_streams) > 0 THEN
    PERFORM __SCHEMA__.notify_instance_owners(__CATEGORY_OUTBOX__, v_notify_outbox_streams);
  END IF;
  IF cardinality(v_notify_persp_streams) > 0 THEN
    PERFORM __SCHEMA__.notify_instance_owners(__CATEGORY_PERSPECTIVE__, v_notify_persp_streams);
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.store_outbox_messages(JSONB, UUID, TIMESTAMPTZ, TIMESTAMPTZ, INTEGER) IS
  'Stores new outbox messages (149, 161), links each keyed collective to the one stored before it on its key '
  '(190, #1003), and fills each audit record''s stream position from its stored event (216, #1485), in the same '
  'transaction. 228: refuses an event that would make its stream hold both ephemeral and Sourced events (WHIZ133, '
  '#1546), raising when the element asks for it and dead-lettering the row otherwise. 232: reads every bit of flags '
  'written as a name list, and raises 22P02 on a name EventFlags does not define (#1612). 241 (#1722): a message '
  'recovered from a dead letter (RecoveredFromDeadLetterId) re-enters here as one more arrival: it claims its dead '
  'letter and its row names it for complete_outbox_published to settle. 266: raises SQLSTATE WHS01 '
  '(StreamSealedException) for an event the caller awaits on a sealed stream (#1733). 271: and SQLSTATE WHA01 or WHA02 '
  '(StreamArchivedException) for one on an archived stream, by its type''s AppendToArchived policy (#1738). 559: writes '
  'the hold the origin decided for a published event (SequenceHoldMilliseconds) as sequence_hold_until (#2542). '
  '567 (#2555): with the service''s switch on, no stored owner is pinned or leased to; leased by partition (wh_i_own). 580 (#2551): hands the events it inserted to the refusals and the emit chain in the order the producer emitted them (their position in p_messages), never sorted by id.';
