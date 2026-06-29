-- Migration: 062_PersistEventFlags.sql
-- Date: 2026-06-29
-- Description: Persist the EventFlags `flags` column when storing outbox/inbox messages.
--              Migration 061 added the `flags` column to wh_outbox/wh_inbox and carries it through
--              `_emit_event_store_chain[_for_inbox]` into wh_event_store, where the `(flags & 1) = 1`
--              collective predicate creates the `__collective__` sink row. But the insert procs
--              (store_outbox_messages / store_inbox_messages, migrations 020/021) never wrote the column —
--              so locally-emitted collective events landed with flags = 0 and were never routed to the sink.
--              Collective events are the first feature to depend on the persisted flags column (composites
--              fan out by payload type, not by the column), so this gap only surfaces end-to-end.
--
--              C# sets OutboxMessage.Flags / InboxMessage.Flags (Dispatcher + TransportConsumerWorker) and
--              serializes them into the proc JSON. The reader below is robust to EventFlags serializing as a
--              numeric value (default) or a [Flags] string ("Collective", "Collective, …") depending on the
--              caller's JsonSerializerOptions.
-- Dependencies: 020, 021 (the procs redefined here), 061 (the flags column + emit-chain carry)

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
  v_cold_stream_ids UUID[] := ARRAY[]::UUID[];
  v_was_pinned INTEGER;
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
      (elem->>'IsEvent')::BOOLEAN as is_event,
      -- EventFlags (062): persisted so migration 061's collective routing can see (flags & 1). Robust to
      -- numeric (default System.Text.Json enum) or [Flags] string serialization of EventFlags.
      CASE
        WHEN elem->>'Flags' IS NULL OR elem->>'Flags' = '' THEN 0
        WHEN elem->>'Flags' ~ '^[0-9]+$' THEN (elem->>'Flags')::INTEGER
        WHEN elem->>'Flags' ILIKE '%Collective%' THEN 1
        ELSE 0
      END as flags,
      NULLIF(elem->>'ScheduledFor', '')::TIMESTAMPTZ as scheduled_for
    FROM jsonb_array_elements(p_messages) as elem
    ORDER BY (elem->>'StreamId')::UUID NULLS FIRST, (elem->>'MessageId')::UUID
  LOOP
    IF v_msg.stream_id IS NOT NULL THEN
      v_partition := __SCHEMA__.compute_partition(v_msg.stream_id, p_partition_count);
    ELSE
      v_partition := NULL;
    END IF;

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
      flags,
      status,
      attempts,
      created_at,
      instance_id,
      lease_expiry,
      scheduled_for
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
      COALESCE(v_msg.flags, 0),
      1,  -- Stored flag
      0,  -- Initial attempts
      p_now,
      p_instance_id,  -- Immediate lease
      p_lease_expiry,
      v_msg.scheduled_for
    )
    ON CONFLICT ON CONSTRAINT wh_outbox_pkey DO NOTHING;

    GET DIAGNOSTICS v_was_new = ROW_COUNT;

    IF v_was_new AND COALESCE(v_msg.is_event, false) AND v_msg.stream_id IS NOT NULL THEN
      v_inserted_event_ids := array_append(v_inserted_event_ids, v_msg.msg_id);
    END IF;

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
        GET DIAGNOSTICS v_was_pinned = ROW_COUNT;
        IF v_was_pinned = 1 THEN
          v_cold_stream_ids := array_append(v_cold_stream_ids, v_pin_stream);
        END IF;
      END;
    END IF;

    RETURN QUERY SELECT v_msg.msg_id AS message_id, v_msg.stream_id AS stream_id, v_was_new AS was_newly_created;
  END LOOP;

  IF cardinality(v_inserted_event_ids) > 0 THEN
    PERFORM __SCHEMA__._emit_event_store_chain(
      v_inserted_event_ids,
      p_instance_id,
      p_lease_expiry,
      p_now,
      p_partition_count
    );
  END IF;

  IF cardinality(v_cold_stream_ids) > 0 THEN
    PERFORM __SCHEMA__.notify_instance_owners('outbox', v_cold_stream_ids);
    IF cardinality(v_inserted_event_ids) > 0 THEN
      PERFORM __SCHEMA__.notify_instance_owners('perspective', v_cold_stream_ids);
    END IF;
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.store_outbox_messages IS
'Stores new outbox messages (062: persists the EventFlags `flags` column so collective events route to the __collective__ sink). Optionally with immediate lease (when p_instance_id + p_lease_expiry are non-null) or without lease (NULL params — claim_orphaned_outbox picks them up next tick). After inserting, calls _emit_event_store_chain for newly-inserted events with stream_id. Returns (message_id, stream_id, was_newly_created) per row.';

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
  v_cold_stream_ids UUID[] := ARRAY[]::UUID[];
  v_was_pinned INTEGER;
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
      -- EventFlags (062): same robust read as store_outbox_messages so collective events delivered
      -- cross-service via the inbox also route to the __collective__ sink.
      CASE
        WHEN elem->>'Flags' IS NULL OR elem->>'Flags' = '' THEN 0
        WHEN elem->>'Flags' ~ '^[0-9]+$' THEN (elem->>'Flags')::INTEGER
        WHEN elem->>'Flags' ILIKE '%Collective%' THEN 1
        ELSE 0
      END as flags,
      (elem->>'SourceServiceId')::UUID as source_service_id,
      (elem->>'SourceCommitSequence')::BIGINT as source_commit_sequence
    FROM jsonb_array_elements(p_messages) as elem
    ORDER BY (elem->>'StreamId')::UUID NULLS FIRST, (elem->>'MessageId')::UUID
  LOOP
    INSERT INTO wh_message_deduplication (message_id, first_seen_at)
    VALUES (v_msg.msg_id, p_now)
    ON CONFLICT ON CONSTRAINT wh_message_deduplication_pkey DO NOTHING;

    GET DIAGNOSTICS v_was_new = ROW_COUNT;

    IF v_was_new = 1 THEN
      IF v_msg.stream_id IS NOT NULL THEN
        v_partition := __SCHEMA__.compute_partition(v_msg.stream_id, p_partition_count);
      ELSE
        v_partition := NULL;
      END IF;

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
      flags,
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
      COALESCE(v_msg.flags, 0),
      1,  -- Stored flag
      0,  -- Initial attempts
      p_now,
      NULL,  -- No lease — immediately claimable by WorkCoordinatorPublisherWorker
      NULL,
      COALESCE(v_msg.source_service_id, (SELECT service_id FROM __SCHEMA__.wh_service_config LIMIT 1)),
      COALESCE(v_msg.source_commit_sequence, 0)
    )
    ON CONFLICT ON CONSTRAINT wh_inbox_pkey DO NOTHING;

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
          GET DIAGNOSTICS v_was_pinned = ROW_COUNT;
          IF v_was_pinned = 1 THEN
            v_cold_stream_ids := array_append(v_cold_stream_ids, v_pin_stream);
          END IF;
        END;
      END IF;

      RETURN QUERY SELECT v_msg.msg_id AS message_id, v_msg.stream_id AS stream_id, (v_was_new = 1) AS was_newly_created;
    END IF;  -- Close IF v_was_new = 1 THEN
  END LOOP;

  IF cardinality(v_cold_stream_ids) > 0 THEN
    PERFORM __SCHEMA__.notify_instance_owners('inbox', v_cold_stream_ids);
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.store_inbox_messages IS
'Stores new inbox messages without lease (062: persists the EventFlags `flags` column so cross-service collective events route to the __collective__ sink). Calculates partition for load balancing, updates active streams for ownership tracking. Returns message IDs for NewlyStored flag in orchestrator response.';
