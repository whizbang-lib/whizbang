-- Whizbang Infrastructure Schema for SQLite
-- Generated: 2025-12-01 22:47:19 UTC
-- Infrastructure Prefix: wb_
-- Perspective Prefix: wb_per_

-- SQLite Type Notes:
--   UUIDs: TEXT (hex format)
--   JSON: TEXT (use JSON1 extension for querying)
--   Timestamps: TEXT (ISO8601 format)
--   Booleans: INTEGER (0 = false, 1 = true)

-- Inbox - Message deduplication and idempotency
CREATE TABLE IF NOT EXISTS wb_inbox (
  message_id TEXT NOT NULL PRIMARY KEY,
  event_type TEXT NOT NULL,
  event_data TEXT NOT NULL,
  metadata TEXT NOT NULL,
  scope TEXT NULL,
  processed_at TEXT NULL,
  received_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_inbox_processed_at ON wb_inbox (processed_at);
CREATE INDEX IF NOT EXISTS idx_inbox_received_at ON wb_inbox (received_at);

-- Outbox - Transactional messaging pattern
CREATE TABLE IF NOT EXISTS wb_outbox (
  message_id TEXT NOT NULL PRIMARY KEY,
  destination TEXT NOT NULL,
  event_type TEXT NOT NULL,
  event_data TEXT NOT NULL,
  metadata TEXT NOT NULL,
  scope TEXT NULL,
  status TEXT NOT NULL DEFAULT 'Pending',
  attempts INTEGER NOT NULL DEFAULT 0,
  created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
  published_at TEXT NULL
);

CREATE INDEX IF NOT EXISTS idx_outbox_status_created_at ON wb_outbox (status, created_at);
CREATE INDEX IF NOT EXISTS idx_outbox_published_at ON wb_outbox (published_at);

-- Event Store - Event sourcing and audit trail
CREATE TABLE IF NOT EXISTS wb_event_store (
  event_id TEXT NOT NULL PRIMARY KEY,
  aggregate_id TEXT NOT NULL,
  aggregate_type TEXT NOT NULL,
  event_type TEXT NOT NULL,
  event_data TEXT NOT NULL,
  metadata TEXT NOT NULL,
  sequence_number INTEGER NOT NULL,
  version INTEGER NOT NULL,
  created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_event_store_aggregate ON wb_event_store (aggregate_id, version);
CREATE INDEX IF NOT EXISTS idx_event_store_aggregate_type ON wb_event_store (aggregate_type, created_at);
CREATE INDEX IF NOT EXISTS idx_event_store_sequence ON wb_event_store (sequence_number);

-- Request/Response - Async request/response tracking
CREATE TABLE IF NOT EXISTS wb_request_response (
  request_id TEXT NOT NULL PRIMARY KEY,
  correlation_id TEXT NOT NULL,
  request_type TEXT NOT NULL,
  request_data TEXT NOT NULL,
  response_type TEXT NULL,
  response_data TEXT NULL,
  status TEXT NOT NULL DEFAULT 'Pending',
  created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
  completed_at TEXT NULL,
  expires_at TEXT NULL
);

CREATE INDEX IF NOT EXISTS idx_request_response_correlation ON wb_request_response (correlation_id);
CREATE INDEX IF NOT EXISTS idx_request_response_status_created ON wb_request_response (status, created_at);
CREATE INDEX IF NOT EXISTS idx_request_response_expires ON wb_request_response (expires_at);

-- Sequences - Distributed sequence generation
CREATE TABLE IF NOT EXISTS wb_sequences (
  sequence_name TEXT NOT NULL PRIMARY KEY,
  current_value INTEGER NOT NULL DEFAULT 0,
  increment_by INTEGER NOT NULL DEFAULT 1,
  last_updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);


