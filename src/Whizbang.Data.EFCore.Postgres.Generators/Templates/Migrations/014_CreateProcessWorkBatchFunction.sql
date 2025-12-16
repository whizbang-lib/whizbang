-- Migration 004: Create process_work_batch function for lease-based coordination
-- Date: 2025-12-02
-- Updated: 2025-12-04 - Added instance management and stale cleanup
-- Updated: 2025-12-06 - Complete rewrite for partition-based stream ordering
-- Updated: 2025-12-15 - Fix NULL metadata constraint (COALESCE empty Hops array)
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

-- Drop existing function to ensure clean replacement
DROP FUNCTION IF EXISTS process_work_batch;

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

  -- Receptor processing completions
  p_receptor_completions JSONB DEFAULT '[]'::JSONB,  -- [{"event_id": "uuid", "receptor_name": "...", "status": 2}, ...]
  p_receptor_failures JSONB DEFAULT '[]'::JSONB,     -- [{"event_id": "uuid", "receptor_name": "...", "status": 4, "error": "..."}, ...]

  -- Perspective checkpoint completions
  p_perspective_completions JSONB DEFAULT '[]'::JSONB,  -- [{"stream_id": "uuid", "perspective_name": "...", "last_event_id": "uuid", "status": 2}, ...]
  p_perspective_failures JSONB DEFAULT '[]'::JSONB,     -- [{"stream_id": "uuid", "perspective_name": "...", "last_event_id": "uuid", "status": 4, "error": "..."}, ...]

  -- Immediate processing support
  p_new_outbox_messages JSONB DEFAULT '[]'::JSONB,  -- Array of new messages to store
  p_new_inbox_messages JSONB DEFAULT '[]'::JSONB,

  -- Lease renewal (for buffering messages awaiting transport readiness)
  p_renew_outbox_lease_ids JSONB DEFAULT '[]'::JSONB,  -- ["uuid1", "uuid2", ...]
  p_renew_inbox_lease_ids JSONB DEFAULT '[]'::JSONB,

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
  envelope_type VARCHAR,  -- Assembly-qualified envelope type name
  envelope_data TEXT,  -- Complete serialized envelope JSON
  metadata TEXT,
  scope TEXT,
  stream_uuid UUID,
  partition_num INTEGER,
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
  v_current_status INTEGER;
  v_active_instance_count INTEGER;
  v_dynamic_max_partitions INTEGER;
  v_fair_share INTEGER;
  -- Arrays to track message IDs instead of temp tables
  v_new_outbox_ids UUID[] := '{}';
  v_new_inbox_ids UUID[] := '{}';
  v_orphaned_outbox_ids UUID[] := '{}';
  v_orphaned_inbox_ids UUID[] := '{}';
  -- Debug logging variables
  v_before_instance_id UUID;
  v_before_lease_expiry TIMESTAMPTZ;
  v_before_status INTEGER;
  v_after_instance_id UUID;
  v_after_lease_expiry TIMESTAMPTZ;
  v_after_status INTEGER;

