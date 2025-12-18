-- Migration 017: Add stream ownership check to work claiming
-- Date: 2025-12-18
-- Description: Enhance work claiming to respect wh_active_streams ownership
--              - Only claim work from streams with no active owner
--              - Only claim work from streams with expired leases
--              - Allow re-claiming work from streams already owned by this instance
--              Ensures sticky stream assignment and prevents mid-processing stream reassignment

-- Drop and recreate function with stream ownership checks
DROP FUNCTION IF EXISTS process_work_batch;

CREATE OR REPLACE FUNCTION process_work_batch(
  -- Instance identification
  p_instance_id UUID,
  p_service_name VARCHAR(200),
  p_host_name VARCHAR(200),
  p_process_id INTEGER,
  p_metadata JSONB DEFAULT NULL,

  -- Completion tracking (with status pairing)
  p_outbox_completions JSONB DEFAULT '[]'::JSONB,
  p_outbox_failures JSONB DEFAULT '[]'::JSONB,
  p_inbox_completions JSONB DEFAULT '[]'::JSONB,
  p_inbox_failures JSONB DEFAULT '[]'::JSONB,

  -- Receptor processing completions
  p_receptor_completions JSONB DEFAULT '[]'::JSONB,
  p_receptor_failures JSONB DEFAULT '[]'::JSONB,

  -- Perspective checkpoint completions
  p_perspective_completions JSONB DEFAULT '[]'::JSONB,
  p_perspective_failures JSONB DEFAULT '[]'::JSONB,

  -- Immediate processing support
  p_new_outbox_messages JSONB DEFAULT '[]'::JSONB,
  p_new_inbox_messages JSONB DEFAULT '[]'::JSONB,

  -- Lease renewal
  p_renew_outbox_lease_ids JSONB DEFAULT '[]'::JSONB,
  p_renew_inbox_lease_ids JSONB DEFAULT '[]'::JSONB,

  -- Configuration
  p_lease_seconds INTEGER DEFAULT 300,
  p_stale_threshold_seconds INTEGER DEFAULT 600,
  p_flags INTEGER DEFAULT 0,
  p_partition_count INTEGER DEFAULT 10000
)
RETURNS TABLE(
  source VARCHAR,
  msg_id UUID,
  destination VARCHAR,
  envelope_type VARCHAR,
  envelope_data TEXT,
  metadata TEXT,
  scope TEXT,
  stream_uuid UUID,
  partition_num INTEGER,
  attempts INTEGER,
  status INTEGER,
  flags INTEGER,
  sequence_order BIGINT
) AS $$
DECLARE
  v_now TIMESTAMPTZ := CURRENT_TIMESTAMP;
  v_lease_expiry TIMESTAMPTZ := v_now + (p_lease_seconds || ' seconds')::INTERVAL;
  v_stale_cutoff TIMESTAMPTZ := v_now - (p_stale_threshold_seconds || ' seconds')::INTERVAL;
  v_debug_mode BOOLEAN := (p_flags & 4) = 4;
  v_new_msg RECORD;
  v_partition INTEGER;
  v_completion RECORD;
  v_failure RECORD;
  v_current_status INTEGER;
  v_active_instance_count INTEGER;
  v_instance_rank INTEGER;
  v_new_outbox_ids UUID[] := '{}';
  v_new_inbox_ids UUID[] := '{}';
  v_orphaned_outbox_ids UUID[] := '{}';
  v_orphaned_inbox_ids UUID[] := '{}';
  v_before_instance_id UUID;
  v_before_lease_expiry TIMESTAMPTZ;
  v_before_status INTEGER;
  v_after_instance_id UUID;
  v_after_lease_expiry TIMESTAMPTZ;
  v_after_status INTEGER;
  v_stream_id UUID;  -- For stream cleanup iteration
  v_claimed_streams UUID[];  -- Streams claimed when reclaiming orphaned work

