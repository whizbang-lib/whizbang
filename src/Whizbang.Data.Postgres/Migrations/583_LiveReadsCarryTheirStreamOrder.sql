-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 583_LiveReadsCarryTheirStreamOrder.sql
-- Date: 2026-11-24
-- Description: The live reads rank a stream in its stream order and hand the rank to the worker on each row, never by
--              id (#2551).
--
--              An id is time-ordered and never a stream position. fetch_inbox_batch ranked a stream's received rows by
--              message id, and the workers re-sorted every batch by id in C#. A stream's received events are stored in
--              receive order (581), so their stored version is their position; a command has no version, and its
--              position is its arrival, which nothing recorded: every row of one store_inbox_messages call shares one
--              received_at, and the call inserts in stream-and-id lock order.
--
--                arrival_position   wh_inbox.arrival_position, from the sequence wh_inbox_arrival_seq. store_inbox_
--                                   messages takes one value per element in the order it is handed the delivery
--                                   (p_messages, WITH ORDINALITY) and stores each element's value, so the lock order of
--                                   its inserts decides nothing. The column defaults to the sequence, so any other
--                                   writer still records an arrival. The pending rows already queued are given theirs
--                                   once, here, in the order they were dispatched in until now (received_at, then id),
--                                   so a backlog drains as it would have.
--                fetch_inbox_batch  ranks a stream by kind: its events by stored version, its commands by arrival,
--                                   each kind dealt into the arrival slots it holds, and returns the rank as
--                                   stream_position. The byte budget's running total and the output follow it.
--                get_stream_events  returns each event's version as out_version, so the perspective drain applies a
--                                   stream by the version each event carries.
--
--              Cost: the store adds one sequence value per element and one column per row (no index). The fetch adds a
--              primary-key probe of wh_event_store per leased row and three window passes over the rows it already
--              reads. The stream read returns a column it already reads. EXPLAIN in the commit report.
--
--              Number 583: reserved for this change (580 to 584).
--
-- Reversal: reversible (the down file restores the three functions' previous last words, drops the column default and
--           the column, and the sequence)
-- Dependencies: 567 (store_inbox_messages), 230 (fetch_inbox_batch), 582 (get_stream_events), 581 (receive order)
-- Objects: wh_inbox.arrival_position, wh_inbox_arrival_seq, store_inbox_messages, fetch_inbox_batch, get_stream_events
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

CREATE SEQUENCE IF NOT EXISTS __SCHEMA__.wh_inbox_arrival_seq;

ALTER TABLE __SCHEMA__.wh_inbox ADD COLUMN IF NOT EXISTS arrival_position BIGINT;

-- The rows already queued take their arrival once, in the order they have been dispatched in until now. A row already
-- processed (kept by debug mode) is never dispatched again and keeps none.
DO $$
DECLARE
  v_count BIGINT;
  v_base BIGINT;
BEGIN
  SELECT count(*) INTO v_count
  FROM __SCHEMA__.wh_inbox i
  JOIN __SCHEMA__.wh_inbox_state ist ON ist.message_id = i.message_id
  WHERE i.arrival_position IS NULL AND ist.processed_at IS NULL;
  IF v_count > 0 THEN
    v_base := nextval('__SCHEMA__.wh_inbox_arrival_seq');
    PERFORM setval('__SCHEMA__.wh_inbox_arrival_seq', v_base + v_count);
    UPDATE __SCHEMA__.wh_inbox i
    SET arrival_position = v_base + q.rn
    FROM (
      SELECT ist.message_id, row_number() OVER (ORDER BY ist.received_at, ist.message_id) AS rn
      FROM __SCHEMA__.wh_inbox_state ist
      JOIN __SCHEMA__.wh_inbox i2 ON i2.message_id = ist.message_id
      WHERE i2.arrival_position IS NULL AND ist.processed_at IS NULL
    ) q
    WHERE i.message_id = q.message_id;
  END IF;
END;
$$;

ALTER TABLE __SCHEMA__.wh_inbox ALTER COLUMN arrival_position SET DEFAULT nextval('__SCHEMA__.wh_inbox_arrival_seq');

COMMENT ON COLUMN __SCHEMA__.wh_inbox.arrival_position IS
'583 (#2551): the message''s arrival, its position in the order store_inbox_messages was handed its delivery. A command''s stream position (fetch_inbox_batch); for an event, the arrival slot its stored version is dealt into.';

-- ---------------------------------------------------------------------------------------------
-- store_inbox_messages: copied from its last word (567), recording each message's arrival (583).
-- ---------------------------------------------------------------------------------------------
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
-- <docs>resilience/stream-integrity#cuts-carry-their-digest</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutsCarryTheirDigestSqlTests.cs:Purge_KeepsTheBucketCarried_AndARedeliveryOfThePurgedStreamIsRefusedAsync</tests>
-- <docs>fundamentals/work-coordinator/partition-assignment#ownership-by-map</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipByMapStoreSqlTests.cs:StoreInbox_InMapMode_RecordsTheStreamWithoutAnOwnerAsync</tests>
-- <docs>fundamentals/event-store/version-ordering#dispatch-order</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FetchInboxBatchStreamOrderSqlTests.cs:Commands_AreFetchedInTheOrderTheyArrived_NotByIdAsync</tests>
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
  c_flag_superseding CONSTANT INTEGER := 128;  -- 295: EventFlags.Superseding (1 << 7)
  c_flags_never_repaired CONSTANT INTEGER := 8 | 32;  -- EventFlags.Ephemeral (1 << 3) | EventFlags.AtMostOnce (1 << 5)
  v_restore BOOLEAN;          -- 252: repair traffic deduplication remembers and the store lost, stored again as state only
  v_refusal TEXT;             -- 252: why such a child is refused instead, stored settled with the reason
  -- 566 (#2554, #2555): the service's switch, read once: on, the partition ownership table alone decides.
  v_map BOOLEAN := __SCHEMA__.wh_partition_ownership_by_map();
  -- 583 (#2551): one arrival per element, in the order the delivery lists them.
  v_arrivals BIGINT[];
BEGIN
  IF jsonb_array_length(p_messages) = 0 THEN RETURN; END IF;

  -- 583 (#2551): the delivery's arrivals, taken in its order before the inserts, which run in lock order.
  v_arrivals := ARRAY(
    SELECT nextval('__SCHEMA__.wh_inbox_arrival_seq') FROM generate_series(1, jsonb_array_length(p_messages)) AS g(n) ORDER BY g.n);

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
      COALESCE((elem->>'RestoreIfMissing')::BOOLEAN, false) as restore_if_missing,
      -- 583 (#2551): the element's place in the delivery.
      listed.position as arrival_ordinal
    FROM jsonb_array_elements(p_messages) WITH ORDINALITY AS listed(elem, position)
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
    -- 454 (#2350): a copy of a superseding key that a newer copy already held here supersedes is never stored: stored,
    -- it would sit above the newest by version and be applied after it. It is settled as satisfied, the redelivery's
    -- reconciliation reads it as present, and nothing claims, copies or applies it. This holds for a copy arriving for
    -- the first time, by any path (a live receive out of the origin's order, an integrity or an operator redelivery),
    -- and for a repair child deduplication remembers and the store lost. Newer is the origin's order
    -- (_wh_supersedes); without the origin's sequence on both copies nothing is refused, as before.
    -- 460 (#2363): a purged stream stays purged. Its digest is carried and its purge marker is its tombstone, so a
    -- redelivered event of it (repair or operator traffic) is refused, settled with the reason, whether deduplication
    -- remembers it or not (the purge removed its deduplication rows).
    IF v_msg.restore_if_missing
       AND v_msg.recovered_from IS NULL
       AND v_msg.discarded_on_receive IS NULL
       AND COALESCE(v_msg.is_event, false)
       AND v_msg.stream_id IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = v_msg.msg_id)
       AND EXISTS (
         SELECT 1 FROM __SCHEMA__.wh_stream_purge_markers pm
         WHERE pm.stream_id = v_msg.stream_id AND pm.perspective_name = '*') THEN
      v_refusal := __REPAIR_REFUSED_PURGED__;
    ELSIF v_msg.recovered_from IS NULL
       AND v_msg.discarded_on_receive IS NULL
       AND COALESCE(v_msg.is_event, false)
       AND v_msg.stream_id IS NOT NULL
       AND COALESCE(v_msg.flags, 0) & c_flag_superseding <> 0
       AND v_msg.metadata ->> 'sk' IS NOT NULL
       AND (v_observations = 1
            OR (v_msg.restore_if_missing
                AND COALESCE(v_msg.flags, 0) & c_flags_never_repaired = 0
                AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = v_msg.msg_id)
                AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_inbox i WHERE i.message_id = v_msg.msg_id)))
       AND EXISTS (
         SELECT 1 FROM __SCHEMA__.wh_event_store n
         WHERE n.stream_id = v_msg.stream_id
           AND n.supersede_key = v_msg.metadata ->> 'sk'
           AND COALESCE(n.supersede_group, n.event_type)
               = COALESCE(v_msg.metadata ->> 'sg', __SCHEMA__.normalize_event_type(v_msg.message_type))
           AND __SCHEMA__._wh_supersedes(n.origin_service_id, n.origin_commit_sequence, n.version,
                                        v_msg.source_service_id, NULLIF(v_msg.source_commit_sequence, 0), 2147483647)) THEN
      v_refusal := __REPAIR_SATISFIED_SUPERSEDED__;
    ELSIF v_observations > 1
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
      priority,
      arrival_position
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
      v_msg.priority,
      v_arrivals[v_msg.arrival_ordinal]
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
          -- 567 (#2555): in map mode the first writer is not the stream's owner; the partition's owner is named when
          -- it claims.
          (v_msg.stream_id, COALESCE(v_partition, 0), CASE WHEN v_map THEN NULL ELSE p_instance_id END, p_now)
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
  'Stores received inbox messages with deduplication (121: every arrival counted), priority (149), the work-state row (162). 236 (#1664): a message recovered from a dead letter (RecoveredFromDeadLetterId) re-enters here as one more arrival: it claims its dead letter, is stored even though its id was seen, and its work-state row names the dead letter for the completion to settle. 242 (#1720): a message the consumer skipped on receive (DiscardedOnReceive) is stored settled: processed, Stored and Published, the reason in error, never claimed, copied or returned as new work. 368: and without its body (event_data and metadata stored empty), since nothing reads a settled row''s body and deduplication does not need it. 252 (#1843): a repair child (RestoreIfMissing) whose id was seen but which neither the event store nor the inbox holds is stored again as state only, or stored settled as refused when its stream had earlier history truncated. 460 (#2363): refuses a redelivered event of a purged stream, recording why in wh_inbox_state.error. 567 (#2555): with the service''s switch on, no stored owner is pinned or leased to; leased by partition (wh_i_own). 583 (#2551): records each message''s arrival (wh_inbox.arrival_position) in the order the delivery lists it.';

-- ---------------------------------------------------------------------------------------------
-- fetch_inbox_batch: copied from its last word (230), ranked in stream order and returning the rank (583).
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('fetch_inbox_batch');

-- <docs>fundamentals/work-coordinator/claim-loop</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceivedStreamOrderSqlTests.cs:AWaitingEvent_IsNotFetched_UntilItIsStoredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FetchInboxBatchSqlTests.cs:FetchInboxBatch_ReturnsRowsForOwnedStreams_InReceivedAtOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FetchInboxBatchSqlTests.cs:FetchInboxBatch_FiltersOutOtherInstancesRowsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FetchInboxBatchSqlTests.cs:FetchInboxBatch_FiltersRowsWithProcessedAtSet_DebugModeRetainedAsync</tests>
-- <docs>fundamentals/event-store/version-ordering#dispatch-order</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FetchInboxBatchStreamOrderSqlTests.cs:ReceivedEvents_AreFetchedInTheirStoredOrder_WithTheirStreamPositionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FetchInboxBatchStreamOrderSqlTests.cs:Commands_AreFetchedInTheOrderTheyArrived_NotByIdAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FetchInboxBatchStreamOrderSqlTests.cs:AStreamsEventsAndCommands_EachKindKeepsItsOrder_InTheArrivalSlotsItHoldsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/StreamOrderNeverByIdGuardTests.cs:NoStoreReceiveOrReplayFunction_OrdersAStreamById_InItsLatestDefinitionAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.fetch_inbox_batch(
  p_stream_ids UUID[],
  p_instance_id UUID,
  p_max_per_stream INTEGER DEFAULT 100,
  p_max_bytes BIGINT DEFAULT NULL
) RETURNS TABLE(
  message_id UUID,
  stream_id UUID,
  handler_name VARCHAR(200),
  message_type VARCHAR(500),
  event_data TEXT,
  metadata JSONB,
  scope JSONB,
  status INTEGER,
  attempts INTEGER,
  partition_number INTEGER,
  is_event BOOLEAN,
  error TEXT,
  priority INTEGER,
  stream_position BIGINT
) AS $$
BEGIN
  IF p_stream_ids IS NULL OR array_length(p_stream_ids, 1) IS NULL THEN
    RETURN;
  END IF;

  -- 583 (#2551): a stream's rows are ranked in its stream order, never by id: its events by stored version (the receive
  -- order 581 stored them in), its commands by arrival, each kind dealt into the arrival slots it holds. The rank is
  -- returned as stream_position, and the drain dispatches by it.
  --
  -- 162: this function needs both tables, because it returns the message and selects on its claim
  -- state. The LEASE table drives the join: every predicate below is on it, so the narrow table is
  -- scanned and the wide row is fetched only for the rows that survive. The previous form scanned
  -- the wide row to apply the same predicate. i.* is enumerated rather than starred because the
  -- moved columns are no longer on wh_inbox and a star would silently change the result shape.
  RETURN QUERY
  WITH candidates AS (
    SELECT
      i.message_id,
      ist.stream_id,
      i.handler_name,
      i.message_type,
      i.event_data,
      i.metadata,
      i.scope,
      ist.status,
      ist.attempts,
      ist.partition_number,
      ist.is_event,
      ist.error,
      i.priority,
      i.arrival_position,
      -- An event's stored version: one primary-key probe per leased row. A command has none.
      (SELECT es.version FROM __SCHEMA__.wh_event_store es WHERE es.event_id = i.message_id) AS stored_version
    FROM __SCHEMA__.wh_inbox_state ist
    JOIN __SCHEMA__.wh_inbox i ON i.message_id = ist.message_id
    -- v0.658 slice 7: mirror of fetch_outbox_batch's Empty/NULL stream handling -- see the matching
    -- comment in the outbox query for the full rationale.
    WHERE (
        ist.stream_id = ANY(p_stream_ids)
        OR ((ist.stream_id IS NULL OR ist.stream_id = __EMPTY_UUID__::uuid)
            AND ist.message_id = ANY(p_stream_ids))
      )
      AND ist.instance_id = p_instance_id
      AND ist.lease_expiry > NOW()
      AND ist.processed_at IS NULL  -- inbox uses processed_at as both production-marker and debug-kept-marker
      -- 230 (#1607): an event is dispatched once it is stored. The emit chain stores a leased event in the claim
      -- that leased it, unless the event waits behind its stream's head; such a row stays leased and unstored,
      -- and completing it would delete it before it ever reached the event store.
      AND (ist.chain_emitted_at IS NOT NULL OR ist.is_event = false OR ist.stream_id IS NULL)
      AND (ist.scheduled_for IS NULL OR ist.scheduled_for <= NOW())
  ),
  -- Each kind's rank in its own order, and the arrival slots each kind holds.
  kinds AS (
    SELECT c.*,
           (c.stored_version IS NOT NULL) AS versioned,
           row_number() OVER (PARTITION BY c.stream_id ORDER BY c.arrival_position) AS slot,
           row_number() OVER (PARTITION BY c.stream_id, (c.stored_version IS NOT NULL)
                              ORDER BY c.stored_version, c.arrival_position) AS kind_rank
    FROM candidates c
  ),
  -- The row of rank n in its kind takes its kind's n-th slot. Both sides are numbered over the batch in (stream, kind,
  -- rank) order, so they meet on one key.
  by_kind AS (
    SELECT k.*, row_number() OVER (ORDER BY k.stream_id, k.versioned, k.kind_rank) AS pair FROM kinds k
  ),
  slots AS (
    SELECT k.slot, row_number() OVER (ORDER BY k.stream_id, k.versioned, k.slot) AS pair FROM kinds k
  ),
  ranked AS (
    SELECT r.*, s.slot AS rank_in_stream
    FROM by_kind r
    JOIN slots s ON s.pair = r.pair
  ),
  -- Running byte total in the SAME order the rows are returned, so the cut is a suffix of the
  -- slice and stream-FIFO is preserved. Measured on the payload columns because those are what
  -- cross the wire and land on the heap; the fixed-width columns are noise by comparison.
  budgeted AS (
    SELECT
      r.*,
      SUM(COALESCE(octet_length(r.event_data::TEXT), 0)
          + COALESCE(octet_length(r.metadata::TEXT), 0))
        OVER (PARTITION BY r.stream_id ORDER BY r.rank_in_stream
              ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS running_bytes
    FROM ranked r
    WHERE r.rank_in_stream <= p_max_per_stream
  )
  SELECT
    b.message_id,
    b.stream_id,
    b.handler_name::VARCHAR(200),
    b.message_type::VARCHAR(500),
    b.event_data::TEXT,
    b.metadata,
    b.scope,
    b.status,
    b.attempts,
    b.partition_number,
    b.is_event,
    b.error,
    b.priority,
    b.rank_in_stream
  FROM budgeted b
  WHERE p_max_bytes IS NULL
     OR b.rank_in_stream = 1              -- never starve a stream on an oversized head message
     OR b.running_bytes <= p_max_bytes
  ORDER BY b.stream_id, b.rank_in_stream;
END;
$$ LANGUAGE plpgsql;

-- ---------------------------------------------------------------------------------------------
-- get_stream_events: copied from its last word (582), returning each event's version (583).
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('get_stream_events');

-- <docs>fundamentals/perspectives/perspectives-with-actions#no-row-contract</docs>
-- <docs>fundamentals/work-coordinator/commit-sequence</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FirstEventInStreamReadTests.cs:GetStreamEvents_ReturnsWhetherEachEventIsTheFirstInItsStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/GetStreamEventsCommitSequencePropagationSqlTests.cs:GetStreamEvents_StampedReturnedUnstampedExcludedOnSameStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveFailureCounterSqlTests.cs:GetStreamEvents_SurfacesFailures_ForTheDeadLetterDecisionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/GetStreamEventsCrossPodOwnershipTests.cs:LiveOwnersExpiredLeaseRow_NotStolenByAnotherInstance</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveWorkQueueBacklogCostSqlTests.cs:GetStreamEvents_ReadsWhatItsBatchHolds_NotTheBacklogAsync</tests>
-- <docs>fundamentals/work-coordinator/partition-assignment#ownership-by-map</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipByMapClaimSqlTests.cs:Fetch_TheNewOwnerFetchesTheStream_AndTheStoredOwnerNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/ClaimPathsDecideThroughTheOwnershipTableGuardTests.cs:EveryClaimFunction_DecidesThroughWhIOwn_AndRanksOnlyInTheLegacyBranchAsync</tests>
-- <docs>fundamentals/event-store/version-ordering#reads</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FetchInboxBatchStreamOrderSqlTests.cs:GetStreamEvents_CarriesEachEventsVersion_InStreamOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/StreamOrderNeverByIdGuardTests.cs:NoStoreReceiveOrReplayFunction_OrdersAStreamById_InItsLatestDefinitionAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.get_stream_events(
  p_instance_id UUID,
  p_stream_ids UUID[],
  p_now TIMESTAMPTZ DEFAULT NOW(),
  p_lease_seconds INTEGER DEFAULT 300
) RETURNS TABLE(
  out_stream_id UUID,
  out_event_id UUID,
  out_event_type TEXT,
  out_event_data TEXT,
  out_metadata TEXT,
  out_scope TEXT,
  out_event_work_id UUID,
  out_perspective_name VARCHAR(200),
  out_commit_sequence BIGINT,
  out_attempts INTEGER,
  out_failures INTEGER,
  out_first_in_stream BOOLEAN,
  out_version BIGINT
) AS $$
DECLARE
  v_lease_expiry TIMESTAMPTZ;
  v_stamp_grace CONSTANT INTERVAL := INTERVAL '5 seconds';
  v_stamp_cutoff TIMESTAMPTZ;
  -- 566 (#2554, #2555): the service's switch, read once: on, the partition ownership table alone decides.
  v_map BOOLEAN := __SCHEMA__.wh_partition_ownership_by_map();
BEGIN
  v_lease_expiry := p_now + (p_lease_seconds || ' seconds')::INTERVAL;
  v_stamp_cutoff := p_now - v_stamp_grace;

  -- Atomic claim+fetch (slice 25) + grace-windowed unstamped gate (mig 058) + live-owner gate
  -- (mig 059). A row is claimable only if its stream is NOT owned by a DIFFERENT live instance:
  -- enforcing single-writer-per-stream so two pods never apply one stream concurrently.
  -- 536: the event and the ledger row are each one probe per work row (LATERAL ... LIMIT 1 cannot be flattened into a
  -- join, so no plan can scan either table whole against the batch).
  WITH
  -- 566 (#2555): in map mode, the partitions this instance owns, read once per call through wh_partitions_i_own (the set
  -- form of wh_i_own: the same rule over the same table) and tested by hash membership, as the rank was tested by
  -- arithmetic. A per-row probe over the window would cost one index probe per row the window reads. Empty when off.
  mine_partitions AS MATERIALIZED (
    SELECT mp.partition_number
    FROM __SCHEMA__.wh_partitions_i_own(p_instance_id) AS mp(partition_number)
    WHERE v_map
  ),
  eligible AS (
    SELECT pe.event_work_id, pe.instance_id, pe.attempts
    FROM __SCHEMA__.wh_perspective_events pe
    CROSS JOIN LATERAL (
      SELECT es.commit_sequence
      FROM __SCHEMA__.wh_event_store es
      WHERE es.event_id = pe.event_id
        AND es.stream_id = pe.stream_id
      LIMIT 1
    ) es
    LEFT JOIN LATERAL (
      -- mig 059: single-writer gate. Do not claim a row whose stream is owned by a different LIVE instance. 317
      -- (#2027): the owner counts while the one liveness rule calls it live. Caller-owned / unowned / dead-owner
      -- streams stay claimable.
      SELECT 1 AS held
      FROM __SCHEMA__.wh_active_streams ast
      WHERE ast.stream_id = pe.stream_id
        AND ast.assigned_instance_id IS NOT NULL
        AND ast.assigned_instance_id <> p_instance_id
        AND ast.assigned_instance_id IN (SELECT l.instance_id FROM __SCHEMA__.wh_live_instance_ids() l)
        -- 566: the stored owner decides nothing in map mode, so the ledger is not read.
        AND NOT v_map
      LIMIT 1
    ) owner ON TRUE
    WHERE pe.stream_id = ANY(p_stream_ids)
      AND pe.processed_at IS NULL
      AND (pe.scheduled_for IS NULL OR pe.scheduled_for <= p_now)
      -- 536: the lease test is a filter on the batch's rows, never an index condition (IS TRUE keeps it out of the
      -- lease and per-instance indexes): as a range over a lease index it read every row the instance holds, or every
      -- lapsed row, whichever the statistics guessed was the smaller.
      AND (
        pe.instance_id IS NULL
        OR pe.lease_expiry < p_now
      ) IS TRUE
      AND (es.commit_sequence IS NOT NULL OR pe.created_at <= v_stamp_cutoff)
      -- 566 (#2555): in map mode a row is fetched exactly when this instance owns its partition (the set form of
      -- wh_i_own); the live-owner gate is the legacy mode's.
      AND CASE WHEN v_map THEN (pe.partition_number % 10000) IN (SELECT mp.partition_number FROM mine_partitions mp) ELSE owner.held IS NULL END
    ORDER BY pe.event_work_id
    FOR UPDATE OF pe SKIP LOCKED
  )
  UPDATE __SCHEMA__.wh_perspective_events pe
  SET instance_id = p_instance_id,
      lease_expiry = v_lease_expiry,
      attempts = pe.attempts + 1
  -- 536: by key, the locked rows as an index condition (an UPDATE ... FROM may be planned as a pass over the queue).
  WHERE pe.event_work_id = ANY(ARRAY(SELECT e.event_work_id FROM eligible e));

  RETURN QUERY
  SELECT
    pe.stream_id,
    es.event_id,
    es.event_type::TEXT,
    eb.event_data::TEXT,
    eb.metadata::TEXT,
    es.scope::TEXT,
    pe.event_work_id,
    pe.perspective_name,
    es.commit_sequence,
    pe.attempts,
    pe.failures,
    -- 264 (#1878): the event's position decides it, never a row. A version-1 event is first unless its stream also
    -- holds a version 0 (numbered from 0, before numbering was unified); the probe runs for version-1 rows only.
    CASE
      WHEN es.version = 0 THEN TRUE
      WHEN es.version = 1 THEN NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store e0
        WHERE e0.stream_id = es.stream_id AND e0.version = 0
      )
      ELSE FALSE
    END,
    -- 583 (#2551): the event's stream position, which the drain applies the stream in.
    es.version::BIGINT
  FROM __SCHEMA__.wh_perspective_events pe
  -- 536: the event by its key, one probe per work row.
  CROSS JOIN LATERAL (
    SELECT s.event_id, s.stream_id, s.event_type, s.scope, s.commit_sequence, s.version
    FROM __SCHEMA__.wh_event_store s
    WHERE s.event_id = pe.event_id
      AND s.stream_id = pe.stream_id
    LIMIT 1
  ) es
  -- Migration 072: COALESCE the offloaded ephemeral body back in. Sourced events have a non-NULL
  -- inline body so the join contributes nothing; ephemeral events read their body from wh_event_body.
  -- 536: by its key, one probe per work row.
  LEFT JOIN LATERAL (
    SELECT b.event_data, b.metadata
    FROM __SCHEMA__.wh_event_body b
    WHERE b.event_id = es.event_id
    LIMIT 1
  ) eb ON TRUE
  -- 536: the batch's rows by stream, the lease a filter on them (IS TRUE: as an index condition on the instance's lease
  -- it read every row the instance holds, which during a fan-out is the whole backlog).
  WHERE (pe.instance_id = p_instance_id AND pe.lease_expiry > p_now) IS TRUE
    AND pe.processed_at IS NULL
    AND pe.stream_id = ANY(p_stream_ids)
    AND (es.commit_sequence IS NOT NULL OR pe.created_at <= v_stamp_cutoff)
  -- 582 (#2551): an unstamped event is read in stream order, its version, never by id.
  ORDER BY pe.stream_id, es.commit_sequence ASC NULLS LAST, es.version;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.get_stream_events(UUID, UUID[], TIMESTAMPTZ, INTEGER) IS
  'Atomic claim+fetch of pending perspective work for the given streams (slice 25): claims unleased '
  'or lapsed rows for the instance (bumping attempts), gates unstamped rows behind a grace window '
  '(058) and streams owned by another live instance (059), then returns the rows leased to the '
  'instance with their event bodies, commit sequence, attempts and failures (139), and whether each '
  'event is the first in its stream (264). 536: the event, its body and the stream''s ledger row are each read by one '
  'probe per work row, so a call reads what its batch holds and never the whole event store or ledger. 566 (#2554, #2555): with the service''s switch on (wh_partition_ownership_by_map), decided through wh_i_own alone.';
