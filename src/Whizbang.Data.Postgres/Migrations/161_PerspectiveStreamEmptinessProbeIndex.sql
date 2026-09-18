-- Migration: 161_PerspectiveStreamEmptinessProbeIndex.sql
-- Date: 2026-09-17
-- Description: The store's perspective emptiness probe answers from an index instead of scanning,
--              on the stream state where it was scanning: a stream whose work is all done.
--
--              160 fixed this defect on wh_outbox and recorded that the other two probes in the
--              same loop were fine, wh_perspective_events being "served by idx_perspective_event_order
--              ... measured at three buffers". That measurement was taken on a stream the table had
--              never held. It is correct for that case and it is the wrong case to have measured.
--              This migration corrects that note.
--
--              The probe:
--
--                PERFORM 1 FROM __SCHEMA__.wh_perspective_events pe
--                  WHERE pe.stream_id = v_msg.stream_id AND pe.processed_at IS NULL
--                  LIMIT 1 FOR SHARE OF pe SKIP LOCKED;
--
--              Three stream states, and only one of them was ever measured:
--
--                stream state                       blocks before    blocks after
--                never held by the table (fresh)             2              2
--                holding pending work                        6              6
--                every row processed (drained)             359              2
--
--              A drained stream is not an edge case. It is what every long-lived stream becomes,
--              and it stays that way between bursts of work, so it is the state the probe is in
--              most of the time. On that stream the probe read the whole table -- 359 blocks and
--              20,400 rows filtered on a 20,400-row fixture -- to answer that one stream has
--              nothing pending. The cost grows with the table, not with the stream.
--
--              The mechanism is the one 160 describes, reached by a different route. There the
--              predicate matched no index at all. Here an index leads with stream_id, but under a
--              bare LIMIT 1 the planner prices a sequential scan as though the first row it reads
--              will match: processed_at IS NULL is true of nearly every row, so it expects a hit
--              almost immediately. On a drained stream there is no hit, so "almost immediately"
--              becomes the entire table. As before, the answer the probe most often wants is the
--              one that scans to exhaustion.
--
--              Two things were tried and did not work, recorded so they are not tried again:
--              adding the partial index alone (the planner still chose the sequential scan), and
--              extended statistics on (stream_id, processed_at) (dependency statistics do not
--              capture an IS NULL test, and an MCV list over stream ids is not viable). The plan
--              has to be removed rather than out-costed, which is what ordering by the index key
--              does: a sequential scan would have to sort before it could answer LIMIT 1.
--
--              The ORDER BY changes no result. stream_id is pinned by the equality, and the query
--              only ever asks whether a row exists. event_id is in the clause because ordering by
--              stream_id alone is dropped as redundant for exactly that reason.
--
--              The second probe in the same function -- the array form that folds the empty-stream
--              list back down after the emit chain -- needs only the index. It has no LIMIT, so the
--              planner never made the optimistic estimate, and it takes an Index Only Scan at two
--              blocks as soon as the index exists. It is unchanged.
--
--              FOR SHARE OF ... SKIP LOCKED is unchanged, for the reason 160 gives: it serializes
--              against an in-flight completion of a stream's last pending row and is the MVCC
--              lost-wakeup guard 114 and 140 exist for.
--
-- Dependencies: 149 (store_outbox_messages), 160 (the same defect on wh_outbox, and the note this corrects)
-- Objects: idx_perspective_stream_pending, store_outbox_messages

CREATE INDEX IF NOT EXISTS idx_perspective_stream_pending
  ON __SCHEMA__.wh_perspective_events (stream_id, event_id)
  WHERE processed_at IS NULL;
COMMENT ON INDEX __SCHEMA__.idx_perspective_stream_pending IS
  'Pending perspective events by stream (161), so the store''s emptiness probe answers "has this '
  'stream anything pending" with one index descent instead of a scan. event_id is in the key so the '
  'probe can order by it: under a bare LIMIT 1 the planner prefers a sequential scan it expects to '
  'satisfy from the first row, which is exactly wrong on a stream whose work is all done. Partial on '
  'processed_at IS NULL so the index tracks pending work rather than settled history.';

