-- Migration: Add lease-based processing columns to outbox table
-- Date: 2025-12-02
-- Description: Adds instance_id and lease_expiry columns to enable
--              multi-instance coordination and orphaned work recovery

-- Add instance_id column to track which service instance is processing a message
ALTER TABLE wb_outbox
ADD COLUMN IF NOT EXISTS instance_id UUID NULL;

-- Add lease_expiry column to track when a lease expires for orphaned work recovery
ALTER TABLE wb_outbox
ADD COLUMN IF NOT EXISTS lease_expiry TIMESTAMPTZ NULL;

-- Create index for efficient lease expiry queries
-- Used by WorkCoordinator to find orphaned messages
CREATE INDEX IF NOT EXISTS idx_outbox_lease_expiry
ON wb_outbox (lease_expiry)
WHERE lease_expiry IS NOT NULL;

-- Create composite index for status-based queries
-- Used by WorkCoordinator to find pending and orphaned work
CREATE INDEX IF NOT EXISTS idx_outbox_status_lease
ON wb_outbox (status, lease_expiry)
WHERE status IN ('Pending', 'Publishing');

-- Add comment for documentation
COMMENT ON COLUMN wb_outbox.instance_id IS 'Service instance ID currently processing this message';
COMMENT ON COLUMN wb_outbox.lease_expiry IS 'Timestamp when the processing lease expires (for orphaned work recovery)';
