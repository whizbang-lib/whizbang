-- Whizbang Infrastructure Schema for Postgres
-- Generated: 2025-12-02 (Updated for lease-based coordination)
-- Infrastructure Prefix: wb_ (modern) and whizbang_ (legacy compatibility)
-- Perspective Prefix: wb_per_

-- Service Instances - Track active service instances for distributed coordination
CREATE TABLE IF NOT EXISTS wb_service_instances (
  instance_id UUID NOT NULL PRIMARY KEY,
  heartbeat TIMESTAMPTZ NOT NULL,
  active BOOLEAN NOT NULL DEFAULT true
);

CREATE INDEX IF NOT EXISTS idx_service_instances_active_heartbeat ON wb_service_instances (active, heartbeat);

-- Inbox - Message deduplication and idempotency
CREATE TABLE IF NOT EXISTS wb_inbox (
  message_id UUID NOT NULL PRIMARY KEY,
  handler_name VARCHAR(500) NOT NULL,
  event_type VARCHAR(500) NOT NULL,
  event_data JSONB NOT NULL,
  metadata JSONB NOT NULL,
  scope JSONB NULL,
  status VARCHAR(50) NOT NULL DEFAULT 'Pending',
  attempts INTEGER NOT NULL DEFAULT 0,
  error TEXT NULL,
  received_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
  processed_at TIMESTAMPTZ NULL,
  instance_id UUID NULL,
  lease_expiry TIMESTAMPTZ NULL
);

CREATE INDEX IF NOT EXISTS idx_inbox_status_received_at ON wb_inbox (status, received_at);
CREATE INDEX IF NOT EXISTS idx_inbox_processed_at ON wb_inbox (processed_at);
CREATE INDEX IF NOT EXISTS idx_inbox_lease_expiry ON wb_inbox (lease_expiry) WHERE status = 'Processing';

-- Outbox - Transactional messaging pattern with lease-based coordination
CREATE TABLE IF NOT EXISTS wb_outbox (
  message_id UUID NOT NULL PRIMARY KEY,
  destination VARCHAR(500) NOT NULL,
  event_type VARCHAR(500) NOT NULL,
  event_data JSONB NOT NULL,
  metadata JSONB NOT NULL,
  scope JSONB NULL,
  status VARCHAR(50) NOT NULL DEFAULT 'Pending',
  attempts INTEGER NOT NULL DEFAULT 0,
  error TEXT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
  published_at TIMESTAMPTZ NULL,
  instance_id UUID NULL,
  lease_expiry TIMESTAMPTZ NULL
);

CREATE INDEX IF NOT EXISTS idx_outbox_status_created_at ON wb_outbox (status, created_at);
CREATE INDEX IF NOT EXISTS idx_outbox_published_at ON wb_outbox (published_at);
CREATE INDEX IF NOT EXISTS idx_outbox_lease_expiry ON wb_outbox (lease_expiry) WHERE status = 'Publishing';

