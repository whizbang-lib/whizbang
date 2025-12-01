-- Whizbang Infrastructure Schema for Postgres
-- Generated: 2025-12-01 22:48:03 UTC
-- Infrastructure Prefix: wb_
-- Perspective Prefix: wb_per_

-- Inbox - Message deduplication and idempotency
CREATE TABLE IF NOT EXISTS wb_inbox (
  message_id UUID NOT NULL PRIMARY KEY,
  event_type VARCHAR(500) NOT NULL,
  event_data JSONB NOT NULL,
  metadata JSONB NOT NULL,
  scope JSONB NULL,
  processed_at TIMESTAMPTZ NULL,
  received_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_inbox_processed_at ON wb_inbox (processed_at);
CREATE INDEX IF NOT EXISTS idx_inbox_received_at ON wb_inbox (received_at);

-- Outbox - Transactional messaging pattern
CREATE TABLE IF NOT EXISTS wb_outbox (
  message_id UUID NOT NULL PRIMARY KEY,
  destination VARCHAR(500) NOT NULL,
  event_type VARCHAR(500) NOT NULL,
  event_data JSONB NOT NULL,
  metadata JSONB NOT NULL,
  scope JSONB NULL,
  status VARCHAR(50) NOT NULL DEFAULT 'Pending',
  attempts INTEGER NOT NULL DEFAULT 0,
  created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
  published_at TIMESTAMPTZ NULL
);

CREATE INDEX IF NOT EXISTS idx_outbox_status_created_at ON wb_outbox (status, created_at);
CREATE INDEX IF NOT EXISTS idx_outbox_published_at ON wb_outbox (published_at);

-- Event Store - Event sourcing and audit trail
CREATE TABLE IF NOT EXISTS wb_event_store (
  event_id UUID NOT NULL PRIMARY KEY,
  aggregate_id UUID NOT NULL,
  aggregate_type VARCHAR(500) NOT NULL,
  event_type VARCHAR(500) NOT NULL,
  event_data JSONB NOT NULL,
  metadata JSONB NOT NULL,
  sequence_number BIGINT NOT NULL,
  version INTEGER NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_event_store_aggregate ON wb_event_store (aggregate_id, version);
CREATE INDEX IF NOT EXISTS idx_event_store_aggregate_type ON wb_event_store (aggregate_type, created_at);
CREATE INDEX IF NOT EXISTS idx_event_store_sequence ON wb_event_store (sequence_number);

-- Request/Response - Async request/response tracking
CREATE TABLE IF NOT EXISTS wb_request_response (
  request_id UUID NOT NULL PRIMARY KEY,
  correlation_id UUID NOT NULL,
  request_type VARCHAR(500) NOT NULL,
  request_data JSONB NOT NULL,
  response_type VARCHAR(500) NULL,
  response_data JSONB NULL,
  status VARCHAR(50) NOT NULL DEFAULT 'Pending',
  created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
  completed_at TIMESTAMPTZ NULL,
  expires_at TIMESTAMPTZ NULL
);

CREATE INDEX IF NOT EXISTS idx_request_response_correlation ON wb_request_response (correlation_id);
CREATE INDEX IF NOT EXISTS idx_request_response_status_created ON wb_request_response (status, created_at);
CREATE INDEX IF NOT EXISTS idx_request_response_expires ON wb_request_response (expires_at);

-- Sequences - Distributed sequence generation
CREATE TABLE IF NOT EXISTS wb_sequences (
  sequence_name VARCHAR(200) NOT NULL PRIMARY KEY,
  current_value BIGINT NOT NULL DEFAULT 0,
  increment_by INTEGER NOT NULL DEFAULT 1,
  last_updated_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);


