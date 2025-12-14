-- Migration: Create work coordination columns for inbox table
-- Date: 2025-12-10
-- Description: Adds columns for lease-based processing, multi-instance coordination,
--              orphaned work recovery, and failure classification to wh_inbox table

-- Add instance_id column to track which service instance is processing a message
ALTER TABLE wh_inbox
ADD COLUMN IF NOT EXISTS instance_id UUID NULL;

-- Add lease_expiry column to track when a lease expires for orphaned work recovery
ALTER TABLE wh_inbox
ADD COLUMN IF NOT EXISTS lease_expiry TIMESTAMPTZ NULL;

-- Add failure_reason column to enable typed failure classification
ALTER TABLE wh_inbox
ADD COLUMN IF NOT EXISTS failure_reason INTEGER NOT NULL DEFAULT 99;

-- Create index for efficient lease expiry queries
-- Used by WorkCoordinator to find orphaned messages
CREATE INDEX IF NOT EXISTS idx_inbox_lease_expiry
ON wh_inbox (lease_expiry)
WHERE lease_expiry IS NOT NULL;

-- Create index for efficient failure reason filtering
-- Used by WorkCoordinator to filter and retry messages by failure type
CREATE INDEX IF NOT EXISTS idx_inbox_failure_reason
ON wh_inbox (failure_reason);
-- Note: Partial index WHERE clause removed - inbox doesn't have status column

-- Add comments for documentation
COMMENT ON COLUMN wh_inbox.instance_id IS 'Service instance ID currently processing this message';
COMMENT ON COLUMN wh_inbox.lease_expiry IS 'Timestamp when the processing lease expires (for orphaned work recovery)';
COMMENT ON COLUMN wh_inbox.failure_reason IS 'Classified failure reason (MessageFailureReason enum): None=0, TransportNotReady=1, TransportException=2, SerializationError=3, ValidationError=4, MaxAttemptsExceeded=5, LeaseExpired=6, Unknown=99';
