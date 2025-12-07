-- Migration 004: Create process_work_batch function for lease-based coordination
-- Date: 2025-12-02
-- Updated: 2025-12-04 - Added instance management and stale cleanup
-- Updated: 2025-12-06 - Complete rewrite for partition-based stream ordering
-- Description: Single SQL function that handles all work coordination operations:
--              - Register/update instance with real metadata
--              - Clean up stale instances (expired heartbeats)
--              - Mark completed/failed messages (outbox and inbox separately)
--              - Claim orphaned work (expired leases)
--              - Return orphaned work to process
--              - Partition-based stream ordering with fair work distribution
--              - Event store integration for events
--              - Granular status tracking with MessageProcessingStatus flags
--              This minimizes database round-trips and ensures atomic operations

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
  p_max_partitions_per_instance INTEGER DEFAULT NULL  -- NULL = no limit, or set explicitly for load balancing
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
#variable_conflict use_column
DECLARE
  v_now TIMESTAMPTZ := CURRENT_TIMESTAMP;
  v_lease_expiry TIMESTAMPTZ := v_now + (p_lease_seconds || ' seconds')::INTERVAL;
  v_stale_cutoff TIMESTAMPTZ := v_now - (p_stale_threshold_seconds || ' seconds')::INTERVAL;
  v_debug_mode BOOLEAN := (p_flags & 4) = 4;  -- DebugMode flag
  v_new_msg RECORD;
  v_partition INTEGER;
  v_completion RECORD;
  v_failure RECORD;
  v_current_status INTEGER;
  v_active_instance_count INTEGER;
  v_dynamic_max_partitions INTEGER;
  v_fair_share INTEGER;
