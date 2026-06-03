-- Migration: 051_DeadLetterRecovery.sql
-- Date: 2026-06-03 (v0.502 slice C.7 foundation)
-- Description: SQL surface for the recovery worker:
--   - fetch_dead_letters_due(p_now, p_max): rows ready for recovery
--   - recover_dead_letter(p_dead_letter_id): atomic re-emit into source table + mark Recovered
--   - mark_dead_letter_holding(p_dead_letter_id) / _permanently_failed: terminal-state setters
--   - reset_dead_letters_for_generation(p_current_generation): generation-replay sweep
-- Dependencies: 001-050

-- ============================================================================
-- fetch_dead_letters_due — selects rows the worker should try to recover
-- ============================================================================
-- Returns the decision-making columns the worker needs to apply
-- IDeadLetterRecoveryPolicy without a second roundtrip per row. Excludes terminal
-- statuses (Recovered=3, PermanentlyFailed=4) and HoldForReview (status=2) — those rows
-- shouldn't enter the recovery loop. Respects operator disposition: rows with
-- operator_disposition=2 (HoldIndefinitely) are also skipped.

SELECT __SCHEMA__.drop_all_overloads('fetch_dead_letters_due');

CREATE OR REPLACE FUNCTION __SCHEMA__.fetch_dead_letters_due(
  p_now    TIMESTAMPTZ,
  p_max    INTEGER
) RETURNS TABLE(
  dead_letter_id     UUID,
  source_table       TEXT,
  source_id          UUID,
  stream_id          UUID,
  message_type       TEXT,
  failure_reason     INTEGER,
  attempts_when_dlq  INTEGER,
  dead_lettered_at   TIMESTAMPTZ,
  recovery_status    INTEGER,
  recovery_attempts  INTEGER,
  generation         TEXT
) AS $$
BEGIN
  RETURN QUERY
  SELECT
    dl.dead_letter_id, dl.source_table, dl.source_id, dl.stream_id, dl.message_type,
    dl.failure_reason, dl.attempts_when_dlq, dl.dead_lettered_at,
    dl.recovery_status, dl.recovery_attempts, dl.generation
  FROM wh_dead_letters dl
  WHERE dl.recovered_at IS NULL
    AND dl.recovery_status NOT IN (2, 4)  -- HoldForReview, PermanentlyFailed
    AND dl.operator_disposition NOT IN (2, 3)  -- HoldIndefinitely, MarkPermanentlyFailed
    AND (dl.next_recovery_at IS NULL OR dl.next_recovery_at <= p_now)
  ORDER BY dl.dead_lettered_at  -- oldest first; FIFO-fair recovery
  LIMIT p_max;
END;
$$ LANGUAGE plpgsql STABLE;

COMMENT ON FUNCTION __SCHEMA__.fetch_dead_letters_due IS
'Returns up to p_max wh_dead_letters rows the recovery worker should attempt. Filters: recovered_at IS NULL, recovery_status NOT terminal (HoldForReview/PermanentlyFailed), operator_disposition NOT HoldIndefinitely/MarkPermanentlyFailed, next_recovery_at <= p_now (or NULL = ready immediately). FIFO-ordered by dead_lettered_at.';

-- ============================================================================
-- recover_dead_letter — atomic INSERT-back-into-source + mark Recovered
-- ============================================================================
-- Re-emits the dead-letter into its source table with attempts=0, then marks the DLQ
-- row Recovered. Single transaction so a partial-failure crash leaves consistent state.
--
-- The envelope JSONB carries the original event_data + metadata captured by
-- move_to_dead_letters. We unpack those subfields and rebuild a fresh source-table row
-- using the original message_id — receiver-side dedup will correctly skip re-processing
-- if the message was already successfully handled.

