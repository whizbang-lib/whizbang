-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 266_SealedStreamsRefuseAppends.sql
-- Date: 2026-10-29
-- Description: Developer-driven stream finalization and sealing (#1733, WS-3 slice S4). A stream ends in one of two
--              ways: a developer finalizes it (IStreamFinalizer.FinalizeAsync, through wh_finalize_stream), or an event
--              whose type is declared [TerminalEvent] is stored on it (EventFlags.Terminal, 64). Either way the
--              stream's lifecycle row (wh_stream_lifecycle, migration 260) becomes Sealed, with sealed_at and the new
--              sealed_reason, and every later append to the stream is refused, on every write path:
--
--                a direct append    wh_append_event raises SQLSTATE WHS01, which both drivers surface as
--                                   StreamSealedException (stream id, sealed_at, reason). The caller awaits the
--                                   append, so nothing is stored.
--                an awaited outbox  an element carrying RaiseOnMixedStream (the immediate and scoped strategies' flush,
--                write              which the caller awaits) raises WHS01 from store_outbox_messages, as the
--                                   mixed-stream refusal (228) raises WH133 for it; the whole write rolls back.
--                the outbox chain   every other refused event alone is dead-lettered (MessageFailureReason.StreamSealed,
--                                   25) with the same wording as the exception, and the rest of the batch is stored. The
--                                   refusal sits in _emit_event_store_chain itself, so no outbox writer can go around it.
--                the inbox chain    the same, for a received event: recorded in wh_dead_letters and its lease row
--                                   completed in place, as the mixed-stream refusal (228) does.
--
--              The refusal is per event, never per batch: the archive tombstone's guard (262) raises WHA01 from a
--              statement trigger, which fails the whole chain batch, and a sealed stream must not take other streams'
--              events down with it. A terminal event is stored and seals its stream in the same statement that copies
--              it; an event that sorts after it in the same write (version order is message id order) is refused as
--              if the stream had been sealed before the write.
--
--              The seal is sealed_at, not the state: a sealed stream that is archived reads Archived and stays sealed,
--              and wh_restore_archived_stream (copied from 262) now returns it to Sealed rather than Active. Finalizing
--              is idempotent: a sealed stream keeps its first seal's time and reason. A stream with no Sourced event
--              (never written, ephemeral, or the empty stream id) has no lifecycle row and nothing to seal; a stream
--              the one-time backfill has not reached yet has its row built from its events by 260's rules.
--
--              The append path pays one probe per batch of a partial index that holds only sealed streams
--              (idx_stream_lifecycle_sealed), and nothing more unless a stream in the batch is sealed or the batch
--              carries a terminal event. The column the index is partial on is written only by finalization and a
--              terminal event, so an append's ledger update stays heap-only.
--
--              Number 266: reserved for this change.
--
-- Dependencies: 260 (wh_stream_lifecycle; last word of _emit_event_store_chain, _emit_event_store_chain_for_inbox and
--               wh_append_event), 262 (last word of wh_restore_archived_stream), 232 (last word of
--               _wh_event_flags_from_names), 228 (_wh_inbox_dead_letter_snapshot), 241 (move_to_dead_letters, and the last word of
--               store_outbox_messages)
-- Objects: sealed_reason, idx_stream_lifecycle_sealed, _wh_event_flags_from_names, _wh_terminal_seal_reason,
--          _wh_stream_sealed_refusal_text, _wh_sealed_stream_refusals, _wh_refuse_sealed_stream_outbox_events,
--          _wh_refuse_sealed_stream_inbox_events, _wh_raise_sealed_stream_outbox_events, store_outbox_messages,
--          wh_finalize_stream, _emit_event_store_chain,
--          _emit_event_store_chain_for_inbox, wh_append_event, wh_restore_archived_stream
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- ===========================================================================================
-- The seal's reason, and the index the append path probes
-- ===========================================================================================

-- Additive, nullable, no default: no row is rewritten.
ALTER TABLE __SCHEMA__.wh_stream_lifecycle ADD COLUMN IF NOT EXISTS sealed_reason TEXT NULL;

COMMENT ON COLUMN __SCHEMA__.wh_stream_lifecycle.sealed_reason IS
  'Why the stream was sealed: the reason given to IStreamFinalizer.FinalizeAsync, or the terminal event that ended it. Written with sealed_at and never changed after (#1733).';

-- Only the sealed streams. Every write path asks "is any stream of this batch sealed?" before it stores, and this index
-- answers it with one probe per stream into a structure as small as the number of sealed streams, whatever the size of
-- the ledger. Partial on sealed_at, which an append never writes, so an append's ledger update stays heap-only.
CREATE INDEX IF NOT EXISTS idx_stream_lifecycle_sealed
  ON __SCHEMA__.wh_stream_lifecycle (stream_id)
  WHERE sealed_at IS NOT NULL;

-- ===========================================================================================
-- _wh_event_flags_from_names: last word 232, with EventFlags.Terminal (1 << 6). Nothing else changes.
-- ===========================================================================================

-- <docs>fundamentals/messaging/collective-events</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StringEventFlagsStoreTests.cs:StoreOutbox_FlagsWrittenAsNames_KeepsEveryBitAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StringEventFlagsStoreTests.cs:StoreInbox_FlagsWrittenAsNames_KeepsEveryBitAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StringEventFlagsStoreTests.cs:StoreOutbox_FlagsNamedUnderANamingPolicy_KeepsEveryBitAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StringEventFlagsStoreTests.cs:StoreOutbox_FlagsWrittenAsNamesAndANumber_KeepsEveryBitAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StringEventFlagsStoreTests.cs:StoreOutbox_FlagsNameNoEventFlagsValueHas_RaisesAndStoresNothingAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_event_flags_from_names(p_names TEXT)
RETURNS INTEGER AS $$
DECLARE
  v_token TEXT;
  v_name TEXT;
  v_bits INTEGER;
  v_flags INTEGER := 0;
BEGIN
  FOREACH v_token IN ARRAY string_to_array(p_names, ',') LOOP
    -- One spelling per name under every naming policy: atMostOnce, at_most_once, AT_MOST_ONCE, at-most-once.
    v_name := lower(regexp_replace(v_token, '[[:space:]_-]', '', 'g'));
    v_bits := CASE
      WHEN v_name ~ '^[0-9]+$' THEN v_name::INTEGER
      WHEN v_name = 'none' THEN 0             -- EventFlags.None
      WHEN v_name = 'collective' THEN 1       -- EventFlags.Collective (1 << 0)
      WHEN v_name = 'composite' THEN 2        -- EventFlags.Composite (1 << 1)
      WHEN v_name = 'norebroadcast' THEN 4    -- EventFlags.NoRebroadcast (1 << 2)
      WHEN v_name = 'ephemeral' THEN 8        -- EventFlags.Ephemeral (1 << 3)
      WHEN v_name = 'compacted' THEN 16       -- EventFlags.Compacted (1 << 4)
      WHEN v_name = 'atmostonce' THEN 32      -- EventFlags.AtMostOnce (1 << 5)
      WHEN v_name = 'terminal' THEN 64        -- EventFlags.Terminal (1 << 6), 266
    END;
    IF v_bits IS NULL THEN
      RAISE EXCEPTION USING
        ERRCODE = 'invalid_text_representation',
        MESSAGE = format('Event flags %L name %L, which no EventFlags value has, so the event''s kind cannot be read '
          'and it is not stored (#1612).', p_names, btrim(v_token));
    END IF;
    v_flags := v_flags | v_bits;
  END LOOP;
  RETURN v_flags;
END;
$$ LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__._wh_event_flags_from_names(TEXT) IS
  'The bits of EventFlags written as a comma-separated name list (JsonStringEnumConverter''s form), matched without '
  'regard to case, spaces, underscores or hyphens; a number among the names is read as its bits. Raises 22P02 on a '
  'name EventFlags does not define. Read by store_outbox_messages and store_inbox_messages for a value that is not a '
  'number (232, #1612). 266: and Terminal (#1733).';

-- ===========================================================================================
-- The wording: one place for the terminal seal's reason and one for the refusal
-- ===========================================================================================

SELECT __SCHEMA__.drop_all_overloads('_wh_terminal_seal_reason');

-- The reason a terminal event seals its stream with, the same on every write path.
-- <docs>fundamentals/events/stream-end-of-life#terminal-events</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:TerminalEvent_OnTheOutbox_SealsTheStream_InTheTransactionThatStoresItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:TerminalEvent_DirectAppend_SealsTheStream_AndTheNextAppendThrowsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_terminal_seal_reason(p_type_name TEXT, p_event_id UUID)
RETURNS TEXT AS $$
  SELECT format('Terminal event %s (event %s)', p_type_name, p_event_id);
$$ LANGUAGE sql IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__._wh_terminal_seal_reason(TEXT, UUID) IS
  'The sealed_reason a terminal event writes: the event''s type name and id (#1733).';

SELECT __SCHEMA__.drop_all_overloads('_wh_stream_sealed_refusal_text');

-- The refusal's wording, the same in a raised WHS01, a dead letter's error text and StreamSealedException.Message.
-- StreamSealedRefusal reads the three fields back from it, so the reason comes last and may hold any text, and the time
-- is written in UTC to the microsecond, as the ledger holds it.
-- <docs>fundamentals/events/stream-end-of-life#a-sealed-stream-refuses-appends</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:DirectAppend_ToASealedStream_ThrowsStreamSealedException_AndStoresNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:OutboxAppend_ToASealedStream_DeadLettersThatEvent_AndTheRestOfTheBatchCommitsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_stream_sealed_refusal_text(p_stream_id UUID, p_sealed_at TIMESTAMPTZ, p_reason TEXT)
RETURNS TEXT AS $$
  SELECT format('Stream %s was sealed at %s and refuses appends. Reason: %s',
                p_stream_id,
                to_char(p_sealed_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
                COALESCE(p_reason, ''));
$$ LANGUAGE sql STABLE PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__._wh_stream_sealed_refusal_text(UUID, TIMESTAMPTZ, TEXT) IS
  'The refusal of an append to a sealed stream, worded once for the raised WHS01, the dead letter and StreamSealedException (#1733).';

-- ===========================================================================================
-- The refusal: one finder, and one way to refuse per queue (the shape of 228)
-- ===========================================================================================

SELECT __SCHEMA__.drop_all_overloads('_wh_sealed_stream_refusals');

-- The events of a write (parallel arrays, one element per event) that a sealed stream refuses: every event of a stream
-- whose row is sealed, and every event that sorts after the first terminal event of its stream in this write (version
-- order is message id order), which that terminal event is about to seal. Reads only; the caller holds the stream locks
-- (every caller takes them before it stores), so the seal it reads cannot change before the copy.
-- <docs>fundamentals/events/stream-end-of-life#a-sealed-stream-refuses-appends</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:OutboxAppend_ToASealedStream_DeadLettersThatEvent_AndTheRestOfTheBatchCommitsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:OutboxBatch_EventsAfterATerminalEvent_AreDeadLettered_AndTheOnesBeforeItAreStoredAsync</tests>
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
  WITH candidate AS (
    SELECT c.event_id, c.stream_id, COALESCE(c.flags, 0) AS flags, c.event_type
    FROM unnest(p_event_ids, p_stream_ids, p_flags, p_event_types) AS c(event_id, stream_id, flags, event_type)
    WHERE c.stream_id IS NOT NULL
      AND c.stream_id <> __EMPTY_UUID__::uuid
  ),
  ending AS (
    SELECT DISTINCT ON (k.stream_id)
           k.stream_id,
           k.event_id,
           __SCHEMA__._wh_terminal_seal_reason(split_part(__SCHEMA__.normalize_event_type(k.event_type), ',', 1), k.event_id) AS reason
    FROM candidate k
    WHERE k.flags & c_flag_terminal <> 0
      AND k.flags & c_flags_off_the_ledger = 0
    ORDER BY k.stream_id, k.event_id
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
     OR k.event_id > e.event_id
  ORDER BY k.stream_id, k.event_id;
END;
$$ LANGUAGE plpgsql STABLE;

COMMENT ON FUNCTION __SCHEMA__._wh_sealed_stream_refusals(UUID[], UUID[], INTEGER[], TEXT[], TIMESTAMPTZ) IS
  'Finds the events of a write that a sealed stream refuses (#1733): those of a stream whose wh_stream_lifecycle row is '
  'sealed, and those after the first terminal event of their stream in the same write. Reads only; the caller holds the '
  'stream locks.';

SELECT __SCHEMA__.drop_all_overloads('_wh_refuse_sealed_stream_outbox_events');

-- The outbox: called by the outbox emit chain under its stream locks, before the copy. The batch's rows were inserted by
-- the same transaction (store_outbox_messages is the chain's caller), so moving a refused row to wh_dead_letters deletes
-- a row this transaction already holds; the copy then no longer sees it.
-- <docs>fundamentals/events/stream-end-of-life#a-sealed-stream-refuses-appends</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:OutboxAppend_ToASealedStream_DeadLettersThatEvent_AndTheRestOfTheBatchCommitsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:StoreOutboxMessages_ToASealedStream_DeadLettersTheEvent_ThroughTheCoordinatorAsync</tests>
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
  SELECT array_agg(o.message_id), array_agg(o.stream_id), array_agg(o.flags), array_agg(o.message_type)
    INTO v_ids, v_streams, v_flags, v_types
  FROM __SCHEMA__.wh_outbox o
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

SELECT __SCHEMA__.drop_all_overloads('_wh_refuse_sealed_stream_inbox_events');

-- The inbox: called by the inbox emit chain inside claim_work, after the stream ledger, so like 228's inbox refusal it
-- never locks wh_inbox. The dead letter is recorded from a read of the message row and the lease row is completed in
-- place; the completed-inbox purge removes the message row later, and until then a redelivery of it is deduplicated.
-- <docs>fundamentals/events/stream-end-of-life#a-sealed-stream-refuses-appends</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:InboxAppend_ToASealedStream_DeadLettersTheRow_AndStoresTheRestAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/WorkTablesAreLockedInOneOrderTests.CallGraph.cs:TheInboxEmitChain_AndWhatItCalls_NeverLocksATableBeforeTheClaimsOwnAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_refuse_sealed_stream_inbox_events(
  p_event_ids UUID[],
  p_stream_ids UUID[],
  p_flags INTEGER[],
  p_event_types TEXT[],
  p_instance_id UUID,
  p_now TIMESTAMPTZ
) RETURNS UUID[] AS $$
DECLARE
  c_failure_reason CONSTANT INTEGER := 25;    -- MessageFailureReason.StreamSealed
  c_flag_failed CONSTANT INTEGER := 32768;    -- MessageProcessingStatus.Failed (1 << 15)
  c_generation CONSTANT TEXT := 'whizbang/event-store-copy';  -- see _wh_refuse_sealed_stream_outbox_events
  v_ids UUID[];
  v_texts TEXT[];
BEGIN
  SELECT array_agg(r.refused_event_id), array_agg(r.refusal_text)
    INTO v_ids, v_texts
  FROM __SCHEMA__._wh_sealed_stream_refusals(p_event_ids, p_stream_ids, p_flags, p_event_types, p_now) r;

  IF v_ids IS NULL THEN
    RETURN ARRAY[]::UUID[];
  END IF;

  -- The dead letter, with the columns move_to_dead_letters records for a wh_inbox source, read from the message and
  -- lease rows without locking either. The retry budget is the message's, cumulative within the generation (#518).
  INSERT INTO __SCHEMA__.wh_dead_letters (
    dead_letter_id, source_table, source_id, stream_id, message_type, destination, perspective_name,
    envelope, metadata, failure_reason, error_text, error_fingerprint, error_fingerprint_version,
    attempts_when_dlq, dead_lettered_by, generation, recovery_attempts
  )
  SELECT gen_random_uuid(),
         s.snap_source_table,
         s.snap_message_id,
         s.snap_stream_id,
         s.snap_message_type,
         NULL,
         NULL,
         s.snap_envelope,
         s.snap_metadata,
         c_failure_reason,
         r.refusal_text,
         __SCHEMA__.compute_dead_letter_fingerprint(r.refusal_text),
         __SCHEMA__.current_dead_letter_fingerprint_version(),
         ist.attempts,
         p_instance_id,
         c_generation,
         COALESCE((
           SELECT SUM(d.recovery_attempts)::INTEGER
           FROM __SCHEMA__.wh_dead_letters d
           WHERE d.source_table = s.snap_source_table
             AND d.source_id = s.snap_message_id
             AND d.generation = c_generation
         ), 0)
  FROM unnest(v_ids, v_texts) AS r(message_id, refusal_text)
  JOIN __SCHEMA__._wh_inbox_dead_letter_snapshot(v_ids) s ON s.snap_message_id = r.message_id
  JOIN __SCHEMA__.wh_inbox_state ist ON ist.message_id = r.message_id;

  -- Completed where it stands: the claim no longer offers it, the copy no longer reads it, nothing dispatches it.
  UPDATE __SCHEMA__.wh_inbox_state ist
  SET processed_at = p_now,
      chain_emitted_at = p_now,
      status = ist.status | c_flag_failed,
      failure_reason = c_failure_reason,
      error = r.refusal_text
  FROM unnest(v_ids, v_texts) AS r(message_id, refusal_text)
  WHERE ist.message_id = r.message_id;

  RETURN v_ids;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_refuse_sealed_stream_inbox_events(UUID[], UUID[], INTEGER[], TEXT[], UUID, TIMESTAMPTZ) IS
  'Refuses the received events a sealed stream refuses (#1733): records each in wh_dead_letters (reason 25) and '
  'completes its lease row in place, without locking the message row, so the inbox emit chain keeps the canonical lock '
  'order inside claim_work. Returns the refused message ids.';

-- ===========================================================================================
-- Finalizing a stream
-- ===========================================================================================

SELECT __SCHEMA__.drop_all_overloads('wh_finalize_stream');

-- Seals a stream: state Sealed, sealed_at and sealed_reason, under the stream's append lock, so an append in flight
-- commits first and every later one is refused. Idempotent: a sealed stream keeps its first seal and is reported
-- already_sealed. An archived stream stays Archived (the tombstone) and records the seal; a restore returns it Sealed.
-- A stream the backfill has not reached yet gets its row, built from its events by 260's rules. A stream with no
-- Sourced event, or the empty stream id, has nothing to seal.
-- <docs>fundamentals/events/stream-end-of-life#finalizing-a-stream</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:Finalize_SealsTheStream_WithTheTimeAndTheReasonAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:Finalize_IsIdempotent_TheSecondCallKeepsTheFirstSealAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:Finalize_AStreamWithNoLedgerRowYet_BuildsItFromTheEvents_AndSealsItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:Finalize_AStreamWithNoSourcedEvent_HasNothingToSealAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:Finalize_WithoutAReason_IsRefusedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:Finalize_WaitsForTheStreamsAppendLockAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:Finalize_AnArchivedStream_StaysArchived_AndARestoreReturnsItSealedAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStreamSealingTests.cs:Finalize_ThroughTheRegisteredFinalizer_SealsTheStream_AndTheNextAppendThrowsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_finalize_stream(p_stream_id UUID, p_reason TEXT, p_now TIMESTAMPTZ)
RETURNS TABLE(finalize_status TEXT, stream_sealed_at TIMESTAMPTZ, stream_sealed_reason TEXT) AS $$
DECLARE
  v_sealed_at TIMESTAMPTZ;
  v_reason TEXT;
  v_inserted INTEGER;
  c_flags_off_the_ledger CONSTANT INTEGER := 24;  -- ephemeral (8) or a compaction origin (16): no ledger row (260)
BEGIN
  IF p_reason IS NULL OR btrim(p_reason) = '' THEN
    RAISE EXCEPTION USING
      ERRCODE = 'invalid_parameter_value',
      MESSAGE = 'wh_finalize_stream: a reason is required; it is recorded on the stream and carried by every refusal.';
  END IF;

  -- The empty stream id is "no stream": it has no row and nothing to seal.
  IF p_stream_id IS NULL OR p_stream_id = __EMPTY_UUID__::uuid THEN
    RETURN QUERY SELECT 'nothing_to_seal'::TEXT, NULL::TIMESTAMPTZ, NULL::TEXT;
    RETURN;
  END IF;

  -- The append lock every write path takes, so the seal and an append are never interleaved.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || p_stream_id::text));

  SELECT l.sealed_at, l.sealed_reason INTO v_sealed_at, v_reason
  FROM __SCHEMA__.wh_stream_lifecycle l
  WHERE l.stream_id = p_stream_id;

  IF FOUND THEN
    IF v_sealed_at IS NOT NULL THEN
      RETURN QUERY SELECT 'already_sealed'::TEXT, v_sealed_at, v_reason;
      RETURN;
    END IF;
    UPDATE __SCHEMA__.wh_stream_lifecycle l
    SET state = CASE WHEN l.state = 'Active' THEN 'Sealed' ELSE l.state END,
        sealed_at = p_now,
        sealed_reason = p_reason
    WHERE l.stream_id = p_stream_id;
    RETURN QUERY SELECT 'sealed'::TEXT, p_now, p_reason;
    RETURN;
  END IF;

  -- No row: the backfill has not reached the stream, or it holds no Sourced event. Built by 260's rules, sealed.
  INSERT INTO __SCHEMA__.wh_stream_lifecycle
    (stream_id, stream_type, tenant, first_appended_at, last_appended_bucket, last_version, approx_bytes,
     state, sealed_at, sealed_reason)
  SELECT p_stream_id, f.aggregate_type, f.scope ->> 't', f.created_at, date_trunc('hour', a.newest_at, 'UTC'),
         a.last_version, a.bytes, 'Sealed', p_now, p_reason
  FROM (
    SELECT es.aggregate_type, es.scope, es.created_at
    FROM __SCHEMA__.wh_event_store es
    WHERE es.stream_id = p_stream_id
      AND COALESCE(es.flags, 0) & c_flags_off_the_ledger = 0
    ORDER BY es.version
    LIMIT 1
  ) f
  CROSS JOIN LATERAL (
    SELECT MAX(es.version) AS last_version,
           MAX(es.created_at) AS newest_at,
           COALESCE(SUM(COALESCE(pg_column_size(b.event_data), 0) + COALESCE(pg_column_size(b.metadata), 0)), 0) AS bytes
    FROM __SCHEMA__.wh_event_store es
    LEFT JOIN __SCHEMA__.wh_event_body b ON b.event_id = es.event_id
    WHERE es.stream_id = p_stream_id
      AND COALESCE(es.flags, 0) & c_flags_off_the_ledger = 0
  ) a;
  GET DIAGNOSTICS v_inserted = ROW_COUNT;

  IF v_inserted = 0 THEN
    RETURN QUERY SELECT 'nothing_to_seal'::TEXT, NULL::TIMESTAMPTZ, NULL::TEXT;
    RETURN;
  END IF;
  RETURN QUERY SELECT 'sealed'::TEXT, p_now, p_reason;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_finalize_stream(UUID, TEXT, TIMESTAMPTZ) IS
  'Seals a stream for IStreamFinalizer (#1733): wh_stream_lifecycle state Sealed (an Archived row stays Archived), '
  'sealed_at and sealed_reason, under the stream''s append lock. Returns sealed; already_sealed with the first seal '
  'when it was sealed before (idempotent); nothing_to_seal for a stream with no Sourced event or the empty stream id. '
  'Raises 22023 without a reason.';

-- ===========================================================================================
-- The write paths
-- ===========================================================================================

SELECT __SCHEMA__.drop_all_overloads('_wh_raise_sealed_stream_outbox_events');

-- An awaited outbox write (the element carries RaiseOnMixedStream, set by the immediate and scoped strategies' flush)
-- to a sealed stream: raised to the caller as SQLSTATE WHS01 (StreamSealedException), so the whole write rolls back,
-- the same way 228 raises a mixed-stream refusal. Takes the write's stream locks first, in the copy's order, so the seal
-- it reads cannot change before the copy; the copy's later request for the same locks is a no-op in this transaction.
-- <docs>fundamentals/events/stream-end-of-life#a-sealed-stream-refuses-appends</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:StoreOutboxMessages_AnAwaitedWriteToASealedStream_ThrowsStreamSealedException_AndStoresNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:StoreOutboxMessages_AnAwaitedWriteAfterATerminalEventInTheSameWrite_ThrowsAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStreamSealingTests.cs:StoreOutboxMessagesAsync_AnAwaitedWriteToASealedStream_ThrowsStreamSealedExceptionAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_raise_sealed_stream_outbox_events(
  p_event_ids UUID[],
  p_stream_ids UUID[],
  p_flags INTEGER[],
  p_event_types TEXT[],
  p_raise_ids UUID[],
  p_now TIMESTAMPTZ
) RETURNS VOID AS $$
DECLARE
  v_refusal_text TEXT;
BEGIN
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || s.sid::text))
  FROM (
    SELECT DISTINCT u.sid
    FROM unnest(p_stream_ids) AS u(sid)
    WHERE u.sid IS NOT NULL
    ORDER BY u.sid
  ) AS s;

  SELECT r.refusal_text INTO v_refusal_text
  FROM __SCHEMA__._wh_sealed_stream_refusals(p_event_ids, p_stream_ids, p_flags, p_event_types, p_now) r
  WHERE r.refused_event_id = ANY(p_raise_ids)
  LIMIT 1;
  IF FOUND THEN
    RAISE EXCEPTION USING
      ERRCODE = 'WHS01',
      MESSAGE = v_refusal_text,
      HINT = 'A sealed stream has ended; append to a new stream instead.';
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_raise_sealed_stream_outbox_events(UUID[], UUID[], INTEGER[], TEXT[], UUID[], TIMESTAMPTZ) IS
  'Raises SQLSTATE WHS01 (StreamSealedException) for an outbox event its caller awaits on a sealed stream, under the '
  'write''s stream locks (#1733). Writes nothing.';

