-- Migration: 079_EventDestructionHold.sql
-- Date: 2026-07-16
-- Description: E2-3 — honor a PreDestruction hook's Cancel / Defer decision. A hook that returns Cancel (keep
--              the data) or Defer(until) (reschedule) must STOP the reaper from deleting those bodies. Since
--              the reap is SQL (Task 8 of perform_maintenance) and the hook is C#, the worker records the
--              decision in a per-event hold table BEFORE the reap runs, and Task 8 skips any body with an
--              active hold. Cancel = a far-future hold (leak-risk, the developer's call); Defer(until) = a
--              hold to that instant, after which the body is offered to the hook again and re-decided.
--              Also cleans up hold rows whose body is gone, so the table stays bounded.
-- Dependencies: 073 (perform_maintenance Task 8 reaper + wh_event_body)

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_event_destruction_hold (
  event_id   UUID PRIMARY KEY,
  hold_until TIMESTAMPTZ NOT NULL
);

-- Re-create perform_maintenance with the destruction-hold gate added to Task 8 (reproduced verbatim from
-- migration 073 except that one gate + the hold cleanup). Signature unchanged => CREATE OR REPLACE in place.

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
BEGIN
  -- Read debug_mode flag once for the cycle. When true, the complete_* functions
  -- retain rows for forensics with processed_at stamped — this maintenance pass
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
    DELETE FROM __SCHEMA__.wh_inbox WHERE processed_at IS NOT NULL;
    GET DIAGNOSTICS v_rows = ROW_COUNT;
  END IF;
  RETURN QUERY SELECT
    'purge_completed_inbox'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_debug_mode THEN 'skipped (debug_mode=true)' ELSE 'ok' END::TEXT;

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
  -- Task 4: Purge old deduplication entries
  -- ========================================
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'dedup_retention_days'),
    30
  ) INTO v_dedup_retention_days;

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
  -- Task 5: Purge ancient stuck inbox messages
  -- ========================================
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'stuck_inbox_retention_days'),
    7
  ) INTO v_stuck_inbox_retention_days;

  v_start := clock_timestamp();
  DELETE FROM __SCHEMA__.wh_inbox
  WHERE processed_at IS NULL
    AND lease_expiry IS NULL
    AND instance_id IS NULL
    AND received_at < NOW() - (v_stuck_inbox_retention_days || ' days')::INTERVAL;
  GET DIAGNOSTICS v_rows = ROW_COUNT;
  RETURN QUERY SELECT
    'purge_stuck_inbox'::TEXT,
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
  --   (1) Version-aware backfill — re-hashes raw wh_dead_letters rows with
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
  -- Task 8: Reap consumed ephemeral event bodies (E1 #13b2)
  -- ========================================
  -- wh_event_body holds ONLY ephemeral bodies (offloaded by the emit chain, migration 072). A body is
  -- reapable once every perspective that consumes its event has processed it — i.e. no unprocessed
  -- wh_perspective_events work item still references the event_id. The emit chain writes the body and
  -- its perspective work items in one transaction, so a body with consumers always has a matching
  -- gating work item (no premature-reap window); an ephemeral event with no consuming perspective has
  -- no work item and is reapable at once. The wh_event_store pointer is left in place — a
  -- pointer-present / body-NULL row is the deterministic rebuild-guard signal (#13d), not a lost event.
  -- Skipped under debug_mode so retained forensic bodies survive with the retained work items.
  -- Grace window: a consumed body is also kept until it is OLDER than v_ephemeral_grace_seconds, so an
  -- out-of-order straggler can still rewind through it (rewind uses the surviving bodies + a snapshot floor).
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
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
        WHERE pe.event_id = eb.event_id
          AND pe.processed_at IS NULL
      )
      -- Snapshot-coverage gate: the reap must never outrun the rewind floor. Reap only once EVERY consuming
      -- perspective has a snapshot at/past this event's commit_sequence — i.e. there is no association whose
      -- perspective lacks a covering snapshot for the stream. The reap-driven step (MaintenanceWorker) drives
      -- those snapshots just before this runs, so coverage is normally satisfied; an event with no consuming
      -- perspective is vacuously covered, and an unstamped event (commit_sequence NULL) is held until stamped.
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_message_associations ma
        WHERE ma.normalized_message_type = es.event_type
          AND ma.association_type = 'perspective'
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
END;
$$ LANGUAGE plpgsql;
