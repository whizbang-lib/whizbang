-- Migration: Create inbox table for message deduplication and idempotency
-- Date: 2025-12-10
-- Description: Creates the wh_inbox table with work coordination columns for
--              lease-based processing, multi-instance coordination, orphaned work recovery,
--              and failure classification

-- Inbox - Message deduplication and idempotency
CREATE TABLE IF NOT EXISTS wh_inbox (
  message_id UUID NOT NULL PRIMARY KEY,
  handler_name VARCHAR(500) NOT NULL,
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
  processed_at TIMESTAMPTZ NULL,
  received_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- Index for processed message queries
CREATE INDEX IF NOT EXISTS idx_inbox_processed_at ON wh_inbox (processed_at);

-- Index for received message queries (time-based)
CREATE INDEX IF NOT EXISTS idx_inbox_received_at ON wh_inbox (received_at);

-- Index for efficient lease expiry queries (orphaned work recovery)
CREATE INDEX IF NOT EXISTS idx_inbox_lease_expiry
ON wh_inbox (lease_expiry)
WHERE lease_expiry IS NOT NULL;

-- Composite index for status-based queries (pending and orphaned work)
CREATE INDEX IF NOT EXISTS idx_inbox_status_lease
ON wh_inbox (status, lease_expiry)
WHERE (status & 32768) = 0 AND (status & 2) != 2;  -- Not failed and not event stored

-- Index for efficient failure reason filtering
CREATE INDEX IF NOT EXISTS idx_inbox_failure_reason
ON wh_inbox (failure_reason)
WHERE (status & 32768) = 32768;  -- Only index failed messages

-- Add comments for documentation
COMMENT ON COLUMN wh_inbox.message_id IS 'Unique message identifier (UUIDv7)';
COMMENT ON COLUMN wh_inbox.handler_name IS 'Fully-qualified handler type name';
COMMENT ON COLUMN wh_inbox.event_type IS 'Fully-qualified event type name';
COMMENT ON COLUMN wh_inbox.event_data IS 'Serialized event payload (JSONB)';
COMMENT ON COLUMN wh_inbox.metadata IS 'Message metadata (JSONB)';
COMMENT ON COLUMN wh_inbox.scope IS 'Security/tenant scope (JSONB, nullable)';
COMMENT ON COLUMN wh_inbox.stream_id IS 'Event stream identifier (for event sourcing)';
COMMENT ON COLUMN wh_inbox.partition_number IS 'Partition number for work distribution';
COMMENT ON COLUMN wh_inbox.status IS 'Processing status flags (bitwise): Stored=1, EventStored=2, Published=4, Failed=32768';
COMMENT ON COLUMN wh_inbox.attempts IS 'Number of processing attempts';
COMMENT ON COLUMN wh_inbox.error IS 'Last error message (if failed)';
COMMENT ON COLUMN wh_inbox.instance_id IS 'Service instance ID currently processing this message';
COMMENT ON COLUMN wh_inbox.lease_expiry IS 'Timestamp when the processing lease expires (for orphaned work recovery)';
COMMENT ON COLUMN wh_inbox.failure_reason IS 'Classified failure reason (MessageFailureReason enum): None=0, TransportNotReady=1, TransportException=2, SerializationError=3, ValidationError=4, MaxAttemptsExceeded=5, LeaseExpired=6, Unknown=99';
COMMENT ON COLUMN wh_inbox.processed_at IS 'Timestamp when message processing completed';
COMMENT ON COLUMN wh_inbox.received_at IS 'Timestamp when message was received';
