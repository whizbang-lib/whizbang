-- Migration: 032_PerformMaintenance.sql
-- Date: 2026-03-12
-- Description: Creates perform_maintenance function for startup database health tasks.
--              Extensible — add new maintenance operations here over time.
-- Dependencies: 001-031

SELECT __SCHEMA__.drop_all_overloads('perform_maintenance');

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
BEGIN
  -- Read debug_mode flag once for the cycle. When true, the complete_* functions
  -- retain rows for forensics with processed_at stamped — this maintenance pass
  -- MUST skip purging those rows or the debug-mode design breaks.
  SELECT COALESCE(
    (SELECT setting_value::BOOLEAN FROM wh_settings WHERE setting_key = 'debug_mode'),
    FALSE
  ) INTO v_debug_mode;

  -- ========================================
  -- Task 1: Purge completed outbox messages
  -- ========================================
  v_start := clock_timestamp();
  IF v_debug_mode THEN
    v_rows := 0;
  ELSE
    DELETE FROM wh_outbox WHERE processed_at IS NOT NULL;
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
    DELETE FROM wh_inbox WHERE processed_at IS NOT NULL;
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
    DELETE FROM wh_perspective_events WHERE processed_at IS NOT NULL;
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
  DELETE FROM wh_message_deduplication
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
  DELETE FROM wh_inbox
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
  -- Removes wh_active_streams entries whose assigned_instance_id no longer exists
  -- in wh_service_instances. After the heartbeat-recency liveness check in
  -- claim_orphaned_inbox / claim_orphaned_outbox (migrations 024/025) these rows
  -- are already non-blocking, but they accumulate indefinitely because
  -- cleanup_stale_instances only nulls assigned_instance_id in the tick where the
  -- instance is deleted, and the field can be re-populated by concurrent UPSERTs
  -- via the COALESCE preserve-existing-ownership clause in process_work_batch
  -- Phase 5.5. No age guard — a missing wh_service_instances row means the
  -- instance is fully gone (UUIDv7 IDs never repeat).
  v_start := clock_timestamp();
  DELETE FROM __SCHEMA__.wh_active_streams
  WHERE assigned_instance_id IS NOT NULL
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_service_instances si
      WHERE si.instance_id = __SCHEMA__.wh_active_streams.assigned_instance_id
    );
  GET DIAGNOSTICS v_rows = ROW_COUNT;
  RETURN QUERY SELECT
    'purge_abandoned_active_streams'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    'ok'::TEXT;
END;
$$ LANGUAGE plpgsql;

-- Seed default retention settings
INSERT INTO wh_settings (setting_key, setting_value, value_type, description)
VALUES ('dedup_retention_days', '30', 'integer', 'Days to retain message deduplication entries')
ON CONFLICT (setting_key) DO NOTHING;

INSERT INTO wh_settings (setting_key, setting_value, value_type, description)
VALUES ('stuck_inbox_retention_days', '7', 'integer', 'Days to retain inbox messages that were never processed')
ON CONFLICT (setting_key) DO NOTHING;

-- Debug-mode flag mirrors IWorkCoordinatorStrategy.DebugMode in C#. When true, the
-- complete_* functions retain rows for forensics with processed_at stamped, and
-- perform_maintenance skips its purge tasks so those rows survive maintenance cycles.
-- Default false: production ephemeral semantics.
INSERT INTO wh_settings (setting_key, setting_value, value_type, description)
VALUES ('debug_mode', 'false', 'boolean', 'When true, complete_* functions retain rows and perform_maintenance skips purge of completed messages.')
ON CONFLICT (setting_key) DO NOTHING;

COMMENT ON FUNCTION __SCHEMA__.perform_maintenance IS
'Runs maintenance tasks: purges completed messages, old deduplication entries, stuck inbox messages, and abandoned active-stream ownership rows.
Returns a result set with task name, rows affected, duration, and status.
Retention periods configurable via wh_settings (dedup_retention_days, stuck_inbox_retention_days). Abandoned active-streams purge has no retention knob — a missing wh_service_instances row is sufficient evidence that the owner is gone.';