SELECT __SCHEMA__.drop_all_overloads('recover_dead_letter');

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
  -- Atomically claim the row by transitioning to Recovering AND fetch its forensic
  -- payload. If another worker raced us OR the row is already terminal, the UPDATE
  -- affects zero rows and we return false.
  WITH claimed AS (
    UPDATE wh_dead_letters
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

  -- Extract the original event_data from the envelope JSONB.
  v_event_data := v_envelope -> 'event_data';
  v_partition := CASE WHEN v_stream_id IS NULL THEN 0 ELSE 0 END;  -- partition recomputed on store_*_messages path; fixed to 0 here is fine because claim_orphaned_* recomputes via wh_active_streams

  -- Re-emit into the appropriate source table with attempts=0.
  IF v_source_table = 'wh_outbox' THEN
    INSERT INTO wh_outbox (message_id, destination, message_type, envelope_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number)
    VALUES (v_source_id, v_destination, v_message_type, 'recovered', v_event_data, v_metadata, 0, 0, NOW(), v_stream_id, v_partition)
    ON CONFLICT (message_id) DO NOTHING;  -- already re-published; idempotent
  ELSIF v_source_table = 'wh_inbox' THEN
    INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at, stream_id, partition_number)
    VALUES (v_source_id, COALESCE(v_perspective, 'recovered'), v_message_type, v_event_data, v_metadata, 0, 0, NOW(), v_stream_id, v_partition)
    ON CONFLICT (message_id) DO NOTHING;
  ELSIF v_source_table = 'wh_perspective_events' THEN
    -- Perspective recovery uses the event_id snapshot to recreate the work row.
    INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at)
    VALUES (v_source_id, v_stream_id, v_perspective, (v_envelope ->> 'event_id')::UUID, v_partition, 0, 0, NOW())
    ON CONFLICT (event_work_id) DO NOTHING;
  ELSE
    -- Unknown source table — leave as Recovering for an operator to investigate.
    RAISE WARNING 'recover_dead_letter: unsupported source table %', v_source_table;
    RETURN FALSE;
  END IF;

  -- Mark Recovered.
  UPDATE wh_dead_letters
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
'Atomically re-emits a wh_dead_letters row back into its source table (with attempts=0) and marks the DLQ row Recovered. Returns true on successful re-emit, false if the row was already terminal or claimed by another worker. Single-transaction so crash-safe; ON CONFLICT DO NOTHING on the INSERT side handles double-recovery races.';

-- ============================================================================
-- mark_dead_letter_holding — terminal: HoldForReview after policy-exhaustion
-- ============================================================================

SELECT __SCHEMA__.drop_all_overloads('mark_dead_letter_holding');

CREATE OR REPLACE FUNCTION __SCHEMA__.mark_dead_letter_holding(
  p_dead_letter_id UUID
) RETURNS VOID AS $$
BEGIN
  UPDATE wh_dead_letters
  SET recovery_status = 2,                          -- HoldForReview
      next_recovery_at = NULL
  WHERE dead_letter_id = p_dead_letter_id
    AND recovered_at IS NULL;
END;
$$ LANGUAGE plpgsql;

-- ============================================================================
-- mark_dead_letter_permanently_failed — terminal: PermanentlyFailed
-- ============================================================================

SELECT __SCHEMA__.drop_all_overloads('mark_dead_letter_permanently_failed');

CREATE OR REPLACE FUNCTION __SCHEMA__.mark_dead_letter_permanently_failed(
  p_dead_letter_id UUID
) RETURNS VOID AS $$
BEGIN
  UPDATE wh_dead_letters
  SET recovery_status = 4,                          -- PermanentlyFailed
      next_recovery_at = NULL
  WHERE dead_letter_id = p_dead_letter_id
    AND recovered_at IS NULL;
END;
$$ LANGUAGE plpgsql;

-- ============================================================================
-- schedule_next_dead_letter_attempt — after a failed recovery attempt
-- ============================================================================

SELECT __SCHEMA__.drop_all_overloads('schedule_next_dead_letter_attempt');

CREATE OR REPLACE FUNCTION __SCHEMA__.schedule_next_dead_letter_attempt(
  p_dead_letter_id UUID,
  p_next_at        TIMESTAMPTZ
) RETURNS VOID AS $$
BEGIN
  UPDATE wh_dead_letters
  SET recovery_status = 0,                          -- back to Pending
      next_recovery_at = p_next_at
  WHERE dead_letter_id = p_dead_letter_id
    AND recovered_at IS NULL;
END;
$$ LANGUAGE plpgsql;

-- ============================================================================
-- reset_dead_letters_for_generation — generation-replay sweep
-- ============================================================================
-- Called once on worker startup. Finds DLQ rows whose generation is NOT in the current
-- generation's retried_on_generations array AND aren't terminal — schedules them for
-- immediate retry. Implements the "we shipped a fix; auto-retry the prior generation's
-- DLQ exactly once" semantic.

SELECT __SCHEMA__.drop_all_overloads('reset_dead_letters_for_generation');

CREATE OR REPLACE FUNCTION __SCHEMA__.reset_dead_letters_for_generation(
  p_current_generation TEXT
) RETURNS INTEGER AS $$
DECLARE
  v_count INTEGER;
BEGIN
  UPDATE wh_dead_letters
  SET next_recovery_at = NOW(),
      retried_on_generations = array_append(retried_on_generations, p_current_generation),
      recovery_status = 0  -- Pending
  WHERE recovered_at IS NULL
    AND recovery_status NOT IN (4)  -- not PermanentlyFailed (operator can re-enable via API)
    AND operator_disposition NOT IN (2)  -- not HoldIndefinitely
    AND NOT (p_current_generation = ANY(retried_on_generations));
  GET DIAGNOSTICS v_count = ROW_COUNT;
  RETURN v_count;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.reset_dead_letters_for_generation IS
'Generation-replay: schedules every non-terminal, non-held DLQ row whose generation has not yet been replayed on the current build for immediate recovery. Appends p_current_generation to retried_on_generations so the row gets exactly one replay per new build. Returns the number of rows scheduled. Called on recovery worker startup.';