BEGIN
  -- 1. Register/update this instance with heartbeat
  INSERT INTO wh_service_instances (
    instance_id, service_name, host_name, process_id, started_at, last_heartbeat_at, metadata
  ) VALUES (
    p_instance_id, p_service_name, p_host_name, p_process_id, v_now, v_now, p_metadata
  )
  ON CONFLICT (instance_id) DO UPDATE SET
    last_heartbeat_at = v_now,
    metadata = COALESCE(EXCLUDED.metadata, wh_service_instances.metadata);

  -- 2. Clean up stale instances
  DELETE FROM wh_service_instances
  WHERE last_heartbeat_at < v_stale_cutoff AND instance_id != p_instance_id;

  -- Partition assignments will cascade delete automatically (ON DELETE CASCADE)

  -- 2.5. Calculate dynamic max partitions based on active instance count
  -- This ensures single instances own all partitions, while multiple instances share evenly
  SELECT COUNT(*) INTO v_active_instance_count
  FROM wh_service_instances
  WHERE last_heartbeat_at >= v_stale_cutoff;

  -- Ensure at least 1 instance (this instance just registered/updated)
  v_active_instance_count := GREATEST(v_active_instance_count, 1);

  -- Calculate fair share per instance
  v_fair_share := CEIL(p_partition_count::NUMERIC / v_active_instance_count::NUMERIC)::INTEGER;

  -- Apply explicit limit if provided (for load balancing), otherwise use fair share
  -- When maxPartitionsPerInstance is NULL (default): single instance claims all needed partitions
  -- When explicitly set (e.g., testing with maxPartitionsPerInstance: 10): enforces load balancing
  IF p_max_partitions_per_instance IS NOT NULL THEN
    v_dynamic_max_partitions := LEAST(p_max_partitions_per_instance, v_fair_share);
  ELSE
    v_dynamic_max_partitions := v_fair_share;
  END IF;

  -- 3. Update heartbeat for already-owned partitions
  UPDATE wh_partition_assignments
  SET last_heartbeat = v_now
  WHERE instance_id = p_instance_id;

  -- 4. Process completions (with status pairing)
  IF jsonb_array_length(p_outbox_completions) > 0 THEN
    FOR v_completion IN
      SELECT
        (elem->>'messageId')::UUID as msg_id,
        (elem->>'status')::INTEGER as status_flags
      FROM jsonb_array_elements(p_outbox_completions) as elem
    LOOP
      IF v_debug_mode THEN
        -- Keep completed messages, update status and timestamps
        UPDATE wh_outbox
        SET status = wh_outbox.status | v_completion.status_flags,  -- Bitwise OR to add new flags
            processed_at = v_now,
            published_at = CASE WHEN (v_completion.status_flags & 4) = 4 THEN v_now ELSE wh_outbox.published_at END,
            instance_id = NULL,
            lease_expiry = NULL
        WHERE wh_outbox.message_id = v_completion.msg_id;
      ELSE
        -- Get current status from database
        SELECT wh_outbox.status INTO v_current_status FROM wh_outbox WHERE wh_outbox.message_id = v_completion.msg_id;

        -- Delete if fully completed (both ReceptorProcessed AND PerspectiveProcessed)
        IF ((v_current_status | v_completion.status_flags) & 24) = 24 THEN
          DELETE FROM wh_outbox WHERE wh_outbox.message_id = v_completion.msg_id;
        ELSE
          -- Partially completed - update status
          UPDATE wh_outbox
          SET status = wh_outbox.status | v_completion.status_flags,
              processed_at = v_now,
              published_at = CASE WHEN (v_completion.status_flags & 4) = 4 THEN v_now ELSE wh_outbox.published_at END,
              instance_id = NULL,
              lease_expiry = NULL
          WHERE wh_outbox.message_id = v_completion.msg_id;
        END IF;
      END IF;
    END LOOP;
  END IF;

  -- Similar for inbox completions
  IF jsonb_array_length(p_inbox_completions) > 0 THEN
    FOR v_completion IN
      SELECT
        (elem->>'messageId')::UUID as msg_id,
        (elem->>'status')::INTEGER as status_flags
      FROM jsonb_array_elements(p_inbox_completions) as elem
    LOOP
      IF v_debug_mode THEN
        UPDATE wh_inbox
        SET status = wh_inbox.status | v_completion.status_flags,
            processed_at = v_now,
            instance_id = NULL,
            lease_expiry = NULL
        WHERE wh_inbox.message_id = v_completion.msg_id;
      ELSE
        -- Get current status from database
        SELECT wh_inbox.status INTO v_current_status FROM wh_inbox WHERE wh_inbox.message_id = v_completion.msg_id;

        IF ((v_current_status | v_completion.status_flags) & 24) = 24 THEN
          DELETE FROM wh_inbox WHERE wh_inbox.message_id = v_completion.msg_id;
        ELSE
          UPDATE wh_inbox
          SET status = wh_inbox.status | v_completion.status_flags,
              processed_at = v_now,
              instance_id = NULL,
              lease_expiry = NULL
          WHERE wh_inbox.message_id = v_completion.msg_id;
        END IF;
      END IF;
    END LOOP;
  END IF;

  -- 5. Process failures (with partial status tracking)
  IF jsonb_array_length(p_outbox_failures) > 0 THEN
    FOR v_failure IN
      SELECT
        (elem->>'messageId')::UUID as msg_id,
        (elem->>'completedStatus')::INTEGER as status_flags,
        elem->>'error' as error_message
      FROM jsonb_array_elements(p_outbox_failures) as elem
    LOOP
      UPDATE wh_outbox
      SET status = (wh_outbox.status | v_failure.status_flags | 32768),  -- Add completed flags + Failed flag (bit 15)
          error = v_failure.error_message,
          attempts = wh_outbox.attempts + 1,
          instance_id = NULL,
          lease_expiry = NULL
      WHERE wh_outbox.message_id = v_failure.msg_id;
    END LOOP;
  END IF;

  IF jsonb_array_length(p_inbox_failures) > 0 THEN
    FOR v_failure IN
      SELECT
        (elem->>'messageId')::UUID as msg_id,
        (elem->>'completedStatus')::INTEGER as status_flags,
        elem->>'error' as error_message
      FROM jsonb_array_elements(p_inbox_failures) as elem
    LOOP
      UPDATE wh_inbox
      SET status = (wh_inbox.status | v_failure.status_flags | 32768),
          error = v_failure.error_message,
          attempts = wh_inbox.attempts + 1,
          instance_id = NULL,
          lease_expiry = NULL
      WHERE wh_inbox.message_id = v_failure.msg_id;
    END LOOP;
  END IF;

  -- 6. Store new outbox messages (with partition assignment)
  -- Note: Outbox doesn't use deduplication table (outbox is transactional within service boundary)
  DROP TABLE IF EXISTS temp_new_outbox_ids;
  CREATE TEMP TABLE temp_new_outbox_ids (message_id UUID PRIMARY KEY) ON COMMIT DROP;

  IF jsonb_array_length(p_new_outbox_messages) > 0 THEN
    FOR v_new_msg IN
      SELECT
        (elem->>'messageId')::UUID as message_id,
        elem->>'destination' as destination,
        elem->>'eventType' as message_type,
        elem->>'eventData' as message_data,
        elem->>'metadata' as metadata,
        elem->>'scope' as scope,
        (elem->>'isEvent')::BOOLEAN as is_event,
        (elem->>'streamId')::UUID as stream_id
      FROM jsonb_array_elements(p_new_outbox_messages) as elem
    LOOP
      -- Compute partition from stream_id
      v_partition := compute_partition(v_new_msg.stream_id, p_partition_count);

      -- Store in outbox with lease
      INSERT INTO wh_outbox (
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

      -- Track newly stored outbox message
      INSERT INTO temp_new_outbox_ids (message_id) VALUES (v_new_msg.message_id);
    END LOOP;
  END IF;

  -- 7. Store new inbox messages (with partition assignment and deduplication)
  -- Uses permanent deduplication table to track which messages are truly new
  DROP TABLE IF EXISTS temp_new_inbox_ids;
  IF jsonb_array_length(p_new_inbox_messages) > 0 THEN
    -- First, record all message IDs in permanent deduplication table
    -- Only messages that are actually new will be returned
    CREATE TEMP TABLE temp_new_inbox_ids (message_id UUID PRIMARY KEY) ON COMMIT DROP;

    -- Use CTE pattern for INSERT ... RETURNING
    WITH new_msgs AS (
      INSERT INTO wh_message_deduplication (message_id, first_seen_at)
      SELECT (elem->>'messageId')::UUID, v_now
      FROM jsonb_array_elements(p_new_inbox_messages) as elem
      ON CONFLICT (message_id) DO NOTHING
      RETURNING message_id
    )
    INSERT INTO temp_new_inbox_ids (message_id)
    SELECT message_id FROM new_msgs;

    -- Now insert only the truly new messages into inbox
    FOR v_new_msg IN
      SELECT
        (elem->>'messageId')::UUID as message_id,
        elem->>'handlerName' as handler_name,
        elem->>'eventType' as message_type,
        elem->>'eventData' as message_data,
        elem->>'metadata' as metadata,
        elem->>'scope' as scope,
        (elem->>'isEvent')::BOOLEAN as is_event,
        (elem->>'streamId')::UUID as stream_id
      FROM jsonb_array_elements(p_new_inbox_messages) as elem
      WHERE (elem->>'messageId')::UUID IN (SELECT message_id FROM temp_new_inbox_ids)
    LOOP
      -- Compute partition
      v_partition := compute_partition(v_new_msg.stream_id, p_partition_count);

      -- Insert into inbox (we know it's not a duplicate)
      INSERT INTO wh_inbox (
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
        1 | CASE WHEN v_new_msg.is_event THEN 2 ELSE 0 END,  -- Stored | EventStored (if event)
        0,
        p_instance_id,
        v_lease_expiry,
        v_now
      );
    END LOOP;
  ELSE
    -- No new messages, but create empty temp table for work return query
    CREATE TEMP TABLE temp_new_inbox_ids (message_id UUID PRIMARY KEY) ON COMMIT DROP;
  END IF;

  -- 7.25. Claim partitions for newly stored messages
  -- Ensure the instance owns the partitions of newly created messages
  INSERT INTO wh_partition_assignments (partition_number, instance_id, assigned_at, last_heartbeat)
  SELECT DISTINCT partition_number, p_instance_id, v_now, v_now
  FROM wh_outbox
  WHERE instance_id = p_instance_id
    AND partition_number NOT IN (SELECT partition_number FROM wh_partition_assignments WHERE instance_id = p_instance_id)
  LIMIT v_dynamic_max_partitions - (SELECT COUNT(*) FROM wh_partition_assignments WHERE instance_id = p_instance_id)
  ON CONFLICT (partition_number) DO NOTHING;

  INSERT INTO wh_partition_assignments (partition_number, instance_id, assigned_at, last_heartbeat)
  SELECT DISTINCT partition_number, p_instance_id, v_now, v_now
  FROM wh_inbox
  WHERE instance_id = p_instance_id
    AND partition_number NOT IN (SELECT partition_number FROM wh_partition_assignments WHERE instance_id = p_instance_id)
  LIMIT v_dynamic_max_partitions - (SELECT COUNT(*) FROM wh_partition_assignments WHERE instance_id = p_instance_id)
  ON CONFLICT (partition_number) DO NOTHING;

  -- 7.4. Claim partitions for orphaned/unleased messages (with load balancing)
  -- Prioritizes claiming partitions that have actual work (orphaned messages)
  -- Respects v_dynamic_max_partitions limit for fair distribution across instances
  WITH orphaned_partitions AS (
    -- Find all partitions with orphaned/unleased work
    SELECT DISTINCT partition_number
    FROM wh_outbox
    WHERE (instance_id IS NULL OR lease_expiry IS NULL OR lease_expiry < v_now)
      AND (status & 32768) = 0  -- Not failed
      AND (status & 24) != 24    -- Not fully completed
      AND partition_number IS NOT NULL
    UNION
    SELECT DISTINCT partition_number
    FROM wh_inbox
    WHERE (instance_id IS NULL OR lease_expiry IS NULL OR lease_expiry < v_now)
      AND (status & 32768) = 0
      AND (status & 24) != 24
      AND partition_number IS NOT NULL
  ),
  available_orphaned_partitions AS (
    -- Only claim partitions not already owned by this instance or other active instances
    SELECT op.partition_number
    FROM orphaned_partitions op
    LEFT JOIN wh_partition_assignments pa ON op.partition_number = pa.partition_number
    WHERE pa.partition_number IS NULL  -- Unassigned
       OR pa.instance_id = p_instance_id  -- Already owned by this instance
       OR pa.instance_id NOT IN (  -- Owned by stale instance
         SELECT instance_id FROM wh_service_instances WHERE last_heartbeat_at >= v_stale_cutoff
       )
  ),
  partitions_to_claim AS (
    -- Use consistent hashing to determine which partitions this instance should claim
    SELECT
      partition_number,
      ROW_NUMBER() OVER (
        ORDER BY abs(hashtext(p_instance_id::TEXT || partition_number::TEXT))
      ) as priority
    FROM available_orphaned_partitions
    LIMIT v_dynamic_max_partitions - (
      SELECT COUNT(*) FROM wh_partition_assignments WHERE instance_id = p_instance_id
    )
  )
  INSERT INTO wh_partition_assignments (partition_number, instance_id, assigned_at, last_heartbeat)
  SELECT partition_number, p_instance_id, v_now, v_now
  FROM partitions_to_claim
  ON CONFLICT (partition_number) DO UPDATE SET
    instance_id = EXCLUDED.instance_id,
    assigned_at = EXCLUDED.assigned_at,
    last_heartbeat = EXCLUDED.last_heartbeat
  WHERE wh_partition_assignments.instance_id = p_instance_id
     OR wh_partition_assignments.instance_id NOT IN (
       SELECT instance_id FROM wh_service_instances WHERE last_heartbeat_at >= v_stale_cutoff
     );

  -- 7.5. Event Store Integration (Phase 7)
  -- Atomically persist events from both inbox and outbox to event store
  -- Convention: Events end with "Event" suffix and have stream_id

  -- Insert events from outbox (published events)
  -- Uses windowing function to handle multiple events in same stream within a single batch
  WITH outbox_events AS (
    SELECT
      elem,
      (elem->>'streamId')::UUID as stream_id,
      ROW_NUMBER() OVER (
        PARTITION BY (elem->>'streamId')::UUID
        ORDER BY (elem->>'messageId')::UUID
      ) as row_num
    FROM jsonb_array_elements(p_new_outbox_messages) as elem
    WHERE (elem->>'isEvent')::BOOLEAN = true
      AND (elem->>'streamId') IS NOT NULL
      AND (elem->>'eventType') LIKE '%Event'
  ),
  base_versions AS (
    SELECT
      oe.stream_id,
      oe.row_num,
      oe.elem,
      COALESCE(
        (SELECT MAX(version) FROM wh_event_store WHERE wh_event_store.stream_id = oe.stream_id),
        0
      ) as base_version
    FROM outbox_events oe
  )
  INSERT INTO wh_event_store (
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
    bv.stream_id,
    bv.stream_id,  -- For now, aggregate_id = stream_id
    -- Extract aggregate type from event_type (e.g., "Product.ProductCreatedEvent" → "Product")
    CASE
      WHEN (bv.elem->>'eventType') LIKE '%.%' THEN
        split_part(bv.elem->>'eventType', '.', -2)  -- Get second-to-last segment
      WHEN (bv.elem->>'eventType') LIKE '%Event' THEN
        regexp_replace(bv.elem->>'eventType', '([A-Z][a-z]+).*Event$', '\1')  -- Extract leading word
      ELSE 'Unknown'
    END,
    bv.elem->>'eventType',
    (bv.elem->>'eventData')::JSONB,
    (bv.elem->>'metadata')::JSONB,
    CASE WHEN (bv.elem->>'scope') IS NOT NULL THEN (bv.elem->>'scope')::JSONB ELSE NULL END,
    nextval('wh_event_sequence'),  -- Global sequence for event ordering
    bv.base_version + bv.row_num,  -- Sequential versioning within batch
    v_now
  FROM base_versions bv
  ON CONFLICT (stream_id, version) DO NOTHING;  -- Optimistic concurrency

  -- Insert events from inbox (received events)
  -- Uses windowing function to handle multiple events in same stream within a single batch
  WITH inbox_events AS (
    SELECT
      elem,
      (elem->>'streamId')::UUID as stream_id,
      ROW_NUMBER() OVER (
        PARTITION BY (elem->>'streamId')::UUID
        ORDER BY (elem->>'messageId')::UUID
      ) as row_num
    FROM jsonb_array_elements(p_new_inbox_messages) as elem
    WHERE (elem->>'isEvent')::BOOLEAN = true
      AND (elem->>'streamId') IS NOT NULL
      AND (elem->>'eventType') LIKE '%Event'
  ),
  base_versions AS (
    SELECT
      ie.stream_id,
      ie.row_num,
      ie.elem,
      COALESCE(
        (SELECT MAX(version) FROM wh_event_store WHERE wh_event_store.stream_id = ie.stream_id),
        0
      ) as base_version
    FROM inbox_events ie
  )
  INSERT INTO wh_event_store (
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
    bv.stream_id,
    bv.stream_id,
    CASE
      WHEN (bv.elem->>'eventType') LIKE '%.%' THEN
        split_part(bv.elem->>'eventType', '.', -2)
      WHEN (bv.elem->>'eventType') LIKE '%Event' THEN
        regexp_replace(bv.elem->>'eventType', '([A-Z][a-z]+).*Event$', '\1')
      ELSE 'Unknown'
    END,
    bv.elem->>'eventType',
    (bv.elem->>'eventData')::JSONB,
    (bv.elem->>'metadata')::JSONB,
    CASE WHEN (bv.elem->>'scope') IS NOT NULL THEN (bv.elem->>'scope')::JSONB ELSE NULL END,
    nextval('wh_event_sequence'),
    bv.base_version + bv.row_num,  -- Sequential versioning within batch
    v_now
  FROM base_versions bv
  ON CONFLICT (stream_id, version) DO NOTHING;

  -- 7.75. Claim unleased messages in owned partitions
  -- This ensures that when an instance owns a partition, it automatically claims
  -- any messages in that partition that aren't currently leased (orphaned or newly inserted)
  -- Track which messages are orphaned and being re-leased so we can return them as work
  DROP TABLE IF EXISTS temp_orphaned_outbox_ids;
  DROP TABLE IF EXISTS temp_orphaned_inbox_ids;
  CREATE TEMP TABLE temp_orphaned_outbox_ids (message_id UUID PRIMARY KEY) ON COMMIT DROP;
  CREATE TEMP TABLE temp_orphaned_inbox_ids (message_id UUID PRIMARY KEY) ON COMMIT DROP;

  -- Claim orphaned outbox messages and track them
  WITH orphaned AS (
    UPDATE wh_outbox
    SET instance_id = p_instance_id,
        lease_expiry = v_lease_expiry
    WHERE partition_number IN (
      SELECT partition_number FROM wh_partition_assignments WHERE instance_id = p_instance_id
    )
    AND (instance_id IS NULL OR lease_expiry IS NULL OR lease_expiry < v_now)
    AND (status & 32768) = 0  -- Not failed
    AND (status & 24) != 24  -- Not fully completed
    RETURNING message_id
  )
  INSERT INTO temp_orphaned_outbox_ids (message_id)
  SELECT message_id FROM orphaned;

  -- Claim orphaned inbox messages and track them
  WITH orphaned AS (
    UPDATE wh_inbox
    SET instance_id = p_instance_id,
        lease_expiry = v_lease_expiry
    WHERE partition_number IN (
      SELECT partition_number FROM wh_partition_assignments WHERE instance_id = p_instance_id
    )
    AND (instance_id IS NULL OR lease_expiry IS NULL OR lease_expiry < v_now)
    AND (status & 32768) = 0  -- Not failed
    AND (status & 24) != 24  -- Not fully completed
    RETURNING message_id
  )
  INSERT INTO temp_orphaned_inbox_ids (message_id)
  SELECT message_id FROM orphaned;

  -- 8. Return work from OWNED PARTITIONS ONLY, maintaining stream order
  -- Only return messages that were newly stored OR orphaned and re-leased in this call
  -- This prevents returning messages that already had a valid lease before this call
  RETURN QUERY
  WITH owned_partitions AS (
    SELECT pa.partition_number AS part_num
    FROM wh_partition_assignments pa
    WHERE pa.instance_id = p_instance_id
  )
  SELECT
    'outbox'::VARCHAR AS source,
    o.message_id AS msg_id,
    o.destination AS destination,
    o.event_type AS event_type,
    o.event_data::TEXT AS event_data,
    o.metadata::TEXT AS metadata,
    o.scope::TEXT AS scope,
    o.stream_id AS stream_id,
    o.partition_number AS partition_number,
    o.attempts AS attempts,
    o.status AS status,
    CASE
      WHEN o.message_id IN (SELECT message_id FROM temp_new_outbox_ids) THEN 1  -- NewlyStored
      ELSE 2  -- Orphaned
    END::INTEGER AS flags,
    EXTRACT(EPOCH FROM o.created_at)::BIGINT * 1000 AS sequence_order  -- Epoch ms
  FROM wh_outbox o
  INNER JOIN owned_partitions op ON o.partition_number = op.part_num
  WHERE o.instance_id = p_instance_id
    AND o.lease_expiry > v_now
    AND (o.status & 32768) = 0  -- Not failed
    AND (o.status & 24) != 24   -- Not fully completed
    AND (o.message_id IN (SELECT message_id FROM temp_new_outbox_ids) OR o.message_id IN (SELECT message_id FROM temp_orphaned_outbox_ids))  -- Only newly stored OR orphaned/re-leased
  ORDER BY o.stream_id, o.created_at;  -- CRITICAL: Stream ordering

  RETURN QUERY
  WITH owned_partitions AS (
    SELECT pa.partition_number AS part_num
    FROM wh_partition_assignments pa
    WHERE pa.instance_id = p_instance_id
  )
  SELECT
    'inbox'::VARCHAR AS source,
    i.message_id AS msg_id,
    i.handler_name AS destination,
    i.event_type AS event_type,
    i.event_data::TEXT AS event_data,
    i.metadata::TEXT AS metadata,
    i.scope::TEXT AS scope,
    i.stream_id AS stream_id,
    i.partition_number AS partition_number,
    i.attempts AS attempts,
    i.status AS status,
    CASE
      WHEN i.message_id IN (SELECT message_id FROM temp_new_inbox_ids) THEN 1  -- NewlyStored
      ELSE 2  -- Orphaned
    END::INTEGER AS flags,
    EXTRACT(EPOCH FROM i.received_at)::BIGINT * 1000 AS sequence_order
  FROM wh_inbox i
  INNER JOIN owned_partitions op ON i.partition_number = op.part_num
  WHERE i.instance_id = p_instance_id
    AND i.lease_expiry > v_now
    AND (i.status & 32768) = 0
    AND (i.status & 24) != 24
    AND (i.message_id IN (SELECT message_id FROM temp_new_inbox_ids) OR i.message_id IN (SELECT message_id FROM temp_orphaned_inbox_ids))  -- Only newly stored OR orphaned/re-leased
  ORDER BY i.stream_id, i.received_at;  -- CRITICAL: Stream ordering

END;
$$ LANGUAGE plpgsql;

-- Add comment for documentation
COMMENT ON FUNCTION process_work_batch IS 'Atomic work coordination: register/heartbeat instance, cleanup stale instances, mark completed/failed, claim orphaned work, partition-based stream ordering with event store integration';
