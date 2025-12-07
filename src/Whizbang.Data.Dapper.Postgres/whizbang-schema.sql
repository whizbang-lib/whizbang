-- Whizbang Infrastructure Schema for Postgres
-- Generated: 2025-12-02 (Updated for lease-based coordination)
-- Updated: 2025-12-06 - Standardized on wh_ prefix, function moved to shared library
-- Infrastructure Prefix: wh_ (modern) and whizbang_ (legacy compatibility)
-- Perspective Prefix: wh_per_

-- NOTE: The process_work_batch function is now loaded from the shared library
-- (Whizbang.Data.Postgres/Migrations/004_CreateProcessWorkBatchFunction.sql)
-- to ensure consistency between Dapper and EF Core implementations.

-- Service Instances - Track active service instances for distributed coordination
CREATE TABLE IF NOT EXISTS wh_service_instances (
  instance_id UUID NOT NULL PRIMARY KEY,
  service_name VARCHAR(200) NOT NULL,
  host_name VARCHAR(200) NOT NULL,
  process_id INTEGER NOT NULL,
  started_at TIMESTAMPTZ NOT NULL,
  last_heartbeat_at TIMESTAMPTZ NOT NULL,
  metadata JSONB NULL
);

CREATE INDEX IF NOT EXISTS idx_service_instances_last_heartbeat ON wh_service_instances (last_heartbeat_at);

-- Inbox - Message deduplication and idempotency
-- Updated: 2025-12-06 - Added partitioning support for stream ordering
CREATE TABLE IF NOT EXISTS wh_inbox (
  message_id UUID NOT NULL PRIMARY KEY,
  handler_name VARCHAR(500) NOT NULL,
  event_type VARCHAR(500) NOT NULL,
  event_data JSONB NOT NULL,
  metadata JSONB NOT NULL,
  scope JSONB NULL,
  status INTEGER NOT NULL DEFAULT 1,  -- MessageProcessingStatus flags (1 = Stored)
  attempts INTEGER NOT NULL DEFAULT 0,
  error TEXT NULL,
  received_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
  processed_at TIMESTAMPTZ NULL,
  instance_id UUID NULL,
  lease_expiry TIMESTAMPTZ NULL,
  stream_id UUID NULL,  -- For stream ordering (aggregate ID or message ID)
  partition_number INTEGER NULL  -- Computed from stream_id for load distribution
);

-- Partition-based indexes for efficient work claiming
CREATE INDEX IF NOT EXISTS idx_inbox_partition_status ON wh_inbox (partition_number, status)
  WHERE (status & 32768) = 0 AND (status & 24) != 24;  -- Not failed AND not fully completed

CREATE INDEX IF NOT EXISTS idx_inbox_partition_stream_order ON wh_inbox (partition_number, stream_id, received_at);
CREATE INDEX IF NOT EXISTS idx_inbox_processed_at ON wh_inbox (processed_at);
CREATE INDEX IF NOT EXISTS idx_inbox_lease_expiry ON wh_inbox (lease_expiry) WHERE (status & 24) != 24;

-- Outbox - Transactional messaging pattern with lease-based coordination
-- Updated: 2025-12-06 - Added partitioning support for stream ordering
CREATE TABLE IF NOT EXISTS wh_outbox (
  message_id UUID NOT NULL PRIMARY KEY,
  destination VARCHAR(500) NOT NULL,
  event_type VARCHAR(500) NOT NULL,
  event_data JSONB NOT NULL,
  metadata JSONB NOT NULL,
  scope JSONB NULL,
  status INTEGER NOT NULL DEFAULT 1,  -- MessageProcessingStatus flags (1 = Stored)
  attempts INTEGER NOT NULL DEFAULT 0,
  error TEXT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
  published_at TIMESTAMPTZ NULL,
  processed_at TIMESTAMPTZ NULL,  -- When fully completed
  instance_id UUID NULL,
  lease_expiry TIMESTAMPTZ NULL,
  stream_id UUID NULL,  -- For stream ordering (aggregate ID or message ID)
  partition_number INTEGER NULL  -- Computed from stream_id for load distribution
);

-- Partition-based indexes for efficient work claiming
CREATE INDEX IF NOT EXISTS idx_outbox_partition_status ON wh_outbox (partition_number, status)
  WHERE (status & 32768) = 0 AND (status & 24) != 24;  -- Not failed AND not fully completed

