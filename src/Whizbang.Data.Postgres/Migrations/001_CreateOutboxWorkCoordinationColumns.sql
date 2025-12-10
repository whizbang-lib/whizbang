-- Migration: Create work coordination columns for outbox table
-- Date: 2025-12-10
-- Description: Adds columns for lease-based processing, multi-instance coordination,
--              orphaned work recovery, and failure classification to wh_outbox table

-- Add instance_id column to track which service instance is processing a message
ALTER TABLE wh_outbox
ADD COLUMN IF NOT EXISTS instance_id UUID NULL;

-- Add lease_expiry column to track when a lease expires for orphaned work recovery
ALTER TABLE wh_outbox
ADD COLUMN IF NOT EXISTS lease_expiry TIMESTAMPTZ NULL;

-- Add failure_reason column to enable typed failure classification
ALTER TABLE wh_outbox
ADD COLUMN IF NOT EXISTS failure_reason INTEGER NOT NULL DEFAULT 99;

-- Create index for efficient lease expiry queries
-- Used by WorkCoordinator to find orphaned messages
CREATE INDEX IF NOT EXISTS idx_outbox_lease_expiry
ON wh_outbox (lease_expiry)
WHERE lease_expiry IS NOT NULL;

-- Create composite index for status-based queries
-- Used by WorkCoordinator to find pending and orphaned work recovery
CREATE INDEX IF NOT EXISTS idx_outbox_status_lease
ON wh_outbox (status, lease_expiry)
WHERE (status & 32768) = 0 AND (status & 4) != 4;  -- Not failed and not published

-- Create index for efficient failure reason filtering
-- Used by WorkCoordinator to filter and retry messages by failure type
CREATE INDEX IF NOT EXISTS idx_outbox_failure_reason
ON wh_outbox (failure_reason)
WHERE (status & 32768) = 32768;  -- Only index failed messages

-- Add comments for documentation
COMMENT ON COLUMN wh_outbox.instance_id IS 'Service instance ID currently processing this message';
COMMENT ON COLUMN wh_outbox.lease_expiry IS 'Timestamp when the processing lease expires (for orphaned work recovery)';
COMMENT ON COLUMN wh_outbox.failure_reason IS 'Classified failure reason (MessageFailureReason enum): None=0, TransportNotReady=1, TransportException=2, SerializationError=3, ValidationError=4, MaxAttemptsExceeded=5, LeaseExpired=6, Unknown=99';
