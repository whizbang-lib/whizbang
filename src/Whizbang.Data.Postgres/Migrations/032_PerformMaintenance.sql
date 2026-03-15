-- Migration: 032_PerformMaintenance.sql
-- Date: 2026-03-12
-- Description: Creates perform_maintenance function for startup database health tasks.
--              Extensible — add new maintenance operations here over time.
-- Dependencies: 001-031

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
  -- Future tasks go here
  -- ========================================
  -- Examples:
  -- - Purge orphaned active_streams older than X days
  -- - Archive old event_store entries
  -- - Clean up stale service_instances
  -- - Rebuild bloated indexes (REINDEX)
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.perform_maintenance IS
'Runs startup maintenance tasks: purges completed messages, reclaims space.
Returns a result set with task name, rows affected, duration, and status.
Extensible — add new maintenance operations as needed.';
