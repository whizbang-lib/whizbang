-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 358_StreamsRememberWhenTheyStarted.sql
-- Date: 2026-11-08
-- Description: A stream remembers when it started after its first event is removed, so a rebuild dates a row it writes
--              afresh at the stream's start rather than at the oldest event left (#2152).
--
--              A perspective row's created_at is business time: the hop timestamp of the event that brought the row
--              into being. The superseding reaper (295, 352) and the prefix truncate (240, 336) delete events
--              outright, the first one included, and a rebuild that then writes the row afresh (blue-green into an
--              empty shadow table, or in place where the row is missing) had nothing older than the oldest surviving
--              event to date it by.
--
--                first_event_at        a new nullable column of wh_stream_lifecycle: the business time of the
--                                      stream's first event (version 0 or 1), recorded when that event is removed.
--                                      Recorded at removal rather than on append, so no write path changes and the
--                                      append hot path pays nothing. first_appended_at is not the same value: it is
--                                      the database clock when the event was stored, while business time is the
--                                      timestamp the producer put on the envelope (an imported or replayed event
--                                      carries a time well before it was stored).
--                _wh_event_business_time
--                                      the business time a stored event's metadata gives: the timestamp of its last
--                                      stored hop, the one the generated perspective runner reads (slim metadata
--                                      keeps one hop, the first hop's timestamp), cut to the microsecond as the
--                                      driver cuts a .NET time it writes. NULL when no hop carries one, and the
--                                      writers then record the event's created_at.
--                wh_stream_started_at  the stream's start for a stream that no longer holds its first event:
--                                      first_event_at, else first_appended_at for a first event removed before this
--                                      migration recorded anything. NULL while the stream still holds an event at
--                                      version 0 or 1, so a stream with its whole history rebuilds exactly as before.
--
--              wh_reap_superseded_events (last word 352) and wh_truncate_stream_prefix (last word 336) are copied whole
--              with the record step added before they delete.
--
--              Number 358: 354 to 357 are taken on develop or by changes in flight.
--
-- Dependencies: 260 (wh_stream_lifecycle), 336 (last word of wh_truncate_stream_prefix), 352 (last word of
--               wh_reap_superseded_events), 077 (wh_event_body)
-- Objects: wh_stream_lifecycle.first_event_at, _wh_event_business_time, wh_stream_started_at,
--          wh_reap_superseded_events, wh_truncate_stream_prefix
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

ALTER TABLE __SCHEMA__.wh_stream_lifecycle ADD COLUMN IF NOT EXISTS first_event_at TIMESTAMPTZ NULL;

COMMENT ON COLUMN __SCHEMA__.wh_stream_lifecycle.first_event_at IS
  'The business time of the stream''s first event (its last stored hop''s timestamp), recorded when the superseding reaper or a prefix truncate removes that event (358, #2152). NULL while the event is held, and for a first event removed before 358. Written once.';

-- ===========================================================================================
-- An event's business time, and a stream's start
-- ===========================================================================================

-- -- <docs>fundamentals/events/superseding-events#what-a-rebuild-keeps</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamStartedAtSqlTests.cs:BusinessTime_IsTheLastStoredHopsTimestamp_CutToTheMicrosecondAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamStartedAtSqlTests.cs:BusinessTime_WithNoHop_IsNullAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_event_business_time(p_metadata JSONB)
RETURNS TIMESTAMPTZ AS $$
  SELECT CASE
    WHEN jsonb_typeof(h.hops) = 'array' AND jsonb_typeof(h.hops -> -1 -> 'ts') = 'string'
      THEN regexp_replace(h.hops -> -1 ->> 'ts', '(\.[0-9]{6})[0-9]+', '\1')::TIMESTAMPTZ
  END
  FROM (SELECT COALESCE(p_metadata -> 'Hops', p_metadata -> 'hops') AS hops) h;
$$ LANGUAGE sql IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__._wh_event_business_time(JSONB) IS
  'Issue #2152: the business time a stored event''s metadata gives, the timestamp of its last stored hop (Hops, or hops), cut to the microsecond as the driver cuts a .NET time; NULL when no hop carries one.';

-- -- <docs>fundamentals/events/superseding-events#what-a-rebuild-keeps</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamStartedAtSqlTests.cs:StartedAt_AStreamHoldingItsFirstEvent_IsNullAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamStartedAtSqlTests.cs:Reap_TheStreamsFirstEvent_RecordsItsBusinessTimeOnTheLedgerAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamStartedAtSqlTests.cs:StartedAt_AFirstEventRemovedWithNothingRecorded_IsTheFirstAppendAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_stream_started_at(p_stream_id UUID)
RETURNS TIMESTAMPTZ AS $$
  SELECT COALESCE(l.first_event_at, l.first_appended_at)
  FROM __SCHEMA__.wh_stream_lifecycle l
  WHERE l.stream_id = p_stream_id
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = p_stream_id AND es.version <= 1);
$$ LANGUAGE sql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_stream_started_at(UUID) IS
  'Issue #2152: when a stream no longer holds its first event (nothing at version 0 or 1), the business time it started at: first_event_at, else first_appended_at. NULL while the stream holds its first event or has no ledger row. Two probes by key.';