BEGIN
  IF v_debug_mode THEN
    RAISE NOTICE '==== MIGRATION VERSION: 2025-12-18_STREAM_OWNERSHIP_CHECK ===';
    RAISE NOTICE '=== SECTION 0: INPUT PARAMETERS ===';
    RAISE NOTICE 'p_outbox_completions: %', p_outbox_completions;
    RAISE NOTICE 'p_outbox_failures: %', p_outbox_failures;
    RAISE NOTICE 'p_new_outbox_messages count: %', jsonb_array_length(p_new_outbox_messages);
  END IF;

  -- 1. Register/update this instance with heartbeat
  INSERT INTO wh_service_instances (
    instance_id, service_name, host_name, process_id, started_at, last_heartbeat_at, metadata
  ) VALUES (
    p_instance_id, p_service_name, p_host_name, p_process_id, v_now, v_now, p_metadata
  )
  ON CONFLICT (instance_id) DO UPDATE SET
    last_heartbeat_at = v_now,
    metadata = COALESCE(EXCLUDED.metadata, wh_service_instances.metadata);

  -- 2. Clean up stale instances and release their messages/streams
  CREATE TEMP TABLE IF NOT EXISTS deleted_instance_ids (instance_id UUID);
  DELETE FROM deleted_instance_ids;

  INSERT INTO deleted_instance_ids (instance_id)
  SELECT instance_id FROM wh_service_instances
  WHERE last_heartbeat_at < v_stale_cutoff AND instance_id != p_instance_id;

  -- Delete stale instances (FK CASCADE handles wh_active_streams cleanup)
  DELETE FROM wh_service_instances
  WHERE instance_id IN (SELECT instance_id FROM deleted_instance_ids);

  -- Release outbox messages from deleted instances
  UPDATE wh_outbox
  SET instance_id = NULL,
      lease_expiry = NULL
  WHERE instance_id IN (SELECT instance_id FROM deleted_instance_ids);

  -- Release inbox messages from deleted instances
  UPDATE wh_inbox
  SET instance_id = NULL,
      lease_expiry = NULL
  WHERE instance_id IN (SELECT instance_id FROM deleted_instance_ids);

  -- 2.5. Calculate dynamic max partitions based on active instance count
  SELECT COUNT(*) INTO v_active_instance_count
  FROM wh_service_instances
  WHERE last_heartbeat_at >= v_stale_cutoff;

  v_active_instance_count := GREATEST(v_active_instance_count, 1);

  -- 2.6. Calculate this instance's rank
  WITH instance_ranks AS (
    SELECT instance_id,
           (ROW_NUMBER() OVER (ORDER BY instance_id) - 1) as rank
    FROM wh_service_instances
    WHERE last_heartbeat_at >= v_stale_cutoff
  )
  SELECT rank INTO v_instance_rank
  FROM instance_ranks
  WHERE instance_id = p_instance_id;

  IF v_instance_rank IS NULL THEN
    RAISE EXCEPTION 'Failed to calculate rank for instance %. Instance not found in active instances.', p_instance_id;
  END IF;

  IF v_debug_mode THEN
    RAISE NOTICE 'Instance % has rank % out of % active instances',
      p_instance_id, v_instance_rank, v_active_instance_count;
  END IF;

  -- 4. Process completions
  IF v_debug_mode THEN
    RAISE NOTICE '=== SECTION 4: PROCESS COMPLETIONS ===';
  END IF;

  IF jsonb_array_length(p_outbox_completions) > 0 THEN
    FOR v_completion IN
      SELECT
        (elem->>'MessageId')::UUID as msg_id,
        (elem->>'Status')::INTEGER as status_flags
      FROM jsonb_array_elements(p_outbox_completions) as elem
    LOOP
      IF v_debug_mode THEN
        RAISE NOTICE 'Processing completion: messageId=%, status=%', v_completion.msg_id, v_completion.status_flags;
      END IF;

      IF v_debug_mode THEN
        UPDATE wh_outbox AS o
        SET status = o.status | v_completion.status_flags,
            processed_at = v_now,
            published_at = CASE WHEN (v_completion.status_flags & 4) = 4 THEN v_now ELSE o.published_at END,
            instance_id = NULL,
            lease_expiry = NULL
        WHERE o.message_id = v_completion.msg_id;
      ELSE
        SELECT wh_outbox.status INTO v_current_status FROM wh_outbox WHERE wh_outbox.message_id = v_completion.msg_id;

        IF ((v_current_status | v_completion.status_flags) & 4) = 4 THEN
          DELETE FROM wh_outbox WHERE wh_outbox.message_id = v_completion.msg_id;
          IF v_debug_mode THEN
            RAISE NOTICE 'DELETED outbox message (published): messageId=%', v_completion.msg_id;
          END IF;
        ELSE
          UPDATE wh_outbox AS o
          SET status = o.status | v_completion.status_flags,
              processed_at = v_now,
              published_at = CASE WHEN (v_completion.status_flags & 4) = 4 THEN v_now ELSE o.published_at END,
              instance_id = NULL,
              lease_expiry = NULL
          WHERE o.message_id = v_completion.msg_id;

          IF v_debug_mode THEN
            RAISE NOTICE 'Updated message (partially completed): messageId=%', v_completion.msg_id;
          END IF;
        END IF;
      END IF;
    END LOOP;
  END IF;

  IF v_debug_mode THEN
    RAISE NOTICE '=== SECTION 4 COMPLETE: All completions processed ===';
  END IF;

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
        SELECT wh_inbox.status INTO v_current_status FROM wh_inbox WHERE wh_inbox.message_id = v_completion.msg_id;

        IF ((v_current_status | v_completion.status_flags) & 2) = 2 THEN
          DELETE FROM wh_inbox WHERE wh_inbox.message_id = v_completion.msg_id;
          IF v_debug_mode THEN
            RAISE NOTICE 'DELETED inbox message (event stored): messageId=%', v_completion.msg_id;
          END IF;
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

  -- 4.5. Clean up streams with no pending work
  -- After completions/deletions, check if any streams no longer have work in outbox/inbox
  -- Delete from wh_active_streams if stream has no pending work
  FOR v_stream_id IN
    SELECT DISTINCT stream_id
    FROM (
      SELECT (elem->>'MessageId')::UUID as msg_id FROM jsonb_array_elements(p_outbox_completions) elem
      UNION ALL
      SELECT (elem->>'MessageId')::UUID FROM jsonb_array_elements(p_inbox_completions) elem
    ) completed_messages
    JOIN (
      SELECT message_id, stream_id FROM wh_outbox
      UNION ALL
      SELECT message_id, stream_id FROM wh_inbox
    ) all_messages ON all_messages.message_id = completed_messages.msg_id
  LOOP
    -- Check if stream has any remaining work
    IF NOT EXISTS (
      SELECT 1 FROM wh_outbox WHERE stream_id = v_stream_id AND (status & 4) != 4  -- Not published
    ) AND NOT EXISTS (
      SELECT 1 FROM wh_inbox WHERE stream_id = v_stream_id AND (status & 2) != 2   -- Not event stored
    ) THEN
      -- No more work for this stream - remove from active streams
      DELETE FROM wh_active_streams WHERE wh_active_streams.stream_id = v_stream_id;

      IF v_debug_mode THEN
        RAISE NOTICE 'Removed stream % from active_streams (no pending work)', v_stream_id;
      END IF;
    END IF;
  END LOOP;

  -- 5. Process failures
  IF jsonb_array_length(p_outbox_failures) > 0 THEN
    FOR v_failure IN
      SELECT
        (elem->>'MessageId')::UUID as msg_id,
        (elem->>'CompletedStatus')::INTEGER as status_flags,
        elem->>'Error' as error_message
      FROM jsonb_array_elements(p_outbox_failures) as elem
    LOOP
      UPDATE wh_outbox
      SET status = (wh_outbox.status | v_failure.status_flags | 32768),
          error = v_failure.error_message,
          attempts = wh_outbox.attempts + 1,
          scheduled_for = v_now + (INTERVAL '30 seconds' * POWER(2, wh_outbox.attempts + 1)),
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
          scheduled_for = v_now + (INTERVAL '30 seconds' * POWER(2, wh_inbox.attempts + 1)),
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
        NULL
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
        NULL
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

  -- 5.5. Renew leases for buffered messages
  IF jsonb_array_length(p_renew_outbox_lease_ids) > 0 THEN
    UPDATE wh_outbox
    SET lease_expiry = v_lease_expiry
    WHERE wh_outbox.message_id IN (
      SELECT (elem::TEXT)::UUID
      FROM jsonb_array_elements_text(p_renew_outbox_lease_ids) elem
    )
    AND wh_outbox.instance_id = p_instance_id;

    -- Also renew stream leases for renewed messages
    UPDATE wh_active_streams
    SET lease_expiry = v_lease_expiry,
        updated_at = v_now
    WHERE wh_active_streams.stream_id IN (
      SELECT DISTINCT o.stream_id
      FROM wh_outbox o
      WHERE o.message_id IN (
        SELECT (elem::TEXT)::UUID
        FROM jsonb_array_elements_text(p_renew_outbox_lease_ids) elem
      )
    )
    AND wh_active_streams.assigned_instance_id = p_instance_id;
  END IF;

  IF jsonb_array_length(p_renew_inbox_lease_ids) > 0 THEN
    UPDATE wh_inbox
    SET lease_expiry = v_lease_expiry
    WHERE wh_inbox.message_id IN (
      SELECT (elem::TEXT)::UUID
      FROM jsonb_array_elements_text(p_renew_inbox_lease_ids) elem
    )
    AND wh_inbox.instance_id = p_instance_id;

    -- Also renew stream leases for renewed messages
    UPDATE wh_active_streams
    SET lease_expiry = v_lease_expiry,
        updated_at = v_now
    WHERE wh_active_streams.stream_id IN (
      SELECT DISTINCT i.stream_id
      FROM wh_inbox i
      WHERE i.message_id IN (
        SELECT (elem::TEXT)::UUID
        FROM jsonb_array_elements_text(p_renew_inbox_lease_ids) elem
      )
    )
    AND wh_active_streams.assigned_instance_id = p_instance_id;
  END IF;

  -- 6. Store new outbox messages
  IF v_debug_mode THEN
    RAISE NOTICE '=== SECTION 6: STORE NEW OUTBOX MESSAGES ===';
  END IF;

  IF jsonb_array_length(p_new_outbox_messages) > 0 THEN
    FOR v_new_msg IN
      SELECT
        (elem->>'MessageId')::UUID as message_id,
        elem->>'Destination' as destination,
        elem->>'MessageType' as message_type,
        elem->>'EnvelopeType' as envelope_type,
        (elem->'Envelope')::TEXT as envelope_data,
        COALESCE((elem->'Envelope'->'Hops'->0)::TEXT, '{}') as metadata,
        NULL::TEXT as scope,
        (elem->>'IsEvent')::BOOLEAN as is_event,
        (elem->>'StreamId')::UUID as stream_id
      FROM jsonb_array_elements(p_new_outbox_messages) as elem
    LOOP
      v_partition := compute_partition(v_new_msg.stream_id, p_partition_count);

      -- Insert/update stream in active_streams (just-in-time assignment)
      INSERT INTO wh_active_streams (
        stream_id, partition_number, assigned_instance_id, lease_expiry, created_at, updated_at
      ) VALUES (
        v_new_msg.stream_id, v_partition, p_instance_id, v_lease_expiry, v_now, v_now
      )
      ON CONFLICT (stream_id) DO UPDATE SET
        assigned_instance_id = p_instance_id,
        lease_expiry = v_lease_expiry,
        updated_at = v_now;

      -- Store in outbox with lease
      INSERT INTO wh_outbox (
        message_id, destination, event_type, event_data, metadata, scope,
        stream_id, partition_number, is_event,
        status, attempts, instance_id, lease_expiry, created_at
      ) VALUES (
        v_new_msg.message_id,
        v_new_msg.destination,
        v_new_msg.envelope_type,
        v_new_msg.envelope_data::JSONB,
        v_new_msg.metadata::JSONB,
        CASE WHEN v_new_msg.scope IS NOT NULL THEN v_new_msg.scope::JSONB ELSE NULL END,
        v_new_msg.stream_id,
        v_partition,
        v_new_msg.is_event,
        1,
        0,
        p_instance_id,
        v_lease_expiry,
        v_now
      );

      v_new_outbox_ids := array_append(v_new_outbox_ids, v_new_msg.message_id);
      IF v_debug_mode THEN
        RAISE NOTICE 'Stored new outbox message: %, v_new_outbox_ids now: %', v_new_msg.message_id, v_new_outbox_ids;
      END IF;
    END LOOP;
  END IF;

  IF v_debug_mode THEN
    RAISE NOTICE 'Final v_new_outbox_ids: %, cardinality: %', v_new_outbox_ids, cardinality(v_new_outbox_ids);
  END IF;

  -- 7. Store new inbox messages (with deduplication)
  IF jsonb_array_length(p_new_inbox_messages) > 0 THEN
    WITH new_msgs AS (
      INSERT INTO wh_message_deduplication (message_id, first_seen_at)
      SELECT (elem->>'MessageId')::UUID, v_now
      FROM jsonb_array_elements(p_new_inbox_messages) as elem
      ON CONFLICT (message_id) DO NOTHING
      RETURNING message_id
    )
    SELECT array_agg(message_id) INTO v_new_inbox_ids FROM new_msgs;

    v_new_inbox_ids := COALESCE(v_new_inbox_ids, '{}');

    FOR v_new_msg IN
      SELECT
        (elem->>'MessageId')::UUID as message_id,
        elem->>'HandlerName' as handler_name,
        elem->>'MessageType' as message_type,
        elem->>'EnvelopeType' as envelope_type,
        (elem->'Envelope')::TEXT as envelope_data,
        COALESCE((elem->'Envelope'->'Hops'->0)::TEXT, '{}') as metadata,
        NULL::TEXT as scope,
        (elem->>'IsEvent')::BOOLEAN as is_event,
        (elem->>'StreamId')::UUID as stream_id
      FROM jsonb_array_elements(p_new_inbox_messages) as elem
      WHERE (elem->>'MessageId')::UUID = ANY(v_new_inbox_ids)
    LOOP
      v_partition := compute_partition(v_new_msg.stream_id, p_partition_count);

      -- Insert/update stream in active_streams (just-in-time assignment)
      INSERT INTO wh_active_streams (
        stream_id, partition_number, assigned_instance_id, lease_expiry, created_at, updated_at
      ) VALUES (
        v_new_msg.stream_id, v_partition, p_instance_id, v_lease_expiry, v_now, v_now
      )
      ON CONFLICT (stream_id) DO UPDATE SET
        assigned_instance_id = p_instance_id,
        lease_expiry = v_lease_expiry,
        updated_at = v_now;

      -- Insert into inbox
      INSERT INTO wh_inbox (
        message_id, handler_name, event_type, event_data, metadata, scope,
        stream_id, partition_number, is_event,
        status, attempts, instance_id, lease_expiry, received_at
      ) VALUES (
        v_new_msg.message_id,
        v_new_msg.handler_name,
        v_new_msg.envelope_type,
        v_new_msg.envelope_data::JSONB,
        v_new_msg.metadata::JSONB,
        CASE WHEN v_new_msg.scope IS NOT NULL THEN v_new_msg.scope::JSONB ELSE NULL END,
        v_new_msg.stream_id,
        v_partition,
        v_new_msg.is_event,
        1,
        0,
        p_instance_id,
        v_lease_expiry,
        v_now
      );
    END LOOP;
  END IF;

  -- 7.5. Event Store Integration
  IF v_debug_mode THEN
    RAISE NOTICE '=== DEBUG: Sample outbox message JSON ===';
    RAISE NOTICE 'First outbox message: %', (SELECT jsonb_array_element(p_new_outbox_messages, 0));
    RAISE NOTICE 'Envelope property: %', (SELECT jsonb_array_element(p_new_outbox_messages, 0)->'Envelope');
  END IF;

  -- Insert events from outbox
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
    (bv.elem->'Envelope'->'Payload')::JSONB,
    (bv.elem->'Envelope')::JSONB,
    NULL,
    nextval('wh_event_sequence'),
    bv.base_version + bv.row_num,
    v_now
  FROM base_versions bv
  ON CONFLICT (stream_id, version) DO NOTHING;

  -- Insert events from inbox
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
    (bv.elem->'Envelope'->'Payload')::JSONB,
    (bv.elem->'Envelope')::JSONB,
    NULL,
    nextval('wh_event_sequence'),
    bv.base_version + bv.row_num,
    v_now
  FROM base_versions bv
  ON CONFLICT (stream_id, version) DO NOTHING;

  -- 7.75. Claim unleased messages in owned partitions (with stream ownership check)
  IF v_debug_mode THEN
    RAISE NOTICE '=== SECTION 7.75: CLAIM ORPHANED MESSAGES (WITH STREAM OWNERSHIP CHECK) ===';
    RAISE NOTICE 'About to claim orphaned outbox messages. Exclusion check against completions: %', p_outbox_completions;
    RAISE NOTICE 'About to claim orphaned outbox messages. Exclusion check against failures: %', p_outbox_failures;

    RAISE NOTICE 'Messages that WOULD be claimed (before NOT EXISTS check): %', (
      SELECT array_agg(wh_outbox.message_id)
      FROM wh_outbox
      WHERE (wh_outbox.instance_id IS NULL OR wh_outbox.lease_expiry IS NULL OR wh_outbox.lease_expiry < v_now)
      AND (wh_outbox.status & 32768) = 0
      AND (wh_outbox.status & 4) != 4
      AND (wh_outbox.status & 24) != 24
    );
  END IF;

  -- Claim orphaned outbox messages + update active_streams (WITH STREAM OWNERSHIP CHECK)
  WITH orphaned AS (
    UPDATE wh_outbox
    SET instance_id = p_instance_id,
        lease_expiry = v_lease_expiry
    WHERE (wh_outbox.instance_id IS NULL OR wh_outbox.lease_expiry IS NULL OR wh_outbox.lease_expiry < v_now)
    AND (wh_outbox.status & 4) != 4
    AND (wh_outbox.scheduled_for IS NULL OR wh_outbox.scheduled_for <= v_now)
    AND wh_outbox.partition_number % v_active_instance_count = v_instance_rank
    -- STREAM OWNERSHIP CHECK: Only claim if stream is not owned by another instance
    AND NOT EXISTS (
      SELECT 1 FROM wh_active_streams
      WHERE wh_active_streams.stream_id = wh_outbox.stream_id
        AND wh_active_streams.assigned_instance_id != p_instance_id
        AND wh_active_streams.lease_expiry > v_now
    )
    AND NOT EXISTS (
      SELECT 1 FROM wh_outbox earlier
      WHERE earlier.stream_id = wh_outbox.stream_id
        AND earlier.created_at < wh_outbox.created_at
        AND (
          (earlier.scheduled_for IS NOT NULL AND earlier.scheduled_for > v_now)
          OR (earlier.instance_id IS NOT NULL AND earlier.lease_expiry > v_now)
        )
    )
    AND NOT EXISTS (
      SELECT 1 FROM jsonb_array_elements(p_outbox_completions) elem
      WHERE (elem->>'MessageId')::UUID = wh_outbox.message_id
        AND ((elem->>'Status')::INTEGER) > 0
    )
    AND NOT EXISTS (
      SELECT 1 FROM jsonb_array_elements(p_outbox_failures) elem
      WHERE (elem->>'MessageId')::UUID = wh_outbox.message_id
    )
    RETURNING message_id, stream_id, partition_number
  )
  SELECT array_agg(message_id), array_agg(DISTINCT stream_id)
  INTO v_orphaned_outbox_ids, v_claimed_streams
  FROM orphaned;

  v_orphaned_outbox_ids := COALESCE(v_orphaned_outbox_ids, '{}');

  -- Update active_streams for claimed orphaned outbox streams
  IF v_claimed_streams IS NOT NULL THEN
    FOREACH v_stream_id IN ARRAY v_claimed_streams
    LOOP
      INSERT INTO wh_active_streams (
        stream_id,
        partition_number,
        assigned_instance_id,
        lease_expiry,
        created_at,
        updated_at
      )
      SELECT
        v_stream_id,
        compute_partition(v_stream_id, p_partition_count),
        p_instance_id,
        v_lease_expiry,
        v_now,
        v_now
      WHERE NOT EXISTS (SELECT 1 FROM wh_active_streams WHERE wh_active_streams.stream_id = v_stream_id)
      ON CONFLICT (stream_id) DO UPDATE SET
        assigned_instance_id = p_instance_id,
        lease_expiry = v_lease_expiry,
        updated_at = v_now;
    END LOOP;
  END IF;

  IF v_debug_mode THEN
    RAISE NOTICE 'Claimed v_orphaned_outbox_ids: %, cardinality: %', v_orphaned_outbox_ids, cardinality(v_orphaned_outbox_ids);
  END IF;

  -- Claim orphaned inbox messages + update active_streams (WITH STREAM OWNERSHIP CHECK)
  WITH orphaned AS (
    UPDATE wh_inbox
    SET instance_id = p_instance_id,
        lease_expiry = v_lease_expiry
    WHERE (wh_inbox.instance_id IS NULL OR wh_inbox.lease_expiry IS NULL OR wh_inbox.lease_expiry < v_now)
    AND (wh_inbox.status & 2) != 2
    AND (wh_inbox.scheduled_for IS NULL OR wh_inbox.scheduled_for <= v_now)
    AND wh_inbox.partition_number % v_active_instance_count = v_instance_rank
    -- STREAM OWNERSHIP CHECK: Only claim if stream is not owned by another instance
    AND NOT EXISTS (
      SELECT 1 FROM wh_active_streams
      WHERE wh_active_streams.stream_id = wh_inbox.stream_id
        AND wh_active_streams.assigned_instance_id != p_instance_id
        AND wh_active_streams.lease_expiry > v_now
    )
    AND NOT EXISTS (
      SELECT 1 FROM wh_inbox earlier
      WHERE earlier.stream_id = wh_inbox.stream_id
        AND earlier.received_at < wh_inbox.received_at
        AND (
          (earlier.scheduled_for IS NOT NULL AND earlier.scheduled_for > v_now)
          OR (earlier.instance_id IS NOT NULL AND earlier.lease_expiry > v_now)
        )
    )
    AND NOT EXISTS (
      SELECT 1 FROM jsonb_array_elements(p_inbox_completions) elem
      WHERE (elem->>'MessageId')::UUID = wh_inbox.message_id
        AND ((elem->>'Status')::INTEGER) > 0
    )
    AND NOT EXISTS (
      SELECT 1 FROM jsonb_array_elements(p_inbox_failures) elem
      WHERE (elem->>'MessageId')::UUID = wh_inbox.message_id
    )
    RETURNING message_id, stream_id, partition_number
  )
  SELECT array_agg(message_id), array_agg(DISTINCT stream_id)
  INTO v_orphaned_inbox_ids, v_claimed_streams
  FROM orphaned;

  v_orphaned_inbox_ids := COALESCE(v_orphaned_inbox_ids, '{}');

  -- Update active_streams for claimed orphaned inbox streams
  IF v_claimed_streams IS NOT NULL THEN
    FOREACH v_stream_id IN ARRAY v_claimed_streams
    LOOP
      INSERT INTO wh_active_streams (
        stream_id,
        partition_number,
        assigned_instance_id,
        lease_expiry,
        created_at,
        updated_at
      )
      SELECT
        v_stream_id,
        compute_partition(v_stream_id, p_partition_count),
        p_instance_id,
        v_lease_expiry,
        v_now,
        v_now
      WHERE NOT EXISTS (SELECT 1 FROM wh_active_streams WHERE wh_active_streams.stream_id = v_stream_id)
      ON CONFLICT (stream_id) DO UPDATE SET
        assigned_instance_id = p_instance_id,
        lease_expiry = v_lease_expiry,
        updated_at = v_now;
    END LOOP;
  END IF;

  -- 8. Return work from owned partitions
  IF v_debug_mode THEN
    RAISE NOTICE '=== SECTION 8: RETURNING WORK ===';
    RAISE NOTICE 'About to return outbox work. v_new_outbox_ids: %, v_orphaned_outbox_ids: %', v_new_outbox_ids, v_orphaned_outbox_ids;
  END IF;

  RETURN QUERY
  SELECT
    'outbox'::VARCHAR AS source,
    o.message_id AS msg_id,
    o.destination::VARCHAR AS destination,
    o.event_type::VARCHAR AS envelope_type,
    o.event_data::TEXT AS envelope_data,
    o.metadata::TEXT AS metadata,
    o.scope::TEXT AS scope,
    o.stream_id AS stream_uuid,
    o.partition_number::INTEGER AS partition_num,
    o.attempts AS attempts,
    o.status AS status,
    CASE
      WHEN o.message_id = ANY(v_new_outbox_ids) THEN 1
      ELSE 2
    END::INTEGER AS flags,
    EXTRACT(EPOCH FROM o.created_at)::BIGINT * 1000 AS sequence_order
  FROM wh_outbox o
  WHERE o.instance_id = p_instance_id
    AND o.lease_expiry > v_now
    AND (o.status & 32768) = 0
    AND (o.status & 4) != 4
    AND (o.message_id = ANY(v_new_outbox_ids) OR o.message_id = ANY(v_orphaned_outbox_ids))
  ORDER BY o.stream_id, o.created_at;

  RETURN QUERY
  SELECT
    'inbox'::VARCHAR AS source,
    i.message_id AS msg_id,
    i.handler_name::VARCHAR AS destination,
    i.event_type::VARCHAR AS envelope_type,
    i.event_data::TEXT AS envelope_data,
    i.metadata::TEXT AS metadata,
    i.scope::TEXT AS scope,
    i.stream_id AS stream_uuid,
    i.partition_number::INTEGER AS partition_num,
    i.attempts AS attempts,
    i.status AS status,
    CASE
      WHEN i.message_id = ANY(v_new_inbox_ids) THEN 1
      ELSE 2
    END::INTEGER AS flags,
    EXTRACT(EPOCH FROM i.received_at)::BIGINT * 1000 AS sequence_order
  FROM wh_inbox i
  WHERE i.instance_id = p_instance_id
    AND i.lease_expiry > v_now
    AND (i.status & 32768) = 0
    AND (i.status & 2) != 2
    AND (i.message_id = ANY(v_new_inbox_ids) OR i.message_id = ANY(v_orphaned_inbox_ids))
  ORDER BY i.stream_id, i.received_at;

END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION process_work_batch IS 'Atomic work coordination with sticky stream assignment: register/heartbeat instance, cleanup stale instances, mark completed/failed (outbox/inbox/receptor/perspective), claim orphaned work (respecting wh_active_streams ownership), partition-based stream ordering with event store integration';
