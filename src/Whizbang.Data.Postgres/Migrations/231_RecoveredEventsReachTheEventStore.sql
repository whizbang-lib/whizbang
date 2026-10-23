-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 231_RecoveredEventsReachTheEventStore.sql
-- Date: 2026-10-22
-- Description: A recovered inbox or broker dead letter re-enters through the inbox's own arrival path, and its
--              dead letter reads Recovered only once the message has been processed (#1664).
--
--              recover_dead_letter re-emitted an inbox or broker dead letter with INSERTs of its own, beside
--              store_inbox_messages rather than through it, and the copy had drifted: it wrote is_event = false and,
--              for a broker import, no stream unless the broker carried a session. The inbox copy into the event
--              store reads only events with a stream, so a recovered event was never appended. fetch_inbox_batch
--              withholds an event only until it is stored, and this row was not an event, so it was dispatched,
--              completed and deleted. Its id was recorded as seen, and the dead letter had already been marked
--              Recovered when the row was written: the event was lost and the ledger said it succeeded.
--
--              Recovery now builds the row the way a delivery of the same envelope is built (the recovery worker
--              uses the transport receive path's builder, so is_event, the stream, flags, the producer's identity,
--              scope, priority and the handler name cannot drift) and stores it with store_inbox_messages, which
--              applies every arrival rule. A row that names the dead letter it came from
--              (RecoveredFromDeadLetterId) differs in three ways only: it claims that dead letter (Recovering) in
--              the same transaction; it is not dropped as a duplicate, though its observation is still counted;
--              and its work-state row carries the dead-letter id (wh_inbox_state.recovered_from_dead_letter_id).
--
--              The dead letter is settled when the message completes (process_inbox_completions): Recovered when it
--              was processed (completed as EventStored, or an event the inbox copy already appended), and
--              HoldForReview with the outcome recorded when a gate completed it unprocessed. A message the inbox
--              dead-letters again (the copy's mixed-stream refusal, the attempts ceiling, an unreadable payload)
--              settles the dead letter it was recovered from for review, under an operator hold, through a trigger
--              on the new dead letter, so a cohort release cannot re-drive it while the new row holds its custody.
--              recover_dead_letter keeps its outbox and perspective branches and declines inbox and broker rows.
--
--              Number 231: 230 is the latest on develop and no open branch takes 231.
--
-- Dependencies: 162 (last word of store_inbox_messages, process_inbox_completions, recover_dead_letter and the
--               wh_inbox_state table), 119 (broker import), 050 (wh_dead_letters)
-- Objects: wh_inbox_state, store_inbox_messages, process_inbox_completions, recover_dead_letter,
--          _wh_settle_recovered_dead_letter, _wh_settle_superseded_recovery
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- The dead letter a recovered message re-entered from. NULL for every other arrival. On the work-state row
-- because process_inbox_completions already reads that row to settle a completion, so the settle costs no
-- extra lookup; no index, since nothing selects on it.
ALTER TABLE __SCHEMA__.wh_inbox_state ADD COLUMN IF NOT EXISTS recovered_from_dead_letter_id UUID;

-- _wh_settle_recovered_dead_letter: settles the dead letter a recovered message re-entered from, once.
-- <docs>operations/dead-letter-queue/transport-recovery</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredBrokerDeadLetterEventStoreTests.cs:AnEventImportedFromTheBrokerDeadLetterQueue_AndRecovered_IsAppended_AndScheduledForItsPerspectiveAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredDeadLetterReentrySqlTests.cs:ARecoveredMessage_CompletedUnprocessedByAGate_IsHeldForReview_WithTheOutcomeAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_settle_recovered_dead_letter(
  p_dead_letter_id UUID,
  p_processed BOOLEAN,
  p_now TIMESTAMPTZ
) RETURNS VOID AS $$
BEGIN
  IF p_processed THEN
    UPDATE __SCHEMA__.wh_dead_letters dl
    SET recovery_status = 3,                  -- Recovered: the message was processed
        recovered_at = p_now,
        retried_on_generations =
          CASE WHEN dl.generation = ANY(dl.retried_on_generations) THEN dl.retried_on_generations
               ELSE array_append(dl.retried_on_generations, dl.generation) END
    WHERE dl.dead_letter_id = p_dead_letter_id
      AND dl.recovery_status = 1;
  ELSE
    UPDATE __SCHEMA__.wh_dead_letters dl
    SET recovery_status = 2,                  -- HoldForReview: re-driven, and completed without being processed
        recovered_at = NULL,
        operator_notes = COALESCE(dl.operator_notes || E'\n', '')
          || 'recovery did not complete: the re-driven message was completed without being processed (an inbox gate discarded it)'
    WHERE dl.dead_letter_id = p_dead_letter_id
      AND dl.recovery_status = 1;
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_settle_recovered_dead_letter(UUID, BOOLEAN, TIMESTAMPTZ) IS
  'Settles the dead letter a recovered message re-entered from (231, #1664): Recovered when the message was processed, HoldForReview with the outcome in operator_notes when a gate completed it unprocessed. Only a Recovering row is settled, so a repeated completion is a no-op.';

-- store_inbox_messages: last word 162, with dead-letter recovery as one more arrival.
-- <docs>fundamentals/work-coordinator/overview</docs>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStoreInboxMessagesTests.cs:SingleMessage_StoresInboxAndDedupAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStoreInboxMessagesTests.cs:DuplicateMessageId_SecondCallNoOpsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredDeadLetterReentrySqlTests.cs:ARecoveredMessage_IsStoredInTheSameWorkShapeAsADeliveryOfTheSameEnvelopeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredDeadLetterReentrySqlTests.cs:ARecoveredMessage_AlreadySeen_IsStored_AndItsObservationCountedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredDeadLetterReentrySqlTests.cs:ARecovery_OfADeadLetterAlreadyClaimed_StoresNothingAsync</tests>
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
  v_is_new BOOLEAN;           -- 231: a first arrival, or a recovery that claimed its dead letter
  v_claimed INTEGER;          -- 231: rows the recovery claim or the message insert touched
BEGIN
  IF jsonb_array_length(p_messages) = 0 THEN RETURN; END IF;

  FOR v_msg IN
    SELECT
      (elem->>__ENVELOPE_FIELD_MESSAGE_ID__)::UUID as msg_id,
      elem->>'HandlerName' as handler_name,
      elem->>'EnvelopeType' as envelope_type,
      elem->>'MessageType' as message_type,
      elem->'Envelope' as envelope_data,
      elem->'Metadata' as metadata,
      elem->'Scope' as scope,
      (elem->>__ENVELOPE_FIELD_STREAM_ID__)::UUID as stream_id,
      (elem->>'IsEvent')::BOOLEAN as is_event,
      -- 149: the effective priority the consumer classified; zero (undeclared) and an absent field read as standard.
      COALESCE(NULLIF((elem->>'Priority')::INTEGER, 0), 150) as priority,
      -- EventFlags (062): same robust read as store_outbox_messages so collective events delivered
      -- cross-service via the inbox also route to the __collective__ sink.
      CASE
        WHEN elem->>c_field_flags IS NULL OR elem->>c_field_flags = '' THEN 0
        WHEN elem->>c_field_flags ~ '^[0-9]+$' THEN (elem->>c_field_flags)::INTEGER
        WHEN elem->>c_field_flags ILIKE '%Collective%' THEN 1
        ELSE 0
      END as flags,
      -- 146 (#727): a zero GUID is "unknown", not a producer; COALESCE below then falls back to this service.
      NULLIF((elem->>'SourceServiceId')::UUID, __EMPTY_UUID__::UUID) as source_service_id,
      (elem->>'SourceCommitSequence')::BIGINT as source_commit_sequence,
      -- 231 (#1664): set only by dead-letter recovery, naming the dead-letter row the message re-enters from.
      (elem->>'RecoveredFromDeadLetterId')::UUID as recovered_from
    FROM jsonb_array_elements(p_messages) as elem
    ORDER BY (elem->>__ENVELOPE_FIELD_STREAM_ID__)::UUID NULLS FIRST, (elem->>__ENVELOPE_FIELD_MESSAGE_ID__)::UUID
  LOOP
    -- 231 (#1664): dead-letter recovery re-enters a message HERE, as one more arrival, so every arrival rule
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

    -- 231: a recovery that claimed its dead letter stores the message whatever the count says.
    v_is_new := v_observations = 1 OR v_msg.recovered_from IS NOT NULL;
    IF v_is_new THEN
      IF v_msg.stream_id IS NOT NULL THEN
        v_partition := __SCHEMA__.compute_partition(v_msg.stream_id, p_partition_count);
      ELSE
        v_partition := NULL;
      END IF;

      -- 114: emptiness probe, see store_outbox_messages; inbox pending = not processed
      -- and schedule-eligible (the drain-fetch predicate minus the lease dimension).
      IF v_msg.stream_id IS NOT NULL AND NOT (v_msg.stream_id = ANY(v_probed_streams)) THEN
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
      COALESCE(v_msg.envelope_data, '{}'::jsonb),
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
    -- 231: a recovery that lost the race to a concurrent arrival of the same id hands its dead letter back,
    -- so the dead letter is never left Recovering behind a row that does not name it.
    GET DIAGNOSTICS v_claimed = ROW_COUNT;
    IF v_claimed = 0 AND v_msg.recovered_from IS NOT NULL THEN
      UPDATE __SCHEMA__.wh_dead_letters dl
      SET recovery_status = 0
      WHERE dl.dead_letter_id = v_msg.recovered_from AND dl.recovery_status = 1;
      CONTINUE;
    END IF;

    -- The lease row, unleased and unattempted. A message with no lease row can never be claimed and
    -- would fail silently, so this insert is not optional bookkeeping: it is what makes the message
    -- visible to the claim at all.
    INSERT INTO __SCHEMA__.wh_inbox_state (
      message_id, stream_id, received_at, partition_number, priority, is_event,
      status, processed_at, instance_id, lease_expiry, attempts, recovered_from_dead_letter_id
    ) VALUES (
      v_msg.msg_id,
      v_msg.stream_id,
      p_now,
      v_partition,
      v_msg.priority,
      COALESCE(v_msg.is_event, false),
      1,     -- Stored flag
      NULL,
      NULL,  -- No lease, immediately claimable by WorkCoordinatorPublisherWorker
      NULL,
      0,     -- Initial attempts
      v_msg.recovered_from  -- 231: NULL for every arrival but a recovery
    )
    ON CONFLICT (message_id) DO NOTHING;

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
    END IF;  -- end of the single-observation branch (231: or a claimed recovery)
  END LOOP;

  IF cardinality(v_notify_inbox_streams) > 0 THEN
    PERFORM __SCHEMA__.notify_instance_owners(__CATEGORY_INBOX__, v_notify_inbox_streams);
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.store_inbox_messages(JSONB, UUID, TIMESTAMPTZ, TIMESTAMPTZ, INTEGER) IS
  'Stores received inbox messages with deduplication (121: every arrival counted), priority (149), the work-state row (162). 231 (#1664): a message recovered from a dead letter (RecoveredFromDeadLetterId) re-enters here as one more arrival: it claims its dead letter, is stored even though its id was seen, and its work-state row names the dead letter for the completion to settle.';

-- process_inbox_completions: last word 162, settling a recovered message's dead letter.
-- <docs>fundamentals/work-coordinator/handler-commit</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitHandlerResultSqlTests.cs:CommitHandlerResult_HappyPath_MarksInboxProcessedAndStoresOutboxAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitHandlerResultSqlTests.cs:CommitHandlerResult_DebugModeInRequest_RetainsInboxRowEvenWhenEventStoredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitHandlerResultSqlTests.cs:CommitHandlerResult_ProductionMode_DeletesInboxRowOnEventStoredCompletionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredDeadLetterReentrySqlTests.cs:ARecoveredMessage_CompletedUnprocessedByAGate_IsHeldForReview_WithTheOutcomeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredDeadLetterReentrySqlTests.cs:ARecoveredCommand_CompletedAsProcessed_IsRecoveredAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.process_inbox_completions(
  p_completions JSONB,
  p_now TIMESTAMPTZ,
  p_debug_mode BOOLEAN DEFAULT FALSE
) RETURNS TABLE(
  message_id UUID,
  stream_id UUID,
  was_deleted BOOLEAN
) AS $$
DECLARE
  v_completion RECORD;
  v_current_status INTEGER;
  v_new_status INTEGER;
  v_stream_id UUID;
  v_recovered_from UUID;   -- 231: the dead letter a recovered message re-entered from
  v_is_event BOOLEAN;      -- 231
BEGIN
  IF jsonb_array_length(p_completions) = 0 THEN RETURN; END IF;

  FOR v_completion IN
    SELECT
      (elem->>__ENVELOPE_FIELD_MESSAGE_ID__)::UUID as msg_id,
      (elem->>'Status')::INTEGER as status_flags
    FROM jsonb_array_elements(p_completions) as elem
  LOOP
    -- Get current status and stream_id
    -- 162: both columns are on the state table now (stream_id as an immutable copy), so the
    -- lookup never touches the wide row.
    -- 231: and the recovery columns, from the same row this lookup already reads.
    SELECT ist.status, ist.stream_id, ist.recovered_from_dead_letter_id, ist.is_event
    INTO v_current_status, v_stream_id, v_recovered_from, v_is_event
    FROM __SCHEMA__.wh_inbox_state ist
    WHERE ist.message_id = v_completion.msg_id;

    -- Skip if message not found (already deleted or never existed)
    IF NOT FOUND THEN
      CONTINUE;
    END IF;

    v_new_status := v_current_status | v_completion.status_flags;

    -- 231 (#1664): a recovered message settles its dead letter when it completes. It was processed when the
    -- dispatch completed it as processed (EventStored), or when it is an event the inbox copy already appended
    -- (a gate after the copy declines only the receptors). Anything else is a gate completing it unprocessed.
    IF v_recovered_from IS NOT NULL THEN
      PERFORM __SCHEMA__._wh_settle_recovered_dead_letter(
        v_recovered_from,
        (v_new_status & 2) = 2
          OR (v_is_event AND EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = v_completion.msg_id)),
        p_now);
    END IF;

    IF p_debug_mode THEN
      -- Debug mode: Retain message for troubleshooting
      -- 162: status is work state too, so this stays one statement against one table.
      UPDATE __SCHEMA__.wh_inbox_state ist
      SET status = v_new_status,
          processed_at = p_now,
          instance_id = NULL,
          lease_expiry = NULL
      WHERE ist.message_id = v_completion.msg_id;

      RETURN QUERY SELECT v_completion.msg_id AS message_id, v_stream_id AS stream_id, FALSE AS was_deleted;

    ELSE
      -- Production: Delete if EventStored flag set (inbox completion = event stored)
      IF (v_new_status & 2) = 2 THEN
        -- 162: unchanged. The lease row goes with it through ON DELETE CASCADE, which is why the
        -- foreign key exists rather than being tidiness: a lease left behind for a deleted message
        -- is a row every reclaim path would keep considering forever.
        DELETE FROM __SCHEMA__.wh_inbox i WHERE i.message_id = v_completion.msg_id;
        RETURN QUERY SELECT v_completion.msg_id AS message_id, v_stream_id AS stream_id, TRUE AS was_deleted;
      ELSE
        -- Event not yet stored, retain with updated status
        -- 162: same single statement as the debug branch above.
        UPDATE __SCHEMA__.wh_inbox_state ist
        SET status = v_new_status,
            processed_at = p_now,
            instance_id = NULL,
            lease_expiry = NULL
        WHERE ist.message_id = v_completion.msg_id;
        RETURN QUERY SELECT v_completion.msg_id AS message_id, v_stream_id AS stream_id, FALSE AS was_deleted;
      END IF;
    END IF;
  END LOOP;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.process_inbox_completions(JSONB, TIMESTAMPTZ, BOOLEAN) IS
  'Applies inbox completions: deletes a row completed as EventStored, otherwise records the status (162: on wh_inbox_state). 231 (#1664): a recovered message settles the dead letter it re-entered from, Recovered when processed and HoldForReview with the outcome when a gate completed it unprocessed.';

-- recover_dead_letter: last word 162, without the inbox and broker re-emits (they re-enter through store_inbox_messages).
-- <docs>messaging/dead-letters</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredDeadLetterReentrySqlTests.cs:RecoverDeadLetter_OfAnInboxOrBrokerRow_LeavesItForReentryAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxDlqRecoverySqlTests.cs</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.recover_dead_letter(
  p_dead_letter_id UUID
) RETURNS BOOLEAN AS $$
DECLARE
  v_source_table   TEXT;
  v_source_id      UUID;
  v_stream_id      UUID;
  v_message_type   TEXT;
  v_destination    TEXT;
  v_perspective    TEXT;
  v_envelope       JSONB;
  v_metadata       JSONB;
  v_event_data     JSONB;
  v_partition      INTEGER;
BEGIN
  -- 231 (#1664): an inbox or broker dead letter does not re-enter here. Recovery re-enters it through
  -- store_inbox_messages, as one more arrival built the way a delivery of the same envelope is built, so this
  -- function no longer has inbox INSERTs of its own to drift from that path. Such a row is left untouched (a
  -- caller from before 231 sees "not recovered" and the row stays due for a recovery that re-enters it).
  IF EXISTS (
    SELECT 1 FROM __SCHEMA__.wh_dead_letters dl
    WHERE dl.dead_letter_id = p_dead_letter_id AND dl.source_table IN ('wh_inbox', 'broker')
  ) THEN
    RETURN FALSE;
  END IF;

  -- Atomically claim the row by transitioning to Recovering AND fetch its forensic
  -- payload. If another worker raced us OR the row is already terminal, the UPDATE
  -- affects zero rows and we return false.
  WITH claimed AS (
    UPDATE __SCHEMA__.wh_dead_letters
    SET recovery_status = 1,                  -- Recovering
        recovery_attempts = recovery_attempts + 1,
        last_recovery_at = NOW()
    WHERE dead_letter_id = p_dead_letter_id
      AND recovery_status NOT IN (1, 2, 3, 4)  -- not already Recovering, HoldForReview, Recovered, PermanentlyFailed
      AND recovered_at IS NULL
    RETURNING source_table, source_id, stream_id, message_type, destination, perspective_name, envelope, metadata
  )
  SELECT c.source_table, c.source_id, c.stream_id, c.message_type, c.destination, c.perspective_name, c.envelope, c.metadata
  INTO v_source_table, v_source_id, v_stream_id, v_message_type, v_destination, v_perspective, v_envelope, v_metadata
  FROM claimed c;

  IF v_source_table IS NULL THEN
    RETURN FALSE;  -- already claimed by another worker or already terminal
  END IF;

  -- v0.657 slice 4: DLQ replay self-repair. If the DLQ row preserves a
  -- Guid.Empty stream_id (a pattern observed in production, producer bug from before the
  -- v0.657 storage-time Reject guard shipped), normalize to NULL on the
  -- INSERT back into the source table. Otherwise the recovered row immediately
  -- re-sticks under the same silent-stuck pattern that DLQ'd it in the first
  -- place: stream_id=Empty bypasses the NULL-only `??` coalesce in the C#
  -- coordinator (pre-v0.657) and the slice-3 coordinator backstop sees Empty
  -- as "no real stream identity" → WorkId fallback. NULL is the documented
  -- singleton-stream marker; that's the value we want on recovery.
  IF v_stream_id = __EMPTY_UUID__::uuid THEN
    v_stream_id := NULL;
  END IF;

  -- Extract the original event_data from the envelope JSONB.
  v_event_data := v_envelope -> 'event_data';
  v_partition := CASE WHEN v_stream_id IS NULL THEN 0 ELSE 0 END;  -- partition recomputed on store_*_messages path; fixed to 0 here is fine because claim_orphaned_* recomputes via wh_active_streams

  -- Re-emit into the appropriate source table with attempts=0.
  IF v_source_table = 'wh_outbox' THEN
    INSERT INTO __SCHEMA__.wh_outbox (message_id, destination, message_type, envelope_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number, priority)
    VALUES (v_source_id, v_destination, v_message_type, 'recovered', v_event_data, v_metadata, 0, 0, NOW(), v_stream_id, v_partition, __PRIORITY_BACKGROUND__)
    ON CONFLICT (message_id) DO NOTHING;  -- already re-published; idempotent
  ELSIF v_source_table = 'wh_perspective_events' THEN
    -- Perspective recovery uses the event_id snapshot to recreate the work row.
    INSERT INTO __SCHEMA__.wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at, priority)
    VALUES (v_source_id, v_stream_id, v_perspective, (v_envelope ->> 'event_id')::UUID, v_partition, 0, 0, NOW(), __PRIORITY_BACKGROUND__)
    ON CONFLICT (event_work_id) DO NOTHING;
  ELSE
    -- Unknown source table, leave as Recovering for an operator to investigate.
    RAISE WARNING 'recover_dead_letter: unsupported source table %', v_source_table;
    RETURN FALSE;
  END IF;

  -- Mark Recovered.
  UPDATE __SCHEMA__.wh_dead_letters
  SET recovery_status = 3,
      recovered_at = NOW(),
      retried_on_generations =
        CASE WHEN generation = ANY(retried_on_generations) THEN retried_on_generations
             ELSE array_append(retried_on_generations, generation) END
  WHERE dead_letter_id = p_dead_letter_id;

  RETURN TRUE;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.recover_dead_letter(UUID) IS
  'Re-emits an outbox or perspective dead letter into its source table with attempts reset and marks it Recovered (051; 151: at background priority; 162). 231 (#1664): an inbox or broker dead letter is declined here and left due: recovery re-enters it through store_inbox_messages, and its completion settles it.';

-- <docs>operations/dead-letter-queue/transport-recovery</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredBrokerDeadLetterEventStoreTests.cs:ARecoveredEvent_RefusedByTheInboxCopy_LeavesItsDeadLetterNotRecovered_WithTheOutcomeRecordedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredDeadLetterReentrySqlTests.cs:ARecoveredMessage_DeadLetteredAgain_IsHeldForReview_NamingTheNewDeadLetterAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_settle_superseded_recovery() RETURNS TRIGGER AS $$
BEGIN
  -- A recovered message the inbox dead-letters again: NEW holds its custody now, so the row it was recovered from
  -- (Recovering while the message is in flight, or Recovered by a recovery from before 231) is settled for review
  -- with the outcome recorded. The operator hold keeps a cohort release from re-driving the message a second time
  -- while NEW holds it.
  UPDATE __SCHEMA__.wh_dead_letters prior
  SET recovery_status = 2,        -- HoldForReview
      recovered_at = NULL,
      operator_disposition = 2,   -- HoldIndefinitely
      operator_notes = COALESCE(prior.operator_notes || E'\n', '')
        || format('recovery did not complete: the re-driven message was dead-lettered again as %s (failure reason %s), '
                  || 'which holds its custody now', NEW.dead_letter_id, NEW.failure_reason)
  WHERE prior.source_id = NEW.source_id
    -- NEW.source_table is the inbox's (the trigger's WHEN): the message re-entered from the broker or the inbox.
    AND prior.source_table IN ('broker', NEW.source_table)
    AND prior.dead_letter_id <> NEW.dead_letter_id
    AND prior.recovery_status IN (1, 3);
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_settle_superseded_recovery() IS
  'AFTER INSERT trigger fn on wh_dead_letters for an inbox source (231, #1664): a recovered message the inbox dead-letters again settles the row it was recovered from as HoldForReview under an operator hold, naming the new dead letter in operator_notes, so Recovered never reads true of a message whose recovery did not complete.';

DROP TRIGGER IF EXISTS trg_wh_dead_letters_settle_superseded_recovery ON __SCHEMA__.wh_dead_letters;
CREATE TRIGGER trg_wh_dead_letters_settle_superseded_recovery
  AFTER INSERT ON __SCHEMA__.wh_dead_letters
  FOR EACH ROW
  WHEN (NEW.source_table = 'wh_inbox')
  EXECUTE FUNCTION __SCHEMA__._wh_settle_superseded_recovery();
