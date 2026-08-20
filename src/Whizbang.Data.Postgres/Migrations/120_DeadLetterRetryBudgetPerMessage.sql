-- ============================================================================
-- Migration: 120_DeadLetterRetryBudgetPerMessage.sql
-- Date: 2026-08-20
-- Description: Fixes the dead-letter retry livelock (issue #518). The recovery exhaustion
--   check is keyed on the dead-letter ROW, but move_to_dead_letters mints a new row on every
--   re-failure — so the budget reset each cycle and HoldForReviewAfterExhaustion was
--   unreachable. Seeds each new row's recovery_attempts from the attempts already spent on
--   the same (source_table, source_id) so the budget is cumulative per MESSAGE. Also captures
--   wh_inbox.error into error_text (symmetric with the perspective branch added in 119), so an
--   inbox dead-letter's terminal cause survives log rotation.
-- Dependencies: 050 (wh_dead_letters), 051 (recovery), 119 (broker import + perspective error capture)
-- ============================================================================

-- The per-message budget lookup runs on every dead-letter promotion; index it. Also serves
-- operator forensics ("show me every incarnation of this message").
CREATE INDEX IF NOT EXISTS wh_dead_letters_source_lookup_idx
  ON __SCHEMA__.wh_dead_letters (source_table, source_id, generation);

-- ============================================================================
-- move_to_dead_letters — re-created verbatim from 119 + the two deltas above
-- ============================================================================

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
        attempts,
        error
    )
    SELECT m.stream_id, m.message_type, NULL::TEXT,
           jsonb_build_object('event_data', m.event_data, 'metadata', m.metadata),
           m.metadata, m.attempts, m.error
      INTO v_stream_id, v_message_type, v_destination, v_envelope, v_metadata, v_attempts, v_source_error
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

  -- Issue #518: the retry budget belongs to the MESSAGE, not to one dead-letter row.
  -- move_to_dead_letters mints a NEW dead_letter_id on every re-failure, and the recovery
  -- worker's exhaustion check reads recovery_attempts off THAT row — so a message that fails
  -- again after recovery restarted from zero, HoldForReviewAfterExhaustion could never engage,
  -- and a single poison message cycled indefinitely (observed: one message dead-lettered 257
  -- times in 15 minutes; 46k rows from 7.6k distinct messages, enough churn to exhaust a shared
  -- database's connection limit). Seeding the new row with the attempts already spent on this
  -- same (source_table, source_id) makes the budget cumulative across incarnations, so the
  -- ladder terminates. A first-time failure still gets its full budget (COALESCE to 0).
  -- Scoped to the CURRENT generation on purpose: a new build is a new chance. Generation-tagged
  -- auto-replay ("we shipped a fix — replay the casualties") depends on previously-exhausted
  -- messages getting a fresh budget after a deploy; a globally-cumulative counter would hold
  -- them forever and silently kill that recovery path.
  SELECT COALESCE(SUM(recovery_attempts), 0) INTO v_prior_recovery_attempts
  FROM __SCHEMA__.wh_dead_letters
  WHERE source_table = p_source_table
    AND source_id = p_source_id
    AND generation IS NOT DISTINCT FROM p_generation;

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

COMMENT ON FUNCTION __SCHEMA__.move_to_dead_letters IS
'Atomically moves a wh_outbox/wh_inbox/wh_perspective_events row into wh_dead_letters with a forensic snapshot. Seeds recovery_attempts from prior dead-letters of the SAME (source_table, source_id) so the recovery retry budget is cumulative per message WITHIN A BUILD GENERATION rather than per row (a new generation restores the budget so generation-tagged auto-replay still works) — without this a re-failing message mints a fresh row each cycle, HoldForReviewAfterExhaustion never engages, and the message churns forever (issue #518). Captures the source row''s stored terminal error (wh_inbox.error / wh_perspective_events.error) into error_text alongside the caller''s promotion wrapper, with the fingerprint computed on the enriched text. Returns the new dead_letter_id, or NULL if the source row was already gone (idempotent under retry).';
