-- Migration: 073_ReapConsumedEphemeralBodies.sql
-- Date: 2026-07-15
-- Description: Tier-1 consumption-gated ephemeral body reaper (E1 #13b2). Adds Task 8 to
--              perform_maintenance: DELETE FROM wh_event_body once every perspective that consumes an
--              event has processed it (no unprocessed wh_perspective_events work item still references
--              it). wh_event_body holds ONLY ephemeral bodies (migration 072), so the reap is scoped to
--              ephemeral events by construction; the wh_event_store pointer is left in place (a
--              pointer-present / body-NULL row is the deterministic rebuild-guard signal, not a lost
--              event). Skipped under debug_mode so retained forensic bodies survive alongside the
--              retained completed work items, exactly like Tasks 1-3.
--              Also: a partial index supporting the reap's event_id anti-join, and aggressive autovacuum
--              on the delete-churn-heavy wh_event_body table so it reaches a bounded steady state.
-- Dependencies: 032 (perform_maintenance), 072 (wh_event_body + ephemeral offload emit chain),
--               009 (wh_perspective_events)

-- Supports Task 8's anti-join: probe UNPROCESSED work items by event_id. Partial (only rows with
-- processed_at IS NULL) so it stays small and does not weigh on the hot perspective-completion path.
CREATE INDEX IF NOT EXISTS ix_perspective_events_event_id_unprocessed
  ON __SCHEMA__.wh_perspective_events (event_id)
  WHERE processed_at IS NULL;

-- wh_event_body is delete-churn-heavy: bodies are reaped as fast as ephemeral events are consumed.
-- Tighten autovacuum so dead tuples are reclaimed promptly and space is recycled into a bounded
-- steady state (continuous appends reuse reaped space).
ALTER TABLE __SCHEMA__.wh_event_body SET (
  autovacuum_vacuum_scale_factor = 0.02,
  autovacuum_analyze_scale_factor = 0.02
);

-- Per-type rewind-grace overrides ([Ephemeral(RewindGraceSeconds=..)]). Synced from the catalog at startup;
-- the reaper LEFT JOINs it and resolves COALESCE(type grace, global default) per event. Absent row = global.
CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_ephemeral_type_grace (
  event_type    VARCHAR(500) PRIMARY KEY,
  grace_seconds INTEGER NOT NULL
);

-- Full replace of the per-type grace overrides: upsert the declared set (normalized), then prune any row no
-- longer declared. Called from the startup reconciler with the current [Ephemeral(RewindGraceSeconds>=0)] set.
-- Empty input clears all overrides (every type falls back to the global default).
CREATE OR REPLACE FUNCTION __SCHEMA__.sync_ephemeral_type_grace(p_names TEXT[], p_graces INTEGER[])
RETURNS VOID AS $$
BEGIN
  INSERT INTO __SCHEMA__.wh_ephemeral_type_grace (event_type, grace_seconds)
  SELECT __SCHEMA__.normalize_event_type(t), g
  FROM unnest(p_names, p_graces) AS x(t, g)
  ON CONFLICT (event_type) DO UPDATE SET grace_seconds = EXCLUDED.grace_seconds;

  DELETE FROM __SCHEMA__.wh_ephemeral_type_grace
  WHERE event_type <> ALL (SELECT __SCHEMA__.normalize_event_type(t) FROM unnest(p_names) AS t);
END;
$$ LANGUAGE plpgsql;

-- Re-create perform_maintenance with Task 8 appended. Tasks 1-7 are reproduced verbatim from migration
-- 032 (identical behavior); only Task 8 is new. Signature is unchanged, so CREATE OR REPLACE swaps it
-- in place with no need to drop overloads.
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
    (SELECT setting_value::BOOLEAN FROM wh_settings WHERE setting_key = 'debug_mode'),
    FALSE
  ) INTO v_debug_mode;

  -- Grace period before an owner-less active-stream row is purged (Task 6). Configurable via
  -- wh_settings; default 1 hour preserves the transient-NULL race window between
  -- cleanup_stale_instances nulling the owner and the next claim cycle re-assigning it.
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM wh_settings WHERE setting_key = 'abandoned_stream_hours'),
    1
  ) INTO v_abandoned_stream_hours;

  -- Rewind grace window (seconds): an ephemeral body is retained this long AFTER consumption so an
  -- out-of-order straggler can still rewind through it (events arrive out of order in a short window).
  -- Configurable via wh_settings; default 300s. A per-type [Ephemeral(RewindGrace)] override lands later.
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM wh_settings WHERE setting_key = 'ephemeral_rewind_grace_seconds'),
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
    (SELECT setting_value::INTEGER FROM wh_settings WHERE setting_key = 'dedup_retention_days'),
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
    (SELECT setting_value::INTEGER FROM wh_settings WHERE setting_key = 'stuck_inbox_retention_days'),
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
  SELECT COUNT(*) FROM wh_dead_letter_summary INTO v_rows;
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
  END IF;
  RETURN QUERY SELECT
    'reap_consumed_ephemeral_bodies'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_debug_mode THEN 'skipped (debug_mode=true)' ELSE 'ok' END::TEXT;
END;
$$ LANGUAGE plpgsql;

-- Rewind grace window: how long (seconds) a CONSUMED ephemeral body is retained — so an out-of-order
-- straggler can still rewind through it — before the reaper deletes it. Default 300s (5 min).
INSERT INTO wh_settings (setting_key, setting_value, value_type, description)
VALUES ('ephemeral_rewind_grace_seconds', '300', 'integer', 'Seconds a consumed ephemeral event body is retained (for out-of-order rewind) before the reaper deletes it.')
ON CONFLICT (setting_key) DO NOTHING;

COMMENT ON FUNCTION __SCHEMA__.perform_maintenance IS
'Runs maintenance tasks: purges completed messages, old deduplication entries, stuck inbox messages, abandoned active-stream ownership rows, refreshes the dead-letter summary, and reaps consumed ephemeral event bodies (wh_event_body, migration 073 Task 8 — consumption-gated on wh_perspective_events AND aged past the rewind grace window, pointer preserved).
Returns a result set with task name, rows affected, duration, and status.
Retention periods configurable via wh_settings (dedup_retention_days, stuck_inbox_retention_days, abandoned_stream_hours, ephemeral_rewind_grace_seconds). Abandoned active-streams whose owner row is gone are purged immediately; abandoned_stream_hours only governs the owner-less transient-NULL race grace window. The ephemeral-body reap and completed-message purges are skipped under debug_mode.';