BEGIN
  -- DEBUG: Migration version identifier
  RAISE NOTICE '====  MIGRATION VERSION: 2025-12-15_NULL_METADATA_FIX ===';

  -- DEBUG: Log input parameters for completions
  RAISE NOTICE '=== SECTION 0: INPUT PARAMETERS ===';
  RAISE NOTICE 'p_outbox_completions: %', p_outbox_completions;
  RAISE NOTICE 'p_outbox_failures: %', p_outbox_failures;
  RAISE NOTICE 'p_new_outbox_messages count: %', jsonb_array_length(p_new_outbox_messages);

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
  RAISE NOTICE '=== SECTION 4: PROCESS COMPLETIONS ===';
  IF jsonb_array_length(p_outbox_completions) > 0 THEN
    FOR v_completion IN
      SELECT
        (elem->>'MessageId')::UUID as msg_id,
        (elem->>'Status')::INTEGER as status_flags
      FROM jsonb_array_elements(p_outbox_completions) as elem
    LOOP
      RAISE NOTICE 'Processing completion: messageId=%, status=%', v_completion.msg_id, v_completion.status_flags;

      IF v_debug_mode THEN
        -- Keep completed messages, update status and timestamps
        UPDATE wh_outbox AS o
        SET status = o.status | v_completion.status_flags,  -- Bitwise OR to add new flags
            processed_at = v_now,
            published_at = CASE WHEN (v_completion.status_flags & 4) = 4 THEN v_now ELSE o.published_at END,
            instance_id = NULL,
            lease_expiry = NULL
        WHERE o.message_id = v_completion.msg_id;
      ELSE
        -- Get current status from database
        SELECT wh_outbox.status INTO v_current_status FROM wh_outbox WHERE wh_outbox.message_id = v_completion.msg_id;

        -- Delete if Published (outbox messages are done once published to transport)
        IF ((v_current_status | v_completion.status_flags) & 4) = 4 THEN
          DELETE FROM wh_outbox WHERE wh_outbox.message_id = v_completion.msg_id;
          RAISE NOTICE 'DELETED outbox message (published): messageId=%', v_completion.msg_id;
        ELSE
          -- Partially completed - update status
          UPDATE wh_outbox AS o
          SET status = o.status | v_completion.status_flags,
              processed_at = v_now,
              published_at = CASE WHEN (v_completion.status_flags & 4) = 4 THEN v_now ELSE o.published_at END,
              instance_id = NULL,
              lease_expiry = NULL
          WHERE o.message_id = v_completion.msg_id;

          RAISE NOTICE 'Updated message (partially completed): messageId=%', v_completion.msg_id;
        END IF;
      END IF;
    END LOOP;
  END IF;

  RAISE NOTICE '=== SECTION 4 COMPLETE: All completions processed ===' ;

  -- Similar for inbox completions
  IF jsonb_array_length(p_inbox_completions) > 0 THEN
    FOR v_completion IN
      SELECT
        (elem->>'MessageId')::UUID as msg_id,
        (elem->>'Status')::INTEGER as status_flags
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

        -- Delete if EventStored (inbox messages are done once event is stored and handlers invoked)
        IF ((v_current_status | v_completion.status_flags) & 2) = 2 THEN
          DELETE FROM wh_inbox WHERE wh_inbox.message_id = v_completion.msg_id;
          RAISE NOTICE 'DELETED inbox message (event stored): messageId=%', v_completion.msg_id;
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
        (elem->>'MessageId')::UUID as msg_id,
        (elem->>'CompletedStatus')::INTEGER as status_flags,
        elem->>'Error' as error_message
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
        (elem->>'MessageId')::UUID as msg_id,
        (elem->>'CompletedStatus')::INTEGER as status_flags,
        elem->>'Error' as error_message
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

  -- 5.25. Process receptor processing completions
  IF jsonb_array_length(p_receptor_completions) > 0 THEN
    FOR v_completion IN
      SELECT
        (elem->>'EventId')::UUID as event_id,
        elem->>'ReceptorName' as receptor_name,
        (elem->>'Status')::SMALLINT as status
      FROM jsonb_array_elements(p_receptor_completions) as elem
    LOOP
      PERFORM complete_receptor_processing_work(
        v_completion.event_id,
        v_completion.receptor_name,
        v_completion.status,
        NULL  -- No error for successful completion
      );
    END LOOP;
  END IF;

  -- 5.3. Process receptor processing failures
  IF jsonb_array_length(p_receptor_failures) > 0 THEN
    FOR v_failure IN
      SELECT
        (elem->>'EventId')::UUID as event_id,
        elem->>'ReceptorName' as receptor_name,
        (elem->>'Status')::SMALLINT as status,
        elem->>'Error' as error_message
      FROM jsonb_array_elements(p_receptor_failures) as elem
    LOOP
      PERFORM complete_receptor_processing_work(
        v_failure.event_id,
        v_failure.receptor_name,
        v_failure.status,
        v_failure.error_message
      );
    END LOOP;
  END IF;

  -- 5.35. Process perspective checkpoint completions
  IF jsonb_array_length(p_perspective_completions) > 0 THEN
    FOR v_completion IN
      SELECT
        (elem->>'StreamId')::UUID as stream_id,
        elem->>'PerspectiveName' as perspective_name,
        (elem->>'LastEventId')::UUID as last_event_id,
        (elem->>'Status')::SMALLINT as status
      FROM jsonb_array_elements(p_perspective_completions) as elem
    LOOP
      PERFORM complete_perspective_checkpoint_work(
        v_completion.stream_id,
        v_completion.perspective_name,
        v_completion.last_event_id,
        v_completion.status,
        NULL  -- No error for successful completion
      );
    END LOOP;
  END IF;

  -- 5.4. Process perspective checkpoint failures
  IF jsonb_array_length(p_perspective_failures) > 0 THEN
    FOR v_failure IN
      SELECT
        (elem->>'StreamId')::UUID as stream_id,
        elem->>'PerspectiveName' as perspective_name,
        (elem->>'LastEventId')::UUID as last_event_id,
        (elem->>'Status')::SMALLINT as status,
        elem->>'Error' as error_message
      FROM jsonb_array_elements(p_perspective_failures) as elem
    LOOP
      PERFORM complete_perspective_checkpoint_work(
        v_failure.stream_id,
        v_failure.perspective_name,
        v_failure.last_event_id,
        v_failure.status,
        v_failure.error_message
      );
    END LOOP;
  END IF;

  -- 5.5. Renew leases for buffered messages (awaiting transport readiness)
  -- Extends lease_expiry for messages that are being held for publishing/processing
  -- without completing or failing them. This prevents lease expiration while
  -- messages are buffered (e.g., waiting for transport connection).
  IF jsonb_array_length(p_renew_outbox_lease_ids) > 0 THEN
    UPDATE wh_outbox
    SET lease_expiry = v_lease_expiry
    WHERE wh_outbox.message_id IN (
      SELECT (elem::TEXT)::UUID
      FROM jsonb_array_elements_text(p_renew_outbox_lease_ids) elem
    )
    AND wh_outbox.instance_id = p_instance_id;
  END IF;

  IF jsonb_array_length(p_renew_inbox_lease_ids) > 0 THEN
    UPDATE wh_inbox
    SET lease_expiry = v_lease_expiry
    WHERE wh_inbox.message_id IN (
      SELECT (elem::TEXT)::UUID
      FROM jsonb_array_elements_text(p_renew_inbox_lease_ids) elem
    )
    AND wh_inbox.instance_id = p_instance_id;
  END IF;

  -- 6. Store new outbox messages (with partition assignment)
  -- Note: Outbox doesn't use deduplication table (outbox is transactional within service boundary)
  RAISE NOTICE '=== SECTION 6: STORE NEW OUTBOX MESSAGES ===';
  IF jsonb_array_length(p_new_outbox_messages) > 0 THEN
    FOR v_new_msg IN
      SELECT
        (elem->>'MessageId')::UUID as message_id,
        elem->>'Destination' as destination,
        elem->>'MessageType' as message_type,
        elem->>'EnvelopeType' as envelope_type,
        (elem->'Envelope')::TEXT as envelope_data,  -- Extract envelope sub-object and serialize as TEXT
        COALESCE((elem->'Envelope'->'Hops'->0)::TEXT, '{}') as metadata,  -- Extract first hop as metadata (use -> not ->> to get object), default to empty object
        NULL::TEXT as scope,  -- Scope is not currently used in OutboxMessage
        (elem->>'IsEvent')::BOOLEAN as is_event,
        (elem->>'StreamId')::UUID as stream_id
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
        v_new_msg.envelope_type,  -- Store envelope type for deserialization
        v_new_msg.envelope_data::JSONB,  -- Store complete envelope data
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

      -- Track newly stored outbox message in array
      v_new_outbox_ids := array_append(v_new_outbox_ids, v_new_msg.message_id);
      RAISE NOTICE 'Stored new outbox message: %, v_new_outbox_ids now: %', v_new_msg.message_id, v_new_outbox_ids;
    END LOOP;
  END IF;

  RAISE NOTICE 'Final v_new_outbox_ids: %, cardinality: %', v_new_outbox_ids, cardinality(v_new_outbox_ids);

  -- 7. Store new inbox messages (with partition assignment and deduplication)
  -- Uses permanent deduplication table to track which messages are truly new
  IF jsonb_array_length(p_new_inbox_messages) > 0 THEN
    -- First, record all message IDs in permanent deduplication table
    -- Only messages that are actually new will be returned
    WITH new_msgs AS (
      INSERT INTO wh_message_deduplication (message_id, first_seen_at)
      SELECT (elem->>'MessageId')::UUID, v_now
      FROM jsonb_array_elements(p_new_inbox_messages) as elem
      ON CONFLICT (message_id) DO NOTHING
      RETURNING message_id
    )
    SELECT array_agg(message_id) INTO v_new_inbox_ids FROM new_msgs;

    -- Handle NULL case (no new messages, all were duplicates)
    v_new_inbox_ids := COALESCE(v_new_inbox_ids, '{}');

    -- Now insert only the truly new messages into inbox
    FOR v_new_msg IN
      SELECT
        (elem->>'MessageId')::UUID as message_id,
        elem->>'HandlerName' as handler_name,
        elem->>'MessageType' as message_type,
        elem->>'EnvelopeType' as envelope_type,
        (elem->'Envelope')::TEXT as envelope_data,  -- Extract envelope sub-object and serialize as TEXT
        COALESCE((elem->'Envelope'->'Hops'->0)::TEXT, '{}') as metadata,  -- Extract first hop as metadata (use -> not ->> to get object), default to empty object
        NULL::TEXT as scope,  -- Scope is not currently used in InboxMessage
        (elem->>'IsEvent')::BOOLEAN as is_event,
        (elem->>'StreamId')::UUID as stream_id
      FROM jsonb_array_elements(p_new_inbox_messages) as elem
      WHERE (elem->>'MessageId')::UUID = ANY(v_new_inbox_ids)
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
        v_new_msg.envelope_type,  -- Store envelope type for deserialization
        v_new_msg.envelope_data::JSONB,  -- Store complete envelope data
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

  -- 7.25. Claim partitions for newly stored messages
  -- Ensure the instance owns the partitions of newly created messages
  INSERT INTO wh_partition_assignments (partition_number, instance_id, assigned_at, last_heartbeat)
  SELECT DISTINCT o.partition_number, p_instance_id, v_now, v_now
  FROM wh_outbox o
  WHERE o.instance_id = p_instance_id
    AND o.partition_number NOT IN (SELECT wh_partition_assignments.partition_number FROM wh_partition_assignments WHERE wh_partition_assignments.instance_id = p_instance_id)
  LIMIT v_dynamic_max_partitions - (SELECT COUNT(*) FROM wh_partition_assignments WHERE wh_partition_assignments.instance_id = p_instance_id)
  ON CONFLICT (partition_number) DO NOTHING;

  INSERT INTO wh_partition_assignments (partition_number, instance_id, assigned_at, last_heartbeat)
  SELECT DISTINCT i.partition_number, p_instance_id, v_now, v_now
  FROM wh_inbox i
  WHERE i.instance_id = p_instance_id
    AND i.partition_number NOT IN (SELECT wh_partition_assignments.partition_number FROM wh_partition_assignments WHERE wh_partition_assignments.instance_id = p_instance_id)
  LIMIT v_dynamic_max_partitions - (SELECT COUNT(*) FROM wh_partition_assignments WHERE wh_partition_assignments.instance_id = p_instance_id)
  ON CONFLICT (partition_number) DO NOTHING;

  -- 7.4. Claim partitions for orphaned/unleased messages (with load balancing)
  -- Prioritizes claiming partitions that have actual work (orphaned messages)
  -- Respects v_dynamic_max_partitions limit for fair distribution across instances
  WITH orphaned_partitions AS (
    -- Find all partitions with orphaned/unleased work
    SELECT DISTINCT wh_outbox.partition_number
    FROM wh_outbox
    WHERE (wh_outbox.instance_id IS NULL OR wh_outbox.lease_expiry IS NULL OR wh_outbox.lease_expiry < v_now)
      AND (wh_outbox.status & 32768) = 0  -- Not failed
      AND (wh_outbox.status & 4) != 4     -- Not published (outbox is done when published)
      AND wh_outbox.partition_number IS NOT NULL
    UNION
    SELECT DISTINCT wh_inbox.partition_number
    FROM wh_inbox
    WHERE (wh_inbox.instance_id IS NULL OR wh_inbox.lease_expiry IS NULL OR wh_inbox.lease_expiry < v_now)
      AND (wh_inbox.status & 32768) = 0  -- Not failed
      AND (wh_inbox.status & 2) != 2     -- Not event stored (inbox is done when event stored)
      AND wh_inbox.partition_number IS NOT NULL
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
  active_instances AS (
    -- Get all active instances sorted deterministically for modulo-based distribution
    SELECT
      instance_id,
      ROW_NUMBER() OVER (ORDER BY instance_id) - 1 AS instance_index  -- 0-based index
    FROM wh_service_instances
    WHERE last_heartbeat_at >= v_stale_cutoff
  ),
  partitions_to_claim AS (
    -- Use modulo-based distribution: partition_number % instance_count = instance_index
    SELECT
      aop.partition_number
    FROM available_orphaned_partitions aop
    CROSS JOIN active_instances ai
    WHERE ai.instance_id = p_instance_id
      AND (aop.partition_number % v_active_instance_count) = ai.instance_index
    LIMIT v_dynamic_max_partitions - (
      SELECT COUNT(*) FROM wh_partition_assignments WHERE wh_partition_assignments.instance_id = p_instance_id
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
  -- Events are identified by IsEvent=true flag and must have stream_id

  -- DEBUG: Output sample JSON to see structure
  RAISE NOTICE '=== DEBUG: Sample outbox message JSON ===';
  RAISE NOTICE 'First outbox message: %', (SELECT jsonb_array_element(p_new_outbox_messages, 0));
  RAISE NOTICE 'Envelope property: %', (SELECT jsonb_array_element(p_new_outbox_messages, 0)->'Envelope');

  -- Insert events from outbox (published events)
  -- Uses windowing function to handle multiple events in same stream within a single batch
  WITH outbox_events AS (
    SELECT
      elem,
      (elem->>'StreamId')::UUID as stream_id,
      ROW_NUMBER() OVER (
        PARTITION BY (elem->>'StreamId')::UUID
        ORDER BY (elem->>'MessageId')::UUID
      ) as row_num
    FROM jsonb_array_elements(p_new_outbox_messages) as elem
    WHERE (elem->>'IsEvent')::BOOLEAN = true
      AND (elem->>'StreamId') IS NOT NULL
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
      WHEN (bv.elem->>'MessageType') LIKE '%.%' THEN
        split_part(bv.elem->>'MessageType', '.', -2)  -- Get second-to-last segment
      WHEN (bv.elem->>'MessageType') LIKE '%Event' THEN
        regexp_replace(bv.elem->>'MessageType', '([A-Z][a-z]+).*Event$', '\1')  -- Extract leading word
      ELSE 'Unknown'
    END,
    bv.elem->>'MessageType',
    (bv.elem->'Envelope'->'Payload')::JSONB,  -- Extract payload from envelope
    (bv.elem->'Envelope')::JSONB,  -- Store complete envelope as metadata for now
    NULL,  -- Scope is not currently used
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
      (elem->>'StreamId')::UUID as stream_id,
      ROW_NUMBER() OVER (
        PARTITION BY (elem->>'StreamId')::UUID
        ORDER BY (elem->>'MessageId')::UUID
      ) as row_num
    FROM jsonb_array_elements(p_new_inbox_messages) as elem
    WHERE (elem->>'IsEvent')::BOOLEAN = true
      AND (elem->>'StreamId') IS NOT NULL
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
      WHEN (bv.elem->>'MessageType') LIKE '%.%' THEN
        split_part(bv.elem->>'MessageType', '.', -2)
      WHEN (bv.elem->>'MessageType') LIKE '%Event' THEN
        regexp_replace(bv.elem->>'MessageType', '([A-Z][a-z]+).*Event$', '\1')
      ELSE 'Unknown'
    END,
    bv.elem->>'MessageType',
    (bv.elem->'Envelope'->'Payload')::JSONB,  -- Extract payload from envelope
    (bv.elem->'Envelope')::JSONB,  -- Store complete envelope as metadata for now
    NULL,  -- Scope is not currently used
    nextval('wh_event_sequence'),
    bv.base_version + bv.row_num,  -- Sequential versioning within batch
    v_now
  FROM base_versions bv
  ON CONFLICT (stream_id, version) DO NOTHING;

  -- 7.75. Claim unleased messages in owned partitions
  -- This ensures that when an instance owns a partition, it automatically claims
  -- any messages in that partition that aren't currently leased (orphaned or newly inserted)
  -- Track which messages are orphaned and being re-leased so we can return them as work

  -- Claim orphaned outbox messages and track them in array
  -- Exclude messages that were just completed/failed in this call by checking JSONB parameters directly
  RAISE NOTICE '=== SECTION 7.75: CLAIM ORPHANED MESSAGES ===';
  RAISE NOTICE 'About to claim orphaned outbox messages. Exclusion check against completions: %', p_outbox_completions;
  RAISE NOTICE 'About to claim orphaned outbox messages. Exclusion check against failures: %', p_outbox_failures;

  -- First, let's see what messages would match WITHOUT the NOT EXISTS exclusion
  RAISE NOTICE 'Messages that WOULD be claimed (before NOT EXISTS check): %', (
    SELECT array_agg(wh_outbox.message_id)
    FROM wh_outbox
    WHERE wh_outbox.partition_number IN (
      SELECT wh_partition_assignments.partition_number FROM wh_partition_assignments WHERE wh_partition_assignments.instance_id = p_instance_id
    )
    AND (wh_outbox.instance_id IS NULL OR wh_outbox.lease_expiry IS NULL OR wh_outbox.lease_expiry < v_now)
    AND (wh_outbox.status & 32768) = 0  -- Not failed
    AND (wh_outbox.status & 4) != 4     -- Not published (prevents reclaiming completed messages)
    AND (wh_outbox.status & 24) != 24   -- Not fully completed
  );

  WITH orphaned AS (
    UPDATE wh_outbox
    SET instance_id = p_instance_id,
        lease_expiry = v_lease_expiry
    WHERE wh_outbox.partition_number IN (
      SELECT wh_partition_assignments.partition_number FROM wh_partition_assignments WHERE wh_partition_assignments.instance_id = p_instance_id
    )
    AND (wh_outbox.instance_id IS NULL OR wh_outbox.lease_expiry IS NULL OR wh_outbox.lease_expiry < v_now)
    AND (wh_outbox.status & 32768) = 0  -- Not failed
    AND (wh_outbox.status & 4) != 4     -- Not published (outbox is done when published)
    AND NOT EXISTS (  -- Don't reclaim messages being completed
      SELECT 1 FROM jsonb_array_elements(p_outbox_completions) elem
      WHERE (elem->>'MessageId')::UUID = wh_outbox.message_id
    )
    AND NOT EXISTS (  -- Don't reclaim messages being failed
      SELECT 1 FROM jsonb_array_elements(p_outbox_failures) elem
      WHERE (elem->>'MessageId')::UUID = wh_outbox.message_id
    )
    RETURNING message_id
  )
  SELECT array_agg(message_id) INTO v_orphaned_outbox_ids FROM orphaned;

  -- Handle NULL case (no orphaned messages)
  v_orphaned_outbox_ids := COALESCE(v_orphaned_outbox_ids, '{}');

  RAISE NOTICE 'Claimed v_orphaned_outbox_ids: %, cardinality: %', v_orphaned_outbox_ids, cardinality(v_orphaned_outbox_ids);

  -- Claim orphaned inbox messages and track them in array
  -- Exclude messages that were just completed/failed in this call by checking JSONB parameters directly
  WITH orphaned AS (
    UPDATE wh_inbox
    SET instance_id = p_instance_id,
        lease_expiry = v_lease_expiry
    WHERE wh_inbox.partition_number IN (
      SELECT wh_partition_assignments.partition_number FROM wh_partition_assignments WHERE wh_partition_assignments.instance_id = p_instance_id
    )
    AND (wh_inbox.instance_id IS NULL OR wh_inbox.lease_expiry IS NULL OR wh_inbox.lease_expiry < v_now)
    AND (wh_inbox.status & 32768) = 0  -- Not failed
    AND (wh_inbox.status & 2) != 2     -- Not event stored (inbox is done when event stored)
    AND NOT EXISTS (  -- Don't reclaim messages being completed
      SELECT 1 FROM jsonb_array_elements(p_inbox_completions) elem
      WHERE (elem->>'MessageId')::UUID = wh_inbox.message_id
    )
    AND NOT EXISTS (  -- Don't reclaim messages being failed
      SELECT 1 FROM jsonb_array_elements(p_inbox_failures) elem
      WHERE (elem->>'MessageId')::UUID = wh_inbox.message_id
    )
    RETURNING message_id
  )
  SELECT array_agg(message_id) INTO v_orphaned_inbox_ids FROM orphaned;

  -- Handle NULL case (no orphaned messages)
  v_orphaned_inbox_ids := COALESCE(v_orphaned_inbox_ids, '{}');

  -- 8. Return work from OWNED PARTITIONS ONLY, maintaining stream order
  -- Only return messages that were newly stored OR orphaned and re-leased in this call
  -- This prevents returning messages that already had a valid lease before this call

  -- DEBUG: Log what we're about to return
  RAISE NOTICE '=== SECTION 8: RETURNING WORK ===';
  RAISE NOTICE 'About to return outbox work. v_new_outbox_ids: %, v_orphaned_outbox_ids: %', v_new_outbox_ids, v_orphaned_outbox_ids;

  RETURN QUERY
  WITH owned_partitions AS (
    SELECT pa.partition_number AS part_num
    FROM wh_partition_assignments pa
    WHERE pa.instance_id = p_instance_id
  )
  SELECT
    'outbox'::VARCHAR AS source,
    o.message_id AS msg_id,
    o.destination::VARCHAR AS destination,
    o.event_type::VARCHAR AS envelope_type,  -- Renamed to match WorkBatchRow
    o.event_data::TEXT AS envelope_data,  -- Renamed to match WorkBatchRow
    o.metadata::TEXT AS metadata,
    o.scope::TEXT AS scope,
    o.stream_id AS stream_uuid,
    o.partition_number::INTEGER AS partition_num,
    o.attempts AS attempts,
    o.status AS status,
    CASE
      WHEN o.message_id = ANY(v_new_outbox_ids) THEN 1  -- NewlyStored
      ELSE 2  -- Orphaned
    END::INTEGER AS flags,
    EXTRACT(EPOCH FROM o.created_at)::BIGINT * 1000 AS sequence_order  -- Epoch ms
  FROM wh_outbox o
  INNER JOIN owned_partitions op ON o.partition_number = op.part_num
  WHERE o.instance_id = p_instance_id
    AND o.lease_expiry > v_now
    AND (o.status & 32768) = 0  -- Not failed
    AND (o.status & 4) != 4     -- Not published (outbox is done when published)
    AND (o.message_id = ANY(v_new_outbox_ids) OR o.message_id = ANY(v_orphaned_outbox_ids))  -- Only newly stored OR orphaned/re-leased
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
    i.handler_name::VARCHAR AS destination,
    i.event_type::VARCHAR AS envelope_type,  -- Renamed to match WorkBatchRow
    i.event_data::TEXT AS envelope_data,  -- Renamed to match WorkBatchRow
    i.metadata::TEXT AS metadata,
    i.scope::TEXT AS scope,
    i.stream_id AS stream_uuid,
    i.partition_number::INTEGER AS partition_num,
    i.attempts AS attempts,
    i.status AS status,
    CASE
      WHEN i.message_id = ANY(v_new_inbox_ids) THEN 1  -- NewlyStored
      ELSE 2  -- Orphaned
    END::INTEGER AS flags,
    EXTRACT(EPOCH FROM i.received_at)::BIGINT * 1000 AS sequence_order
  FROM wh_inbox i
  INNER JOIN owned_partitions op ON i.partition_number = op.part_num
  WHERE i.instance_id = p_instance_id
    AND i.lease_expiry > v_now
    AND (i.status & 32768) = 0  -- Not failed
    AND (i.status & 2) != 2     -- Not event stored (inbox is done when event stored)
    AND (i.message_id = ANY(v_new_inbox_ids) OR i.message_id = ANY(v_orphaned_inbox_ids))  -- Only newly stored OR orphaned/re-leased
  ORDER BY i.stream_id, i.received_at;  -- CRITICAL: Stream ordering

END;
$$ LANGUAGE plpgsql;

-- Add comment for documentation
COMMENT ON FUNCTION process_work_batch IS 'Atomic work coordination: register/heartbeat instance, cleanup stale instances, mark completed/failed (outbox/inbox/receptor/perspective), claim orphaned work, partition-based stream ordering with event store integration and receptor/perspective tracking';
