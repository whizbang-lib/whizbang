-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 239_RecoveredWorkReentersThroughItsStore.sql
-- Date: 2026-10-24
-- Description: A recovered outbox or perspective dead letter re-enters through its own arrival path, and its dead
--              letter reads Recovered only once the message is published or the event applied (#1722).
--
--              236 (#1664) made inbox and broker recovery re-enter through store_inbox_messages. The outbox and
--              perspective branches of recover_dead_letter kept the shape that caused #1664: each re-inserted its row
--              with a column list of its own and marked the dead letter Recovered in the same call, before anything
--              ran, with no check that the insert happened. The outbox copy had drifted from store_outbox_messages:
--              envelope_type 'recovered' (the drain sends the row's envelope type as the wire's envelope type header,
--              so a receiver resolved the message under a name no context knows and dropped it), not an event, no
--              scope or flags, status 0, partition 0, background priority, and none of the store's steps (doorbell,
--              stream pin, collective link, mixed-stream refusal, event-store chain, audit position fill). The
--              perspective copy wrote partition 0, background priority and no rewind check. The root cause is the
--              capture: move_to_dead_letters kept the outbox row's body, metadata and destination only.
--
--              Now the capture keeps every field the store needs, and recovery re-enters through the store: an outbox
--              dead letter through store_outbox_messages, a perspective dead letter through store_perspective_events
--              (the perspective work store, redefined here with the event-store chain's rules: partition, Stored
--              status, priority, the lease routing, the rewind of a late event and the doorbell). A row that names the
--              dead letter it came from (RecoveredFromDeadLetterId) differs in two ways only: it claims that dead
--              letter (Recovering) in the same transaction, and the row carries the dead-letter id
--              (recovered_from_dead_letter_id). The completion settles it: complete_outbox_published once the message
--              is published, process_perspective_event_completions once the event is applied. A message dead-lettered
--              again settles the row it was recovered from for review under an operator hold, through the 236 trigger,
--              now on every source table. A dead letter captured before this migration is re-entered with what it
--              kept, the envelope type its message type names (the name EnvelopeSerializer writes), and the event
--              flag, flags and scope of its event when the event store holds it. A perspective dead letter whose event
--              is no longer in the event store is held for review with the reason, since re-driving it can apply
--              nothing.
--
--              wh_recover_dead_letter is recover_dead_letter with the partition count every arrival is stored with
--              (ClaimWorkerOptions), which the recovery service passes; recover_dead_letter keeps its signature and
--              calls it at the claim's default count.
--
-- Dependencies: 236 (last word of recover_dead_letter and _wh_settle_superseded_recovery, and
--               _wh_settle_recovered_dead_letter), 232 (last word of store_outbox_messages), 162 (last word of
--               move_to_dead_letters), 172 (last word of complete_outbox_published), 177 (last word of
--               process_perspective_event_completions), 022 (last word of store_perspective_events)
-- Objects: wh_outbox, wh_perspective_events, move_to_dead_letters, store_outbox_messages, store_perspective_events,
--          wh_recover_dead_letter, recover_dead_letter, complete_outbox_published, process_perspective_event_completions,
--          _wh_settle_superseded_recovery
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- The dead letter a recovered row re-entered from. NULL for every other arrival. Read by the completion that retires
-- the row, so the settle costs no extra lookup; no index, since nothing selects on it.
ALTER TABLE __SCHEMA__.wh_outbox ADD COLUMN IF NOT EXISTS recovered_from_dead_letter_id UUID;
ALTER TABLE __SCHEMA__.wh_perspective_events ADD COLUMN IF NOT EXISTS recovered_from_dead_letter_id UUID;

-- move_to_dead_letters: last word 162, with an outbox row's capture keeping every field its store takes and a
-- perspective row's keeping its priority (#1722).
-- <docs>operations/dead-letter-queue/recovery</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MoveToDeadLettersSqlTests.cs:MoveToDeadLetters_InboxRow_MovesIntoDlqAndDeletesSourceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DlqStackTracePreservationSqlTests.cs:MoveToDeadLetters_PreservesFullStackTextAcrossAllSourceTablesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredWorkReentrySqlTests.cs:ARecoveredOutboxMessage_IsStoredInTheShapeItWasStoredInAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredWorkReentrySqlTests.cs:AnOutboxEventRefusedBeforeItWasAppended_IsAppendedWhenRecovered_WithItsScopeAndFlagsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredWorkReentrySqlTests.cs:ARecoveredPerspectiveWorkRow_IsStoredInTheShapeTheEventStoreChainStoresItInAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.move_to_dead_letters(
  p_dead_letter_id UUID,                                       -- caller generates (TrackedGuid.NewMedo on C# side)
  p_source_table  TEXT,
  p_source_id     UUID,
  p_failure_reason INTEGER,
  p_error_text    TEXT,
  p_instance_id   UUID,
  p_generation    TEXT
) RETURNS UUID AS $$
DECLARE
  v_stream_id      UUID;
  v_message_type   TEXT;
  v_destination    TEXT;
  v_perspective    TEXT;
  v_envelope       JSONB;
  v_metadata       JSONB;
  v_attempts       INTEGER;
  v_source_error   TEXT;
  v_error_text     TEXT;
  v_prior_recovery_attempts INTEGER;
BEGIN

  -- Snapshot per source table. Each branch reads the canonical columns and DELETEs in a
  -- single CTE so the row movement is atomic. The wh_outbox / wh_inbox tables carry the
  -- envelope + metadata directly; wh_perspective_events carries an event_id pointer that
  -- the recovery worker will rejoin against wh_event_store at recovery time.
  IF p_source_table = 'wh_outbox' THEN
    WITH moved AS (
      DELETE FROM __SCHEMA__.wh_outbox
      WHERE message_id = p_source_id
      RETURNING
        stream_id,
        message_type,
        destination,
        event_data,
        metadata,
        attempts,
        envelope_type,
        is_event,
        flags,
        scope,
        priority,
        scheduled_for,
        coalesce_group
    )
    -- 239 (#1722): every field store_outbox_messages takes, so recovery re-enters the message as it was stored.
    SELECT m.stream_id, m.message_type, m.destination,
           jsonb_build_object('event_data', m.event_data, 'metadata', m.metadata,
                              'envelope_type', m.envelope_type, 'is_event', m.is_event, 'flags', m.flags,
                              'scope', m.scope, 'priority', m.priority, 'scheduled_for', m.scheduled_for,
                              'coalesce_group', m.coalesce_group),
           m.metadata, m.attempts
      INTO v_stream_id, v_message_type, v_destination, v_envelope, v_metadata, v_attempts
    FROM moved m;
  ELSIF p_source_table = 'wh_inbox' THEN
    -- 162: attempts and error moved to wh_inbox_state, and a DELETE's RETURNING clause cannot reach
    -- another table's columns. The lease row is read in a sibling CTE instead, which is correct
    -- because every CTE in one statement sees the same pre-statement snapshot: the lease row is
    -- still visible here even though the DELETE's ON DELETE CASCADE is removing it in the same
    -- statement.
    --
    -- LEFT JOIN rather than an inner one, deliberately. If the lease row were somehow missing, an
    -- inner join would return no row, leave v_attempts NULL, and the guard below would raise after
    -- the message had already been deleted, which loses it. The outer join always returns the moved
    -- row, so a missing lease surfaces as the existing NULL-attempts guard rather than as a lost
    -- message.
    WITH lease AS (
      SELECT ist.attempts, ist.error
      FROM __SCHEMA__.wh_inbox_state ist
      WHERE ist.message_id = p_source_id
    ),
    moved AS (
      DELETE FROM __SCHEMA__.wh_inbox
      WHERE message_id = p_source_id
      RETURNING
        stream_id,
        message_type,
        event_data,
        metadata
    )
    SELECT m.stream_id, m.message_type, NULL::TEXT,
           jsonb_build_object('event_data', m.event_data, 'metadata', m.metadata),
           m.metadata, l.attempts, l.error
      INTO v_stream_id, v_message_type, v_destination, v_envelope, v_metadata, v_attempts, v_source_error
    FROM moved m LEFT JOIN lease l ON TRUE;
  ELSIF p_source_table = 'wh_perspective_events' THEN
    WITH moved AS (
      DELETE FROM __SCHEMA__.wh_perspective_events
      WHERE event_work_id = p_source_id
      RETURNING
        stream_id,
        perspective_name,
        event_id,
        attempts,
        error,
        priority
    )
    -- 239 (#1722): and the work row's priority, which store_perspective_events takes.
    SELECT m.stream_id, m.perspective_name,
           jsonb_build_object('event_id', m.event_id, 'perspective_name', m.perspective_name, 'priority', m.priority),
           '{}'::JSONB, m.attempts, m.error
      INTO v_stream_id, v_perspective, v_envelope, v_metadata, v_attempts, v_source_error
    FROM moved m;
    v_message_type := 'perspective_event';
  ELSE
    RAISE EXCEPTION 'move_to_dead_letters: unsupported source table %', p_source_table;
  END IF;

  -- If no row was found (already DLQ'd, already DELETEd by another path), no-op.
  IF v_attempts IS NULL THEN
    RETURN NULL;
  END IF;

  -- Issue #518: the retry budget belongs to the MESSAGE, not to one dead-letter row.
  -- move_to_dead_letters mints a NEW dead_letter_id on every re-failure, and the recovery
  -- worker's exhaustion check reads recovery_attempts off THAT row, so a message that fails
  -- again after recovery restarted from zero, HoldForReviewAfterExhaustion could never engage,
  -- and a single poison message cycled indefinitely (observed: one message dead-lettered 257
  -- times in 15 minutes; 46k rows from 7.6k distinct messages, enough churn to exhaust a shared
  -- database's connection limit). Seeding the new row with the attempts already spent on this
  -- same (source_table, source_id) makes the budget cumulative across incarnations, so the
  -- ladder terminates. A first-time failure still gets its full budget (COALESCE to 0).
  -- Scoped to the CURRENT generation on purpose: a new build is a new chance. Generation-tagged
  -- auto-replay ("we shipped a fix, replay the casualties") depends on previously-exhausted
  -- messages getting a fresh budget after a deploy; a globally-cumulative counter would hold
  -- them forever and silently kill that recovery path.
  SELECT COALESCE(SUM(recovery_attempts), 0) INTO v_prior_recovery_attempts
  FROM __SCHEMA__.wh_dead_letters
  WHERE source_table = p_source_table
    AND source_id = p_source_id
    AND generation IS NOT DISTINCT FROM p_generation;

  -- Migration 118: preserve the source row's stored terminal error (wh_perspective_events.error
  --, the actual apply exception) alongside the caller's promotion wrapper. Without this, DLQ
  -- rows carried only "attempts=N > max=M" and the root cause was unrecoverable once pod logs
  -- rotated. The fingerprint is computed on the ENRICHED text so failure modes cluster by the
  -- real exception instead of collapsing into one wrapper cluster.
  v_error_text := CASE
    WHEN v_source_error IS NOT NULL AND v_source_error <> ''
      THEN COALESCE(p_error_text || ', last error: ', '') || v_source_error
    ELSE p_error_text
  END;

  -- Slice 3a of release/v0.645.0-alpha.1, auto-fingerprint every row at INSERT
  -- time via Slice 2's compute_dead_letter_fingerprint. One source of truth
  -- (the SQL function), three call sites (this INSERT, plus Slice 6's
  -- aggregate_dead_letters version-aware backfill). NULL p_error_text →
  -- NULL fingerprint + NULL version so the column NULLability flows through.
  INSERT INTO __SCHEMA__.wh_dead_letters (
    dead_letter_id,
    source_table,
    source_id,
    stream_id,
    message_type,
    destination,
    perspective_name,
    envelope,
    metadata,
    failure_reason,
    error_text,
    error_fingerprint,
    error_fingerprint_version,
    attempts_when_dlq,
    dead_lettered_by,
    generation,
    recovery_attempts
  ) VALUES (
    p_dead_letter_id,
    p_source_table,
    p_source_id,
    v_stream_id,
    v_message_type,
    v_destination,
    v_perspective,
    v_envelope,
    v_metadata,
    p_failure_reason,
    v_error_text,
    __SCHEMA__.compute_dead_letter_fingerprint(v_error_text),
    CASE WHEN v_error_text IS NOT NULL
         THEN __SCHEMA__.current_dead_letter_fingerprint_version()
         ELSE NULL
    END,
    v_attempts,
    p_instance_id,
    p_generation,
    v_prior_recovery_attempts
  );

  RETURN p_dead_letter_id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.move_to_dead_letters(UUID, TEXT, UUID, INTEGER, TEXT, UUID, TEXT) IS
  'Moves a failed work row into wh_dead_letters and deletes it from its source table, atomically (050; 118: the source '
  'error kept; 120: the retry budget is the message''s; 162: inbox attempts from wh_inbox_state). 239 (#1722): an outbox '
  'row''s envelope keeps every field store_outbox_messages takes (envelope type, event flag, flags, scope, priority, '
  'schedule, coalesce group) and a perspective row''s keeps its priority, so recovery re-enters it as it was stored.';

-- store_outbox_messages: last word 232, with dead-letter recovery as one more arrival (#1722).
-- <docs>fundamentals/events/system-events#audit</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AuditRecordStreamPositionSqlTests.cs:Store_EventAndItsAuditTogether_RecordCarriesTheEventsVersionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/EFCoreCollectiveOrderingHeadTests.cs:StoreOutboxMessagesAsync_TwoCoordinators_LinkTheSecondToTheFirstAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MixedStreamGuardSqlTests.cs:Store_RefusedEventInABatch_LeavesTheOtherStreamsStoredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StringEventFlagsStoreTests.cs:StoreOutbox_FlagsWrittenAsNames_KeepsEveryBitAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredWorkReentrySqlTests.cs:ARecoveredOutboxMessage_IsStoredInTheShapeItWasStoredInAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredWorkReentrySqlTests.cs:ARecovery_OfAnOutboxMessageStillInTheOutbox_LeavesItsDeadLetterDueAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredOutboxDeadLetterPublishTests.cs:ARecoveredOutboxEvent_IsPublishedAsTheEventItWasStoredAs_AndReadsRecoveredOncePublishedAsync</tests>
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
  v_claimed INTEGER;          -- 239: rows the recovery claim touched
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
      -- 239 (#1722): set only by dead-letter recovery, naming the dead-letter row the message re-enters from.
      (elem->>'RecoveredFromDeadLetterId')::UUID as recovered_from
    FROM jsonb_array_elements(p_messages) as elem
    ORDER BY (elem->>__ENVELOPE_FIELD_STREAM_ID__)::UUID NULLS FIRST, (elem->>__ENVELOPE_FIELD_MESSAGE_ID__)::UUID
  LOOP
    -- 239 (#1722): dead-letter recovery re-enters a message HERE, as one more arrival, so every rule below applies to
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
      recovered_from_dead_letter_id
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
      v_msg.recovered_from  -- 239: NULL for every arrival but a recovery
    )
    ON CONFLICT ON CONSTRAINT wh_outbox_pkey DO NOTHING;

    GET DIAGNOSTICS v_was_new = ROW_COUNT;

    -- 239: a recovery that lost the race to a concurrent store of the same id hands its dead letter back, so the dead
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
  'written as a name list, and raises 22P02 on a name EventFlags does not define (#1612). 239 (#1722): a message '
  'recovered from a dead letter (RecoveredFromDeadLetterId) re-enters here as one more arrival: it claims its dead '
  'letter and its row names it for complete_outbox_published to settle.';

-- store_perspective_events: last word 022, redefined as the perspective work store with the event-store chain's
-- rules (partition, Stored status, priority, the lease routing, the rewind of a late event and the doorbell), and with
-- dead-letter recovery as one more arrival (#1722). Signature unchanged.
-- <docs>fundamentals/work-coordinator/stream-ownership</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveEventsPartitionSqlTests.cs:StorePerspectiveEvents_PopulatesPartitionNumberAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredWorkReentrySqlTests.cs:ARecoveredPerspectiveWorkRow_IsStoredInTheShapeTheEventStoreChainStoresItInAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredWorkReentrySqlTests.cs:ARecoveredPerspectiveEvent_BehindItsCursor_FlagsTheCursorForRewindAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredPerspectiveDeadLetterApplyTests.cs:ARecoveredPerspectiveWorkRow_IsApplied_AndReadsRecoveredOnceAppliedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.store_perspective_events(
  p_events JSONB,
  p_instance_id UUID,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_partition_count INTEGER DEFAULT 10000
) RETURNS TABLE(
  event_work_id UUID,
  stream_id UUID,
  perspective_name VARCHAR(200),
  was_newly_created BOOLEAN
) AS $$
DECLARE
  v_event RECORD;
  v_was_new BOOLEAN;
  v_work_id UUID;
  v_lease_instance UUID;
  v_claimed INTEGER;
  -- The doorbell, as store_outbox_messages rings it: streams probed once per call before this call inserts for them,
  -- the probed-empty ones, and those this call gave new work.
  v_probed_streams UUID[] := ARRAY[]::UUID[];
  v_empty_streams UUID[] := ARRAY[]::UUID[];
  v_notify_streams UUID[] := ARRAY[]::UUID[];
BEGIN
  IF jsonb_array_length(p_events) = 0 THEN RETURN; END IF;

  FOR v_event IN
    SELECT
      (elem->>__ENVELOPE_FIELD_STREAM_ID__)::UUID AS ev_stream_id,
      (elem->>'PerspectiveName')::VARCHAR(200) AS ev_perspective_name,
      (elem->>'EventId')::UUID AS ev_event_id,
      -- 239: the work id to store the row under; a new one when absent. Recovery keeps the dead letter's, so a row
      -- dead-lettered again is recognized as the same work.
      (elem->>'EventWorkId')::UUID AS ev_work_id,
      -- The chain schedules work with its source row's priority; zero (undeclared) and an absent field read as standard.
      COALESCE(NULLIF((elem->>'Priority')::INTEGER, 0), 150) AS ev_priority,
      -- 239 (#1722): set only by dead-letter recovery, naming the dead-letter row the work re-enters from.
      (elem->>'RecoveredFromDeadLetterId')::UUID AS ev_recovered_from
    FROM jsonb_array_elements(p_events) AS elem
    ORDER BY (elem->>__ENVELOPE_FIELD_STREAM_ID__)::UUID, (elem->>'EventId')::UUID
  LOOP
    -- 239 (#1722): a recovery claims its dead letter first (Recovering), in this transaction, so two recoveries of one
    -- row cannot both re-enter it, and a dead letter whose work is still queued is left alone.
    IF v_event.ev_recovered_from IS NOT NULL THEN
      UPDATE __SCHEMA__.wh_dead_letters dl
      SET recovery_status = 1,                  -- Recovering, until the event is applied
          recovery_attempts = dl.recovery_attempts + 1,
          last_recovery_at = p_now
      WHERE dl.dead_letter_id = v_event.ev_recovered_from
        AND dl.source_id = v_event.ev_work_id
        AND dl.recovery_status NOT IN (1, 2, 3, 4)  -- not already Recovering, HoldForReview, Recovered, PermanentlyFailed
        AND dl.recovered_at IS NULL
        AND NOT EXISTS (
          SELECT 1 FROM __SCHEMA__.wh_perspective_events queued
          WHERE queued.stream_id = v_event.ev_stream_id
            AND queued.perspective_name = v_event.ev_perspective_name
            AND queued.event_id = v_event.ev_event_id);
      GET DIAGNOSTICS v_claimed = ROW_COUNT;
      IF v_claimed = 0 THEN
        CONTINUE;  -- claimed by another recovery, already settled, or the work is still queued
      END IF;
    END IF;

    -- Emptiness probe, once per stream per call, before this call's first insert for it (the loop is stream-ordered).
    -- 140: a locked pending row is being completed, so it reads as empty and rings. 161: ordered by the index key so
    -- the probe never scans a stream whose work is all done.
    IF NOT (v_event.ev_stream_id = ANY(v_probed_streams)) THEN
      v_probed_streams := array_append(v_probed_streams, v_event.ev_stream_id);
      PERFORM 1 FROM __SCHEMA__.wh_perspective_events pe
        WHERE pe.stream_id = v_event.ev_stream_id
          AND pe.processed_at IS NULL
        ORDER BY pe.stream_id, pe.event_id
        LIMIT 1
        FOR SHARE OF pe SKIP LOCKED;
      IF NOT FOUND THEN
        v_empty_streams := array_append(v_empty_streams, v_event.ev_stream_id);
      END IF;
    END IF;

    -- The chain's lease routing (Slice 26.14): a leasing caller leases to the stream's live pinned owner, falling back
    -- to itself; a caller passing no lease expiry leaves the row for the claim.
    v_lease_instance := NULL;
    IF p_lease_expiry IS NOT NULL THEN
      SELECT ast.assigned_instance_id INTO v_lease_instance
      FROM __SCHEMA__.wh_active_streams ast
      WHERE ast.stream_id = v_event.ev_stream_id
        AND ast.assigned_instance_id IS NOT NULL
        AND EXISTS (
          SELECT 1 FROM __SCHEMA__.wh_service_instances si
          WHERE si.instance_id = ast.assigned_instance_id);
      v_lease_instance := COALESCE(v_lease_instance, p_instance_id);
    END IF;

    v_work_id := COALESCE(v_event.ev_work_id, gen_random_uuid());
    INSERT INTO __SCHEMA__.wh_perspective_events (
      event_work_id, stream_id, perspective_name, event_id,
      partition_number, status, attempts, created_at, instance_id, lease_expiry, priority,
      recovered_from_dead_letter_id
    ) VALUES (
      v_work_id,
      v_event.ev_stream_id,
      v_event.ev_perspective_name,
      v_event.ev_event_id,
      __SCHEMA__.compute_partition(v_event.ev_stream_id, p_partition_count),
      1,                  -- Stored flag
      0,                  -- Initial attempts
      p_now,
      v_lease_instance,
      p_lease_expiry,
      v_event.ev_priority,
      v_event.ev_recovered_from  -- 239: NULL for every arrival but a recovery
    )
    -- Constraint-less, so a work id that is already taken is a conflict as well as the (stream, perspective, event) key.
    ON CONFLICT DO NOTHING;
    GET DIAGNOSTICS v_was_new = ROW_COUNT;

    IF NOT v_was_new THEN
      -- 239: a recovery that lost the race to a concurrent store of the same work hands its dead letter back, so the
      -- dead letter is never left Recovering behind a row that does not name it.
      IF v_event.ev_recovered_from IS NOT NULL THEN
        UPDATE __SCHEMA__.wh_dead_letters dl
        SET recovery_status = 0
        WHERE dl.dead_letter_id = v_event.ev_recovered_from AND dl.recovery_status = 1;
        CONTINUE;
      END IF;
      -- The work is already queued: answer with its id.
      SELECT pe.event_work_id INTO v_work_id
      FROM __SCHEMA__.wh_perspective_events pe
      WHERE pe.stream_id = v_event.ev_stream_id
        AND pe.perspective_name = v_event.ev_perspective_name
        AND pe.event_id = v_event.ev_event_id;
    ELSE
      -- A late event (behind its cursor's last applied event) is applied by replaying the stream in order: the cursor
      -- is flagged RewindRequired with the earliest such event as its trigger, exactly as the chain flags it (100).
      UPDATE __SCHEMA__.wh_perspective_cursors pc
      SET status = pc.status | 32,  -- RewindRequired flag (1 << 5)
          rewind_trigger_event_id = CASE
            WHEN pc.rewind_trigger_event_id IS NULL THEN v_event.ev_event_id
            WHEN v_event.ev_event_id < pc.rewind_trigger_event_id THEN v_event.ev_event_id
            ELSE pc.rewind_trigger_event_id
          END,
          rewind_flagged_at = p_now,
          rewind_first_flagged_at = COALESCE(pc.rewind_first_flagged_at, p_now)
      WHERE pc.stream_id = v_event.ev_stream_id
        AND pc.perspective_name = v_event.ev_perspective_name
        AND pc.last_event_id IS NOT NULL
        AND v_event.ev_event_id < pc.last_event_id;

      IF v_event.ev_stream_id = ANY(v_empty_streams)
         AND NOT (v_event.ev_stream_id = ANY(v_notify_streams)) THEN
        v_notify_streams := array_append(v_notify_streams, v_event.ev_stream_id);
      END IF;
    END IF;

    RETURN QUERY SELECT v_work_id, v_event.ev_stream_id, v_event.ev_perspective_name, v_was_new;
  END LOOP;

  IF cardinality(v_notify_streams) > 0 THEN
    PERFORM __SCHEMA__.notify_instance_owners(__CATEGORY_PERSPECTIVE__, v_notify_streams);
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.store_perspective_events(JSONB, UUID, TIMESTAMPTZ, TIMESTAMPTZ, INTEGER) IS
  'Stores perspective work rows (022) with the event-store chain''s rules (239): partition from the stream, Stored, the '
  'given priority, leased to the stream''s live owner (or the caller) when a lease expiry is given, the cursor flagged '
  'RewindRequired for an event behind it, and the perspective doorbell rung for a stream that had no pending work. '
  'Unique on (stream, perspective, event). 239 (#1722): work recovered from a dead letter (RecoveredFromDeadLetterId) '
  're-enters here: it claims its dead letter and its row names it for process_perspective_event_completions to settle.';

-- wh_recover_dead_letter: recover_dead_letter's body (last word 236), re-entering an outbox or perspective dead letter
-- through its store (#1722) with the partition count arrivals are stored with. A function of its own rather than a
-- second argument on recover_dead_letter: a new arity adds an overload, and the earlier files that comment on the
-- function by its bare name would raise 42725 when the ledger re-runs them.
-- <docs>operations/dead-letter-queue/recovery</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredDeadLetterReentrySqlTests.cs:RecoverDeadLetter_OfAnInboxOrBrokerRow_LeavesItForReentryAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxDlqRecoverySqlTests.cs:RecoverDeadLetter_OutboxSource_ReturnsToOutboxAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredWorkReentrySqlTests.cs:ARecoveredOutboxMessage_IsStoredInTheShapeItWasStoredInAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredWorkReentrySqlTests.cs:ARecoveredPerspectiveEvent_NoLongerInTheEventStore_IsHeldForReview_AndNotReEnteredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredOutboxDeadLetterPublishTests.cs:AnOutboxDeadLetterCapturedBeforeItKeptTheEnvelopeType_IsPublishedUnderTheNameItsStoreWritesAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_recover_dead_letter(
  p_dead_letter_id UUID,
  p_partition_count INTEGER
) RETURNS BOOLEAN AS $$
DECLARE
  v_dead RECORD;
  v_envelope JSONB;
  v_stream_id UUID;
  v_event_id UUID;
  v_event_flags INTEGER;      -- a pre-239 capture's event, when the event store holds it
  v_event_scope JSONB;
  v_has_event BOOLEAN := FALSE;
  v_stored BOOLEAN;
BEGIN
  -- Only an outbox or perspective dead letter re-enters here. 236 (#1664): an inbox or broker dead letter re-enters
  -- through store_inbox_messages, from the recovery worker; it, and a row from any other source, is left untouched
  -- and due. 239: the row is read, not claimed. The store it re-enters through claims it atomically (Recovering) as
  -- it stores the row, so a row another recovery claimed, or one already settled, is not re-entered twice.
  SELECT dl.source_table, dl.source_id, dl.stream_id, dl.message_type, dl.destination, dl.perspective_name, dl.envelope
  INTO v_dead
  FROM __SCHEMA__.wh_dead_letters dl
  WHERE dl.dead_letter_id = p_dead_letter_id
    AND dl.source_table IN ('wh_outbox', 'wh_perspective_events')
    AND dl.recovery_status NOT IN (1, 2, 3, 4)  -- not already Recovering, HoldForReview, Recovered, PermanentlyFailed
    AND dl.recovered_at IS NULL;
  IF NOT FOUND THEN
    RETURN FALSE;
  END IF;

  v_envelope := COALESCE(v_dead.envelope, '{}'::jsonb);
  -- v0.657 slice 4: a Guid.Empty stream (a producer bug before the storage-time guard) is the singleton-stream marker.
  v_stream_id := NULLIF(v_dead.stream_id, __EMPTY_UUID__::uuid);

  IF v_dead.source_table = 'wh_outbox' THEN
    -- A dead letter captured before 239 kept the body and the metadata only. Its event flag, flags and scope are its
    -- event's when the event store holds it (an outbox event with a stream is appended as it is stored), and its
    -- envelope type is the one its message type names (EnvelopeSerializer's form).
    IF NOT (v_envelope ? 'is_event') THEN
      SELECT es.flags, to_jsonb(es.scope) INTO v_event_flags, v_event_scope
      FROM __SCHEMA__.wh_event_store es
      WHERE es.event_id = v_dead.source_id;
      v_has_event := FOUND;
    END IF;

    SELECT EXISTS (
      SELECT 1
      FROM __SCHEMA__.store_outbox_messages(
        jsonb_build_array(jsonb_build_object(
          __ENVELOPE_FIELD_MESSAGE_ID__, v_dead.source_id,
          'Destination', v_dead.destination,
          'MessageType', v_dead.message_type,
          'EnvelopeType', COALESCE(
            v_envelope->>'envelope_type',
            'Whizbang.Core.Observability.MessageEnvelope`1[[' || v_dead.message_type || ']], Whizbang.Core'),
          'Envelope', v_envelope->'event_data',
          'Metadata', v_envelope->'metadata',
          'Scope', CASE WHEN v_envelope ? 'scope' THEN v_envelope->'scope'
                        ELSE v_event_scope END,
          __ENVELOPE_FIELD_STREAM_ID__, v_stream_id,
          'IsEvent', COALESCE((v_envelope->>'is_event')::BOOLEAN, v_has_event),
          __ENVELOPE_FIELD_FLAGS__, COALESCE((v_envelope->>'flags')::INTEGER, v_event_flags, 0),
          -- A dead letter captured before 239 keeps no priority; it re-enters in the band recovery used to give it.
          'Priority', COALESCE((v_envelope->>'priority')::INTEGER, __PRIORITY_BACKGROUND__),
          'ScheduledFor', v_envelope->>'scheduled_for',
          'CoalesceGroup', v_envelope->>'coalesce_group',
          'RecoveredFromDeadLetterId', p_dead_letter_id)),
        NULL,   -- no instance: the row is left unleased for the claim
        NULL,
        NOW(),
        p_partition_count) stored
      WHERE stored.was_newly_created
    ) INTO v_stored;
    RETURN v_stored;
  END IF;

  -- A perspective dead letter.
  v_event_id := (v_envelope->>'event_id')::UUID;
  -- Work for an event the event store no longer holds (its stream was purged) can never apply anything, so it is
  -- held for review with the reason instead of being re-driven until its budget runs out.
  IF NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = v_event_id) THEN
    UPDATE __SCHEMA__.wh_dead_letters dl
    SET recovery_status = 2,                  -- HoldForReview
        operator_notes = COALESCE(dl.operator_notes || E'\n', '')
          || format('recovery cannot re-enter this work: event %s is no longer in the event store', v_event_id)
    WHERE dl.dead_letter_id = p_dead_letter_id
      AND dl.recovery_status NOT IN (1, 2, 3, 4)
      AND dl.recovered_at IS NULL;
    RETURN FALSE;
  END IF;

  SELECT EXISTS (
    SELECT 1
    FROM __SCHEMA__.store_perspective_events(
      jsonb_build_array(jsonb_build_object(
        __ENVELOPE_FIELD_STREAM_ID__, v_stream_id,
        'PerspectiveName', v_dead.perspective_name,
        'EventId', v_event_id,
        'EventWorkId', v_dead.source_id,
        -- A dead letter captured before 239 keeps no priority; it re-enters in the band recovery used to give it.
        'Priority', COALESCE((v_envelope->>'priority')::INTEGER, __PRIORITY_BACKGROUND__),
        'RecoveredFromDeadLetterId', p_dead_letter_id)),
      NULL,   -- no instance: the row is left unleased for the claim
      NULL,
      NOW(),
      p_partition_count) stored
    WHERE stored.was_newly_created
  ) INTO v_stored;
  RETURN v_stored;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_recover_dead_letter(UUID, INTEGER) IS
  'Re-enters an outbox or perspective dead letter through its store (239, #1722): store_outbox_messages or '
  'store_perspective_events, which claims the dead letter (Recovering) and stores the row naming it; the completion '
  'settles it Recovered once the message is published or the event applied. Returns whether the row re-entered. Takes '
  'the partition count arrivals are stored with. A perspective dead letter whose event the event store no longer holds '
  'is held for review with the reason. 236 (#1664): an inbox or broker dead letter, or one from any other source, is '
  'declined and left due.';

-- recover_dead_letter: last word 236, now wh_recover_dead_letter at the claim's default partition count (#1722).
-- Signature unchanged; callers that know the service's partition count call wh_recover_dead_letter.
-- <docs>operations/dead-letter-queue/recovery</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredDeadLetterReentrySqlTests.cs:RecoverDeadLetter_OfAnInboxOrBrokerRow_LeavesItForReentryAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredWorkReentrySqlTests.cs:ARecoveredOutboxMessage_IsStoredInTheShapeItWasStoredInAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.recover_dead_letter(
  p_dead_letter_id UUID
) RETURNS BOOLEAN AS $$
BEGIN
  RETURN __SCHEMA__.wh_recover_dead_letter(p_dead_letter_id, 10000);
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.recover_dead_letter(UUID) IS
  'Re-enters a dead letter through its store at the claim''s default partition count (239, #1722): '
  'wh_recover_dead_letter(id, 10000). 236 (#1664): an inbox or broker dead letter is declined and left due.';

-- complete_outbox_published: last word 172, settling a published recovered message's dead letter (#1722). Signature
-- unchanged.
-- <docs>fundamentals/work-coordinator/per-stream-drain</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:CompleteOutboxPublished_DeletesItsRowsInStreamOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:CompleteOutboxPublished_InDebugMode_UpdatesItsRowsInStreamOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CompleteOutboxPublishedSqlTests.cs:CompleteOutboxPublished_Production_DeletesRowsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredOutboxDeadLetterPublishTests.cs:ARecoveredOutboxEvent_IsPublishedAsTheEventItWasStoredAs_AndReadsRecoveredOncePublishedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.complete_outbox_published(
  p_ids UUID[],
  p_debug_mode BOOLEAN DEFAULT FALSE
) RETURNS INTEGER AS $$
DECLARE
  v_affected INTEGER;
  v_recovered UUID[];         -- 239: the dead letters the completed rows were recovered from
  v_dead_letter_id UUID;
BEGIN
  IF p_ids IS NULL OR array_length(p_ids, 1) IS NULL THEN
    RETURN 0;
  END IF;

  IF p_debug_mode THEN
    -- Debug mode: retain the row for forensics; stamp published_at + processed_at
    -- and set the Published bit. eligible_outbox filters published_at IS NULL so
    -- claim_work treats the row as if deleted on subsequent polls.
    -- 239: counted and collected from the statement's own rows, so the settle below reads nothing again.
    WITH completed AS (
      UPDATE __SCHEMA__.wh_outbox o
      SET processed_at = NOW(),
          published_at = NOW(),
          status = o.status | 4,         -- Published flag
          instance_id = NULL,
          lease_expiry = NULL
      FROM (
        -- 172: locked in (stream_id, created_at, message_id) order; see the production branch.
        SELECT l.message_id
        FROM __SCHEMA__.wh_outbox l
        WHERE l.message_id = ANY(p_ids)
          AND l.processed_at IS NULL
        ORDER BY l.stream_id, l.created_at, l.message_id
        FOR UPDATE OF l
      ) locked
      WHERE o.message_id = locked.message_id
        AND o.processed_at IS NULL
      RETURNING o.recovered_from_dead_letter_id
    )
    SELECT count(*), array_agg(c.recovered_from_dead_letter_id) FILTER (WHERE c.recovered_from_dead_letter_id IS NOT NULL)
    INTO v_affected, v_recovered
    FROM completed c;
  ELSE
    -- Production mode: row exits the table on success, structurally immune to
    -- claim_work re-issuing it on the next poll cycle.
    --
    -- 172: the rows are locked in (stream_id, created_at, message_id) order before they are deleted.
    -- The bare DELETE locked them in primary-key order, which is not the order any other writer of
    -- the outbox uses; a completion flush that also reports failures holds these rows while
    -- process_outbox_failures takes more, so the two passes must run the same way. Primary-key
    -- lookups, then a sort of the batch: priced by the batch.
    WITH completed AS (
      DELETE FROM __SCHEMA__.wh_outbox o
      USING (
        SELECT l.message_id
        FROM __SCHEMA__.wh_outbox l
        WHERE l.message_id = ANY(p_ids)
          AND l.processed_at IS NULL
        ORDER BY l.stream_id, l.created_at, l.message_id
        FOR UPDATE OF l
      ) locked
      WHERE o.message_id = locked.message_id
        AND o.processed_at IS NULL
      RETURNING o.recovered_from_dead_letter_id
    )
    SELECT count(*), array_agg(c.recovered_from_dead_letter_id) FILTER (WHERE c.recovered_from_dead_letter_id IS NOT NULL)
    INTO v_affected, v_recovered
    FROM completed c;
  END IF;

  -- 239 (#1722): a recovered message that has been published settles the dead letter it re-entered from (Recovered).
  -- After the outbox rows, so the outbox keeps its place first in the lock order.
  IF v_recovered IS NOT NULL THEN
    FOREACH v_dead_letter_id IN ARRAY v_recovered LOOP
      PERFORM __SCHEMA__._wh_settle_recovered_dead_letter(v_dead_letter_id, TRUE, NOW());
    END LOOP;
  END IF;

  RETURN v_affected;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.complete_outbox_published(UUID[], BOOLEAN) IS
'Completes outbox rows after successful transport publish. Production (p_debug_mode=FALSE, default): DELETEs the row, structurally immune to claim_work re-issuing it. Debug mode (p_debug_mode=TRUE): retains the row, stamps published_at + processed_at + Published bit; eligible_outbox filters published_at IS NULL so claim_work skips it. Coalesced + batched by C# OutboxCompletionFlushWorker. Idempotent: unknown ids silently no-op. Returns rows-affected (deletions in prod, updates in debug). 172: rows are locked in (stream_id, created_at, message_id) order. 239 (#1722): a completed row recovered from a dead letter settles that dead letter Recovered.';

-- process_perspective_event_completions: last word 177, settling an applied recovered row's dead letter (#1722).
-- Signature unchanged.
-- <docs>fundamentals/perspectives/perspective-sync#applied-ledger</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveAppliedLedgerSqlTests.cs</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredPerspectiveDeadLetterApplyTests.cs:ARecoveredPerspectiveWorkRow_IsApplied_AndReadsRecoveredOnceAppliedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.process_perspective_event_completions(
  p_completions JSONB,
  p_now TIMESTAMPTZ,
  p_debug_mode BOOLEAN DEFAULT FALSE
) RETURNS TABLE(
  event_work_id UUID,
  stream_id UUID,
  perspective_name VARCHAR(200),
  was_deleted BOOLEAN
) AS $$
BEGIN
  IF jsonb_array_length(p_completions) = 0 THEN RETURN; END IF;

  -- One set-based statement per mode (v0.671). Not-found completions are naturally filtered: the join
  -- only matches IDs that exist in wh_perspective_events, so already-deleted or never-existing IDs neither
  -- appear in RETURNING nor reach the ledger.
  --
  -- #959: each retired row is recorded in wh_perspective_applied in the same statement, so the ledger
  -- holds exactly the applies the worker completed. ON CONFLICT: a redelivered completion, or a debug-mode
  -- row completed twice, records once.
  IF p_debug_mode THEN
    -- Debug mode: retain rows, stamp status | status_flags + processed_at, clear lease columns.
    RETURN QUERY
    WITH completions AS (
      SELECT
        (elem->>'EventWorkId')::UUID AS work_id,
        (elem->>'StatusFlags')::INTEGER AS status_flags
      FROM jsonb_array_elements(p_completions) AS elem
    ),
    completed AS (
      UPDATE __SCHEMA__.wh_perspective_events pe
      SET status = pe.status | c.status_flags,
          processed_at = p_now,
          instance_id = NULL,
          lease_expiry = NULL
      FROM completions c
      WHERE pe.event_work_id = c.work_id
      RETURNING pe.event_work_id, pe.stream_id, pe.perspective_name, pe.event_id, pe.recovered_from_dead_letter_id
    ),
    recorded AS (
      INSERT INTO __SCHEMA__.wh_perspective_applied (event_id, perspective_name, stream_id, applied_at)
      SELECT DISTINCT ON (cd.event_id, cd.perspective_name) cd.event_id, cd.perspective_name, cd.stream_id, p_now
      FROM completed cd
      ON CONFLICT ON CONSTRAINT pk_wh_perspective_applied DO NOTHING
    ),
    -- 239 (#1722): an applied row recovered from a dead letter settles that dead letter Recovered, in the same
    -- statement (the processed branch of _wh_settle_recovered_dead_letter, written set-based).
    settled AS (
      UPDATE __SCHEMA__.wh_dead_letters dl
      SET recovery_status = 3,
          recovered_at = p_now,
          retried_on_generations =
            CASE WHEN dl.generation = ANY(dl.retried_on_generations) THEN dl.retried_on_generations
                 ELSE array_append(dl.retried_on_generations, dl.generation) END
      FROM completed cd
      WHERE cd.recovered_from_dead_letter_id IS NOT NULL
        AND dl.dead_letter_id = cd.recovered_from_dead_letter_id
        AND dl.recovery_status = 1
    )
    SELECT cd.event_work_id, cd.stream_id, cd.perspective_name, FALSE AS was_deleted
    FROM completed cd;
  ELSE
    -- Production: DELETE matched rows, returning identity for cursor advancement.
    RETURN QUERY
    WITH completions AS (
      SELECT (elem->>'EventWorkId')::UUID AS work_id
      FROM jsonb_array_elements(p_completions) AS elem
    ),
    completed AS (
      DELETE FROM __SCHEMA__.wh_perspective_events pe
      USING completions c
      WHERE pe.event_work_id = c.work_id
      RETURNING pe.event_work_id, pe.stream_id, pe.perspective_name, pe.event_id, pe.recovered_from_dead_letter_id
    ),
    recorded AS (
      INSERT INTO __SCHEMA__.wh_perspective_applied (event_id, perspective_name, stream_id, applied_at)
      SELECT DISTINCT ON (cd.event_id, cd.perspective_name) cd.event_id, cd.perspective_name, cd.stream_id, p_now
      FROM completed cd
      ON CONFLICT ON CONSTRAINT pk_wh_perspective_applied DO NOTHING
    ),
    -- 239 (#1722): an applied row recovered from a dead letter settles that dead letter Recovered, in the same
    -- statement (the processed branch of _wh_settle_recovered_dead_letter, written set-based).
    settled AS (
      UPDATE __SCHEMA__.wh_dead_letters dl
      SET recovery_status = 3,
          recovered_at = p_now,
          retried_on_generations =
            CASE WHEN dl.generation = ANY(dl.retried_on_generations) THEN dl.retried_on_generations
                 ELSE array_append(dl.retried_on_generations, dl.generation) END
      FROM completed cd
      WHERE cd.recovered_from_dead_letter_id IS NOT NULL
        AND dl.dead_letter_id = cd.recovered_from_dead_letter_id
        AND dl.recovery_status = 1
    )
    SELECT cd.event_work_id, cd.stream_id, cd.perspective_name, TRUE AS was_deleted
    FROM completed cd;
  END IF;

  -- Bounded prune of the ledger: rows older than an hour, oldest first, at most 1000 per call, so a
  -- backlog of expired rows drains across calls rather than stalling one completion.
  DELETE FROM __SCHEMA__.wh_perspective_applied pa
  WHERE pa.ctid IN (
    SELECT old.ctid
    FROM __SCHEMA__.wh_perspective_applied old
    WHERE old.applied_at < p_now - INTERVAL '1 hour'
    ORDER BY old.applied_at
    LIMIT 1000
  );
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.process_perspective_event_completions(JSONB, TIMESTAMPTZ, BOOLEAN) IS
  'Retires completed perspective work rows (DELETE, or stamped in debug mode) and records each in the applied-event '
  'ledger wh_perspective_applied (177, #959), pruning ledger rows older than an hour. 239 (#1722): a retired row '
  'recovered from a dead letter settles that dead letter Recovered.';

-- _wh_settle_superseded_recovery: last word 236, on every source table (#1722).
-- <docs>operations/dead-letter-queue/transport-recovery</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredDeadLetterReentrySqlTests.cs:ARecoveredMessage_DeadLetteredAgain_IsHeldForReview_NamingTheNewDeadLetterAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredWorkReentrySqlTests.cs:ARecoveredOutboxMessage_DeadLetteredAgain_IsHeldForReview_NamingTheNewDeadLetterAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredWorkReentrySqlTests.cs:ARecoveredPerspectiveWorkRow_DeadLetteredAgain_IsHeldForReview_NamingTheNewDeadLetterAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_settle_superseded_recovery() RETURNS TRIGGER AS $$
BEGIN
  -- A recovered message dead-lettered again: NEW holds its custody now, so the row it was recovered from (Recovering
  -- while the message is in flight, or Recovered by a recovery from before 236 and 239) is settled for review with the
  -- outcome recorded. The operator hold keeps a cohort release from re-driving the message a second time while NEW
  -- holds it. 239 (#1722): on every source table; a perspective row keeps its work id through recovery.
  UPDATE __SCHEMA__.wh_dead_letters prior
  SET recovery_status = 2,        -- HoldForReview
      recovered_at = NULL,
      operator_disposition = 2,   -- HoldIndefinitely
      operator_notes = COALESCE(prior.operator_notes || E'\n', '')
        || format('recovery did not complete: the re-driven message was dead-lettered again as %s (failure reason %s), '
                  || 'which holds its custody now', NEW.dead_letter_id, NEW.failure_reason)
  WHERE prior.source_id = NEW.source_id
    -- The same source table; and a message re-entered from the broker, when NEW is the inbox's (the trigger's WHEN
    -- admits three source tables, and the other two never hold a broker message).
    AND (prior.source_table = NEW.source_table
         OR (prior.source_table = 'broker' AND NEW.source_table NOT IN ('wh_outbox', 'wh_perspective_events')))
    AND prior.dead_letter_id <> NEW.dead_letter_id
    AND prior.recovery_status IN (1, 3);
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_settle_superseded_recovery() IS
  'AFTER INSERT trigger fn on wh_dead_letters (236, #1664; 239, #1722: every source table): a recovered message or work row dead-lettered again settles the row it was recovered from as HoldForReview under an operator hold, naming the new dead letter in operator_notes, so Recovered never reads true of a recovery that did not complete.';

DROP TRIGGER IF EXISTS trg_wh_dead_letters_settle_superseded_recovery ON __SCHEMA__.wh_dead_letters;
CREATE TRIGGER trg_wh_dead_letters_settle_superseded_recovery
  AFTER INSERT ON __SCHEMA__.wh_dead_letters
  FOR EACH ROW
  WHEN (NEW.source_table IN ('wh_inbox', 'wh_outbox', 'wh_perspective_events'))
  EXECUTE FUNCTION __SCHEMA__._wh_settle_superseded_recovery();
