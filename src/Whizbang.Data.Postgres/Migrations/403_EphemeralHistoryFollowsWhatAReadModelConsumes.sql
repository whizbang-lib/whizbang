-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 403_EphemeralHistoryFollowsWhatAReadModelConsumes.sql
-- Date: 2026-11-12
-- Description: The ephemeral body reap follows what each read model needs (#2294, #2295).
--
--              A body is reaped only once every consuming perspective has a snapshot at or past it (Task 8's coverage
--              gate). A perspective that purged its row (ApplyResult.Purge) can never take that snapshot: the bootstrap
--              finds no row. Every body of the stream was then held for good, with the row's cursor and snapshots, and
--              the reap-driven pass selected the same pairs on every cycle.
--
--                1. A perspective's purge marker (wh_stream_purge_markers, 180) covers every body of its stream, in
--                   the reap and in the two statements that mirror it (the reap-driven pass's targets and the batch the
--                   destruction hook is offered, CoordinatorCatalogSql and EphemeralDestructionSql).
--                2. Task 8b removes a purged row's cursor and every snapshot (_wh_remove_purged_row_companions), once
--                   the stream has no work pending for the perspective and its table holds no row for the stream.
--
--                3. A perspective that keeps no rewind floor (it applies only ephemeral events and is not marked
--                   [KeepEphemeralRewindFloor], #2295) holds no body back: the startup associations step registers a
--                   __ASSOCIATION_NO_REWIND_FLOOR__ marker association for it, and Task 8 and both mirrors skip the
--                   coverage check for a perspective with one. The other gates (consumption, grace, TTL, holds) stay.
--
--              The rest of wh_perform_maintenance is 366's body unchanged. perform_maintenance() is not redefined; it
--              already calls wh_perform_maintenance(NULL).
--
--              Number 403: 401 is the highest on develop; even numbers are left to the integrity bundle.
--
-- Reversal: lossy (366's wh_perform_maintenance is restored and the sweep dropped; cursors and snapshots it removed
--           stay removed, and bodies it reaped stay reaped)
-- Dependencies: 366 (last word of wh_perform_maintenance), 180 (wh_stream_purge_markers), 211 (perspective_names),
--               165 (the registry-driven table loop this sweep follows)
-- Objects: _wh_remove_purged_row_companions, wh_perform_maintenance
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

SELECT __SCHEMA__.drop_all_overloads('_wh_remove_purged_row_companions');

-- ===========================================================================================
-- _wh_remove_purged_row_companions: a purged row's cursor and snapshots, at most p_batch_size pairs per call
-- ===========================================================================================
-- A pair qualifies when the perspective's own marker stands for the stream (an operator '*' marker never does), the
-- perspective has a cursor or a snapshot left for it, no work for the perspective on the stream is unprocessed (an
-- event still to apply may resurrect the row), and every table the registry says the perspective folds into holds no
-- row for the stream. The table check is what keeps a live row whole when a crash left its marker behind (the runner
-- clears a marker only after it writes the resurrected row). A perspective no registry row names is never touched: the
-- sweep cannot see its row, and guesses nothing. Returns the cursors and snapshots removed.
-- <docs>fundamentals/events/ephemeral-events#a-purged-row-covers-its-history</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PurgedRowEphemeralHistorySqlTests.cs:Maintenance_APurgedRowsCursorAndSnapshots_AreRemoved_AndOtherPerspectivesKeepTheirsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PurgedRowEphemeralHistorySqlTests.cs:Maintenance_ALiveRowWithAStaleMarker_KeepsItsCursorAndSnapshotsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PurgedRowEphemeralHistorySqlTests.cs:Maintenance_APurgedRowWithWorkStillPending_KeepsItsCursorAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PurgedRowEphemeralHistorySqlTests.cs:Maintenance_APerspectiveNoRegistryRowNames_IsLeftAloneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PurgedRowEphemeralHistorySqlTests.cs:Maintenance_AnOperatorPurgeMarker_RemovesNothingAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_remove_purged_row_companions(p_batch_size INTEGER)
RETURNS BIGINT AS $$
DECLARE
  v_streams   UUID[];
  v_names     TEXT[];
  v_table     TEXT;
  v_present_s UUID[] := '{}';
  v_present_n TEXT[] := '{}';
  v_found_s   UUID[];
  v_found_n   TEXT[];
  v_named     TEXT[] := '{}';
  v_folding   TEXT[];
  v_cursors   BIGINT := 0;
  v_snapshots BIGINT := 0;
BEGIN
  SELECT array_agg(p.stream_id), array_agg(p.perspective_name::TEXT)
    INTO v_streams, v_names
    FROM (
      SELECT m.stream_id, m.perspective_name
        FROM __SCHEMA__.wh_stream_purge_markers m
       WHERE m.perspective_name <> '*'
         AND (EXISTS (SELECT 1 FROM __SCHEMA__.wh_perspective_cursors c
                       WHERE c.stream_id = m.stream_id AND c.perspective_name = m.perspective_name)
              OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_perspective_snapshots s
                          WHERE s.stream_id = m.stream_id AND s.perspective_name = m.perspective_name))
         AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
                          WHERE pe.stream_id = m.stream_id AND pe.perspective_name = m.perspective_name
                            AND pe.processed_at IS NULL)
       LIMIT GREATEST(p_batch_size, 1)) p;

  IF v_streams IS NULL THEN
    RETURN 0;
  END IF;

  -- The tables these perspectives fold into, from the registry, each probed once for the candidate streams. The schema
  -- is this function's own (README rule 14): replace() strips the quotes the EF Core driver substitutes.
  FOR v_table IN
    SELECT DISTINCT r.table_name
      FROM __SCHEMA__.wh_perspective_registry r
     WHERE r.perspective_names && v_names
       AND EXISTS (SELECT 1 FROM information_schema.tables t
                    WHERE t.table_schema = replace('__SCHEMA__', '"', '')
                      AND t.table_name = r.table_name)
  LOOP
    SELECT array_agg(DISTINCT n.perspective_name)
      INTO v_folding
      FROM __SCHEMA__.wh_perspective_registry r
     CROSS JOIN LATERAL unnest(r.perspective_names) AS n(perspective_name)
     WHERE r.table_name = v_table;
    v_named := v_named || v_folding;

    EXECUTE format(
      'SELECT array_agg(p.s), array_agg(p.n)
         FROM unnest($1, $2) AS p(s, n)
        WHERE p.n = ANY($3)
          AND EXISTS (SELECT 1 FROM %I.%I t WHERE t.id = p.s)',
      replace('__SCHEMA__', '"', ''), v_table)
      INTO v_found_s, v_found_n
      USING v_streams, v_names, v_folding;
    v_present_s := v_present_s || COALESCE(v_found_s, '{}');
    v_present_n := v_present_n || COALESCE(v_found_n, '{}');
  END LOOP;

  -- Gone: named by a registry table that exists, and present in none of them.
  SELECT array_agg(g.s), array_agg(g.n)
    INTO v_streams, v_names
    FROM (
      SELECT c.s, c.n FROM unnest(v_streams, v_names) AS c(s, n) WHERE c.n = ANY(v_named)
      EXCEPT
      SELECT x.s, x.n FROM unnest(v_present_s, v_present_n) AS x(s, n)) g;

  IF v_streams IS NULL THEN
    RETURN 0;
  END IF;

  DELETE FROM __SCHEMA__.wh_perspective_cursors c
   USING unnest(v_streams, v_names) AS g(s, n)
   WHERE c.stream_id = g.s AND c.perspective_name = g.n;
  GET DIAGNOSTICS v_cursors = ROW_COUNT;

  DELETE FROM __SCHEMA__.wh_perspective_snapshots s
   USING unnest(v_streams, v_names) AS g(s, n)
   WHERE s.stream_id = g.s AND s.perspective_name = g.n;
  GET DIAGNOSTICS v_snapshots = ROW_COUNT;

  RETURN v_cursors + v_snapshots;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_remove_purged_row_companions(INTEGER) IS
  'Removes the cursor and every snapshot of a row its perspective purged (#2294), at most p_batch_size pairs per call: '
  'the perspective''s own purge marker stands, no work for it on the stream is pending, and no table the registry says '
  'it folds into holds the row. Called by wh_perform_maintenance (Task 8b).';

