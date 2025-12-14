-- Whizbang Infrastructure Schema for Postgres
-- Generated: 2025-12-14 13:28:59 UTC
-- Infrastructure Prefix: wh_
-- Perspective Prefix: wh_per_

-- Service Instances - Distributed work coordination
CREATE TABLE IF NOT EXISTS wh_service_instances (
  instance_id UUID NOT NULL PRIMARY KEY,
  service_name VARCHAR(200) NOT NULL,
  host_name VARCHAR(200) NOT NULL,
  process_id INTEGER NOT NULL,
  started_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
  last_heartbeat_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
  metadata JSONB NULL
);

CREATE INDEX IF NOT EXISTS idx_service_instances_service_name ON wh_service_instances (service_name, last_heartbeat_at);
CREATE INDEX IF NOT EXISTS idx_service_instances_heartbeat ON wh_service_instances (last_heartbeat_at);

-- Message Deduplication - Permanent idempotency tracking
CREATE TABLE IF NOT EXISTS wh_message_deduplication (
  message_id UUID NOT NULL PRIMARY KEY,
  first_seen_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_message_dedup_first_seen ON wh_message_deduplication (first_seen_at);

-- Inbox - Message deduplication and idempotency
CREATE TABLE IF NOT EXISTS wh_inbox (
  message_id UUID NOT NULL PRIMARY KEY,
  event_type VARCHAR(500) NOT NULL,
  event_data JSONB NOT NULL,
  metadata JSONB NOT NULL,
  scope JSONB NULL,
  processed_at TIMESTAMPTZ NULL,
  received_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_inbox_processed_at ON wh_inbox (processed_at);
CREATE INDEX IF NOT EXISTS idx_inbox_received_at ON wh_inbox (received_at);

-- Outbox - Transactional messaging pattern
CREATE TABLE IF NOT EXISTS wh_outbox (
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

CREATE INDEX IF NOT EXISTS idx_outbox_status_created_at ON wh_outbox (status, created_at);
CREATE INDEX IF NOT EXISTS idx_outbox_published_at ON wh_outbox (published_at);

-- Event Store - Event sourcing and audit trail
CREATE TABLE IF NOT EXISTS wh_event_store (
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

CREATE UNIQUE INDEX IF NOT EXISTS idx_event_store_aggregate ON wh_event_store (aggregate_id, version);
CREATE INDEX IF NOT EXISTS idx_event_store_aggregate_type ON wh_event_store (aggregate_type, created_at);
CREATE INDEX IF NOT EXISTS idx_event_store_sequence ON wh_event_store (sequence_number);

-- Receptor Processing - Event handler tracking (log-style)
CREATE TABLE IF NOT EXISTS wh_receptor_processing (
  id UUID NOT NULL PRIMARY KEY,
  event_id UUID NOT NULL,
  receptor_name TEXT NOT NULL,
  status SMALLINT NOT NULL DEFAULT 0,
  attempts INTEGER NOT NULL DEFAULT 0,
  error TEXT NULL,
  started_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
  processed_at TIMESTAMPTZ NULL
);

CREATE INDEX IF NOT EXISTS idx_receptor_processing_event_id ON wh_receptor_processing (event_id);
CREATE INDEX IF NOT EXISTS idx_receptor_processing_receptor_name ON wh_receptor_processing (receptor_name);
CREATE INDEX IF NOT EXISTS idx_receptor_processing_status ON wh_receptor_processing (status);

-- Perspective Checkpoints - Read model projection tracking (checkpoint-style)
CREATE TABLE IF NOT EXISTS wh_perspective_checkpoints (
  stream_id UUID NOT NULL,
  perspective_name TEXT NOT NULL,
  last_event_id UUID NOT NULL,
  status SMALLINT NOT NULL DEFAULT 0,
  processed_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
  error TEXT NULL,
  PRIMARY KEY (stream_id, perspective_name)
);

CREATE INDEX IF NOT EXISTS idx_perspective_checkpoints_perspective_name ON wh_perspective_checkpoints (perspective_name);
CREATE INDEX IF NOT EXISTS idx_perspective_checkpoints_last_event_id ON wh_perspective_checkpoints (last_event_id);

-- Request/Response - Async request/response tracking
CREATE TABLE IF NOT EXISTS wh_request_response (
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

CREATE INDEX IF NOT EXISTS idx_request_response_correlation ON wh_request_response (correlation_id);
CREATE INDEX IF NOT EXISTS idx_request_response_status_created ON wh_request_response (status, created_at);
CREATE INDEX IF NOT EXISTS idx_request_response_expires ON wh_request_response (expires_at);

-- Sequences - Distributed sequence generation
CREATE TABLE IF NOT EXISTS wh_sequences (
  sequence_name VARCHAR(200) NOT NULL PRIMARY KEY,
  current_value BIGINT NOT NULL DEFAULT 0,
  increment_by INTEGER NOT NULL DEFAULT 1,
  last_updated_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);


