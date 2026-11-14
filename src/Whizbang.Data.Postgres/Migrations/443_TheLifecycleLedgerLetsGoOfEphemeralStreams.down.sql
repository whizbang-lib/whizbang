-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 443_TheLifecycleLedgerLetsGoOfEphemeralStreams.sql (#2328): wh_ephemeral_history_apply goes back to its
-- 397 definition, then the release and settle functions and the release's settings rows go. Rows already released are
-- not put back: the ledger never holds an ephemeral stream.

SELECT __SCHEMA__.drop_all_overloads('wh_ephemeral_history_apply');

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

SELECT __SCHEMA__.drop_all_overloads('wh_stream_lifecycle_release_ephemeral_batch');
SELECT __SCHEMA__.drop_all_overloads('_wh_stream_lifecycle_settle');
DELETE FROM __SCHEMA__.wh_settings
WHERE setting_key IN ('stream_lifecycle_ephemeral_release_version', 'stream_lifecycle_ephemeral_release_cursor');
