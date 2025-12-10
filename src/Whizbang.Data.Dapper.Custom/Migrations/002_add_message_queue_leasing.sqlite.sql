-- Whizbang Messaging Infrastructure - SQLite Migration
-- Version: 002
-- Description: Add message queue table with lease-based reservation for distributed processing

-- Message queue table for pending messages (distributed inbox pattern)
CREATE TABLE IF NOT EXISTS whizbang_message_queue (
    message_id TEXT PRIMARY KEY,
    event_type TEXT NOT NULL,
    event_data TEXT NOT NULL,  -- JSON as TEXT in SQLite
    metadata TEXT,
    received_at TEXT NOT NULL DEFAULT (datetime('now')),
    leased_by TEXT,  -- Instance ID that leased this message
    lease_expires_at TEXT,  -- When the lease expires (null = not leased)
    CHECK (
        (leased_by IS NULL AND lease_expires_at IS NULL) OR
        (leased_by IS NOT NULL AND lease_expires_at IS NOT NULL)
    )
);

-- Index for finding unleased or expired messages (hot path for workers)
CREATE INDEX IF NOT EXISTS ix_whizbang_message_queue_available
ON whizbang_message_queue(received_at)
WHERE leased_by IS NULL OR lease_expires_at < datetime('now');

-- Index for lease management queries
CREATE INDEX IF NOT EXISTS ix_whizbang_message_queue_lease
ON whizbang_message_queue(leased_by, lease_expires_at)
WHERE leased_by IS NOT NULL;

-- Rename existing inbox table to better reflect its purpose
-- Note: SQLite doesn't support ALTER TABLE RENAME COLUMN directly
-- So we create new table and copy data
CREATE TABLE IF NOT EXISTS whizbang_processed_messages (
    message_id TEXT PRIMARY KEY,
    handler_name TEXT NOT NULL,
    processed_at TEXT NOT NULL DEFAULT (datetime('now')),
    event_type TEXT,
    processed_by TEXT
);

-- Copy existing data from old table
INSERT INTO whizbang_processed_messages (message_id, handler_name, processed_at)
SELECT message_id, handler_name, processed_at
FROM whizbang_inbox
WHERE NOT EXISTS (SELECT 1 FROM whizbang_processed_messages WHERE message_id = whizbang_inbox.message_id);

-- Drop old table
DROP TABLE IF EXISTS whizbang_inbox;

-- Create index
CREATE INDEX IF NOT EXISTS ix_whizbang_processed_messages_processed_at
ON whizbang_processed_messages(processed_at);
