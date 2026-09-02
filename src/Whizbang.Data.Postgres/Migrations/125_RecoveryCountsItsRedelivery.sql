-- Migration: 125_RecoveryCountsItsRedelivery.sql
-- Date: 2026-09-02
-- Description: Re-creates recover_dead_letter VERBATIM from 119 plus redelivery accounting on the
--              two paths that re-deliver into wh_inbox (source_table 'wh_inbox' and 'broker').
--
--              121 replaced count-based poison detection with an observation counter the framework
--              maintains itself, because a broker delivery counter cannot bound a redelivery loop on
--              a session-enabled entity. store_inbox_messages increments
--              wh_message_deduplication.observation_count on every arrival and PoisonMessageDetector
--              acts on that count.
--
--              recover_dead_letter re-delivers by INSERTing straight into wh_inbox, bypassing
--              store_inbox_messages and therefore the counter. Every recovery-driven arrival was
--              invisible to poison detection, and attempts is reset to 0 on the way in, so a message
--              could be recovered without limit. The loop was unbounded by construction and raising
--              MaxRecoveryAttempts could not help, because each pass presents a row on its first
--              attempt.
--
--              The observation is charged only when the INSERT actually inserted: ON CONFLICT DO
--              NOTHING means a double-recovery race delivered nothing, and charging for a delivery
--              that did not happen would push a healthy message toward quarantine.
-- Dependencies: 119 (recover_dead_letter), 121 (wh_message_deduplication.observation_count)

CREATE OR REPLACE FUNCTION __SCHEMA__.recover_dead_letter(
  p_dead_letter_id UUID
) RETURNS BOOLEAN AS $$
DECLARE
  v_source_table   TEXT;
  v_source_id      UUID;
  v_redelivered    BIGINT;
  v_stream_id      UUID;
  v_message_type   TEXT;
  v_destination    TEXT;
  v_perspective    TEXT;
  v_envelope       JSONB;
  v_metadata       JSONB;
  v_event_data     JSONB;
  v_partition      INTEGER;
