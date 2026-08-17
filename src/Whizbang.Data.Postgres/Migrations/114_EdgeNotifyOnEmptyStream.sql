-- Migration: 114_EdgeNotifyOnEmptyStream.sql
-- Date: 2026-08-18
-- Description: Replace the v0.686.1 cold-only store NOTIFY gate with empty→non-empty
--              queue-edge wake semantics (proposal: notify-on-the-true-edge).
--
--              The cold-only gate notified only when the store PINNED the stream (its first
--              store ever); hot streams were silent, on the assumption that "the pinned owner's
--              worker picks up new rows on its own claim cycle". Independently, ClaimWorker
--              relaxes that claim cycle to NotifyHealthyPollingIntervalMilliseconds (5 s;
--              ~10 s bus-wired backstop) on the assumption that "NOTIFY carries all wakeups".
--              Neither side covers hot streams: an interactive stream (idle between hops)
--              pays 0-5 s per hop, while a bulk import never notices because its claim loop
--              always has work in hand.
--
--              New rule, per category: the doorbell rings when a store creates the stream's
--              FIRST pending row — the queue's empty→non-empty transition — judged by the
--              SAME eligibility predicate the drain fetch uses (minus the lease dimension,
--              which is who-may-drain, not is-pending):
--                outbox:      processed_at IS NULL AND published_at IS NULL AND schedule-eligible
--                inbox:       processed_at IS NULL AND schedule-eligible
--                perspective: wh_perspective_events.processed_at IS NULL
--              Rows piled behind pending work stay silent (the bulk-import protection is
--              preserved BY CONSTRUCTION — same ~1 notify per stream per burst as the cold
--              gate); a drained-to-empty stream re-arms the edge, so resume-after-idle rings
--              instantly regardless of how long the stream has existed.
--
--              MVCC lost-wakeup guard: the emptiness probe is a LOCKING read (FOR SHARE).
--              A plain read racing a concurrent completion of the stream's last pending row
--              can see the stale "still pending" version and stay silent while the drain's
--              final refetch snapshot predates this store's commit — nothing would ever ring.
--              FOR SHARE conflicts with the completion UPDATE's row lock, so the probe blocks,
--              re-evaluates after the commit, sees the queue empty, and rings. Producer-first
--              ordering needs no lock: the completion and final refetch then both follow this
--              store's commit, so the refetch sees the new row (the drain's refetch-until-empty
--              loop is the consumer-side double-check).
--
--              The 'perspective' doorbell is precise, not a proxy: it rings only when the
--              stream's perspective queue was empty before the call AND this call's emit
--              chain actually created work items (association-less event types create none —
--              ringing for them on every store would be the v0.686 storm on a new channel).
--
--              wh_active_streams pinning is unchanged (ownership/routing is a separate axis);
--              notify_instance_owners routing (Step 1 pinned owner / Step 2 deterministic
--              cold-start target) is unchanged.
-- Dependencies: 062 (the previous last word on these functions — verbatim base), 091/096
--              (the drain-fetch predicates this file's probes mirror), 045 (notify routing).
--              Re-run note: this file is now the LAST word on store_outbox_messages /
--              store_inbox_messages — earlier files (020, 021, 029, 046, 062) also define
--              them, so ANY hash-driven re-run of those must be followed by this file
--              restoring the edge-notify versions. Ledger order guarantees that.

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
  -- 114 edge-notify state: streams probed once per call (first encounter, BEFORE this
  -- call inserts for them), the probed-empty subsets per category, and the notify sets.
  v_probed_streams UUID[] := ARRAY[]::UUID[];
  v_empty_outbox_streams UUID[] := ARRAY[]::UUID[];
  v_empty_persp_streams UUID[] := ARRAY[]::UUID[];
  v_notify_outbox_streams UUID[] := ARRAY[]::UUID[];
  v_notify_persp_streams UUID[] := ARRAY[]::UUID[];
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

    -- 114: emptiness probe, once per distinct stream per call, BEFORE this call's first
    -- insert for that stream (the loop is stream-ordered, so first encounter precedes all
    -- of the stream's inserts). FOR SHARE serializes against an in-flight completion of
    -- the last pending row — see the header's MVCC lost-wakeup guard.
    IF v_msg.stream_id IS NOT NULL AND NOT (v_msg.stream_id = ANY(v_probed_streams)) THEN
      v_probed_streams := array_append(v_probed_streams, v_msg.stream_id);

      PERFORM 1 FROM __SCHEMA__.wh_outbox o
        WHERE o.stream_id = v_msg.stream_id
          AND o.processed_at IS NULL
          AND o.published_at IS NULL
          AND (o.scheduled_for IS NULL OR o.scheduled_for <= p_now)
        LIMIT 1
        FOR SHARE OF o;
      IF NOT FOUND THEN
        v_empty_outbox_streams := array_append(v_empty_outbox_streams, v_msg.stream_id);
      END IF;

      PERFORM 1 FROM __SCHEMA__.wh_perspective_events pe
        WHERE pe.stream_id = v_msg.stream_id
          AND pe.processed_at IS NULL
        LIMIT 1
        FOR SHARE OF pe;
      IF NOT FOUND THEN
        v_empty_persp_streams := array_append(v_empty_persp_streams, v_msg.stream_id);
      END IF;
    END IF;

    INSERT INTO __SCHEMA__.wh_outbox (
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

    -- 114: a genuinely-new, drainable-now row on a probed-empty stream is the
    -- empty→non-empty edge. Future-scheduled rows are not drainable now, so they
    -- do not ring (they surface via the scheduled-retry NOTIFY / poll when due).
    IF v_was_new AND v_msg.stream_id IS NOT NULL
       AND (v_msg.scheduled_for IS NULL OR v_msg.scheduled_for <= p_now)
       AND v_msg.stream_id = ANY(v_empty_outbox_streams)
       AND NOT (v_msg.stream_id = ANY(v_notify_outbox_streams)) THEN
      v_notify_outbox_streams := array_append(v_notify_outbox_streams, v_msg.stream_id);
    END IF;

    -- Pinning is ownership/routing, unchanged by 114 (the cold-notify tracking that
    -- used to ride this block is gone — the notify decision now keys on queue state).
    IF v_was_new AND v_msg.stream_id IS NOT NULL AND p_instance_id IS NOT NULL THEN
      INSERT INTO __SCHEMA__.wh_active_streams
        (stream_id, partition_number, assigned_instance_id, last_activity_at)
      VALUES
        (v_msg.stream_id, COALESCE(v_partition, 0), p_instance_id, p_now)
      ON CONFLICT (stream_id) DO NOTHING;
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

    -- 114: the perspective edge is decided AFTER the emit chain, so it rings only for
    -- streams whose perspective queue was empty before this call AND for which this
    -- call actually created work items (our own inserts are visible in-transaction).
    -- Association-less event types create no work and therefore never ring here.
    IF cardinality(v_empty_persp_streams) > 0 THEN
      SELECT COALESCE(array_agg(DISTINCT pe.stream_id), ARRAY[]::UUID[])
        INTO v_notify_persp_streams
        FROM __SCHEMA__.wh_perspective_events pe
        WHERE pe.stream_id = ANY(v_empty_persp_streams)
          AND pe.processed_at IS NULL;
    END IF;
  END IF;

  IF cardinality(v_notify_outbox_streams) > 0 THEN
    PERFORM __SCHEMA__.notify_instance_owners('outbox', v_notify_outbox_streams);
  END IF;
  IF cardinality(v_notify_persp_streams) > 0 THEN
    PERFORM __SCHEMA__.notify_instance_owners('perspective', v_notify_persp_streams);
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.store_outbox_messages IS
'Stores new outbox messages (114: edge-notify — the doorbell rings when a store creates a stream''s first pending row, judged by the drain-fetch eligibility predicate with a FOR SHARE probe; piled-up rows stay silent, drained streams re-arm the edge). Optionally with immediate lease (p_instance_id + p_lease_expiry non-null) or without (NULL params — claim_orphaned_outbox picks them up). Calls _emit_event_store_chain for newly-inserted events; the perspective doorbell rings only when the chain created work for a previously-empty perspective queue. Returns (message_id, stream_id, was_newly_created) per row.';

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
  v_was_new INTEGER;  -- ROW_COUNT returns integer
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
    INSERT INTO __SCHEMA__.wh_message_deduplication (message_id, first_seen_at)
    VALUES (v_msg.msg_id, p_now)
    ON CONFLICT ON CONSTRAINT wh_message_deduplication_pkey DO NOTHING;

    GET DIAGNOSTICS v_was_new = ROW_COUNT;

    IF v_was_new = 1 THEN
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

      RETURN QUERY SELECT v_msg.msg_id AS message_id, v_msg.stream_id AS stream_id, (v_was_new = 1) AS was_newly_created;
    END IF;  -- Close IF v_was_new = 1 THEN
  END LOOP;

  IF cardinality(v_notify_inbox_streams) > 0 THEN
    PERFORM __SCHEMA__.notify_instance_owners('inbox', v_notify_inbox_streams);
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.store_inbox_messages IS
'Stores new inbox messages without lease (114: edge-notify — the doorbell rings when a store creates a stream''s first pending row, judged by the drain-fetch eligibility predicate with a FOR SHARE probe; piled-up rows stay silent, drained streams re-arm the edge). Calculates partition for load balancing, updates active streams for ownership tracking. Returns message IDs for NewlyStored flag in orchestrator response.';
