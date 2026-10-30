-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 267_DirectAppendsStartNewStreamsAtOne.sql
-- Date: 2026-10-30
-- Description: A direct append starts a new stream at version 1 (#1913), as the outbox and inbox emit chains always
--              have. wh_append_event (IEventStore.AppendAsync on both drivers: system events, compaction, direct use)
--              inserted at COALESCE(MAX(version), -1) + 1, so a stream it created began at 0 while a dispatched stream
--              began at 1. It now inserts at COALESCE(MAX(version), 0) + 1.
--
--              Existing streams keep their numbering: a stream that began at 0 continues from its highest version, so
--              its next event takes the next version as before. Only a stream's first event moves, from 0 to 1. The
--              first-event fact (get_stream_events.out_first_in_stream, migration 264) reads version 0 as first and
--              version 1 as first only when its stream holds no version 0, so it stays right for both numberings.
--
--              Nothing else changes: the function is the 266 definition copied whole (rule 5), at the same arity, with
--              the sealed-stream refusal, the terminal seal, the digest fold and the lifecycle ledger exactly as they
--              were. Numbered after 266, its last word before this file. The ledger records each file by name, so a
--              database that has already applied 268 and 269 applies this file when it arrives.
-- Dependencies: 266 (wh_append_event, the last word before this file), 264 (the first-event fact)
-- Objects: wh_append_event
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

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
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SchemaInitializationTests.cs:EnsureWhizbangDatabaseInitialized_ADatabaseAlreadyPast267_StillApplies267_AndANewStreamStartsAtOneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FirstEventInStreamReadTests.cs:DirectAppend_ToAStreamNumberedFromZero_ContinuesItsNumbering_AndItsVersionZeroStaysFirstAsync</tests>
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
    COALESCE((SELECT MAX(es.version) FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = p_stream_id), 0) + 1,
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
'Appends one event to a stream for IEventStore.AppendAsync (both drivers) and returns the version it assigned. Takes the per-stream advisory lock the emit chains take (hashtext(''wh_event_store:'' || stream_id)), then inserts at COALESCE(MAX(version), 0) + 1 in a separate statement so the read sees every append committed during the wait (224, #1555). 225: folds the stored event into wh_stream_digests as the emit chain does, unless it is ephemeral, at-most-once or of a digest-exempt type (#1600). 228: stores the event''s kind bits (p_flags) and refuses, raising SQLSTATE WH133, an event that would make its stream hold both ephemeral and Sourced events (WHIZ133, #1546). 260: keeps the stream''s wh_stream_lifecycle row (#1732). 266: refuses an append to a sealed stream, raising SQLSTATE WHS01 (StreamSealedException), and a terminal event seals its stream (#1733). 267: a new stream starts at version 1, as on the outbox and inbox paths; an existing stream continues from its highest version (#1913).';
