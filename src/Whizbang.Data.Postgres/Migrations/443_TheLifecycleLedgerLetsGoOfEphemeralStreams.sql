-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 443_TheLifecycleLedgerLetsGoOfEphemeralStreams.sql
-- Date: 2026-11-14
-- Description: The stream lifecycle ledger stops holding ephemeral streams, and stops counting bytes it no longer
--              should (#2328).
--
--              wh_stream_lifecycle holds one row per stream that holds a Sourced event (260): every write path and the
--              one-time backfill skip ephemeral events and compaction origins. But a stream whose events were written
--              while their type was Sourced got its row then, and when the history walk later reclassified the stream
--              ephemeral (#2206) the row stayed, its approx_bytes still counting bodies the reaper went on to delete.
--              Measured on a long-running database: about 74,000 such rows, 830 MB of approx_bytes, which skews the size
--              ranking and any byte-cap policy.
--
--                _wh_stream_lifecycle_settle      for the given streams: removes the row of one that holds no event the
--                                                 ledger counts, and recomputes approx_bytes (as the backfill computes it)
--                                                 for one that holds both kinds
--                wh_ephemeral_history_apply       settles the streams of each batch it reclassifies, under the batch's
--                                                 per-stream locks (copied whole from 397; that is the only change)
--                wh_stream_lifecycle_release_     the one-time release of the rows already held: a bounded batch of
--                ephemeral_batch                  ledger rows after a cursor, settled under their append locks; gated on
--                                                 stream_lifecycle_ephemeral_release_version (0 = to do, 1 = done) and
--                                                 driven from the maintenance cycle by StreamLifecycleBackfill, after the
--                                                 backfill itself is done
--
--              Number 443: reserved for this change.
--
-- Reversal: reversible (wh_ephemeral_history_apply goes back to 397; the two new functions and the two settings rows go;
--           rows already released are not put back, which the backfill would never have seeded)
-- Dependencies: 260 (wh_stream_lifecycle, its backfill), 397 (wh_ephemeral_history_apply, last word), 077 (wh_event_body)
-- Objects: _wh_stream_lifecycle_settle, wh_stream_lifecycle_release_ephemeral_batch, wh_ephemeral_history_apply,
--          wh_settings rows stream_lifecycle_ephemeral_release_version and stream_lifecycle_ephemeral_release_cursor
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

SELECT __SCHEMA__.drop_all_overloads('_wh_stream_lifecycle_settle');
SELECT __SCHEMA__.drop_all_overloads('wh_stream_lifecycle_release_ephemeral_batch');
SELECT __SCHEMA__.drop_all_overloads('wh_ephemeral_history_apply');

-- For each given stream that has a ledger row: removes the row when the stream holds no event the ledger counts
-- (ephemeral and compaction origins are not counted), and otherwise recomputes approx_bytes from the counted events
-- when the stream also holds uncounted ones. The caller holds the streams' append locks. Returns the rows removed.
-- <docs>fundamentals/events/stream-end-of-life#ephemeral-streams</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleEphemeralSqlTests.cs:Reclassifying_AStreamWhole_RemovesItsLedgerRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleEphemeralSqlTests.cs:Release_RemovesHeldEphemeralRows_InBatches_AndRecountsAMixedStreamAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_stream_lifecycle_settle(p_streams UUID[])
RETURNS BIGINT
LANGUAGE plpgsql
AS $$
DECLARE
  -- EventFlags.Ephemeral (1 << 3) | EventFlags.Compacted (1 << 4), the events the lifecycle ledger does not count.
  c_flags_off_the_ledger CONSTANT INTEGER := 24;
  v_removed BIGINT;
BEGIN
  DELETE FROM __SCHEMA__.wh_stream_lifecycle l
  WHERE l.stream_id = ANY(p_streams)
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_store es
      WHERE es.stream_id = l.stream_id AND COALESCE(es.flags, 0) & c_flags_off_the_ledger = 0);
  GET DIAGNOSTICS v_removed = ROW_COUNT;

  UPDATE __SCHEMA__.wh_stream_lifecycle l
  SET approx_bytes = a.bytes
  FROM (
    SELECT es.stream_id,
           COALESCE(SUM(COALESCE(pg_column_size(b.event_data), 0) + COALESCE(pg_column_size(b.metadata), 0))
             FILTER (WHERE COALESCE(es.flags, 0) & c_flags_off_the_ledger = 0), 0) AS bytes
    FROM __SCHEMA__.wh_event_store es
    LEFT JOIN __SCHEMA__.wh_event_body b ON b.event_id = es.event_id
    WHERE es.stream_id = ANY(p_streams)
    GROUP BY es.stream_id
    HAVING bool_or(COALESCE(es.flags, 0) & c_flags_off_the_ledger <> 0)
  ) a
  WHERE l.stream_id = a.stream_id
    AND l.approx_bytes IS DISTINCT FROM a.bytes;

  RETURN v_removed;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_stream_lifecycle_settle(UUID[]) IS
  'Issue #2328: removes the wh_stream_lifecycle row of each given stream that holds no counted event (an ephemeral '
  'stream), and recounts approx_bytes over the counted events of one that also holds uncounted ones. Returns the rows '
  'removed. The caller holds the streams'' append locks.';

-- The release's version marker and cursor (README rule 6). A ledger with no rows has nothing to release.
INSERT INTO __SCHEMA__.wh_settings (setting_key, setting_value, value_type, description, updated_at, updated_by)
SELECT 'stream_lifecycle_ephemeral_release_version',
       CASE WHEN EXISTS (SELECT 1 FROM __SCHEMA__.wh_stream_lifecycle) THEN '0' ELSE '1' END,
       'integer',
       'Gates the one-time release of ephemeral streams from wh_stream_lifecycle (#2328, migration 443). 0 = to do, 1 = done.',
       NOW(),
       'migration:443_TheLifecycleLedgerLetsGoOfEphemeralStreams'
ON CONFLICT (setting_key) DO NOTHING;

INSERT INTO __SCHEMA__.wh_settings (setting_key, setting_value, value_type, description, updated_at, updated_by)
VALUES ('stream_lifecycle_ephemeral_release_cursor',
        __EMPTY_UUID__,
        'string',
        'The last stream id the wh_stream_lifecycle ephemeral release committed (#2328, migration 443).',
        NOW(),
        'migration:443_TheLifecycleLedgerLetsGoOfEphemeralStreams')
ON CONFLICT (setting_key) DO NOTHING;

-- One batch of the one-time release: the next p_limit ledger rows after the cursor, settled under their streams' append
-- locks (in stream id order, the emit chain's order). Returns the rows visited, 0 when none is left (recorded as done),
-- -1 when another caller holds the cursor.
-- <docs>fundamentals/events/stream-end-of-life#ephemeral-streams</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleEphemeralSqlTests.cs:Release_RemovesHeldEphemeralRows_InBatches_AndRecountsAMixedStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleEphemeralSqlTests.cs:Release_WhileAnotherCallerHoldsTheCursor_StepsAsideAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_stream_lifecycle_release_ephemeral_batch(p_limit INTEGER)
RETURNS INTEGER
LANGUAGE plpgsql
AS $$
DECLARE
  c_version_key CONSTANT TEXT := 'stream_lifecycle_ephemeral_release_version';
  c_cursor_key CONSTANT TEXT := 'stream_lifecycle_ephemeral_release_cursor';
  v_cursor_text TEXT;
  v_streams UUID[];
BEGIN
  IF COALESCE((SELECT s.setting_value::INTEGER FROM __SCHEMA__.wh_settings s WHERE s.setting_key = c_version_key), 1) >= 1 THEN
    RETURN 0;
  END IF;

  SELECT s.setting_value INTO v_cursor_text
  FROM __SCHEMA__.wh_settings s
  WHERE s.setting_key = c_cursor_key
  FOR UPDATE SKIP LOCKED;
  IF NOT FOUND THEN
    RETURN -1;
  END IF;

  SELECT array_agg(b.stream_id ORDER BY b.stream_id) INTO v_streams
  FROM (
    SELECT l.stream_id FROM __SCHEMA__.wh_stream_lifecycle l
    WHERE l.stream_id > v_cursor_text::UUID
    ORDER BY l.stream_id
    LIMIT GREATEST(COALESCE(p_limit, 1), 1)
  ) b;

  IF v_streams IS NULL THEN
    UPDATE __SCHEMA__.wh_settings
    SET setting_value = '1', updated_at = NOW(), updated_by = 'wh_stream_lifecycle_release_ephemeral_batch'
    WHERE setting_key = c_version_key;
    RETURN 0;
  END IF;

  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || u.sid::text))
  FROM unnest(v_streams) AS u(sid)
  ORDER BY u.sid;

  PERFORM __SCHEMA__._wh_stream_lifecycle_settle(v_streams);

  UPDATE __SCHEMA__.wh_settings
  SET setting_value = v_streams[cardinality(v_streams)]::text, updated_at = NOW(),
      updated_by = 'wh_stream_lifecycle_release_ephemeral_batch'
  WHERE setting_key = c_cursor_key;
  RETURN cardinality(v_streams);
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_stream_lifecycle_release_ephemeral_batch(INTEGER) IS
  'Issue #2328: one batch of the one-time release of ephemeral streams from wh_stream_lifecycle: the next p_limit rows '
  'after the cursor, settled (_wh_stream_lifecycle_settle) under their append locks. Returns the rows visited, 0 when '
  'done (recorded in stream_lifecycle_ephemeral_release_version), -1 when another caller holds the cursor.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- wh_ephemeral_history_apply, last word 397_TypesDeclaredEphemeralTogetherConvertTheirStreamsWhole.sql, copied whole.
-- The only change: the streams of the batch are settled in the lifecycle ledger after they are stamped.
-- <docs>fundamentals/events/ephemeral-events#making-an-existing-type-ephemeral</docs>
-- <docs>fundamentals/events/ephemeral-events#what-holds-ephemeral-history-back</docs>
-- <docs>fundamentals/events/ephemeral-events#allowing-mixed-streams</docs>
-- <docs>fundamentals/events/ephemeral-events#a-stream-becomes-ephemeral-whole</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleEphemeralSqlTests.cs:Reclassifying_AStreamWhole_RemovesItsLedgerRowAsync</tests>
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

  -- 443 (#2328): a stream this batch made ephemeral whole leaves the lifecycle ledger, which never holds an ephemeral
  -- stream, and a stream that still holds Sourced events stops counting the bytes of the events it no longer counts.
  -- Under the same per-stream locks, so no append lands between the stamp and the ledger.
  IF v_streams IS NOT NULL THEN
    PERFORM __SCHEMA__._wh_stream_lifecycle_settle(v_streams);
  END IF;

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
