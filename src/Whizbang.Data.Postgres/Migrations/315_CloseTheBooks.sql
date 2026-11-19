-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 315_CloseTheBooks.sql
-- Date: 2026-11-03
-- Description: Closing the books (#1741, WS-3 S12).
--
--              wh_close_books closes a stream's books in one transaction: it appends the closing summary to the
--              stream, seals it, and opens the successor stream with the opening balance. It calls the direct append
--              (wh_append_event, its last word in 296) twice and the finalizer (wh_finalize_stream, 266) once, so every
--              rule those apply (the version under the stream's lock, the ledger, the terminal flag, the mixed-stream,
--              seal and archive refusals) applies here too, and a failure in any step rolls all three back.
--
--              The caller numbers the opening balance's link before it calls: it reads the stream's newest version and
--              sets the predecessor's final version to the next one. Under the stream's lock the function checks that
--              the newest version is still the one the caller read; when another append got there first nothing is
--              written and the answer is stream_changed, so the link stored on the successor is always exact.
--
--              Number 315: reserved for this change.
--
-- Dependencies: 266 (wh_finalize_stream, sealed_at), 296 (wh_append_event), 260 (wh_stream_lifecycle)
-- Objects: wh_close_books
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

SELECT __SCHEMA__.drop_all_overloads('wh_close_books');

-- Closes p_predecessor's books: appends the closing summary (the p_closing_* values) at p_expected_last_version + 1,
-- seals the stream with p_reason, and appends the opening balance (the p_opening_* values) as the first event of
-- p_successor, all in the caller's transaction. Answers close_status and the closing summary's version:
--   nothing_to_close  the empty stream id, or a stream with no Sourced event;
--   archived          the stream is archived whole (restore it first);
--   already_sealed    the stream is sealed;
--   stream_changed    the stream's newest version is no longer p_expected_last_version;
--   closed            all three steps ran.
-- <docs>fundamentals/events/closing-the-books</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CloseBooksSqlTests.cs:CloseBooks_AppendsTheSummary_SealsTheStream_AndOpensTheSuccessorLinkedToItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CloseBooksSqlTests.cs:CloseBooks_AFailureOpeningTheSuccessor_LeavesThePredecessorUnsealed_AndStoresNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CloseBooksSqlTests.cs:CloseBooks_RefusesEachStreamItCannotClose_AndChangesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CloseBooksSqlTests.cs:CloseBooks_WaitsForTheStreamsAppendLockAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_close_books(
  p_predecessor_stream_id UUID,
  p_expected_last_version BIGINT,
  p_closing_event_id UUID,
  p_closing_aggregate_type TEXT,
  p_closing_event_type TEXT,
  p_closing_event_data JSONB,
  p_closing_metadata JSONB,
  p_closing_scope JSONB,
  p_closing_flags INTEGER,
  p_successor_stream_id UUID,
  p_opening_event_id UUID,
  p_opening_aggregate_type TEXT,
  p_opening_event_type TEXT,
  p_opening_event_data JSONB,
  p_opening_metadata JSONB,
  p_opening_scope JSONB,
  p_opening_flags INTEGER,
  p_reason TEXT,
  p_now TIMESTAMPTZ)
RETURNS TABLE(close_status TEXT, closing_version BIGINT) AS $$
DECLARE
  v_newest BIGINT;
  v_state TEXT;
  v_sealed_at TIMESTAMPTZ;
  v_version INTEGER;
  c_flags_off_the_ledger CONSTANT INTEGER := 24;  -- ephemeral (8) or a compaction origin (16): no ledger row (260)
BEGIN
  IF p_predecessor_stream_id IS NULL OR p_predecessor_stream_id = __EMPTY_UUID__::uuid THEN
    RETURN QUERY SELECT 'nothing_to_close'::TEXT, NULL::BIGINT;
    RETURN;
  END IF;

  -- The append lock every write path takes, so the version checked is the version the summary follows.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || p_predecessor_stream_id::text));

  SELECT l.state, l.sealed_at INTO v_state, v_sealed_at
  FROM __SCHEMA__.wh_stream_lifecycle l
  WHERE l.stream_id = p_predecessor_stream_id;

  IF v_state = 'Archived' THEN
    RETURN QUERY SELECT 'archived'::TEXT, NULL::BIGINT;
    RETURN;
  END IF;
  IF v_sealed_at IS NOT NULL THEN
    RETURN QUERY SELECT 'already_sealed'::TEXT, NULL::BIGINT;
    RETURN;
  END IF;

  SELECT MAX(es.version) INTO v_newest
  FROM __SCHEMA__.wh_event_store es
  WHERE es.stream_id = p_predecessor_stream_id
    AND COALESCE(es.flags, 0) & c_flags_off_the_ledger = 0;
  IF v_newest IS NULL THEN
    RETURN QUERY SELECT 'nothing_to_close'::TEXT, NULL::BIGINT;
    RETURN;
  END IF;
  IF v_newest <> p_expected_last_version THEN
    RETURN QUERY SELECT 'stream_changed'::TEXT, NULL::BIGINT;
    RETURN;
  END IF;

  -- (1) The closing summary, the stream's last event.
  v_version := __SCHEMA__.wh_append_event(
    p_closing_event_id, p_predecessor_stream_id, p_predecessor_stream_id, p_closing_aggregate_type, p_closing_event_type,
    p_closing_event_data, p_closing_metadata, p_closing_scope, p_now, p_closing_flags);
  IF v_version <> p_expected_last_version + 1 THEN
    RAISE EXCEPTION USING
      ERRCODE = 'internal_error',
      MESSAGE = format('wh_close_books: the closing summary of stream %s was stored at version %s, not %s.',
                       p_predecessor_stream_id, v_version, p_expected_last_version + 1);
  END IF;

  -- (2) The seal: every later append to the stream is refused.
  PERFORM __SCHEMA__.wh_finalize_stream(p_predecessor_stream_id, p_reason, p_now);

  -- (3) The successor, opened by the opening balance.
  PERFORM __SCHEMA__.wh_append_event(
    p_opening_event_id, p_successor_stream_id, p_successor_stream_id, p_opening_aggregate_type, p_opening_event_type,
    p_opening_event_data, p_opening_metadata, p_opening_scope, p_now, p_opening_flags);

  RETURN QUERY SELECT 'closed'::TEXT, v_version::BIGINT;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_close_books(UUID, BIGINT, UUID, TEXT, TEXT, JSONB, JSONB, JSONB, INTEGER, UUID, UUID, TEXT, TEXT, JSONB, JSONB, JSONB, INTEGER, TEXT, TIMESTAMPTZ) IS
  'Closes a stream''s books (#1741): appends the closing summary, seals the stream and opens the successor with the '
  'opening balance, in one transaction. Answers closed, nothing_to_close, archived, already_sealed or stream_changed.';