-- ===========================================================================================
-- The removers record the first event's business time before they delete it
-- ===========================================================================================

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- <docs>fundamentals/events/superseding-events#what-a-rebuild-keeps</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamStartedAtSqlTests.cs:Reap_TheStreamsFirstEvent_RecordsItsBusinessTimeOnTheLedgerAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamStartedAtSqlTests.cs:Reap_ALaterEvent_RecordsNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/RebuildCreatedAtIntegrationTests.cs:RebuildBlueGreen_AfterTheFirstEventIsReaped_DatesTheRowAtTheStreamsStartAsync</tests>
-- <docs>fundamentals/events/superseding-events#the-reaper</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_SupersededAndConsumed_IsDeletedWithItsBodyAndItsDigestContribution_TheNewestIsKeptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_UnprocessedPerspectiveWork_KeepsIt_UntilItIsProcessedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_ACursorOnIt_KeepsIt_UntilTheCursorMovesOnAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_ASnapshotOnIt_KeepsIt_UntilTheSnapshotIsGoneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_AnActiveDestructionHold_KeepsIt_ALapsedOneDoesNotAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_AnEventTheStamperHasNotReached_IsKeptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_ADifferentTypeOrKeyInTheSameStream_IsUntouchedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_TheNewestOfItsKey_IsKept_EvenWhenQueuedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_DebugMode_KeepsEverythingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_AQueuedEventAlreadyGone_IsForgottenAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_RemovingFoldedHistory_RefoldsAndBumpsTheGenerationOnce_AnAtMostOnceEventDoesNotAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_TakesAtMostTheBatchSize_OldestQueuedFirstAsync</tests>
-- <docs>fundamentals/events/superseding-events#superseding-groups</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingGroupSqlTests.cs:Reap_AnEventSupersededByAnotherTypeOfItsGroup_IsDeleted_TheNewestIsKeptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingGroupSqlTests.cs:Reap_AQueuedEventWhoseOnlyNewerEventIsOutsideItsGroup_IsKeptAsync</tests>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_reap_superseded_events(p_batch_size INTEGER DEFAULT 5000)
RETURNS TABLE(task_name TEXT, rows_affected BIGINT, duration_ms DOUBLE PRECISION, status TEXT) AS $$
DECLARE
  v_start TIMESTAMPTZ := clock_timestamp();
  v_debug BOOLEAN;
  v_candidates UUID[];
  v_streams UUID[];
  v_victims UUID[];
  v_lanes UUID[];
  v_mins BIGINT[];
  v_maxs BIGINT[];
  v_i INTEGER;
  v_deleted BIGINT := 0;
  c_task CONSTANT TEXT := 'reap_superseded_events';
  c_flag_superseding CONSTANT INTEGER := 128;  -- EventFlags.Superseding (1 << 7)
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  c_flags_state_based CONSTANT INTEGER := 24;  -- EventFlags.Ephemeral (1 << 3) | EventFlags.Compacted (1 << 4)
BEGIN
  -- debug_mode keeps every event for forensics, as every other reaper does.
  SELECT COALESCE(
    (SELECT s.setting_value::BOOLEAN FROM __SCHEMA__.wh_settings s WHERE s.setting_key = 'debug_mode'),
    FALSE
  ) INTO v_debug;
  IF v_debug THEN
    RETURN QUERY SELECT c_task, 0::BIGINT,
      EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION, 'skipped (debug_mode=true)'::TEXT;
    RETURN;
  END IF;

  -- A queued event something else already removed (a prefix truncate, an archive, a purge) is forgotten.
  DELETE FROM __SCHEMA__.wh_superseded_events q
  WHERE NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = q.event_id);

  -- The batch, oldest queued first, and its streams in lock order.
  SELECT array_agg(b.event_id), array_agg(DISTINCT b.stream_id)
  INTO v_candidates, v_streams
  FROM (
    SELECT q.event_id, q.stream_id
    FROM __SCHEMA__.wh_superseded_events q
    ORDER BY q.queued_at, q.event_id
    LIMIT p_batch_size
  ) b;
  IF v_candidates IS NULL THEN
    RETURN QUERY SELECT c_task, 0::BIGINT,
      EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION, 'ok'::TEXT;
    RETURN;
  END IF;

  -- The emit chains' per-stream locks, in stream id order (array_agg DISTINCT sorts), so nothing is appended to,
  -- truncated from or archived out of these streams while their events are judged and removed.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || u.sid::text))
  FROM unnest(v_streams) AS u(sid)
  ORDER BY u.sid;

  -- The gates, read under the locks.
  SELECT array_agg(es.event_id) INTO v_victims
  FROM __SCHEMA__.wh_event_store es
  WHERE es.event_id = ANY(v_candidates)
    AND es.supersede_key IS NOT NULL
    AND COALESCE(es.flags, 0) & c_flag_superseding <> 0
    AND COALESCE(es.flags, 0) & c_flags_state_based = 0
    -- Never the newest of its key: a newer event of the same stream, group and key is stored. 352 (#2124): the group is
    -- the stored supersede_group, or the event type for an ungrouped row, so an ungrouped type is its own group.
    AND EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_store n
      WHERE n.stream_id = es.stream_id
        AND COALESCE(n.supersede_group, n.event_type) = COALESCE(es.supersede_group, es.event_type)
        AND n.supersede_key = es.supersede_key
        AND n.version > es.version)
    -- The commit-sequence stamper has reached it, so nothing that walks the stamped order still owes it a place.
    AND es.commit_sequence IS NOT NULL
    -- Every perspective that consumes it has processed it.
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
      WHERE pe.event_id = es.event_id AND pe.processed_at IS NULL)
    -- No cursor rests on it (fk_perspective_cursors_event is RESTRICT on the EF Core schema).
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_cursors c
      WHERE c.stream_id = es.stream_id AND c.last_event_id = es.event_id)
    -- No snapshot rests on it: a rewind restores a snapshot and moves the cursor onto its event.
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_snapshots s
      WHERE s.stream_id = es.stream_id AND s.snapshot_event_id = es.event_id)
    -- A PreDestruction hook's Cancel or Defer is honored, as by every other path that removes an event.
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_destruction_hold h
      WHERE h.event_id = es.event_id AND h.hold_until > NOW());

  IF v_victims IS NOT NULL THEN
    -- The sequence range of the fold-relevant victims, per lane, for the epoch refold after the delete (as 263).
    SELECT array_agg(t.lane), array_agg(t.mn), array_agg(t.mx)
    INTO v_lanes, v_mins, v_maxs
    FROM (
      SELECT COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid) AS lane,
             MIN(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mn,
             MAX(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mx
      FROM __SCHEMA__.wh_event_store es
      WHERE es.event_id = ANY(v_victims)
        AND COALESCE(es.flags, 0) & c_flag_at_most_once = 0
        AND split_part(es.event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__)
      GROUP BY 1
    ) t
    WHERE t.mn IS NOT NULL;

    -- Subtract the victims from wh_stream_digests before they go, as the prefix truncate does (263): XOR is
    -- self-inverse, so folding the same hashes again removes them. Only rows a fold ever added are subtracted.
    PERFORM __SCHEMA__._wh_fold_stream_digests(
      array_agg(COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid)), array_agg(COALESCE(es.scope ->> 't', '')),
      array_agg(es.event_type), array_agg(es.stream_id), array_agg(es.event_id), 'unfold', NOW())
    FROM __SCHEMA__.wh_event_store es
    WHERE es.event_id = ANY(v_victims)
      AND COALESCE(es.flags, 0) & c_flag_at_most_once = 0
      AND split_part(es.event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__);

    DELETE FROM __SCHEMA__.wh_stream_digests d
    WHERE d.stream_id = ANY(v_streams) AND d.event_count <= 0;

    -- 358 (#2152): a victim that is its stream's first event (version 0 or 1) leaves its business time on the stream's
    -- ledger row, so a rebuild that writes the stream's row afresh still dates it at the stream's start. Written once:
    -- the lowest such victim of each stream, and never over a value already recorded.
    UPDATE __SCHEMA__.wh_stream_lifecycle l
    SET first_event_at = f.business_time
    FROM (
      SELECT DISTINCT ON (es.stream_id) es.stream_id,
             COALESCE(__SCHEMA__._wh_event_business_time(eb.metadata), es.created_at) AS business_time
      FROM __SCHEMA__.wh_event_store es
      LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = es.event_id
      WHERE es.event_id = ANY(v_victims) AND es.version <= 1
      ORDER BY es.stream_id, es.version
    ) f
    WHERE l.stream_id = f.stream_id AND l.first_event_at IS NULL;

    -- Lapsed holds of the victims go with them; the gates proved no active one remains. Their processed perspective
    -- work rows are left to Task 3's purge: this task runs after the work tables were visited in their canonical order,
    -- and touching one again here would break that order.
    DELETE FROM __SCHEMA__.wh_event_destruction_hold h WHERE h.event_id = ANY(v_victims);

    -- Bodies first, then pointers (wh_receptor_processing rows cascade on the EF Core schema).
    DELETE FROM __SCHEMA__.wh_event_body eb WHERE eb.event_id = ANY(v_victims);
    DELETE FROM __SCHEMA__.wh_event_store es WHERE es.event_id = ANY(v_victims);
    GET DIAGNOSTICS v_deleted = ROW_COUNT;

    DELETE FROM __SCHEMA__.wh_superseded_events q WHERE q.event_id = ANY(v_victims);

    -- Refold the sealed epochs that covered the victims, then bump the generation once, so consumers reset their
    -- seals and re-verify instead of alarming on a deliberate change (093).
    IF v_lanes IS NOT NULL THEN
      FOR v_i IN 1 .. array_length(v_lanes, 1) LOOP
        PERFORM __SCHEMA__._wh_refold_epochs_covering(v_lanes[v_i], v_mins[v_i], v_maxs[v_i]);
      END LOOP;
      PERFORM __SCHEMA__._wh_bump_origin_generation();
    END IF;
  END IF;

  RETURN QUERY SELECT c_task, v_deleted,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION, 'ok'::TEXT;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_reap_superseded_events(INTEGER) IS
  'Task 16 of perform_maintenance (295, #1729): deletes up to p_batch_size queued superseded events, with their bodies '
  'and their digest contribution, once a newer event of the same stream, group and key is stored and every consumer has '
  'processed them (no unprocessed perspective work, no cursor or snapshot on them, no active destruction hold, commit '
  'sequence stamped). Skipped under debug_mode. Reduces the digests as wh_truncate_stream_prefix does, refolding the '
  'covering sealed epochs and bumping the origin generation once when it removed folded history. 352: a group is the stored supersede_group, or the event type for an ungrouped row (#2124). 358: records the business time of a victim that is its stream''s first event on the stream''s ledger row (first_event_at, #2152).';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- <docs>fundamentals/events/superseding-events#what-a-rebuild-keeps</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamStartedAtSqlTests.cs:Truncate_ThroughTheFirstEvent_RecordsItsBusinessTimeOnTheLedgerAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/RebuildCreatedAtIntegrationTests.cs:RebuildBlueGreen_AfterAPrefixTruncate_DatesTheRowAtTheStreamsStartAsync</tests>
-- <docs>fundamentals/events/event-streams#truncating-a-stream-prefix</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamCloseSqlTests.cs:TruncateStreamPrefix_AfterMigration_IsTheOnlyNameTheTruncateGoesByAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamCloseSqlTests.cs:TruncateStreamPrefix_TheLastMigrationNamingCloseStream_AlsoDefinesTheNewNameAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamCloseSqlTests.cs:CloseStream_ConsumptionGate_BlocksThenTruncatesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamCloseSqlTests.cs:CloseStream_NoCarryForward_RefusesTotalTruncationAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamCloseSqlTests.cs:CloseStream_DebugMode_SkippedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamCloseSqlTests.cs:CloseStream_ActiveDestructionHoldInsideTheRange_IsRefused_ALapsedOneIsRemovedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamCloseSqlTests.cs:CloseStream_CursorBehindABoundaryEventItsPerspectiveStillOwes_IsRefusedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamCloseSqlTests.cs:CloseStream_CursorOfAPerspectiveWithNothingLeftToConsume_IsReanchoredOntoTheBoundaryEventAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamCloseSqlTests.cs:CloseStream_RemovesEverySnapshotThatNeedsATruncatedEvent_KeepsTheExactFloorsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamCloseSqlTests.cs:CloseStream_RemovesProcessedWorkRowsOfTruncatedEvents_KeepsTheirDeduplicationEntriesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamCloseSqlTests.cs:CloseStream_Archive_MovesDetailToColdStorage_RetrievableAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestTableSqlTests.cs:CloseStream_TruncatingAnAtMostOnceEvent_SubtractsOnlyWhatWasFoldedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestTableSqlTests.cs:CloseStream_SubtractsTruncatedRows_SurvivorDigestRemainsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestTableSqlTests.cs:TruncateStreamPrefix_APrefixOfOnlyAnAuditRecord_IsNotAMutationOfFoldedHistoryAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestTableSqlTests.cs:TruncateStreamPrefix_APrefixHoldingAnAuditRecord_LeavesTheDigestEqualToAFreshFoldAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStreamCloseCompanionTests.cs:TruncateStreamPrefix_OnADapperMigratedDatabase_ReplacesCloseStreamAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStreamCloseCompanionTests.cs:CloseStream_OnADapperMigratedDatabase_ReanchorsTheCursor_RemovesStrandedSnapshotsAsync</tests>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestWritersTests.cs:TruncateStreamPrefix_SubtractsEveryLaneOfTheTruncatedEventsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_truncate_stream_prefix(
  p_stream_id UUID, p_through_version BIGINT, p_archive BOOLEAN DEFAULT FALSE)