BEGIN
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
  -- Guid.Empty stream_id (a pattern observed in production — producer bug from before the
  -- v0.657 storage-time Reject guard shipped), normalize to NULL on the
  -- INSERT back into the source table. Otherwise the recovered row immediately
  -- re-sticks under the same silent-stuck pattern that DLQ'd it in the first
  -- place: stream_id=Empty bypasses the NULL-only `??` coalesce in the C#
  -- coordinator (pre-v0.657) and the slice-3 coordinator backstop sees Empty
  -- as "no real stream identity" → WorkId fallback. NULL is the documented
  -- singleton-stream marker; that's the value we want on recovery.
  IF v_stream_id = '00000000-0000-0000-0000-000000000000'::uuid THEN
    v_stream_id := NULL;
  END IF;

  -- Extract the original event_data from the envelope JSONB.
  v_event_data := v_envelope -> 'event_data';
  v_partition := CASE WHEN v_stream_id IS NULL THEN 0 ELSE 0 END;  -- partition recomputed on store_*_messages path; fixed to 0 here is fine because claim_orphaned_* recomputes via wh_active_streams

  -- Re-emit into the appropriate source table with attempts=0.
  IF v_source_table = 'wh_outbox' THEN
    INSERT INTO __SCHEMA__.wh_outbox (message_id, destination, message_type, envelope_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number)
    VALUES (v_source_id, v_destination, v_message_type, 'recovered', v_event_data, v_metadata, 0, 0, NOW(), v_stream_id, v_partition)
    ON CONFLICT (message_id) DO NOTHING;  -- already re-published; idempotent
  ELSIF v_source_table = 'wh_inbox' THEN
    INSERT INTO __SCHEMA__.wh_inbox (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at, stream_id, partition_number)
    VALUES (v_source_id, COALESCE(v_perspective, 'recovered'), v_message_type, v_event_data, v_metadata, 0, 0, NOW(), v_stream_id, v_partition)
    ON CONFLICT (message_id) DO NOTHING;
    -- 125: count the re-delivery this recovery just caused.
    --
    -- 121 replaced count-based poison detection with an observation counter the framework keeps
    -- itself, because a broker delivery counter cannot bound a redelivery loop. store_inbox_messages
    -- increments wh_message_deduplication.observation_count on every arrival and
    -- PoisonMessageDetector reads that count. Recovery re-delivers by INSERTing straight into
    -- wh_inbox, which bypasses that path, so before this every recovery-driven arrival was
    -- invisible: a message could be recovered without limit because no pass was ever observed and
    -- attempts is reset to 0 on the way in.
    --
    -- Charged only when the INSERT actually inserted. ON CONFLICT DO NOTHING means a double-recovery
    -- race delivered nothing, and charging for a delivery that did not happen would push a healthy
    -- message toward quarantine.
    GET DIAGNOSTICS v_redelivered = ROW_COUNT;
    IF v_redelivered > 0 THEN
      INSERT INTO __SCHEMA__.wh_message_deduplication AS dedup
        (message_id, first_seen_at, observation_count)
      VALUES (v_source_id, NOW(), 1)
      ON CONFLICT ON CONSTRAINT wh_message_deduplication_pkey DO UPDATE
        SET observation_count = dedup.observation_count + 1;
    END IF;

  ELSIF v_source_table = 'wh_perspective_events' THEN
    -- Perspective recovery uses the event_id snapshot to recreate the work row.
    INSERT INTO __SCHEMA__.wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at)
    VALUES (v_source_id, v_stream_id, v_perspective, (v_envelope ->> 'event_id')::UUID, v_partition, 0, 0, NOW())
    ON CONFLICT (event_work_id) DO NOTHING;
  ELSIF v_source_table = 'broker' THEN
    -- Broker-imported rows (wh_import_dead_letter, migration 119) re-enter through the inbox
    -- front door: normal dispatch, composite fan-out, and the internal max-attempts ladder all
    -- apply unchanged. A row that still cannot be processed on the current build parks again in
    -- wh_dead_letters via move_to_dead_letters — visible, fingerprinted, attempt-accounted —
    -- instead of orbiting the broker's opaque DLQ.
    INSERT INTO __SCHEMA__.wh_inbox (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at, stream_id, partition_number)
    VALUES (v_source_id, 'broker-recovered', v_message_type, v_event_data, v_metadata, 0, 0, NOW(), v_stream_id, v_partition)
    ON CONFLICT (message_id) DO NOTHING;
    -- 125: count the re-delivery this recovery just caused.
    --
    -- 121 replaced count-based poison detection with an observation counter the framework keeps
    -- itself, because a broker delivery counter cannot bound a redelivery loop. store_inbox_messages
    -- increments wh_message_deduplication.observation_count on every arrival and
    -- PoisonMessageDetector reads that count. Recovery re-delivers by INSERTing straight into
    -- wh_inbox, which bypasses that path, so before this every recovery-driven arrival was
    -- invisible: a message could be recovered without limit because no pass was ever observed and
    -- attempts is reset to 0 on the way in.
    --
    -- Charged only when the INSERT actually inserted. ON CONFLICT DO NOTHING means a double-recovery
    -- race delivered nothing, and charging for a delivery that did not happen would push a healthy
    -- message toward quarantine.
    GET DIAGNOSTICS v_redelivered = ROW_COUNT;
    IF v_redelivered > 0 THEN
      INSERT INTO __SCHEMA__.wh_message_deduplication AS dedup
        (message_id, first_seen_at, observation_count)
      VALUES (v_source_id, NOW(), 1)
      ON CONFLICT ON CONSTRAINT wh_message_deduplication_pkey DO UPDATE
        SET observation_count = dedup.observation_count + 1;
    END IF;

  ELSE
    -- Unknown source table — leave as Recovering for an operator to investigate.
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

COMMENT ON FUNCTION __SCHEMA__.recover_dead_letter IS
'Atomically re-emits a wh_dead_letters row back into its source table (with attempts=0) and marks the DLQ row Recovered. Broker-imported rows (source_table=broker) re-emit into wh_inbox — the same front door every received message uses. Returns true on successful re-emit, false if the row was already terminal or claimed by another worker. Single-transaction so crash-safe; ON CONFLICT DO NOTHING on the INSERT side handles double-recovery races.';
