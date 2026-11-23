-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 558_EventsWaitForTheirCommitOrderStamp.sql
-- Date: 2026-11-23
-- Description: Sequence integrity (#2542): the outbox can hold an event until the commit-order stamper has given it a
--              commit sequence, so it reaches its receivers with its origin position.
--
--              The outbox published an event as soon as the drain fetched it, so whenever the publish won the race
--              with the stamper the event left without a commit sequence and the receiver stored it with no origin
--              position. Each event now carries the hold its origin decided (DispatchOptions, the type's
--              [SequenceIntegrity], the configured default): none, a bounded hold, or until stamped.
--
--                * wh_outbox.sequence_hold_until: NULL for no hold, 'infinity' until stamped, and otherwise the instant
--                  a bounded hold runs out, taken from the store's clock. Nullable with no default, so adding it
--                  rewrites nothing.
--                * store_outbox_messages (271) writes it from the element's SequenceHoldMilliseconds, for a published
--                  event only.
--                * fetch_outbox_batch (151) and wh_continue_outbox_streams (172) cut each stream at its first held
--                  row: a hold that has not run out on an event whose stored row has no commit sequence. That row
--                  and every row after it stay leased where they are and come back with the next offer, so a
--                  receiver never sees a stream out of order. The test reads columns the fetch already joins (the
--                  event store row by primary key); no new index, no new scan.
--                * stamp_pending_commit_sequences (248) also rings the outbox owners of the streams it stamped that
--                  still have pending outbox rows, one probe of idx_outbox_stream_run per stamped stream, so a held
--                  event goes out when its stamp lands. A bounded hold's deadline is the fallback.
--                * wh_oldest_sequence_held_outbox: the arrival time of the oldest event an instance holds for its stamp,
--                  for the held-age metric. Walks the instance's holdings through idx_outbox_held_arrival in arrival
--                  order and stops at a bound, so it costs at most the bound whatever the outbox holds.
--
--              Number 558: the next free number after 557.
--
-- Reversal: reversible (the down file restores each function's previous definition and drops the column and the new
--           function; events held when it runs are published at the next fetch)
-- Dependencies: 271 (store_outbox_messages), 151 (fetch_outbox_batch), 172 (wh_continue_outbox_streams),
--               248 (stamp_pending_commit_sequences), 171 (idx_outbox_stream_run), 158 (idx_outbox_held_arrival)
-- Objects: wh_outbox, store_outbox_messages, fetch_outbox_batch, wh_continue_outbox_streams, stamp_pending_commit_sequences, wh_oldest_sequence_held_outbox
-- Constants: the double-underscore tokens in this file (for example __CATEGORY_OUTBOX__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

ALTER TABLE __SCHEMA__.wh_outbox ADD COLUMN IF NOT EXISTS sequence_hold_until TIMESTAMPTZ;
COMMENT ON COLUMN __SCHEMA__.wh_outbox.sequence_hold_until IS
  'Sequence integrity (558, #2542): until when the drain holds this event for its commit-order stamp. NULL: no hold; '
  '''infinity'': until stamped; otherwise a bounded hold that runs out at this instant. Ignored once the event is stamped.';

-- ---------------------------------------------------------------------------------------------
-- store_outbox_messages: last word 271, writing the row's hold.
-- ---------------------------------------------------------------------------------------------
-- <docs>fundamentals/events/system-events#audit</docs>
-- <docs>fundamentals/messaging/sequence-integrity#how-the-hold-works</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SequenceIntegritySqlTests.cs:Store_WritesEachModesHoldOntoAnEventsRow_AndNeverOntoACommandsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AuditRecordStreamPositionSqlTests.cs:Store_EventAndItsAuditTogether_RecordCarriesTheEventsVersionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/EFCoreCollectiveOrderingHeadTests.cs:StoreOutboxMessagesAsync_TwoCoordinators_LinkTheSecondToTheFirstAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MixedStreamGuardSqlTests.cs:Store_RefusedEventInABatch_LeavesTheOtherStreamsStoredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StringEventFlagsStoreTests.cs:StoreOutbox_FlagsWrittenAsNames_KeepsEveryBitAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredWorkReentrySqlTests.cs:ARecoveredOutboxMessage_IsStoredInTheShapeItWasStoredInAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredWorkReentrySqlTests.cs:ARecovery_OfAnOutboxMessageStillInTheOutbox_LeavesItsDeadLetterDueAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredOutboxDeadLetterPublishTests.cs:ARecoveredOutboxEvent_IsPublishedAsTheEventItWasStoredAs_AndReadsRecoveredOncePublishedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:StoreOutboxMessages_AnAwaitedWriteToASealedStream_ThrowsStreamSealedException_AndStoresNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:StoreOutboxMessages_TheSameWriteNotAwaited_IsDeadLettered_AndAnAwaitedWriteToAnOpenStreamIsStoredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveAppendPolicySqlTests.cs:StoreOutboxMessages_AnAwaitedWrite_Refuse_ThrowsStreamArchivedException_AndStoresNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveAppendPolicySqlTests.cs:StoreOutboxMessages_AnAwaitedWrite_AutoRestore_RaisesTheRestoreStateAsync</tests>
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
  -- 216: streams this call pins, written after the audit position fill so the work tables stay in canonical lock order.
  v_pin_streams UUID[] := ARRAY[]::UUID[];
  v_pin_partitions INTEGER[] := ARRAY[]::INTEGER[];
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
  -- 228 (#1546): the stream, flags and type of each event in v_inserted_event_ids (same positions), the events whose
  -- refusal is raised to the caller, and the events the refusal dead-lettered.
  v_inserted_event_streams UUID[] := ARRAY[]::UUID[];
  v_inserted_event_flags INTEGER[] := ARRAY[]::INTEGER[];
  v_inserted_event_types TEXT[] := ARRAY[]::TEXT[];
  v_raise_event_ids UUID[] := ARRAY[]::UUID[];
  v_refused_event_ids UUID[];
  v_claimed INTEGER;          -- 241: rows the recovery claim touched
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
      -- 558 (#2542): the hold the origin decided for the event, in milliseconds: 0 none, -1 until stamped.
      COALESCE((elem->>'SequenceHoldMilliseconds')::INTEGER, 0) as sequence_hold_ms,
      -- EventFlags (062): persisted so migration 061's collective routing can see (flags & 1). 232 (#1612): the
      -- number (what the framework writes) is read here; a name list is read bit by bit by _wh_event_flags_from_names,
      -- which raises on a name it does not know rather than store the event as Sourced.
      CASE
        WHEN elem->>__ENVELOPE_FIELD_FLAGS__ IS NULL OR elem->>__ENVELOPE_FIELD_FLAGS__ = '' THEN 0
        WHEN elem->>__ENVELOPE_FIELD_FLAGS__ ~ '^[0-9]+$' THEN (elem->>__ENVELOPE_FIELD_FLAGS__)::INTEGER
        ELSE __SCHEMA__._wh_event_flags_from_names(elem->>__ENVELOPE_FIELD_FLAGS__)
      END as flags,
      NULLIF(elem->>'ScheduledFor', '')::TIMESTAMPTZ as scheduled_for,
      -- 115 tag-bound coalescing: the group a pending single belongs to (NULL for normal rows).
      NULLIF(elem->>'CoalesceGroup', '') as coalesce_group,
      -- 190: set only on a collective with an ordering key; the type its successor names as its predecessor.
      NULLIF(elem->>'CollectiveLinkType', '') as collective_link_type,
      -- 228 (#1546): set by a write its caller awaits; a refusal of this event is raised instead of dead-lettered.
      COALESCE((elem->>'RaiseOnMixedStream')::BOOLEAN, false) as raise_on_mixed_stream,
      -- 241 (#1722): set only by dead-letter recovery, naming the dead-letter row the message re-enters from.
      (elem->>'RecoveredFromDeadLetterId')::UUID as recovered_from
    FROM jsonb_array_elements(p_messages) as elem
    ORDER BY (elem->>__ENVELOPE_FIELD_STREAM_ID__)::UUID NULLS FIRST, (elem->>__ENVELOPE_FIELD_MESSAGE_ID__)::UUID
  LOOP
    -- 241 (#1722): dead-letter recovery re-enters a message HERE, as one more arrival, so every rule below applies to
    -- it unchanged. A row that names its dead letter claims it first (Recovering), in this transaction, so two
    -- recoveries of one row cannot both re-enter it, and a dead letter whose message the outbox still holds is left
    -- alone. The row then carries the dead-letter id, and complete_outbox_published settles it once published.
    IF v_msg.recovered_from IS NOT NULL THEN
      UPDATE __SCHEMA__.wh_dead_letters dl
      SET recovery_status = 1,                  -- Recovering, until the message is published
          recovery_attempts = dl.recovery_attempts + 1,
          last_recovery_at = p_now
      WHERE dl.dead_letter_id = v_msg.recovered_from
        AND dl.source_id = v_msg.msg_id
        AND dl.recovery_status NOT IN (1, 2, 3, 4)  -- not already Recovering, HoldForReview, Recovered, PermanentlyFailed
        AND dl.recovered_at IS NULL
        AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_outbox o WHERE o.message_id = v_msg.msg_id);
      GET DIAGNOSTICS v_claimed = ROW_COUNT;
      IF v_claimed = 0 THEN
        CONTINUE;  -- claimed by another recovery, already settled, or the message is still in the outbox
      END IF;
    END IF;

    IF v_msg.stream_id IS NOT NULL THEN
      v_partition := __SCHEMA__.compute_partition(v_msg.stream_id, p_partition_count);
    ELSE
      v_partition := NULL;
    END IF;

    -- 114: emptiness probe, once per distinct stream per call, BEFORE this call's first
    -- insert for that stream (the loop is stream-ordered, so first encounter precedes all
    -- of the stream's inserts). FOR SHARE serializes against an in-flight completion of
    -- the last pending row; see the header's MVCC lost-wakeup guard.
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
      priority,
      recovered_from_dead_letter_id,
      sequence_hold_until
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
      v_msg.priority,
      v_msg.recovered_from,  -- 241: NULL for every arrival but a recovery
      -- 558 (#2542): only an event that is published is stamped and held. The deadline is taken from the store's own
      -- clock, the one created_at is, so a bounded hold lasts what the origin asked for however late the row is fetched.
      CASE
        WHEN NOT COALESCE(v_msg.is_event, false) OR v_msg.destination IS NULL OR v_msg.sequence_hold_ms = 0 THEN NULL
        WHEN v_msg.sequence_hold_ms < 0 THEN 'infinity'::TIMESTAMPTZ
        ELSE p_now + v_msg.sequence_hold_ms * INTERVAL '1 millisecond'
      END
    )
    ON CONFLICT ON CONSTRAINT wh_outbox_pkey DO NOTHING;

    GET DIAGNOSTICS v_was_new = ROW_COUNT;

    -- 241: a recovery that lost the race to a concurrent store of the same id hands its dead letter back, so the dead
    -- letter is never left Recovering behind a row that does not name it.
    IF NOT v_was_new AND v_msg.recovered_from IS NOT NULL THEN
      UPDATE __SCHEMA__.wh_dead_letters dl
      SET recovery_status = 0
      WHERE dl.dead_letter_id = v_msg.recovered_from AND dl.recovery_status = 1;
      CONTINUE;
    END IF;

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
      v_inserted_event_streams := array_append(v_inserted_event_streams, v_msg.stream_id);
      v_inserted_event_flags := array_append(v_inserted_event_flags, COALESCE(v_msg.flags, 0));
      v_inserted_event_types := array_append(v_inserted_event_types, v_msg.message_type);
      IF v_msg.raise_on_mixed_stream THEN
        v_raise_event_ids := array_append(v_raise_event_ids, v_msg.msg_id);
      END IF;
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
    -- used to ride this block is gone; the notify decision now keys on queue state).
    -- 216: the pin is collected here and written after the audit position fill below, so this function
    -- locks wh_outbox before wh_active_streams. The emit chain reads a stream's pinned owner and falls back
    -- to p_instance_id when none is pinned, which is the owner the pin would have named.
    IF v_was_new AND v_msg.stream_id IS NOT NULL AND p_instance_id IS NOT NULL THEN
      v_pin_streams := array_append(v_pin_streams, v_msg.stream_id);
      v_pin_partitions := array_append(v_pin_partitions, COALESCE(v_partition, 0));
    END IF;

    RETURN QUERY SELECT v_msg.msg_id AS message_id, v_msg.stream_id AS stream_id, v_was_new AS was_newly_created;
  END LOOP;

  -- 228 (#1546): an event that would make its stream hold both ephemeral and Sourced events is refused before the
  -- copy. A refusal the caller awaits raises (the whole call rolls back); any other is dead-lettered, which moves its
  -- outbox row out of the queue, and is left out of the copy so the rest of the write is stored.
  -- 266 (#1733): an event the caller awaits that a sealed stream refuses raises SQLSTATE WHS01 (StreamSealedException)
  -- and the whole call rolls back, as the mixed-stream refusal raises for it. Checked first, so an awaited event that is
  -- both is reported as sealed. Every other refused event is dead-lettered by the emit chain, which stores the rest.
  -- 271 (#1738): then one the caller awaits that an archived stream refuses raises SQLSTATE WHA01 (Refuse) or WHA02
  -- (AutoRestore: the caller restores the stream and retries once), and the whole call rolls back. After the seal, so a
  -- sealed archived stream is reported as sealed. Every other refused event is dead-lettered by the emit chain.
  IF cardinality(v_raise_event_ids) > 0 THEN
    PERFORM __SCHEMA__._wh_raise_sealed_stream_outbox_events(
      v_inserted_event_ids, v_inserted_event_streams, v_inserted_event_flags, v_inserted_event_types, v_raise_event_ids, p_now);
    PERFORM __SCHEMA__._wh_raise_archived_stream_outbox_events(
      v_inserted_event_ids, v_inserted_event_streams, v_raise_event_ids);
  END IF;

  IF cardinality(v_inserted_event_ids) > 0 THEN
    v_refused_event_ids := __SCHEMA__._wh_refuse_mixed_stream_outbox_events(
      v_inserted_event_ids, v_inserted_event_streams, v_inserted_event_flags, v_inserted_event_types,
      v_raise_event_ids, p_instance_id);
    IF cardinality(v_refused_event_ids) > 0 THEN
      v_inserted_event_ids := ARRAY(
        SELECT kept.id FROM unnest(v_inserted_event_ids) AS kept(id) WHERE kept.id <> ALL(v_refused_event_ids));
    END IF;
  END IF;

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

  -- Pin the streams collected above: the first message of a stream names its partition, as the per-message
  -- insert did. Sorted by stream id, so two calls pinning the same streams take them in one order.
  IF cardinality(v_pin_streams) > 0 THEN
    INSERT INTO __SCHEMA__.wh_active_streams
      (stream_id, partition_number, assigned_instance_id, last_activity_at)
    SELECT DISTINCT ON (p.stream_id) p.stream_id, p.partition_number, p_instance_id, p_now
    FROM unnest(v_pin_streams, v_pin_partitions) WITH ORDINALITY AS p(stream_id, partition_number, ord)
    ORDER BY p.stream_id, p.ord
    ON CONFLICT (stream_id) DO NOTHING;
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
  'transaction. 228: refuses an event that would make its stream hold both ephemeral and Sourced events (WHIZ133, '
  '#1546), raising when the element asks for it and dead-lettering the row otherwise. 232: reads every bit of flags '
  'written as a name list, and raises 22P02 on a name EventFlags does not define (#1612). 241 (#1722): a message '
  'recovered from a dead letter (RecoveredFromDeadLetterId) re-enters here as one more arrival: it claims its dead '
  'letter and its row names it for complete_outbox_published to settle. 266: raises SQLSTATE WHS01 '
  '(StreamSealedException) for an event the caller awaits on a sealed stream (#1733). 271: and SQLSTATE WHA01 or WHA02 '
  '(StreamArchivedException) for one on an archived stream, by its type''s AppendToArchived policy (#1738). 558: writes '
  'the hold the origin decided for a published event (SequenceHoldMilliseconds) as sequence_hold_until (#2542).';

-- ---------------------------------------------------------------------------------------------
-- fetch_outbox_batch: last word 151, cut at a stream's first held event.
-- ---------------------------------------------------------------------------------------------
-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('fetch_outbox_batch');

-- <docs>fundamentals/messaging/sequence-integrity#how-the-hold-works</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SequenceIntegritySqlTests.cs:Fetch_AnUnstampedEventWithNoHold_IsPublishedAtOnceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SequenceIntegritySqlTests.cs:Fetch_AnUnstampedEventWithinItsHold_IsHeld_AndPublishedOnceStampedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SequenceIntegritySqlTests.cs:Fetch_ABoundedHoldThatRanOut_IsPublishedUnstampedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SequenceIntegritySqlTests.cs:Fetch_ARowBehindAHeldEvent_IsHeldWhateverItsOwnSettingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SequenceIntegritySqlTests.cs:Fetch_AHoldWithNoStoredEvent_HoldsNothingAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.fetch_outbox_batch(
  p_stream_ids UUID[],
  p_instance_id UUID,
  p_max_per_stream INTEGER DEFAULT 100,
  p_max_bytes BIGINT DEFAULT NULL
) RETURNS TABLE(
  message_id UUID,
  stream_id UUID,
  destination VARCHAR(200),
  message_type VARCHAR(500),
  envelope_type VARCHAR(500),
  event_data TEXT,
  metadata JSONB,
  scope JSONB,
  status INTEGER,
  attempts INTEGER,
  partition_number INTEGER,
  is_event BOOLEAN,
  -- Slice 26.6b: JOINed event-store fields used by the publisher to populate envelope
  -- SourceServiceId + SourceCommitSequence before serializing to transport. NULL for
  -- non-event outbox rows; commit_sequence may be NULL if stamper hasn't caught up.
  commit_sequence BIGINT,
  origin_service_id UUID,
  origin_commit_sequence BIGINT,
  -- Slice 1 of release/v0.648.0-alpha.1: the row's existing error column (real
  -- exception text from the last process_outbox_failures cycle). The pre-publish
  -- DLQ gate uses this as errorText when promoting via move_to_dead_letters so
  -- the DLQ row's fingerprint reflects the real root cause instead of a
  -- meta-message that collapses every failure mode to one fingerprint.
  error TEXT,
  -- 151 (priority step 1 on the wire): the row's number, so the drain publishes what the producer declared.
  priority INTEGER
) AS $$
BEGIN
  IF p_stream_ids IS NULL OR array_length(p_stream_ids, 1) IS NULL THEN
    RETURN;
  END IF;

  -- Ordering invariant: sort by (stream_id, commit_sequence NULLS LAST, message_id).
  -- Slice 26.9: live publish order should match the source's commit-completion order so
  -- downstream consumers see events in the same order across live + replay. For unstamped
  -- rows the tail fall-back is message_id (UUIDv7, monotonic-at-generation).
  RETURN QUERY
  WITH ranked AS (
    SELECT
      o.*,
      es.commit_sequence AS es_commit_sequence,
      es.origin_service_id AS es_origin_service_id,
      es.origin_commit_sequence AS es_origin_commit_sequence,
      -- 558 (#2542): an event held for its commit-order stamp: it has a hold that has not run out, and its stored
      -- event has no commit sequence yet. A row with no stored event is never held, since nothing would stamp it.
      (o.sequence_hold_until IS NOT NULL AND o.sequence_hold_until > NOW()
        AND es.event_id IS NOT NULL AND es.commit_sequence IS NULL) AS sequence_held,
      ROW_NUMBER() OVER (
        PARTITION BY o.stream_id
        ORDER BY es.commit_sequence ASC NULLS LAST, o.message_id
      ) AS rank_in_stream
    FROM __SCHEMA__.wh_outbox o
    LEFT JOIN __SCHEMA__.wh_event_store es
      ON o.is_event AND es.event_id = o.message_id
    -- v0.658 slice 7: singleton-stream / Empty-stream rows are looked up by
    -- message_id-as-sentinel rather than stream_id. The coordinator's claim_work
    -- output emits message_id as the sentinel for rows whose stream_id is NULL
    -- (the documented singleton-stream marker) or Guid.Empty (the producer-side
    -- bug from a production forensic investigation — v0.657 slice 3's Empty→WorkId fallback). The
    -- pre-v0.658 filter `stream_id = ANY(p_stream_ids)` couldn't match either
    -- case: NULL=ANY is NULL/false, and Empty doesn't equal the message_id
    -- sentinel. The additive OR branch only fires when stream_id is non-routable,
    -- so real-stream rows still match exclusively via their stream_id.
    WHERE (
        o.stream_id = ANY(p_stream_ids)
        OR ((o.stream_id IS NULL OR o.stream_id = __EMPTY_UUID__::uuid)
            AND o.message_id = ANY(p_stream_ids))
      )
      AND o.instance_id = p_instance_id
      AND o.lease_expiry > NOW()
      AND o.processed_at IS NULL
      AND o.published_at IS NULL  -- skip debug-mode forensic rows
      AND (o.scheduled_for IS NULL OR o.scheduled_for <= NOW())
  ),
  -- Running byte total in the SAME order the rows are returned, so the cut is a suffix of the
  -- slice and stream-FIFO publish order is preserved. Measured on the payload columns because
  -- those are what cross the wire and land on the heap; the fixed-width columns are noise by
  -- comparison.
  budgeted AS (
    SELECT
      r.*,
      -- 558 (#2542): true from a stream's first held row on. That row and every row after it stay where they are,
      -- whatever their own setting, so a receiver never sees the stream out of order.
      bool_or(r.sequence_held)
        OVER (PARTITION BY r.stream_id
              ORDER BY r.es_commit_sequence ASC NULLS LAST, r.message_id
              ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS behind_hold,
      SUM(COALESCE(octet_length(r.event_data::TEXT), 0)
          + COALESCE(octet_length(r.metadata::TEXT), 0))
        OVER (PARTITION BY r.stream_id
              ORDER BY r.es_commit_sequence ASC NULLS LAST, r.message_id
              ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS running_bytes
    FROM ranked r
    WHERE r.rank_in_stream <= p_max_per_stream
  )
  SELECT
    b.message_id,
    b.stream_id,
    b.destination::VARCHAR(200),
    b.message_type::VARCHAR(500),
    b.envelope_type::VARCHAR(500),
    b.event_data::TEXT,
    b.metadata,
    b.scope,
    b.status,
    b.attempts,
    b.partition_number,
    b.is_event,
    b.es_commit_sequence,
    b.es_origin_service_id,
    b.es_origin_commit_sequence,
    b.error,
    b.priority
  FROM budgeted b
  WHERE NOT b.behind_hold
    AND (p_max_bytes IS NULL
     OR b.rank_in_stream = 1              -- never starve a stream on an oversized head message
     OR b.running_bytes <= p_max_bytes)
  ORDER BY b.stream_id, b.es_commit_sequence ASC NULLS LAST, b.message_id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.fetch_outbox_batch(UUID[], UUID, INTEGER, BIGINT) IS
'Per-stream-id payload fetch for OutboxDrainWorker, bounded by row count (p_max_per_stream) and payload bytes (p_max_bytes, NULL = unbounded). Returns every column the drain publishes from, including the row''s priority (151) so the wire envelope carries the number the producer declared. The byte budget trims the tail of a slice, never the head. 558 (#2542): a stream is cut at its first event held for its commit-order stamp (sequence_hold_until not yet passed and no commit sequence), so that row and every row after it wait for the stamp or the hold''s end.';

SELECT __SCHEMA__.drop_all_overloads('wh_continue_outbox_streams');

-- <docs>fundamentals/work-coordinator/per-stream-drain</docs>
-- <docs>fundamentals/messaging/sequence-integrity#how-the-hold-works</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SequenceIntegritySqlTests.cs:Continue_StopsAStreamAtItsHeldEvent_AsTheFetchDoesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:ContinueOutboxStreams_WhileAnotherSessionHoldsTheStreamLedger_NeverWaitsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:ConcurrentClaimsDrainsContinuationsCompletionsAndFailureReleases_NeverDeadlockAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunSqlTests.cs:ContinueOutboxStreams_LeasesTheNextRunAndReturnsOnlyRowsAfterTheCursorAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunSqlTests.cs:ContinueOutboxStreams_AStreamThisInstanceDoesNotOwn_IsNotContinuedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_continue_outbox_streams(
  p_instance_id UUID,
  p_stream_ids UUID[],
  -- The last message the drain PUBLISHED on each stream, position for position with p_stream_ids.
  -- Rows this instance holds at or before it are published and waiting for their completion to
  -- land; they are neither leased again nor returned.
  p_after_message_ids UUID[],
  p_run_length INTEGER DEFAULT 100,
  p_max_bytes BIGINT DEFAULT NULL
) RETURNS TABLE(
  message_id UUID,
  stream_id UUID,
  destination VARCHAR(200),
  message_type VARCHAR(500),
  envelope_type VARCHAR(500),
  event_data TEXT,
  metadata JSONB,
  scope JSONB,
  status INTEGER,
  attempts INTEGER,
  partition_number INTEGER,
  is_event BOOLEAN,
  commit_sequence BIGINT,
  origin_service_id UUID,
  origin_commit_sequence BIGINT,
  error TEXT,
  priority INTEGER
) AS $$
#variable_conflict use_column
DECLARE
  v_now TIMESTAMPTZ := NOW();
  v_run INTEGER := GREATEST(COALESCE(p_run_length, 1), 1);
  v_run_ids UUID[];
BEGIN
  IF p_stream_ids IS NULL OR array_length(p_stream_ids, 1) IS NULL THEN
    RETURN;
  END IF;

  -- Lease each stream's next run. Only a stream whose lease this instance holds is continued: the
  -- claim took that lease with the stream's earlier rows, and continuing under it is what lets the
  -- drain move on without waiting for the next claim cycle while keeping the stream on one instance.
  -- The rows join the stream's lease rather than starting a new one.
  WITH asked AS (
    SELECT a.a_stream_id, a.a_after_id
    FROM unnest(p_stream_ids, p_after_message_ids) AS a(a_stream_id, a_after_id)
    WHERE a.a_stream_id IS NOT NULL
      AND a.a_stream_id <> __EMPTY_UUID__::uuid
  ),
  owned AS (
    SELECT DISTINCT ON (ak.a_stream_id) ak.a_stream_id AS o_stream_id, ak.a_after_id AS o_after_id,
           ast.lease_expiry AS o_lease_expiry
    FROM asked ak
    JOIN __SCHEMA__.wh_active_streams ast
      ON ast.stream_id = ak.a_stream_id
     AND ast.assigned_instance_id = p_instance_id
     AND ast.lease_expiry > v_now
    ORDER BY ak.a_stream_id
  ),
  run_walk AS (
    SELECT ow.o_stream_id, ow.o_lease_expiry, w.w_message_id, w.w_mine, w.w_takeable, w.w_pos
    FROM owned ow
    CROSS JOIN LATERAL (
      -- The stream's pending rows in its own order, skipping the ones already published and waiting
      -- for their completion (held here, at or before the cursor). Rows this instance holds after
      -- the cursor are unpublished work of the run already and count toward it. Same range scan as
      -- the claim's run walk: idx_outbox_stream_run, Index Cond stream_id = $, LIMIT the run.
      SELECT o.message_id AS w_message_id,
             COALESCE(o.instance_id = p_instance_id AND o.lease_expiry > v_now, FALSE) AS w_mine,
             -- COALESCEd because bool_and below skips a NULL, which would read as "not blocking".
             COALESCE((o.instance_id IS NULL OR o.lease_expiry < v_now)
               AND (o.scheduled_for IS NULL OR o.scheduled_for <= v_now), FALSE) AS w_takeable,
             ROW_NUMBER() OVER (ORDER BY o.created_at, o.message_id) AS w_pos
      FROM __SCHEMA__.wh_outbox o
      WHERE o.stream_id = ow.o_stream_id
        AND o.processed_at IS NULL
        AND o.published_at IS NULL
        AND o.coalesce_group IS NULL
        AND NOT COALESCE(o.instance_id = p_instance_id
                         AND o.lease_expiry > v_now
                         AND o.message_id <= ow.o_after_id, FALSE)
      ORDER BY o.created_at, o.message_id
      LIMIT v_run
    ) w
  ),
  -- A prefix, as in the claim: it ends at the first row neither held here nor takeable.
  run_open AS (
    SELECT rw.*, bool_and(rw.w_mine OR rw.w_takeable) OVER (PARTITION BY rw.o_stream_id ORDER BY rw.w_pos) AS w_open
    FROM run_walk rw
  ),
  -- 172: as in the claim, the lock re-asserts that the row is still takeable, so a row another session
  -- leased since the snapshot is neither held nor leased here; it ends the run as a gap does.
  run_locked AS (
    SELECT o.message_id AS locked_message_id
    FROM __SCHEMA__.wh_outbox o
    WHERE o.message_id IN (SELECT ro.w_message_id FROM run_open ro WHERE ro.w_open AND NOT ro.w_mine)
      AND (o.instance_id IS NULL OR o.lease_expiry < v_now)
      AND (o.scheduled_for IS NULL OR o.scheduled_for <= v_now)
      AND o.processed_at IS NULL
    FOR UPDATE OF o SKIP LOCKED
  ),
  run_first_gap AS (
    SELECT ro.o_stream_id AS gap_stream_id, MIN(ro.w_pos) AS gap_pos
    FROM run_open ro
    WHERE ro.w_open
      AND NOT ro.w_mine
      AND NOT EXISTS (SELECT 1 FROM run_locked rl WHERE rl.locked_message_id = ro.w_message_id)
    GROUP BY ro.o_stream_id
  ),
  run_rows AS (
    SELECT ro.w_message_id, ro.w_mine, ro.o_lease_expiry
    FROM run_open ro
    LEFT JOIN run_first_gap g ON g.gap_stream_id = ro.o_stream_id
    WHERE ro.w_open
      AND (g.gap_pos IS NULL OR ro.w_pos < g.gap_pos)
  ),
  leased AS (
    UPDATE __SCHEMA__.wh_outbox o
    SET instance_id = p_instance_id,
        lease_expiry = r.o_lease_expiry,
        -- Single-source attempt counting, as in claim_orphaned_outbox: a lease charges one.
        attempts = o.attempts + 1
    FROM run_rows r
    WHERE o.message_id = r.w_message_id
      AND NOT r.w_mine
      AND (o.instance_id IS NULL OR o.lease_expiry < v_now)
      AND (o.scheduled_for IS NULL OR o.scheduled_for <= v_now)
      AND o.processed_at IS NULL
      AND o.coalesce_group IS NULL
    RETURNING o.message_id AS leased_message_id
  ),
  -- 172: the continuation never waits. Its outbox locks already skip what another session holds;
  -- this is its only other write, and it used to wait. It only moves last_activity_at, and the
  -- session holding a ledger row is the claim refreshing it or the lease renewal extending it, both
  -- of which keep the stream's lease current themselves, so a locked row is skipped. Waiting here,
  -- holding the run rows it had just locked, against the claim's refresh of the same instance's
  -- ledger rows in another order, was the deadlock of #936. A statement that waits for nothing
  -- cannot be part of a cycle.
  renew_locked AS (
    SELECT ast.stream_id AS locked_stream_id
    FROM owned ow
    JOIN __SCHEMA__.wh_active_streams ast ON ast.stream_id = ow.o_stream_id
    WHERE ast.assigned_instance_id = p_instance_id
    ORDER BY ast.stream_id
    FOR UPDATE OF ast SKIP LOCKED
  ),
  renewed AS (
    -- The stream's activity follows its work, as the claim's refresh does. Nothing reads this CTE;
    -- a data-modifying CTE runs exactly once whether or not the statement reads it.
    UPDATE __SCHEMA__.wh_active_streams ast
    SET last_activity_at = v_now
    FROM renew_locked rl
    WHERE ast.stream_id = rl.locked_stream_id
    RETURNING ast.stream_id
  )
  SELECT array_agg(x.run_id)
  INTO v_run_ids
  FROM (
    SELECT r.w_message_id AS run_id FROM run_rows r WHERE r.w_mine
    UNION
    SELECT l.leased_message_id FROM leased l
  ) x;

  IF v_run_ids IS NULL THEN
    RETURN;
  END IF;

  -- The run's rows, as fetch_outbox_batch (151) returns them: the same columns, the same event
  -- store join, the same order, and the same byte budget that trims a slice's tail and never its
  -- head. Addressed by primary key, so this reads exactly the run.
  RETURN QUERY
  WITH ranked AS (
    SELECT
      o.*,
      es.commit_sequence AS es_commit_sequence,
      es.origin_service_id AS es_origin_service_id,
      es.origin_commit_sequence AS es_origin_commit_sequence,
      -- 558 (#2542): an event held for its commit-order stamp: it has a hold that has not run out, and its stored
      -- event has no commit sequence yet. A row with no stored event is never held, since nothing would stamp it.
      (o.sequence_hold_until IS NOT NULL AND o.sequence_hold_until > v_now
        AND es.event_id IS NOT NULL AND es.commit_sequence IS NULL) AS sequence_held,
      ROW_NUMBER() OVER (
        PARTITION BY o.stream_id
        ORDER BY es.commit_sequence ASC NULLS LAST, o.message_id
      ) AS rank_in_stream
    FROM __SCHEMA__.wh_outbox o
    LEFT JOIN __SCHEMA__.wh_event_store es
      ON o.is_event AND es.event_id = o.message_id
    WHERE o.message_id = ANY(v_run_ids)
      AND o.instance_id = p_instance_id
      AND o.lease_expiry > v_now
      AND o.processed_at IS NULL
      AND o.published_at IS NULL
  ),
  budgeted AS (
    SELECT
      r.*,
      -- 558 (#2542): true from a stream's first held row on. That row and every row after it stay where they are,
      -- whatever their own setting, so a receiver never sees the stream out of order.
      bool_or(r.sequence_held)
        OVER (PARTITION BY r.stream_id
              ORDER BY r.es_commit_sequence ASC NULLS LAST, r.message_id
              ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS behind_hold,
      SUM(COALESCE(octet_length(r.event_data::TEXT), 0)
          + COALESCE(octet_length(r.metadata::TEXT), 0))
        OVER (PARTITION BY r.stream_id
              ORDER BY r.es_commit_sequence ASC NULLS LAST, r.message_id
              ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS running_bytes
    FROM ranked r
  )
  SELECT
    b.message_id,
    b.stream_id,
    b.destination::VARCHAR(200),
    b.message_type::VARCHAR(500),
    b.envelope_type::VARCHAR(500),
    b.event_data::TEXT,
    b.metadata,
    b.scope,
    b.status,
    b.attempts,
    b.partition_number,
    b.is_event,
    b.es_commit_sequence,
    b.es_origin_service_id,
    b.es_origin_commit_sequence,
    b.error,
    b.priority
  FROM budgeted b
  WHERE NOT b.behind_hold
    AND (p_max_bytes IS NULL
     OR b.rank_in_stream = 1
     OR b.running_bytes <= p_max_bytes)
  ORDER BY b.stream_id, b.es_commit_sequence ASC NULLS LAST, b.message_id;
END;
$$ LANGUAGE plpgsql SET plan_cache_mode = force_custom_plan;

COMMENT ON FUNCTION __SCHEMA__.wh_continue_outbox_streams(UUID, UUID[], UUID[], INTEGER, BIGINT) IS
  'Continues outbox streams from the lease this instance already holds (171): for each stream it owns, leases '
  'up to p_run_length of the stream''s next consecutive rows as a prefix (stopping at a row it may not take), '
  'and returns the rows it holds after the last one the drain published, in fetch_outbox_batch''s shape and '
  'order. Lets the drain move a long stream on without waiting for the next claim cycle. 172: it never waits for a '
  'lock: run rows and ledger rows another session holds are skipped. 558 (#2542): cuts each stream at its first event '
  'held for its commit-order stamp, as fetch_outbox_batch does.';

-- ---------------------------------------------------------------------------------------------
-- stamp_pending_commit_sequences: last word 248, also ringing the outbox.
-- ---------------------------------------------------------------------------------------------
-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('stamp_pending_commit_sequences');

-- <docs>fundamentals/work-coordinator/commit-sequence</docs>
-- <docs>fundamentals/messaging/sequence-integrity#released-by-the-stamp</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SequenceIntegritySqlTests.cs:Stamp_AStreamWithPendingOutboxRows_RingsItsOutboxOwnerAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SequenceIntegritySqlTests.cs:Stamp_AStreamWithNoPendingOutboxRows_DoesNotRingTheOutboxAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitSequenceStreamOrderSqlTests.cs:Stamp_ABacklogTheStatisticsDoNotKnow_IsReadOnceThroughTheInsertXidIndexAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitSequenceStreamOrderSqlTests.cs:Stamp_ABacklogTheStatisticsDoNotKnow_NeverComparesTheBatchWithItselfAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitSequenceStreamOrderSqlTests.cs:Stamp_ABacklogTheStatisticsDoNotKnow_DrainsInInsertionOrderBehindTheFenceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitSequenceStreamOrderSqlTests.cs:Stamp_RowsBehindAnOpenTransaction_AreNotReadToFindNothingEligibleAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitSequenceStreamOrderSqlTests.cs:Stamp_StreamsInDifferentBatches_FollowTheOrderTheirTransactionsBeganInAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitSequenceStreamOrderSqlTests.cs:Stamp_ALowerIdTransactionAppendsAfterAHigherOne_TheStreamKeepsItsVersionOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitSequenceStreamOrderSqlTests.cs:Stamp_TheEarlierVersionIsNotYetEligible_TheLaterOneWaitsForItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitSequenceStreamOrderSqlTests.cs:Stamp_OneTransactionInsertsAStreamOutOfVersionOrder_TheSequenceStillFollowsTheVersionsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitSequenceStreamOrderSqlTests.cs:Stamp_ABatchSmallerThanAStream_StampsTheStreamFromItsFirstVersionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitSequenceStreamOrderSqlTests.cs:Stamp_StreamsEligibleTogether_AreNumberedInInsertionOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitSequenceStreamOrderSqlTests.cs:Stamp_ABacklog_CostsWhatTheBatchStampsNotWhatTheBacklogHoldsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StampPendingCommitSequencesSqlTests.cs:Stamp_DefersWhileOlderTxStillInFlightAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StampPendingCommitSequencesSqlTests.cs:Stamp_ConcurrentCallers_NoRowStampedTwiceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StampPendingCommitSequencesSqlTests.cs:Stamp_BatchSizeLimitsPerCallAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.stamp_pending_commit_sequences(
  p_batch_size INTEGER DEFAULT 1000,
  p_notify_owners BOOLEAN DEFAULT FALSE
) RETURNS INTEGER AS $$
DECLARE
  v_stamped_count INTEGER;
  v_stream_ids UUID[];
  v_outbox_streams UUID[];
  v_horizon xid8;
  v_reached UUID[];
  v_event_ids UUID[];
  v_ranks INTEGER[];
  v_sequences BIGINT[];
  v_seqscan TEXT := current_setting('enable_seqscan');
  v_bitmapscan TEXT := current_setting('enable_bitmapscan');
  v_sort TEXT := current_setting('enable_sort');
  v_hashjoin TEXT := current_setting('enable_hashjoin');
  v_mergejoin TEXT := current_setting('enable_mergejoin');
BEGIN
  -- The visibility fence, unchanged from 116: the oldest transaction id still in flight in THIS database
  -- (prepared transactions included), or the snapshot's xmax when none is. Every row whose inserting
  -- transaction is below it has committed or aborted, so nothing below it can still appear.
  SELECT LEAST(
    COALESCE((SELECT min(sa.backend_xid::text::xid8)
              FROM pg_stat_activity sa
              WHERE sa.datname = current_database()
                AND sa.pid <> pg_backend_pid()
                AND sa.backend_xid IS NOT NULL),
             pg_snapshot_xmax(pg_current_snapshot())),
    COALESCE((SELECT min(px.transaction::text::xid8)
              FROM pg_prepared_xacts px
              WHERE px.database = current_database()),
             pg_snapshot_xmax(pg_current_snapshot()))
  ) INTO v_horizon;

  -- The statements below are written to be read through their indexes and nothing else. Scans that read every
  -- row are switched off for them and put back after them (197).
  PERFORM set_config('enable_seqscan', 'off', true);
  PERFORM set_config('enable_bitmapscan', 'off', true);

  -- The walk (205, now its own statement): unstamped rows past the horizon, found through the id of the
  -- transaction that wrote them, earliest transaction first and insertion order within one, stopped at the batch.
  -- With sort off, the range scan of idx_event_store_unstamped_insert_xid is the only plan that yields this order,
  -- so the walk reads the batch and stops, however many rows are unstamped and whatever their statistics say.
  -- It decides which streams move and how many rows each may stamp; it is not the set stamped.
  PERFORM set_config('enable_sort', 'off', true);
  SELECT array_agg(w.stream_id)
  INTO v_reached
  FROM (
    SELECT es.stream_id
    FROM __SCHEMA__.wh_event_store es
    WHERE es.commit_sequence IS NULL
      AND es.insert_xid < v_horizon
    ORDER BY es.insert_xid, es.insert_order
    LIMIT p_batch_size
  ) w;
  PERFORM set_config('enable_sort', v_sort, true);

  IF v_reached IS NULL THEN
    PERFORM set_config('enable_seqscan', v_seqscan, true);
    PERFORM set_config('enable_bitmapscan', v_bitmapscan, true);
    RETURN 0;
  END IF;

  WITH chosen AS MATERIALIZED (
    SELECT r.stream_id, count(*)::INTEGER AS row_budget
    FROM unnest(v_reached) AS r(stream_id)
    GROUP BY r.stream_id
  ),
  prefixes AS MATERIALIZED (
    -- Each chosen stream from its lowest unstamped version, as many rows as the walk gave it. A row is
    -- admitted only while every lower version of its stream is eligible too.
    SELECT p.event_id, p.stream_id, p.version, p.insert_order,
           bool_and(p.eligible) OVER (PARTITION BY p.stream_id ORDER BY p.version) AS admitted
    FROM chosen c
    CROSS JOIN LATERAL (
      SELECT u.event_id, u.stream_id, u.version, u.insert_order,
             (u.xmin::text::xid8 < v_horizon) AS eligible
      FROM __SCHEMA__.wh_event_store u
      WHERE u.stream_id = c.stream_id
        AND u.commit_sequence IS NULL
      ORDER BY u.version
      LIMIT c.row_budget
    ) p
  ),
  locked AS MATERIALIZED (
    -- Re-asserts the predicate that chose the row (172's rule 4): a row another session stamped since this
    -- statement's snapshot is not locked. Looked up by key, never joined to a walk of the unstamped rows.
    SELECT es.event_id, es.stream_id, es.version, es.insert_order
    FROM __SCHEMA__.wh_event_store es
    WHERE es.event_id = ANY (ARRAY(SELECT pr.event_id FROM prefixes pr WHERE pr.admitted))
      AND es.commit_sequence IS NULL
    ORDER BY es.event_id
    FOR UPDATE OF es SKIP LOCKED
  ),
  marked AS (
    -- Every admitted row once, and once more if the lock took it; grouped below. Grouping two lists of the batch
    -- costs the batch at any estimate, where a join of them could be planned as every row against every row.
    SELECT pr.event_id, pr.stream_id, pr.version, pr.insert_order, FALSE AS is_locked
    FROM prefixes pr
    WHERE pr.admitted
    UNION ALL
    SELECT l.event_id, l.stream_id, l.version, l.insert_order, TRUE
    FROM locked l
  ),
  admitted AS (
    SELECT m.event_id, m.stream_id, m.version, m.insert_order, bool_or(m.is_locked) AS is_locked
    FROM marked m
    GROUP BY m.event_id, m.stream_id, m.version, m.insert_order
  ),
  held AS (
    -- A version another session holds stops its stream here: nothing after it is stamped in this call.
    SELECT h.event_id, h.stream_id, h.version, h.insert_order
    FROM (
      SELECT a.event_id, a.stream_id, a.version, a.insert_order,
             bool_and(a.is_locked) OVER (PARTITION BY a.stream_id ORDER BY a.version) AS contiguous
      FROM admitted a
    ) h
    WHERE h.contiguous
  ),
  ranked AS (
    SELECT hd.event_id, hd.stream_id, hd.version,
           row_number() OVER (ORDER BY hd.insert_order, hd.event_id)::INTEGER AS batch_rank
    FROM held hd
  )
  -- Insertion order across the batch; within a stream the same ranks go to its rows in version order. Both
  -- lists are sorted by stream first and hold the same number of rows per stream, so the n-th row of a stream
  -- in version order sits at the same position as the stream's n-th rank: the arrays pair them by position.
  SELECT array_agg(rk.event_id ORDER BY rk.stream_id, rk.version),
         array_agg(rk.batch_rank ORDER BY rk.stream_id, rk.batch_rank)
  INTO v_event_ids, v_ranks
  FROM ranked rk;

  -- Sequence values for the batch, handed out in rank order so the promise does not rest on the order a
  -- statement happens to emit rows in.
  SELECT array_agg(a.seq ORDER BY a.seq)
  INTO v_sequences
  FROM (
    SELECT nextval('__SCHEMA__.wh_commit_seq') AS seq
    FROM generate_series(1, COALESCE(cardinality(v_event_ids), 0))
  ) a;

  -- The update reaches each row by its key. Hash and merge joins are off for it so that no estimate of the
  -- paired lists can make a scan of the table look cheaper than one lookup per row.
  PERFORM set_config('enable_hashjoin', 'off', true);
  PERFORM set_config('enable_mergejoin', 'off', true);
  WITH stamped AS (
    UPDATE __SCHEMA__.wh_event_store es
    SET commit_sequence = v_sequences[s.batch_rank]
    FROM unnest(v_event_ids, v_ranks) AS s(event_id, batch_rank)
    WHERE es.event_id = s.event_id
    RETURNING es.stream_id
  )
  SELECT COUNT(*)::INTEGER, array_agg(DISTINCT stream_id)
  INTO v_stamped_count, v_stream_ids
  FROM stamped;
  PERFORM set_config('enable_hashjoin', v_hashjoin, true);
  PERFORM set_config('enable_mergejoin', v_mergejoin, true);

  PERFORM set_config('enable_seqscan', v_seqscan, true);
  PERFORM set_config('enable_bitmapscan', v_bitmapscan, true);

  -- Unchanged from 132: every stamp that affected rows rings the owners; the debounce owns redundancy.
  IF v_stamped_count > 0 THEN
    PERFORM __SCHEMA__.notify_instance_owners(__CATEGORY_PERSPECTIVE__, v_stream_ids);

    -- 558 (#2542): the outbox holds an event until it is stamped, so the stamp is what releases it. The owners of the
    -- stamped streams that still have pending outbox rows are rung on the outbox doorbell, and a held event goes out as
    -- soon as its stamp lands instead of when its hold runs out. One probe per stamped stream, the first entry of
    -- idx_outbox_stream_run (the ORDER BY is the index's own key, which is what keeps the probe on the index).
    SELECT array_agg(s.stream_id)
    INTO v_outbox_streams
    FROM unnest(v_stream_ids) AS s(stream_id)
    WHERE (
      SELECT 1 FROM __SCHEMA__.wh_outbox o
      WHERE o.stream_id = s.stream_id
        AND o.processed_at IS NULL
        AND o.published_at IS NULL
      ORDER BY o.created_at, o.message_id
      LIMIT 1
    ) IS NOT NULL;
    IF v_outbox_streams IS NOT NULL THEN
      PERFORM __SCHEMA__.notify_instance_owners(__CATEGORY_OUTBOX__, v_outbox_streams);
    END IF;
  END IF;

  RETURN v_stamped_count;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.stamp_pending_commit_sequences(INTEGER, BOOLEAN) IS
'248 supersedes 205. Stamps rows past the visibility horizon (unchanged): the walk, its own statement with sort off, range-scans insert_xid past the horizon, earliest transaction first and insertion order within one, and stops at the batch; each stream it reaches is stamped from its lowest unstamped version and stops at the first that is not yet eligible or is held elsewhere; values go out in insertion order across the batch and version order within each stream. No step joins the batch with itself and the update reaches each row by its key, so a call costs what it stamps under any statistics or plan. The post-stamp ring is 132''s; 558 (#2542) also rings the outbox owners of stamped streams with pending outbox rows, which releases the events held for their stamp. p_notify_owners is retained for signature compatibility and ignored. Returns count of rows stamped.';

-- ---------------------------------------------------------------------------------------------
-- wh_oldest_sequence_held_outbox: the held-age metric's reading.
-- ---------------------------------------------------------------------------------------------
-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_oldest_sequence_held_outbox');

-- <docs>fundamentals/messaging/sequence-integrity#watching-held-messages</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SequenceIntegritySqlTests.cs:OldestHeld_IsTheArrivalOfTheOldestEventThisInstanceHoldsForItsStampAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SequenceIntegritySqlTests.cs:OldestHeld_StopsAtItsBoundAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_oldest_sequence_held_outbox(
  p_instance_id UUID,
  p_max_rows INTEGER DEFAULT 1000
) RETURNS TIMESTAMPTZ AS $$
  -- The instance's pending rows in arrival order, through idx_outbox_held_arrival (instance_id, created_at,
  -- message_id; partial on the same predicate), stopped at the bound before anything else is read. The first of them
  -- held for its stamp is the oldest held.
  SELECT w.created_at
  FROM (
    SELECT o.message_id, o.created_at, o.sequence_hold_until
    FROM __SCHEMA__.wh_outbox o
    WHERE o.instance_id = p_instance_id
      AND o.processed_at IS NULL
      AND o.coalesce_group IS NULL
    ORDER BY o.created_at, o.message_id
    LIMIT GREATEST(COALESCE(p_max_rows, 1), 1)
  ) w
  JOIN __SCHEMA__.wh_event_store es ON es.event_id = w.message_id
  WHERE w.sequence_hold_until > NOW()
    AND es.commit_sequence IS NULL
  ORDER BY w.created_at, w.message_id
  LIMIT 1;
$$ LANGUAGE sql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_oldest_sequence_held_outbox(UUID, INTEGER) IS
'558 (#2542): the arrival time of the oldest outbox event the instance holds for its commit-order stamp, or NULL when it holds none among its p_max_rows oldest pending rows. Reads at most that many rows, so the held-age metric costs the same whatever the instance holds.';
