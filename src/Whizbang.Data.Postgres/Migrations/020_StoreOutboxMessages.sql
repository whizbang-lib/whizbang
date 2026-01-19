-- Migration: 020_StoreOutboxMessages.sql
-- Date: 2025-12-25
-- Description: Creates store_outbox_messages function for inserting new outbox messages with immediate lease.
--              Returns message IDs for marking as "NewlyStored" in orchestrator response.
-- Dependencies: 001-019 (requires wh_outbox, wh_active_streams tables, compute_partition function)

CREATE OR REPLACE FUNCTION __SCHEMA__.store_outbox_messages(
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
DECLARE
  v_msg RECORD;
  v_partition INTEGER;
  v_was_new BOOLEAN;
BEGIN
  FOR v_msg IN
    SELECT
      (elem->>'MessageId')::UUID as msg_id,
      elem->>'Destination' as destination,
      elem->>'MessageType' as message_type,
      elem->>'EnvelopeType' as envelope_type,
      elem->'Envelope' as envelope_data,
      elem->'Metadata' as metadata,
      elem->'Scope' as scope,
      (elem->>'StreamId')::UUID as stream_id,
      (elem->>'IsEvent')::BOOLEAN as is_event
    FROM jsonb_array_elements(p_messages) as elem
  LOOP
    -- Calculate partition for stream-based load balancing
    IF v_msg.stream_id IS NOT NULL THEN
      v_partition := __SCHEMA__.compute_partition(v_msg.stream_id, p_partition_count);
    ELSE
      v_partition := NULL;
    END IF;

    -- Insert message with immediate lease (ON CONFLICT for idempotency)
    INSERT INTO wh_outbox (
      message_id,
      destination,
      message_type,
      envelope_type,
      event_data,
      metadata,
      scope,
      stream_id,
      partition_number,
      is_event,
      status,
      attempts,
      created_at,
      instance_id,
      lease_expiry
    ) VALUES (
      v_msg.msg_id,
      v_msg.destination,
      v_msg.message_type,
      v_msg.envelope_type,
      COALESCE(v_msg.envelope_data, '{}'::jsonb),
      COALESCE(v_msg.metadata, '{}'::jsonb),
      COALESCE(v_msg.scope, 'null'::jsonb),
      v_msg.stream_id,
      v_partition,
      COALESCE(v_msg.is_event, false),
      1,  -- Stored flag
      0,  -- Initial attempts
      p_now,
      p_instance_id,  -- Immediate lease
      p_lease_expiry
    )
    ON CONFLICT ON CONSTRAINT wh_outbox_pkey DO NOTHING;

    -- Check if insert succeeded (ROW_COUNT = 1 means new row)
    GET DIAGNOSTICS v_was_new = ROW_COUNT;

    -- Update active streams for stream ownership tracking
    IF v_msg.stream_id IS NOT NULL THEN
      INSERT INTO __SCHEMA__.wh_active_streams (
        stream_id,
        assigned_instance_id,
        lease_expiry,
        partition_number,
        last_activity_at
      ) VALUES (
        v_msg.stream_id,
        p_instance_id,
        p_lease_expiry,
        v_partition,
        p_now
      )
      ON CONFLICT ON CONSTRAINT wh_active_streams_pkey DO UPDATE SET
        assigned_instance_id = p_instance_id,
        lease_expiry = p_lease_expiry,
        last_activity_at = p_now;
    END IF;

    RETURN QUERY SELECT v_msg.msg_id AS message_id, v_msg.stream_id AS stream_id, v_was_new AS was_newly_created;
  END LOOP;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.store_outbox_messages IS
'Stores new outbox messages with immediate lease to current instance. Calculates partition for load balancing, updates active streams for ownership tracking. Returns message IDs for NewlyStored flag in orchestrator response.';
