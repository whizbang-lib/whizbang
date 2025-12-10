-- Migration 008: Create Event Store Table
-- Date: 2025-12-08
-- Description: Creates the wh_event_store table for event sourcing with optimistic concurrency control

-- Event Store - Persisted events for event sourcing
CREATE TABLE IF NOT EXISTS wh_event_store (
  event_id UUID NOT NULL PRIMARY KEY,
  stream_id UUID NOT NULL,
  aggregate_id UUID NOT NULL,
  aggregate_type VARCHAR(500) NOT NULL,
  event_type VARCHAR(500) NOT NULL,
  event_data JSONB NOT NULL,
  metadata JSONB NOT NULL,
  scope JSONB NULL,
  sequence_number BIGINT NOT NULL,
  version INTEGER NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- Unique index for stream_id + version (optimistic concurrency control)
CREATE UNIQUE INDEX IF NOT EXISTS idx_event_store_stream ON wh_event_store (stream_id, version);

-- Unique index for aggregate_id + version
CREATE UNIQUE INDEX IF NOT EXISTS idx_event_store_aggregate ON wh_event_store (aggregate_id, version);

-- Index for querying by aggregate type and creation time
CREATE INDEX IF NOT EXISTS idx_event_store_aggregate_type ON wh_event_store (aggregate_type, created_at);

-- Index for global sequence ordering
CREATE INDEX IF NOT EXISTS idx_event_store_sequence ON wh_event_store (sequence_number);

-- Add comments for documentation
COMMENT ON TABLE wh_event_store IS 'Event store for event sourcing with optimistic concurrency control via version number';
COMMENT ON COLUMN wh_event_store.event_id IS 'Unique event identifier';
COMMENT ON COLUMN wh_event_store.stream_id IS 'Stream identifier (maps to message stream_id)';
COMMENT ON COLUMN wh_event_store.aggregate_id IS 'Aggregate root identifier';
COMMENT ON COLUMN wh_event_store.aggregate_type IS 'Aggregate type name for querying';
COMMENT ON COLUMN wh_event_store.event_type IS 'Fully qualified event type name';
COMMENT ON COLUMN wh_event_store.event_data IS 'Event payload as JSONB';
COMMENT ON COLUMN wh_event_store.metadata IS 'Event metadata as JSONB';
COMMENT ON COLUMN wh_event_store.scope IS 'Optional scoping data (tenant, user, etc.)';
COMMENT ON COLUMN wh_event_store.sequence_number IS 'Global sequence number for total event ordering';
COMMENT ON COLUMN wh_event_store.version IS 'Version number within stream for optimistic concurrency';
COMMENT ON COLUMN wh_event_store.created_at IS 'Event creation timestamp';
