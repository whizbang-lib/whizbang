-- Whizbang Infrastructure Schema for SQLite
-- Generated: 2025-12-17 15:30:23 UTC
-- Infrastructure Prefix: wh_
-- Perspective Prefix: wb_per_

-- SQLite Type Notes:
--   UUIDs: TEXT (hex format)
--   JSON: TEXT (use JSON1 extension for querying)
--   Timestamps: TEXT (ISO8601 format)
--   Booleans: INTEGER (0 = false, 1 = true)

-- Inbox - Message deduplication and idempotency
CREATE TABLE IF NOT EXISTS wh_inbox (
  message_id TEXT NOT NULL PRIMARY KEY,
  handler_name TEXT NOT NULL,
  event_type TEXT NOT NULL,
  event_data TEXT NOT NULL,
  metadata TEXT NOT NULL,
  scope TEXT NULL,
  stream_id TEXT NULL,
  partition_number INTEGER NULL,
  status INTEGER NOT NULL DEFAULT 1,
  attempts INTEGER NOT NULL DEFAULT 0,
  error TEXT NULL,
  instance_id TEXT NULL,
  lease_expiry TEXT NULL,
  failure_reason INTEGER NOT NULL DEFAULT 99,
  scheduled_for TEXT NULL,
  processed_at TEXT NULL,
  received_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_inbox_processed_at ON wh_inbox (processed_at);
CREATE INDEX IF NOT EXISTS idx_inbox_received_at ON wh_inbox (received_at);
CREATE INDEX IF NOT EXISTS idx_inbox_lease_expiry ON wh_inbox (lease_expiry);
CREATE INDEX IF NOT EXISTS idx_inbox_status_lease ON wh_inbox (status, lease_expiry);
CREATE INDEX IF NOT EXISTS idx_inbox_failure_reason ON wh_inbox (failure_reason);
CREATE INDEX IF NOT EXISTS idx_inbox_scheduled_for ON wh_inbox (stream_id, scheduled_for, received_at);

-- Outbox - Transactional messaging pattern
CREATE TABLE IF NOT EXISTS wh_outbox (
  message_id TEXT NOT NULL PRIMARY KEY,
  destination TEXT NOT NULL,
  event_type TEXT NOT NULL,
  event_data TEXT NOT NULL,
  metadata TEXT NOT NULL,
  scope TEXT NULL,
  stream_id TEXT NULL,
  partition_number INTEGER NULL,
  status INTEGER NOT NULL DEFAULT 1,
  attempts INTEGER NOT NULL DEFAULT 0,
  error TEXT NULL,
  instance_id TEXT NULL,
  lease_expiry TEXT NULL,
  failure_reason INTEGER NOT NULL DEFAULT 99,
  scheduled_for TEXT NULL,
  created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
  published_at TEXT NULL,
  processed_at TEXT NULL
);

CREATE INDEX IF NOT EXISTS idx_outbox_status_created_at ON wh_outbox (status, created_at);
CREATE INDEX IF NOT EXISTS idx_outbox_published_at ON wh_outbox (published_at);
CREATE INDEX IF NOT EXISTS idx_outbox_lease_expiry ON wh_outbox (lease_expiry);
CREATE INDEX IF NOT EXISTS idx_outbox_status_lease ON wh_outbox (status, lease_expiry);
CREATE INDEX IF NOT EXISTS idx_outbox_failure_reason ON wh_outbox (failure_reason);
CREATE INDEX IF NOT EXISTS idx_outbox_scheduled_for ON wh_outbox (stream_id, scheduled_for, created_at);

-- Event Store - Event sourcing and audit trail
CREATE TABLE IF NOT EXISTS wh_event_store (
  event_id TEXT NOT NULL PRIMARY KEY,
  stream_id TEXT NOT NULL,
  aggregate_id TEXT NOT NULL,
  aggregate_type TEXT NOT NULL,
  event_type TEXT NOT NULL,
  event_data TEXT NOT NULL,
  metadata TEXT NOT NULL,
  scope TEXT NULL,
  sequence_number INTEGER NOT NULL,
  version INTEGER NOT NULL,
  created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_event_store_stream ON wh_event_store (stream_id, version);
CREATE UNIQUE INDEX IF NOT EXISTS idx_event_store_aggregate ON wh_event_store (aggregate_id, version);
CREATE INDEX IF NOT EXISTS idx_event_store_aggregate_type ON wh_event_store (aggregate_type, created_at);
CREATE INDEX IF NOT EXISTS idx_event_store_sequence ON wh_event_store (sequence_number);

-- Request/Response - Async request/response tracking
CREATE TABLE IF NOT EXISTS wh_request_response (
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

CREATE UNIQUE INDEX IF NOT EXISTS idx_request_response_correlation ON wh_request_response (correlation_id);
CREATE INDEX IF NOT EXISTS idx_request_response_status_created ON wh_request_response (status, created_at);
CREATE INDEX IF NOT EXISTS idx_request_response_expires ON wh_request_response (expires_at);

-- Sequences - Distributed sequence generation
CREATE TABLE IF NOT EXISTS wh_sequences (
  sequence_name TEXT NOT NULL PRIMARY KEY,
  current_value INTEGER NOT NULL DEFAULT 0,
  increment_by INTEGER NOT NULL DEFAULT 1,
  last_updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);


