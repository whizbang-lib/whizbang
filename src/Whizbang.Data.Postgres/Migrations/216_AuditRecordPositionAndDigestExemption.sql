-- Migration: 216_AuditRecordPositionAndDigestExemption.sql
-- Date: 2026-10-20
-- Description: An audit record written on the outbox path names the stream position its event was stored at
--              (#1485).
--
--              The outbox audit builder runs when the event is queued, before the event has a version: the
--              version is assigned by the emit chain, inside store_outbox_messages. So the builder wrote 0, and
--              every audit record from the main path claimed its event sat at position 0. store_outbox_messages
--              now fills the position in after the emit chain has run, by reading the audited event's version by
--              primary key. A record is recognized by its type (EventAudited) and its payload's
--              OriginalEventId; nothing else is touched.
--
--              Cost: one primary key read of wh_event_store and one update of the just-inserted row per audit
--              record, inside the storing transaction; nothing for any other message. A record whose event is
--              stored by a later call (the stream-affinity batcher flushes per stream, so the two can travel in
--              different writes) is filled when it travels after its event, and keeps 0 when it travels first.
--
--              Number 216: 214 is taken by an open change and 215 is left for a concurrent one.
--
-- Dependencies: 190 (last word of store_outbox_messages)
-- Objects: store_outbox_messages
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- store_outbox_messages: last word 190_CollectiveOrderingHeads.sql, with the audit record position fill. Nothing
-- else in the function changes.

-- <docs>fundamentals/events/system-events#audit</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AuditRecordStreamPositionSqlTests.cs:Store_EventAndItsAuditTogether_RecordCarriesTheEventsVersionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AuditRecordStreamPositionSqlTests.cs:Store_AuditAfterItsEventInALaterWrite_RecordCarriesTheEventsVersionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/EFCoreCollectiveOrderingHeadTests.cs:StoreOutboxMessagesAsync_TwoCoordinators_LinkTheSecondToTheFirstAsync</tests>
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
  -- 190: the outbox envelope as stored, with a keyed collective's predecessor link written into its payload.
  v_envelope JSONB;
  v_predecessor_id UUID;
  v_predecessor_type TEXT;
  -- 216 (#1485): the audit records this call stores, and the event each one audits, for the position fill below.
  v_audit_record_ids UUID[] := ARRAY[]::UUID[];
  v_audited_event_ids UUID[] := ARRAY[]::UUID[];
  v_audited_event_text TEXT;
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
      NULLIF(elem->>'CoalesceGroup', '') as coalesce_group,
      -- 190: set only on a collective with an ordering key; the type its successor names as its predecessor.
      NULLIF(elem->>'CollectiveLinkType', '') as collective_link_type
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

    -- 190: a keyed collective is linked to the one stored before it on its key (its stream), in this
    -- transaction, and the key's head moves to it. The head row is locked until commit, so publishers on one
    -- key, on any instance, take turns and form one chain. A message already stored (a retry's republish, or
    -- a scheduled row stored again when due) is not linked again: that would move the head backward.
    v_envelope := COALESCE(v_msg.envelope_data, '{}'::jsonb);
    IF v_msg.collective_link_type IS NOT NULL
       AND v_msg.stream_id IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_outbox o WHERE o.message_id = v_msg.msg_id)
       AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = v_msg.msg_id) THEN
      SELECT l.predecessor_id, l.predecessor_type
        INTO v_predecessor_id, v_predecessor_type
        FROM __SCHEMA__.wh_link_collective_predecessor(v_msg.stream_id, v_msg.msg_id, v_msg.collective_link_type, p_now) l;
      IF v_predecessor_id IS NOT NULL
         AND jsonb_typeof(v_envelope -> 'p') = 'object'
         AND v_envelope #>> __COLLECTIVE_PREDECESSOR_ID_PATH__ IS NULL THEN
        v_envelope := jsonb_set(
          jsonb_set(v_envelope, __COLLECTIVE_PREDECESSOR_ID_PATH__, to_jsonb(v_predecessor_id::TEXT)),
          __COLLECTIVE_PREDECESSOR_TYPE_PATH__, to_jsonb(v_predecessor_type));
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
      v_envelope,
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

    -- 216 (#1485): an audit record built on the outbox path names the event it audits but carries position 0,
    -- because that event has no version until the emit chain stores it. Collected here and filled in below,
    -- after the chain has run. Matched by type, so no other message's payload is ever touched, and by a
    -- well-formed id, so a malformed payload is left alone rather than failing the batch.
    IF v_was_new
       AND NOT COALESCE(v_msg.is_event, false)
       AND split_part(v_msg.message_type, ',', 1) = __AUDIT_RECORD_TYPE__
       AND jsonb_typeof(v_envelope #> __AUDIT_ORIGINAL_STREAM_POSITION_PATH__) = 'number' THEN
      v_audited_event_text := v_envelope #>> __AUDIT_ORIGINAL_EVENT_ID_PATH__;
      IF v_audited_event_text ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' THEN
        v_audit_record_ids := array_append(v_audit_record_ids, v_msg.msg_id);
        v_audited_event_ids := array_append(v_audited_event_ids, v_audited_event_text::UUID);
      END IF;
    END IF;

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

  -- 216 (#1485): each audit record stored by this call takes its event's version, read by primary key. The
  -- chain above has stored the events this call carried; an event stored by an earlier call is found too. An
  -- event not yet stored (its write is still to come) leaves the record at 0.
  IF cardinality(v_audit_record_ids) > 0 THEN
    UPDATE __SCHEMA__.wh_outbox o
    SET event_data = jsonb_set(o.event_data::jsonb, __AUDIT_ORIGINAL_STREAM_POSITION_PATH__, to_jsonb(es.version::BIGINT))
    FROM unnest(v_audit_record_ids, v_audited_event_ids) AS a(record_id, audited_event_id)
    JOIN __SCHEMA__.wh_event_store es ON es.event_id = a.audited_event_id
    WHERE o.message_id = a.record_id;
  END IF;

  IF cardinality(v_notify_outbox_streams) > 0 THEN
    PERFORM __SCHEMA__.notify_instance_owners(__CATEGORY_OUTBOX__, v_notify_outbox_streams);
  END IF;
  IF cardinality(v_notify_persp_streams) > 0 THEN
    PERFORM __SCHEMA__.notify_instance_owners(__CATEGORY_PERSPECTIVE__, v_notify_persp_streams);
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.store_outbox_messages(JSONB, UUID, TIMESTAMPTZ, TIMESTAMPTZ, INTEGER) IS
  'Stores new outbox messages (149, 161), links each keyed collective to the one stored before it on its key '
  '(190, #1003), and fills each audit record''s stream position from its stored event (216, #1485), in the same '
  'transaction.';
