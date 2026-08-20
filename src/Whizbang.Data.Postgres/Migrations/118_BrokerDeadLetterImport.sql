-- ============================================================================
-- Migration: 118_BrokerDeadLetterImport.sql
-- Date: 2026-08-20
-- Description: Broker DLQ import — one custody model for failed messages (proposal:
--   proposals/broker-dlq-import). Adds wh_import_dead_letter so the transport dead-letter
--   drain gives broker-dead-lettered messages durable custody as wh_dead_letters rows
--   (source_table='broker', failure_reason=17 BrokerDeadLetter, RAW wire body stored
--   verbatim — no deserialization at import). Re-creates recover_dead_letter verbatim from
--   051 plus a 'broker' branch that re-emits imported rows into wh_inbox — the same front
--   door every received message uses. Import is idempotent on the wire message id via a
--   partial unique index.
-- Dependencies: 050 (wh_dead_letters + compute_dead_letter_fingerprint), 051 (recovery)
-- ============================================================================

-- Import idempotency: one custody row per wire message id. Partial so the constraint
-- costs nothing on the (far larger) internal-failure population, whose source_id values
-- are per-attempt work ids rather than stable wire ids.
CREATE UNIQUE INDEX IF NOT EXISTS wh_dead_letters_broker_import_idx
  ON __SCHEMA__.wh_dead_letters (source_id)
  WHERE source_table = 'broker';

-- ============================================================================
-- wh_import_dead_letter — broker DLQ → durable custody
-- ============================================================================
-- The body arrives as TEXT and is parsed to JSONB defensively: a body that is not valid
-- JSON still gets custody (stored as a JSON string) — messages that cannot even parse are
-- precisely the ones that need forensics. The envelope column uses the same
-- {event_data, metadata} shape move_to_dead_letters writes, so the recovery re-emit into
-- wh_inbox is uniform across sources.

SELECT __SCHEMA__.drop_all_overloads('wh_import_dead_letter');

CREATE OR REPLACE FUNCTION __SCHEMA__.wh_import_dead_letter(
  p_dead_letter_id   UUID,          -- caller generates (TrackedGuid.NewMedo on the C# side)
  p_message_id       UUID,          -- wire message id = idempotency key
  p_stream_id        UUID,          -- session key when present; NULL otherwise
  p_message_type     TEXT,          -- wire envelope type name; NULL when the property is absent
  p_destination      TEXT,          -- broker coordinates, e.g. topic/subscription
  p_envelope_json    TEXT,          -- RAW wire body, verbatim
  p_broker_reason    TEXT,          -- broker DeadLetterReason
  p_broker_description TEXT,        -- broker DeadLetterErrorDescription
  p_enqueued_at      TIMESTAMPTZ,   -- broker enqueue time when known
  p_delivery_count   INTEGER,       -- broker delivery attempts before dead-lettering
  p_instance_id      UUID,
  p_generation       TEXT
) RETURNS BOOLEAN AS $$
DECLARE
  v_body       JSONB;
  v_error_text TEXT;
  v_inserted   INTEGER;
BEGIN
  BEGIN
    v_body := p_envelope_json::jsonb;
  EXCEPTION WHEN others THEN
    v_body := to_jsonb(p_envelope_json);   -- custody over correctness: never lose the bytes
  END;

  v_error_text := 'BrokerDeadLetter: ' || COALESCE(p_broker_reason, 'unknown')
    || CASE WHEN p_broker_description IS NOT NULL THEN ' — ' || p_broker_description ELSE '' END;

  INSERT INTO __SCHEMA__.wh_dead_letters (
    dead_letter_id, source_table, source_id, stream_id, message_type, destination,
    perspective_name, envelope, metadata, failure_reason, error_text,
    error_fingerprint, error_fingerprint_version, attempts_when_dlq,
    dead_lettered_by, generation
  ) VALUES (
    p_dead_letter_id,
    'broker',
    p_message_id,
    NULLIF(p_stream_id, '00000000-0000-0000-0000-000000000000'::uuid),
    COALESCE(p_message_type, 'unknown'),
    p_destination,
    NULL,
    jsonb_build_object('event_data', v_body, 'metadata', '{}'::jsonb),
    jsonb_strip_nulls(jsonb_build_object(
      'broker_reason', p_broker_reason,
      'broker_description', p_broker_description,
      'broker_enqueued_at', p_enqueued_at,
      'broker_delivery_count', p_delivery_count,
      'imported_at', NOW()
    )),
    17,                                    -- MessageFailureReason.BrokerDeadLetter
    v_error_text,
    __SCHEMA__.compute_dead_letter_fingerprint(v_error_text),
    __SCHEMA__.current_dead_letter_fingerprint_version(),
    COALESCE(p_delivery_count, 0),
    p_instance_id,
    p_generation
  )
  ON CONFLICT (source_id) WHERE source_table = 'broker' DO NOTHING;

  GET DIAGNOSTICS v_inserted = ROW_COUNT;
  RETURN v_inserted > 0;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_import_dead_letter IS
'Gives a broker-dead-lettered message durable custody as a wh_dead_letters row (source_table=broker, failure_reason=17). Stores the RAW wire body verbatim (defensive JSONB parse; non-JSON bodies become JSON strings) so import never deserializes. Idempotent on the wire message id via wh_dead_letters_broker_import_idx: returns true when a row was created, false for a duplicate. Recovery re-emits broker rows into wh_inbox (see recover_dead_letter).';

-- ============================================================================
-- recover_dead_letter — re-created verbatim from 051 + the 'broker' branch
-- ============================================================================
-- Signature unchanged; per the migration rules a modified function is CREATE OR REPLACEd
-- whole (copied verbatim + the delta).

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
  ELSIF v_source_table = 'wh_perspective_events' THEN
    -- Perspective recovery uses the event_id snapshot to recreate the work row.
    INSERT INTO __SCHEMA__.wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at)
    VALUES (v_source_id, v_stream_id, v_perspective, (v_envelope ->> 'event_id')::UUID, v_partition, 0, 0, NOW())
    ON CONFLICT (event_work_id) DO NOTHING;
  ELSIF v_source_table = 'broker' THEN
    -- Broker-imported rows (wh_import_dead_letter, migration 118) re-enter through the inbox
    -- front door: normal dispatch, composite fan-out, and the internal max-attempts ladder all
    -- apply unchanged. A row that still cannot be processed on the current build parks again in
    -- wh_dead_letters via move_to_dead_letters — visible, fingerprinted, attempt-accounted —
    -- instead of orbiting the broker's opaque DLQ.
    INSERT INTO __SCHEMA__.wh_inbox (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at, stream_id, partition_number)
    VALUES (v_source_id, 'broker-recovered', v_message_type, v_event_data, v_metadata, 0, 0, NOW(), v_stream_id, v_partition)
    ON CONFLICT (message_id) DO NOTHING;
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

