-- Whizbang Messaging Infrastructure - PostgreSQL Migration
-- Version: 001
-- Description: Initial schema for inbox, outbox, request/response store, and event store

-- Inbox table for message deduplication (ExactlyOnce receiving)
CREATE TABLE IF NOT EXISTS whizbang_inbox (
    message_id UUID PRIMARY KEY,
    handler_name VARCHAR(500) NOT NULL,
    processed_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_whizbang_inbox_processed_at ON whizbang_inbox(processed_at);

-- Outbox table for transactional outbox pattern (ExactlyOnce sending)
CREATE TABLE IF NOT EXISTS whizbang_outbox (
    message_id UUID PRIMARY KEY,
    destination VARCHAR(500) NOT NULL,
    payload BYTEA NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    published_at TIMESTAMPTZ NULL
);

CREATE INDEX IF NOT EXISTS ix_whizbang_outbox_published_at ON whizbang_outbox(published_at) WHERE published_at IS NULL;

-- Request/Response store for request-response pattern on pub/sub transports
CREATE TABLE IF NOT EXISTS whizbang_request_response (
    correlation_id UUID PRIMARY KEY,
    request_id UUID NOT NULL,
    response_envelope TEXT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_whizbang_request_response_expires_at ON whizbang_request_response(expires_at);

-- Event store for streaming/replay capability
CREATE TABLE IF NOT EXISTS whizbang_event_store (
    offset BIGSERIAL,
    stream_key VARCHAR(500) NOT NULL,
    message_id UUID NOT NULL,
    envelope TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (stream_key, offset),
    CONSTRAINT uq_whizbang_event_store_stream_message UNIQUE (stream_key, message_id)
);

CREATE INDEX IF NOT EXISTS ix_whizbang_event_store_stream_key_offset ON whizbang_event_store(stream_key, offset);
CREATE INDEX IF NOT EXISTS ix_whizbang_event_store_message_id ON whizbang_event_store(message_id);
