-- Migration 016: Create distributed sequence generation table
-- Date: 2025-12-15
-- Description: Creates the wh_sequences table for distributed sequence number generation

-- Sequences - Distributed sequence generation
CREATE TABLE IF NOT EXISTS wh_sequences (
  sequence_name VARCHAR(200) NOT NULL PRIMARY KEY,
  current_value BIGINT NOT NULL DEFAULT 0,
  increment_by INTEGER NOT NULL DEFAULT 1,
  last_updated_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- Add comments for documentation
COMMENT ON COLUMN wh_sequences.sequence_name IS 'Unique sequence name identifier';
COMMENT ON COLUMN wh_sequences.current_value IS 'Current sequence value (increments atomically)';
COMMENT ON COLUMN wh_sequences.increment_by IS 'Increment step size (default: 1)';
COMMENT ON COLUMN wh_sequences.last_updated_at IS 'Timestamp of last sequence update';