-- ============================================================================
-- move_to_dead_letters — re-created verbatim from 050 + terminal-error capture
-- ============================================================================
-- Signature unchanged. The perspective branch now also snapshots the source row's stored
-- error column so the DLQ row carries the ACTUAL apply exception, not only the promotion
-- wrapper (fingerprints computed on the enriched text restore per-failure-mode clustering).

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
        attempts
    )
    SELECT m.stream_id, m.message_type, m.destination,
           jsonb_build_object('event_data', m.event_data, 'metadata', m.metadata),
           m.metadata, m.attempts
      INTO v_stream_id, v_message_type, v_destination, v_envelope, v_metadata, v_attempts
    FROM moved m;
  ELSIF p_source_table = 'wh_inbox' THEN
    WITH moved AS (
      DELETE FROM __SCHEMA__.wh_inbox
      WHERE message_id = p_source_id
      RETURNING
        stream_id,
        message_type,
        event_data,
        metadata,
        attempts
    )
    SELECT m.stream_id, m.message_type, NULL::TEXT,
           jsonb_build_object('event_data', m.event_data, 'metadata', m.metadata),
           m.metadata, m.attempts
      INTO v_stream_id, v_message_type, v_destination, v_envelope, v_metadata, v_attempts
    FROM moved m;
  ELSIF p_source_table = 'wh_perspective_events' THEN
    WITH moved AS (
      DELETE FROM __SCHEMA__.wh_perspective_events
      WHERE event_work_id = p_source_id
      RETURNING
        stream_id,
        perspective_name,
        event_id,
        attempts,
        error
    )
    SELECT m.stream_id, m.perspective_name,
           jsonb_build_object('event_id', m.event_id, 'perspective_name', m.perspective_name),
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

  -- Migration 118: preserve the source row's stored terminal error (wh_perspective_events.error
  -- — the actual apply exception) alongside the caller's promotion wrapper. Without this, DLQ
  -- rows carried only "attempts=N > max=M" and the root cause was unrecoverable once pod logs
  -- rotated. The fingerprint is computed on the ENRICHED text so failure modes cluster by the
  -- real exception instead of collapsing into one wrapper cluster.
  v_error_text := CASE
    WHEN v_source_error IS NOT NULL AND v_source_error <> ''
      THEN COALESCE(p_error_text || ' — last error: ', '') || v_source_error
    ELSE p_error_text
  END;

  -- Slice 3a of release/v0.645.0-alpha.1 — auto-fingerprint every row at INSERT
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
    generation
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
    p_generation
  );

  RETURN p_dead_letter_id;
END;
$$ LANGUAGE plpgsql;