-- store_outbox_messages: last word 149_MessagePriority.sql, with the perspective probe ordered so
-- the index above is reachable. Nothing else in the function changes.

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
      (elem->>__ENVELOPE_FIELD_MESSAGE_ID__)::UUID as msg_id,
      elem->>'Destination' as destination,
      elem->>'MessageType' as message_type,
      elem->>'EnvelopeType' as envelope_type,
      elem->'Envelope' as envelope_data,
      elem->'Metadata' as metadata,
      elem->'Scope' as scope,
      (elem->>__ENVELOPE_FIELD_STREAM_ID__)::UUID as stream_id,
      (elem->>'IsEvent')::BOOLEAN as is_event,
      -- 149: the priority the producer declared; zero (undeclared) and an absent field read as standard.
      COALESCE(NULLIF((elem->>'Priority')::INTEGER, 0), 150) as priority,
      -- EventFlags (062): persisted so migration 061's collective routing can see (flags & 1). Robust to
      -- numeric (default System.Text.Json enum) or [Flags] string serialization of EventFlags.
      CASE
        WHEN elem->>__ENVELOPE_FIELD_FLAGS__ IS NULL OR elem->>__ENVELOPE_FIELD_FLAGS__ = '' THEN 0
        WHEN elem->>__ENVELOPE_FIELD_FLAGS__ ~ '^[0-9]+$' THEN (elem->>__ENVELOPE_FIELD_FLAGS__)::INTEGER
        WHEN elem->>__ENVELOPE_FIELD_FLAGS__ ILIKE '%Collective%' THEN 1
        ELSE 0
      END as flags,
      NULLIF(elem->>'ScheduledFor', '')::TIMESTAMPTZ as scheduled_for,
      -- 115 tag-bound coalescing: the group a pending single belongs to (NULL for normal rows).
      NULLIF(elem->>'CoalesceGroup', '') as coalesce_group
    FROM jsonb_array_elements(p_messages) as elem
    ORDER BY (elem->>__ENVELOPE_FIELD_STREAM_ID__)::UUID NULLS FIRST, (elem->>__ENVELOPE_FIELD_MESSAGE_ID__)::UUID
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
        FOR SHARE OF o SKIP LOCKED;   -- 140: a locked pending row is being completed; treat as empty and ring
      IF NOT FOUND THEN
        v_empty_outbox_streams := array_append(v_empty_outbox_streams, v_msg.stream_id);
      END IF;

      PERFORM 1 FROM __SCHEMA__.wh_perspective_events pe
        WHERE pe.stream_id = v_msg.stream_id
          AND pe.processed_at IS NULL
        -- 161: the ORDER BY is what makes the index reachable, and it is the whole fix. Under a
        -- bare LIMIT 1 the planner prices a sequential scan as though the first row it reads will
        -- match, because processed_at IS NULL is true of nearly every row; on a stream whose work
        -- is all done there is no match at all, so it scans to exhaustion. An index alone does not
        -- change that choice and neither do extended statistics, both measured. Ordering by the
        -- index's own key removes the bad plan instead of out-costing it: a sequential scan would
        -- have to sort before it could answer. stream_id is pinned by the equality above, so the
        -- clause changes no result -- order by that column alone is dropped as redundant, which is
        -- why event_id is here too.
        ORDER BY pe.stream_id, pe.event_id
        LIMIT 1
        FOR SHARE OF pe SKIP LOCKED;  -- 140: same rule for the perspective doorbell
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
      scheduled_for,
      coalesce_group,
      priority
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
      v_msg.scheduled_for,
      v_msg.coalesce_group,
      v_msg.priority
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
    PERFORM __SCHEMA__.notify_instance_owners(__CATEGORY_OUTBOX__, v_notify_outbox_streams);
  END IF;
  IF cardinality(v_notify_persp_streams) > 0 THEN
    PERFORM __SCHEMA__.notify_instance_owners(__CATEGORY_PERSPECTIVE__, v_notify_persp_streams);
  END IF;
END;
$$ LANGUAGE plpgsql;
