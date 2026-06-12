-- Migration: 021_StoreInboxMessages.sql
-- Date: 2025-12-25
-- Description: Creates store_inbox_messages function for inserting new inbox messages without lease.
--              Returns message IDs for marking as "NewlyStored" in orchestrator response.
-- Dependencies: 001-020 (requires wh_inbox, wh_active_streams tables, compute_partition function)

SELECT __SCHEMA__.drop_all_overloads('store_inbox_messages');

CREATE OR REPLACE FUNCTION __SCHEMA__.store_inbox_messages(
  p_messages JSONB,
  p_instance_id UUID,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_partition_count INTEGER
) RETURNS TABLE(
  message_id UUID,
  stream_id UUID,
  was_newly_created BOOLEAN
) AS $$
#variable_conflict use_column
DECLARE
  v_msg RECORD;
  v_partition INTEGER;
  v_was_new INTEGER;  -- Changed from BOOLEAN - ROW_COUNT returns integer
  -- v0.686: stream_ids of newly-stored rows are collected for end-of-call
  -- notify_instance_owners — wakes consumers immediately instead of waiting on
  -- the safety-net poll. notify_instance_owners (mig 045) early-returns when
  -- every stream is already pinned, so cost is sub-millisecond on the hot path.
  v_new_stream_ids UUID[] := ARRAY[]::UUID[];
BEGIN
  IF jsonb_array_length(p_messages) = 0 THEN RETURN; END IF;

  FOR v_msg IN
    SELECT
      (elem->>'MessageId')::UUID as msg_id,
      elem->>'HandlerName' as handler_name,
      elem->>'EnvelopeType' as envelope_type,
      elem->>'MessageType' as message_type,
      elem->'Envelope' as envelope_data,
      elem->'Metadata' as metadata,
      elem->'Scope' as scope,
      (elem->>'StreamId')::UUID as stream_id,
      (elem->>'IsEvent')::BOOLEAN as is_event,
      -- Slice 26: source identity. Real cross-service envelopes carry these fields
      -- (receive-boundary gate enforces); local-emit / handler paths and tests omit
      -- them, in which case they default to the local service's identity + sequence 0.
      (elem->>'SourceServiceId')::UUID as source_service_id,
      (elem->>'SourceCommitSequence')::BIGINT as source_commit_sequence
    FROM jsonb_array_elements(p_messages) as elem
    -- Sort by stream_id so the UPSERT on wh_active_streams below acquires row locks in
    -- a canonical order across all concurrent callers — prevents A→B vs B→A deadlock
    -- cycles. NULLS FIRST keeps the null-stream path stable. msg_id as tiebreaker for
    -- determinism within the same stream.
    ORDER BY (elem->>'StreamId')::UUID NULLS FIRST, (elem->>'MessageId')::UUID
  LOOP
    -- Deduplication: Try to insert into deduplication table first
    -- If message_id already exists, this returns 0 rows and we skip the inbox insert
    INSERT INTO wh_message_deduplication (message_id, first_seen_at)
    VALUES (v_msg.msg_id, p_now)
    ON CONFLICT ON CONSTRAINT wh_message_deduplication_pkey DO NOTHING;

    GET DIAGNOSTICS v_was_new = ROW_COUNT;

    -- Only proceed if deduplication insert succeeded (message is new)
    IF v_was_new = 1 THEN
      -- Calculate partition for stream-based load balancing
      IF v_msg.stream_id IS NOT NULL THEN
        v_partition := __SCHEMA__.compute_partition(v_msg.stream_id, p_partition_count);
      ELSE
        v_partition := NULL;
      END IF;

      -- Insert message without lease — WorkCoordinatorPublisherWorker claims via claim_orphaned_inbox
      INSERT INTO wh_inbox (
      message_id,
      handler_name,
      message_type,
      event_data,
      metadata,
      scope,
      stream_id,
      partition_number,
      is_event,
      status,
      attempts,
      received_at,
      instance_id,
      lease_expiry,
      source_service_id,
      source_commit_sequence
    ) VALUES (
      v_msg.msg_id,
      v_msg.handler_name,
      v_msg.message_type,  -- FIXED: Use message_type instead of envelope_type
      COALESCE(v_msg.envelope_data, '{}'::jsonb),
      COALESCE(v_msg.metadata, '{}'::jsonb),
      COALESCE(v_msg.scope, 'null'::jsonb),
      v_msg.stream_id,
      v_partition,
      COALESCE(v_msg.is_event, false),
      1,  -- Stored flag
      0,  -- Initial attempts
      p_now,
      NULL,  -- No lease — immediately claimable by WorkCoordinatorPublisherWorker
      NULL,
      -- Slice 26: source identity. Real cross-service envelopes carry these; otherwise
      -- default to local service's identity. wh_service_config has exactly one row.
      COALESCE(v_msg.source_service_id, (SELECT service_id FROM __SCHEMA__.wh_service_config LIMIT 1)),
      COALESCE(v_msg.source_commit_sequence, 0)
    )
    ON CONFLICT ON CONSTRAINT wh_inbox_pkey DO NOTHING;

      -- Stream ownership pinning (Phase H step 6 slice 1). UPSERT into wh_active_streams
      -- on first event for the stream — first-write-wins via ON CONFLICT DO NOTHING.
      -- Subsequent stores by other instances do NOT steal ownership. Local variables
      -- avoid plpgsql FOR-record field-name shadowing.
      IF v_msg.stream_id IS NOT NULL AND p_instance_id IS NOT NULL THEN
        DECLARE
          v_pin_stream UUID := v_msg.stream_id;
          v_pin_partition INTEGER := COALESCE(v_partition, 0);
        BEGIN
          INSERT INTO __SCHEMA__.wh_active_streams
            (stream_id, partition_number, assigned_instance_id, last_activity_at)
          VALUES
            (v_pin_stream, v_pin_partition, p_instance_id, p_now)
          ON CONFLICT (stream_id) DO NOTHING;
        END;
      END IF;

      -- Collect newly-stored stream_ids (non-null only) for end-of-call NOTIFY. Null
      -- stream_ids skip routing because notify_instance_owners cannot compute a
      -- partition for them; polling backstop covers the stream-less inbox rows.
      IF v_msg.stream_id IS NOT NULL THEN
        v_new_stream_ids := array_append(v_new_stream_ids, v_msg.stream_id);
      END IF;

      -- Return message as newly created (deduplication succeeded)
      RETURN QUERY SELECT v_msg.msg_id AS message_id, v_msg.stream_id AS stream_id, (v_was_new = 1) AS was_newly_created;
    END IF;  -- Close IF v_was_new = 1 THEN
  END LOOP;

  -- v0.686: emit NOTIFY for newly-stored stream-bearing rows so WorkCoordinatorPublisherWorker
  -- (the inbox dispatcher) wakes immediately instead of waiting on the safety-net poll.
  -- notify_instance_owners emits one pg_notify per unique owner across the input set
  -- and short-circuits Step 2 when every stream is already pinned (mig 045 perf rewrite).
  IF cardinality(v_new_stream_ids) > 0 THEN
    PERFORM __SCHEMA__.notify_instance_owners('inbox', v_new_stream_ids);
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.store_inbox_messages IS
'Stores new inbox messages without lease (immediately claimable by WorkCoordinatorPublisherWorker). Calculates partition for load balancing, updates active streams for ownership tracking. Returns message IDs for NewlyStored flag in orchestrator response.';
