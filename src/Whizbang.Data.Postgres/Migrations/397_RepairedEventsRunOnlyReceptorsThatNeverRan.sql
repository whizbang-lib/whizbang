-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 397_RepairedEventsRunOnlyReceptorsThatNeverRan.sql
-- Date: 2026-11-11
-- Description: A repaired event runs a receptor only when this service can prove it never handled the event (#2273).
--
--              Integrity repair delivers historical events a receiver is missing, with their original ids. A receiver
--              can be missing an event it handled (a trigger receptor ran, then the stored row was lost or never kept),
--              and with no deduplication record left, the repaired copy ran the receptor a second time. The owner's
--              decision: a repaired event runs a receptor only when the receptor opts in ([RunOnRepair]), the event's
--              origin time is inside the configured handled lookback, and a ledger of handled events shows it was never
--              handled here. This migration is the ledger:
--
--                wh_receptor_handled           one row per event this service completed as processed, keyed by the
--                                              event id alone. Range-partitioned by that id (a version-7 id begins
--                                              with its origin time), so a lookup is one index probe in one partition
--                                              and retention drops whole partitions once the partition manager keeps
--                                              the table; until then everything lands in the default partition and
--                                              the sweep removes rows.
--                wh_receptor_handled_coverage  the moment from which every handled event is recorded. It starts when
--                                              this migration first runs and only moves forward, when the sweep keeps
--                                              less than before, so a lookback raised past it is clamped to it and an
--                                              incomplete window never looks complete.
--                process_inbox_completions     records the completed event, inside the completion it already runs:
--                                              no round trip is added, and an instance on an earlier release records
--                                              too, because it calls this same function.
--                wh_receptor_handled_lookup    the handled subset of a bundle's ids and the coverage start, one call
--                                              per bundle.
--                wh_receptor_handled_sweep     moves the coverage start up to the retention cutoff, then removes the
--                                              older rows from the relation a row sweep owns (the table, or only its
--                                              default partition once partitions are maintained), in one transaction.
--
--              Cost: one primary-key insert per completed event (one heap tuple and one index entry, nothing read) in
--              the completion that already deletes the inbox row.
--
--              Number 397: 392 to 396 are taken by changes in flight.
--
-- Reversal: lossy (the ledger and its coverage are dropped and process_inbox_completions is restored from its last
--           word, 236; a later forward run starts coverage afresh, so nothing reads the lost records as complete)
-- Dependencies: 236 (process_inbox_completions' last word, copied whole with the change above), 233
--               (wh_partition_sweep_relation, _wh_partition_bound), 162 (wh_inbox_state.is_event)
-- Objects: wh_receptor_handled, wh_receptor_handled_default, wh_receptor_handled_coverage, process_inbox_completions,
--          wh_receptor_handled_lookup, wh_receptor_handled_sweep
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_receptor_handled_lookup');
SELECT __SCHEMA__.drop_all_overloads('wh_receptor_handled_sweep');

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- The ledger
CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_receptor_handled (
  event_id UUID NOT NULL,
  CONSTRAINT pk_receptor_handled PRIMARY KEY (event_id)
) PARTITION BY RANGE (event_id);

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_receptor_handled_default
  PARTITION OF __SCHEMA__.wh_receptor_handled DEFAULT;

COMMENT ON TABLE __SCHEMA__.wh_receptor_handled IS
  'Issue #2273: the events this service completed as processed, by id, kept for the repair receptor lookback. A '
  'repaired event inside the coverage (wh_receptor_handled_coverage) that is absent here was never handled here.';

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_receptor_handled_coverage (
  id SMALLINT NOT NULL DEFAULT 1,
  covers_from TIMESTAMPTZ NOT NULL,
  CONSTRAINT pk_receptor_handled_coverage PRIMARY KEY (id),
  CONSTRAINT ck_receptor_handled_coverage_single CHECK (id = 1)
);

-- Coverage starts the first time this runs; a re-run keeps it.
INSERT INTO __SCHEMA__.wh_receptor_handled_coverage (id, covers_from) VALUES (1, clock_timestamp())
ON CONFLICT (id) DO NOTHING;

COMMENT ON TABLE __SCHEMA__.wh_receptor_handled_coverage IS
  'Issue #2273: one row, the moment from which wh_receptor_handled records every handled event. It only moves forward.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- process_inbox_completions: last word 236, copied whole with the 397 delta (the handled record).
-- <docs>fundamentals/work-coordinator/handler-commit</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HandledLedgerSqlTests.cs:AnEventCompletedAsProcessed_IsRecordedHandledAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HandledLedgerSqlTests.cs:ACompletionThatIsNotProcessed_ACommand_AndAnUntimedId_AreNotRecordedAsync</tests>
-- <docs>resilience/stream-integrity#repaired-events-and-receptors</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitHandlerResultSqlTests.cs:CommitHandlerResult_HappyPath_MarksInboxProcessedAndStoresOutboxAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitHandlerResultSqlTests.cs:CommitHandlerResult_DebugModeInRequest_RetainsInboxRowEvenWhenEventStoredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitHandlerResultSqlTests.cs:CommitHandlerResult_ProductionMode_DeletesInboxRowOnEventStoredCompletionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredDeadLetterReentrySqlTests.cs:ARecoveredMessage_CompletedUnprocessedByAGate_IsHeldForReview_WithTheOutcomeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredDeadLetterReentrySqlTests.cs:ARecoveredCommand_CompletedAsProcessed_IsRecoveredAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.process_inbox_completions(
  p_completions JSONB,
  p_now TIMESTAMPTZ,
  p_debug_mode BOOLEAN DEFAULT FALSE
) RETURNS TABLE(
  message_id UUID,
  stream_id UUID,
  was_deleted BOOLEAN
) AS $$
DECLARE
  v_completion RECORD;
  v_current_status INTEGER;
  v_new_status INTEGER;
  v_stream_id UUID;
  v_recovered_from UUID;   -- 236: the dead letter a recovered message re-entered from
  v_is_event BOOLEAN;      -- 236
BEGIN
  IF jsonb_array_length(p_completions) = 0 THEN RETURN; END IF;

  FOR v_completion IN
    SELECT
      (elem->>__ENVELOPE_FIELD_MESSAGE_ID__)::UUID as msg_id,
      (elem->>'Status')::INTEGER as status_flags
    FROM jsonb_array_elements(p_completions) as elem
  LOOP
    -- Get current status and stream_id
    -- 162: both columns are on the state table now (stream_id as an immutable copy), so the
    -- lookup never touches the wide row.
    -- 236: and the recovery columns, from the same row this lookup already reads.
    SELECT ist.status, ist.stream_id, ist.recovered_from_dead_letter_id, ist.is_event
    INTO v_current_status, v_stream_id, v_recovered_from, v_is_event
    FROM __SCHEMA__.wh_inbox_state ist
    WHERE ist.message_id = v_completion.msg_id;

    -- Skip if message not found (already deleted or never existed)
    IF NOT FOUND THEN
      CONTINUE;
    END IF;

    v_new_status := v_current_status | v_completion.status_flags;

    -- 397 (#2273): an event completed as processed is recorded handled, so a repair can later prove it was never
    -- handled here by its absence. Only a time-ordered (version 7) id: the ledger is kept by the time an id carries,
    -- and an event whose id carries none is never run on repair, so its record would be dead weight. One index entry.
    IF v_is_event AND (v_completion.status_flags & 2) = 2 AND substr(v_completion.msg_id::TEXT, 15, 1) = '7' THEN
      INSERT INTO __SCHEMA__.wh_receptor_handled (event_id) VALUES (v_completion.msg_id) ON CONFLICT DO NOTHING;
    END IF;

    -- 236 (#1664): a recovered message settles its dead letter when it completes. It was processed when the
    -- dispatch completed it as processed (EventStored), or when it is an event the inbox copy already appended
    -- (a gate after the copy declines only the receptors). Anything else is a gate completing it unprocessed.
    IF v_recovered_from IS NOT NULL THEN
      PERFORM __SCHEMA__._wh_settle_recovered_dead_letter(
        v_recovered_from,
        (v_new_status & 2) = 2
          OR (v_is_event AND EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = v_completion.msg_id)),
        p_now);
    END IF;

    IF p_debug_mode THEN
      -- Debug mode: Retain message for troubleshooting
      -- 162: status is work state too, so this stays one statement against one table.
      UPDATE __SCHEMA__.wh_inbox_state ist
      SET status = v_new_status,
          processed_at = p_now,
          instance_id = NULL,
          lease_expiry = NULL
      WHERE ist.message_id = v_completion.msg_id;

      RETURN QUERY SELECT v_completion.msg_id AS message_id, v_stream_id AS stream_id, FALSE AS was_deleted;

    ELSE
      -- Production: Delete if EventStored flag set (inbox completion = event stored)
      IF (v_new_status & 2) = 2 THEN
        -- 162: unchanged. The lease row goes with it through ON DELETE CASCADE, which is why the
        -- foreign key exists rather than being tidiness: a lease left behind for a deleted message
        -- is a row every reclaim path would keep considering forever.
        DELETE FROM __SCHEMA__.wh_inbox i WHERE i.message_id = v_completion.msg_id;
        RETURN QUERY SELECT v_completion.msg_id AS message_id, v_stream_id AS stream_id, TRUE AS was_deleted;
      ELSE
        -- Event not yet stored, retain with updated status
        -- 162: same single statement as the debug branch above.
        UPDATE __SCHEMA__.wh_inbox_state ist
        SET status = v_new_status,
            processed_at = p_now,
            instance_id = NULL,
            lease_expiry = NULL
        WHERE ist.message_id = v_completion.msg_id;
        RETURN QUERY SELECT v_completion.msg_id AS message_id, v_stream_id AS stream_id, FALSE AS was_deleted;
      END IF;
    END IF;
  END LOOP;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.process_inbox_completions(JSONB, TIMESTAMPTZ, BOOLEAN) IS
  'Applies inbox completions: deletes a row completed as EventStored, otherwise records the status (162: on wh_inbox_state). 236 (#1664): a recovered message settles the dead letter it re-entered from, Recovered when processed and HoldForReview with the outcome when a gate completed it unprocessed. 397 (#2273): an event completed as processed is recorded in wh_receptor_handled.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- The handled subset of p_event_ids and the coverage start. With partitions maintained, each id is pruned to the one
-- partition its time falls in.
-- <docs>resilience/stream-integrity#repaired-events-and-receptors</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HandledLedgerSqlTests.cs:TheLookup_AnswersTheHandledSubset_AndTheCoverageStartAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperHandledLedgerTests.cs:Lookup_AndSweep_RunOnTheDapperDriverAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_receptor_handled_lookup(p_event_ids UUID[])
RETURNS TABLE(covers_from TIMESTAMPTZ, handled_ids UUID[]) AS $$
  SELECT
    -- A missing coverage row proves nothing older than now.
    COALESCE((SELECT c.covers_from FROM __SCHEMA__.wh_receptor_handled_coverage c WHERE c.id = 1), now()),
    COALESCE(
      (SELECT array_agg(h.event_id) FROM __SCHEMA__.wh_receptor_handled h WHERE h.event_id = ANY(p_event_ids)),
      '{}'::UUID[]);
$$ LANGUAGE sql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_receptor_handled_lookup(UUID[]) IS
  'Issue #2273: which of the ids wh_receptor_handled records as handled, and the moment its coverage starts.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- Keeps the ledger to p_keep. Coverage moves first, in the same transaction as the delete, so no reader sees records
-- removed from a window it still reads as covered. The partition manager drops expired partitions after this runs in
-- the same maintenance cycle, by the same window.
-- <docs>resilience/stream-integrity#repaired-events-and-receptors</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HandledLedgerSqlTests.cs:TheSweep_MovesCoverageUpToTheCutoff_ThenRemovesOlderRecordsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HandledLedgerSqlTests.cs:AWindowGrownPastTheCoverage_NeverMovesTheCoverageBackAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HandledLedgerSqlTests.cs:OncePartitionsAreMaintained_TheSweepClearsOnlyTheDefaultPartitionAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperHandledLedgerTests.cs:Lookup_AndSweep_RunOnTheDapperDriverAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_receptor_handled_sweep(p_now TIMESTAMPTZ, p_keep INTERVAL)
RETURNS BIGINT AS $$
DECLARE
  v_cutoff TIMESTAMPTZ := p_now - GREATEST(p_keep, INTERVAL '0');
  v_relation TEXT;
  v_removed BIGINT := 0;
BEGIN
  UPDATE __SCHEMA__.wh_receptor_handled_coverage c
  SET covers_from = GREATEST(c.covers_from, v_cutoff)
  WHERE c.id = 1;

  v_relation := __SCHEMA__.wh_partition_sweep_relation('wh_receptor_handled');
  IF v_relation IS NOT NULL THEN
    EXECUTE format('DELETE FROM %s WHERE event_id < $1', v_relation)
      USING __SCHEMA__._wh_partition_bound(v_cutoff, 'uuid'::REGTYPE)::UUID;
    GET DIAGNOSTICS v_removed = ROW_COUNT;
  END IF;
  RETURN v_removed;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_receptor_handled_sweep(TIMESTAMPTZ, INTERVAL) IS
  'Issue #2273: moves the handled ledger''s coverage start up to p_now - p_keep, then removes the records of events '
  'older than that from the relation a row sweep owns. Returns the records removed.';
