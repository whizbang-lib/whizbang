-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 279_CollectiveApplyProgress.sql
-- Date: 2026-11-01
-- Description: A collective apply records how far it reached, batch by batch, so every retry resumes after its last
--              committed batch (#1969).
--
--              The collective sink applies a collective to its rows in batches, each batch in a transaction of its own,
--              and its cursor lived only in memory. Every retry (a busy apply lock, a failure, a crash, a re-delivery in
--              the window before the completion was written, a dead-letter recovery, a fan-out whose later handler
--              failed) applied the collective again from its first row, so an increment-style setter reached the rows
--              of the committed batches twice.
--
--              wh_collective_apply_progress holds one row per collective event and handler (entry_key: the handler
--              type, method and model, so two handlers that apply one event to one model keep their own progress).
--              Each batch statement reads its row, selects the next batch after through_id, moves the row and updates
--              the batch, in one statement, inside the batch's transaction and under its apply lock: no extra round
--              trip, no write per row and no new lock. The move is compare-and-set on the through_id the batch started
--              after. A row marked completed skips the apply. applied_at is the time the apply started; a resumed apply
--              stamps the rows it reaches with it.
--
--              The primary key is the only index: every reader and writer finds a row by it.
--
--              A dead-lettered apply (#1970) is marked abandoned by a trigger on wh_dead_letters, and a handler with no
--              record of its own is recorded under entry_key '*' as having applied nothing. wh_collective_apply_reach
--              tells a replay how far each apply reached and which collectives are still queued, so a rewind or a
--              rebuild folds a collective into a row only where the live sink applied it or will not apply it.
--              Dead-letter recovery resumes the apply after through_id and completes the rest exactly once.
--
--              The snapshot fence (276, #1963) counts a collective with an abandoned record as in flight (owner
--              decision, 2026-11-01): a snapshot taken while it is dead-lettered would record it as held by rows its
--              batches never reached, which recovery may yet reach live. When the dead letter settles Recovered, a
--              trigger clears the collective's abandoned marks and removes the '*' record (every handler then has a
--              record of its own); purging the dead letter removes them as below. Either way the fence is quiet again.
--
--              Retention: maintenance (reap_unbounded_ledgers) sweeps a finished record past
--              collective_apply_progress_retention_days (default 7) once its collective's work row is no longer
--              pending. An abandoned record is deleted with the last dead letter of its collective; after that a
--              rewind folds that collective in full again.
--
--              Number 279: reserved for this change.
--
-- Dependencies: 050 (wh_dead_letters, wh_dead_letters_stream_idx), 073 (ix_perspective_events_event_id_unprocessed),
--               238 (last word of reap_unbounded_ledgers), 241 (last word of move_to_dead_letters, whose perspective
--               capture records the event id and the perspective name), 236 (_wh_settle_recovered_dead_letter, which
--               sets recovered_at), 276 (last word of wh_collective_snapshot_fence)
-- Objects: wh_collective_apply_progress, _wh_collective_progress_abandon, _wh_collective_progress_forget,
--          _wh_collective_progress_recovered, trg_wh_collective_progress_abandon, trg_wh_collective_progress_forget,
--          trg_wh_collective_progress_recovered, wh_collective_apply_reach, wh_collective_snapshot_fence,
--          reap_unbounded_ledgers
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_collective_apply_progress (
  event_id    UUID        NOT NULL,                -- the collective event
  entry_key   TEXT        NOT NULL,                -- CollectiveApplyEntry.EntryKey: handler type, method and model
  through_id  UUID,                                -- the greatest row id the apply has reached
  completed   BOOLEAN     NOT NULL DEFAULT FALSE,  -- every batch committed
  abandoned   BOOLEAN     NOT NULL DEFAULT FALSE,  -- the apply was dead-lettered before it completed
  applied_at  TIMESTAMPTZ NOT NULL,                -- when the apply started; a resumed apply reuses it
  PRIMARY KEY (event_id, entry_key)
);

COMMENT ON TABLE __SCHEMA__.wh_collective_apply_progress IS
  'How far each handler''s apply of a collective event reached (#1969): the greatest row id its committed batches '
  'updated, and whether every batch committed. A retry resumes after through_id and a completed apply is skipped. '
  'Moved by the batch statement itself, compare-and-set on through_id.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- A dead letter marks the apply abandoned (#1970). When the collective sink's work row is dead-lettered, every unfinished
-- record of the collective stops where it is (abandoned), and a record under entry_key '*' says that a handler with no
-- record of its own applied nothing. A rewind folds an abandoned apply only into the rows it reached, and an apply that
-- reached nothing into no row. Dead-letter recovery re-queues the collective; its batches resume after through_id and
-- clear abandoned as they move the record. A trigger, so every path that dead-letters a work row records it.
-- <docs>fundamentals/messaging/collective-events#dead-lettered-collectives</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveApplyProgressSqlTests.cs:DeadLetteringACollectiveSinkRow_MarksItsUnfinishedApplyAbandoned_AndLeavesTheFinishedOneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveApplyProgressSqlTests.cs:DeadLetteringACollectiveThatAppliedNothing_RecordsThatNothingWasAppliedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveApplyProgressSqlTests.cs:DeadLetteringAnotherPerspectivesWorkRow_RecordsNothingAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_collective_progress_abandon() RETURNS TRIGGER AS $$
DECLARE
  v_event_id UUID := (NEW.envelope ->> 'event_id')::UUID;
BEGIN
  UPDATE __SCHEMA__.wh_collective_apply_progress
  SET abandoned = TRUE
  WHERE event_id = v_event_id AND NOT completed;

  INSERT INTO __SCHEMA__.wh_collective_apply_progress (event_id, entry_key, through_id, completed, abandoned, applied_at)
  VALUES (v_event_id, '*', NULL, FALSE, TRUE, NEW.dead_lettered_at)
  ON CONFLICT (event_id, entry_key) DO UPDATE SET abandoned = TRUE;
  RETURN NULL;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_collective_progress_abandon() IS
  'Trigger (#1970): a dead-lettered __collective__ work row marks the collective''s unfinished apply records abandoned, '
  'and records under entry_key ''*'' that a handler with no record applied nothing.';

DROP TRIGGER IF EXISTS trg_wh_collective_progress_abandon ON __SCHEMA__.wh_dead_letters;
CREATE TRIGGER trg_wh_collective_progress_abandon
  AFTER INSERT ON __SCHEMA__.wh_dead_letters
  FOR EACH ROW
  WHEN (NEW.source_table = 'wh_perspective_events' AND NEW.perspective_name = '__collective__')
  EXECUTE FUNCTION __SCHEMA__._wh_collective_progress_abandon();

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- A recovered dead letter clears the abandoned marks (owner decision, 2026-11-01). The recovery resumed the apply and the
-- completion settled the dead letter Recovered (_wh_settle_recovered_dead_letter, 236): every handler's apply has finished
-- and has a record of its own, so the '*' record is removed and no record of the collective stays abandoned. The snapshot
-- fence stops counting the collective as in flight.
-- <docs>fundamentals/messaging/collective-events#dead-lettered-collectives</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveSnapshotFenceSqlTests.cs:Fence_AfterTheDeadLetterIsRecovered_AndTheApplyCompleted_IsQuietAgainAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/CollectiveReplayRebuildIntegrationTests.cs:Rewind_AfterRecoveryCompletedACollectiveDeadLetteredPartway_FoldsItIntoTheRowsTheRecoveryReachedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_collective_progress_recovered() RETURNS TRIGGER AS $$
DECLARE
  v_event_id UUID := (NEW.envelope ->> 'event_id')::UUID;
BEGIN
  DELETE FROM __SCHEMA__.wh_collective_apply_progress
  WHERE event_id = v_event_id AND entry_key = '*';

  UPDATE __SCHEMA__.wh_collective_apply_progress
  SET abandoned = FALSE
  WHERE event_id = v_event_id AND abandoned;
  RETURN NULL;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_collective_progress_recovered() IS
  'Trigger (owner decision, 2026-11-01): a dead-lettered collective settled Recovered clears its abandoned marks and its '
  '''*'' record, so the snapshot fence stops counting it as in flight.';

DROP TRIGGER IF EXISTS trg_wh_collective_progress_recovered ON __SCHEMA__.wh_dead_letters;
CREATE TRIGGER trg_wh_collective_progress_recovered
  AFTER UPDATE OF recovered_at ON __SCHEMA__.wh_dead_letters
  FOR EACH ROW
  WHEN (OLD.recovered_at IS NULL AND NEW.recovered_at IS NOT NULL
        AND NEW.source_table = 'wh_perspective_events' AND NEW.perspective_name = '__collective__')
  EXECUTE FUNCTION __SCHEMA__._wh_collective_progress_recovered();

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- An abandoned record goes with its dead letter (#1970, the owner's retention choice). When the last dead letter of a
-- collective is purged, its abandoned records are deleted; after that a rewind folds the collective in full again. A
-- finished record is left to the maintenance sweep below. The lookup walks wh_dead_letters_stream_idx (050): a
-- collective's work rows, and so its dead letters, are all on the collective's own sink stream.
-- <docs>fundamentals/messaging/collective-events#dead-lettered-collectives</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveApplyProgressSqlTests.cs:PurgingTheDeadLetter_SweepsItsAbandonedRecords_AndKeepsTheFinishedOnesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveApplyProgressSqlTests.cs:PurgingADeadLetter_KeepsTheAbandonedRecords_WhileAnotherDeadLetterOfTheCollectiveRemainsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_collective_progress_forget() RETURNS TRIGGER AS $$
BEGIN
  IF NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_dead_letters d
      WHERE d.stream_id IS NOT DISTINCT FROM OLD.stream_id
        AND d.source_table = 'wh_perspective_events'
        AND d.perspective_name = '__collective__'
        AND d.envelope ->> 'event_id' = OLD.envelope ->> 'event_id') THEN
    DELETE FROM __SCHEMA__.wh_collective_apply_progress
    WHERE event_id = (OLD.envelope ->> 'event_id')::UUID AND abandoned;
  END IF;
  RETURN NULL;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_collective_progress_forget() IS
  'Trigger (#1970): purging the last dead letter of a collective deletes its abandoned apply records.';

DROP TRIGGER IF EXISTS trg_wh_collective_progress_forget ON __SCHEMA__.wh_dead_letters;
CREATE TRIGGER trg_wh_collective_progress_forget
  AFTER DELETE ON __SCHEMA__.wh_dead_letters
  FOR EACH ROW
  WHEN (OLD.source_table = 'wh_perspective_events' AND OLD.perspective_name = '__collective__')
  EXECUTE FUNCTION __SCHEMA__._wh_collective_progress_forget();

-- ONE overload: the sweep force-replays by name and a second signature would be left behind.
SELECT __SCHEMA__.drop_all_overloads('wh_collective_apply_reach');

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- How far each apply of the named collectives reached, for a replay (#1970): one row per record, and one row (entry_key
-- NULL, out_queued TRUE) per collective whose __collective__ work row is still pending (queued, mid-apply, or recovered
-- from a dead letter). The records are found by their primary key, the pending work rows by
-- ix_perspective_events_event_id_unprocessed (073).
-- <docs>fundamentals/messaging/collective-events#collective-replay-from-a-start</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveApplyProgressSqlTests.cs:Reach_ReadsEveryRecordOfTheEvents_AndWhichCollectivesAreStillQueuedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/CollectiveReplayRebuildIntegrationTests.cs:Rewind_WhileACollectiveIsMidApply_FoldsItOnlyIntoTheRowsItReachedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_collective_apply_reach(p_event_ids UUID[])
RETURNS TABLE(
  out_event_id UUID,
  out_entry_key TEXT,
  out_through_id UUID,
  out_completed BOOLEAN,
  out_abandoned BOOLEAN,
  out_queued BOOLEAN
) AS $$
  SELECT p.event_id, p.entry_key, p.through_id, p.completed, p.abandoned, FALSE
  FROM __SCHEMA__.wh_collective_apply_progress p
  WHERE p.event_id = ANY(p_event_ids)
  UNION ALL
  SELECT DISTINCT pe.event_id, NULL::TEXT, NULL::UUID, FALSE, FALSE, TRUE
  FROM __SCHEMA__.wh_perspective_events pe
  WHERE pe.event_id = ANY(p_event_ids)
    AND pe.processed_at IS NULL
    AND pe.perspective_name = '__collective__';
$$ LANGUAGE sql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_collective_apply_reach(UUID[]) IS
  'How far each apply of the given collectives reached (#1970): every progress record, and which collectives are still '
  'queued. A replay folds a collective into a row only as far as the live sink has applied it or will not.';

-- ONE overload: the sweep force-replays by name and a second signature would be left behind.
SELECT __SCHEMA__.drop_all_overloads('wh_collective_snapshot_fence');

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- wh_collective_snapshot_fence, last word 276_CollectiveSnapshotFence.sql, copied whole. The only change: a collective of
-- the tenant and types with an abandoned apply record (#1970: dead-lettered partway, or before any batch) is in flight
-- (owner decision, 2026-11-01), until its dead letter is recovered or purged. The probe reads the abandoned records,
-- which the progress table holds only while a dead letter does, and finds each one's event by its primary key.
-- <docs>fundamentals/perspectives/snapshots#collectives</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveSnapshotFenceSqlTests.cs:Fence_WithNoCollectiveOfTheRowInFlight_IsQuiet_AndMarksTheStoresHighestCommitSequenceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveSnapshotFenceSqlTests.cs:Fence_WithACollectiveOfTheRowStillQueued_IsInFlightAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveSnapshotFenceSqlTests.cs:Fence_WithACollectiveOfTheRowCompleteButUnstamped_IsInFlightAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveSnapshotFenceSqlTests.cs:Fence_IgnoresAnotherTenantsCollectives_AndCollectivesOfOtherTypesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveSnapshotFenceSqlTests.cs:Fence_OfARowWithNoTenant_SeesOnlyCollectivesWithNoTenantAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveSnapshotFenceSqlTests.cs:Fence_AfterAMark_IsInFlight_WhenACollectiveOfTheRowWasStampedPastItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveSnapshotFenceSqlTests.cs:Fence_OfAnEmptyStore_IsQuiet_WithNoMarkAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveSnapshotFenceSqlTests.cs:Fence_WithACollectiveOfTheRowDeadLetteredPartway_IsInFlightAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveSnapshotFenceSqlTests.cs:Fence_AfterTheDeadLetterIsRecovered_AndTheApplyCompleted_IsQuietAgainAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveSnapshotFenceSqlTests.cs:Fence_AfterTheDeadLetterIsPurged_IsQuietAgainAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveSnapshotFenceSqlTests.cs:Fence_IgnoresAnAbandonedCollectiveOfAnotherTenantOrTypeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/CollectiveReplayRebuildIntegrationTests.cs:Rewind_AfterRecoveryCompletedACollectiveDeadLetteredPartway_FoldsItIntoTheRowsTheRecoveryReachedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_collective_snapshot_fence(
  p_tenant TEXT,
  p_event_types TEXT[],
  p_after BIGINT
) RETURNS TABLE(
  out_complete_through BIGINT,
  out_in_flight BOOLEAN
) AS $$
  SELECT
    (SELECT max(es.commit_sequence)
     FROM __SCHEMA__.wh_event_store es
     WHERE es.commit_sequence IS NOT NULL),
    EXISTS (
      SELECT 1
      FROM __SCHEMA__.wh_perspective_events pe
      INNER JOIN __SCHEMA__.wh_event_store es
        ON es.stream_id = pe.stream_id
        AND es.event_id = pe.event_id
      WHERE pe.perspective_name = '__collective__'
        AND pe.processed_at IS NULL
        AND pe.stream_version IS NOT NULL
        AND es.event_type = ANY(p_event_types)
        AND (es.scope ->> 't') IS NOT DISTINCT FROM p_tenant)
    OR EXISTS (
      SELECT 1
      FROM __SCHEMA__.wh_event_store es
      WHERE es.commit_sequence IS NULL
        AND es.event_type = ANY(p_event_types)
        AND (es.scope ->> 't') IS NOT DISTINCT FROM p_tenant)
    OR EXISTS (
      SELECT 1
      FROM __SCHEMA__.wh_collective_apply_progress p
      INNER JOIN __SCHEMA__.wh_event_store es
        ON es.event_id = p.event_id
      WHERE p.abandoned
        AND es.event_type = ANY(p_event_types)
        AND (es.scope ->> 't') IS NOT DISTINCT FROM p_tenant)
    OR (p_after IS NOT NULL AND EXISTS (
      SELECT 1
      FROM __SCHEMA__.wh_event_store es
      WHERE es.commit_sequence IS NOT NULL
        AND es.commit_sequence > p_after
        AND es.event_type = ANY(p_event_types)
        AND (es.scope ->> 't') IS NOT DISTINCT FROM p_tenant));
$$ LANGUAGE sql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_collective_snapshot_fence(TEXT, TEXT[], BIGINT) IS
  'The collective snapshot fence (#1963): the store''s highest commit sequence, and whether a collective of the tenant and '
  'types is in flight (its __collective__ work row unprocessed, its event unstamped, or, when p_after is given, stamped '
  'past it). Read before a snapshot''s row read and after its write; the snapshot records the mark only when neither '
  'read finds one in flight. One statement, so both answers come from one view. 279: a collective with an abandoned apply '
  'record (dead-lettered before it completed) is in flight until its dead letter is recovered or purged.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- reap_unbounded_ledgers, last word 238_LogSweepClearsTheDefaultPartition.sql, copied whole. The only change: a last task
-- sweeps finished collective apply records (#1969) past collective_apply_progress_retention_days (default 7).
-- <docs>fundamentals/workers/maintenance-steps#ledger-retention</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LedgerRetentionSqlTests.cs:Log_OlderThanTheWindow_IsRemoved_YoungerIsKeptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LedgerRetentionSqlTests.cs:Log_ConfiguredWindow_IsHonoredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LedgerRetentionSqlTests.cs:ScheduleRuns_OlderThanTheWindow_AreRemoved_ButTheNewestRunOfEachScheduleIsKeptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LedgerRetentionSqlTests.cs:StreamPurgeAudit_IsKeptForeverByDefault_AndBoundedWhenConfiguredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LedgerRetentionSqlTests.cs:IntegrityLedger_IsTrimmedToTheConfiguredCount_NewestKeptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LedgerRetentionSqlTests.cs:DebugMode_KeepsEveryLedgerRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/LogPartitionConversionTests.cs:OnceConverted_RetentionDropsPartitions_InsteadOfDeletingRowsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/LogPartitionConversionTests.cs:AConvertedLog_WithPartitioningSwitchedOff_IsStillBoundedByTheRowDeleteAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/LogPartitionConversionTests.cs:OnceManaged_TheRowDeleteSweepsOnlyTheDefaultPartition_AndLeavesPartitionsToTheDropAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/LogPartitionConversionTests.cs:OnceManaged_WithoutADefaultPartition_TheRowDeleteSkipsAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperPartitionManagerSqlTests.cs:TheLog_OnceManaged_IsSweptInItsDefaultPartitionOnlyAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveApplyProgressSqlTests.cs:TheSweep_RemovesAFinishedRecordPastTheHorizon_AndKeepsAYoungerOneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveApplyProgressSqlTests.cs:TheSweep_HonorsTheConfiguredHorizonAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveApplyProgressSqlTests.cs:TheSweep_KeepsAFinishedRecord_WhileItsWorkRowIsPendingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveApplyProgressSqlTests.cs:TheSweep_KeepsUnfinishedAndAbandonedRecords_AndTheFinishedRecordsOfADeadLetteredCollectiveAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveApplyProgressSqlTests.cs:TheSweep_UnderDebugMode_KeepsEveryRecordAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.reap_unbounded_ledgers(p_batch_size INTEGER DEFAULT 5000)
RETURNS TABLE(
  task_name TEXT,
  rows_affected BIGINT,
  duration_ms DOUBLE PRECISION,
  status TEXT
) AS $$
DECLARE
  v_debug_mode BOOLEAN;
  v_start TIMESTAMPTZ;
  v_rows BIGINT;
  v_batch BIGINT;
  v_log_days INTEGER;
  v_run_days INTEGER;
  v_audit_days INTEGER;
  v_ledger_max INTEGER;
  v_log_relation TEXT;
  v_log_default BOOLEAN;
  v_progress_days INTEGER;
BEGIN
  SELECT COALESCE(
    (SELECT setting_value::BOOLEAN FROM __SCHEMA__.wh_settings WHERE setting_key = 'debug_mode'), FALSE)
    INTO v_debug_mode;
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'log_retention_days'), 14)
    INTO v_log_days;
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'schedule_run_retention_days'), 30)
    INTO v_run_days;
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'stream_purge_audit_retention_days'), 0)
    INTO v_audit_days;
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'integrity_ledger_max_entries'), 100000)
    INTO v_ledger_max;
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'collective_apply_progress_retention_days'), 7)
    INTO v_progress_days;
  -- 238: the relation the wh_log row delete works on (233): wh_log itself while it is plain or partition maintenance
  -- is off; once it is partitioned and maintained, only its default partition, which holds the rows no partition
  -- covers and which no partition drop ever reaches; NULL when a maintained wh_log has no default partition.
  v_log_relation := __SCHEMA__.wh_partition_sweep_relation('wh_log');
  SELECT COALESCE((SELECT c.relispartition FROM pg_class c WHERE c.oid = to_regclass(v_log_relation)), FALSE)
    INTO v_log_default;

  -- wh_log, by logged_at (indexed, 028; a partition inherits the parent's index).
  v_start := clock_timestamp();
  v_rows := 0;
  IF NOT v_debug_mode AND v_log_relation IS NOT NULL THEN
    LOOP
      EXECUTE format(
        'DELETE FROM %s WHERE log_id IN (SELECT l.log_id FROM %s l WHERE l.logged_at < $1 LIMIT $2)',
        v_log_relation, v_log_relation)
        USING NOW() - (v_log_days * INTERVAL '1 day'), p_batch_size;
      GET DIAGNOSTICS v_batch = ROW_COUNT;
      v_rows := v_rows + v_batch;
      EXIT WHEN v_batch < p_batch_size;
    END LOOP;
  END IF;
  RETURN QUERY SELECT
    'purge_old_log_entries'::TEXT, v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE
      WHEN v_debug_mode THEN 'skipped (debug_mode=true)'
      WHEN v_log_relation IS NULL THEN 'skipped (partitioned: no default partition)'
      WHEN v_log_default THEN 'ok (partitioned: the default partition only; expired partitions are dropped)'
      ELSE 'ok'
    END::TEXT;

  -- wh_schedule_runs, by fired_at, never a schedule's newest run (the (schedule_id, fired_at DESC) index, 066,
  -- answers "is there a newer run" for each candidate).
  v_start := clock_timestamp();
  v_rows := 0;
  IF NOT v_debug_mode THEN
    LOOP
      DELETE FROM __SCHEMA__.wh_schedule_runs
      WHERE run_id IN (
        SELECT r.run_id FROM __SCHEMA__.wh_schedule_runs r
        WHERE r.fired_at < NOW() - (v_run_days * INTERVAL '1 day')
          AND EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_schedule_runs newer
            WHERE newer.schedule_id = r.schedule_id AND newer.fired_at > r.fired_at)
        LIMIT p_batch_size);
      GET DIAGNOSTICS v_batch = ROW_COUNT;
      v_rows := v_rows + v_batch;
      EXIT WHEN v_batch < p_batch_size;
    END LOOP;
  END IF;
  RETURN QUERY SELECT
    'purge_old_schedule_runs'::TEXT, v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_debug_mode THEN 'skipped (debug_mode=true)' ELSE 'ok' END::TEXT;

  -- wh_stream_purge_audit, by purged_at, only when an operator set a window.
  v_start := clock_timestamp();
  v_rows := 0;
  IF NOT v_debug_mode AND v_audit_days > 0 THEN
    DELETE FROM __SCHEMA__.wh_stream_purge_audit a
    WHERE a.purged_at < NOW() - (v_audit_days * INTERVAL '1 day');
    GET DIAGNOSTICS v_rows = ROW_COUNT;
  END IF;
  RETURN QUERY SELECT
    'purge_old_stream_purge_audit'::TEXT, v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE
      WHEN v_debug_mode THEN 'skipped (debug_mode=true)'
      WHEN v_audit_days = 0 THEN 'kept (stream_purge_audit_retention_days=0)'
      ELSE 'ok'
    END::TEXT;

  -- wh_integrity_ledger, to its row cap, by the trim 090 shipped.
  v_start := clock_timestamp();
  v_rows := 0;
  IF NOT v_debug_mode THEN
    v_rows := __SCHEMA__.wh_integrity_ledger_trim(v_ledger_max);
  END IF;
  RETURN QUERY SELECT
    'trim_integrity_ledger'::TEXT, v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_debug_mode THEN 'skipped (debug_mode=true)' ELSE 'ok' END::TEXT;

  -- 279: wh_collective_apply_progress, a finished record once it is past its horizon (by applied_at) and the work row
  -- of its collective is no longer pending, so a re-delivery of a pending collective still finds its finished apply.
  -- Never an abandoned record, nor a finished record of a collective that has one: those go with the dead letter.
  v_start := clock_timestamp();
  v_rows := 0;
  IF NOT v_debug_mode THEN
    LOOP
      DELETE FROM __SCHEMA__.wh_collective_apply_progress d
      WHERE (d.event_id, d.entry_key) IN (
        SELECT p.event_id, p.entry_key FROM __SCHEMA__.wh_collective_apply_progress p
        WHERE p.completed
          AND NOT p.abandoned
          AND p.applied_at < NOW() - (v_progress_days * INTERVAL '1 day')
          AND NOT EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_collective_apply_progress a
            WHERE a.event_id = p.event_id AND a.abandoned)
          AND NOT EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
            WHERE pe.event_id = p.event_id AND pe.processed_at IS NULL AND pe.perspective_name = '__collective__')
        LIMIT p_batch_size);
      GET DIAGNOSTICS v_batch = ROW_COUNT;
      v_rows := v_rows + v_batch;
      EXIT WHEN v_batch < p_batch_size;
    END LOOP;
  END IF;
  RETURN QUERY SELECT
    'purge_collective_apply_progress'::TEXT, v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_debug_mode THEN 'skipped (debug_mode=true)' ELSE 'ok' END::TEXT;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.reap_unbounded_ledgers(INTEGER) IS
  'Issue #1572: bounds wh_log, wh_schedule_runs (keeping each schedule''s newest run), wh_stream_purge_audit '
  '(opt-in) and wh_integrity_ledger, in batches, from their wh_settings windows. Called by perform_maintenance. '
  'Issue #1611: skips the wh_log row delete while wh_log is partitioned and partition maintenance is enabled. '
  'Issue #1694: a partitioned and maintained wh_log is swept in its default partition only (wh_partition_sweep_relation). '
  'Issue #1969: finished collective apply records past collective_apply_progress_retention_days (default 7), once the '
  'collective''s work row is no longer pending.';
