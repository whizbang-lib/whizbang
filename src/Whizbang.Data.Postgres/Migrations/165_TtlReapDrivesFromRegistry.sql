-- Migration: 165_TtlReapDrivesFromRegistry.sql
-- Date: 2026-09-20
-- Description: The row-expiry reap inside perform_maintenance chooses its tables from the
--              perspective registry instead of the catalog, which was 98 percent of the
--              maintenance cycle's cost and deleted nothing.
--
--              expires_at comes from the shared perspective table template, so every wh_per_* table
--              has the column whether or not its perspective declares a row lifetime. Enumerating
--              information_schema.columns for the column therefore selected all of them, and each
--              took an unindexed DELETE. The predicate matched nothing -- a perspective that
--              declares no lifetime never stamps the column -- but not matching still costs a full
--              scan of every table.
--
--              Measured on a deployed service: 120 tables scanned for the 3 that declare a
--              lifetime, 14,758 ms to delete zero rows, against 377 ms for the other twelve
--              maintenance tasks combined. A second service scanned 81 tables for one declaring
--              perspective, a third 32 for nine. perform_maintenance was the heaviest statement on
--              that database by total time, averaging 19.2 seconds and 615k buffers per call.
--
--              The registry records the declaration in row_ttl_seconds, and the three sibling
--              reapers -- reap_enrolled_perspective_rows (112), reap_perspective_row_caps (113) and
--              collect_perspective_row_reap_targets (113) -- already drive from it. This task was
--              the one place still on the pre-retention-program shape, so this is a consistency fix
--              that happens to remove the cost, not a new mechanism.
--
--              Filtered on row_ttl_seconds rather than row_retention_enrolled: expires_at is
--              stamped by the perspective's own write path from its TtlRow declaration, so it is
--              live whether or not an operator has acknowledged the retention program. Gating on
--              the acknowledgement would stop reaping rows that are still being stamped.
--
--              The table-exists guard remains, matching the siblings: the registry can name a table
--              a replayed ledger has not created yet, and a DELETE against a missing relation is
--              42P01 and would wedge the whole cycle.
--
-- Dependencies: 162 (last definition of perform_maintenance), 112/113 (the sibling reapers whose
--               registry-driven shape this adopts)
-- Objects: perform_maintenance

-- <docs>operations/infrastructure/maintenance</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/TtlReapDrivesFromRegistryTests.cs:TheRowExpiryReap_SelectsItsTablesFromTheRegistry_NotTheCatalogAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MaintenanceTests.cs:PerformMaintenance_PurgesStuckInboxMessages_OlderThanRetentionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MaintenanceTests.cs:PerformMaintenance_PreservesRecentStuckInboxMessages_WithinRetentionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MaintenanceTests.cs:PerformMaintenance_PreservesLeasedInboxMessages_EvenIfOldAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MaintenanceTests.cs:PerformMaintenance_PreservesClaimedInboxMessages_EvenIfOldAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/WorkTablesAreLockedInOneOrderTests.cs:NoFunctionLocksTheWorkTablesOutOfCanonicalOrderAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.perform_maintenance()
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
  v_instance_eviction_retention_hours INTEGER;
  v_dead_letter_retention_days INTEGER;
  v_orphan_grace_hours INTEGER;
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
    -- 162: processed_at is work state. The DELETE stays on wh_inbox because that is the row being
    -- removed, and the state row goes with it through ON DELETE CASCADE; the predicate reads the
    -- state table. USING keeps this one statement so the GET DIAGNOSTICS below still measures it.
    DELETE FROM __SCHEMA__.wh_inbox i
    USING __SCHEMA__.wh_inbox_state ist
    WHERE ist.message_id = i.message_id AND ist.processed_at IS NOT NULL;
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
      -- E2-3 destruction hold: a PreDestruction hook may Cancel (hold far-future) or Defer(until) a body;
      -- while a hold is active the reap skips it, so the hook's decision is honoured.
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
      -- perspective has a snapshot at/past this event's commit_sequence, i.e. there is no association whose
      -- perspective lacks a covering snapshot for the stream. The reap-driven step (MaintenanceWorker) drives
      -- those snapshots just before this runs, so coverage is normally satisfied; an event with no consuming
      -- perspective is vacuously covered, and an unstamped event (commit_sequence NULL) is held until stamped.
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_message_associations ma
        WHERE ma.normalized_message_type = es.event_type
          AND ma.association_type = __CATEGORY_PERSPECTIVE__
          AND NOT EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_perspective_snapshots s
            WHERE s.stream_id = es.stream_id
              AND s.perspective_name = ma.target_name
              AND s.snapshot_commit_sequence >= es.commit_sequence
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
    -- current_schema() (NOT the __SCHEMA__ placeholder): the EFCore schema-init replaces __SCHEMA__ with a
    -- QUOTED identifier ("public"), which is correct for `schema.table` refs but wrong inside a string literal
    -- compared to information_schema.table_schema (unquoted). current_schema() is the effective schema the
    -- maintenance connection runs in (same pattern as migration 046).
    FOR v_per_table IN
      SELECT r.table_name
      FROM __SCHEMA__.wh_perspective_registry r
      WHERE r.row_ttl_seconds IS NOT NULL
        AND EXISTS (
          SELECT 1 FROM information_schema.tables t
          WHERE t.table_schema = current_schema()
            AND t.table_name = r.table_name)
    LOOP
      EXECUTE format(
        'DELETE FROM %I.%I WHERE expires_at IS NOT NULL AND expires_at < NOW()',
        current_schema(), v_per_table);
      GET DIAGNOSTICS v_per_deleted = ROW_COUNT;
      v_rows := v_rows + v_per_deleted;
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

END;
$$ LANGUAGE plpgsql;
