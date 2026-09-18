-- Whizbang Infrastructure Schema for Postgres
-- Generated: 2025-12-21 21:54:17 UTC
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

-- Active Streams - Ephemeral stream coordination
CREATE TABLE IF NOT EXISTS wh_active_streams (
  stream_id UUID NOT NULL PRIMARY KEY,
  partition_number INTEGER NOT NULL,
  assigned_instance_id UUID NULL,
  lease_expiry TIMESTAMPTZ NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_active_streams_instance ON wh_active_streams (assigned_instance_id) WHERE assigned_instance_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_active_streams_partition ON wh_active_streams (partition_number) WHERE assigned_instance_id IS NULL;
CREATE INDEX IF NOT EXISTS idx_active_streams_lease_expiry ON wh_active_streams (lease_expiry) WHERE lease_expiry IS NOT NULL;

-- Partition Assignments - Distributed work coordination
CREATE TABLE IF NOT EXISTS wh_partition_assignments (
  partition_number INTEGER NOT NULL PRIMARY KEY,
  instance_id UUID NOT NULL,
  assigned_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
  last_heartbeat TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_partition_assignments_instance ON wh_partition_assignments (instance_id, last_heartbeat);

-- Message Deduplication - Permanent idempotency tracking
CREATE TABLE IF NOT EXISTS wh_message_deduplication (
  message_id UUID NOT NULL PRIMARY KEY,
  first_seen_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_message_dedup_first_seen ON wh_message_deduplication (first_seen_at);

-- Inbox - Message deduplication and idempotency
CREATE TABLE IF NOT EXISTS wh_inbox (
  message_id UUID NOT NULL PRIMARY KEY,
  handler_name VARCHAR(500) NOT NULL,
  message_type VARCHAR(500) NOT NULL,
  event_data JSONB NOT NULL,
  metadata JSONB NOT NULL,
  scope JSONB NULL,
  stream_id UUID NULL,
  partition_number INTEGER NULL,
  is_event BOOLEAN NOT NULL DEFAULT FALSE,
  status INTEGER NOT NULL DEFAULT 1,
  attempts INTEGER NOT NULL DEFAULT 0,
  error TEXT NULL,
  instance_id UUID NULL,
  lease_expiry TIMESTAMPTZ NULL,
  failure_reason INTEGER NOT NULL DEFAULT 99,
  scheduled_for TIMESTAMPTZ NULL,
  processed_at TIMESTAMPTZ NULL,
  received_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- Only received_at is indexed here, and the five indexes that used to sit beside it are gone rather
-- than moved. They keyed processed_at, lease_expiry, status, failure_reason and scheduled_for, all
-- of which migration 162 moves to wh_inbox_state, where it creates the replacements.
--
-- Leaving them would not be untidy, it would break the second startup. This script runs on EVERY
-- start, before the migrations. `CREATE INDEX IF NOT EXISTS` tests the INDEX NAME, not the columns,
-- so once 162 has dropped a column -- taking its index with it -- the name is free again and
-- PostgreSQL goes on to build the index, against a column that is no longer there: 42703, and the
-- start fails behind the schema-ready gate. A fresh database never sees it, because on the first
-- boot the columns still exist when this runs, which is exactly why no test caught it: every
-- per-test database is a first boot.
--
-- This is the THIRD place the inbox is declared, after the Whizbang.Data.Schema descriptors and the
-- numbered migrations. The descriptor hazard was the mirror image of this one -- it re-ADDED the
-- dropped columns silently via ADD COLUMN IF NOT EXISTS -- and both have the same cause: a
-- create-time declaration that outlives the shape it describes.
CREATE INDEX IF NOT EXISTS idx_inbox_received_at ON wh_inbox (received_at);

-- Outbox - Transactional messaging pattern
CREATE TABLE IF NOT EXISTS wh_outbox (
  message_id UUID NOT NULL PRIMARY KEY,
  destination VARCHAR(500) NOT NULL,
  message_type VARCHAR(500) NOT NULL,
  event_data JSONB NOT NULL,
  metadata JSONB NOT NULL,
  scope JSONB NULL,
  stream_id UUID NULL,
  partition_number INTEGER NULL,
  is_event BOOLEAN NOT NULL DEFAULT FALSE,
  status INTEGER NOT NULL DEFAULT 1,
  attempts INTEGER NOT NULL DEFAULT 0,
  error TEXT NULL,
  instance_id UUID NULL,
  lease_expiry TIMESTAMPTZ NULL,
  failure_reason INTEGER NOT NULL DEFAULT 99,
  scheduled_for TIMESTAMPTZ NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
  published_at TIMESTAMPTZ NULL,
  processed_at TIMESTAMPTZ NULL
);

CREATE INDEX IF NOT EXISTS idx_outbox_status_created_at ON wh_outbox (status, created_at);
CREATE INDEX IF NOT EXISTS idx_outbox_published_at ON wh_outbox (published_at);
CREATE INDEX IF NOT EXISTS idx_outbox_lease_expiry ON wh_outbox (lease_expiry) WHERE lease_expiry IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_outbox_status_lease ON wh_outbox (status, lease_expiry) WHERE (status & 32768) = 0 AND (status & 4) != 4;
CREATE INDEX IF NOT EXISTS idx_outbox_failure_reason ON wh_outbox (failure_reason) WHERE (status & 32768) = 32768;
CREATE INDEX IF NOT EXISTS idx_outbox_scheduled_for ON wh_outbox (stream_id, scheduled_for, created_at) WHERE scheduled_for IS NOT NULL;

-- Event Store - Event sourcing and audit trail
CREATE TABLE IF NOT EXISTS wh_event_store (
  event_id UUID NOT NULL PRIMARY KEY,
  stream_id UUID NOT NULL,
  aggregate_id UUID NOT NULL,
  aggregate_type VARCHAR(500) NOT NULL,
  event_type VARCHAR(500) NOT NULL,
  event_data JSONB NOT NULL,
  metadata JSONB NOT NULL,
  scope JSONB NULL,
  version INTEGER NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_event_store_stream ON wh_event_store (stream_id, version);
CREATE UNIQUE INDEX IF NOT EXISTS idx_event_store_aggregate ON wh_event_store (aggregate_id, version);
CREATE INDEX IF NOT EXISTS idx_event_store_aggregate_type ON wh_event_store (aggregate_type, created_at);

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

-- Perspective Cursors - Read model projection tracking (checkpoint-style)
CREATE TABLE IF NOT EXISTS wh_perspective_cursors (
  stream_id UUID NOT NULL,
  perspective_name TEXT NOT NULL,
  last_event_id UUID NOT NULL,
  status SMALLINT NOT NULL DEFAULT 0,
  processed_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
  error TEXT NULL
,
  CONSTRAINT pk_perspective_cursors PRIMARY KEY (stream_id, perspective_name)
);

CREATE INDEX IF NOT EXISTS idx_perspective_cursors_perspective_name ON wh_perspective_cursors (perspective_name);
CREATE INDEX IF NOT EXISTS idx_perspective_cursors_last_event_id ON wh_perspective_cursors (last_event_id);

-- Message Associations - Message type to consumer mappings
CREATE TABLE IF NOT EXISTS wh_message_associations (
  id UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
  message_type VARCHAR(500) NOT NULL,
  association_type VARCHAR(50) NOT NULL,
  target_name VARCHAR(500) NOT NULL,
  service_name VARCHAR(500) NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
,
  CONSTRAINT uq_message_association UNIQUE (message_type, association_type, target_name, service_name)
);

CREATE INDEX IF NOT EXISTS idx_message_associations_message_type ON wh_message_associations (message_type);
CREATE INDEX IF NOT EXISTS idx_message_associations_association_type ON wh_message_associations (association_type);
CREATE INDEX IF NOT EXISTS idx_message_associations_target_name ON wh_message_associations (target_name);
CREATE INDEX IF NOT EXISTS idx_message_associations_service_name ON wh_message_associations (service_name);
CREATE INDEX IF NOT EXISTS idx_message_associations_target_lookup ON wh_message_associations (association_type, target_name, service_name);

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


-- Event Sequence - Global sequence for event ordering across all streams
CREATE SEQUENCE IF NOT EXISTS wh_event_sequence START WITH 1 INCREMENT BY 1;

