-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 579_TheOutboxDrainsInEmissionOrder.sql
-- Date: 2026-11-24
-- Description: A stream's outbox rows are claimed, fetched, continued and released in the order they were emitted,
--              never by id (#2551), as the inbox follows its arrival order since 576.
--
--              An id is time-ordered and never a stream position, and the rows one store call writes share created_at,
--              so (created_at, message_id) ordered a batch's rows by id. Each row now takes its emission position
--              (wh_outbox.emission_position, from wh_outbox_emission_seq): store_outbox_messages reserves one per element
--              in the order the producer listed them, before its inserts, which run in lock order. The claim's run walk
--              (claim_orphaned_outbox), the fetch and the continuation walk and rank a stream by it, through
--              idx_outbox_stream_emission; the continuation's cursor is the last position the drain published; and a
--              failure releases the rows after it in emission order (process_outbox_failures, and flush_completions,
--              which locks those rows first). Rows are still locked in the canonical (stream_id, created_at,
--              message_id) order, the order every writer of the outbox takes them in (172).
--
--              The drivers call two new functions, fetch_outbox_batch_in_stream_order and
--              wh_continue_outbox_streams_in_emission_order, which return each row's stream_position: a function's
--              result never changes under its name (454), so fetch_outbox_batch and wh_continue_outbox_streams keep
--              theirs for a binary built before this one.
--
--              Rows already queued take their positions once, in the order they have been drained in until now.
--
-- Reversal: reversible (the down file restores each function's previous last word and drops what this adds)
-- Dependencies: 573 (store_outbox_messages), 566 (claim_orphaned_outbox, wh_continue_outbox_streams), 559
--               (fetch_outbox_batch), 570 (flush_completions), 412 (process_outbox_failures), 171 (idx_outbox_stream_run)
-- Objects: wh_outbox.emission_position, wh_outbox_emission_seq, idx_outbox_stream_emission, store_outbox_messages,
--          claim_orphaned_outbox, wh_continue_outbox_streams_in_emission_order, fetch_outbox_batch_in_stream_order,
--          flush_completions, process_outbox_failures

CREATE SEQUENCE IF NOT EXISTS __SCHEMA__.wh_outbox_emission_seq;

ALTER TABLE __SCHEMA__.wh_outbox ADD COLUMN IF NOT EXISTS emission_position BIGINT;

-- The rows already queued take their positions once, in the order they have been drained in until now. A row already
-- processed (kept by debug mode) is never drained again and keeps none.
DO $$
DECLARE
  v_count BIGINT;
  v_base BIGINT;
BEGIN
  SELECT count(*) INTO v_count
  FROM __SCHEMA__.wh_outbox o
  WHERE o.emission_position IS NULL AND o.processed_at IS NULL;
  IF v_count > 0 THEN
    v_base := nextval('__SCHEMA__.wh_outbox_emission_seq');
    PERFORM setval('__SCHEMA__.wh_outbox_emission_seq', v_base + v_count);
    UPDATE __SCHEMA__.wh_outbox o
    SET emission_position = v_base + q.rn
    FROM (
      SELECT o2.message_id, row_number() OVER (ORDER BY o2.created_at, o2.message_id) AS rn
      FROM __SCHEMA__.wh_outbox o2
      WHERE o2.emission_position IS NULL AND o2.processed_at IS NULL
    ) q
    WHERE o.message_id = q.message_id;
  END IF;
END;
$$;

ALTER TABLE __SCHEMA__.wh_outbox ALTER COLUMN emission_position SET DEFAULT nextval('__SCHEMA__.wh_outbox_emission_seq');

COMMENT ON COLUMN __SCHEMA__.wh_outbox.emission_position IS
'579 (#2551): the row''s place in its stream, the order store_outbox_messages was handed the stream''s messages in. The claim, the fetch and the continuation walk a stream by it, never by id.';

-- The run walk's index in emission order: the same columns and predicate as idx_outbox_stream_run (171), keyed by
-- stream and emission position.
CREATE INDEX IF NOT EXISTS idx_outbox_stream_emission
  ON __SCHEMA__.wh_outbox (stream_id, emission_position)
  INCLUDE (scheduled_for, instance_id, lease_expiry, coalesce_group)
  WHERE processed_at IS NULL AND published_at IS NULL;
COMMENT ON INDEX __SCHEMA__.idx_outbox_stream_emission IS
  'Pending unpublished outbox rows of a stream in emission order (579). The claim leases a run of a stream''s next rows '
  'and the drain continues a stream from its lease; both walk this index.';

-- ---------------------------------------------------------------------------------------------
-- store_outbox_messages: copied from its last word (573), with the 579 delta: each row's emission position.
-- ---------------------------------------------------------------------------------------------
-- <docs>fundamentals/events/system-events#audit</docs>
-- <docs>fundamentals/work-coordinator/partition-assignment#ownership-by-map</docs>
-- <docs>fundamentals/messaging/sequence-integrity#how-the-hold-works</docs>
-- <docs>fundamentals/event-store/version-ordering#emission-order</docs>
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
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipByMapStoreSqlTests.cs:StoreOutbox_InMapMode_RecordsTheStreamWithoutAnOwnerAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipByMapStoreSqlTests.cs:StoreOutbox_SwitchOff_PinsTheFirstWriter_AsBeforeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SequenceIntegritySqlTests.cs:Store_WritesEachModesHoldOntoAnEventsRow_AndNeverOntoACommandsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EmissionOrderVersioningSqlTests.cs:Store_ACauseAndItsDerivedEffectInOneBatch_TheCauseTakesTheLowerVersionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EmissionOrderVersioningSqlTests.cs:CommitHandlerResult_ACauseAndItsDerivedEffect_TheCauseTakesTheLowerVersionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MixedStreamGuardSqlTests.cs:Store_FirstWriteMixesBothKinds_KeepsTheFirstKindAndDeadLettersTheOtherAsync</tests>
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
  -- 573 (#2551): each inserted event's position in p_messages (same positions as v_inserted_event_ids), the order the
  -- producer emitted the batch in.
  v_inserted_event_positions BIGINT[] := ARRAY[]::BIGINT[];
  v_raise_event_ids UUID[] := ARRAY[]::UUID[];
  v_refused_event_ids UUID[];
  v_claimed INTEGER;          -- 241: rows the recovery claim touched
  -- 566 (#2554, #2555): the service's switch, read once: on, the partition ownership table alone decides.
  v_map BOOLEAN := __SCHEMA__.wh_partition_ownership_by_map();
  -- 579 (#2551): one emission position per element, in the order the producer emitted them.
  v_emissions BIGINT[];
BEGIN
  IF jsonb_array_length(p_messages) = 0 THEN RETURN; END IF;

  -- 579 (#2551): the batch's emission positions, taken in its order before the inserts, which run in lock order.
  v_emissions := ARRAY(
    SELECT nextval('__SCHEMA__.wh_outbox_emission_seq') FROM generate_series(1, jsonb_array_length(p_messages)) AS g(n) ORDER BY g.n);

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
      -- 559 (#2542): the hold the origin decided for the event, in milliseconds: 0 none, -1 until stamped.
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
      (elem->>'RecoveredFromDeadLetterId')::UUID as recovered_from,
      -- 573 (#2551): the element's position in the batch, which is the order the producer emitted it in.
      listed.position as emission_position
    FROM jsonb_array_elements(p_messages) WITH ORDINALITY AS listed(elem, position)
    -- Inserted in stream and id order, the canonical lock order of the rows this loop writes; the order the events are
    -- versioned in is their emission position, collected below.
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
        -- why a second key of the index is here too. 573 (#2551): the second key is perspective_name, the next column
        -- of uq_perspective_event (stream_id, perspective_name, event_id), not the event id: the probe asks only whether
        -- a row exists, and nothing is ordered by id.
        ORDER BY pe.stream_id, pe.perspective_name
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
      sequence_hold_until,
      emission_position
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
      -- 559 (#2542): only an event that is published is stamped and held. The deadline is taken from the store's own
      -- clock, the one created_at is, so a bounded hold lasts what the origin asked for however late the row is fetched.
      CASE
        WHEN NOT COALESCE(v_msg.is_event, false) OR v_msg.destination IS NULL OR v_msg.sequence_hold_ms = 0 THEN NULL
        WHEN v_msg.sequence_hold_ms < 0 THEN 'infinity'::TIMESTAMPTZ
        ELSE p_now + v_msg.sequence_hold_ms * INTERVAL '1 millisecond'
      END,
      -- 579 (#2551): the row's place in its stream, the order the producer emitted it in.
      v_emissions[v_msg.emission_position]
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
      v_inserted_event_positions := array_append(v_inserted_event_positions, v_msg.emission_position);
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

  -- 573 (#2551): the inserted events are handed on in the order the producer emitted them (their position in
  -- p_messages), not in the loop's lock order, which sorts them by stream and id. The refusals judge "first" and
  -- "after" by position in these arrays, and the chain numbers each stream in their order: an id is time-ordered,
  -- never stream position.
  IF cardinality(v_inserted_event_ids) > 1 THEN
    SELECT array_agg(k.id ORDER BY k.pos), array_agg(k.sid ORDER BY k.pos), array_agg(k.bits ORDER BY k.pos),
           array_agg(k.type_name ORDER BY k.pos)
      INTO v_inserted_event_ids, v_inserted_event_streams, v_inserted_event_flags, v_inserted_event_types
    FROM unnest(v_inserted_event_ids, v_inserted_event_streams, v_inserted_event_flags, v_inserted_event_types,
                v_inserted_event_positions) AS k(id, sid, bits, type_name, pos);
  END IF;

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
        SELECT kept.id FROM unnest(v_inserted_event_ids) WITH ORDINALITY AS kept(id, ord)
        WHERE kept.id <> ALL(v_refused_event_ids) ORDER BY kept.ord);
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
    -- 567 (#2555): in map mode the first writer is not the stream's owner; the row records the stream and its
    -- partition, and the partition's owner is named when it claims.
    SELECT DISTINCT ON (p.stream_id) p.stream_id, p.partition_number, CASE WHEN v_map THEN NULL ELSE p_instance_id END, p_now
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
  '(StreamArchivedException) for one on an archived stream, by its type''s AppendToArchived policy (#1738). 559: writes '
  'the hold the origin decided for a published event (SequenceHoldMilliseconds) as sequence_hold_until (#2542). '
  '567 (#2555): with the service''s switch on, no stored owner is pinned or leased to; leased by partition (wh_i_own). 573 (#2551): hands the events it inserted to the refusals and the emit chain in the order the producer emitted them (their position in p_messages), never sorted by id.';

-- ---------------------------------------------------------------------------------------------
-- claim_orphaned_outbox: copied from its last word (566), with the 579 delta: the run in emission order.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('claim_orphaned_outbox');

-- <docs>fundamentals/work-coordinator/claim-loop</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxAcquisitionWindowSqlTests.cs:ClaimOrphanedOutbox_BacklogOwnedByALivePeer_CostFollowsTheBatchNotTheBacklogAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxAcquisitionWindowSqlTests.cs:ClaimOrphanedOutbox_AnExpiredLeaseBehindAPeerBacklog_IsStillReclaimedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxAcquisitionWindowSqlTests.cs:ClaimOrphanedOutbox_UnownedStreamInsideTheWindow_IsTakenFromItsHeadAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:ClaimOrphanedOutbox_RefreshesTheStreamLedgerInStreamOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:ClaimOrphanedOutbox_RefreshedAndPinnedStreams_TakesTheirLedgerRowsInOneStreamOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:ConcurrentClaimsDrainsContinuationsCompletionsAndFailureReleases_NeverDeadlockAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:ConcurrentClaims_WithNoInstanceEverRegistered_NeverDeadlockAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimLedgerLockOrderSqlTests.cs:ClaimWork_LedgerRowsOfEveryAcquisition_AreTakenInOneStreamOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimOrphanedDeadlockMitigationSqlTests.cs:ClaimOrphanedOutbox_AlreadyOwnedWithLiveLease_RefreshesLastActivityAtAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimOrphanedDeadlockMitigationSqlTests.cs:ClaimOrphanedOutbox_NullOwner_StillTakesPinPathAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimOrphanedDeadlockMitigationSqlTests.cs:ClaimOrphanedOutbox_NeverPinned_PinPathInsertsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunSqlTests.cs:ClaimWork_OneLongStream_DrainsInRunsNotRowsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunSqlTests.cs:ClaimOrphanedOutbox_RunStopsAtTheFirstRowItMayNotTakeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimOrphanedAttemptsIncrementSqlTests.cs:ClaimOrphanedOutbox_FirstClaim_BumpsToOneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ListenLivenessSqlTests.cs:ClaimOrphanedOutbox_DirectOwnerHoldingItsAliveLock_LeasesNotReclaimedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ListenLivenessSqlTests.cs:ClaimOrphanedOutbox_StaleHeartbeat_WithOnlyAConnectionNamedForIt_LeasesReclaimableAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ListenLivenessSqlTests.cs:ClaimOrphanedInbox_StaleHeartbeat_WithOnlyAConnectionNamedForIt_LeasesReclaimableAsync</tests>
-- <docs>fundamentals/work-coordinator/partition-assignment#ownership-by-map</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipByMapClaimSqlTests.cs:Outbox_TheMapMovesThePartition_TheNewOwnerClaimsTheStream_AndTheStoredOwnerNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/ClaimPathsDecideThroughTheOwnershipTableGuardTests.cs:EveryClaimFunction_DecidesThroughWhIOwn_AndRanksOnlyInTheLegacyBranchAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionMapClaimSqlTests.cs:ClaimWork_Outbox_WithAMap_TakesThePartitionsTheMapGivesIt_NotItsResidueAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.claim_orphaned_outbox(
  p_instance_id UUID,
  p_instance_rank INTEGER,
  p_active_instance_count INTEGER,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_partition_count INTEGER,
  p_stale_cutoff TIMESTAMPTZ,
  p_max_rows INTEGER DEFAULT NULL,
  -- 171: how many consecutive rows one stream may lease in one call. 1 is the previous behavior.
  p_run_length INTEGER DEFAULT 1,
  -- 171: how many of the oldest rows choose the streams that move. NULL means p_max_rows, the
  -- previous behavior; claim_work passes its stream window.
  p_max_heads INTEGER DEFAULT NULL,
  -- 202 (#1256): TRUE leaves the stream ledger to the caller. claim_work passes it so its acquisitions
  -- write no ledger row of their own; it writes every claimed stream's row once, in one stream_id pass,
  -- after the last acquisition (wh_lease_claimed_streams). A direct caller keeps the default and gets
  -- the same one-pass write here, inside this statement.
  p_ledger_deferred BOOLEAN DEFAULT FALSE,
  -- 566 (#2558): the published partition map, when the caller's assignment carries one: entry p + 1 is the index in
  -- the assignment's members of the member partition p belongs to. NULL ranks by modulo, as before. Every legacy-mode
  -- partition test in this function reads it through wh_partition_in_share.
  p_partition_owners SMALLINT[] DEFAULT NULL
) RETURNS TABLE(
  message_id UUID,
  stream_id UUID,
  -- 202: the partition the ledger row of a new stream is recorded under, for a caller that defers the ledger.
  partition_number INTEGER
) AS $$
#variable_conflict use_column
DECLARE
  -- 566 (#2554, #2555): the service's switch, read once: on, the partition ownership table alone decides.
  v_map BOOLEAN := __SCHEMA__.wh_partition_ownership_by_map();
  v_run INTEGER := GREATEST(COALESCE(p_run_length, 1), 1);
  -- LEAST ignores a NULL, so a NULL head cap falls back to the row bound, and a NULL row bound (an
  -- untaught caller) stays unlimited exactly as before.
  v_heads INTEGER := LEAST(p_max_heads, p_max_rows);
  -- 194: how many of the oldest claimable rows this call examines, per lane: eight times the heads it
  -- chooses (the window only chooses heads; each head's run is walked separately below, priced by its
  -- share). A constant against the backlog, as 159 made it for the inbox. A NULL head bound (an
  -- untaught caller) keeps the window unlimited, exactly as before. A variable rather than a subquery,
  -- so the planner sees the number: with a LIMIT it cannot read it estimates the whole table, and a
  -- plan built for the whole table joins the window back to wh_outbox by hashing wh_outbox.
  v_window BIGINT := CASE WHEN v_heads IS NULL THEN NULL
                          ELSE GREATEST(LEAST(v_heads, 1000000), 1)::BIGINT * 8 END;
BEGIN
  RETURN QUERY
  -- Bound ACQUISITION: see claim_orphaned_inbox (mig 025) for the full rationale. Unbounded, this
  -- statement leases every eligible row at once and charges an attempt to each, so the caller's
  -- claim limit governs only what comes back out, never how much gets taken. LIMIT NULL is
  -- unlimited in Postgres, so the default preserves the previous behavior for untaught callers.
  WITH
  -- 566 (#2555): in map mode, the partitions this instance owns, read once per call through wh_partitions_i_own (the set
  -- form of wh_i_own: the same rule over the same table) and tested by hash membership, as the rank was tested by
  -- arithmetic. A per-row probe over the window would cost one index probe per row the window reads. Empty when off.
  mine_partitions AS MATERIALIZED (
    SELECT mp.partition_number
    FROM __SCHEMA__.wh_partitions_i_own(p_instance_id) AS mp(partition_number)
    WHERE v_map
  ),
  -- 194: ownership is a property of the STREAM, so it is decided once per call against the two small
  -- sets below and tested by hash membership (145's shape for the inbox). "Live" keeps 025's meaning:
  -- a heartbeat within p_stale_cutoff OR a registered LISTEN connection in pg_stat_activity.
  others_live AS MATERIALIZED (
    SELECT DISTINCT ast.stream_id
    FROM __SCHEMA__.wh_active_streams ast
    JOIN __SCHEMA__.wh_service_instances si ON si.instance_id = ast.assigned_instance_id
    WHERE ast.assigned_instance_id <> p_instance_id
      AND ast.lease_expiry > p_now
      -- 566 (#2555): a stored owner holds nothing in map mode.
      AND NOT v_map
      -- 317 (#2027): the one liveness rule. p_stale_cutoff is kept in the signature and no longer read.
      AND __SCHEMA__.wh_instance_liveness(si, si.instance_id IN (SELECT h.instance_id FROM __SCHEMA__.wh_direct_alive_lock_holders() h))
  ),
  mine_owned AS MATERIALIZED (
    SELECT ast.stream_id
    FROM __SCHEMA__.wh_active_streams ast
    WHERE ast.assigned_instance_id = p_instance_id
      AND ast.lease_expiry > p_now
      AND NOT v_map
  ),
  -- 194: the rows this call looks at, read once, each lane in an order an index carries and stopped
  -- at the window. Before this the heads were chosen by walking every pending row in arrival order
  -- and testing each for ownership until a batch was found. Under a load the oldest pending rows are
  -- the unleased tails of streams a live peer owns (it leases a run at a time), so every other
  -- instance walked all of them on every poll and found nothing: a measured call returned 13 rows
  -- after visiting 79,590,235 shared buffers, almost all cache hits. The bound has to be on rows
  -- examined, not on rows found, or a call that cannot reach its LIMIT runs to the end of the table.
  --   unowned rows by arrival, through idx_outbox_pending_arrival (157);
  --   expired leases by expiry, through idx_outbox_lease_expiry (158), so a lease that lapsed on a
  --   vanished instance is reclaimed wherever it sits in arrival order.
  -- A row deferred into the future is not claimable and does not take a place in the window, as in
  -- the inbox: a parked row (scheduled_for = infinity) must never be able to fill it.
  scanned AS MATERIALIZED (
    (SELECT o.message_id AS s_message_id, o.stream_id AS s_stream_id, o.created_at AS s_created_at,
            o.partition_number AS s_partition_number
     FROM __SCHEMA__.wh_outbox o
     WHERE o.processed_at IS NULL
       AND o.coalesce_group IS NULL
       AND o.instance_id IS NULL
       AND (o.scheduled_for IS NULL OR o.scheduled_for <= p_now)
     ORDER BY o.created_at, o.message_id
     LIMIT v_window)
    UNION ALL
    (SELECT o.message_id, o.stream_id, o.created_at, o.partition_number
     FROM __SCHEMA__.wh_outbox o
     WHERE o.processed_at IS NULL
       AND o.coalesce_group IS NULL
       AND o.instance_id IS NOT NULL
       AND o.lease_expiry < p_now
       AND (o.scheduled_for IS NULL OR o.scheduled_for <= p_now)
     ORDER BY o.lease_expiry, o.created_at, o.message_id
     LIMIT v_window)
  ),
  -- The ownership rules, unchanged from 148, applied to the window only.
  eligible AS MATERIALIZED (
    SELECT s.s_message_id AS e_message_id, s.s_created_at AS e_created_at
    FROM scanned s
    WHERE (
        EXISTS (SELECT 1 FROM mine_owned m WHERE m.stream_id = s.s_stream_id)
        OR (
          (s.s_partition_number IS NULL
           OR CASE WHEN v_map THEN (s.s_partition_number % 10000) IN (SELECT mp.partition_number FROM mine_partitions mp) ELSE __SCHEMA__.wh_partition_in_share(s.s_partition_number, p_instance_rank, p_active_instance_count, p_partition_owners) END)
          AND NOT EXISTS (SELECT 1 FROM others_live ol WHERE ol.stream_id = s.s_stream_id)
        )
      )
  ),
  candidates AS (
    -- The HEADS: the oldest claimable rows of the window, which choose the streams that move this
    -- call. Locked here, re-asserting the takeability the window read under the statement's
    -- snapshot (172's rule 4), so a row another session took since then is skipped, not held. Driven
    -- from the window and ordered by its column, so the plan sorts the window and probes the primary
    -- key per row; ordered by the table's column, a planner walks the arrival index instead and the
    -- window bounds nothing.
    SELECT o.message_id AS cand_message_id, o.stream_id AS cand_stream_id, o.created_at AS cand_created_at
    FROM eligible e
    JOIN __SCHEMA__.wh_outbox o ON o.message_id = e.e_message_id
    WHERE (o.instance_id IS NULL OR o.lease_expiry < p_now)
      AND (o.scheduled_for IS NULL OR o.scheduled_for <= p_now)
      AND o.processed_at IS NULL
      AND o.coalesce_group IS NULL
      -- The stream-ordering rule, unchanged from 148: an earlier row deferred into the future blocks
      -- the later ones. Evaluated here rather than over the whole window, so it runs only for the rows
      -- the walk reaches before it has its heads.
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_outbox earlier
        WHERE earlier.stream_id = o.stream_id
          AND earlier.created_at < o.created_at
          AND earlier.scheduled_for IS NOT NULL
          AND earlier.scheduled_for > p_now
          AND earlier.processed_at IS NULL
          -- #668 H3: a coalesce-pending row parks with scheduled_for = created + MaxDelay,
          -- which made it an 'earlier scheduled row' that blocked EVERY later row on the
          -- same stream from claiming for the whole window: re-armed by each new coalesce
          -- row, i.e. permanently under sustained ingest. Coalesce rows are not deferred
          -- deliveries; they are parked for folding and must not gate their stream.
          AND earlier.coalesce_group IS NULL
      )
    ORDER BY e.e_created_at, e.e_message_id
    LIMIT v_heads
    FOR UPDATE OF o SKIP LOCKED
  ),
  -- 171: the streams whose runs extend past their heads. A NULL or empty stream id is a singleton
  -- (the drain looks it up by message id), so it has no run to extend. With a run of 1 there is
  -- nothing to extend either, and the walk below never executes.
  run_streams AS (
    SELECT DISTINCT c.cand_stream_id AS run_stream_id
    FROM candidates c
    WHERE v_run > 1
      AND c.cand_stream_id IS NOT NULL
      AND c.cand_stream_id <> __EMPTY_UUID__::uuid
  ),
  -- Each stream's share of the row bound: the run length, or an even split of the bound when the
  -- chosen streams cannot all have a full run. Walking only the share is what keeps the walk priced
  -- by the batch: streams x share is about the row bound, never streams x run length.
  run_share AS (
    SELECT CASE
             WHEN p_max_rows IS NULL THEN v_run
             ELSE LEAST(v_run, GREATEST(1, CEIL(p_max_rows::NUMERIC / GREATEST(count(*), 1))::INTEGER))
           END AS share
    FROM run_streams
  ),
  run_walk AS (
    SELECT w.w_message_id, w.w_stream_id, w.w_created_at, w.w_takeable, w.w_pos
    FROM run_streams rs
    CROSS JOIN LATERAL (
      -- The stream's pending rows from its first entry, in its own order, skipping the ones this
      -- instance already holds under a live lease (they are its own run already, in flight). One
      -- range scan on idx_outbox_stream_run: Index Cond stream_id = rs.run_stream_id, the ORDER BY
      -- is the index order, the lease test is an index filter on INCLUDEd columns, and the LIMIT
      -- stops the scan at the share.
      SELECT o.message_id AS w_message_id,
             o.stream_id AS w_stream_id,
             o.created_at AS w_created_at,
             -- COALESCEd because bool_and below skips a NULL, which would read as "not blocking".
             COALESCE((o.instance_id IS NULL OR o.lease_expiry < p_now)
               AND (o.scheduled_for IS NULL OR o.scheduled_for <= p_now), FALSE) AS w_takeable,
             -- 579 (#2551): the run is the stream's next rows in emission order, never by id.
             ROW_NUMBER() OVER (ORDER BY o.emission_position) AS w_pos
      FROM __SCHEMA__.wh_outbox o
      WHERE o.stream_id = rs.run_stream_id
        AND o.processed_at IS NULL
        AND o.published_at IS NULL
        AND o.coalesce_group IS NULL
        AND (o.instance_id IS DISTINCT FROM p_instance_id
             OR o.lease_expiry IS NULL
             OR o.lease_expiry <= p_now)
      ORDER BY o.emission_position
      LIMIT (SELECT share FROM run_share)
    ) w
  ),
  -- A run is a prefix: it ends at the first row this instance may not take -- a row another
  -- instance holds under a live lease, or a retry deferred into the future. Taking a row beyond it
  -- would publish past a row that has not been published.
  run_open AS (
    SELECT rw.*, bool_and(rw.w_takeable) OVER (PARTITION BY rw.w_stream_id ORDER BY rw.w_pos) AS w_open
    FROM run_walk rw
  ),
  -- 172: the lock re-asserts that the row is still takeable. The walk above read the rows under
  -- the statement's snapshot; a row another session leased and committed since then (the drain's
  -- continuation, a sibling's claim) passes a lock that tests only its id, and this statement then
  -- held it until claim_work committed while never updating it, so that session's completion of
  -- the row waited for the whole poll. With the predicate the lock's recheck drops it, and it ends
  -- the run as a gap does.
  run_locked AS (
    SELECT o.message_id AS locked_message_id
    FROM __SCHEMA__.wh_outbox o
    WHERE o.message_id IN (SELECT ro.w_message_id FROM run_open ro WHERE ro.w_open)
      AND (o.instance_id IS NULL OR o.lease_expiry < p_now)
      AND (o.scheduled_for IS NULL OR o.scheduled_for <= p_now)
      AND o.processed_at IS NULL
    FOR UPDATE OF o SKIP LOCKED
  ),
  -- ...and at the first row another session holds locked, for the same reason: a lock skipped in
  -- the middle of a run would leave a gap in it.
  run_first_gap AS (
    SELECT ro.w_stream_id AS gap_stream_id, MIN(ro.w_pos) AS gap_pos
    FROM run_open ro
    WHERE ro.w_open
      AND NOT EXISTS (SELECT 1 FROM run_locked rl WHERE rl.locked_message_id = ro.w_message_id)
    GROUP BY ro.w_stream_id
  ),
  run_rows AS (
    SELECT ro.w_message_id, ro.w_pos, ro.w_created_at
    FROM run_open ro
    LEFT JOIN run_first_gap g ON g.gap_stream_id = ro.w_stream_id
    WHERE ro.w_open
      AND (g.gap_pos IS NULL OR ro.w_pos < g.gap_pos)
  ),
  -- The heads always, then run rows round-robin by position (every stream's 2nd row before any
  -- stream's 3rd), cut at the row bound. Cutting by position keeps each stream's rows a prefix.
  chosen AS (
    SELECT u.chosen_message_id
    FROM (
      SELECT a.chosen_message_id, MIN(a.chosen_pos) AS chosen_pos, MIN(a.chosen_created_at) AS chosen_created_at
      FROM (
        SELECT c.cand_message_id AS chosen_message_id, 0::BIGINT AS chosen_pos, c.cand_created_at AS chosen_created_at
        FROM candidates c
        UNION ALL
        SELECT r.w_message_id, r.w_pos, r.w_created_at
        FROM run_rows r
      ) a
      GROUP BY a.chosen_message_id
    ) u
    ORDER BY u.chosen_pos, u.chosen_created_at, u.chosen_message_id
    LIMIT p_max_rows
  ),
  claimed AS (
    UPDATE __SCHEMA__.wh_outbox o
    SET instance_id = p_instance_id,
        lease_expiry = p_lease_expiry,
        -- Phase H step 8 slice D: see claim_orphaned_inbox (mig 025). Single-source
        -- attempt counting; first claim → 1, every re-claim bumps; failures don't bump.
        attempts = o.attempts + 1
    FROM chosen c
    WHERE o.message_id = c.chosen_message_id
      AND (o.instance_id IS NULL OR o.lease_expiry < p_now)
      AND (o.scheduled_for IS NULL OR o.scheduled_for <= p_now)
      AND o.processed_at IS NULL
      AND o.coalesce_group IS NULL  -- 115: coalesce-pending singles are never leased by the pump
    -- Ownership, partition routing and the stream-ordering check were all resolved in `candidates`
    -- above, under a row lock, and a run shares its head's stream and so its ownership. Only the
    -- cheap invariants are re-asserted here, against a lease that lapsed between selection and write.
    --
    --   OWNER PATH: a stream's live owner always claims its messages, partition modulo ignored,
    --   which prevents the rank-churn wedge described in migration 025.
    --   UNOWNED / ABANDONED PATH: partition-based load balancing for streams with no live owner;
    --   "live" means a fresh heartbeat OR a registered LISTEN connection in pg_stat_activity.
    --   STREAM ORDERING: an earlier message in the same stream awaiting a future retry blocks the
    --   later ones, so per-stream order survives a scheduled retry.
    RETURNING o.message_id AS c_message_id, o.stream_id AS c_stream_id, o.partition_number AS c_partition_number
  ),
  -- The stream ledger: one row per stream this call leased rows on. PR #227 split its maintenance
  -- into a REFRESH (a plain UPDATE of the streams this instance already owns under a live lease, so
  -- the steady state never takes the INSERT path) and a PIN for the rest; 172 put each path's rows in
  -- stream_id order.
  --
  -- 200 (#1238): each path in order was not enough, because the refreshes all ran before the pins, so
  -- the statement took its ledger rows in two ascending runs. A claim that refreshes s3 and pins s2
  -- and a claim that refreshes s2 and pins s3 then wait on each other. A claim pins a stream another
  -- instance owns when it sees that owner as not live, which is every stream when the whole fleet's
  -- heartbeats are stale at once.
  --
  -- So every existing ledger row of the claimed streams is locked here, whoever owns it, in the one
  -- order every waiting writer of wh_active_streams uses (172's rule 2). This is the only place this
  -- statement waits for an existing ledger row. The refresh and the takeover below update rows it
  -- already holds and cannot wait, and the insert of a new stream comes after it, in the same order.
  -- The rows locked are the ones the two paths locked before; only their order changes.
  ledger_locked AS (
    SELECT ast.stream_id AS locked_stream_id,
           ast.assigned_instance_id AS locked_owner,
           ast.lease_expiry AS locked_lease_expiry
    -- 202: nothing to lock when the caller writes the ledger itself.
    FROM (SELECT DISTINCT c.c_stream_id FROM claimed c WHERE c.c_stream_id IS NOT NULL AND NOT p_ledger_deferred) cs
    JOIN __SCHEMA__.wh_active_streams ast ON ast.stream_id = cs.c_stream_id
    ORDER BY ast.stream_id
    FOR UPDATE OF ast
  ),
  -- REFRESH: the streams this instance owns under a live lease, judged on the row as locked. Same
  -- write as before: last_activity_at and the lease move, the owner does not.
  refreshed AS (
    UPDATE __SCHEMA__.wh_active_streams ast
    SET last_activity_at = p_now,
        lease_expiry = p_lease_expiry
    FROM ledger_locked ll
    WHERE ast.stream_id = ll.locked_stream_id
      AND ll.locked_owner = p_instance_id
      AND ll.locked_lease_expiry > p_now
    RETURNING ast.stream_id AS refreshed_stream_id
  ),
  -- TAKEOVER: every other locked row, under the ownership rules the pin's ON CONFLICT branch applied
  -- (148), unchanged. Whoever the CASE leaves as owner holds the lease: a stream with no owner, one
  -- whose owner is no longer registered, or one already this instance's under a lapsed lease is
  -- leased to p_lease_expiry; a stream another registered instance owns keeps its owner and its lease.
  taken_over AS (
    UPDATE __SCHEMA__.wh_active_streams ast
    SET last_activity_at = p_now,
        assigned_instance_id = CASE
          -- 566 (#2555): in map mode the partition's owner claimed the stream, so the ledger names it.
          WHEN v_map THEN p_instance_id
          WHEN ast.assigned_instance_id IS NULL THEN p_instance_id
          WHEN NOT EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_service_instances si
            WHERE si.instance_id = ast.assigned_instance_id
          ) THEN p_instance_id
          ELSE ast.assigned_instance_id
        END,
        lease_expiry = CASE
          WHEN v_map THEN p_lease_expiry
          WHEN ast.assigned_instance_id IS NULL THEN p_lease_expiry
          WHEN ast.assigned_instance_id = p_instance_id THEN p_lease_expiry
          WHEN NOT EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_service_instances si
            WHERE si.instance_id = ast.assigned_instance_id
          ) THEN p_lease_expiry
          ELSE ast.lease_expiry
        END
    FROM ledger_locked ll
    WHERE ast.stream_id = ll.locked_stream_id
      -- Exactly the rows the refresh does not take. COALESCEd because a NULL owner or lease makes the
      -- refresh's test NULL, and NOT NULL would leave the row to neither path.
      AND NOT COALESCE(ll.locked_owner = p_instance_id AND ll.locked_lease_expiry > p_now, FALSE)
    RETURNING ast.stream_id AS taken_stream_id
  ),
  -- PIN: the streams with no ledger row, inserted after the lock pass and in stream_id order. The
  -- sort consumes the whole anti-join before the first insert, and a stream that is not in
  -- ledger_locked reads it to the end, so every row lock above is taken before any insert. An insert
  -- waits only for another claim's uncommitted insert of the same stream, which that claim also makes
  -- after its own lock pass and in the same order. A row that another session created and committed
  -- since this statement's snapshot is left to it (DO NOTHING) rather than locked here, out of order.
  pinned AS (
    INSERT INTO __SCHEMA__.wh_active_streams AS ast
      (stream_id, partition_number, assigned_instance_id, last_activity_at, lease_expiry)
    SELECT DISTINCT ON (sub.stream_id) sub.stream_id, sub.partition_number, p_instance_id, p_now, p_lease_expiry
    FROM (
      SELECT c.c_stream_id AS stream_id, COALESCE(c.c_partition_number, 0) AS partition_number
      FROM claimed c
      WHERE c.c_stream_id IS NOT NULL
        AND NOT p_ledger_deferred
        AND NOT EXISTS (
          SELECT 1 FROM ledger_locked ll WHERE ll.locked_stream_id = c.c_stream_id
        )
    ) sub
    ORDER BY sub.stream_id
    ON CONFLICT (stream_id) DO NOTHING
    RETURNING ast.stream_id AS pinned_stream_id
  )
  SELECT c.c_message_id AS message_id, c.c_stream_id AS stream_id, c.c_partition_number AS partition_number FROM claimed c;
END;
$$ LANGUAGE plpgsql;
COMMENT ON FUNCTION __SCHEMA__.claim_orphaned_outbox(UUID, INTEGER, INTEGER, TIMESTAMPTZ, TIMESTAMPTZ, INTEGER, TIMESTAMPTZ, INTEGER, INTEGER, INTEGER, BOOLEAN, SMALLINT[]) IS
  'Acquires unowned/abandoned pending outbox rows for an instance (see 024 for the ownership rationale, 115 for the '
  'coalesce-aware selection, 148 for the stream lease). 171: the oldest claimable rows (at most p_max_heads) choose '
  'the streams that move, and each leases up to p_run_length of its next consecutive rows as a prefix, within '
  'p_max_rows in total; a run of 1 is the previous behavior. 172: a run row another session leased since the snapshot '
  'is not locked. 194: the heads are chosen from a window of eight times as many oldest claimable rows per lane '
  '(unowned by arrival, expired leases by expiry), so a poll costs a multiple of its batch whatever the backlog holds. '
  '200: the stream ledger rows of the claimed streams are locked in one stream_id pass, whoever owns them, before they '
  'are refreshed or taken over, and streams with no row are inserted after that pass in the same order. 202: '
  'p_ledger_deferred leaves the ledger to the caller (claim_work writes it once for all its acquisitions), and each '
  'row carries its partition number for that write. 566 (#2554, #2555): with the service''s switch on (wh_partition_ownership_by_map), decided through wh_i_own alone.';

-- ---------------------------------------------------------------------------------------------
-- wh_continue_outbox_streams_in_emission_order: wh_continue_outbox_streams' last word (566), with a position cursor.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_continue_outbox_streams_in_emission_order');

-- <docs>fundamentals/work-coordinator/per-stream-drain</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:ContinueOutboxStreams_WhileAnotherSessionHoldsTheStreamLedger_NeverWaitsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:ConcurrentClaimsDrainsContinuationsCompletionsAndFailureReleases_NeverDeadlockAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunSqlTests.cs:ContinueOutboxStreams_LeasesTheNextRunAndReturnsOnlyRowsAfterTheCursorAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunSqlTests.cs:ContinueOutboxStreams_AStreamThisInstanceDoesNotOwn_IsNotContinuedAsync</tests>
-- <docs>fundamentals/work-coordinator/partition-assignment#ownership-by-map</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipByMapClaimSqlTests.cs:Continuation_ContinuesOnlyAStreamOnAPartitionThisInstanceOwnsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/ClaimPathsDecideThroughTheOwnershipTableGuardTests.cs:EveryClaimFunction_DecidesThroughWhIOwn_AndRanksOnlyInTheLegacyBranchAsync</tests>
-- <docs>fundamentals/messaging/sequence-integrity#how-the-hold-works</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SequenceIntegritySqlTests.cs:Continue_StopsAStreamAtItsHeldEvent_AsTheFetchDoesAsync</tests>
-- <docs>fundamentals/event-store/version-ordering#dispatch-order</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxEmissionOrderTests.cs</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_continue_outbox_streams_in_emission_order(
  p_instance_id UUID,
  p_stream_ids UUID[],
  -- The last message the drain PUBLISHED on each stream, position for position with p_stream_ids.
  -- Rows this instance holds at or before it are published and waiting for their completion to
  -- land; they are neither leased again nor returned.
  p_after_positions BIGINT[],
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
  priority INTEGER,
  stream_position BIGINT
) AS $$
#variable_conflict use_column
DECLARE
  v_now TIMESTAMPTZ := NOW();
  v_run INTEGER := GREATEST(COALESCE(p_run_length, 1), 1);
  v_run_ids UUID[];
  -- 566 (#2554, #2555): the service's switch, read once: on, the partition ownership table alone decides.
  v_map BOOLEAN := __SCHEMA__.wh_partition_ownership_by_map();
BEGIN
  IF p_stream_ids IS NULL OR array_length(p_stream_ids, 1) IS NULL THEN
    RETURN;
  END IF;

  -- Lease each stream's next run. Only a stream whose lease this instance holds is continued: the
  -- claim took that lease with the stream's earlier rows, and continuing under it is what lets the
  -- drain move on without waiting for the next claim cycle while keeping the stream on one instance.
  -- The rows join the stream's lease rather than starting a new one.
  WITH asked AS (
    SELECT a.a_stream_id, a.a_after_position
    FROM unnest(p_stream_ids, p_after_positions) AS a(a_stream_id, a_after_position)
    WHERE a.a_stream_id IS NOT NULL
      AND a.a_stream_id <> __EMPTY_UUID__::uuid
  ),
  owned AS (
    SELECT DISTINCT ON (ak.a_stream_id) ak.a_stream_id AS o_stream_id, ak.a_after_position AS o_after_position,
           ast.lease_expiry AS o_lease_expiry
    FROM asked ak
    JOIN __SCHEMA__.wh_active_streams ast
      ON ast.stream_id = ak.a_stream_id
     -- 566 (#2555): in map mode a stream is continued only while this instance owns its partition (wh_i_own), so a
     -- moved partition's next run is not leased by its old owner; the stored owner is the legacy mode's test.
     AND CASE WHEN v_map THEN __SCHEMA__.wh_i_own(ast.partition_number, p_instance_id) IS NOT NULL ELSE ast.assigned_instance_id = p_instance_id END
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
             ROW_NUMBER() OVER (ORDER BY o.emission_position) AS w_pos
      FROM __SCHEMA__.wh_outbox o
      WHERE o.stream_id = ow.o_stream_id
        AND o.processed_at IS NULL
        AND o.published_at IS NULL
        AND o.coalesce_group IS NULL
        AND NOT COALESCE(o.instance_id = p_instance_id
                         AND o.lease_expiry > v_now
                         AND o.emission_position <= ow.o_after_position, FALSE)
      ORDER BY o.emission_position
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
    WHERE (v_map OR ast.assigned_instance_id = p_instance_id)
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
      -- 559 (#2542): an event held for its commit-order stamp: it has a hold that has not run out, and its stored
      -- event has no commit sequence yet. A row with no stored event is never held, since nothing would stamp it.
      (o.sequence_hold_until IS NOT NULL AND o.sequence_hold_until > v_now
        AND es.event_id IS NOT NULL AND es.commit_sequence IS NULL) AS sequence_held,
      ROW_NUMBER() OVER (
        PARTITION BY o.stream_id
        ORDER BY o.emission_position
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
      -- 559 (#2542): true from a stream's first held row on. That row and every row after it stay where they are,
      -- whatever their own setting, so a receiver never sees the stream out of order.
      bool_or(r.sequence_held)
        OVER (PARTITION BY r.stream_id
              ORDER BY r.emission_position
              ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS behind_hold,
      SUM(COALESCE(octet_length(r.event_data::TEXT), 0)
          + COALESCE(octet_length(r.metadata::TEXT), 0))
        OVER (PARTITION BY r.stream_id
              ORDER BY r.emission_position
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
    b.priority,
    b.emission_position
  FROM budgeted b
  WHERE NOT b.behind_hold
    AND (p_max_bytes IS NULL
     OR b.rank_in_stream = 1
     OR b.running_bytes <= p_max_bytes)
  ORDER BY b.stream_id, b.emission_position;
END;
$$ LANGUAGE plpgsql SET plan_cache_mode = force_custom_plan;

COMMENT ON FUNCTION __SCHEMA__.wh_continue_outbox_streams_in_emission_order(UUID, UUID[], BIGINT[], INTEGER, BIGINT) IS
  '579 (#2551): wh_continue_outbox_streams in emission order: each stream''s next run after the last position the drain '
  'published (p_after_positions), walked and returned by emission position, never by id, with each row''s stream_position. '
  'wh_continue_outbox_streams keeps its id cursor for a caller built before it.';

-- ---------------------------------------------------------------------------------------------
-- wh_claim_due_schedules: copied from its last word (068), with the 566 delta: the switch's map branch.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_claim_due_schedules');

-- <docs>fundamentals/temporal/temporal-engine</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TemporalScheduleClaimSqlTests.cs:DueOwned_SpawnsOccurrence_AdvancesNextFire_LogsRunAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TemporalScheduleClaimSqlTests.cs:NotOwned_NotClaimedAsync</tests>
-- <docs>fundamentals/work-coordinator/partition-assignment#ownership-by-map</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipByMapClaimSqlTests.cs:Schedules_FireOnThePartitionsOwner_NotTheStoredOwnerAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/ClaimPathsDecideThroughTheOwnershipTableGuardTests.cs:EveryClaimFunction_DecidesThroughWhIOwn_AndRanksOnlyInTheLegacyBranchAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_claim_due_schedules(
  p_instance_id UUID,
  p_lease_expiry TIMESTAMPTZ,
  p_partition_count INTEGER,
  p_limit INTEGER DEFAULT 100,
  p_now TIMESTAMPTZ DEFAULT NULL
) RETURNS TABLE(
  o_schedule_id UUID,
  o_occurrence_id UUID,
  o_fired_at TIMESTAMPTZ,
  o_next_fire_at TIMESTAMPTZ,
  o_completed BOOLEAN
)
SET timezone = 'UTC'   -- function-local: due comparison + spawn run in UTC regardless of caller session
AS $$
DECLARE
  v_sched RECORD;
  v_occurrence_id UUID;
  v_next TIMESTAMPTZ;
  v_completed BOOLEAN;
  v_cursor TIMESTAMPTZ;
  v_horizon TIMESTAMPTZ;
  v_skipped BOOLEAN;
  -- DB clock is authority for the due decision (avoids multi-instance skew). p_now is a test seam.
  v_now TIMESTAMPTZ := COALESCE(p_now, NOW());
  -- 566 (#2554, #2555): the service's switch, read once: on, the partition ownership table alone decides.
  v_map BOOLEAN := __SCHEMA__.wh_partition_ownership_by_map();
BEGIN
  FOR v_sched IN
    SELECT sc.*
    FROM __SCHEMA__.wh_schedules sc
    -- 566 (#2555): in map mode a schedule fires on the owner of its stream's partition (wh_i_own), the schedule's own
    -- number (compute_partition for a row without one). The stored owner is the legacy mode's test, and a stream with no
    -- ledger row fires nowhere in it, as before.
    LEFT JOIN __SCHEMA__.wh_active_streams s ON s.stream_id = sc.stream_id
    WHERE CASE WHEN v_map
               THEN __SCHEMA__.wh_i_own(COALESCE(sc.partition_number, __SCHEMA__.compute_partition(sc.stream_id, p_partition_count)), p_instance_id) IS NOT NULL
               ELSE s.assigned_instance_id = p_instance_id END
      AND sc.status = 0
      AND sc.next_fire_at <= v_now
    ORDER BY sc.next_fire_at
    LIMIT p_limit
    FOR UPDATE OF sc SKIP LOCKED
  LOOP
    -- Deterministic occurrence id (schedule + occurrence#) so a redelivered fire dedups at the outbox.
    v_occurrence_id := md5(v_sched.schedule_id::text || ':' || v_sched.occurrence_count::text)::uuid;
    v_cursor := v_sched.next_fire_at;
    v_skipped := FALSE;

    -- CatchUp burst bound: never replay occurrences older than the lookback horizon. Jump the cursor
    -- forward to the horizon first so a long outage can't unleash an unbounded replay storm.
    IF v_sched.misfire_policy = 1 AND v_sched.catch_up_lookback_ms IS NOT NULL THEN
      v_horizon := v_now - make_interval(secs => v_sched.catch_up_lookback_ms / 1000.0);
      IF v_cursor < v_horizon THEN
        v_cursor := COALESCE(__SCHEMA__._wh_advance_past(
          v_sched.recurrence_kind, v_sched.cron, v_sched.interval_ms, v_sched.timezone,
          v_cursor, v_horizon), v_cursor);
      END IF;
    END IF;

    -- One-step next fire from the cursor.
    v_next := __SCHEMA__.wh_schedule_next_fire(
      v_sched.recurrence_kind, v_sched.cron, v_sched.interval_ms, v_sched.timezone, v_cursor);

    -- MISFIRE: the very next fire is ALSO already due => more than one fire was missed (paused a long
    -- time / owner was down). Without this, an interval schedule would advance one step per claim and
    -- grind through every missed occurrence.
    IF v_next IS NOT NULL AND v_next <= v_now THEN
      IF v_sched.misfire_policy = 0 THEN
        -- Coalesce (default): fire ONCE now, then fast-forward past the whole missed window.
        v_next := __SCHEMA__._wh_advance_past(
          v_sched.recurrence_kind, v_sched.cron, v_sched.interval_ms, v_sched.timezone, v_cursor, v_now);
      ELSIF v_sched.misfire_policy = 2 THEN
        -- Skip: do NOT fire for the missed window at all; just fast-forward back onto cadence.
        v_skipped := TRUE;
        v_next := __SCHEMA__._wh_advance_past(
          v_sched.recurrence_kind, v_sched.cron, v_sched.interval_ms, v_sched.timezone, v_cursor, v_now);
      END IF;
      -- CatchUp (1): keep the one-step next => replay each missed occurrence, one per claim
      -- (bounded by catch_up_lookback_ms above).
    END IF;

    -- Bounds. A skipped fire doesn't consume a max_occurrences slot.
    IF v_next IS NOT NULL AND v_sched.until_at IS NOT NULL AND v_next > v_sched.until_at THEN
      v_next := NULL;
    END IF;
    IF NOT v_skipped AND v_next IS NOT NULL AND v_sched.max_occurrences IS NOT NULL
       AND (v_sched.occurrence_count + 1) >= v_sched.max_occurrences THEN
      v_next := NULL;
    END IF;
    v_completed := (v_next IS NULL);

    IF v_skipped THEN
      -- No occurrence spawned: record the misfire so the skip stays auditable.
      INSERT INTO __SCHEMA__.wh_schedule_runs
        (schedule_id, occurrence_id, fired_at, status, instance_id)
      VALUES (v_sched.schedule_id, v_occurrence_id, v_now, 2, p_instance_id);   -- 2 = Skipped (misfire)
    ELSE
      -- Spawn the occurrence transactionally through the shared emit (run status 0 = fired).
      PERFORM __SCHEMA__._wh_spawn_occurrence(
        v_sched.schedule_id, v_occurrence_id, v_sched.occurrence_count,
        v_sched.event_type, v_sched.event_data, v_sched.scope, v_sched.stream_id,
        v_sched.delivery_guarantee, p_instance_id, p_lease_expiry, v_now, p_partition_count,
        0::SMALLINT, v_sched.authority_principal_id, v_sched.authority_claims);
    END IF;

    -- Advance the schedule. Completed schedules keep their (past) next_fire_at; status 2 excludes
    -- them from the due-detect/notify queries so they never re-fire, satisfying the NOT NULL column.
    UPDATE __SCHEMA__.wh_schedules
    SET last_fire_at = CASE WHEN v_skipped THEN last_fire_at ELSE v_now END,
        next_fire_at = COALESCE(v_next, next_fire_at),
        occurrence_count = occurrence_count + CASE WHEN v_skipped THEN 0 ELSE 1 END,
        status = CASE WHEN v_completed THEN 2 ELSE status END,
        version = version + 1
    WHERE schedule_id = v_sched.schedule_id;

    o_schedule_id := v_sched.schedule_id;
    o_occurrence_id := v_occurrence_id;
    o_fired_at := v_now;
    o_next_fire_at := v_next;
    o_completed := v_completed;
    RETURN NEXT;
  END LOOP;
END;
$$ LANGUAGE plpgsql;

-- ---------------------------------------------------------------------------------------------
-- fetch_outbox_batch_in_stream_order: fetch_outbox_batch's last word (559), in emission order, with its position.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('fetch_outbox_batch_in_stream_order');

-- <docs>fundamentals/messaging/sequence-integrity#how-the-hold-works</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SequenceIntegritySqlTests.cs:Fetch_AnUnstampedEventWithNoHold_IsPublishedAtOnceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SequenceIntegritySqlTests.cs:Fetch_AnUnstampedEventWithinItsHold_IsHeld_AndPublishedOnceStampedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SequenceIntegritySqlTests.cs:Fetch_ABoundedHoldThatRanOut_IsPublishedUnstampedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SequenceIntegritySqlTests.cs:Fetch_ARowBehindAHeldEvent_IsHeldWhateverItsOwnSettingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SequenceIntegritySqlTests.cs:Fetch_AHoldWithNoStoredEvent_HoldsNothingAsync</tests>
-- <docs>fundamentals/event-store/version-ordering#dispatch-order</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxEmissionOrderTests.cs</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.fetch_outbox_batch_in_stream_order(
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
  priority INTEGER,
  stream_position BIGINT
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
      -- 559 (#2542): an event held for its commit-order stamp: it has a hold that has not run out, and its stored
      -- event has no commit sequence yet. A row with no stored event is never held, since nothing would stamp it.
      (o.sequence_hold_until IS NOT NULL AND o.sequence_hold_until > NOW()
        AND es.event_id IS NOT NULL AND es.commit_sequence IS NULL) AS sequence_held,
      ROW_NUMBER() OVER (
        PARTITION BY o.stream_id
        ORDER BY o.emission_position
      ) AS rank_in_stream
    FROM __SCHEMA__.wh_outbox o
    LEFT JOIN __SCHEMA__.wh_event_store es
      ON o.is_event AND es.event_id = o.message_id
    -- v0.658 slice 7: singleton-stream / Empty-stream rows are looked up by
    -- message_id-as-sentinel rather than stream_id. The coordinator's claim_work
    -- output emits message_id as the sentinel for rows whose stream_id is NULL
    -- (the documented singleton-stream marker) or Guid.Empty (the producer-side
    -- bug from a production forensic investigation: v0.657 slice 3's Empty→WorkId fallback). The
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
      -- 559 (#2542): true from a stream's first held row on. That row and every row after it stay where they are,
      -- whatever their own setting, so a receiver never sees the stream out of order.
      bool_or(r.sequence_held)
        OVER (PARTITION BY r.stream_id
              ORDER BY r.emission_position
              ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS behind_hold,
      SUM(COALESCE(octet_length(r.event_data::TEXT), 0)
          + COALESCE(octet_length(r.metadata::TEXT), 0))
        OVER (PARTITION BY r.stream_id
              ORDER BY r.emission_position
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
    b.priority,
    b.emission_position
  FROM budgeted b
  WHERE NOT b.behind_hold
    AND (p_max_bytes IS NULL
     OR b.rank_in_stream = 1              -- never starve a stream on an oversized head message
     OR b.running_bytes <= p_max_bytes)
  ORDER BY b.stream_id, b.emission_position;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.fetch_outbox_batch_in_stream_order(UUID[], UUID, INTEGER, BIGINT) IS
  '579 (#2551): fetch_outbox_batch in emission order: a stream''s leased rows ranked and returned by emission position, '
  'never by id, with each row''s stream_position, the order the drain publishes the stream in. fetch_outbox_batch keeps '
  'its result for a caller built before it.';

-- ---------------------------------------------------------------------------------------------
-- flush_completions: copied from its last word (570), with the 579 delta: a failure's run in emission order.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('flush_completions');

-- <docs>fundamentals/work-coordinator/batched-flushers</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FlushCompletionsSqlTests.cs:FlushCompletions_OutboxAndPerspective_AppliesBothInOneCallAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:FlushCompletions_FailingARowAnotherTransactionCompletesFirst_NeverDeadlocksAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:FlushCompletions_ReleasingARunAnotherTransactionCompletesFirst_NeverDeadlocksAsync</tests>
-- <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipFencingSqlTests.cs:Flush_AnOutboxCompletionUnderAStaleEpoch_AffectsNoRow_AndIsReportedFencedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipFencingSqlTests.cs:Flush_AnOutboxCompletionUnderTheCurrentEpoch_CompletesAsBeforeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipFencingSqlTests.cs:Flush_APerspectiveEventCompletionUnderAStaleEpoch_AffectsNoRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipFencingSqlTests.cs:Flush_ACursorAdvanceUnderAStaleEpoch_LeavesTheCursorAsItWasAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipFencingSqlTests.cs:Flush_AFailureUnderAStaleEpoch_IsNotRecordedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipFencingSqlTests.cs:SwitchOff_AnEpochIsIgnored_AndTheWriteLandsAsBeforeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipFencingSqlTests.cs:MapMode_AWriteThatNamesNoEpoch_LandsAsBeforeAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.flush_completions(
  p_outbox_ids UUID[],
  p_perspective_cursors JSONB,
  p_perspective_event_work_ids UUID[],
  p_failures JSONB,          -- [{Category: the work category, Items: [...]}]
  -- 570 (#2556): the partition epoch each outbox id and each perspective work id was claimed under, by position. NULL
  -- (an older caller, an inline path) names none, and nothing is fenced.
  p_outbox_epochs BIGINT[] DEFAULT NULL,
  p_perspective_event_epochs BIGINT[] DEFAULT NULL
) RETURNS VOID AS $$
DECLARE
  v_failure_group RECORD;
  -- 245: the failed outbox rows' ids, read from the request without touching a table.
  v_outbox_failed UUID[];
  v_has_failures BOOLEAN := p_failures IS NOT NULL AND jsonb_array_length(p_failures) > 0;
BEGIN
  -- 570 (#2556): in map mode a completion or failure made under a partition epoch that is no longer current lands
  -- nowhere: it is dropped from the request before any row is written, and counted (whizbang.fenced). Perspective work
  -- and cursors are fenced by complete_perspective, which receives the epochs. Off, or naming no epoch, nothing changes.
  IF (p_outbox_epochs IS NOT NULL OR v_has_failures) AND __SCHEMA__.wh_partition_ownership_by_map() THEN
    p_outbox_ids := __SCHEMA__._wh_fence_outbox_ids(p_outbox_ids, p_outbox_epochs);
    IF v_has_failures THEN
      p_failures := __SCHEMA__._wh_fence_failures(p_failures);
      v_has_failures := jsonb_array_length(p_failures) > 0;
    END IF;
  END IF;

  IF v_has_failures THEN
    SELECT array_agg((i.item->>__ENVELOPE_FIELD_MESSAGE_ID__)::UUID)
      INTO v_outbox_failed
      FROM jsonb_array_elements(p_failures) AS g(elem)
      CROSS JOIN LATERAL jsonb_array_elements(
        CASE WHEN jsonb_typeof(g.elem->'Items') = 'array' THEN g.elem->'Items' ELSE '[]'::JSONB END) AS i(item)
     WHERE g.elem->>'Category' = __CATEGORY_OUTBOX__
       AND i.item->>__ENVELOPE_FIELD_MESSAGE_ID__ IS NOT NULL;
  END IF;

  -- 245: every outbox row the steps below write, locked in one (stream_id, created_at, message_id) pass before any of
  -- them is written. kind 1: a completion, which complete_outbox_published writes only while the row is open. kind 2:
  -- a failed row, which process_outbox_failures writes whatever its state. kind 3: the rest of a failed row's run,
  -- which process_outbox_failures releases while its holder still leases it and it is pending.
  IF array_length(p_outbox_ids, 1) IS NOT NULL OR v_outbox_failed IS NOT NULL THEN
    PERFORM 1
    FROM (
      SELECT o.message_id
      FROM __SCHEMA__.wh_outbox o
      JOIN (
        SELECT c.id AS k_id, 1 AS k_kind, NULL::UUID AS k_holder
        FROM unnest(p_outbox_ids) AS c(id)
        UNION ALL
        SELECT f.id, 2, NULL::UUID
        FROM unnest(v_outbox_failed) AS f(id)
        UNION ALL
        SELECT t.message_id, 3, f.instance_id
        FROM __SCHEMA__.wh_outbox f
        JOIN __SCHEMA__.wh_outbox t
          ON t.stream_id = f.stream_id
         -- 579 (#2551): the rest of the run is the rows after the failed one in emission order, as process_outbox_failures
         -- releases them; they are still locked in the canonical (stream_id, created_at, message_id) order below.
         AND t.emission_position > f.emission_position
         AND t.instance_id = f.instance_id
        WHERE f.message_id = ANY(v_outbox_failed)
          AND f.instance_id IS NOT NULL
          AND f.stream_id IS NOT NULL
          AND f.stream_id <> __EMPTY_UUID__::uuid
          AND NOT (f.metadata->>'scheduleId' IS NOT NULL
                   AND COALESCE((f.metadata->>'deliveryGuarantee')::INTEGER, 0) = 1)
          AND t.processed_at IS NULL
          AND t.published_at IS NULL
          AND t.coalesce_group IS NULL
      ) k ON o.message_id = k.k_id
      WHERE (k.k_kind = 1 AND o.processed_at IS NULL)
         OR k.k_kind = 2
         OR (k.k_kind = 3
             AND o.instance_id = k.k_holder
             AND o.processed_at IS NULL
             AND o.published_at IS NULL
             AND o.coalesce_group IS NULL)
      ORDER BY o.stream_id, o.created_at, o.message_id
      FOR UPDATE OF o
    ) locked;
  END IF;

  IF p_outbox_ids IS NOT NULL AND array_length(p_outbox_ids, 1) IS NOT NULL THEN
    PERFORM __SCHEMA__.complete_outbox_published(p_outbox_ids);
  END IF;

  -- 245: the failures in the canonical table order, outbox then inbox, before the perspective work.
  IF v_has_failures THEN
    FOR v_failure_group IN
      SELECT elem->>'Category' AS category, elem->'Items' AS items
      FROM jsonb_array_elements(p_failures) WITH ORDINALITY AS e(elem, ord)
      WHERE elem->>'Category' IN (__CATEGORY_OUTBOX__, __CATEGORY_INBOX__)
      ORDER BY (elem->>'Category' = __CATEGORY_INBOX__), ord
    LOOP
      PERFORM __SCHEMA__.report_failures(v_failure_group.category, v_failure_group.items);
    END LOOP;
  END IF;

  IF (p_perspective_cursors IS NOT NULL AND jsonb_array_length(p_perspective_cursors) > 0)
     OR (p_perspective_event_work_ids IS NOT NULL AND array_length(p_perspective_event_work_ids, 1) IS NOT NULL) THEN
    PERFORM __SCHEMA__.complete_perspective(
      COALESCE(p_perspective_cursors, '[]'::JSONB),
      COALESCE(p_perspective_event_work_ids, ARRAY[]::UUID[]),
      FALSE,
      -- 570 (#2556): the work ids' epochs; the cursors carry their own.
      p_perspective_event_epochs
    );
  END IF;

  -- Perspective failures, and any category report_failures does not know (it raises, as before).
  IF v_has_failures THEN
    FOR v_failure_group IN
      SELECT elem->>'Category' AS category, elem->'Items' AS items
      FROM jsonb_array_elements(p_failures) WITH ORDINALITY AS e(elem, ord)
      WHERE elem->>'Category' IS DISTINCT FROM __CATEGORY_OUTBOX__
        AND elem->>'Category' IS DISTINCT FROM __CATEGORY_INBOX__
      ORDER BY ord
    LOOP
      PERFORM __SCHEMA__.report_failures(v_failure_group.category, v_failure_group.items);
    END LOOP;
  END IF;
END;
$$ LANGUAGE plpgsql;
COMMENT ON FUNCTION __SCHEMA__.flush_completions(UUID[], JSONB, UUID[], JSONB, BIGINT[], BIGINT[]) IS
'Composite single-round-trip flusher: complete_outbox_published + report_failures (per category) + complete_perspective in one transaction. 245 (#1785): every outbox row it writes (completed rows, failed rows, the runs the failures release) is locked first in one (stream_id, created_at, message_id) pass, and the steps run in the canonical table order (outbox, inbox, perspective work), so it cannot deadlock against another writer that takes outbox rows in that order. 570 (#2556): in map mode, a completion, failure or cursor named under a partition epoch that is no longer current is dropped before anything is written and reported as fenced (NOTICE whizbang.fenced, DETAIL kind=count).';

-- ---------------------------------------------------------------------------------------------
-- process_outbox_failures: copied from its last word (412), with the 579 delta: a failure's run in emission order.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('process_outbox_failures');

-- <docs>fundamentals/work-coordinator/per-stream-drain</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:ProcessOutboxFailures_TakesItsRowsInStreamOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunSqlTests.cs:ProcessOutboxFailures_MidRun_ReleasesTheRestOfTheRunToBeRetriedInOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxInboxFailureReasonSqlTests.cs:OutboxFailure_InTheShapeTheRuntimeWrites_KeepsItsReasonAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimOrphanedAttemptsIncrementSqlTests.cs:ProcessOutboxFailures_DoesNotDoubleCountAttemptsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:ProcessOutboxFailures_TakesTheRunItReleasesInStreamOrderAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.process_outbox_failures(
  p_failures JSONB,
  p_now TIMESTAMPTZ
) RETURNS VOID AS $$
DECLARE
  v_failure RECORD;
  v_schedule_id TEXT;
  v_delivery INTEGER;
  -- 171: where the failed row sat, so the rest of its run can be released behind it.
  v_holder UUID;
  v_stream UUID;
  -- 579 (#2551): the failed row's place in its stream; the rows after it in emission order are its run's rest.
  v_emission BIGINT;
BEGIN
  IF jsonb_array_length(p_failures) = 0 THEN RETURN; END IF;

  -- 172: the failures are visited in (stream_id, created_at, message_id) order, not the order the
  -- flush delivered them in. Each one locks its row and then the rest of its run, which lie after it
  -- in that same order, so a flush takes its rows in one ascending pass, the order every other
  -- waiting writer of wh_outbox uses (complete_outbox_published, renew_leases). The read that sorts
  -- them takes no lock. A failure whose row is gone sorts last and updates nothing, as before.
  FOR v_failure IN
    SELECT f.msg_id, f.status_flags, f.error_message, f.failure_reason
    FROM (
      SELECT
        (e.elem->>__ENVELOPE_FIELD_MESSAGE_ID__)::UUID as msg_id,
        (e.elem->>'CompletedStatus')::INTEGER as status_flags,
        e.elem->>'Error' as error_message,
        COALESCE(e.elem->>'Reason', e.elem->>'FailureReason')::INTEGER as failure_reason,
        e.ord
      FROM jsonb_array_elements(p_failures) WITH ORDINALITY AS e(elem, ord)
    ) f
    LEFT JOIN __SCHEMA__.wh_outbox fo ON fo.message_id = f.msg_id
    ORDER BY fo.stream_id NULLS LAST, fo.created_at, f.msg_id, f.ord
  LOOP
    SELECT o.metadata->>'scheduleId', COALESCE((o.metadata->>'deliveryGuarantee')::INTEGER, 0),
           o.instance_id, o.stream_id, o.emission_position
    INTO v_schedule_id, v_delivery, v_holder, v_stream, v_emission
    FROM __SCHEMA__.wh_outbox o
    WHERE o.message_id = v_failure.msg_id;

    IF v_schedule_id IS NOT NULL THEN
      INSERT INTO __SCHEMA__.wh_schedule_runs
        (schedule_id, occurrence_id, fired_at, status, error_message)
      VALUES (v_schedule_id::UUID, v_failure.msg_id, p_now, 1, v_failure.error_message);
    END IF;

    IF v_schedule_id IS NOT NULL AND v_delivery = 1 THEN
      -- AT-MOST-ONCE (delivery_guarantee = 1): never redeliver. Parked terminally; the failure is
      -- durably recorded in wh_schedule_runs above, which is what makes this safe.
      UPDATE __SCHEMA__.wh_outbox o
      SET status = o.status | v_failure.status_flags | 32768,  -- Set Failed bit (32768)
          error = v_failure.error_message,
          failure_reason = COALESCE(v_failure.failure_reason, 0),
          scheduled_for = 'infinity'::TIMESTAMPTZ,             -- terminal: no retry, ever
          instance_id = NULL,
          lease_expiry = NULL
      WHERE o.message_id = v_failure.msg_id;
    ELSE
      -- Default (at-least-once, and every non-schedule message): retry with backoff. Attempts are
      -- counted by claim_orphaned_outbox alone, so the backoff reads o.attempts as it stands.
      UPDATE __SCHEMA__.wh_outbox o
      SET status = o.status | v_failure.status_flags | 32768,  -- Set Failed bit (32768)
          error = v_failure.error_message,
          failure_reason = COALESCE(v_failure.failure_reason, 0),  -- Default to Unknown (0)
          -- Exponential backoff: 30s * 2^attempts, capped at 5 minutes
          scheduled_for = p_now + (INTERVAL '30 seconds' * LEAST(POWER(2, LEAST(o.attempts, 10)), 10)),
          instance_id = NULL,
          lease_expiry = NULL
      WHERE o.message_id = v_failure.msg_id;

      -- 171: release the rest of the run. The rows of the same stream after the failed one that its
      -- holder still leases were claimed with it and must not publish ahead of its retry. Before
      -- this they stayed leased, the next drain of the stream fetched them, and they went out
      -- before the row that failed. Released, they are unowned and the acquisition's ordering rule
      -- (an earlier row deferred into the future blocks the later ones) holds them behind the
      -- retry. The attempt their claim charged is refunded: none of them was attempted. One range
      -- scan on idx_outbox_stream_run from the failed row's position; the holder test is an index
      -- filter on an INCLUDEd column.
      IF v_holder IS NOT NULL AND v_stream IS NOT NULL AND v_stream <> __EMPTY_UUID__::uuid THEN
        -- 412: the run's rows are locked in (created_at, message_id) order before they are released. The bare
        -- multi-row UPDATE locked them in whatever order its plan read them, which on a plan that reads the heap is not
        -- the stream's, so it could hold a later row of the run while waiting on an earlier one that a completion of
        -- the same rows, locking in stream order, already held: a deadlock.
        UPDATE __SCHEMA__.wh_outbox o
        SET instance_id = NULL,
            lease_expiry = NULL,
            attempts = GREATEST(o.attempts - 1, 0)
        FROM (
          SELECT r.message_id
          FROM __SCHEMA__.wh_outbox r
          WHERE r.stream_id = v_stream
            AND r.emission_position > v_emission
            AND r.instance_id = v_holder
            AND r.processed_at IS NULL
            AND r.published_at IS NULL
            AND r.coalesce_group IS NULL
          ORDER BY r.created_at, r.message_id
          FOR UPDATE OF r
        ) run
        WHERE o.message_id = run.message_id
          AND o.instance_id = v_holder
          AND o.processed_at IS NULL
          AND o.published_at IS NULL
          AND o.coalesce_group IS NULL;
      END IF;
    END IF;
  END LOOP;
END;
$$ LANGUAGE plpgsql;
COMMENT ON FUNCTION __SCHEMA__.process_outbox_failures(JSONB, TIMESTAMPTZ) IS
  '412: the run a retried row releases is locked in (created_at, message_id) order, whatever the plan reads. '
  'Processes outbox message failures: sets the Failed flag, records the error and reason, and '
  'schedules the retry with exponential backoff (capped at 5 minutes), releasing the lease for '
  'reclaim. A failing schedule occurrence also lands a wh_schedule_runs Failed row, and an '
  'at-most-once schedule (delivery_guarantee=1) is parked terminally instead of retried. Reads the '
  'element the runtime writes (MessageId, Reason) as well as the older name (FailureReason). 171: a '
  'retried row releases the rest of its run (later rows of its stream its holder still leases, attempt '
  'refunded), so the stream is retried in order. 172: the failures are applied in (stream_id, created_at, message_id) '
  'order, so the rows are locked in the order every waiting writer of wh_outbox uses.';