-- store_outbox_messages: last word 241, with the sealed-stream raise for an awaited event before the mixed-stream
-- refusal. Nothing else changes.

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
  v_raise_event_ids UUID[] := ARRAY[]::UUID[];
  v_refused_event_ids UUID[];
  v_claimed INTEGER;          -- 241: rows the recovery claim touched
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
      (elem->>'RecoveredFromDeadLetterId')::UUID as recovered_from
    FROM jsonb_array_elements(p_messages) as elem
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
        -- why event_id is here too.
        ORDER BY pe.stream_id, pe.event_id
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
      recovered_from_dead_letter_id
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
      v_msg.recovered_from  -- 241: NULL for every arrival but a recovery
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

  -- 228 (#1546): an event that would make its stream hold both ephemeral and Sourced events is refused before the
  -- copy. A refusal the caller awaits raises (the whole call rolls back); any other is dead-lettered, which moves its
  -- outbox row out of the queue, and is left out of the copy so the rest of the write is stored.
  -- 266 (#1733): an event the caller awaits that a sealed stream refuses raises SQLSTATE WHS01 (StreamSealedException)
  -- and the whole call rolls back, as the mixed-stream refusal raises for it. Checked first, so an awaited event that is
  -- both is reported as sealed. Every other refused event is dead-lettered by the emit chain, which stores the rest.
  IF cardinality(v_raise_event_ids) > 0 THEN
    PERFORM __SCHEMA__._wh_raise_sealed_stream_outbox_events(
      v_inserted_event_ids, v_inserted_event_streams, v_inserted_event_flags, v_inserted_event_types, v_raise_event_ids, p_now);
  END IF;

  IF cardinality(v_inserted_event_ids) > 0 THEN
    v_refused_event_ids := __SCHEMA__._wh_refuse_mixed_stream_outbox_events(
      v_inserted_event_ids, v_inserted_event_streams, v_inserted_event_flags, v_inserted_event_types,
      v_raise_event_ids, p_instance_id);
    IF cardinality(v_refused_event_ids) > 0 THEN
      v_inserted_event_ids := ARRAY(
        SELECT kept.id FROM unnest(v_inserted_event_ids) AS kept(id) WHERE kept.id <> ALL(v_refused_event_ids));
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
    SELECT DISTINCT ON (p.stream_id) p.stream_id, p.partition_number, p_instance_id, p_now
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
  '(StreamSealedException) for an event the caller awaits on a sealed stream (#1733).';


-- _emit_event_store_chain: last word 260, with the sealing check before the copy and the terminal seal in the ledger
-- upsert. Nothing else changes.

-- <docs>fundamentals/events/stream-end-of-life#the-stream-lifecycle-ledger</docs>
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
  v_streams UUID[];                        -- 266: the batch's streams, in lock order
  v_ends_a_stream BOOLEAN;                 -- 266: the batch carries a terminal event
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
  -- for one sealed stream. The common case (no stream sealed, no terminal event in the batch) costs one probe per
  -- stream of idx_stream_lifecycle_sealed, which holds only the sealed streams. Under the locks taken above, so a seal
  -- cannot land between this check and the copy.
  IF v_ends_a_stream OR EXISTS (
    SELECT 1 FROM __SCHEMA__.wh_stream_lifecycle l
    WHERE l.stream_id = ANY(v_streams)
      AND l.sealed_at IS NOT NULL
  ) THEN
    PERFORM __SCHEMA__._wh_refuse_sealed_stream_outbox_events(p_outbox_message_ids, p_instance_id, p_now);
  END IF;

  -- Phase 4.5A-equivalent: store outbox events into wh_event_store with sequential versioning.
  -- Phase H step 10 slice 1: ORDER BY o.message_id (UUIDv7 = chronological at the source)
  -- so version assignment matches canonical event_id ordering. Without this, two events stored
  -- "out of wall-clock order" (e.g., one took longer to land) would receive versions that
  -- disagree with their UUIDv7 ordering, perspective cursors advance by version, then later
  -- see an "earlier" event_id and trip the cursor-inversion detector + full replay.
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
      ROW_NUMBER() OVER (PARTITION BY o.stream_id ORDER BY o.message_id) AS row_num
    FROM __SCHEMA__.wh_outbox o
    WHERE o.message_id = ANY(p_outbox_message_ids)
      AND o.is_event = true
      AND o.stream_id IS NOT NULL
  ),
  -- Migration 072: materialize the extracted payload + built metadata ONCE, so the pointer INSERT
  -- and the ephemeral body offload read identical values without recomputing the JSON extraction.
  computed AS (
    SELECT
      oe.message_id,
      oe.stream_id,
      SPLIT_PART(__SCHEMA__.normalize_event_type(oe.message_type), ',', 1) AS aggregate_type,
      __SCHEMA__.normalize_event_type(oe.message_type) AS event_type,
      COALESCE(oe.event_data::jsonb -> 'p', oe.event_data::jsonb -> 'Payload', oe.event_data::jsonb -> 'payload') AS body_data,
      jsonb_build_object(
        c_field_message_id, COALESCE(oe.event_data::jsonb -> 'id', oe.event_data::jsonb -> c_field_message_id, oe.event_data::jsonb -> 'messageId'),
        c_field_hops, COALESCE(oe.event_data::jsonb -> 'h', oe.event_data::jsonb -> c_field_hops, oe.event_data::jsonb -> 'hops', '[]'::jsonb)
      ) || CASE
        WHEN oe.metadata IS NOT NULL
             AND jsonb_typeof(oe.metadata::jsonb -> 'ett') = 'number'
        THEN jsonb_build_object('ephemeral_expires_at',
               p_now + ((oe.metadata::jsonb ->> 'ett')::int * INTERVAL '1 second'))
        ELSE '{}'::jsonb
      END AS body_meta,
      oe.scope,
      oe.row_num,
      oe.flags
    FROM outbox_events oe
  ),
  stored_events AS (
    INSERT INTO __SCHEMA__.wh_event_store (
      event_id, stream_id, aggregate_id, aggregate_type, event_type,
      scope, version, created_at, flags
    )
    SELECT
      c.message_id,
      c.stream_id,
      c.stream_id,
      c.aggregate_type,
      c.event_type,
      c.scope,
      COALESCE((SELECT MAX(es.version) FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = c.stream_id), 0) + c.row_num,
      p_now,
      c.flags
    FROM computed c
    -- Phase H step 10 slice 4: DO NOTHING with NO constraint specifier so PG handles BOTH the
    -- event_id PK conflict (idempotent re-store) AND the idx_event_store_stream (stream_id, version)
    -- UNIQUE conflict gracefully. Conflicting rows are silently skipped; the next claim_work cycle
    -- re-attempts them with a fresh MAX(version) snapshot.
    ON CONFLICT DO NOTHING
    RETURNING event_id, stream_id, version
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
    INSERT INTO __SCHEMA__.wh_stream_digests AS d
      (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count, updated_at)
    SELECT
      __EMPTY_UUID__::uuid,  -- NOSONAR S1192: the no-source-service sentinel recurs per function; PL/pgSQL has no file-level constants
      COALESCE(c.scope::jsonb ->> 't', ''),
      c.event_type,
      c.stream_id,
      bit_xor(hashtextextended(c.message_id::text, 0)),
      bit_xor(hashtextextended(c.message_id::text, 1)),
      COUNT(*)::int,
      p_now
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    WHERE COALESCE(c.flags, 0) & 8 = 0
      -- 214 (#1484): the guarantee is a flag bit on the row. The stored metadata never carried it, so the
      -- body_meta test this replaces never matched and every at-most-once occurrence was folded.
      AND COALESCE(c.flags, 0) & c_flag_at_most_once = 0
      -- 216 (#1492): audit records are never digested.
      AND split_part(c.event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__)
    GROUP BY 1, 2, 3, 4
    ORDER BY 1, 2, 3, 4
    ON CONFLICT (origin_service_id, scope_tenant, event_type, stream_id) DO UPDATE SET
      digest_lo = d.digest_lo # EXCLUDED.digest_lo,
      digest_hi = d.digest_hi # EXCLUDED.digest_hi,
      event_count = d.event_count + EXCLUDED.event_count,
      updated_at = EXCLUDED.updated_at
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
  SELECT array_agg(event_id) INTO v_stored_event_ids FROM stored_events;
  v_stored_event_ids := COALESCE(v_stored_event_ids, '{}');
  v_count := cardinality(v_stored_event_ids);

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
    CASE WHEN p_lease_expiry IS NOT NULL THEN COALESCE(owner.assigned_instance_id, p_instance_id) ELSE NULL END,
    p_lease_expiry,
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
    CASE WHEN p_lease_expiry IS NOT NULL THEN COALESCE(owner.assigned_instance_id, p_instance_id) ELSE NULL END,
    p_lease_expiry,
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
  ) owner ON TRUE
  WHERE es.event_id = ANY(v_stored_event_ids)
    AND (es.flags & c_flag_collective) = c_flag_collective
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
  'Copies committed outbox events into the event store and creates their perspective work (146). 149: the perspective rows inherit the outbox row''s priority. 214: the digest fold leaves out at-most-once events (flags & 32). 216: and audit records (#1492). 260: keeps each stored stream''s wh_stream_lifecycle row, one upsert per batch (#1732). 266: dead-letters each event a sealed stream refuses and stores the rest; a terminal event seals its stream in the same statement (#1733).';


-- _emit_event_store_chain_for_inbox: last word 260, with the sealing check before the copy and the terminal seal in the
-- ledger upsert. Nothing else changes.

-- <docs>fundamentals/events/stream-end-of-life#the-stream-lifecycle-ledger</docs>
-- <docs>fundamentals/events/stream-end-of-life#a-sealed-stream-refuses-appends</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:InboxAppend_CreatesAndAdvancesTheRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceivedStreamOrderSqlTests.cs:TwoClaimsOfOneStream_TheHeadsClaimCommitsLast_VersionsFollowTheProducerOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AuditDigestLaneSqlTests.cs:InboxStoredAuditRecord_LeavesTheOriginsDigestLaneUntouched_WhileADomainEventFoldsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:TerminalEvent_OnTheInbox_SealsTheStream_AndLaterRowsAreRefusedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:InboxAppend_ToASealedStream_DeadLettersTheRow_AndStoresTheRestAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._emit_event_store_chain_for_inbox(
  p_instance_id UUID,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_partition_count INTEGER DEFAULT 10000
) RETURNS INTEGER AS $$
DECLARE
  v_stored_event_ids UUID[];
  v_count INTEGER;
  v_local_service_id UUID;
  c_field_message_id CONSTANT TEXT := __ENVELOPE_FIELD_MESSAGE_ID__;
  c_field_hops CONSTANT TEXT := 'Hops';
  c_source_perspective CONSTANT TEXT := __CATEGORY_PERSPECTIVE__;
  -- Migration 061: collective routing sink + flag bit (EventFlags.Collective = 1 << 0).
  c_collective_sink CONSTANT TEXT := '__collective__';
  c_flag_collective CONSTANT INTEGER := 1;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  -- 260: EventFlags.Ephemeral (1 << 3) | EventFlags.Compacted (1 << 4), the events the lifecycle ledger does not count.
  c_flags_off_the_ledger CONSTANT INTEGER := 24;
  -- 228 (#1546): the rows this pass would copy, for the refusal before the copy.
  v_candidate_ids UUID[];
  v_candidate_streams UUID[];
  v_candidate_flags INTEGER[];
  v_candidate_types TEXT[];
  c_flag_terminal CONSTANT INTEGER := 64;  -- 266: EventFlags.Terminal (1 << 6)
  v_sealed_refused UUID[];                 -- 266: the rows a sealed stream refused
BEGIN
  -- Migration 087: resolve the LOCAL service id once. store_inbox_messages (062) COALESCEs a
  -- missing envelope SourceServiceId to the local id, so a wh_inbox row attributed to SELF (or
  -- zero) is a locally-originated event (loopback), its origin_service_id must stay NULL,
  -- matching the 046 contract ("NULL for locally-originated events").
  SELECT service_id INTO v_local_service_id FROM __SCHEMA__.wh_service_config LIMIT 1;

  -- Phase H step 10 slice 2: per-stream advisory locks. Mirrors _emit_event_store_chain (lines
  -- 311-329). Without these, two concurrent claim_work calls (e.g., NOTIFY-driven wake racing
  -- a heartbeat-driven poll) can both read MAX(version)=N from wh_event_store for the same
  -- stream and both attempt INSERT at version=N+1, violating idx_event_store_stream
  -- UNIQUE(stream_id, version) (PG error 23505). Reproduced in production on a consumer's
  -- service during job creation. Lock order is hashtext(stream_id::text), sorted ASC,
  -- ensures deadlock-free nesting between any pair of transactions touching overlapping stream
  -- sets. pg_advisory_xact_lock auto-releases at commit/rollback.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || sid::text))
  FROM (
    -- 162: every column this pass reads is on wh_inbox_state, so the lock pass never touches the
    -- wide message row at all. chain_emitted_at moved here precisely so this predicate stays on one
    -- table and one partial index can cover it; see idx_inbox_state_chain_pending.
    SELECT DISTINCT ist.stream_id AS sid
    FROM __SCHEMA__.wh_inbox_state ist
    WHERE ist.instance_id = p_instance_id
      AND ist.lease_expiry > p_now
      AND ist.processed_at IS NULL
      AND ist.is_event = true
      AND ist.stream_id IS NOT NULL
      -- 158: only rows this chain has never read (idx_inbox_state_chain_pending); every held row was
      -- read against the event store on every poll before, twice.
      AND ist.chain_emitted_at IS NULL
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = ist.message_id
      )
    ORDER BY ist.stream_id
  ) AS streams_to_lock;

  -- 228 (#1546): a received event that would make its stream hold both ephemeral and Sourced events is refused
  -- before the copy. Nobody awaits a claim, so every refusal here is dead-lettered: recorded in wh_dead_letters and
  -- its lease row completed in place, so the copy below no longer sees it and the claim never offers it again. The
  -- message row is not touched here: this runs inside claim_work after the stream ledger, and wh_inbox comes first
  -- in the canonical lock order. The rows are the ones the copy reads, by the same predicate, under the locks taken
  -- above.
  SELECT array_agg(ist.message_id), array_agg(ist.stream_id), array_agg(i.flags), array_agg(i.message_type)
    INTO v_candidate_ids, v_candidate_streams, v_candidate_flags, v_candidate_types
  FROM __SCHEMA__.wh_inbox_state ist
  JOIN __SCHEMA__.wh_inbox i ON i.message_id = ist.message_id
  WHERE ist.instance_id = p_instance_id
    AND ist.lease_expiry > p_now
    AND ist.processed_at IS NULL
    AND ist.is_event = true
    AND ist.stream_id IS NOT NULL
    AND ist.chain_emitted_at IS NULL
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = ist.message_id
    );
  -- 266 (#1733): a sealed stream refuses every append, so its rows are dead-lettered and completed in place first, as
  -- the mixed-stream refusal does, and leave the candidates. The common case (no stream sealed, no terminal event)
  -- costs one probe per stream of idx_stream_lifecycle_sealed, which holds only the sealed streams.
  IF v_candidate_ids IS NOT NULL
     AND (EXISTS (
            SELECT 1 FROM unnest(v_candidate_flags) AS f(bits)
            WHERE COALESCE(f.bits, 0) & c_flag_terminal <> 0
              AND COALESCE(f.bits, 0) & c_flags_off_the_ledger = 0)
          OR EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_stream_lifecycle l
            WHERE l.stream_id = ANY(v_candidate_streams)
              AND l.sealed_at IS NOT NULL)) THEN
    v_sealed_refused := __SCHEMA__._wh_refuse_sealed_stream_inbox_events(
      v_candidate_ids, v_candidate_streams, v_candidate_flags, v_candidate_types, p_instance_id, p_now);
    IF cardinality(v_sealed_refused) > 0 THEN
      SELECT array_agg(k.id ORDER BY k.ord), array_agg(k.sid ORDER BY k.ord), array_agg(k.bits ORDER BY k.ord),
             array_agg(k.type_name ORDER BY k.ord)
        INTO v_candidate_ids, v_candidate_streams, v_candidate_flags, v_candidate_types
      FROM unnest(v_candidate_ids, v_candidate_streams, v_candidate_flags, v_candidate_types)
             WITH ORDINALITY AS k(id, sid, bits, type_name, ord)
      WHERE k.id <> ALL(v_sealed_refused);
    END IF;
  END IF;
  IF v_candidate_ids IS NOT NULL THEN
    PERFORM __SCHEMA__._wh_refuse_mixed_stream_inbox_events(
      v_candidate_ids, v_candidate_streams, v_candidate_flags, v_candidate_types, p_instance_id, p_now);
  END IF;

  -- Auto-create event_store rows for inbox rows owned by this instance that:
  --   - are events (is_event = true)
  --   - have a stream_id
  --   - aren't yet in wh_event_store (idempotent, ON CONFLICT swallows duplicates)
  -- Bounded by lease ownership so we don't scan the whole inbox every tick.
  -- Phase H step 10 slice 1: ORDER BY i.message_id (UUIDv7 = chronological at the source) so
  -- version assignment matches canonical event_id order. See _emit_event_store_chain above
  -- for the rationale, same fix applies to the inbox backfill path.
  -- Migration 061: i.flags carried into wh_event_store.flags (was dropped here previously).
  WITH inbox_events AS (
    SELECT
      i.message_id,
      ist.stream_id,
      i.message_type,
      i.event_data,
      i.metadata,
      i.scope,
      i.flags,
      i.source_service_id,
      i.source_commit_sequence,
      ist.received_at
    -- 162: this pass needs the message body, so both tables. The LEASE table drives the join, so
    -- selection happens on the narrow table and the wide row is fetched only for the rows that
    -- survive it, which is the opposite of scanning the wide row to apply the same predicate.
    FROM __SCHEMA__.wh_inbox_state ist
    JOIN __SCHEMA__.wh_inbox i ON i.message_id = ist.message_id
    WHERE ist.instance_id = p_instance_id
      AND ist.lease_expiry > p_now
      AND ist.processed_at IS NULL
      AND ist.is_event = true
      AND ist.stream_id IS NOT NULL
      AND ist.chain_emitted_at IS NULL  -- 158: see the lock pass above
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = ist.message_id
      )
  ),
  -- 230 (#1607): a received stream is stored in the producer's order, however many claims carry it. A claim
  -- numbers the rows it holds after the stream's tail, so a claim that holds a stream's later events and reaches
  -- this pass first would number them ahead of the head another claim leased earlier and has not stored yet.
  -- The head is visible here as a pending event of the stream that this pass does not hold: not yet in the event
  -- store, and not leased to this instance under a live lease (a peer's lease, an expired one, none, or a lease a
  -- concurrent claim has not committed). Every row of this batch that sorts after the stream's first such event
  -- waits: it is not stored and not stamped, so the next claim re-reads it, and fetch_inbox_batch does not hand
  -- it to the dispatcher before it is stored. Rows before it are stored now, so the head of a stream never waits.
  -- The order is the message id, the order this pass has always numbered by (slice 1): a fan-out child's id is
  -- derived from its composite and ordinal, so it sorts in producer order whether or not it carries an origin
  -- commit sequence.
  --
  -- One probe per stream, over idx_inbox_state_stream_order (stream_id, received_at, message_id), bounded by the
  -- batch: only rows received no later than the batch's newest row and sorting before its last one can hold it
  -- back. In a stream's FIFO backlog that range is the batch and the rows before it, never the rows queued behind
  -- it, and the lease test is answered from the index, so only a row this pass does not hold reads the heap. No
  -- index is added: wh_inbox_state's index count is a gated design property (InboxStateWriteCostScenarioTests).
  -- An event received after the rows it precedes is not waited for; that is the transport reordering two
  -- deliveries, which the receiver cannot see coming.
  batch_streams AS (
    SELECT ie.stream_id,
           MAX(ie.received_at) AS newest_received_at,
           (array_agg(ie.message_id ORDER BY ie.message_id DESC))[1] AS last_message_id
    FROM inbox_events ie
    GROUP BY ie.stream_id
  ),
  held_back_from AS (
    SELECT s.stream_id,
           (SELECT b.message_id
            FROM __SCHEMA__.wh_inbox_state b
            WHERE b.stream_id = s.stream_id
              AND b.processed_at IS NULL
              AND b.received_at <= s.newest_received_at
              AND b.message_id < s.last_message_id
              AND NOT COALESCE(b.instance_id = p_instance_id AND b.lease_expiry > p_now, false)
              AND b.is_event = true
              AND b.chain_emitted_at IS NULL
              AND NOT EXISTS (
                SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = b.message_id
              )
            ORDER BY b.message_id
            LIMIT 1) AS first_unheld
    FROM batch_streams s
  ),
  storable_events AS (
    SELECT ie.*,
           ROW_NUMBER() OVER (PARTITION BY ie.stream_id ORDER BY ie.message_id) AS row_num
    FROM inbox_events ie
    JOIN held_back_from h ON h.stream_id = ie.stream_id
    WHERE h.first_unheld IS NULL OR ie.message_id < h.first_unheld
  ),
  -- Phase H step 10 slice 3: version computed via correlated subquery rather than a
  -- pre-materialized CTE. Inside the per-stream advisory lock the values are equivalent, but
  -- the per-row form is defensive, see _emit_event_store_chain above for the rationale.
  -- Migration 072: materialize the extracted payload + built metadata ONCE (see outbox fn above).
  computed AS (
    SELECT
      ie.message_id,
      ie.stream_id,
      SPLIT_PART(__SCHEMA__.normalize_event_type(ie.message_type), ',', 1) AS aggregate_type,
      __SCHEMA__.normalize_event_type(ie.message_type) AS event_type,
      COALESCE(ie.event_data::jsonb -> 'p', ie.event_data::jsonb -> 'Payload', ie.event_data::jsonb -> 'payload') AS body_data,
      jsonb_build_object(
        c_field_message_id, COALESCE(ie.event_data::jsonb -> 'id', ie.event_data::jsonb -> c_field_message_id, ie.event_data::jsonb -> 'messageId'),
        c_field_hops, COALESCE(ie.event_data::jsonb -> 'h', ie.event_data::jsonb -> c_field_hops, ie.event_data::jsonb -> 'hops', '[]'::jsonb)
      ) || CASE
        WHEN ie.metadata IS NOT NULL
             AND jsonb_typeof(ie.metadata::jsonb -> 'ett') = 'number'
        THEN jsonb_build_object('ephemeral_expires_at',
               p_now + ((ie.metadata::jsonb ->> 'ett')::int * INTERVAL '1 second'))
        ELSE '{}'::jsonb
      END AS body_meta,
      ie.scope,
      ie.row_num,
      ie.flags,
      -- Migration 087: normalize the received origin, self/zero means locally-originated (NULL).
      CASE
        WHEN ie.source_service_id IS NULL
             OR ie.source_service_id = __EMPTY_UUID__::uuid
             OR ie.source_service_id = v_local_service_id
        THEN NULL
        ELSE ie.source_service_id
      END AS origin_service_id,
      NULLIF(ie.source_commit_sequence, 0) AS origin_commit_sequence
    FROM storable_events ie
  ),
  stored_events AS (
    INSERT INTO __SCHEMA__.wh_event_store (
      event_id, stream_id, aggregate_id, aggregate_type, event_type,
      scope, version, created_at, flags, origin_service_id, origin_commit_sequence
    )
    SELECT
      c.message_id,
      c.stream_id,
      c.stream_id,
      c.aggregate_type,
      c.event_type,
      c.scope,
      COALESCE((SELECT MAX(es.version) FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = c.stream_id), 0) + c.row_num,
      p_now,
      c.flags,
      -- Migration 087: stamp the origin identity the transport delivered (046 columns were never
      -- populated by the emit chain before this, consumer-side origin-keyed verification needs them).
      c.origin_service_id,
      CASE WHEN c.origin_service_id IS NULL THEN NULL ELSE c.origin_commit_sequence END
    FROM computed c
    -- Phase H step 10 slice 4: DO NOTHING with NO constraint specifier so PG handles BOTH the
    -- event_id PK conflict (idempotent re-store) AND the idx_event_store_stream (stream_id, version)
    -- UNIQUE conflict gracefully. Conflicting rows are silently skipped; the next claim_work cycle
    -- re-attempts them with a fresh MAX(version) snapshot.
    ON CONFLICT DO NOTHING
    RETURNING event_id, stream_id, version
  ),
  -- Migration 077 (full split): offload EVERY body (see outbox fn above).
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
    INSERT INTO __SCHEMA__.wh_stream_digests AS d
      (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count, updated_at)
    SELECT
      COALESCE(c.origin_service_id, __EMPTY_UUID__::uuid),
      COALESCE(c.scope::jsonb ->> 't', ''),
      c.event_type,
      c.stream_id,
      bit_xor(hashtextextended(c.message_id::text, 0)),
      bit_xor(hashtextextended(c.message_id::text, 1)),
      COUNT(*)::int,
      p_now
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    WHERE COALESCE(c.flags, 0) & 8 = 0
      -- 214 (#1484): the guarantee is a flag bit on the row. The stored metadata never carried it, so the
      -- body_meta test this replaces never matched and every at-most-once occurrence was folded.
      AND COALESCE(c.flags, 0) & c_flag_at_most_once = 0
      -- 216 (#1492): audit records are never digested; a received one no longer lands in its origin's lane.
      AND split_part(c.event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__)
    GROUP BY 1, 2, 3, 4
    ORDER BY 1, 2, 3, 4
    ON CONFLICT (origin_service_id, scope_tenant, event_type, stream_id) DO UPDATE SET
      digest_lo = d.digest_lo # EXCLUDED.digest_lo,
      digest_hi = d.digest_hi # EXCLUDED.digest_hi,
      event_count = d.event_count + EXCLUDED.event_count,
      updated_at = EXCLUDED.updated_at
  ),
  -- 260 (#1732): the stream lifecycle ledger. One row per stream this call stored a Sourced event on, in the same
  -- statement as the copy: one set-based upsert per batch, grouped by stream (never a write per event), in stream id
  -- order under the per-stream advisory locks taken above, so it can wait only on a batch that holds the same stream.
  -- An event that is ephemeral or a compaction origin (flags & 24) does not count, and neither does a stream-less event:
  -- an ephemeral stream is reaped by its time to live and never reaches end of life. The update touches no indexed
  -- column (last_version, approx_bytes, and a bucket that changes only when the UTC hour does), so it stays heap-only.
  -- 266 (#1733): a terminal event among them seals the stream in this statement (see _emit_event_store_chain).
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
  SELECT array_agg(event_id) INTO v_stored_event_ids FROM stored_events;
  v_stored_event_ids := COALESCE(v_stored_event_ids, '{}');
  v_count := cardinality(v_stored_event_ids);

  -- 158: stamp the held rows the chain is finished with, so the next poll reads none of them.
  -- "Finished" is the row's event being in the event store: either this pass inserted it, or it was
  -- already there and the pass above excluded the row for exactly that reason. A row whose insert hit
  -- ON CONFLICT DO NOTHING has no event-store row, so it stays unstamped and the next poll re-attempts
  -- it with a fresh version snapshot, which is the contract that conflict clause states. The stamp runs
  -- before the nothing-was-stored return below, because that return is the steady state of a busy
  -- instance (every held event already chained) and the case the stamp exists for. The stamp and the
  -- inserts share this transaction, so a failure leaves both undone.
  -- 162: the stamp is the write this migration helps most. It used to rewrite a row carrying the
  -- whole message body across twenty-four indexes to set one timestamp, measured at about 41 blocks
  -- per row stamped with only 30 of those spent finding the rows. It now writes the narrow table,
  -- and the predicate that finds them is local to it.
  UPDATE __SCHEMA__.wh_inbox_state ist
  SET chain_emitted_at = p_now
  WHERE ist.instance_id = p_instance_id
    AND ist.lease_expiry > p_now
    AND ist.processed_at IS NULL
    AND ist.is_event = true
    AND ist.stream_id IS NOT NULL
    AND ist.chain_emitted_at IS NULL
    AND EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = ist.message_id
    );

  IF v_count = 0 THEN
    RETURN 0;
  END IF;

  -- Auto-create perspective_events for the newly-stored events.
  -- Phase H step 6 slice 2: populate partition_number for symmetric load balancing.
  -- Slice 26.14: route the lease through wh_active_streams (live owner wins; fall back to
  -- commit instance when no live owner). Mirror of the outbox-side change in
  -- _emit_event_store_chain.
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
    CASE WHEN p_lease_expiry IS NOT NULL THEN COALESCE(owner.assigned_instance_id, p_instance_id) ELSE NULL END,
    p_lease_expiry,
    COALESCE(src.priority, 150)   -- 149: the projection is scheduled with the source row's number
  FROM __SCHEMA__.wh_event_store es
  LEFT JOIN __SCHEMA__.wh_inbox src ON src.message_id = es.event_id
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
  ) owner ON TRUE
  WHERE es.event_id = ANY(v_stored_event_ids)
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events pe_check
      WHERE pe_check.stream_id = es.stream_id
        AND pe_check.perspective_name = ma.target_name
        AND pe_check.event_id = es.event_id
    )
  ON CONFLICT ON CONSTRAINT uq_perspective_event DO NOTHING;

  -- 100: reconcile self-declares the rewind. Any of THIS invocation's fresh work items whose
  -- event id slots BELOW its cursor's last-applied event is a straggler by construction, a
  -- backfilled event keeps its ORIGINAL id, while its fresh local commit_sequence sits above
  -- the cursor and hides it from the runtime inversion detector forever. Flag the cursor
  -- RewindRequired with the earliest straggler as trigger (min-merge with any existing
  -- trigger, mirroring complete_perspective_checkpoint's straggler path); the worker's
  -- existing rewind routing replays the stream through the corrected order.
  UPDATE __SCHEMA__.wh_perspective_cursors pc
  SET status = pc.status | 32,  -- RewindRequired flag (1 << 5)
      rewind_trigger_event_id = CASE
        WHEN pc.rewind_trigger_event_id IS NULL THEN s.straggler_event_id
        WHEN s.straggler_event_id < pc.rewind_trigger_event_id THEN s.straggler_event_id
        ELSE pc.rewind_trigger_event_id
      END,
      rewind_flagged_at = p_now,
      rewind_first_flagged_at = COALESCE(pc.rewind_first_flagged_at, p_now)
  FROM (
    SELECT pe.stream_id, pe.perspective_name,
           (array_agg(pe.event_id ORDER BY pe.event_id))[1] AS straggler_event_id
    FROM __SCHEMA__.wh_perspective_events pe
    JOIN __SCHEMA__.wh_perspective_cursors c
      ON c.stream_id = pe.stream_id AND c.perspective_name = pe.perspective_name
    WHERE pe.event_id = ANY(v_stored_event_ids)
      AND pe.processed_at IS NULL
      AND c.last_event_id IS NOT NULL
      AND pe.event_id < c.last_event_id
    GROUP BY pe.stream_id, pe.perspective_name
  ) s
  WHERE pc.stream_id = s.stream_id AND pc.perspective_name = s.perspective_name;

  -- Migration 061: collective-event routing (inbox path). See _emit_event_store_chain above.
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
    CASE WHEN p_lease_expiry IS NOT NULL THEN COALESCE(owner.assigned_instance_id, p_instance_id) ELSE NULL END,
    p_lease_expiry,
    COALESCE(src.priority, 150)   -- 149: the projection is scheduled with the source row's number
  FROM __SCHEMA__.wh_event_store es
  LEFT JOIN __SCHEMA__.wh_inbox src ON src.message_id = es.event_id
  LEFT JOIN LATERAL (
    SELECT ast.assigned_instance_id
    FROM __SCHEMA__.wh_active_streams ast
    WHERE ast.stream_id = es.stream_id
      AND ast.assigned_instance_id IS NOT NULL
      AND EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_service_instances si
        WHERE si.instance_id = ast.assigned_instance_id
      )
  ) owner ON TRUE
  WHERE es.event_id = ANY(v_stored_event_ids)
    AND (es.flags & c_flag_collective) = c_flag_collective
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events pe_check
      WHERE pe_check.stream_id = es.stream_id
        AND pe_check.perspective_name = c_collective_sink
        AND pe_check.event_id = es.event_id
    )
  ON CONFLICT ON CONSTRAINT uq_perspective_event DO NOTHING;

  -- Slice 26.4: wake the commit-order stamper. See _emit_event_store_chain above for
  -- the rationale and dedup semantics. Inbox backfill is generally a smaller fan-in
  -- than outbox-emit, but the NOTIFY is just as cheap and keeps the stamper hot path
  -- responsive whether events arrive locally or via transport.
  PERFORM __SCHEMA__._queue_doorbell('wh_committed', '');

  RETURN v_count;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._emit_event_store_chain_for_inbox(UUID, TIMESTAMPTZ, TIMESTAMPTZ, INTEGER) IS
  'Copies leased inbox events into the event store, stamps their origin and creates their perspective work (162). 214: the digest fold leaves out at-most-once events (flags & 32). 216: and audit records (#1492). 228: an event that would make its stream hold both ephemeral and Sourced events is dead-lettered instead (WHIZ133, #1546). 230: a row waits behind an earlier pending row of its stream that this pass does not hold, so a received stream is stored in the producer''s order (#1607). 260: keeps each stored stream''s wh_stream_lifecycle row, one upsert per batch (#1732). 266: dead-letters each received event a sealed stream refuses and stores the rest; a terminal event seals its stream in the same statement (#1733).';

