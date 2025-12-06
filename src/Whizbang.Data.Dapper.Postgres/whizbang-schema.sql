-- Whizbang Infrastructure Schema for Postgres
-- Generated: 2025-12-02 (Updated for lease-based coordination)
-- Infrastructure Prefix: wb_ (modern) and whizbang_ (legacy compatibility)
-- Perspective Prefix: wb_per_

-- Service Instances - Track active service instances for distributed coordination
CREATE TABLE IF NOT EXISTS wb_service_instances (
  instance_id UUID NOT NULL PRIMARY KEY,
  service_name VARCHAR(200) NOT NULL,
  host_name VARCHAR(200) NOT NULL,
  process_id INTEGER NOT NULL,
  started_at TIMESTAMPTZ NOT NULL,
  last_heartbeat_at TIMESTAMPTZ NOT NULL,
  metadata JSONB NULL
);

CREATE INDEX IF NOT EXISTS idx_service_instances_last_heartbeat ON wb_service_instances (last_heartbeat_at);

-- Inbox - Message deduplication and idempotency
-- Updated: 2025-12-06 - Added partitioning support for stream ordering
CREATE TABLE IF NOT EXISTS wb_inbox (
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
CREATE INDEX IF NOT EXISTS idx_inbox_partition_status ON wb_inbox (partition_number, status)
  WHERE (status & 32768) = 0 AND (status & 24) != 24;  -- Not failed AND not fully completed

CREATE INDEX IF NOT EXISTS idx_inbox_partition_stream_order ON wb_inbox (partition_number, stream_id, received_at);
CREATE INDEX IF NOT EXISTS idx_inbox_processed_at ON wb_inbox (processed_at);
CREATE INDEX IF NOT EXISTS idx_inbox_lease_expiry ON wb_inbox (lease_expiry) WHERE (status & 24) != 24;

-- Outbox - Transactional messaging pattern with lease-based coordination
-- Updated: 2025-12-06 - Added partitioning support for stream ordering
CREATE TABLE IF NOT EXISTS wb_outbox (
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
CREATE INDEX IF NOT EXISTS idx_outbox_partition_status ON wb_outbox (partition_number, status)
  WHERE (status & 32768) = 0 AND (status & 24) != 24;  -- Not failed AND not fully completed

CREATE INDEX IF NOT EXISTS idx_outbox_partition_stream_order ON wb_outbox (partition_number, stream_id, created_at);
CREATE INDEX IF NOT EXISTS idx_outbox_published_at ON wb_outbox (published_at);
CREATE INDEX IF NOT EXISTS idx_outbox_lease_expiry ON wb_outbox (lease_expiry) WHERE (status & 24) != 24;

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

CREATE UNIQUE INDEX IF NOT EXISTS idx_request_response_correlation ON wb_request_response (correlation_id);
CREATE INDEX IF NOT EXISTS idx_request_response_status_created ON wb_request_response (status, created_at);
CREATE INDEX IF NOT EXISTS idx_request_response_expires ON wb_request_response (expires_at);

-- Sequences - Distributed sequence generation
CREATE TABLE IF NOT EXISTS wb_sequences (
  sequence_name VARCHAR(200) NOT NULL PRIMARY KEY,
  current_value BIGINT NOT NULL DEFAULT 0,
  increment_by INTEGER NOT NULL DEFAULT 1,
  last_updated_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- Event Store Sequence - Global sequence for event ordering across all streams
CREATE SEQUENCE IF NOT EXISTS wb_event_sequence START WITH 1 INCREMENT BY 1;

-- Partition Assignments - Track which partitions are owned by which service instances
-- Updated: 2025-12-06 - Partition-based stream ordering support
CREATE TABLE IF NOT EXISTS wb_partition_assignments (
  partition_number INTEGER NOT NULL PRIMARY KEY,
  instance_id UUID NOT NULL REFERENCES wb_service_instances(instance_id) ON DELETE CASCADE,
  assigned_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
  last_heartbeat TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_partition_instance ON wb_partition_assignments(instance_id);
CREATE INDEX IF NOT EXISTS idx_partition_heartbeat ON wb_partition_assignments(last_heartbeat);

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
CREATE OR REPLACE VIEW whizbang_event_store AS SELECT * FROM wb_event_store;
CREATE OR REPLACE VIEW whizbang_outbox AS SELECT * FROM wb_outbox;
CREATE OR REPLACE VIEW whizbang_inbox AS SELECT * FROM wb_inbox;
CREATE OR REPLACE VIEW whizbang_request_response AS SELECT * FROM wb_request_response;
CREATE OR REPLACE VIEW whizbang_sequences AS SELECT * FROM wb_sequences;

-- Coordinated Work Processing Function with Partitioning and Stream Ordering
-- Atomically processes work batches with partition assignment, event store integration,
-- and granular status tracking in a single transaction.
-- Updated: 2025-12-06 - Complete rewrite for partition-based stream ordering
CREATE OR REPLACE FUNCTION process_work_batch(
  -- Instance identification
  p_instance_id UUID,
  p_service_name VARCHAR(200),
  p_host_name VARCHAR(200),
  p_process_id INTEGER,
  p_metadata JSONB DEFAULT NULL,

  -- Completion tracking (with status pairing)
  p_outbox_completions JSONB DEFAULT '[]'::JSONB,  -- [{"message_id": "uuid", "status": 12}, ...]
  p_outbox_failures JSONB DEFAULT '[]'::JSONB,     -- [{"message_id": "uuid", "status": 8, "error": "..."}, ...]
  p_inbox_completions JSONB DEFAULT '[]'::JSONB,
  p_inbox_failures JSONB DEFAULT '[]'::JSONB,

  -- Immediate processing support
  p_new_outbox_messages JSONB DEFAULT '[]'::JSONB,  -- Array of new messages to store
  p_new_inbox_messages JSONB DEFAULT '[]'::JSONB,

  -- Configuration
  p_lease_seconds INTEGER DEFAULT 300,
  p_stale_threshold_seconds INTEGER DEFAULT 600,
  p_flags INTEGER DEFAULT 0,  -- WorkBatchFlags
  p_partition_count INTEGER DEFAULT 10000,
  p_max_partitions_per_instance INTEGER DEFAULT 100
)
RETURNS TABLE(
  source VARCHAR,
  msg_id UUID,
  destination VARCHAR,
  event_type VARCHAR,
  event_data TEXT,
  metadata TEXT,
  scope TEXT,
  stream_id UUID,
  partition_number INTEGER,
  attempts INTEGER,
  status INTEGER,  -- MessageProcessingStatus flags
  flags INTEGER,   -- WorkBatchFlags
  sequence_order BIGINT
) AS $$
DECLARE
  v_now TIMESTAMPTZ := CURRENT_TIMESTAMP;
  v_lease_expiry TIMESTAMPTZ := v_now + (p_lease_seconds || ' seconds')::INTERVAL;
  v_stale_cutoff TIMESTAMPTZ := v_now - (p_stale_threshold_seconds || ' seconds')::INTERVAL;
  v_debug_mode BOOLEAN := (p_flags & 4) = 4;  -- DebugMode flag
  v_new_msg RECORD;
  v_partition INTEGER;
  v_completion RECORD;
  v_failure RECORD;
BEGIN
  -- 1. Register/update this instance with heartbeat
  INSERT INTO wb_service_instances (
    instance_id, service_name, host_name, process_id, started_at, last_heartbeat_at, metadata
  ) VALUES (
    p_instance_id, p_service_name, p_host_name, p_process_id, v_now, v_now, p_metadata
  )
  ON CONFLICT (instance_id) DO UPDATE SET
    last_heartbeat_at = v_now,
    metadata = COALESCE(EXCLUDED.metadata, wb_service_instances.metadata);

  -- 2. Clean up stale instances
  DELETE FROM wb_service_instances
  WHERE last_heartbeat_at < v_stale_cutoff AND instance_id != p_instance_id;

  -- Partition assignments will cascade delete automatically (ON DELETE CASCADE)

  -- 3. Claim partitions for this instance (consistent hashing)
  WITH available_partitions AS (
    SELECT gs.partition_num
    FROM generate_series(0, p_partition_count - 1) AS gs(partition_num)
    WHERE gs.partition_num NOT IN (SELECT partition_number FROM wb_partition_assignments)
    LIMIT p_max_partitions_per_instance
  ),
  partitions_to_claim AS (
    SELECT
      partition_num,
      ROW_NUMBER() OVER (
        ORDER BY abs(hashtext(p_instance_id::TEXT || partition_num::TEXT))
      ) as priority
    FROM available_partitions
    LIMIT p_max_partitions_per_instance - (
      SELECT COUNT(*) FROM wb_partition_assignments WHERE instance_id = p_instance_id
    )
  )
  INSERT INTO wb_partition_assignments (partition_number, instance_id, assigned_at, last_heartbeat)
  SELECT partition_num, p_instance_id, v_now, v_now
  FROM partitions_to_claim
  ON CONFLICT (partition_number) DO NOTHING;

  -- Update heartbeat for already-owned partitions
  UPDATE wb_partition_assignments
  SET last_heartbeat = v_now
  WHERE instance_id = p_instance_id;

  -- 4. Process completions (with status pairing)
  IF jsonb_array_length(p_outbox_completions) > 0 THEN
    FOR v_completion IN
      SELECT
        (elem->>'message_id')::UUID as message_id,
        (elem->>'status')::INTEGER as status
      FROM jsonb_array_elements(p_outbox_completions) as elem
    LOOP
      IF v_debug_mode THEN
        -- Keep completed messages, update status and timestamps
        UPDATE wb_outbox
        SET status = status | v_completion.status,  -- Bitwise OR to add new flags
            processed_at = v_now,
            published_at = CASE WHEN (v_completion.status & 4) = 4 THEN v_now ELSE published_at END,
            instance_id = NULL,
            lease_expiry = NULL
        WHERE message_id = v_completion.message_id;
      ELSE
        -- Delete if fully completed (both ReceptorProcessed AND PerspectiveProcessed)
        IF ((status | v_completion.status) & 24) = 24 THEN
          DELETE FROM wb_outbox WHERE message_id = v_completion.message_id;
        ELSE
          -- Partially completed - update status
          UPDATE wb_outbox
          SET status = status | v_completion.status,
              processed_at = v_now,
              published_at = CASE WHEN (v_completion.status & 4) = 4 THEN v_now ELSE published_at END,
              instance_id = NULL,
              lease_expiry = NULL
          WHERE message_id = v_completion.message_id;
        END IF;
      END IF;
    END LOOP;
  END IF;

  -- Similar for inbox completions
  IF jsonb_array_length(p_inbox_completions) > 0 THEN
    FOR v_completion IN
      SELECT
        (elem->>'message_id')::UUID as message_id,
        (elem->>'status')::INTEGER as status
      FROM jsonb_array_elements(p_inbox_completions) as elem
    LOOP
      IF v_debug_mode THEN
        UPDATE wb_inbox
        SET status = status | v_completion.status,
            processed_at = v_now,
            instance_id = NULL,
            lease_expiry = NULL
        WHERE message_id = v_completion.message_id;
      ELSE
        IF ((status | v_completion.status) & 24) = 24 THEN
          DELETE FROM wb_inbox WHERE message_id = v_completion.message_id;
        ELSE
          UPDATE wb_inbox
          SET status = status | v_completion.status,
              processed_at = v_now,
              instance_id = NULL,
              lease_expiry = NULL
          WHERE message_id = v_completion.message_id;
        END IF;
      END IF;
    END LOOP;
  END IF;

  -- 5. Process failures (with partial status tracking)
  IF jsonb_array_length(p_outbox_failures) > 0 THEN
    FOR v_failure IN
      SELECT
        (elem->>'message_id')::UUID as message_id,
        (elem->>'status')::INTEGER as status,
        elem->>'error' as error
      FROM jsonb_array_elements(p_outbox_failures) as elem
    LOOP
      UPDATE wb_outbox
      SET status = (status | v_failure.status | 32768),  -- Add completed flags + Failed flag (bit 15)
          error = v_failure.error,
          attempts = attempts + 1,
          instance_id = NULL,
          lease_expiry = NULL
      WHERE message_id = v_failure.message_id;
    END LOOP;
  END IF;

  IF jsonb_array_length(p_inbox_failures) > 0 THEN
    FOR v_failure IN
      SELECT
        (elem->>'message_id')::UUID as message_id,
        (elem->>'status')::INTEGER as status,
        elem->>'error' as error
      FROM jsonb_array_elements(p_inbox_failures) as elem
    LOOP
      UPDATE wb_inbox
      SET status = (status | v_failure.status | 32768),
          error = v_failure.error,
          attempts = attempts + 1,
          instance_id = NULL,
          lease_expiry = NULL
      WHERE message_id = v_failure.message_id;
    END LOOP;
  END IF;

  -- 6. Store new outbox messages (with partition assignment)
  IF jsonb_array_length(p_new_outbox_messages) > 0 THEN
    FOR v_new_msg IN
      SELECT
        (elem->>'message_id')::UUID as message_id,
        elem->>'destination' as destination,
        elem->>'message_type' as message_type,
        elem->>'message_data' as message_data,
        elem->>'metadata' as metadata,
        elem->>'scope' as scope,
        (elem->>'is_event')::BOOLEAN as is_event,
        (elem->>'stream_id')::UUID as stream_id
      FROM jsonb_array_elements(p_new_outbox_messages) as elem
    LOOP
      -- Compute partition from stream_id
      v_partition := compute_partition(v_new_msg.stream_id, p_partition_count);

      -- Store in outbox with lease
      INSERT INTO wb_outbox (
        message_id, destination, event_type, event_data, metadata, scope,
        stream_id, partition_number,
        status, attempts, instance_id, lease_expiry, created_at
      ) VALUES (
        v_new_msg.message_id,
        v_new_msg.destination,
        v_new_msg.message_type,
        v_new_msg.message_data::JSONB,
        v_new_msg.metadata::JSONB,
        CASE WHEN v_new_msg.scope IS NOT NULL THEN v_new_msg.scope::JSONB ELSE NULL END,
        v_new_msg.stream_id,
        v_partition,
        1 | CASE WHEN v_new_msg.is_event THEN 2 ELSE 0 END,  -- Stored | EventStored (if event)
        0,
        p_instance_id,
        v_lease_expiry,
        v_now
      );
    END LOOP;
  END IF;

  -- 7. Store new inbox messages (with partition assignment and deduplication)
  IF jsonb_array_length(p_new_inbox_messages) > 0 THEN
    FOR v_new_msg IN
      SELECT
        (elem->>'message_id')::UUID as message_id,
        elem->>'handler_name' as handler_name,
        elem->>'message_type' as message_type,
        elem->>'message_data' as message_data,
        elem->>'metadata' as metadata,
        elem->>'scope' as scope,
        (elem->>'is_event')::BOOLEAN as is_event,
        (elem->>'stream_id')::UUID as stream_id
      FROM jsonb_array_elements(p_new_inbox_messages) as elem
    LOOP
      -- Compute partition
      v_partition := compute_partition(v_new_msg.stream_id, p_partition_count);

      -- Atomic deduplication via ON CONFLICT
      INSERT INTO wb_inbox (
        message_id, handler_name, event_type, event_data, metadata, scope,
        stream_id, partition_number,
        status, attempts, instance_id, lease_expiry, received_at
      ) VALUES (
        v_new_msg.message_id,
        v_new_msg.handler_name,
        v_new_msg.message_type,
        v_new_msg.message_data::JSONB,
        v_new_msg.metadata::JSONB,
        CASE WHEN v_new_msg.scope IS NOT NULL THEN v_new_msg.scope::JSONB ELSE NULL END,
        v_new_msg.stream_id,
        v_partition,
        1,  -- Stored
        0,
        p_instance_id,
        v_lease_expiry,
        v_now
      )
      ON CONFLICT (message_id) DO NOTHING;  -- Idempotent deduplication!
    END LOOP;
  END IF;

  -- 7.5. Event Store Integration (Phase 7)
  -- Atomically persist events from both inbox and outbox to event store
  -- Convention: Events end with "Event" suffix and have stream_id

  -- Insert events from outbox (published events)
  INSERT INTO wb_event_store (
    event_id,
    stream_id,
    aggregate_id,
    aggregate_type,
    event_type,
    event_data,
    metadata,
    scope,
    sequence_number,
    version,
    created_at
  )
  SELECT
    gen_random_uuid(),  -- Generate new event ID
    (elem->>'stream_id')::UUID,
    (elem->>'stream_id')::UUID,  -- For now, aggregate_id = stream_id
    -- Extract aggregate type from event_type (e.g., "Product.ProductCreatedEvent" → "Product")
    CASE
      WHEN (elem->>'message_type') LIKE '%.%' THEN
        split_part(elem->>'message_type', '.', -2)  -- Get second-to-last segment
      WHEN (elem->>'message_type') LIKE '%Event' THEN
        regexp_replace(elem->>'message_type', '([A-Z][a-z]+).*Event$', '\1')  -- Extract leading word
      ELSE 'Unknown'
    END,
    elem->>'message_type',
    (elem->>'message_data')::JSONB,
    (elem->>'metadata')::JSONB,
    CASE WHEN (elem->>'scope') IS NOT NULL THEN (elem->>'scope')::JSONB ELSE NULL END,
    nextval('wb_event_sequence'),  -- Global sequence for event ordering
    COALESCE(
      (SELECT MAX(version) + 1 FROM wb_event_store WHERE stream_id = (elem->>'stream_id')::UUID),
      1
    ),  -- Auto-increment version per stream
    v_now
  FROM jsonb_array_elements(p_new_outbox_messages) as elem
  WHERE (elem->>'is_event')::BOOLEAN = true
    AND (elem->>'stream_id') IS NOT NULL
    AND (elem->>'message_type') LIKE '%Event'  -- Convention: events end with "Event"
  ON CONFLICT (stream_id, version) DO NOTHING;  -- Optimistic concurrency

  -- Insert events from inbox (received events)
  INSERT INTO wb_event_store (
    event_id,
    stream_id,
    aggregate_id,
    aggregate_type,
    event_type,
    event_data,
    metadata,
    scope,
    sequence_number,
    version,
    created_at
  )
  SELECT
    gen_random_uuid(),
    (elem->>'stream_id')::UUID,
    (elem->>'stream_id')::UUID,
    CASE
      WHEN (elem->>'message_type') LIKE '%.%' THEN
        split_part(elem->>'message_type', '.', -2)
      WHEN (elem->>'message_type') LIKE '%Event' THEN
        regexp_replace(elem->>'message_type', '([A-Z][a-z]+).*Event$', '\1')
      ELSE 'Unknown'
    END,
    elem->>'message_type',
    (elem->>'message_data')::JSONB,
    (elem->>'metadata')::JSONB,
    CASE WHEN (elem->>'scope') IS NOT NULL THEN (elem->>'scope')::JSONB ELSE NULL END,
    nextval('wb_event_sequence'),
    COALESCE(
      (SELECT MAX(version) + 1 FROM wb_event_store WHERE stream_id = (elem->>'stream_id')::UUID),
      1
    ),
    v_now
  FROM jsonb_array_elements(p_new_inbox_messages) as elem
  WHERE (elem->>'is_event')::BOOLEAN = true
    AND (elem->>'stream_id') IS NOT NULL
    AND (elem->>'message_type') LIKE '%Event'
  ON CONFLICT (stream_id, version) DO NOTHING;

  -- 8. Return work from OWNED PARTITIONS ONLY, maintaining stream order
  RETURN QUERY
  WITH owned_partitions AS (
    SELECT partition_number FROM wb_partition_assignments WHERE instance_id = p_instance_id
  )
  SELECT
    'outbox'::VARCHAR,
    o.message_id,
    o.destination,
    o.event_type,
    o.event_data::TEXT,
    o.metadata::TEXT,
    o.scope::TEXT,
    o.stream_id,
    o.partition_number,
    o.attempts,
    o.status,
    CASE
      WHEN o.message_id IN (
        SELECT (jsonb_array_elements(p_new_outbox_messages)->>'message_id')::UUID
      ) THEN 1  -- NewlyStored
      ELSE 2    -- Orphaned
    END::INTEGER,
    EXTRACT(EPOCH FROM o.created_at)::BIGINT * 1000  -- Epoch ms
  FROM wb_outbox o
  WHERE o.partition_number IN (SELECT partition_number FROM owned_partitions)
    AND o.instance_id = p_instance_id
    AND o.lease_expiry > v_now
    AND (o.status & 32768) = 0  -- Not failed
    AND (o.status & 24) != 24   -- Not fully completed
  ORDER BY o.stream_id, o.created_at;  -- CRITICAL: Stream ordering

  RETURN QUERY
  WITH owned_partitions AS (
    SELECT partition_number FROM wb_partition_assignments WHERE instance_id = p_instance_id
  )
  SELECT
    'inbox'::VARCHAR,
    i.message_id,
    i.handler_name,
    i.event_type,
    i.event_data::TEXT,
    i.metadata::TEXT,
    i.scope::TEXT,
    i.stream_id,
    i.partition_number,
    i.attempts,
    i.status,
    CASE
      WHEN i.message_id IN (
        SELECT (jsonb_array_elements(p_new_inbox_messages)->>'message_id')::UUID
      ) THEN 1
      ELSE 2
    END::INTEGER,
    EXTRACT(EPOCH FROM i.received_at)::BIGINT * 1000
  FROM wb_inbox i
  WHERE i.partition_number IN (SELECT partition_number FROM owned_partitions)
    AND i.instance_id = p_instance_id
    AND i.lease_expiry > v_now
    AND (i.status & 32768) = 0
    AND (i.status & 24) != 24
  ORDER BY i.stream_id, i.received_at;  -- CRITICAL: Stream ordering

END;
$$ LANGUAGE plpgsql;