CREATE INDEX IF NOT EXISTS idx_outbox_partition_stream_order ON wh_outbox (partition_number, stream_id, created_at);
CREATE INDEX IF NOT EXISTS idx_outbox_published_at ON wh_outbox (published_at);
CREATE INDEX IF NOT EXISTS idx_outbox_lease_expiry ON wh_outbox (lease_expiry) WHERE (status & 24) != 24;

-- Event Store - Event sourcing and audit trail
-- Uses stream_id as the primary event stream identifier (preferred)
-- aggregate_id maintained for backwards compatibility
-- Uses 3-column JSONB pattern: event_data, metadata, scope
CREATE TABLE IF NOT EXISTS wh_event_store (
  event_id UUID NOT NULL PRIMARY KEY,
  stream_id UUID NOT NULL,
  aggregate_id UUID NOT NULL,
  aggregate_type VARCHAR(500) NOT NULL,
  event_type VARCHAR(500) NOT NULL,
  event_data JSONB NOT NULL,
  metadata JSONB NOT NULL,
  scope JSONB NULL,
  sequence_number BIGINT NOT NULL,
  version INTEGER NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_event_store_stream ON wh_event_store (stream_id, version);
CREATE UNIQUE INDEX IF NOT EXISTS idx_event_store_aggregate ON wh_event_store (aggregate_id, version);
CREATE INDEX IF NOT EXISTS idx_event_store_aggregate_type ON wh_event_store (aggregate_type, created_at);
CREATE INDEX IF NOT EXISTS idx_event_store_sequence ON wh_event_store (sequence_number);

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

CREATE UNIQUE INDEX IF NOT EXISTS idx_request_response_correlation ON wh_request_response (correlation_id);
CREATE INDEX IF NOT EXISTS idx_request_response_status_created ON wh_request_response (status, created_at);
CREATE INDEX IF NOT EXISTS idx_request_response_expires ON wh_request_response (expires_at);

-- Sequences - Distributed sequence generation
CREATE TABLE IF NOT EXISTS wh_sequences (
  sequence_name VARCHAR(200) NOT NULL PRIMARY KEY,
  current_value BIGINT NOT NULL DEFAULT 0,
  increment_by INTEGER NOT NULL DEFAULT 1,
  last_updated_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- Event Store Sequence - Global sequence for event ordering across all streams
CREATE SEQUENCE IF NOT EXISTS wh_event_sequence START WITH 1 INCREMENT BY 1;

-- Message Deduplication - Permanent deduplication tracking (never deleted)
-- Tracks all message IDs ever received for idempotent delivery guarantees
CREATE TABLE IF NOT EXISTS wh_message_deduplication (
  message_id UUID NOT NULL PRIMARY KEY,
  first_seen_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_message_dedup_first_seen ON wh_message_deduplication(first_seen_at);

-- Partition Assignments - Track which partitions are owned by which service instances
-- Updated: 2025-12-06 - Partition-based stream ordering support
CREATE TABLE IF NOT EXISTS wh_partition_assignments (
  partition_number INTEGER NOT NULL PRIMARY KEY,
  instance_id UUID NOT NULL REFERENCES wh_service_instances(instance_id) ON DELETE CASCADE,
  assigned_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
  last_heartbeat TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_partition_instance ON wh_partition_assignments(instance_id);
CREATE INDEX IF NOT EXISTS idx_partition_heartbeat ON wh_partition_assignments(last_heartbeat);

-- Helper function: Compute partition number from stream_id
-- Uses hashtext for consistent hashing across all stream IDs
CREATE OR REPLACE FUNCTION compute_partition(p_stream_id UUID, p_partition_count INTEGER DEFAULT 10000)
RETURNS INTEGER AS $$
BEGIN
  -- Use hashtext on UUID string for consistent hashing
  -- Modulo to get partition number (0 to partition_count-1)
  -- Handle NULL stream_id by using random partition (for non-event messages)
  IF p_stream_id IS NULL THEN
    RETURN floor(random() * p_partition_count)::INTEGER;
  END IF;

  RETURN (abs(hashtext(p_stream_id::TEXT)) % p_partition_count)::INTEGER;
END;
$$ LANGUAGE plpgsql IMMUTABLE;

-- Legacy table name aliases for backwards compatibility
CREATE OR REPLACE VIEW whizbang_event_store AS SELECT * FROM wh_event_store;
CREATE OR REPLACE VIEW whizbang_outbox AS SELECT * FROM wh_outbox;
CREATE OR REPLACE VIEW whizbang_inbox AS SELECT * FROM wh_inbox;
CREATE OR REPLACE VIEW whizbang_request_response AS SELECT * FROM wh_request_response;
CREATE OR REPLACE VIEW whizbang_sequences AS SELECT * FROM wh_sequences;
