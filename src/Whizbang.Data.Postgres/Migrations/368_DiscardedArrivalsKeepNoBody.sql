-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 368_DiscardedArrivalsKeepNoBody.sql
-- Date: 2026-11-08
-- Description: A message the consumer discards on receive is stored without its body.
--
--              Since 242 (#1720) a message the consumer skips on receive (no local consumer, directed at another
--              service, this service's own echo, an event of a type it did not know) is stored settled with the
--              reason, and it kept its full envelope and metadata. Nothing ever reads them: a settled row is never
--              claimed, copied into the event store, dispatched or dead-lettered, and a later delivery of the same id
--              is recognized as a duplicate by wh_message_deduplication, not by the inbox row. On a subscriber that
--              receives many types it does not consume, those bodies were most of the inbox (heap and TOAST) until
--              the completed-inbox purge removed them.
--
--              store_inbox_messages stores such a row with an empty envelope (event_data '{}') and empty metadata
--              ('{}'). It keeps what deduplication, diagnostics and the redelivery reconciliation need: the message
--              id, handler, message type, stream, scope, flags, priority, source service and commit sequence, the
--              timestamps, and the reason in wh_inbox_state.error. Both columns stay NOT NULL, so no schema change is
--              needed. The body is not even extracted from the batch for a discarded message, so the store copies
--              less on the hot path than before. Only a first arrival that is discarded is affected: a discarded
--              redelivery of a message already stored is a duplicate and leaves the stored row as it was, and a
--              repair child refused by 252 still keeps its body.
--
--              Existing rows are not rewritten: on a large inbox that would be a full-table update of the very rows
--              the completed-inbox purge removes anyway.
--
--              Number 368: 359 to 367 are taken on develop or by changes in flight.
--
-- Dependencies: 252 (store_inbox_messages, the last word before this file)
-- Objects: store_inbox_messages
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- store_inbox_messages: last word 252, with a message discarded on receive stored without its body. Signature unchanged.
-- <docs>resilience/stream-integrity#remembered-but-missing</docs>
-- <docs>messaging/transports/transport-consumer#batch-handler</docs>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStoreInboxMessagesTests.cs:SingleMessage_StoresInboxAndDedupAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStoreInboxMessagesTests.cs:DuplicateMessageId_SecondCallNoOpsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredDeadLetterReentrySqlTests.cs:ARecoveredMessage_IsStoredInTheSameWorkShapeAsADeliveryOfTheSameEnvelopeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InboxDiscardedOnReceiveSqlTests.cs:AMessageDiscardedOnReceive_IsStoredSettled_WithItsReason_AndNeverClaimedOrCopiedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredMissingEventRestoreSqlTests.cs:ARepairedEvent_DeduplicationRemembers_TheStoreLost_IsRestored_AndTheBucketHealsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredMissingEventRestoreSqlTests.cs:ARestoredEvent_IsStoredStateOnly_SoTriggerReceptorsThatRanOnItsFirstArrivalDoNotRunAgainAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredMissingEventRestoreSqlTests.cs:ADuplicateOfAStoredEvent_IsStillDropped_EvenAsRepairTrafficAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredMissingEventRestoreSqlTests.cs:OrdinaryTraffic_DeduplicationRemembers_TheStoreLost_IsDroppedAsBeforeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredMissingEventRestoreSqlTests.cs:ARepairedEvent_StillPendingInTheInbox_IsNotStoredTwiceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredMissingEventRestoreSqlTests.cs:ARepairedEphemeralEvent_IsNotRestoredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredMissingEventRestoreSqlTests.cs:ARepairedEvent_OnAStreamWhoseEarlierHistoryWasTruncated_IsRefused_AndReportedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InboxDiscardedOnReceiveKeepsNoBodySqlTests.cs:AMessageDiscardedOnReceive_IsStoredWithoutItsBody_AndIsStillADuplicateOnRedeliveryAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InboxDiscardedOnReceiveKeepsNoBodySqlTests.cs:AMessageDiscardedOnReceive_RedeliveredForProcessing_IsStillADuplicate_AndNotDispatchedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InboxDiscardedOnReceiveKeepsNoBodySqlTests.cs:AnOrdinaryArrival_KeepsItsBodyAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InboxDiscardedOnReceiveKeepsNoBodySqlTests.cs:ADiscardedRedelivery_OfAProcessedMessage_LeavesItsStoredBodyAloneAsync</tests>
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
  c_field_flags CONSTANT TEXT := __ENVELOPE_FIELD_FLAGS__;
  v_msg RECORD;
  v_partition INTEGER;
  v_observations INTEGER;  -- 1 on first sight; N on the Nth redelivery
  v_probed_streams UUID[] := ARRAY[]::UUID[];
  v_empty_inbox_streams UUID[] := ARRAY[]::UUID[];
  v_notify_inbox_streams UUID[] := ARRAY[]::UUID[];
  v_is_new BOOLEAN;           -- 236: a first arrival, or a recovery that claimed its dead letter
  v_claimed INTEGER;          -- 236: rows the recovery claim or the message insert touched
  v_settled BOOLEAN;          -- 242: the consumer skipped it on receive, so the row is stored already settled
  -- 252 (#1843): the flags of events integrity never counts, so their absence from the store is no gap.
  c_flags_never_repaired CONSTANT INTEGER := 8 | 32;  -- EventFlags.Ephemeral (1 << 3) | EventFlags.AtMostOnce (1 << 5)
  v_restore BOOLEAN;          -- 252: repair traffic deduplication remembers and the store lost, stored again as state only
  v_refusal TEXT;             -- 252: why such a child is refused instead, stored settled with the reason
BEGIN
  IF jsonb_array_length(p_messages) = 0 THEN RETURN; END IF;

  FOR v_msg IN
    SELECT
      (elem->>__ENVELOPE_FIELD_MESSAGE_ID__)::UUID as msg_id,
      elem->>'HandlerName' as handler_name,
      elem->>'EnvelopeType' as envelope_type,
      elem->>'MessageType' as message_type,
      -- 368: a message the consumer discarded on receive keeps no body. Nothing reads a settled row's envelope or
      -- metadata (it is never claimed, copied, dispatched or dead-lettered, and deduplication is answered by
      -- wh_message_deduplication), so both are left unread here and stored empty by the COALESCE in the insert.
      CASE WHEN NULLIF(elem->>'DiscardedOnReceive', '') IS NULL THEN elem->'Envelope' END as envelope_data,
      CASE WHEN NULLIF(elem->>'DiscardedOnReceive', '') IS NULL THEN elem->'Metadata' END as metadata,
      elem->'Scope' as scope,
      (elem->>__ENVELOPE_FIELD_STREAM_ID__)::UUID as stream_id,
      (elem->>'IsEvent')::BOOLEAN as is_event,
      -- 149: the effective priority the consumer classified; zero (undeclared) and an absent field read as standard.
      COALESCE(NULLIF((elem->>'Priority')::INTEGER, 0), 150) as priority,
      -- EventFlags (062): same read as store_outbox_messages so collective events delivered
      -- cross-service via the inbox also route to the __collective__ sink. 232 (#1612): every bit of a name list.
      CASE
        WHEN elem->>c_field_flags IS NULL OR elem->>c_field_flags = '' THEN 0
        WHEN elem->>c_field_flags ~ '^[0-9]+$' THEN (elem->>c_field_flags)::INTEGER
        ELSE __SCHEMA__._wh_event_flags_from_names(elem->>c_field_flags)
      END as flags,
      -- 146 (#727): a zero GUID is "unknown", not a producer; COALESCE below then falls back to this service.
      NULLIF((elem->>'SourceServiceId')::UUID, __EMPTY_UUID__::UUID) as source_service_id,
      (elem->>'SourceCommitSequence')::BIGINT as source_commit_sequence,
      -- 236 (#1664): set only by dead-letter recovery, naming the dead-letter row the message re-enters from.
      (elem->>'RecoveredFromDeadLetterId')::UUID as recovered_from,
      -- 242 (#1720): set by a consumer that skipped the message on receive, naming why.
      NULLIF(elem->>'DiscardedOnReceive', '') as discarded_on_receive,
      -- 252 (#1843): set on the children of a redelivery that repair or an operator asked for.
      COALESCE((elem->>'RestoreIfMissing')::BOOLEAN, false) as restore_if_missing
    FROM jsonb_array_elements(p_messages) as elem
    ORDER BY (elem->>__ENVELOPE_FIELD_STREAM_ID__)::UUID NULLS FIRST, (elem->>__ENVELOPE_FIELD_MESSAGE_ID__)::UUID
  LOOP
    -- 236 (#1664): dead-letter recovery re-enters a message HERE, as one more arrival, so every arrival rule
    -- applies to it unchanged. Three things differ, and only for a row that names its dead letter:
    --   1. it claims that dead letter first (Recovering), in this transaction, so two recoveries of one row
    --      cannot both re-enter it, and a dead letter whose message is still in the inbox is left alone;
    --   2. a recovered id is usually already in wh_message_deduplication, so the observation is still counted
    --      below (poison bounding keeps working) but the message is not dropped as a duplicate;
    --   3. the work-state row carries the dead-letter id, so the completion settles the dead letter
    --      (process_inbox_completions) instead of recovery declaring it Recovered before anything ran.
    -- The attempts budget is refunded because the row is new: attempts starts at 0 as for every arrival.
    IF v_msg.recovered_from IS NOT NULL THEN
      UPDATE __SCHEMA__.wh_dead_letters dl
      SET recovery_status = 1,                  -- Recovering, until the inbox completes the message
          recovery_attempts = dl.recovery_attempts + 1,
          last_recovery_at = p_now
      WHERE dl.dead_letter_id = v_msg.recovered_from
        AND dl.source_id = v_msg.msg_id
        AND dl.recovery_status NOT IN (1, 2, 3, 4)  -- not already Recovering, HoldForReview, Recovered, PermanentlyFailed
        AND dl.recovered_at IS NULL
        AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_inbox i WHERE i.message_id = v_msg.msg_id);
      GET DIAGNOSTICS v_claimed = ROW_COUNT;
      IF v_claimed = 0 THEN
        CONTINUE;  -- claimed by another recovery, already settled, or the message is still in the inbox
      END IF;
    END IF;

    -- 121: DO UPDATE (was DO NOTHING) so a redelivery is COUNTED rather than silently swallowed.
    -- RETURNING gives the post-write count on both arms, so newness is read from the value
    -- (= 1) instead of from ROW_COUNT, which DO UPDATE would report as 1 either way.
    INSERT INTO __SCHEMA__.wh_message_deduplication AS dedup
      (message_id, first_seen_at, observation_count)
    VALUES (v_msg.msg_id, p_now, 1)
    ON CONFLICT ON CONSTRAINT wh_message_deduplication_pkey DO UPDATE
      SET observation_count = dedup.observation_count + 1
    RETURNING dedup.observation_count INTO v_observations;

    -- 252 (#1843): for repair traffic deduplication is checked against the event store, not trusted alone. The
    -- deduplication row is written on receipt and the event-store row can be lost afterwards (a failed fan-out in an
    -- older version, manual cleanup, a store restored from an older backup while deduplication survived), and then
    -- every redelivery repair asked for was dropped here, so the bucket never healed and repair asked forever. A
    -- repair child whose id was seen, that is an event integrity counts, and that neither the event store nor the
    -- inbox holds, is stored again. Its envelope is marked state only: deduplication remembers the message, so the
    -- reactions to it had their chance, and repair restores state, never a second reaction. One case is refused: a
    -- stream whose lowest stored version is above 1 had its earlier history truncated here (wh_truncate_stream_prefix
    -- keeps a stream's deduplication rows precisely so a redelivered copy is not stored below the truncation point),
    -- and a lost event cannot be told from a truncated one. The refused child is stored settled with the reason,
    -- which the redelivery's reconciliation reads (wh_reconcile_redelivered_bundle) to report the bucket once as one
    -- repair cannot change. A child the store or the inbox still holds is a duplicate, as before.
    v_restore := FALSE;
    v_refusal := NULL;
    IF v_observations > 1
       AND v_msg.restore_if_missing
       AND v_msg.recovered_from IS NULL
       AND v_msg.discarded_on_receive IS NULL
       AND COALESCE(v_msg.is_event, false)
       AND v_msg.stream_id IS NOT NULL
       AND COALESCE(v_msg.flags, 0) & c_flags_never_repaired = 0
       AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = v_msg.msg_id)
       AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_inbox i WHERE i.message_id = v_msg.msg_id) THEN
      IF (SELECT MIN(es.version) FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = v_msg.stream_id) > 1 THEN
        v_refusal := __REPAIR_REFUSED_TRUNCATED__;
      ELSE
        v_restore := TRUE;
      END IF;
    END IF;

    -- 236: a recovery that claimed its dead letter stores the message whatever the count says.
    -- 252: and so does a repair child restored or refused above.
    v_is_new := v_observations = 1 OR v_msg.recovered_from IS NOT NULL OR v_restore OR v_refusal IS NOT NULL;
    -- 242 (#1720): a message the consumer skipped on receive (no local consumer, directed at another service, this
    -- service's own echo, an event of an unknown type) is stored like every arrival, so the skip is durable and
    -- inspectable, but already settled: processed, Stored and Published, the reason in its error column. Nothing
    -- claims, copies or dispatches a processed row, so it rings no doorbell, pins no stream and is not new work.
    -- 252: a refused repair child is stored settled the same way.
    v_settled := v_msg.discarded_on_receive IS NOT NULL OR v_refusal IS NOT NULL;
    IF v_is_new THEN
      IF v_msg.stream_id IS NOT NULL THEN
        v_partition := __SCHEMA__.compute_partition(v_msg.stream_id, p_partition_count);
      ELSE
        v_partition := NULL;
      END IF;

      -- 114: emptiness probe, see store_outbox_messages; inbox pending = not processed
      -- and schedule-eligible (the drain-fetch predicate minus the lease dimension).
      IF NOT v_settled AND v_msg.stream_id IS NOT NULL AND NOT (v_msg.stream_id = ANY(v_probed_streams)) THEN
        v_probed_streams := array_append(v_probed_streams, v_msg.stream_id);

        -- 162: all three columns are work state now, so the probe reads the narrow table.
        --
        -- The ORDER BY is the remedy from 160 and 161, applied here BEFORE it bites rather than
        -- after. This is the third instance of the same shape: an emptiness probe by stream id with
        -- processed_at IS NULL under a bare LIMIT 1. The planner prices a sequential scan as though
        -- the first row it reads will match, because that predicate is true of nearly every row in
        -- a table carrying a backlog, and on a stream whose work is all done there is no match at
        -- all, so it reads the whole table. Ordering by the index key removes that plan instead of
        -- out-costing it, and stream_id is pinned by the equality so it changes no result. received_at
        -- is in the clause because ordering by stream_id alone is dropped as redundant.
        PERFORM 1 FROM __SCHEMA__.wh_inbox_state ist
          WHERE ist.stream_id = v_msg.stream_id
            AND ist.processed_at IS NULL
            AND (ist.scheduled_for IS NULL OR ist.scheduled_for <= p_now)
          ORDER BY ist.stream_id, ist.received_at
          LIMIT 1
          FOR SHARE OF ist SKIP LOCKED;   -- 140: a locked pending row is being completed; treat as empty and ring
        IF NOT FOUND THEN
          v_empty_inbox_streams := array_append(v_empty_inbox_streams, v_msg.stream_id);
        END IF;
      END IF;

      -- 162: the message row and its claim state are two inserts now. Both carry a DO NOTHING
    -- conflict clause, so the pair is idempotent under the redelivery this function exists to
    -- absorb: a duplicate leaves the existing message AND its existing claim state untouched,
    -- rather than resetting a lease some instance is holding.
    INSERT INTO __SCHEMA__.wh_inbox (
      message_id,
      handler_name,
      message_type,
      event_data,
      metadata,
      scope,
      stream_id,
      is_event,
      flags,
      received_at,
      source_service_id,
      source_commit_sequence,
      priority
    ) VALUES (
      v_msg.msg_id,
      v_msg.handler_name,
      v_msg.message_type,
      -- 252: a restored repair child is state only ('sto', the envelope's StateOnly on the wire).
      CASE WHEN v_restore
        THEN jsonb_set(COALESCE(v_msg.envelope_data, '{}'::jsonb), ARRAY['sto'], 'true'::jsonb)
        ELSE COALESCE(v_msg.envelope_data, '{}'::jsonb)
      END,
      COALESCE(v_msg.metadata, '{}'::jsonb),
      COALESCE(v_msg.scope, 'null'::jsonb),
      v_msg.stream_id,
      COALESCE(v_msg.is_event, false),
      COALESCE(v_msg.flags, 0),
      p_now,
      COALESCE(v_msg.source_service_id, (SELECT service_id FROM __SCHEMA__.wh_service_config LIMIT 1)),
      COALESCE(v_msg.source_commit_sequence, 0),
      v_msg.priority
    )
    ON CONFLICT ON CONSTRAINT wh_inbox_pkey DO NOTHING;
    -- 236: a recovery that lost the race to a concurrent arrival of the same id hands its dead letter back,
    -- so the dead letter is never left Recovering behind a row that does not name it.
    GET DIAGNOSTICS v_claimed = ROW_COUNT;
    IF v_claimed = 0 AND v_msg.recovered_from IS NOT NULL THEN
      UPDATE __SCHEMA__.wh_dead_letters dl
      SET recovery_status = 0
      WHERE dl.dead_letter_id = v_msg.recovered_from AND dl.recovery_status = 1;
      CONTINUE;
    END IF;
    -- 252: a restore that lost the race to a concurrent arrival of the same id leaves that arrival as it is.
    IF v_claimed = 0 AND v_restore THEN
      CONTINUE;
    END IF;

    -- The lease row, unleased and unattempted. A message with no lease row can never be claimed and
    -- would fail silently, so this insert is not optional bookkeeping: it is what makes the message
    -- visible to the claim at all.
    INSERT INTO __SCHEMA__.wh_inbox_state (
      message_id, stream_id, received_at, partition_number, priority, is_event,
      status, processed_at, instance_id, lease_expiry, attempts, recovered_from_dead_letter_id, error
    ) VALUES (
      v_msg.msg_id,
      v_msg.stream_id,
      p_now,
      v_partition,
      v_msg.priority,
      COALESCE(v_msg.is_event, false),
      CASE WHEN v_settled THEN 1 | 4 ELSE 1 END,  -- Stored flag; 242: and Published, for a row settled on receive
      CASE WHEN v_settled THEN p_now END,          -- 242: a settled row is processed as it arrives
      NULL,  -- No lease, immediately claimable by WorkCoordinatorPublisherWorker
      NULL,
      0,     -- Initial attempts
      v_msg.recovered_from,  -- 236: NULL for every arrival but a recovery
      -- 242: the reason a settled row records, on the column a processing failure would use; NULL otherwise.
      -- 252: a refused repair child records why it was refused.
      COALESCE(v_refusal, 'discarded on receive: ' || v_msg.discarded_on_receive)
    )
    ON CONFLICT (message_id) DO NOTHING;

    IF v_settled THEN
      CONTINUE;  -- 242: no doorbell, no pin, not new work
    END IF;

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
    END IF;  -- end of the single-observation branch (236: or a claimed recovery)
  END LOOP;

  IF cardinality(v_notify_inbox_streams) > 0 THEN
    PERFORM __SCHEMA__.notify_instance_owners(__CATEGORY_INBOX__, v_notify_inbox_streams);
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.store_inbox_messages(JSONB, UUID, TIMESTAMPTZ, TIMESTAMPTZ, INTEGER) IS
  'Stores received inbox messages with deduplication (121: every arrival counted), priority (149), the work-state row (162). 236 (#1664): a message recovered from a dead letter (RecoveredFromDeadLetterId) re-enters here as one more arrival: it claims its dead letter, is stored even though its id was seen, and its work-state row names the dead letter for the completion to settle. 242 (#1720): a message the consumer skipped on receive (DiscardedOnReceive) is stored settled: processed, Stored and Published, the reason in error, never claimed, copied or returned as new work. 368: and without its body (event_data and metadata stored empty), since nothing reads a settled row''s body and deduplication does not need it. 252 (#1843): a repair child (RestoreIfMissing) whose id was seen but which neither the event store nor the inbox holds is stored again as state only, or stored settled as refused when its stream had earlier history truncated.';