-- ===========================================================================================
-- wh_perform_maintenance: 366's body copied whole, with Task 8's coverage gate reading a purge as coverage (#2294)
-- and Task 8b removing a purged row's cursor and snapshots (#2294)
-- ===========================================================================================

-- <docs>operations/infrastructure/maintenance</docs>
-- <docs>operations/infrastructure/maintenance#completed-inbox-purge-is-bounded</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MaintenanceTests.cs:PerformMaintenance_PurgesAtMostTheBatchOfCompletedInboxRows_AndTheRestOnTheNextCycleAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MaintenanceTests.cs:PerformMaintenance_ABatchSetBelowOne_StillPurgesOneRowPerCycleAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MaintenanceTests.cs:PerformMaintenance_WithNoBatchSetting_PurgesUpToTheDefaultAsync</tests>
-- <docs>fundamentals/events/ephemeral-events#a-purged-row-covers-its-history</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PurgedRowEphemeralHistorySqlTests.cs:Reap_TheBodiesOfAPurgedRow_AreReapedWithoutASnapshotAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PurgedRowEphemeralHistorySqlTests.cs:Reap_AnOperatorPurgeOfEveryPerspective_DoesNotCoverTheBodyAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PurgedRowEphemeralHistorySqlTests.cs:Maintenance_ReportsHowManyCompanionsItRemovedAsync</tests>
-- <docs>fundamentals/events/ephemeral-events#snapshots-follow-what-a-read-model-consumes</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralOnlyReadModelReapSqlTests.cs:Reap_TheBodyOfAReadModelThatKeepsNoFloor_IsReapedWithoutASnapshotAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralOnlyReadModelReapSqlTests.cs:Reap_ABodyAnotherConsumerStillNeedsCoveredFor_IsHeldAsync</tests>
-- <docs>fundamentals/events/ephemeral-events#reap-deletes-what-the-hook-saw</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralReapSeenSetTests.cs:Delete_ABodyThatBecomesEligibleWhileTheHookRuns_IsNotReapedUnseen_AndIsOfferedNextCycleAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralReapSeenSetTests.cs:Compact_ABodyThatBecomesEligibleWhileTheHandlerRuns_IsNotReapedUncompacted_AndIsCompactedNextCycleAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralReapSeenSetTests.cs:Reap_GivenIds_DeletesOnlyThose_AnEmptyListNothing_AndNullEveryEligibleBodyAsync</tests>
-- <docs>fundamentals/events/superseding-events#the-reaper</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_SupersededAndConsumed_IsDeletedWithItsBodyAndItsDigestContribution_TheNewestIsKeptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/TtlReapDrivesFromRegistryTests.cs:TheRowExpiryReap_SelectsItsTablesFromTheRegistry_NotTheCatalogAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EvictionCompanionCleanupSqlTests.cs:StampedExpiryReap_RemovesTheEvictedRowsCursors_KeepsItsLatestSnapshot_LeavesTheRestAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Collective/CollectiveOrderingHeadSqlTests.cs:Maintenance_PrunesOnlyHeadsIdlePastTheRetentionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MaintenanceTests.cs:PerformMaintenance_PurgesStuckInboxMessages_OlderThanRetentionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MaintenanceTests.cs:PerformMaintenance_PreservesRecentStuckInboxMessages_WithinRetentionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MaintenanceTests.cs:PerformMaintenance_PreservesLeasedInboxMessages_EvenIfOldAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MaintenanceTests.cs:PerformMaintenance_PreservesClaimedInboxMessages_EvenIfOldAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/WorkTablesAreLockedInOneOrderTests.cs:NoFunctionLocksTheWorkTablesOutOfCanonicalOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBodyReaperSqlTests.cs:Reap_SnapshotEarlierInTheStream_DoesNotCoverTheBodyAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/NamedSchemaMigrationReplayTests.cs:MaintenanceReapsTheContextsOwnExpiredRowsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LedgerRetentionSqlTests.cs:Log_OlderThanTheWindow_IsRemoved_YoungerIsKeptAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_perform_maintenance(p_ephemeral_reap_event_ids UUID[])
RETURNS TABLE(
  task_name TEXT,
  rows_affected BIGINT,
  duration_ms DOUBLE PRECISION,
  status TEXT
) AS $$
DECLARE
  v_start TIMESTAMPTZ;
  v_rows BIGINT;
  v_dedup_retention_days INTEGER;
  v_stuck_inbox_retention_days INTEGER;
  v_debug_mode BOOLEAN;
  v_abandoned_stream_hours INTEGER;
  v_ephemeral_grace_seconds INTEGER;
  v_per_table TEXT;
  v_per_deleted BIGINT;
  v_per_victims UUID[];
  v_instance_eviction_retention_hours INTEGER;
  v_dead_letter_retention_days INTEGER;
  v_orphan_grace_hours INTEGER;
  v_ordering_head_retention_days INTEGER;
  v_completed_inbox_purge_batch INTEGER;
BEGIN
  -- Read debug_mode flag once for the cycle. When true, the complete_* functions
  -- retain rows for forensics with processed_at stamped, this maintenance pass
  -- MUST skip purging those rows or the debug-mode design breaks.
  SELECT COALESCE(
    (SELECT setting_value::BOOLEAN FROM __SCHEMA__.wh_settings WHERE setting_key = 'debug_mode'),
    FALSE
  ) INTO v_debug_mode;

  -- Grace period before an owner-less active-stream row is purged (Task 6). Configurable via
  -- wh_settings; default 1 hour preserves the transient-NULL race window between
  -- cleanup_stale_instances nulling the owner and the next claim cycle re-assigning it.
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'abandoned_stream_hours'),
    1
  ) INTO v_abandoned_stream_hours;

  -- Rewind grace window (seconds): an ephemeral body is retained this long AFTER consumption so an
  -- out-of-order straggler can still rewind through it (events arrive out of order in a short window).
  -- Configurable via wh_settings; default 300s. A per-type [Ephemeral(RewindGrace)] override lands later.
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'ephemeral_rewind_grace_seconds'),
    300
  ) INTO v_ephemeral_grace_seconds;

  -- Retention for wh_instance_evictions tombstones (Task 10, migration 106/107). The tombstone only
  -- needs to outlive a paused instance's resumption window, not the fleet's lifetime. Default 24
  -- hours is generous against any realistic pause while still bounding the table.
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'instance_eviction_retention_hours'),
    24
  ) INTO v_instance_eviction_retention_hours;

  -- ========================================
  -- Task 1: Purge completed outbox messages
  -- ========================================
  v_start := clock_timestamp();
  IF v_debug_mode THEN
    v_rows := 0;
  ELSE
    DELETE FROM __SCHEMA__.wh_outbox WHERE processed_at IS NOT NULL;
    GET DIAGNOSTICS v_rows = ROW_COUNT;
  END IF;
  RETURN QUERY SELECT
    'purge_completed_outbox'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_debug_mode THEN 'skipped (debug_mode=true)' ELSE 'ok' END::TEXT;

  -- ========================================
  -- Task 2: Purge completed inbox messages
  -- ========================================
  v_start := clock_timestamp();
  IF v_debug_mode THEN
    v_rows := 0;
  ELSE
    -- 366: at most a batch per call, so the call fits its command budget however large the backlog is;
    -- the rest goes on later cycles. Never below 1: a batch of zero would stop the purge for good.
    SELECT GREATEST(COALESCE(
      (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'completed_inbox_purge_batch_size'),
      50000
    ), 1) INTO v_completed_inbox_purge_batch;

    -- 162: processed_at is work state. The DELETE stays on wh_inbox because that is the row being
    -- removed, and the state row goes with it through ON DELETE CASCADE; the predicate reads the
    -- state table. USING keeps this one statement so the GET DIAGNOSTICS below still measures it.
    -- 366: the batch is picked without an order; which completed row goes first does not matter.
    DELETE FROM __SCHEMA__.wh_inbox i
    USING (
      SELECT ist.message_id
      FROM __SCHEMA__.wh_inbox_state ist
      WHERE ist.processed_at IS NOT NULL
      LIMIT v_completed_inbox_purge_batch
    ) done
    WHERE i.message_id = done.message_id;
    GET DIAGNOSTICS v_rows = ROW_COUNT;
  END IF;
  RETURN QUERY SELECT
    'purge_completed_inbox'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_debug_mode THEN 'skipped (debug_mode=true)' ELSE 'ok' END::TEXT;

  -- ========================================
  -- Task 5: Purge ancient stuck inbox messages
  -- ========================================
  -- POSITION: this sweep sits beside Task 2 because both DELETE from wh_inbox, and a transaction
  -- that locks a table, moves on, and comes back to it can deadlock against a sibling that took
  -- the two tables the other way round. Every pod runs this function on the same tick, so the
  -- sibling here is another copy of this very function: one holds a wh_perspective_events row and
  -- wants wh_active_streams, the other holds wh_active_streams and wants wh_perspective_events.
  -- Each table is therefore visited exactly once, in the canonical order wh_outbox, wh_inbox,
  -- wh_inbox_state, wh_perspective_events, wh_active_streams. The report is looked up by task
  -- name everywhere it is read, never by position, so moving a block is free.
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'stuck_inbox_retention_days'),
    7
  ) INTO v_stuck_inbox_retention_days;

  v_start := clock_timestamp();
  -- 162: same shape as the purge above. Every term of this predicate is on the state table,
  -- received_at as a write-once copy, so the sweep never reads the wide message row to decide.
  DELETE FROM __SCHEMA__.wh_inbox i
  USING __SCHEMA__.wh_inbox_state ist
  WHERE ist.message_id = i.message_id
    AND ist.processed_at IS NULL
    AND ist.lease_expiry IS NULL
    AND ist.instance_id IS NULL
    AND ist.received_at < NOW() - (v_stuck_inbox_retention_days || ' days')::INTERVAL;
  GET DIAGNOSTICS v_rows = ROW_COUNT;
  RETURN QUERY SELECT
    'purge_stuck_inbox'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    'ok'::TEXT;

  -- ========================================
  -- Task 3: Purge completed perspective events
  -- ========================================
  v_start := clock_timestamp();
  IF v_debug_mode THEN
    v_rows := 0;
  ELSE
    DELETE FROM __SCHEMA__.wh_perspective_events WHERE processed_at IS NOT NULL;
    GET DIAGNOSTICS v_rows = ROW_COUNT;
  END IF;
  RETURN QUERY SELECT
    'purge_completed_perspective_events'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_debug_mode THEN 'skipped (debug_mode=true)' ELSE 'ok' END::TEXT;

  -- ========================================
  -- Task 12: Reap orphaned perspective-event rows (issue #687)
  -- ========================================
  -- POSITION: beside Task 3 for the same reason Task 5 sits beside Task 2 -- both DELETE from
  -- wh_perspective_events, and this one used to run after the wh_active_streams purge, which put
  -- the two tables in both orders inside one transaction. Nothing here depends on the blocks it
  -- moved past: the grace hours are read from wh_settings by this block itself, the predicate
  -- reads wh_event_store which this function never writes, and Task 13 still runs after it and
  -- still sees v_orphan_grace_hours set.
  -- A wh_perspective_events row whose source event no longer exists in wh_event_store is
  -- UNPROJECTABLE forever: the drainer's inner join (get_stream_events) returns nothing, so the
  -- row is re-claimed every cycle with attempts climbing and no error, livelocking the pipeline
  -- (root cause of #679). These arise when an event is reaped/purged after its perspective work
  -- was created. Deleting is correct: the event is gone, so there is nothing to project and the
  -- projection cursor never advanced past the row. Age-bounded on created_at so a row whose event
  -- write has simply not committed yet (a legitimate in-flight window) is never reaped out from
  -- under itself. Not gated on debug_mode: this is unprojectable garbage, not forensic evidence,
  -- and leaving it keeps the pipeline wedged.
  v_start := clock_timestamp();
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'orphan_perspective_grace_hours'),
    1
  ) INTO v_orphan_grace_hours;
  DELETE FROM __SCHEMA__.wh_perspective_events pe
  WHERE pe.processed_at IS NULL
    AND pe.created_at < NOW() - (v_orphan_grace_hours * INTERVAL '1 hour')
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = pe.event_id
    );
  GET DIAGNOSTICS v_rows = ROW_COUNT;
  RETURN QUERY SELECT
    'reap_orphaned_perspective_events'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    'ok'::TEXT;

  -- ========================================
  -- Task 4: Purge old deduplication entries
  -- ========================================
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'dedup_retention_days'),
    30
  ) INTO v_dedup_retention_days;

  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'dead_letter_retention_days'),
    7
  ) INTO v_dead_letter_retention_days;

  v_start := clock_timestamp();
  DELETE FROM __SCHEMA__.wh_message_deduplication
  WHERE first_seen_at < NOW() - (v_dedup_retention_days || ' days')::INTERVAL;
  GET DIAGNOSTICS v_rows = ROW_COUNT;
  RETURN QUERY SELECT
    'purge_old_deduplication'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    'ok'::TEXT;

  -- ========================================
  -- Task 6: Purge abandoned active-stream rows
  -- ========================================
  -- Two branches, both safe because UUIDv7 IDs never repeat (a missing
  -- wh_service_instances row means the instance is fully gone):
  --
  --   (a) Rows whose assigned_instance_id is non-NULL but points at a
  --       wh_service_instances row that no longer exists. After the
  --       heartbeat-recency liveness check in claim_orphaned_inbox /
  --       claim_orphaned_outbox (migrations 024/025) these are already
  --       non-blocking; the cleanup just bounds accumulation. No age guard.
  --
  --   (b) Rows whose assigned_instance_id IS NULL AND whose last_activity_at
  --       is older than the grace period. cleanup_stale_instances nulls the
  --       assigned_instance_id in the same tick where it deletes the dead
  --       wh_service_instances row, so without this branch every dead
  --       instance leaves its streams in the table forever (production forensic:
  --       tens of thousands of rows accumulated, 99% with NULL owner). The age
  --       guard preserves the legitimate transient-NULL race window between
  --       cleanup_stale_instances nulling the field and the next
  --       claim_orphaned_* cycle re-assigning via INSERT ON CONFLICT.
  v_start := clock_timestamp();
  DELETE FROM __SCHEMA__.wh_active_streams
  WHERE (
      assigned_instance_id IS NOT NULL
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_service_instances si
        WHERE si.instance_id = __SCHEMA__.wh_active_streams.assigned_instance_id
      )
    )
    OR (
      assigned_instance_id IS NULL
      AND last_activity_at < NOW() - (v_abandoned_stream_hours * INTERVAL '1 hour')
    );
  GET DIAGNOSTICS v_rows = ROW_COUNT;
  RETURN QUERY SELECT
    'purge_abandoned_active_streams'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    'ok'::TEXT;

  -- ========================================
  -- Task 7: Refresh wh_dead_letter_summary
  -- ========================================
  -- Slice 6 of release/v0.645.0-alpha.1 (outbox-DLQ + dual-hash analysis).
  -- Two-step pipeline inside aggregate_dead_letters:
  --   (1) Version-aware backfill, re-hashes raw wh_dead_letters rows with
  --       stale error_fingerprint_version; current-version rows are skipped.
  --   (2) GROUP BY upsert into wh_dead_letter_summary.
  -- The summary table is the operator/AI-facing rollup view: ~dozens of
  -- distinct fingerprint clusters instead of tens of thousands of raw rows.
  -- Cluster-count metric is the rows_affected for this task (post-aggregation).
  v_start := clock_timestamp();
  PERFORM __SCHEMA__.aggregate_dead_letters();
  SELECT COUNT(*) FROM __SCHEMA__.wh_dead_letter_summary INTO v_rows;
  RETURN QUERY SELECT
    'aggregate_dead_letters'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    'ok'::TEXT;

  -- ========================================
  -- Task 8: Reap consumed ephemeral event bodies (E1 #13b2, + E2-4c TTL floor)
  -- ========================================
  -- wh_event_body holds ONLY ephemeral bodies (offloaded by the emit chain, migration 072). A body is
  -- reapable once every perspective that consumes its event has processed it, i.e. no unprocessed
  -- wh_perspective_events work item still references the event_id. The emit chain writes the body and
  -- its perspective work items in one transaction, so a body with consumers always has a matching
  -- gating work item (no premature-reap window); an ephemeral event with no consuming perspective has
  -- no work item and is reapable at once. The wh_event_store pointer is left in place, a
  -- pointer-present / body-NULL row is the deterministic rebuild-guard signal (#13d), not a lost event.
  -- Skipped under debug_mode so retained forensic bodies survive with the retained work items.
  -- Grace window: a consumed body is also kept until it is OLDER than v_ephemeral_grace_seconds, so an
  -- out-of-order straggler can still rewind through it (rewind uses the surviving bodies + a snapshot floor).
  -- TTL floor (E2-4c): an AfterTtl event carries its own absolute expiry in body metadata
  -- ('ephemeral_expires_at', stamped at dispatch). It EXTENDS retention, the consumed body is kept until it
  -- is ALSO past that expiry. An event with no key (Sourced / WhenConsumed) is unaffected: the gate is
  -- vacuously true, and it reaps as soon as consumed+aged.
  v_start := clock_timestamp();
  IF v_debug_mode THEN
    v_rows := 0;
  ELSE
    DELETE FROM __SCHEMA__.wh_event_body eb
    USING __SCHEMA__.wh_event_store es
    LEFT JOIN __SCHEMA__.wh_ephemeral_type_grace g ON g.event_type = es.event_type
    WHERE es.event_id = eb.event_id
      -- #13b4 safety gate: the reap is scoped to EPHEMERAL events explicitly. Pre-split this was
      -- guaranteed "by construction" (wh_event_body held only ephemeral bodies); once SOURCED bodies
      -- move into the body table (full split), this gate is what keeps the durable log un-reapable.
      AND (es.flags & 8) = 8
      -- 310 (#1999): the bodies the destruction hook and the compaction handler were given, and no others. The worker
      -- reads that batch before this runs, in another statement, so a body that became eligible in between (its grace
      -- or TTL passed, its perspective work finished, a snapshot came to cover it) would otherwise be deleted with no
      -- hook or handler ever seeing it. NULL: no hook or handler ran, so no batch was read, and every eligible body goes.
      AND (p_ephemeral_reap_event_ids IS NULL OR eb.event_id = ANY(p_ephemeral_reap_event_ids))
      -- E2-3 destruction hold: a PreDestruction hook may Cancel (hold far-future) or Defer(until) a body;
      -- while a hold is active the reap skips it, so the hook's decision is honored.
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_destruction_hold h
        WHERE h.event_id = eb.event_id AND h.hold_until > NOW()
      )
      AND es.created_at < NOW() - (COALESCE(g.grace_seconds, v_ephemeral_grace_seconds) * INTERVAL '1 second')
      -- E2-4c TTL retention floor: an AfterTtl body carries an absolute 'ephemeral_expires_at' in its
      -- metadata; it is kept until past that instant. No key (Sourced / WhenConsumed) => vacuously true.
      AND (
        eb.metadata ->> 'ephemeral_expires_at' IS NULL
        OR (eb.metadata ->> 'ephemeral_expires_at')::timestamptz < NOW()
      )
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
        WHERE pe.event_id = eb.event_id
          AND pe.processed_at IS NULL
      )
      -- Snapshot-coverage gate: the reap must never outrun the rewind floor. Reap only once EVERY consuming
      -- perspective has a snapshot at or past this event IN THE STREAM, i.e. there is no association whose
      -- perspective lacks a covering snapshot for the stream. The reap-driven step (MaintenanceWorker) drives
      -- those snapshots just before this runs, so coverage is normally satisfied; an event with no consuming
      -- perspective is vacuously covered.
      --
      -- 209: compared by version, not by commit sequence. Commit sequence is assignment order, and a row
      -- stamped after the fact carries a sequence far below rows written long before it, so a snapshot that
      -- sits EARLIER in the stream than the event could still compare "at or past" it and let the body go.
      -- The body is the only thing a rewind past that point can replay, so losing it leaves the perspective
      -- permanently short with nothing pending to repair it. Version is the stream's own counter, unique per
      -- stream by index, and cannot invert.
      --
      -- Both NULL cases hold the body rather than reap it, which is the recoverable direction: an event not
      -- yet placed in a stream, and a snapshot written before 209 that records no version, each compare NULL
      -- and so never count as covering.
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_message_associations ma
        WHERE ma.normalized_message_type = es.event_type
          AND ma.association_type = __CATEGORY_PERSPECTIVE__
          -- 403 (#2295): a perspective that keeps no rewind floor (it applies only ephemeral events and is not marked
          -- [KeepEphemeralRewindFloor]) never takes the snapshot this gate waits for, and needs none: its snapshot would
          -- only be the floor of a rewind the grace window has already closed. The startup associations step registers
          -- a marker for it; without the marker (another driver, an older instance) the gate stays, which holds bodies
          -- rather than losing them.
          AND NOT EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_message_associations nf
            WHERE nf.association_type = __ASSOCIATION_NO_REWIND_FLOOR__
              AND nf.target_name = ma.target_name
          )
          AND NOT EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_perspective_snapshots s
            WHERE s.stream_id = es.stream_id
              AND s.perspective_name = ma.target_name
              AND s.snapshot_version >= es.version
          )
          -- 403 (#2294): a row the perspective purged covers every body of its stream. While its marker stands the row
          -- is gone: the events before the purge built state the purge removed, and the events after it were skipped,
          -- so no snapshot can be taken and none is needed. Only the perspective's own marker counts: an operator purge
          -- ('*') can stand beside a row a later Apply resurrected, because a resurrect clears only its own marker.
          AND NOT EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_stream_purge_markers pm
            WHERE pm.stream_id = es.stream_id
              AND pm.perspective_name = ma.target_name
          )
      );
    GET DIAGNOSTICS v_rows = ROW_COUNT;

    -- Keep the hold table bounded: drop holds whose body is already gone (a Defer whose window lapsed and
    -- was then reaped, or any body reaped by another path). A permanent Cancel keeps body + hold together.
    DELETE FROM __SCHEMA__.wh_event_destruction_hold h
    WHERE NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_body eb WHERE eb.event_id = h.event_id);
  END IF;
  RETURN QUERY SELECT
    'reap_consumed_ephemeral_bodies'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_debug_mode THEN 'skipped (debug_mode=true)' ELSE 'ok' END::TEXT;

  -- ========================================
  -- Task 8b: Remove the cursors and snapshots of rows their perspective purged (403, #2294)
  -- ========================================
  -- A purge removes state; the cursor and snapshots describe that state, so they go too. Unlike an eviction (211),
  -- which keeps the latest snapshot because the row is rebuilt from it on the next event, a purged row stays purged:
  -- there is nothing to rebuild, and a later event that resurrects it starts a new row. Skipped under debug_mode.
  v_start := clock_timestamp();
  IF v_debug_mode THEN
    v_rows := 0;
  ELSE
    v_rows := __SCHEMA__._wh_remove_purged_row_companions(5000);
  END IF;
  RETURN QUERY SELECT
    'remove_purged_row_companions'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_debug_mode THEN 'skipped (debug_mode=true)' ELSE 'ok' END::TEXT;

  -- ========================================
  -- Task 9: Reap expired TtlRow perspective rows (E2-4d)
  -- ========================================
  -- TransientStorage.TtlRow perspective rows carry an expires_at (stamped on upsert = now + ttl). Once past,
  -- a row is logically expired (already hidden from lens reads) and is physically deleted here.
  --
  -- 165: driven by the REGISTRY, not the catalog. This enumerated every wh_per_* table carrying an expires_at
  -- column, reasoning that a perspective declaring no lifetime never stamps it so its rows cannot match. True,
  -- and irrelevant to cost: expires_at comes from the shared table template, so EVERY perspective table has
  -- the column, and each took an unindexed DELETE that matched nothing. Measured on a deployed service: 120
  -- tables scanned for the 3 that declare a lifetime, 14,758 ms to delete zero rows -- 98 percent of the whole
  -- maintenance cycle, which itself was the heaviest statement on that database. Another service scanned 81
  -- tables for one declaring perspective. The registry already records the declaration, and
  -- reap_enrolled_perspective_rows (112), reap_perspective_row_caps (113) and
  -- collect_perspective_row_reap_targets (113) all read it; this task was the one place left on the old shape.
  --
  -- row_ttl_seconds rather than row_retention_enrolled: expires_at is stamped by the perspective's own write
  -- path from its TtlRow declaration, so it is live whether or not an operator has acknowledged the retention
  -- program. Gating on the acknowledgement would stop reaping rows that are still being stamped today.
  -- Skipped under debug_mode, like the body reaper.
  v_start := clock_timestamp();
  v_rows := 0;
  IF NOT v_debug_mode THEN
    -- The schema is this function's own, from the substitution baked into its body, never current_schema(): that
    -- is the CALLER's search_path, which on the EF Core driver's (and a pooled) connection is public whatever
    -- schema the service owns. replace() strips the quotes the EF Core driver substitutes (Dapper substitutes
    -- the bare name), so the value compares equal to information_schema.table_schema on both drivers.
    FOR v_per_table IN
      SELECT r.table_name
      FROM __SCHEMA__.wh_perspective_registry r
      WHERE r.row_ttl_seconds IS NOT NULL
        AND EXISTS (
          SELECT 1 FROM information_schema.tables t
          WHERE t.table_schema = replace('__SCHEMA__', '"', '')
            AND t.table_name = r.table_name)
    LOOP
      EXECUTE format(
        'WITH del AS (
           DELETE FROM %I.%I WHERE expires_at IS NOT NULL AND expires_at < NOW()
           RETURNING id)
         SELECT ARRAY(SELECT id FROM del)',
        replace('__SCHEMA__', '"', ''), v_per_table)
        INTO v_per_victims;
      v_per_deleted := cardinality(v_per_victims);
      v_rows := v_rows + v_per_deleted;
      -- Issue #1483: an eviction reclaims storage only. The expired rows' cursors and all but their
      -- latest snapshots go with them (the helper above), so the next event on the stream rebuilds the row.
      PERFORM __SCHEMA__._wh_evict_perspective_row_companions(v_per_table, v_per_victims);
    END LOOP;
  END IF;
  RETURN QUERY SELECT
    'reap_expired_perspective_rows'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_debug_mode THEN 'skipped (debug_mode=true)' ELSE 'ok' END::TEXT;

  -- ========================================
  -- Task 10: Purge expired instance-eviction tombstones (migration 106)
  -- ========================================
  -- The tombstone in wh_instance_evictions only needs to survive long enough for a genuinely
  -- paused instance to resume and be correctly refused. Once it is older than the retention
  -- window, either the instance is long dead for real, or, since instance ids are generated
  -- per PROCESS, not per deployment slot, anything still calling with that id is not the same
  -- process that was reaped. Keeping the row past that point only grows the table. Not gated on
  -- debug_mode: this is instance-identity bookkeeping, not forensic message data (same treatment
  -- as Task 6's abandoned-active-stream purge).
  v_start := clock_timestamp();
  DELETE FROM __SCHEMA__.wh_instance_evictions
  WHERE evicted_at < NOW() - (v_instance_eviction_retention_hours * INTERVAL '1 hour');
  GET DIAGNOSTICS v_rows = ROW_COUNT;
  RETURN QUERY SELECT
    'purge_instance_evictions'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    'ok'::TEXT;

  -- ========================================
  -- Task 11: Purge settled dead letters
  -- ========================================
  -- Recovered(3) means the message was successfully re-driven, so the row is a receipt rather than
  -- work. Every other status is either unresolved or a deliberate human hold, and none of those may
  -- be discarded on age alone. Skipped under debug_mode, where the operator asked to keep evidence.
  v_start := clock_timestamp();
  IF v_debug_mode THEN
    v_rows := 0;
  ELSE
    -- Retention keys on when the row SETTLED (#682): a backlog older than the window would
    -- otherwise have its receipts deleted within one maintenance cycle of recovering ,
    -- recovered counts went BACKWARDS while a drain made real progress. recovered_at is
    -- NULL only on legacy rows settled before it was stamped; those fall back to the
    -- original failure time rather than living forever.
    -- A row referenced by an UNRESOLVED campaign's probe_ids is evidence, not clutter:
    -- deleting it resolves the campaign on an empty evidence set (see 127's evaluate).
    DELETE FROM __SCHEMA__.wh_dead_letters d
    WHERE d.recovery_status = 3
      AND COALESCE(d.recovered_at, d.dead_lettered_at) < NOW() - (v_dead_letter_retention_days || ' days')::INTERVAL
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_dlq_probe_campaigns c
        WHERE c.verdict = 0 AND d.dead_letter_id = ANY(c.probe_ids)
      );
    GET DIAGNOSTICS v_rows = ROW_COUNT;
  END IF;
  RETURN QUERY SELECT
    'purge_recovered_dead_letters'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_debug_mode THEN 'skipped (debug_mode=true)' ELSE 'ok' END::TEXT;

  -- ========================================
  -- Task 13: Settle orphaned perspective-event DEAD LETTERS (issue #687)
  -- ========================================
  -- Task 12 reaps orphans still in wh_perspective_events. A row that was dead-lettered before
  -- its event vanished sits in wh_dead_letters instead, held forever: recovery would re-drive
  -- it into an empty join, and generation replay excludes held rows by design. When the row's
  -- ENTIRE source stream is absent from wh_event_store, every event of it is gone, so the
  -- perspective work is unrecoverable, settle it (Recovered + note, same disposition as the
  -- disabled-subsystem discard) so the ledger records the disposal and retention ages it out.
  -- The whole-stream predicate needs no per-event lookup and has no false positives: a stream
  -- with any surviving event is left for review (a genuine apply failure, not an orphan). Age-
  -- gated on dead_lettered_at by the same grace window so a stream still being written is safe.
  -- Operator holds (operator_disposition 2/3) are respected.
  v_start := clock_timestamp();
  UPDATE __SCHEMA__.wh_dead_letters dl
  SET recovery_status = 3,  -- Recovered: settled, eligible for the retention purge
      recovered_at    = NOW(),
      operator_notes  = COALESCE(operator_notes || E'\n', '')
        || 'auto-settled by maintenance: orphaned perspective event, source stream absent from event store'
  WHERE dl.source_table = 'wh_perspective_events'
    AND dl.recovered_at IS NULL
    AND dl.recovery_status NOT IN (3, 4)
    AND dl.operator_disposition NOT IN (2, 3)
    AND dl.stream_id IS NOT NULL
    AND dl.dead_lettered_at < NOW() - (v_orphan_grace_hours * INTERVAL '1 hour')
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = dl.stream_id
    );
  GET DIAGNOSTICS v_rows = ROW_COUNT;
  RETURN QUERY SELECT
    'settle_orphaned_perspective_dead_letters'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    'ok'::TEXT;

  -- ========================================
  -- Task 14: Prune idle collective ordering heads (190)
  -- ========================================
  -- A head only matters while the next collective on its key could still be published and the one it
  -- names could still be in flight to a receiver. A key idle for the retention window (wh_settings
  -- 'collective_ordering_head_retention_days', default 7) is forgotten: its next collective goes out
  -- unlinked and applies at once, which is right, because its predecessor applied days ago. Pruning by
  -- idleness bounds the table by the keys in use within the window, not by every key ever used.
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'collective_ordering_head_retention_days'),
    7
  ) INTO v_ordering_head_retention_days;
  v_start := clock_timestamp();
  DELETE FROM __SCHEMA__.wh_collective_ordering_heads h
  WHERE h.updated_at < NOW() - (v_ordering_head_retention_days * INTERVAL '1 day');
  GET DIAGNOSTICS v_rows = ROW_COUNT;
  RETURN QUERY SELECT
    'prune_collective_ordering_heads'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    'ok'::TEXT;

  -- ========================================
  -- Task 15: Bound the ledgers that have no other delete path (221)
  -- ========================================
  RETURN QUERY SELECT * FROM __SCHEMA__.reap_unbounded_ledgers();

  -- ========================================
  -- Task 16: Reap superseded events (295, #1729)
  -- ========================================
  -- Last, after the purges above: it writes none of the work tables, only the event store, its bodies, the stream
  -- digests, lapsed destruction holds and its own queue, so it adds no table to the canonical lock order.
  RETURN QUERY SELECT * FROM __SCHEMA__.wh_reap_superseded_events();

END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_perform_maintenance(UUID[]) IS
  'The maintenance tasks (perform_maintenance''s body from 310, #1999). p_ephemeral_reap_event_ids restricts Task 8''s '
  'ephemeral body reap to the bodies the destruction hook and the compaction handler were given; every other gate still '
  'applies to them. NULL reaps every eligible body, for a cycle that ran no hook or handler. 366: the completed-inbox '
  'purge deletes at most wh_settings.completed_inbox_purge_batch_size rows per call (default 50000, never below 1). 403: '
  'a perspective''s purge marker covers its stream''s bodies, Task 8b removes a purged row''s cursor and snapshots, and a '
  'perspective with a no_rewind_floor marker association holds no body back.';
