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
CREATE TABLE IF NOT EXISTS wb_event_store (
  event_id UUID NOT NULL PRIMARY KEY,
  stream_id UUID NOT NULL,
  aggregate_id UUID NOT NULL,
  aggregate_type VARCHAR(500) NOT NULL,
  event_type VARCHAR(500) NOT NULL,
  event_data JSONB NOT NULL,
  metadata JSONB NOT NULL,
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
CREATE OR REPLACE FUNCTION process_work_batch(
  p_instance_id UUID,
  p_heartbeat TIMESTAMPTZ,
  p_lease_seconds INTEGER,
  p_outbox_batch_size INTEGER,
  p_inbox_batch_size INTEGER,
  p_completed_outbox_ids UUID[],
  p_failed_outbox_ids UUID[],
  p_completed_inbox_ids UUID[],
  p_failed_inbox_ids UUID[]
)
RETURNS TABLE(
  outbox_message_id UUID,
  outbox_destination VARCHAR,
  outbox_event_type VARCHAR,
  outbox_event_data TEXT,
  outbox_metadata TEXT,
  outbox_scope TEXT,
  inbox_message_id UUID,
  inbox_handler_name VARCHAR,
  inbox_event_type VARCHAR,
  inbox_event_data TEXT,
  inbox_metadata TEXT,
  inbox_scope TEXT
) AS $$
DECLARE
  v_now TIMESTAMPTZ := CURRENT_TIMESTAMP;
  v_lease_expiry TIMESTAMPTZ := v_now + (p_lease_seconds || ' seconds')::INTERVAL;
BEGIN
  -- 1. Upsert service instance heartbeat
  INSERT INTO wb_service_instances (instance_id, heartbeat, active)
  VALUES (p_instance_id, p_heartbeat, true)
  ON CONFLICT (instance_id)
  DO UPDATE SET heartbeat = EXCLUDED.heartbeat, active = true;

  -- 2. Mark completed outbox messages as Published
  IF array_length(p_completed_outbox_ids, 1) > 0 THEN
    UPDATE wb_outbox
    SET status = 'Published',
        published_at = v_now,
        instance_id = NULL,
        lease_expiry = NULL
    WHERE message_id = ANY(p_completed_outbox_ids);
  END IF;

  -- 3. Mark failed outbox messages as Failed
  IF array_length(p_failed_outbox_ids, 1) > 0 THEN
    UPDATE wb_outbox
    SET status = 'Failed',
        attempts = attempts + 1,
        instance_id = NULL,
        lease_expiry = NULL
    WHERE message_id = ANY(p_failed_outbox_ids);
  END IF;

  -- 4. Mark completed inbox messages as Completed
  IF array_length(p_completed_inbox_ids, 1) > 0 THEN
    UPDATE wb_inbox
    SET status = 'Completed',
        processed_at = v_now,
        instance_id = NULL,
        lease_expiry = NULL
    WHERE message_id = ANY(p_completed_inbox_ids);
  END IF;

  -- 5. Mark failed inbox messages as Failed
  IF array_length(p_failed_inbox_ids, 1) > 0 THEN
    UPDATE wb_inbox
    SET status = 'Failed',
        attempts = attempts + 1,
        instance_id = NULL,
        lease_expiry = NULL
    WHERE message_id = ANY(p_failed_inbox_ids);
  END IF;

  -- 6. Claim orphaned outbox messages (expired leases)
  RETURN QUERY
  WITH claimed_outbox AS (
    UPDATE wb_outbox
    SET instance_id = p_instance_id,
        lease_expiry = v_lease_expiry,
        attempts = attempts + 1
    WHERE message_id IN (
      SELECT message_id
      FROM wb_outbox
      WHERE status = 'Publishing'
        AND (lease_expiry IS NULL OR lease_expiry < v_now)
      ORDER BY created_at
      LIMIT p_outbox_batch_size
    )
    RETURNING
      message_id,
      destination,
      event_type,
      event_data::TEXT,
      metadata::TEXT,
      scope::TEXT
  ),
  -- 7. Claim orphaned inbox messages (expired leases)
  claimed_inbox AS (
    UPDATE wb_inbox
    SET instance_id = p_instance_id,
        lease_expiry = v_lease_expiry,
        attempts = attempts + 1
    WHERE message_id IN (
      SELECT message_id
      FROM wb_inbox
      WHERE status = 'Processing'
        AND (lease_expiry IS NULL OR lease_expiry < v_now)
      ORDER BY received_at
      LIMIT p_inbox_batch_size
    )
    RETURNING
      message_id,
      handler_name,
      event_type,
      event_data::TEXT,
      metadata::TEXT,
      scope::TEXT
  )
  SELECT
    co.message_id,
    co.destination,
    co.event_type,
    co.event_data,
    co.metadata,
    co.scope,
    ci.message_id,
    ci.handler_name,
    ci.event_type,
    ci.event_data,
    ci.metadata,
    ci.scope
  FROM claimed_outbox co
  FULL OUTER JOIN claimed_inbox ci ON false; -- Cartesian product for separate result sets

END;
$$ LANGUAGE plpgsql;

