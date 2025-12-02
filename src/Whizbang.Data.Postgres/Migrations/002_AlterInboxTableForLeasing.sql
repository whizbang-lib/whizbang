-- Migration: Add lease-based processing columns to inbox table
-- Date: 2025-12-02
-- Description: Adds instance_id and lease_expiry columns to enable
--              multi-instance coordination and orphaned work recovery

-- Add instance_id column to track which service instance is processing a message
ALTER TABLE wb_inbox
ADD COLUMN IF NOT EXISTS instance_id UUID NULL;

-- Add lease_expiry column to track when a lease expires for orphaned work recovery
ALTER TABLE wb_inbox
ADD COLUMN IF NOT EXISTS lease_expiry TIMESTAMPTZ NULL;

-- Add status column to track processing state (similar to outbox)
ALTER TABLE wb_inbox
ADD COLUMN IF NOT EXISTS status VARCHAR(50) NOT NULL DEFAULT 'Pending';

-- Create index for efficient lease expiry queries
-- Used by WorkCoordinator to find orphaned messages
CREATE INDEX IF NOT EXISTS idx_inbox_lease_expiry
ON wb_inbox (lease_expiry)
WHERE lease_expiry IS NOT NULL;

-- Create composite index for status-based queries
-- Used by WorkCoordinator to find pending and orphaned work
CREATE INDEX IF NOT EXISTS idx_inbox_status_lease
ON wb_inbox (status, lease_expiry)
WHERE status IN ('Pending', 'Processing');

-- Add comment for documentation
COMMENT ON COLUMN wb_inbox.instance_id IS 'Service instance ID currently processing this message';
COMMENT ON COLUMN wb_inbox.lease_expiry IS 'Timestamp when the processing lease expires (for orphaned work recovery)';
COMMENT ON COLUMN wb_inbox.status IS 'Processing status: Pending, Processing, Completed, Failed';