-- wh_append_event: last word 260, with the sealed-stream refusal before the insert and the terminal seal in the ledger
-- upsert. Nothing else changes; the signature is the same, and the overloads are cleared first because this name has had
-- more than one arity (README rule 5).
SELECT __SCHEMA__.drop_all_overloads('wh_append_event');

-- <docs>fundamentals/events/stream-end-of-life#the-stream-lifecycle-ledger</docs>
-- <docs>fundamentals/events/stream-end-of-life#a-sealed-stream-refuses-appends</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:DirectAppend_CreatesAndAdvancesTheRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:DirectAppend_ThroughTheEventStore_CreatesTheRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:EphemeralAndCompactionOriginEvents_CreateNoRow_WhileASourcedEventDoesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:EventsWithoutAStream_CreateNoRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EventStoreConcurrentAppendTests.cs:ParallelAppendsToOneStream_AllSucceed_WithVersionsZeroToNMinusOneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DirectAppendStreamDigestTests.cs:MixedPathStream_TableDigestEqualsARecomputationOverAllItsEventsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DirectAppendMixedStreamGuardTests.cs:DirectAppend_SourcedEventOnAnEphemeralStream_ThrowsAndStoresNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:TerminalEvent_DirectAppend_SealsTheStream_AndTheNextAppendThrowsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:DirectAppend_ToASealedStream_ThrowsStreamSealedException_AndStoresNothingAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStreamSealingTests.cs:AppendAsync_TerminalEvent_SealsTheStream_AndTheNextAppendThrowsStreamSealedExceptionAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_append_event(
  p_event_id UUID,
  p_stream_id UUID,
  p_aggregate_id UUID,
  p_aggregate_type TEXT,
  p_event_type TEXT,
  p_event_data JSONB,
  p_metadata JSONB,
  p_scope JSONB,
  p_created_at TIMESTAMPTZ,
  -- 228 (#1546): the event's kind bits (EventFlags.Ephemeral, EventFlags.Compacted), stored on the row as the outbox
  -- and inbox copies store them. Defaulted so a caller from before this migration still resolves.
  p_flags INTEGER DEFAULT 0
) RETURNS INTEGER AS $$
DECLARE
  v_version INTEGER;
  v_flags INTEGER;
  v_refusal RECORD;
  v_body_bytes BIGINT;  -- 260: the size of the body row as written, for the lifecycle ledger
  c_flag_ephemeral CONSTANT INTEGER := 8;     -- EventFlags.Ephemeral (1 << 3), as the emit chain tests it
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  -- 260: EventFlags.Ephemeral (1 << 3) | EventFlags.Compacted (1 << 4), the events the lifecycle ledger does not count.
  c_flags_off_the_ledger CONSTANT INTEGER := 24;
  c_flag_terminal CONSTANT INTEGER := 64;  -- 266: EventFlags.Terminal (1 << 6)
  v_sealed_at TIMESTAMPTZ;                 -- 266: when the stream was sealed, if it was
  v_sealed_reason TEXT;
  v_ends_stream BOOLEAN;                   -- 266: this event seals its stream
