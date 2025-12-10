-- Whizbang Messaging Infrastructure - PostgreSQL Migration
-- Version: 002
-- Description: Add message queue table with lease-based reservation for distributed processing

-- Message queue table for pending messages (distributed inbox pattern)
-- Messages are leased by instances for processing to prevent duplicate work
CREATE TABLE IF NOT EXISTS whizbang_message_queue (
    message_id UUID PRIMARY KEY,
    event_type VARCHAR(500) NOT NULL,
    event_data JSONB NOT NULL,
    metadata JSONB,
    received_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    leased_by VARCHAR(255),  -- Instance ID that leased this message
    lease_expires_at TIMESTAMPTZ,  -- When the lease expires (null = not leased)
    CONSTRAINT chk_lease_consistency CHECK (
        (leased_by IS NULL AND lease_expires_at IS NULL) OR
        (leased_by IS NOT NULL AND lease_expires_at IS NOT NULL)
    )
);

-- Index for finding unleased or expired messages (hot path for workers)
CREATE INDEX IF NOT EXISTS ix_whizbang_message_queue_available
ON whizbang_message_queue(received_at)
WHERE leased_by IS NULL OR lease_expires_at < NOW();

-- Index for lease management queries
CREATE INDEX IF NOT EXISTS ix_whizbang_message_queue_lease
ON whizbang_message_queue(leased_by, lease_expires_at)
WHERE leased_by IS NOT NULL;

-- Rename existing inbox table to better reflect its purpose (processed messages for idempotency)
-- Note: This is a breaking change if code references whizbang_inbox directly
-- The IInbox interface will handle both tables appropriately
ALTER TABLE whizbang_inbox RENAME TO whizbang_processed_messages;

-- Update index names to match new table name
ALTER INDEX IF EXISTS ix_whizbang_inbox_processed_at
RENAME TO ix_whizbang_processed_messages_processed_at;

-- Add event_type column to processed_messages for better tracking
ALTER TABLE whizbang_processed_messages
ADD COLUMN IF NOT EXISTS event_type VARCHAR(500);

-- Add processed_by column to track which instance processed the message
ALTER TABLE whizbang_processed_messages
ADD COLUMN IF NOT EXISTS processed_by VARCHAR(255);
