-- Migration: 121_RedeliveryObservationCounter.sql
-- Date: 2026-08-20
-- Description: Durable redelivery-observation counter for non-count-based poison detection
--              (transport-topology arc phase 8.5, layer 2).
--
--              WHY. A live Standard-namespace probe confirmed the emulator spike: on
--              SESSION-enabled broker entities, a lock lost to connection death does NOT
--              increment the broker's delivery counter (an explicit abandon does; a NON-session
--              lock loss does). Command inboxes are session-enabled by default, so the broker's
--              MaxDeliveryCount valve — and every transport branch reading the same counter — is
--              structurally unreachable under a consumer-death storm. Those messages are hostage,
--              not poison, and NOTHING bounded the loop.
--
--              WHAT. wh_message_deduplication is already the store-side idempotency record for
--              every message id this service has ever received, and store_inbox_messages already
--              writes to it on every delivery. Counting redeliveries there is therefore free —
--              no new table, no extra round trip — and the count is durable across the process
--              death that defines the failure mode.
--
--              Deliberately NOT wh_inbox.attempts: that counts PROCESSING attempts on a claimed
--              row and feeds wh_dead_letters.attempts_when_dlq. A redelivery is a different
--              event from a processing attempt, and conflating them corrupts both signals.
--
--              NO CONTRACT CHANGE. store_inbox_messages' signature and returned rowset are
--              byte-identical to 114 — still exactly one row per NEWLY stored message, still
--              (message_id, stream_id, was_newly_created). Only the dedup INSERT's ON CONFLICT
--              arm changes, from DO NOTHING to DO UPDATE ... + 1. Callers that want the counts
--              read wh_message_deduplication directly in the same round trip; the store's own
--              "did I create this?" answer is untouched, so nothing downstream shifts.
--
--              NO overlap with the dead-letter tables: this file touches only
--              wh_message_deduplication and store_inbox_messages. The layer-2 quarantine it
--              enables calls the EXISTING move_to_dead_letters() contract unchanged, so whatever
--              shape wh_dead_letters has at that point is inherited, never redefined here.
--
-- Dependencies: 114_EdgeNotifyOnEmptyStream.sql (last-word store_inbox_messages),
--               000_MigrationTracking.sql (drop_all_overloads)

-- The column also lives in MessageDeduplicationSchema (rendered as ADD COLUMN IF NOT EXISTS by
-- the schema builder, which runs before migrations). Repeated here so this migration is
-- self-sufficient on any runner that applies migrations against an already-built schema.
ALTER TABLE __SCHEMA__.wh_message_deduplication
  ADD COLUMN IF NOT EXISTS observation_count INTEGER NOT NULL DEFAULT 1;

COMMENT ON COLUMN __SCHEMA__.wh_message_deduplication.observation_count IS
'Durable redelivery-observation counter (phase 8.5 poison detection layer 2): how many times the broker has handed this service the same message id, including the first. Incremented by store_inbox_messages on every already-seen delivery. NOT a processing-attempt count — see wh_inbox.attempts.';

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
  v_observations INTEGER;  -- 1 on first sight; N on the Nth redelivery
  v_probed_streams UUID[] := ARRAY[]::UUID[];
  v_empty_inbox_streams UUID[] := ARRAY[]::UUID[];
  v_notify_inbox_streams UUID[] := ARRAY[]::UUID[];
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
    -- 121: DO UPDATE (was DO NOTHING) so a redelivery is COUNTED rather than silently swallowed.
    -- RETURNING gives the post-write count on both arms, so newness is read from the value
    -- (= 1) instead of from ROW_COUNT, which DO UPDATE would report as 1 either way.
    INSERT INTO __SCHEMA__.wh_message_deduplication AS dedup
      (message_id, first_seen_at, observation_count)
    VALUES (v_msg.msg_id, p_now, 1)
    ON CONFLICT ON CONSTRAINT wh_message_deduplication_pkey DO UPDATE
      SET observation_count = dedup.observation_count + 1
    RETURNING dedup.observation_count INTO v_observations;

    IF v_observations = 1 THEN
      IF v_msg.stream_id IS NOT NULL THEN
        v_partition := __SCHEMA__.compute_partition(v_msg.stream_id, p_partition_count);
      ELSE
        v_partition := NULL;
      END IF;

      -- 114: emptiness probe — see store_outbox_messages; inbox pending = not processed
      -- and schedule-eligible (the drain-fetch predicate minus the lease dimension).
      IF v_msg.stream_id IS NOT NULL AND NOT (v_msg.stream_id = ANY(v_probed_streams)) THEN
        v_probed_streams := array_append(v_probed_streams, v_msg.stream_id);

        PERFORM 1 FROM __SCHEMA__.wh_inbox i
          WHERE i.stream_id = v_msg.stream_id
            AND i.processed_at IS NULL
            AND (i.scheduled_for IS NULL OR i.scheduled_for <= p_now)
          LIMIT 1
          FOR SHARE OF i;
        IF NOT FOUND THEN
          v_empty_inbox_streams := array_append(v_empty_inbox_streams, v_msg.stream_id);
        END IF;
      END IF;

      INSERT INTO __SCHEMA__.wh_inbox (
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
      v_msg.message_type,
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

      IF v_msg.stream_id IS NOT NULL
         AND v_msg.stream_id = ANY(v_empty_inbox_streams)
         AND NOT (v_msg.stream_id = ANY(v_notify_inbox_streams)) THEN
        v_notify_inbox_streams := array_append(v_notify_inbox_streams, v_msg.stream_id);
      END IF;

      -- Pinning is ownership/routing, unchanged by 114.
      IF v_msg.stream_id IS NOT NULL AND p_instance_id IS NOT NULL THEN
        INSERT INTO __SCHEMA__.wh_active_streams
          (stream_id, partition_number, assigned_instance_id, last_activity_at)
        VALUES
          (v_msg.stream_id, COALESCE(v_partition, 0), p_instance_id, p_now)
        ON CONFLICT (stream_id) DO NOTHING;
      END IF;
      RETURN QUERY SELECT v_msg.msg_id AS message_id, v_msg.stream_id AS stream_id, TRUE AS was_newly_created;
    END IF;  -- Close IF v_observations = 1 THEN
  END LOOP;

  IF cardinality(v_notify_inbox_streams) > 0 THEN
    PERFORM __SCHEMA__.notify_instance_owners('inbox', v_notify_inbox_streams);
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.store_inbox_messages IS
'Stores new inbox messages without lease (114: edge-notify — the doorbell rings when a store creates a stream''s first pending row, judged by the drain-fetch eligibility predicate with a FOR SHARE probe; piled-up rows stay silent, drained streams re-arm the edge). Calculates partition for load balancing, updates active streams for ownership tracking. 121: the dedup write now COUNTS redeliveries into wh_message_deduplication.observation_count (ON CONFLICT DO UPDATE, was DO NOTHING) so poison detection layer 2 can bound a redelivery loop the broker delivery counter cannot see on a session-enabled entity; the returned rowset is unchanged — still one row per NEWLY stored message. Returns (message_id, stream_id, was_newly_created).';