-- Event Store - Event sourcing and audit trail
-- Uses stream_id as the primary event stream identifier (preferred)
-- aggregate_id maintained for backwards compatibility
-- Uses 3-column JSONB pattern: event_data, metadata, scope
CREATE TABLE IF NOT EXISTS wb_event_store (
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

CREATE UNIQUE INDEX IF NOT EXISTS idx_event_store_stream ON wb_event_store (stream_id, version);
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

-- Legacy table name aliases for backwards compatibility
CREATE OR REPLACE VIEW whizbang_event_store AS SELECT * FROM wb_event_store;
CREATE OR REPLACE VIEW whizbang_outbox AS SELECT * FROM wb_outbox;
CREATE OR REPLACE VIEW whizbang_inbox AS SELECT * FROM wb_inbox;
CREATE OR REPLACE VIEW whizbang_request_response AS SELECT * FROM wb_request_response;
CREATE OR REPLACE VIEW whizbang_sequences AS SELECT * FROM wb_sequences;

-- Coordinated Work Processing Function
-- Atomically processes work batches with heartbeat updates, orphaned message recovery,
-- message completion, and failure tracking in a single transaction.
-- Phase 6 design: Accepts JSONB arrays for failed messages with error text.
CREATE OR REPLACE FUNCTION process_work_batch(
  p_instance_id UUID,
  p_outbox_completed_ids UUID[],
  p_outbox_failed_messages JSONB,
  p_inbox_completed_ids UUID[],
  p_inbox_failed_messages JSONB,
  p_lease_seconds INTEGER
)
RETURNS TABLE(
  source VARCHAR,
  msg_id UUID,
  destination VARCHAR,
  event_type VARCHAR,
  event_data TEXT,
  metadata TEXT,
  scope TEXT,
  attempts INTEGER
) AS $$
DECLARE
  v_now TIMESTAMPTZ := CURRENT_TIMESTAMP;
  v_lease_expiry TIMESTAMPTZ := v_now + (p_lease_seconds || ' seconds')::INTERVAL;
  v_failed_msg JSONB;
  v_msg_id UUID;
  v_error TEXT;
BEGIN
  -- 1. Upsert service instance heartbeat (generate timestamp here)
  INSERT INTO wb_service_instances (instance_id, heartbeat, active)
  VALUES (p_instance_id, v_now, true)
  ON CONFLICT (instance_id)
  DO UPDATE SET heartbeat = v_now, active = true;

  -- 2. Mark completed outbox messages as Published
  IF array_length(p_outbox_completed_ids, 1) > 0 THEN
    UPDATE wb_outbox o
    SET status = 'Published',
        published_at = v_now,
        instance_id = NULL,
        lease_expiry = NULL
    WHERE o.message_id = ANY(p_outbox_completed_ids);
  END IF;

  -- 3. Mark failed outbox messages as Failed (with error text)
  IF jsonb_array_length(p_outbox_failed_messages) > 0 THEN
    FOR v_failed_msg IN SELECT * FROM jsonb_array_elements(p_outbox_failed_messages)
    LOOP
      v_msg_id := (v_failed_msg->>'MessageId')::UUID;
      v_error := v_failed_msg->>'Error';

      UPDATE wb_outbox o
      SET status = 'Failed',
          attempts = o.attempts + 1,
          error = v_error,
          instance_id = NULL,
          lease_expiry = NULL
      WHERE o.message_id = v_msg_id;
    END LOOP;
  END IF;

  -- 4. Mark completed inbox messages as Completed
  IF array_length(p_inbox_completed_ids, 1) > 0 THEN
    UPDATE wb_inbox i
    SET status = 'Completed',
        processed_at = v_now,
        instance_id = NULL,
        lease_expiry = NULL
    WHERE i.message_id = ANY(p_inbox_completed_ids);
  END IF;

  -- 5. Mark failed inbox messages as Failed (with error text)
  IF jsonb_array_length(p_inbox_failed_messages) > 0 THEN
    FOR v_failed_msg IN SELECT * FROM jsonb_array_elements(p_inbox_failed_messages)
    LOOP
      v_msg_id := (v_failed_msg->>'MessageId')::UUID;
      v_error := v_failed_msg->>'Error';

      UPDATE wb_inbox i
      SET status = 'Failed',
          attempts = i.attempts + 1,
          error = v_error,
          instance_id = NULL,
          lease_expiry = NULL
      WHERE i.message_id = v_msg_id;
    END LOOP;
  END IF;

  -- 6. Claim and return orphaned outbox messages
  RETURN QUERY
  WITH to_claim AS (
    SELECT message_id AS claim_id
    FROM wb_outbox
    WHERE status = 'Publishing'
      AND (lease_expiry IS NULL OR lease_expiry < v_now)
    ORDER BY created_at
    LIMIT 100
    FOR UPDATE SKIP LOCKED
  ),
  claimed_outbox AS (
    UPDATE wb_outbox o
    SET instance_id = p_instance_id,
        lease_expiry = v_lease_expiry,
        attempts = o.attempts + 1
    FROM to_claim tc
    WHERE o.message_id = tc.claim_id
    RETURNING o.message_id, o.destination, o.event_type, o.event_data::TEXT, o.metadata::TEXT, o.scope::TEXT, o.attempts
  )
  SELECT
    'outbox'::VARCHAR as source,
    co.message_id as msg_id,
    co.destination,
    co.event_type,
    co.event_data,
    co.metadata,
    co.scope,
    co.attempts
  FROM claimed_outbox co;

  -- 7. Claim and return orphaned inbox messages
  RETURN QUERY
  WITH to_claim AS (
    SELECT message_id AS claim_id
    FROM wb_inbox
    WHERE status = 'Processing'
      AND (lease_expiry IS NULL OR lease_expiry < v_now)
    ORDER BY received_at
    LIMIT 100
    FOR UPDATE SKIP LOCKED
  ),
  claimed_inbox AS (
    UPDATE wb_inbox i
    SET instance_id = p_instance_id,
        lease_expiry = v_lease_expiry,
        attempts = i.attempts + 1
    FROM to_claim tc
    WHERE i.message_id = tc.claim_id
    RETURNING i.message_id, i.event_type, i.event_data::TEXT, i.metadata::TEXT, i.scope::TEXT, i.attempts
  )
  SELECT
    'inbox'::VARCHAR as source,
    ci.message_id as msg_id,
    NULL::VARCHAR as destination,
    ci.event_type,
    ci.event_data,
    ci.metadata,
    ci.scope,
    ci.attempts
  FROM claimed_inbox ci;

END;
$$ LANGUAGE plpgsql;