BEGIN
  -- The emit chains' key for this stream (214), so a direct append and a chain emit serialize too.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || p_stream_id::text));

  -- 266 (#1733): a sealed stream refuses every append. The caller awaits the append, so the refusal is raised (SQLSTATE
  -- WHS01, which both drivers surface as StreamSealedException) and nothing is stored. One probe of
  -- idx_stream_lifecycle_sealed, under the lock above.
  SELECT l.sealed_at, l.sealed_reason INTO v_sealed_at, v_sealed_reason
  FROM __SCHEMA__.wh_stream_lifecycle l
  WHERE l.stream_id = p_stream_id
    AND l.sealed_at IS NOT NULL;
  IF FOUND THEN
    RAISE EXCEPTION USING
      ERRCODE = 'WHS01',
      MESSAGE = __SCHEMA__._wh_stream_sealed_refusal_text(p_stream_id, v_sealed_at, v_sealed_reason),
      HINT = 'A sealed stream has ended; append to a new stream instead.';
  END IF;

  -- 228 (#1546): an event that would make its stream hold both ephemeral and Sourced events is refused, under the
  -- lock above, before anything is stored. The caller awaits the append, so the refusal is raised (SQLSTATE WH133,
  -- which both drivers surface as MixedStreamEventException). The check only reads.
  SELECT r.refusal_text, r.refusal_detail INTO v_refusal
  FROM __SCHEMA__._wh_mixed_stream_refusals(
    ARRAY[p_event_id], ARRAY[p_stream_id], ARRAY[COALESCE(p_flags, 0)], ARRAY[p_event_type]) r
  LIMIT 1;
  IF FOUND THEN
    RAISE EXCEPTION USING
      ERRCODE = 'WH133',
      MESSAGE = v_refusal.refusal_text,
      DETAIL = v_refusal.refusal_detail::TEXT,
      HINT = 'Publish the ephemeral events on their own stream.';
  END IF;

  -- A separate statement: its snapshot is taken after the lock was granted.
  INSERT INTO __SCHEMA__.wh_event_store (
    event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, created_at, flags
  )
  SELECT
    p_event_id,
    p_stream_id,
    p_aggregate_id,
    p_aggregate_type,
    p_event_type,
    p_scope,
    COALESCE((SELECT MAX(es.version) FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = p_stream_id), -1) + 1,
    p_created_at,
    COALESCE(p_flags, 0)
  RETURNING version, flags INTO v_version, v_flags;

  -- The body half of the pointer/body split (077). Constraint-less ON CONFLICT, as the emit chain writes it:
  -- event_id is the table's only key.
  INSERT INTO __SCHEMA__.wh_event_body (event_id, event_data, metadata)
  VALUES (p_event_id, p_event_data, p_metadata)
  ON CONFLICT DO NOTHING
  -- 260: measured on the row the insert formed, as the emit chains measure theirs (the values as the row holds them,
  -- before any TOAST compression), so the two paths count a body the same way.
  RETURNING pg_column_size(event_data) + pg_column_size(metadata) INTO v_body_bytes;

  -- 225 (#1600): fold the event into its stream digest bucket, as the emit chain's digest_folds does, still under
  -- the stream lock taken above. One event, so one row: the chain's bit_xor and COUNT(*) over a single event are
  -- its two hashes and 1.
  IF COALESCE(v_flags, 0) & c_flag_ephemeral = 0
     AND COALESCE(v_flags, 0) & c_flag_at_most_once = 0
     AND split_part(p_event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__) THEN
    INSERT INTO __SCHEMA__.wh_stream_digests AS d
      (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count, updated_at)
    VALUES (
      __EMPTY_UUID__::uuid,  -- NOSONAR S1192: the local-origin sentinel recurs per function; PL/pgSQL has no file-level constants
      COALESCE(p_scope ->> 't', ''),
      p_event_type,
      p_stream_id,
      hashtextextended(p_event_id::text, 0),
      hashtextextended(p_event_id::text, 1),
      1,
      p_created_at
    )
    ON CONFLICT (origin_service_id, scope_tenant, event_type, stream_id) DO UPDATE SET
      digest_lo = d.digest_lo # EXCLUDED.digest_lo,
      digest_hi = d.digest_hi # EXCLUDED.digest_hi,
      event_count = d.event_count + EXCLUDED.event_count,
      updated_at = EXCLUDED.updated_at;
  END IF;

  -- 260 (#1732): the stream lifecycle ledger, as the emit chains keep it, still under the stream lock taken above. The
  -- same events count (not ephemeral, not a compaction origin, on a stream), and the same columns move: an append never
  -- writes an indexed column, so the update is heap-only.
  -- 266 (#1733): a terminal event seals the stream in this transaction; any other append writes the sealing columns
  -- back unchanged, which keeps the update heap-only.
  IF COALESCE(v_flags, 0) & c_flags_off_the_ledger = 0 AND p_stream_id <> __EMPTY_UUID__::uuid THEN
    v_ends_stream := COALESCE(v_flags, 0) & c_flag_terminal <> 0;
    INSERT INTO __SCHEMA__.wh_stream_lifecycle AS l
      (stream_id, stream_type, tenant, first_appended_at, last_appended_bucket, last_version, approx_bytes,
       state, sealed_at, sealed_reason)
    VALUES (
      p_stream_id,
      p_aggregate_type,
      p_scope ->> 't',
      p_created_at,
      date_trunc('hour', p_created_at, 'UTC'),
      v_version,
      COALESCE(v_body_bytes, 0),
      CASE WHEN v_ends_stream THEN 'Sealed' ELSE 'Active' END,
      CASE WHEN v_ends_stream THEN p_created_at END,
      CASE WHEN v_ends_stream THEN __SCHEMA__._wh_terminal_seal_reason(split_part(p_event_type, ',', 1), p_event_id) END
    )
    ON CONFLICT (stream_id) DO UPDATE SET
      last_version = GREATEST(l.last_version, EXCLUDED.last_version),
      approx_bytes = l.approx_bytes + EXCLUDED.approx_bytes,
      last_appended_bucket = GREATEST(l.last_appended_bucket, EXCLUDED.last_appended_bucket),
      state = CASE WHEN EXCLUDED.sealed_at IS NOT NULL AND l.sealed_at IS NULL AND l.state = 'Active'
                   THEN 'Sealed' ELSE l.state END,
      sealed_reason = CASE WHEN l.sealed_at IS NULL THEN EXCLUDED.sealed_reason ELSE l.sealed_reason END,
      sealed_at = COALESCE(l.sealed_at, EXCLUDED.sealed_at);
  END IF;

  RETURN v_version;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_append_event(UUID, UUID, UUID, TEXT, TEXT, JSONB, JSONB, JSONB, TIMESTAMPTZ, INTEGER) IS
'Appends one event to a stream for IEventStore.AppendAsync (both drivers) and returns the version it assigned. Takes the per-stream advisory lock the emit chains take (hashtext(''wh_event_store:'' || stream_id)), then inserts at COALESCE(MAX(version), -1) + 1 in a separate statement so the read sees every append committed during the wait (224, #1555). 225: folds the stored event into wh_stream_digests as the emit chain does, unless it is ephemeral, at-most-once or of a digest-exempt type (#1600). 228: stores the event''s kind bits (p_flags) and refuses, raising SQLSTATE WH133, an event that would make its stream hold both ephemeral and Sourced events (WHIZ133, #1546). 260: keeps the stream''s wh_stream_lifecycle row (#1732). 266: refuses an append to a sealed stream, raising SQLSTATE WHS01 (StreamSealedException), and a terminal event seals its stream (#1733).';

-- wh_restore_archived_stream: last word 262, returning a sealed stream to Sealed rather than Active. Nothing else
-- changes.

SELECT __SCHEMA__.drop_all_overloads('wh_restore_archived_stream');

-- <docs>fundamentals/events/archive-store#restoring-an-archive</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Restore_PutsTheStreamBackExactly_CursorsDigestsAndPositionsIncludedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Restore_APrefix_UnderItsSurvivingTailAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Restore_IsIdempotent_TheSecondCallFindsNothingArchivedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:ApproxBytes_ArchiveSubtractsWhatLeft_RestoreAddsItBackAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Restore_AHotEventAtAnArchivedVersion_IsAVersionConflict_AndRestoresNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Restore_ARowArchivedBeforeTheStoreKeptPositions_IsStoredUnflaggedForTheStamperAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperArchiveStoreTests.cs:WholeStream_OnADapperMigratedDatabase_ArchivesAndRestoresAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:Finalize_AnArchivedStream_StaysArchived_AndARestoreReturnsItSealedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_restore_archived_stream(p_stream_id UUID)
RETURNS TABLE(restore_status TEXT, events_restored BIGINT, origin_generation BIGINT) AS $$
DECLARE
  v_generation BIGINT;
  v_ids UUID[];
  v_lanes UUID[];
  v_mins BIGINT[];
  v_maxs BIGINT[];
  v_i INTEGER;
  v_bytes BIGINT;
  v_hot_bytes BIGINT;
  v_type TEXT;
  v_tenant TEXT;
  v_first_at TIMESTAMPTZ;
  v_bucket TIMESTAMPTZ;
  v_last BIGINT;
  c_flag_ephemeral CONSTANT INTEGER := 8;
  c_flag_at_most_once CONSTANT INTEGER := 32;
  c_flags_off_the_ledger CONSTANT INTEGER := 24;  -- ephemeral (8) or a compaction origin (16): no ledger row (260)
BEGIN
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || p_stream_id::text));
  SELECT (s.setting_value)::bigint INTO v_generation
  FROM __SCHEMA__.wh_settings s WHERE s.setting_key = 'integrity_origin_generation';

  IF NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_archive a WHERE a.stream_id = p_stream_id) THEN
    RETURN QUERY SELECT 'nothing_archived'::TEXT, 0::BIGINT, v_generation;
    RETURN;
  END IF;

  -- Two events at one version would break every reader of the stream: refuse and move nothing.
  IF EXISTS (
    SELECT 1
    FROM __SCHEMA__.wh_event_archive a
    JOIN __SCHEMA__.wh_event_store es ON es.stream_id = a.stream_id AND es.version = a.version AND es.event_id <> a.event_id
    WHERE a.stream_id = p_stream_id) THEN
    RETURN QUERY SELECT 'version_conflict'::TEXT, 0::BIGINT, v_generation;
    RETURN;
  END IF;

  -- Active again before the events go back, so the archived-version guard lets them in. 266 (#1733): Sealed instead
  -- when the stream was sealed, so a restore never reopens a stream that ended.
  UPDATE __SCHEMA__.wh_stream_lifecycle l
  SET state = CASE WHEN l.state = 'Archived' THEN (CASE WHEN l.sealed_at IS NOT NULL THEN 'Sealed' ELSE 'Active' END)
                   ELSE l.state END,
      archive_locator = NULL
  WHERE l.stream_id = p_stream_id;

  -- Each event at its original version and position: a restored row with its commit sequence is not stamped again,
  -- so it is not read as new by anything that follows the lanes.
  WITH restored AS (
    INSERT INTO __SCHEMA__.wh_event_store
      (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, created_at,
       flags, commit_sequence, origin_service_id, origin_commit_sequence, insert_order)
    SELECT a.event_id, a.stream_id, a.aggregate_id, a.aggregate_type, a.event_type, a.scope, a.version,
           COALESCE(a.created_at, a.archived_at), COALESCE(a.flags, 0), a.commit_sequence, a.origin_service_id,
           a.origin_commit_sequence, COALESCE(a.insert_order, nextval('__SCHEMA__.wh_event_insert_order_seq'))
    FROM __SCHEMA__.wh_event_archive a
    WHERE a.stream_id = p_stream_id
    ORDER BY a.version
    ON CONFLICT DO NOTHING
    RETURNING event_id)
  SELECT COALESCE(array_agg(r.event_id), ARRAY[]::UUID[]) INTO v_ids FROM restored r;

  INSERT INTO __SCHEMA__.wh_event_body (event_id, event_data, metadata)
  SELECT a.event_id, a.event_data, a.metadata
  FROM __SCHEMA__.wh_event_archive a
  WHERE a.stream_id = p_stream_id AND a.event_id = ANY(v_ids)
    AND a.event_data IS NOT NULL AND a.metadata IS NOT NULL
  ON CONFLICT DO NOTHING;

  -- Fold the restored rows back in with the chain's own predicates.
  INSERT INTO __SCHEMA__.wh_stream_digests AS d
    (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count, updated_at)
  SELECT COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid),
         COALESCE(es.scope ->> 't', ''),
         es.event_type,
         es.stream_id,
         bit_xor(hashtextextended(es.event_id::text, 0)),
         bit_xor(hashtextextended(es.event_id::text, 1)),
         COUNT(*)::int,
         NOW()
  FROM __SCHEMA__.wh_event_store es
  WHERE es.event_id = ANY(v_ids)
    AND COALESCE(es.flags, 0) & c_flag_ephemeral = 0
    AND COALESCE(es.flags, 0) & c_flag_at_most_once = 0
    AND split_part(es.event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__)
  GROUP BY 1, 2, 3, 4
  ORDER BY 1, 2, 3, 4
  ON CONFLICT (origin_service_id, scope_tenant, event_type, stream_id) DO UPDATE SET
    digest_lo = d.digest_lo # EXCLUDED.digest_lo,
    digest_hi = d.digest_hi # EXCLUDED.digest_hi,
    event_count = d.event_count + EXCLUDED.event_count,
    updated_at = EXCLUDED.updated_at;

  SELECT array_agg(t.lane), array_agg(t.mn), array_agg(t.mx)
  INTO v_lanes, v_mins, v_maxs
  FROM (
    SELECT COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid) AS lane,
           MIN(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mn,
           MAX(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mx
    FROM __SCHEMA__.wh_event_store es
    WHERE es.event_id = ANY(v_ids)
      AND COALESCE(es.flags, 0) & c_flag_ephemeral = 0
      AND COALESCE(es.flags, 0) & c_flag_at_most_once = 0
    GROUP BY 1
  ) t
  WHERE t.mn IS NOT NULL;

  IF v_lanes IS NOT NULL THEN
    FOR v_i IN 1 .. array_length(v_lanes, 1) LOOP
      PERFORM __SCHEMA__._wh_refold_epochs_covering(v_lanes[v_i], v_mins[v_i], v_maxs[v_i]);
    END LOOP;
  END IF;

  -- The parked cursors return as they were; a cursor a perspective has made since is kept.
  INSERT INTO __SCHEMA__.wh_perspective_cursors
  SELECT (jsonb_populate_record(NULL::__SCHEMA__.wh_perspective_cursors, p.cursor_row)).*
  FROM __SCHEMA__.wh_event_archive_cursors p
  WHERE p.stream_id = p_stream_id
  ON CONFLICT DO NOTHING;

  DELETE FROM __SCHEMA__.wh_event_archive_cursors p WHERE p.stream_id = p_stream_id;
  DELETE FROM __SCHEMA__.wh_event_archive a WHERE a.stream_id = p_stream_id;

  PERFORM __SCHEMA__._wh_bump_origin_generation();
  SELECT (s.setting_value)::bigint INTO v_generation
  FROM __SCHEMA__.wh_settings s WHERE s.setting_key = 'integrity_origin_generation';

  -- The ledger row ends Active and counts the bytes that came back. A stream with no row gets one, by 260's rules.
  SELECT COALESCE(SUM(b.sz) FILTER (WHERE b.restored), 0), COALESCE(SUM(b.sz), 0) INTO v_bytes, v_hot_bytes
  FROM (
    SELECT es.event_id = ANY(v_ids) AS restored,
           COALESCE(pg_column_size(eb.event_data), 0) + COALESCE(pg_column_size(eb.metadata), 0) AS sz
    FROM __SCHEMA__.wh_event_store es
    LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = es.event_id
    WHERE es.stream_id = p_stream_id AND COALESCE(es.flags, 0) & c_flags_off_the_ledger = 0
  ) b;
  SELECT es.aggregate_type, es.scope ->> 't', es.created_at INTO v_type, v_tenant, v_first_at
  FROM __SCHEMA__.wh_event_store es
  WHERE es.stream_id = p_stream_id
  ORDER BY (COALESCE(es.flags, 0) & c_flags_off_the_ledger <> 0), es.version
  LIMIT 1;
  SELECT MAX(es.version), date_trunc('hour', MAX(es.created_at), 'UTC') INTO v_last, v_bucket
  FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = p_stream_id;

  INSERT INTO __SCHEMA__.wh_stream_lifecycle AS l
    (stream_id, stream_type, tenant, first_appended_at, last_appended_bucket, last_version, approx_bytes, state,
     archive_locator, origin_generation)
  VALUES (p_stream_id, v_type, v_tenant, v_first_at, v_bucket, v_last, v_hot_bytes, 'Active', NULL, v_generation)
  ON CONFLICT (stream_id) DO UPDATE SET
    state = CASE WHEN l.state = 'Archived' THEN (CASE WHEN l.sealed_at IS NOT NULL THEN 'Sealed' ELSE 'Active' END)
                 ELSE l.state END,
    last_version = GREATEST(l.last_version, EXCLUDED.last_version),
    last_appended_bucket = GREATEST(l.last_appended_bucket, EXCLUDED.last_appended_bucket),
    approx_bytes = l.approx_bytes + v_bytes,
    archive_locator = NULL,
    origin_generation = EXCLUDED.origin_generation;

  RETURN QUERY SELECT 'restored'::TEXT, cardinality(v_ids)::BIGINT, v_generation;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_restore_archived_stream(UUID) IS
'Puts a stream''s archived events back into the hot store at their original versions and commit positions, refolds their digests and sealed epochs, returns parked cursors, marks the stream Active (266: Sealed when it was sealed, #1733), deletes the archived copy and bumps the origin generation (#1736). Refuses with version_conflict when a hot event holds an archived version; nothing_archived when there is nothing to restore.';
