-- Whizbang Messaging Infrastructure - SQLite Migration
-- Version: 001
-- Description: Initial schema for inbox, outbox, request/response store, and event store

-- Inbox table for message ingestion staging (receives from remote outbox)
-- Uses JSONB pattern with separate columns for event_type, event_data, metadata, scope
CREATE TABLE IF NOT EXISTS whizbang_inbox (
    message_id TEXT PRIMARY KEY,
    handler_name TEXT NOT NULL,
    event_type TEXT NOT NULL,
    event_data TEXT NOT NULL,
    metadata TEXT NOT NULL,
    scope TEXT NULL,
    received_at TEXT NOT NULL DEFAULT (datetime('now')),
    processed_at TEXT NULL
);

CREATE INDEX IF NOT EXISTS ix_whizbang_inbox_processed_at ON whizbang_inbox(processed_at) WHERE processed_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_whizbang_inbox_received_at ON whizbang_inbox(received_at);

-- Outbox table for transactional outbox pattern (ExactlyOnce sending)
-- Uses JSONB pattern with separate columns for event_type, event_data, metadata, scope
CREATE TABLE IF NOT EXISTS whizbang_outbox (
    message_id TEXT PRIMARY KEY,
    destination TEXT NOT NULL,
    event_type TEXT NOT NULL,
    event_data TEXT NOT NULL,
    metadata TEXT NOT NULL,
    scope TEXT NULL,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    published_at TEXT NULL
);

CREATE INDEX IF NOT EXISTS ix_whizbang_outbox_published_at ON whizbang_outbox(published_at) WHERE published_at IS NULL;

-- Request/Response store for request-response pattern on pub/sub transports
CREATE TABLE IF NOT EXISTS whizbang_request_response (
    correlation_id TEXT PRIMARY KEY,
    request_id TEXT NOT NULL,
    response_envelope TEXT NULL,
    expires_at TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS ix_whizbang_request_response_expires_at ON whizbang_request_response(expires_at);

-- Event store for streaming/replay capability
-- Offset is per-stream (like Kafka partitions), calculated on insert
CREATE TABLE IF NOT EXISTS whizbang_event_store (
    stream_key TEXT NOT NULL,
    event_offset INTEGER NOT NULL,
    message_id TEXT NOT NULL,
    envelope TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    PRIMARY KEY (stream_key, event_offset),
    CONSTRAINT uq_whizbang_event_store_stream_message UNIQUE (stream_key, message_id)
);

CREATE INDEX IF NOT EXISTS ix_whizbang_event_store_stream_key_event_offset ON whizbang_event_store(stream_key, event_offset);
CREATE INDEX IF NOT EXISTS ix_whizbang_event_store_message_id ON whizbang_event_store(message_id);
