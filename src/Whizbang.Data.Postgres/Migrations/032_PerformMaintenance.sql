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
BEGIN
  -- ========================================
  -- Task 1: Purge completed outbox messages
  -- ========================================
  v_start := clock_timestamp();
  DELETE FROM wh_outbox WHERE processed_at IS NOT NULL;
  GET DIAGNOSTICS v_rows = ROW_COUNT;
  RETURN QUERY SELECT
    'purge_completed_outbox'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    'ok'::TEXT;

  -- ========================================
  -- Task 2: Purge completed inbox messages
  -- ========================================
  v_start := clock_timestamp();
  DELETE FROM wh_inbox WHERE processed_at IS NOT NULL;
  GET DIAGNOSTICS v_rows = ROW_COUNT;
  RETURN QUERY SELECT
    'purge_completed_inbox'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    'ok'::TEXT;

  -- ========================================
  -- Task 3: Purge completed perspective events
  -- ========================================
  v_start := clock_timestamp();
  DELETE FROM wh_perspective_events WHERE processed_at IS NOT NULL;
  GET DIAGNOSTICS v_rows = ROW_COUNT;
  RETURN QUERY SELECT
    'purge_completed_perspective_events'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    'ok'::TEXT;

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
END;
$$ LANGUAGE plpgsql;

-- Seed default retention settings
INSERT INTO wh_settings (setting_key, setting_value, value_type, description)
VALUES ('dedup_retention_days', '30', 'integer', 'Days to retain message deduplication entries')
ON CONFLICT (setting_key) DO NOTHING;

INSERT INTO wh_settings (setting_key, setting_value, value_type, description)
VALUES ('stuck_inbox_retention_days', '7', 'integer', 'Days to retain inbox messages that were never processed')
ON CONFLICT (setting_key) DO NOTHING;

COMMENT ON FUNCTION __SCHEMA__.perform_maintenance IS
'Runs maintenance tasks: purges completed messages, old deduplication entries, and stuck inbox messages.
Returns a result set with task name, rows affected, duration, and status.
Retention periods configurable via wh_settings (dedup_retention_days, stuck_inbox_retention_days).';
