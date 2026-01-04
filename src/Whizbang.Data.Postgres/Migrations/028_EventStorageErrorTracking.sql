-- =====================================================================================
-- Migration 028: Event Storage Error Tracking & Logging System
-- =====================================================================================
-- Purpose: Add comprehensive error tracking and logging for event storage operations
--
-- This migration adds:
-- 1. wh_log table - Permanent SQL logging table for errors/warnings
-- 2. wh_settings table - Configuration table for log levels and other settings
-- 3. log_event() function - Helper function for conditional logging based on log level
--
-- Author: Phil Carbone <phil@extravaganza.software>
-- Date: 2025-12-28
-- =====================================================================================

-- =====================================================================================
-- Part 1: Create wh_log Table
-- =====================================================================================
-- Stores all log entries from SQL functions (process_work_batch, etc.)
-- Log levels: 0=Debug, 1=Info, 2=Warning, 3=Error
-- =====================================================================================

CREATE TABLE IF NOT EXISTS wh_log (
  log_id BIGSERIAL PRIMARY KEY,
  logged_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  log_level INTEGER NOT NULL,
  source VARCHAR(50) NOT NULL,
  event_id UUID NULL,
  message_id UUID NULL,
  event_type VARCHAR(500) NULL,
  error_message TEXT NOT NULL,
  metadata JSONB NULL
);

CREATE INDEX IF NOT EXISTS idx_wh_log_logged_at ON wh_log (logged_at DESC);
CREATE INDEX IF NOT EXISTS idx_wh_log_level ON wh_log (log_level);
CREATE INDEX IF NOT EXISTS idx_wh_log_event_id ON wh_log (event_id) WHERE event_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_wh_log_message_id ON wh_log (message_id) WHERE message_id IS NOT NULL;

COMMENT ON TABLE wh_log IS 'Stores log entries from SQL functions for debugging and monitoring';
COMMENT ON COLUMN wh_log.log_level IS '0=Debug, 1=Info, 2=Warning, 3=Error';
COMMENT ON COLUMN wh_log.source IS 'Function or component that generated the log entry';
COMMENT ON COLUMN wh_log.metadata IS 'Additional context (phase, counts, types, etc.)';

-- =====================================================================================
-- Part 2: Create wh_settings Table
-- =====================================================================================
-- Stores configuration settings that can be managed by C# application
-- =====================================================================================

CREATE TABLE IF NOT EXISTS wh_settings (
  setting_key VARCHAR(200) PRIMARY KEY,
  setting_value TEXT NOT NULL,
  value_type VARCHAR(50) NOT NULL,
  description TEXT NULL,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_by VARCHAR(200) NULL
);

COMMENT ON TABLE wh_settings IS 'Application settings managed via C# API';
COMMENT ON COLUMN wh_settings.value_type IS 'Data type: string, integer, boolean, json';

-- Insert default log level (Warning and above)
INSERT INTO wh_settings (setting_key, setting_value, value_type, description)
VALUES ('sql_log_level', '2', 'integer', 'Minimum log level for wh_log table (0=Debug, 1=Info, 2=Warning, 3=Error)')
ON CONFLICT (setting_key) DO NOTHING;

-- =====================================================================================
-- Part 3: Create log_event() Helper Function
-- =====================================================================================
-- Conditionally logs events based on configured log level
-- Only logs if p_log_level >= configured minimum level
-- =====================================================================================

CREATE OR REPLACE FUNCTION __SCHEMA__.log_event(
  p_log_level INTEGER,
  p_source VARCHAR(50),
  p_message TEXT,
  p_event_id UUID DEFAULT NULL,
  p_message_id UUID DEFAULT NULL,
  p_event_type VARCHAR(500) DEFAULT NULL,
  p_metadata JSONB DEFAULT NULL
) RETURNS VOID AS $$
DECLARE
  v_min_log_level INTEGER;
BEGIN
  -- Get configured minimum log level from settings
  SELECT setting_value::INTEGER INTO v_min_log_level
  FROM wh_settings
  WHERE setting_key = 'sql_log_level';

  -- Default to Warning (2) if not configured
  v_min_log_level := COALESCE(v_min_log_level, 2);

  -- Only log if level >= configured minimum
  IF p_log_level >= v_min_log_level THEN
    INSERT INTO wh_log (
      log_level,
      source,
      error_message,
      event_id,
      message_id,
      event_type,
      metadata
    )
    VALUES (
      p_log_level,
      p_source,
      p_message,
      p_event_id,
      p_message_id,
      p_event_type,
      p_metadata
    );
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.log_event IS 'Conditionally logs events based on configured log level';