RETURNS TABLE(truncate_status TEXT, events_truncated BIGINT) AS $$
DECLARE
  v_debug BOOLEAN;
  v_blocked BIGINT;
  v_carry BIGINT;
  v_held BIGINT;
  v_boundary UUID;
  v_behind BIGINT;
  v_deleted BIGINT;
  v_lanes UUID[];
  v_mins BIGINT[];
  v_maxs BIGINT[];
  v_i INTEGER;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
BEGIN
  -- debug_mode retains forensic history; a prefix truncate destroys detail, so skip it entirely (like the reaper).
  SELECT (setting_value = 'true') INTO v_debug FROM __SCHEMA__.wh_settings WHERE setting_key = 'debug_mode';
  IF COALESCE(v_debug, FALSE) THEN
    RETURN QUERY SELECT 'debug_skipped'::TEXT, 0::BIGINT;
    RETURN;
  END IF;

  -- (1) Consumption gate: any perspective work item for an event in this stream at/below the close point that
  -- is still UNPROCESSED blocks the close.
  SELECT count(*) INTO v_blocked
  FROM __SCHEMA__.wh_perspective_events pe
  JOIN __SCHEMA__.wh_event_store es ON es.event_id = pe.event_id
  WHERE es.stream_id = p_stream_id
    AND es.version <= p_through_version
    AND pe.processed_at IS NULL;

  IF v_blocked > 0 THEN
    RETURN QUERY SELECT 'blocked'::TEXT, 0::BIGINT;
    RETURN;
  END IF;

  -- (2) Carry-forward guard: refuse to discard a stream's entire history; there must be a surviving event
  -- above the close point (the domain's closing event / new origin).
  SELECT count(*) INTO v_carry
  FROM __SCHEMA__.wh_event_store es
  WHERE es.stream_id = p_stream_id AND es.version > p_through_version;

  IF v_carry = 0 THEN
    RETURN QUERY SELECT 'no_carry_forward'::TEXT, 0::BIGINT;
    RETURN;
  END IF;

  -- Migration 212 (#1486), (3) Hold guard: an active destruction hold on an event in the range is a recorded
  -- PreDestruction decision to keep that event (Cancel, or Defer until a time not yet reached).
  SELECT count(*) INTO v_held
  FROM __SCHEMA__.wh_event_destruction_hold h
  JOIN __SCHEMA__.wh_event_store es ON es.event_id = h.event_id
  WHERE es.stream_id = p_stream_id
    AND es.version <= p_through_version
    AND h.hold_until > NOW();

  IF v_held > 0 THEN
    RETURN QUERY SELECT 'held'::TEXT, 0::BIGINT;
    RETURN;
  END IF;

  -- Migration 212 (#1486), (4) Cursor guard: a cursor inside the range moves onto the boundary event (the
  -- first survivor above the close point). The cursor is the read position (the next apply reads events with
  -- an id above it), so moving it onto a boundary event its perspective still owes would skip that event.
  -- Refuse until the perspective has consumed it.
  SELECT es.event_id INTO v_boundary
  FROM __SCHEMA__.wh_event_store es
  WHERE es.stream_id = p_stream_id AND es.version > p_through_version
  ORDER BY es.version
  LIMIT 1;

  SELECT count(*) INTO v_behind
  FROM __SCHEMA__.wh_perspective_cursors c
  JOIN __SCHEMA__.wh_event_store es ON es.event_id = c.last_event_id
  JOIN __SCHEMA__.wh_perspective_events pe
    ON pe.stream_id = c.stream_id
   AND pe.perspective_name = c.perspective_name
   AND pe.event_id = v_boundary
  WHERE c.stream_id = p_stream_id
    AND es.stream_id = p_stream_id
    AND es.version <= p_through_version
    AND pe.processed_at IS NULL;

  IF v_behind > 0 THEN
    RETURN QUERY SELECT 'cursor_behind'::TEXT, 0::BIGINT;
    RETURN;
  END IF;

  -- Archive BEFORE truncate. Same transaction as the DELETEs below => atomic: a failure here rolls back the
  -- whole close, so the detail is never lost (stays hot) nor left half-archived. Body columns come from
  -- wh_event_body (post-split every body lives there); LEFT JOIN tolerates an already-reaped body defensively.
  IF p_archive THEN
    INSERT INTO __SCHEMA__.wh_event_archive
      (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, scope, version, created_at)
    SELECT es.event_id, es.stream_id, es.aggregate_id, es.aggregate_type, es.event_type,
           eb.event_data, eb.metadata, es.scope, es.version, es.created_at
    FROM __SCHEMA__.wh_event_store es
    LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = es.event_id
    WHERE es.stream_id = p_stream_id AND es.version <= p_through_version
    ON CONFLICT (event_id) DO NOTHING;
  END IF;

  -- Migration 093 (#80-F): capture, per lane, the sequence range of the fold-relevant rows this
  -- close is about to truncate (263, #1891: a digest-exempt type is not fold-relevant, so a prefix of only
  -- audit records refolds nothing and leaves the origin generation alone); the sealed epochs covering those ranges refold AFTER the delete
  -- (they must fold the post-close world), and the ranges are unknowable once the rows are gone.
  SELECT array_agg(t.lane), array_agg(t.mn), array_agg(t.mx)
  INTO v_lanes, v_mins, v_maxs
  FROM (
    SELECT COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid) AS lane,
           MIN(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mn,
           MAX(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mx
    FROM __SCHEMA__.wh_event_store es
    WHERE es.stream_id = p_stream_id
      AND es.version <= p_through_version
      AND COALESCE(es.flags, 0) & 8 = 0
      AND COALESCE(es.flags, 0) & c_flag_at_most_once = 0  -- 214 (#1484)
      AND split_part(es.event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__)  -- 263 (#1891)
    GROUP BY 1
  ) t
  WHERE t.mn IS NOT NULL;

  -- Migration 087 (A1c): subtract the about-to-be-truncated rows from wh_stream_digests BEFORE
  -- the delete (the rows must still exist to be read). Only rows that ever
  -- contributed to a digest (non-ephemeral, not at-most-once, not a digest-exempt type, 263) are XOR-ed back out; XOR is
  -- self-inverse, so folding the same hashes again removes them. A missing bucket is a no-op;
  -- the periodic full-sweep verification heals any residual drift.
  PERFORM __SCHEMA__._wh_fold_stream_digests(
    array_agg(COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid)), array_agg(COALESCE(es.scope ->> 't', '')),
    array_agg(es.event_type), array_agg(es.stream_id), array_agg(es.event_id), 'unfold', NOW())
  FROM __SCHEMA__.wh_event_store es
  WHERE es.stream_id = p_stream_id
    AND es.version <= p_through_version
    AND COALESCE(es.flags, 0) & 8 = 0
    AND COALESCE(es.flags, 0) & c_flag_at_most_once = 0  -- 214 (#1484)
    AND split_part(es.event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__);  -- 263 (#1891)

  -- Drop buckets the subtraction emptied (a fully-truncated (type, stream) bucket). Separate
  -- statement so the UPDATE above is visible.
  DELETE FROM __SCHEMA__.wh_stream_digests d
  WHERE d.stream_id = p_stream_id AND d.event_count <= 0;

  -- Migration 212 (#1486): snapshots a rewind could restore and then not replay. Kept: a snapshot at or
  -- above the close point, and, for a perspective whose cursor is inside the range, one at or after that
  -- cursor's event (the perspective applied nothing after it). Removed: every other versioned snapshot below
  -- that floor, and a versionless snapshot on a truncated event. Runs before the re-anchor below, which moves
  -- the cursor this floor is read from.
  DELETE FROM __SCHEMA__.wh_perspective_snapshots sn
  WHERE sn.stream_id = p_stream_id
    AND (
      (sn.snapshot_version IS NULL
       AND EXISTS (
         SELECT 1 FROM __SCHEMA__.wh_event_store es
         WHERE es.event_id = sn.snapshot_event_id
           AND es.stream_id = p_stream_id
           AND es.version <= p_through_version))
      OR sn.snapshot_version < COALESCE(
        (SELECT es.version
         FROM __SCHEMA__.wh_perspective_cursors c
         JOIN __SCHEMA__.wh_event_store es ON es.event_id = c.last_event_id
         WHERE c.stream_id = p_stream_id
           AND c.perspective_name = sn.perspective_name
           AND es.stream_id = p_stream_id
           AND es.version <= p_through_version),
        p_through_version));

  -- Migration 212 (#1486): re-anchor the cursors inside the range onto the boundary event. The cursor guard
  -- above proved none of their perspectives still owes it. fk_perspective_cursors_event is ON DELETE
  -- RESTRICT on the EF Core schema, so this has to happen before the events go.
  UPDATE __SCHEMA__.wh_perspective_cursors c
  SET last_event_id = v_boundary
  FROM __SCHEMA__.wh_event_store es
  WHERE c.stream_id = p_stream_id
    AND es.event_id = c.last_event_id
    AND es.stream_id = p_stream_id
    AND es.version <= p_through_version;

  -- Migration 212 (#1486): finished work rows and lapsed destruction holds of the truncated events. The
  -- gates above proved no unprocessed work row and no active hold remains in the range.
  DELETE FROM __SCHEMA__.wh_perspective_events pe
  USING __SCHEMA__.wh_event_store es
  WHERE pe.event_id = es.event_id
    AND pe.stream_id = p_stream_id
    AND pe.processed_at IS NOT NULL
    AND es.stream_id = p_stream_id
    AND es.version <= p_through_version;

  DELETE FROM __SCHEMA__.wh_event_destruction_hold h
  USING __SCHEMA__.wh_event_store es
  WHERE h.event_id = es.event_id
    AND es.stream_id = p_stream_id
    AND es.version <= p_through_version;

  -- 358 (#2152): a prefix holding the stream's first event (version 0 or 1) leaves that event's business time on the
  -- stream's ledger row, so a rebuild that writes the stream's row afresh still dates it at the stream's start. Written
  -- once, never over a value already recorded.
  UPDATE __SCHEMA__.wh_stream_lifecycle l
  SET first_event_at = f.business_time
  FROM (
    SELECT COALESCE(__SCHEMA__._wh_event_business_time(eb.metadata), es.created_at) AS business_time
    FROM __SCHEMA__.wh_event_store es
    LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = es.event_id
    WHERE es.stream_id = p_stream_id AND es.version <= LEAST(p_through_version, 1)
    ORDER BY es.version
    LIMIT 1
  ) f
  WHERE l.stream_id = p_stream_id AND l.first_event_at IS NULL;

  -- Truncate the detail at/below the close point: bodies first, then pointers. Cursors were re-anchored
  -- above, so fk_perspective_cursors_event (EF Core schema) no longer references anything removed here.
  DELETE FROM __SCHEMA__.wh_event_body eb
  USING __SCHEMA__.wh_event_store es
  WHERE eb.event_id = es.event_id
    AND es.stream_id = p_stream_id
    AND es.version <= p_through_version;

  DELETE FROM __SCHEMA__.wh_event_store es
  WHERE es.stream_id = p_stream_id AND es.version <= p_through_version;
  GET DIAGNOSTICS v_deleted = ROW_COUNT;

  -- Migration 093 (#80-F): refold the sealed epochs the truncation touched (post-delete world),
  -- then bump the generation ONCE; consumers reset their seals and re-verify instead of
  -- alarming on deliberate change.
  IF v_lanes IS NOT NULL THEN
    FOR v_i IN 1 .. array_length(v_lanes, 1) LOOP
      PERFORM __SCHEMA__._wh_refold_epochs_covering(v_lanes[v_i], v_mins[v_i], v_maxs[v_i]);
    END LOOP;
    PERFORM __SCHEMA__._wh_bump_origin_generation();
  END IF;

  RETURN QUERY SELECT 'truncated'::TEXT, v_deleted;  -- 240 (#1731): was 'closed'
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_truncate_stream_prefix(UUID, BIGINT, BOOLEAN) IS
'Gated prefix truncate of one stream through a version (close_stream until 240, #1731). Refuses with debug_skipped, blocked (unprocessed work in the range), no_carry_forward (no event above the truncate point), held (an active destruction hold in the range) or cursor_behind (a cursor in the range whose perspective still owes the boundary event). On truncated it re-anchors cursors in the range onto the boundary event, removes snapshots a rewind could no longer replay from, removes processed work rows and lapsed holds of the truncated events, and keeps their deduplication entries (#1486). At-most-once events (flags & 32), never folded, are never subtracted (214), nor are the digest-exempt types (the audit records, 216), so a prefix of only those refolds nothing and leaves the origin generation alone (263, #1891). 358: records the business time of the stream''s first event, when the prefix holds it, on the stream''s ledger row (first_event_at, #2152).';
