-- Migration: 020_StoreOutboxMessages.sql
-- Date: 2025-12-25
-- Description: Creates store_outbox_messages function for inserting new outbox messages with immediate lease.
--              Returns message IDs for marking as "NewlyStored" in orchestrator response.
-- Dependencies: 001-019 (requires wh_outbox, wh_active_streams tables, compute_partition function)

SELECT __SCHEMA__.drop_all_overloads('store_outbox_messages');

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
#variable_conflict use_column
DECLARE
  v_msg RECORD;
  v_partition INTEGER;
  v_was_new BOOLEAN;
  v_inserted_event_ids UUID[] := ARRAY[]::UUID[];
BEGIN
  IF jsonb_array_length(p_messages) = 0 THEN RETURN; END IF;

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
    -- Sort by stream_id so the UPSERT on wh_active_streams below acquires row locks in
    -- a canonical order across all concurrent callers — prevents A→B vs B→A deadlock
    -- cycles. NULLS FIRST keeps the null-stream path stable. msg_id as tiebreaker for
    -- determinism within the same stream.
    ORDER BY (elem->>'StreamId')::UUID NULLS FIRST, (elem->>'MessageId')::UUID
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

    -- Collect inserted-event ids for the event-store chain below. Only events with a
    -- non-null stream_id participate (matches commit_handler_result's filter exactly).
    IF v_was_new AND COALESCE(v_msg.is_event, false) AND v_msg.stream_id IS NOT NULL THEN
      v_inserted_event_ids := array_append(v_inserted_event_ids, v_msg.msg_id);
    END IF;

    -- Stream ownership pinning (Phase H step 6 slice 1). UPSERT into wh_active_streams
    -- on first event for the stream — first-write-wins via ON CONFLICT DO NOTHING.
    -- Subsequent stores by other instances do NOT steal ownership; they no-op on the
    -- conflict check. The deterministic stream-id ordering at the FOR loop keeps row-
    -- lock acquisition canonical across concurrent callers, blocking the 40P01 cycle
    -- class that prompted the original deletion of this UPSERT.
    -- The local UUID/INTEGER variables avoid plpgsql FOR-record field-name shadowing
    -- that would otherwise make `stream_id` ambiguous to the planner.
    IF v_was_new AND v_msg.stream_id IS NOT NULL AND p_instance_id IS NOT NULL THEN
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

    RETURN QUERY SELECT v_msg.msg_id AS message_id, v_msg.stream_id AS stream_id, v_was_new AS was_newly_created;
  END LOOP;

  -- Phase 4.5A-equivalent: copy newly-inserted outbox events into wh_event_store and
  -- create wh_perspective_events rows for matching perspectives. Without this step,
  -- locally-emitted events (from receptors invoked via Dispatcher's strategy flush
  -- path, NOT via InboxDispatchWorker → commit_handler_result) sit forever in
  -- wh_outbox and never reach perspective consumers — UI shows "no activity" because
  -- read models are never updated.
  --
  -- When the strategy flush calls us with NULL p_instance_id / p_lease_expiry, pass
  -- those through unchanged. The created wh_perspective_events rows then have NULL
  -- instance_id and NULL lease_expiry, which claim_orphaned_perspective_events claims
  -- on the next ClaimWorker tick (its filter is `instance_id IS NULL OR lease_expiry
  -- < now`). Defaulting to a placeholder UUID + future lease here strands the rows
  -- in unclaimable state forever.
  IF cardinality(v_inserted_event_ids) > 0 THEN
    PERFORM __SCHEMA__._emit_event_store_chain(
      v_inserted_event_ids,
      p_instance_id,
      p_lease_expiry,
      p_now,
      p_partition_count  -- Phase H step 6 slice 2: thread partition_count for wh_perspective_events.partition_number
    );
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.store_outbox_messages IS
'Stores new outbox messages. Optionally with immediate lease (when p_instance_id + p_lease_expiry are non-null) or without lease (NULL params — claim_orphaned_outbox picks them up next tick). After inserting, calls _emit_event_store_chain for newly-inserted events with stream_id so locally-dispatched events reach wh_event_store + wh_perspective_events without going through commit_handler_result. NULL instance_id / lease_expiry pass through to the chain so claim_orphaned_perspective_events can claim the new rows on the next tick. Returns (message_id, stream_id, was_newly_created) per row.';
