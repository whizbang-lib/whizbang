-- Migration: Create outbox table for transactional messaging
-- Date: 2025-12-10
-- Description: Creates the wh_outbox table with work coordination columns for
--              lease-based processing, multi-instance coordination, orphaned work recovery,
--              and failure classification

-- Outbox - Transactional messaging pattern
CREATE TABLE IF NOT EXISTS wh_outbox (
  message_id UUID NOT NULL PRIMARY KEY,
  destination VARCHAR(500) NOT NULL,
  event_type VARCHAR(500) NOT NULL,
  event_data JSONB NOT NULL,
  metadata JSONB NOT NULL,
  scope JSONB NULL,
  stream_id UUID NULL,
  partition_number INTEGER NULL,
  status INTEGER NOT NULL DEFAULT 1,
  attempts INTEGER NOT NULL DEFAULT 0,
  error TEXT NULL,
  instance_id UUID NULL,
  lease_expiry TIMESTAMPTZ NULL,
  failure_reason INTEGER NOT NULL DEFAULT 99,
  created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
  published_at TIMESTAMPTZ NULL,
  processed_at TIMESTAMPTZ NULL
);

-- Index for status-based queries (pending messages)
CREATE INDEX IF NOT EXISTS idx_outbox_status_created_at ON wh_outbox (status, created_at);

-- Index for published message queries
CREATE INDEX IF NOT EXISTS idx_outbox_published_at ON wh_outbox (published_at);

-- Index for efficient lease expiry queries (orphaned work recovery)
CREATE INDEX IF NOT EXISTS idx_outbox_lease_expiry
ON wh_outbox (lease_expiry)
WHERE lease_expiry IS NOT NULL;

-- Composite index for status-based queries (pending and orphaned work)
CREATE INDEX IF NOT EXISTS idx_outbox_status_lease
ON wh_outbox (status, lease_expiry)
WHERE (status & 32768) = 0 AND (status & 4) != 4;  -- Not failed and not published

-- Index for efficient failure reason filtering
CREATE INDEX IF NOT EXISTS idx_outbox_failure_reason
ON wh_outbox (failure_reason)
WHERE (status & 32768) = 32768;  -- Only index failed messages

-- Add comments for documentation
COMMENT ON COLUMN wh_outbox.message_id IS 'Unique message identifier (UUIDv7)';
COMMENT ON COLUMN wh_outbox.destination IS 'Target destination (topic, queue, etc.)';
COMMENT ON COLUMN wh_outbox.event_type IS 'Fully-qualified event type name';
COMMENT ON COLUMN wh_outbox.event_data IS 'Serialized event payload (JSONB)';
COMMENT ON COLUMN wh_outbox.metadata IS 'Message metadata (JSONB)';
COMMENT ON COLUMN wh_outbox.scope IS 'Security/tenant scope (JSONB, nullable)';
COMMENT ON COLUMN wh_outbox.stream_id IS 'Event stream identifier (for event sourcing)';
COMMENT ON COLUMN wh_outbox.partition_number IS 'Partition number for work distribution';
COMMENT ON COLUMN wh_outbox.status IS 'Processing status flags (bitwise): Stored=1, Published=4, Failed=32768';
COMMENT ON COLUMN wh_outbox.attempts IS 'Number of processing attempts';
COMMENT ON COLUMN wh_outbox.error IS 'Last error message (if failed)';
COMMENT ON COLUMN wh_outbox.instance_id IS 'Service instance ID currently processing this message';
COMMENT ON COLUMN wh_outbox.lease_expiry IS 'Timestamp when the processing lease expires (for orphaned work recovery)';
COMMENT ON COLUMN wh_outbox.failure_reason IS 'Classified failure reason (MessageFailureReason enum): None=0, TransportNotReady=1, TransportException=2, SerializationError=3, ValidationError=4, MaxAttemptsExceeded=5, LeaseExpired=6, Unknown=99';
COMMENT ON COLUMN wh_outbox.created_at IS 'Timestamp when message was created';
COMMENT ON COLUMN wh_outbox.published_at IS 'Timestamp when message was published';
COMMENT ON COLUMN wh_outbox.processed_at IS 'Timestamp when message processing completed';
